// MpHarness.cs  (EditMode, Editor-only; ISOLATED REVIEW CLONE ONLY)
// Shared helpers of the multipoint-support review tests: result recording, rig/component construction through reflection, geometry builders.
// The same test sources run unchanged against two source trees: the baseline fixture (GroundFallRecovery of HEAD 05714d65, single ray) and the patched clone.
// Environment: REVIEW_RESULTS_DIR (output folder), REVIEW_IMPL ("baseline" or "patched", default: detected from the presence of the field multiPointSupport).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;

public static class MpHarness
{
    public static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    public static string ResultsDir { get { return Environment.GetEnvironmentVariable("REVIEW_RESULTS_DIR") ?? "/Users/qiaoy/Desktop/project/environment/_review_clones/results_multipoint"; } }
    public static Type RecType { get { var t = Type.GetType("GroundFallRecovery, Assembly-CSharp"); Assert.IsNotNull(t, "GroundFallRecovery missing"); return t; } }
    public static bool IsPatched { get { return RecType.GetField("multiPointSupport") != null; } }
    public static string Impl { get { var e = Environment.GetEnvironmentVariable("REVIEW_IMPL"); return string.IsNullOrEmpty(e) ? (IsPatched ? "patched" : "baseline") : e; } }

    // implementation variants evaluated on the patched tree; the baseline tree has exactly one variant
    public static string[] Modes { get { return IsPatched ? new[] { "single", "anyhit", "bridge" } : new[] { "baseline" }; } }
    public static string ModeLabel(string mode) { return mode == "baseline" ? "baseline" : "patched_" + mode; }
    public static bool IsFinal(string mode) { return mode == "bridge"; }       // the implementation proposed for adoption

    // ---------------------------------------------------------------- recording
    public class Row { public string id, scenario, geometry, gap, normal, offset, sample, mode, result, expected, notes; public double ms; }
    public static readonly List<Row> Rows = new List<Row>();
    public static void Rec(string id, string scenario, string geometry, string gap, string normal, string offset, string sample, string mode, string result, string expected, string notes = "", double ms = double.NaN)
    {
        Rows.Add(new Row { id = id, scenario = scenario, geometry = geometry, gap = gap, normal = normal, offset = offset, sample = sample, mode = mode, result = result, expected = expected, notes = notes, ms = ms });
    }
    static string Q(string s) { s = s ?? ""; return s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }
    public static void Flush(string fixtureName)
    {
        Directory.CreateDirectory(ResultsDir);
        var sb = new StringBuilder("test_id,scenario,geometry,gap_width_m,surface_normal_y,rig_root_to_head_offset_m,support_sample,implementation,result,expected_result,elapsed_ms,notes\n");
        foreach (var r in Rows)
            sb.Append(Q(r.id)).Append(',').Append(Q(r.scenario)).Append(',').Append(Q(r.geometry)).Append(',').Append(Q(r.gap)).Append(',').Append(Q(r.normal)).Append(',').Append(Q(r.offset)).Append(',').Append(Q(r.sample)).Append(',')
              .Append(ModeLabel(r.mode)).Append(',').Append(Q(r.result)).Append(',').Append(Q(r.expected)).Append(',').Append(double.IsNaN(r.ms) ? "" : r.ms.ToString("F4", CI)).Append(',').Append(Q(r.notes)).Append('\n');
        File.WriteAllText(Path.Combine(ResultsDir, "rows_" + Impl + "_" + fixtureName + ".csv"), sb.ToString());
        Rows.Clear();
    }

    // ---------------------------------------------------------------- component access
    public static void Set(Component c, string f, object v) { var fi = RecType.GetField(f); if (fi != null) fi.SetValue(c, v); }
    public static T Get<T>(Component c, string f) { return (T)RecType.GetField(f).GetValue(c); }
    public static T GetPriv<T>(Component c, string f) { return (T)RecType.GetField(f, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(c); }
    public static int Count(Component c) { return (int)RecType.GetProperty("RecoveryCount").GetValue(c); }
    public static string Reason(Component c) { return (string)RecType.GetProperty("LastRecoveryReason").GetValue(c); }
    public static void Tick(Component c, float t) { RecType.GetMethod("Tick").Invoke(c, new object[] { t }); }
    public delegate bool SupportFn(Vector3 p, out float y);
    public static SupportFn SupportOf(Component c) { return (SupportFn)Delegate.CreateDelegate(typeof(SupportFn), c, RecType.GetMethod("HasSupport", BindingFlags.Instance | BindingFlags.NonPublic)); }
    public static string LastHitInfo(Component c) { return GetPriv<string>(c, "lastHitInfo"); }
    public static void Apply(Component c, string mode, float spread = 0.15f)
    {
        if (!IsPatched) return;
        Set(c, "multiPointSupport", mode != "single");
        Set(c, "requireBridgeForSideSamples", mode == "bridge");
        Set(c, "supportProbeSpread", spread);
    }

    // ---------------------------------------------------------------- rig construction
    public class RigHandle { public GameObject go; public CharacterController cc; public Component c; public float[] clock = new float[1]; public SupportFn Support; }
    public static RigHandle MakeRig(List<GameObject> keep, Vector3 root, Vector3 capsuleOffsetXZ, float height, string mode, float spread = 0.15f, bool tickable = true)
    {
        var h = new RigHandle(); h.go = new GameObject("__rig"); keep.Add(h.go); h.go.transform.position = root;
        h.cc = h.go.AddComponent<CharacterController>(); h.cc.height = height; h.cc.radius = 0.1f; h.cc.skinWidth = 0.08f; h.cc.slopeLimit = 45f; h.cc.stepOffset = 0.5f;
        h.cc.center = new Vector3(capsuleOffsetXZ.x, height / 2f + 0.08f, capsuleOffsetXZ.z);
        h.c = h.go.AddComponent(RecType);
        Set(h.c, "rig", h.go.transform); Set(h.c, "groundMask", (LayerMask)(1 << 3)); Set(h.c, "writeDiagnosticsFile", false);
        // Awake() does not run in Edit Mode: set the private references it would resolve
        RecType.GetField("cc", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(h.c, h.cc);
        var clk = h.clock; Set(h.c, "Clock", (Func<float>)(() => clk[0]));
        Apply(h.c, mode, spread);
        h.Support = SupportOf(h.c);
        return h;
    }
    // the footprint is derived from the rig's TRANSFORM (cc.transform.TransformPoint(cc.center)), so the rig must be moved to the position before the support test
    public static bool SupportAt(RigHandle h, Vector3 p, out float y) { h.go.transform.position = p; return h.Support(p, out y); }
    public static void Step(RigHandle h, float t) { h.clock[0] = t; Tick(h.c, t); }

    // ---------------------------------------------------------------- geometry
    public static GameObject Box(List<GameObject> keep, string name, Vector3 centre, Vector3 size, Quaternion rot)
    {
        var g = new GameObject(name) { layer = 3 }; var b = g.AddComponent<BoxCollider>(); b.size = size; g.transform.position = centre; g.transform.rotation = rot; keep.Add(g); return g;
    }
    public static GameObject Box(List<GameObject> keep, string name, Vector3 centre, Vector3 size) { return Box(keep, name, centre, size, Quaternion.identity); }

    // two Ground tiles (top surface y = 0) separated by an open slit of width w, seam direction rotated by thetaDeg about Y; the slit centre line passes through the origin.
    // The tiles are huge boxes in seam coordinates (u across, v along).
    public static void SeamTiles(List<GameObject> keep, float w, float thetaDeg, float overlapOrStep = 0f)
    {
        var rot = Quaternion.Euler(0f, thetaDeg, 0f);
        float len = 400f;
        // seam along local x; "south" tile occupies local z <= -w/2, "north" tile z >= +w/2 (w < 0: overlapping tiles)
        float half = w / 2f;
        Vector3 cS = rot * new Vector3(0f, -0.5f, -half - len / 2f), cN = rot * new Vector3(0f, -0.5f + overlapOrStep, half + len / 2f);
        Box(keep, "tileSouth", cS, new Vector3(len, 1f, len), rot);
        Box(keep, "tileNorth", cN, new Vector3(len, 1f, len), rot);
        Physics.SyncTransforms();
    }

    // a one-sided triangle-mesh surface (like the TIN terrain) from quads; each quad carries the normal its FRONT face must point to (winding is fixed per triangle)
    public static GameObject MeshSurface(List<GameObject> keep, string name, List<Vector3[]> quads, List<Vector3> frontNormals)
    {
        var verts = new List<Vector3>(); var tris = new List<int>();
        for (int i = 0; i < quads.Count; i++)
        {
            var q = quads[i]; int b0 = verts.Count; verts.AddRange(q);
            AddTri(verts, tris, b0, b0 + 1, b0 + 2, frontNormals[i]); AddTri(verts, tris, b0, b0 + 2, b0 + 3, frontNormals[i]);
        }
        var m = new Mesh { name = name }; m.SetVertices(verts); m.SetTriangles(tris, 0); m.RecalculateNormals(); m.RecalculateBounds();
        var g = new GameObject(name) { layer = 3 }; var mc = g.AddComponent<MeshCollider>(); mc.sharedMesh = m; keep.Add(g); return g;
    }
    static void AddTri(List<Vector3> v, List<int> t, int a, int b, int c, Vector3 desired)
    {
        // Unity front face = clockwise winding seen from the front: the front normal is Cross(b - a, c - a)
        Vector3 n = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
        if (Vector3.Dot(n, desired) >= 0f) { t.Add(a); t.Add(b); t.Add(c); } else { t.Add(a); t.Add(c); t.Add(b); }
    }

    public static string F(float v) { return v.ToString("F4", CI); }
    public static string FormatSupport(bool s, float y) { return s ? "supported" : "unsupported"; }

    // failures of the "final" implementation are collected and asserted together at the end of a test so every row is still recorded
    public static void Check(List<string> failures, string id, string mode, string result, string expected)
    {
        if (IsFinal(mode) && result != expected) failures.Add(id + ": " + result + " != " + expected);
    }
}
