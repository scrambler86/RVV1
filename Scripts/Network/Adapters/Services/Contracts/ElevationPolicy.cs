using System;
using UnityEngine;

namespace Game.Networking.Adapters
{
    public enum ElevationPolicyMode
    {
        PreserveNetwork,
        GroundSnap
    }

    public interface IElevationPolicy
    {
        Vector3 ResolveServer(Vector3 integratedPosition,
                              Vector3 predictedPosition,
                              Func<Vector3, float> groundSampler,
                              ElevationPolicyMode mode,
                              bool hasVerticalIntent);

        Vector3 ResolveClient(Vector3 authoritativePosition,
                              Vector3 predictedPosition,
                              Func<Vector3, float> groundSampler,
                              ElevationPolicyMode mode,
                              bool hasVerticalIntent);
    }

    public static class ElevationPolicies
    {
        sealed class NoOpElevationPolicy : IElevationPolicy
        {
            public Vector3 ResolveServer(Vector3 integratedPosition, Vector3 predictedPosition,
                                         Func<Vector3, float> groundSampler, ElevationPolicyMode mode,
                                         bool hasVerticalIntent)
                => integratedPosition;

            public Vector3 ResolveClient(Vector3 authoritativePosition, Vector3 predictedPosition,
                                         Func<Vector3, float> groundSampler, ElevationPolicyMode mode,
                                         bool hasVerticalIntent)
                => authoritativePosition;
        }

        sealed class GroundSnapElevationPolicy : IElevationPolicy
        {
            readonly NoOpElevationPolicy _fallback = new();

            public Vector3 ResolveServer(Vector3 integratedPosition, Vector3 predictedPosition,
                                         Func<Vector3, float> groundSampler, ElevationPolicyMode mode,
                                         bool hasVerticalIntent)
            {
                if (mode == ElevationPolicyMode.PreserveNetwork)
                    return integratedPosition;

                if (groundSampler == null)
                    return integratedPosition;

                if (hasVerticalIntent)
                    return integratedPosition;

                float y = groundSampler(integratedPosition);
                integratedPosition.y = y;
                return integratedPosition;
            }

            public Vector3 ResolveClient(Vector3 authoritativePosition, Vector3 predictedPosition,
                                         Func<Vector3, float> groundSampler, ElevationPolicyMode mode,
                                         bool hasVerticalIntent)
            {
                if (mode == ElevationPolicyMode.PreserveNetwork || groundSampler == null)
                    return authoritativePosition;

                if (hasVerticalIntent)
                    return authoritativePosition;

                float predictedY = groundSampler(predictedPosition);
                float authoritativeY = groundSampler(authoritativePosition);

                // Blend slightly towards authoritative while keeping grounded sample.
                float blended = Mathf.Lerp(predictedY, authoritativeY, 0.65f);
                authoritativePosition.y = blended;
                return authoritativePosition;
            }
        }

        public static IElevationPolicy None { get; } = new NoOpElevationPolicy();
        public static IElevationPolicy FlatGround { get; } = new GroundSnapElevationPolicy();
    }
}