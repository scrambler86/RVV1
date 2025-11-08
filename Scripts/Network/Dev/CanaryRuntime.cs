using System;
using System.Collections.Generic;
using UnityEngine;
using FishNet;
using FishNet.Object;
using FishNet.Connection;
using Game.Networking.Adapters;

// ===== BOOKMARK: CANARY_RUNTIME: HEADER =====
// CanaryRuntime: invia payload di test (canary) ai client per verificare integrità trasporto
// - Nessuna dipendenza da Channel.*
// - Usa PlayerNetworkDriverFishNet per l'invio (shard/snapshot)
// - Envelope degli shard contiene metadata del payload completo (len + hash)
// ============================================

public class CanaryRuntime : NetworkBehaviour
{
    // ===== BOOKMARK: CANARY_RUNTIME: CONFIG =====
    [Header("Canary")]
    [Tooltip("Lunghezza del payload di test in byte.")]
    public int canaryLen = 2048;

    [Tooltip("Dimensione singolo shard FEC.")]
    public int shardSize = 1024;

    [Tooltip("Numero di shard di parità (FEC). 0 = disabilitato.")]
    public int parity = 2;

    [Tooltip("Esegue automaticamente un invio ogni N secondi (solo server).")]
    public float intervalSec = 2.0f;

    [Tooltip("Avvio automatico su server.")]
    public bool autoRun = false;

    [Tooltip("Usare shard (FEC) invece di inviare il payload intero.")]
    public bool useShards = true;

    [Tooltip("Abilita il runtime (se false non invia nulla).")]
    public bool enabledRuntime = true;

    [Header("Logging")]
    public bool verboseLogs = false;

    // ===== BOOKMARK: CANARY_RUNTIME: STATE =====
    private byte[] _canaryPayload;

    // OnValidate di Unity non è virtual → usiamo 'new' per evitare warning.
    // Tenuto solo in Editor.
#if UNITY_EDITOR
    protected new void OnValidate()
    {
        canaryLen = Mathf.Max(1, canaryLen);
        shardSize = Mathf.Max(64, shardSize);
        parity = Mathf.Max(0, parity);
        intervalSec = Mathf.Max(0.25f, intervalSec);
    }
#endif

    private void Awake()
    {
        // ===== BOOKMARK: CANARY_RUNTIME: BUILD_PAYLOAD =====
        _canaryPayload = BuildCanary(canaryLen);
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        if (!enabledRuntime) return;
        if (autoRun)
            InvokeRepeating(nameof(BroadcastOnce), 1f, intervalSec);
    }

    private void OnDisable()
    {
        if (IsServer)
            CancelInvoke(nameof(BroadcastOnce));
    }

    // ===== BOOKMARK: CANARY_RUNTIME: API =====
    public static byte[] BuildCanary(int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; ++i)
            b[i] = (byte)(i & 0xFF);
        return b;
    }

    [ContextMenu("Canary/Broadcast Once")]
    public void BroadcastOnce() => BroadcastOnce(useShards);

    public void BroadcastOnce(bool shards)
    {
        if (!IsServer || !enabledRuntime) return;
        var dict = InstanceFinder.ServerManager?.Clients;
        if (dict == null || dict.Count == 0) return;

        foreach (var kv in dict)
        {
            var conn = kv.Value;
            if (conn == null) continue;
            SendCanaryTo(conn, shards);
        }
    }

    // ===== BOOKMARK: CANARY_RUNTIME: SEND =====
    public void SendCanaryTo(NetworkConnection conn, bool shards)
    {
        if (!IsServer || conn == null || _canaryPayload == null) return;

        var driver = FindObjectOfType<PlayerNetworkDriverFishNet>();
        if (driver == null)
        {
            if (verboseLogs) Debug.LogWarning("[Canary] Driver non trovato, annullo invio.");
            return;
        }

        if (shards && parity > 0)
        {
            // Costruisci shard con metadata del payload COMPLETO nell'envelope
            List<byte[]> sList = FecReedSolomon.BuildShards(_canaryPayload, shardSize, parity);
            ulong fullHash = EnvelopeUtil.ComputeHash64(_canaryPayload);
            int fullLen = _canaryPayload.Length;
            uint seq = (uint)Environment.TickCount;
            uint messageId = (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF);

            for (int i = 0; i < sList.Count; i++)
            {
                var shardBytes = sList[i];
                Envelope env = new Envelope
                {
                    messageId = messageId,
                    seq = seq,
                    payloadLen = fullLen,     // FULL payload len
                    payloadHash = fullHash,   // FULL payload hash
                    flags = 0x4               // shard
                };
                var packed = EnvelopeUtil.Pack(env, shardBytes);
                driver.SendPackedShardToClient(conn, packed);
            }

            if (verboseLogs || true)
                Debug.Log($"[Canary] SHARDS sent to {conn.ClientId}, totalShards={sList.Count} fullLen={fullLen}");
        }
        else
        {
            // Invia payload intero come snapshot
            uint seq = (uint)Environment.TickCount;
            Envelope env = new Envelope
            {
                messageId = (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF),
                seq = seq,
                payloadLen = _canaryPayload.Length,
                payloadHash = EnvelopeUtil.ComputeHash64(_canaryPayload),
                flags = 0x8 | 0x1 // snapshot + marker
            };
            var packed = EnvelopeUtil.Pack(env, _canaryPayload);
            driver.SendPackedSnapshotToClient(conn, packed, env.payloadHash);

            if (verboseLogs || true)
                Debug.Log($"[Canary] SNAPSHOT sent to {conn.ClientId}, len={_canaryPayload.Length}");
        }
    }
}
