// Copyright 2020 New Relic, Inc. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using MongoDB.Driver;
using NewRelic.Agent.Extensions.Providers.Wrapper;
using NewRelic.Agent.Extensions.Parsing;
using NewRelic.Agent.Api;

namespace NewRelic.Providers.Wrapper.MongoDb
{
    public class MongoCollectionFindWrapper : IWrapper
    {
        private const string WrapperName = "MongoCollectionFindWrapper";
        public bool IsTransactionRequired => true;

        public CanWrapResponse CanWrap(InstrumentedMethodInfo methodInfo)
        {
            return new CanWrapResponse(WrapperName.Equals(methodInfo.RequestedWrapperName));
        }

        public AfterWrappedMethodDelegate BeforeWrappedMethod(InstrumentedMethodCall instrumentedMethodCall, IAgent agent, ITransaction transaction)
        {
            var operation = GetOperationName(instrumentedMethodCall.MethodCall);
            var model = MongoDBHelper.GetCollectionModelName(instrumentedMethodCall.MethodCall);
            var segment = transaction.StartDatastoreSegment(instrumentedMethodCall.MethodCall, new ParsedSqlStatement(DatastoreVendor.MongoDB, model, operation));

            return Delegates.GetDelegateFor(segment);
        }

        private string GetOperationName(MethodCall methodCall)
        {
            // Haven't seen any instance of MethodArguments being empty, but in case it could happen.
            if (!methodCall.MethodArguments.Any())
                return "Find";

            var firstArg = methodCall.MethodArguments[0];

            // FindAll passes a null IMongoQuery argument.
            if (firstArg == null)
                return "FindAll";

            if (firstArg is FindOneArgs)
                return "FindOne";

            return "Find";
        }
    }
}
