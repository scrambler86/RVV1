using UnityEngine;
using System.IO;

public static class CompressionHelper
{
    private const float SCALE = 100f; // 1cm precision (pos/vel stored as Int32)

    public static byte[] CompressSnapshot(Vector3 pos, Vector3 vel, byte animState, uint seq, double time)
    {
        using (var ms = new MemoryStream(48))
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write((int)Mathf.Clamp(Mathf.RoundToInt(pos.x * SCALE), int.MinValue, int.MaxValue));
            bw.Write((int)Mathf.Clamp(Mathf.RoundToInt(pos.y * SCALE), int.MinValue, int.MaxValue));
            bw.Write((int)Mathf.Clamp(Mathf.RoundToInt(pos.z * SCALE), int.MinValue, int.MaxValue));

            bw.Write((int)Mathf.Clamp(Mathf.RoundToInt(vel.x * SCALE), int.MinValue, int.MaxValue));
            bw.Write((int)Mathf.Clamp(Mathf.RoundToInt(vel.y * SCALE), int.MinValue, int.MaxValue));
            bw.Write((int)Mathf.Clamp(Mathf.RoundToInt(vel.z * SCALE), int.MinValue, int.MaxValue));

            bw.Write(animState);
            bw.Write(seq);
            bw.Write(time);

            return ms.ToArray();
        }
    }

    public static (Vector3 pos, Vector3 vel, byte animState, uint seq, double time) DecompressSnapshot(byte[] data)
    {
        using (var ms = new MemoryStream(data))
        using (var br = new BinaryReader(ms))
        {
            Vector3 pos = new Vector3(
                br.ReadInt32() / SCALE,
                br.ReadInt32() / SCALE,
                br.ReadInt32() / SCALE
            );

            Vector3 vel = new Vector3(
                br.ReadInt32() / SCALE,
                br.ReadInt32() / SCALE,
                br.ReadInt32() / SCALE
            );

            byte animState = br.ReadByte();
            uint seq = br.ReadUInt32();
            double time = br.ReadDouble();

            return (pos, vel, animState, seq, time);
        }
    }
}
