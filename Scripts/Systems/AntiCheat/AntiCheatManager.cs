// [BOOKMARK: USING]
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Game.Networking.Adapters;

// [BOOKMARK: SUMMARY]
/// <summary>
/// Validatore server-side degli input movimento del client.
/// Ritorna true se l'input sembra legittimo, false se è sospetto.
/// Viene chiamato da PlayerNetworkDriverFishNet.CmdSendInput().
/// </summary>
public class AntiCheatManager : MonoBehaviour, IAntiCheatValidator
{
    // [BOOKMARK: SWITCHES]
    [Header("Switches")]
    [Tooltip("Controlla la distanza planare massima percorsa in 1 tick.")]
    public bool enableMaxStepCheck = true;

    [Tooltip("Verifica che la posizione proposta sia ancora su NavMesh.")]
    public bool enableNavMeshCheck = true;

    [Tooltip("Blocca spike verticali esagerati (salti/voli istantanei).")]
    public bool enableVerticalClamp = true;

    [Tooltip("Controllo extra di velocità massima simulata lato server (un po' più costoso).")]
    public bool enableCheapSim = false;

    [Tooltip("Logga warning quando falliscono i check.")]
    public bool debugLogs = true;

    // [BOOKMARK: LIMITS]
    [Header("Limits")]
    [Tooltip("raggio di sample NavMesh.SamplePosition, metri.")]
    public float navMeshSampleDist = 1.0f;

    [Tooltip("velocità verticale massima consentita (m/s).")]
    public float maxVerticalSpeed = 2.0f;

    [Tooltip("quanti soft-fail prima di avvisare in log.")]
    public int maxSoftFailsBeforeWarning = 30;

    [Tooltip("quanti soft-fail prima di segnalare quarantena/kick.")]
    public int maxSoftFailsBeforeQuarantine = 80;

    // [BOOKMARK: TELEMETRY]
    [Header("Telemetry (optional)")]
    public TelemetryManager telemetry;

    // conteggio dei soft-fail per clientId
    private readonly Dictionary<int, int> _softFailCounts = new Dictionary<int, int>();

    // =====================================================================
    // 1) Metodo richiesto dall'interfaccia IAntiCheatValidator (senza dt)
    // =====================================================================
    // [BOOKMARK: API_NO_DT]
    public bool ValidateInput(
        IPlayerNetworkDriver drv,
        uint seq,
        double timestamp,
        Vector3 predictedPos,
        Vector3 lastServerPos,
        float maxStepAllowance,
        Vector3[] pathCorners,
        bool running)
    {
        // fallback: usiamo Time.fixedDeltaTime come dt lato server
        float dtServer = Time.fixedDeltaTime;
        return ValidateInputInternal(
            drv,
            seq,
            timestamp,
            predictedPos,
            lastServerPos,
            maxStepAllowance,
            pathCorners,
            running,
            dtServer
        );
    }

    // =====================================================================
    // 2) Overload usato dal driver quando può passare dtServer
    //    (NOTA: usa IPlayerNetworkDriver, non il tipo concreto!)
    // =====================================================================
    // [BOOKMARK: API_WITH_DT]
    public bool ValidateInput(
        IPlayerNetworkDriver drv,
        uint seq,
        double timestamp,
        Vector3 predictedPos,
        Vector3 lastServerPos,
        float maxStepAllowance,
        Vector3[] pathCorners,
        bool running,
        float dtServer)
    {
        return ValidateInputInternal(
            drv,
            seq,
            timestamp,
            predictedPos,
            lastServerPos,
            maxStepAllowance,
            pathCorners,
            running,
            dtServer
        );
    }

    // =====================================================================
    // 3) Logica vera dei controlli, basata su IPlayerNetworkDriver
    // =====================================================================
    // [BOOKMARK: CORE]
    private bool ValidateInputInternal(
        IPlayerNetworkDriver drv,
        uint seq,
        double timestamp,
        Vector3 predictedPos,
        Vector3 lastServerPos,
        float maxStepAllowance,
        Vector3[] pathCorners,
        bool running,
        float dtServer)
    {
        bool ok = true;

        // ricava PlayerControllerCore per velocità consentita (se presente sullo stesso GO)
        PlayerControllerCore core = null;
        if (drv is MonoBehaviour mb)
            core = mb.GetComponent<PlayerControllerCore>();

        float baseSpeed = core ? core.speed : 3.5f;
        float runMul = core ? core.runMultiplier : 1.6f;
        float allowedSpeed = running ? baseSpeed * runMul : baseSpeed;

        // 1) distanza planare massima per tick
        if (enableMaxStepCheck)
        {
            Vector2 a = new Vector2(predictedPos.x, predictedPos.z);
            Vector2 b = new Vector2(lastServerPos.x, lastServerPos.z);
            float planarDist = Vector2.Distance(a, b);

            if (planarDist > maxStepAllowance + 0.001f)
            {
                ok = false;
                if (debugLogs)
                    Debug.LogWarning($"[AC] client={drv?.OwnerClientId} seq={seq} planarDist={planarDist:0.###} > maxStep={maxStepAllowance:0.###}");
            }
        }

        // 2) validità NavMesh
        if (ok && enableNavMeshCheck)
        {
            if (!NavMesh.SamplePosition(predictedPos, out NavMeshHit nh, navMeshSampleDist, NavMesh.AllAreas))
            {
                ok = false;
                if (debugLogs)
                    Debug.LogWarning($"[AC] client={drv?.OwnerClientId} seq={seq} off-NavMesh");
            }
            else
            {
                // clamp su navmesh per tolleranza
                predictedPos = nh.position;
            }
        }

        // 3) clamp verticale
        if (ok && enableVerticalClamp)
        {
            float dy = Mathf.Abs(predictedPos.y - lastServerPos.y);
            float maxDy = maxVerticalSpeed * Mathf.Max(0.001f, dtServer);
            if (dy > maxDy + 0.001f)
            {
                ok = false;
                if (debugLogs)
                    Debug.LogWarning($"[AC] client={drv?.OwnerClientId} seq={seq} vertical spike dy={dy:0.###} > maxDy={maxDy:0.###}");
            }
        }

        // 4) cheap sim (opzionale)  verifica che la velocità planare non superi un tetto ragionevole
        if (ok && enableCheapSim)
        {
            Vector3 delta = predictedPos - lastServerPos;
            delta.y = 0f;
            float speed = delta.magnitude / Mathf.Max(0.001f, dtServer);

            // fattore di margine (deriva dal tuo driver): maxSpeedTolerance già considerato nel maxStepAllowance,
            // qui usiamo un tetto extra per spike ancora più eclatanti
            float hardCap = allowedSpeed * 1.8f;
            if (speed > hardCap)
            {
                ok = false;
                if (debugLogs)
                    Debug.LogWarning($"[AC] client={drv?.OwnerClientId} seq={seq} speed={speed:0.###} > hardCap={hardCap:0.###}");
            }
        }

        // Telemetria semplice sui fail
        if (!ok)
        {
            int id = drv?.OwnerClientId ?? -1;
            if (id >= 0)
            {
                _softFailCounts.TryGetValue(id, out int cur);
                cur++;
                _softFailCounts[id] = cur;

                if (cur == maxSoftFailsBeforeWarning)
                    Debug.LogWarning($"[AC] client={id} warning threshold reached ({cur})");

                if (cur == maxSoftFailsBeforeQuarantine)
                    Debug.LogError($"[AC] client={id} quarantine/kick threshold reached ({cur})");

                telemetry?.Increment($"client.{id}.anti_cheat.soft_fails");
            }
        }

        return ok;
    }
}
