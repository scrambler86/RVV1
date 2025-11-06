using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Lightweight snapshot of player-relevant state for deterministic replay.
// Keep minimal: pos, vel, animState, seq, deterministic RNG state if used by movement.
[Serializable]
public struct DeterministicSnapshot
{
    public double serverTime;
    public uint seq;
    public Vector3 pos;
    public Vector3 vel;
    public byte animState;
}

public class SnapshotReplay : MonoBehaviour
{
    public int historySeconds = 10;
    public float tickHz = 60f;

    private readonly List<DeterministicSnapshot> _buffer = new List<DeterministicSnapshot>();
    private double _lastSaveTime = 0.0;

    void FixedUpdate()
    {
        // no-op: Save/Restore called externally when needed
    }

    public void SaveSnapshot(Vector3 pos, Vector3 vel, byte anim, uint seq)
    {
        var snap = new DeterministicSnapshot
        {
            serverTime = Time.timeAsDouble,
            seq = seq,
            pos = pos,
            vel = vel,
            animState = anim
        };
        _buffer.Add(snap);
        // trim old
        double cutoff = Time.timeAsDouble - historySeconds;
        _buffer.RemoveAll(s => s.serverTime < cutoff);
    }

    // Returns the most recent snapshot at or before targetTime
    public bool TryGetSnapshotAt(double targetTime, out DeterministicSnapshot snap)
    {
        snap = default;
        if (_buffer.Count == 0) return false;
        DeterministicSnapshot best = _buffer[0];
        for (int i = 0; i < _buffer.Count; ++i)
        {
            if (_buffer[i].serverTime <= targetTime) best = _buffer[i];
            else break;
        }
        snap = best;
        return true;
    }

    public DeterministicSnapshot[] ExportTrace()
    {
        return _buffer.ToArray();
    }

    // Simple replay helper (calls back into provided applier)
    // NOTE: IEnumerator (non-generic) is required by Unity coroutines.
    public IEnumerator ReplayFromTo(double fromTime, double toTime, Action<DeterministicSnapshot> apply)
    {
        foreach (var s in _buffer)
        {
            if (s.serverTime < fromTime) continue;
            if (s.serverTime > toTime) break;
            apply?.Invoke(s);
            yield return null;
        }
    }
}
