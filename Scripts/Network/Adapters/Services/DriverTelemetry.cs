using System.Collections.Generic;
using Game.Network;

namespace Game.Networking.Adapters
{
    public interface IDriverTelemetry
    {
        void Increment(string key, long by = 1);
        void Observe(string key, double value);
        void SetGauge(string key, double value);
        void Event(string name, IDictionary<string, string> tags = null, IDictionary<string, double> metrics = null);
    }

    static class DriverTelemetry
    {
        sealed class NullTelemetry : IDriverTelemetry
        {
            public void Increment(string key, long by = 1) { }
            public void Observe(string key, double value) { }
            public void SetGauge(string key, double value) { }
            public void Event(string name, IDictionary<string, string> tags = null, IDictionary<string, double> metrics = null) { }
        }

        sealed class AdapterTelemetry : IDriverTelemetry
        {
            readonly TelemetryManager _telemetry;

            public AdapterTelemetry(TelemetryManager telemetry)
            {
                _telemetry = telemetry;
            }

            public void Increment(string key, long by = 1) => _telemetry?.Increment(key, by);
            public void Observe(string key, double value) => _telemetry?.Observe(key, value);
            public void SetGauge(string key, double value) => _telemetry?.SetGauge(key, value);
            public void Event(string name, IDictionary<string, string> tags = null, IDictionary<string, double> metrics = null) =>
                _telemetry?.Event(name, tags, metrics);
        }

        public static readonly IDriverTelemetry Null = new NullTelemetry();

        public static IDriverTelemetry Create(TelemetryManager telemetry)
        {
            if (telemetry == null)
                return Null;

            return new AdapterTelemetry(telemetry);
        }
    }
}