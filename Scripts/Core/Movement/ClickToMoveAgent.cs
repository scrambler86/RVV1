using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using FishNet.Object;
using FishNet;
using FishNet.Connection;

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
    private NetworkObject _nobj;

    private float _nextClick;
    private bool _hasLastGoal;
    private Vector3 _lastGoal;

    private int _uiLayer = -1;
    private static readonly List<RaycastResult> _uiHits = new List<RaycastResult>();

    private bool _uiConsumeUntilUp;

    public bool HasPath => _agent && _agent.hasPath && !_agent.isStopped;

    void Awake()
    {
        if (!cam) cam = Camera.main;
        _agent = GetComponent<NavMeshAgent>();
        _ctrl = GetComponent<PlayerControllerCore>();
        _nobj = GetComponent<NetworkObject>();

        // Important: NavMeshAgent is ONLY used for pathfinding/steering,
        // movement is actually applied via Rigidbody in PlayerControllerCore.
        _agent.updatePosition = false;
        _agent.updateRotation = false;
        _agent.autoRepath = true;
        _agent.autoBraking = true;
        _agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        _agent.avoidancePriority = 50;
        _agent.stoppingDistance = Mathf.Max(0.15f, _agent.stoppingDistance);
    }

    void LateUpdate()
    {
        if (!cam) cam = Camera.main;
        if (_uiLayer == -1 && !string.IsNullOrEmpty(uiLayerName))
            _uiLayer = LayerMask.NameToLayer(uiLayerName);
    }

    bool HasLocalAuthorityForInput()
    {
        // Owner può sempre comandare.
        // Sul server possiamo opzionalmente permettere input (per debugging host).
        if (_nobj == null)
            return true;
        if (!_nobj.IsSpawned)
            return true;
        if (_nobj.IsOwner)
            return true;
        if (allowServerOnlyInput && _nobj.IsServerInitialized)
            return true;
        return false;
    }

    void Update()
    {
        if (!HasLocalAuthorityForInput())
            return;
        if (!cam)
            return;

        // se abbiamo bloccato il click perché era su UI, sblocchiamo quando rilascio
        if (_uiConsumeUntilUp && Input.GetMouseButtonUp(clickButton))
            _uiConsumeUntilUp = false;

        bool bypass = allowBypassKey && Input.GetKey(bypassKey);

        if (Input.GetMouseButtonDown(clickButton))
        {
            // Se eravamo in consumo-UI, e non sto bypassando, non fare nulla
            if (_uiConsumeUntilUp && !bypass)
                return;

            // Non permettere click su UI (es: inventory) se non tengo ALT
            if (!bypass && blockUIClicks && IsPointerOverUILayer())
            {
                _uiConsumeUntilUp = true;
                return;
            }

            // debounce: click troppo ravvicinato ignorato
            if (enableDebounce && Time.time < _nextClick)
                return;
            _nextClick = Time.time + clickCooldown;

            // Ray dalla camera verso il mondo
            Ray r = cam.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(r, out RaycastHit hit, 5000f, ~0, QueryTriggerInteraction.Ignore))
                return;

            // Proiettiamo il punto sul NavMesh
            if (!NavMesh.SamplePosition(hit.point, out NavMeshHit nh, maxSampleDist, NavMesh.AllAreas))
                return;

            // evita spam se il nuovo punto è praticamente identico al vecchio
            if (enableMinRepathDistance && _hasLastGoal &&
                Vector3.Distance(nh.position, _lastGoal) < minRepathDistance)
                return;

            // se clicco praticamente sotto i piedi, inutile
            if (Vector3.Distance(transform.position, nh.position) <= _agent.stoppingDistance + 0.05f)
                return;

            // imposta la nuova path
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

    // --- Queste funzioni sono IMPORTANTI per PlayerControllerCore e per il server ---

    public void CancelPath()
    {
        if (!_agent)
            return;
        _agent.isStopped = true;
        if (_agent.hasPath)
            _agent.ResetPath();
        _hasLastGoal = false;
        // riallinea l'agent alla nostra posizione attuale
        _agent.nextPosition = transform.position;
        // ATTENZIONE: NON chiamare _ctrl.StopMovement() qua per non creare loop
    }

    public Vector3 GetDesiredVelocity()
    {
        return _agent ? _agent.desiredVelocity : Vector3.zero;
    }

    public float RemainingDistance()
    {
        return _agent ? _agent.remainingDistance : Mathf.Infinity;
    }

    public float StoppingDistance()
    {
        return _agent ? _agent.stoppingDistance : 0.15f;
    }

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
