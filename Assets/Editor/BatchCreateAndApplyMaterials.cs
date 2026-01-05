// File: Assets/Editor/BatchCreateAndApplyMaterials.cs
// This editor script does TWO things:
//
// (1) Create materials from textures in Assets/Textures/BATI
//     Material name = texture filename (without extension)
//     Materials are saved to Assets/Materials/BATI
//
// (2) Apply materials to prefabs in Assets/Prefabs/BATI
//     Matching rule:
//       Prefab name = <ID>
//       Material name = <ID>_image_<index>
//     Materials are assigned to renderer material slots by index
//
// IMPORTANT:
// - Prefabs are modified and saved on disk
// - Backup or use version control before running

using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Linq;

public class BatchCreateAndApplyMaterials
{
    private const string TexturesFolder  = "Assets/Textures/BATI";
    private const string MaterialsFolder = "Assets/Materials/BATI";
    private const string PrefabsFolder   = "Assets/Prefabs/BATI";

    // =========================
    // MENU ENTRIES
    // =========================

    [MenuItem("Tools/BATI Materials/Step 1 - Create Materials From Textures")]
    public static void Step1_CreateMaterials()
    {
        CreateMaterialsFromTextures();
    }

    [MenuItem("Tools/BATI Materials/Step 2 - Apply Materials To Prefabs")]
    public static void Step2_ApplyMaterials()
    {
        ApplyMaterialsToPrefabs();
    }

    // =========================
    // STEP 1
    // =========================

    // Create one material per texture found in TexturesFolder.
    // Material name == texture file name (without extension).
    private static void CreateMaterialsFromTextures()
    {
        if (!AssetDatabase.IsValidFolder(TexturesFolder))
        {
            Debug.LogError("Textures folder not found: " + TexturesFolder);
            return;
        }

        // Ensure materials folder exists
        if (!AssetDatabase.IsValidFolder(MaterialsFolder))
        {
            AssetDatabase.CreateFolder("Assets/Materials", "BATI");
            AssetDatabase.Refresh();
        }

        // Prefer URP Lit, fallback to Standard
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");

        string[] textureGuids = AssetDatabase.FindAssets("t:Texture2D", new[] { TexturesFolder });

        int created = 0;
        int updated = 0;

        foreach (string guid in textureGuids)
        {
            string texPath = AssetDatabase.GUIDToAssetPath(guid);
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (texture == null) continue;

            string matName = Path.GetFileNameWithoutExtension(texPath);
            string matPath = $"{MaterialsFolder}/{matName}.mat";

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);

            if (mat == null)
            {
                mat = new Material(shader);
                mat.name = matName;

                // Assign main texture
                if (mat.HasProperty("_BaseMap"))
                    mat.SetTexture("_BaseMap", texture);
                else
                    mat.mainTexture = texture;

                AssetDatabase.CreateAsset(mat, matPath);
                created++;
            }
            else
            {
                // Update texture on existing material
                if (mat.HasProperty("_BaseMap"))
                    mat.SetTexture("_BaseMap", texture);
                else
                    mat.mainTexture = texture;

                EditorUtility.SetDirty(mat);
                updated++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[BATI] Materials created: {created}, updated: {updated}");
    }

    // =========================
    // STEP 2
    // =========================

    // Apply materials to prefabs using the rule:
    //   prefabName = 69382AD1
    //   materialName = 69382AD1_image_0
    //
    // Materials are assigned to renderer material slots by index.
    private static void ApplyMaterialsToPrefabs()
    {
        if (!AssetDatabase.IsValidFolder(MaterialsFolder) ||
            !AssetDatabase.IsValidFolder(PrefabsFolder))
        {
            Debug.LogError("Required folders not found.");
            return;
        }

        // Load all BATI materials
        string[] materialGuids = AssetDatabase.FindAssets("t:Material", new[] { MaterialsFolder });
        List<Material> allMaterials = materialGuids
            .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(m => m != null)
            .ToList();

        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabsFolder });

        int prefabsModified = 0;

        foreach (string prefabGuid in prefabGuids)
        {
            string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuid);
            string prefabName = Path.GetFileNameWithoutExtension(prefabPath);

            // Find materials that belong to this prefab
            // Example: 69382AD1_image_0, 69382AD1_image_1 ...
            List<Material> matchedMaterials = allMaterials
                .Where(m => m.name.StartsWith(prefabName + "_image_"))
                .OrderBy(m => ExtractIndex(m.name))
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
                prefabsModified++;
                Debug.Log($"[BATI] Updated prefab: {prefabName}");
            }

            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.Refresh();
        Debug.Log($"[BATI] Prefabs updated: {prefabsModified}");
    }

    // Extract numeric index from material name:
    // Example: "69382AD1_image_0" -> 0
    private static int ExtractIndex(string materialName)
    {
        int idx = materialName.LastIndexOf("_image_");
        if (idx < 0) return int.MaxValue;

        string number = materialName.Substring(idx + "_image_".Length);
        if (int.TryParse(number, out int result))
            return result;

        return int.MaxValue;
    }
}
