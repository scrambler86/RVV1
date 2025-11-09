using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace Game.Networking.Adapters
{
    public partial class PlayerNetworkDriverFishNet
<<<<<<< HEAD
    {
        // BOOKMARK: HANDLE_PACKED_SHARD
        void HandlePackedShard(byte[] shard, NetworkConnection sourceConn = null)
        {
=======
        {
        // BOOKMARK: HANDLE_PACKED_SHARD
        void HandlePackedShard(byte[] shard, NetworkConnection sourceConn = null)
        {
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
            EnsureServices();

            uint messageId = 0;
            bool isCanary = false;
            byte[] innerShard = shard;
            ShardBufferKey bufferKey = ShardBufferKey.ForLocalClient(0);

            if (EnvelopeUtil.TryUnpack(shard, out var env, out var inner))
            {
                innerShard = inner;
                messageId = env.messageId;
                isCanary = (env.flags & 0x08) != 0;

                bufferKey = (IsServerInitialized && sourceConn != null)
                    ? ShardBufferKey.ForConnection(sourceConn, messageId)
                    : ShardBufferKey.ForLocalClient(messageId);

                if (isCanary)
                    _canaryMessageIds.Add(bufferKey);

                if (verboseNetLog)
                {
                    Debug.Log(
                        $"[Driver.Debug] HandlePackedShard envelope id={env.messageId} payloadLen={env.payloadLen} " +
                        $"flags=0x{env.flags:X2} innerFirst8={_packingService.PreviewBytes(inner, 8)}");
                }

                try
                {
                    _incomingEnvelopeMeta[bufferKey] = (env.payloadHash, env.payloadLen);
                }
                catch { }
            }
            else if (verboseNetLog)
            {
                Debug.Log($"[Driver.Debug] HandlePackedShard raw first8={_packingService.PreviewBytes(shard, 8)}");
            }

            if (innerShard == null || innerShard.Length < 8)
                return;
    
            ushort total = BitConverter.ToUInt16(innerShard, 0);
            ushort idx = BitConverter.ToUInt16(innerShard, 2);
            uint dataLenU = BitConverter.ToUInt32(innerShard, 4);
            int dataLen = (int)dataLenU;
    
            if (dataLen < 0 || innerShard.Length < 8 + dataLen)
                return;
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
            byte[] data = new byte[dataLen];
            Array.Copy(innerShard, 8, data, 0, dataLen);

            if (messageId == 0)
                messageId = (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF);

            if (bufferKey.MessageId == 0)
            {
                bufferKey = (IsServerInitialized && sourceConn != null)
                    ? ShardBufferKey.ForConnection(sourceConn, messageId)
                    : ShardBufferKey.ForLocalClient(messageId);
            }

            var list = _shardRegistry.GetOrCreate(bufferKey, total, Time.realtimeSinceStartup);

            if (idx < list.Count)
            {
                list[idx] = new ShardInfo
                {
                    Total = total,
                    Index = idx,
                    DataLength = dataLen,
                    Data = data
                };
            }
    
            _telemetry?.Increment("pack.shards_received");
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
            // Check if all data shards are present or try to recover with FEC
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

                if (_fecService.TryRecover(list, parityCount, dataShards, fecShardSize, out var recoveredShard))
                {
                    list[recoveredShard.Index] = recoveredShard;
                    _telemetry?.Increment("pack.shards_recovered");
                }
                else
                {
                    // Not enough info to reconstruct, wait for more shards
                    return;
                }
            }

            // Reassemble payload from data shards
            int totalShardsFinal = _shardRegistry.GetTotalCount(bufferKey);
            int parityCnt = fecParityShards;
            int dataCount = Math.Max(1, totalShardsFinal - parityCnt);

            long payloadLengthLong = 0;
            for (int i = 0; i < dataCount; i++)
                payloadLengthLong += list[i].DataLength;
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
            if (payloadLengthLong > int.MaxValue)
            {
                CleanupShardBuffer(bufferKey);
                return;
            }
    
            int payloadLength = (int)payloadLengthLong;
            byte[] payload = new byte[payloadLength];
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
            int writePos = 0;
            for (int i = 0; i < dataCount; i++)
            {
                var sInfo = list[i];
                Array.Copy(sInfo.Data, 0, payload, writePos, sInfo.DataLength);
                writePos += sInfo.DataLength;
            }

            try
            {
                ulong computed = EnvelopeUtil.ComputeHash64(payload);

                if (_incomingEnvelopeMeta.TryGetValue(bufferKey, out var meta))
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

            CleanupShardBuffer(bufferKey);

            if (isCanary)
            {
                if (verboseNetLog)
                    Debug.Log($"[Driver.Canary] ignoring canary shards id={messageId}");
                return;
            }
    
            HandlePackedPayload(payload);
        }
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
        // BOOKMARK: SHARD_BUFFER_CLEANUP
        void CleanupShardBuffer(ShardBufferKey key)
        {
            _shardRegistry.Forget(key);
        }

        // BOOKMARK: SHARD_BUFFER_TIMEOUTS
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
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
        // BOOKMARK: HANDLE_PACKED_PAYLOAD
        void HandlePackedPayload(byte[] payload)
        {
            ulong h = ComputeStateHashFromPayload(payload);
            HandlePackedPayload(payload, h);
        }
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
        // BOOKMARK: HANDLE_PACKED_PAYLOAD_WITH_HASH
        void HandlePackedPayload(byte[] payload, ulong serverStateHash)
        {
            MovementSnapshot snap;
            int cs = _chunk ? _chunk.cellSize : 128;
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
            if (!PackedMovement.TryUnpack(payload, cs,
                    ref _haveAnchor, ref _anchorCellX, ref _anchorCellY,
                    ref _baseSnap, out snap))
            {
                ReportCrcFailureOncePerWindow(
                    "[PackedMovement] CRC mismatch or unpack failure detected");
                _telemetry?.Increment("pack.unpack_fail");
                RequestFullSnapshotFromServer(true);
                return;
            }
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
            // ordered insert by serverTime
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
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
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
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
                RequestFullSnapshotFromServer(true);
            }
            else
            {
                NoteSuccessfulSnapshotDelivery();
            }
        }
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
        // BOOKMARK: REQUEST_FULL_SNAPSHOT_SERVER_RPC
        [ServerRpc(RequireOwnership = false)]
        void RequestFullSnapshotServerRpc(bool preferNoFec)
        {
            if (_shuttingDown || s_AppQuitting)
                return;

            EnsureServices();

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
    
            _retryManager.RecordPayload(conn, full, _netTime.Now());

            _retryManager.RecordPayload(conn, full, _netTime.Now());

            if (fecParityShards > 0 && !debugForceFullSnapshots && !IsFecSuppressed(conn))
            {
                var shards = _fecService.BuildShards(full, fecShardSize, fecParityShards);
                _retryManager.RecordShards(conn, shards, _netTime.Now());

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
                            $"[Server.Debug] Shard idx? shardLen={s.Length} first8={_packingService.PreviewBytes(s, 8)}");

                    byte[] envelopeBytes =
                        CreateEnvelopeBytesForShard(s, messageId, fullLen, fullHash);

                    TargetPackedShardTo(conn, envelopeBytes);
                }
            }
            else
            {
                byte[] fullEnv = CreateEnvelopeBytes(full);
                TargetPackedSnapshotTo(conn, fullEnv, stateHash);
            }
    
            _telemetry?.Increment($"client.{OwnerClientId}.full_requested_by_client");
        }
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
        // BOOKMARK: REQUEST_FULL_SNAPSHOT_FROM_SERVER
        void RequestFullSnapshotFromServer(bool preferNoFec = false)
        {
            double now = Time.realtimeSinceStartup;

            EnsureServices();

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
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
        // BOOKMARK: NOTE_SUCCESSFUL_SNAPSHOT
        void NoteSuccessfulSnapshotDelivery()
        {
            _fullRequestWindowCount = 0;
            _fecDisableRequested = false;
            _fullRequestWindowStart = Time.realtimeSinceStartup;
        }
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
        // BOOKMARK: STATE_HASH_UTILS
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
<<<<<<< HEAD

=======
    
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
        // BOOKMARK: BUILD_FEC_SHARDS
        // BOOKMARK: INTERP_HELPERS
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