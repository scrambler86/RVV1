using System.Collections.Generic;
using UnityEngine;

namespace Game.Networking.Adapters
{
    internal sealed class PlayerDriverRemoteState
    {
        public readonly List<MovementSnapshot> Buffer = new(256);
        public double EmaDelay;
        public double EmaJitter;
        public double Back;
        public double BackTarget;
        public Vector3 LastRenderPos;
        public float DisplaySpeed;
    }
<<<<<<< HEAD
}
=======
}
>>>>>>> d20e4fd9b22d900ba7ed7950a8f75d0ef03392d0
