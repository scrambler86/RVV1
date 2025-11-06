// Assets/Scripts/Network/Utils/EnvelopeUtil.cs
using System;
using System.Security.Cryptography;

public static class EnvelopeUtil
{
    public static ulong ComputeHash64(byte[] data)
    {
        if (data == null) return 0;
        using (var sha = SHA256.Create())
        {
            var h = sha.ComputeHash(data);
            return BitConverter.ToUInt64(h, 0);
        }
    }

    // Simple pack: [env fields in fixed order][payload]
    public static byte[] Pack(Envelope env, byte[] payload)
    {
        int header = 4 + 4 + 4 + 8 + 1;
        var outb = new byte[header + (payload?.Length ?? 0)];
        Array.Copy(BitConverter.GetBytes(env.messageId), 0, outb, 0, 4);
        Array.Copy(BitConverter.GetBytes(env.seq), 0, outb, 4, 4);
        Array.Copy(BitConverter.GetBytes(env.payloadLen), 0, outb, 8, 4);
        Array.Copy(BitConverter.GetBytes(env.payloadHash), 0, outb, 12, 8);
        outb[20] = env.flags;
        if (payload != null) Array.Copy(payload, 0, outb, header, payload.Length);
        return outb;
    }

    public static bool TryUnpack(byte[] data, out Envelope env, out byte[] payload)
    {
        env = default; payload = null;
        if (data == null || data.Length < 21) return false;
        env.messageId = BitConverter.ToUInt32(data, 0);
        env.seq = BitConverter.ToUInt32(data, 4);
        env.payloadLen = BitConverter.ToInt32(data, 8);
        env.payloadHash = BitConverter.ToUInt64(data, 12);
        env.flags = data[20];
        int header = 21;
        if (data.Length < header + env.payloadLen) return false;
        if (env.payloadLen > 0)
        {
            payload = new byte[env.payloadLen];
            Array.Copy(data, header, payload, 0, env.payloadLen);
        }
        return true;
    }
}
