using System.Collections.Generic;
using FishNet.Connection;
using UnityEngine;

namespace Game.Networking.Adapters
{
    public interface IClockSync
    {
        double ClientToServerTime(double clientTimeSeconds);
        double ServerToClientTime(double serverTimeSeconds);
        void RecordSample(double rttMs, double clientToServerOffsetMs);
        double OffsetSeconds { get; }
        double RttMs { get; }
        double OffsetJitterSeconds { get; }
    }

    public interface IChunkInterest
    {
        int CellSize { get; }
        void RegisterPlayer(IPlayerNetworkDriver driver);
        void UnregisterPlayer(IPlayerNetworkDriver driver);
        void UpdatePlayerChunk(IPlayerNetworkDriver driver, Vector3 worldPos);
        void CollectWithinRadius(NetworkConnection owner, int ring, HashSet<NetworkConnection> outSet);
        bool TryGetCellOf(NetworkConnection owner, out (int x, int y) cell);
    }

    public readonly struct AntiCheatInputContext
    {
        public AntiCheatInputContext(IPlayerNetworkDriver driver,
                                     uint sequence,
                                     double clientTimestamp,
                                     Vector3 clientPredictedPosition,
                                     Vector3 lastServerPosition,
                                     float maxStepAllowance,
                                     Vector3[] pathCorners,
                                     bool running,
                                     float serverDeltaTime)
        {
            Driver = driver;
            Sequence = sequence;
            ClientTimestamp = clientTimestamp;
            ClientPredictedPosition = clientPredictedPosition;
            LastServerPosition = lastServerPosition;
            MaxStepAllowance = maxStepAllowance;
            PathCorners = pathCorners;
            Running = running;
            ServerDeltaTime = serverDeltaTime;
        }

        public IPlayerNetworkDriver Driver { get; }
        public uint Sequence { get; }
        public double ClientTimestamp { get; }
        public Vector3 ClientPredictedPosition { get; }
        public Vector3 LastServerPosition { get; }
        public float MaxStepAllowance { get; }
        public Vector3[] PathCorners { get; }
        public bool Running { get; }
        public float ServerDeltaTime { get; }
    }

    public interface IAntiCheatValidator
    {
        bool ValidateInput(in AntiCheatInputContext context);
    }
}