// ===== [AAA-NET] NetDebugConfig ==============================================
// BOOKMARK: AAA-NET NetDebugConfig v1.0
// Scopo: feature flags e parametri runtime per la diagnostica (canary, logs, rate).
// - In Editor/Development: abilitato di default
// - In Release: disabilitato di default (puoi abilitarlo a runtime dall'Inspector)
using UnityEngine;

namespace AAA.NetDebug
{
    [DefaultExecutionOrder(-10000)]
    public class NetDebugConfig : MonoBehaviour
    {
        private static NetDebugConfig _inst;
        public static NetDebugConfig Instance
        {
            get
            {
                if (_inst == null) _inst = FindObjectOfType<NetDebugConfig>();
                return _inst;
            }
        }

        [Header("Global Flags (runtime)")]
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Tooltip("Abilita la diagnostica (es. canary) in Editor/Dev build.")]
        public bool diagnosticsEnabled = true;
#else
        [Tooltip("Abilita la diagnostica (es. canary) in Release build.")]
        public bool diagnosticsEnabled = false;
#endif

        [Tooltip("Log verbosi di diagnostica (riduci in Release).")]
        public bool verboseLogs = false;

        [Header("Canary (runtime)")]
        [Tooltip("Abilita la sonda di rete Canary (solo diagnostica).")]
        public bool canaryEnabled = false;

        [Tooltip("Hz della sonda: invii al secondo (es. 0.5 = 1 invio ogni 2s).")]
        [Range(0.1f, 4f)] public float canaryHz = 0.5f;

        [Tooltip("Invia canary via shards FEC (parity=1).")]
        public bool canaryUseFec = true;

        [Tooltip("Shard size bytes (FEC).")]
        [Range(256, 4096)] public int canaryShardSize = 1024;

        [Tooltip("Lunghezza payload canary (bytes).")]
        [Range(256, 16384)] public int canaryLen = 2048;

        [Tooltip("Channel affidabile per canary (consigliato).")]
        public bool canaryReliableChannel = true;

        void Awake()
        {
            if (_inst != null && _inst != this) { Destroy(gameObject); return; }
            _inst = this;
            DontDestroyOnLoad(gameObject);
        }

        // API statiche comode
        public static bool DiagnosticsOn => Instance != null && Instance.diagnosticsEnabled;
        public static bool CanaryOn => DiagnosticsOn && Instance != null && Instance.canaryEnabled;
        public static bool Verbose => Instance != null && Instance.verboseLogs;

        public static float CanaryIntervalSec
        {
            get
            {
                var hz = (Instance != null) ? Mathf.Max(0.1f, Instance.canaryHz) : 0.5f;
                return 1f / hz;
            }
        }
    }
}
