// NavMeshVerticalProjector.cs
// Place this file in an Editor folder (e.g. Assets/Editor/NavMeshVerticalProjector.cs).
// Open the tool via the menu: Window -> NavMesh Vertical Projector
//
// What it does:
// - For each child under a chosen root GameObject (e.g. NodesOrganic), vertically projects
//   the child's X,Z onto the NavMesh to find the NavMesh surface directly above/below that X,Z.
// - Only the Y coordinate is changed (vertical projection); X and Z remain exactly the same.
// - It scans along Y (from sourceY + searchUp down to sourceY - searchDown) and accepts a NavMesh
//   sample only when the returned hit's X,Z are within horizontalTolerance of the original X,Z.
// - Optionally creates a visual marker at the projected point. If a marker prefab is provided,
//   it will instantiate that; otherwise it will create a small sphere primitive (collider removed).
// - Optionally moves the original transforms to the projected positions.
// - Supports Undo for created/moved objects.
//
// Notes:
// - Editor-time tool only (uses UnityEditor APIs); does not run in builds.
// - Tweak search parameters (step, searchUp, searchDown, sampleRadius, horizontalTolerance) to suit your NavMesh resolution.

using UnityEngine;
using UnityEditor;
using UnityEngine.AI;
using System.Collections.Generic;

public class NavMeshVerticalProjector : EditorWindow
{
    // ----- UI fields -----
    GameObject rootObject = null;                    // source parent (NodesOrganic)
    Transform outputParent = null;                   // where to create projected points
    GameObject markerPrefab = null;                  // optional marker prefab to instantiate
    bool createMarker = true;                        // NEW: whether to create a marker at each projected point
    bool moveOriginals = false;                      // whether to move original child transforms
    bool recursive = false;                          // whether to traverse children recursively

    // marker options
    float markerScale = 2.0f;                        // scale for auto-created sphere marker (if no prefab provided)
    string markerNamePrefix = "Projected_";          // name prefix for created markers

    // vertical search parameters (meters)
    float searchUp = 50.0f;                           // how far above source Y to start scanning
    float searchDown = 50.0f;                        // how far below source Y to scan
    float step = 0.5f;                               // sampling step size (meters)
    float sampleRadius = 0.25f;                      // maxDistance for NavMesh.SamplePosition at each sample
    float horizontalTolerance = 0.2f;                // maximum horizontal offset allowed between sample and original X,Z

    // results
    int succeeded = 0;
    int failed = 0;

    [MenuItem("Tools/NavMesh Vertical Projector")]
    public static void ShowWindow()
    {
        GetWindow<NavMeshVerticalProjector>("NavMesh Vertical Projector");
    }

    void OnGUI()
    {
        GUILayout.Label("Project child transforms vertically onto NavMesh", EditorStyles.boldLabel);

        EditorGUILayout.Space();

        rootObject = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Source Root (NodesOrganic)", "Root object whose children will be projected vertically"), rootObject, typeof(GameObject), true);
        outputParent = (Transform)EditorGUILayout.ObjectField(new GUIContent("Output Parent", "Parent transform for created projected points (optional)"), outputParent, typeof(Transform), true);
        markerPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Marker Prefab (optional)", "Prefab to instantiate at each projected position (e.g. small sphere)"), markerPrefab, typeof(GameObject), false);
        createMarker = EditorGUILayout.Toggle(new GUIContent("Create Marker?", "Create a visual marker at each projected point (uses marker prefab if assigned, otherwise creates a small sphere)"), createMarker);
        markerScale = EditorGUILayout.FloatField(new GUIContent("Auto Marker Scale (if no prefab)", "Scale for the auto-created sphere marker"), markerScale);
        markerNamePrefix = EditorGUILayout.TextField(new GUIContent("Marker Name Prefix", "Name prefix for created markers"), markerNamePrefix);

        moveOriginals = EditorGUILayout.Toggle(new GUIContent("Move original transforms", "Also move the original child transforms to the projected position"), moveOriginals);
        recursive = EditorGUILayout.Toggle(new GUIContent("Recursive traverse", "Include all descendants (recursive), not only direct children"), recursive);

        EditorGUILayout.Space();
        GUILayout.Label("Vertical search parameters (meters)", EditorStyles.label);
        searchUp = EditorGUILayout.FloatField(new GUIContent("Search Up (m)","Distance above object's Y to start scanning"), searchUp);
        searchDown = EditorGUILayout.FloatField(new GUIContent("Search Down (m)","Distance below object's Y to scan"), searchDown);
        step = EditorGUILayout.FloatField(new GUIContent("Step (m)","Vertical sampling step size"), step);
        sampleRadius = EditorGUILayout.FloatField(new GUIContent("Sample Radius (m)","NavMesh.SamplePosition maxDistance"), sampleRadius);
        horizontalTolerance = EditorGUILayout.FloatField(new GUIContent("Horizontal Tol (m)","Allowed horizontal difference between sample and source X,Z"), horizontalTolerance);

        EditorGUILayout.Space();

        if (GUILayout.Button("Project Children Vertically"))
        {
            if (rootObject == null)
            {
                EditorUtility.DisplayDialog("Error", "Please assign a Source Root (e.g. NodesOrganic).", "OK");
            }
            else
            {
                ProjectAllChildren();
            }
        }

        if (GUILayout.Button("Clear Results Under Output Parent"))
        {
            if (outputParent != null)
            {
                if (EditorUtility.DisplayDialog("Confirm", "Destroy all children under the selected Output Parent?", "Yes", "No"))
                {
                    for (int i = outputParent.childCount - 1; i >= 0; --i)
                    {
                        Undo.DestroyObjectImmediate(outputParent.GetChild(i).gameObject);
                    }
                }
            }
            else
            {
                EditorUtility.DisplayDialog("Info", "No Output Parent assigned.", "OK");
            }
        }

        EditorGUILayout.Space();
        GUILayout.Label($"Last run: {succeeded} succeeded, {failed} failed", EditorStyles.helpBox);
    }

    // main operation
    void ProjectAllChildren()
    {
        succeeded = 0;
        failed = 0;

        List<Transform> list = new List<Transform>();

        if (recursive)
        {
            foreach (Transform t in rootObject.GetComponentsInChildren<Transform>(true))
            {
                if (t == rootObject.transform) continue;
                list.Add(t);
            }
        }
        else
        {
            foreach (Transform t in rootObject.transform)
            {
                list.Add(t);
            }
        }

        if (list.Count == 0)
        {
            EditorUtility.DisplayDialog("Info", "No child transforms found under the selected root.", "OK");
            return;
        }

        // create an output parent if none assigned
        Transform createdParent = outputParent;
        if (createdParent == null)
        {
            GameObject go = new GameObject(rootObject.name + "_ProjectedPoints");
            Undo.RegisterCreatedObjectUndo(go, "Create output parent");
            createdParent = go.transform;
        }

        // iterate and project
        int idx = 0;
        foreach (var t in list)
        {
            idx++;
            Vector3 sourcePos = t.position;
            Vector3? projected = VerticalProjectAtXZ(sourcePos);

            if (projected.HasValue)
            {
                Vector3 projPos = projected.Value;

                // optionally create marker (either prefab or auto sphere)
                GameObject marker = null;
                if (createMarker)
                {
                    if (markerPrefab != null)
                    {
                        // instantiate prefab using PrefabUtility so prefab connection is preserved in editor
                        marker = (GameObject)PrefabUtility.InstantiatePrefab(markerPrefab);
                        if (marker != null)
                        {
                            Undo.RegisterCreatedObjectUndo(marker, "Instantiate marker prefab");
                            marker.transform.position = projPos;
                            marker.transform.rotation = Quaternion.identity;
                            marker.transform.SetParent(createdParent, true);
                            marker.name = markerNamePrefix + t.name;
                        }
                    }
                    else
                    {
                        // create a simple sphere as visual marker
                        marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        Undo.RegisterCreatedObjectUndo(marker, "Create marker sphere");
                        // remove collider to avoid accidental physics interactions in editor
                        Collider col = marker.GetComponent<Collider>();
                        if (col != null)
                        {
                            Undo.DestroyObjectImmediate(col);
                        }
                        marker.transform.position = projPos;
                        marker.transform.rotation = Quaternion.identity;
                        marker.transform.localScale = Vector3.one * Mathf.Max(0.0001f, markerScale);
                        marker.transform.SetParent(createdParent, true);
                        marker.name = markerNamePrefix + t.name;
                    }
                }

                // optionally move original transform to projected position
                if (moveOriginals)
                {
                    Undo.RecordObject(t, "Move original to projected NavMesh pos");
                    t.position = projPos;
                }

                succeeded++;
            }
            else
            {
                Debug.LogWarning($"No NavMesh vertical projection found for '{t.name}' at XZ ({sourcePos.x:F3},{sourcePos.z:F3}).");
                failed++;
            }
        }

        // ping parent in editor
        if (createdParent != null)
        {
            Selection.activeTransform = createdParent;
        }

        EditorUtility.DisplayDialog("Finished", $"Project finished: {succeeded} succeeded, {failed} failed.", "OK");
    }

    // Try to find a NavMesh point vertically aligned with source position's X,Z.
    // We scan from sourceY + searchUp down to sourceY - searchDown by steps, and for each sample
    // use NavMesh.SamplePosition with a small radius. Accept a hit only if the returned position's
    // X,Z are within horizontalTolerance of the original X,Z (ensuring true vertical projection).
    //
    // Returns the first found projected position (NavMesh point), or null if none found.
    Vector3? VerticalProjectAtXZ(Vector3 source)
    {
        // start from top (above) and proceed downwards so we hit highest surface first
        float top = source.y + Mathf.Abs(searchUp);
        float bottom = source.y - Mathf.Abs(searchDown);

        // ensure step positive
        float s = Mathf.Max(0.01f, step);

        // We'll sample at y = top, top - s, top - 2s, ... down to bottom
        for (float y = top; y >= bottom; y -= s)
        {
            Vector3 samplePos = new Vector3(source.x, y, source.z);
            NavMeshHit hit;
            // SamplePosition will return a nearby NavMesh point within sampleRadius (maxDistance)
            // we give it a small maxDistance so that it only returns a point if NavMesh is near this exact X,Z/Y slice.
            if (NavMesh.SamplePosition(samplePos, out hit, sampleRadius, NavMesh.AllAreas))
            {
                // verify horizontal closeness: ensure the found navmesh point is essentially at same X,Z
                Vector2 hitXZ = new Vector2(hit.position.x, hit.position.z);
                Vector2 srcXZ = new Vector2(source.x, source.z);
                float horizDist = Vector2.Distance(hitXZ, srcXZ);
                if (horizDist <= horizontalTolerance)
                {
                    // success: we have a NavMesh position vertically aligned within tolerance
                    // Note: hit.position.y is the NavMesh surface Y — this is the vertical projection result.
                    // We return hit.position but ensure X,Z are precisely the original X,Z (so X/Z unchanged).
                    return new Vector3(source.x, hit.position.y, source.z);
                }

                // If the hit is horizontally far, ignore and keep scanning downwards.
            }
        }

        // no vertical navmesh found within range
        return null;
    }
}
