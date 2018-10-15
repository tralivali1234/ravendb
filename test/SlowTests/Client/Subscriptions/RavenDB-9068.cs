﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FastTests;
using Raven.Tests.Core.Utils.Entities;
using Tests.Infrastructure;
using Xunit;

namespace SlowTests.Client.Subscriptions
{
    public class RavenDB_9068 : RavenTestBase
    {
        private readonly TimeSpan _reasonableWaitTime = Debugger.IsAttached ? TimeSpan.FromSeconds(60 * 10) : TimeSpan.FromSeconds(50);
        [Fact]
        public async Task CancellingPassedCancellationTokenToRunShouldCancelSubscriptionExecution()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User());
                    await session.SaveChangesAsync();
                }

                var subscriptionId = await store.Subscriptions.CreateAsync<User>();
                var subscription = store.Subscriptions.GetSubscriptionWorker<User>(subscriptionId);
                var cts = new CancellationTokenSource();
                var subscriptionTask = subscription.Run(x => { }, cts.Token);
                cts.Cancel();

                var task = Assert.ThrowsAnyAsync<Exception>(() => subscriptionTask);
                Assert.True(await task.WaitAsync(_reasonableWaitTime));
                var e = await task;
                Assert.True(e is OperationCanceledException || e is TaskCanceledException);

                subscription = store.Subscriptions.GetSubscriptionWorker<User>(subscriptionId);
                cts = new CancellationTokenSource();
                subscriptionTask = subscription.Run(x => Task.CompletedTask, cts.Token);
                cts.Cancel();

                task = Assert.ThrowsAnyAsync<Exception>(() => subscriptionTask);
                Assert.True(await task.WaitAsync(_reasonableWaitTime));
                e = await task;
                Assert.True(e is OperationCanceledException || e is TaskCanceledException);
            }
        }
    }
}
