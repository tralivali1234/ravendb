﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Http;
using Raven.Client.ServerWide.Operations;
using Raven.Client.Util;
using Sparrow.Json;

namespace Raven.Client.Documents.Operations
{
    public class MaintenanceOperationExecutor
    {
        private readonly DocumentStoreBase _store;
        private readonly string _databaseName;
        private RequestExecutor _requestExecutor;
        private ServerOperationExecutor _serverOperationExecutor;

        private RequestExecutor RequestExecutor => _requestExecutor ?? (_databaseName != null ? _requestExecutor = _store.GetRequestExecutor(_databaseName) : null);

        public MaintenanceOperationExecutor(DocumentStoreBase store, string databaseName = null)
        {
            _store = store;
            _databaseName = databaseName ?? store.Database;
        }

        public ServerOperationExecutor Server => _serverOperationExecutor ?? (_serverOperationExecutor = new ServerOperationExecutor(_store));

        public MaintenanceOperationExecutor ForDatabase(string databaseName)
        {
            if (string.Equals(_databaseName, databaseName, StringComparison.OrdinalIgnoreCase))
                return this;

            return new MaintenanceOperationExecutor(_store, databaseName);
        }

        public void Send(IMaintenanceOperation operation)
        {
            AsyncHelpers.RunSync(() => SendAsync(operation));
        }

        public TResult Send<TResult>(IMaintenanceOperation<TResult> operation)
        {
            return AsyncHelpers.RunSync(() => SendAsync(operation));
        }

        public async Task SendAsync(IMaintenanceOperation operation, CancellationToken token = default(CancellationToken))
        {
            using (GetContext(out JsonOperationContext context))
            {
                var command = operation.GetCommand(_requestExecutor.Conventions, context);
                await RequestExecutor.ExecuteAsync(command, context, token: token).ConfigureAwait(false);
            }
        }

        public async Task<TResult> SendAsync<TResult>(IMaintenanceOperation<TResult> operation, CancellationToken token = default(CancellationToken))
        {
            using (GetContext(out JsonOperationContext context))
            {
                var command = operation.GetCommand(_requestExecutor.Conventions, context);

                await RequestExecutor.ExecuteAsync(command, context, token: token).ConfigureAwait(false);
                return command.Result;
            }
        }

        public Operation Send(IMaintenanceOperation<OperationIdResult> operation)
        {
            return AsyncHelpers.RunSync(() => SendAsync(operation));
        }

        public async Task<Operation> SendAsync(IMaintenanceOperation<OperationIdResult> operation, CancellationToken token = default(CancellationToken))
        {
            using (GetContext(out JsonOperationContext context))
            {
                var command = operation.GetCommand(RequestExecutor.Conventions, context);

                await RequestExecutor.ExecuteAsync(command, context, token: token).ConfigureAwait(false);
                return new Operation(RequestExecutor, () => _store.Changes(_databaseName), RequestExecutor.Conventions, command.Result.OperationId);
            }
        }

        private IDisposable GetContext(out JsonOperationContext context)
        {
            if (RequestExecutor == null)
                throw new InvalidOperationException("Cannot use Maintenance without a database defined, did you forget to call ForDatabase?");
            return RequestExecutor.ContextPool.AllocateOperationContext(out context);
        }
    }
}
