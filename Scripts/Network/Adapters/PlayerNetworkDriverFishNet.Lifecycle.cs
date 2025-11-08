using System;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;
using UnityEngine.AI;
using Game.Network;

namespace Game.Networking.Adapters
{
    public partial class PlayerNetworkDriverFishNet
    {
        // ---------- lifecycle ----------
        void Awake()
        {
            Application.quitting -= OnAppQuit;
            Application.quitting += OnAppQuit;

            if (!_core) _core = GetComponent<PlayerControllerCore>();
            if (!_rb) _rb = GetComponent<Rigidbody>();
            if (!_agent) _agent = GetComponent<NavMeshAgent>();
            if (!_ctm) _ctm = GetComponent<ClickToMoveAgent>();

            // Adattatore tempo FishNet -> INetTime
            _netTime = new NetTimeFishNet();

            _anti = FindObjectOfType<AntiCheatManager>();
            _chunk = FindObjectOfType<ChunkManager>();
            _telemetry = FindObjectOfType<TelemetryManager>();

            _clockSync = GetComponentInChildren<ClockSyncManager>();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            _sendDt = 1f / Mathf.Max(1, sendRateHz);
            _core.SetAllowInput(IsOwner);

            if (IsOwner)
            {
                _rb.isKinematic = false;
                _rb.detectCollisions = true;
                _rb.interpolation = RigidbodyInterpolation.Interpolate;
            }
            else
            {
                _rb.isKinematic = true;
                _rb.detectCollisions = false;
                _rb.interpolation = RigidbodyInterpolation.None;
                _remoteLastRenderPos = transform.position;
                _remoteDisplaySpeed = 0f;
            }

            var tm = InstanceFinder.TimeManager;
            double rtt = (tm != null)
                ? Math.Max(0.01, tm.RoundTripTime / 1000.0)
                : 0.06;

            _back = _backTarget = ClampD(rtt * 0.6, minBack, maxBack);

            _haveAnchor = false;
            _baseSnap = default;

            _tokens = maxInputsPerSecond + burstAllowance;
            _lastRefill = _netTime.Now();
            _shuttingDown = false;

            _serverLastTime = _netTime.Now();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            _shuttingDown = false;
            _chunk?.RegisterPlayer(this);

            _serverLastPos = transform.position;
            _tokens = maxInputsPerSecond + burstAllowance;
            _lastRefill = _netTime.Now();
            _serverLastTime = _netTime.Now();
        }

        public override void OnStopServer()
        {
            _shuttingDown = true;
            _chunk?.UnregisterPlayer(this);
            base.OnStopServer();
        }

        public override void OnStopClient()
        {
            _shuttingDown = true;
            _core.SetAllowInput(false);
            base.OnStopClient();
        }

        void OnDisable()
        {
            _shuttingDown = true;

            if (IsServerInitialized && _chunk != null)
            {
                try
                {
                    _chunk.UnregisterPlayer(this);
                }
                catch
                {
                    // safe to ignore during shutdown/unload
                }
            }
        }

        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            base.OnOwnershipClient(previousOwner);
            _core.SetAllowInput(IsOwner);
        }
    }
}
