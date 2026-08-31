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
// Project context: Unity 2022.3, URP 14, Linear colour space (hence the sRGB RenderTexture readback).
//
// All comments in English per project convention.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

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

            ConfigureCamera(bounds, ovr, layer);
            ConfigureLight(layer);

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

    private void ConfigureLight(int layer)
    {
        if (!useDedicatedLight || previewLight == null) return;

        previewLight.transform.rotation = Quaternion.Euler(lightEulerAngles);
        previewLight.color = lightColor;
        previewLight.intensity = lightIntensity;
        previewLight.bounceIntensity = lightIndirectMultiplier;
        previewLight.cullingMask = 1 << layer;
        previewLight.enabled = true;
    }
}