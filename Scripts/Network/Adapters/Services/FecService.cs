using System;
using System.Collections.Generic;

namespace Game.Networking.Adapters
{
    public interface IFecService
    {
        List<byte[]> BuildShards(byte[] payload, int shardSize, int parityCount);
        bool TryRecover(IList<ShardInfo> shards,
                        int parityCount,
                        int dataShards,
                        int shardPadSize,
                        IList<ShardInfo> recovered);
    }

    public sealed class ReedSolomonFecService : IFecService
    {
        const int FIELD_SIZE = 256;
        const int GENERATOR = 0x11D;
        static readonly byte[] s_Exp = new byte[FIELD_SIZE * 2];
        static readonly byte[] s_Log = new byte[FIELD_SIZE];
        static bool s_Initialized;

        static void EnsureTables()
        {
            if (s_Initialized)
                return;

            byte x = 1;
            for (int i = 0; i < FIELD_SIZE - 1; i++)
            {
                s_Exp[i] = x;
                s_Log[x] = (byte)i;
                x = (byte)(x << 1);
                if ((x & 0x100) != 0)
                    x ^= GENERATOR;
            }

            for (int i = FIELD_SIZE - 1; i < s_Exp.Length; i++)
                s_Exp[i] = s_Exp[i - (FIELD_SIZE - 1)];

            s_Initialized = true;
        }

        static byte GfMul(byte a, byte b)
        {
            if (a == 0 || b == 0)
                return 0;

            int idx = s_Log[a] + s_Log[b];
            return s_Exp[idx];
        }

        static byte GfDiv(byte a, byte b)
        {
            if (a == 0)
                return 0;
            if (b == 0)
                throw new DivideByZeroException();

            int idx = s_Log[a] - s_Log[b];
            if (idx < 0)
                idx += FIELD_SIZE - 1;
            return s_Exp[idx];
        }

        static byte GfPow(byte a, int power)
        {
            if (power == 0)
                return 1;
            if (a == 0)
                return 0;

            int idx = (s_Log[a] * power) % (FIELD_SIZE - 1);
            if (idx < 0)
                idx += FIELD_SIZE - 1;
            return s_Exp[idx];
        }

        static void CopyPadded(byte[] source, byte[] dest)
        {
            Array.Clear(dest, 0, dest.Length);
            if (source == null)
                return;
            Array.Copy(source, 0, dest, 0, Math.Min(source.Length, dest.Length));
        }

        public List<byte[]> BuildShards(byte[] payload, int shardSize, int parityCount)
        {
            EnsureTables();

            var shards = new List<byte[]>();
            if (payload == null || payload.Length == 0)
                return shards;

            int effectiveShardSize = Math.Max(1, Math.Min(shardSize <= 0 ? payload.Length : shardSize, payload.Length));
            int dataShards = (payload.Length + effectiveShardSize - 1) / effectiveShardSize;
            int totalShards = dataShards + Math.Max(0, parityCount);

            var paddedData = new byte[dataShards][];

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

                paddedData[i] = new byte[effectiveShardSize];
                Array.Copy(payload, start, paddedData[i], 0, len);
            }

            for (int p = 0; p < parityCount; p++)
            {
                byte[] parity = new byte[effectiveShardSize];
                byte alpha = (byte)(p + 2);

                for (int j = 0; j < dataShards; j++)
                {
                    byte coeff = GfPow(alpha, j);
                    var data = paddedData[j];
                    for (int b = 0; b < effectiveShardSize; b++)
                    {
                        parity[b] ^= GfMul(coeff, data[b]);
                    }
                }

                byte[] shard = new byte[2 + 2 + 4 + effectiveShardSize];
                Array.Copy(BitConverter.GetBytes((ushort)totalShards), 0, shard, 0, 2);
                Array.Copy(BitConverter.GetBytes((ushort)(dataShards + p)), 0, shard, 2, 2);
                Array.Copy(BitConverter.GetBytes((uint)effectiveShardSize), 0, shard, 4, 4);
                Array.Copy(parity, 0, shard, 8, effectiveShardSize);
                shards.Add(shard);
            }

            return shards;
        }

        public bool TryRecover(IList<ShardInfo> shards,
                               int parityCount,
                               int dataShards,
                               int shardPadSize,
                               IList<ShardInfo> recovered)
        {
            EnsureTables();

            recovered?.Clear();

            if (parityCount <= 0 || shards == null || recovered == null)
                return false;

            var missing = new List<int>();
            for (int i = 0; i < dataShards && i < shards.Count; i++)
            {
                if (shards[i] == null)
                    missing.Add(i);
            }

            if (missing.Count == 0)
                return false;

            if (missing.Count > parityCount)
                return false;

            var parityRows = new List<ShardInfo>();
            for (int i = dataShards; i < shards.Count && parityRows.Count < missing.Count; i++)
            {
                if (shards[i] != null)
                    parityRows.Add(shards[i]);
            }

            if (parityRows.Count < missing.Count)
                return false;

            int n = missing.Count;
            byte[,] matrix = new byte[n, n];
            byte[,] inverse = new byte[n, n];
            byte[][] rhs = new byte[n][];
            byte[][] knownData = new byte[dataShards][];

            for (int i = 0; i < dataShards; i++)
            {
                knownData[i] = new byte[shardPadSize];
                if (shards.Count > i && shards[i] != null)
                    CopyPadded(shards[i].Data, knownData[i]);
            }

            for (int r = 0; r < n; r++)
            {
                byte alpha = (byte)(r + 2);
                var parity = parityRows[r];
                rhs[r] = new byte[shardPadSize];
                CopyPadded(parity.Data, rhs[r]);

                for (int c = 0; c < n; c++)
                {
                    int missingIdx = missing[c];
                    matrix[r, c] = GfPow(alpha, missingIdx);
                }

                for (int dataIdx = 0; dataIdx < dataShards; dataIdx++)
                {
                    if (missing.Contains(dataIdx))
                        continue;

                    byte coeff = GfPow(alpha, dataIdx);
                    var known = knownData[dataIdx];
                    for (int b = 0; b < shardPadSize; b++)
                    {
                        byte term = GfMul(coeff, known[b]);
                        rhs[r][b] ^= term;
                    }
                }

                for (int c = 0; c < n; c++)
                    inverse[r, c] = (byte)(r == c ? 1 : 0);
            }

            if (!InvertMatrix(matrix, inverse, n))
                return false;

            for (int m = 0; m < n; m++)
            {
                byte[] solved = new byte[shardPadSize];
                for (int r = 0; r < n; r++)
                {
                    byte coeff = inverse[m, r];
                    if (coeff == 0)
                        continue;
                    for (int b = 0; b < shardPadSize; b++)
                        solved[b] ^= GfMul(coeff, rhs[r][b]);
                }

                int shardIndex = missing[m];
                int dataLength = shardPadSize;
                if (shardIndex < shards.Count && shards[shardIndex] != null)
                    dataLength = shards[shardIndex].DataLength;

                recovered.Add(new ShardInfo
                {
                    Total = (ushort)shards.Count,
                    Index = (ushort)shardIndex,
                    DataLength = dataLength,
                    Data = solved
                });
            }

            return recovered.Count > 0;
        }

        static bool InvertMatrix(byte[,] matrix, byte[,] inverse, int n)
        {
            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                for (int row = col; row < n; row++)
                {
                    if (matrix[row, col] != 0)
                    {
                        pivot = row;
                        break;
                    }
                }

                if (matrix[pivot, col] == 0)
                    return false;

                if (pivot != col)
                {
                    SwapRows(matrix, col, pivot, n);
                    SwapRows(inverse, col, pivot, n);
                }

                byte pivotVal = matrix[col, col];
                byte invPivot = GfDiv(1, pivotVal);

                for (int j = 0; j < n; j++)
                {
                    matrix[col, j] = GfMul(matrix[col, j], invPivot);
                    inverse[col, j] = GfMul(inverse[col, j], invPivot);
                }

                for (int row = 0; row < n; row++)
                {
                    if (row == col)
                        continue;

                    byte factor = matrix[row, col];
                    if (factor == 0)
                        continue;

                    for (int j = 0; j < n; j++)
                    {
                        matrix[row, j] ^= GfMul(factor, matrix[col, j]);
                        inverse[row, j] ^= GfMul(factor, inverse[col, j]);
                    }
                }
            }

            return true;
        }

        static void SwapRows(byte[,] matrix, int a, int b, int n)
        {
            for (int i = 0; i < n; i++)
            {
                byte tmp = matrix[a, i];
                matrix[a, i] = matrix[b, i];
                matrix[b, i] = tmp;
            }
        }
    }
}
