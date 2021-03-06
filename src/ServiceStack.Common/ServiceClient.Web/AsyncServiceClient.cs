using System;
using System.IO;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using ServiceStack.Logging;
using ServiceStack.ServiceHost;
using ServiceStack.Text;

namespace ServiceStack.ServiceClient.Web
{
	/**
	 * Need to provide async request options
	 * http://msdn.microsoft.com/en-us/library/86wf6409(VS.71).aspx
	 */

	public class AsyncServiceClient
	{
		private static readonly ILog Log = LogManager.GetLogger(typeof(AsyncServiceClient));
		private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

		public static Action<HttpWebRequest> HttpWebRequestFilter { get; set; }

		const int BufferSize = 4096;

		public ICredentials Credentials { get; set; }

		internal class RequestState<TResponse> : IDisposable
		{
			public RequestState()
			{
				BufferRead = new byte[BufferSize];
				TextData = new StringBuilder();
				BytesData = new MemoryStream(BufferSize);
				WebRequest = null;
				ResponseStream = null;
			}

			public string HttpMethod;

			public string Url;

			public StringBuilder TextData;

			public MemoryStream BytesData;

			public byte[] BufferRead;

			public object Request;

			public HttpWebRequest WebRequest;

			public HttpWebResponse WebResponse;

			public Stream ResponseStream;

			public int Completed;

			public int RequestCount;

			public Timer Timer;

			public Action<TResponse> OnSuccess;

			public Action<TResponse, Exception> OnError;

			public void HandleError(TResponse response, Exception ex)
			{
				if (OnError != null)
				{
					OnError(response, ex);
				}
			}

			public void StartTimer(TimeSpan timeOut)
			{
				this.Timer = new Timer(this.TimedOut, this, (int)timeOut.TotalMilliseconds, System.Threading.Timeout.Infinite);
			}

			public void TimedOut(object state)
			{
				if (Interlocked.Increment(ref Completed) == 1)
				{
					if (this.WebRequest != null)
					{
						this.WebRequest.Abort();
					}
				}
				this.Timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
				this.Timer.Dispose();
				this.Dispose();
			}

			public void Dispose()
			{
				if (this.BytesData == null) return;
				this.BytesData.Dispose();
				this.BytesData = null;
			}
		}

		public string UserName { get; set; }
	
		public string Password { get; set; }

		public void SetCredentials(string userName, string password)
		{
			this.UserName = userName;
			this.Password = password;
		}

		public TimeSpan? Timeout { get; set; }

		public string ContentType { get; set; }

		public StreamSerializerDelegate StreamSerializer { get; set; }

		public StreamDeserializerDelegate StreamDeserializer { get; set; }

		public void SendAsync<TResponse>(string httpMethod, string absoluteUrl, object request,
			Action<TResponse> onSuccess, Action<TResponse, Exception> onError)
		{
			SendWebRequest(httpMethod, absoluteUrl, request, onSuccess, onError);
		}

		private RequestState<TResponse> SendWebRequest<TResponse>(string httpMethod, string absoluteUrl, object request, 
			Action<TResponse> onSuccess, Action<TResponse, Exception> onError)
		{
			if (httpMethod == null) throw new ArgumentNullException("httpMethod");

			var requestUri = absoluteUrl;
			var httpGetOrDelete = (httpMethod == "GET" || httpMethod == "DELETE");
			var hasQueryString = request != null && httpGetOrDelete;
			if (hasQueryString)
			{
				var queryString = QueryStringSerializer.SerializeToString(request);
				if (!string.IsNullOrEmpty(queryString))
				{
					requestUri += "?" + queryString;
				}
			}

			var webRequest = (HttpWebRequest)WebRequest.Create(requestUri);

			var requestState = new RequestState<TResponse>
			{
				HttpMethod = httpMethod,
				Url = requestUri,
				WebRequest = webRequest,
				Request = request,
				OnSuccess = onSuccess,
				OnError = onError,
			};
			requestState.StartTimer(this.Timeout.GetValueOrDefault(DefaultTimeout));

			SendWebRequestAsync(httpMethod, request, requestState, webRequest);

			return requestState;
		}

		private void SendWebRequestAsync<TResponse>(string httpMethod, object request, 
			RequestState<TResponse> requestState, HttpWebRequest webRequest)
		{
			var httpGetOrDelete = (httpMethod == "GET" || httpMethod == "DELETE");
			webRequest.Accept = string.Format("{0}, */*", ContentType);
			webRequest.Method = httpMethod;

			if (this.Credentials != null)
			{
				webRequest.Credentials = this.Credentials;
			}


			if (HttpWebRequestFilter != null)
			{
				HttpWebRequestFilter(webRequest);
			}

			if (!httpGetOrDelete && request != null)
			{
				webRequest.ContentType = ContentType;
				webRequest.BeginGetRequestStream(RequestCallback<TResponse>, requestState);
			}
			else
			{
				requestState.WebRequest.BeginGetResponse(ResponseCallback<TResponse>, requestState);
			}
		}

		private void RequestCallback<T>(IAsyncResult asyncResult)
		{
			var requestState = (RequestState<T>)asyncResult.AsyncState;
			try
			{
				var req = requestState.WebRequest;

				var postStream = req.EndGetRequestStream(asyncResult);
				StreamSerializer(null, requestState.Request, postStream);
				postStream.Close();
				requestState.WebRequest.BeginGetResponse(ResponseCallback<T>, requestState);
			}
			catch (Exception ex)
			{
				HandleResponseError(ex, requestState);
			}
		}

		private void ResponseCallback<T>(IAsyncResult asyncResult)
		{
			var requestState = (RequestState<T>)asyncResult.AsyncState;
			try
			{
				var webRequest = requestState.WebRequest;
				requestState.WebResponse = (HttpWebResponse)webRequest.EndGetResponse(asyncResult);

				// Read the response into a Stream object.
				var responseStream = requestState.WebResponse.GetResponseStream();
				requestState.ResponseStream = responseStream;

				responseStream.BeginRead(requestState.BufferRead, 0, BufferSize, ReadCallBack<T>, requestState);
				return;
			}
			catch (Exception ex)
			{
				var firstCall = Interlocked.Increment(ref requestState.RequestCount) == 1;
				if (firstCall && WebRequestUtils.ShouldAuthenticate(ex, this.UserName, this.Password))
				{
					try
					{
						requestState.WebRequest = (HttpWebRequest)WebRequest.Create(requestState.Url);

						requestState.WebRequest.AddBasicAuth(this.UserName, this.Password);

						SendWebRequestAsync(
							requestState.HttpMethod, requestState.Request,
							requestState, requestState.WebRequest);
					}
					catch (Exception /*subEx*/)
					{
						HandleResponseError(ex, requestState);
					}
					return;
				}

				HandleResponseError(ex, requestState);
			}
		}

		private void ReadCallBack<T>(IAsyncResult asyncResult)
		{
			var requestState = (RequestState<T>)asyncResult.AsyncState;
			try
			{
				var responseStream = requestState.ResponseStream;
				int read = responseStream.EndRead(asyncResult);

				if (read > 0)
				{

					requestState.BytesData.Write(requestState.BufferRead, 0, read);
					responseStream.BeginRead(
						requestState.BufferRead, 0, BufferSize, ReadCallBack<T>, requestState);

					return;
				}

				Interlocked.Increment(ref requestState.Completed);

				var response = default(T);
				try
				{
					requestState.BytesData.Position = 0;
					using (var reader = requestState.BytesData)
					{
						response = (T)this.StreamDeserializer(typeof(T), reader);
					}

					if (requestState.OnSuccess != null)
					{
						requestState.OnSuccess(response);
					}
				}
				catch (Exception ex)
				{
					Log.Debug(string.Format("Error Reading Response Error: {0}", ex.Message), ex);
					requestState.HandleError(default(T), ex);
				}
				finally
				{
					responseStream.Close();
				}
			}
			catch (Exception ex)
			{
				HandleResponseError(ex, requestState);
			}
		}

		private void HandleResponseError<TResponse>(Exception exception, RequestState<TResponse> requestState)
		{
			var webEx = exception as WebException;
			if (webEx != null && webEx.Status == WebExceptionStatus.ProtocolError)
			{
				var errorResponse = ((HttpWebResponse)webEx.Response);
				Log.Error(webEx);
				Log.DebugFormat("Status Code : {0}", errorResponse.StatusCode);
				Log.DebugFormat("Status Description : {0}", errorResponse.StatusDescription);

				var serviceEx = new WebServiceException(errorResponse.StatusDescription)
				{
					StatusCode = (int)errorResponse.StatusCode,
				};

				try
				{
					using (var stream = errorResponse.GetResponseStream())
					{
						//Uncomment to Debug exceptions:
						//var strResponse = new StreamReader(stream).ReadToEnd();
						//Console.WriteLine("Response: " + strResponse);
						//stream.Position = 0;

						serviceEx.ResponseDto = this.StreamDeserializer(typeof(TResponse), stream);
						requestState.HandleError((TResponse)serviceEx.ResponseDto, serviceEx);
					}
				}
				catch (Exception innerEx)
				{
					// Oh, well, we tried
					Log.Debug(string.Format("WebException Reading Response Error: {0}", innerEx.Message), innerEx);
					requestState.HandleError(default(TResponse), new WebServiceException(errorResponse.StatusDescription, innerEx)
						{
							StatusCode = (int)errorResponse.StatusCode,
						});
				}
				return;
			}

			var authEx = exception as AuthenticationException;
			if (authEx != null)
			{
				var customEx = WebRequestUtils.CreateCustomException(requestState.Url, authEx);

				Log.Debug(string.Format("AuthenticationException: {0}", customEx.Message), customEx);
				requestState.HandleError(default(TResponse), authEx);
			}

			Log.Debug(string.Format("Exception Reading Response Error: {0}", exception.Message), exception);
			requestState.HandleError(default(TResponse), exception);
		}

		public void Dispose() { }
	}
}