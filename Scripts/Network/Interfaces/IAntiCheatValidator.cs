using UnityEngine;

public interface IAntiCheatValidator
{
    bool ValidateInput(IPlayerNetworkDriver drv, uint seq, double clientTimestamp,
                       Vector3 predictedPos, Vector3 lastServerPos,
                       float maxStepWithSlack, Vector3[] pathCorners, bool runningFlag);
}
