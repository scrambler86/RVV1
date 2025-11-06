using UnityEngine;

public struct InputState
{
    public Vector3 dir;       // direzione input (camera-relative)
    public bool running;      // toggle corsa
    public uint seq;          // numero di sequenza
    public float dt;          // delta time per quel tick (s)
    public double timestamp;  // tempo client (diagnostica)

    public InputState(Vector3 dir, bool running, uint seq, float dt, double timestamp = 0)
    {
        this.dir = dir;
        this.running = running;
        this.seq = seq;
        this.dt = dt;
        this.timestamp = timestamp;
    }
}
