using UnityEngine;
using FishNet;
using FishNet.Managing.Timing;

public class NetTimeFishNet : INetTime
{
    public double Now()
    {
        var tm = InstanceFinder.TimeManager;
        if (tm == null) return Time.timeAsDouble;
        return tm.TicksToTime(tm.GetPreciseTick(TickType.Tick));
    }
}
