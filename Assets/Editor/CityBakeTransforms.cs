// CityBakeTransforms.cs
// Put this file under Assets/Editor/ in your Unity project.
// Usage:
//  - Select one or more root GameObjects (e.g., the parent that contains all building GameObjects).
//  - Menu: Tools -> City -> Bake Selected Buildings (or Bake Selected + Reset Roots)
//  - The script will duplicate meshes (avoid editing shared assets), bake transforms into vertex positions,
//    transform normals/tangents correctly, then reset each MeshFilter's transform to identity (pos=0,rot=0,scale=1).
//  - Visual positions will remain unchanged in the Scene.

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class CityBakeTransforms : EditorWindow
{
    [MenuItem("Tools/City/Bake Selected Buildings")]
    static void BakeSelectedBuildings()
    {
        if (Selection.transforms.Length == 0)
        {
            if (!EditorUtility.DisplayDialog("Bake Transforms", "No object selected. Do you want to bake everything in the scene root?", "Yes (scene root)", "Cancel"))
                return;
            // else continue with scene root
        }

        // Ask for confirmation
        if (!EditorUtility.DisplayDialog("Bake Mesh Transforms",
            "This will modify meshes (create new mesh instances) and reset transforms of the selected objects/children.\n\n" +
            "Please BACKUP your project or test on a copy first.\n\nProceed?", "Proceed", "Cancel"))
            return;

        // Operate on selected roots; if nothing selected, operate on all root GameObjects in scene
        Transform[] roots = Selection.transforms;
        if (roots.Length == 0)
        {
            var rootsList = new List<Transform>();
            foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                rootsList.Add(go.transform);
            roots = rootsList.ToArray();
        }

        // Process each root
        int total = roots.Length;
        int processed = 0;
        foreach (var root in roots)
        {
            // find all MeshFilters under root
            MeshFilter[] mfs = root.GetComponentsInChildren<MeshFilter>(true);

            // For each MeshFilter, bake its mesh verts to world space
            foreach (var mf in mfs)
            {
                if (mf.sharedMesh == null) continue;
                // Skip if Mesh is marked as not readable - log and skip
                Mesh src = mf.sharedMesh;
                // Create a copy of the mesh so we don't overwrite imported shared assets
                Mesh mesh = Object.Instantiate(src);
                mesh.name = src.name + "_baked_" + mf.transform.GetInstanceID();

                // Get transform matrix from mesh local -> world
                Matrix4x4 localToWorld = mf.transform.localToWorldMatrix;

                // Transform vertices
                Vector3[] verts = mesh.vertices;
                for (int i = 0; i < verts.Length; i++)
                {
                    verts[i] = localToWorld.MultiplyPoint3x4(verts[i]);
                }
                mesh.vertices = verts;

                // Transform normals (using inverse transpose)
                if (mesh.normals != null && mesh.normals.Length == verts.Length)
                {
                    Vector3[] norms = mesh.normals;
                    Matrix4x4 normalMatrix = localToWorld.inverse.transpose;
                    for (int i = 0; i < norms.Length; i++)
                    {
                        norms[i] = normalMatrix.MultiplyVector(norms[i]).normalized;
                    }
                    mesh.normals = norms;
                }
                else
                {
                    mesh.RecalculateNormals();
                }

                // Transform tangents if present
                if (mesh.tangents != null && mesh.tangents.Length == verts.Length)
                {
                    Vector4[] tangs = mesh.tangents;
                    for (int i = 0; i < tangs.Length; i++)
                    {
                        Vector3 t = new Vector3(tangs[i].x, tangs[i].y, tangs[i].z);
                        t = localToWorld.MultiplyVector(t).normalized;
                        tangs[i] = new Vector4(t.x, t.y, t.z, tangs[i].w);
                    }
                    mesh.tangents = tangs;
                }
                else
                {
                    mesh.RecalculateTangents();
                }

                mesh.RecalculateBounds();

                // Assign the new mesh instance to the MeshFilter and MeshCollider if present
                mf.sharedMesh = mesh;
                MeshCollider mc = mf.GetComponent<MeshCollider>();
                if (mc != null)
                {
                    // assign shared mesh instance to collider
                    mc.sharedMesh = mesh;
                }
            }

            // After baking all MeshFilters under this root, reset transforms of all child objects to identity.
            // We want to keep geometry in the same world positions because vertices are now in world space.
            // We'll reset transforms for all transforms under the root.
            Transform[] allTransforms = root.GetComponentsInChildren<Transform>(true);
            // Sort so we set children before parents? We can just set in any order because meshes now contain world positions.
            foreach (var t in allTransforms)
            {
                // We preserve the root's world position? Usually reset all to identity (world space)
                // But to maintain scene organization, we set each transform to identity in world-space,
                // so we compute matrix to move to identity while keeping world-space vertex positions.
                Undo.RecordObject(t, "Reset Transform");
                t.position = Vector3.zero;
                t.rotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }

            processed++;
            EditorUtility.DisplayProgressBar("Baking Mesh Transforms", $"Processed {root.name} ({processed}/{total})", (float)processed / total);
        }

        EditorUtility.ClearProgressBar();
        Debug.Log("[CityBakeTransforms] Done baking meshes to world-space and resetting transforms.");
        // Focus scene view update
        SceneView.RepaintAll();
    }
}
