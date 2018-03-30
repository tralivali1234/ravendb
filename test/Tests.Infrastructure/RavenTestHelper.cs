﻿using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Server.Utils;
using Voron.Platform.Posix;
using Sparrow.Collections;
using Sparrow.Platform;
using Sparrow.Utils;
using Xunit;

namespace FastTests
{
    public static class RavenTestHelper
    {
        public static readonly ParallelOptions DefaultParallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = ProcessorInfo.ProcessorCount * 2
        };

        private static int _pathCount;

        public static string NewDataPath(string testName, int serverPort, bool forceCreateDir = false)
        {
            testName = testName?.Replace("<", "").Replace(">", "");

            var newDataDir = Path.GetFullPath($".\\Databases\\{testName ?? "TestDatabase"}_{serverPort}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss-fff}-{Interlocked.Increment(ref _pathCount)}");

            if (PlatformDetails.RunningOnPosix)
                newDataDir = PosixHelper.FixLinuxPath(newDataDir);

            if (forceCreateDir && Directory.Exists(newDataDir) == false)
                Directory.CreateDirectory(newDataDir);

            return newDataDir;
        }

        public static void DeletePaths(ConcurrentSet<string> pathsToDelete, ExceptionAggregator exceptionAggregator)
        {
            var localPathsToDelete = pathsToDelete.ToArray();
            foreach (var pathToDelete in localPathsToDelete)
            {
                pathsToDelete.TryRemove(pathToDelete);
                exceptionAggregator.Execute(() => ClearDatabaseDirectory(pathToDelete));
            }
        }

        private static void ClearDatabaseDirectory(string dataDir)
        {
            var isRetry = false;

            while (true)
            {
                try
                {
                    IOExtensions.DeleteDirectory(dataDir);
                    break;
                }
                catch (IOException)
                {
                    if (isRetry)
                        throw;

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    isRetry = true;

                    Thread.Sleep(200);
                }
            }
        }

        public static IndexQuery GetIndexQuery<T>(IQueryable<T> queryable)
        {
            var inspector = (IRavenQueryInspector)queryable;
            return inspector.GetIndexQuery(isAsync: false);
        }

        public static void AssertNoIndexErrors(IDocumentStore store, string databaseName = null)
        {
            var errors = store.Maintenance.ForDatabase(databaseName).Send(new GetIndexErrorsOperation());

            Assert.Empty(errors.SelectMany(x => x.Errors));
        }

        public static void AssertEqualRespectingNewLines(string expected, string actual)
        {
            var regex = new Regex("\r*\n");
            var converted = regex.Replace(expected, Environment.NewLine);

            Assert.Equal(converted, actual);
        }
    }
}
