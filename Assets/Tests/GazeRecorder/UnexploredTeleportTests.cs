// UnexploredTeleportTests.cs
// Tests for "no unexplored teleportation point is visible or usable during Part 2" (UnexploredTeleportAvailability + the GuardedTeleportationProvider backstop).
//   * TeleportStateLedgerTests            - pure: the "restore exactly the original" ledger (also runs under a plain mono/NUnit host).
//   * UnexploredTeleportComponentTests    - PlayMode, REAL production components (Assembly-CSharp, reached by reflection) on a SYNTHETIC hierarchy that mirrors the scene:
//                                           TeleportationArea/{GridTeleportPoint,OrganicTeleportPoint}/{Unexplored,Explored}/<anchor: TeleportationAnchor + Visuals(MeshRenderer, MeshCollider)>.
//     NOT covered here: the real city scene (see the EditMode real-scene audit), Quest Pro behaviour (controller ray, visuals on the device).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using static Reflect;

public class TeleportStateLedgerTests
{
    static readonly Type LedgerT = T("TeleportStateLedger");

    [Test]
    public void Capture_StoresTheOriginalOnce_AndNeverOverwritesItWithTheRestrictedValue()
    {
        object l = Activator.CreateInstance(LedgerT);
        Assert.IsTrue((bool)Call(l, "Capture", 1, true));
        Assert.IsFalse((bool)Call(l, "Capture", 1, false), "a second capture (the restricted value) must not replace the original");
        object[] a = { 1, null }; Assert.IsTrue((bool)LedgerT.GetMethod("TryGetOriginal").Invoke(l, a)); Assert.AreEqual(true, a[1]);
        Assert.AreEqual(1, Get<int>(l, "Count"));
    }

    [Test]
    public void AnObjectThatWasAlreadyDisabled_IsRestoredAsDisabled_NothingIsBlindlyEnabled()
    {
        object l = Activator.CreateInstance(LedgerT);
        Call(l, "Capture", 10, false); Call(l, "Capture", 11, true);
        var snap = (System.Collections.IEnumerable)Call(l, "Snapshot");
        var d = new Dictionary<int, bool>();
        foreach (object kv in snap) d[(int)kv.GetType().GetProperty("Key").GetValue(kv)] = (bool)kv.GetType().GetProperty("Value").GetValue(kv);
        Assert.IsFalse(d[10]); Assert.IsTrue(d[11]);
        Call(l, "Clear"); Assert.AreEqual(0, Get<int>(l, "Count"));
        Assert.IsTrue((bool)Call(l, "Capture", 10, true), "after a restore a new restriction captures afresh");
    }
}

// Needs UnityEngine: Unity Test Runner (PlayMode) only.
public class UnexploredTeleportComponentTests
{
    static readonly Type AvT = T("UnexploredTeleportAvailability");
    static readonly Type EmT = T("ExperimentManager");
    static readonly Type AnchorT = Type.GetType("UnityEngine.XR.Interaction.Toolkit.TeleportationAnchor, Unity.XR.Interaction.Toolkit");
    static readonly Type GuardT = T("GuardedTeleportationProvider");
    readonly List<GameObject> spawned = new List<GameObject>();

    sealed class Point { public GameObject root, visuals, anchorDest, ground; public Component anchor; public Renderer renderer; public Collider marker, groundCol; }
    readonly Dictionary<string, Point> pts = new Dictionary<string, Point>();
    Component em;

    GameObject Make(string name, Transform parent = null) { var g = new GameObject(name); if (parent != null) g.transform.SetParent(parent, false); spawned.Add(g); return g; }

    [SetUp]
    public void SetUp()
    {
        Assert.IsNotNull(AnchorT, "XRI TeleportationAnchor type not found");
        foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(AvT, true)) UnityEngine.Object.DestroyImmediate(o);   // a bootstrapped instance from the test scene
        BuildHierarchy();
    }

    [TearDown] public void TearDown() { foreach (var g in spawned) if (g != null) UnityEngine.Object.DestroyImmediate(g); spawned.Clear(); pts.Clear(); em = null; }

    void BuildHierarchy()
    {
        var area = Make("TeleportationArea");
        var grid = Make("GridTeleportPoint", area.transform); var org = Make("OrganicTeleportPoint", area.transform); var other = Make("PracticeTeleportPoint", area.transform);
        var gU = Make("Unexplored", grid.transform); var gE = Make("Explored", grid.transform); var oU = Make("Unexplored", org.transform); var oE = Make("Explored", org.transform);
        float x = 0f;
        foreach (var spec in new[] { ("gU1", gU), ("gU2", gU), ("gE1", gE), ("oU1", oU), ("oU2", oU), ("oE1", oE), ("p1", other) })
            pts[spec.Item1] = MakePoint(spec.Item1, spec.Item2.transform, x += 10f);
        em = Make("EM").AddComponent(EmT);
        EmT.GetMethod("SetParticipantControl", All).Invoke(em, new object[] { true });
    }

    Point MakePoint(string name, Transform parent, float x)
    {
        var p = new Point { root = Make(name, parent) };
        p.root.transform.position = new Vector3(x, 0f, 0f);
        p.visuals = Make("Visuals", p.root.transform);
        p.visuals.AddComponent<MeshFilter>(); p.renderer = p.visuals.AddComponent<MeshRenderer>();
        p.marker = p.visuals.AddComponent<BoxCollider>();                       // the interaction collider (Default layer)
        p.ground = Make("GroundContact", p.root.transform); p.ground.layer = 3; // a collider that also provides ground contact (Ground layer): must never be touched
        p.groundCol = p.ground.AddComponent<BoxCollider>();
        p.anchorDest = Make("Anchor", p.root.transform); p.anchorDest.transform.localPosition = new Vector3(0f, 0.1f, 0f);
        p.anchor = p.root.AddComponent(AnchorT);
        AnchorT.GetProperty("teleportAnchorTransform").SetValue(p.anchor, p.anchorDest.transform);
        return p;
    }

    Component NewAvailability()
    {
        var c = Make("UnexploredTeleportAvailability").AddComponent(AvT);
        return c;
    }

    void RaisePart(string type, string part)
        => Call(em, "RaiseEvt", type, part, "", "", null, false, null, Vector3.zero, Vector3.zero);

    static bool En(Component c) => ((Behaviour)c).enabled;
    bool IsUnexplored(string key) => key.StartsWith("gU") || key.StartsWith("oU");

    void AssertOriginal(string msg)
    {
        foreach (var kv in pts)
        {
            Assert.IsTrue(En(kv.Value.anchor), msg + " anchor " + kv.Key); Assert.IsTrue(kv.Value.renderer.enabled, msg + " renderer " + kv.Key);
            Assert.IsTrue(kv.Value.marker.enabled, msg + " marker " + kv.Key); Assert.IsTrue(kv.Value.groundCol.enabled, msg + " ground " + kv.Key);
        }
    }

    void AssertPart2State(string msg)
    {
        foreach (var kv in pts)
        {
            bool hidden = IsUnexplored(kv.Key);
            Assert.AreEqual(!hidden, En(kv.Value.anchor), msg + " anchor " + kv.Key);
            Assert.AreEqual(!hidden, kv.Value.renderer.enabled, msg + " renderer " + kv.Key);
            Assert.AreEqual(!hidden, kv.Value.marker.enabled, msg + " marker " + kv.Key);
            Assert.IsTrue(kv.Value.groundCol.enabled, msg + " the Ground-layer collider is never disabled: " + kv.Key);
            Assert.IsTrue(kv.Value.root.activeSelf && kv.Value.anchorDest.activeSelf, msg + " nothing is deactivated: " + kv.Key);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------- phases

    [Test]
    public void Part1_LeavesEveryPointUnchanged()
    {
        var av = NewAvailability();
        RaisePart("part_start", "Part1");
        AssertOriginal("Part 1");
        Assert.IsFalse(Get<bool>(av, "Restricted"));
    }

    [Test]
    public void Part2_HidesAndDisablesOnlyTheUnexploredPoints_OfBothAreas()
    {
        var av = NewAvailability();
        RaisePart("part_start", "Part2");
        AssertPart2State("Part 2");
        Assert.AreEqual(4, Get<int>(av, "AffectedAnchorCount"), "2 Grid + 2 Organic unexplored anchors");
        Assert.AreEqual(12, Get<int>(av, "ManagedComponentCount"), "anchor + renderer + marker collider per unexplored point; nothing else");
        // the explored points and the point outside the groups still work
        foreach (string k in new[] { "gE1", "oE1", "p1" }) { Assert.IsTrue(En(pts[k].anchor)); Assert.IsTrue(pts[k].renderer.enabled); Assert.IsTrue(pts[k].marker.enabled); }
    }

    [Test]
    public void Part3_RestoresTheOriginalStates_IncludingOnesThatWereAlreadyDisabled()
    {
        pts["gU2"].renderer.enabled = false;                    // originally hidden
        ((Behaviour)pts["oU1"].anchor).enabled = false;        // originally not interactable
        var av = NewAvailability();
        RaisePart("part_start", "Part2");
        RaisePart("part_start", "Part3");
        foreach (var kv in pts)
        {
            bool origRenderer = !(kv.Key == "gU2"), origAnchor = !(kv.Key == "oU1");
            Assert.AreEqual(origRenderer, kv.Value.renderer.enabled, "renderer " + kv.Key);
            Assert.AreEqual(origAnchor, En(kv.Value.anchor), "anchor " + kv.Key);
            Assert.IsTrue(kv.Value.marker.enabled); Assert.IsTrue(kv.Value.groundCol.enabled);
        }
        Assert.AreEqual(0, Get<int>(av, "ManagedComponentCount"));
    }

    [Test]
    public void RepeatedAndOutOfOrderTransitions_NeverLeaveStaleState()
    {
        var av = NewAvailability();
        string[] seq = { "Part1", "Part2", "Part3", "Part2", "Part2", "Part1", "Part2", "Part3" };
        foreach (string p in seq)
        {
            RaisePart("part_start", p);
            if (p == "Part2") AssertPart2State("after " + p); else AssertOriginal("after " + p);
        }
        RaisePart("part_start", "Part2"); RaisePart("part_end", "Part2"); AssertOriginal("part_end restores");
        RaisePart("part_end", "Part2"); AssertOriginal("a repeated part_end is harmless");
        Assert.AreEqual(0, Get<int>(av, "ManagedComponentCount"));
    }

    [Test]
    public void ExperimentStartedDirectlyInPart2_IsRestrictedAtOnce()
    {
        var running = EmT.GetNestedType("RunningPart", All);
        EmT.GetField("currentRunningPart", All).SetValue(em, Enum.Parse(running, "Part2"));
        NewAvailability();   // OnEnable reads ExperimentManager.RunningPartName: no part_start event is needed
        AssertPart2State("direct Part 2");
    }

    [Test]
    public void DisablingTheComponent_RestoresEverything_AndReEnablingInPart2RestrictsAgain_WithoutDuplicateSubscriptions()
    {
        var running = EmT.GetNestedType("RunningPart", All);
        var av = NewAvailability();
        RaisePart("part_start", "Part2"); AssertPart2State("1");
        ((Behaviour)av).enabled = false; AssertOriginal("disabled component never leaves anything switched off");
        EmT.GetField("currentRunningPart", All).SetValue(em, Enum.Parse(running, "Part2"));
        ((Behaviour)av).enabled = true; ((Behaviour)av).enabled = false; ((Behaviour)av).enabled = true; AssertPart2State("re-enabled in Part 2");
        RaisePart("part_start", "Part2");
        Assert.AreEqual(12, Get<int>(av, "ManagedComponentCount"), "repeated events and re-enabling do not stack entries");
        RaisePart("part_start", "Part3"); AssertOriginal("one restore is enough");
    }

    [Test]
    public void Restriction_DoesNotActivateTheHierarchy_AndKeepsAnInactiveAreaRootInactive()
    {
        pts["gU1"].root.transform.parent.parent.parent.gameObject.SetActive(false);   // the shared 'TeleportationArea' root is saved inactive in the scene
        var av = NewAvailability();
        RaisePart("part_start", "Part2");
        Assert.IsFalse(pts["gU1"].root.transform.parent.parent.parent.gameObject.activeSelf, "the component never activates the shared hierarchy");
        Assert.IsFalse(En(pts["gU1"].anchor));
        RaisePart("part_start", "Part3");
        Assert.IsTrue(En(pts["gU1"].anchor)); Assert.IsFalse(pts["gU1"].root.transform.parent.parent.parent.gameObject.activeSelf);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the guard backstop

    [Test]
    public void Guard_RejectsAnUnexploredDestinationInPart2_ButNotAnExploredOne_AndNotOutsidePart2()
    {
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
        try
        {
            var running = EmT.GetNestedType("RunningPart", All);
            var guardGo = Make("Guard"); guardGo.SetActive(false);
            var guard = guardGo.AddComponent(GuardT);
            Set(guard, "experimentManager", em);
            guardGo.SetActive(true);

            Type reqT = Type.GetType("UnityEngine.XR.Interaction.Toolkit.TeleportRequest, Unity.XR.Interaction.Toolkit");
            MethodInfo eval = GuardT.GetMethod("Evaluate", All);
            Func<string, string> reasonFor = key =>
            {
                object req = Activator.CreateInstance(reqT);
                reqT.GetField("destinationPosition").SetValue(req, pts[key].anchorDest.transform.position);
                object[] args = { req, null, null, null };
                eval.Invoke(guard, args);
                return (string)args[1];
            };

            EmT.GetField("currentRunningPart", All).SetValue(em, Enum.Parse(running, "Part2"));
            StringAssert.StartsWith("unexplored_anchor_Part2", reasonFor("gU1"));
            StringAssert.StartsWith("unexplored_anchor_Part2", reasonFor("oU2"), "also in the Organic area");
            StringAssert.DoesNotContain("unexplored_anchor", reasonFor("gE1") ?? "", "explored points are judged by the existing rules only");
            StringAssert.DoesNotContain("unexplored_anchor", reasonFor("oE1") ?? "");

            EmT.GetField("currentRunningPart", All).SetValue(em, Enum.Parse(running, "Part3"));
            StringAssert.DoesNotContain("unexplored_anchor", reasonFor("gU1") ?? "", "outside Part 2 the existing Part 3 rules apply unchanged");
            EmT.GetField("currentRunningPart", All).SetValue(em, Enum.Parse(running, "Part1"));
            StringAssert.DoesNotContain("unexplored_anchor", reasonFor("gU1") ?? "", "Part 1 is unchanged");
        }
        finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false; }
    }

    [Test]
    public void Availability_IsA_SingleInstance_AndIsIdentifiedByTheHierarchyNames()
    {
        var f = AvT.GetMethod("IsUnexplored");
        Func<string, bool> u = k => (bool)f.Invoke(null, new object[] { pts[k].root.transform, "Unexplored", new[] { "GridTeleportPoint", "OrganicTeleportPoint" } });
        Assert.IsTrue(u("gU1")); Assert.IsTrue(u("oU2")); Assert.IsFalse(u("gE1")); Assert.IsFalse(u("oE1")); Assert.IsFalse(u("p1"));
    }
}
