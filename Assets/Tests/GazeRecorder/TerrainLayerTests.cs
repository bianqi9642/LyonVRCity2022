// TerrainLayerTests.cs
// Regression tests for the terrain collider Layer correction (schema v17 work).
//
// WHY: the TIN terrain colliders of "Grid Area" / "Organic Area" (14 prefab instances) and the SouthRiver_TIN_GapPatch were on the Default layer while the bridges (PONT) and the Practice Area tiles were on
// Ground. Everything that is layer-based therefore saw almost no terrain: ViewFrameProbe.TryGroundY (Ground mask) found a floor in 53 of 1266 gaze rows of the audited session and AoiClassifier called
// ~295 terrain hits OTHER instead of TERRAIN. The scene now puts every genuine terrain collider on Ground (layer 3).
//
// WHAT THESE TESTS ESTABLISH
//   * Scene file (YAML) tests: every MeshCollider whose mesh is a TIN tile, a PONT bridge or the TIN gap patch is on Ground; every other collider keeps the layer it had (building colliders 0 / 6,
//     exploration-boundary BoxColliders 2, attractor SphereColliders 0); the masks that matter (gaze ROI, AutoGroundAlign) and the physics collision matrix are as analysed. These tests read
//     Assets/Scenes/LyonVRVity35.unity and are skipped (Ignore) when the scene file is not part of the project being tested.
//   * Physics tests with synthetic geometry: a Ground-layer collider gives the ground height, an NPC standing on it never does, a Ground-layer hit classifies as TERRAIN, an NPC as NPC.
// They do NOT test the real terrain geometry; the replay of a recorded session against the real colliders was a separate scratch-project measurement (see docs/schema_v17_changes.md).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public class TerrainLayerTests
{
    // ---------------------------------------------------------------------------------------------------------------- minimal Unity-YAML reader
    sealed class Doc { public int cls; public long id; public bool stripped; public string body; }
    static Dictionary<long, Doc> s_docs; static Dictionary<string, string> s_guidToPath;
    const int Ground = 3;

    static string DataPath => Application.dataPath;

    static void LoadScene()
    {
        if (s_docs != null) return;
        string path = Path.Combine(DataPath, "Scenes", "LyonVRVity35.unity");
        if (!File.Exists(path)) Assert.Ignore("scene file not present in this project: " + path);
        string text = File.ReadAllText(path);
        var rx = new Regex(@"^--- !u!(\d+) &(-?\d+)( stripped)?[ \t]*\r?$", RegexOptions.Multiline);
        var ms = rx.Matches(text); var docs = new Dictionary<long, Doc>(ms.Count);
        for (int i = 0; i < ms.Count; i++)
        {
            int start = ms[i].Index + ms[i].Length, end = i + 1 < ms.Count ? ms[i + 1].Index : text.Length;
            long id = long.Parse(ms[i].Groups[2].Value);
            docs[id] = new Doc { cls = int.Parse(ms[i].Groups[1].Value), id = id, stripped = ms[i].Groups[3].Success, body = text.Substring(start, end - start) };
        }
        s_docs = docs;
        s_guidToPath = new Dictionary<string, string>();
        foreach (string sub in new[] { "Models/TIN", "Models/PONT" })
        {
            string dir = Path.Combine(DataPath, sub); if (!Directory.Exists(dir)) continue;
            foreach (string meta in Directory.GetFiles(dir, "*.meta", SearchOption.AllDirectories))
            {
                string first = File.ReadLines(meta).Skip(1).FirstOrDefault() ?? "";
                var m = Regex.Match(first, @"guid: ([0-9a-f]{32})"); if (m.Success) s_guidToPath[m.Groups[1].Value] = meta.Substring(0, meta.Length - 5).Replace('\\', '/');
            }
        }
    }

    static string Field(string body, string name) { var m = Regex.Match(body, @"^\s*" + Regex.Escape(name) + @":[ \t]*(.*?)\r?$", RegexOptions.Multiline); return m.Success ? m.Groups[1].Value.Trim() : null; }
    static long Ref(string v) { if (v == null) return 0; var m = Regex.Match(v, @"fileID:\s*(-?\d+)"); return m.Success ? long.Parse(m.Groups[1].Value) : 0; }
    static string Guid(string v) { if (v == null) return ""; var m = Regex.Match(v, @"guid:\s*([0-9a-f]{32})"); return m.Success ? m.Groups[1].Value : ""; }

    // m_Layer overrides of one prefab instance, keyed by the fileID of the source GameObject they target
    static readonly Dictionary<long, Dictionary<long, int>> s_layerMods = new Dictionary<long, Dictionary<long, int>>();
    static Dictionary<long, int> LayerMods(long prefabInstance)
    {
        if (s_layerMods.TryGetValue(prefabInstance, out var d)) return d;
        d = new Dictionary<long, int>();
        if (s_docs.TryGetValue(prefabInstance, out var pi))
            foreach (Match m in Regex.Matches(pi.body, @"- target: \{fileID: (-?\d+), guid: [0-9a-f]+, type: \d\}\s*\r?\n\s+propertyPath: m_Layer\s*\r?\n\s+value: (-?\d+)"))
                d[long.Parse(m.Groups[1].Value)] = int.Parse(m.Groups[2].Value);
        s_layerMods[prefabInstance] = d; return d;
    }

    /// <summary>Effective layer of a GameObject of the scene: its own m_Layer, or for a prefab-instance object the instance's m_Layer override, else the prefab default (0 for every prefab used here: glTF imports use layer 0).</summary>
    static int EffectiveLayer(long goId)
    {
        var go = s_docs[goId];
        if (!go.stripped) return int.Parse(Field(go.body, "m_Layer") ?? "0");
        long pi = Ref(Field(go.body, "m_PrefabInstance")), src = Ref(Field(go.body, "m_CorrespondingSourceObject"));
        return LayerMods(pi).TryGetValue(src, out int l) ? l : 0;
    }

    sealed class ColliderInfo { public int cls; public long go; public int layer; public string meshPath = ""; public string meshGuid = ""; }
    static List<ColliderInfo> Colliders()
    {
        LoadScene();
        var l = new List<ColliderInfo>();
        foreach (var d in s_docs.Values)
        {
            if (d.stripped || (d.cls != 64 && d.cls != 65 && d.cls != 135 && d.cls != 136)) continue;
            long go = Ref(Field(d.body, "m_GameObject")); if (!s_docs.ContainsKey(go)) continue;
            var c = new ColliderInfo { cls = d.cls, go = go, layer = EffectiveLayer(go) };
            if (d.cls == 64) { c.meshGuid = Guid(Field(d.body, "m_Mesh")); s_guidToPath.TryGetValue(c.meshGuid, out c.meshPath); c.meshPath = c.meshPath ?? ""; }
            l.Add(c);
        }
        return l;
    }

    static bool IsTerrainMesh(ColliderInfo c) => c.cls == 64 && (c.meshPath.Contains("/Models/TIN/") || c.meshPath.Contains("/Models/PONT/"));

    // ---------------------------------------------------------------------------------------------------------------- scene-file tests

    [Test]
    public void Scene_EveryTerrainCollider_TinTiles_Bridges_AndTheGapPatch_IsOnTheGroundLayer()
    {
        var all = Colliders(); var terrain = all.Where(IsTerrainMesh).ToList();
        Assert.GreaterOrEqual(terrain.Count, 37, "20 TIN tile colliders + 16 PONT bridge colliders + the TIN gap patch (counted by mesh path below)");
        var notGround = terrain.Where(c => c.layer != Ground).Select(c => Field(s_docs[c.go].body, "m_Name") ?? ("go " + c.go + " (" + Path.GetFileName(c.meshPath) + ")")).ToList();
        Assert.IsEmpty(notGround, "terrain / bridge colliders that are not on Ground: " + string.Join(", ", notGround));
        // the gap patch uses a mesh asset under Models/TIN/Patches
        Assert.IsTrue(terrain.Any(c => c.meshPath.EndsWith("SouthRiver_TIN_GapPatch.asset")), "the TIN gap patch collider is part of the terrain set");
        // the 14 colliders this change touched (Grid Area / Organic Area, Textured variants) are all identified by their mesh path under Models/TIN/Textured
        int textured = terrain.Count(c => c.meshPath.Contains("/Models/TIN/Textured/")); Assert.AreEqual(14, textured, "14 Textured TIN tile colliders in Grid Area / Organic Area");
        Assert.IsTrue(terrain.Where(c => c.meshPath.Contains("/Models/TIN/Textured/")).All(c => c.layer == Ground));
    }

    [Test]
    public void Scene_NonTerrainColliders_KeepTheirOriginalLayers()
    {
        var all = Colliders();
        var nonTerrainMesh = all.Where(c => c.cls == 64 && !IsTerrainMesh(c)).ToList();
        var byLayer = nonTerrainMesh.GroupBy(c => c.layer).ToDictionary(g => g.Key, g => g.Count());
        foreach (var k in byLayer.Keys) Assert.IsTrue(k == 0 || k == 6, "non-terrain mesh colliders are only on Default (0) or TargetPreview (6), found layer " + k);
        Assert.AreEqual(0, byLayer.ContainsKey(Ground) ? byLayer[Ground] : 0, "no building / helper mesh collider was moved to Ground");
        Assert.AreEqual(9, byLayer.ContainsKey(6) ? byLayer[6] : 0, "the 9 target-preview candidate colliders are unchanged (layer 6)");
        var boxes = all.Where(c => c.cls == 65).ToList(); Assert.AreEqual(159, boxes.Count, "exploration boundary walls / posts / area volume");
        Assert.IsTrue(boxes.All(c => c.layer == 2), "boundary BoxColliders keep Ignore Raycast (2): they must stay invisible to gaze and ground queries");
        var spheres = all.Where(c => c.cls == 135).ToList(); Assert.IsTrue(spheres.All(c => c.layer == 0), "attractor / start-point SphereColliders unchanged");
    }

    [Test]
    public void Scene_TheMasksThatMatter_StillIncludeGround_AndTheCollisionMatrixIsUnchangedForDefaultVsGround()
    {
        LoadScene();
        string text = File.ReadAllText(Path.Combine(DataPath, "Scenes", "LyonVRVity35.unity"));
        var roi = Regex.Match(text, @"roiLayerMask:\s*\r?\n\s+serializedVersion: 2\s*\r?\n\s+m_Bits: (\d+)"); Assert.IsTrue(roi.Success, "GazeRecorder.roiLayerMask in the scene");
        uint roiBits = uint.Parse(roi.Groups[1].Value);
        Assert.AreNotEqual(0u, roiBits & (1u << Ground), "the gaze ROI raycast includes Ground (terrain stays hittable)"); Assert.AreNotEqual(0u, roiBits & 1u, "...and Default (buildings)");
        var ag = Regex.Match(text, @"groundLayer:\s*\r?\n\s+serializedVersion: 2\s*\r?\n\s+m_Bits: (\d+)"); Assert.IsTrue(ag.Success, "AutoGroundAlign.groundLayer in the scene");
        Assert.AreEqual(1u << Ground, uint.Parse(ag.Groups[1].Value), "AutoGroundAlign (and therefore the ViewFrame ground query that falls back to it) uses exactly the Ground layer");
        Assert.AreEqual(Ground, LayerMask.NameToLayer("Ground"), "layer 3 is named Ground");
        // physics collision matrix: Default (0) and Ground (3) collide with every layer, so a layer swap cannot change a collision
        string dyn = Path.Combine(Directory.GetParent(DataPath).FullName, "ProjectSettings", "DynamicsManager.asset");
        if (!File.Exists(dyn)) Assert.Ignore("DynamicsManager.asset not found");
        string matrix = Regex.Match(File.ReadAllText(dyn), @"m_LayerCollisionMatrix: ([0-9a-f]+)").Groups[1].Value; Assert.AreEqual(256, matrix.Length);
        Assert.AreEqual("ffffffff", matrix.Substring(0 * 8, 8), "layer Default collides with all layers"); Assert.AreEqual("ffffffff", matrix.Substring(3 * 8, 8), "layer Ground collides with all layers");
    }

    // ---------------------------------------------------------------------------------------------------------------- physics tests (synthetic geometry)

    readonly List<GameObject> _objs = new List<GameObject>();
    [TearDown] public void TearDown() { foreach (var o in _objs) if (o != null) UnityEngine.Object.DestroyImmediate(o); _objs.Clear(); CallStatic(T("AoiClassifier"), "ResetDiagnostics"); }

    GameObject Terrain(float y, int layer, string name = "terrain")
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Plane); g.name = name; g.transform.position = new Vector3(0, y, 0); g.transform.localScale = new Vector3(20, 1, 20); g.layer = layer; _objs.Add(g); return g;
    }

    GameObject Npc(Vector3 pos, int layer)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Capsule); g.name = "NpcBody"; g.transform.position = pos; g.layer = layer; g.AddComponent(T("PedestrianAgent")); _objs.Add(g); return g;
    }

    static object GroundProbe(out bool ok, Vector3 from, out float y, int mask = 1 << Ground)
    {
        var p = Activator.CreateInstance(T("ViewFrameProbe")); Set(p, "GroundMask", mask);
        var args = new object[] { from, 0f }; ok = (bool)p.GetType().GetMethod("TryGroundY").Invoke(p, args); y = (float)args[1]; return p;
    }

    static string Classify(Collider c) => Get<string>(CallStatic(T("AoiClassifier"), "Classify", true, c, null, null), "semanticClass");

    [Test]
    public void GroundHeight_HitsTheGroundLayerTerrain_NotAnNpcStandingOnIt_AndNotADefaultLayerSurface()
    {
        Assert.AreEqual(Ground, LayerMask.NameToLayer("Ground"));
        Terrain(100f, Ground);
        Npc(new Vector3(0, 100.9f, 0), 0);   // an NPC capsule (default layer) right under the probe origin, top at 101.9
        Physics.SyncTransforms();
        GroundProbe(out bool ok, new Vector3(0, 103f, 0), out float y);
        Assert.IsTrue(ok); Assert.AreEqual(100f, y, 1e-3f, "the ground is the terrain surface, never the NPC's head");
        // even an NPC collider wrongly placed on the Ground layer is filtered by the PedestrianAgent rule
        Npc(new Vector3(0, 100.9f, 0), Ground); Physics.SyncTransforms();
        GroundProbe(out ok, new Vector3(0, 103f, 0), out y); Assert.IsTrue(ok); Assert.AreEqual(100f, y, 1e-3f, "an NPC is never ground, whatever its layer");
        // a surface on the Default layer is not ground (this is why the terrain layers had to be corrected)
        foreach (var o in _objs.ToArray()) UnityEngine.Object.DestroyImmediate(o); _objs.Clear();
        Terrain(100f, 0, "gml_default_layer_tile"); Physics.SyncTransforms();
        GroundProbe(out ok, new Vector3(0, 103f, 0), out y); Assert.IsFalse(ok, "a terrain collider left on Default yields NO ground height (blank ground_y)");
        // the 12 m range limit is unchanged
        foreach (var o in _objs.ToArray()) UnityEngine.Object.DestroyImmediate(o); _objs.Clear();
        Terrain(100f, Ground); Physics.SyncTransforms();
        GroundProbe(out ok, new Vector3(0, 111f, 0), out y); Assert.IsTrue(ok);
        GroundProbe(out ok, new Vector3(0, 113f, 0), out y); Assert.IsFalse(ok, "13 m above the surface: beyond the 12 m ground ray");
    }

    [Test]
    public void Classification_GroundLayerTerrainIsTERRAIN_DefaultLayerTileIsOTHER_AndAnNpcIsNeverTerrain()
    {
        var t = Terrain(100f, Ground, "gml_tile_on_ground"); Physics.SyncTransforms();
        Assert.AreEqual("TERRAIN", Classify(t.GetComponent<Collider>()));
        CallStatic(T("AoiClassifier"), "ResetDiagnostics");
        var d = Terrain(100f, 0, "gml_tile_on_default"); Physics.SyncTransforms();
        Assert.AreEqual("OTHER", Classify(d.GetComponent<Collider>()), "the classifier is layer based: this is the pre-change behaviour of the 14 Textured TIN tiles");
        CallStatic(T("AoiClassifier"), "ResetDiagnostics");
        var n = Npc(new Vector3(50, 100.9f, 50), Ground);   // even on the Ground layer an NPC is classified by its PedestrianAgent first
        Assert.AreEqual("NPC", Classify(n.GetComponent<Collider>()));
    }


    // ---------------------------------------------------------------------------------------------------------------- AutoGroundAlign (real component, synthetic surfaces)
    // AutoGroundAlign (scene values: raycastDistance 3, groundLayer = Ground, smoothSpeed 10, heightOffset 0) every frame casts from (base + 0.1 m) straight down and lerps base.y towards the closest hit.
    GameObject Rig(Vector3 pos)
    {
        var rig = new GameObject("RigBase"); rig.transform.position = pos; _objs.Add(rig);
        var ag = rig.AddComponent(T("AutoGroundAlign")); Set(ag, "groundLayer", (LayerMask)(1 << Ground)); Set(ag, "raycastDistance", 3f); Set(ag, "smoothSpeed", 10f); Set(ag, "heightOffset", 0f);
        return rig;
    }

    // AutoGroundAlign lerps with Time.deltaTime * 10: wait REAL time (a batch-mode frame is a millisecond), n x 25 ms
    // waits (bounded, real time) until the base is within 1.5 mm of the target
    IEnumerator Settle(Transform t, float target, float timeout = 10f) { float t0 = Time.realtimeSinceStartup; while (Mathf.Abs(t.position.y - target) > 0.0015f && Time.realtimeSinceStartup - t0 < timeout) yield return null; }

    IEnumerator Frames(int n) { float t0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - t0 < n * 0.025f) yield return null; }

    [UnityTest]
    public IEnumerator AutoGroundAlign_SettlesOnAGroundLayerTerrain_MonotonicallyWithoutOscillation_AndStaysThereWhenStationary()
    {
        Terrain(100f, Ground); Physics.SyncTransforms();
        var rig = Rig(new Vector3(0, 100.08f, 0));   // a landing 8 cm above the surface (a teleport anchor sits 4-14 cm above the terrain in the audited route)
        var ys = new List<float>();
        float tEnd = Time.realtimeSinceStartup + 10f; while (Mathf.Abs(rig.transform.position.y - 100f) > 0.0015f && Time.realtimeSinceStartup < tEnd) { yield return null; ys.Add(rig.transform.position.y); }
        Assert.AreEqual(100f, rig.transform.position.y, 2e-3f, "the base settles on the terrain surface"); Assert.Greater(ys.Count, 5);
        for (int i = 1; i < ys.Count; i++) Assert.LessOrEqual(ys[i], ys[i - 1] + 1e-6f, "monotone descent: no oscillation / overshoot at frame " + i);
        float settled = rig.transform.position.y;
        yield return Frames(30);
        Assert.LessOrEqual(Mathf.Abs(rig.transform.position.y - 100f), Mathf.Abs(settled - 100f) + 1e-6f, "a stationary participant is only ever moved closer to the surface, never away from it (no hunting)"); Assert.AreEqual(100f, rig.transform.position.y, 1.6e-3f);
        // XZ is never touched by the component
        Assert.AreEqual(0f, rig.transform.position.x); Assert.AreEqual(0f, rig.transform.position.z);
    }

    [UnityTest]
    public IEnumerator AutoGroundAlign_IgnoresATerrainLeftOnTheDefaultLayer_ThisIsWhyTheLayerCorrectionChangesIt()
    {
        Terrain(100f, 0, "gml_default_layer_tile"); Physics.SyncTransforms();
        var rig = Rig(new Vector3(0, 100.08f, 0)); yield return Frames(20);
        Assert.AreEqual(100.08f, rig.transform.position.y, 1e-5f, "no Ground surface: the rig keeps the height it was teleported to");
        foreach (var o in _objs.ToArray()) if (o != null && o != rig) UnityEngine.Object.DestroyImmediate(o);
        Terrain(100f, Ground); Physics.SyncTransforms(); yield return Settle(rig.transform, 100f);
        Assert.AreEqual(100f, rig.transform.position.y, 2e-3f, "the same terrain on Ground: the rig is aligned");
    }

    [UnityTest]
    public IEnumerator AutoGroundAlign_Characterisation_CorrectsOnlyWithinItsRayWindow_ARigMoreThan10cmBelowTheSurfaceIsNotLifted_Within10cmIs()
    {
        // CHARACTERISATION of the existing behaviour (not an endorsement): the ray starts 0.1 m above the base, so a one-sided mesh surface more than 0.1 m ABOVE the base is never hit and the rig stays below it.
        // In the audited route the Part 3 start point was 0.131 m below the terrain: the first 15 s of the trial were recorded with the rig base under the surface (299 rows). Not changed by the schema-v17 work.
        Terrain(100f, Ground); Physics.SyncTransforms();
        var low = Rig(new Vector3(0, 99.869f, 0)); yield return Frames(30);
        Assert.AreEqual(99.869f, low.transform.position.y, 1e-5f, "13 cm below the surface: not corrected");
        var near = Rig(new Vector3(5, 99.95f, 0)); yield return Settle(near.transform, 100f);
        Assert.AreEqual(100f, near.transform.position.y, 3e-3f, "5 cm below the surface: the ray starts 5 cm ABOVE the plane (base + 0.1 m), so it IS hit and the rig is lifted to the surface");
    }

    [UnityTest]
    public IEnumerator AutoGroundAlign_PicksTheClosestSurfaceBelow_BridgeAboveTerrain_AndNeverTheUndersideOrAnNpcCollider()
    {
        Terrain(100f, Ground, "terrain");                                // terrain
        var deck = Terrain(101.5f, Ground, "bridge_deck"); deck.transform.localScale = new Vector3(2, 1, 2);   // a bridge deck 1.5 m above it, 20 x 20 m
        Physics.SyncTransforms();
        var onDeck = Rig(new Vector3(0, 101.55f, 0)); var underDeck = Rig(new Vector3(50, 100.4f, 0));
        yield return Settle(onDeck.transform, 101.5f); yield return Settle(underDeck.transform, 100f);
        Assert.AreEqual(101.5f, onDeck.transform.position.y, 3e-3f, "on the deck: the deck (closest below) is the ground");
        Assert.AreEqual(100f, underDeck.transform.position.y, 3e-3f, "(outside the deck's footprint, 0.4 m above the terrain: the terrain is the ground)");
        // beneath the deck: the deck is ABOVE the ray origin, so it is not selected; the terrain below is
        var beneath = Rig(new Vector3(0.5f, 100.45f, 0.5f)); yield return Settle(beneath.transform, 100f);
        Assert.AreEqual(100f, beneath.transform.position.y, 3e-3f, "under the deck the underside is never selected: the terrain is");
    }

    [Test]
    public void NpcColliders_AreNeverOnGroundAtRuntime_ThePrefabRootIsLayer3ButAIStatsForcesItOffAtAwake()
    {
        // AutoGroundAlign has no NPC filter. The NPC prefab's root (tag Npc, capsule 2 m x 0.2 m) is serialised on layer 3 (Ground); EasyNpcs AI_Stats.Awake assigns the layer named "Npc"; no such layer exists, so the
        // documented fallback is Default (0). Audited evidence: all 21 recorded NPC gaze hits had hit_layer 0. This test pins the chain: if an "Npc" layer is ever added it must not be Ground, and AI_Stats must stay on the prefab.
        string prefab = Path.Combine(DataPath, "Prefabs", "Npc.prefab"), stats = Path.Combine(DataPath, "EasyNpcs", "Scripts", "AI", "AI_Stats.cs"), meta = stats + ".meta";
        if (!File.Exists(prefab) || !File.Exists(stats)) Assert.Ignore("NPC prefab / AI_Stats not part of the project being tested");
        string guid = Regex.Match(File.ReadAllText(meta), @"guid: ([0-9a-f]{32})").Groups[1].Value;
        string pf = File.ReadAllText(prefab);
        Assert.IsTrue(pf.Contains("guid: " + guid), "AI_Stats is on the NPC prefab");
        string src = File.ReadAllText(stats);
        StringAssert.Contains("gameObject.layer = idx;", src); StringAssert.Contains("LayerMask.NameToLayer(layerName)", src); StringAssert.Contains("idx = 0;", src);
        int npcLayer = LayerMask.NameToLayer("Npc");
        Assert.AreNotEqual(Ground, npcLayer, "an Npc layer, if one exists, must not be the Ground layer");
        if (npcLayer < 0) Assert.Pass("no layer named Npc: AI_Stats falls back to Default (0), never Ground");
    }

    [Test]
    public void GazeRoiMask123_HitsGroundLayerTerrain_AndGroundIsPartOfTheNpcLosMask()
    {
        Terrain(100f, Ground); Physics.SyncTransforms();
        Assert.IsTrue(Physics.Raycast(new Vector3(0, 110f, 0), Vector3.down, out var hit, 50f, 123), "the ROI mask (123) reaches a Ground-layer surface");
        Assert.AreEqual(Ground, hit.collider.gameObject.layer);
        // the LOS mask is built from Default | Ground (DataCaptureManager.Start): moving terrain Default -> Ground keeps it inside the mask
        int los = (1 << 0) | (1 << LayerMask.NameToLayer("Ground"));
        Assert.AreNotEqual(0, los & (1 << Ground)); Assert.AreNotEqual(0, los & 1);
    }
}
