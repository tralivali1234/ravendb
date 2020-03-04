using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Raven.Client.ServerWide.Tcp;
using Raven.Server.Utils;
using Raven.Server.Utils.Metrics;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Voron.Util;

namespace Raven.Server.Documents.TcpHandlers
{
    public class TcpConnectionOptions: IDisposable
    {
        private static long _sequence;

        private readonly MeterMetric _bytesReceivedMetric;
        private readonly MeterMetric _bytesSentMetric;
        private readonly DateTime _connectedAt;

        private bool _isDisposed;

        public DocumentDatabase DocumentDatabase;
        
        public TcpConnectionHeaderMessage.OperationTypes Operation;

        public Stream Stream;

        public TcpClient TcpClient;

        public int ProtocolVersion;
        public TcpConnectionOptions()
        {
            _bytesReceivedMetric = new MeterMetric();
            _bytesSentMetric = new MeterMetric();

            MetricsScheduler.Instance.StartTickingMetric(_bytesSentMetric);
            MetricsScheduler.Instance.StartTickingMetric(_bytesReceivedMetric);
            _connectedAt = DateTime.UtcNow;

            Id = Interlocked.Increment(ref _sequence);
        }

        public long Id { get; set; }
        public JsonContextPool ContextPool;

        private readonly SemaphoreSlim _running = new SemaphoreSlim(1);
        private string _debugTag;

        public override string ToString()
        {
            return "Tcp Connection " + _debugTag;
        }
        public IDisposable ConnectionProcessingInProgress(string debugTag)
        {
            _debugTag = debugTag;
            _running.Wait();
            return new DisposableAction(() => _running.Release());
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            Stream?.Dispose();
            TcpClient?.Dispose();

            _running.Wait();
            try
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;

                DocumentDatabase?.RunningTcpConnections.TryRemove(this);

                Stream = null;
                TcpClient = null;
            }
            finally
            {
                _running.Release();
            }
            // we'll let the _running be finalized, because otherwise we have
            // a possible race condition on dispose
        }

        public void RegisterBytesSent(long bytesAmount)
        {
            _bytesSentMetric.Mark(bytesAmount);
        }

        public void RegisterBytesReceived(long bytesAmount)
        {
            _bytesReceivedMetric.Mark(bytesAmount);
        }

        public bool CheckMatch(long? minSecondsDuration, long? maxSecondsDuration, string ip,
            TcpConnectionHeaderMessage.OperationTypes? operationType)
        {
            var totalSeconds = (long) (DateTime.UtcNow - _connectedAt).TotalSeconds;

            if (totalSeconds < minSecondsDuration)
                return false;

            if (totalSeconds > maxSecondsDuration)
                return false;

            if (string.IsNullOrEmpty(ip) == false)
            {
                if (TcpClient.Client.RemoteEndPoint.ToString().Equals(ip, StringComparison.OrdinalIgnoreCase) == false)
                    return false;
            }

            if (operationType.HasValue)
            {
                if (operationType != Operation)
                    return false;
            }

            return true;
        }

        public DynamicJsonValue GetConnectionStats(JsonOperationContext context)
        {
            var stats = new DynamicJsonValue
            {
                ["Id"] = Id,
                ["Operation"] = Operation.ToString(),
                ["ClientUri"] = TcpClient.Client.RemoteEndPoint.ToString(),
                ["ConnectedAt"] = _connectedAt,
                ["Duration"] = (DateTime.UtcNow - _connectedAt).ToString()
            };


            _bytesReceivedMetric.SetMinimalHumaneMeterData("Received", stats);
            _bytesSentMetric.SetMinimalHumaneMeterData("Sent", stats);

            _bytesReceivedMetric.SetMinimalMeterData("Received", stats);
            _bytesSentMetric.SetMinimalMeterData("Sent", stats);
                        
            return stats;
        }
    }
}
