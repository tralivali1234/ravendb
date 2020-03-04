﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.ServerWide.Tcp;
using Raven.Client.Util;
using Raven.Server.Json;
using Raven.Server.Rachis;
using Raven.Server.Routing;
using Raven.Server.Utils;
using Raven.Server.Web;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.Documents.Handlers.Debugging
{
    class NodeDebugHandler : RequestHandler
    {
        [RavenAction("/admin/debug/node/remote-connections", "GET", AuthorizationStatus.Operator, IsDebugInformationEndpoint = true)]
        public Task ListRemoteConnections()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var write = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                context.Write(write,
                    new DynamicJsonValue
                    {
                        ["Remote-Connections"] = new DynamicJsonArray(RemoteConnection.RemoteConnectionsList
                            .Select(connection => new DynamicJsonValue
                            {
                                [nameof(RemoteConnection.RemoteConnectionInfo.Caller)] = connection.Caller,
                                [nameof(RemoteConnection.RemoteConnectionInfo.Term)] = connection.Term,
                                [nameof(RemoteConnection.RemoteConnectionInfo.Destination)] = connection.Destination,
                                [nameof(RemoteConnection.RemoteConnectionInfo.StartAt)] = connection.StartAt,
                                ["Duration"] = DateTime.UtcNow - connection.StartAt,
                                [nameof(RemoteConnection.RemoteConnectionInfo.Number)] = connection.Number,
                            }))
                    });
                write.Flush();
            }
            return Task.CompletedTask;
        }

        [RavenAction("/admin/debug/node/engine-logs", "GET", AuthorizationStatus.Operator, IsDebugInformationEndpoint = true)]
        public Task ListRecentEngineLogs()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var write = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                context.Write(write, ServerStore.Engine.InMemoryDebug.ToJson());
                write.Flush();
            }
            return Task.CompletedTask;
        }

        [RavenAction("/admin/debug/node/state-change-history", "GET", AuthorizationStatus.Operator, IsDebugInformationEndpoint = true)]
        public Task GetStateChangeHistory()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteStartObject();
                writer.WriteArray("States", ServerStore.Engine.PrevStates.Select(s => s.ToString()));
                writer.WriteEndObject();
            }
            return Task.CompletedTask;
        }

        [RavenAction("/admin/debug/node/ping", "GET", AuthorizationStatus.Operator, IsDebugInformationEndpoint = true)]
        public async Task PingTest()
        {
            var dest = GetStringQueryString("url", false) ?? GetStringQueryString("node", false);
            var topology = ServerStore.GetClusterTopology();
            var tasks = new List<Task<PingResult>>();
            if (string.IsNullOrEmpty(dest))
            {
                foreach (var node in topology.AllNodes)
                {
                    tasks.Add(PingOnce(node.Value));
                }
            }
            else
            {
                var url = topology.GetUrlFromTag(dest);
                tasks.Add(PingOnce(url ?? dest));
            }

            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var write = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                write.WriteStartObject();
                write.WritePropertyName("Result");

                write.WriteStartArray();
                while (tasks.Count > 0)
                {
                    var task = await Task.WhenAny(tasks);
                    tasks.Remove(task);
                    context.Write(write, task.Result.ToJson());
                    if (tasks.Count > 0)
                    {
                        write.WriteComma();
                    }
                    write.Flush();
                }
                write.WriteEndArray();
                write.WriteEndObject();
                write.Flush();
            }
        }

        private class PingResult : IDynamicJsonValueConvertible
        {
            public string Url;
            public long TcpInfoTime;
            public long SendTime;
            public long RecieveTime;
            public string Error;
            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue
                {
                    [nameof(Url)] = Url,
                    [nameof(TcpInfoTime)] = TcpInfoTime,
                    [nameof(SendTime)] = SendTime,
                    [nameof(RecieveTime)] = RecieveTime,
                    [nameof(Error)] = Error
                };
            }
        }

        private async Task<PingResult> PingOnce(string url)
        {
            var sp = Stopwatch.StartNew();
            var result = new PingResult
            {
                Url = url
            };
            try
            {
                using (var cts = new CancellationTokenSource(ServerStore.Engine.TcpConnectionTimeout))
                {
                    var info = await ReplicationUtils.GetTcpInfoAsync(url, null, "PingTest", ServerStore.Engine.ClusterCertificate, cts.Token);
                    result.TcpInfoTime = sp.ElapsedMilliseconds;
                    using (var tcpClient = await TcpUtils.ConnectAsync(info.Url, ServerStore.Engine.TcpConnectionTimeout).ConfigureAwait(false))
                    using (var stream = await TcpUtils
                        .WrapStreamWithSslAsync(tcpClient, info, ServerStore.Engine.ClusterCertificate, ServerStore.Engine.TcpConnectionTimeout).ConfigureAwait(false))
                    using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
                    {
                        var msg = new DynamicJsonValue
                        {
                            [nameof(TcpConnectionHeaderMessage.DatabaseName)] = null,
                            [nameof(TcpConnectionHeaderMessage.Operation)] = TcpConnectionHeaderMessage.OperationTypes.Ping,
                            [nameof(TcpConnectionHeaderMessage.OperationVersion)] = -1
                        };

                        using (var writer = new BlittableJsonTextWriter(context, stream))
                        using (var msgJson = context.ReadObject(msg, "message"))
                        {
                            result.SendTime = sp.ElapsedMilliseconds;
                            context.Write(writer, msgJson);
                        }
                        using (var response = context.ReadForMemory(stream, "cluster-ConnectToPeer-header-response"))
                        {
                            JsonDeserializationServer.TcpConnectionHeaderResponse(response);
                            result.RecieveTime = sp.ElapsedMilliseconds;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                result.Error = e.ToString();
            }
            return result;
        }
    }
}
