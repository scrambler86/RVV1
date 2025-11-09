using UnityEngine;

namespace Game.Networking.Adapters
{
    /// <summary>
    /// Optional scene component that wires explicit adapter services without relying on dynamic lookups.
    /// </summary>
    public sealed class AdapterServiceBootstrap : MonoBehaviour
    {
        [Header("Service Overrides")]
        [SerializeField] Object timeSource;
        [SerializeField] Object antiCheat;
        [SerializeField] Object chunkInterest;
        [SerializeField] Object clockSync;
        [SerializeField] Object telemetry;
        [SerializeField] Object elevationPolicy;
        [SerializeField] Object snapshotPacking;
        [SerializeField] Object fec;
        [SerializeField] Object shardRegistry;
        [SerializeField] Object retryManager;

        [Header("Lifecycle")]
        [SerializeField] bool registerOnAwake = true;

        IAdapterServiceRegistry _previous;

        void Awake()
        {
            if (!registerOnAwake)
                return;

            Register();
        }

        void OnDestroy()
        {
            if (_previous != null)
            {
                AdapterServiceLocator.SetRegistry(_previous);
                _previous = null;
            }
            else
            {
                AdapterServiceLocator.ResetToDefault();
            }
        }

        public void Register()
        {
            _previous = AdapterServiceLocator.Registry;
            AdapterServiceLocator.SetRegistry(new ExplicitRegistry(this));
        }

        sealed class ExplicitRegistry : IAdapterServiceRegistry
        {
            readonly AdapterServiceBootstrap _bootstrap;
            readonly INetTime _netTime;
            readonly IAntiCheatValidator _anti;
            readonly IChunkInterest _chunk;
            readonly IClockSync _clock;
            readonly IDriverTelemetry _telemetry;
            readonly IElevationPolicy _elevation;
            readonly ISnapshotPackingService _packing;
            readonly IFecService _fec;
            readonly IShardRegistry _shards;
            readonly IFullSnapshotRetryManager _retry;

            public ExplicitRegistry(AdapterServiceBootstrap bootstrap)
            {
                _bootstrap = bootstrap;
                _netTime = Resolve<INetTime>(bootstrap.timeSource) ?? AdapterServiceLocator.DefaultRegistry.GetNetTime();
                _anti = Resolve<IAntiCheatValidator>(bootstrap.antiCheat);
                _chunk = Resolve<IChunkInterest>(bootstrap.chunkInterest);
                _clock = Resolve<IClockSync>(bootstrap.clockSync);
                _telemetry = Resolve<IDriverTelemetry>(bootstrap.telemetry) ?? AdapterServiceLocator.DefaultRegistry.GetTelemetry();
                _elevation = Resolve<IElevationPolicy>(bootstrap.elevationPolicy) ?? AdapterServiceLocator.DefaultRegistry.GetElevationPolicy();
                _packing = Resolve<ISnapshotPackingService>(bootstrap.snapshotPacking);
                _fec = Resolve<IFecService>(bootstrap.fec);
                _shards = Resolve<IShardRegistry>(bootstrap.shardRegistry);
                _retry = Resolve<IFullSnapshotRetryManager>(bootstrap.retryManager);
            }

            public INetTime GetNetTime() => _netTime ?? AdapterServiceLocator.DefaultRegistry.GetNetTime();
            public IAntiCheatValidator GetAntiCheat() => _anti;
            public IChunkInterest GetChunkInterest() => _chunk;
            public IClockSync GetClockSync(PlayerNetworkDriverFishNet driver) => _clock;
            public IDriverTelemetry GetTelemetry() => _telemetry ?? AdapterServiceLocator.DefaultRegistry.GetTelemetry();
            public IElevationPolicy GetElevationPolicy() => _elevation ?? AdapterServiceLocator.DefaultRegistry.GetElevationPolicy();
            public ISnapshotPackingService CreatePackingService(PlayerNetworkDriverFishNet driver)
                => _packing ?? AdapterServiceLocator.DefaultRegistry.CreatePackingService(driver);
            public IFecService CreateFecService(PlayerNetworkDriverFishNet driver)
                => _fec ?? AdapterServiceLocator.DefaultRegistry.CreateFecService(driver);
            public IShardRegistry CreateShardRegistry(PlayerNetworkDriverFishNet driver)
                => _shards ?? AdapterServiceLocator.DefaultRegistry.CreateShardRegistry(driver);
            public IFullSnapshotRetryManager CreateRetryManager(PlayerNetworkDriverFishNet driver)
                => _retry ?? AdapterServiceLocator.DefaultRegistry.CreateRetryManager(driver);

            static T Resolve<T>(Object obj) where T : class
            {
                if (obj == null)
                    return null;

                if (obj is T direct)
                    return direct;

                if (obj is Component component)
                {
                    if (component is T componentAsT)
                        return componentAsT;
                    return component.GetComponent<T>();
                }

                if (obj is ScriptableObject so && so is T asScriptable)
                    return asScriptable;

                return null;
            }
        }
    }
}
