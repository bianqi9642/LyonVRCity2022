// File: Assets/Editor/TextureOptimization/GltfTextureOptimizer.cs
//
// Role
// ----
// Editor-only helper for the 6EME / PONT texture optimisation work package
// (see Documentation/TextureOptimization/texture_optimization_report.md).
//
// The 6EME buildings and PONT bridges used JPEGs embedded in their .gltf files; glTFast
// imports those as uncompressed RGBA32 sub-assets (~4.8 GB of the Android build). The
// Python tool Documentation/TextureOptimization/extract_gltf_textures.py copies the ORIGINAL
// JPEG bytes out to ordinary files:
//     Assets/Textures/BATI/6EME/<block>/<ID>_image_<i>.jpg
//     Assets/Textures/PONT/<ID>_image_<i>.jpg
// This class then
//   1. applies Android-specific texture import overrides (ASTC 6x6) to those files,
//   2. REUSES Step 1 of the existing Assets/Editor/BATIMaterialTool.cs (create materials named
//      <ID>_image_<i>) through reflection, so the proven 2EME/3EME/5EME material creation is not
//      re-implemented or modified. Step 2 (apply) is done here in a TARGETED way: the tool's Step 2
//      writes material i into slot i of EVERY renderer, but each 6EME prefab has two child renderers
//      (textured "geometry0" + plain grey "geometry0_1"); the tool would also give the grey one the
//      building texture. Here only the slot that currently holds the glTF *textured* material is
//      replaced, so the slot layout and the grey material stay exactly as they are,
//   3. optionally aligns the new materials' surface values with the original glTF values,
//   4. snapshots / verifies prefab material slots before and after.
//
// Everything here only touches the 6EME + PONT texture / material / prefab folders. It never
// touches scenes, NavMesh, colliders, gltf files, or the existing 2EME/3EME/5EME assets.
//
// Environment variables (batch mode) - all optional:
//   TEXOPT_PONT_MAX      Android max texture size for PONT   (default 2048)
//   TEXOPT_6EME_MAX      Android max texture size for 6EME   (default 4096 = original size)
//   TEXOPT_MATCH_GLTF    "1" (default) = set _Smoothness/_Metallic of the NEW materials to the glTF
//                        values (roughness 1 / metallic 0); "0" = keep the BATIMaterialTool defaults
//                        (smoothness 0.5, identical to the existing 2EME/3EME/5EME materials)
//   TEXOPT_OUT           Output JSON path for snapshots / verification reports.
//   TEXOPT_RENDER_DIR    (RenderCompareBatch) output folder for before/after PNGs.
//   TEXOPT_RENDER_LIST   (RenderCompareBatch) ';'-separated prefab asset paths to render.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class GltfTextureOptimizer
{
    // ---- configuration -------------------------------------------------------------------------
    private class SetConfig
    {
        public string label;
        public string texturesFolder;
        public string materialsFolder;
        public string prefabsFolder;
        public int androidMaxSize;
        public bool perBlock;   // 6EME mirrors the existing <arrondissement>/<block>/ layout
    }

    private static int EnvInt(string name, int def)
    {
        string v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out int r) && r > 0 ? r : def;
    }

    private static List<SetConfig> Sets()
    {
        return new List<SetConfig>
        {
            new SetConfig { label = "6EME", texturesFolder = "Assets/Textures/BATI/6EME",
                materialsFolder = "Assets/Materials/BATI/6EME", prefabsFolder = "Assets/Prefabs/BATI/6EME",
                androidMaxSize = EnvInt("TEXOPT_6EME_MAX", 4096), perBlock = true },
            new SetConfig { label = "PONT", texturesFolder = "Assets/Textures/PONT",
                materialsFolder = "Assets/Materials/PONT", prefabsFolder = "Assets/Prefabs/PONT",
                androidMaxSize = EnvInt("TEXOPT_PONT_MAX", 2048), perBlock = false },
        };
    }

    // ---- menu / batch entry points -------------------------------------------------------------
    [MenuItem("Tools/BATI Materials/Texture Optimisation/1 - Apply Android Texture Settings")]
    public static void ApplyAndroidSettingsMenu() { ApplyAndroidImportSettings(); }

    [MenuItem("Tools/BATI Materials/Texture Optimisation/2 - Create + Apply Materials (6EME, PONT)")]
    public static void CreateAndApplyMenu() { CreateAndApplyMaterials(); }

    public static void SnapshotBatch() { WriteJson(Snapshot("snapshot"), EnvOut("snapshot.json")); }

    public static void ApplyAndroidSettingsBatch() { ApplyAndroidImportSettings(); }

    public static void CreateAndApplyBatch() { CreateAndApplyMaterials(); }

    public static void RunAllBatch()
    {
        string outPath = EnvOut("report.json");
        string dir = Path.GetDirectoryName(outPath);
        WriteJson(Snapshot("before"), Path.Combine(dir, "snapshot_before.json"));
        ApplyAndroidImportSettings();
        CreateAndApplyMaterials();
        WriteJson(Snapshot("after"), Path.Combine(dir, "snapshot_after.json"));
        Debug.Log("[GltfTextureOptimizer] RunAllBatch finished.");
    }

    private static string EnvOut(string fallbackName)
    {
        string v = Environment.GetEnvironmentVariable("TEXOPT_OUT");
        return string.IsNullOrEmpty(v) ? Path.Combine(Path.GetTempPath(), fallbackName) : v;
    }


    // ---- render comparison (needs a GPU: run WITHOUT -nographics) ---------------------------------
    // Renders each listed prefab from two fixed viewpoints in a throw-away empty scene (NO project
    // scene is opened or saved). Run once on an untouched copy ("before") and once after the
    // optimisation ("after"), then diff the PNGs. The Editor renders with the default-platform
    // texture import (not Android ASTC), so this checks material/texture/UV mapping and surface
    // values, not Android compression quality.
    public static void RenderCompareBatch()
    {
        string dir = Environment.GetEnvironmentVariable("TEXOPT_RENDER_DIR");
        string list = Environment.GetEnvironmentVariable("TEXOPT_RENDER_LIST");
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(list)) { Debug.LogError("[GltfTextureOptimizer] TEXOPT_RENDER_DIR / TEXOPT_RENDER_LIST not set"); return; }
        Directory.CreateDirectory(dir);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
        RenderSettings.skybox = null;
        var lightGo = new GameObject("TexOptLight");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional; light.intensity = 1.2f; light.shadows = LightShadows.None;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        var camGo = new GameObject("TexOptCam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.45f, 0.55f, 0.65f);
        cam.nearClipPlane = 0.05f; cam.farClipPlane = 5000f; cam.allowHDR = false; cam.allowMSAA = false;
        QualitySettings.globalTextureMipmapLimit = 0;
        var rt = new RenderTexture(1024, 768, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        cam.targetTexture = rt;

        foreach (string prefabPath in list.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) { Debug.LogWarning("[GltfTextureOptimizer] missing " + prefabPath); continue; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Bounds b = default; bool has = false;
            foreach (Renderer r in inst.GetComponentsInChildren<Renderer>())
            { if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds); }
            float dist = Mathf.Max(b.extents.magnitude * 2.2f, 2f);
            string name = Path.GetFileNameWithoutExtension(prefabPath).Replace(' ', '_');
            float[] azimuths = { 30f, 210f };
            for (int v = 0; v < azimuths.Length; v++)
            {
                Quaternion q = Quaternion.Euler(25f, azimuths[v], 0f);
                camGo.transform.position = b.center - q * Vector3.forward * dist;
                camGo.transform.rotation = q;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(dir, $"{name}_v{v}.png"), tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                RenderTexture.active = null;
            }
            UnityEngine.Object.DestroyImmediate(inst);
        }
        rt.Release();
        Debug.Log("[GltfTextureOptimizer] render compare done -> " + dir);
    }

    // ---- step 1: Android import overrides -----------------------------------------------------
    public static void ApplyAndroidImportSettings()
    {
        AssetDatabase.Refresh();   // make sure the extracted files are imported
        int changed = 0, unchanged = 0, problems = 0;
        foreach (SetConfig cfg in Sets())
        {
            if (!AssetDatabase.IsValidFolder(cfg.texturesFolder)) { Debug.LogWarning("[GltfTextureOptimizer] Missing folder " + cfg.texturesFolder); continue; }
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { cfg.texturesFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) { problems++; continue; }

                // Sanity: the defaults must already match the 2EME/3EME/5EME reference textures.
                if (imp.textureType != TextureImporterType.Default || !imp.mipmapEnabled || !imp.sRGBTexture || imp.isReadable)
                {
                    Debug.LogWarning("[GltfTextureOptimizer] Non-default import settings, check: " + path);
                    problems++;
                }

                TextureImporterPlatformSettings ps = imp.GetPlatformTextureSettings("Android");
                bool already = ps.overridden && ps.maxTextureSize == cfg.androidMaxSize &&
                               ps.format == TextureImporterFormat.ASTC_6x6 &&
                               ps.textureCompression == TextureImporterCompression.Compressed;
                if (already) { unchanged++; continue; }

                ps.name = "Android";
                ps.overridden = true;
                ps.maxTextureSize = cfg.androidMaxSize;
                ps.format = TextureImporterFormat.ASTC_6x6;
                ps.textureCompression = TextureImporterCompression.Compressed;
                ps.compressionQuality = 50;   // "Normal", same as the reference textures
                imp.SetPlatformTextureSettings(ps);
                imp.SaveAndReimport();
                changed++;
            }
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[GltfTextureOptimizer] Android import settings: changed {changed}, already ok {unchanged}, issues {problems}");
    }

    // ---- step 2: materials via the existing BATIMaterialTool ------------------------------------
    public static void CreateAndApplyMaterials()
    {
        AssetDatabase.Refresh();
        bool matchGltf = (Environment.GetEnvironmentVariable("TEXOPT_MATCH_GLTF") ?? "1") != "0";
        Type toolType = FindType("BATIMaterialTool");
        if (toolType == null) { Debug.LogError("[GltfTextureOptimizer] BATIMaterialTool type not found."); return; }

        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        FieldInfo fTex = toolType.GetField("texturesFolder", F), fMat = toolType.GetField("materialsFolder", F),
                  fPre = toolType.GetField("prefabsFolder", F);
        MethodInfo step1 = toolType.GetMethod("CreateMaterialsFromTextures", F);
        if (fTex == null || fMat == null || fPre == null || step1 == null)
        { Debug.LogError("[GltfTextureOptimizer] BATIMaterialTool API changed; aborting."); return; }

        var tool = ScriptableObject.CreateInstance(toolType);
        var newMaterials = new List<string>();
        var outcomes = new ApplyReport();
        foreach (SetConfig cfg in Sets())
        {
            EnsureFolder(cfg.materialsFolder);
            var units = new List<(string tex, string mat, string pre)>();
            if (cfg.perBlock)
            {
                foreach (string blockTex in AssetDatabase.GetSubFolders(cfg.texturesFolder))
                {
                    string block = Path.GetFileName(blockTex);
                    units.Add((blockTex, cfg.materialsFolder + "/" + block, cfg.prefabsFolder + "/" + block));
                }
            }
            else units.Add((cfg.texturesFolder, cfg.materialsFolder, cfg.prefabsFolder));

            foreach (var u in units)
            {
                if (!AssetDatabase.IsValidFolder(u.pre)) { Debug.LogWarning("[GltfTextureOptimizer] No prefab folder " + u.pre); continue; }
                fTex.SetValue(tool, u.tex); fMat.SetValue(tool, u.mat); fPre.SetValue(tool, u.pre);
                step1.Invoke(tool, null);
                ApplyTargeted(cfg.label, u.mat, u.pre, outcomes);
                foreach (string g in AssetDatabase.FindAssets("t:Material", new[] { u.mat }))
                    newMaterials.Add(AssetDatabase.GUIDToAssetPath(g));
            }
        }
        UnityEngine.Object.DestroyImmediate(tool);

        if (matchGltf)
        {
            // The glTF materials have no roughnessFactor (glTF default 1.0) and metallicFactor 0.
            // URP/Lit created by BATIMaterialTool defaults to smoothness 0.5; align the NEW materials
            // with the original look. Existing 2EME/3EME/5EME materials are never touched.
            foreach (string mp in newMaterials)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(mp);
                if (m == null) continue;
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                EditorUtility.SetDirty(m);
            }
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        WriteJson(outcomes, Path.Combine(Path.GetDirectoryName(EnvOut("x.json")), "apply_report.json"));
        Debug.Log($"[GltfTextureOptimizer] Materials processed: {newMaterials.Count} (matchGltfSurface={matchGltf})");
    }


    // ---- targeted step 2 ------------------------------------------------------------------------
    [Serializable] public class ApplyItem { public string set, prefab, status, detail; }
    [Serializable] public class ApplyReport { public List<ApplyItem> items = new List<ApplyItem>(); }

    private static bool IsGltfTexturedMaterial(Material m)
    {
        if (m == null) return false;
        string mp = AssetDatabase.GetAssetPath(m);
        if (!mp.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) return false;   // still the imported glTF material
        foreach (string prop in new[] { "_BaseMap", "baseColorTexture", "_MainTex" })
            if (m.HasProperty(prop) && m.GetTexture(prop) != null) return true;
        return false;
    }

    private static int ImageIndex(string materialName)
    {
        int i = materialName.LastIndexOf("_image_", StringComparison.Ordinal);
        return i >= 0 && int.TryParse(materialName.Substring(i + 7), out int n) ? n : int.MaxValue;
    }

    private static void ApplyTargeted(string label, string materialsFolder, string prefabsFolder, ApplyReport report)
    {
        var mats = AssetDatabase.FindAssets("t:Material", new[] { materialsFolder })
            .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(m => m != null).ToList();
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { prefabsFolder }))
        {
            string pp = AssetDatabase.GUIDToAssetPath(guid);
            string name = Path.GetFileNameWithoutExtension(pp);
            var item = new ApplyItem { set = label, prefab = name };
            report.items.Add(item);
            List<Material> mine = mats.Where(m => m.name.StartsWith(name + "_image_", StringComparison.Ordinal))
                                      .OrderBy(m => ImageIndex(m.name)).ToList();
            GameObject root = PrefabUtility.LoadPrefabContents(pp);
            try
            {
                // collect (renderer, slot) pairs that still hold the glTF textured material
                var targets = new List<(Renderer r, int slot)>();
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] slots = r.sharedMaterials;
                    for (int i = 0; i < slots.Length; i++)
                        if (IsGltfTexturedMaterial(slots[i])) targets.Add((r, i));
                }
                if (mine.Count == 0)
                {
                    item.status = targets.Count == 0 ? "no-texture-in-gltf (unchanged)" : "UNRESOLVED: no new material";
                    item.detail = $"textured slots={targets.Count}";
                    continue;
                }
                if (targets.Count != 1 || mine.Count != 1)
                {
                    item.status = "UNRESOLVED: mapping not 1:1 (left unchanged)";
                    item.detail = $"textured slots={targets.Count}, new materials={mine.Count}";
                    continue;
                }
                var (rend, slot) = targets[0];
                Material[] cur = rend.sharedMaterials;
                cur[slot] = mine[0];
                rend.sharedMaterials = cur;
                PrefabUtility.SaveAsPrefabAsset(root, pp);
                item.status = "applied";
                item.detail = $"{GetPath(rend.transform, root.transform)} slot {slot} -> {mine[0].name}";
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }

    private static Type FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(name);
            if (t != null) return t;
        }
        return null;
    }

    private static void EnsureFolder(string assetFolder)
    {
        if (AssetDatabase.IsValidFolder(assetFolder)) return;
        string parent = Path.GetDirectoryName(assetFolder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(assetFolder));
    }

    // ---- snapshot of prefab renderer material slots ---------------------------------------------
    [Serializable] public class SlotInfo { public string material, shader, materialPath, baseMapPath; public int baseMapW, baseMapH; public string baseMapFormat; }
    [Serializable] public class RendererInfo { public string path, mesh; public int subMeshCount; public List<SlotInfo> slots = new List<SlotInfo>(); }
    [Serializable] public class PrefabInfo { public string set, prefab, prefabPath; public List<RendererInfo> renderers = new List<RendererInfo>(); }
    [Serializable] public class SnapshotFile { public string label; public List<PrefabInfo> prefabs = new List<PrefabInfo>(); }

    public static SnapshotFile Snapshot(string label)
    {
        var file = new SnapshotFile { label = label };
        foreach (SetConfig cfg in Sets())
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { cfg.prefabsFolder }))
            {
                string pp = AssetDatabase.GUIDToAssetPath(guid);
                var info = new PrefabInfo { set = cfg.label, prefab = Path.GetFileNameWithoutExtension(pp), prefabPath = pp };
                GameObject root = PrefabUtility.LoadPrefabContents(pp);
                try
                {
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        var ri = new RendererInfo { path = GetPath(r.transform, root.transform) };
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null) { ri.mesh = mf.sharedMesh.name; ri.subMeshCount = mf.sharedMesh.subMeshCount; }
                        foreach (Material m in r.sharedMaterials)
                        {
                            var si = new SlotInfo();
                            if (m != null)
                            {
                                si.material = m.name; si.shader = m.shader != null ? m.shader.name : "";
                                si.materialPath = AssetDatabase.GetAssetPath(m);
                                Texture t = null;
                                if (m.HasProperty("_BaseMap")) t = m.GetTexture("_BaseMap");
                                if (t == null && m.HasProperty("baseColorTexture")) t = m.GetTexture("baseColorTexture");
                                if (t == null && m.HasProperty("_MainTex")) t = m.GetTexture("_MainTex");
                                if (t != null)
                                {
                                    si.baseMapPath = AssetDatabase.GetAssetPath(t);
                                    si.baseMapW = t.width; si.baseMapH = t.height;
                                    if (t is Texture2D t2) si.baseMapFormat = t2.format.ToString();
                                }
                            }
                            ri.slots.Add(si);
                        }
                        info.renderers.Add(ri);
                    }
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
                file.prefabs.Add(info);
            }
        }
        return file;
    }

    private static string GetPath(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (Transform c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
        parts.Reverse();
        return parts.Count == 0 ? "<root>" : string.Join("/", parts);
    }

    private static void WriteJson(object o, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(o, true));
        Debug.Log("[GltfTextureOptimizer] wrote " + path);
    }
}
