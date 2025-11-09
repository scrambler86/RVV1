using System.Text;

namespace Game.Networking.Adapters
{
    public interface ISnapshotPackingService
    {
        byte[] CreateEnvelope(byte[] payload, ref uint nextMessageId, uint lastSeqSent);
        byte[] CreateShardEnvelope(byte[] shard, uint messageId, uint lastSeqSent, int fullPayloadLen, ulong fullPayloadHash);
        string PreviewBytes(byte[] buffer, int maxBytes);
    }

    /// <summary>
    /// BOOKMARK: SNAPSHOT_PACKING_SERVICE
    /// - FULL: envelope standard
    /// - SHARD: envelope con FLAG_IS_SHARD, header porta i metadati del FULL (len/hash),
    ///          il payload effettivo è solo la shard (TryUnpack accetta lunghezze "parziali").
    /// </summary>
    public sealed class DefaultSnapshotPackingService : ISnapshotPackingService
    {
        // BOOKMARK: CREATE_ENVELOPE_FULL
        public byte[] CreateEnvelope(byte[] payload, ref uint nextMessageId, uint lastSeqSent)
        {
            var p = payload ?? System.Array.Empty<byte>();

            uint msgId = nextMessageId++;
            if (nextMessageId == 0) nextMessageId = 1;

            var env = new Envelope
            {
                messageId = msgId,
                seq = lastSeqSent,
                payloadLen = p.Length,
                payloadHash = EnvelopeUtil.ComputeHash64(p),
                flags = 0
            };

            return EnvelopeUtil.Pack(env, p);
        }

        // BOOKMARK: CREATE_ENVELOPE_SHARD
        public byte[] CreateShardEnvelope(byte[] shard, uint messageId, uint lastSeqSent, int fullPayloadLen, ulong fullPayloadHash)
        {
            var s = shard ?? System.Array.Empty<byte>();

            var env = new Envelope
            {
                messageId = messageId,
                seq = lastSeqSent,
                // Importante: nel header continuiamo a portare i metadati del FULL
                // (payloadLen/hash del full) per la verifica dopo il riassemblaggio:
                payloadLen = fullPayloadLen,
                payloadHash = fullPayloadHash,
                // Flag: è una SHARD → TryUnpack accetterà buffer parziale
                flags = EnvelopeUtil.FLAG_IS_SHARD
            };

            return EnvelopeUtil.Pack(env, s);
        }

        // BOOKMARK: PREVIEW_BYTES
        public string PreviewBytes(byte[] buffer, int maxBytes)
        {
            if (buffer == null || buffer.Length == 0) return "(null)";
            if (maxBytes <= 0) return string.Empty;

            int count = System.Math.Min(maxBytes, buffer.Length);
            var sb = new StringBuilder(count * 2 + 2);
            for (int i = 0; i < count; ++i)
                sb.AppendFormat("{0:X2}", buffer[i]);

            if (buffer.Length > count)
                sb.Append("..");

            return sb.ToString();
        }
    }
}
