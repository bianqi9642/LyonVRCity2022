// Part3TargetsRealSceneTests.cs  (EditMode, Editor-only assembly)
// Real-scene audit of the Part 3 landmarks and of the target assignment, using the objects ExperimentManager is actually wired to in Assets/Scenes/LyonVRVity35.unity.
//
// WHAT IT ESTABLISHES (offline, no headset, no XR):
//   * each of the four zones GE / GU / OE / OU holds exactly two distinct landmark objects; every landmark has a TargetSelectable on its own object with a unique id and an enabled non-trigger
//     Collider; no object or id is used in two zones;
//   * the REAL resolver (ExperimentManager.TryResolvePart3Assignment) over all 12 Table 5 groups x many participants: Destination 1 and 2 are different zones, Target 1 lies in the
//     Destination 1 zone, Target 2 in the Destination 2 zone, the two ids differ, both are drawn from two-element candidate lists;
//   * a printed table of the zones and ids, and a printed (non-asserting) description of the controller-ray delivery chain wired in the scene (EventSystem modules, raycasters, ray interactor masks,
//     the layer of the landmarks), for the human audit.
// WHAT IT DOES NOT ESTABLISH: that a Quest controller ray really reaches these colliders (that needs the headset), or any timing / panel behaviour (see Part3TargetFlowTests, PlayMode).
// The scene is opened ADDITIVELY and closed without saving; the test is Ignored when the scene file is absent or already open in the Editor. Category "RealScene":
//   -runTests -testPlatform EditMode -assemblyNames GazeRecorder.EditModeTests

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

[Category("RealScene")]
public class Part3TargetsRealSceneTests
{
    const string ScenePath = "Assets/Scenes/LyonVRVity35.unity";
    static readonly string[] Zones = { "GE", "GU", "OE", "OU" };
    static readonly string[] Table5 = { "GE,GU,OE", "GE,OE,GU", "GE,OU,GE", "GU,GE,OU", "GU,OE,GU", "GU,OU,GE", "OE,GE,OU", "OE,GU,OE", "OE,OU,GE", "OU,GE,OU", "OU,GU,OE", "OU,OE,GU" };

    Scene _scene; bool _opened; Component _em; Type _emType;
    readonly Dictionary<string, GameObject[]> _landmarks = new Dictionary<string, GameObject[]>();

    static Type TypeOf(string n) { var t = Type.GetType(n + ", Assembly-CSharp"); Assert.IsNotNull(t, n); return t; }

    [OneTimeSetUp]
    public void Open()
    {
        if (!File.Exists(ScenePath)) Assert.Ignore("scene file not present: " + ScenePath);
        for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).path == ScenePath) Assert.Ignore("the experiment scene is open in the Editor; close it (or run in batch mode) so this test does not touch it");
        bool ignore = LogAssert.ignoreFailingMessages; LogAssert.ignoreFailingMessages = true;   // opening a large scene can log unrelated import / missing-reference errors
        try { _scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive); _opened = true; } finally { LogAssert.ignoreFailingMessages = ignore; }
        _emType = TypeOf("ExperimentManager");
        _em = (Component)_scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(_emType, true)).FirstOrDefault();
        Assert.IsNotNull(_em, "ExperimentManager not found in the scene");
        var so = new SerializedObject(_em);
        foreach (string z in Zones)
        {
            var a = so.FindProperty("part3Zone" + z + ".buildingA").objectReferenceValue as GameObject; var b = so.FindProperty("part3Zone" + z + ".buildingB").objectReferenceValue as GameObject;
            _landmarks[z] = new[] { a, b };
        }
    }

    [OneTimeTearDown]
    public void Close() { if (_opened) { LogAssert.ignoreFailingMessages = true; try { EditorSceneManager.CloseScene(_scene, true); } finally { LogAssert.ignoreFailingMessages = false; } } }

    [Test]
    public void EveryZoneHasExactlyTwoDistinctValidLandmarks_WithUniqueIds()
    {
        var tsType = TypeOf("TargetSelectable");
        var ids = new Dictionary<string, string>(); var objects = new HashSet<GameObject>();
        var table = new System.Text.StringBuilder("PART3 LANDMARK TABLE (zone | landmark 1 id | landmark 2 id | valid collider | TargetSelectable | layer)\n");
        foreach (string z in Zones)
        {
            var pair = _landmarks[z];
            Assert.AreEqual(2, pair.Count(g => g != null), "zone " + z + " must have exactly two landmarks assigned"); Assert.AreNotSame(pair[0], pair[1], "zone " + z + ": two distinct objects");
            var cells = new List<string>();
            foreach (var g in pair)
            {
                Assert.IsTrue(objects.Add(g), g.name + " is assigned to two zones");
                var ts = g.GetComponent(tsType); Assert.IsNotNull(ts, g.name + ": TargetSelectable on the landmark object itself");
                string id = (string)tsType.GetProperty("ResolvedTargetId").GetValue(ts);
                Assert.IsFalse(string.IsNullOrWhiteSpace(id)); Assert.IsFalse(ids.ContainsKey(id), "id " + id + " used twice (" + (ids.ContainsKey(id) ? ids[id] : "") + " and " + z + ")"); ids[id] = z;
                var col = g.GetComponents<Collider>().FirstOrDefault(c => c.enabled && !c.isTrigger); Assert.IsNotNull(col, g.name + ": enabled non-trigger Collider on the same object");
                cells.Add(id);
                Assert.AreEqual(g.name, id, "the project convention: TargetSelectable id == GameObject name");
            }
            table.AppendLine($"  {z} | {cells[0]} | {cells[1]} | yes | yes | {LayerMask.LayerToName(pair[0].layer)}({pair[0].layer})");
        }
        Debug.Log(table.ToString());
        Assert.AreEqual(8, objects.Count);
    }

    [Test]
    public void TheRealResolver_ForAllTwelveTable5Groups_GivesTwoIndependentTargetsInTheirOwnZones_WithDistinctIds()
    {
        var mgr = TypeOf("ExperimentRngManager"); var cfgType = TypeOf("RngSeedConfiguration");
        mgr.GetMethod("ResetForTests").Invoke(null, null);
        try
        {
            object cfg = Activator.CreateInstance(cfgType, 0x0123456789ABCDEFUL, "REALSCENE");
            mgr.GetField("ConfigurationOverride").SetValue(null, Expression.Lambda(typeof(Func<>).MakeGenericType(cfgType), Expression.Constant(cfg, cfgType)).Compile());
            var resolve = _emType.GetMethod("TryResolvePart3Assignment", BindingFlags.Instance | BindingFlags.NonPublic);
            var tryZones = _emType.GetMethod("TryGetPart3GroupZones");
            var ids = new Dictionary<string, HashSet<string>>(); foreach (string z in Zones) ids[z] = new HashSet<string>(_landmarks[z].Select(g => g.name));
            var combos = new HashSet<string>();
            for (int g = 0; g < 12; g++)
            {
                var zz = new object[] { g, "", "", "" }; Assert.IsTrue((bool)tryZones.Invoke(null, zz));
                Assert.AreEqual(Table5[g], (string)zz[1] + "," + (string)zz[2] + "," + (string)zz[3], "group " + g + " = Table 5");
                Assert.AreNotEqual((string)zz[2], (string)zz[3], "group " + g + ": Destination 1 zone != Destination 2 zone");
                var pairsSeen = new HashSet<string>();
                for (int p = 1; p <= 150; p++)
                {
                    var args = new object[] { g, "P" + p.ToString("D3"), null, null };
                    Assert.IsTrue((bool)resolve.Invoke(_em, args), "group " + g + " participant " + p + ": " + args[3]);
                    var info = args[2]; var d1 = info.GetType().GetField("Destination1").GetValue(info); var d2 = info.GetType().GetField("Destination2").GetValue(info);
                    string z1 = (string)d1.GetType().GetField("RegionId").GetValue(d1), z2 = (string)d2.GetType().GetField("RegionId").GetValue(d2);
                    string t1 = (string)d1.GetType().GetField("SelectedId").GetValue(d1), t2 = (string)d2.GetType().GetField("SelectedId").GetValue(d2);
                    Assert.AreEqual((string)zz[2], z1); Assert.AreEqual((string)zz[3], z2); Assert.AreNotEqual(z1, z2);
                    Assert.IsTrue(ids[z1].Contains(t1), t1 + " is a " + z1 + " landmark"); Assert.IsTrue(ids[z2].Contains(t2), t2 + " is a " + z2 + " landmark"); Assert.AreNotEqual(t1, t2);
                    Assert.AreEqual(2, ((string[])d1.GetType().GetField("CandidateIds").GetValue(d1)).Length); Assert.AreEqual(2, ((string[])d2.GetType().GetField("CandidateIds").GetValue(d2)).Length);
                    pairsSeen.Add(t1 + "|" + t2);
                    combos.Add($"g{g:D2} {z1}:{t1} {z2}:{t2}");
                }
                Assert.AreEqual(4, pairsSeen.Count, "group " + g + ": all four independent (Target 1, Target 2) pairs occur across participants");
            }
            Debug.Log("PART3 real-scene resolution: 12 groups x 150 participants, " + combos.Count + " distinct (group, Target 1, Target 2) combinations, 0 duplicates, 0 same-zone, 0 outside-zone");
        }
        finally { mgr.GetMethod("ResetForTests").Invoke(null, null); }
    }

    // Report-only: what the scene wires between a controller ray and TargetSelectable. Nothing is asserted: whether it works on a Quest can only be decided on the device.
    [Test]
    public void Report_ControllerRayDeliveryChainWiredInTheScene()
    {
        var sb = new System.Text.StringBuilder("PART3 RAY-CHAIN REPORT (static scene wiring, not a device test)\n");
        foreach (var es in _scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<EventSystem>(true)))
            sb.AppendLine($"  EventSystem '{es.name}' activeInHierarchy={es.gameObject.activeInHierarchy} modules=" + string.Join(",", es.GetComponents<BaseInputModule>().Select(m => m.GetType().Name + (m.enabled ? "(enabled)" : "(disabled)"))));
        foreach (var r in _scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<PhysicsRaycaster>(true)))
            sb.AppendLine($"  PhysicsRaycaster on '{r.name}' enabled={r.enabled} eventMask={r.eventMask.value}");
        foreach (var mb in _scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true)).Where(m => m != null && m.GetType().Name.Contains("RayInteractor") && m.gameObject.activeInHierarchy))
        {
            var so = new SerializedObject(mb); var rm = so.FindProperty("m_RaycastMask"); var ui = so.FindProperty("m_EnableUIInteraction");
            sb.AppendLine($"  {mb.GetType().Name} '{mb.transform.parent?.name}/{mb.name}' enabled={mb.enabled} raycastMask={(rm != null ? rm.intValue.ToString() : "?")} enableUIInteraction={(ui != null ? ui.boolValue.ToString() : "?")}");
        }
        foreach (string z in Zones) sb.AppendLine($"  landmark layer {z}: " + string.Join(",", _landmarks[z].Select(g => g.name + "=" + LayerMask.LayerToName(g.layer) + "(" + g.layer + ")")));
        Debug.Log(sb.ToString());
        Assert.Pass(sb.ToString());
    }
}
