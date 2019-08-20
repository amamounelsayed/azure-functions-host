﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Grpc.Core;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.Eventing.Rpc;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Azure.WebJobs.Script.ManagedDependencies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FunctionMetadata = Microsoft.Azure.WebJobs.Script.Description.FunctionMetadata;
using MsgType = Microsoft.Azure.WebJobs.Script.Grpc.Messages.StreamingMessage.ContentOneofCase;

namespace Microsoft.Azure.WebJobs.Script.Rpc
{
    internal class LanguageWorkerChannel : ILanguageWorkerChannel, IDisposable
    {
        private readonly TimeSpan workerInitTimeout = TimeSpan.FromSeconds(30);
        private readonly string _rootScriptPath;
        private readonly IScriptEventManager _eventManager;
        private readonly WorkerConfig _workerConfig;
        private readonly string _runtime;
        private readonly ILoggerFactory _loggerFactory;
        private bool _disposed;
        private bool _disposing;
        private WorkerInitResponse _initMessage;
        private string _workerId;
        private LanguageWorkerChannelState _state;
        private Queue<string> _processStdErrDataQueue = new Queue<string>(3);
        private IDictionary<string, Exception> _functionLoadErrors = new Dictionary<string, Exception>();
        private ConcurrentDictionary<string, ScriptInvocationContext> _executingInvocations = new ConcurrentDictionary<string, ScriptInvocationContext>();
        private IDictionary<string, BufferBlock<ScriptInvocationContext>> _functionInputBuffers = new ConcurrentDictionary<string, BufferBlock<ScriptInvocationContext>>();
        private IObservable<InboundEvent> _inboundWorkerEvents;
        private List<IDisposable> _inputLinks = new List<IDisposable>();
        private List<IDisposable> _eventSubscriptions = new List<IDisposable>();
        private IDisposable _startSubscription;
        private IDisposable _startLatencyMetric;
        private IOptions<ManagedDependencyOptions> _managedDependencyOptions;
        private IEnumerable<FunctionMetadata> _functions;
        private Capabilities _workerCapabilities;
        private ILogger _workerChannelLogger;
        private ILanguageWorkerProcess _languageWorkerProcess;
        private TaskCompletionSource<bool> _reloadTask = new TaskCompletionSource<bool>();
        private TaskCompletionSource<bool> _workerInitTask = new TaskCompletionSource<bool>();
        private Grpc.Messages.FunctionRpc.FunctionRpcClient client;

        internal LanguageWorkerChannel(
           string workerId,
           string rootScriptPath,
           IScriptEventManager eventManager,
           WorkerConfig workerConfig,
           ILanguageWorkerProcess languageWorkerProcess,
           ILogger logger,
           ILoggerFactory loggerFactory,
           IMetricsLogger metricsLogger,
           int attemptCount,
           IOptions<ManagedDependencyOptions> managedDependencyOptions = null)
        {
            _workerId = workerId;
            _rootScriptPath = rootScriptPath;
            _eventManager = eventManager;
            _workerConfig = workerConfig;
            _runtime = workerConfig.Language;
            _languageWorkerProcess = languageWorkerProcess;
            _workerChannelLogger = logger;
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(LoggerFactory));

            _workerCapabilities = new Capabilities(_workerChannelLogger);

            _inboundWorkerEvents = _eventManager.OfType<InboundEvent>()
                .Where(msg => msg.WorkerId == _workerId);

            _eventSubscriptions.Add(_inboundWorkerEvents
                .Where(msg => msg.MessageType == MsgType.RpcLog)
                .Subscribe(Log));

            _eventSubscriptions.Add(_eventManager.OfType<FileEvent>()
                .Where(msg => _workerConfig.Extensions.Contains(Path.GetExtension(msg.FileChangeArguments.FullPath)))
                .Throttle(TimeSpan.FromMilliseconds(300)) // debounce
                .Subscribe(msg => _eventManager.Publish(new HostRestartEvent())));

            _eventSubscriptions.Add(_inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.FunctionLoadResponse)
                .Subscribe((msg) => LoadResponse(msg.Message.FunctionLoadResponse)));

            _eventSubscriptions.Add(_inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.InvocationResponse)
                .Subscribe((msg) => InvokeResponse(msg.Message.InvocationResponse)));

            _startLatencyMetric = metricsLogger?.LatencyEvent(string.Format(MetricEventNames.WorkerInitializeLatency, workerConfig.Language, attemptCount));
            _managedDependencyOptions = managedDependencyOptions;

            _state = LanguageWorkerChannelState.Default;
            Channel channel = new Channel("127.0.0.1:49150", ChannelCredentials.Insecure);
            client = new FunctionRpc.FunctionRpcClient(channel);
        }

        public string Id => _workerId;

        public IDictionary<string, BufferBlock<ScriptInvocationContext>> FunctionInputBuffers => _functionInputBuffers;

        public LanguageWorkerChannelState State => _state;

        internal ILanguageWorkerProcess WorkerProcess => _languageWorkerProcess;

        public Task StartWorkerProcessAsync()
        {
            _startSubscription = _inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.StartStream)
                .Timeout(TimeSpan.FromSeconds(LanguageWorkerConstants.ProcessStartTimeoutSeconds))
                .Take(1)
                .Subscribe(SendWorkerInitRequest, HandleWorkerChannelError);

            _languageWorkerProcess.StartProcess();

            _state = LanguageWorkerChannelState.Initializing;

            return _workerInitTask.Task;
        }

        // send capabilities to worker, wait for WorkerInitResponse
        internal void SendWorkerInitRequest(RpcEvent startEvent)
        {
            _inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.WorkerInitResponse)
                .Timeout(workerInitTimeout)
                .Take(1)
                .Subscribe(WorkerInitResponse, HandleWorkerChannelError);

            SendStreamingMessage(new StreamingMessage
            {
                WorkerInitRequest = new WorkerInitRequest()
                {
                    HostVersion = ScriptHost.Version
                }
            });
        }

        internal void FunctionEnvironmentReloadResponse(FunctionEnvironmentReloadResponse res)
        {
            _workerChannelLogger.LogDebug("Received FunctionEnvironmentReloadResponse");
            if (res.Result.IsFailure(out Exception reloadEnvironmentVariablesException))
            {
                _workerChannelLogger.LogError(reloadEnvironmentVariablesException, "Failed to reload environment variables");
                _reloadTask.SetResult(false);
            }
            _reloadTask.SetResult(true);
        }

        internal void WorkerInitResponse(RpcEvent initEvent)
        {
            _startLatencyMetric?.Dispose();
            _startLatencyMetric = null;

            _workerChannelLogger.LogDebug("Received WorkerInitResponse");
            _initMessage = initEvent.Message.WorkerInitResponse;
            if (_initMessage.Result.IsFailure(out Exception exc))
            {
                HandleWorkerChannelError(exc);
                _workerInitTask.SetResult(false);
                return;
            }
            _state = LanguageWorkerChannelState.Initialized;
            _workerCapabilities.UpdateCapabilities(_initMessage.Capabilities);
            _workerInitTask.SetResult(true);
        }

        public void SetupFunctionInvocationBuffers(IEnumerable<FunctionMetadata> functions)
        {
            _functions = functions;
            foreach (FunctionMetadata metadata in functions)
            {
                _workerChannelLogger.LogDebug("Setting up FunctionInvocationBuffer for function:{functionName} with functionId:{id}", metadata.Name, metadata.FunctionId);
                _functionInputBuffers[metadata.FunctionId] = new BufferBlock<ScriptInvocationContext>();
            }
        }

        public void SendFunctionLoadRequests()
        {
            if (_functions != null)
            {
                foreach (FunctionMetadata metadata in _functions)
                {
                    SendFunctionLoadRequest(metadata);
                }
            }
        }

        public Task SendFunctionEnvironmentReloadRequest()
        {
            _workerChannelLogger.LogDebug("Sending FunctionEnvironmentReloadRequest");
            _eventSubscriptions
                .Add(_inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.FunctionEnvironmentReloadResponse)
                .Timeout(workerInitTimeout)
                .Take(1)
                .Subscribe((msg) => FunctionEnvironmentReloadResponse(msg.Message.FunctionEnvironmentReloadResponse)));

            IDictionary processEnv = Environment.GetEnvironmentVariables();

            FunctionEnvironmentReloadRequest request = GetFunctionEnvironmentReloadRequest(processEnv);

            SendStreamingMessage(new StreamingMessage
            {
                FunctionEnvironmentReloadRequest = request
            });

            return _reloadTask.Task;
        }

        internal FunctionEnvironmentReloadRequest GetFunctionEnvironmentReloadRequest(IDictionary processEnv)
        {
            FunctionEnvironmentReloadRequest request = new FunctionEnvironmentReloadRequest();
            foreach (DictionaryEntry entry in processEnv)
            {
                request.EnvironmentVariables.Add(entry.Key.ToString(), entry.Value.ToString());
            }
            return request;
        }

        internal void SendFunctionLoadRequest(FunctionMetadata metadata)
        {
            _workerChannelLogger.LogDebug("Sending FunctionLoadRequest for function:{functionName} with functionId:{id}", metadata.Name, metadata.FunctionId);

            // send a load request for the registered function
            SendStreamingMessage(new StreamingMessage
            {
                FunctionLoadRequest = GetFunctionLoadRequest(metadata)
            });
        }

        internal FunctionLoadRequest GetFunctionLoadRequest(FunctionMetadata metadata)
        {
            FunctionLoadRequest request = new FunctionLoadRequest()
            {
                FunctionId = metadata.FunctionId,
                Metadata = new RpcFunctionMetadata()
                {
                    Name = metadata.Name,
                    Directory = metadata.FunctionDirectory ?? string.Empty,
                    EntryPoint = metadata.EntryPoint ?? string.Empty,
                    ScriptFile = metadata.ScriptFile ?? string.Empty,
                    IsProxy = metadata.IsProxy
                }
            };

            if (_managedDependencyOptions?.Value != null && _managedDependencyOptions.Value.Enabled)
            {
                _workerChannelLogger?.LogDebug($"Adding dependency download request to {_workerConfig.Language} language worker");
                request.ManagedDependencyEnabled = _managedDependencyOptions.Value.Enabled;
            }

            foreach (var binding in metadata.Bindings)
            {
                BindingInfo bindingInfo = binding.ToBindingInfo();

                request.Metadata.Bindings.Add(binding.Name, bindingInfo);
            }
            return request;
        }

        internal void LoadResponse(FunctionLoadResponse loadResponse)
        {
            _workerChannelLogger.LogDebug("Received FunctionLoadResponse for functionId:{functionId}", loadResponse.FunctionId);
            DateTime dateValue_4 = DateTime.Now;
            _workerChannelLogger.LogError("Opaaa 11 Received FunctionLoadResponse:" + ":" + dateValue_4.ToString("MM/dd/yyyy hh:mm:ss.fff tt"));

            if (loadResponse.Result.IsFailure(out Exception ex))
            {
                //Cache function load errors to replay error messages on invoking failed functions
                _functionLoadErrors[loadResponse.FunctionId] = ex;

                FunctionMetadata metadata = _functions.SingleOrDefault(p => p.FunctionId == loadResponse.FunctionId);
                if (metadata?.Name != null)
                {
                    _loggerFactory.CreateLogger(LogCategories.CreateFunctionCategory(metadata.Name)).LogError(ex, "Function load error.");
                }
                else
                {
                    // If we cannot find the function name for some reason, make sure we log the error anyway.
                    _workerChannelLogger.LogError(ex, "Function load error.");
                }
            }

            if (loadResponse.IsDependencyDownloaded)
            {
                _workerChannelLogger?.LogInformation($"Managed dependency successfully downloaded by the {_workerConfig.Language} language worker");
            }

            // link the invocation inputs to the invoke call
            DateTime dateValue_2 = DateTime.Now;
            _workerChannelLogger.LogError("Opaaa 111 before ActionBlock:" + ":" + dateValue_2.ToString("MM/dd/yyyy hh:mm:ss.fff tt"));

            var invokeBlock = new ActionBlock<ScriptInvocationContext>(ctx => SendInvocationRequest(ctx),
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 6
            });
            DateTime dateValue_3 = DateTime.Now;
            _workerChannelLogger.LogError("Opaaa 1111 after ActionBlock:" + ":" + dateValue_3.ToString("MM/dd/yyyy hh:mm:ss.fff tt"));
            // associate the invocation input buffer with the function
            var disposableLink = _functionInputBuffers[loadResponse.FunctionId].LinkTo(invokeBlock);
            _inputLinks.Add(disposableLink);
        }

        public void SendInvocationRequest(ScriptInvocationContext context)
        {
            try
            {
                string temp = context.ExecutionContext.InvocationId.ToString();
                DateTime dateValue_2 = DateTime.Now;
                _workerChannelLogger.LogError("Opaaa 1 start SendInvocationRequest:" + temp + ":" + dateValue_2.ToString("MM/dd/yyyy hh:mm:ss.fff tt"));

                if (_functionLoadErrors.ContainsKey(context.FunctionMetadata.FunctionId))
                {
                    _workerChannelLogger.LogDebug($"Function {context.FunctionMetadata.Name} failed to load");
                    context.ResultSource.TrySetException(_functionLoadErrors[context.FunctionMetadata.FunctionId]);
                    _executingInvocations.TryRemove(context.ExecutionContext.InvocationId.ToString(), out ScriptInvocationContext _);
                }
                else
                {
                    if (context.CancellationToken.IsCancellationRequested)
                    {
                        context.ResultSource.SetCanceled();
                        return;
                    }

                    var functionMetadata = context.FunctionMetadata;

                    InvocationRequest invocationRequest = new InvocationRequest()
                    {
                        FunctionId = functionMetadata.FunctionId,
                        InvocationId = temp,
                    };
                    foreach (var pair in context.BindingData)
                    {
                        if (pair.Value != null)
                        {
                            invocationRequest.TriggerMetadata.Add(pair.Key, pair.Value.ToRpc(_workerChannelLogger, _workerCapabilities));
                        }
                    }
                    foreach (var input in context.Inputs)
                    {
                        invocationRequest.InputData.Add(new ParameterBinding()
                        {
                            Name = input.name,
                            Data = input.val.ToRpc(_workerChannelLogger, _workerCapabilities)
                        });
                    }

                    _executingInvocations.TryAdd(invocationRequest.InvocationId, context);
                    SendStreamingMessage(new StreamingMessage
                    {
                        InvocationRequest = invocationRequest
                    });

                    DateTime dateValue = DateTime.Now;
                    _workerChannelLogger.LogError("Opaaa 2 end SendInvocationRequest:" + temp + ":" + dateValue.ToString("MM/dd/yyyy hh:mm:ss.fff tt"));
                }
            }
            catch (Exception invokeEx)
            {
                context.ResultSource.TrySetException(invokeEx);
            }
        }

        internal void InvokeResponse(InvocationResponse invokeResponse)
        {
            DateTime dateValue = DateTime.Now;
            _workerChannelLogger.LogError("Opaaa 5 start InvokeResponse:" + invokeResponse.InvocationId + ":" + dateValue.ToString("MM/dd/yyyy hh:mm:ss.fff tt"));
            if (_executingInvocations.TryRemove(invokeResponse.InvocationId, out ScriptInvocationContext context)
                && invokeResponse.Result.IsSuccess(context.ResultSource))
            {
                try
                {
                    IDictionary<string, object> bindingsDictionary = invokeResponse.OutputData
                        .ToDictionary(binding => binding.Name, binding => binding.Data.ToObject());

                    var result = new ScriptInvocationResult()
                    {
                        Outputs = bindingsDictionary,
                        Return = invokeResponse?.ReturnValue?.ToObject()
                    };
                    context.ResultSource.SetResult(result);
                }
                catch (Exception responseEx)
                {
                    context.ResultSource.TrySetException(responseEx);
                }
            }
            DateTime dateValue_2 = DateTime.Now;
            _workerChannelLogger.LogError("Opaaa 6 end InvokeResponse:" + invokeResponse.InvocationId + ":" + dateValue_2.ToString("MM/dd/yyyy hh:mm:ss.fff tt"));
        }

        internal void Log(RpcEvent msg)
        {
            var rpcLog = msg.Message.RpcLog;
            LogLevel logLevel = (LogLevel)rpcLog.Level;
            if (_executingInvocations.TryGetValue(rpcLog.InvocationId, out ScriptInvocationContext context))
            {
                // Restore the execution context from the original invocation. This allows AsyncLocal state to flow to loggers.
                System.Threading.ExecutionContext.Run(context.AsyncExecutionContext, (s) =>
                {
                    if (rpcLog.Exception != null)
                    {
                        var exception = new RpcException(rpcLog.Message, rpcLog.Exception.Message, rpcLog.Exception.StackTrace);
                        context.Logger.Log(logLevel, new EventId(0, rpcLog.EventId), rpcLog.Message, exception, (state, exc) => state);
                    }
                    else
                    {
                        context.Logger.Log(logLevel, new EventId(0, rpcLog.EventId), rpcLog.Message, null, (state, exc) => state);
                    }
                }, null);
            }
        }

        internal void HandleWorkerChannelError(Exception exc)
        {
            if (_disposing)
            {
                return;
            }
            _eventManager.Publish(new WorkerErrorEvent(_runtime, Id, exc));
        }

        private async void SendStreamingMessage(StreamingMessage msg)
        {
            //   var call = client.EventStream();
            //   call.RequestStream.WriteAsync(msg);
            using (var call = client.EventStream())
            {
                var responseReaderTask = Task.Run(async () =>
                {
                    while (await call.ResponseStream.MoveNext())
                    {
                        var currentMessage = call.ResponseStream.Current;
                        if (currentMessage.InvocationResponse != null && !string.IsNullOrEmpty(currentMessage.InvocationResponse.InvocationId))
                        {
                            DateTime dateValue = DateTime.Now;
                        }
                        _eventManager.Publish(new InboundEvent(_workerId, currentMessage));
                    }
                });
                await call.RequestStream.WriteAsync(msg);
                await call.RequestStream.CompleteAsync();
                await responseReaderTask;
            }
       //     _eventManager.Publish(new OutboundEvent(_workerId, msg));
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _startLatencyMetric?.Dispose();
                    _startSubscription?.Dispose();

                    // unlink function inputs
                    foreach (var link in _inputLinks)
                    {
                        link.Dispose();
                    }

                    (_languageWorkerProcess as IDisposable)?.Dispose();

                    foreach (var sub in _eventSubscriptions)
                    {
                        sub.Dispose();
                    }
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            _disposing = true;
            Dispose(true);
        }
    }
}
