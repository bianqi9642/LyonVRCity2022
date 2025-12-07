// Place this file in Assets/Editor/CityKMLImporter.cs
// This Editor script contains two behaviors when a prefab is not found:
// 1) CreatePlaceholder: create a simple cube placeholder in the scene.
// 2) SkipMissingPrefab: skip the placemark and log a warning.
// You can choose which behavior at runtime using the window UI (MissingPrefabBehavior enum).

using UnityEngine;
using UnityEditor;
using System.Xml.Linq;
using System.IO;
using System.Linq;
using System.Globalization;

public class CityKMLImporter : EditorWindow
{
    // --- Editor window fields ---
    string kmlFilePath = "Assets/Models/KML/BATI_2018_Tile.kml";
    string modelsAssetsRoot = "Assets/Prefabs/BATI/"; // path where Prefabs are placed
    double originLon = 4.800807078748886; // example: set to master JSON center or your chosen origin
    double originLat = 45.756000126075136;
    double originAlt = 0.0; // in meters (WGS84 height)
    GameObject rootParentPrefab; // optional parent GameObject to group imported models

    // Options
    bool markAsStatic = true;
    bool addMeshCollider = true;
    bool addNavMeshObstacle = false;

    // New: choose behavior when prefab is missing
    public enum MissingPrefabBehavior
    {
        CreatePlaceholder,   // original behavior: create a placeholder cube
        SkipMissingPrefab    // new behavior: skip the placemark and log warning
    }
    MissingPrefabBehavior missingPrefabBehavior = MissingPrefabBehavior.SkipMissingPrefab;

    [MenuItem("Tools/City KML Importer")]
    public static void ShowWindow()
    {
        var w = GetWindow<CityKMLImporter>("City KML Importer");
        w.minSize = new Vector2(600, 220);
    }

    void OnGUI()
    {
        GUILayout.Label("Batch import KML Placemark -> Prefabs", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        kmlFilePath = EditorGUILayout.TextField("KML file path", kmlFilePath);
        modelsAssetsRoot = EditorGUILayout.TextField("Models assets root", modelsAssetsRoot);

        GUILayout.Label("Geodetic origin (WGS84)", EditorStyles.label);
        originLon = EditorGUILayout.DoubleField("Origin Longitude", originLon);
        originLat = EditorGUILayout.DoubleField("Origin Latitude", originLat);
        originAlt = EditorGUILayout.DoubleField("Origin Altitude (m)", originAlt);

        rootParentPrefab = (GameObject)EditorGUILayout.ObjectField("Parent prefab (optional)", rootParentPrefab, typeof(GameObject), false);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Missing prefab behavior", EditorStyles.boldLabel);
        // Enum popup to choose behavior at runtime
        missingPrefabBehavior = (MissingPrefabBehavior)EditorGUILayout.EnumPopup(new GUIContent("Behavior", "What to do when no prefab is found for a Placemark"), missingPrefabBehavior);

        EditorGUILayout.Space();
        markAsStatic = EditorGUILayout.Toggle("Mark instantiated as Static", markAsStatic);
        addMeshCollider = EditorGUILayout.Toggle("Add MeshCollider (if none)", addMeshCollider);
        addNavMeshObstacle = EditorGUILayout.Toggle("Add NavMeshObstacle", addNavMeshObstacle);

        EditorGUILayout.Space();
        if (GUILayout.Button("Import KML and Place Models"))
        {
            ImportKmlAndPlace();
        }
    }

    void ImportKmlAndPlace()
    {
        if (!File.Exists(kmlFilePath))
        {
            EditorUtility.DisplayDialog("Error", "KML file not found: " + kmlFilePath, "OK");
            return;
        }

        XDocument doc = XDocument.Load(kmlFilePath);
        // KML default namespace
        XNamespace ns = doc.Root.GetDefaultNamespace();

        var placemarks = doc.Descendants(ns + "Placemark");
        if (!placemarks.Any())
        {
            EditorUtility.DisplayDialog("Info", "No Placemark elements found in KML.", "OK");
            return;
        }

        // Create or find a root object to parent all imported models
        GameObject root = new GameObject("City_Root");
        if (rootParentPrefab != null)
        {
            GameObject parentInstance = (GameObject)PrefabUtility.InstantiatePrefab(rootParentPrefab) as GameObject;
            if (parentInstance != null)
            {
                parentInstance.name = "City_Root";
                root = parentInstance;
            }
        }

        int countPlaced = 0;
        int countSkipped = 0;
        int countPlaceholder = 0;

        foreach (var pm in placemarks)
        {
            string name = pm.Element(ns + "name") != null ? pm.Element(ns + "name").Value : "Placemark";
            var modelNode = pm.Element(ns + "Model");
            if (modelNode == null)
            {
                // skip if no Model node
                continue;
            }
            // read location
            var loc = modelNode.Element(ns + "Location");
            if (loc == null) continue;
            double lon = double.Parse(loc.Element(ns + "longitude").Value, CultureInfo.InvariantCulture);
            double lat = double.Parse(loc.Element(ns + "latitude").Value, CultureInfo.InvariantCulture);
            double alt = 0.0;
            var altEl = loc.Element(ns + "altitude");
            if (altEl != null) alt = double.Parse(altEl.Value, CultureInfo.InvariantCulture);

            // read orientation heading if present
            double heading = 0.0;
            var orient = modelNode.Element(ns + "Orientation");
            if (orient != null && orient.Element(ns + "heading") != null)
            {
                heading = double.Parse(orient.Element(ns + "heading").Value, CultureInfo.InvariantCulture);
            }

            // read href (link to file)
            var link = modelNode.Element(ns + "Link");
            string href = null;
            if (link != null && link.Element(ns + "href") != null)
            {
                href = link.Element(ns + "href").Value.Trim();
            }

            // Determine prefab name: try href filename without extension, else use Placemark name
            string prefabName = null;
            if (!string.IsNullOrEmpty(href))
            {
                prefabName = Path.GetFileNameWithoutExtension(href);
            }
            if (string.IsNullOrEmpty(prefabName))
            {
                prefabName = name;
            }

            // Try to find a prefab in modelsAssetsRoot
            string prefabPath = FindPrefabPath(modelsAssetsRoot, prefabName);
            GameObject prefab = null;
            if (!string.IsNullOrEmpty(prefabPath))
            {
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            }

            // Behavior when prefab is missing:
            if (prefab == null)
            {
                if (missingPrefabBehavior == MissingPrefabBehavior.SkipMissingPrefab)
                {
                    // Skip and warn
                    Debug.LogWarning($"[CityKMLImporter] Prefab not found for '{prefabName}'. Skipping Placemark.");
                    countSkipped++;
                    continue;
                }
                else if (missingPrefabBehavior == MissingPrefabBehavior.CreatePlaceholder)
                {
                    // Create a lightweight placeholder (cube)
                    GameObject placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    placeholder.name = prefabName + "_placeholder";
                    placeholder.transform.localScale = Vector3.one * 5.0f;
                    // set parent and position below (after computing pos)
                    Vector3 posPlaceholder = GeoToUnityPosition(lat, lon, alt, originLat, originLon, originAlt);
                    placeholder.transform.SetParent(root.transform, worldPositionStays: false);
                    placeholder.transform.position = posPlaceholder;
                    // apply rotation based on heading
                    float rotY_ph = ((float)heading + 180f) % 360f;
                    placeholder.transform.rotation = Quaternion.Euler(-90f, rotY_ph, 0f);

                    // optionally mark static / add colliders / navmesh obstacle
                    if (markAsStatic)
                    {
                        GameObjectUtility.SetStaticEditorFlags(placeholder, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic | StaticEditorFlags.ContributeGI);
                    }

                    if (addMeshCollider)
                    {
                        // The primitive already has a BoxCollider, so nothing special required
                        // but ensure it exists
                        if (placeholder.GetComponent<Collider>() == null)
                            placeholder.AddComponent<BoxCollider>();
                    }

                    if (addNavMeshObstacle)
                    {
                        var nbo = placeholder.GetComponent<UnityEngine.AI.NavMeshObstacle>();
                        if (nbo == null)
                            nbo = placeholder.AddComponent<UnityEngine.AI.NavMeshObstacle>();
                        nbo.carving = true;
                    }

                    countPlaceholder++;
                    // continue to next placemark (we already placed the placeholder)
                    continue;
                }
            }

            // If we reach here, prefab != null => instantiate
            Vector3 pos = GeoToUnityPosition(lat, lon, alt, originLat, originLon, originAlt);

            GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (go == null)
            {
                Debug.LogError($"[CityKMLImporter] Failed to instantiate prefab at path '{prefabPath}' for '{prefabName}'.");
                continue;
            }

            go.name = prefab.name;
            go.transform.SetParent(root.transform, worldPositionStays: false);
            go.transform.position = pos;

            // Convert KML heading (degrees clockwise from North) to Unity Y rotation
            // NOTE: Unity axes: X = East, Z = North, Y = Up (we use that mapping).
            // KML heading: 0 = North, 90 = East, 180 = South, 270 = West, positive clockwise.
            float rotY = ((float)heading + 180f) % 360f;
            go.transform.rotation = Quaternion.Euler(-90f, rotY, 0f);

            // mark static if requested
            if (markAsStatic)
            {
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic | StaticEditorFlags.ContributeGI);
            }

            // add simple collider if none
            if (addMeshCollider)
            {
                if (go.GetComponent<Collider>() == null)
                {
                    MeshFilter mf = go.GetComponentInChildren<MeshFilter>();
                    if (mf != null)
                    {
                        MeshCollider mc = go.AddComponent<MeshCollider>();
                        mc.sharedMesh = mf.sharedMesh;
                        mc.convex = false;
                    }
                    else
                    {
                        BoxCollider bc = go.AddComponent<BoxCollider>();
                    }
                }
            }

            if (addNavMeshObstacle)
            {
                var nbo = go.GetComponent<UnityEngine.AI.NavMeshObstacle>();
                if (nbo == null)
                    nbo = go.AddComponent<UnityEngine.AI.NavMeshObstacle>();
                nbo.carving = true;
            }

            countPlaced++;
        }

        Debug.Log($"Imported and placed {countPlaced} placemarks from KML into scene under {root.name}. Skipped: {countSkipped}. Placeholders created: {countPlaceholder}.");
        EditorUtility.DisplayDialog("City KML Import", $"Placed: {countPlaced}\nSkipped (missing prefab): {countSkipped}\nPlaceholders created: {countPlaceholder}", "OK");
    }

    // Search for a prefab file under a root folder by name (case-insensitive)
    string FindPrefabPath(string rootFolder, string prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
            return null;

        // Normalize user input into an Asset folder path like "Assets/Prefabs/BATI"
        string folderAssetPath = rootFolder != null ? rootFolder.Trim() : "Assets";
        // If user passed a path not starting with "Assets", prepend "Assets/"
        if (!folderAssetPath.StartsWith("Assets"))
        {
            folderAssetPath = Path.Combine("Assets", folderAssetPath).Replace('\\', '/');
        }
        // Remove trailing slashes
        folderAssetPath = folderAssetPath.TrimEnd('/', '\\');

        // If the folder exists in the project, search only inside it
        if (AssetDatabase.IsValidFolder(folderAssetPath))
        {
            // Use AssetDatabase.FindAssets with folder restriction to avoid scanning the whole project
            string filter = prefabName + " t:Prefab";
            string[] guids = AssetDatabase.FindAssets(filter, new[] { folderAssetPath });
            foreach (var g in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                // match filename (case-insensitive)
                if (Path.GetFileNameWithoutExtension(p).Equals(prefabName, System.StringComparison.InvariantCultureIgnoreCase))
                    return p;
            }

            // If nothing matched exactly, return null (no global fallback here)
            return null;
        }
        else
        {
            // Folder not found: fallback to a global search but warn so user notices mis-typed folder
            Debug.LogWarning($"[CityKMLImporter] Specified modelsAssetsRoot folder '{folderAssetPath}' not found. Falling back to global prefab search.");
            string[] guids = AssetDatabase.FindAssets(prefabName + " t:Prefab");
            foreach (var g in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                if (Path.GetFileNameWithoutExtension(p).Equals(prefabName, System.StringComparison.InvariantCultureIgnoreCase))
                    return p;
            }
            return null;
        }
    }


    // Convert lat,lon,alt to Unity ENU (meters) relative to origin.
    // Unity mapping: X = East, Y = Up, Z = North.
    Vector3 GeoToUnityPosition(double lat, double lon, double alt, double originLatDeg, double originLonDeg, double originAlt)
    {
        // Convert both points to ECEF (meters)
        var ecefOrigin = GeoToECEF(originLatDeg, originLonDeg, originAlt);
        var ecefPoint = GeoToECEF(lat, lon, alt);

        // Compute ENU vector of point relative to origin
        var enu = EcefToEnu(ecefPoint, ecefOrigin, originLatDeg, originLonDeg);

        // Map ENU to Unity coordinates: (E, U, N) -> (X, Y, Z)
        return new Vector3((float)enu.x, (float)enu.z, (float)enu.y);
    }

    // --- Helper math: WGS84 constants and transforms ---
    const double a = 6378137.0; // semi-major axis
    const double f = 1.0 / 298.257223563; // flattening
    const double b = a * (1 - f);
    const double e2 = (a * a - b * b) / (a * a);

    struct Vec3d { public double x, y, z; public Vec3d(double X, double Y, double Z) { x = X; y = Y; z = Z; } }

    Vec3d GeoToECEF(double latDeg, double lonDeg, double h)
    {
        double lat = latDeg * Mathf.Deg2Rad;
        double lon = lonDeg * Mathf.Deg2Rad;
        double sinLat = Mathf.Sin((float)lat);
        double cosLat = Mathf.Cos((float)lat);
        double sinLon = Mathf.Sin((float)lon);
        double cosLon = Mathf.Cos((float)lon);

        double N = a / System.Math.Sqrt(1 - e2 * sinLat * sinLat);
        double x = (N + h) * cosLat * cosLon;
        double y = (N + h) * cosLat * sinLon;
        double z = (N * (1 - e2) + h) * sinLat;
        return new Vec3d(x, y, z);
    }

    // Convert ECEF difference to ENU (east, north, up) at reference lat/lon
    Vec3d EcefToEnu(Vec3d pointEcef, Vec3d originEcef, double refLatDeg, double refLonDeg)
    {
        double lat = refLatDeg * Mathf.Deg2Rad;
        double lon = refLonDeg * Mathf.Deg2Rad;

        double dx = pointEcef.x - originEcef.x;
        double dy = pointEcef.y - originEcef.y;
        double dz = pointEcef.z - originEcef.z;

        double sinLat = System.Math.Sin(lat);
        double cosLat = System.Math.Cos(lat);
        double sinLon = System.Math.Sin(lon);
        double cosLon = System.Math.Cos(lon);

        double east  = -sinLon * dx + cosLon * dy;
        double north = -cosLon * sinLat * dx - sinLat * sinLon * dy + cosLat * dz;
        double up    =  cosLat * cosLon * dx + cosLat * sinLon * dy + sinLat * dz;

        return new Vec3d(east, north, up);
    }
}
