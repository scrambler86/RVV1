using UnityEngine;
using Game.Network;

namespace Game.Networking.Adapters
{
    /// <summary>
    /// Optional scene component that wires explicit adapter services, avoiding dynamic lookups.
    /// </summary>
    public class AdapterServiceBootstrap : MonoBehaviour, IAdapterServiceProvider
    {
        [Header("Overrides")]
        [Tooltip("Optional reference implementing INetTime (MonoBehaviour or ScriptableObject). Leave empty to use the FishNet adapter.")]
        [SerializeField] Object timeSource;
        [SerializeField] AntiCheatManager antiCheat;
        [SerializeField] ChunkManager chunkManager;
        [SerializeField] ClockSyncManager clockSync;
        [SerializeField] TelemetryManager telemetry;

        [Header("Lifecycle")]
        [SerializeField] bool registerOnAwake = true;

        IAdapterServiceProvider _fallback;

        void Awake()
        {
            if (!registerOnAwake)
                return;

            _fallback = AdapterServiceLocator.Provider;
            AdapterServiceLocator.RegisterProvider(this);
        }

        void OnDestroy()
        {
            if (AdapterServiceLocator.Provider == this)
            {
                AdapterServiceLocator.RegisterProvider(_fallback ?? AdapterServiceLocator.DefaultProvider);
            }
        }

        public INetTime ResolveNetTime(PlayerNetworkDriverFishNet driver)
        {
            if (timeSource is INetTime netTime)
                return netTime;

            if (timeSource is Component comp && comp is INetTime componentTime)
                return componentTime;

            if (timeSource is ScriptableObject so && so is INetTime timeAsset)
                return timeAsset;

            return AdapterServiceLocator.DefaultProvider.ResolveNetTime(driver);
        }

        public IAntiCheatValidator ResolveAntiCheat(PlayerNetworkDriverFishNet driver)
        {
            if (antiCheat != null)
                return antiCheat;

            return AdapterServiceLocator.DefaultProvider.ResolveAntiCheat(driver);
        }

        public ChunkManager ResolveChunkManager(PlayerNetworkDriverFishNet driver)
        {
            if (chunkManager != null)
                return chunkManager;

            return AdapterServiceLocator.DefaultProvider.ResolveChunkManager(driver);
        }

        public ClockSyncManager ResolveClockSync(PlayerNetworkDriverFishNet driver)
        {
            if (clockSync != null)
                return clockSync;

            return AdapterServiceLocator.DefaultProvider.ResolveClockSync(driver);
        }

        public IDriverTelemetry ResolveTelemetry(PlayerNetworkDriverFishNet driver)
        {
            if (telemetry != null)
                return DriverTelemetry.Create(telemetry);

            return AdapterServiceLocator.DefaultProvider.ResolveTelemetry(driver);
        }

        public ISnapshotPackingService CreatePackingService(PlayerNetworkDriverFishNet driver) =>
            AdapterServiceLocator.DefaultProvider.CreatePackingService(driver);

        public IFecService CreateFecService(PlayerNetworkDriverFishNet driver) =>
            AdapterServiceLocator.DefaultProvider.CreateFecService(driver);

        public IShardRegistry CreateShardRegistry(PlayerNetworkDriverFishNet driver) =>
            AdapterServiceLocator.DefaultProvider.CreateShardRegistry(driver);

        public IFullSnapshotRetryManager CreateRetryManager(PlayerNetworkDriverFishNet driver) =>
            AdapterServiceLocator.DefaultProvider.CreateRetryManager(driver);
    }
}
