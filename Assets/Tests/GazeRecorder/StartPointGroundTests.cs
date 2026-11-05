// StartPointGroundTests.cs
// Regression tests for the vertical placement of every experiment start point (ground-alignment audit).
//
// WHY: the Part 2 / Part 3 start anchors StartPointO, StartPointOE, StartPointG, StartPointGE, StartPointGU and StartPointOU carried serialized heights that no longer matched the terrain collider
// (-0.263 / -0.263 / -0.123 / -0.123 / -0.059 / +0.042 m). ExperimentManager.TeleportPlayerTo copies the anchor position to the XR Origin base unchanged, and AutoGroundAlign (ray from base + 0.1 m,
// one-sided mesh) cannot lift a base that starts more than 0.1 m below the surface - so participants started under the terrain. The six Y values were corrected in LyonVRVity35.unity.
//
// WHAT THESE TESTS ESTABLISH (and what they do not)
//   * Scene-file tests (YAML, no scene load): the transforms ExperimentManager is wired to as start points (Part 1, Part 2 start + transition, Part 3 GE/GU/OE/OU) are exactly the nine in the frozen table;
//     their world X / Z / yaw are unchanged; their world Y is within 0.02 m of the terrain collider height measured at that X / Z with the real colliders (FROZEN measurement, see
//     docs/ground_alignment_start_points.md). Moving a start point, adding an un-audited one, or re-introducing a serialized offset fails here.
//     They skip (Ignore) when the scene file is not part of the project being tested.
//   * Synthetic PlayMode tests with the REAL ExperimentManager.TeleportPlayerTo and the REAL AutoGroundAlign on a flat Ground collider: the rig base lands exactly on the anchor (no vertical offset), an
//     anchor on the surface is left alone by AutoGroundAlign, and an anchor more than 0.1 m under the surface is not recovered (the failure mode the correction removes).
//   * The REAL terrain geometry is exercised by the EditMode test StartPointGroundRealSceneTests (Assets/Tests/EditMode), which loads the scene and raycasts the actual colliders. Neither is hardware validation.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public class StartPointGroundTests
{
    // fileID of the start transform in LyonVRVity35.unity -> (name, world X, world Z, terrain collider height measured at that X/Z, number of ExperimentManager roles)
    // The terrain height is the closest Ground-layer MeshCollider surface hit by a ray cast from 30 m above (real colliders, probe run 2026-11-04; local slope <= 1 cm within 0.1 m of each point).
    static readonly (long id, string name, float x, float z, float groundY)[] Frozen =
    {
        (1571442372, "StartPoint",   2588.8800f, 2629.9570f, 167.2891f),   // Part 1 start
        (1664251513, "StartPointO",  2228.1800f,  810.9568f, 169.4431f),   // Part 2 start (group 0) / transition (group 1)
        (1773426603, "StartPointG",  2393.1800f,  807.3568f, 168.1532f),   // Part 2 transition (group 0) / start (group 1)
        (623708798,  "StartPointGE", 2393.1800f,  807.3568f, 168.1532f),   // Part 3 GE
        (1286414815, "StartPointGU", 2332.7800f,  402.4568f, 169.1996f),   // Part 3 GU
        (1870100303, "StartPointOE", 2228.1800f,  810.9568f, 169.4431f),   // Part 3 OE
        (394228928,  "StartPointOU", 2106.8800f,  418.7568f, 169.3685f),   // Part 3 OU
    };
    const float YTol = 0.02f, XZTol = 0.001f;

    // ---------------------------------------------------------------------------------------------------------------- minimal Unity-YAML reader
    sealed class Doc { public int cls; public long id; public string body; }
    static Dictionary<long, Doc> s_docs;

    static void Load()
    {
        if (s_docs != null) return;
        string path = Path.Combine(Application.dataPath, "Scenes", "LyonVRVity35.unity");
        if (!File.Exists(path)) Assert.Ignore("scene file not present in this project: " + path);
        string text = File.ReadAllText(path);
        var rx = new Regex(@"^--- !u!(\d+) &(-?\d+)( stripped)?[ \t]*\r?$", RegexOptions.Multiline);
        var ms = rx.Matches(text); var docs = new Dictionary<long, Doc>(ms.Count);
        for (int i = 0; i < ms.Count; i++)
        {
            int start = ms[i].Index + ms[i].Length, end = i + 1 < ms.Count ? ms[i + 1].Index : text.Length;
            long id = long.Parse(ms[i].Groups[2].Value);
            docs[id] = new Doc { cls = int.Parse(ms[i].Groups[1].Value), id = id, body = text.Substring(start, end - start) };
        }
        s_docs = docs;
    }

    static float[] Vec(string body, string name, int n)
    {
        var m = Regex.Match(body, @"^\s*" + name + @": \{(.*?)\}", RegexOptions.Multiline); Assert.IsTrue(m.Success, "missing " + name);
        var parts = m.Groups[1].Value.Split(',').Select(p => float.Parse(p.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.AreEqual(n, parts.Length); return parts;
    }

    /// <summary>World position / yaw of a scene Transform whose ancestors are translation-only (the start anchors' chain: EnvironmentConfig/Participant/*Phase). Fails loudly if an ancestor rotates or scales.</summary>
    static (Vector3 pos, float yaw) World(long id)
    {
        Vector3 p = Vector3.zero; long cur = id; int guard = 0; bool first = true; float yaw = 0;
        while (cur != 0 && guard++ < 50)
        {
            Assert.IsTrue(s_docs.TryGetValue(cur, out var d) && d.cls == 4, "transform " + cur + " not found in the scene file (or it is a stripped prefab proxy): the start anchors are plain scene objects");
            var lp = Vec(d.body, "m_LocalPosition", 3); var lr = Vec(d.body, "m_LocalRotation", 4); var ls = Vec(d.body, "m_LocalScale", 3);
            var q = new Quaternion(lr[0], lr[1], lr[2], lr[3]);
            if (first) { yaw = q.eulerAngles.y; first = false; Assert.AreEqual(0f, Quaternion.Angle(q, Quaternion.identity), 0.01f, "anchor " + id + " must keep its identity rotation"); }
            else
            {
                Assert.AreEqual(0f, Quaternion.Angle(q, Quaternion.identity), 0.01f, "ancestor " + cur + " of " + id + " rotates: extend World() before relying on it");
                Assert.IsTrue(Mathf.Abs(ls[0] - 1) < 1e-4f && Mathf.Abs(ls[1] - 1) < 1e-4f && Mathf.Abs(ls[2] - 1) < 1e-4f, "ancestor " + cur + " of " + id + " is scaled: extend World() before relying on it");
            }
            p += new Vector3(lp[0], lp[1], lp[2]);
            var f = Regex.Match(d.body, @"^\s*m_Father: \{fileID: (-?\d+)\}", RegexOptions.Multiline); cur = f.Success ? long.Parse(f.Groups[1].Value) : 0;
        }
        return (p, yaw);
    }

    /// <summary>Every transform ExperimentManager is wired to as a start / transition anchor (part1StartPosition, part2Groups[*].startPositions / teleportPositions, part3StartGE/GU/OE/OU).</summary>
    static SortedSet<long> WiredAnchors()
    {
        Load();
        var em = s_docs.Values.FirstOrDefault(d => d.cls == 114 && d.body.Contains("part3StartGE:") && d.body.Contains("part1StartPosition:"));
        Assert.IsNotNull(em, "ExperimentManager component not found in the scene file");
        var set = new SortedSet<long>();
        long R(string key) { var m = Regex.Match(em.body, @"^\s*" + key + @": \{fileID: (-?\d+)\}", RegexOptions.Multiline); Assert.IsTrue(m.Success, "no " + key); return long.Parse(m.Groups[1].Value); }
        set.Add(R("part1StartPosition")); set.Add(R("part3StartGE")); set.Add(R("part3StartGU")); set.Add(R("part3StartOE")); set.Add(R("part3StartOU"));
        int a = em.body.IndexOf("part2Groups:", StringComparison.Ordinal), b = em.body.IndexOf("part3StartGE:", StringComparison.Ordinal);
        Assert.Greater(a, 0); Assert.Greater(b, a);
        foreach (Match m in Regex.Matches(em.body.Substring(a, b - a), @"\{fileID: (-?\d+)\}")) set.Add(long.Parse(m.Groups[1].Value));
        return set;
    }

    // ---------------------------------------------------------------------------------------------------------------- scene-file tests

    [Test]
    public void Scene_TheWiredStartAndTransitionAnchors_AreExactlyTheAuditedSet()
    {
        var wired = WiredAnchors(); var frozen = new SortedSet<long>(Frozen.Select(f => f.id));
        CollectionAssert.AreEqual(frozen, wired, "ExperimentManager is wired to a start/transition anchor that is not in the audited table (or one was unwired): audit its ground height, then update StartPointGroundTests.Frozen " +
            "and StartPointGroundRealSceneTests");
    }

    [Test]
    public void Scene_EveryStartAnchor_KeepsItsFrozenHorizontalPosition_AndIdentityRotation()
    {
        Load();
        foreach (var f in Frozen)
        {
            var (w, yaw) = World(f.id);
            Assert.AreEqual(f.x, w.x, XZTol, f.name + " world X changed (the audit corrected only the vertical position)");
            Assert.AreEqual(f.z, w.z, XZTol, f.name + " world Z changed");
            Assert.AreEqual(0f, yaw, 0.01f, f.name + " yaw changed");
        }
    }

    [Test]
    public void Scene_EveryStartAnchor_StandsOnTheTerrainSurface_WithinTwoCentimetres()
    {
        Load();
        var bad = new List<string>();
        foreach (var f in Frozen)
        {
            float y = World(f.id).pos.y, dy = y - f.groundY;   // negative = anchor (= XR Origin base after TeleportPlayerTo) below the ground
            if (Mathf.Abs(dy) > YTol) bad.Add(f.name + " world Y " + y.ToString("F4") + " vs terrain " + f.groundY.ToString("F4") + " = " + dy.ToString("+0.0000;-0.0000") + " m");
        }
        Assert.IsEmpty(bad, "start anchors off the terrain surface by more than " + YTol + " m (rig base = anchor, see ExperimentManager.TeleportPlayerTo): " + string.Join("; ", bad));
    }

    [Test]
    public void Scene_EveryStartAnchor_LetsAutoGroundAlignHitTheSurface_RayOriginAboveTheOneSidedMesh()
    {
        // AutoGroundAlign casts from (base + 0.1 m). It only hits the one-sided terrain mesh when that origin is above the surface, i.e. anchor.y > ground - 0.1 m.
        Load();
        foreach (var f in Frozen) Assert.Greater(World(f.id).pos.y + 0.1f, f.groundY, f.name + ": the AutoGroundAlign ray would start under the terrain surface and could never correct the height");
    }

    // ---------------------------------------------------------------------------------------------------------------- synthetic PlayMode tests (real TeleportPlayerTo + real AutoGroundAlign)
    const int Ground = 3;
    readonly List<UnityEngine.Object> _objs = new List<UnityEngine.Object>();
    [TearDown] public void TearDown() { foreach (var o in _objs) if (o != null) UnityEngine.Object.DestroyImmediate(o); _objs.Clear(); }

    GameObject Surface(float topY)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube); g.name = "gml_flat_ground"; g.layer = Ground;
        g.transform.position = new Vector3(0, topY - 0.5f, 0); g.transform.localScale = new Vector3(400, 1, 400); _objs.Add(g); Physics.SyncTransforms(); return g;
    }

    /// <summary>A rig base with the real AutoGroundAlign (scene values) and a Camera-Offset-like child, plus a real ExperimentManager (kept inactive so Awake / OnEnable never run) pointing at it.</summary>
    (Transform rig, Transform cameraOffset, Component em) Setup(Vector3 startPos, bool withAutoGroundAlign)
    {
        var rig = new GameObject("XR Origin (XR Rig)"); rig.transform.position = startPos; _objs.Add(rig);
        var off = new GameObject("Camera Offset"); off.transform.SetParent(rig.transform, false); off.transform.localPosition = new Vector3(0, 1.36144f, 0);
        if (withAutoGroundAlign) { var ag = rig.AddComponent(T("AutoGroundAlign")); Set(ag, "groundLayer", (LayerMask)(1 << Ground)); Set(ag, "raycastDistance", 3f); Set(ag, "smoothSpeed", 10f); Set(ag, "heightOffset", 0f); }
        var emGo = new GameObject("ExperimentManagerForTest"); emGo.SetActive(false); _objs.Add(emGo);
        var em = emGo.AddComponent(T("ExperimentManager")); Set(em, "playerRig", rig.transform);
        return (rig.transform, off.transform, em);
    }

    Transform Anchor(Vector3 pos, float yaw = 0f) { var a = new GameObject("StartAnchor"); a.transform.position = pos; a.transform.rotation = Quaternion.Euler(0, yaw, 0); _objs.Add(a); return a.transform; }

    static IEnumerator Frames(int n) { float t0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - t0 < n * 0.025f) yield return null; }

    [Test]
    public void TeleportPlayerTo_PlacesTheRigBaseExactlyAtTheAnchor_NoVerticalOffset_AndOnlyTheBaseMoves()
    {
        var (rig, cam, em) = Setup(new Vector3(1, 50, 2), false);
        var a = Anchor(new Vector3(10.25f, 100.123f, -5.5f), 90f);
        Call(em, "TeleportPlayerTo", a);
        Assert.AreEqual(a.position.x, rig.position.x, 0f); Assert.AreEqual(a.position.y, rig.position.y, 0f, "the rig base takes the anchor's Y unchanged: the anchor height IS the ground-contact height");
        Assert.AreEqual(a.position.z, rig.position.z, 0f);
        Assert.AreEqual(0f, Quaternion.Angle(a.rotation, rig.rotation), 1e-3f);
        Assert.AreEqual(a.position.y + 1.36144f, cam.position.y, 1e-4f, "the camera offset keeps its local height above the base (the eye height is never derived from the HMD or added to the anchor)");
    }

    [UnityTest]
    public IEnumerator AnchorOnTheSurface_IsLeftExactlyWhereItWasPlaced_ByAutoGroundAlign()
    {
        Surface(100f);
        var (rig, _, em) = Setup(new Vector3(0, 100.5f, 0), true);
        Call(em, "TeleportPlayerTo", Anchor(new Vector3(3, 100.0f, 4)));
        Assert.AreEqual(100.0f, rig.position.y, 0f, "initial placement, before any frame ran");
        yield return Frames(40);
        Assert.AreEqual(100.0f, rig.position.y, 1e-5f, "AutoGroundAlign does not have to move a base that is already on the surface");
        Assert.AreEqual(3f, rig.position.x, 0f); Assert.AreEqual(4f, rig.position.z, 0f);
    }

    [UnityTest]
    public IEnumerator AnchorTwoCentimetresOff_IsStillWithinTolerance_AndAutoGroundAlignRemovesTheResidual()
    {
        Surface(100f);
        var (rig, _, em) = Setup(new Vector3(0, 100.5f, 0), true);
        Call(em, "TeleportPlayerTo", Anchor(new Vector3(0, 99.98f, 0)));   // -2 cm: the audit tolerance
        float t0 = Time.realtimeSinceStartup; while (Mathf.Abs(rig.position.y - 100f) > 0.0015f && Time.realtimeSinceStartup - t0 < 20f) yield return null;
        Assert.AreEqual(100f, rig.position.y, 3e-3f);   // 3 mm: the wait is real-time bound and a loaded machine can leave the lerp a hair short
    }

    [UnityTest]
    public IEnumerator AnchorMoreThanTenCentimetresUnderTheSurface_IsNeverRecovered_ThisIsTheDefectTheCorrectionRemoved()
    {
        // Characterisation of AutoGroundAlign (unchanged): its ray starts 0.1 m above the base, so a base 0.131 m / 0.263 m below the one-sided surface (old StartPointGE / StartPointO) stays under it.
        Surface(100f);
        var (low, _, emLow) = Setup(new Vector3(0, 100.5f, 0), true);
        Call(emLow, "TeleportPlayerTo", Anchor(new Vector3(0, 99.869f, 0)));
        yield return Frames(40);
        Assert.AreEqual(99.869f, low.position.y, 1e-5f, "13 cm under the surface: AutoGroundAlign finds no ground and the participant stays under the terrain");
    }
}
