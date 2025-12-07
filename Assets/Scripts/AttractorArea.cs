using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// AttractorArea: attach to a GameObject with a Collider (Box/Mesh/Polygon).
/// Provides SamplePointOnNavMesh() to return a Vector3 within the area that lies on NavMesh.
/// </summary>
[RequireComponent(typeof(Collider))]
public class AttractorArea : MonoBehaviour
{
    public Collider areaCollider;             // assign BoxCollider / MeshCollider (convex ideally)
    public float sampleRadius = 0.5f;
    public int attempts = 8;
    public float[] verticalOffsets = new float[]{2f, 1f, 0.5f, 0f, -1f};

    void Reset() { areaCollider = GetComponent<Collider>(); }

    /// <summary>
    /// Sample a NavMesh position within the collider bounds. Returns Vector3.zero if none found.
    /// </summary>
    public Vector3 SamplePointOnNavMesh()
    {
        if (areaCollider == null) return Vector3.zero;
        Bounds b = areaCollider.bounds;

        for (int i = 0; i < Mathf.Max(1, attempts); i++)
        {
            // uniform sample in bounds XZ
            float x = Random.Range(b.min.x, b.max.x);
            float z = Random.Range(b.min.z, b.max.z);
            Vector3 sample = new Vector3(x, b.center.y, z);

            // project to collider (ensures point is inside/nearest)
            Vector3 closest = areaCollider.ClosestPoint(sample);

            // try sampling NavMesh near this point (try several vertical offsets)
            foreach (float vo in verticalOffsets)
            {
                Vector3 probe = new Vector3(closest.x, closest.y + vo, closest.z);
                NavMeshHit hit;
                if (NavMesh.SamplePosition(probe, out hit, sampleRadius + Mathf.Abs(vo), NavMesh.AllAreas))
                {
                    return hit.position;
                }
            }
        }
        return Vector3.zero;
    }
}
