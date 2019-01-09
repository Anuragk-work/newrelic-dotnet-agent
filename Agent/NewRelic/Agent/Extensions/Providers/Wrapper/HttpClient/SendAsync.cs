﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using NewRelic.Agent.Extensions.Providers.Wrapper;
using NewRelic.SystemExtensions;

namespace NewRelic.Providers.Wrapper.HttpClient
{
	public class SendAsync : IWrapper
	{
		public const String InstrumentedTypeName = "System.Net.Http.HttpClient";

		public bool IsTransactionRequired => true;

		public CanWrapResponse CanWrap(InstrumentedMethodInfo methodInfo)
		{
			var method = methodInfo.Method;
			if (method.MatchesAny(assemblyName: "System.Net.Http", typeName: InstrumentedTypeName, methodName: "SendAsync"))
			{
				return TaskFriendlySyncContextValidator.CanWrapAsyncMethod("System.Net.Http", "System.Net.Http.HttpClient", method.MethodName);
			}
			else
			{
				return new CanWrapResponse(false);
			}
		}

		public AfterWrappedMethodDelegate BeforeWrappedMethod(InstrumentedMethodCall instrumentedMethodCall, IAgentWrapperApi agentWrapperApi, ITransactionWrapperApi transactionWrapperApi)
		{
			if (instrumentedMethodCall.IsAsync)
			{
				transactionWrapperApi.AttachToAsync();
			}

			var httpRequestMessage = instrumentedMethodCall.MethodCall.MethodArguments.ExtractNotNullAs<HttpRequestMessage>(0);
			var httpClient = (System.Net.Http.HttpClient) instrumentedMethodCall.MethodCall.InvocationTarget;
			var uri = TryGetAbsoluteUri(httpRequestMessage, httpClient);
			if (uri == null)
			{
				// It is possible for RequestUri to be null, but if it is then SendAsync method will eventually throw (which we will see). It would not be valuable to throw another exception here.
				return Delegates.NoOp;
			}
			
			var method = (httpRequestMessage.Method != null ? httpRequestMessage.Method.Method : "<unknown>") ?? "<unknown>";
			var segment = agentWrapperApi.CurrentTransactionWrapperApi.StartExternalRequestSegment(instrumentedMethodCall.MethodCall, uri, method);

			if (agentWrapperApi.Configuration.ForceSynchronousTimingCalculationHttpClient)
			{
				//When segments complete on a thread that is different than the thread of the parent segment,
				//we typically do not deduct the child segment's duration from the parent segment's duration
				//when calculating exclusive time for the parent. In versions of the agent prior to 6.20 this was not
				//the case and at least one customer is complaining about this. We are special-casing this behavior
				//for HttpClient to make this customer happier, and because HttpClient is not a real "async" method.
				//Please refer to the "total time" definition in https://source.datanerd.us/agents/agent-specs/blob/master/Total-Time-Async.md
				//for more information.

				//This pattern should not be copied to other instrumentation without a good reason, because there may be a better
				//pattern to use for that use case.
				segment.DurationShouldBeDeductedFromParent = true;
			}


			// We cannot rely on SerializeHeadersWrapper to attach the headers because it is called on a thread that does not have access to the transaction
			TryAttachHeadersToRequest(agentWrapperApi, httpRequestMessage);

			return Delegates.GetDelegateFor<Task<HttpResponseMessage>>(
				onFailure: segment.End, 
				onSuccess: OnSuccess
			);

			void OnSuccess(Task<HttpResponseMessage> task)
			{
				segment.RemoveSegmentFromCallStack();

				if (task == null)
				{
					return;
				}

				//Since this finishes on a background thread, it is possible it will race the end of
				//the transaction. This line of code prevents the transaction from ending early. 
				transactionWrapperApi.Hold();

				//Do not want to post to the sync context as this library is commonly used with the
				//blocking TPL pattern of .Result or .Wait(). Posting to the sync context will result
				//in recording time waiting for the current unit of work on the sync context to finish.
				task.ContinueWith(responseTask => agentWrapperApi.HandleExceptions(() =>
				{
					TryProcessResponse(agentWrapperApi, responseTask, transactionWrapperApi, segment);
					segment.End();
					transactionWrapperApi.Release();
				}));
			}
		}

		[CanBeNull]
		private static Uri TryGetAbsoluteUri([NotNull] HttpRequestMessage httpRequestMessage, [NotNull] System.Net.Http.HttpClient httpClient)
		{
			// If RequestUri is specified and it is an absolute URI then we should use it
			if (httpRequestMessage.RequestUri?.IsAbsoluteUri == true)
				return httpRequestMessage.RequestUri;

			// If RequestUri is specified but isn't absolute then we need to combine it with the BaseAddress, as long as the BaseAddress is an absolute URI
			if (httpRequestMessage.RequestUri?.IsAbsoluteUri == false && httpClient.BaseAddress?.IsAbsoluteUri == true)
				return new Uri(httpClient.BaseAddress, httpRequestMessage.RequestUri);

			// If only BaseAddress is specified and it is an absolute URI then we can use it instead
			if (httpRequestMessage.RequestUri == null && httpClient.BaseAddress?.IsAbsoluteUri == true)
				return httpClient.BaseAddress;

			// In all other cases we cannot construct a valid absolute URI
			return null;
		}

		private static void TryAttachHeadersToRequest([NotNull] IAgentWrapperApi agentWrapperApi, [NotNull] HttpRequestMessage httpRequestMessage)
		{
			try
			{
				var headers = agentWrapperApi.CurrentTransactionWrapperApi.GetRequestMetadata()
					.Where(header => header.Key != null);

				foreach (var header in headers)
				{
					// "Add" will not replace an existing value, so we must remove it first
					httpRequestMessage.Headers?.Remove(header.Key);
					httpRequestMessage.Headers?.Add(header.Key, header.Value);
				}
			}
			catch (Exception ex)
			{
				agentWrapperApi.HandleWrapperException(ex);
			}
		}

		private static void TryProcessResponse([NotNull] IAgentWrapperApi agentWrapperApi, [CanBeNull] Task<HttpResponseMessage> response, [NotNull] ITransactionWrapperApi transactionWrapperApi, [CanBeNull] ISegment segment)
		{
			try
			{
				if (!ValidTaskResponse(response) || (segment == null))
				{
					return;
				}

				var headers = response?.Result?.Headers?.ToList();
				if (headers == null)
					return;

				var flattenedHeaders = headers.Select(Flatten);

				transactionWrapperApi.ProcessInboundResponse(flattenedHeaders, segment);
			}
			catch (Exception ex)
			{
				agentWrapperApi.HandleWrapperException(ex);
			}
		}

		private static Boolean ValidTaskResponse([CanBeNull] Task<HttpResponseMessage> response)
		{
			return (response?.Status == TaskStatus.RanToCompletion);
		}

		private static KeyValuePair<String, String> Flatten(KeyValuePair<String, IEnumerable<String>> header)
		{
			var key = header.Key;
			var values = header.Value ?? Enumerable.Empty<String>();

			// According to RFC 2616 (http://www.w3.org/Protocols/rfc2616/rfc2616-sec4.html#sec4.2), multi-valued headers can be represented as a single comma-delimited list of values
			var flattenedValues = String.Join(",", values);

			return new KeyValuePair<String, String>(key, flattenedValues);
		}
	}
}