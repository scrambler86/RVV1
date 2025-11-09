using System;
using System.Collections.Generic;
using UnityEngine;

public class TraceRecorder : MonoBehaviour
{
    public int capacity = 8000;
    public bool enabledRecording = true;

    public struct TraceEntry { public double t; public string tag; public string payload; }

    readonly List<TraceEntry> _buf = new List<TraceEntry>();

    public void Record(string tag, string payload)
    {
        if (!enabledRecording) return;
        lock (_buf)
        {
            _buf.Add(new TraceEntry { t = Time.realtimeSinceStartupAsDouble, tag = tag, payload = payload });
            if (_buf.Count > capacity) _buf.RemoveAt(0);
        }
    }

    public TraceEntry[] Export() { lock (_buf) { return _buf.ToArray(); } }

    public void DumpToConsole(string header = "TRACE DUMP")
    {
        var arr = Export();
        Debug.Log($"=== {header} ({arr.Length} entries) ===");
        foreach (var e in arr) Debug.Log($"{e.t:F3} | {e.tag} | {e.payload}");
    }
}