// Assets/Scripts/Server/AntiCheat/AntiCheatManagerAdvanced.cs
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Orchestratore AntiCheat modulare "AAA".
/// Inietta/compose i vari moduli e espone un metodo ValidateInput()
/// che il PlayerNetworkDriverFishNet chiama in CmdSendInput.
///</summary>
public class AntiCheatManagerAdvanced : MonoBehaviour, IAntiCheatValidator
{
    [Header("Modules")]
    public AntiCheatHeuristicBuffer heuristicBuffer;
    public AntiCheatCheapSimValidator cheapSim;
    public AntiCheatPatternDetector patternDetector;
    public AntiCheatEscalationPolicy escalation;
    public AntiCheatTelemetryHooks telemetryHooks;

    [Header("Global Switches")]
    public bool enabledChecks = true;

    void Awake()
    {
        // lazy-find if not wired in inspector
        heuristicBuffer = heuristicBuffer ?? gameObject.GetComponent<AntiCheatHeuristicBuffer>() ?? gameObject.AddComponent<AntiCheatHeuristicBuffer>();
        cheapSim = cheapSim ?? gameObject.GetComponent<AntiCheatCheapSimValidator>() ?? gameObject.AddComponent<AntiCheatCheapSimValidator>();
        patternDetector = patternDetector ?? gameObject.GetComponent<AntiCheatPatternDetector>() ?? gameObject.AddComponent<AntiCheatPatternDetector>();
        escalation = escalation ?? gameObject.GetComponent<AntiCheatEscalationPolicy>() ?? gameObject.AddComponent<AntiCheatEscalationPolicy>();
        telemetryHooks = telemetryHooks ?? gameObject.GetComponent<AntiCheatTelemetryHooks>() ?? gameObject.AddComponent<AntiCheatTelemetryHooks>();
    }

    /// <summary>
    /// Main validation entry called by driver. Returns true if input is allowed.
    /// The driver should call this before accepting predictedPos as authoritative.
    /// </summary>
    public bool ValidateInput(
        IPlayerNetworkDriver drv,
        uint seq,
        double timestampClient,
        Vector3 predictedPos,
        Vector3 lastServerPos,
        float maxStepAllowance,
        Vector3[] pathCorners,
        bool running,
        float dtServer = 1f/30f)
    {
        if (!enabledChecks) return true;

        // 0) register / append input into heuristic buffer
        heuristicBuffer?.PushInput(drv, seq, predictedPos, timestampClient, running, dtServer);

        // 1) cheap sim check (fast, lightweight)
        if (cheapSim != null && cheapSim.enableCheapSim)
        {
            bool ok = cheapSim.Validate(drv, lastServerPos, predictedPos, dtServer, running);
            if (!ok)
            {
                telemetryHooks?.OnSoftFail(drv, "cheap_sim");
                escalation?.RegisterSoftFail(drv, seq, "cheap_sim");
                return false;
            }
        }

        // 2) heuristic drift analysis (longer term)
        if (heuristicBuffer != null && heuristicBuffer.Enabled)
        {
            var verdict = heuristicBuffer.Evaluate(drv);
            if (!verdict.allowed)
            {
                telemetryHooks?.OnSoftFail(drv, "heuristic:" + verdict.reason);
                escalation?.RegisterSoftFail(drv, seq, "heuristic:" + verdict.reason);
                return false;
            }
        }

        // 3) pattern detector for bot-like behavior
        if (patternDetector != null && patternDetector.enablePatternDetection)
        {
            var p = patternDetector.Evaluate(drv);
            if (!p.allowed)
            {
                telemetryHooks?.OnSoftFail(drv, "pattern:" + p.reason);
                escalation?.RegisterSoftFail(drv, seq, "pattern:" + p.reason);
                return false;
            }
        }

        // 4) navmesh check (optional, can be expensive)
        if (cheapSim != null && cheapSim.enableNavMeshCheck)
        {
            if (!cheapSim.ValidateNavMesh(predictedPos, cheapSim.navMeshMaxSampleDist))
            {
                telemetryHooks?.OnSoftFail(drv, "navmesh");
                escalation?.RegisterSoftFail(drv, seq, "navmesh");
                return false;
            }
        }

        // if all checks passed
        telemetryHooks?.OnPass(drv);
        return true;
    }
}


// Assets/Scripts/Server/AntiCheat/AntiCheatHeuristicBuffer.cs
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Mantiene un buffer di N snapshot per client e fornisce metriche statistiche
/// per rilevare drift progressive o speed hacks a bassa frequenza.
/// </summary>
public class AntiCheatHeuristicBuffer : MonoBehaviour
{
    public bool Enabled = true;
    public int windowSize = 20; // default last 20 inputs
    public float maxDriftMeters = 1.2f; // se drift medio > questa soglia -> sospetto
    public float maxStdDevMeters = 0.8f;

    // Per semplicità: map client id -> circular buffer
    Dictionary<int, CircularBuffer> _map = new Dictionary<int, CircularBuffer>();

    class CircularBuffer
    {
        public readonly Queue<Vector3> q = new Queue<Vector3>();
        public readonly Queue<double> times = new Queue<double>();
        public int limit;
        public CircularBuffer(int l) { limit = l; }
        public void Push(Vector3 pos, double t)
        {
            q.Enqueue(pos); times.Enqueue(t);
            if (q.Count > limit) { q.Dequeue(); times.Dequeue(); }
        }
    }

    public void PushInput(IPlayerNetworkDriver drv, uint seq, Vector3 predictedPos, double timestampClient, bool running, float dt)
    {
        if (!Enabled || drv == null) return;
        int id = drv.OwnerClientId;
        if (!_map.TryGetValue(id, out var b)) { b = new CircularBuffer(windowSize); _map[id] = b; }
        b.Push(predictedPos, timestampClient);
    }

    /// <summary>
    /// Evaluate returns (allowed, reason)
    /// </summary>
    public (bool allowed, string reason) Evaluate(IPlayerNetworkDriver drv)
    {
        if (!Enabled || drv == null) return (true, "");
        int id = drv.OwnerClientId;
        if (!_map.TryGetValue(id, out var b)) return (true, "no-data");
        if (b.q.Count < 3) return (true, "insufficient-data");

        var arr = b.q.ToArray();
        float total = 0f; int n = arr.Length - 1;
        for (int i = 0; i < n; i++) total += Vector3.Distance(arr[i], arr[i + 1]);
        float mean = total / Mathf.Max(1, n);

        // standard deviation on increments
        var diffs = new float[n];
        for (int i = 0; i < n; i++) diffs[i] = Vector3.Distance(arr[i], arr[i + 1]);
        float avg = 0f; foreach (var d in diffs) avg += d; avg /= Mathf.Max(1, n);
        float sd = 0f; foreach (var d in diffs) sd += (d - avg) * (d - avg); sd = Mathf.Sqrt(sd / Mathf.Max(1, n));

        if (mean > maxDriftMeters) return (false, $"mean_drift:{mean:0.###}");
        if (sd > maxStdDevMeters) return (false, $"stddev:{sd:0.###}");
        return (true, "ok");
    }
}


// Assets/Scripts/Server/AntiCheat/AntiCheatCheapSimValidator.cs
using UnityEngine;

/// <summary>
/// Lightweight server-side sim validator. Non vuole sostituire la fisica,
/// ma dare una buona probabilit di catch per speedhacks e teleport.
/// </summary>
public class AntiCheatCheapSimValidator : MonoBehaviour
{
    public bool enableCheapSim = true;
    public bool enableNavMeshCheck = false;
    public float navMeshMaxSampleDist = 1.0f;
    public float simToleranceMeters = 0.5f; // extra tolerance

    public bool Validate(IPlayerNetworkDriver drv, Vector3 lastServerPos, Vector3 predictedPos, float dt, bool running)
    {
        if (!enableCheapSim) return true;
        if (drv == null) return true;

        float spd = 3.5f; // fallback
        var mb = drv as MonoBehaviour;
        if (mb)
        {
            var core = mb.GetComponent<PlayerControllerCore>();
            if (core != null) spd = core.speed * (core.IsRunning ? core.runMultiplier : 1f);
        }

        float maxMove = spd * Mathf.Max(0.001f, dt) * 1.15f + simToleranceMeters;
        float planar = Vector3.Distance(new Vector3(lastServerPos.x, 0, lastServerPos.z), new Vector3(predictedPos.x, 0, predictedPos.z));
        if (planar > maxMove) return false;

        if (enableNavMeshCheck)
        {
            if (!NavMesh.SamplePosition(predictedPos, out var nh, navMeshMaxSampleDist, NavMesh.AllAreas)) return false;
        }

        return true;
    }

    public bool ValidateNavMesh(Vector3 pos, float sampleDist)
    {
        return NavMesh.SamplePosition(pos, out _, sampleDist, NavMesh.AllAreas);
    }
}


// Assets/Scripts/Server/AntiCheat/AntiCheatPatternDetector.cs
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Rileva pattern meccanici tipici di bot: timing regolare, direzioni ricorrenti,
/// eccessiva precisione su path corners, ecc.
/// </summary>
public class AntiCheatPatternDetector : MonoBehaviour
{
    public bool enablePatternDetection = true;
    public int window = 24;
    public float timingRegularityThreshold = 0.02f; // seconds

    Dictionary<int, List<double>> _timings = new Dictionary<int, List<double>>();

    public void PushTiming(IPlayerNetworkDriver drv, double clientTimestamp)
    {
        if (!enablePatternDetection || drv == null) return;
        int id = drv.OwnerClientId;
        if (!_timings.TryGetValue(id, out var list)) { list = new List<double>(); _timings[id] = list; }
        list.Add(clientTimestamp);
        if (list.Count > window) list.RemoveAt(0);
    }

    public (bool allowed, string reason) Evaluate(IPlayerNetworkDriver drv)
    {
        if (!enablePatternDetection || drv == null) return (true, "");
        int id = drv.OwnerClientId;
        if (!_timings.TryGetValue(id, out var list) || list.Count < 4) return (true, "insufficient");

        // compute diffs and their stddev
        var diffs = new List<double>();
        for (int i = 1; i < list.Count; i++) diffs.Add(list[i] - list[i - 1]);
        double avg = 0; foreach(var d in diffs) avg += d; avg /= diffs.Count;
        double var = 0; foreach(var d in diffs) var += (d - avg) * (d - avg); var /= diffs.Count; double sd = System.Math.Sqrt(var);

        if (sd < timingRegularityThreshold) return (false, $"timing_regularity:{sd:0.000}");
        return (true, "ok");
    }
}


// Assets/Scripts/Server/AntiCheat/AntiCheatEscalationPolicy.cs
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stato per-client, escalation e inner policies.
/// Mantiene contatori di soft-fail e attua azioni (warning/kick/quarantine)
/// </summary>
public class AntiCheatEscalationPolicy : MonoBehaviour
{
    public int warnThreshold = 3;
    public int quarantineThreshold = 6;
    public float quarantineSeconds = 8f;

    TelemetryManager _telemetry;

    class State { public int softFails = 0; public double lastFailTs = 0; public bool quarantined = false; }
    Dictionary<int, State> _states = new Dictionary<int, State>();

    void Awake() { _telemetry = FindObjectOfType<TelemetryManager>(); }

    public void RegisterSoftFail(IPlayerNetworkDriver drv, uint seq, string reason)
    {
        if (drv == null) return;
        int id = drv.OwnerClientId;
        if (!_states.TryGetValue(id, out var s)) { s = new State(); _states[id] = s; }
        s.softFails++;
        s.lastFailTs = Time.time;
        _telemetry?.Increment("anti_cheat.softfails");
        _telemetry?.Event("anti_cheat.softfail", new System.Collections.Generic.Dictionary<string,string>{{"client", id.ToString()},{"reason",reason}});

        if (s.softFails == warnThreshold)
            IssueWarning(drv, reason);
        else if (s.softFails >= quarantineThreshold)
            Quarantine(drv, reason);
    }

    void IssueWarning(IPlayerNetworkDriver drv, string reason)
    {
        Debug.LogWarning($"[AC] Warning client={drv.OwnerClientId} reason={reason}");
        _telemetry?.Increment("anti_cheat.warnings");
        // TODO: hook to game manager to show warning message to client
    }

    void Quarantine(IPlayerNetworkDriver drv, string reason)
    {
        Debug.LogWarning($"[AC] Quarantine client={drv.OwnerClientId} reason={reason}");
        _telemetry?.Increment("anti_cheat.quarantines");
        if (!_states.TryGetValue(drv.OwnerClientId, out var s)) return; s.quarantined = true;
        // TODO: call game manager to apply action (block input, move to safe zone, kick)
    }
}


// Assets/Scripts/Server/AntiCheat/AntiCheatTelemetryHooks.cs
using UnityEngine;

/// <summary>
/// Thin wrapper to centralize Telemetry calls from AC modules
/// </summary>
public class AntiCheatTelemetryHooks : MonoBehaviour
{
    TelemetryManager _tm;
    void Awake() { _tm = FindObjectOfType<TelemetryManager>(); }
    public void OnSoftFail(IPlayerNetworkDriver drv, string reason)
    {
        if (drv == null) return;
        _tm?.Increment("anti_cheat.softfails");
        _tm?.Event("ac.softfail", new System.Collections.Generic.Dictionary<string,string>{{"client", drv.OwnerClientId.ToString()},{"reason",reason}});
    }
    public void OnPass(IPlayerNetworkDriver drv)
    {
        if (drv == null) return;
        _tm?.Increment("anti_cheat.passes");
    }
}

