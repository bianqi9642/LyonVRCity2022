// Save this file as Assets/Editor/ConvertGltfToPrefabs.cs
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// Convert all .gltf/.glb files found under Assets/Models/City into .prefab files placed in Assets/Prefabs.
/// This script tries multiple strategies:
///  1) If a same-name .prefab already exists next to the .gltf, copy it to Assets/Prefabs.
///  2) Try to load a GameObject asset via AssetDatabase (some glTF importers create GameObjects/assets on import).
///     IMPORTANT: If we get a persistent GameObject asset we must instantiate it into the scene before saving as prefab.
///  3) If both fail, create a small placeholder GameObject and save it as a prefab so every model has a prefab target.
/// 
/// NOTE:
/// - For full fidelity automatic conversion you need a glTF importer package in your project (glTFast, UnityGLTF, GLTFUtility, etc.).
/// - This tool does not implement a low-level glTF parser; it relies on whatever importer the project already has.
/// </summary>
public class ConvertGltfToPrefabs : EditorWindow
{
    string sourceRoot = "Assets/Models/LYON_5EME_BATI_2018";
    string targetRoot = "Assets/Prefabs";
    bool keepFolderStructure = false;
    bool createPlaceholders = true;

    [MenuItem("Tools/Convert GLTF -> Prefabs")]
    static void OpenWindow()
    {
        GetWindow<ConvertGltfToPrefabs>("GLTF -> Prefabs");
    }

    void OnGUI()
    {
        GUILayout.Label("Convert .gltf/.glb to .prefab", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        sourceRoot = EditorGUILayout.TextField("Source root (glTF files)", sourceRoot);
        targetRoot = EditorGUILayout.TextField("Target root (prefabs)", targetRoot);
        keepFolderStructure = EditorGUILayout.Toggle("Keep subfolder structure in target", keepFolderStructure);
        createPlaceholders = EditorGUILayout.Toggle("Create placeholder prefab if import fails", createPlaceholders);

        EditorGUILayout.Space();
        if (GUILayout.Button("Run conversion"))
        {
            ConvertAllGltfToPrefabs();
        }
    }

    void ConvertAllGltfToPrefabs()
    {
        if (!AssetDatabase.IsValidFolder(sourceRoot))
        {
            EditorUtility.DisplayDialog("Error", $"Source folder not found: {sourceRoot}", "OK");
            return;
        }

        // Ensure target folder exists
        if (!AssetDatabase.IsValidFolder(targetRoot))
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath.Substring(0, Application.dataPath.Length - 6), targetRoot.TrimStart('/')));
            AssetDatabase.Refresh();
        }

        // Find all .gltf/.glb under sourceRoot
        string absSource = Application.dataPath.Substring(0, Application.dataPath.Length - 6) + sourceRoot.TrimStart('/');
        var gltfFiles = new List<string>();
        if (Directory.Exists(absSource))
        {
            var files = Directory.GetFiles(absSource, "*.*", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".gltf" || ext == ".glb")
                {
                    // convert absolute path back to asset path
                    string assetPath = "Assets" + f.Substring(Application.dataPath.Length).Replace('\\', '/');
                    gltfFiles.Add(assetPath);
                }
            }
        }

        if (gltfFiles.Count == 0)
        {
            EditorUtility.DisplayDialog("Info", "No .gltf or .glb files found under: " + sourceRoot, "OK");
            return;
        }

        int success = 0, placeholders = 0, skipped = 0;
        var failedList = new List<string>();

        try
        {
            for (int i = 0; i < gltfFiles.Count; i++)
            {
                string gltfPath = gltfFiles[i];
                EditorUtility.DisplayProgressBar("Converting glTF -> Prefab", $"Processing {Path.GetFileName(gltfPath)} ({i+1}/{gltfFiles.Count})", (float)i / gltfFiles.Count);

                string baseName = Path.GetFileNameWithoutExtension(gltfPath);

                // Determine relative folder under sourceRoot for optional structure keeping
                string targetFolder = targetRoot;
                if (keepFolderStructure)
                {
                    string relative = Path.GetDirectoryName(gltfPath).Replace('\\','/'); // e.g. Assets/Models/City/Sub/...
                    if (relative.StartsWith(sourceRoot))
                    {
                        string sub = relative.Substring(sourceRoot.Length).Trim('/');
                        if (!string.IsNullOrEmpty(sub))
                            targetFolder = Path.Combine(targetRoot, sub).Replace('\\','/');
                    }
                    // ensure folder exists
                    if (!AssetDatabase.IsValidFolder(targetFolder))
                    {
                        string[] parts = targetFolder.Split('/');
                        string current = parts[0];
                        for (int p = 1; p < parts.Length; p++)
                        {
                            string next = current + "/" + parts[p];
                            if (!AssetDatabase.IsValidFolder(next))
                            {
                                AssetDatabase.CreateFolder(current, parts[p]);
                            }
                            current = next;
                        }
                    }
                }

                // target prefab path (ensure unique)
                string targetPrefabPath = $"{targetFolder}/{baseName}.prefab";
                targetPrefabPath = AssetDatabase.GenerateUniqueAssetPath(targetPrefabPath);

                bool converted = false;

                // Strategy A: check for same-name .prefab near the gltf (some importers generate a prefab automatically)
                string gltfDir = Path.GetDirectoryName(gltfPath).Replace('\\','/');
                string candidatePrefab = $"{gltfDir}/{baseName}.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(candidatePrefab) != null)
                {
                    // Copy prefab asset to target folder (preserve original prefab as well)
                    AssetDatabase.CopyAsset(candidatePrefab, targetPrefabPath);
                    AssetDatabase.SaveAssets();
                    converted = true;
                    success++;
                    continue;
                }

                // Strategy B: try to load GameObject main asset from the gltf path (some importers register a GameObject asset)
                Object mainAsset = AssetDatabase.LoadMainAssetAtPath(gltfPath);
                if (mainAsset is GameObject)
                {
                    // IMPORTANT: mainAsset is a persistent asset in the Project. We must instantiate it into the scene
                    // to obtain a scene instance (non-persistent) before calling SaveAsPrefabAssetAndConnect.
                    GameObject assetGo = mainAsset as GameObject;

                    // Instantiate the asset into the scene as a temporary instance
                    GameObject instance = null;
                    try
                    {
                        // Use PrefabUtility.InstantiatePrefab when possible to preserve prefab links,
                        // but it also returns a scene instance which we can save.
                        instance = (GameObject)PrefabUtility.InstantiatePrefab(assetGo);
                        if (instance == null)
                        {
                            // fallback to plain Instantiate if InstantiatePrefab failed
                            instance = GameObject.Instantiate(assetGo);
                            instance.name = assetGo.name;
                        }
                        else
                        {
                            instance.name = assetGo.name;
                        }

                        // Save the scene instance as a new prefab asset in target folder and connect it
                        PrefabUtility.SaveAsPrefabAssetAndConnect(instance, targetPrefabPath, InteractionMode.UserAction);

                        converted = true;
                        success++;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"Failed to save prefab from main asset for {gltfPath}: {ex.Message}");
                        // if failed, ensure we will create placeholder later if enabled
                    }
                    finally
                    {
                        // Clean up the temporary instance from the scene
                        if (instance != null)
                            GameObject.DestroyImmediate(instance);
                    }

                    if (converted) continue;
                }

                // Strategy C: try to find any GameObject asset that references the gltf by name in AssetDatabase
                // (this helps when importer created a GameObject somewhere else)
                string[] guids = AssetDatabase.FindAssets(baseName + " t:Prefab");
                foreach (var g in guids)
                {
                    string p = AssetDatabase.GUIDToAssetPath(g);
                    if (Path.GetFileNameWithoutExtension(p).Equals(baseName, System.StringComparison.InvariantCultureIgnoreCase))
                    {
                        // copy this prefab to target
                        AssetDatabase.CopyAsset(p, targetPrefabPath);
                        AssetDatabase.SaveAssets();
                        converted = true;
                        success++;
                        break;
                    }
                }
                if (converted) continue;

                // Strategy D: load all assets at path and find a GameObject among them
                Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(gltfPath);
                foreach (var a in subAssets)
                {
                    if (a is GameObject)
                    {
                        GameObject assetGo = a as GameObject;

                        // As above: instantiate a scene copy first, then save it as prefab.
                        GameObject instance = null;
                        try
                        {
                            instance = (GameObject)PrefabUtility.InstantiatePrefab(assetGo);
                            if (instance == null)
                            {
                                instance = GameObject.Instantiate(assetGo);
                                instance.name = assetGo.name;
                            }
                            else
                            {
                                instance.name = assetGo.name;
                            }

                            PrefabUtility.SaveAsPrefabAssetAndConnect(instance, targetPrefabPath, InteractionMode.UserAction);
                            converted = true;
                            success++;
                            break;
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogError($"Failed to save prefab from sub-asset for {gltfPath}: {ex.Message}");
                        }
                        finally
                        {
                            if (instance != null)
                                GameObject.DestroyImmediate(instance);
                        }
                    }
                }
                if (converted) continue;

                // If still not converted and createPlaceholders enabled, create a small placeholder prefab
                if (createPlaceholders)
                {
                    GameObject placeholder = new GameObject(baseName + "_placeholder");
                    // optional: add a simple visible cube so it's visible in scene prefabs
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = "visual";
                    cube.transform.SetParent(placeholder.transform, false);
                    cube.transform.localScale = new Vector3(2f, 2f, 2f);
                    // store original source path in a small helper component (optional)
                    SourceInfo si = placeholder.AddComponent<SourceInfo>();
                    si.sourcePath = gltfPath;

                    // This placeholder is a scene object (not persistent) so SaveAsPrefabAssetAndConnect works fine.
                    PrefabUtility.SaveAsPrefabAssetAndConnect(placeholder, targetPrefabPath, InteractionMode.UserAction);
                    // cleanup temporary objects
                    GameObject.DestroyImmediate(placeholder);
                    placeholders++;
                    converted = true;
                }

                if (!converted)
                {
                    failedList.Add(gltfPath);
                    skipped++;
                }
            } // foreach gltf
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
        }

        // Log summary
        Debug.Log($"GLTF -> Prefab: succeeded {success}, placeholders created {placeholders}, skipped {skipped}.");
        if (failedList.Count > 0)
        {
            Debug.LogWarning("The following glTF files could not be converted automatically (no importer / no generated GameObject). Please install a glTF importer or handle manually:\n" + string.Join("\n", failedList.ToArray()));
        }

        EditorUtility.DisplayDialog("Conversion finished", $"Succeeded: {success}\nPlaceholders: {placeholders}\nSkipped: {skipped}", "OK");
    }

    /// <summary>
    /// Simple MonoBehaviour to store original source path so placeholders carry metadata.
    /// This component will be saved inside placeholder prefabs so you can inspect / re-import later.
    /// </summary>
    public class SourceInfo : MonoBehaviour
    {
        // This field is intentionally public and serializable so Unity stores it in the prefab.
        public string sourcePath;
    }
}
