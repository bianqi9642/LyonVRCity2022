using UnityEngine;

/// <summary>
/// Automatically aligns the XR Origin to the ground using a downward Raycast.
/// This prevents the player from floating in the air on uneven terrain,
/// and makes normal movement naturally follow the ground height.
/// </summary>
public class AutoGroundAlign : MonoBehaviour
{
    [Header("Raycast Settings")]
    public float raycastDistance = 3f;           // Max distance to check downward
    public LayerMask groundLayer;                // Layers considered as ground (set in Inspector)
    public float smoothSpeed = 10f;              // Smooth interpolation for vertical movement

    [Header("Offset")]
    public float heightOffset = 0.0f;            // Offset from ground (if needed)

    private Transform xrOrigin;

    void Start()
    {
        xrOrigin = this.transform;
    }

    void Update()
    {
        AlignToGround();
    }

    /// <summary>
    /// Casts a ray downward from the XR Origin and smoothly adjusts height.
    /// </summary>
    private void AlignToGround()
    {
        Ray ray = new Ray(xrOrigin.position + Vector3.up * 0.1f, Vector3.down);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, raycastDistance, groundLayer))
        {
            float targetY = hit.point.y + heightOffset;

            // Smoothly move to the ground height
            Vector3 newPosition = new Vector3(
                xrOrigin.position.x,
                Mathf.Lerp(xrOrigin.position.y, targetY, Time.deltaTime * smoothSpeed),
                xrOrigin.position.z
            );

            xrOrigin.position = newPosition;
        }
    }

    private void OnDrawGizmosSelected()
    {
        // Gizmo to visualize the raycast
        Gizmos.color = Color.yellow;
        Vector3 startPos = transform.position + Vector3.up * 0.1f;
        Gizmos.DrawLine(startPos, startPos + Vector3.down * raycastDistance);
    }
}
