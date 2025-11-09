using UnityEngine;

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
        [SerializeField] Object antiCheat;
        [SerializeField] Object chunkInterest;
        [SerializeField] Object clockSync;
        [SerializeField] Object telemetry;

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
            var resolved = ResolveFromUnityObject<INetTime>(timeSource);
            return resolved ?? AdapterServiceLocator.DefaultProvider.ResolveNetTime(driver);
        }

        public IAntiCheatValidator ResolveAntiCheat(PlayerNetworkDriverFishNet driver)
        {
            var resolved = ResolveFromUnityObject<IAntiCheatValidator>(antiCheat);
            return resolved ?? AdapterServiceLocator.DefaultProvider.ResolveAntiCheat(driver);
        }

        public IChunkInterest ResolveChunkInterest(PlayerNetworkDriverFishNet driver)
        {
            var resolved = ResolveFromUnityObject<IChunkInterest>(chunkInterest);
            return resolved ?? AdapterServiceLocator.DefaultProvider.ResolveChunkInterest(driver);
        }

        public IClockSync ResolveClockSync(PlayerNetworkDriverFishNet driver)
        {
            var resolved = ResolveFromUnityObject<IClockSync>(clockSync);
            return resolved ?? AdapterServiceLocator.DefaultProvider.ResolveClockSync(driver);
        }

        public IDriverTelemetry ResolveTelemetry(PlayerNetworkDriverFishNet driver)
        {
            var resolved = ResolveFromUnityObject<IDriverTelemetry>(telemetry);
            return resolved ?? AdapterServiceLocator.DefaultProvider.ResolveTelemetry(driver);
        }

        public ISnapshotPackingService CreatePackingService(PlayerNetworkDriverFishNet driver) =>
            AdapterServiceLocator.DefaultProvider.CreatePackingService(driver);

        public IFecService CreateFecService(PlayerNetworkDriverFishNet driver) =>
            AdapterServiceLocator.DefaultProvider.CreateFecService(driver);

        public IShardRegistry CreateShardRegistry(PlayerNetworkDriverFishNet driver) =>
            AdapterServiceLocator.DefaultProvider.CreateShardRegistry(driver);

        public IFullSnapshotRetryManager CreateRetryManager(PlayerNetworkDriverFishNet driver) =>
            AdapterServiceLocator.DefaultProvider.CreateRetryManager(driver);

        static T ResolveFromUnityObject<T>(Object source) where T : class
        {
            if (source == null)
                return null;

            if (source is T direct)
                return direct;

            if (source is Component component)
            {
                if (component is T componentAsT)
                    return componentAsT;

                return component.GetComponent<T>();
            }

            if (source is ScriptableObject so && so is T scriptable)
                return scriptable;

            return null;
        }
    }
}
