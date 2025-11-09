// Assets/Scripts/Network/Utils/EnvelopeUtil.cs
using System;
using System.Security.Cryptography;

public static class EnvelopeUtil
{
    // BOOKMARK: ENVELOPE_FLAGS
    /// <summary>Flag: il payload incapsulato è una SHARD parziale del FULL.</summary>
    public const byte FLAG_IS_SHARD = 0x02;
    /// <summary>Flag: busta CANARY/diagnostica (non movimento).</summary>
    public const byte FLAG_IS_CANARY = 0x08;

    // BOOKMARK: HASH64
    public static ulong ComputeHash64(byte[] data)
    {
        if (data == null) return 0;
        using (var sha = SHA256.Create())
        {
            var h = sha.ComputeHash(data);
            return BitConverter.ToUInt64(h, 0);
        }
    }

    // BOOKMARK: PACK
    // Layout fisso dell'header: 4(messageId) + 4(seq) + 4(payloadLen) + 8(payloadHash) + 1(flags) = 21 bytes
    public static byte[] Pack(Envelope env, byte[] payload)
    {
        int header = 4 + 4 + 4 + 8 + 1;
        var outb = new byte[header + (payload?.Length ?? 0)];
        Array.Copy(BitConverter.GetBytes(env.messageId), 0, outb, 0, 4);
        Array.Copy(BitConverter.GetBytes(env.seq), 0, outb, 4, 4);
        Array.Copy(BitConverter.GetBytes(env.payloadLen), 0, outb, 8, 4);
        Array.Copy(BitConverter.GetBytes(env.payloadHash), 0, outb, 12, 8);
        outb[20] = env.flags;

        if (payload != null && payload.Length > 0)
            Array.Copy(payload, 0, outb, header, payload.Length);

        return outb;
    }

    // BOOKMARK: TRY_UNPACK
    // - Caso FULL (no FLAG_IS_SHARD): comportamento originale, richiede header + payloadLen bytes.
    // - Caso SHARD (FLAG_IS_SHARD): il buffer dopo l'header contiene solo la shard; la accettiamo senza confrontarla con payloadLen del FULL.
    public static bool TryUnpack(byte[] data, out Envelope env, out byte[] payload)
    {
        env = default; payload = null;
        const int HEADER = 21;

        if (data == null || data.Length < HEADER)
            return false;

        env.messageId = BitConverter.ToUInt32(data, 0);
        env.seq = BitConverter.ToUInt32(data, 4);
        env.payloadLen = BitConverter.ToInt32(data, 8);
        env.payloadHash = BitConverter.ToUInt64(data, 12);
        env.flags = data[20];

        int avail = data.Length - HEADER;
        if (avail < 0) return false;

        // Se è una SHARD, il payload reale è "tutto ciò che resta" dopo l'header (può essere anche 0).
        if ((env.flags & FLAG_IS_SHARD) != 0)
        {
            if (avail == 0)
            {
                payload = Array.Empty<byte>();
                return true;
            }

            payload = new byte[avail];
            Array.Copy(data, HEADER, payload, 0, avail);
            return true;
        }

        // Non è shard: manteniamo il controllo originale
        if (env.payloadLen < 0) return false;
        if (data.Length < HEADER + env.payloadLen) return false;

        if (env.payloadLen > 0)
        {
            payload = new byte[env.payloadLen];
            Array.Copy(data, HEADER, payload, 0, env.payloadLen);
        }
        else
        {
            payload = Array.Empty<byte>();
        }

        return true;
    }
}
