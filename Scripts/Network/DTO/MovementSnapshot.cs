using UnityEngine;

/// <summary>
/// MovementSnapshot semplice e globale (compatibilità).
/// </summary>
public struct MovementSnapshot
{
    public Vector3 pos;
    public Vector3 vel;
    public double serverTime;
    public uint seq;
    public byte animState;

    public MovementSnapshot(Vector3 pos, Vector3 vel, double serverTime, uint seq, byte animState = 0)
    {
        this.pos = pos;
        this.vel = vel;
        this.serverTime = serverTime;
        this.seq = seq;
        this.animState = animState;
    }

    public override string ToString()
    {
        return $"MovementSnapshot(pos={pos}, vel={vel}, t={serverTime:F3}, seq={seq}, anim={animState})";
    }
}
