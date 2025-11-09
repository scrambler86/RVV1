using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;
using UnityEngine.AI;
using Game.Network;

namespace Game.Networking.Adapters
{
    public partial class PlayerNetworkDriverFishNet
        {
        // ==================== SERVER ====================
    
        bool RateLimitOk(double now)
        {
            float cap = maxInputsPerSecond + burstAllowance;
            float refill = (float)((now - _lastRefill) * refillPerSecond);
    
            if (refill > 0f)
            {
                _tokens = Mathf.Min(cap, _tokens + refill);
                _lastRefill = now;
            }
    
            if (_tokens >= 1f)
            {
                _tokens -= 1f;
                return true;
            }
    
            return false;
        }
    
        /// <summary>
        /// Server-side integrazione movimento (authoritative).
        /// Usa solo dir e speed; non si fida della posizione grezza del client.
        /// </summary>
        Vector3 IntegrateServerMovement(Vector3 startPos, Vector3 dir, bool running, float dt)
        {
            if (dt <= 0f)
                return startPos;

            Vector3 planar = new Vector3(dir.x, 0f, dir.z);
            float mag = planar.magnitude;
            if (mag > 1f)
                planar /= mag;

            float speed = running ? _core.speed * _core.runMultiplier : _core.speed;
            Vector3 step = planar * speed * dt;

            Vector3 result = startPos + new Vector3(step.x, 0f, step.z);

            bool hasVerticalInput = !ignoreNetworkY && Mathf.Abs(dir.y) > 0.0001f;
            if (hasVerticalInput)
                result.y = startPos.y + dir.y * speed * dt;
            else if (_core != null)
                result.y = _core.SampleGroundY(result);
            else
                result.y = startPos.y;

            return result;
        }
    
        // ---------- FEC suppression (per-connection) ----------
    
        bool IsFecSuppressed(NetworkConnection conn)
        {
            if (conn == null)
                return false;

            EnsureServices();

            if (_fecSuppressedUntil.TryGetValue(conn, out double until))
            {
                double now = _netTime.Now();
                if (now < until)
                    return true;
    
                _fecSuppressedUntil.Remove(conn);
            }
    
            return false;
        }
    
        void SuppressFecTemporarily(NetworkConnection conn)
        {
            if (conn == null)
                return;

            EnsureServices();

            _fecSuppressedUntil[conn] = _netTime.Now() + FEC_DISABLE_DURATION_SECONDS;
        }
    
        // ---------- Input dal client → server authoritative ----------
    
        [ServerRpc(RequireOwnership = false)]
        void CmdSendInput(Vector3 dir,
                          Vector3 clientPredictedPos,
                          bool running,
                          uint seq,
                          bool isCTM,
                          Vector3[] pathCorners,
                          double timestamp)
        {
            if (_shuttingDown || s_AppQuitting)
                return;

            EnsureServices();
            EnsureOwnerRuntime();

            double now = _netTime.Now();
            if (!RateLimitOk(now))
                return;
    
            // dt robusto (server clock vs timestamp client)
            double dtServerRaw = now - _serverLastTime;
            double dtClientEstimate = Math.Max(0.0, now - timestamp);
            double dt = Math.Max(0.001, Math.Max(dtServerRaw, dtClientEstimate));
            float dtF = (float)dt;
    
            _serverLastTime = now;
    
            // Predicted pos del client (solo per anti-cheat / diagnostica)
            Vector3 predictedPos = clientPredictedPos;
    
            // Integrazione authoritative lato server
            Vector3 serverIntegratedPos = IntegrateServerMovement(_serverLastPos, dir, running, dtF);
            _telemetry?.Observe(
                $"client.{OwnerClientId}.predicted_vs_integrated_cm",
                Vector3.Distance(serverIntegratedPos, clientPredictedPos) * 100.0);
    
            // Stima RTT e slack di tolleranza
            double oneWay = Math.Max(0.0, now - timestamp); // ≈ metà RTT
            _lastRttMs = oneWay * 2000.0;
    
            float stepSlack = Mathf.Clamp(
                1f + (float)(oneWay * 2.0) * slackK,
                slackMin,
                slackMax);
    
            float speed = running ? _core.speed * _core.runMultiplier : _core.speed;
            float maxStep = speed * dtF * maxSpeedTolerance * stepSlack;
    
            _telemetry?.Increment($"client.{OwnerClientId}.inputs_received");
            _telemetry?.Observe($"client.{OwnerClientId}.maxStep_cm", maxStep * 100.0);
    
            // ---------- Anti-cheat ----------
            bool ok = true;
            if (_anti != null)
            {
                if (_anti is AntiCheatManager acm)
                    ok = acm.ValidateInput(this, seq, timestamp, predictedPos, _serverLastPos, maxStep, pathCorners, running, dtF);
                else
                    ok = _anti.ValidateInput(this, seq, timestamp, predictedPos, _serverLastPos, maxStep, pathCorners, running);
            }
    
            if (!ok)
            {
                Vector3 delta = predictedPos - _serverLastPos;
                Vector3 planar = new Vector3(delta.x, 0f, delta.z);
                float planarDist = planar.magnitude;
    
                _telemetry?.Observe($"client.{OwnerClientId}.planarDist_cm", planarDist * 100.0);
    
                var tags = new Dictionary<string, string>
                {
                    { "clientId", OwnerClientId.ToString() },
                    { "event", "soft_clamp" }
                };
                var metrics = new Dictionary<string, double>
                {
                    { "planarDist_cm", planarDist * 100.0 },
                    { "maxStep_cm", maxStep * 100.0 },
                    { "rtt_ms", _lastRttMs }
                };
    
                int sampleCount = 0;
                var inpList = new List<string>();
                var ownerInputs = _ownerRuntime?.InputBuffer;
                if (ownerInputs != null)
                {
                    foreach (var inp in ownerInputs)
                    {
                        if (sampleCount++ >= 6) break;
                        inpList.Add($"{inp.seq}:{inp.dir.x:0.00},{inp.dir.z:0.00}");
                    }
                }
                if (inpList.Count > 0)
                    tags["input_sample"] = string.Join("|", inpList);
    
                _telemetry?.Event("anti_cheat.soft_clamp", tags, metrics);
    
                if (planarDist > 1e-6f)
                {
                    float allowed = Mathf.Max(0f, maxStep);
    
                    if (planarDist > allowed)
                    {
                        predictedPos =
                            _serverLastPos +
                            planar.normalized * allowed +
                            new Vector3(
                                0f,
                                Mathf.Clamp(delta.y,
                                    -maxVerticalSpeed * dtF,
                                    maxVerticalSpeed * dtF),
                                0f);
                    }
                    else
                    {
                        predictedPos = _serverLastPos;
                    }
                }
                else
                {
                    predictedPos = _serverLastPos;
                }
    
                if (_anti is AntiCheatManager ac && ac.debugLogs && verboseNetLog)
                {
                    Debug.LogWarning(
                        $"[AC] Soft-clamp seq={seq} clientDelta={planarDist:0.###} " +
                        $"maxStep={maxStep:0.###} rttMs={_lastRttMs:0.0}");
                }
    
                _telemetry?.Increment($"client.{OwnerClientId}.anti_cheat.soft_clamps");
                _telemetry?.Increment("anti_cheat.soft_clamps");
            }
    
            // ---------- NavMesh + velocità ----------
            bool hasVerticalInput = !ignoreNetworkY && Mathf.Abs(dir.y) > 0.0001f;

            Vector3 finalPos = ok ? serverIntegratedPos : predictedPos;

            if (!hasVerticalInput && _core != null)
            {
                float sampleY = _core.SampleGroundY(finalPos);
                finalPos.y = sampleY;
            }

            if (validateNavMesh &&
                NavMesh.SamplePosition(finalPos, out var nh, navMeshMaxSampleDist, NavMesh.AllAreas))
            {
                finalPos = nh.position;
                if (!hasVerticalInput && _core != null)
                {
                    float sampleY = _core.SampleGroundY(finalPos);
                    finalPos.y = sampleY;
                }
            }
    
            Vector3 deltaPos = finalPos - _serverLastPos;
            Vector3 vel = deltaPos / dtF;
    
            // Clamp verticale
            if (Mathf.Abs(vel.y) > maxVerticalSpeed)
            {
                finalPos.y = _serverLastPos.y;
                deltaPos = finalPos - _serverLastPos;
                vel = deltaPos / dtF;
            }
            vel.y = 0f;
    
            // Aggiorna stato server authoritative
            Vector3 oldServerLast = _serverLastPos;
            _serverLastPos = finalPos;
    
            byte anim = (byte)((vel.magnitude > 0.12f)
                ? (running ? 2 : 1)
                : 0);
    
            var snap = new MovementSnapshot(finalPos, vel, now, seq, anim);
    
            // Lag compensation buffer
            GetComponent<LagCompBuffer>()?.Push(finalPos, vel, now);
    
            // ---------- Decide se forzare un FULL keyframe ----------
            bool requireFull = false;
            float planarErr = Vector3.Distance(finalPos, oldServerLast);
    
            _sinceKeyframe.TryGetValue(Owner, out int sinceKFcount);
            if (sinceKFcount >= Math.Max(1, keyframeEvery / 2))
                requireFull = true;
    
            if (planarErr > hardSnapDist * 0.9f &&
                (now - _lastReconcileSentTime) < RECONCILE_COOLDOWN_SEC * 1.5)
            {
                requireFull = true;
                _telemetry?.Increment("reconcile.suppressed_in_favor_of_full");
            }
    
            _telemetry?.Increment($"client.{OwnerClientId}.snap_broadcasts");
    
            if (requireFull)
            {
                short cellX = 0, cellY = 0;
                int cellSize = DEFAULT_CHUNK_CELL_SIZE;

                if (_chunk != null)
                {
                    if (_chunk.TryGetCellOf(Owner, out var ccell))
                    {
                        cellX = (short)ccell.x;
                        cellY = (short)ccell.y;
                    }

                    cellSize = _chunk.cellSize;
                }

                byte[] full = PackedMovement.PackFull(
                    snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq,
                    cellX, cellY, cellSize);
    
                ulong stateHash = ComputeStateHashForSnapshot(snap);
    
                _retryManager.RecordPayload(Owner, full, _netTime.Now());

                if (fecParityShards > 0 && !debugForceFullSnapshots && !IsFecSuppressed(Owner))
                {
                    var shards = _fecService.BuildShards(full, fecShardSize, fecParityShards);
                    _retryManager.RecordShards(Owner, shards, _netTime.Now());

                    ulong fullHash = EnvelopeUtil.ComputeHash64(full);
                    int fullLen = full.Length;
                    uint messageId = _nextOutgoingMessageId++;

                    if (verboseNetLog)
                    {
                        Debug.Log(
                            $"[Server.Debug] Sending full as shards messageId={messageId} " +
                            $"fullLen={fullLen} fullHash=0x{fullHash:X16} totalShards={shards.Count}");
                    }

                    for (int i = 0; i < shards.Count; i++)
                    {
                        var s = shards[i];

                        if (verboseNetLog)
                        {
                            Debug.Log(
                                $"[Server.Debug] Shard idx={i} shardLen={s.Length} shardHead={_packingService.PreviewBytes(s, 8)}");
                        }

                        byte[] envelopeBytes =
                            CreateEnvelopeBytesForShard(s, messageId, fullLen, fullHash);

                        TargetPackedShardTo(Owner, envelopeBytes);
                    }
                }
                else
                {
                    byte[] fullEnv = CreateEnvelopeBytes(full);
                    TargetPackedSnapshotTo(Owner, fullEnv, stateHash);
                }
    
                _telemetry?.Increment($"client.{OwnerClientId}.full_forced");
            }
            else
            {
                // Correzione al proprietario basata sulla posizione authoritative finale
                SendTargetOwnerCorrection(seq, finalPos);
            }
    
            // Broadcast agli altri client secondo interest management
            Server_BroadcastPacked(snap);
        }
    
        // ---------- Ping / ClockSync RPCs ----------
    
        [ServerRpc(RequireOwnership = false)]
        public void PingRequest(double clientSendTimeSeconds)
        {
            if (_shuttingDown || s_AppQuitting)
                return;
    
            double serverRecv = _netTime.Now();
            PingReply(Owner, clientSendTimeSeconds, serverRecv, _netTime.Now());
        }
    
        [TargetRpc]
        public void PingReply(NetworkConnection conn,
                              double clientSendTimeSeconds,
                              double serverRecvTimeSeconds,
                              double serverSendTimeSeconds)
        {
            if (_shuttingDown || s_AppQuitting)
                return;
    
            OnClientReceivePingReply(
                clientSendTimeSeconds,
                serverRecvTimeSeconds,
                serverSendTimeSeconds);
        }
    
        void OnClientReceivePingReply(double clientSendTimeSeconds,
                                      double serverRecvTimeSeconds,
                                      double serverSendTimeSeconds)
        {
            double clientRecvTimeSeconds = _netTime.Now();
    
            double rttMs = Math.Max(0.0,
                (clientRecvTimeSeconds - clientSendTimeSeconds) * 1000.0);
            _lastRttMs = rttMs;
    
            double serverMid =
                (serverRecvTimeSeconds + serverSendTimeSeconds) * 0.5;
            double clientMid =
                clientSendTimeSeconds +
                (clientRecvTimeSeconds - clientSendTimeSeconds) * 0.5;
    
            double offsetMs = (serverMid - clientMid) * 1000.0;
    
            if (_clockOffsetEmaMs == 0.0)
                _clockOffsetEmaMs = offsetMs;
            else
                _clockOffsetEmaMs =
                    (1.0 - CLOCK_ALPHA) * _clockOffsetEmaMs +
                    CLOCK_ALPHA * offsetMs;
    
            double sampleJ = Math.Abs(offsetMs - _clockOffsetEmaMs);
            if (_clockOffsetJitterMs == 0.0)
                _clockOffsetJitterMs = sampleJ;
            else
                _clockOffsetJitterMs =
                    (1.0 - CLOCK_ALPHA_JITTER) * _clockOffsetJitterMs +
                    CLOCK_ALPHA_JITTER * sampleJ;
    
            _clockOffsetSeconds = _clockOffsetEmaMs / 1000.0;
    
            _telemetry?.Observe($"client.{OwnerClientId}.rtt_ms", rttMs);
            _telemetry?.Observe($"client.{OwnerClientId}.clock_offset_ms", offsetMs);
    
            _telemetry?.Event("clock.sample",
                new Dictionary<string, string>
                {
                    { "clientId", OwnerClientId.ToString() },
                    { "sampleType", "pingReply" }
                },
                new Dictionary<string, double>
                {
                    { "rtt_ms", rttMs },
                    { "offset_ms", offsetMs }
                });
    
            var csm = GetComponentInChildren<ClockSyncManager>();
            if (csm != null)
                csm.RecordSample(rttMs, offsetMs);
        }
    
        public double GetEstimatedClientToServerOffsetSeconds()
            => _clockOffsetSeconds;
    
        public double GetLastMeasuredRttMs()
            => _lastRttMs;
    
        // ---------- Broadcast snapshot osservatori ----------
    
        void Server_BroadcastPacked(MovementSnapshot snap)
        {
            if (_shuttingDown || s_AppQuitting)
                return;
    
            if (forceBroadcastAll || _chunk == null || Owner == null)
            {
                byte[] envBytes = PackFullForObservers(snap);
                ObserversPackedSnapshot(envBytes);
                return;
            }
    
            _chunk.CollectWithinRadius(Owner, nearRing, _tmpNear);
            _chunk.CollectWithinRadius(Owner, midRing, _tmpMid);
            _chunk.CollectWithinRadius(Owner, farRing, _tmpFar);
    
            foreach (var c in _tmpNear) _tmpMid.Remove(c);
            foreach (var c in _tmpNear) _tmpFar.Remove(c);
            foreach (var c in _tmpMid) _tmpFar.Remove(c);
    
            double now = _netTime.Now();
    
            short cellX = 0, cellY = 0;
            if (_chunk.TryGetCellOf(Owner, out var cell))
            {
                cellX = (short)cell.x;
                cellY = (short)cell.y;
            }
    
            foreach (var conn in _tmpNear)
                TrySendPackedTo(conn, snap, cellX, cellY, now, 1.0 / Math.Max(1, nearHz));
    
            foreach (var conn in _tmpMid)
                TrySendPackedTo(conn, snap, cellX, cellY, now, 1.0 / Math.Max(1, midHz));
    
            foreach (var conn in _tmpFar)
                TrySendPackedTo(conn, snap, cellX, cellY, now, 1.0 / Math.Max(1, farHz));
    
            _tmpNear.Clear();
            _tmpMid.Clear();
            _tmpFar.Clear();
        }
    
        byte[] PackFullForObservers(MovementSnapshot snap)
        {
            short cx = 0, cy = 0;
            if (_chunk && Owner != null && _chunk.TryGetCellOf(Owner, out var cell))
            {
                cx = (short)cell.x;
                cy = (short)cell.y;
            }
    
            int cs = _chunk ? _chunk.cellSize : 128;
            return PackedMovement.PackFull(
                snap.pos, snap.vel, snap.animState, snap.serverTime,
                snap.seq, cx, cy, cs);
        }
    
        void TrySendPackedTo(NetworkConnection conn,
                             MovementSnapshot snap,
                             short cellX, short cellY,
                             double now,
                             double interval)
        {
            if (_shuttingDown || s_AppQuitting)
                return;
            if (conn == null || !conn.IsActive)
                return;
            if (_nextSendAt.TryGetValue(conn, out var t) && now < t)
                return;
    
            bool sendFull = false;
    
            if (!_lastSentCell.TryGetValue(conn, out var lastCell))
                sendFull = true;
            else if (lastCell.cellX != cellX || lastCell.cellY != cellY)
                sendFull = true;
    
            _lastSentCell[conn] = (cellX, cellY);
    
            _sinceKeyframe.TryGetValue(conn, out int sinceKF);
            if (keyframeEvery > 0 && sinceKF >= keyframeEvery)
                sendFull = true;
    
            if (debugForceFullSnapshots)
                sendFull = true;
    
            byte[] payload = null;
    
            if (!sendFull && _lastSentSnap.TryGetValue(conn, out var last))
            {
                var lastSnapLocal = last;
    
                payload = PackedMovement.PackDelta(
                    in lastSnapLocal,
                    snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq,
                    cellX, cellY, _chunk.cellSize,
                    maxPosDeltaCm, maxVelDeltaCms, maxDtMs);
    
                if (payload == null)
                {
                    sendFull = true;
                    _telemetry?.Increment("pack.fallback_count");
                }
            }
    
            if (sendFull)
            {
                payload = PackedMovement.PackFull(
                    snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq,
                    cellX, cellY, _chunk.cellSize);
    
                _sinceKeyframe[conn] = 0;
    
                ulong stateHash = ComputeStateHashForSnapshot(snap);
    
                _retryManager.RecordPayload(conn, payload, now);

                if (fecParityShards > 0 && !debugForceFullSnapshots && !IsFecSuppressed(conn))
                {
                    var shards = _fecService.BuildShards(payload, fecShardSize, fecParityShards);
                    _retryManager.RecordShards(conn, shards, now);

                    ulong fullHash = EnvelopeUtil.ComputeHash64(payload);
                    int fullLen = payload.Length;
                    uint messageId = _nextOutgoingMessageId++;

                    if (verboseNetLog)
                    {
                        Debug.Log(
                            $"[Server.Debug] Sending full as shards messageId={messageId} " +
                            $"fullLen={fullLen} fullHash=0x{fullHash:X16} totalShards={shards.Count}");
                    }

                    for (int i = 0; i < shards.Count; i++)
                    {
                        var s = shards[i];

                        if (verboseNetLog)
                        {
                            Debug.Log(
                                $"[Server.Debug] Shard idx={i} shardLen={s.Length} shardHead={_packingService.PreviewBytes(s, 8)}");
                        }

                        byte[] envelopeBytes =
                            CreateEnvelopeBytesForShard(s, messageId, fullLen, fullHash);

                        TargetPackedShardTo(conn, envelopeBytes);
                    }
                }
                else
                {
                    byte[] fullEnv = CreateEnvelopeBytes(payload);
                    TargetPackedSnapshotTo(conn, fullEnv, stateHash);
                }
            }
            else
            {
                _sinceKeyframe[conn] = sinceKF + 1;
    
                var lastSnapLocal = _lastSentSnap[conn];
    
                byte[] deltaPayload = PackedMovement.PackDelta(
                    in lastSnapLocal,
                    snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq,
                    cellX, cellY, _chunk.cellSize,
                    maxPosDeltaCm, maxVelDeltaCms, maxDtMs);
    
                byte[] deltaEnv = CreateEnvelopeBytes(deltaPayload);
                TargetPackedSnapshotTo(conn, deltaEnv, ComputeStateHashForSnapshot(snap));
            }
    
            _lastSentSnap[conn] = snap;
            _nextSendAt[conn] = now + interval;
        }
    
        [ObserversRpc]
        void ObserversPackedSnapshot(byte[] payload)
        {
            if (_shuttingDown || s_AppQuitting)
                return;

            EnsureServices();

            int olen = payload?.Length ?? 0;
    
            if (verboseNetLog)
            {
                Debug.Log(
                    $"[Driver.Debug] ObserversPackedSnapshot len={olen} first8={_packingService.PreviewBytes(payload, 8)} " +
                    $"envelope={EnvelopeUtil.TryUnpack(payload, out var _, out var _)}");
            }
    
            if (payload == null || payload.Length < 8)
            {
                if (verboseNetLog)
                    Debug.LogWarning($"[Driver] Ignoring too-small observers payload len={payload?.Length ?? 0}");
                return;
            }
    
            if (EnvelopeUtil.TryUnpack(payload, out var envObs, out var innerObs))
            {
                if ((envObs.flags & 0x08) != 0)
                {
                    if (verboseNetLog)
                        Debug.Log($"[Driver.Canary] observers canary id={envObs.messageId} len={envObs.payloadLen}");
                    return;
                }
    
                if (verboseNetLog)
                {
                        Debug.Log(
                            $"[Driver.Debug] ObserversPackedSnapshot envelope id={envObs.messageId} " +
                            $"payloadLen={envObs.payloadLen} innerFirst8={_packingService.PreviewBytes(innerObs, 8)}");
                }
    
                payload = innerObs;
            }
            else
            {
                if (verboseNetLog)
                {
                        Debug.Log(
                            $"[Driver.Debug] ObserversPackedSnapshot raw first8={_packingService.PreviewBytes(payload, 8)}");
                }
            }
    
            HandlePackedPayload(payload);
        }
    
        [TargetRpc]
        void TargetPackedSnapshotTo(NetworkConnection conn,
                                    byte[] payload,
                                    ulong stateHash)
        {
            if (_shuttingDown || s_AppQuitting)
                return;

            EnsureServices();

            int len = payload?.Length ?? 0;
    
            if (verboseNetLog)
            {
                    Debug.Log(
                        $"[Driver.Debug] TargetPackedSnapshotTo conn={conn?.ClientId} len={len} " +
                        $"first8={_packingService.PreviewBytes(payload, 8)} " +
                        $"envelope={EnvelopeUtil.TryUnpack(payload, out var _, out var _)}");
            }
    
            if (payload == null || payload.Length < 8)
            {
                if (verboseNetLog)
                    Debug.LogWarning($"[Driver] Ignoring too-small payload len={payload?.Length ?? 0}");
                return;
            }
    
            if (EnvelopeUtil.TryUnpack(payload, out var env, out var inner))
            {
                if ((env.flags & 0x08) != 0)
                {
                    if (verboseNetLog)
                        Debug.Log($"[Driver.Canary] full canary id={env.messageId} len={env.payloadLen}");
                    return;
                }
    
                if (verboseNetLog)
                {
                        Debug.Log(
                            $"[Driver.Debug] TargetPackedSnapshotTo envelope id={env.messageId} " +
                            $"payloadLen={env.payloadLen} innerFirst8={_packingService.PreviewBytes(inner, 8)}");
                }
    
                try
                {
                    var key = ShardBufferKey.ForConnection(conn, env.messageId);
                    _incomingEnvelopeMeta[key] = (env.payloadHash, env.payloadLen);
                }
                catch { }
    
                payload = inner;
            }
            else
            {
                if (verboseNetLog)
                {
                    Debug.Log(
                        $"[Driver.Debug] TargetPackedSnapshotTo raw first8={_packingService.PreviewBytes(payload, 8)} " +
                        $"firstByte={(payload.Length > 0 ? payload[0] : 0)}");
                }
            }
    
            HandlePackedPayload(payload, stateHash);
        }
    
        [TargetRpc]
        void TargetPackedShardTo(NetworkConnection conn, byte[] shard)
        {
            if (_shuttingDown || s_AppQuitting)
                return;

            EnsureServices();

            int slen = shard?.Length ?? 0;
    
            if (verboseNetLog)
            {
                Debug.Log(
                    $"[Driver.Debug] TargetPackedShardTo conn={conn?.ClientId} len={slen} " +
                    $"first8={_packingService.PreviewBytes(shard, 8)} " +
                    $"envelope={EnvelopeUtil.TryUnpack(shard, out var _, out var _)}");
            }
    
            if (shard == null || shard.Length < 8)
            {
                if (verboseNetLog)
                    Debug.LogWarning($"[Driver] Ignoring too-small shard len={shard?.Length ?? 0}");
                return;
            }
    
            HandlePackedShard(shard, conn);
        }
    
        [ServerRpc(RequireOwnership = false)]
        void ServerAckFullSnapshot(uint ackSeq, ulong clientStateHash)
        {
            if (_shuttingDown || s_AppQuitting)
                return;

            EnsureServices();

            var conn = base.Owner;
            if (conn == null)
                return;

            _retryManager.Clear(conn);

            _telemetry?.Increment($"client.{OwnerClientId}.full_ack");
        }
    
        // ---------- Correction owner ----------
    
        void SendTargetOwnerCorrection(uint serverSeq, Vector3 serverPos)
        {
            try
            {
                TargetOwnerCorrection(Owner, serverSeq, serverPos);
            }
            catch
            {
                // Owner può non essere valido durante shutdown / despawn, ignora
            }
        }
    }
}
