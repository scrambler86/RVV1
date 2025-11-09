using System;
using System.Collections.Generic;
using System.Linq;

// Shard header: total (ushort), index (ushort), dataLen (uint) => 8 bytes
public class Shard
{
    public ushort total;
    public ushort index;
    public int dataLen;
    public byte[] bytes; // raw data portion (length = dataLen)
    public byte[] ToBytes()
    {
        var b = new byte[8 + dataLen];
        Array.Copy(BitConverter.GetBytes(total), 0, b, 0, 2);
        Array.Copy(BitConverter.GetBytes(index), 0, b, 2, 2);
        Array.Copy(BitConverter.GetBytes((uint)dataLen), 0, b, 4, 4);
        Array.Copy(bytes, 0, b, 8, dataLen);
        return b;
    }
    public static bool TryParse(byte[] raw, out Shard s)
    {
        s = null;
        if (raw == null || raw.Length < 8) return false;
        ushort total = BitConverter.ToUInt16(raw, 0);
        ushort idx = BitConverter.ToUInt16(raw, 2);
        uint dataLenU = BitConverter.ToUInt32(raw, 4);
        int dataLen = (int)dataLenU;
        if (dataLen < 0 || raw.Length < 8 + dataLen) return false;
        var data = new byte[dataLen];
        Array.Copy(raw, 8, data, 0, dataLen);
        s = new Shard { total = total, index = idx, dataLen = dataLen, bytes = data };
        return true;
    }
}

public static class FecReedSolomon
{
    // Build shards: naive XOR parity option (parityCount = 1), interface compatible with RS later
    public static List<byte[]> BuildShards(byte[] payload, int shardSize, int parityCount)
    {
        var outList = new List<byte[]>();
        if (payload == null || payload.Length == 0)
        {
            return outList;
        }

        int dataShards = (payload.Length + shardSize - 1) / shardSize;
        int totalShards = dataShards + parityCount;

        // data shards
        for (int i = 0; i < dataShards; ++i)
        {
            int start = i * shardSize;
            int len = Math.Min(shardSize, payload.Length - start);
            var shard = new Shard { total = (ushort)totalShards, index = (ushort)i, dataLen = len, bytes = new byte[len] };
            Array.Copy(payload, start, shard.bytes, 0, len);
            outList.Add(shard.ToBytes());
        }

        // parity: XOR across padded data shards; store parity payload of length shardSize
        for (int p = 0; p < parityCount; ++p)
        {
            byte[] parity = new byte[shardSize];
            for (int i = 0; i < dataShards; ++i)
            {
                var raw = outList[i];
                // parse to get data
                Shard tmp;
                Shard.TryParse(raw, out tmp);
                int dlen = tmp.dataLen;
                for (int b = 0; b < shardSize; ++b)
                {
                    byte vb = (b < dlen) ? tmp.bytes[b] : (byte)0;
                    parity[b] ^= vb;
                }
            }
            var ps = new Shard { total = (ushort)totalShards, index = (ushort)(dataShards + p), dataLen = shardSize, bytes = parity };
            outList.Add(ps.ToBytes());
        }

        return outList;
    }

    // Try reconstruct: accepts received shard bytes (some may be duplicates); attempts single-missing recovery if parity present
    // Returns reconstructed payload or null if unrecoverable
    public static byte[] TryReconstruct(List<byte[]> receivedRaw, int shardSize, int parityCount)
    {
        if (receivedRaw == null || receivedRaw.Count == 0) return null;
        var shards = new List<Shard>();
        foreach (var r in receivedRaw)
        {
            if (Shard.TryParse(r, out var s)) shards.Add(s);
        }
        if (shards.Count == 0) return null;
        int total = shards[0].total;
        int parity = Math.Min(parityCount, Math.Max(0, total - shards.Count));
        // build map by index
        var map = new Shard[total];
        foreach (var s in shards) if (s.index < total) map[s.index] = s;

        int dataShards = Math.Max(1, total - parityCount);
        // check how many data shards missing
        int missingCount = 0;
        int missingIdx = -1;
        for (int i = 0; i < dataShards; ++i)
        {
            if (map[i] == null) { missingCount++; missingIdx = i; }
        }

        if (missingCount == 0)
        {
            // concatenate using actual dataLens
            int totalLen = 0;
            for (int i = 0; i < dataShards; ++i) totalLen += map[i].dataLen;
            var payload = new byte[totalLen];
            int pos = 0;
            for (int i = 0; i < dataShards; ++i)
            {
                Array.Copy(map[i].bytes, 0, payload, pos, map[i].dataLen);
                pos += map[i].dataLen;
            }
            return payload;
        }

        if (missingCount == 1 && parityCount >= 1)
        {
            // single-missing XOR recovery
            int maxPad = shardSize;
            var recoveredPadded = new byte[maxPad];
            for (int s = 0; s < total; ++s)
            {
                if (map[s] != null)
                {
                    var dat = map[s].bytes;
                    int dlen = map[s].dataLen;
                    for (int b = 0; b < maxPad; ++b)
                    {
                        byte vb = (b < dlen) ? dat[b] : (byte)0;
                        recoveredPadded[b] ^= vb;
                    }
                }
            }
            // compute recovered dataLen: if missing is last data shard, may be shorter
            int recoveredDataLen = maxPad;
            if (missingIdx == dataShards - 1)
            {
                // total payload length estimation: sum of known dataLens + unknown
                int knownSum = 0;
                for (int i = 0; i < dataShards; ++i) if (map[i] != null) knownSum += map[i].dataLen;
                // unknown length = totalPayload - knownSum; but we don't have totalPayload directly.
                // best-effort: trim trailing zeros
                int trim = recoveredPadded.Length;
                while (trim > 0 && recoveredPadded[trim - 1] == 0) trim--;
                recoveredDataLen = Math.Max(0, trim);
            }
            byte[] recovered = new byte[recoveredDataLen];
            Array.Copy(recoveredPadded, 0, recovered, 0, recoveredDataLen);

            // set into map
            map[missingIdx] = new Shard { total = (ushort)total, index = (ushort)missingIdx, dataLen = recoveredDataLen, bytes = recovered };

            // now concatenate
            int totalLen = 0;
            for (int i = 0; i < dataShards; ++i) totalLen += map[i].dataLen;
            var payload = new byte[totalLen];
            int pos = 0;
            for (int i = 0; i < dataShards; ++i)
            {
                Array.Copy(map[i].bytes, 0, payload, pos, map[i].dataLen);
                pos += map[i].dataLen;
            }
            return payload;
        }

        // If multiple missing or parity insufficient -> unrecoverable
        return null;
    }
}