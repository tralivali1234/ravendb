﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Server.Rachis;
using Raven.Server.ServerWide.Context;
using Tests.Infrastructure;
using Xunit;

namespace RachisTests
{
    public class CommandsTests : RachisConsensusTestBase
    {
        [Fact]
        public async Task When_command_committed_CompletionTaskSource_is_notified()
        {
            const int commandCount = 10;
            const int clusterSize = 3;
            var leader = await CreateNetworkAndGetLeader(clusterSize);
            var nonLeader = GetRandomFollower();
            var tasks = new List<Task>();
            long lastIndex;
            TransactionOperationContext context;
            using (leader.ContextPool.AllocateOperationContext(out context))
            {
                for (var i = 0; i < commandCount; i++)
                {
                    tasks.Add(leader.PutAsync(new TestCommand { Name = "test", Value = i }));
                }
                using (context.OpenReadTransaction())
                    lastIndex = leader.GetLastEntryIndex(context);
            }
            var waitForAllCommits = nonLeader.WaitForCommitIndexChange(RachisConsensus.CommitIndexModification.GreaterOrEqual, lastIndex);

            Assert.True(await Task.WhenAny(waitForAllCommits, Task.Delay(LongWaitTime)) == waitForAllCommits, "didn't commit in time");
            var waitForNotificationsOnTasks = Task.WhenAll(tasks);
            Assert.True(await Task.WhenAny(waitForNotificationsOnTasks, Task.Delay(LongWaitTime)) == waitForNotificationsOnTasks, "Some commands didn't complete");
        }

        [Fact]
        public async Task Command_not_committed_after_timeout_CompletionTaskSource_is_notified()
        {
            const int commandCount = 3;
            const int clusterSize = 3;
            var leader = await CreateNetworkAndGetLeader(clusterSize);
            var nonLeader = GetRandomFollower();
            var tasks = new List<Task>();
            long lastIndex;
            TransactionOperationContext context;
            using (leader.ContextPool.AllocateOperationContext(out context))
            {
                for (var i = 0; i < commandCount; i++)
                {
                    tasks.Add(leader.PutAsync(new TestCommand { Name = "test", Value = i }));
                }
                using (context.OpenReadTransaction())
                    lastIndex = leader.GetLastEntryIndex(context);
            }
            var waitForAllCommits = nonLeader.WaitForCommitIndexChange(RachisConsensus.CommitIndexModification.GreaterOrEqual, lastIndex);
            Assert.True(await waitForAllCommits.WaitAsync(LongWaitTime), "didn't commit in time");

            Assert.True(tasks.All(t => t.Status == TaskStatus.RanToCompletion), "Some commands didn't complete");
            DisconnectFromNode(leader);
            using (leader.ContextPool.AllocateOperationContext(out context))
            {
                try
                {
                    var task = leader.PutAsync(new TestCommand { Name = "test", Value = commandCount });
                    Assert.True(await task.WaitAsync((int)leader.ElectionTimeout.TotalMilliseconds * 10));
                    await task;
                    Assert.True(false, "We should have gotten an error");
                }
                // expecting either one of those
                catch (TimeoutException)
                {
                }
                catch (NotLeadingException)
                {
                }
            }
        }
    }
}
