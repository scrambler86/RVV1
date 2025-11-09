using System.Collections.Generic;

namespace Game.Networking.Adapters
{
    public interface IDriverTelemetry
    {
        void Increment(string key, long by = 1);
        void Observe(string key, double value);
        void SetGauge(string key, double value);
        void Event(string name, IDictionary<string, string> tags = null, IDictionary<string, double> metrics = null);
    }

    public static class DriverTelemetry
    {
        private sealed class NullTelemetry : IDriverTelemetry
        {
            public void Increment(string key, long by = 1) { }
            public void Observe(string key, double value) { }
            public void SetGauge(string key, double value) { }
            public void Event(string name, IDictionary<string, string> tags = null, IDictionary<string, double> metrics = null) { }
        }

        public static IDriverTelemetry Null { get; } = new NullTelemetry();
    }
}
