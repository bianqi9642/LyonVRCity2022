// TargetPreviewOverride.cs
//
// OPTIONAL. Add to a Phase 3 candidate building only if the automatic framing in
// TargetBuildingPreviewGenerator produces a bad preview for that specific building (bad angle,
// cropped, or its renderer bounds include stray far-away child geometry).
//
// TargetBuildingPreviewGenerator finds this automatically via GetComponentInChildren. The generator and
// the experiment work fine with NO instances of this component anywhere.
//
// All comments in English per project convention.

using UnityEngine;

[DisallowMultipleComponent]
public class TargetPreviewOverride : MonoBehaviour
{
    [Header("View direction (FROM building TO camera, world space)")]
    [Tooltip("Leave at (0,0,0) to use the generator default; otherwise this exact direction is used.")]
    public Vector3 viewDirectionOverride = Vector3.zero;

    [Header("Framing")]
    [Tooltip("0 = generator default. Otherwise multiplies the generator's framing padding for this building.")]
    public float paddingMultiplier = 0f;

    [Tooltip("0 = generator default field of view (perspective mode only).")]
    public float fovOverride = 0f;

    [Header("Background")]
    public bool useBackgroundOverride = false;
    public Color backgroundOverride = new Color(0.85f, 0.85f, 0.86f, 1f);

    [Header("Manual bounds (local space of THIS GameObject)")]
    [Tooltip("If ticked, the generator ignores renderer bounds and frames this box instead. " +
             "Use when the building has stray or oversized child renderers.")]
    public bool useManualBounds = false;
    public Vector3 manualLocalCenter = Vector3.zero;
    public Vector3 manualLocalSize = new Vector3(20f, 20f, 20f);

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!useManualBounds) return;
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.7f);
        Matrix4x4 old = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(manualLocalCenter, manualLocalSize);
        Gizmos.matrix = old;
    }
#endif
}