using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(NavMeshAgent))]
public class PlayerControllerCore : MonoBehaviour
{
    [Header("Movimento base")]
    public float speed = 3.5f;
    public float runMultiplier = 2f;

    [Header("NavMesh & Ground")]
    [Range(0, 89)] public float maxSlope = 60f;
    public LayerMask walkableMask = ~0;
    public Terrain terrain;
    public float groundProbeExtra = 0.6f;

    [Header("Collider/Altezza")]
    public float colliderHeight = 2f;

    [Header("Camera di riferimento per WASD")]
    public Camera mainCamera;

    [Header("Visual Root (mesh child)")]
    public Transform visualRoot;
    public string visualChildName = "Char_01";
    public bool forceUprightVisual = true;

    [Header("Animator params")]
    public string animSpeedParam = "Speed";
    public string animRunBoolParam = "Run";

    [Header("WASD NavMesh Constraint")]
    public bool constrainWASDToNavMesh = true;
    public float navClampMaxDistance = 0.8f;

    [Header("State / Gating")]
    public bool canMove = true;
    public bool useDeterministicPlanar = false; // hook futuro

    Rigidbody _rb;
    Collider _col;
    NavMeshAgent _agent;
    ClickToMoveAgent _ctm;
    Animator _anim;

    bool _hasAnimSpeed, _hasAnimRun;

    bool _runToggle;
    bool _allowInput = true;
    Vector3 _lastMoveDir;
    float _lastPlanarSpeed;
    RaycastHit[] _groundBuffer;

    // evita di ruotare il root (che trascina la camera) se non abbiamo un visual child separato
    bool _canRotateVisualRoot = true;

    public bool IsRunning => _runToggle;
    public Vector3 DebugLastMoveDir => _lastMoveDir;
    public Transform VisualRoot => visualRoot;
    public float DebugPlanarSpeed => _lastPlanarSpeed;
    public bool AllowInput => _allowInput;
    public bool CanMoveState => canMove;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _col = GetComponent<Collider>();
        _agent = GetComponent<NavMeshAgent>();
        _ctm = GetComponent<ClickToMoveAgent>();
        _anim = GetComponentInChildren<Animator>();

        if (!mainCamera) mainCamera = Camera.main;
        if (!terrain) terrain = Terrain.activeTerrain;

        if (_anim) _anim.applyRootMotion = false;
        CacheAnimParams();

        _rb.isKinematic = false;
        _rb.detectCollisions = true;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        _rb.constraints = RigidbodyConstraints.FreezeRotationX |
                          RigidbodyConstraints.FreezeRotationY |
                          RigidbodyConstraints.FreezeRotationZ;

        if (_agent != null)
        {
            _agent.updatePosition = false;
            _agent.updateRotation = false;
            _agent.autoRepath = true;
            _agent.autoBraking = true;
            _agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            _agent.avoidancePriority = 50;
            _agent.stoppingDistance = Mathf.Max(0.15f, _agent.stoppingDistance);
            _agent.baseOffset = HalfHeight();
        }

        if (!visualRoot && !string.IsNullOrWhiteSpace(visualChildName))
        {
            Transform t = transform.Find(visualChildName);
            if (t) visualRoot = t;
        }
        if (!visualRoot)
            visualRoot = transform;

        // <-- questa è la protezione anti-"mi gira tutta la mappa"
        _canRotateVisualRoot = (visualRoot != transform);

        Vector3 p = transform.position;
        p = SnapToGround(p);
        _rb.position = p;
        if (_agent)
        {
            _agent.Warp(p);
            _agent.nextPosition = p;
        }

        _lastMoveDir = Vector3.zero;
        _lastPlanarSpeed = 0f;
    }

    void Update()
    {
        if (!mainCamera) mainCamera = Camera.main;
        if (!terrain) terrain = Terrain.activeTerrain;

        // shift toggle run ON/OFF (tua logica originale)
        if (_allowInput && (Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift)))
            _runToggle = !_runToggle;
    }

    void FixedUpdate()
    {
        if (!mainCamera) mainCamera = Camera.main;
        if (!terrain) terrain = Terrain.activeTerrain;

        if (_agent) _agent.nextPosition = _rb.position;

        if (!_allowInput || !canMove)
        {
            IdleAnim();
            return;
        }

        _ctm?.SyncAgentToTransform();

        float hz = Input.GetAxisRaw("Horizontal");
        float vt = Input.GetAxisRaw("Vertical");
        if (Mathf.Abs(hz) < 0.2f) hz = 0f;
        if (Mathf.Abs(vt) < 0.2f) vt = 0f;
        Vector3 inputRaw = new Vector3(hz, 0f, vt);
        if (inputRaw.sqrMagnitude > 1f)
            inputRaw.Normalize();

        bool wasdPressed =
            Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) ||
            Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D) ||
            Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.LeftArrow) ||
            Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.RightArrow);

        if (wasdPressed && _ctm != null && _ctm.HasPath)
            _ctm.CancelPath();

        bool usingCTM = (_ctm != null && _ctm.HasPath);

        float walkMax = speed;
        float runMax = speed * runMultiplier;
        float curSpeed = _runToggle ? runMax : walkMax;

        if (_agent != null)
        {
            _agent.speed = curSpeed;
            _agent.acceleration = _runToggle ? (runMax * 3f) : (walkMax * 4f);
            _agent.angularSpeed = _runToggle ? 360f : 240f;
        }

        Vector3 desiredDir = Vector3.zero;
        if (usingCTM)
        {
            Vector3 dv = _ctm.GetDesiredVelocity(); dv.y = 0f;
            if (dv.sqrMagnitude > 0.0001f)
                desiredDir = dv.normalized;

            if (_ctm.RemainingDistance() <= Mathf.Max(_ctm.StoppingDistance(), 0.15f))
                _ctm.CancelPath();
        }
        else if (inputRaw.sqrMagnitude > 0.01f)
        {
            desiredDir = inputRaw;
            if (mainCamera)
            {
                desiredDir = (Quaternion.Euler(0f, mainCamera.transform.eulerAngles.y, 0f) * desiredDir).normalized;
            }
        }

        if (desiredDir.sqrMagnitude < 0.0001f)
        {
            _lastMoveDir = Vector3.zero;
            _lastPlanarSpeed = 0f;
            IdleAnim();
            return;
        }

        _lastMoveDir = desiredDir;

        Vector3 step = desiredDir * curSpeed * Time.fixedDeltaTime;
        Vector3 target = _rb.position + new Vector3(step.x, 0f, step.z);
        target = SnapToGround(target);

        if (constrainWASDToNavMesh && !usingCTM)
        {
            if (NavMesh.Raycast(_rb.position, target, out var rh, NavMesh.AllAreas))
            {
                target = rh.position;
            }
            else if (NavMesh.SamplePosition(target, out var nh, navClampMaxDistance, NavMesh.AllAreas))
            {
                target = nh.position;
            }
            else
            {
                target = _rb.position;
            }
        }

        float planarSpeed = (target - _rb.position).magnitude / Time.fixedDeltaTime;
        _lastPlanarSpeed = planarSpeed;
        float animSpeed = Mathf.Min(planarSpeed, curSpeed);

        _rb.MovePosition(target);
        if (_agent) _agent.nextPosition = target;

        if (_canRotateVisualRoot && visualRoot)
        {
            Quaternion q = Quaternion.LookRotation(desiredDir);
            visualRoot.rotation = Quaternion.Slerp(
                visualRoot.rotation,
                q,
                Time.fixedDeltaTime * 12f
            );
            if (forceUprightVisual)
            {
                visualRoot.rotation = Quaternion.Euler(
                    0f,
                    visualRoot.eulerAngles.y,
                    0f
                );
            }
        }

        SafeAnimSpeedRaw(animSpeed);
        SafeAnimRun(_runToggle);
    }

    float HalfHeight() => Mathf.Max(0.9f, colliderHeight * 0.5f);

    public float SampleGroundY(Vector3 xzWorld)
    {
        float half = HalfHeight();
        Vector3 p = new Vector3(xzWorld.x, xzWorld.y, xzWorld.z);
        if (TryGetGround(p, out var gh))
            return gh.point.y + half;
        if (terrain)
            return terrain.SampleHeight(p) + terrain.transform.position.y + half;
        return p.y;
    }

    Vector3 SnapToGround(Vector3 p)
    {
        float half = HalfHeight();
        float targetY;

        if (TryGetGround(p, out var gh))
            targetY = gh.point.y + half;
        else if (terrain)
            targetY = terrain.SampleHeight(p) + terrain.transform.position.y + half;
        else
            return p;

        if (Mathf.Abs(targetY - p.y) < 0.01f)
            return p;

        p.y = targetY;
        return p;
    }

    bool TryGetGround(Vector3 worldPos, out RaycastHit bestHit)
    {
        float half = HalfHeight();
        Vector3 origin = worldPos + Vector3.up * (half + groundProbeExtra);

        RaycastHit[] hits = _groundBuffer ??= new RaycastHit[6];
        int count = Physics.RaycastNonAlloc(
            origin,
            Vector3.down,
            hits,
            half + groundProbeExtra + 2f,
            walkableMask,
            QueryTriggerInteraction.Ignore
        );

        bestHit = default;
        float bestY = float.NegativeInfinity;
        float maxY = worldPos.y + 0.05f;

        for (int i = 0; i < count; i++)
        {
            var h = hits[i];
            if (!h.collider || h.collider.transform.IsChildOf(transform))
                continue;
            if (h.point.y > maxY)
                continue;

            if (h.point.y > bestY)
            {
                bestY = h.point.y;
                bestHit = h;
            }
        }

        return bestY > float.NegativeInfinity;
    }

    public void StopMovement()
    {
        _lastMoveDir = Vector3.zero;
        _lastPlanarSpeed = 0f;
        IdleAnim();

        if (_ctm != null)
        {
            _ctm.CancelPath();
            if (_agent) _agent.nextPosition = _rb.position;
        }
    }

    public void SetAllowInput(bool allow)
    {
        _allowInput = allow;
        if (!allow)
            StopMovement();
    }

    public void SetCanMove(bool can)
    {
        canMove = can;
        if (!can)
            StopMovement();
    }

    void IdleAnim()
    {
        SafeAnimSpeedRaw(0f);
        SafeAnimRun(false);
    }

    void CacheAnimParams()
    {
        _hasAnimSpeed = _hasAnimRun = false;
        if (_anim == null) return;

        foreach (var p in _anim.parameters)
        {
            if (!string.IsNullOrEmpty(animSpeedParam) &&
                p.type == AnimatorControllerParameterType.Float &&
                p.name == animSpeedParam)
                _hasAnimSpeed = true;

            if (!string.IsNullOrEmpty(animRunBoolParam) &&
                p.type == AnimatorControllerParameterType.Bool &&
                p.name == animRunBoolParam)
                _hasAnimRun = true;
        }
    }

    public void SafeAnimSpeedRaw(float v)
    {
        if (_anim != null && _hasAnimSpeed)
            _anim.SetFloat(animSpeedParam, v);
    }

    public void SafeAnimRun(bool v)
    {
        if (_anim != null && _hasAnimRun)
            _anim.SetBool(animRunBoolParam, v);
    }
}
