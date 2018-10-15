﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
#if NETSTANDARD2_0
using System.Runtime.Loader;
#endif
using System.Text;
using Raven.Client.Documents;
using Raven.Client.Exceptions;
using Raven.Client.Extensions;
using Raven.Client.ServerWide.Operations;
using System.Security.Cryptography.X509Certificates;
using Raven.Client.Http;
using Sparrow.Logging;
using Raven.Client.Util;
using Sparrow.Platform;

namespace Raven.Embedded
{
    public class EmbeddedServer : IDisposable
    {
        public static EmbeddedServer Instance = new EmbeddedServer();

        internal EmbeddedServer()
        {
        }

        private readonly Logger _logger = LoggingSource.Instance.GetLogger<EmbeddedServer>("Embedded");
        private Lazy<Task<(Uri ServerUrl, Process ServerProcess)>> _serverTask;

        private readonly ConcurrentDictionary<string, Lazy<Task<IDocumentStore>>> _documentStores = new ConcurrentDictionary<string, Lazy<Task<IDocumentStore>>>();
        private X509Certificate2 _certificate;

        private TimeSpan _gracefulShutdownTimeout;

        public void StartServer(ServerOptions options = null)
        {
            options = options ?? ServerOptions.Default;

            _gracefulShutdownTimeout = options.GracefulShutdownTimeout;
            var startServer = new Lazy<Task<(Uri ServerUrl, Process ServerProcess)>>(() => RunServer(options));
            if (Interlocked.CompareExchange(ref _serverTask, startServer, null) != null)
                throw new InvalidOperationException("The server was already started");

            if (options.Security != null)
            {
                _certificate = options.Security.ClientCertificate;

                try
                {
                    var thumbprint = options.Security.ServerCertificateThumbprint;
                    RequestExecutor.RemoteCertificateValidationCallback += (sender, certificate, chain, errors) =>
                    {
                        var certificate2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
                        return certificate2.Thumbprint == thumbprint;
                    };
                }
                catch (NotSupportedException)
                {
                    // not supported on Mono
                }
                catch (InvalidOperationException)
                {
                    // not supported on MacOSX
                }
            }

            // this forces the server to start running in an async manner.
            GC.KeepAlive(startServer.Value);
        }

        public IDocumentStore GetDocumentStore(string database)
        {
            return AsyncHelpers.RunSync(() => GetDocumentStoreAsync(database));
        }

        public IDocumentStore GetDocumentStore(DatabaseOptions options)
        {
            return AsyncHelpers.RunSync(() => GetDocumentStoreAsync(options));
        }

        public Task<IDocumentStore> GetDocumentStoreAsync(string database, CancellationToken token = default)
        {
            return GetDocumentStoreAsync(new DatabaseOptions(database), token);
        }

        public async Task<IDocumentStore> GetDocumentStoreAsync(DatabaseOptions options, CancellationToken token = default)
        {
            var databaseName = options.DatabaseRecord.DatabaseName;
            if (string.IsNullOrWhiteSpace(databaseName))
                throw new ArgumentNullException(nameof(options.DatabaseRecord.DatabaseName), "The database name is mandatory");

            if (_logger.IsInfoEnabled)
                _logger.Info($"Creating document store for '{databaseName}'.");

            token.ThrowIfCancellationRequested();

            var lazy = new Lazy<Task<IDocumentStore>>(async () =>
            {
                var serverUrl = await GetServerUriAsync(token).ConfigureAwait(false);
                var store = new DocumentStore
                {
                    Urls = new[] { serverUrl.AbsoluteUri },
                    Database = databaseName,
                    Certificate = _certificate,
                    Conventions = options.Conventions
                };

                store.AfterDispose += (sender, args) => _documentStores.TryRemove(databaseName, out _);

                store.Initialize();
                if (options.SkipCreatingDatabase == false)
                    await TryCreateDatabase(options, store, token).ConfigureAwait(false);

                return store;
            });

            return await _documentStores.GetOrAdd(databaseName, lazy).Value.WithCancellation(token).ConfigureAwait(false);
        }

        private async Task TryCreateDatabase(DatabaseOptions options, IDocumentStore store, CancellationToken token)
        {
            try
            {
                await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(options.DatabaseRecord), token).ConfigureAwait(false);
            }
            catch (ConcurrencyException)
            {
                // Expected behaviour when the database is already exists

                if (_logger.IsInfoEnabled)
                    _logger.Info($"{options.DatabaseRecord.DatabaseName} already exists.");
            }
        }

        public async Task<Uri> GetServerUriAsync(CancellationToken token = default)
        {
            var server = _serverTask;
            if (server == null)
                throw new InvalidOperationException($"Please run {nameof(StartServer)}() before trying to use the server");

            return (await server.Value.WithCancellation(token).ConfigureAwait(false)).ServerUrl;
        }

        private void ShutdownServerProcess(Process process)
        {
            if (process == null || process.HasExited)
                return;

            lock (process)
            {
                if (process.HasExited)
                    return;

                try
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info($"Try shutdown server PID {process.Id} gracefully.");

                    using (var inputStream = process.StandardInput)
                    {
                        inputStream.Write($"q{Environment.NewLine}y{Environment.NewLine}");
                    }

                    if (process.WaitForExit((int)_gracefulShutdownTimeout.TotalMilliseconds))
                        return;
                }
                catch (Exception e)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"Failed to shutdown server PID {process.Id} gracefully in {_gracefulShutdownTimeout.ToString()}", e);
                    }
                }

                try
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info($"Killing global server PID {process.Id}.");

                    process.Kill();
                }
                catch (Exception e)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"Failed to kill process {process.Id}", e);
                    }
                }
            }
        }

        private async Task<(Uri ServerUrl, Process ServerProcess)> RunServer(ServerOptions options)
        {
            var process = RavenServerRunner.Run(options);
            if (_logger.IsInfoEnabled)
                _logger.Info($"Starting global server: { process.Id }");

            var domainBind = false;

#if NETSTANDARD2_0
            AssemblyLoadContext.Default.Unloading += c =>
            {
                ShutdownServerProcess(process);
            };
            domainBind = true;
#endif
#if NET461
            AppDomain.CurrentDomain.DomainUnload += (s, args) =>
            {
                ShutdownServerProcess(process);
            };
            domainBind = true;
#endif

            if (domainBind == false)
                throw new InvalidOperationException("Should not happen!");

            string url = null;
            var startupDuration = Stopwatch.StartNew();

            var outputString = await ReadOutput(process.StandardOutput, startupDuration, options, async (line, builder) =>
            {
                if (line == null)
                {
                    var errorString = await ReadOutput(process.StandardError, startupDuration, options, null).ConfigureAwait(false);

                    ShutdownServerProcess(process);

                    throw new InvalidOperationException(BuildStartupExceptionMessage(builder.ToString(), errorString));
                }

                const string prefix = "Server available on: ";
                if (line.StartsWith(prefix))
                {
                    url = line.Substring(prefix.Length);
                    return true;
                }

                return false;
            }).ConfigureAwait(false);

            if (url == null)
            {
                var errorString = await ReadOutput(process.StandardError, startupDuration, options, null).ConfigureAwait(false);

                ShutdownServerProcess(process);

                throw new InvalidOperationException(BuildStartupExceptionMessage(outputString, errorString));
            }

            return (new Uri(url), process);
        }

        private static string BuildStartupExceptionMessage(string outputString, string errorString)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Unable to start the RavenDB Server");

            if (string.IsNullOrWhiteSpace(errorString) == false)
            {
                sb.AppendLine("Error:");
                sb.AppendLine(errorString);
            }

            if (string.IsNullOrWhiteSpace(outputString) == false)
            {
                sb.AppendLine("Output:");
                sb.AppendLine(outputString);
            }

            return sb.ToString();
        }

        private static async Task<string> ReadOutput(StreamReader output, Stopwatch startupDuration, ServerOptions options, Func<string, StringBuilder, Task<bool>> onLine)
        {
            var sb = new StringBuilder();

            Task<string> readLineTask = null;
            while (true)
            {
                if (readLineTask == null)
                    readLineTask = output.ReadLineAsync();

                var hasResult = await readLineTask.WaitWithTimeout(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                if (startupDuration.Elapsed > options.MaxServerStartupTimeDuration)
                    return null;

                if (hasResult == false)
                    continue;

                var line = readLineTask.Result;

                readLineTask = null;

                if (line != null)
                    sb.AppendLine(line);

                var shouldStop = false;
                if (onLine != null)
                    shouldStop = await onLine(line, sb).ConfigureAwait(false);

                if (shouldStop)
                    break;

                if (line == null)
                    break;
            }

            return sb.ToString();
        }

        public void OpenStudioInBrowser()
        {
            var serverUrl = AsyncHelpers.RunSync(() => GetServerUriAsync());
            var url = serverUrl.AbsoluteUri;

            if (PlatformDetails.RunningOnPosix == false)
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start \"Stop & look at Studio\" \"{url}\""));
                return;
            }

            if (PlatformDetails.RunningOnMacOsx)
            {
                Process.Start("open", url);
                return;
            }

            Process.Start("xdg-open", url);
        }

        public void Dispose()
        {
            var lazy = Interlocked.Exchange(ref _serverTask, null);
            if (lazy == null || lazy.IsValueCreated == false)
                return;

            var process = lazy.Value.Result.ServerProcess;
            ShutdownServerProcess(process);
            foreach (var item in _documentStores)
            {
                if (item.Value.IsValueCreated)
                {
                    item.Value.Value.Dispose();
                }
            }
            _documentStores.Clear();
        }
    }
}
