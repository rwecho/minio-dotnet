﻿/*
 * MinIO .NET Library for Amazon S3 Compatible Cloud Storage,
 * (C) 2017, 2018, 2019, 2020 MinIO, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Minio.DataModel.Tracing;
using Minio.Exceptions;
using Minio.Helper;

namespace Minio
{
    public partial class MinioClient
    {
        // Save Credentials from user
        internal string AccessKey { get; private set; }
        internal string SecretKey { get; private set; }
        internal string BaseUrl { get; private set; }

        // Reconstructed endpoint with scheme and host.In the case of Amazon, this url
        // is the virtual style path or location based endpoint
        internal string Endpoint { get; private set; }
        internal string Region;
        internal string SessionToken { get; private set; }
        // Corresponding URI for above endpoint
        internal Uri uri;

        // Indicates if we are using HTTPS or not
        internal bool Secure { get; private set; }

        // Handler for task retry policy
        internal RetryPolicyHandlingDelegate retryPolicyHandler;

        // Cache holding bucket to region mapping for buckets seen so far.
        internal BucketRegionCache regionCache;

        private IRequestLogger logger;

        // Enables HTTP tracing if set to true
        private bool trace = false;

        private int requestTimeout;

        private const string RegistryAuthHeaderKey = "X-Registry-Auth";
        private HttpClient HttpClient { get; } = new HttpClient();

        internal readonly IEnumerable<ApiResponseErrorHandlingDelegate> NoErrorHandlers = Enumerable.Empty<ApiResponseErrorHandlingDelegate>();

        /// <summary>
        /// Default error handling delegate
        /// </summary>
        private readonly ApiResponseErrorHandlingDelegate _defaultErrorHandlingDelegate = (response) =>
        {
            if (response.StatusCode < HttpStatusCode.OK || response.StatusCode >= HttpStatusCode.BadRequest)
            {
                ParseError(response);
            }
        };

        private static string SystemUserAgent
        {
            get
            {
                string release = "minio-dotnet/1.0.9";
#if NET46
                string arch = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
                return $"MinIO ({Environment.OSVersion};{arch}) {release}";
#else
                string arch = RuntimeInformation.OSArchitecture.ToString();
                return $"MinIO ({RuntimeInformation.OSDescription};{arch}) {release}";
#endif
            }
        }

        private string CustomUserAgent = string.Empty;

        /// <summary>
        /// Returns the User-Agent header for the request
        /// </summary>
        public string FullUserAgent
        {
            get
            {
                return $"{SystemUserAgent} {CustomUserAgent}";
            }
        }

        /// <summary>
        /// Resolve region bucket resides in.
        /// </summary>
        /// <param name="bucketName"></param>
        /// <returns></returns>
        private async Task<string> GetRegion(string bucketName)
        {
            // Use user specified region in client constructor if present
            if (this.Region != string.Empty)
            {
                return this.Region;
            }

            // pick region from endpoint if present
            string region = Regions.GetRegionFromEndpoint(this.Endpoint);

            // Pick region from location HEAD request
            if (region == string.Empty)
            {
                if (!BucketRegionCache.Instance.Exists(bucketName))
                {
                    region = await BucketRegionCache.Instance.Update(this, bucketName).ConfigureAwait(false);
                }
                else
                {
                    region = BucketRegionCache.Instance.Region(bucketName);
                }
            }
            // Default to us-east-1 if region could not be found
            return (region == string.Empty) ? "us-east-1" : region;
        }

        /// <summary>
        /// Constructs a HttpRequestMessage builder. For AWS, this function has the side-effect of overriding the baseUrl
        /// in the HttpClient with region specific host path or virtual style path.
        /// </summary>
        /// <param name="method">HTTP method</param>
        /// <param name="bucketName">Bucket Name</param>
        /// <param name="objectName">Object Name</param>
        /// <param name="headerMap">headerMap</param>
        /// <param name="contentType">Content Type</param>
        /// <param name="body">request body</param>
        /// <param name="resourcePath">query string</param>
        /// <returns>A HttpRequestMessage builder</returns>
        internal async Task<HttpRequestMessageBuilder> CreateRequest(
            HttpMethod method,
            string bucketName = null, 
            string objectName = null,
            Dictionary<string, string> headerMap = null,
            string contentType = "application/octet-stream",
            byte[] body = null, string resourcePath = null)
        {
            string region = string.Empty;
            if (bucketName != null)
            {
                utils.ValidateBucketName(bucketName);
                // Fetch correct region for bucket
                region = await GetRegion(bucketName).ConfigureAwait(false);
            }

            if (objectName != null)
            {
                utils.ValidateObjectName(objectName);
            }

            // This section reconstructs the url with scheme followed by location specific endpoint (s3.region.amazonaws.com)
            // or Virtual Host styled endpoint (bucketname.s3.region.amazonaws.com) for Amazon requests.
            string resource = string.Empty;
            bool usePathStyle = false;
            if (bucketName != null)
            {
                if (s3utils.IsAmazonEndPoint(this.BaseUrl))
                {
                    usePathStyle = false;

                    if (method == HttpMethod.Put && objectName == null && resourcePath == null)
                    {
                        // use path style for make bucket to workaround "AuthorizationHeaderMalformed" error from s3.amazonaws.com
                        usePathStyle = true;
                    }
                    else if (resourcePath != null && resourcePath.Contains("location"))
                    {
                        // use path style for location query
                        usePathStyle = true;
                    }
                    else if (bucketName != null && bucketName.Contains(".") && this.Secure)
                    {
                        // use path style where '.' in bucketName causes SSL certificate validation error
                        usePathStyle = true;
                    }

                    if (usePathStyle)
                    {
                        resource += utils.UrlEncode(bucketName) + "/";
                    }
                }
                else
                {
                    resource += utils.UrlEncode(bucketName) + "/";
                }
            }

            // Set Target URL
            Uri requestUrl = RequestUtil.MakeTargetURL(this.BaseUrl, this.Secure, bucketName, region, usePathStyle);

            if (objectName != null)
            {
                resource += utils.EncodePath(objectName);
            }

            // Append query string passed in
            if (resourcePath != null)
            {
                resource += resourcePath;
            }

            var messageBuilder = new HttpRequestMessageBuilder(method, requestUrl, resource);

            if (body != null)
            {
                messageBuilder.SetBody(body);
            }

            if (headerMap != null)
            {
                foreach (var entry in headerMap)
                {
                    messageBuilder.AddHeaderParameter(entry.Key, entry.Value);
                }
            }

            return messageBuilder;
        }

        /// <summary>
        /// Sets app version and name. Used by RestSharp for constructing User-Agent header in all HTTP requests
        /// </summary>
        /// <param name="appName"></param>
        /// <param name="appVersion"></param>
        public void SetAppInfo(string appName, string appVersion)
        {
            if (string.IsNullOrEmpty(appName))
            {
                throw new ArgumentException("Appname cannot be null or empty", nameof(appName));
            }

            if (string.IsNullOrEmpty(appVersion))
            {
                throw new ArgumentException("Appversion cannot be null or empty", nameof(appVersion));
            }

            this.CustomUserAgent = $"{appName}/{appVersion}";
        }

        /// <summary>
        /// Creates and returns an Cloud Storage client
        /// </summary>
        /// <param name="endpoint">Location of the server, supports HTTP and HTTPS</param>
        /// <param name="accessKey">Access Key for authenticated requests (Optional, can be omitted for anonymous requests)</param>
        /// <param name="secretKey">Secret Key for authenticated requests (Optional, can be omitted for anonymous requests)</param>
        /// <param name="region">Optional custom region</param>
        /// <param name="sessionToken">Optional session token</param>
        /// <returns>Client initialized with user credentials</returns>
        public MinioClient(string endpoint, string accessKey = "", string secretKey = "", string region = "",
            string sessionToken = "")
        {
            this.Secure = false;

            // Save user entered credentials
            this.BaseUrl = endpoint;
            this.AccessKey = accessKey;
            this.SecretKey = secretKey;
            this.SessionToken = sessionToken;
            this.Region = region;
            // Instantiate a region cache
            this.regionCache = BucketRegionCache.Instance;

            if (string.IsNullOrEmpty(this.BaseUrl))
            {
                throw new InvalidEndpointException("Endpoint cannot be empty.");
            }

            string host = this.BaseUrl;
            var scheme = this.Secure ? utils.UrlEncode("https") : utils.UrlEncode("http");
            // This is the actual url pointed to for all HTTP requests
            this.Endpoint = string.Format("{0}://{1}", scheme, host);
            this.uri = RequestUtil.GetEndpointURL(this.BaseUrl, this.Secure);
            RequestUtil.ValidateEndpoint(this.uri, this.Endpoint);

            this.HttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", this.FullUserAgent);
        }

        /// <summary>
        /// Connects to Cloud Storage with HTTPS if this method is invoked on client object
        /// </summary>
        /// <returns></returns>
        public MinioClient WithSSL()
        {
            this.Secure = true;
            Uri secureUrl = RequestUtil.MakeTargetURL(this.BaseUrl, this.Secure);
            return this;
        }

        /// <summary>
        /// Uses webproxy for all requests if this method is invoked on client object
        /// </summary>
        /// <returns></returns>
        public MinioClient WithProxy(IWebProxy proxy)
        {
            // todo implement proxy;
            return this;
        }

        /// <summary>
        /// Uses the set timeout for all requests if this method is invoked on client object
        /// </summary>
        /// <param name="timeout">Timeout in milliseconds.</param>
        /// <returns></returns>
        public MinioClient WithTimeout(int timeout)
        {
            requestTimeout = timeout;
            return this;
        }

        /// <summary>
        /// Allows to add retry policy handler
        /// </summary>
        /// <param name="retryPolicyHandler">Delegate that will wrap execution of http client requests.</param>
        public MinioClient WithRetryPolicy(RetryPolicyHandlingDelegate retryPolicyHandler)
        {
            this.retryPolicyHandler = retryPolicyHandler;
            return this;
        }

        /// <summary>
        /// Actual doer that executes the http request to the server
        /// </summary>
        /// <param name="errorHandlers">List of handlers to override default handling</param>
        /// <param name="requestBuilder">The build of HttpRequestMessage builder</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>Response result</returns>
        internal Task<ResponseResult> ExecuteTaskAsync(
            IEnumerable<ApiResponseErrorHandlingDelegate> errorHandlers,
            HttpRequestMessageBuilder requestBuilder,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecuteWithRetry(
                () => ExecuteTaskCoreAsync(errorHandlers, requestBuilder, cancellationToken));
        }

        private async Task<ResponseResult> ExecuteTaskCoreAsync(
            IEnumerable<ApiResponseErrorHandlingDelegate> errorHandlers,
            HttpRequestMessageBuilder requestBuilder,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var startTime = DateTime.Now;
            // Logs full url when HTTPtracing is enabled.
            if (this.trace)
            {
                var fullUrl = requestBuilder.RequestUri;
                Console.WriteLine($"Full URL of Request {fullUrl}");
            }

            var v4Authenticator = new V4Authenticator(this.Secure, this.AccessKey, this.SecretKey, this.Region,
                this.SessionToken);

            requestBuilder.AddHeaderParameter("Authorization", v4Authenticator.Authenticate(requestBuilder));
            var request = requestBuilder.Request;
            ResponseResult responseResult;
            try
            {
                if (requestTimeout > 0)
                {
                    this.HttpClient.Timeout = new TimeSpan(0, 0, 0, 0, requestTimeout);
                }
                var response = await this.HttpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                responseResult = new ResponseResult(request, response);
            }
            catch (OperationCanceledException )
            {
                throw;
            }
            catch (Exception e)
            {
                responseResult = new ResponseResult(request ,e);
            }

            this.HandleIfErrorResponse(responseResult, errorHandlers, startTime);
            return responseResult;
        }

        /// <summary>
        /// Parse response errors if any and return relevant error messages
        /// </summary>
        /// <param name="response"></param>
        internal static void ParseError(ResponseResult response)
        {
            if (response == null)
            {
                throw new ConnectionException("Response is nil. Please report this issue https://github.com/minio/minio-dotnet/issues", response);
            }

            if (HttpStatusCode.Redirect.Equals(response.StatusCode) || HttpStatusCode.TemporaryRedirect.Equals(response.StatusCode) || HttpStatusCode.MovedPermanently.Equals(response.StatusCode))
            {
                throw new RedirectionException("Redirection detected. Please report this issue https://github.com/minio/minio-dotnet/issues");
            }

            if (string.IsNullOrWhiteSpace(response.Content))
            {
                ParseErrorNoContent(response);
                return;
            }

            ParseErrorFromContent(response);
        }

        private static void ParseErrorNoContent(ResponseResult response)
        {
            if (HttpStatusCode.Forbidden.Equals(response.StatusCode)
                || HttpStatusCode.BadRequest.Equals(response.StatusCode)
                || HttpStatusCode.NotFound.Equals(response.StatusCode)
                || HttpStatusCode.MethodNotAllowed.Equals(response.StatusCode)
                || HttpStatusCode.NotImplemented.Equals(response.StatusCode))
            {
                ParseWellKnownErrorNoContent(response);
            }

            if (response.StatusCode == 0)
                throw new ConnectionException("Connection error" , response);

            throw new InternalClientException(
                "Unsuccessful response from server without XML", response);
        }

        private static void ParseWellKnownErrorNoContent(ResponseResult response)
        {
            MinioException error = null;
            ErrorResponse errorResponse = new ErrorResponse();

            foreach (var parameter in response.Headers)
            {
                if (parameter.Key.Equals("x-amz-id-2", StringComparison.CurrentCultureIgnoreCase))
                {
                    errorResponse.HostId = parameter.Value.ToString();
                }

                if (parameter.Key.Equals("x-amz-request-id", StringComparison.CurrentCultureIgnoreCase))
                {
                    errorResponse.RequestId = parameter.Value.ToString();
                }

                if (parameter.Key.Equals("x-amz-bucket-region", StringComparison.CurrentCultureIgnoreCase))
                {
                    errorResponse.BucketRegion = parameter.Value.ToString();
                }
            }

            var pathAndQuery = response.Request.RequestUri.PathAndQuery;
            var host = response.Request.RequestUri.Host;
            errorResponse.Resource = pathAndQuery;

            // zero, one or two segments
            var resourceSplits = pathAndQuery.Split(new[] { '/' }, 2, StringSplitOptions.RemoveEmptyEntries);

            if (HttpStatusCode.NotFound.Equals(response.StatusCode))
            {
                int pathLength = resourceSplits.Length;
                bool isAWS = host.EndsWith("s3.amazonaws.com");
                bool isVirtual = isAWS && !host.StartsWith("s3.amazonaws.com");

                if (pathLength > 1)
                {
                    var objectName = resourceSplits[1];
                    errorResponse.Code = "NoSuchKey";
                    error = new ObjectNotFoundException(objectName, "Not found.");
                }
                else if (pathLength == 1)
                {
                    var resource = resourceSplits[0];

                    if (isAWS && isVirtual && pathAndQuery != string.Empty)
                    {
                        errorResponse.Code = "NoSuchKey";
                        error = new ObjectNotFoundException(resource, "Not found.");
                    }
                    else
                    {
                        errorResponse.Code = "NoSuchBucket";
                        BucketRegionCache.Instance.Remove(resource);
                        error = new BucketNotFoundException(resource, "Not found.");
                    }
                }
                else
                {
                    error = new InternalClientException("404 without body resulted in path with less than two components", response);
                }
            }
            else if (HttpStatusCode.BadRequest.Equals(response.StatusCode))
            {
                int pathLength = resourceSplits.Length;

                if (pathLength > 1)
                {
                    var objectName = resourceSplits[1];
                    errorResponse.Code = "InvalidObjectName";
                    error = new InvalidObjectNameException(objectName, "Invalid object name.");
                }
                else
                {
                    error = new InternalClientException("400 without body resulted in path with less than two components", response);
                }
            }
            else if (HttpStatusCode.Forbidden.Equals(response.StatusCode))
            {
                errorResponse.Code = "Forbidden";
                error = new AccessDeniedException("Access denied on the resource: " + pathAndQuery);
            }

            error.Response = errorResponse;
            throw error;
        }

        private static void ParseErrorFromContent(ResponseResult response)
        {
            if (response.StatusCode.Equals(HttpStatusCode.NotFound)
                && response.Request.RequestUri.PathAndQuery.EndsWith("?location")
                && response.Request.Method.Equals(HttpMethod.Get))
            {
                var bucketName = response.Request.RequestUri.PathAndQuery.Split('?')[0];
                BucketRegionCache.Instance.Remove(bucketName);
                throw new BucketNotFoundException(bucketName, "Not found.");
            }

            var contentBytes = response.ContentBytes;
            var contentString = response.Content;
            var stream = new MemoryStream(contentBytes);
            ErrorResponse errResponse = (ErrorResponse)new XmlSerializer(typeof(ErrorResponse)).Deserialize(stream);

            // Handle XML response for Bucket Policy not found case
            if (response.StatusCode.Equals(HttpStatusCode.NotFound)
                && response.Request.RequestUri.PathAndQuery.EndsWith("?policy")
                && response.Request.Method.Equals(HttpMethod.Get)
                && errResponse.Code == "NoSuchBucketPolicy")
            {
                throw new ErrorResponseException(errResponse, response)
                {
                    XmlError = contentString
                };
            }

            if (response.StatusCode.Equals(HttpStatusCode.NotFound)
                && errResponse.Code == "NoSuchBucket")
            {
                throw new BucketNotFoundException(errResponse.BucketName, "Not found.");
            }

            throw new UnexpectedMinioException(errResponse.Message)
            {
                Response = errResponse,
                XmlError = contentString
            };
        }

        /// <summary>
        /// Delegate errors to handlers
        /// </summary>
        /// <param name="response"></param>
        /// <param name="handlers"></param>
        /// <param name="startTime"></param>
        private void HandleIfErrorResponse(ResponseResult response, IEnumerable<ApiResponseErrorHandlingDelegate> handlers, DateTime startTime)
        {
            // Logs Response if HTTP tracing is enabled
            if (this.trace)
            {
                DateTime now = DateTime.Now;
                LogRequest(response.Request, response, (now - startTime).TotalMilliseconds);
            }

            if (handlers == null)
            {
                throw new ArgumentNullException(nameof(handlers));
            }

            // Run through handlers passed to take up error handling
            foreach (var handler in handlers)
            {
                handler(response);
            }

            // Fall back default error handler
            _defaultErrorHandlingDelegate(response);
        }

        /// <summary>
        /// Sets HTTP tracing On.Writes output to Console
        /// </summary>
        public void SetTraceOn(IRequestLogger logger = null)
        {
            this.logger = logger ?? new DefaultRequestLogger();
            this.trace = true;
        }

        /// <summary>
        /// Sets HTTP tracing Off.
        /// </summary>
        public void SetTraceOff()
        {
            this.trace = false;
        }

        /// <summary>
        /// Logs the request sent to server and corresponding response
        /// </summary>
        /// <param name="request"></param>
        /// <param name="response"></param>
        /// <param name="durationMs"></param>
        private void LogRequest(HttpRequestMessage request, ResponseResult response, double durationMs)
        {
            var requestToLog = new RequestToLog
            {
                resource = request.RequestUri.PathAndQuery,
                // Parameters are custom anonymous objects in order to have the parameter type as a nice string
                // otherwise it will just show the enum value
                parameters = request.Headers.Select(parameter => new RequestParameter
                {
                    name = parameter.Key,
                    value = parameter.Value,
                }),
                // ToString() here to have the method as a nice string otherwise it will just show the enum value
                method = request.Method.ToString(),
                // This will generate the actual Uri used in the request
                uri = request.RequestUri
            };

            var responseToLog = new ResponseToLog
            {
                statusCode = response.StatusCode,
                content = response.Content,
                headers = response.Headers.ToDictionary(o => o.Key, o => string.Join(Environment.NewLine, o.Value)),
                // The Uri that actually responded (could be different from the requestUri if a redirection occurred)
                responseUri = response.Request.RequestUri,
                errorMessage = response.ErrorMessage,
                durationMs = durationMs
            };

            this.logger.LogRequest(requestToLog, responseToLog, durationMs);
        }

        private Task<ResponseResult> ExecuteWithRetry(
            Func<Task<ResponseResult>> executeRequestCallback)
        {
            return retryPolicyHandler == null
                ? executeRequestCallback()
                : retryPolicyHandler(executeRequestCallback);
        }
    }

    internal delegate void ApiResponseErrorHandlingDelegate(ResponseResult response);

    public delegate Task<ResponseResult> RetryPolicyHandlingDelegate(
        Func<Task<ResponseResult>> executeRequestCallback);
}
