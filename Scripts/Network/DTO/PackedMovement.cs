using System;
using System.IO;
using UnityEngine;
using Game.Common;

//
// PackedMovement with:
// - FORMAT_VERSION header byte
// - 4-byte CRC32 checksum of payload portion (flags+payload) placed immediately after version
// - short-first encoding with int32 fallback (UseInt32Pos flag) retained
// - Uses PooledBinaryWriter to reduce allocations on pack hot-path
// - TryPackFull/TryPackDelta and TryUnpack APIs
//
// Note: This implementation returns byte[] for compatibility with existing RPCs. For extreme scale, migrate to ArraySegment<byte>/Span-based send path.
// CRC32 implementation provided inline (portable), replace with optimized native/Simd/xxHash if available.
//

public static class PackedMovement
{
    // current payload version
    const byte FORMAT_VERSION = 1;

    [Flags]
    public enum Flags : byte
    {
        None = 0,
        IsDelta = 1 << 0,
        HasPos = 1 << 1,
        HasVel = 1 << 2,
        HasAnim = 1 << 3,
        HasDtMs = 1 << 4,
        UseInt32Pos = 1 << 5 // fallback mode: store pos/vel as int32 (1cm) for large worlds
    }

    const float POS_SCALE = 100f; // 1 cm units
    const float VEL_SCALE = 100f; // 1 cm/s units

    static (short cx, short cy, short cz) QPosShort(Vector3 w, Vector3 origin)
    {
        float lx = (w.x - origin.x) * POS_SCALE;
        float ly = w.y * POS_SCALE;
        float lz = (w.z - origin.z) * POS_SCALE;
        return ((short)Mathf.Clamp(Mathf.RoundToInt(lx), short.MinValue, short.MaxValue),
                (short)Mathf.Clamp(Mathf.RoundToInt(ly), short.MinValue, short.MaxValue),
                (short)Mathf.Clamp(Mathf.RoundToInt(lz), short.MinValue, short.MaxValue));
    }
    static Vector3 DPosShort(short cx, short cy, short cz, Vector3 origin)
        => new Vector3(origin.x + cx / POS_SCALE, cy / POS_SCALE, origin.z + cz / POS_SCALE);

    static (int ix, int iy, int iz) QPosInt(Vector3 w, Vector3 origin)
    {
        float lx = (w.x - origin.x) * POS_SCALE;
        float ly = w.y * POS_SCALE;
        float lz = (w.z - origin.z) * POS_SCALE;
        return (Mathf.RoundToInt(lx), Mathf.RoundToInt(ly), Mathf.RoundToInt(lz));
    }
    static Vector3 DPosInt(int cx, int cy, int cz, Vector3 origin)
        => new Vector3(origin.x + cx / POS_SCALE, cy / POS_SCALE, origin.z + cz / POS_SCALE);

    static (short vx, short vy, short vz) QVelShort(Vector3 v)
    {
        float vx = v.x * VEL_SCALE, vy = v.y * VEL_SCALE, vz = v.z * VEL_SCALE;
        return ((short)Mathf.Clamp(Mathf.RoundToInt(vx), short.MinValue, short.MaxValue),
                (short)Mathf.Clamp(Mathf.RoundToInt(vy), short.MinValue, short.MaxValue),
                (short)Mathf.Clamp(Mathf.RoundToInt(vz), short.MinValue, short.MaxValue));
    }
    static Vector3 DVelShort(short vx, short vy, short vz)
        => new Vector3(vx / VEL_SCALE, vy / VEL_SCALE, vz / VEL_SCALE);

    static (int vx, int vy, int vz) QVelInt(Vector3 v)
    {
        float vx = v.x * VEL_SCALE, vy = v.y * VEL_SCALE, vz = v.z * VEL_SCALE;
        return (Mathf.RoundToInt(vx), Mathf.RoundToInt(vy), Mathf.RoundToInt(vz));
    }
    static Vector3 DVelInt(int vx, int vy, int vz)
        => new Vector3(vx / VEL_SCALE, vy / VEL_SCALE, vz / VEL_SCALE);

    public static Vector3 CellOrigin(int cellSize, short cellX, short cellY)
        => new Vector3(cellX * cellSize, 0f, cellY * cellSize);

    // ---------------- CRC32 (portable) ----------------
    // Simple CRC32 table implementation. Fast enough for moderate packet rates; replace with optimized native or xxHash in high throughput.
    static readonly uint[] _crcTable = MakeCrcTable();

    static uint[] MakeCrcTable()
    {
        uint[] table = new uint[256];
        const uint poly = 0xEDB88320u;
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0) crc = (crc >> 1) ^ poly;
                else crc >>= 1;
            }
            table[i] = crc;
        }
        return table;
    }

    static uint ComputeCRC32(ArraySegment<byte> data)
    {
        if (data.Array == null || data.Count == 0) return 0;
        uint crc = 0xFFFFFFFFu;
        int off = data.Offset;
        int end = data.Offset + data.Count;
        for (int i = off; i < end; i++)
        {
            crc = (crc >> 8) ^ _crcTable[(crc ^ data.Array[i]) & 0xFF];
        }
        return ~crc;
    }

    // -------- TryPackFull --------
    public static bool TryPackFull(Vector3 pos, Vector3 vel, byte anim, double serverTime, uint seq,
                                   short cellX, short cellY, int cellSize, out byte[] payload)
    {
        payload = null;

        // Try short encoding first; use pooled writer to reduce allocations.
        using var pw = new PooledBinaryWriter(128);
        var bw = pw.Writer;
        try
        {
            // Write version + placeholder CRC (4 bytes reserved)
            bw.Write(FORMAT_VERSION);
            bw.Write((uint)0); // placeholder for CRC32

            var origin = CellOrigin(cellSize, cellX, cellY);

            // Short encoding
            var (px, py, pz) = QPosShort(pos, origin);
            var (vxS, vyS, vzS) = QVelShort(vel);

            bw.Write((byte)Flags.None);
            bw.Write(cellX); bw.Write(cellY);
            bw.Write(px); bw.Write(py); bw.Write(pz);
            bw.Write(vxS); bw.Write(vyS); bw.Write(vzS);
            bw.Write(anim);
            uint msTime = (uint)Mathf.Clamp(Mathf.RoundToInt((float)(serverTime * 1000.0)), 0, int.MaxValue);
            bw.Write(msTime);
            bw.Write(seq);
            bw.Flush();

            var seg = pw.ToSegment();
            int payloadOffset = 1 + 4; // version + crc
            var payloadSeg = new ArraySegment<byte>(seg.Array, seg.Offset + payloadOffset, seg.Count - payloadOffset);
            uint crc = ComputeCRC32(payloadSeg);

            // write crc into reserved slot (little-endian)
            Array.Copy(BitConverter.GetBytes(crc), 0, seg.Array, seg.Offset + 1, 4);

            // copy out final array sized to used length
            payload = new byte[seg.Count];
            Array.Copy(seg.Array, seg.Offset, payload, 0, seg.Count);
            return true;
        }
        catch (Exception)
        {
            // fallback to int32 encoding path (also using pooled writer)
            try
            {
                pw.Reset();
                var bw2 = pw.Writer;
                bw2.Write(FORMAT_VERSION);
                bw2.Write((uint)0); // reserved CRC

                var origin = CellOrigin(cellSize, cellX, cellY);
                bw2.Write((byte)Flags.UseInt32Pos);
                bw2.Write(cellX); bw2.Write(cellY);

                var (ix, iy, iz) = QPosInt(pos, origin);
                var (ivx, ivy, ivz) = QVelInt(vel);
                bw2.Write(ix); bw2.Write(iy); bw2.Write(iz);
                bw2.Write(ivx); bw2.Write(ivy); bw2.Write(ivz);
                bw2.Write(anim);
                uint msTime2 = (uint)Mathf.Clamp(Mathf.RoundToInt((float)(serverTime * 1000.0)), 0, int.MaxValue);
                bw2.Write(msTime2);
                bw2.Write(seq);
                bw2.Flush();

                var seg2 = pw.ToSegment();
                int payloadOffset2 = 1 + 4;
                var payloadSeg2 = new ArraySegment<byte>(seg2.Array, seg2.Offset + payloadOffset2, seg2.Count - payloadOffset2);
                uint crc2 = ComputeCRC32(payloadSeg2);
                Array.Copy(BitConverter.GetBytes(crc2), 0, seg2.Array, seg2.Offset + 1, 4);

                payload = new byte[seg2.Count];
                Array.Copy(seg2.Array, seg2.Offset, payload, 0, seg2.Count);

                // telemetry/log could be emitted here to indicate fallback
                Debug.LogWarning("[PackedMovement] PackFull fallback to int32 encoding.");
                return true;
            }
            catch (Exception ex2)
            {
                Debug.LogWarning($"[PackedMovement] PackFull failed: {ex2.Message}");
                payload = null;
                return false;
            }
        }
    }

    // legacy compatibility wrapper
    public static byte[] PackFull(Vector3 pos, Vector3 vel, byte anim, double serverTime, uint seq,
                                  short cellX, short cellY, int cellSize)
    {
        if (TryPackFull(pos, vel, anim, serverTime, seq, cellX, cellY, cellSize, out var p)) return p;
        return null;
    }

    // -------- TryPackDelta --------
    public static bool TryPackDelta(in MovementSnapshot last,
                                   Vector3 pos, Vector3 vel, byte anim, double serverTime, uint seq,
                                   short cellX, short cellY, int cellSize,
                                   int maxPosDeltaCm, int maxVelDeltaCms, int maxDtMs,
                                   out byte[] payload)
    {
        payload = null;
        // Use pooled writer
        using var pw = new PooledBinaryWriter(96);
        var bw = pw.Writer;

        var origin = CellOrigin(cellSize, cellX, cellY);

        // positions short quantization relative to origin
        var (px, py, pz) = QPosShort(pos, origin);
        var (lpx, lpy, lpz) = QPosShort(last.pos, origin);

        int dpx = px - lpx, dpy = py - lpy, dpz = pz - lpz;
        if (Mathf.Abs(dpx) > maxPosDeltaCm || Mathf.Abs(dpy) > maxPosDeltaCm || Mathf.Abs(dpz) > maxPosDeltaCm)
            return false;

        var (vx, vy, vz) = QVelShort(vel);
        var (lvx, lvy, lvz) = QVelShort(last.vel);
        int dvx = vx - lvx, dvy = vy - lvy, dvz = vz - lvz;
        if (Mathf.Abs(dvx) > maxVelDeltaCms || Mathf.Abs(dvy) > maxVelDeltaCms || Mathf.Abs(dvz) > maxVelDeltaCms)
            return false;

        int dtMs = Mathf.RoundToInt((float)((serverTime - last.serverTime) * 1000.0));
        if (Mathf.Abs(dtMs) > maxDtMs) return false;

        var f = Flags.IsDelta | Flags.HasPos | Flags.HasVel | Flags.HasDtMs;
        if (anim != last.animState) f |= Flags.HasAnim;

        // header
        bw.Write(FORMAT_VERSION);
        bw.Write((uint)0); // reserved CRC for consistency (deltas also protected)

        bw.Write((byte)f);
        bw.Write((short)dpx); bw.Write((short)dpy); bw.Write((short)dpz);
        bw.Write((short)dvx); bw.Write((short)dvy); bw.Write((short)dvz);
        if ((f & Flags.HasAnim) != 0) bw.Write(anim);
        bw.Write((short)dtMs);
        bw.Write((ushort)(seq - last.seq));
        bw.Flush();

        var seg = pw.ToSegment();
        int payloadOffset = 1 + 4;
        var payloadSeg = new ArraySegment<byte>(seg.Array, seg.Offset + payloadOffset, seg.Count - payloadOffset);
        uint crc = ComputeCRC32(payloadSeg);
        Array.Copy(BitConverter.GetBytes(crc), 0, seg.Array, seg.Offset + 1, 4);

        payload = new byte[seg.Count];
        Array.Copy(seg.Array, seg.Offset, payload, 0, seg.Count);
        return true;
    }

    // legacy wrapper
    public static byte[] PackDelta(in MovementSnapshot last,
                                   Vector3 pos, Vector3 vel, byte anim, double serverTime, uint seq,
                                   short cellX, short cellY, int cellSize,
                                   int maxPosDeltaCm, int maxVelDeltaCms, int maxDtMs)
    {
        if (TryPackDelta(in last, pos, vel, anim, serverTime, seq, cellX, cellY, cellSize, maxPosDeltaCm, maxVelDeltaCms, maxDtMs, out var p))
            return p;
        return null;
    }

    // -------- TryUnpack --------
    public static bool TryUnpack(byte[] data, int cellSize,
                                 ref bool haveAnchor, ref short cellX, ref short cellY,
                                 ref MovementSnapshot baseSnap, out MovementSnapshot snap)
    {
        snap = default;
        if (data == null || data.Length < 6) return false; // at least version + crc + flags
        try
        {
            // basic header checks
            byte version = data[0];
            if (version != FORMAT_VERSION)
            {
                Debug.LogWarning($"[PackedMovement] Unsupported payload version {version}");
                return false;
            }

            // read crc and validate
            uint sentCrc = BitConverter.ToUInt32(data, 1);
            int payloadOffset = 1 + 4;
            var payloadSeg = new ArraySegment<byte>(data, payloadOffset, data.Length - payloadOffset);
            uint calc = ComputeCRC32(payloadSeg);
            if (calc != sentCrc)
            {
                Debug.LogWarning($"[PackedMovement] CRC mismatch (recv={sentCrc:X8} calc={calc:X8})");
                return false;
            }

            // parse payload using MemoryStream/BinaryReader starting at payloadOffset
            using var ms = new MemoryStream(data, payloadOffset, data.Length - payloadOffset, writable: false);
            using var br = new BinaryReader(ms);

            var f = (Flags)br.ReadByte();

            if ((f & Flags.IsDelta) == 0)
            {
                // full snapshot
                cellX = br.ReadInt16(); cellY = br.ReadInt16();
                var origin = CellOrigin(cellSize, cellX, cellY);

                if ((f & Flags.UseInt32Pos) == 0)
                {
                    short px = br.ReadInt16(), py = br.ReadInt16(), pz = br.ReadInt16();
                    short vx = br.ReadInt16(), vy = br.ReadInt16(), vz = br.ReadInt16();
                    byte anim = br.ReadByte();
                    uint msTime = br.ReadUInt32();
                    uint seq = br.ReadUInt32();

                    var pos = DPosShort(px, py, pz, origin);
                    var vel = DVelShort(vx, vy, vz);
                    double st = msTime / 1000.0;

                    snap = new MovementSnapshot(pos, vel, st, seq, anim);
                    haveAnchor = true;
                    baseSnap = snap;
                    return true;
                }
                else
                {
                    int px = br.ReadInt32(), py = br.ReadInt32(), pz = br.ReadInt32();
                    int vx = br.ReadInt32(), vy = br.ReadInt32(), vz = br.ReadInt32();
                    byte anim = br.ReadByte();
                    uint msTime = br.ReadUInt32();
                    uint seq = br.ReadUInt32();

                    var pos = DPosInt(px, py, pz, origin);
                    var vel = DVelInt(vx, vy, vz);
                    double st = msTime / 1000.0;

                    snap = new MovementSnapshot(pos, vel, st, seq, anim);
                    haveAnchor = true;
                    baseSnap = snap;
                    return true;
                }
            }
            else
            {
                // delta; require anchor
                if (!haveAnchor) return false;
                var origin = CellOrigin(cellSize, cellX, cellY);

                int dpx = 0, dpy = 0, dpz = 0;
                int dvx = 0, dvy = 0, dvz = 0;
                byte anim = baseSnap.animState;
                int dtMs = 0;

                if ((f & Flags.HasPos) != 0) { dpx = br.ReadInt16(); dpy = br.ReadInt16(); dpz = br.ReadInt16(); }
                if ((f & Flags.HasVel) != 0) { dvx = br.ReadInt16(); dvy = br.ReadInt16(); dvz = br.ReadInt16(); }
                if ((f & Flags.HasAnim) != 0) anim = br.ReadByte();
                if ((f & Flags.HasDtMs) != 0) dtMs = br.ReadInt16();
                ushort dseq = br.ReadUInt16();

                var (lpx, lpy, lpz) = QPosShort(baseSnap.pos, origin);
                var (lvx, lvy, lvz) = QVelShort(baseSnap.vel);
                short px = (short)(lpx + dpx), py = (short)(lpy + dpy), pz = (short)(lpz + dpz);
                short vx = (short)(lvx + dvx), vy = (short)(lvy + dvy), vz = (short)(lvz + dvz);

                var pos = DPosShort(px, py, pz, origin);
                var vel = DVelShort(vx, vy, vz);
                double st = baseSnap.serverTime + dtMs / 1000.0;
                uint seq = baseSnap.seq + dseq;

                snap = new MovementSnapshot(pos, vel, st, seq, anim);
                baseSnap = snap;
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[PackedMovement] TryUnpack failed: {ex.Message}");
            return false;
        }
    }
}
