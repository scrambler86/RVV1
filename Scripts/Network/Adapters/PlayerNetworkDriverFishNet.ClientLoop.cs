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
            _retryManager.CollectDue(now, FULL_RETRY_SECONDS, FULL_RETRY_MAX, _serverRetryScratch);

            foreach (var conn in _serverRetryScratch)
            {
                if (!_retryManager.TryGetRecord(conn, out var record))
                    continue;

                if (record.RetryCount >= FULL_RETRY_MAX)
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
            EnsureOwnerRuntime();

            if (_ownerRuntime == null)
                return;

            var ctx = new PlayerDriverOwnerRuntime.ElasticContext(
                Time.fixedDeltaTime,
                maxCorrectionSpeed,
                correctionDecay,
                correctionMinVisible,
                _telemetry,
                OwnerClientId,
                _rb,
                _core,
                _agent);

            _ownerRuntime.TickElastic(ctx);
        }

        /// <summary>
        /// Processes pending hard snap requests accumulated during reconciliation.
        /// </summary>
        void Owner_ProcessHardSnap()
        {
            EnsureOwnerRuntime();

            if (_ownerRuntime == null || _ownerRuntime.IsElasticActive)
                return;

            if (_ownerRuntime.ConsumeHardSnap(out var target))
            {
                _rb.MovePosition(target);
                if (_core && _core.visualRoot != null)
                    _core.visualRoot.position = target;
                if (_agent)
                    _agent.nextPosition = target;
            }
        }

        /// <summary>
        /// Reconciles local rigidbody state with the authoritative server snapshot.
        /// </summary>
        void Owner_ProcessReconciliation()
        {
            EnsureOwnerRuntime();

            if (_ownerRuntime == null || _ownerRuntime.IsElasticActive || !_ownerRuntime.ReconcileActive)
                return;

            var ctx = new PlayerDriverOwnerRuntime.ReconcileContext(
                Time.fixedDeltaTime,
                maxCorrectionSpeed,
                hardSnapDist,
                hardSnapRateLimitSeconds,
                reconcileRate,
                reconciliationSmoothing,
                _netTime.Now(),
                _telemetry,
                OwnerClientId,
                _rb,
                _core,
                _agent);

            _ownerRuntime.TickReconciliation(ctx);
        }

        /// <summary>
        /// Maintains the shard reassembly buffer lifetime.
        /// </summary>
        void ProcessShardBufferTimeouts()
        {
            CheckShardBufferTimeouts();
        }

    // ---------- owner side ----------
    void Owner_Send()
    {
        if (_shuttingDown || s_AppQuitting)
            return;

        EnsureOwnerRuntime();

        var ctx = new PlayerDriverOwnerRuntime.SendContext(
            Time.fixedDeltaTime,
            sendRateHz,
            _core,
            _rb,
            _ctm,
            _clockSync,
            _netTime,
            _telemetry,
            OwnerClientId,
            NextInputSequence,
            CmdSendInput,
            () => Time.timeAsDouble);

        _ownerRuntime.TickSend(ctx, _shuttingDown);
    }

    // ---------- remotes render ----------
    void Remote_Update()
    {
        if (_remoteState.Buffer.Count == 0)
            return;

        _remoteState.Back = Mathf.Lerp((float)_remoteState.Back, (float)_remoteState.BackTarget, Time.deltaTime * 1.0f);
        double now = _netTime.Now();
        double renderT = now - _remoteState.Back;

        if (TryGetBracket(renderT, out MovementSnapshot A, out MovementSnapshot B))
            Remote_RenderInterpolated(A, B, renderT);
        else
            Remote_RenderExtrapolated(now);

        CleanupOld(renderT - 0.35);
    }

    /// <summary>
    /// Blends between two authoritative snapshots for smooth remote presentation.
    /// </summary>
    void Remote_RenderInterpolated(MovementSnapshot A, MovementSnapshot B, double renderT)
    {
        double span = B.serverTime - A.serverTime;
        float t = (span > 1e-6) ? (float)((renderT - A.serverTime) / span) : 1f;
        t = Mathf.Clamp01(t);

        bool lowVel = (A.vel.sqrMagnitude < 0.0001f && B.vel.sqrMagnitude < 0.0001f);
        bool tinyMove = (A.pos - B.pos).sqrMagnitude < 0.000004f;

        bool useHermite = (span > 0.02 && span < 0.5 && _emaJitter < 0.12);

        Vector3 target = (!useHermite || lowVel || tinyMove)
            ? Vector3.Lerp(A.pos, B.pos, t)
            : Hermite(A.pos, A.vel * (float)span, B.pos, B.vel * (float)span, t);

        bool hasVerticalIntent = Mathf.Abs(A.vel.y) > 0.0001f || Mathf.Abs(B.vel.y) > 0.0001f;

        DriveRemote(target, A.animState, hasVerticalIntent);
    }

    /// <summary>
    /// Falls back to extrapolation when the buffer cannot provide a future bracket.
    /// </summary>
    void Remote_RenderExtrapolated(double now)
    {
        var buffer = _remoteState.Buffer;
        MovementSnapshot last = buffer[buffer.Count - 1];
        double dt = Math.Min(now - last.serverTime, 0.15);
        Vector3 target = last.pos + last.vel * (float)dt;
        bool hasVerticalIntent = Mathf.Abs(last.vel.y) > 0.0001f;
        DriveRemote(target, last.animState, hasVerticalIntent);
    }

    void DriveRemote(Vector3 target, byte animState, bool hasVerticalIntent)
    {
        Func<Vector3, float> sampler = (_core != null) ? new Func<Vector3, float>(_core.SampleGroundY) : null;
        target = _elevationPolicy.ResolveClient(
            target,
            _rb.position,
            sampler,
            elevationPolicy,
            elevationPolicy == ElevationPolicyMode.PreserveNetwork || hasVerticalIntent);

        Transform vr = _core ? _core.visualRoot : null;
        Vector3 current = (remoteMoveVisualOnly && vr) ? vr.position : _rb.position;
        float k = 1f - Mathf.Exp(-remoteVisualLerpSpeed * Time.deltaTime);
        Vector3 smoothed = Vector3.Lerp(current, target, k);

        if (remoteMoveVisualOnly && vr)
            vr.position = smoothed;
        else
            _rb.position = smoothed;

        Vector3 moveVec = smoothed - _remoteState.LastRenderPos;
        float dt = Mathf.Max(Time.deltaTime, 1e-6f);
        float rawSpeed = moveVec.magnitude / dt;
        _remoteState.DisplaySpeed = Mathf.Lerp(
            _remoteState.DisplaySpeed,
            rawSpeed,
            Time.deltaTime * remoteAnimSmooth);

        if (vr != null)
        {
            Vector3 dir = moveVec;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0004f && _remoteState.DisplaySpeed > 0.5f)
            {
                Quaternion face = Quaternion.LookRotation(dir.normalized);
                vr.rotation = Quaternion.Slerp(vr.rotation, face, Time.deltaTime * 6f);
                vr.rotation = Quaternion.Euler(0f, vr.eulerAngles.y, 0f);
            }
        }

        _remoteState.LastRenderPos = smoothed;

        _core.SafeAnimSpeedRaw(_remoteState.DisplaySpeed);
        bool shouldRun = (animState == 2) &&
                         (_remoteState.DisplaySpeed > remoteRunSpeedThreshold * 0.75f);
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

        EnsureOwnerRuntime();

        var inputBuffer = _ownerRuntime.InputBuffer;
        _ownerRuntime.ClearInputBufferUpTo(serverSeq);

        Vector3 corrected = _ownerRuntime.IntegratePendingInputs(serverPos, _core);

        float errXZ = Vector2.Distance(
            new Vector2(_rb.position.x, _rb.position.z),
            new Vector2(corrected.x, corrected.z));

        if (errXZ < deadZone)
            return;

        Func<Vector3, float> sampler = (_core != null) ? new Func<Vector3, float>(_core.SampleGroundY) : null;
        bool hasVerticalIntent = elevationPolicy == ElevationPolicyMode.PreserveNetwork ||
                                 (_core != null && Mathf.Abs(_core.DebugLastMoveDir.y) > 0.0001f);
        corrected = _elevationPolicy.ResolveClient(
            corrected,
            _rb.position,
            sampler,
            elevationPolicy,
            hasVerticalIntent);

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
        foreach (var inp in inputBuffer)
        {
            if (sc++ >= 6)
                break;
            sample.Add($"{inp.seq}:{inp.dir.x:0.00},{inp.dir.z:0.00}");
        }

        if (sample.Count > 0)
            rTags["input_sample"] = string.Join("|", sample);

        _telemetry?.Event("reconcile.requested", rTags, rMetrics);

        _ownerRuntime.SetReconcileTarget(corrected);
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

        EnsureOwnerRuntime();

        float duration = Mathf.Max(0.05f, correctionDurationSeconds);
        _ownerRuntime.StartElastic(_rb.position, target, duration, correctionInitialMultiplier);

        _telemetry?.Event("elastic.start",
            new Dictionary<string, string>
            {
                { "clientId", OwnerClientId.ToString() },
                { "startPos", $"{_rb.position.x:0.00},{_rb.position.y:0.00},{_rb.position.z:0.00}" },
                { "targetPos", $"{target.x:0.00},{target.y:0.00},{target.z:0.00}" }
            },
            new Dictionary<string, double>
            {
                { "dist_cm", dist * 100.0 },
                { "duration_s", duration }
            });

        _telemetry?.Increment($"client.{OwnerClientId}.elastic_started");
    }
    }
}
