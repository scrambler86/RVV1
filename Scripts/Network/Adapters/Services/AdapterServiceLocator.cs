using UnityEngine;
using Game.Network;

namespace Game.Networking.Adapters
{
    public interface IAdapterServiceProvider
    {
        INetTime ResolveNetTime(PlayerNetworkDriverFishNet driver);
        IAntiCheatValidator ResolveAntiCheat(PlayerNetworkDriverFishNet driver);
        ChunkManager ResolveChunkManager(PlayerNetworkDriverFishNet driver);
        ClockSyncManager ResolveClockSync(PlayerNetworkDriverFishNet driver);
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
            readonly NetTimeAdapter _time = new();

            public INetTime ResolveNetTime(PlayerNetworkDriverFishNet driver) => _time;

<<<<<<< HEAD
            public IAntiCheatValidator ResolveAntiCheat(PlayerNetworkDriverFishNet driver)
            {
                // Cerca qualunque MonoBehaviour in scena che implementi IAntiCheatValidator.
                // Più robusto rispetto a FindObjectOfType<AntiCheatManager>() e tollera implementazioni custom.
                var all = Object.FindObjectsOfType<MonoBehaviour>();
                foreach (var mb in all)
                {
                    if (mb is IAntiCheatValidator validator)
                        return validator;
                }

                return null;
            }
=======
            public IAntiCheatValidator ResolveAntiCheat(PlayerNetworkDriverFishNet driver) =>
                Object.FindObjectOfType<AntiCheatManager>();
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0

            public ChunkManager ResolveChunkManager(PlayerNetworkDriverFishNet driver) =>
                Object.FindObjectOfType<ChunkManager>();

            public ClockSyncManager ResolveClockSync(PlayerNetworkDriverFishNet driver)
            {
                if (driver != null)
                {
                    var local = driver.GetComponentInChildren<ClockSyncManager>();
                    if (local != null)
                        return local;
                }

                return Object.FindObjectOfType<ClockSyncManager>();
            }

            public IDriverTelemetry ResolveTelemetry(PlayerNetworkDriverFishNet driver)
            {
                var telemetry = Object.FindObjectOfType<TelemetryManager>();
                return DriverTelemetry.Create(telemetry);
            }

            public ISnapshotPackingService CreatePackingService(PlayerNetworkDriverFishNet driver) =>
                new DefaultSnapshotPackingService();

            public IFecService CreateFecService(PlayerNetworkDriverFishNet driver) =>
                new DefaultFecService();

            public IShardRegistry CreateShardRegistry(PlayerNetworkDriverFishNet driver) =>
                new DefaultShardRegistry();

            public IFullSnapshotRetryManager CreateRetryManager(PlayerNetworkDriverFishNet driver) =>
                new DefaultFullSnapshotRetryManager();
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
