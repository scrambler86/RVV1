using UnityEngine;
using UnityEngine.UI;

public class MovementDebugOverlay : MonoBehaviour
{
    public PlayerControllerCore _player;
    public Text debugText;

    void Update()
    {
        if (!_player || !debugText) return;

        string info = $"Speed: {_player.DebugPlanarSpeed:F2}\n" +
                      $"Running: {_player.IsRunning}\n" +  // CORRETTO: IsRunning al posto di IsRunningToggle
                      $"Input Allowed: {_player.AllowInput}\n" +
                      $"Move Dir: {_player.DebugLastMoveDir}";

        debugText.text = info;
    }
}
