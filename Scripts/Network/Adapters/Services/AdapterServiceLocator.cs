using System;

namespace Game.Networking.Adapters
{
    public interface IAdapterServiceRegistry
    {
        INetTime GetNetTime();
        IAntiCheatValidator GetAntiCheat();
        IChunkInterest GetChunkInterest();
        IClockSync GetClockSync(PlayerNetworkDriverFishNet driver);
        IDriverTelemetry GetTelemetry();
        IElevationPolicy GetElevationPolicy();
        ISnapshotPackingService CreatePackingService(PlayerNetworkDriverFishNet driver);
        IFecService CreateFecService(PlayerNetworkDriverFishNet driver);
        IShardRegistry CreateShardRegistry(PlayerNetworkDriverFishNet driver);
        IFullSnapshotRetryManager CreateRetryManager(PlayerNetworkDriverFishNet driver);
    }

    public static class AdapterServiceLocator
    {
        sealed class DefaultAdapterServiceRegistry : IAdapterServiceRegistry
        {
            readonly INetTime _time = new NetTimeAdapter();
            readonly IDriverTelemetry _telemetry = DriverTelemetry.Null;
            readonly IElevationPolicy _elevation = ElevationPolicies.FlatGround;

            public INetTime GetNetTime() => _time;
            public IAntiCheatValidator GetAntiCheat() => NullAntiCheatValidator.Instance;
            public IChunkInterest GetChunkInterest() => null;
            public IClockSync GetClockSync(PlayerNetworkDriverFishNet driver) => null;
            public IDriverTelemetry GetTelemetry() => _telemetry;
            public IElevationPolicy GetElevationPolicy() => _elevation;
            public ISnapshotPackingService CreatePackingService(PlayerNetworkDriverFishNet driver) => new DefaultSnapshotPackingService();
            public IFecService CreateFecService(PlayerNetworkDriverFishNet driver) => new ReedSolomonFecService();
            public IShardRegistry CreateShardRegistry(PlayerNetworkDriverFishNet driver) => new DefaultShardRegistry();
            public IFullSnapshotRetryManager CreateRetryManager(PlayerNetworkDriverFishNet driver) => new DefaultFullSnapshotRetryManager();
        }

        sealed class NullAntiCheatValidator : IAntiCheatValidator
        {
            NullAntiCheatValidator() { }
            public static NullAntiCheatValidator Instance { get; } = new NullAntiCheatValidator();
            public bool ValidateInput(in AntiCheatInputContext context) => true;
        }

        static readonly IAdapterServiceRegistry s_Default = new DefaultAdapterServiceRegistry();
        static IAdapterServiceRegistry s_Current = s_Default;

        public static IAdapterServiceRegistry Registry => s_Current;
        public static IAdapterServiceRegistry DefaultRegistry => s_Default;

        public static void SetRegistry(IAdapterServiceRegistry registry)
        {
            s_Current = registry ?? s_Default;
        }

        public static void ResetToDefault()
        {
            s_Current = s_Default;
        }
    }
}
