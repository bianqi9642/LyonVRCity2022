using UnityEngine;
using UnityEditor;
using System.IO;
using Newtonsoft.Json.Linq; // Make sure you have Newtonsoft JSON in your project
using System.Globalization;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine.AI;

public class GeoJsonImporter : EditorWindow
{
    // --- Editor fields ---
    [SerializeField] private string geoJsonFilePath = "Assets/Models/Network/entrance_exit.geojson"; // Default path
    [SerializeField] private GameObject pointPrefab; // Optional prefab to instantiate for points
    [SerializeField] private GameObject rootParentPrefab; // Optional parent prefab to group imported points

    // Geodetic origin (shared with KML import)
    [SerializeField] private double originLon = 4.800807078748886;
    [SerializeField] private double originLat = 45.756000126075136;
    [SerializeField] private double originAlt = 0.0;

    // Options
    [SerializeField] private bool markAsStatic = true;
    [SerializeField] private float pointScale = 1.0f;

    // Dataset placement offset (metres, Unity world axes: X = East, Y = Up, Z = North). It is applied ONCE, as a translation of the
    // GeoJSON_Root object; the per-point positions stay pure ENU relative to the geodetic origin, so GeoToUnityPosition() is shared
    // and identical for every dataset. Default zero = ENU relative to the origin (what the Organic data needs).
    // The existing Grid environment sits under the "Grid Area" object at (-792, 0, -36), so Grid points need that same translation.
    private static readonly Vector3 GridAreaOffset = new Vector3(-792f, 0f, -36f);
    [SerializeField] private Vector3 placementOffset = Vector3.zero;

    // Missing prefab behavior
    public enum MissingPrefabBehavior
    {
        CreatePlaceholder,   // create a simple sphere
        SkipMissingPrefab    // skip and log warning
    }
    [SerializeField] private MissingPrefabBehavior missingPrefabBehavior = MissingPrefabBehavior.CreatePlaceholder;

    // Vertical projection onto the NavMesh is done by the project's existing NavMeshVerticalProjector (run unchanged, on the points
    // created by this import only). That tool searches +-50 m around each point's CURRENT world Y, so every new point is first staged
    // at a height read from the loaded NavMesh near its X/Z (see NavMeshHeightSeed); the projector then replaces that staging
    // height with the real surface height. X and Z are never changed by the projector.
    [SerializeField] private bool projectToNavMesh = true;

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

        placementOffset = EditorGUILayout.Vector3Field(new GUIContent("Placement Offset (m)",
            "Translation of GeoJSON_Root in Unity world axes (X = East, Y = Up, Z = North), applied once. Keep zero for Organic. " +
            "Grid: (-792, 0, -36), the position of the existing 'Grid Area' object."), placementOffset);
        if (GUILayout.Button("Set Grid Area Offset (-792, 0, -36)"))
        {
            placementOffset = GridAreaOffset;
        }

        projectToNavMesh = EditorGUILayout.Toggle(new GUIContent("Project Onto NavMesh",
            "Run the existing NavMeshVerticalProjector on the new points (Edit Mode, immediately after the import). Needs the scene's " +
            "baked NavMesh to be loaded. Points it cannot project are reported by the projector and renamed with _UNPROJECTED."),
            projectToNavMesh);

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

        NavMeshHeightSeed heightSeed = null;
        if (projectToNavMesh)
        {
            heightSeed = NavMeshHeightSeed.Build();
            if (heightSeed == null)
            {
                string msg = "No NavMesh is loaded, so points cannot be projected. Open the experiment scene with its NavMesh surface " +
                             "active (or build the NavMesh), or switch off 'Project Onto NavMesh'. Nothing was imported.";
                Debug.LogError("[GeoJsonImporter] " + msg);
                EditorUtility.DisplayDialog("GeoJSON Import", msg, "OK");
                return;
            }
        }
        var newPoints = new List<Transform>();
        int countUncertainSeed = 0;
        float projectorWindow = projectToNavMesh ? ProjectorWindowMetres() : 50f;

        // Create a parent root object for all points
        // Identity rotation: GeoToUnityPosition already returns (X = East, Y = Up, Z = North), so no compensating rotation is
        // needed (a rotated root would also tip every imported prefab, e.g. the flat Teleport Anchor disc, onto its edge).
        GameObject root = new GameObject("GeoJSON_Root");

        if (rootParentPrefab != null)
        {
            GameObject parentInstance = (GameObject)PrefabUtility.InstantiatePrefab(rootParentPrefab) as GameObject;
            if (parentInstance != null)
            {
                parentInstance.name = "GeoJSON_Root";
                root = parentInstance;
            }
        }

        // Dataset alignment: one world-space translation of the root (zero by default). Points keep pure ENU local positions.
        root.transform.position += placementOffset;

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
            pointObject.transform.localPosition = position;

            if (projectToNavMesh)
            {
                // Staging height for the existing projector: world X/Z (ENU + Placement Offset, applied once) are kept; only Y is set,
                // to a height read from the NavMesh near this X/Z so the projector's +-50 m search window contains the real surface.
                Vector3 w = pointObject.transform.position;
                float seedY = heightSeed.Estimate(w.x, w.z, out float halfSpread);
                if (!float.IsNaN(seedY))
                {
                    pointObject.transform.position = new Vector3(w.x, seedY, w.z);
                    if (halfSpread > projectorWindow * 0.8f) countUncertainSeed++;
                }
                newPoints.Add(pointObject.transform);
            }

            if (markAsStatic)
            {
                GameObjectUtility.SetStaticEditorFlags(pointObject, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic | StaticEditorFlags.ContributeGI);
            }

            countPlaced++;
            countPlaceholder++;
        }

        Debug.Log($"Imported {countPlaced} points from GeoJSON into scene under {root.name}. Skipped: {countSkipped}, Placeholders: {countPlaceholder}");

        string projectionLine = "";
        if (projectToNavMesh && newPoints.Count > 0)
        {
            projectionLine = RunExistingProjector(root.transform, newPoints, countUncertainSeed);
            Debug.Log("[GeoJsonImporter] " + projectionLine);
        }
        EditorUtility.DisplayDialog("GeoJSON Import", $"Placed: {countPlaced}\nSkipped: {countSkipped}\nPlaceholders: {countPlaceholder}" +
                                    (projectionLine.Length > 0 ? "\n\n" + projectionLine : ""), "OK");
    }

    // --- Existing NavMeshVerticalProjector integration ---
    // NavMeshVerticalProjector is an EditorWindow with no public API: its settings are private fields and its run is the private
    // ProjectAllChildren() behind the "Project Children Vertically" button. It is used here unchanged, through reflection, with its
    // own default search settings: for each DIRECT CHILD of the chosen root it scans the vertical line at the child's X/Z from
    // (child Y + searchUp) to (child Y - searchDown) (50 m each by default), accepts the first (highest) NavMesh hit within its
    // horizontal tolerance, and with 'moveOriginals' sets the child's position to (same X, hit Y, same Z). It projects the child
    // transform itself, i.e. the prefab root; the Anchor destination keeps its prefab offset above the root. Failures are logged by
    // the projector as warnings "No NavMesh vertical projection found for '<name>' ..." and counted in its dialog.
    static readonly BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    static float ProjectorWindowMetres()
    {
        // Smaller of the projector's own searchUp / searchDown defaults (read from a throw-away instance).
        var tmp = ScriptableObject.CreateInstance<NavMeshVerticalProjector>();
        try
        {
            float up = (float)typeof(NavMeshVerticalProjector).GetField("searchUp", Inst).GetValue(tmp);
            float down = (float)typeof(NavMeshVerticalProjector).GetField("searchDown", Inst).GetValue(tmp);
            return Mathf.Min(Mathf.Abs(up), Mathf.Abs(down));
        }
        catch { return 50f; }
        finally { DestroyImmediate(tmp); }
    }

    // Runs NavMeshVerticalProjector on exactly the given points (and nothing else under the root) and returns a one-line outcome.
    static string RunExistingProjector(Transform root, List<Transform> points, int uncertainSeeds)
    {
        var stagedY = new Dictionary<Transform, float>();
        foreach (Transform t in points) stagedY[t] = t.position.y;

        // The projector handles every direct child of its root; a temporary container keeps it away from any other children a
        // Parent Prefab may carry. The container is an identity transform at the scene root, so world poses are unchanged.
        var stage = new GameObject("GeoJSON_ProjectionStage");
        foreach (Transform t in points) t.SetParent(stage.transform, true);

        var failedNames = new List<string>();
        int succeeded = -1, failed = -1;
        string problem = null;
        var projector = ScriptableObject.CreateInstance<NavMeshVerticalProjector>();
        UnityEngine.Application.LogCallback handler = (msg, stack, type) =>
        {
            const string prefix = "No NavMesh vertical projection found for '";
            if (type == LogType.Warning && msg.StartsWith(prefix))
            {
                int end = msg.IndexOf('\'', prefix.Length);
                if (end > prefix.Length) failedNames.Add(msg.Substring(prefix.Length, end - prefix.Length));
            }
        };
        try
        {
            System.Type pt = typeof(NavMeshVerticalProjector);
            FieldInfo fRoot = pt.GetField("rootObject", Inst), fOut = pt.GetField("outputParent", Inst),
                      fMarker = pt.GetField("createMarker", Inst), fMove = pt.GetField("moveOriginals", Inst),
                      fRec = pt.GetField("recursive", Inst), fOk = pt.GetField("succeeded", Inst), fBad = pt.GetField("failed", Inst);
            MethodInfo run = pt.GetMethod("ProjectAllChildren", Inst);
            if (fRoot == null || fOut == null || fMarker == null || fMove == null || fRec == null || fOk == null || fBad == null || run == null)
            {
                problem = "NavMeshVerticalProjector no longer has the expected members; nothing was projected.";
            }
            else
            {
                fRoot.SetValue(projector, stage);
                fOut.SetValue(projector, stage.transform);   // existing parent: the projector then creates no extra output object
                fMarker.SetValue(projector, false);          // move the points themselves, no marker copies
                fMove.SetValue(projector, true);
                fRec.SetValue(projector, false);
                UnityEngine.Application.logMessageReceived += handler;
                run.Invoke(projector, null);
                succeeded = (int)fOk.GetValue(projector);
                failed = (int)fBad.GetValue(projector);
            }
        }
        catch (System.Exception e)
        {
            problem = "NavMeshVerticalProjector failed: " + (e.InnerException != null ? e.InnerException.Message : e.Message);
        }
        finally
        {
            UnityEngine.Application.logMessageReceived -= handler;
            DestroyImmediate(projector);
        }

        // Back under the real root with world poses kept; then drop the container.
        foreach (Transform t in points) t.SetParent(root, true);
        DestroyImmediate(stage);

        // A point is projected only if the projector moved it off its staging height. Anything else is marked, never passed off.
        int unprojected = 0;
        float minY = float.MaxValue, maxY = float.MinValue;
        var unprojectedIds = new List<string>();
        foreach (Transform t in points)
        {
            bool moved = Mathf.Abs(t.position.y - stagedY[t]) > 1e-4f;
            if (moved && !failedNames.Contains(t.name))
            {
                minY = Mathf.Min(minY, t.position.y);
                maxY = Mathf.Max(maxY, t.position.y);
            }
            else
            {
                unprojected++;
                if (unprojectedIds.Count < 30) unprojectedIds.Add(t.name);
                t.name += "_UNPROJECTED";
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"NavMesh projection (existing NavMeshVerticalProjector): {points.Count - unprojected} projected, {unprojected} not projected");
        if (succeeded >= 0) sb.Append($" (projector reported {succeeded} succeeded, {failed} failed)");
        if (points.Count - unprojected > 0) sb.Append($"; final Y {minY:F1}..{maxY:F1}");
        sb.Append('.');
        if (uncertainSeeds > 0) sb.Append($" {uncertainSeeds} point(s) had a poor staging estimate (steep or stacked NavMesh nearby).");
        if (unprojected > 0) sb.Append(" Not projected (renamed _UNPROJECTED): " + string.Join(", ", unprojectedIds) + (unprojected > unprojectedIds.Count ? ", ..." : "") + ".");
        if (problem != null) sb.Append(" " + problem);
        return sb.ToString();
    }

    // Coarse height of the loaded NavMesh near an X/Z, used only to stage points inside the projector's search window. It is not a
    // projection: it never produces a final Y. Source: the vertices of the currently loaded NavMesh (NavMesh.CalculateTriangulation),
    // mid-range of the 8 vertices nearest to the point horizontally.
    class NavMeshHeightSeed
    {
        const float Cell = 25f;
        const int K = 8;
        readonly Vector3[] verts;
        readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();

        NavMeshHeightSeed(Vector3[] v)
        {
            verts = v;
            for (int i = 0; i < v.Length; i++)
            {
                long key = Key(Mathf.FloorToInt(v[i].x / Cell), Mathf.FloorToInt(v[i].z / Cell));
                if (!cells.TryGetValue(key, out List<int> list)) cells[key] = list = new List<int>();
                list.Add(i);
            }
        }

        static long Key(int cx, int cz) { return ((long)cx << 32) ^ (uint)cz; }

        public static NavMeshHeightSeed Build()
        {
            NavMeshTriangulation t = NavMesh.CalculateTriangulation();
            return (t.vertices == null || t.vertices.Length == 0) ? null : new NavMeshHeightSeed(t.vertices);
        }

        // Returns NaN if no NavMesh vertex is within ~1.6 km. halfSpread = half the height range of the vertices used.
        public float Estimate(float x, float z, out float halfSpread)
        {
            halfSpread = 0f;
            int cx = Mathf.FloorToInt(x / Cell), cz = Mathf.FloorToInt(z / Cell);
            var found = new List<int>();
            for (int r = 1; r <= 64; r *= 2)
            {
                found.Clear();
                for (int a = cx - r; a <= cx + r; a++)
                    for (int b = cz - r; b <= cz + r; b++)
                        if (cells.TryGetValue(Key(a, b), out List<int> list)) found.AddRange(list);
                if (found.Count >= K) break;
            }
            if (found.Count == 0) return float.NaN;

            found.Sort((i, j) => Sq(verts[i], x, z).CompareTo(Sq(verts[j], x, z)));
            float lo = float.MaxValue, hi = float.MinValue;
            for (int n = 0; n < Mathf.Min(K, found.Count); n++)
            {
                float y = verts[found[n]].y;
                lo = Mathf.Min(lo, y);
                hi = Mathf.Max(hi, y);
            }
            halfSpread = 0.5f * (hi - lo);
            return 0.5f * (hi + lo);
        }

        static float Sq(Vector3 v, float x, float z) { float dx = v.x - x, dz = v.z - z; return dx * dx + dz * dz; }
    }

    // --- Coordinate conversion using WGS84 to ENU (meters) ---
    const double a = 6378137.0; // semi-major axis
    const double f = 1.0 / 298.257223563; // flattening
    const double b = a * (1 - f);
    const double e2 = (a * a - b * b) / (a * a);
    const double DegToRad = System.Math.PI / 180.0; // double (Mathf.Deg2Rad is a float constant)

    struct Vec3d { public double x, y, z; public Vec3d(double X, double Y, double Z) { x = X; y = Y; z = Z; } }

    Vec3d GeoToECEF(double latDeg, double lonDeg, double h)
    {
        // Double precision throughout: casting the angles to float (Mathf.Sin((float)lat)) costs up to ~0.5 m at ECEF scale.
        double lat = latDeg * DegToRad;
        double lon = lonDeg * DegToRad;
        double sinLat = System.Math.Sin(lat);
        double cosLat = System.Math.Cos(lat);
        double sinLon = System.Math.Sin(lon);
        double cosLon = System.Math.Cos(lon);

        double N = a / System.Math.Sqrt(1 - e2 * sinLat * sinLat);
        double x = (N + h) * cosLat * cosLon;
        double y = (N + h) * cosLat * sinLon;
        double z = (N * (1 - e2) + h) * sinLat;
        return new Vec3d(x, y, z);
    }

    Vec3d EcefToEnu(Vec3d pointEcef, Vec3d originEcef, double refLatDeg, double refLonDeg)
    {
        double lat = refLatDeg * DegToRad;
        double lon = refLonDeg * DegToRad;

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

        // Vec3d holds (x = East, y = North, z = Up). Map to Unity: X = East, Y = Up, Z = North (same as CityKMLImporter).
        return new Vector3((float)enu.x, (float)enu.z, (float)enu.y);
    }
}
