using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using Game.Network; // for ClockSyncManager (client-side hook)
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(PlayerControllerCore))]
[RequireComponent(typeof(Rigidbody))]
public partial class PlayerNetworkDriverFishNet : NetworkBehaviour, IPlayerNetworkDriver
{
    [Header("Diagnostics")]
    [Tooltip("If false, CRC mismatch warnings are suppressed at runtime (kept in Editor/Dev builds).")]
    public bool enableCrcWarnings = false;

    // ---- Quit guard (no RPC during shutdown) ----
    private static bool s_AppQuitting = false;
    private static void OnAppQuit() => s_AppQuitting = true;

    [Header("Refs")]
    [SerializeField] private PlayerControllerCore _core;
    [SerializeField] private Rigidbody _rb;
    [SerializeField] private NavMeshAgent _agent;
    [SerializeField] private ClickToMoveAgent _ctm;

    // ---------- ClockSync helper (client) ----------
    private ClockSyncManager _clockSync;

    [Header("Owner → Server send")]
    [Range(10, 60)] public int sendRateHz = 30;

    [Header("Remotes interpolation")]
    [SerializeField] private double minBack = 0.14;
    [SerializeField] private double maxBack = 0.32;
    [Range(0.05f, 0.5f)] public float emaDelayA = 0.18f;
    [Range(0.05f, 0.5f)] public float emaJitterA = 0.18f;

    [Header("Reconciliation (Owner)")]
    public float deadZone = 0.22f;
    public float hardSnapDist = 0.80f;
    public float reconcileRate = 12f;
    public float maxCorrectionSpeed = 6f;
    [Range(0f, 1f)] public float reconciliationSmoothing = 0.85f;
    public float hardSnapRateLimitSeconds = 1.0f;

    [Header("FEC parity (full-keyframe)")]
    [Tooltip("Number of parity shards (simple XOR). 0 = disabled")]
    public int fecParityShards = 1;
    [Tooltip("Max bytes per shard during splitting")]
    public int fecShardSize = 1024;

    [Header("Elastic correction (client)")]
    public float correctionDurationSeconds = 0.25f;
    public float correctionInitialMultiplier = 2.0f;
    public float correctionDecay = 0.55f;
    public float correctionMinVisible = 0.03f;

    [Header("Anti-cheat (Server)")]
    public float maxSpeedTolerance = 1.18f;
    public float slackK = 0.6f;
    public float slackMin = 1.00f;
    public float slackMax = 1.75f;
    public bool validateNavMesh = true;
    public float navMeshMaxSampleDist = 1.0f;
    public float maxVerticalSpeed = 2.0f;

    [Header("Remoti visual/anim")]
    public bool remoteMoveVisualOnly = true;
    public float remoteVisualLerpSpeed = 16f;
    public float remoteAnimSmooth = 8f;
    public float remoteRunSpeedThreshold = 3.0f;

    [Header("Height policy")]
    public bool ignoreNetworkY = true;

    [Header("Broadcast Scheduler (per-conn)")]
    public int nearRing = 1, midRing = 2, farRing = 3;
    public int nearHz = 30, midHz = 10, farHz = 3;

    [Header("Delta Compression")]
    public int keyframeEvery = 20;
    public int maxPosDeltaCm = 60;
    public int maxVelDeltaCms = 150;
    public int maxDtMs = 200;

    [Header("Input Rate Limit (Server)")]
    public int maxInputsPerSecond = 120;
    public int burstAllowance = 30;
    public float refillPerSecond = 120f;

    [Header("DEBUG / Fallback")]
    public bool forceBroadcastAll = false;

    [Header("DEBUG / Tests")]
    [Tooltip("When true forces sending full snapshots (no delta/FEC) to help isolate CRC/fragmentation issues")]
    public bool debugForceFullSnapshots = false;

    [Header("Debug Logging")]
    [Tooltip("Abilita log verbosi di rete. Disattivalo nelle build per evitare spam/log overhead.")]
    public bool verboseNetLog = false;

    // ==== IPlayerNetworkDriver ====
    public INetTime NetTime => _netTime;
    public int OwnerClientId => (Owner != null ? Owner.ClientId : -1);
    public uint LastSeqReceived => _lastSeqReceived;
    public void SetLastSeqReceived(uint seq) => _lastSeqReceived = seq;
    public double ClientRttMs => _lastRttMs;

    public bool HasInputAuthority(bool allowServerFallback)
    {
        var nob = NetworkObject;
        if (nob == null)
            return true;
        if (!nob.IsSpawned)
            return true;
        if (IsOwner)
            return true;
        if (allowServerFallback && nob.IsServerInitialized)
            return true;
        return false;
    }

    // ==== privati ====
    private INetTime _netTime;
    private IAntiCheatValidator _anti;
    private ChunkManager _chunk;
    private TelemetryManager _telemetry;

    private float _sendDt, _sendTimer;
    private uint _lastSeqSent, _lastSeqReceived;

    private readonly List<MovementSnapshot> _buffer = new(256);
    private double _emaDelay, _emaJitter, _back, _backTarget;

    private bool _reconcileActive, _doHardSnapNextFixed;
    private Vector3 _reconcileTarget, _pendingHardSnap;

    private Vector3 _serverLastPos;
    private double _serverLastTime;

    private Vector3 _remoteLastRenderPos;
    private float _remoteDisplaySpeed;

    private readonly Queue<InputState> _inputBuf = new(128);

    private readonly HashSet<NetworkConnection> _tmpNear = new();
    private readonly HashSet<NetworkConnection> _tmpMid = new();
    private readonly HashSet<NetworkConnection> _tmpFar = new();
    private readonly Dictionary<NetworkConnection, double> _nextSendAt = new();

    private readonly Dictionary<NetworkConnection, MovementSnapshot> _lastSentSnap = new();
    private readonly Dictionary<NetworkConnection, int> _sinceKeyframe = new();
    private readonly Dictionary<NetworkConnection, (short cellX, short cellY)> _lastSentCell = new();
    private readonly HashSet<NetworkConnection> _serverRetryScratch = new();

    private bool _haveAnchor;
    private short _anchorCellX, _anchorCellY;
    private MovementSnapshot _baseSnap;

    private float _tokens;
    private double _lastRefill;
    private double _lastRttMs;

    private bool _shuttingDown;

    private double _lastHardSnapTime = -9999.0;

    // ---------- ClockSync fields ----------
    private double _clockOffsetSeconds = 0.0;
    private double _clockOffsetEmaMs = 0.0;
    private double _clockOffsetJitterMs = 0.0;
    private const double CLOCK_ALPHA = 0.15;
    private const double CLOCK_ALPHA_JITTER = 0.12;

    // ---------- Reconcile cooldown ----------
    private double _lastReconcileSentTime = -9999.0;
    private const double RECONCILE_COOLDOWN_SEC = 0.20;

    // ---------- Reliable full-keyframe + FEC storage ----------
    private readonly Dictionary<NetworkConnection, byte[]> _lastFullPayload = new();
    private readonly Dictionary<NetworkConnection, double> _lastFullSentAt = new();
    private readonly Dictionary<NetworkConnection, int> _fullRetryCount = new();
    private readonly Dictionary<NetworkConnection, List<byte[]>> _lastFullShards = new();

    private const double FULL_RETRY_SECONDS = 0.6;
    private const int FULL_RETRY_MAX = 4;

    // ---------- Full snapshot request window (client-side) ----------
    private double _lastFullRequestTime = -9999.0;
    private double _fullRequestWindowStart = -9999.0;
    private int _fullRequestWindowCount = 0;
    private bool _fecDisableRequested = false;
    private const double FULL_REQUEST_COOLDOWN_SECONDS = 0.75;
    private const double FULL_REQUEST_WINDOW_SECONDS = 6.0;
    private const int FULL_REQUEST_DISABLE_THRESHOLD = 4;

    // ---------- Elastic correction state (client) ----------
    private bool _isApplyingElastic = false;
    private Vector3 _elasticStartPos;
    private Vector3 _elasticTargetPos;
    private float _elasticElapsed = 0f;
    private float _elasticDuration = 0f;
    private float _elasticCurrentMultiplier = 1f;

    // ---------- CRC rate-limited logging ----------
    private int _crcFailCount = 0;
    private double _crcFirstFailTime = -1.0;
    private double _crcLastLogTime = -9999.0;
    private const double CRC_LOG_WINDOW_SECONDS = 5.0;
    private const int CRC_LOG_MAX_PER_WINDOW = 5;

    // monotonic message id generator used for shards/full envelopes
    private uint _nextOutgoingMessageId = 1;

    // ---------- shard reassembly per-messageId (client) ----------
    private readonly ShardBufferRegistry _shardRegistry = new();
    private readonly List<uint> _shardTimeoutScratch = new();
    private const double SHARD_BUFFER_TIMEOUT_SECONDS = 2.0;

    // ---------- FEC suppression state ----------
    private readonly Dictionary<NetworkConnection, double> _fecSuppressedUntil = new();
    private const double FEC_DISABLE_DURATION_SECONDS = 10.0;

    // Diagnostics: map incoming envelope messageId -> server-provided payloadHash / payloadLen
    private readonly Dictionary<uint, (ulong hash, int len)> _incomingEnvelopeMeta = new();

    // Track messageIds flagged as CANARY so we don't try to decode them as movement
    private readonly HashSet<uint> _canaryMessageIds = new();

    // ------- CRC reporting helper (rate-limited, non-blocking) -------
    private void ReportCrcFailureOncePerWindow(string msg)
    {
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
        if (!enableCrcWarnings) return;
#else
        if (!enableCrcWarnings) return;
#endif

        double now = Time.realtimeSinceStartup;
        if (_crcFirstFailTime < 0.0)
            _crcFirstFailTime = now;

        _crcFailCount++;

        if (now - _crcFirstFailTime >= CRC_LOG_WINDOW_SECONDS)
        {
            int toLog = Math.Min(_crcFailCount, CRC_LOG_MAX_PER_WINDOW);
            Debug.LogWarning(
                $"{msg} — occurrences in last {CRC_LOG_WINDOW_SECONDS:0.#}s: {_crcFailCount}. Emitting {toLog} sample(s).");

            for (int i = 0; i < toLog; ++i)
                Debug.LogWarning($"{msg} [sample {i + 1}/{toLog}]");

            _telemetry?.Observe("client.crc_fail_count", _crcFailCount);

            _crcFailCount = 0;
            _crcFirstFailTime = -1.0;
            _crcLastLogTime = now;
            return;
        }

        if (_crcFailCount == CRC_LOG_MAX_PER_WINDOW &&
            (now - _crcLastLogTime) > 0.5)
        {
            Debug.LogWarning($"{msg} — repeated (count={_crcFailCount})");
            _telemetry?.Observe("client.crc_fail_burst", _crcFailCount);
            _crcLastLogTime = now;
        }
    }

    private sealed class ShardBufferRegistry
    {
        private readonly Dictionary<uint, List<ShardInfo>> _buffers = new();
        private readonly Dictionary<uint, int> _totalCounts = new();
        private readonly Dictionary<uint, double> _firstSeen = new();

        public List<ShardInfo> GetOrCreate(uint messageId, ushort total, double now)
        {
            if (!_buffers.TryGetValue(messageId, out var list))
            {
                list = new List<ShardInfo>(total);
                for (int i = 0; i < total; i++)
                    list.Add(null);

                _buffers[messageId] = list;
                _totalCounts[messageId] = total;
                _firstSeen[messageId] = now;
                return list;
            }

            if (list.Count != total)
            {
                if (list.Count < total)
                {
                    for (int i = list.Count; i < total; i++)
                        list.Add(null);
                }
                else
                {
                    list.RemoveRange(total, list.Count - total);
                }

                _totalCounts[messageId] = total;
            }

            if (!_firstSeen.ContainsKey(messageId))
                _firstSeen[messageId] = now;

            return list;
        }

        public int GetTotalCount(uint messageId) =>
            _totalCounts.TryGetValue(messageId, out var total) ? total : 0;

        public void Forget(uint messageId)
        {
            _buffers.Remove(messageId);
            _totalCounts.Remove(messageId);
            _firstSeen.Remove(messageId);
        }

        public void CollectExpired(double now, double timeoutSeconds, List<uint> expired)
        {
            expired.Clear();
            foreach (var kv in _firstSeen)
            {
                if (now - kv.Value > timeoutSeconds)
                    expired.Add(kv.Key);
            }
        }
    }

    // ------- PUBLIC WRAPPERS (for external hooks/helpers) -------
    public void HandlePackedShardPublic(byte[] shard)
    {
        HandlePackedShard(shard);
    }

    public void SendPackedSnapshotToClient(NetworkConnection conn, byte[] packedEnvelope, ulong stateHash)
    {
        TargetPackedSnapshotTo(conn, packedEnvelope, stateHash);
    }

    public void SendPackedShardToClient(NetworkConnection conn, byte[] packedShard)
    {
        TargetPackedShardTo(conn, packedShard);
    }

    // debug helper: preview primi N byte in hex
    static string BytesPreview(byte[] b, int n)
    {
        if (b == null || b.Length == 0)
            return "(null)";

        int m = Math.Min(n, b.Length);
        var sb = new StringBuilder();
        for (int i = 0; i < m; ++i)
            sb.AppendFormat("{0:X2}", b[i]);

        if (b.Length > m)
            sb.Append("..");

        return sb.ToString();
    }

    byte[] CreateEnvelopeBytes(byte[] payload)
    {
        var env = new Envelope
        {
            messageId = _nextOutgoingMessageId++,
            seq = _lastSeqSent,
            payloadLen = payload?.Length ?? 0,
            payloadHash = EnvelopeUtil.ComputeHash64(payload),
            flags = 0
        };

        return EnvelopeUtil.Pack(env, payload);
    }

    byte[] CreateEnvelopeBytesForShard(byte[] shard, uint messageId, int fullPayloadLen, ulong fullPayloadHash)
    {
        var env = new Envelope
        {
            messageId = messageId,
            seq = _lastSeqSent,
            payloadLen = fullPayloadLen,
            payloadHash = fullPayloadHash,
            flags = 0
        };

        return EnvelopeUtil.Pack(env, shard);
    }
}
