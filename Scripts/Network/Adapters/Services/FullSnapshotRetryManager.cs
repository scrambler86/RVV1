using System.Collections.Generic;
using FishNet.Connection;

namespace Game.Networking.Adapters
{
    public readonly struct FullSnapshotRetryRecord
    {
        public FullSnapshotRetryRecord(byte[] payload, List<byte[]> shards, double lastSentAt, int retryCount)
        {
            Payload = payload;
            Shards = shards;
            LastSentAt = lastSentAt;
            RetryCount = retryCount;
        }

        public byte[] Payload { get; }
        public List<byte[]> Shards { get; }
        public double LastSentAt { get; }
        public int RetryCount { get; }
        public bool HasShards => Shards != null && Shards.Count > 0;
    }

    public interface IFullSnapshotRetryManager
    {
        bool IsEmpty { get; }
        void RecordPayload(NetworkConnection conn, byte[] payload, double now);
        void RecordShards(NetworkConnection conn, List<byte[]> shards, double now);
        IEnumerable<NetworkConnection> EnumerateConnections();
        bool TryGetRecord(NetworkConnection conn, out FullSnapshotRetryRecord record);
        void MarkSent(NetworkConnection conn, double now);
        void Clear(NetworkConnection conn);
    }

    public sealed class DefaultFullSnapshotRetryManager : IFullSnapshotRetryManager
    {
        readonly Dictionary<NetworkConnection, byte[]> _payloads = new();
        readonly Dictionary<NetworkConnection, List<byte[]>> _shards = new();
        readonly Dictionary<NetworkConnection, double> _lastSent = new();
        readonly Dictionary<NetworkConnection, int> _retryCounts = new();

        public bool IsEmpty => _payloads.Count == 0 && _shards.Count == 0;

        public void RecordPayload(NetworkConnection conn, byte[] payload, double now)
        {
            if (conn == null)
                return;

            _payloads[conn] = payload;
            _lastSent[conn] = now;
            _retryCounts[conn] = 0;
            _shards.Remove(conn);
        }

        public void RecordShards(NetworkConnection conn, List<byte[]> shards, double now)
        {
            if (conn == null)
                return;

            _shards[conn] = shards;
            _lastSent[conn] = now;
            _retryCounts[conn] = 0;
        }

        public IEnumerable<NetworkConnection> EnumerateConnections() => _lastSent.Keys;

        public bool TryGetRecord(NetworkConnection conn, out FullSnapshotRetryRecord record)
        {
            record = default;
            if (!_lastSent.TryGetValue(conn, out var sentAt))
                return false;

            _payloads.TryGetValue(conn, out var payload);
            _shards.TryGetValue(conn, out var shards);
            _retryCounts.TryGetValue(conn, out var retryCount);

            record = new FullSnapshotRetryRecord(payload, shards, sentAt, retryCount);
            return true;
        }

        public void MarkSent(NetworkConnection conn, double now)
        {
            if (conn == null)
                return;

            _lastSent[conn] = now;
            _retryCounts[conn] = _retryCounts.TryGetValue(conn, out var count) ? count + 1 : 1;
        }

        public void Clear(NetworkConnection conn)
        {
            if (conn == null)
                return;

            _payloads.Remove(conn);
            _shards.Remove(conn);
            _lastSent.Remove(conn);
            _retryCounts.Remove(conn);
        }
    }
<<<<<<< HEAD
}
=======
}
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
