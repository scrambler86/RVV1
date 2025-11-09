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
}