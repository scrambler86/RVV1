using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Game.Network;

namespace Game.Networking.Adapters
{
    internal sealed class PlayerDriverOwnerRuntime
    {
        internal readonly struct SendContext
        {
            public SendContext(float fixedDeltaTime,
                               int sendRateHz,
                               PlayerControllerCore core,
                               Rigidbody rigidbody,
                               ClickToMoveAgent ctm,
                               ClockSyncManager clockSync,
                               INetTime netTime,
                               IDriverTelemetry telemetry,
                               int ownerClientId,
                               Func<uint> nextSequence,
                               Action<Vector3, Vector3, bool, uint, bool, Vector3[], double> sendInput,
                               Func<double> getLocalTime)
            {
                FixedDeltaTime = fixedDeltaTime;
                SendRateHz = sendRateHz;
                Core = core;
                Rigidbody = rigidbody;
                ClickToMove = ctm;
                ClockSync = clockSync;
                NetTime = netTime;
                Telemetry = telemetry;
                OwnerClientId = ownerClientId;
                NextSequence = nextSequence;
                SendInput = sendInput;
                GetLocalTime = getLocalTime;
            }

            public float FixedDeltaTime { get; }
            public int SendRateHz { get; }
            public PlayerControllerCore Core { get; }
            public Rigidbody Rigidbody { get; }
            public ClickToMoveAgent ClickToMove { get; }
            public ClockSyncManager ClockSync { get; }
            public INetTime NetTime { get; }
            public IDriverTelemetry Telemetry { get; }
            public int OwnerClientId { get; }
            public Func<uint> NextSequence { get; }
            public Action<Vector3, Vector3, bool, uint, bool, Vector3[], double> SendInput { get; }
            public Func<double> GetLocalTime { get; }
        }

        internal readonly struct ElasticContext
        {
            public ElasticContext(float fixedDeltaTime,
                                  float maxCorrectionSpeed,
                                  float correctionDecay,
                                  float correctionMinVisible,
                                  IDriverTelemetry telemetry,
                                  int ownerClientId,
                                  Rigidbody rigidbody,
                                  PlayerControllerCore core,
                                  NavMeshAgent agent)
            {
                FixedDeltaTime = fixedDeltaTime;
                MaxCorrectionSpeed = maxCorrectionSpeed;
                CorrectionDecay = correctionDecay;
                CorrectionMinVisible = correctionMinVisible;
                Telemetry = telemetry;
                OwnerClientId = ownerClientId;
                Rigidbody = rigidbody;
                Core = core;
                Agent = agent;
            }

            public float FixedDeltaTime { get; }
            public float MaxCorrectionSpeed { get; }
            public float CorrectionDecay { get; }
            public float CorrectionMinVisible { get; }
            public IDriverTelemetry Telemetry { get; }
            public int OwnerClientId { get; }
            public Rigidbody Rigidbody { get; }
            public PlayerControllerCore Core { get; }
            public NavMeshAgent Agent { get; }
        }

        internal readonly struct ReconcileContext
        {
            public ReconcileContext(float fixedDeltaTime,
                                    float maxCorrectionSpeed,
                                    float hardSnapDist,
                                    float hardSnapRateLimitSeconds,
                                    float reconcileRate,
                                    float reconciliationSmoothing,
                                    double now,
                                    IDriverTelemetry telemetry,
                                    int ownerClientId,
                                    Rigidbody rigidbody,
                                    PlayerControllerCore core,
                                    NavMeshAgent agent)
            {
                FixedDeltaTime = fixedDeltaTime;
                MaxCorrectionSpeed = maxCorrectionSpeed;
                HardSnapDist = hardSnapDist;
                HardSnapRateLimitSeconds = hardSnapRateLimitSeconds;
                ReconcileRate = reconcileRate;
                ReconciliationSmoothing = reconciliationSmoothing;
                Now = now;
                Telemetry = telemetry;
                OwnerClientId = ownerClientId;
                Rigidbody = rigidbody;
                Core = core;
                Agent = agent;
            }

            public float FixedDeltaTime { get; }
            public float MaxCorrectionSpeed { get; }
            public float HardSnapDist { get; }
            public float HardSnapRateLimitSeconds { get; }
            public float ReconcileRate { get; }
            public float ReconciliationSmoothing { get; }
            public double Now { get; }
            public IDriverTelemetry Telemetry { get; }
            public int OwnerClientId { get; }
            public Rigidbody Rigidbody { get; }
            public PlayerControllerCore Core { get; }
            public NavMeshAgent Agent { get; }
        }

        readonly Queue<InputState> _inputBuffer = new(128);

        float _sendDt;
        float _sendTimer;

        bool _reconcileActive;
        bool _doHardSnapNext;
        Vector3 _reconcileTarget;
        Vector3 _pendingHardSnap;
        double _lastHardSnapTime = -9999.0;

        bool _isApplyingElastic;
        Vector3 _elasticStart;
        Vector3 _elasticTarget;
        float _elasticElapsed;
        float _elasticDuration;
        float _elasticMultiplier = 1f;

        public Queue<InputState> InputBuffer => _inputBuffer;
        public bool IsElasticActive => _isApplyingElastic;
        public bool HasPendingHardSnap => _doHardSnapNext;
        public Vector3 PendingHardSnap => _pendingHardSnap;
        public bool ReconcileActive => _reconcileActive;
        public Vector3 ReconcileTarget => _reconcileTarget;

        public void Reset(bool clearInputs = true)
        {
            _sendDt = 0f;
            _sendTimer = 0f;
            if (clearInputs)
                _inputBuffer.Clear();

            _reconcileActive = false;
            _doHardSnapNext = false;
            _reconcileTarget = Vector3.zero;
            _pendingHardSnap = Vector3.zero;
            _lastHardSnapTime = -9999.0;

            _isApplyingElastic = false;
            _elasticElapsed = 0f;
            _elasticDuration = 0f;
            _elasticMultiplier = 1f;
            _elasticStart = Vector3.zero;
            _elasticTarget = Vector3.zero;
        }

        public void TickSend(in SendContext ctx, bool isShutdown)
        {
            if (isShutdown)
                return;

            _sendDt = 1f / Mathf.Max(1, ctx.SendRateHz);
            _sendTimer += ctx.FixedDeltaTime;
            if (_sendTimer < _sendDt)
                return;

            _sendTimer -= _sendDt;

            uint seq = ctx.NextSequence();

            Vector3 pos = ctx.Rigidbody.position;
            Vector3 dir = ctx.Core != null ? ctx.Core.DebugLastMoveDir : Vector3.zero;
            bool running = ctx.Core != null && ctx.Core.IsRunning;
            bool isCtm = ctx.ClickToMove != null && ctx.ClickToMove.HasPath;
            Vector3[] pathCorners = isCtm ? ctx.ClickToMove.GetPathCorners() : null;

            double localClientTime = ctx.GetLocalTime();
            double timestampToSend =
                ctx.ClockSync != null
                    ? ctx.ClockSync.ClientToServerTime(localClientTime)
                    : ctx.NetTime.Now();

            ctx.Telemetry?.Observe(
                $"client.{ctx.OwnerClientId}.sent_timestamp_diff_ms",
                (timestampToSend - localClientTime) * 1000.0);

            ctx.SendInput(dir, pos, running, seq, isCtm, pathCorners, timestampToSend);

            var input = new InputState(dir, running, seq, _sendDt, localClientTime);
            _inputBuffer.Enqueue(input);
            if (_inputBuffer.Count > 128)
                _inputBuffer.Dequeue();
        }

        public void TickElastic(in ElasticContext ctx)
        {
            if (!_isApplyingElastic)
                return;

            _elasticElapsed += ctx.FixedDeltaTime;
            float t = Mathf.Clamp01(_elasticElapsed / _elasticDuration);
            float ease = 1f - Mathf.Pow(1f - t, 3f);

            Vector3 current = ctx.Rigidbody.position;
            Vector3 target = Vector3.Lerp(_elasticStart, _elasticTarget, ease);
            float maxAllowed = ctx.MaxCorrectionSpeed * ctx.FixedDeltaTime * _elasticMultiplier;
            Vector3 next = Vector3.MoveTowards(current, target, maxAllowed);

            ctx.Rigidbody.MovePosition(next);
            if (ctx.Core && ctx.Core.visualRoot != null)
                ctx.Core.visualRoot.position = next;

            if (ctx.Agent)
                ctx.Agent.nextPosition = next;

            float applied = Vector3.Distance(current, next);
            ctx.Telemetry?.Observe($"client.{ctx.OwnerClientId}.elastic_applied_cm", applied * 100.0);
            ctx.Telemetry?.Observe($"client.{ctx.OwnerClientId}.elastic_progress", ease * 100.0);

            _elasticMultiplier *= ctx.CorrectionDecay;

            if (t >= 1f || Vector3.Distance(next, _elasticTarget) < ctx.CorrectionMinVisible)
            {
                StopElastic(ctx.Telemetry, ctx.OwnerClientId);
            }
        }

        public void TickHardSnap(Rigidbody rb, PlayerControllerCore core, NavMeshAgent agent)
        {
            if (!_doHardSnapNext)
                return;

            Vector3 target = _pendingHardSnap;
            rb.MovePosition(target);
            if (core && core.visualRoot != null)
                core.visualRoot.position = target;
            if (agent)
                agent.nextPosition = target;
            _doHardSnapNext = false;
        }

        public void TickReconciliation(in ReconcileContext ctx)
        {
            if (!_reconcileActive || _isApplyingElastic)
                return;

            float maxAllowed = ctx.MaxCorrectionSpeed * ctx.FixedDeltaTime;
            Vector3 current = ctx.Rigidbody.position;
            Vector3 toTarget = _reconcileTarget - current;
            float distance = toTarget.magnitude;

            if (distance <= 0.0001f)
            {
                _reconcileActive = false;
                return;
            }

            if (distance > ctx.HardSnapDist)
            {
                double now = ctx.Now;
                if (now - _lastHardSnapTime > ctx.HardSnapRateLimitSeconds)
                {
                    _pendingHardSnap = _reconcileTarget;
                    _doHardSnapNext = true;
                    _reconcileActive = false;
                    _lastHardSnapTime = now;
                    ctx.Telemetry?.Increment("reconcile.hard_snaps");
                    ctx.Telemetry?.Increment($"client.{ctx.OwnerClientId}.hard_snaps");
                }
                else
                {
                    Vector3 step = Vector3.ClampMagnitude(toTarget, maxAllowed);
                    Vector3 next = current + step;
                    next = Vector3.Lerp(current, next, 1f - ctx.ReconciliationSmoothing);
                    ctx.Rigidbody.MovePosition(next);
                    if (ctx.Core && ctx.Core.visualRoot != null)
                        ctx.Core.visualRoot.position = next;
                    if (ctx.Agent)
                        ctx.Agent.nextPosition = next;
                    ctx.Telemetry?.Increment("reconcile.rate_limited_snaps");
                }
                return;
            }

            float alpha = 1f - Mathf.Exp(-ctx.ReconcileRate * ctx.FixedDeltaTime * 0.66f);
            Vector3 desired = Vector3.Lerp(current, _reconcileTarget, alpha);
            Vector3 capped = Vector3.MoveTowards(current, desired, maxAllowed);
            Vector3 smoothed = Vector3.Lerp(current, capped, 1f - ctx.ReconciliationSmoothing);

            ctx.Rigidbody.MovePosition(smoothed);
            if (ctx.Core && ctx.Core.visualRoot != null)
                ctx.Core.visualRoot.position = smoothed;
            if (ctx.Agent)
                ctx.Agent.nextPosition = smoothed;

            if ((smoothed - _reconcileTarget).sqrMagnitude < 0.0004f)
                _reconcileActive = false;

            ctx.Telemetry?.Increment("reconcile.smooth_steps");
        }

        public void StartElastic(Vector3 start, Vector3 target, float duration, float multiplier)
        {
            _elasticStart = start;
            _elasticTarget = target;
            _elasticDuration = Mathf.Max(0.01f, duration);
            _elasticElapsed = 0f;
            _elasticMultiplier = Mathf.Max(0.01f, multiplier);
            _isApplyingElastic = true;
        }

        public void StopElastic(IDriverTelemetry telemetry, int ownerClientId)
        {
            if (!_isApplyingElastic)
                return;

            _isApplyingElastic = false;
            _elasticElapsed = 0f;
            _elasticMultiplier = 1f;
            telemetry?.Increment($"client.{ownerClientId}.elastic_completed");
        }

        public void SetReconcileTarget(Vector3 target)
        {
            _reconcileTarget = target;
            _reconcileActive = true;
        }

        public void CancelReconciliation()
        {
            _reconcileActive = false;
        }

        public void ForceHardSnap(Vector3 target)
        {
            _pendingHardSnap = target;
            _doHardSnapNext = true;
            _reconcileActive = false;
        }

        public bool ConsumeHardSnap(out Vector3 target)
        {
            if (!_doHardSnapNext)
            {
                target = default;
                return false;
            }

            target = _pendingHardSnap;
            _doHardSnapNext = false;
            return true;
        }

        public bool TryAdvanceElastic(out Vector3 target)
        {
            if (!_isApplyingElastic)
            {
                target = default;
                return false;
            }

            target = _elasticTarget;
            return true;
        }

        public void ClearInputBufferUpTo(uint serverSeq)
        {
            while (_inputBuffer.Count > 0 && _inputBuffer.Peek().seq <= serverSeq)
                _inputBuffer.Dequeue();
        }

        public Vector3 IntegratePendingInputs(Vector3 startPos, PlayerControllerCore core)
        {
            Vector3 corrected = startPos;
            foreach (var inp in _inputBuffer)
            {
                if (core == null)
                    break;

                float speed = inp.running
                    ? core.speed * core.runMultiplier
                    : core.speed;

                if (inp.dir.sqrMagnitude > 1e-6f)
                    corrected += inp.dir.normalized * speed * inp.dt;
            }

            return corrected;
        }
    }
}