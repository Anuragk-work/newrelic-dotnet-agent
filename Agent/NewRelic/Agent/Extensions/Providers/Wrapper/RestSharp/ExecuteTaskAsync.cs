﻿using System;
using System.Threading.Tasks;
using NewRelic.Agent.Extensions.Providers.Wrapper;

namespace NewRelic.Providers.Wrapper.RestSharp
{
	public class ExecuteTaskAsync : IWrapper
	{
		public bool IsTransactionRequired => true;

		public CanWrapResponse CanWrap(InstrumentedMethodInfo instrumentedMethodInfo)
		{
			//Because this does not leverage the sync context, it does not need to check for the legacy pipepline
			return new CanWrapResponse("NewRelic.Providers.Wrapper.RestSharp.ExecuteTaskAsync".Equals(instrumentedMethodInfo.RequestedWrapperName));
		}

		public AfterWrappedMethodDelegate BeforeWrappedMethod(InstrumentedMethodCall instrumentedMethodCall, IAgentWrapperApi agentWrapperApi, ITransactionWrapperApi transactionWrapperApi)
		{
			if (instrumentedMethodCall.IsAsync)
			{
				transactionWrapperApi.AttachToAsync();
			}

			var restClient = instrumentedMethodCall.MethodCall.InvocationTarget;
			var restRequest = instrumentedMethodCall.MethodCall.MethodArguments[0];

			Uri uri;

			try
			{
				uri = RestSharpHelper.BuildUri(restClient, restRequest);
			}
			catch (Exception)
			{
				//BuildUri will throw an exception in RestSharp if the user does not have BaseUrl set.
				//Since the request will never execute, we will just NoOp.
				return Delegates.NoOp;
			}
			
			var method = RestSharpHelper.GetMethod(restRequest).ToString();
			
			var segment = agentWrapperApi.CurrentTransactionWrapperApi.StartExternalRequestSegment(instrumentedMethodCall.MethodCall, uri, method);

			//Outbound CAT headers are added via AppendHeaders instrumentation.

			return Delegates.GetDelegateFor<Task>(
				onFailure: segment.End,
				onSuccess: AfterWrapped
			);

			void AfterWrapped(Task task)
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
				//blocking TPL pattern of .Wait() or .Result. Posting to the sync context will result
				//in recording time waiting for the current unit of work on the sync context to finish.

				task.ContinueWith(responseTask => agentWrapperApi.HandleExceptions(() =>
				{
					TryProcessResponse(agentWrapperApi, responseTask, transactionWrapperApi, segment);
					segment.End();
					transactionWrapperApi.Release();

				}));
			}
		}

		private static void TryProcessResponse(IAgentWrapperApi agentWrapperApi, Task responseTask, ITransactionWrapperApi transactionWrapperApi, ISegment segment)
		{
			try
			{
				if (!ValidTaskResponse(responseTask) || (segment == null))
				{
					return;
				}

				var restResponse = RestSharpHelper.GetRestResponse(responseTask);

				var headers = RestSharpHelper.GetResponseHeaders(restResponse);
				if (headers == null)
				{
					return;
				}

				transactionWrapperApi.ProcessInboundResponse(headers, segment);
			}
			catch (Exception ex)
			{
				agentWrapperApi.HandleWrapperException(ex);
			}
		}

		private static bool ValidTaskResponse(Task response)
		{
			return (response?.Status == TaskStatus.RanToCompletion);
		}

	}
}
