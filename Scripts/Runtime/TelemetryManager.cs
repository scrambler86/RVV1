using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using UnityEngine;
using Game.Networking.Adapters;

public class TelemetryManager : MonoBehaviour, IDriverTelemetry
{
    readonly ConcurrentDictionary<string, long> _counters = new();
    readonly ConcurrentDictionary<string, double> _gauges = new();
    readonly ConcurrentQueue<(string key, double val, long ts)> _histSamples = new();

    // Event buffer for richer events
    readonly ConcurrentQueue<string> _events = new();

    // File sink
    public string outputFolder = "TelemetryLogs";
    public int flushIntervalSeconds = 5;
    DateTime _lastFlush = DateTime.MinValue;

    // Pushgateway
    public bool enablePushGateway = true;
    public string pushGatewayUrl = "http://localhost:9091/metrics/job/gameinstance";
    public string pushGatewayInstanceLabel = "instance=localdev";

    // Cardinality control
    public bool compressClientIds = true; // reduce cardinality by bucketing client ids if true

    void Awake()
    {
        try { if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder); } catch { }
    }

    // COUNTERS
    public void Increment(string key, long by = 1) => _counters.AddOrUpdate(NormalizeKey(key), by, (_, v) => v + by);
    public long GetInt(string key) => _counters.TryGetValue(NormalizeKey(key), out var v) ? v : 0;

    // GAUGES
    public void SetGauge(string key, double value) => _gauges[NormalizeKey(key)] = value;
    public double GetGauge(string key) => _gauges.TryGetValue(NormalizeKey(key), out var v) ? v : 0.0;

    // HISTOGRAM / SAMPLES
    public void Observe(string key, double value) => _histSamples.Enqueue((NormalizeKey(key), value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    // EVENTS: tags (string->string) and numeric metrics (string->double)
    public void Event(string evName, IDictionary<string, string> tags = null, IDictionary<string, double> metrics = null)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("o"));
            sb.Append(" | ").Append(evName);
            if (tags != null && tags.Count > 0)
            {
                sb.Append(" | tags:");
                foreach (var kv in tags) sb.Append($" {kv.Key}={SanitizeTagValue(kv.Value)};");
            }
            if (metrics != null && metrics.Count > 0)
            {
                sb.Append(" | metrics:");
                foreach (var kv in metrics) sb.Append($" {kv.Key}={kv.Value:0.###};");
            }
            _events.Enqueue(sb.ToString());
        }
        catch { }
    }

    void Update()
    {
        if ((DateTime.UtcNow - _lastFlush).TotalSeconds < flushIntervalSeconds) return;
        _lastFlush = DateTime.UtcNow;
        try { FlushToFile(); } catch { }
        if (enablePushGateway) TryPushGateway();
    }

    void FlushToFile()
    {
        var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var file = Path.Combine(outputFolder, $"telemetry_{ts}.log");
        var sb = new StringBuilder();
        sb.AppendLine($"# TIMESTAMP {DateTime.UtcNow:o}");
        sb.AppendLine("# COUNTERS");
        foreach (var kv in _counters.OrderBy(k => k.Key)) sb.AppendLine($"{kv.Key},{kv.Value}");
        sb.AppendLine("# GAUGES");
        foreach (var kv in _gauges.OrderBy(k => k.Key)) sb.AppendLine($"{kv.Key},{kv.Value}");
        sb.AppendLine("# SAMPLES (last chunk)");
        int dumped = 0;
        while (_histSamples.TryDequeue(out var s) && dumped < 20000)
        {
            sb.AppendLine($"{s.ts},{s.key},{s.val}");
            dumped++;
        }
        sb.AppendLine("# EVENTS (recent)");
        int ed = 0;
        while (_events.TryDequeue(out var e) && ed < 10000)
        {
            sb.AppendLine(e);
            ed++;
        }
        File.WriteAllText(file, sb.ToString());
    }

    void TryPushGateway()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var kv in _counters) sb.AppendLine($"{PromSanitize(kv.Key)} {kv.Value}");
            foreach (var kv in _gauges) sb.AppendLine($"{PromSanitize(kv.Key)} {kv.Value}");
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "text/plain; version=0.0.4";
                var url = pushGatewayUrl;
                if (!string.IsNullOrEmpty(pushGatewayInstanceLabel)) url = $"{url}/{pushGatewayInstanceLabel}";
                wc.UploadData(url, "POST", bytes);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Telemetry PushGateway push failed: {ex.Message}");
        }
    }

    string NormalizeKey(string k)
    {
        if (!compressClientIds) return k;
        // simple heuristic: bucket client.{id}.* => client.bucketN.*
        if (k.StartsWith("client."))
        {
            var parts = k.Split('.');
            if (parts.Length >= 2)
            {
                if (int.TryParse(parts[1], out var id))
                {
                    int bucket = Math.Min(100, id / 10); // bucket size 10 to reduce cardinality
                    parts[1] = $"b{bucket}";
                    return string.Join(".", parts);
                }
            }
        }
        return k;
    }

    string PromSanitize(string k) => k.Replace('.', '_').Replace(' ', '_').Replace('/', '_').Replace('-', '_');

    string SanitizeTagValue(string v) => v?.Replace(',', '_').Replace(';', '_').Replace('|', '_') ?? "";

    // Snapshot helpers
    public IDictionary<string, long> SnapshotCounters() => new Dictionary<string, long>(_counters);
    public IDictionary<string, double> SnapshotGauges() => new Dictionary<string, double>(_gauges);
}
