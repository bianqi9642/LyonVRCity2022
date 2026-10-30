// DuskEnvironmentProfileEditor.cs
//
// EDITOR script. Inspector for DuskEnvironmentProfile with three explicit buttons:
//   Apply Dusk                 - write the dusk values into the open scene (records the daylight snapshot first if none exists).
//   Restore Daylight           - write the recorded daylight values back.
//   Re-capture Daylight        - overwrite the snapshot with the CURRENT scene values (asks for confirmation).
// Apply / Restore change the sun Directional Light (Undo supported) and the scene's RenderSettings (Lighting window values), then mark the
// scene dirty. The scene is NOT saved automatically; save it (File > Save) to keep the result. Nothing runs outside these button presses.
// All comments in English per project convention.

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[CustomEditor(typeof(DuskEnvironmentProfile))]
public class DuskEnvironmentProfileEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var p = (DuskEnvironmentProfile)target;

        EditorGUILayout.HelpBox(
            "1. Open the experiment scene (LyonVRVity35).\n" +
            "2. Edit the Dusk settings below.\n" +
            "3. Press 'Apply Dusk' (changes the sun light and Lighting > Environment of the OPEN scene), look at the Scene/Game view, repeat 2-3.\n" +
            "4. Save the scene (File > Save). 'Restore Daylight' puts the recorded original values back.\n" +
            "Daylight snapshot recorded: " + (p.daylight.captured ? "yes (" + p.daylight.sceneName + ")" : "NO - it will be taken automatically on the first Apply"),
            MessageType.Info);

        DrawDefaultInspector();

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Apply Dusk", GUILayout.Height(28))) Apply(p, true);
            using (new EditorGUI.DisabledScope(!p.daylight.captured))
                if (GUILayout.Button("Restore Daylight", GUILayout.Height(28))) Apply(p, false);
        }
        if (GUILayout.Button("Re-capture Daylight from the open scene"))
        {
            if (EditorUtility.DisplayDialog("Overwrite daylight snapshot?",
                "This replaces the recorded original daylight values with the CURRENT values of the open scene. Only do this if the scene currently shows the lighting you want to be able to restore.",
                "Overwrite", "Cancel"))
            {
                Light sun = FindSun();
                if (sun == null) { EditorUtility.DisplayDialog("No sun", "No enabled Directional Light found in the active scene.", "OK"); return; }
                Capture(p, sun);
            }
        }
    }

    // The sun: RenderSettings.sun if set, else the enabled Directional Light with the highest intensity in the active scene.
    private static Light FindSun()
    {
        if (RenderSettings.sun != null && RenderSettings.sun.type == LightType.Directional) return RenderSettings.sun;
        Light best = null;
        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Light l in root.GetComponentsInChildren<Light>(false))
                if (l.type == LightType.Directional && l.enabled && (best == null || l.intensity > best.intensity)) best = l;
        return best;
    }

    private static void Capture(DuskEnvironmentProfile p, Light sun)
    {
        Undo.RecordObject(p, "Capture daylight snapshot");
        var d = p.daylight;
        d.captured = true;
        d.sceneName = SceneManager.GetActiveScene().name;
        d.sunRotation = sun.transform.rotation;
        d.sunColor = sun.color;
        d.sunIntensity = sun.intensity;
        d.skyboxMaterial = RenderSettings.skybox;
        d.ambientMode = RenderSettings.ambientMode;
        d.ambientSky = RenderSettings.ambientSkyColor;
        d.ambientEquator = RenderSettings.ambientEquatorColor;
        d.ambientGround = RenderSettings.ambientGroundColor;
        d.ambientIntensity = RenderSettings.ambientIntensity;
        d.fogEnabled = RenderSettings.fog;
        d.fogColor = RenderSettings.fogColor;
        d.fogMode = RenderSettings.fogMode;
        d.fogDensity = RenderSettings.fogDensity;
        d.fogStart = RenderSettings.fogStartDistance;
        d.fogEnd = RenderSettings.fogEndDistance;
        d.reflectionIntensity = RenderSettings.reflectionIntensity;
        EditorUtility.SetDirty(p);
        AssetDatabase.SaveAssets();
    }

    private static void Apply(DuskEnvironmentProfile p, bool dusk)
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded) return;
        if (!string.IsNullOrEmpty(p.targetSceneName) && scene.name != p.targetSceneName &&
            !EditorUtility.DisplayDialog("Different scene", "The active scene is '" + scene.name + "', this profile targets '" + p.targetSceneName + "'. Apply anyway?", "Apply", "Cancel"))
            return;

        Light sun = FindSun();
        if (sun == null) { EditorUtility.DisplayDialog("No sun", "No enabled Directional Light found in the active scene.", "OK"); return; }

        if (dusk && !p.daylight.captured) Capture(p, sun);   // never lose the original look
        if (!dusk && !p.daylight.captured) return;

        DuskEnvironmentProfile.EnvironmentValues v = dusk ? (DuskEnvironmentProfile.EnvironmentValues)p.dusk : p.daylight;

        Undo.RecordObject(sun, dusk ? "Apply dusk" : "Restore daylight");
        Undo.RecordObject(sun.transform, dusk ? "Apply dusk" : "Restore daylight");
        sun.transform.rotation = dusk ? Quaternion.Euler(p.dusk.sunEuler) : p.daylight.sunRotation;
        sun.color = v.sunColor;
        sun.intensity = v.sunIntensity;

        RenderSettings.skybox = v.skyboxMaterial;
        RenderSettings.ambientMode = v.ambientMode;
        RenderSettings.ambientSkyColor = v.ambientSky;
        RenderSettings.ambientEquatorColor = v.ambientEquator;
        RenderSettings.ambientGroundColor = v.ambientGround;
        RenderSettings.ambientIntensity = v.ambientIntensity;
        RenderSettings.fog = v.fogEnabled;
        RenderSettings.fogColor = v.fogColor;
        RenderSettings.fogMode = v.fogMode;
        RenderSettings.fogDensity = v.fogDensity;
        RenderSettings.fogStartDistance = v.fogStart;
        RenderSettings.fogEndDistance = v.fogEnd;
        RenderSettings.reflectionIntensity = v.reflectionIntensity;

        EditorUtility.SetDirty(sun);
        EditorSceneManager.MarkSceneDirty(scene);
        SceneView.RepaintAll();
        Debug.Log("[DuskEnvironment] " + (dusk ? "Applied dusk" : "Restored daylight") + " to scene '" + scene.name + "' (sun: '" + sun.name + "'). Save the scene to keep it.");
    }
}
