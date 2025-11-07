using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using Game.Networking.Adapters;

namespace Game.Networking.Adapters
{
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(PlayerControllerCore))]
    public class ClickToMoveAgent : MonoBehaviour
        {
        [Header("Camera input")]
        public Camera cam;
        public int clickButton = 1; // 0=LMB,1=RMB
    
        [Header("NavMesh sampling")]
        public float maxSampleDist = 12f;
    
        [Header("Anti-spam click")]
        public bool enableDebounce = true;
        public float clickCooldown = 0.06f;
        public bool enableMinRepathDistance = true;
        public float minRepathDistance = 0.25f;
    
        [Header("UI blocking")]
        public string uiLayerName = "UI";
        public bool blockUIClicks = true;
        public bool allowBypassKey = true;
        public KeyCode bypassKey = KeyCode.LeftAlt;
    
        [Header("Networking / Local Input")]
        public bool allowServerOnlyInput = true;
    
        private NavMeshAgent _agent;
        private PlayerControllerCore _ctrl;
        private IPlayerNetworkDriver _driver;
    
        private float _nextClick;
        private bool _hasLastGoal;
        private Vector3 _lastGoal;
    
        private int _uiLayer = -1;
        private static readonly List<RaycastResult> _uiHits = new List<RaycastResult>();
    
        private bool _uiConsumeUntilUp;
    
        public bool HasPath => _agent && _agent.hasPath && !_agent.isStopped;
    
        void Awake()
        {
            if (!cam)
                cam = Camera.main;
    
            _agent = GetComponent<NavMeshAgent>();
            _ctrl = GetComponent<PlayerControllerCore>();
            CacheNetworkDriver();
    
            // Il NavMeshAgent è usato solo per pathfinding/steering.
            // Il movimento reale è gestito da PlayerControllerCore / Rigidbody.
            _agent.updatePosition = false;
            _agent.updateRotation = false;
            _agent.autoRepath = true;
            _agent.autoBraking = true;
            _agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            _agent.avoidancePriority = 50;
            _agent.stoppingDistance = Mathf.Max(0.15f, _agent.stoppingDistance);
        }
    
        void OnEnable()
        {
            if (_driver == null)
                CacheNetworkDriver();
        }
    
        void LateUpdate()
        {
            if (!cam)
                cam = Camera.main;
    
            if (_uiLayer == -1 && !string.IsNullOrEmpty(uiLayerName))
                _uiLayer = LayerMask.NameToLayer(uiLayerName);
        }
    
        void CacheNetworkDriver()
        {
            _driver = null;
            var behaviours = GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is IPlayerNetworkDriver drv)
                {
                    _driver = drv;
                    break;
                }
            }
        }
    
        bool HasLocalAuthorityForInput()
        {
            if (_driver != null)
                return _driver.HasInputAuthority(allowServerOnlyInput);
    
            // Fallback: se non c'è driver, non blocchiamo l’input.
            return true;
        }
    
        void Update()
        {
            if (!HasLocalAuthorityForInput())
                return;
            if (!cam)
                return;
    
            // Se il precedente click è stato consumato dalla UI, sblocca al rilascio
            if (_uiConsumeUntilUp && Input.GetMouseButtonUp(clickButton))
                _uiConsumeUntilUp = false;
    
            bool bypass = allowBypassKey && Input.GetKey(bypassKey);
    
            if (Input.GetMouseButtonDown(clickButton))
            {
                // Se in stato consume-UI e non bypasso, ignora
                if (_uiConsumeUntilUp && !bypass)
                    return;
    
                // Blocca click su UI se non bypasso
                if (!bypass && blockUIClicks && IsPointerOverUILayer())
                {
                    _uiConsumeUntilUp = true;
                    return;
                }
    
                // Debounce anti-spam
                if (enableDebounce && Time.time < _nextClick)
                    return;
                _nextClick = Time.time + clickCooldown;
    
                // Raycast da camera
                Ray r = cam.ScreenPointToRay(Input.mousePosition);
                if (!Physics.Raycast(r, out RaycastHit hit, 5000f, ~0, QueryTriggerInteraction.Ignore))
                    return;
    
                // Proietta sul NavMesh
                if (!NavMesh.SamplePosition(hit.point, out NavMeshHit nh, maxSampleDist, NavMesh.AllAreas))
                    return;
    
                // Evita micro-spostamenti inutili
                if (enableMinRepathDistance && _hasLastGoal &&
                    Vector3.Distance(nh.position, _lastGoal) < minRepathDistance)
                    return;
    
                // Evita click quasi sotto i piedi
                if (Vector3.Distance(transform.position, nh.position) <= _agent.stoppingDistance + 0.05f)
                    return;
    
                // Imposta path
                _agent.isStopped = false;
                _agent.ResetPath();
                _agent.SetDestination(nh.position);
    
                _lastGoal = nh.position;
                _hasLastGoal = true;
            }
        }
    
        bool IsPointerOverUILayer()
        {
            if (!blockUIClicks)
                return false;
            if (EventSystem.current == null)
                return false;
            if (_uiLayer == -1)
                return false;
    
            var ped = new PointerEventData(EventSystem.current)
            {
                position = Input.mousePosition
            };
    
            _uiHits.Clear();
            EventSystem.current.RaycastAll(ped, _uiHits);
    
            for (int i = 0; i < _uiHits.Count; i++)
            {
                var go = _uiHits[i].gameObject;
                if (!go)
                    continue;
    
                Transform t = go.transform;
                while (t != null)
                {
                    if (t.gameObject.layer == _uiLayer)
                        return true;
    
                    t = t.parent;
                }
            }
    
            return false;
        }
    
        // --- API usate da PlayerControllerCore / server ---
    
        public void CancelPath()
        {
            if (!_agent)
                return;
    
            _agent.isStopped = true;
            if (_agent.hasPath)
                _agent.ResetPath();
    
            _hasLastGoal = false;
            _agent.nextPosition = transform.position;
        }
    
        public Vector3 GetDesiredVelocity()
        {
            return _agent ? _agent.desiredVelocity : Vector3.zero;
        }
    
        public float RemainingDistance()
        {
            return _agent ? _agent.remainingDistance : Mathf.Infinity;
        }
    
        public float StoppingDistance => _agent ? _agent.stoppingDistance : 0.15f;
    
        public Vector3 SteeringTarget()
        {
            return _agent ? _agent.steeringTarget : transform.position;
        }
    
        public void SyncAgentToTransform()
        {
            if (_agent)
                _agent.nextPosition = transform.position;
        }
    
        public Vector3[] GetPathCorners()
        {
            if (!_agent || !_agent.hasPath)
                return System.Array.Empty<Vector3>();
    
            var p = _agent.path;
            if (p == null)
                return System.Array.Empty<Vector3>();
    
            return p.corners;
        }
    }
}
