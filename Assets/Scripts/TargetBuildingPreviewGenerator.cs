// TargetBuildingPreviewGenerator.cs
//
// Role
// ----
// Produces a clean, isolated still image (Texture2D) of ONE building GameObject, on demand, for display
// inside a UI panel. It knows NOTHING about Phase 3, experimental groups, destinations, target
// correctness, or timing - it is handed a GameObject and returns a picture of it.
//
// Isolation (NO permanent scene change):
//  - A dedicated, normally-disabled preview Camera + preview Light are created once as HideFlags.DontSave
//    children of this GameObject. They are never part of the normal URP camera stack (enabled == false);
//    the camera renders only via an explicit, synchronous Camera.Render().
//  - At capture time every GameObject under the target building has its 'layer' temporarily switched to a
//    dedicated preview layer. The preview camera's cullingMask (and the preview light's cullingMask)
//    contain ONLY that layer, so nothing else in the Lyon scene is rendered or lit (no terrain, river,
//    roads, NPCs, other buildings, teleport markers, UI, skybox).
//  - Every original layer, and the building's original active state, are restored in a finally block, so
//    a thrown exception cannot leave the scene modified.
//  - The camera clears to a flat neutral colour: the image carries no spatial / navigational context.
//
// The building is never moved / rotated / scaled / re-materialled. The only temporary changes are the
// per-GameObject 'layer' field and, if the building is inactive, a synchronous SetActive(true) -> render
// -> SetActive(false). Because Camera.Render() is synchronous, the participant's camera does not render
// during that window, so the building is never shown in the world.
//
// Per-target pedestrian viewpoints (see 'targetViewpoints'):
//  - Without a configured viewpoint every building is framed from the SAME world-space direction (an elevated 3/4 view, see
//    ViewMode), which regularly shows the rear or an unrecognisable side of a building the participant never sees from the street.
//  - A target can instead own an explicit viewpoint Transform (position AND rotation, placed in Scene view at eye level on a
//    pedestrian-accessible spot). The SAME private preview camera is simply moved to that pose before each capture; no extra Camera
//    exists. The isolation, flat background, dedicated light and synchronous render are unchanged, so the image still carries no
//    spatial context beyond the building itself. The target is matched by its stable id (TargetSelectable.ResolvedTargetId, i.e.
//    the building's GameObject name), never by list order. A target with no usable viewpoint falls back to the legacy framing and
//    logs ONE warning per target id naming it, so unconfigured targets are always identifiable.
//
// Project context: Unity 2022.3, URP 14, Linear colour space (hence the sRGB RenderTexture readback).
//
// All comments in English per project convention.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>One explicit pedestrian viewpoint for one target building (matched by stable target id).</summary>
[System.Serializable]
public class TargetViewpoint
{
    [Tooltip("Stable id of the target building: TargetSelectable.ResolvedTargetId, which equals the building's GameObject name.")]
    public string targetId;

    [Tooltip("Capture pose: position = pedestrian eye position, rotation = viewing direction (the camera looks along this " +
             "Transform's forward axis, up = world up). Place it in Scene view at eye level (about 1.6 m above the walking " +
             "surface) on a spot a pedestrian can reach, facing the identifying facade. Empty = not configured yet.")]
    public Transform viewpoint;

    [Tooltip("Vertical field of view in degrees for this target. 0 = use the generator's 'Viewpoint Field Of View'.")]
    [Range(0f, 90f)] public float fieldOfView = 0f;
}

[DisallowMultipleComponent]
public class TargetBuildingPreviewGenerator : MonoBehaviour
{
    public enum ViewMode
    {
        FixedWorldDirection,   // always view from the same world-space direction (most robust; hides the building's real facing)
        BuildingLongestFacade  // view 3/4-on to the building's longest horizontal footprint axis
    }

    [Header("Output texture")]
    [Tooltip("Square preview resolution in pixels. 512-768 is plenty for a recognisable building photo.")]
    [Range(128, 2048)] public int textureSize = 640;

    [Header("Camera projection")]
    [Tooltip("true  = orthographic (axonometric-looking, flat).  false = mild perspective (photo-looking).")]
    public bool orthographic = false;

    [Tooltip("Perspective vertical field of view. Low values (25-35) keep architectural distortion minimal.")]
    [Range(10f, 60f)] public float fieldOfView = 30f;

    [Tooltip("Multiplier on the fitted framing distance / orthographic size. >1 leaves a margin around the building.")]
    [Range(1f, 2.5f)] public float framingPadding = 1f;

    [Header("View direction")]
    public ViewMode viewMode = ViewMode.FixedWorldDirection;

    [Tooltip("FixedWorldDirection mode: direction FROM the building TO the camera, in world space. " +
             "Default is an elevated 3/4 view. It intentionally does not encode the building's real orientation.")]
    public Vector3 fixedWorldViewDirection = new Vector3(-1f, 0.7f, -1f);

    [Tooltip("BuildingLongestFacade mode: elevation angle (degrees above the horizon) of the 3/4 view.")]
    [Range(5f, 70f)] public float facadeViewElevation = 25f;

    [Header("Per-target pedestrian viewpoints")]
    [Tooltip("One entry per Part 3 candidate building. Entries are matched by targetId, never by order. A building with no entry " +
             "or an empty viewpoint is rendered with the legacy shared framing above and a warning names it.")]
    public TargetViewpoint[] targetViewpoints = new TargetViewpoint[0];

    [Tooltip("Default vertical field of view for viewpoint captures (a natural pedestrian view of a facade).")]
    [Range(20f, 90f)] public float viewpointFieldOfView = 50f;

    [Tooltip("Viewpoint captures: rotation of the preview light RELATIVE to the capture camera (euler), so the facade the camera " +
             "sees is lit whichever way it faces. The fixed world-space light angle is used only for the legacy framing.")]
    public Vector3 viewpointLightEulerOffset = new Vector3(35f, -30f, 0f);

    [Header("Viewpoint suggestion (Editor tool settings; unused at runtime)")]
    [Tooltip("Eye height above the walking surface used by 'Suggest viewpoints' (metres).")]
    [Range(1.2f, 2f)] public float suggestEyeHeight = 1.6f;

    [Tooltip("Free border kept around the building silhouette, as a fraction of the half-frame (0.06 = 6 %). The WHOLE building must lie inside this inner frame.")]
    [Range(0f, 0.3f)] public float suggestFramingMargin = 0.06f;

    [Tooltip("Largest horizontal distance (metres) from the building centre that Suggest will search. Large buildings may legitimately need 100-250 m at eye level.")]
    [Min(10f)] public float suggestMaxDistance = 400f;

    [Tooltip("Largest upward / downward camera pitch (degrees) a suggestion may use. Keeps the view head-plausible; tall buildings are fitted by moving back, not by looking up steeply.")]
    [Range(0f, 60f)] public float suggestMaxPitchUp = 30f;
    [Range(0f, 45f)] public float suggestMaxPitchDown = 10f;

    [Tooltip("Hard gate: the silhouette must fill at least this fraction of the frame's larger dimension (rejects tiny, far-away targets).")]
    [Range(0f, 1f)] public float suggestMinFill = 0.5f;

    [Tooltip("Hard gate: facade score (0 grazing .. 1 square-on to the longest facade) must be at least this.")]
    [Range(0f, 1f)] public float suggestMinFacade = 0.3f;

    [Tooltip("Minimum overall score to ACCEPT a suggestion: 0.4 x fill + 0.4 x facade + 0.2 x street access (0..1). Below this the target is reported 'needs manual placement'. Calibrate against the exported PNGs.")]
    [Range(0f, 1f)] public float suggestMinScore = 0.6f;

    [Tooltip("A candidate eye position may be moved up to this far (metres, horizontally) onto the NavMesh. 0 disables NavMesh snapping.")]
    [Range(0f, 20f)] public float suggestGroundSnapRadius = 6f;

    [Tooltip("If neither NavMesh nor a ground raycast finds a walking surface, allow the building's base height as an assumed ground. Off by default: such suggestions are not pedestrian-verified.")]
    public bool suggestAllowAssumedGround = false;

    [Header("Background")]
    public Color backgroundColor = new Color(0.85f, 0.85f, 0.86f, 1f);

    [Header("Isolation layer")]
    [Tooltip("Layer index used ONLY during a capture. MUST be an otherwise-unused layer. " +
             "Layers 6-31 are free in this project; 30 is recommended. Add it in Project Settings > Tags and Layers.")]
    [Range(0, 31)] public int previewLayer = 30;

    [Header("Dedicated preview lighting")]
    [Tooltip("If true, a private directional light (culled to the preview layer only) lights the capture, so " +
             "the image looks the same regardless of scene time-of-day. Scene lighting is never modified.")]
    public bool useDedicatedLight = true;
    public Vector3 lightEulerAngles = new Vector3(40f, -40f, 0f);
    [Range(0f, 4f)] public float lightIntensity = 1.25f;
    public Color lightColor = Color.white;
    [Range(0f, 2f)] public float lightIndirectMultiplier = 0.9f;

    [Header("Debug")]
    public bool enableDebugLogs = false;

    // Lazily-created rig: HideFlags.DontSave children of this GameObject, never enabled for normal rendering.
    private Camera previewCamera;
    private Light previewLight;

    private readonly List<Transform> transformScratch = new List<Transform>(256);

    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Render a clean isolated image of 'building' and return it as a NEW Texture2D. The caller owns the
    /// returned texture and must Destroy() it when finished. Returns null if 'building' is null or has no
    /// visible geometry. Synchronous; does not affect the participant's view.
    /// </summary>
    public Texture2D CapturePreview(GameObject building)
    {
        return CapturePreview(building, true);
    }

    /// <summary>
    /// As above; 'useConfiguredViewpoint' = false forces the legacy shared framing (used by the Editor tool for before/after
    /// comparisons). The experiment always calls the one-argument overload.
    /// </summary>
    public Texture2D CapturePreview(GameObject building, bool useConfiguredViewpoint)
    {
        if (building == null)
        {
            if (enableDebugLogs) Debug.LogWarning("[TargetBuildingPreviewGenerator] CapturePreview: building is null.");
            return null;
        }

        int layer = Mathf.Clamp(previewLayer, 0, 31);
        EnsureRig(layer);

        TargetPreviewOverride ovr = building.GetComponentInChildren<TargetPreviewOverride>(true);

        bool wasActiveSelf = building.activeSelf;
        List<KeyValuePair<GameObject, int>> savedLayers = null;
        Texture2D result = null;

        try
        {
            // Renderers only report valid bounds and only draw while the building is active.
            if (!wasActiveSelf) building.SetActive(true);

            Renderer[] renderers = building.GetComponentsInChildren<Renderer>(true);

            Bounds bounds;
            if (!TryGetVisualBounds(renderers, ovr, building.transform, out bounds))
            {
                if (enableDebugLogs)
                    Debug.LogWarning($"[TargetBuildingPreviewGenerator] '{building.name}' has no usable renderer bounds; no preview generated.");
                return null;
            }

            // Temporarily move the whole hierarchy onto the isolation layer.
            savedLayers = new List<KeyValuePair<GameObject, int>>(64);
            transformScratch.Clear();
            building.GetComponentsInChildren<Transform>(true, transformScratch);
            for (int i = 0; i < transformScratch.Count; i++)
            {
                GameObject go = transformScratch[i].gameObject;
                savedLayers.Add(new KeyValuePair<GameObject, int>(go, go.layer));
                go.layer = layer;
            }

            TargetViewpoint vp = useConfiguredViewpoint ? ResolveViewpoint(building) : null;
            if (vp != null) ConfigureCameraFromViewpoint(bounds, vp, layer, building);
            else ConfigureCamera(bounds, ovr, layer);
            ConfigureLight(layer, vp != null);

            int size = Mathf.Clamp(textureSize, 128, 2048);

            // Linear colour-space project: use an sRGB RenderTexture so the bytes we read back are sRGB and
            // the (sRGB) UGUI RawImage displays them at the correct brightness.
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
            RenderTexture previousActive = RenderTexture.active;

            try
            {
                previewCamera.aspect = 1f;
                previewCamera.targetTexture = rt;
                previewCamera.Render(); // synchronous; renders ONLY this camera

                RenderTexture.active = rt;
                result = new Texture2D(size, size, TextureFormat.RGBA32, false, false); // non-mip, sRGB sampling
                result.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
                result.Apply(false, false);
                result.name = "Preview_" + building.name;
            }
            finally
            {
                RenderTexture.active = previousActive;
                previewCamera.targetTexture = null;
                RenderTexture.ReleaseTemporary(rt);
            }

            if (enableDebugLogs)
                Debug.Log($"[TargetBuildingPreviewGenerator] Captured '{building.name}' at {size}x{size}; bounds size {bounds.size}.");
        }
        finally
        {
            // Restore every original layer.
            if (savedLayers != null)
            {
                for (int i = 0; i < savedLayers.Count; i++)
                {
                    if (savedLayers[i].Key != null) savedLayers[i].Key.layer = savedLayers[i].Value;
                }
            }
            // Restore the building's original active state.
            if (!wasActiveSelf && building != null) building.SetActive(false);

            if (previewLight != null) previewLight.enabled = false;
        }

        return result;
    }

    // ------------------------------------------------------------------------------------------------

    /// <summary>The stable id used to match viewpoints: TargetSelectable.ResolvedTargetId, else the GameObject name.</summary>
    public static string ResolveTargetId(GameObject building)
    {
        if (building == null) return null;
        TargetSelectable ts = building.GetComponent<TargetSelectable>();
        if (ts == null) ts = building.GetComponentInChildren<TargetSelectable>(true);
        return ts != null ? ts.ResolvedTargetId : building.name;
    }

    /// <summary>The usable viewpoint entry for 'building' (matching id AND a Transform), or null.</summary>
    public TargetViewpoint FindViewpoint(GameObject building)
    {
        string id = ResolveTargetId(building);
        if (string.IsNullOrEmpty(id) || targetViewpoints == null) return null;
        for (int i = 0; i < targetViewpoints.Length; i++)
        {
            TargetViewpoint e = targetViewpoints[i];
            if (e != null && e.viewpoint != null && string.Equals(e.targetId, id, System.StringComparison.Ordinal))
                return e;
        }
        return null;
    }

    // One warning per target id (a capture happens once per destination per session, so this is never per-frame).
    private readonly HashSet<string> warnedMissing = new HashSet<string>();
    private readonly HashSet<string> warnedCropped = new HashSet<string>();

    private TargetViewpoint ResolveViewpoint(GameObject building)
    {
        TargetViewpoint vp = FindViewpoint(building);
        if (vp != null)
        {
            if (enableDebugLogs) Debug.Log($"[TargetBuildingPreviewGenerator] '{building.name}': using configured viewpoint '{vp.viewpoint.name}'.");
            return vp;
        }

        string id = ResolveTargetId(building);
        if (warnedMissing.Add(id ?? "<null>"))
            Debug.LogWarning($"[TargetBuildingPreviewGenerator] No pedestrian viewpoint is configured for target '{id}'. Its image is rendered " +
                             "from the legacy shared elevated camera angle and may not show a recognisable facade. Add an entry with this " +
                             "targetId and a viewpoint Transform to 'Target Viewpoints' on the TargetBuildingPreviewGenerator.", this);
        return null;
    }

    private void EnsureRig(int layer)
    {
        if (previewCamera == null)
        {
            GameObject camGo = new GameObject("~TargetPreviewCamera");
            camGo.hideFlags = HideFlags.DontSave;
            camGo.transform.SetParent(transform, false);

            previewCamera = camGo.AddComponent<Camera>();
            previewCamera.enabled = false;                          // never in the normal per-frame loop
            previewCamera.clearFlags = CameraClearFlags.SolidColor;
            previewCamera.backgroundColor = backgroundColor;
            previewCamera.cullingMask = 1 << layer;
            previewCamera.allowHDR = false;
            previewCamera.allowMSAA = false;
            previewCamera.useOcclusionCulling = false;
            previewCamera.stereoTargetEye = StereoTargetEyeMask.None; // never render this in stereo/XR
            previewCamera.depth = -100;

            // URP: attach additional data and switch off anything that could tint the preview.
            var urp = camGo.AddComponent<UniversalAdditionalCameraData>();
            urp.renderType = CameraRenderType.Base;
            urp.renderShadows = true;
            urp.renderPostProcessing = false;
            urp.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            urp.antialiasingQuality = AntialiasingQuality.High;
            urp.requiresColorOption = CameraOverrideOption.Off;
            urp.requiresDepthOption = CameraOverrideOption.Off;
            urp.stopNaN = false;
            urp.dithering = false;
        }

        if (useDedicatedLight && previewLight == null)
        {
            GameObject lightGo = new GameObject("~TargetPreviewLight");
            lightGo.hideFlags = HideFlags.DontSave;
            lightGo.transform.SetParent(transform, false);

            previewLight = lightGo.AddComponent<Light>();
            previewLight.type = LightType.Directional;
            previewLight.shadows = LightShadows.Soft;
            previewLight.enabled = false;
        }

        previewCamera.cullingMask = 1 << layer;
        if (previewLight != null) previewLight.cullingMask = 1 << layer;
    }

    private bool TryGetVisualBounds(Renderer[] renderers, TargetPreviewOverride ovr, Transform buildingRoot, out Bounds bounds)
    {
        if (ovr != null && ovr.useManualBounds)
        {
            Vector3 worldCenter = buildingRoot.TransformPoint(ovr.manualLocalCenter);
            Vector3 worldSize = Vector3.Scale(ovr.manualLocalSize, AbsScale(buildingRoot.lossyScale));
            bounds = new Bounds(worldCenter, worldSize);
            return bounds.size.sqrMagnitude > 1e-6f;
        }

        bool has = false;
        bounds = new Bounds(Vector3.zero, Vector3.zero);

        if (renderers != null)
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || !r.enabled) continue;
                if (r is ParticleSystemRenderer) continue;
                if (r is TrailRenderer) continue;

                Bounds b = r.bounds;
                if (b.size.sqrMagnitude <= 0f) continue;

                if (!has) { bounds = b; has = true; }
                else bounds.Encapsulate(b);
            }
        }

        return has && bounds.size.sqrMagnitude > 1e-6f;
    }

    private static Vector3 AbsScale(Vector3 s)
    {
        return new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
    }

    private void ConfigureCamera(Bounds bounds, TargetPreviewOverride ovr, int layer)
    {
        float pad = framingPadding * ((ovr != null && ovr.paddingMultiplier > 0f) ? ovr.paddingMultiplier : 1f);
        float radius = Mathf.Max(0.01f, bounds.extents.magnitude); // bounding-sphere radius

        // Direction FROM the building TO the camera, world space.
        Vector3 dir;
        if (ovr != null && ovr.viewDirectionOverride.sqrMagnitude > 1e-4f)
        {
            dir = ovr.viewDirectionOverride.normalized;
        }
        else if (viewMode == ViewMode.BuildingLongestFacade)
        {
            Vector3 size = bounds.size;
            Vector3 along = (size.x >= size.z) ? Vector3.right : Vector3.forward;
            Vector3 across = (size.x >= size.z) ? Vector3.forward : Vector3.right;
            Vector3 flat = (along + across).normalized; // 45 deg between the two footprint axes
            float el = Mathf.Deg2Rad * Mathf.Clamp(facadeViewElevation, 5f, 80f);
            dir = (flat * Mathf.Cos(el) + Vector3.up * Mathf.Sin(el)).normalized;
        }
        else
        {
            Vector3 wd = (fixedWorldViewDirection.sqrMagnitude > 1e-4f) ? fixedWorldViewDirection : new Vector3(-1f, 0.7f, -1f);
            dir = wd.normalized;
        }

        previewCamera.orthographic = orthographic;

        float distance;
        if (orthographic)
        {
            previewCamera.orthographicSize = radius * pad;
            distance = radius * 3f + 1f;
        }
        else
        {
            float fov = Mathf.Clamp((ovr != null && ovr.fovOverride > 0f) ? ovr.fovOverride : fieldOfView, 5f, 80f);
            previewCamera.fieldOfView = fov;
            distance = (radius / Mathf.Sin(Mathf.Deg2Rad * fov * 0.5f)) * pad;
        }

        previewCamera.transform.position = bounds.center + dir * distance;
        previewCamera.transform.rotation = Quaternion.LookRotation(bounds.center - previewCamera.transform.position, Vector3.up);

        previewCamera.nearClipPlane = Mathf.Max(0.01f, distance - radius * 3f);
        previewCamera.farClipPlane = distance + radius * 6f;

        previewCamera.clearFlags = CameraClearFlags.SolidColor;
        previewCamera.backgroundColor = (ovr != null && ovr.useBackgroundOverride) ? ovr.backgroundOverride : backgroundColor;
        previewCamera.cullingMask = 1 << layer;
    }

    private void ConfigureCameraFromViewpoint(Bounds bounds, TargetViewpoint vp, int layer, GameObject building)
    {
        Transform t = vp.viewpoint;
        Vector3 forward = t.forward;
        // Upright camera: only the viewpoint's heading and pitch are used, never its roll.
        Quaternion rot = forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward, Vector3.up) : t.rotation;

        if (bounds.Contains(t.position) && enableDebugLogs)
            Debug.LogWarning($"[TargetBuildingPreviewGenerator] Viewpoint '{t.name}' lies inside the bounds of '{vp.targetId}'; check its position.", t);

        previewCamera.orthographic = false;
        previewCamera.fieldOfView = Mathf.Clamp(vp.fieldOfView > 0f ? vp.fieldOfView : viewpointFieldOfView, 10f, 100f);
        previewCamera.transform.SetPositionAndRotation(t.position, rot);

        float toFar = Vector3.Distance(t.position, bounds.center) + bounds.extents.magnitude * 2f;
        previewCamera.nearClipPlane = 0.3f;
        previewCamera.farClipPlane = Mathf.Max(toFar, 50f);

        previewCamera.clearFlags = CameraClearFlags.SolidColor;
        previewCamera.backgroundColor = backgroundColor;
        previewCamera.cullingMask = 1 << layer;

        WarnIfCropped(building, vp, rot, previewCamera.fieldOfView);
    }

    // The whole building must be inside the frame. Checked once per target id against the building's oriented mesh-bounds corners with
    // the exact pinhole model the Editor tool uses (ViewpointFraming); a hand-placed viewpoint that crops the building is reported, never hidden.
    private void WarnIfCropped(GameObject building, TargetViewpoint vp, Quaternion rot, float fov)
    {
        string id = vp.targetId ?? "<null>";
        if (warnedCropped.Contains(id)) return;

        List<Vector3> corners = CollectBuildingCorners(building);
        if (corners.Count == 0) return;

        ViewpointFraming.ProjectionResult r = ViewpointFraming.Project(corners, vp.viewpoint.position, rot * Vector3.forward, fov, 0f, 0.3f);
        if (r.Fits) return;

        warnedCropped.Add(id);
        Debug.LogWarning($"[TargetBuildingPreviewGenerator] Viewpoint '{vp.viewpoint.name}' for target '{id}' does not show the whole building " +
                         (r.allInFront ? "(it is cropped by the frame)." : "(part of it is behind or at the camera).") +
                         " Move the viewpoint back or re-aim it; use 'Check configured viewpoints' in the Inspector.", vp.viewpoint);
    }

    /// <summary>
    /// World-space corner points of the building's visible geometry: the 8 corners of every enabled renderer's ORIENTED mesh bounds
    /// (tighter than world AABBs for rotated buildings), or of the renderer's world bounds when it has no MeshFilter mesh. Used by the
    /// runtime cropped-viewpoint check and by the Editor tool, so both judge framing on the same points.
    /// </summary>
    public static List<Vector3> CollectBuildingCorners(GameObject building)
    {
        var corners = new List<Vector3>(64);
        if (building == null) return corners;

        Renderer[] rs = building.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            Renderer r = rs[i];
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (r is ParticleSystemRenderer || r is TrailRenderer) continue;

            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Bounds lb = mf.sharedMesh.bounds;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 local = lb.center + new Vector3((k & 1) == 0 ? -lb.extents.x : lb.extents.x,
                                                            (k & 2) == 0 ? -lb.extents.y : lb.extents.y,
                                                            (k & 4) == 0 ? -lb.extents.z : lb.extents.z);
                    corners.Add(r.transform.TransformPoint(local));
                }
            }
            else
            {
                Bounds wb = r.bounds;
                for (int k = 0; k < 8; k++)
                    corners.Add(wb.center + new Vector3((k & 1) == 0 ? -wb.extents.x : wb.extents.x,
                                                        (k & 2) == 0 ? -wb.extents.y : wb.extents.y,
                                                        (k & 4) == 0 ? -wb.extents.z : wb.extents.z));
            }
        }
        return corners;
    }

    private void ConfigureLight(int layer, bool relativeToCamera)
    {
        if (!useDedicatedLight || previewLight == null) return;

        previewLight.transform.rotation = relativeToCamera
            ? previewCamera.transform.rotation * Quaternion.Euler(viewpointLightEulerOffset)
            : Quaternion.Euler(lightEulerAngles);
        previewLight.color = lightColor;
        previewLight.intensity = lightIntensity;
        previewLight.bounceIntensity = lightIndirectMultiplier;
        previewLight.cullingMask = 1 << layer;
        previewLight.enabled = true;
    }

#if UNITY_EDITOR
    // Scene view: the capture frustum of every configured viewpoint while this component is selected.
    private void OnDrawGizmosSelected()
    {
        if (targetViewpoints == null) return;
        Matrix4x4 old = Gizmos.matrix;
        Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.9f);
        for (int i = 0; i < targetViewpoints.Length; i++)
        {
            TargetViewpoint e = targetViewpoints[i];
            if (e == null || e.viewpoint == null) continue;
            Gizmos.matrix = Matrix4x4.TRS(e.viewpoint.position, e.viewpoint.rotation, Vector3.one);
            Gizmos.DrawFrustum(Vector3.zero, e.fieldOfView > 0f ? e.fieldOfView : viewpointFieldOfView, 25f, 0.3f, 1f);
        }
        Gizmos.matrix = old;
    }
#endif
}
