using System.Linq;
using UnityEngine;

namespace Game.Networking.Adapters
{
    public interface IAdapterServiceProvider
    {
        INetTime ResolveNetTime(PlayerNetworkDriverFishNet driver);
        IAntiCheatValidator ResolveAntiCheat(PlayerNetworkDriverFishNet driver);
        IChunkInterest ResolveChunkInterest(PlayerNetworkDriverFishNet driver);
        IClockSync ResolveClockSync(PlayerNetworkDriverFishNet driver);
        IDriverTelemetry ResolveTelemetry(PlayerNetworkDriverFishNet driver);
        ISnapshotPackingService CreatePackingService(PlayerNetworkDriverFishNet driver);
        IFecService CreateFecService(PlayerNetworkDriverFishNet driver);
        IShardRegistry CreateShardRegistry(PlayerNetworkDriverFishNet driver);
        IFullSnapshotRetryManager CreateRetryManager(PlayerNetworkDriverFishNet driver);
    }

    public static class AdapterServiceLocator
    {
        sealed class DefaultAdapterServiceProvider : IAdapterServiceProvider
        {
            readonly INetTime _time = new NetTimeAdapter();

            public INetTime ResolveNetTime(PlayerNetworkDriverFishNet driver) => _time;

            public IAntiCheatValidator ResolveAntiCheat(PlayerNetworkDriverFishNet driver)
                => FindInScene<IAntiCheatValidator>();

            public IChunkInterest ResolveChunkInterest(PlayerNetworkDriverFishNet driver)
                => FindInScene<IChunkInterest>();

            public IClockSync ResolveClockSync(PlayerNetworkDriverFishNet driver)
            {
                if (driver != null)
                {
                    var local = driver.GetComponentsInChildren<MonoBehaviour>(includeInactive: true)
                                       .OfType<IClockSync>()
                                       .FirstOrDefault();
                    if (local != null)
                        return local;
                }

                return FindInScene<IClockSync>();
            }

            public IDriverTelemetry ResolveTelemetry(PlayerNetworkDriverFishNet driver)
                => FindInScene<IDriverTelemetry>() ?? DriverTelemetry.Null;

            public ISnapshotPackingService CreatePackingService(PlayerNetworkDriverFishNet driver)
                => new DefaultSnapshotPackingService();

            public IFecService CreateFecService(PlayerNetworkDriverFishNet driver)
                => new DefaultFecService();

            public IShardRegistry CreateShardRegistry(PlayerNetworkDriverFishNet driver)
                => new DefaultShardRegistry();

            public IFullSnapshotRetryManager CreateRetryManager(PlayerNetworkDriverFishNet driver)
                => new DefaultFullSnapshotRetryManager();

            static TService FindInScene<TService>() where TService : class
            {
                foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb is TService svc)
                        return svc;
                }

                return null;
            }
        }

        static readonly IAdapterServiceProvider s_Default = new DefaultAdapterServiceProvider();
        static IAdapterServiceProvider s_Current = s_Default;

        public static IAdapterServiceProvider Provider => s_Current;
        public static IAdapterServiceProvider DefaultProvider => s_Default;

        public static void RegisterProvider(IAdapterServiceProvider provider)
        {
            s_Current = provider ?? s_Default;
        }

        public static void ResetToDefault()
        {
            s_Current = s_Default;
        }
    }
}
