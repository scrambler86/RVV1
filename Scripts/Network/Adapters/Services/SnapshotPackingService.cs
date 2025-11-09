using System.Text;

namespace Game.Networking.Adapters
{
    public interface ISnapshotPackingService
    {
        byte[] CreateEnvelope(byte[] payload, ref uint nextMessageId, uint lastSeqSent);
        byte[] CreateShardEnvelope(byte[] shard, uint messageId, uint lastSeqSent, int fullPayloadLen, ulong fullPayloadHash);
        string PreviewBytes(byte[] buffer, int maxBytes);
    }

    public sealed class DefaultSnapshotPackingService : ISnapshotPackingService
    {
        public byte[] CreateEnvelope(byte[] payload, ref uint nextMessageId, uint lastSeqSent)
        {
            var env = new Envelope
            {
                messageId = nextMessageId++,
                seq = lastSeqSent,
                payloadLen = payload?.Length ?? 0,
                payloadHash = EnvelopeUtil.ComputeHash64(payload),
                flags = 0
            };

            return EnvelopeUtil.Pack(env, payload);
        }

        public byte[] CreateShardEnvelope(byte[] shard, uint messageId, uint lastSeqSent, int fullPayloadLen, ulong fullPayloadHash)
        {
            var env = new Envelope
            {
                messageId = messageId,
                seq = lastSeqSent,
                payloadLen = fullPayloadLen,
                payloadHash = fullPayloadHash,
                flags = 0
            };

            return EnvelopeUtil.Pack(env, shard);
        }

        public string PreviewBytes(byte[] buffer, int maxBytes)
        {
            if (buffer == null || buffer.Length == 0)
                return "(null)";

            int count = System.Math.Min(maxBytes, buffer.Length);
            var sb = new StringBuilder();
            for (int i = 0; i < count; ++i)
                sb.AppendFormat("{0:X2}", buffer[i]);

            if (buffer.Length > count)
                sb.Append("..");

            return sb.ToString();
        }
    }
<<<<<<< HEAD
}
=======
}
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
