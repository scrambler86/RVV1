using System;
using System.Collections.Generic;
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

            EnsureServices();

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

        /// <summary>
        /// Executes the owner-side pipeline (input send, elastic correction, reconciliation).
        /// Split out from <see cref="FixedUpdate"/> to keep the frame loop readable.
        /// </summary>
        void TickOwnerClient()
        {
            Owner_Send();
            Owner_ApplyElasticCorrection();
            Owner_ProcessHardSnap();
            Owner_ProcessReconciliation();
        }

        /// <summary>
        /// Updates interpolation for remote-controlled instances.
        /// </summary>
        void TickRemoteClient()
        {
            Remote_Update();
        }

        /// <summary>
        /// Handles retry scheduling for reliable full snapshots on the server.
        /// </summary>
        void TickServerResendLoop()
        {
            if (_retryManager.IsEmpty)
                return;

            double now = _netTime.Now();
            _serverRetryScratch.Clear();

            foreach (var conn in _retryManager.EnumerateConnections())
            {
                if (conn == null || !conn.IsActive)
                    continue;

                if (!_retryManager.TryGetRecord(conn, out var record))
                    continue;

                if (record.RetryCount >= FULL_RETRY_MAX)
                    continue;

                if (now - record.LastSentAt <= FULL_RETRY_SECONDS)
                    continue;

                _serverRetryScratch.Add(conn);
            }

            foreach (var conn in _serverRetryScratch)
            {
                if (!_retryManager.TryGetRecord(conn, out var record))
                    continue;

                if (record.HasShards)
                {
                    byte[] lastFull = record.Payload;
                    ulong fullHash = (lastFull != null) ? EnvelopeUtil.ComputeHash64(lastFull) : 0;
                    int fullLen = (lastFull != null) ? lastFull.Length : 0;

                    uint messageId = _nextOutgoingMessageId++;
                    if (verboseNetLog)
                    {
                        Debug.Log(
                            $"[Server.Debug] Retry sending shards messageId={messageId} " +
                            $"totalShards={record.Shards.Count} fullLen={fullLen} fullHash=0x{fullHash:X16}");
                    }

                    foreach (var shard in record.Shards)
                    {
                        if (verboseNetLog)
                            Debug.Log($"[Server.Debug] Retry shard len={shard.Length} first8={_packingService.PreviewBytes(shard, 8)}");

                        byte[] envelopeBytes = CreateEnvelopeBytesForShard(shard, messageId, fullLen, fullHash);
                        TargetPackedShardTo(conn, envelopeBytes);
                    }
                }
                else if (record.Payload != null)
                {
                    byte[] payloadEnv = CreateEnvelopeBytes(record.Payload);
                    TargetPackedSnapshotTo(conn, payloadEnv, ComputeStateHashFromPayload(record.Payload));
                }

                _retryManager.MarkSent(conn, _netTime.Now());
                _telemetry?.Increment("pack.full_retry");
            }

            _serverRetryScratch.Clear();
        }

        /// <summary>
        /// Applies owner-side elastic correction towards the authoritative server target.
        /// </summary>
        void Owner_ApplyElasticCorrection()
        {
            if (!_isApplyingElastic)
                return;

            _elasticElapsed += Time.fixedDeltaTime;
            float t = Mathf.Clamp01(_elasticElapsed / _elasticDuration);
            float ease = 1f - Mathf.Pow(1f - t, 3f);

            Vector3 current = _rb.position;
            Vector3 target = Vector3.Lerp(_elasticStartPos, _elasticTargetPos, ease);
            float maxAllowed = maxCorrectionSpeed * Time.fixedDeltaTime * _elasticCurrentMultiplier;
            Vector3 next = Vector3.MoveTowards(current, target, maxAllowed);

            _rb.MovePosition(next);
            if (_core && _core.visualRoot != null)
                _core.visualRoot.position = next;

            if (_agent)
                _agent.nextPosition = next;

            float applied = Vector3.Distance(current, next);
            _telemetry?.Observe($"client.{OwnerClientId}.elastic_applied_cm", applied * 100.0);
            _telemetry?.Observe($"client.{OwnerClientId}.elastic_progress", ease * 100.0);

            _elasticCurrentMultiplier *= correctionDecay;

            if (t >= 1f || Vector3.Distance(next, _elasticTargetPos) < correctionMinVisible)
            {
                _isApplyingElastic = false;
                _elasticElapsed = 0f;
                _elasticCurrentMultiplier = 1f;
                _telemetry?.Increment($"client.{OwnerClientId}.elastic_completed");
            }
        }

        /// <summary>
        /// Performs pending hard snap correction (owner).
        /// </summary>
        void Owner_ProcessHardSnap()
        {
            if (!_doHardSnapNextFixed || _isApplyingElastic)
                return;

            Vector3 current = _rb.position;
            Vector3 target = _pendingHardSnap;
            float distance = Vector3.Distance(current, target);

            if (distance > hardSnapDist * 1.1f &&
                (Time.realtimeSinceStartup - (float)_lastHardSnapTime) > 0.05f)
            {
                Vector3 next = Vector3.MoveTowards(
                    current, target,
                    maxCorrectionSpeed * Time.fixedDeltaTime * 1.8f);

                _rb.MovePosition(next);
                if (_core && _core.visualRoot != null)
                    _core.visualRoot.position = next;
                if (_agent)
                    _agent.nextPosition = next;
                if (Vector3.Distance(next, target) < 0.05f)
                    _doHardSnapNextFixed = false;
            }
            else
            {
                _rb.MovePosition(target);
                if (_core && _core.visualRoot != null)
                    _core.visualRoot.position = target;
                if (_agent)
                    _agent.nextPosition = target;
                _doHardSnapNextFixed = false;
            }
        }

        /// <summary>
        /// Reconciles local rigidbody state with the authoritative server snapshot.
        /// </summary>
        void Owner_ProcessReconciliation()
        {
            if (!_reconcileActive || _isApplyingElastic)
                return;

            float maxAllowed = maxCorrectionSpeed * Time.fixedDeltaTime;
            Vector3 current = _rb.position;
            Vector3 toTarget = _reconcileTarget - current;
            float distance = toTarget.magnitude;

            if (distance <= 0.0001f)
            {
                _reconcileActive = false;
                return;
            }

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
                    Vector3 step = Vector3.ClampMagnitude(toTarget, maxAllowed);
                    Vector3 next = current + step;
                    next = Vector3.Lerp(current, next, 1f - reconciliationSmoothing);
                    _rb.MovePosition(next);
                    if (_core && _core.visualRoot != null)
                        _core.visualRoot.position = next;
                    if (_agent)
                        _agent.nextPosition = next;
                    _telemetry?.Increment("reconcile.rate_limited_snaps");
                }
                return;
            }

            float alpha = 1f - Mathf.Exp(-reconcileRate * Time.fixedDeltaTime * 0.66f);
            Vector3 desired = Vector3.Lerp(current, _reconcileTarget, alpha);
            Vector3 capped = Vector3.MoveTowards(current, desired, maxAllowed);
            Vector3 smoothed = Vector3.Lerp(current, capped, 1f - reconciliationSmoothing);

            _rb.MovePosition(smoothed);
            if (_core && _core.visualRoot != null)
                _core.visualRoot.position = smoothed;
            if (_agent)
                _agent.nextPosition = smoothed;
<<<<<<< HEAD
=======

            if ((smoothed - _reconcileTarget).sqrMagnitude < 0.0004f)
                _reconcileActive = false;

            _telemetry?.Increment("reconcile.smooth_steps");
        }

        /// <summary>
        /// Maintains the shard reassembly buffer lifetime.
        /// </summary>
        void ProcessShardBufferTimeouts()
        {
            CheckShardBufferTimeouts();
        }
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0

            if ((smoothed - _reconcileTarget).sqrMagnitude < 0.0004f)
                _reconcileActive = false;

            _telemetry?.Increment("reconcile.smooth_steps");
        }

        /// <summary>
        /// Maintains the shard reassembly buffer lifetime.
        /// </summary>
        void ProcessShardBufferTimeouts()
        {
            CheckShardBufferTimeouts();
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
    }
    }
    }
}
