using System;
using FishNet.Connection;

namespace Game.Networking.Adapters
{
    public readonly struct ShardBufferKey : IEquatable<ShardBufferKey>
    {
        public readonly int ConnectionId;
        public readonly uint MessageId;

        ShardBufferKey(int connectionId, uint messageId)
        {
            ConnectionId = connectionId;
            MessageId = messageId;
        }

        public static ShardBufferKey ForLocalClient(uint messageId) => new ShardBufferKey(-1, messageId);

        public static ShardBufferKey ForConnection(NetworkConnection conn, uint messageId)
        {
            int id = conn != null ? conn.ClientId : -2; // -2 distinguishes null server handles
            return new ShardBufferKey(id, messageId);
        }

        public bool Equals(ShardBufferKey other) => ConnectionId == other.ConnectionId && MessageId == other.MessageId;

        public override bool Equals(object obj) => obj is ShardBufferKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(ConnectionId, MessageId);

        public override string ToString() => $"ShardKey(conn={ConnectionId}, msg={MessageId})";
    }
}