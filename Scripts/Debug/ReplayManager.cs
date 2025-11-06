using System.Collections.Generic;
using UnityEngine;

public class ReplayManager : MonoBehaviour
{
    [Header("Recording")]
    public bool enableRecording = false;

    private readonly List<InputState> _inputs = new();
    private readonly List<MovementSnapshot> _snaps = new();

    public IReadOnlyList<InputState> Inputs => _inputs;
    public IReadOnlyList<MovementSnapshot> Snapshots => _snaps;

    public void RecordInput(InputState s)
    {
        if (!enableRecording) return;
        _inputs.Add(s);
        if (_inputs.Count > 5000) _inputs.RemoveAt(0);
    }

    public void RecordSnapshot(MovementSnapshot s)
    {
        if (!enableRecording) return;
        _snaps.Add(s);
        if (_snaps.Count > 5000) _snaps.RemoveAt(0);
    }

    public void Clear()
    {
        _inputs.Clear();
        _snaps.Clear();
    }
}
