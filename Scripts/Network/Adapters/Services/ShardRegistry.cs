using System.Collections.Generic;

namespace Game.Networking.Adapters
{
    public interface IShardRegistry
    {
        List<ShardInfo> GetOrCreate(ShardBufferKey key, ushort total, double now);
        int GetTotalCount(ShardBufferKey key);
        void Forget(ShardBufferKey key);
        void CollectExpired(double now, double timeoutSeconds, List<ShardBufferKey> expired);
    }

    public sealed class DefaultShardRegistry : IShardRegistry
    {
        readonly Dictionary<ShardBufferKey, List<ShardInfo>> _buffers = new();
        readonly Dictionary<ShardBufferKey, int> _totals = new();
        readonly Dictionary<ShardBufferKey, double> _firstSeen = new();

        public List<ShardInfo> GetOrCreate(ShardBufferKey key, ushort total, double now)
        {
            if (!_buffers.TryGetValue(key, out var list))
            {
                list = new List<ShardInfo>(total);
                for (int i = 0; i < total; i++)
                    list.Add(null);

                _buffers[key] = list;
                _totals[key] = total;
                _firstSeen[key] = now;
                return list;
            }

            if (list.Count != total)
            {
                if (list.Count < total)
                {
                    for (int i = list.Count; i < total; i++)
                        list.Add(null);
                }
                else
                {
                    list.RemoveRange(total, list.Count - total);
                }

                _totals[key] = total;
            }

            if (!_firstSeen.ContainsKey(key))
                _firstSeen[key] = now;

            return list;
        }

        public int GetTotalCount(ShardBufferKey key) =>
            _totals.TryGetValue(key, out var total) ? total : 0;

        public void Forget(ShardBufferKey key)
        {
            _buffers.Remove(key);
            _totals.Remove(key);
            _firstSeen.Remove(key);
        }

        public void CollectExpired(double now, double timeoutSeconds, List<ShardBufferKey> expired)
        {
            expired.Clear();
            foreach (var kv in _firstSeen)
            {
                if (now - kv.Value > timeoutSeconds)
                    expired.Add(kv.Key);
            }
        }
    }
}