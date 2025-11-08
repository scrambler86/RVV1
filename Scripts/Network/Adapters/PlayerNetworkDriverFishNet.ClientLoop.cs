using System;
using System.Collections.Generic;
using System.Linq;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace Game.Networking.Adapters
{
    public partial class PlayerNetworkDriverFishNet
    {
        // ---------- main loop ----------
        void FixedUpdate()
        {
            if (!IsSpawned || _shuttingDown || s_AppQuitting)
                return;

            ProcessShardBufferTimeouts();

            if (IsOwner)
                TickOwnerClient();
            else
                TickRemoteClient();

            if (IsServerInitialized)
                _chunk?.UpdatePlayerChunk(this, _rb.position);

            if (IsServerStarted)
                TickServerResendLoop();
        }

        // Mantiene vivo il buffer shard (timeout + cleanup)
        // Implementazione effettiva in altro partial: CheckShardBufferTimeouts().
        void ProcessShardBufferTimeouts()
        {
            CheckShardBufferTimeouts();
        }

        /// <summary>
        /// Owner-side: invio input, elastic correction, hard snap, reconcile.
        /// </summary>
        void TickOwnerClient()
        {
            Owner_Send();
            Owner_ApplyElasticCorrection();
            Owner_ProcessHardSnap();
            Owner_ProcessReconciliation();
        }

        /// <summary>
        /// Remote-side: sola interpolazione / extrapolazione.
        /// </summary>
        void TickRemoteClient()
        {
            Remote_Update();
        }

        /// <summary>
        /// Server-side: retry per full snapshot affidabili (FEC / full fallback).
        /// </summary>
        void TickServerResendLoop()
        {
            if (_lastFullPayload.Count == 0 && _lastFullShards.Count == 0)
                return;

            double now = _netTime.Now();
            _serverRetryScratch.Clear();

            // Payload completi in attesa di ACK
            foreach (var kv in _lastFullPayload)
            {
                var conn = kv.Key;
                if (conn == null || !conn.IsActive)
                    continue;

                if (!_lastFullSentAt.TryGetValue(conn, out var sentAt))
                    continue;

                int tries = _fullRetryCount.TryGetValue(conn, out var count) ? count : 0;
                if (tries >= FULL_RETRY_MAX)
                    continue;

                if (now - sentAt > FULL_RETRY_SECONDS)
                    _serverRetryScratch.Add(conn);
            }

            // Shard FEC in attesa di ACK
            foreach (var kv in _lastFullShards)
            {
                var conn = kv.Key;
                if (conn == null || !conn.IsActive)
                    continue;

                if (!_lastFullSentAt.TryGetValue(conn, out var sentAt))
                    continue;

                int tries = _fullRetryCount.TryGetValue(conn, out var count) ? count : 0;
                if (tries >= FULL_RETRY_MAX)
                    continue;

                if (now - sentAt > FULL_RETRY_SECONDS)
                    _serverRetryScratch.Add(conn);
            }

            // Retry effettivo
            foreach (var conn in _serverRetryScratch)
            {
                if (_lastFullShards.TryGetValue(conn, out var shards) &&
                    shards != null && shards.Count > 0)
                {
                    byte[] lastFull = null;
                    if (_lastFullPayload.TryGetValue(conn, out var payloadBytes))
                        lastFull = payloadBytes;

                    ulong fullHash = (lastFull != null)
                        ? EnvelopeUtil.ComputeHash64(lastFull)
                        : 0;
                    int fullLen = (lastFull != null) ? lastFull.Length : 0;

                    uint messageId = _nextOutgoingMessageId++;
                    if (verboseNetLog)
                    {
                        Debug.Log(
                            $"[Server.Debug] Retry sending shards messageId={messageId} " +
                            $"totalShards={shards.Count} fullLen={fullLen} fullHash=0x{fullHash:X16}");
                    }

                    foreach (var shard in shards)
                    {
                        if (verboseNetLog)
                            Debug.Log(
                                $"[Server.Debug] Retry shard len={shard.Length} first8={BytesPreview(shard, 8)}");

                        byte[] envelopeBytes =
                            CreateEnvelopeBytesForShard(shard, messageId, fullLen, fullHash);
                        TargetPackedShardTo(conn, envelopeBytes);
                    }
                }
                else if (_lastFullPayload.TryGetValue(conn, out var payload))
                {
                    byte[] payloadEnv = CreateEnvelopeBytes(payload);
                    TargetPackedSnapshotTo(conn, payloadEnv, ComputeStateHashFromPayload(payload));
                }

                _lastFullSentAt[conn] = _netTime.Now();
                _fullRetryCount[conn] = _fullRetryCount.TryGetValue(conn, out var previousTries)
                    ? previousTries + 1
                    : 1;

                _telemetry?.Increment("pack.full_retry");
            }

            _serverRetryScratch.Clear();
        }

        /// <summary>
        /// Owner-side elastic correction verso target autorevole.
        /// </summary>
        void Owner_ApplyElasticCorrection()
        {
            if (!_isApplyingElastic)
                return;

            float lastPlanarSpeed = (_core != null) ? _core.DebugPlanarSpeed : 0f;
            float distToTarget = Vector3.Distance(_rb.position, _elasticTargetPos);

            // Evita micro jitter quando il player è praticamente fermo vicino al target.
            if (lastPlanarSpeed < 0.02f &&
                distToTarget < Mathf.Max(0.06f, correctionMinVisible))
            {
                _isApplyingElastic = false;
                _elasticElapsed = 0f;
                _elasticCurrentMultiplier = 1f;
                _telemetry?.Increment($"client.{OwnerClientId}.elastic_cancelled_at_rest");
                return;
            }

            _elasticElapsed += Time.fixedDeltaTime;
            float t = Mathf.Clamp01(_elasticElapsed / _elasticDuration);
            float ease = 1f - Mathf.Pow(1f - t, 3f);

            Vector3 current = _rb.position;
            Vector3 target = Vector3.Lerp(_elasticStartPos, _elasticTargetPos, ease);
            float maxAllowed = maxCorrectionSpeed * Time.fixedDeltaTime * _elasticCurrentMultiplier;
            Vector3 next = Vector3.MoveTowards(current, target, maxAllowed);

            if (remoteMoveVisualOnly && _core && _core.visualRoot != null)
                _core.visualRoot.position = next;
            else
                _rb.MovePosition(next);

            if (_agent)
                _agent.nextPosition = next;

            float applied = Vector3.Distance(current, next);
            _telemetry?.Observe($"client.{OwnerClientId}.elastic_applied_cm", applied * 100.0);
            _telemetry?.Observe($"client.{OwnerClientId}.elastic_progress", ease * 100.0);

            _elasticCurrentMultiplier *= correctionDecay;

            if (t >= 1f ||
                Vector3.Distance(next, _elasticTargetPos) < correctionMinVisible)
            {
                _isApplyingElastic = false;
                _elasticElapsed = 0f;
                _elasticCurrentMultiplier = 1f;
                _telemetry?.Increment($"client.{OwnerClientId}.elastic_completed");
            }
        }

        /// <summary>
        /// Hard snap owner-side se necessario.
        /// </summary>
        void Owner_ProcessHardSnap()
        {
            if (!_doHardSnapNextFixed || _isApplyingElastic)
                return;

            Vector3 current = _rb.position;
            Vector3 target = _pendingHardSnap;
            float distance = Vector3.Distance(current, target);

            // Se vicino, snap morbido con MovePosition.
            if (distance <= 0.15f)
            {
                _rb.MovePosition(target);
                if (_agent) _agent.nextPosition = target;
                _doHardSnapNextFixed = false;
                return;
            }

            // Se molto lontano, warp sicuro una volta.
            if (distance > hardSnapDist * 1.5f)
            {
                bool prevKinematic = _rb.isKinematic;
                _rb.isKinematic = true;
                _rb.position = target;
                _rb.isKinematic = prevKinematic;

                if (_agent) _agent.Warp(target);
                _doHardSnapNextFixed = false;
                return;
            }

            // Altrimenti avvicina con MoveTowards.
            Vector3 next = Vector3.MoveTowards(
                current, target,
                maxCorrectionSpeed * Time.fixedDeltaTime * 1.8f);

            _rb.MovePosition(next);
            if (_agent) _agent.nextPosition = next;

            if (Vector3.Distance(next, target) < 0.05f)
                _doHardSnapNextFixed = false;
        }

        /// <summary>
        /// Reconcile dolce verso lo stato server authoritative.
        /// </summary>
        void Owner_ProcessReconciliation()
        {
            if (!_reconcileActive || _isApplyingElastic)
                return;

            float lastPlanarSpeed = (_core != null) ? _core.DebugPlanarSpeed : 0f;

            Vector3 current = _rb.position;
            Vector3 toTarget = _reconcileTarget - current;
            float distance = toTarget.magnitude;

            // Se quasi allineato e fermo, termina.
            if (lastPlanarSpeed < 0.02f &&
                distance < Mathf.Max(0.06f, correctionMinVisible))
            {
                _reconcileActive = false;
                return;
            }

            if (distance <= 0.0001f)
            {
                _reconcileActive = false;
                return;
            }

            // Se molto distante → hard snap (con rate limit).
            if (distance > hardSnapDist)
            {
                double now = _netTime.Now();
                if (now - _lastHardSnapTime > hardSnapRateLimitSeconds)
                {
                    _pendingHardSnap = _reconcileTarget;
                    _doHardSnapNextFixed = true;
                    _reconcileActive = false;
                    _lastHardSnapTime = now;
                    _telemetry?.Increment("reconcile.hard_snaps");
                    _telemetry?.Increment($"client.{OwnerClientId}.hard_snaps");
                }
                else
                {
                    float maxAllowed = maxCorrectionSpeed * Time.fixedDeltaTime;
                    Vector3 step = Vector3.ClampMagnitude(toTarget, maxAllowed);
                    Vector3 next = current + step;
                    Vector3 smoothedRateLimited =
                        Vector3.Lerp(current, next, 1f - reconciliationSmoothing);

                    _rb.MovePosition(smoothedRateLimited);
                    if (_agent) _agent.nextPosition = smoothedRateLimited;
                    _telemetry?.Increment("reconcile.rate_limited_snaps");
                }

                return;
            }

            // Smooth reconcile.
            float alpha = 1f - Mathf.Exp(-reconcileRate * Time.fixedDeltaTime * 0.66f);
            float maxAllowedFinal = maxCorrectionSpeed * Time.fixedDeltaTime;

            Vector3 desired = Vector3.Lerp(current, _reconcileTarget, alpha);
            Vector3 capped = Vector3.MoveTowards(current, desired, maxAllowedFinal);
            Vector3 smoothed = Vector3.Lerp(current, capped, 1f - reconciliationSmoothing);

            _rb.MovePosition(smoothed);
            if (_agent) _agent.nextPosition = smoothed;

            if ((smoothed - _reconcileTarget).sqrMagnitude < 0.0004f)
                _reconcileActive = false;

            _telemetry?.Increment("reconcile.smooth_steps");
        }

        // ---------- owner input send ----------
        void Owner_Send()
        {
            if (_shuttingDown || s_AppQuitting)
                return;

            _sendDt = 1f / Mathf.Max(1, sendRateHz);
            _sendTimer += Time.fixedDeltaTime;
            if (_sendTimer < _sendDt)
                return;
            _sendTimer -= _sendDt;

            _lastSeqSent++;

            Vector3 pos = _rb.position;
            Vector3 dir = _core.DebugLastMoveDir;
            bool running = _core.IsRunning;
            bool isCTM = _ctm && _ctm.HasPath;
            Vector3[] pathCorners = isCTM ? _ctm.GetPathCorners() : null;

            double localClientTime = Time.timeAsDouble;
            double timestampToSend =
                _clockSync != null
                    ? _clockSync.ClientToServerTime(localClientTime)
                    : _netTime.Now();

            _telemetry?.Observe(
                $"client.{OwnerClientId}.sent_timestamp_diff_ms",
                (timestampToSend - localClientTime) * 1000.0);

            CmdSendInput(dir, pos, running, _lastSeqSent, isCTM, pathCorners, timestampToSend);

            var input = new InputState(dir, running, _lastSeqSent, _sendDt, localClientTime);
            _inputBuf.Enqueue(input);
            if (_inputBuf.Count > 128)
                _inputBuf.Dequeue();
        }

        // ---------- remoti render ----------
        void Remote_Update()
        {
            if (_buffer.Count == 0)
                return;

            _back = Mathf.Lerp((float)_back, (float)_backTarget, Time.deltaTime * 1.0f);
            double now = _netTime.Now();
            double renderT = now - _back;

            if (TryGetBracket(renderT, out MovementSnapshot A, out MovementSnapshot B))
                Remote_RenderInterpolated(A, B, renderT);
            else
                Remote_RenderExtrapolated(now);

            CleanupOld(renderT - 0.35);
        }

        void Remote_RenderInterpolated(MovementSnapshot A, MovementSnapshot B, double renderT)
        {
            double span = B.serverTime - A.serverTime;
            float t = (span > 1e-6) ? (float)((renderT - A.serverTime) / span) : 1f;
            t = Mathf.Clamp01(t);

            bool lowVel = (A.vel.sqrMagnitude < 0.0001f && B.vel.sqrMagnitude < 0.0001f);
            bool tinyMove = (A.pos - B.pos).sqrMagnitude < 0.000004f;
            bool useHermite = (span > 0.03 && span < 0.5 && _emaJitter < 0.06);

            Vector3 target = (!useHermite || lowVel || tinyMove)
                ? Vector3.Lerp(A.pos, B.pos, t)
                : Hermite(A.pos, A.vel * (float)span, B.pos, B.vel * (float)span, t);

            DriveRemote(target, A.animState);
        }

        void Remote_RenderExtrapolated(double now)
        {
            MovementSnapshot last = _buffer[_buffer.Count - 1];
            double dt = Math.Min(now - last.serverTime, 0.15);
            Vector3 target = last.pos + last.vel * (float)dt;
            DriveRemote(target, last.animState);
        }

        void DriveRemote(Vector3 target, byte animState)
        {
            if (ignoreNetworkY)
                target.y = transform.position.y;

            Transform vr = _core ? _core.visualRoot : null;
            Vector3 current = (remoteMoveVisualOnly && vr)
                ? vr.position
                : _rb.position;

            float k = 1f - Mathf.Exp(-remoteVisualLerpSpeed * Time.deltaTime);
            Vector3 smoothed = Vector3.Lerp(current, target, k);

            if (remoteMoveVisualOnly && vr)
                vr.position = smoothed;
            else
                _rb.MovePosition(smoothed);

            Vector3 moveVec = smoothed - _remoteLastRenderPos;
            float dt = Mathf.Max(Time.deltaTime, 1e-6f);
            float rawSpeed = moveVec.magnitude / dt;
            _remoteDisplaySpeed = Mathf.Lerp(
                _remoteDisplaySpeed,
                rawSpeed,
                Time.deltaTime * remoteAnimSmooth);

            if (vr != null)
            {
                Vector3 dir = moveVec;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.0004f &&
                    _remoteDisplaySpeed > 0.5f)
                {
                    Quaternion face = Quaternion.LookRotation(dir.normalized);
                    vr.rotation = Quaternion.Slerp(
                        vr.rotation, face, Time.deltaTime * 6f);
                    vr.rotation = Quaternion.Euler(0f, vr.eulerAngles.y, 0f);
                }
            }

            _remoteLastRenderPos = smoothed;

            _core.SafeAnimSpeedRaw(_remoteDisplaySpeed);
            bool shouldRun = (animState == 2) &&
                             (_remoteDisplaySpeed > remoteRunSpeedThreshold * 0.75f);
            _core.SafeAnimRun(shouldRun);
        }

        [TargetRpc]
        void TargetOwnerCorrection(NetworkConnection conn, uint serverSeq, Vector3 serverPos)
        {
            if (_shuttingDown || s_AppQuitting)
                return;

            double now = _netTime.Now();
            if (now - _lastReconcileSentTime < RECONCILE_COOLDOWN_SEC)
            {
                _telemetry?.Increment("reconcile.cooldown_skipped");
                return;
            }

            while (_inputBuf.Count > 0 && _inputBuf.Peek().seq <= serverSeq)
                _inputBuf.Dequeue();

            Vector3 corrected = serverPos;
            foreach (var inp in _inputBuf)
            {
                float spd = inp.running
                    ? _core.speed * _core.runMultiplier
                    : _core.speed;

                if (inp.dir.sqrMagnitude > 1e-6f)
                    corrected += inp.dir.normalized * spd * inp.dt;
            }

            float errXZ = Vector2.Distance(
                new Vector2(_rb.position.x, _rb.position.z),
                new Vector2(corrected.x, corrected.z));

            if (errXZ < deadZone)
                return;

            if (ignoreNetworkY)
                corrected.y = transform.position.y;

            var rTags = new Dictionary<string, string>
            {
                { "clientId", OwnerClientId.ToString() },
                { "reason", "server_correction" }
            };

            var rMetrics = new Dictionary<string, double>
            {
                { "errXZ_cm", errXZ * 100.0 },
                { "rtt_ms", _lastRttMs }
            };

            int sc = 0;
            var sample = new List<string>();
            foreach (var inp in _inputBuf)
            {
                if (sc++ >= 6)
                    break;
                sample.Add(
                    $"{inp.seq}:{inp.dir.x:0.00},{inp.dir.z:0.00}");
            }

            if (sample.Count > 0)
                rTags["input_sample"] = string.Join("|", sample);

            _telemetry?.Event("reconcile.requested", rTags, rMetrics);

            _reconcileTarget = corrected;
            _reconcileActive = true;
            _lastReconcileSentTime = now;

            _telemetry?.Increment(
                $"client.{OwnerClientId}.reconcile.requested_smooth");

            if (IsOwner)
                StartElasticCorrection(corrected);
        }

        // ------- elastic helper -------
        void StartElasticCorrection(Vector3 target)
        {
            float dist = Vector3.Distance(_rb.position, target);
            if (dist < correctionMinVisible)
                return;

            _isApplyingElastic = true;
            _elasticStartPos = _rb.position;
            _elasticTargetPos = target;
            _elasticElapsed = 0f;
            _elasticDuration = Mathf.Max(0.05f, correctionDurationSeconds);
            _elasticCurrentMultiplier = correctionInitialMultiplier;

            _telemetry?.Event("elastic.start",
                new Dictionary<string, string>
                {
                    { "clientId", OwnerClientId.ToString() },
                    {
                        "startPos",
                        $"{_elasticStartPos.x:0.00},{_elasticStartPos.y:0.00},{_elasticStartPos.z:0.00}"
                    },
                    {
                        "targetPos",
                        $"{_elasticTargetPos.x:0.00},{_elasticTargetPos.y:0.00},{_elasticTargetPos.z:0.00}"
                    }
                },
                new Dictionary<string, double>
                {
                    { "dist_cm", dist * 100.0 },
                    { "duration_s", _elasticDuration }
                });

            _telemetry?.Increment(
                $"client.{OwnerClientId}.elastic_started");
        }
    }
}
