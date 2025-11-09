using FishNet;
using FishNet.Managing.Timing;
using UnityEngine;

namespace Game.Networking.Adapters
{
    /// <summary>
    /// FishNet-backed implementation of INetTime exposed to the generic adapters layer.
    /// </summary>
    public class NetTimeAdapter : INetTime
    {
        public double Now()
        {
            TimeManager tm = InstanceFinder.TimeManager;
            if (tm == null)
                return Time.timeAsDouble;

            return tm.TicksToTime(tm.GetPreciseTick(TickType.Tick));
        }
    }
}
