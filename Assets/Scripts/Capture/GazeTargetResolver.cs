// GazeTargetResolver.cs
// Hit-priority / occlusion policy that turns the two INDEPENDENT raw gaze results (the physical Physics.Raycast hit and the Canvas UI hit from GazeUiRaycaster) into ONE explicit primary-target
// classification for gaze_ui_hits.csv. Both raw results are always recorded unchanged next to it; this class never edits either of them.
//
// Both rays are the same ray (same origin, direction and maximum distance), so the two hit distances are directly comparable.
//
//   no UI evaluated (head-fallback ray / UI path off)  -> WorldObject if the physical ray hit, else NoHit;           basis ui_not_evaluated
//   neither hit                                          -> NoHit;                                                    basis no_hit
//   UI hit only                                          -> CanvasUI;                                                 basis ui_only
//   physical hit only                                    -> WorldObject;                                              basis world_only
//   both hit, UI at or in front of the world hit         -> CanvasUI  (a visible Graphic precedes the world surface;  basis ui_in_front
//                                                           visibility was established by GazeUiRaycaster: alpha, culling, camera layer)
//   both hit, world hit in front of the UI               -> the physical hit is an ESTABLISHED occluder (non-trigger collider with an enabled, opaque, visible Renderer or a TerrainCollider)
//                                                           -> WorldObject; basis world_occludes_ui   (UI behind an opaque object never wins)
//                                                           -> otherwise (trigger volume, collider without a visible opaque renderer, transparent material): the occlusion cannot be established
//                                                              reliably -> Uncertain; basis occlusion_not_established. Both raw channels stay in the row.
//
// Limitation (documented): an object without ANY collider (no physical hit) that stands in front of a UI element cannot be seen by either ray; the UI hit then wins as ui_only.
// All comments in English per project convention.

using UnityEngine;

public static class GazeTargetResolver
{
    public const string ClsNoHit = "NoHit", ClsWorld = "WorldObject", ClsUi = "CanvasUI", ClsUncertain = "Uncertain";
    public const string BasisNoHit = "no_hit", BasisWorldOnly = "world_only", BasisUiOnly = "ui_only", BasisUiInFront = "ui_in_front",
                        BasisWorldOccludes = "world_occludes_ui", BasisOcclusionNotEstablished = "occlusion_not_established", BasisUiNotEvaluated = "ui_not_evaluated";

    /// <summary>Distances closer than this (m) count as coplanar; the UI wins (it is drawn over the surface it sits on).</summary>
    public const float CoplanarEpsilon = 0.001f;

    public struct Primary { public string cls, basis; }

    /// <param name="occluderEstablished">Whether the physical hit is a reliably visible opaque occluder (see IsEstablishedOccluder). Only consulted when both channels hit and the world hit is in front.</param>
    public static Primary Resolve(bool uiEvaluated, bool uiHit, float uiDistance, bool worldHit, float worldDistance, bool occluderEstablished)
    {
        if (!uiEvaluated) return new Primary { cls = worldHit ? ClsWorld : ClsNoHit, basis = BasisUiNotEvaluated };
        if (!uiHit) return worldHit ? new Primary { cls = ClsWorld, basis = BasisWorldOnly } : new Primary { cls = ClsNoHit, basis = BasisNoHit };
        if (!worldHit) return new Primary { cls = ClsUi, basis = BasisUiOnly };
        if (uiDistance <= worldDistance + CoplanarEpsilon) return new Primary { cls = ClsUi, basis = BasisUiInFront };
        return occluderEstablished ? new Primary { cls = ClsWorld, basis = BasisWorldOccludes } : new Primary { cls = ClsUncertain, basis = BasisOcclusionNotEstablished };
    }

    /// <summary>
    /// True when the collider is a reliably visible, opaque occluder: a non-trigger collider that either is a TerrainCollider or belongs to an object (the collider's own GameObject, a parent, or a child) with an
    /// enabled, active, non-shadow-only Renderer whose shared material is opaque (render queue below the transparent range). Triggers, collider-only objects and transparent materials are NOT established.
    /// </summary>
    public static bool IsEstablishedOccluder(Collider col)
    {
        if (col == null || col.isTrigger) return false;
        if (col is TerrainCollider) return true;
        Renderer r;
        if (!col.TryGetComponent(out r)) r = col.GetComponentInParent<Renderer>();
        if (r == null) r = col.GetComponentInChildren<Renderer>();
        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) return false;
        if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) return false;
        Material m = r.sharedMaterial;
        return m != null && m.renderQueue < 3000;
    }
}
