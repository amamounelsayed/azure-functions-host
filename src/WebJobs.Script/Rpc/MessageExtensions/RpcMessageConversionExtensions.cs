﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http.Headers;
using System.Text;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RpcDataType = Microsoft.Azure.WebJobs.Script.Grpc.Messages.TypedData.DataOneofCase;

namespace Microsoft.Azure.WebJobs.Script.Rpc
{
    internal static class RpcMessageConversionExtensions
    {
        private static readonly JsonSerializerSettings _datetimeSerializerSettings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };

        public static object ToObject(this TypedData typedData)
        {
            switch (typedData.DataCase)
            {
                case RpcDataType.Bytes:
                case RpcDataType.Stream:
                    return typedData.Bytes.ToByteArray();
                case RpcDataType.String:
                    return typedData.String;
                case RpcDataType.Json:
                    return JsonConvert.DeserializeObject(typedData.Json, _datetimeSerializerSettings);
                case RpcDataType.Http:
                    return Utilities.ConvertFromHttpMessageToExpando(typedData.Http);
                case RpcDataType.Int:
                    return typedData.Int;
                case RpcDataType.Double:
                    return typedData.Double;
                case RpcDataType.None:
                    return null;
                default:
                    // TODO better exception
                    throw new InvalidOperationException("Unknown RpcDataType");
            }
        }

        internal static TypedData ToRpcCollection(this object value)
        {
            TypedData typedData = null;
            if (value is byte[][] arrBytes)
            {
                typedData = new TypedData();
                CollectionBytes collectionBytes = new CollectionBytes();
                foreach (byte[] element in arrBytes)
                {
                    if (element != null)
                    {
                        collectionBytes.Bytes.Add(ByteString.CopyFrom(element));
                    }
                }
                typedData.CollectionBytes = collectionBytes;
            }
            else if (value is string[] arrString)
            {
                typedData = new TypedData();
                CollectionString collectionString = new CollectionString();
                foreach (string element in arrString)
                {
                    if (!string.IsNullOrEmpty(element))
                    {
                        collectionString.String.Add(element);
                    }
                }
                typedData.CollectionString = collectionString;
            }
            else if (value is double[] arrDouble)
            {
                typedData = new TypedData();
                CollectionDouble collectionDouble = new CollectionDouble();
                foreach (double element in arrDouble)
                {
                    collectionDouble.Double.Add(element);
                }
                typedData.CollectionDouble = collectionDouble;
            }
            else if (value is long[] arrLong)
            {
                typedData = new TypedData();
                CollectionSInt64 collectionLong = new CollectionSInt64();
                foreach (long element in arrLong)
                {
                    collectionLong.Sint64.Add(element);
                }
                typedData.CollectionSint64 = collectionLong;
            }
            return typedData;
        }

        internal static TypedData ToRpcPrimitive(this object value)
        {
            TypedData typedData = null;
            if (value is byte[] arr)
            {
                typedData = new TypedData();
                typedData.Bytes = ByteString.CopyFrom(arr);
            }
            else if (value is JObject jobj)
            {
                typedData = new TypedData();
                typedData.Json = jobj.ToString();
            }
            else if (value is string str)
            {
                typedData = new TypedData();
                typedData.String = str;
            }
            else if (value is long lng)
            {
                typedData = new TypedData();
                typedData.Int = lng;
            }
            else if (value is double dbl)
            {
                typedData = new TypedData();
                typedData.Double = dbl;
            }
            return typedData;
        }

        internal static TypedData ToRpcHttp(this object value, ILogger logger, Capabilities capabilities)
        {
            TypedData typedData = null;
            if (value is HttpRequest request)
            {
                typedData = new TypedData();
                var http = new RpcHttp()
                {
                    Url = $"{(request.IsHttps ? "https" : "http")}://{request.Host.ToString()}{request.Path.ToString()}{request.QueryString.ToString()}", // [http|https]://{url}{path}{query}
                    Method = request.Method.ToString()
                };
                typedData.Http = http;

                http.RawBody = null;
                foreach (var pair in request.Query)
                {
                    if (!string.IsNullOrEmpty(pair.Value.ToString()))
                    {
                        http.Query.Add(pair.Key, pair.Value.ToString());
                    }
                }

                foreach (var pair in request.Headers)
                {
                    http.Headers.Add(pair.Key.ToLowerInvariant(), pair.Value.ToString());
                }

                if (request.HttpContext.Items.TryGetValue(HttpExtensionConstants.AzureWebJobsHttpRouteDataKey, out object routeData))
                {
                    Dictionary<string, object> parameters = (Dictionary<string, object>)routeData;
                    foreach (var pair in parameters)
                    {
                        if (pair.Value != null)
                        {
                            http.Params.Add(pair.Key, pair.Value.ToString());
                        }
                    }
                }

                // parse ClaimsPrincipal if exists
                if (request.HttpContext?.User?.Identities != null)
                {
                    logger.LogDebug("HttpContext has ClaimsPrincipal; parsing to gRPC.");
                    foreach (var id in request.HttpContext.User.Identities)
                    {
                        var rpcClaimsIdentity = new RpcClaimsIdentity();
                        if (id.AuthenticationType != null)
                        {
                            rpcClaimsIdentity.AuthenticationType = new NullableString { Value = id.AuthenticationType };
                        }

                        if (id.NameClaimType != null)
                        {
                            rpcClaimsIdentity.NameClaimType = new NullableString { Value = id.NameClaimType };
                        }

                        if (id.RoleClaimType != null)
                        {
                            rpcClaimsIdentity.RoleClaimType = new NullableString { Value = id.RoleClaimType };
                        }

                        foreach (var claim in id.Claims)
                        {
                            if (claim.Type != null && claim.Value != null)
                            {
                                rpcClaimsIdentity.Claims.Add(new RpcClaim { Value = claim.Value, Type = claim.Type });
                            }
                        }

                        http.Identities.Add(rpcClaimsIdentity);
                    }
                }

                // parse request body as content-type
                if (request.Body != null && request.ContentLength > 0)
                {
                    object body = null;
                    string rawBodyString = null;
                    byte[] bytes = RequestBodyToBytes(request);

                    MediaTypeHeaderValue mediaType = null;
                    if (MediaTypeHeaderValue.TryParse(request.ContentType, out mediaType))
                    {
                        if (string.Equals(mediaType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                        {
                            var jsonReader = new StreamReader(request.Body, Encoding.UTF8);
                            rawBodyString = jsonReader.ReadToEnd();
                            try
                            {
                                body = JsonConvert.DeserializeObject(rawBodyString);
                            }
                            catch (JsonException)
                            {
                                body = rawBodyString;
                            }
                        }
                        else if (string.Equals(mediaType.MediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                            mediaType.MediaType.IndexOf("multipart/", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            body = bytes;
                            if (!IsRawBodyBytesRequested(capabilities))
                            {
                                rawBodyString = Encoding.UTF8.GetString(bytes);
                            }
                        }
                    }
                    // default if content-tye not found or recognized
                    if (body == null && rawBodyString == null)
                    {
                        var reader = new StreamReader(request.Body, Encoding.UTF8);
                        body = rawBodyString = reader.ReadToEnd();
                    }

                    request.Body.Position = 0;
                    http.Body = body.ToRpc(logger, capabilities);
                    if (IsRawBodyBytesRequested(capabilities))
                    {
                        http.RawBody = bytes.ToRpc(logger, capabilities);
                    }
                    else
                    {
                        http.RawBody = rawBodyString.ToRpc(logger, capabilities);
                    }
                }
            }
            return typedData;
        }

        public static TypedData ToRpc(this object value, ILogger logger, Capabilities capabilities)
        {
            TypedData typedData = null;
            if (value == null)
            {
                typedData = new TypedData();
                return typedData;
            }
            typedData = value.ToRpcPrimitive();
            if (typedData == null)
            {
                typedData = value.ToRpcPrimitive();
            }
            if (typedData == null)
            {
                typedData = value.ToRpcHttp(logger, capabilities);
            }
            if (typedData == null && IsTypeDataCollectionSupported(capabilities))
            {
                typedData = value.ToRpcCollection();
            }
            if (typedData == null)
            {
                // attempt POCO / array of pocos
                typedData = new TypedData();
                try
                {
                    typedData.Json = JsonConvert.SerializeObject(value);
                }
                catch
                {
                    typedData.String = value.ToString();
                }
            }
            return typedData;
        }

        private static bool IsRawBodyBytesRequested(Capabilities capabilities)
        {
            return capabilities.GetCapabilityState(LanguageWorkerConstants.RawHttpBodyBytes) != null;
        }

        private static bool IsTypeDataCollectionSupported(Capabilities capabilities)
        {
            string typeDataCollectionSupported = capabilities.GetCapabilityState(LanguageWorkerConstants.TypedDataCollectionSupported);
            if (!string.IsNullOrEmpty(typeDataCollectionSupported))
            {
                return true;
            }
            return false;
        }

        internal static byte[] RequestBodyToBytes(HttpRequest request)
        {
            var length = Convert.ToInt32(request.ContentLength);
            var bytes = new byte[length];
            request.Body.Read(bytes, 0, length);
            request.Body.Position = 0;
            return bytes;
        }

        public static BindingInfo ToBindingInfo(this BindingMetadata bindingMetadata)
        {
            BindingInfo bindingInfo = new BindingInfo
            {
                Direction = (BindingInfo.Types.Direction)bindingMetadata.Direction,
                Type = bindingMetadata.Type
            };

            if (bindingMetadata.DataType != null)
            {
                bindingInfo.DataType = (BindingInfo.Types.DataType)bindingMetadata.DataType;
            }

            return bindingInfo;
        }
    }
}