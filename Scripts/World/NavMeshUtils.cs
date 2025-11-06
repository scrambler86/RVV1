using UnityEngine;
using UnityEngine.AI;

namespace World
{
    public static class NavMeshUtils
    {
        public static bool RandomPointOnNavMesh(Vector3 center, float radius, out Vector3 result, int maxTries = 24)
        {
            for (int i = 0; i < maxTries; i++)
            {
                var rand = Random.insideUnitSphere;
                rand.y = 0f;
                var pos = center + rand.normalized * Random.Range(0.1f, radius);
                if (NavMesh.SamplePosition(pos, out var hit, 3f, NavMesh.AllAreas))
                {
                    result = hit.position;
                    return true;
                }
            }
            result = center;
            return false;
        }
    }
}
