using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Experimental;
using System.IO;
using System.Linq;
using System.Collections.Generic;

/// <summary>
/// Batch apply textures to materials based on naming convention:
/// - Prefab:  <NAME>.prefab  (e.g. 69382AD1.prefab)
/// - Material: _<NAME>_eff.mat (e.g. _69382AD1_eff.mat)
/// - Texture:  <NAME>.jpg      (e.g. 69382AD1.jpg) located under Assets/Textures (recursive)
///
/// Behavior:
/// - For each prefab under "Prefab Folder", determine NAME = prefab filename (without extension).
/// - Build expected material name: "_" + NAME + "_eff"
/// - Look up a texture under Assets/Textures whose filename (without extension) matches NAME (case-insensitive).
/// - For each material slot in prefab renderers: if material.name matches expected pattern, duplicate the original
///   material asset into outMaterialsFolder/NAME/ or create a new material if original asset cannot be copied.
/// - Assign the found texture as Base Color to the duplicated/created material (try common property names).
/// - Replace the material on the prefab and save the prefab asset.
/// 
/// Notes:
/// - This script modifies prefab assets. It uses PrefabUtility.LoadPrefabContents / SaveAsPrefabAsset to edit safely.
/// - The search for textures only targets .jpg/.jpeg files by default (as requested). Can be extended easily.
/// </summary>
public class ApplyTexturesByName : EditorWindow
{
    // Prefab folder inside the Assets folder where we search for prefabs to process
    string prefabFolder = "Assets/Prefabs";

    // Where to look for textures (recursive). Default as requested.
    string texturesRoot = "Assets/Textures";

    // Where to put duplicated/created materials
    string outMaterialsFolder = "Assets/Materials/FromGLTF";

    // Fallback shader choice when we need to create a material
    enum ShaderChoice { Standard, URP_Lit }
    ShaderChoice shaderChoice = ShaderChoice.Standard;

    [MenuItem("Tools/Apply Textures By Name")]
    public static void ShowWindow()
    {
        GetWindow<ApplyTexturesByName>("Apply Textures By Name");
    }

    void OnGUI()
    {
        GUILayout.Label("Batch Apply Textures By Name", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        GUILayout.Label("Prefab Folder (inside Assets)", EditorStyles.label);
        EditorGUILayout.BeginHorizontal();
        prefabFolder = EditorGUILayout.TextField(prefabFolder);
        if (GUILayout.Button("...", GUILayout.MaxWidth(30)))
        {
            string p = EditorUtility.OpenFolderPanel("Select Prefab Folder", Application.dataPath, "");
            if (!string.IsNullOrEmpty(p)) prefabFolder = "Assets" + p.Replace(Application.dataPath, "").Replace('\\', '/');
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Label("Textures Root (inside Assets)", EditorStyles.label);
        EditorGUILayout.BeginHorizontal();
        texturesRoot = EditorGUILayout.TextField(texturesRoot);
        if (GUILayout.Button("...", GUILayout.MaxWidth(30)))
        {
            string p = EditorUtility.OpenFolderPanel("Select Textures Root", Application.dataPath, "");
            if (!string.IsNullOrEmpty(p)) texturesRoot = "Assets" + p.Replace(Application.dataPath, "").Replace('\\', '/');
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Label("Output Materials Folder", EditorStyles.label);
        outMaterialsFolder = EditorGUILayout.TextField(outMaterialsFolder);

        shaderChoice = (ShaderChoice)EditorGUILayout.EnumPopup("Fallback Shader", shaderChoice);

        EditorGUILayout.HelpBox("This tool looks for textures under the specified Textures Root (only .jpg/.jpeg). Prefab name should match texture filename. Material name expected: '_' + prefabName + '_eff'.", MessageType.Info);

        if (GUILayout.Button("Process All Prefabs"))
        {
            ProcessAllPrefabs();
        }
    }

    /// <summary>
    /// Main pipeline:
    /// 1) Build textures lookup from texturesRoot (key = lowercase filename without extension)
    /// 2) Find all prefabs under prefabFolder
    /// 3) For each prefab:
    ///    - Compute NAME and expected material name: "_" + NAME + "_eff"
    ///    - Find texture by NAME in the lookup
    ///    - Load prefab contents, iterate renderers and materials, and for matching material names:
    ///         duplicate material asset or create a new one, assign texture to Base Color, replace slot
    ///    - Save prefab asset if modified
    /// </summary>
    void ProcessAllPrefabs()
    {
        if (!AssetDatabase.IsValidFolder(prefabFolder))
        {
            EditorUtility.DisplayDialog("Error", $"Prefab folder not found: {prefabFolder}", "OK");
            return;
        }
        if (!AssetDatabase.IsValidFolder(texturesRoot))
        {
            EditorUtility.DisplayDialog("Error", $"Textures root not found: {texturesRoot}", "OK");
            return;
        }
        if (!AssetDatabase.IsValidFolder(outMaterialsFolder))
        {
            AssetDatabase.CreateFolder("Assets", outMaterialsFolder.Substring("Assets/".Length));
        }

        // Build texture lookup dictionary for fast access
        var texturesLookup = BuildTexturesLookup(texturesRoot);

        // Find all prefabs under prefabFolder
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { prefabFolder });
        var prefabPaths = prefabGuids.Select(g => AssetDatabase.GUIDToAssetPath(g)).ToArray();

        int totalProcessedPrefabs = 0;
        int totalMaterialReplacements = 0;

        foreach (var prefabPath in prefabPaths)
        {
            string prefabName = Path.GetFileNameWithoutExtension(prefabPath);
            if (string.IsNullOrEmpty(prefabName)) continue;

            // expected material name: _<NAME>_eff
            string expectedMatName = "_" + prefabName + "_eff";
            string expectedMatNameLower = expectedMatName.ToLower();

            // find texture by prefabName
            Texture2D foundTex = null;
            string texKey = prefabName.ToLower();
            if (texturesLookup.TryGetValue(texKey, out string texAssetPath))
            {
                foundTex = AssetDatabase.LoadAssetAtPath<Texture2D>(texAssetPath);
                if (foundTex == null)
                {
                    Debug.LogWarning($"[ApplyTexturesByName] Texture found in lookup but failed to load: {texAssetPath}");
                }
            }

            bool prefabChanged = false;
            int prefabReplacements = 0;

            // Load prefab contents for safe editing
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                Debug.LogWarning($"[ApplyTexturesByName] Failed to load prefab contents: {prefabPath}");
                continue;
            }

            // Iterate all renderers
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var rend in renderers)
            {
                var mats = rend.sharedMaterials;
                bool anySlotChanged = false;

                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;

                    // match material by exact expected name or contains (case-insensitive)
                    bool isMatch = false;
                    if (mat.name == expectedMatName) isMatch = true;
                    else if (mat.name.ToLower().Contains(expectedMatNameLower)) isMatch = true;

                    if (!isMatch) continue;

                    // Prepare destination folder: outMaterialsFolder/<prefabName>/
                    string destFolder = Path.Combine(outMaterialsFolder, prefabName).Replace('\\', '/');
                    if (!AssetDatabase.IsValidFolder(destFolder))
                    {
                        // create folder chain
                        string[] pieces = destFolder.Substring("Assets/".Length).Split('/');
                        string cur = "Assets";
                        foreach (var p in pieces)
                        {
                            string next = cur + "/" + p;
                            if (!AssetDatabase.IsValidFolder(next))
                                AssetDatabase.CreateFolder(cur, p);
                            cur = next;
                        }
                    }

                    Material newMat = null;
                    string originalMatAssetPath = AssetDatabase.GetAssetPath(mat);

                    if (!string.IsNullOrEmpty(originalMatAssetPath))
                    {
                        // Duplicate the material asset into destination folder
                        string safeMatName = $"{prefabName}_{mat.name}.mat";
                        string destPath = Path.Combine(destFolder, safeMatName).Replace('\\', '/');
                        destPath = AssetDatabase.GenerateUniqueAssetPath(destPath);
                        bool copyOk = AssetDatabase.CopyAsset(originalMatAssetPath, destPath);
                        if (copyOk)
                        {
                            newMat = AssetDatabase.LoadAssetAtPath<Material>(destPath);
                        }
                        else
                        {
                            Debug.LogWarning($"[ApplyTexturesByName] Failed to copy material asset {originalMatAssetPath} -> {destPath}");
                        }
                    }

                    if (newMat == null)
                    {
                        // Create new material using fallback shader
                        Shader shader = shaderChoice == ShaderChoice.Standard ? Shader.Find("Standard") : Shader.Find("Universal Render Pipeline/Lit");
                        newMat = new Material(shader);
                        string createPath = Path.Combine(destFolder, $"{prefabName}_{mat.name}.mat").Replace('\\', '/');
                        createPath = AssetDatabase.GenerateUniqueAssetPath(createPath);
                        AssetDatabase.CreateAsset(newMat, createPath);
                        AssetDatabase.SaveAssets();
                    }

                    // Assign texture as Base Color if found
                    if (foundTex != null && newMat != null)
                    {
                        // try several common property names
                        string[] props = new[] { "_BaseMap", "_MainTex", "_BaseColorMap", "_BaseColor" };
                        bool assigned = false;
                        foreach (var p in props)
                        {
                            if (newMat.HasProperty(p))
                            {
                                newMat.SetTexture(p, foundTex);
                                assigned = true;
                                break;
                            }
                        }
                        if (!assigned && newMat.HasProperty("_MainTex"))
                        {
                            newMat.SetTexture("_MainTex", foundTex);
                        }
                        EditorUtility.SetDirty(newMat);
                    }

                    // Replace material in renderer slot
                    mats[i] = newMat;
                    anySlotChanged = true;
                    prefabReplacements++;
                    totalMaterialReplacements++;
                } // for each slot

                if (anySlotChanged)
                {
                    rend.sharedMaterials = mats;
                    prefabChanged = true;
                }
            } // foreach renderer

            if (prefabChanged)
            {
                // Save modified prefab back to asset
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                totalProcessedPrefabs++;
            }

            // Always unload prefab contents
            PrefabUtility.UnloadPrefabContents(root);

            if (prefabReplacements > 0)
            {
                Debug.Log($"[ApplyTexturesByName] Prefab {prefabPath}: replaced {prefabReplacements} material(s).");
            }
        } // foreach prefab

        AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog("Done", $"Processed prefabs: {totalProcessedPrefabs}\nTotal material replacements: {totalMaterialReplacements}", "OK");
        Debug.Log($"[ApplyTexturesByName] finished. Processed prefabs: {totalProcessedPrefabs}, total replacements: {totalMaterialReplacements}");
    }

    /// <summary>
    /// Build a lookup dictionary of textures under a given folder (inside Assets).
    /// Key = lowercase filename without extension -> asset path
    /// Only include .jpg and .jpeg files per your specification.
    /// </summary>
    Dictionary<string, string> BuildTexturesLookup(string assetsFolder)
    {
        var dict = new Dictionary<string, string>();
        string folderFullPath = Path.Combine(Application.dataPath, assetsFolder.Substring("Assets/".Length));
        if (!Directory.Exists(folderFullPath))
        {
            Debug.LogWarning($"[BuildTexturesLookup] Folder not found: {assetsFolder}");
            return dict;
        }

        var files = Directory.GetFiles(folderFullPath, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLower();
                return ext == ".jpg" || ext == ".jpeg";
            });

        foreach (var f in files)
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(f).ToLower();
            string assetPath = ToAssetPath(f);
            if (!dict.ContainsKey(nameNoExt))
            {
                dict.Add(nameNoExt, assetPath);
            }
            else
            {
                Debug.LogWarning($"[BuildTexturesLookup] Duplicate texture name found for '{nameNoExt}': existing {dict[nameNoExt]}, ignored {assetPath}");
            }
        }

        return dict;
    }

    /// <summary>
    /// Convert a full filesystem path under the Assets folder to a Unity asset path "Assets/..."
    /// </summary>
    string ToAssetPath(string fullPath)
    {
        fullPath = fullPath.Replace('\\', '/');
        if (fullPath.StartsWith(Application.dataPath))
        {
            return "Assets" + fullPath.Substring(Application.dataPath.Length);
        }
        int idx = fullPath.IndexOf("/Assets/");
        if (idx >= 0)
        {
            return fullPath.Substring(idx + 1);
        }
        return fullPath; // fallback; may not be valid asset path
    }
}
