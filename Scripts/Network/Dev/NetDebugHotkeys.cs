// Assets/Scripts/Network/Dev/NetDebugHotkeys.cs
using UnityEngine;
using System.Reflection;
using Game.Networking.Adapters;

public class NetDebugHotkeys : MonoBehaviour
{
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F6)) ForceFullNow();
        if (Input.GetKeyDown(KeyCode.F7)) ToggleCanaryAutoRun();
    }

    PlayerNetworkDriverFishNet FindLocalDriver()
    {
        var all = FindObjectsOfType<PlayerNetworkDriverFishNet>(includeInactive: false);
        // 1) prova l’istanza posseduta localmente (client player)
        foreach (var d in all)
        {
            var no = d.GetComponent<FishNet.Object.NetworkObject>();
            if (no != null && no.IsSpawned && no.IsOwner) return d;
        }
        // 2) se sei Host, prendine una qualsiasi spawned lato server
        foreach (var d in all)
        {
            var no = d.GetComponent<FishNet.Object.NetworkObject>();
            if (no != null && no.IsSpawned && d.IsServerInitialized) return d;
        }
        // 3) fallback
        return all.Length > 0 ? all[0] : null;
    }

    void ForceFullNow()
    {
        var driver = FindLocalDriver();
        if (driver == null) { Debug.LogWarning("[Hotkeys] Nessun PlayerNetworkDriverFishNet in scena."); return; }

        var no = driver.GetComponent<FishNet.Object.NetworkObject>();
        if (no == null || !no.IsSpawned)
        {
            Debug.LogWarning("[Hotkeys] Driver trovato ma NON spawnato (NetworkObject.IsSpawned=false). Collega/avvia Host prima di F6.");
            return;
        }

        // Chiama il privato RequestFullSnapshotFromServer(bool preferNoFec, bool bypassCooldown)
        var mi = typeof(PlayerNetworkDriverFishNet).GetMethod(
            "RequestFullSnapshotFromServer",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new System.Type[] { typeof(bool), typeof(bool) },
            null);

        if (mi == null)
        {
            Debug.LogWarning("[Hotkeys] Metodo privato RequestFullSnapshotFromServer(bool,bool) non trovato.");
            return;
        }

        mi.Invoke(driver, new object[] { true, true });
        Debug.Log("[Hotkeys] FULL richiesto (noFEC, bypassCooldown) → inviato il ServerRpc se connesso e spawnato.");
    }

    void ToggleCanaryAutoRun()
    {
        var canary = FindObjectOfType<CanaryRuntime>();
        if (canary == null) { Debug.LogWarning("[Hotkeys] CanaryRuntime non trovato in scena (è normale se l’hai rimosso)."); return; }
        canary.enabledRuntime = !canary.enabledRuntime;
        if (canary.enabledRuntime) canary.autoRun = false;
        Debug.Log($"[Hotkeys] CanaryRuntime enabledRuntime={canary.enabledRuntime}, autoRun={canary.autoRun}");
    }
}
