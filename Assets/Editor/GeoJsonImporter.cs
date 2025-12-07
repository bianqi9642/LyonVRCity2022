using UnityEngine;
using UnityEditor;
using System.IO;
using Newtonsoft.Json.Linq; // Make sure you have Newtonsoft JSON in your project
using System.Globalization;

public class GeoJsonImporter : EditorWindow
{
    // --- Editor fields ---
    private string geoJsonFilePath = "Assets/Models/Network/entrance_exit.geojson"; // Default path
    private GameObject pointPrefab; // Optional prefab to instantiate for points
    private GameObject rootParentPrefab; // Optional parent prefab to group imported points

    // Geodetic origin (shared with KML import)
    private double originLon = 4.800807078748886;
    private double originLat = 45.756000126075136;
    private double originAlt = 0.0;

    // Options
    private bool markAsStatic = true;
    private float pointScale = 1.0f;

    // Missing prefab behavior
    public enum MissingPrefabBehavior
    {
        CreatePlaceholder,   // create a simple sphere
        SkipMissingPrefab    // skip and log warning
    }
    private MissingPrefabBehavior missingPrefabBehavior = MissingPrefabBehavior.CreatePlaceholder;

    [MenuItem("Tools/Import GeoJSON Points")]
    public static void ShowWindow()
    {
        var w = GetWindow<GeoJsonImporter>("GeoJSON Importer");
        w.minSize = new Vector2(500, 220);
    }

    private void OnGUI()
    {
        GUILayout.Label("Organic Nodes GeoJSON Importer", EditorStyles.boldLabel);

        geoJsonFilePath = EditorGUILayout.TextField("GeoJSON File Path", geoJsonFilePath);
        pointPrefab = (GameObject)EditorGUILayout.ObjectField("Point Prefab (Optional)", pointPrefab, typeof(GameObject), false);
        rootParentPrefab = (GameObject)EditorGUILayout.ObjectField("Parent Prefab (Optional)", rootParentPrefab, typeof(GameObject), false);

        GUILayout.Label("Geodetic Origin (shared with KML)", EditorStyles.label);
        originLat = EditorGUILayout.DoubleField("Origin Latitude", originLat);
        originLon = EditorGUILayout.DoubleField("Origin Longitude", originLon);
        originAlt = EditorGUILayout.DoubleField("Origin Altitude (m)", originAlt);

        markAsStatic = EditorGUILayout.Toggle("Mark Points as Static", markAsStatic);
        pointScale = EditorGUILayout.FloatField("Point Scale", pointScale);

        missingPrefabBehavior = (MissingPrefabBehavior)EditorGUILayout.EnumPopup("Missing Prefab Behavior", missingPrefabBehavior);

        EditorGUILayout.Space();
        if (GUILayout.Button("Import Points"))
        {
            ImportPoints();
        }
    }

    private void ImportPoints()
    {
        if (!File.Exists(geoJsonFilePath))
        {
            Debug.LogError("GeoJSON file not found at path: " + geoJsonFilePath);
            return;
        }

        string jsonText = File.ReadAllText(geoJsonFilePath);

        // Parse the GeoJSON file
        JObject geoJson = JObject.Parse(jsonText);
        JArray features = (JArray)geoJson["features"];
        if (features == null)
        {
            Debug.LogError("No features found in GeoJSON.");
            return;
        }

        // Create a parent root object for all points
        GameObject root = new GameObject("GeoJSON_Root");
        // Apply X=90 rotation to match desired orientation
        root.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        if (rootParentPrefab != null)
        {
            GameObject parentInstance = (GameObject)PrefabUtility.InstantiatePrefab(rootParentPrefab) as GameObject;
            if (parentInstance != null)
            {
                parentInstance.name = "GeoJSON_Root";
                root = parentInstance;
                root.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }
        }

        int countPlaced = 0;
        int countSkipped = 0;
        int countPlaceholder = 0;

        foreach (JObject feature in features)
        {
            JObject geometry = (JObject)feature["geometry"];
            if (geometry == null || geometry["type"].ToString() != "Point") continue;

            JArray coords = (JArray)geometry["coordinates"];
            if (coords == null || coords.Count < 2) continue;

            double lon = coords[0].ToObject<double>();
            double lat = coords[1].ToObject<double>();
            double alt = 0.0; // optional height, default to 0

            // Convert lat/lon/alt to Unity ENU coordinates (X = East, Y = Up, Z = North)
            Vector3 position = GeoToUnityPosition(lat, lon, alt, originLat, originLon, originAlt);

            GameObject pointObject = null;

            if (pointPrefab != null)
            {
                pointObject = PrefabUtility.InstantiatePrefab(pointPrefab) as GameObject;
            }
            else if (missingPrefabBehavior == MissingPrefabBehavior.CreatePlaceholder)
            {
                pointObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                pointObject.transform.localScale = Vector3.one * pointScale;
            }
            else if (missingPrefabBehavior == MissingPrefabBehavior.SkipMissingPrefab)
            {
                string nodeIdSkip = feature["properties"]?["number"]?.ToString() ?? "Point";
                Debug.LogWarning($"Skipping point '{nodeIdSkip}' because no prefab assigned.");
                countSkipped++;
                continue;
            }

            // Name the point using node_id if available
            string nodeId = feature["properties"]?["number"]?.ToString();
            if (!string.IsNullOrEmpty(nodeId))
                pointObject.name = nodeId;

            pointObject.transform.SetParent(root.transform, worldPositionStays: false);
            pointObject.transform.localPosition = position; // use localPosition since parent is rotated

            if (markAsStatic)
            {
                GameObjectUtility.SetStaticEditorFlags(pointObject, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic | StaticEditorFlags.ContributeGI);
            }

            if (pointObject.GetComponent<Collider>() == null)
            {
                pointObject.AddComponent<SphereCollider>();
            }

            countPlaced++;
            countPlaceholder++;
        }

        Debug.Log($"Imported {countPlaced} points from GeoJSON into scene under {root.name}. Skipped: {countSkipped}, Placeholders: {countPlaceholder}");
        EditorUtility.DisplayDialog("GeoJSON Import", $"Placed: {countPlaced}\nSkipped: {countSkipped}\nPlaceholders: {countPlaceholder}", "OK");
    }

    // --- Coordinate conversion using WGS84 to ENU (meters) ---
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

        double east = -sinLon * dx + cosLon * dy;
        double north = -cosLon * sinLat * dx - sinLat * sinLon * dy + cosLat * dz;
        double up = cosLat * cosLon * dx + cosLat * sinLon * dy + sinLat * dz;

        return new Vec3d(east, north, up);
    }

    Vector3 GeoToUnityPosition(double lat, double lon, double alt, double originLatDeg, double originLonDeg, double originAlt)
    {
        Vec3d ecefOrigin = GeoToECEF(originLatDeg, originLonDeg, originAlt);
        Vec3d ecefPoint = GeoToECEF(lat, lon, alt);
        Vec3d enu = EcefToEnu(ecefPoint, ecefOrigin, originLatDeg, originLonDeg);

        // Map ENU to Unity: X = East, Y = Up, Z = North
        return new Vector3((float)enu.x, (float)enu.y, (float)enu.z);
    }
}
