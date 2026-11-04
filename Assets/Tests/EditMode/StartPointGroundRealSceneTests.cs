// StartPointGroundRealSceneTests.cs  (EditMode, Editor-only assembly)
// Real-scene regression test for the ground height of every experiment start point.
//
// WHY: StartPointGroundTests (PlayMode) pins the serialized anchors against a FROZEN terrain table. This test removes that indirection: it opens the real experiment scene, reads the start / transition
// anchors ExperimentManager is actually wired to, and raycasts the real Ground-layer terrain colliders under each of them with the exact AutoGroundAlign parameters. It fails when a start point
// is moved vertically, when the terrain under it changes, or when a new start point is wired without being on the ground.
//
// HOW: the scene is opened ADDITIVELY and closed again without saving. The saved scene keeps its terrain groups ("TIN_Textured", "Practice Area") inactive; this test sets them active IN MEMORY
// ONLY, because without terrain colliders there is nothing to measure against (activation state itself is not examined or changed). The test is Ignored when the scene is already open in the Editor
// (it would otherwise have to touch the user's open, possibly unsaved scene), when the scene file is absent, or when no terrain mesh is imported. The scripts live in Assembly-CSharp, which an
// assembly definition cannot reference, so ExperimentManager / AutoGroundAlign are reached by reflection and SerializedObject.
// Category "RealScene": run with  -runTests -testPlatform EditMode -assemblyNames GazeRecorder.EditModeTests. Offline geometry only - not headset validation.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

[Category("RealScene")]
public class StartPointGroundRealSceneTests
{
    const string ScenePath = "Assets/Scenes/LyonVRVity35.unity";
    const float YTol = 0.02f;
    static readonly Dictionary<string, (float x, float z)> FrozenXZ = new Dictionary<string, (float, float)>
    {
        { "StartPoint",   (2588.8800f, 2629.9570f) }, { "StartPointO",  (2228.1800f,  810.9568f) }, { "StartPointG",  (2393.1800f,  807.3568f) },
        { "StartPointGE", (2393.1800f,  807.3568f) }, { "StartPointGU", (2332.7800f,  402.4568f) }, { "StartPointOE", (2228.1800f,  810.9568f) }, { "StartPointOU", (2106.8800f,  418.7568f) },
    };

    Scene _scene; bool _opened;
    readonly List<(string role, Transform t)> _anchors = new List<(string, Transform)>();
    int _groundMask; float _rayDistance; float _rayUp = 0.1f;

    [OneTimeSetUp]
    public void Open()
    {
        if (!File.Exists(ScenePath)) Assert.Ignore("scene file not present: " + ScenePath);
        for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).path == ScenePath) Assert.Ignore("the experiment scene is open in the Editor; close it (or run in batch mode) so this test does not touch it");
        // Opening a 200 MB city scene can log import / missing-reference errors that are unrelated to this test (a project without every third-party package, for instance); the geometry assertions below are the
        // check that matters, so error logs raised WHILE OPENING do not fail the fixture.
        bool ignore = LogAssert.ignoreFailingMessages; LogAssert.ignoreFailingMessages = true;
        try { _scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive); _opened = true; } finally { LogAssert.ignoreFailingMessages = ignore; }
        foreach (var root in _scene.GetRootGameObjects()) foreach (var tr in root.GetComponentsInChildren<Transform>(true)) if (tr.name == "TIN_Textured" || tr.name == "Practice Area") tr.gameObject.SetActive(true);   // in memory only
        Physics.autoSyncTransforms = true; Physics.SyncTransforms();
        if (!_scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MeshCollider>(true)).Any(m => m.gameObject.layer == 3 && m.sharedMesh != null)) Assert.Ignore("no Ground-layer terrain mesh is imported in this project");

        var emType = Type.GetType("ExperimentManager, Assembly-CSharp"); Assert.IsNotNull(emType);
        var em = (Component)_scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(emType, true)).FirstOrDefault(); Assert.IsNotNull(em, "ExperimentManager not found in the scene");
        var so = new SerializedObject(em);
        Transform P(SerializedProperty p) => p.objectReferenceValue as Transform;
        void Add(string role, SerializedProperty p) { var t = P(p); Assert.IsNotNull(t, role + " is not assigned"); _anchors.Add((role, t)); }
        Add("part1StartPosition", so.FindProperty("part1StartPosition")); foreach (var n in new[] { "part3StartGE", "part3StartGU", "part3StartOE", "part3StartOU" }) Add(n, so.FindProperty(n));
        var groups = so.FindProperty("part2Groups");
        for (int g = 0; g < groups.arraySize; g++) foreach (var arr in new[] { "startPositions", "teleportPositions" })
        { var a = groups.GetArrayElementAtIndex(g).FindPropertyRelative(arr); for (int i = 0; i < a.arraySize; i++) Add("part2Groups[" + g + "]." + arr + "[" + i + "]", a.GetArrayElementAtIndex(i)); }

        var agType = Type.GetType("AutoGroundAlign, Assembly-CSharp"); var ag = (Component)_scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(agType, true)).FirstOrDefault(); Assert.IsNotNull(ag, "AutoGroundAlign not found");
        var aso = new SerializedObject(ag); _groundMask = aso.FindProperty("groundLayer.m_Bits").intValue; _rayDistance = aso.FindProperty("raycastDistance").floatValue;
        Assert.AreEqual(1 << 3, _groundMask, "AutoGroundAlign is expected to use the Ground layer only");
    }

    [OneTimeTearDown]
    public void Close() { if (_opened && _scene.IsValid()) EditorSceneManager.CloseScene(_scene, true); }   // removeScene: true, nothing is saved

    [Test]
    public void EveryWiredStartAnchor_StandsOnTheRealGroundCollider_WithinTwoCentimetres_AndTheAutoGroundAlignRayHitsIt()
    {
        Assert.AreEqual(9, _anchors.Count, "5 Part 1/3 starts + 4 Part 2 group entries are expected");
        var lines = new List<string>(); var bad = new List<string>();
        foreach (var (role, t) in _anchors)
        {
            Assert.IsTrue(FrozenXZ.TryGetValue(t.name, out var xz), role + " -> '" + t.name + "' is not in the audited set");
            Assert.AreEqual(xz.x, t.position.x, 0.001f, t.name + " X changed"); Assert.AreEqual(xz.z, t.position.z, 0.001f, t.name + " Z changed"); Assert.AreEqual(0f, Quaternion.Angle(t.rotation, Quaternion.identity), 0.01f, t.name + " rotation changed");
            var w = t.position;
            Assert.IsTrue(Physics.Raycast(new Vector3(w.x, w.y + 30f, w.z), Vector3.down, out var ground, 80f, _groundMask, QueryTriggerInteraction.Ignore), role + " (" + t.name + "): no Ground collider below the start point");
            float dy = w.y - ground.point.y;
            bool agaHit = Physics.Raycast(new Vector3(w.x, w.y + _rayUp, w.z), Vector3.down, out var agaRay, _rayDistance, _groundMask);
            float y = w.y; for (int k = 0; k < 400; k++) if (Physics.Raycast(new Vector3(w.x, y + _rayUp, w.z), Vector3.down, out var s, _rayDistance, _groundMask)) y = Mathf.Lerp(y, s.point.y, 0.2f);
            lines.Add(role + " " + t.name + " dY=" + dy.ToString("+0.0000;-0.0000") + " aga_hit=" + agaHit + " settle_move=" + (y - w.y).ToString("+0.0000;-0.0000") + " on " + ground.collider.name);
            if (Mathf.Abs(dy) > YTol) bad.Add(role + " " + t.name + " dY=" + dy.ToString("+0.0000;-0.0000"));
            if (!agaHit) bad.Add(role + " " + t.name + ": AutoGroundAlign ray misses (origin under the surface)");
            else if (Mathf.Abs(y - w.y) > YTol) bad.Add(role + " " + t.name + ": AutoGroundAlign would still move the base by " + (y - w.y).ToString("+0.0000;-0.0000") + " m");
        }
        TestContext.Out.WriteLine(string.Join("\n", lines));
        Assert.IsEmpty(bad, "start points off the ground: " + string.Join("; ", bad));
    }
}
