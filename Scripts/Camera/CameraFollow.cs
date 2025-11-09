using UnityEngine;

[RequireComponent(typeof(Camera))]
public class CameraFollow : MonoBehaviour
{
    [Header("Target")]
    public Transform target;
    public float lookUp = 1.2f;

    [Header("Inquadratura isometrica")]
    [Range(30f, 60f)] public float pitch = 32f;
    public float distance = 35f;
    public float minDistance = 20f;
    public float maxDistance = 40f;
    public float zoomSpeed = 10f;
    public float heightOffset = 0.5f;

    [Header("Rotazione Fissa Manuale")]
    public float isometricOffset = 45f;
    public float manualRotationLimit = 190f;
    public float yawSmoothTime = 0.25f;
    public float moveSmoothTime = 0.65f;
    public float manualInputDelay = 0.1f;

    [Header("Anti-jitter")]
    public bool smoothTargetPivot = true;
    public float targetPivotSmooth = 0.08f;
    public float lookAtSmooth = 0.06f;

    Camera _cam;
    Vector3 _posVel;
    float _yaw, _yawVel;
    float _manualYawTimer;
    float _targetYawOffset;
    Vector3 _pivotSmoothed, _pivotVel;
    Vector3 _lookSmoothed, _lookVel;

    void Awake()
    {
        _cam = GetComponent<Camera>();
        _cam.orthographic = false;
        _cam.fieldOfView = 32f;
        _cam.nearClipPlane = 0.05f;
        _cam.farClipPlane = 800f;

        _yaw = isometricOffset;
        if (target) { _pivotSmoothed = target.position; _lookSmoothed = target.position; }
        SnapToTarget();
    }

    void LateUpdate()
    {
        if (!target) return;

        float s = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(s) > 1e-4f)
            distance = Mathf.Clamp(distance - s * zoomSpeed, minDistance, maxDistance);
        if (Input.GetMouseButtonDown(2))
            distance = Mathf.Clamp(26f, minDistance, maxDistance);

        float height = Mathf.Tan(Mathf.Deg2Rad * Mathf.Clamp(pitch, 1f, 89f)) * distance;

        Vector3 planarTargetDir = new Vector3(target.forward.x, 0f, target.forward.z).normalized;
        float targetYaw = Mathf.Atan2(planarTargetDir.x, planarTargetDir.z) * Mathf.Rad2Deg + isometricOffset;

        if (Input.GetKeyDown(KeyCode.Q))
        {
            _targetYawOffset = (Mathf.Abs(_targetYawOffset - manualRotationLimit) < 0.1f) ? 0f : manualRotationLimit;
            _manualYawTimer = manualInputDelay;
        }
        if (_manualYawTimer > 0f) _manualYawTimer -= Time.deltaTime;

        float desiredYaw = targetYaw + _targetYawOffset;
        _yaw = Mathf.SmoothDampAngle(_yaw, desiredYaw, ref _yawVel, yawSmoothTime);

        Vector3 rawPivot = target.position;
        _pivotSmoothed = smoothTargetPivot
            ? Vector3.SmoothDamp(_pivotSmoothed, rawPivot, ref _pivotVel, targetPivotSmooth)
            : rawPivot;

        Quaternion rot = Quaternion.Euler(pitch, _yaw, 0f);
        Vector3 back = rot * Vector3.back;

        Vector3 pivot = _pivotSmoothed + Vector3.up * heightOffset;
        Vector3 wantedPos = pivot + back * distance + Vector3.up * height;
        Vector3 pos = Vector3.SmoothDamp(transform.position, wantedPos, ref _posVel, moveSmoothTime);
        transform.position = pos;

        Vector3 rawLook = _pivotSmoothed + Vector3.up * (heightOffset + lookUp);
        _lookSmoothed = Vector3.SmoothDamp(_lookSmoothed, rawLook, ref _lookVel, lookAtSmooth);
        transform.LookAt(_lookSmoothed, Vector3.up);
    }

    public void SnapToTarget()
    {
        if (!target) return;

        float initialYaw = Mathf.Atan2(target.forward.x, target.forward.z) * Mathf.Rad2Deg + isometricOffset;
        _yaw = initialYaw;
        _targetYawOffset = 0f;

        float height = Mathf.Tan(Mathf.Deg2Rad * Mathf.Clamp(pitch, 1f, 89f)) * distance;
        Quaternion rot = Quaternion.Euler(pitch, _yaw, 0f);
        Vector3 back = rot * Vector3.back;

        _pivotSmoothed = target.position;
        Vector3 pivot = _pivotSmoothed + Vector3.up * heightOffset;
        Vector3 camPos = pivot + back * distance + Vector3.up * height;

        transform.SetPositionAndRotation(
            camPos,
            Quaternion.LookRotation((pivot + Vector3.up * lookUp) - camPos, Vector3.up)
        );

        _posVel = Vector3.zero; _yawVel = 0f;
        _lookSmoothed = pivot + Vector3.up * lookUp;
        _pivotVel = Vector3.zero; _lookVel = Vector3.zero;
    }
}