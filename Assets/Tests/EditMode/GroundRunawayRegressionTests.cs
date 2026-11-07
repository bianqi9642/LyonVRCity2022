// GroundRunawayRegressionTests.cs  (EditMode, Editor-only assembly GazeRecorder.EditModeTests) - SYNTHETIC scenes only, no experiment scene is opened.
// Regression tests for the participant "fall" seen in sessions 20261106T140500Z and 20261106T142958Z (docs/ground_runaway_investigation.md):
//   * ExplorationBoundary.TryRecoverShift used the head HEIGHT (q.y) where it needed Z (q.z), so a head 1 m outside the Exploration Area moved the rig by (z - height) ~ 640-980 m, every frame.
//   * GroundFallRecovery classified that per-frame ~1000 m move as a "teleport" every frame and could never recover.
// Offline geometry / logic only - NOT headset validation.
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class GroundRunawayRegressionTests
{
    // ---------------- ExplorationBoundary (synthetic 100 m x 100 m area centred at the origin, y irrelevant)
    GameObject _area; Component _eb;

    static Type EbType() { var t = Type.GetType("ExplorationBoundary, Assembly-CSharp"); Assert.IsNotNull(t); return t; }

    [SetUp]
    public void MakeArea()
    {
        _area = new GameObject("__area"); var box = _area.AddComponent<BoxCollider>(); box.size = new Vector3(100, 20, 100); box.isTrigger = true;
        _eb = _area.AddComponent(EbType());
        EbType().GetField("areaVolumes", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_eb, new Collider[] { box });
        EbType().GetField("ignoreYInContainmentTest", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_eb, true);
        EbType().GetField("containmentEpsilon", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_eb, 0.05f);
        EbType().GetField("containmentRecoverInset", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_eb, 0.1f);
        EbType().GetField("safetyCheckSkin", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_eb, 0.1f);
        var cap = EbType().GetField("maxContainmentShift", BindingFlags.Instance | BindingFlags.NonPublic); if (cap != null) cap.SetValue(_eb, 30f);   // absent in the pre-fix revision (mutation check)
        Physics.SyncTransforms();
    }

    [TearDown] public void DropArea() { if (_area != null) UnityEngine.Object.DestroyImmediate(_area); }

    Vector3 Shift(Vector3 q, out bool ok)
    {
        var args = new object[] { q, Vector3.zero }; ok = (bool)EbType().GetMethod("TryRecoverShift", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_eb, args); return (Vector3)args[1];
    }

    [TestCase(52f, 200f, 0f)]      // east of the area, head at 200 m height (the bug moved z by -200)
    [TestCase(52f, 0f, 0f)]        // same with height 0 (the bug was invisible here)
    [TestCase(10f, 205.6f, 52f)]   // north of the area
    [TestCase(-51f, 168.9f, -49f)] // west, near a corner
    public void ContainmentShift_IsASmallHorizontalCorrection_IndependentOfHeight(float x, float y, float z)
    {
        var q = new Vector3(x, y, z);
        var s = Shift(q, out bool ok);
        Assert.IsTrue(ok);
        Assert.AreEqual(0f, s.y, 1e-6f, "correction must be horizontal");
        Assert.Less(s.magnitude, 3f, "correction " + s + " for head " + q + " must be about the distance to the edge (+0.1 m inset)");
        var inside = EbType().GetMethod("IsInsideArea");
        Assert.IsTrue((bool)inside.Invoke(_eb, new object[] { q + s }), "head must be inside after ONE correction");
    }

    [Test]
    public void ContainmentShift_ExactValue_NorthOfArea()
    {
        var s = Shift(new Vector3(10f, 205.6f, 52f), out bool ok);   // 2 m beyond the north edge (z = 50): expected z shift = -(2 + 0.1)
        Assert.IsTrue(ok); Assert.AreEqual(0f, s.x, 1e-3f); Assert.AreEqual(-2.1f, s.z, 1e-3f);
    }

    [Test]
    public void ContainmentShift_BeyondTheCap_IsRefused_NotApplied()
    {
        UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("containment correction .* refused"));
        var s = Shift(new Vector3(0f, 200f, 500f), out bool ok);   // 450 m outside: a geometry error, never a legitimate correction
        Assert.IsFalse(ok); Assert.AreEqual(Vector3.zero, s);
    }

    // ---------------- GroundFallRecovery on synthetic ground
    static Type RecType() { var t = Type.GetType("GroundFallRecovery, Assembly-CSharp"); Assert.IsNotNull(t); return t; }
    static void Step(Component c) { RecType().GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, null); }
    static void Set(Component c, string f, object v) { var fi = RecType().GetField(f); if (fi != null) fi.SetValue(c, v); }   // tolerant: pre-fix revisions lack some fields
    static int Count(Component c) { return (int)RecType().GetProperty("RecoveryCount").GetValue(c); }
    static string Reason(Component c) { return (string)RecType().GetProperty("LastRecoveryReason").GetValue(c); }

    GameObject Ground(Vector3 centre) { var g = new GameObject("__ground") { layer = 3 }; var b = g.AddComponent<BoxCollider>(); b.size = new Vector3(400, 1, 400); g.transform.position = centre + Vector3.down * 0.5f; Physics.SyncTransforms(); return g; }
    Component Recovery(GameObject rig) { var c = rig.AddComponent(RecType()); Set(c, "rig", rig.transform); Set(c, "groundMask", (LayerMask)(1 << 3)); Set(c, "noSupportSeconds", 0f); Set(c, "voidDisplacementSeconds", 0f); Set(c, "writeDiagnosticsFile", false); return c; }

    [Test]
    public void Recovery_UndoesAPerFrameRunawayIntoEmptySpace()
    {
        var g = Ground(Vector3.zero); var rig = new GameObject("__rig");
        try
        {
            var c = Recovery(rig); rig.transform.position = new Vector3(10, 0, 5); Step(c);                 // supported
            // the logged runaway: +~980 m in Z every frame, nothing below
            for (int f = 1; f <= 4 && Count(c) == 0; f++) { rig.transform.position = new Vector3(10, 0, 5 + 980f * f); Physics.SyncTransforms(); Step(c); }
            Assert.AreEqual(1, Count(c), "the runaway must be undone"); Assert.AreEqual("void_displacement", Reason(c));
            Assert.AreEqual(10f, rig.transform.position.x, 0.01f); Assert.AreEqual(5f, rig.transform.position.z, 0.01f); Assert.AreEqual(0.05f, rig.transform.position.y, 0.01f);
        }
        finally { UnityEngine.Object.DestroyImmediate(rig); UnityEngine.Object.DestroyImmediate(g); }
    }

    [Test]
    public void Recovery_KeepsALegitimateTeleportOntoGround()
    {
        var g1 = Ground(Vector3.zero); var g2 = Ground(new Vector3(1000, 5, 0)); var rig = new GameObject("__rig");
        try
        {
            var c = Recovery(rig); rig.transform.position = new Vector3(0, 0, 0); Step(c);
            rig.transform.position = new Vector3(1000, 5, 0); Physics.SyncTransforms(); Step(c); Step(c);   // teleport 1 km away, onto other ground
            Assert.AreEqual(0, Count(c)); Assert.AreEqual(1000f, rig.transform.position.x, 0.001f);
        }
        finally { UnityEngine.Object.DestroyImmediate(rig); UnityEngine.Object.DestroyImmediate(g1); UnityEngine.Object.DestroyImmediate(g2); }
    }

    [Test]
    public void Recovery_DoesNotSaveAnUndergroundPosition_AsSafe()
    {
        var g = Ground(Vector3.zero); var rig = new GameObject("__rig");
        try
        {
            var c = Recovery(rig); rig.transform.position = new Vector3(0, 0, 0); Step(c);
            rig.transform.position = new Vector3(0, -0.4f, 0); Physics.SyncTransforms(); Step(c);          // sinking (0.4 m under the surface: still 'supported' but NOT safe)
            float restoredY = float.NaN;
            foreach (float y in new[] { -0.9f, -1.5f, -2.5f }) { rig.transform.position = new Vector3(0, y, 0); Physics.SyncTransforms(); Step(c); if (Count(c) > 0) { restoredY = rig.transform.position.y; break; } }
            Assert.AreEqual(1, Count(c)); Assert.AreEqual(0.05f, restoredY, 0.01f, "must restore to the surface, not to the sunken sample");
        }
        finally { UnityEngine.Object.DestroyImmediate(rig); UnityEngine.Object.DestroyImmediate(g); }
    }

    [Test]
    public void Recovery_WritesItsDiagnosticsCsv_ToPersistentDataPath_WithoutTouchingOtherFiles()
    {
        var g = Ground(Vector3.zero); var rig = new GameObject("__rig"); string path = null;
        try
        {
            var c = Recovery(rig); Set(c, "writeDiagnosticsFile", true);
            rig.transform.position = new Vector3(0, 0, 0); Step(c);
            rig.transform.position = new Vector3(0, 0, 980f); Physics.SyncTransforms(); Step(c);          // jump into the void -> jump_detected, then void_displacement
            Step(c);
            path = (string)RecType().GetProperty("DiagnosticsPath").GetValue(c);
            Assert.IsFalse(string.IsNullOrEmpty(path)); StringAssert.StartsWith(Application.persistentDataPath, path);
            var lines = System.IO.File.ReadAllLines(path);
            StringAssert.StartsWith("t_abs,event,rig_x", lines[0]);
            var all = string.Join("\n", lines); StringAssert.Contains("jump_detected", all); StringAssert.Contains("recovery", all); StringAssert.Contains("void_displacement", all);
            foreach (var l in lines) Assert.AreEqual(lines[0].Split(',').Length, l.Split(',').Length, "row width must match the header: " + l);
        }
        finally { UnityEngine.Object.DestroyImmediate(rig); UnityEngine.Object.DestroyImmediate(g); if (path != null && System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    [Test]
    public void Recovery_RepeatedRunaway_RestoresEachTime_NeverLosesTheParticipant()
    {
        var g = Ground(Vector3.zero); var rig = new GameObject("__rig");
        try
        {
            var c = Recovery(rig); rig.transform.position = new Vector3(3, 0, 3); Step(c);
            for (int round = 0; round < 3; round++)
                for (int f = 1; f <= 4; f++) { rig.transform.position = new Vector3(3, 0, 3 + 980f * f); Physics.SyncTransforms(); Step(c); }
            Assert.GreaterOrEqual(Count(c), 2, "a persisting fault is undone every time");
            Assert.Less(Mathf.Abs(rig.transform.position.z), 5000f);
        }
        finally { UnityEngine.Object.DestroyImmediate(rig); UnityEngine.Object.DestroyImmediate(g); }
    }
}
