using UnityEngine;
public interface IChunkManager
{
    void RegisterPlayer(IPlayerNetworkDriver drv);
    void UnregisterPlayer(IPlayerNetworkDriver drv);
    void UpdatePlayerChunk(IPlayerNetworkDriver drv, Vector3 worldPos);
}
