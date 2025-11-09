using System;
using System.Collections.Generic;

namespace Game.Networking.Adapters
{
    public interface IFecService
    {
        List<byte[]> BuildShards(byte[] payload, int shardSize, int parityCount);
        bool TryRecover(IList<ShardInfo> shards, int parityCount, int dataShards, int shardPadSize, out ShardInfo recovered);
    }

    /// <summary>
    /// Default XOR-based FEC implementation (single missing data shard recovery).
    /// </summary>
    public sealed class DefaultFecService : IFecService
    {
        public List<byte[]> BuildShards(byte[] payload, int shardSize, int parityCount)
        {
            var shards = new List<byte[]>();
            if (payload == null || payload.Length == 0)
                return shards;

            int effectiveShardSize = Math.Max(1, Math.Min(shardSize, payload.Length));
            int dataShards = (payload.Length + effectiveShardSize - 1) / effectiveShardSize;
            int totalShards = dataShards + Math.Max(0, parityCount);

            for (int i = 0; i < dataShards; i++)
            {
                int start = i * effectiveShardSize;
                int len = Math.Min(effectiveShardSize, payload.Length - start);

                byte[] shard = new byte[2 + 2 + 4 + len];
                Array.Copy(BitConverter.GetBytes((ushort)totalShards), 0, shard, 0, 2);
                Array.Copy(BitConverter.GetBytes((ushort)i), 0, shard, 2, 2);
                Array.Copy(BitConverter.GetBytes((uint)len), 0, shard, 4, 4);
                Array.Copy(payload, start, shard, 8, len);

                shards.Add(shard);
            }

            for (int p = 0; p < parityCount; p++)
            {
                int maxLen = effectiveShardSize;
                byte[] parityPayload = new byte[maxLen];

                for (int i = 0; i < dataShards; i++)
                {
                    int dsLen = shards[i].Length - 8;
                    for (int b = 0; b < maxLen; b++)
                    {
                        byte vb = 0;
                        if (b < dsLen)
                            vb = shards[i][8 + b];

                        parityPayload[b] ^= vb;
                    }
                }

                byte[] parityShard = new byte[2 + 2 + 4 + maxLen];
                Array.Copy(BitConverter.GetBytes((ushort)totalShards), 0, parityShard, 0, 2);
                Array.Copy(BitConverter.GetBytes((ushort)(dataShards + p)), 0, parityShard, 2, 2);
                Array.Copy(BitConverter.GetBytes((uint)maxLen), 0, parityShard, 4, 4);
                Array.Copy(parityPayload, 0, parityShard, 8, maxLen);

                shards.Add(parityShard);
            }

            return shards;
        }

        public bool TryRecover(IList<ShardInfo> shards, int parityCount, int dataShards, int shardPadSize, out ShardInfo recovered)
        {
            recovered = null;
            if (parityCount <= 0 || shards == null)
                return false;

            int totalShards = shards.Count;
            int missingCount = 0;
            int missingDataIdx = -1;

            for (int i = 0; i < dataShards && i < totalShards; i++)
            {
                if (shards[i] == null)
                {
                    missingDataIdx = i;
                    missingCount++;
                }
            }

            if (missingCount != 1)
                return false;

            byte[][] padded = new byte[totalShards][];
            for (int i = 0; i < totalShards; i++)
            {
                var sInfo = shards[i];
                padded[i] = new byte[shardPadSize];

                if (sInfo != null && sInfo.Data != null)
                    Array.Copy(sInfo.Data, 0, padded[i], 0, Math.Min(sInfo.DataLength, shardPadSize));
            }

            byte[] recoveredBytes = new byte[shardPadSize];
            for (int s = 0; s < totalShards; s++)
            {
                var block = padded[s];
                for (int b = 0; b < shardPadSize; b++)
                    recoveredBytes[b] ^= block[b];
            }

            int recoveredDataLen = shardPadSize;
            if (missingDataIdx == dataShards - 1)
            {
                int sumPrev = 0;
                for (int i = 0; i < missingDataIdx; i++)
                {
                    var info = shards[i];
                    if (info != null)
                        sumPrev += info.DataLength;
                }

                recoveredDataLen = Math.Min(recoveredDataLen, shardPadSize);
            }

            byte[] recoveredData = new byte[recoveredDataLen];
            Array.Copy(recoveredBytes, 0, recoveredData, 0, recoveredDataLen);

            recovered = new ShardInfo
            {
                Total = (ushort)totalShards,
                Index = (ushort)missingDataIdx,
                DataLength = recoveredDataLen,
                Data = recoveredData
            };

            return true;
        }
    }
}
