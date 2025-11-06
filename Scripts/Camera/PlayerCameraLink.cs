using UnityEngine;
using FishNet.Object;

public class PlayerCameraLink : NetworkBehaviour
{
    public override void OnStartClient()
    {
        base.OnStartClient();
        if (!IsOwner) return;

        var cam = Camera.main;
        if (!cam) return;

        var follow = cam.GetComponent<CameraFollow>();
        if (!follow) follow = cam.gameObject.AddComponent<CameraFollow>();

        follow.target = transform;
        follow.SnapToTarget();
        follow.enabled = true;
    }
}