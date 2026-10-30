// DuskEnvironmentProfile.cs
//
// EDITOR-ONLY data asset (lives in Assets/Editor, so it can never be part of a build and nothing here runs at runtime). It holds
//   1. the editable DUSK values (sun rotation / colour / intensity, procedural dusk sky material, ambient gradient, fog, reflection
//      intensity), and
//   2. a snapshot of the original DAYLIGHT values of LyonVRVity35 taken before the first Apply, so the original lighting can be restored.
// The buttons in DuskEnvironmentProfileEditor write these values into the OPEN scene's Directional Light and Lighting settings
// (RenderSettings) and mark the scene dirty; the experimenter then saves the scene. Nothing continuously modifies lighting.
//
// Scope: the active sun Directional Light and the scene's RenderSettings only. Post-processing is not touched (no Volume exists in the
// scene and the cameras have Render Post Processing disabled), the baked LightingData.asset / reflection EXR are not regenerated, and no
// project-wide setting or shared material is changed.
// All comments in English per project convention.

using System;
using UnityEngine;
using UnityEngine.Rendering;

[CreateAssetMenu(fileName = "Dusk Environment Profile", menuName = "VR Experiment/Dusk Environment Profile")]
public class DuskEnvironmentProfile : ScriptableObject
{
    /// <summary>Values shared by the dusk settings and the daylight snapshot (everything except how the sun rotation is stored).</summary>
    [Serializable]
    public class EnvironmentValues
    {
        [Header("Sun (Directional Light)")]
        [Tooltip("Light colour of the sun. Dusk: warm orange. Daylight original: white.")]
        public Color sunColor = Color.white;
        [Tooltip("Light.intensity of the sun. Raise if the scene is too dark, lower if highlights are too harsh.")]
        public float sunIntensity = 1f;

        [Header("Sky")]
        [Tooltip("Skybox material (RenderSettings.skybox). Dusk: Assets/Materials/Environment/Sky_Dusk.mat. Daylight original: Default-Skybox.")]
        public Material skyboxMaterial;

        [Header("Ambient light (Window > Rendering > Lighting > Environment)")]
        [Tooltip("Environment Lighting Source. Gradient (Trilight) needs no light bake; Skybox would use the stale baked daytime probe.")]
        public AmbientMode ambientMode = AmbientMode.Trilight;
        [Tooltip("Ambient colour from above (Gradient mode: Sky Color). Brighter / bluer = brighter, cooler shadows.")]
        public Color ambientSky = Color.gray;
        [Tooltip("Ambient colour from the sides (Gradient mode: Equator Color). Lights facades in the shade.")]
        public Color ambientEquator = Color.gray;
        [Tooltip("Ambient colour from below (Gradient mode: Ground Color). Lights undersides.")]
        public Color ambientGround = Color.gray;
        [Tooltip("Intensity Multiplier. Only used when the source is Skybox; ignored by Gradient mode (use the three colours).")]
        public float ambientIntensity = 1f;

        [Header("Fog")]
        public bool fogEnabled = false;
        public Color fogColor = Color.gray;
        public FogMode fogMode = FogMode.ExponentialSquared;
        [Tooltip("Exponential / ExponentialSquared density. 0.0003 is a faint haze; 0.01 hides the distance.")]
        public float fogDensity = 0.0003f;
        [Tooltip("Linear mode only: distance where fog starts.")]
        public float fogStart = 0f;
        [Tooltip("Linear mode only: distance where fog is total.")]
        public float fogEnd = 300f;

        [Header("Reflections")]
        [Tooltip("Environment Reflections Intensity Multiplier (the baked default reflection is the daytime sky; lower it for dusk).")]
        [Range(0f, 1f)] public float reflectionIntensity = 1f;
    }

    [Serializable]
    public class DuskValues : EnvironmentValues
    {
        [Header("Sun rotation")]
        [Tooltip("Euler rotation of the sun Directional Light. X = elevation above the horizon (about 12 = low sun; 0 = at the horizon). " +
                 "Y = compass heading the light travels towards (keeps the scene's current shadow direction). Z has no effect for a directional light.")]
        public Vector3 sunEuler = new Vector3(12f, 10.322f, 41.066f);
    }

    [Serializable]
    public class DaySnapshot : EnvironmentValues
    {
        [Tooltip("True once the original daylight settings of the target scene have been recorded. Restore is only possible when true.")]
        public bool captured = false;
        [Tooltip("Scene the snapshot was taken from.")]
        public string sceneName = "";
        [Tooltip("Exact sun rotation (quaternion) so Restore reproduces the original orientation bit for bit.")]
        public Quaternion sunRotation = Quaternion.identity;
    }

    [Header("Target")]
    [Tooltip("Scene this profile is meant for. Apply / Restore ask for confirmation when another scene is active.")]
    public string targetSceneName = "LyonVRVity35";

    [Header("Dusk settings (edit these, then press Apply Dusk in the Inspector)")]
    public DuskValues dusk = new DuskValues();

    [Header("Original daylight (recorded before the first Apply; used by Restore)")]
    public DaySnapshot daylight = new DaySnapshot();
}
