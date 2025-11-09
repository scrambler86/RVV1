namespace Game.Networking.Adapters
{
#if CANARY_DEBUG
    using UnityEngine;
    using FishNet;
    using FishNet.Connection;
    using System;

    /// <summary>
    /// Hook/debug helpers to drive PlayerNetworkDriverFishNet from diagnostics tools
    /// without touching gameplay runtime. Compiled only with CANARY_DEBUG.
    /// </summary>
    public static class PlayerNetworkDriverFishNetHooks
    {
        /// <summary>
        /// Inoltra uno shard esterno al driver locale per tentare la ricomposizione
        /// (di solito chiamato da strumenti di test). Serve solo in debug.
        /// </summary>
        public static void HandleExternalShard(byte[] shardBytes)
        {
            var driver = UnityEngine.Object.FindObjectOfType<PlayerNetworkDriverFishNet>();
            if (driver != null)
            {
                driver.HandlePackedShardPublic(shardBytes);
            }
        }

        /// <summary>
        /// Utility di test per spedire un payload arbitrario a un client con envelope.
        /// Se useFec==true, spezza in shard e setta i metadati corretti (payloadLen/hash del full).
        /// Se isCanary==true, marca i pacchetti con il bit 0x80 per essere filtrati sul client.
        /// </summary>
        public static void SendWithEnvelopeToClient(int clientId,
                                                    uint messageId,
                                                    uint seq,
                                                    byte baseFlags,
                                                    byte[] payload,
                                                    bool useFec,
                                                    int fecShardSize,
                                                    int fecParityShards,
                                                    bool isCanary)
        {
            if (payload == null)
                return;

            var dict = InstanceFinder.ServerManager?.Clients;
            if (dict == null)
                return;

            if (!dict.TryGetValue(clientId, out NetworkConnection client) || client == null)
                return;

            var driver = UnityEngine.Object.FindObjectOfType<PlayerNetworkDriverFishNet>();
            if (driver == null)
                return;

            // set bit canary se richiesto
            byte flags = isCanary ? (byte)(baseFlags | 0x80) : baseFlags;

            if (!useFec || fecParityShards <= 0)
            {
                Envelope env = new Envelope
                {
                    messageId = messageId,
                    seq = seq,
                    payloadLen = payload.Length,
                    payloadHash = EnvelopeUtil.ComputeHash64(payload),
                    flags = flags
                };

                var packed = EnvelopeUtil.Pack(env, payload);

                driver.SendPackedSnapshotToClient(client, packed, EnvelopeUtil.ComputeHash64(payload));
                return;
            }

            // FEC path
            var shards = FecReedSolomon.BuildShards(payload, fecShardSize, fecParityShards);

            ulong fullHash = EnvelopeUtil.ComputeHash64(payload);
            int fullLen = payload.Length;
            uint groupMessageId = (messageId != 0) ? messageId : (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF);

            foreach (var s in shards)
            {
                Envelope envShard = new Envelope
                {
                    messageId = groupMessageId,
                    seq = seq,
                    payloadLen = fullLen,
                    payloadHash = fullHash,
                    // shard + canary bit se isCanary true
                    flags = isCanary ? (byte)(0x80 | 0x04) : (byte)0x04
                };

                var packed = EnvelopeUtil.Pack(envShard, s);
                driver.SendPackedShardToClient(client, packed);
            }
        }
    }
#endif // CANARY_DEBUG
}