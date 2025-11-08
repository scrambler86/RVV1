using System;
using System.Collections.Generic;
using UnityEngine;
using FishNet;
using FishNet.Object;
using FishNet.Connection;
using Game.Networking.Adapters;

// CanaryRuntime: invia payload di test (canary) ai client per verificare integrità del trasporto.
public class CanaryRuntime : NetworkBehaviour
{
    [Header("Canary")]
    public int canaryLen = 2048;
    public int shardSize = 1024;
    public int parity = 2;
    public float intervalSec = 2.0f;
    public bool autoRun = false;
    public bool useShards = true;
    public bool enabledRuntime = true;

    [Header("Logging")]
    public bool verboseLogs = false;

    private byte[] _canaryPayload;

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
        _canaryPayload = BuildCanary(canaryLen);
        if (verboseLogs)
            Debug.Log($"[Canary] Awake. Built payload len={_canaryPayload.Length}");
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        if (!enabledRuntime)
        {
            if (verboseLogs)
                Debug.Log("[Canary] OnStartServer: runtime disabled, non faccio nulla.");
            return;
        }

        if (verboseLogs)
            Debug.Log($"[Canary] OnStartServer: IsServerInitialized={IsServerInitialized}, autoRun={autoRun}");

        if (autoRun && IsServerInitialized)
            InvokeRepeating(nameof(BroadcastOnce), 1f, intervalSec);
    }

    private void OnDisable()
    {
        if (IsServerInitialized)
            CancelInvoke(nameof(BroadcastOnce));
    }

    public static byte[] BuildCanary(int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++)
            b[i] = (byte)(i & 0xFF);
        return b;
    }

    [ContextMenu("Canary/Broadcast Once")]
    public void BroadcastOnce() => BroadcastOnce(useShards);

    public void BroadcastOnce(bool shards)
    {
        if (!enabledRuntime)
        {
            if (verboseLogs)
                Debug.Log("[Canary] BroadcastOnce: runtime disabled.");
            return;
        }

        if (!IsServerInitialized)
        {
            if (verboseLogs)
                Debug.Log("[Canary] BroadcastOnce: IsServerInitialized = false. Sei sicuro di essere Host/Server?");
            return;
        }

        var sm = InstanceFinder.ServerManager;
        if (sm == null)
        {
            if (verboseLogs)
                Debug.LogWarning("[Canary] BroadcastOnce: ServerManager nullo.");
            return;
        }

        var dict = sm.Clients;
        if (dict == null)
        {
            if (verboseLogs)
                Debug.LogWarning("[Canary] BroadcastOnce: Clients dict nullo.");
            return;
        }

        if (dict.Count == 0)
        {
            if (verboseLogs)
                Debug.Log("[Canary] BroadcastOnce: nessun client connesso (Host incluso).");
            return;
        }

        if (verboseLogs)
            Debug.Log($"[Canary] BroadcastOnce: sending to {dict.Count} client(s). shards={shards}");

        foreach (var kv in dict)
        {
            var conn = kv.Value;
            if (conn == null)
            {
                if (verboseLogs)
                    Debug.LogWarning("[Canary] BroadcastOnce: connection null, skip.");
                continue;
            }

            SendCanaryTo(conn, shards);
        }
    }

    public void SendCanaryTo(NetworkConnection conn, bool shards)
    {
        if (!IsServerInitialized)
        {
            if (verboseLogs)
                Debug.Log("[Canary] SendCanaryTo: IsServerInitialized=false, skip.");
            return;
        }

        if (conn == null)
        {
            if (verboseLogs)
                Debug.LogWarning("[Canary] SendCanaryTo: conn null, skip.");
            return;
        }

        if (_canaryPayload == null)
        {
            if (verboseLogs)
                Debug.LogWarning("[Canary] SendCanaryTo: _canaryPayload null, skip.");
            return;
        }

        var driver = FindObjectOfType<PlayerNetworkDriverFishNet>();
        if (driver == null)
        {
            if (verboseLogs)
                Debug.LogWarning("[Canary] PlayerNetworkDriverFishNet non trovato in scena, annullo invio.");
            return;
        }

        if (shards && parity > 0)
        {
            // Usa FEC per shard canary; marca come CANARY per evitare decoding come movimento.
            List<byte[]> sList = FecReedSolomon.BuildShards(_canaryPayload, shardSize, parity);

            ulong fullHash = EnvelopeUtil.ComputeHash64(_canaryPayload);
            int fullLen = _canaryPayload.Length;
            uint seq = (uint)Environment.TickCount;
            uint messageId = (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF);

            for (int i = 0; i < sList.Count; i++)
            {
                var shardBytes = sList[i];
                var env = new Envelope
                {
                    messageId = messageId,
                    seq = seq,
                    payloadLen = fullLen,
                    payloadHash = fullHash,
                    // 0x08 = CANARY, 0x04 = shard.
                    flags = 0x08 | 0x04
                };
                var packed = EnvelopeUtil.Pack(env, shardBytes);
                driver.SendPackedShardToClient(conn, packed);
            }

            if (verboseLogs)
                Debug.Log($"[Canary] SHARDS sent to conn={conn.ClientId}, totalShards={sList.Count} fullLen={fullLen}");
        }
        else
        {
            uint seq = (uint)Environment.TickCount;
            var env = new Envelope
            {
                messageId = (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF),
                seq = seq,
                payloadLen = _canaryPayload.Length,
                payloadHash = EnvelopeUtil.ComputeHash64(_canaryPayload),
                // 0x08 = CANARY, 0x01 = snapshot/full.
                flags = 0x08 | 0x01
            };
            var packed = EnvelopeUtil.Pack(env, _canaryPayload);
            driver.SendPackedSnapshotToClient(conn, packed, env.payloadHash);

            if (verboseLogs)
                Debug.Log($"[Canary] SNAPSHOT sent to conn={conn.ClientId}, len={_canaryPayload.Length}");
        }
    }
}
