using FishNet;
using FishNet.Managing.Timing;
using UnityEngine;

namespace Game.Networking.Adapters
{
    public class NetTimeFishNet : INetTime
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
