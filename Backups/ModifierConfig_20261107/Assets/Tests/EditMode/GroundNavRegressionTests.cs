// GroundNavRegressionTests.cs  (EditMode, Editor-only assembly GazeRecorder.EditModeTests)
// Real-scene regression tests for the NavMesh / ground-collision audit (docs/navmesh_ground_audit.md).
//   1. NavMeshSurface source: no building (BATI) geometry may be collected as WALKABLE; the visual TIN renderers are not sources, the repaired proxies are.
//   2. Ground colliders: no down-facing triangles (one-sided MeshCollider holes) and every TIN collider uses a repaired mesh.
//   3. A CharacterController with the rig's parameters lands on every formerly down-facing TIN triangle; the AutoGroundAlign ray hits it.
//   4. The baked NavMesh has no polygon whose three corners are all > 1 m above the ground (rooftops).
//   5. GroundFallRecovery (synthetic ground): restores a fallen rig, ignores a scripted teleport.
// The scene is opened ADDITIVELY and closed without saving; terrain groups that the scene saves inactive are switched on IN MEMORY only (same convention as StartPointGroundRealSceneTests).
// Ignored when the scene is already open in the Editor, absent, or no terrain is imported. Offline geometry only - NOT headset validation.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

[Category("RealScene")]
public class GroundNavRegressionTests
{
    const string ScenePath = "Assets/Scenes/LyonVRVity35.unity";
    Scene _scene; bool _opened; bool _prevAutoSync;

    [OneTimeSetUp]
    public void Open()
    {
        if (!File.Exists(ScenePath)) Assert.Ignore("scene file not present: " + ScenePath);
        for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).path == ScenePath) Assert.Ignore("the experiment scene is open in the Editor; close it (or run in batch mode)");
        bool ignore = LogAssert.ignoreFailingMessages; LogAssert.ignoreFailingMessages = true;
        try { _scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive); _opened = true; } finally { LogAssert.ignoreFailingMessages = ignore; }
        foreach (var root in _scene.GetRootGameObjects()) foreach (var tr in root.GetComponentsInChildren<Transform>(true)) if (tr.name == "TIN_Textured" || tr.name == "Practice Area" || tr.name == "Grid Area" || tr.name == "Organic Area" || tr.name == "NavMeshGroundProxy" || tr.name == "EnvironmentConfig" || tr.name == "NavMesh") tr.gameObject.SetActive(true);   // in memory only
        _prevAutoSync = Physics.autoSyncTransforms; Physics.autoSyncTransforms = true; Physics.SyncTransforms();   // restored in TearDown (project setting)
        if (!AllIn<MeshCollider>().Any(m => m.gameObject.layer == 3 && m.sharedMesh != null)) Assert.Ignore("no Ground-layer terrain mesh is imported in this project");
    }

    [OneTimeTearDown]
    public void Close() { Physics.autoSyncTransforms = _prevAutoSync; if (_opened && _scene.IsValid()) EditorSceneManager.CloseScene(_scene, true); }

    IEnumerable<T> AllIn<T>() where T : Component { return _scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true)); }
    static string AssetPath(UnityEngine.Object o) { return o == null ? "" : AssetDatabase.GetAssetPath(o); }
    static bool IsTin(string ap) { return ap.Contains("/Models/TIN/") && !ap.Contains("/Models/TIN/Patches/"); }

    [Test]
    public void NavMeshSurface_CollectsNoWalkableBuilding_AndOnlyRepairedProxiesAsTin()
    {
        var surfType = Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation"); Assert.IsNotNull(surfType, "Unity.AI.Navigation not available");
        var surface = (Component)_scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(surfType, true)).FirstOrDefault(); Assert.IsNotNull(surface, "no NavMeshSurface in the scene");
        var sources = (IList<NavMeshBuildSource>)surfType.GetMethod("CollectSources", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(surface, null);
        int buildingWalkable = 0, buildings = 0, visualTin = 0, proxies = 0, walkable = 0;
        foreach (var s in sources)
        {
            string ap = AssetPath(s.sourceObject);
            if (ap.Contains("/Models/BATI/")) { buildings++; if (s.area != 1) buildingWalkable++; }
            else if (ap.StartsWith("Assets/Models/TIN/Repaired/")) proxies++;
            else if (IsTin(ap)) visualTin++;
            if (s.area != 1) walkable++;
        }
        TestContext.Out.WriteLine("sources=" + sources.Count + " buildings(not walkable)=" + buildings + " visualTin=" + visualTin + " repairedProxies=" + proxies + " walkable-area sources=" + walkable);
        Assert.Greater(buildings, 0, "buildings must stay in the build as obstacles");
        Assert.AreEqual(0, buildingWalkable, "building geometry contributes WALKABLE area (rooftops)");
        Assert.AreEqual(0, visualTin, "visual TIN renderers must not be NavMesh sources");
        Assert.Greater(proxies, 0, "repaired ground proxies are not collected");
    }

    [Test]
    public void GroundColliders_HaveNoDownFacingTriangles_AndUseRepairedMeshes()
    {
        int tiles = 0; var bad = new List<string>();
        foreach (var c in AllIn<MeshCollider>().Where(m => m.gameObject.layer == 3 && m.sharedMesh != null))
        {
            string ap = AssetPath(c.sharedMesh); if (!IsTin(ap)) continue; tiles++;
            if (!ap.StartsWith("Assets/Models/TIN/Repaired/")) { bad.Add(c.name + " still uses " + ap); continue; }
            var v = c.sharedMesh.vertices; var t = c.sharedMesh.triangles; var m = c.transform.localToWorldMatrix; int down = 0;
            for (int i = 0; i < t.Length; i += 3)
            {
                var a = m.MultiplyPoint3x4(v[t[i]]); var b = m.MultiplyPoint3x4(v[t[i + 1]]); var d = m.MultiplyPoint3x4(v[t[i + 2]]); var n = Vector3.Cross(b - a, d - a);
                if (n.magnitude > 2e-6f && n.y < -1e-3f * n.magnitude) down++;
            }
            if (down > 0) bad.Add(c.name + ": " + down + " down-facing triangles");
        }
        Assert.Greater(tiles, 0); Assert.IsEmpty(bad, string.Join("; ", bad));
    }

    [Test]
    public void CharacterController_LandsOnFormerlyFlippedTriangles_AndAutoGroundAlignRayHits()
    {
        // formerly flipped = down-facing in the ORIGINAL visual TIN meshes (which are untouched)
        var tris = new List<Vector3>();
        foreach (var mf in AllIn<MeshFilter>())
        {
            string ap = AssetPath(mf.sharedMesh); if (!IsTin(ap) || ap.StartsWith("Assets/Models/TIN/Repaired/")) continue;
            var v = mf.sharedMesh.vertices; var t = mf.sharedMesh.triangles; var m = mf.transform.localToWorldMatrix;
            for (int i = 0; i < t.Length; i += 3)
            {
                var a = m.MultiplyPoint3x4(v[t[i]]); var b = m.MultiplyPoint3x4(v[t[i + 1]]); var d = m.MultiplyPoint3x4(v[t[i + 2]]); var n = Vector3.Cross(b - a, d - a);
                if (0.5f * n.magnitude >= 0.05f && n.y < -1e-3f * n.magnitude) tris.Add((a + b + d) / 3f);
            }
        }
        Assert.Greater(tris.Count, 20, "expected the original TIN to contain down-facing triangles to test against");
        var rnd = new System.Random(7); var pick = tris.OrderBy(x => rnd.Next()).Take(80).ToList();
        var go = new GameObject("__ccTest") { layer = 2 }; var cc = go.AddComponent<CharacterController>(); cc.height = 1.36144f; cc.radius = 0.1f; cc.skinWidth = 0.08f; cc.stepOffset = 0.5f; cc.slopeLimit = 45f; cc.center = new Vector3(0, 0.7607f, 0);
        int fell = 0, agaMiss = 0;
        try
        {
            foreach (var p in pick)
            {
                Physics.Raycast(new Vector3(p.x, p.y + 30f, p.z), Vector3.down, out var g, 80f, 1 << 3, QueryTriggerInteraction.Ignore); float gy = g.collider != null ? g.point.y : p.y;
                go.transform.position = new Vector3(p.x, gy + 0.4f, p.z); Physics.SyncTransforms(); cc.enabled = false; cc.enabled = true;
                for (int s = 0; s < 6; s++) cc.Move(new Vector3(0, -0.5f, 0));
                if (go.transform.position.y < gy - 0.25f) fell++;
                if (!Physics.Raycast(new Vector3(p.x, gy + 0.1f, p.z), Vector3.down, 3f, 1 << 3, QueryTriggerInteraction.Ignore)) agaMiss++;
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
        Assert.AreEqual(0, fell, "CharacterController fell through " + fell + "/" + pick.Count + " formerly flipped triangles");
        Assert.AreEqual(0, agaMiss, "AutoGroundAlign ray misses at " + agaMiss + "/" + pick.Count + " formerly flipped triangles");
    }

    [Test]
    public void BakedNavMesh_HasNoRooftopPolygons()
    {
        var tr = NavMesh.CalculateTriangulation(); Assert.Greater(tr.indices.Length, 0, "no NavMesh loaded from the additive scene");
        int roof = 0, total = tr.indices.Length / 3; var hits = new RaycastHit[16];
        for (int t = 0; t < total; t++)
        {
            Vector3 a = tr.vertices[tr.indices[3 * t]], b = tr.vertices[tr.indices[3 * t + 1]], c = tr.vertices[tr.indices[3 * t + 2]]; bool allAbove = true;
            foreach (var q in new[] { a, b, c })
            {
                int n = Physics.RaycastNonAlloc(new Vector3(q.x, 600f, q.z), Vector3.down, hits, 1200f, 1 << 3, QueryTriggerInteraction.Ignore); if (n == 0) { allAbove = false; break; }
                float top = float.MinValue; for (int h = 0; h < n; h++) top = Mathf.Max(top, hits[h].point.y);
                if (q.y < top + 1.0f) { allAbove = false; break; }   // a bridge deck (Ground layer) counts as ground, a roof does not: roofs have no Ground collider under their rays
            }
            if (allAbove) roof++;
        }
        Assert.AreEqual(0, roof, roof + " of " + total + " NavMesh polygons float > 1 m above every ground surface (rooftops)");
    }

    // ---- GroundFallRecovery on synthetic ground (script lives in Assembly-CSharp, so reflection)
    static Type RecType() { var t = Type.GetType("GroundFallRecovery, Assembly-CSharp"); Assert.IsNotNull(t, "GroundFallRecovery missing"); return t; }
    static void Step(Component c) { RecType().GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, null); }
    static void Set(Component c, string f, object v) { RecType().GetField(f).SetValue(c, v); }
    static int Count(Component c) { return (int)RecType().GetProperty("RecoveryCount").GetValue(c); }

    GameObject MakeGround() { var g = new GameObject("__ground") { layer = 3 }; var b = g.AddComponent<BoxCollider>(); b.size = new Vector3(2000, 1, 2000); g.transform.position = new Vector3(0, -0.5f, 0); Physics.SyncTransforms(); return g; }
    Component MakeRig(GameObject rig) { var c = rig.AddComponent(RecType()); Set(c, "rig", rig.transform); Set(c, "groundMask", (LayerMask)(1 << 3)); Set(c, "noSupportSeconds", 0f); Set(c, "writeDiagnosticsFile", false); return c; }

    [Test]
    public void GroundFallRecovery_RestoresAFallenRig_ToTheLastSafePosition()
    {
        var ground = MakeGround(); var rig = new GameObject("__rig");
        try
        {
            var c = MakeRig(rig); rig.transform.position = new Vector3(10, 0, 5); Step(c);                       // supported: records the safe position
            float restoredY = float.NaN;
            foreach (float y in new[] { -0.4f, -0.9f, -1.5f, -2.5f }) { rig.transform.position = new Vector3(10, y, 5); Physics.SyncTransforms(); Step(c); if (Count(c) > 0) { restoredY = rig.transform.position.y; break; } }   // continuous fall below the surface
            Assert.AreEqual(1, Count(c), "one recovery expected"); Assert.AreEqual(0.05f, restoredY, 0.01f); Assert.AreEqual(10f, rig.transform.position.x, 0.01f);
        }
        finally { UnityEngine.Object.DestroyImmediate(rig); UnityEngine.Object.DestroyImmediate(ground); }
    }

    [Test]
    public void GroundFallRecovery_IgnoresScriptedTeleports()
    {
        var ground = MakeGround(); var rig = new GameObject("__rig");
        try
        {
            var c = MakeRig(rig); rig.transform.position = Vector3.zero; Step(c);
            rig.transform.position = new Vector3(500, -30, 500); Physics.SyncTransforms(); Step(c); Step(c);       // a teleport (even into the void) is not a fall
            Assert.AreEqual(0, Count(c)); Assert.AreEqual(500f, rig.transform.position.x, 0.001f); Assert.AreEqual(-30f, rig.transform.position.y, 0.001f);
        }
        finally { UnityEngine.Object.DestroyImmediate(rig); UnityEngine.Object.DestroyImmediate(ground); }
    }

    // ---- ExplorationBoundary: the logged onset of sessions 20261106T140500Z / T142958Z, replayed against the REAL Exploration Area volumes
    [Test]
    public void ExplorationBoundary_ContainmentShift_AtLoggedOnsetPositions_IsASmallHorizontalCorrection()
    {
        var ebType = Type.GetType("ExplorationBoundary, Assembly-CSharp"); Assert.IsNotNull(ebType);
        var eb = (Component)_scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(ebType, true)).FirstOrDefault(); Assert.IsNotNull(eb, "ExplorationBoundary not in scene");
        Physics.SyncTransforms();
        var inside = ebType.GetMethod("IsInsideArea"); var rec = ebType.GetMethod("TryRecoverShift", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var head in new[] { new Vector3(1966.93f, 205.60f, 866.09f), new Vector3(1966.80f, 205.60f, 866.09f), new Vector3(1971.77f, 203.77f, 843.01f) })
        {
            Assert.IsFalse((bool)inside.Invoke(eb, new object[] { head }), "test position expected to be just outside the area: " + head);
            var args = new object[] { head, Vector3.zero }; Assert.IsTrue((bool)rec.Invoke(eb, args)); var shift = (Vector3)args[1];
            Assert.Less(shift.magnitude, 2f, "containment shift " + shift + " at head " + head + " must be a small correction, not ~600+ m (z - height bug)");
            Assert.IsTrue((bool)inside.Invoke(eb, new object[] { head + shift }), "head must be inside the area after ONE correction (no runaway loop)");
        }
    }
}
