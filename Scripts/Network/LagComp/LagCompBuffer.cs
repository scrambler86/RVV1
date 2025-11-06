using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Buffer storico per lag compensation (server-side).
/// Conserva pos/vel con timestamp serverTime e permette sampling al tempo passato.
/// </summary>
public class LagCompBuffer : MonoBehaviour
{
    struct Sample { public Vector3 p, v; public double t; }

    [SerializeField] int maxSamples = 96; // ~3.2s @30Hz
    private readonly List<Sample> _buf = new();

    /// <summary>Push di un nuovo campione (chiamato dal server ad ogni tick del player).</summary>
    public void Push(Vector3 pos, Vector3 vel, double serverTime)
    {
        _buf.Add(new Sample { p = pos, v = vel, t = serverTime });
        if (_buf.Count > maxSamples)
            _buf.RemoveAt(0);
    }

    /// <summary>Interpola lo stato a un tempo passato (serverTime). Ritorna true se trovato.</summary>
    public bool TrySampleAt(double atTime, out Vector3 pos, out Vector3 vel)
    {
        pos = default; vel = default;
        if (_buf.Count == 0) return false;

        int hi = _buf.FindIndex(s => s.t >= atTime);
        if (hi <= 0)
        {
            pos = _buf[0].p; vel = _buf[0].v; return true;
        }
        if (hi >= _buf.Count)
        {
            var last = _buf[_buf.Count - 1];
            pos = last.p; vel = last.v; return true;
        }

        var a = _buf[hi - 1];
        var b = _buf[hi];
        double span = b.t - a.t;
        float t = span > 1e-6 ? (float)((atTime - a.t) / span) : 1f;

        pos = Vector3.Lerp(a.p, b.p, t);
        vel = Vector3.Lerp(a.v, b.v, t);
        return true;
    }
}
