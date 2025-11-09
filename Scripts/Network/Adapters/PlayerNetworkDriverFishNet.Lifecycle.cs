using System;
using FishNet;
using FishNet.Connection;
using UnityEngine;
using UnityEngine.AI;

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
        }

        void RefreshRuntimeServices(bool refreshFactories = false)
        {
            var provider = AdapterServiceLocator.Provider ?? AdapterServiceLocator.DefaultProvider;

            _netTime = provider.ResolveNetTime(this) ?? AdapterServiceLocator.DefaultProvider.ResolveNetTime(this);
            _anti = provider.ResolveAntiCheat(this) ?? _anti;
            _chunk = provider.ResolveChunkInterest(this) ?? _chunk;
            _telemetry = provider.ResolveTelemetry(this) ?? DriverTelemetry.Null;
            _clockSync = provider.ResolveClockSync(this) ?? _clockSync;

            if (refreshFactories || _packingService == null)
                _packingService = provider.CreatePackingService(this) ?? AdapterServiceLocator.DefaultProvider.CreatePackingService(this);

            if (refreshFactories || _fecService == null)
                _fecService = provider.CreateFecService(this) ?? AdapterServiceLocator.DefaultProvider.CreateFecService(this);

            if (refreshFactories || _shardRegistry == null)
                _shardRegistry = provider.CreateShardRegistry(this) ?? AdapterServiceLocator.DefaultProvider.CreateShardRegistry(this);

            if (refreshFactories || _retryManager == null)
                _retryManager = provider.CreateRetryManager(this) ?? AdapterServiceLocator.DefaultProvider.CreateRetryManager(this);

            _servicesResolved = true;
        }

        void EnsureServices()
        {
            if (_servicesResolved && _netTime != null && _packingService != null && _fecService != null && _shardRegistry != null && _retryManager != null)
                return;

            RefreshRuntimeServices(true);
        }

        void EnsureOwnerRuntime()
        {
            if (_ownerRuntime == null)
                _ownerRuntime = new PlayerDriverOwnerRuntime();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            EnsureServices();
            EnsureOwnerRuntime();
            _ownerRuntime?.Reset();

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
            double rtt = (tm != null) ? Math.Max(0.01, tm.RoundTripTime / 1000.0) : 0.06;
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

            EnsureServices();
            EnsureOwnerRuntime();
            _ownerRuntime?.Reset(clearInputs: false);

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
            _ownerRuntime?.Reset(clearInputs: false);
            base.OnStopServer();
        }

        public override void OnStopClient()
        {
            _shuttingDown = true;
            _core.SetAllowInput(false);
            _ownerRuntime?.Reset();
            base.OnStopClient();
        }

        void OnDisable()
        {
            _shuttingDown = true;
            _ownerRuntime?.Reset();

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
