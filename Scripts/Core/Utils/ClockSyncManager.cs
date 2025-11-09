using System;
using UnityEngine;
using Game.Networking.Adapters;

namespace Game.Network
{
    /// <summary>
    /// Small client-side clock sync helper using ping/response samples.
    /// Records EMA for offset and RTT; designed to be fed from a ping RPC handshake.
    /// </summary>
    public class ClockSyncManager : MonoBehaviour, IClockSync
    {
        // Offset: estimated serverTime - clientTime in seconds (server = client + Offset)
        public double OffsetSeconds { get; private set; } = 0.0;
        // RTT in milliseconds (EMA)
        public double RttMs { get; private set; } = 60.0;
        // observed jitter of offset (seconds)
        public double OffsetJitterSeconds { get; private set; } = 0.0;

        // smoothing constants
        const double ALPHA = 0.15;
        const double ALPHA_JITTER = 0.12;

        // record a sample: rttMs is measured round-trip in ms, clientToServerOffsetMs is computed sample offset in ms
        public void RecordSample(double rttMs, double clientToServerOffsetMs)
        {
            if (RttMs <= 0) RttMs = rttMs; else RttMs = (1 - ALPHA) * RttMs + ALPHA * rttMs;
            double off = clientToServerOffsetMs / 1000.0;
            if (OffsetSeconds == 0) OffsetSeconds = off; else OffsetSeconds = (1 - ALPHA) * OffsetSeconds + ALPHA * off;
            double jitter = Math.Abs(off - OffsetSeconds);
            if (OffsetJitterSeconds == 0) OffsetJitterSeconds = jitter; else OffsetJitterSeconds = (1 - ALPHA_JITTER) * OffsetJitterSeconds + ALPHA_JITTER * jitter;
        }

        // helper: convert client local time to estimated server time
        public double ClientToServerTime(double clientTimeSeconds)
        {
            return clientTimeSeconds + OffsetSeconds;
        }

        // helper: convert server time to client local estimation
        public double ServerToClientTime(double serverTimeSeconds)
        {
            return serverTimeSeconds - OffsetSeconds;
        }
    }
}
