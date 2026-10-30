// TeleportPointVisual.cs
//
// VISUAL-ONLY component placed on the "Teleport Anchor Variant" prefab root (so every instance, including points created by the
// GeoJSON importer, carries it). It decides which SHARED material the point's visual renderer shows:
//   - ordinary teleportation point  -> 'ordinaryMaterial' (Teleport_Ordinary_Cyan: cyan / electric blue)
//   - river teleportation point     -> 'riverMaterial'    (Teleport_River_Amber: amber / orange)
//
// How a river point is recognised: a RiverTeleportPoint component exists on this GameObject OR ANY OF ITS CHILDREN. In the scene the four
// RiverTeleportPoint components sit on the "Visuals" child (the object that also carries the MeshCollider, which RiverTeleportPoint requires),
// not on the prefab root - looking only at the root classified every river point as ordinary. Both endpoints of a pair are treated alike.
//
// Play Mode / builds: Renderer.sharedMaterial is assigned once in Awake (i.e. when the point is first activated - river points are activated by
// RiverPointAvailability when Part 3 starts). The shared asset is assigned directly: no material instance, nothing per frame. No script in the
// project writes these renderers afterwards (RiverPointAvailability only toggles SetActive and adds a UGUI sign; RiverTeleportPoint never
// touches renderers).
//
// Edit Mode: so river points are not cyan in the Scene view, a river point gets a MaterialPropertyBlock (base + emission colour copied from the
// river material) while the scene is not playing. A property block is not serialized: it creates no prefab override, does not dirty the scene and
// disappears on entering Play Mode, where the real material takes over. Ordinary points need nothing (the prefab renderer is already cyan).
//
// It never touches transforms, colliders, the TeleportationAnchor, the pairing or any teleport logic.
// Change a colour or glow by editing the material asset itself (Assets/Materials/TeleportationPoints), never this script.
// All comments in English per project convention.

using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public class TeleportPointVisual : MonoBehaviour
{
    [Header("Visual renderer")]
    [Tooltip("Renderer that draws the point (the 'Visuals' child of the prefab). Leave empty to use the MeshRenderer that sits on the same object as the point's Collider (fallback: the first MeshRenderer among the children).")]
    public MeshRenderer visualRenderer;

    [Header("Shared materials (edit the material assets to change colour / glow)")]
    [Tooltip("Material of ordinary teleportation points. Default: Assets/Materials/TeleportationPoints/Teleport_Ordinary_Cyan.mat")]
    public Material ordinaryMaterial;

    [Tooltip("Material of river teleportation points (a RiverTeleportPoint on this object or one of its children). Default: Assets/Materials/TeleportationPoints/Teleport_River_Amber.mat")]
    public Material riverMaterial;

    [Header("Debug")]
    [Tooltip("Log one line per point when its material is applied: hierarchy path, river / ordinary, renderer, material before and after. Never per frame.")]
    public bool enableDebugLogs = false;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private void Awake()
    {
        if (Application.isPlaying) Apply();
    }

    private void OnEnable()
    {
        if (!Application.isPlaying) ApplyEditPreview();
    }

    private void OnDisable()
    {
        if (!Application.isPlaying) ClearEditPreview();
    }

    /// <summary>True when a RiverTeleportPoint is attached to this object or any child (active or not).</summary>
    public bool IsRiverPoint()
    {
        return GetComponentInChildren<RiverTeleportPoint>(true) != null;
    }

    private MeshRenderer ResolveRenderer()
    {
        if (visualRenderer != null) return visualRenderer;
        MeshRenderer first = null;
        foreach (MeshRenderer r in GetComponentsInChildren<MeshRenderer>(true))
        {
            if (first == null) first = r;
            if (r.GetComponent<Collider>() != null) return r;   // the disc: renderer and collider share the "Visuals" object
        }
        return first;
    }

    /// <summary>Play Mode: assign the material that matches this point's type (shared asset, same result every call).</summary>
    public void Apply()
    {
        MeshRenderer r = ResolveRenderer();
        if (r == null)
        {
            Debug.LogWarning("[TeleportPointVisual] No MeshRenderer found under '" + Path() + "'; material not applied.", this);
            return;
        }

        bool river = IsRiverPoint();
        Material m = river ? riverMaterial : ordinaryMaterial;
        Material before = r.sharedMaterial;
        if (m == null)
        {
            if (enableDebugLogs) Debug.LogWarning("[TeleportPointVisual] '" + Path() + "': " + (river ? "river" : "ordinary") + " material not assigned; renderer left unchanged.", this);
            return;
        }

        if (before != m) r.sharedMaterial = m;
        if (enableDebugLogs)
            Debug.Log("[TeleportPointVisual] '" + Path() + "' classified " + (river ? "RIVER" : "ordinary") + "; renderer '" + r.name + "'; material '" +
                      (before != null ? before.name : "null") + "' -> '" + (r.sharedMaterial != null ? r.sharedMaterial.name : "null") + "'.", this);
    }

    // ---- Edit Mode preview (non-serialized) -------------------------------------------------------------------------------

    private void ApplyEditPreview()
    {
        MeshRenderer r = ResolveRenderer();
        if (r == null) return;
        if (!IsRiverPoint() || riverMaterial == null) { r.SetPropertyBlock(null); return; }

        var mpb = new MaterialPropertyBlock();
        r.GetPropertyBlock(mpb);
        if (riverMaterial.HasProperty(BaseColorId)) mpb.SetColor(BaseColorId, riverMaterial.GetColor(BaseColorId));
        if (riverMaterial.HasProperty(ColorId)) mpb.SetColor(ColorId, riverMaterial.GetColor(ColorId));
        if (riverMaterial.HasProperty(EmissionColorId)) mpb.SetColor(EmissionColorId, riverMaterial.GetColor(EmissionColorId));
        r.SetPropertyBlock(mpb);
    }

    private void ClearEditPreview()
    {
        MeshRenderer r = ResolveRenderer();
        if (r != null) r.SetPropertyBlock(null);
    }

    private string Path()
    {
        string p = name;
        for (Transform t = transform.parent; t != null; t = t.parent) p = t.name + "/" + p;
        return p;
    }
}
