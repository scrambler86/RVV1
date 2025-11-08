using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace Game.Networking.Adapters
{
    public partial class PlayerNetworkDriverFishNet
    {
        // --- Shard info interno ---
        private class ShardInfo
        {
            public ushort total;
            public ushort index;
            public int dataLen;
            public byte[] data;
        }

        // --- Gestione shard FEC / riassemblaggio ---
        void HandlePackedShard(byte[] shard)
        {
            uint messageId = 0;
            bool isCanary = false;
            byte[] innerShard = shard;

            if (EnvelopeUtil.TryUnpack(shard, out var env, out var inner))
            {
                innerShard = inner;
                messageId = env.messageId;
                isCanary = (env.flags & 0x08) != 0;

                if (isCanary)
                    _canaryMessageIds.Add(messageId);

                if (verboseNetLog)
                {
                    Debug.Log(
                        $"[Driver.Debug] HandlePackedShard envelope id={env.messageId} payloadLen={env.payloadLen} " +
                        $"flags=0x{env.flags:X2} innerFirst8={BytesPreview(inner, 8)}");
                }

                try
                {
                    _incomingEnvelopeMeta[env.messageId] = (env.payloadHash, env.payloadLen);
                }
                catch { }
            }
            else if (verboseNetLog)
            {
                Debug.Log($"[Driver.Debug] HandlePackedShard raw first8={BytesPreview(shard, 8)}");
            }

            if (innerShard == null || innerShard.Length < 8)
                return;

            ushort total = BitConverter.ToUInt16(innerShard, 0);
            ushort idx = BitConverter.ToUInt16(innerShard, 2);
            uint dataLenU = BitConverter.ToUInt32(innerShard, 4);
            int dataLen = (int)dataLenU;

            if (dataLen < 0 || innerShard.Length < 8 + dataLen)
                return;

            var data = new byte[dataLen];
            Array.Copy(innerShard, 8, data, 0, dataLen);

            if (messageId == 0)
                messageId = (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF);

            var list = _shardRegistry.GetOrCreate(messageId, total, Time.realtimeSinceStartup);

            if (idx < list.Count)
            {
                list[idx] = new ShardInfo
                {
                    total = total,
                    index = idx,
                    dataLen = dataLen,
                    data = data
                };
            }

            _telemetry?.Increment("pack.shards_received");

            // Check se tutti i data shards sono presenti, altrimenti prova recovery semplice.
            bool all = true;
            for (int i = 0; i < list.Count; ++i)
            {
                if (list[i] == null)
                {
                    all = false;
                    break;
                }
            }

            if (!all)
            {
                int totalShards = list.Count;
                int parityCount = fecParityShards;
                int dataShards = Math.Max(1, totalShards - parityCount);

                int missingDataIdx = -1;
                int missingCount = 0;

                for (int i = 0; i < dataShards; ++i)
                {
                    if (list[i] == null)
                    {
                        missingDataIdx = i;
                        missingCount++;
                    }
                }

                // Se manca esattamente 1 data shard e abbiamo parità, prova XOR recovery.
                if (missingCount == 1 && parityCount > 0)
                {
                    int shardPadSize = fecShardSize;
                    var padded = new byte[totalShards][];

                    for (int i = 0; i < totalShards; i++)
                    {
                        var sInfo = list[i];
                        padded[i] = new byte[shardPadSize];

                        if (sInfo != null)
                        {
                            Array.Copy(
                                sInfo.data,
                                0,
                                padded[i],
                                0,
                                Math.Min(sInfo.dataLen, shardPadSize));
                        }
                    }

                    var recovered = new byte[shardPadSize];

                    for (int s = 0; s < totalShards; s++)
                    {
                        var p = padded[s];
                        for (int b = 0; b < shardPadSize; b++)
                            recovered[b] ^= p[b];
                    }

                    int recoveredDataLen = shardPadSize;
                    if (missingDataIdx == dataShards - 1)
                    {
                        // Ultimo shard dati: può essere più corto.
                        recoveredDataLen = Math.Min(recoveredDataLen, shardPadSize);
                    }

                    var recData = new byte[recoveredDataLen];
                    Array.Copy(recovered, 0, recData, 0, recoveredDataLen);

                    list[missingDataIdx] = new ShardInfo
                    {
                        total = (ushort)totalShards,
                        index = (ushort)missingDataIdx,
                        dataLen = recoveredDataLen,
                        data = recData
                    };

                    _telemetry?.Increment("pack.shards_recovered");
                }
                else
                {
                    // Non abbastanza info per ricostruire, attendi altri shard.
                    return;
                }
            }

            // Riassembla payload dai data shards.
            int totalShardsFinal = _shardRegistry.GetTotalCount(messageId);
            int parityCnt = fecParityShards;
            int dataCount = Math.Max(1, totalShardsFinal - parityCnt);

            long payloadLengthLong = 0;
            for (int i = 0; i < dataCount; i++)
                payloadLengthLong += list[i].dataLen;

            if (payloadLengthLong > int.MaxValue)
            {
                CleanupShardBuffer(messageId);
                return;
            }

            int payloadLength = (int)payloadLengthLong;
            var payload = new byte[payloadLength];

            int writePos = 0;
            for (int i = 0; i < dataCount; i++)
            {
                var sInfo = list[i];
                Array.Copy(sInfo.data, 0, payload, writePos, sInfo.dataLen);
                writePos += sInfo.dataLen;
            }

            try
            {
                ulong computed = EnvelopeUtil.ComputeHash64(payload);

                if (_incomingEnvelopeMeta.TryGetValue(messageId, out var meta))
                {
                    if (computed != meta.hash || payloadLength != meta.len)
                    {
                        Debug.LogWarning(
                            $"[Driver.Warning] Hash/len mismatch for messageId={messageId} " +
                            $"computedHash=0x{computed:X16} serverHash=0x{meta.hash:X16} " +
                            $"computedLen={payloadLength} serverLen={meta.len}");

                        RequestFullSnapshotFromServer(true);
                    }
                    else if (verboseNetLog)
                    {
                        Debug.Log(
                            $"[Driver.Debug] Reassembled payload ok id={messageId} len={payloadLength} hash=0x{computed:X16}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Driver.Warning] Failed to verify shard hash id={messageId}: {ex.Message}");
            }

            CleanupShardBuffer(messageId);

            if (isCanary)
            {
                if (verboseNetLog)
                    Debug.Log($"[Driver.Canary] ignoring canary shards id={messageId}");
                return;
            }

            HandlePackedPayload(payload);
        }

        void CleanupShardBuffer(uint messageId)
        {
            _shardRegistry.Forget(messageId);
        }

        void CheckShardBufferTimeouts()
        {
            double now = Time.realtimeSinceStartup;
            _shardRegistry.CollectExpired(now, SHARD_BUFFER_TIMEOUT_SECONDS, _shardTimeoutScratch);

            foreach (var id in _shardTimeoutScratch)
            {
                _telemetry?.Increment("pack.shards_timeout");
                RequestFullSnapshotFromServer(true);
                CleanupShardBuffer(id);
                _incomingEnvelopeMeta.Remove(id);
                _canaryMessageIds.Remove(id);
            }

            _shardTimeoutScratch.Clear();
        }

        // --- Decodifica payload ---

        void HandlePackedPayload(byte[] payload)
        {
            ulong h = ComputeStateHashFromPayload(payload);
            HandlePackedPayload(payload, h);
        }

        void HandlePackedPayload(byte[] payload, ulong serverStateHash)
        {
            MovementSnapshot snap;
            int cs = _chunk ? _chunk.cellSize : 128;

            if (!PackedMovement.TryUnpack(
                    payload, cs,
                    ref _haveAnchor, ref _anchorCellX, ref _anchorCellY,
                    ref _baseSnap, out snap))
            {
                ReportCrcFailureOncePerWindow(
                    "[PackedMovement] CRC mismatch or unpack failure detected");
                _telemetry?.Increment("pack.unpack_fail");
                RequestFullSnapshotFromServer(true);
                return;
            }

            // inserimento ordinato per serverTime
            if (_buffer.Count == 0 || snap.serverTime >= _buffer[_buffer.Count - 1].serverTime)
            {
                _buffer.Add(snap);
            }
            else
            {
                for (int i = 0; i < _buffer.Count; i++)
                {
                    if (snap.serverTime < _buffer[i].serverTime)
                    {
                        _buffer.Insert(i, snap);
                        break;
                    }
                }
            }

            if (_buffer.Count > 256)
                _buffer.RemoveAt(0);

            double now = _netTime.Now();
            double delay = Math.Max(0.0, now - snap.serverTime);

            _emaDelay = (_emaDelay <= 0.0)
                ? delay
                : (1.0 - emaDelayA) * _emaDelay + emaDelayA * delay;

            double dev = Math.Abs(delay - _emaDelay);

            _emaJitter = (_emaJitter <= 0.0)
                ? dev
                : (1.0 - emaJitterA) * _emaJitter + emaJitterA * dev;

            _telemetry?.SetGauge($"client.{OwnerClientId}.buffer_size", _buffer.Count);

            double targetBack = _emaDelay * 1.35 + _emaJitter * 1.6;
            _backTarget = ClampD(targetBack, minBack, maxBack);
            if (_back <= 0.0)
                _back = _backTarget;

            // state hash check
            ulong clientHash = ComputeStateHashForSnapshot(snap);
            if (clientHash != serverStateHash)
            {
                _telemetry?.Event("statehash.mismatch",
                    new Dictionary<string, string>
                    {
                        { "clientId", OwnerClientId.ToString() },
                        { "seq", snap.seq.ToString() }
                    },
                    new Dictionary<string, double>
                    {
                        { "serverHash", (double)serverStateHash },
                        { "clientHash", (double)clientHash }
                    });

                _telemetry?.Increment($"client.{OwnerClientId}.statehash_mismatch");
                RequestFullSnapshotFromServer(true);
            }
            else
            {
                NoteSuccessfulSnapshotDelivery();
            }
        }

        // --- Server: invio full snapshot su richiesta ---

        [ServerRpc(RequireOwnership = false)]
        void RequestFullSnapshotServerRpc(bool preferNoFec)
        {
            if (_shuttingDown || s_AppQuitting)
                return;

            var conn = base.Owner;
            if (conn == null)
                return;

            if (preferNoFec)
                SuppressFecTemporarily(conn);

            Vector3 pos = _serverLastPos;
            Vector3 vel = Vector3.zero;
            double now = _netTime.Now();
            uint seq = _lastSeqSent;
            byte anim = 0;

            var snap = new MovementSnapshot(pos, vel, now, seq, anim);

            byte[] full = PackedMovement.PackFull(
                snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq,
                0, 0, _chunk ? _chunk.cellSize : 128);

            ulong stateHash = ComputeStateHashForSnapshot(snap);

            if (fecParityShards > 0 && !debugForceFullSnapshots && !IsFecSuppressed(conn))
            {
                var shards = BuildFecShards(full, fecShardSize, fecParityShards);
                _lastFullShards[conn] = shards;

                ulong fullHash = EnvelopeUtil.ComputeHash64(full);
                int fullLen = full.Length;
                uint messageId = _nextOutgoingMessageId++;

                if (verboseNetLog)
                {
                    Debug.Log(
                        $"[Server.Debug] RequestFullSnapshotServerRpc sending shards messageId={messageId} " +
                        $"fullLen={fullLen} fullHash=0x{fullHash:X16} totalShards={shards.Count}");
                }

                foreach (var s in shards)
                {
                    if (verboseNetLog)
                        Debug.Log(
                            $"[Server.Debug] Shard idx? shardLen={s.Length} first8={BytesPreview(s, 8)}");

                    byte[] envelopeBytes =
                        CreateEnvelopeBytesForShard(s, messageId, fullLen, fullHash);

                    TargetPackedShardTo(conn, envelopeBytes);
                }
            }
            else
            {
                _lastFullPayload[conn] = full;
                _lastFullSentAt[conn] = _netTime.Now();
                _fullRetryCount[conn] = 0;

                byte[] fullEnv = CreateEnvelopeBytes(full);
                TargetPackedSnapshotTo(conn, fullEnv, stateHash);
            }

            _telemetry?.Increment($"client.{OwnerClientId}.full_requested_by_client");
        }

        void RequestFullSnapshotFromServer(bool preferNoFec = false)
        {
            double now = Time.realtimeSinceStartup;

            if (now - _lastFullRequestTime < FULL_REQUEST_COOLDOWN_SECONDS)
            {
                if (verboseNetLog)
                {
                    Debug.Log(
                        $"[Driver.Debug] Full snapshot request suppressed due to cooldown ({now - _lastFullRequestTime:0.###}s)");
                }
                return;
            }

            _lastFullRequestTime = now;

            if (now - _fullRequestWindowStart > FULL_REQUEST_WINDOW_SECONDS)
            {
                _fullRequestWindowStart = now;
                _fullRequestWindowCount = 0;
                _fecDisableRequested = false;
            }

            _fullRequestWindowCount++;

            if (!_fecDisableRequested && _fullRequestWindowCount >= FULL_REQUEST_DISABLE_THRESHOLD)
            {
                _fecDisableRequested = true;
                preferNoFec = true;
            }

            RequestFullSnapshotServerRpc(preferNoFec);
        }

        void NoteSuccessfulSnapshotDelivery()
        {
            _fullRequestWindowCount = 0;
            _fecDisableRequested = false;
            _fullRequestWindowStart = Time.realtimeSinceStartup;
        }

        // --- State hash utils ---
        static ulong ComputeStateHashForSnapshot(MovementSnapshot s)
        {
            Span<byte> buf = stackalloc byte[8 * 6];
            BitConverter.TryWriteBytes(buf.Slice(0, 8), BitConverter.DoubleToInt64Bits(s.serverTime));
            BitConverter.TryWriteBytes(buf.Slice(8, 8), BitConverter.DoubleToInt64Bits(s.pos.x));
            BitConverter.TryWriteBytes(buf.Slice(16, 8), BitConverter.DoubleToInt64Bits(s.pos.z));
            BitConverter.TryWriteBytes(buf.Slice(24, 8), BitConverter.DoubleToInt64Bits(s.vel.x));
            BitConverter.TryWriteBytes(buf.Slice(32, 8), BitConverter.DoubleToInt64Bits(s.vel.z));
            BitConverter.TryWriteBytes(buf.Slice(40, 8), (long)s.seq);

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(buf.ToArray());
                return BitConverter.ToUInt64(hash, 0);
            }
        }

        static ulong ComputeStateHashFromPayload(byte[] payload)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(payload ?? Array.Empty<byte>());
                return BitConverter.ToUInt64(hash, 0);
            }
        }

        // --- FEC shard building ---
        List<byte[]> BuildFecShards(byte[] payload, int shardSize, int parityCount)
        {
            var shards = new List<byte[]>();
            if (payload == null || payload.Length == 0)
                return shards;

            int effectiveShardSize = Math.Min(shardSize, Math.Max(1, payload.Length));
            int dataShards = (payload.Length + effectiveShardSize - 1) / effectiveShardSize;
            int totalShards = dataShards + parityCount;

            // data shards
            for (int i = 0; i < dataShards; i++)
            {
                int start = i * effectiveShardSize;
                int len = Math.Min(effectiveShardSize, payload.Length - start);

                var s = new byte[2 + 2 + 4 + len];
                Array.Copy(BitConverter.GetBytes((ushort)totalShards), 0, s, 0, 2);
                Array.Copy(BitConverter.GetBytes((ushort)i), 0, s, 2, 2);
                Array.Copy(BitConverter.GetBytes((uint)len), 0, s, 4, 4);
                Array.Copy(payload, start, s, 8, len);

                shards.Add(s);
            }

            // parity shards (XOR)
            for (int p = 0; p < parityCount; p++)
            {
                int maxLen = effectiveShardSize;
                var parityPayload = new byte[maxLen];
                Array.Clear(parityPayload, 0, maxLen);

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

                var parityShard = new byte[2 + 2 + 4 + maxLen];
                Array.Copy(BitConverter.GetBytes((ushort)totalShards), 0, parityShard, 0, 2);
                Array.Copy(BitConverter.GetBytes((ushort)(dataShards + p)), 0, parityShard, 2, 2);
                Array.Copy(BitConverter.GetBytes((uint)maxLen), 0, parityShard, 4, 4);
                Array.Copy(parityPayload, 0, parityShard, 8, maxLen);

                shards.Add(parityShard);
            }

            return shards;
        }

        // --- Helpers interpolazione buffer remoti ---
        static Vector3 Hermite(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            return h00 * p0 + h10 * v0 + h01 * p1 + h11 * v1;
        }

        bool TryGetBracket(double renderT, out MovementSnapshot A, out MovementSnapshot B)
        {
            A = default;
            B = default;

            int n = _buffer.Count;
            if (n < 2)
                return false;

            int r = -1;
            for (int i = 0; i < n; i++)
            {
                if (_buffer[i].serverTime > renderT)
                {
                    r = i;
                    break;
                }
            }

            if (r <= 0)
                return false;

            A = _buffer[r - 1];
            B = _buffer[r];
            return true;
        }

        void CleanupOld(double cutoff)
        {
            int removeCount = 0;

            for (int i = 0; i < _buffer.Count; i++)
            {
                if (_buffer[i].serverTime < cutoff)
                    removeCount++;
                else
                    break;
            }

            if (removeCount > 0 && _buffer.Count - removeCount >= 2)
                _buffer.RemoveRange(0, removeCount);
        }

        static double ClampD(double v, double min, double max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
