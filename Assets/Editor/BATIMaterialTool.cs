// File: Assets/Editor/BATIMaterialTool.cs
// A configurable Unity Editor tool to:
//
// Step 1:
//   Create materials from textures in a user-defined folder
//
// Step 2:
//   Apply materials to prefabs in a user-defined folder
//
// Matching rule:
//   Prefab name = <ID>
//   Material name = <ID>_image_<index>
//   Materials are assigned to renderer material slots by index
//
// All paths are entered manually in the EditorWindow.

using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Linq;

public class BATIMaterialTool : EditorWindow
{
    // User-configurable paths (Unity asset paths)
    private string texturesFolder  = "Assets/Textures/BATI";
    private string materialsFolder = "Assets/Materials/BATI";
    private string prefabsFolder   = "Assets/Prefabs/BATI";

    [MenuItem("Tools/BATI Materials/Material Tool")]
    public static void OpenWindow()
    {
        GetWindow<BATIMaterialTool>("BATI Material Tool");
    }

    private void OnGUI()
    {
        GUILayout.Label("BATI Material Pipeline", EditorStyles.boldLabel);

        GUILayout.Space(5);

        // -------- Paths --------
        texturesFolder  = DrawFolderField("Textures Folder", texturesFolder);
        materialsFolder = DrawFolderField("Materials Folder", materialsFolder);
        prefabsFolder   = DrawFolderField("Prefabs Folder", prefabsFolder);

        GUILayout.Space(10);

        // -------- Actions --------
        if (GUILayout.Button("Step 1 - Create Materials From Textures"))
        {
            CreateMaterialsFromTextures();
        }

        if (GUILayout.Button("Step 2 - Apply Materials To Prefabs"))
        {
            ApplyMaterialsToPrefabs();
        }
    }

    // Draw a text field + folder picker for a Unity asset folder
    private string DrawFolderField(string label, string currentPath)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(120));
        currentPath = GUILayout.TextField(currentPath);

        if (GUILayout.Button("...", GUILayout.Width(30)))
        {
            string abs = EditorUtility.OpenFolderPanel(label, Application.dataPath, "");
            if (!string.IsNullOrEmpty(abs) && abs.StartsWith(Application.dataPath))
            {
                currentPath = "Assets" + abs.Substring(Application.dataPath.Length);
            }
        }
        GUILayout.EndHorizontal();
        return currentPath;
    }

    // =========================
    // STEP 1
    // =========================

    private void CreateMaterialsFromTextures()
    {
        if (!AssetDatabase.IsValidFolder(texturesFolder))
        {
            Debug.LogError("Invalid textures folder: " + texturesFolder);
            return;
        }

        if (!AssetDatabase.IsValidFolder(materialsFolder))
        {
            string parent = Path.GetDirectoryName(materialsFolder);
            string name   = Path.GetFileName(materialsFolder);
            AssetDatabase.CreateFolder(parent, name);
            AssetDatabase.Refresh();
        }

        // Prefer URP Lit, fallback to Standard
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");

        string[] textureGuids = AssetDatabase.FindAssets("t:Texture2D", new[] { texturesFolder });

        int created = 0;
        int updated = 0;

        foreach (string guid in textureGuids)
        {
            string texPath = AssetDatabase.GUIDToAssetPath(guid);
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex == null) continue;

            string matName = Path.GetFileNameWithoutExtension(texPath);
            string matPath = $"{materialsFolder}/{matName}.mat";

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);

            if (mat == null)
            {
                mat = new Material(shader);
                mat.name = matName;

                if (mat.HasProperty("_BaseMap"))
                    mat.SetTexture("_BaseMap", tex);
                else
                    mat.mainTexture = tex;

                AssetDatabase.CreateAsset(mat, matPath);
                created++;
            }
            else
            {
                if (mat.HasProperty("_BaseMap"))
                    mat.SetTexture("_BaseMap", tex);
                else
                    mat.mainTexture = tex;

                EditorUtility.SetDirty(mat);
                updated++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[BATI] Step 1 finished. Created: {created}, Updated: {updated}");
    }

    // =========================
    // STEP 2
    // =========================

    private void ApplyMaterialsToPrefabs()
    {
        if (!AssetDatabase.IsValidFolder(materialsFolder) ||
            !AssetDatabase.IsValidFolder(prefabsFolder))
        {
            Debug.LogError("Invalid materials or prefabs folder.");
            return;
        }

        // Load all materials
        string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { materialsFolder });
        List<Material> allMaterials = matGuids
            .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(m => m != null)
            .ToList();

        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { prefabsFolder });

        int prefabsUpdated = 0;

        foreach (string guid in prefabGuids)
        {
            string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
            string prefabName = Path.GetFileNameWithoutExtension(prefabPath);

            // Match materials by prefix: <PrefabName>_image_<index>
            List<Material> matchedMaterials = allMaterials
                .Where(m => m.name.StartsWith(prefabName + "_image_"))
                .OrderBy(m => ExtractImageIndex(m.name))
                .ToList();

            if (matchedMaterials.Count == 0)
                continue;

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            bool changed = false;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in renderers)
            {
                Material[] slots = r.sharedMaterials;

                for (int i = 0; i < slots.Length && i < matchedMaterials.Count; i++)
                {
                    if (slots[i] != matchedMaterials[i])
                    {
                        slots[i] = matchedMaterials[i];
                        changed = true;
                    }
                }

                r.sharedMaterials = slots;
            }

            if (changed)
            {
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                prefabsUpdated++;
                Debug.Log($"[BATI] Updated prefab: {prefabName}");
            }

            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.Refresh();
        Debug.Log($"[BATI] Step 2 finished. Prefabs updated: {prefabsUpdated}");
    }

    // Extract numeric index from material name
    // Example: "69382AD1_image_0" -> 0
    private int ExtractImageIndex(string materialName)
    {
        int idx = materialName.LastIndexOf("_image_");
        if (idx < 0) return int.MaxValue;

        string number = materialName.Substring(idx + "_image_".Length);
        if (int.TryParse(number, out int result))
            return result;

        return int.MaxValue;
    }
}
