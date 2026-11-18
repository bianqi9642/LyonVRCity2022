// MpGeometryTests.cs  (EditMode, Editor-only; ISOLATED REVIEW CLONE ONLY)
// Unit-level and physics-integration tests of the ground-support DECISION (GroundFallRecovery.HasSupport) on deterministic synthetic geometry (BoxColliders / one-sided MeshColliders on layer 3).
// Every scenario is evaluated for every implementation variant of the tree under test; only the proposed implementation ("bridge") is asserted, all variants are recorded
// (rows_<impl>_geometry.csv). "Truth" for a footprint centre is the capsule model: the capsule (radius 0.1 m) is supported when ground lies within 0.1 m horizontally of the centre.
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using H = MpHarness;

[Category("MpGeometry")]
public class MpGeometryTests
{
    readonly List<GameObject> _keep = new List<GameObject>();
    void Reset() { foreach (var g in _keep) if (g != null) UnityEngine.Object.DestroyImmediate(g); _keep.Clear(); }
    [TearDown] public void Cleanup() { Reset(); }
    [OneTimeTearDown] public void FlushRows() { H.Flush("geometry"); }
    const float R = 0.1f;   // capsule radius

    // ---------------------------------------------------------------- G1 continuous flat ground
    [Test]
    public void G1_ContinuousFlatGround_IsSupported_AtAllOffsetsAndHeights()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
        {
            Reset();
            H.Box(_keep, "plate", new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200));
            Physics.SyncTransforms();
            var pts = new (string n, Vector3 p)[] { ("centre", Vector3.zero), ("5cm inside edge", new Vector3(99.95f, 0, 0)), ("1m inside edge", new Vector3(99f, 0, 0)), ("corner 5cm inside", new Vector3(99.95f, 0, 99.95f)) };
            foreach (float off in new[] { 0f, 0.33f, 1.08f })
                foreach (float height in new[] { 1.0f, 1.36f, 1.6f })
                    foreach (var pt in pts)
                    {
                        var rh = H.MakeRig(_keep, pt.p - new Vector3(off, 0, 0), new Vector3(off, 0, 0), height, mode);
                        bool s = rh.Support(rh.go.transform.position, out float y);
                        string id = "G1/" + pt.n + "/off" + off.ToString("F2") + "/h" + height.ToString("F2");
                        H.Rec(id, "flat continuous ground", "single plate / overlapping plates", "", "1.0", off.ToString("F2"), pt.n + "; capsule height " + height, mode, s ? "supported" : "unsupported", "supported", "groundY=" + y.ToString("F3"));
                        H.Check(fails, id, mode, s ? "supported" : "unsupported", "supported");
                        UnityEngine.Object.DestroyImmediate(rh.go); _keep.Remove(rh.go);
                    }
            // moving across the plate at walking speed (1.4 m/s, 0.11 s frames): no unsupported evaluation, no recovery, no height oscillation
            var m = H.MakeRig(_keep, new Vector3(-30, 0, 0), Vector3.zero, 1.36f, mode); int bad = 0; float t = 0; float yMin = 9, yMax = -9;
            for (int i = 0; i < 400; i++) { m.go.transform.position = new Vector3(-30 + 1.4f * 0.11f * i, 0, 0); Physics.SyncTransforms(); t += 0.11f; H.Step(m, t); if (!m.Support(m.go.transform.position, out float yy)) bad++; else { yMin = Mathf.Min(yMin, yy); yMax = Mathf.Max(yMax, yy); } }
            string r = "unsupported_frames=" + bad + ";recoveries=" + H.Count(m.c) + ";groundY_range=" + (yMax - yMin).ToString("F4");
            H.Rec("G1/moving 400 frames", "flat continuous ground, walking", "single plate", "", "1.0", "0", "Tick x400", mode, r, "unsupported_frames=0;recoveries=0;groundY_range=0.0000");
            H.Check(fails, "G1/moving", mode, r, "unsupported_frames=0;recoveries=0;groundY_range=0.0000");
        }
        Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    // ---------------------------------------------------------------- G2 narrow seams and gaps
    [Test]
    public void G2_SeamWidthAndOrientationSweep_RecordsCoverageLimits()
    {
        var fails = new List<string>();
        float[] widths = { -0.05f, 0f, 0.02f, 0.05f, 0.07f, 0.10f, 0.15f, 0.20f, 0.30f, 0.40f };
        float[] thetas = { 0f, 30f, 45f, 90f };
        foreach (var mode in H.Modes)
        {
            var spreads = (mode == "bridge" || mode == "anyhit") ? new[] { 0.05f, 0.10f, 0.15f } : new[] { 0.15f };
            foreach (float spread in spreads)
                foreach (float w in widths)
                    foreach (float th in thetas)
                    {
                        Reset(); H.SeamTiles(_keep, w, th);
                        var rh = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode, spread);
                        var rot = Quaternion.Euler(0, th, 0); int n = 0, unsup = 0, falseLoss = 0, falseSupport = 0; var failU = new List<string>();
                        foreach (float v in new[] { 0f, 3.3f })
                            for (int k = -50; k <= 50; k++)
                            {
                                float u = k * 0.01f; var pos = rot * new Vector3(v, 0, u); n++;
                                bool s = H.SupportAt(rh, pos, out float y);
                                bool truth = (w <= 0f) || Mathf.Abs(u) >= w / 2f || (w / 2f - Mathf.Abs(u)) <= R;   // capsule: ground within its radius of the centre
                                if (!s) { unsup++; if (failU.Count < 6) failU.Add(u.ToString("F2")); }
                                if (!s && truth) falseLoss++; if (s && !truth) falseSupport++;
                            }
                        string id = "G2/w" + w.ToString("F2") + "/theta" + th.ToString("F0") + "/spread" + spread.ToString("F2");
                        string res = "unsupported=" + unsup + "/" + n + ";false_loss=" + falseLoss + ";false_support=" + falseSupport;
                        string expect = null;
                        if (mode == "bridge" && Mathf.Approximately(spread, 0.15f) && w <= 0.10f) expect = "unsupported=0/" + n + ";false_loss=0;false_support=0";
                        H.Rec(id, "seam width/orientation sweep", "two tiles, gap along " + th + " deg", w.ToString("F2"), "1.0", "0", "101 lateral x 2 longitudinal positions", mode, res, expect ?? "recorded (no assertion: beyond the design range)", "first failing u (m): " + string.Join("|", failU));
                        if (expect != null) H.Check(fails, id, mode, res, expect);
                        if (mode == "bridge" && Mathf.Approximately(spread, 0.15f)) { if (falseSupport != 0 && w <= 0.20f) fails.Add(id + ": false support " + falseSupport); }   // wider gaps than the capsule diameter (0.2 m) are recorded, not asserted
                    }
        }
        Reset();
        Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    // ---------------------------------------------------------------- G3 surface normals, slopes, steps
    [Test]
    public void G3_SurfaceNormals_OnlyWalkableSurfacesAreSupport()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
            foreach (float n in new[] { 1.0f, 0.8f, 0.5f, 0.2f, 0.08f })
            {
                Reset(); float a = Mathf.Acos(n); var rot = Quaternion.Euler(0, 0, a * Mathf.Rad2Deg);
                Vector3 up = rot * Vector3.up;                         // surface normal (-sin a, cos a, 0): the surface rises with +x
                H.Box(_keep, "slope", -0.5f * up, new Vector3(100, 1, 100), rot); Physics.SyncTransforms();
                int sup = 0, tot = 0;
                foreach (float s in new[] { 1f, 1.5f, 2f, 3f, 4f })
                { var p = s * new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f); var rh = H.MakeRig(_keep, p, Vector3.zero, 1.36f, mode); tot++; if (H.SupportAt(rh, p, out _)) sup++; UnityEngine.Object.DestroyImmediate(rh.go); _keep.Remove(rh.go); }
                string expect = n >= 0.7072f ? tot + "/" + tot : "0/" + tot; string res = sup + "/" + tot; string id = "G3/normal" + n.ToString("F2");
                H.Rec(id, "surface normal", "tilted box top face", "", n.ToString("F2"), "0", tot + " points on the surface", mode, res, expect, "slopeLimit 45 deg = normal_y >= 0.7071");
                H.Check(fails, id, mode, res, expect);
            }
        // vertical wall (n = 0): rays are parallel to the face; only the cap (0.5+ m above the rig base) is hit
        foreach (var mode in H.Modes)
        {
            Reset(); H.Box(_keep, "wall", new Vector3(0.05f, -5f, 0), new Vector3(0.1f, 10f, 100f)); Physics.SyncTransforms(); int sup = 0, tot = 0;
            foreach (float x in new[] { 0.01f, 0.05f, 0.09f, 0.14f, 0.2f }) { var p = new Vector3(x, -1.5f, 0); var rh = H.MakeRig(_keep, p, Vector3.zero, 1.36f, mode); tot++; if (H.SupportAt(rh, p, out _)) sup++; UnityEngine.Object.DestroyImmediate(rh.go); _keep.Remove(rh.go); }
            H.Rec("G3/vertical wall", "surface normal", "vertical wall 4.7+ m", "", "0.00", "0", tot + " points beside the wall 1.5 m below its cap", mode, sup + "/" + tot, "0/" + tot);
            H.Check(fails, "G3/vertical wall", mode, sup + "/" + tot, "0/" + tot);
        }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    [Test]
    public void G3b_QuayFaceAndWallFloorCorner()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
        {
            // plateau x <= 0 (y = 0); quay face leaning outward (normal_y 0.087) from (0,0) down to (0.41,-4.7); lower floor y = -4.7 for x >= 0.41 (one-sided meshes like the TIN terrain)
            Reset();
            var quads = new List<Vector3[]>(); var nrm = new List<Vector3>();
            quads.Add(new[] { new Vector3(-60, 0, -60), new Vector3(0, 0, -60), new Vector3(0, 0, 60), new Vector3(-60, 0, 60) }); nrm.Add(Vector3.up);
            quads.Add(new[] { new Vector3(0, 0, -60), new Vector3(0.41f, -4.7f, -60), new Vector3(0.41f, -4.7f, 60), new Vector3(0, 0, 60) }); nrm.Add(new Vector3(0.996f, 0.087f, 0));
            quads.Add(new[] { new Vector3(0.41f, -4.7f, -60), new Vector3(60, -4.7f, -60), new Vector3(60, -4.7f, 60), new Vector3(0.41f, -4.7f, 60) }); nrm.Add(Vector3.up);
            H.MeshSurface(_keep, "quay", quads, nrm); Physics.SyncTransforms();
            int faceSup = 0, faceTot = 0, falseNew = 0;
            for (float x = 0.02f; x <= 0.40f; x += 0.02f)
                for (float y = -4.3f; y <= -0.7f; y += 0.2f)
                {
                    float faceY = -x * (4.7f / 0.41f);      // face height at x
                    if (Mathf.Abs(y - faceY) > 0.45f) continue;          // rig base within +-0.45 m of the face (on it)
                    var p = new Vector3(x, y, 0); var rh = H.MakeRig(_keep, p, Vector3.zero, 1.36f, mode); faceTot++; if (H.SupportAt(rh, p, out _)) faceSup++;
                    UnityEngine.Object.DestroyImmediate(rh.go); _keep.Remove(rh.go);
                }
            string res = faceSup + "/" + faceTot; string expect = "0/" + faceTot;
            H.Rec("G3b/quay face", "quay face", "plateau + 85 deg face (normal_y 0.087, 4.7 m) + lower floor", "", "0.087", "0", faceTot + " rig positions on the face", mode, res, expect);
            H.Check(fails, "G3b/quay face", mode, res, expect);
            // wall-floor corner: floor (y=0, x<=0) meets a wall rising on its east side; rig beside the wall base is supported by the floor
            Reset(); H.Box(_keep, "floor", new Vector3(-50, -0.5f, 0), new Vector3(100, 1, 100)); H.Box(_keep, "wallUp", new Vector3(0.05f, 1.5f, 0), new Vector3(0.1f, 3f, 100)); Physics.SyncTransforms();
            int cs = 0, ct = 0; foreach (float x in new[] { -0.02f, -0.05f, -0.1f, -0.2f, -0.5f }) { var p = new Vector3(x, 0, 0); var rh = H.MakeRig(_keep, p, Vector3.zero, 1.36f, mode); ct++; if (H.SupportAt(rh, p, out _)) cs++; UnityEngine.Object.DestroyImmediate(rh.go); _keep.Remove(rh.go); }
            H.Rec("G3b/wall-floor corner", "wall rising from the floor", "floor + vertical wall", "", "1.0 (floor) / 0 (wall)", "0", ct + " positions 2-50 cm from the wall base", mode, cs + "/" + ct, ct + "/" + ct);
            H.Check(fails, "G3b/corner", mode, cs + "/" + ct, ct + "/" + ct);
        }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    [Test]
    public void G3c_RampsStepsAndOverlaps_NoUnstableSupport()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
        {
            // ramp: floor y=0 (x<0), ramp normal_y 0.8 from x=0 to x=1.2, floor again at the top
            Reset(); float a = Mathf.Acos(0.8f); float top = 1.2f * Mathf.Tan(a);
            var rot = Quaternion.Euler(0, 0, a * Mathf.Rad2Deg); Vector3 up = rot * Vector3.up;
            H.Box(_keep, "floorLow", new Vector3(-50, -0.5f, 0), new Vector3(100, 1, 100)); H.Box(_keep, "ramp", 0.5f * (1.2f / Mathf.Cos(a)) * new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) - 0.5f * up, new Vector3(1.2f / Mathf.Cos(a), 1, 100), rot);
            H.Box(_keep, "floorHigh", new Vector3(1.2f + 50f, top - 0.5f, 0), new Vector3(100, 1, 100)); Physics.SyncTransforms();
            var rh = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); int unsup = 0, flips = 0, n = 0; bool prev = true;
            for (float x = -1f; x <= 2.4f; x += 0.01f)
            {
                float yb = x < 0 ? 0 : (x < 1.2f ? x * Mathf.Tan(a) : top); var p = new Vector3(x, yb, 0); n++; bool s = H.SupportAt(rh, p, out _); if (!s) unsup++; if (s != prev) flips++; prev = s;
            }
            string res = "unsupported=" + unsup + "/" + n + ";flips=" + flips;
            H.Rec("G3c/ramp", "slope and terrain transition", "floor -> ramp (normal_y 0.8) -> floor", "", "0.8", "0", n + " positions every 1 cm along the path, rig base on the surface", mode, res, "unsupported=0/" + n + ";flips=0");
            H.Check(fails, "G3c/ramp", mode, res, "unsupported=0/" + n + ";flips=0");
            foreach (float h in new[] { 0.02f, 0.05f, 0.1f, 0.3f, 0.5f })
            {
                Reset(); H.Box(_keep, "A", new Vector3(-50, -0.5f, 0), new Vector3(100, 1, 100)); H.Box(_keep, "B", new Vector3(50, h - 0.5f, 0), new Vector3(100, 1, 100)); Physics.SyncTransforms();
                var r2 = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); int bad = 0, nn = 0; float maxDev = 0;
                for (float x = -0.5f; x <= 0.5f; x += 0.01f) { float yb = x < 0 ? 0 : h; nn++; bool s = H.SupportAt(r2, new Vector3(x, yb, 0), out float gy); if (!s) bad++; else maxDev = Mathf.Max(maxDev, Mathf.Abs(gy - yb)); }
                string rr = "unsupported=" + bad + "/" + nn + ";max_groundY_deviation=" + maxDev.ToString("F3");
                H.Rec("G3c/step" + h.ToString("F2"), "height discontinuity (curb)", "adjacent floors, step " + h, "", "1.0", "0", nn + " positions across the step", mode, rr, "unsupported=0/" + nn + ";max_groundY_deviation<=" + h.ToString("F3"), "rig base on its own side's surface");
                if (bad != 0 || maxDev > h + 0.002f) { if (H.IsFinal(mode)) fails.Add("G3c/step" + h + ": " + rr); }
            }
            // overlapping colliders at different heights (top y = 0 and y = 0.03 over x in [-0.5, 0.5])
            Reset(); H.Box(_keep, "base", new Vector3(0, -0.5f, 0), new Vector3(100, 1, 100)); H.Box(_keep, "overlay", new Vector3(0, 0.03f - 0.5f, 0), new Vector3(1f, 1, 100)); Physics.SyncTransforms();
            var r3 = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); int b3 = 0, n3 = 0; for (float x = -1f; x <= 1f; x += 0.01f) { n3++; if (!H.SupportAt(r3, new Vector3(x, 0, 0), out _)) b3++; }
            H.Rec("G3c/overlap", "overlapping colliders at different heights", "base + 3 cm overlay", "", "1.0", "0", n3 + " positions", mode, "unsupported=" + b3 + "/" + n3, "unsupported=0/" + n3);
            H.Check(fails, "G3c/overlap", mode, "unsupported=" + b3 + "/" + n3, "unsupported=0/" + n3);
        }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    // ---------------------------------------------------------------- G4 edges
    [Test]
    public void G4_EdgeOverhang_TheSupportTestDoesNotReachBeyondTheEdge()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
        {
            Reset(); H.Box(_keep, "plate", new Vector3(-50, -0.5f, 0), new Vector3(100, 1, 100)); Physics.SyncTransforms();
            var rh = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); float maxSup = -1; int falseSupport = 0, falseLoss = 0; var fl = new List<string>();
            for (int k = 0; k <= 40; k++)
            {
                float d = k * 0.01f; bool s = H.SupportAt(rh, new Vector3(d, 0, 0), out _); bool truth = d <= R;
                if (s) maxSup = d; if (s && !truth) falseSupport++; if (!s && truth) { falseLoss++; }
            }
            string res = "max_supported_overhang_m=" + maxSup.ToString("F2") + ";false_support=" + falseSupport + ";false_loss=" + falseLoss;
            H.Rec("G4/edge overhang", "edge: centre beyond the edge by d", "plate edge, capsule radius 0.1 m", "", "1.0", "0", "d = 0..0.40 m step 1 cm", mode, res, "false_support=0 (no reach beyond the baseline edge; false_loss band = baseline)", "baseline supports d <= 0 only; capsule truth d <= 0.10");
            if (H.IsFinal(mode) && (falseSupport != 0 || maxSup > 0.005f)) fails.Add("G4/edge: " + res);
            // corner overhang (both axes)
            Reset(); H.Box(_keep, "quad", new Vector3(-50, -0.5f, -50), new Vector3(100, 1, 100)); Physics.SyncTransforms();
            var r2 = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); float maxC = -1; for (int k = 0; k <= 30; k++) { float d = k * 0.01f; if (H.SupportAt(r2, new Vector3(d, 0, d), out _)) maxC = d; }
            H.Rec("G4/corner overhang", "corner: centre beyond both edges by d", "plate corner", "", "1.0", "0", "diagonal d = 0..0.30 m", mode, "max_supported_d=" + maxC.ToString("F2"), "<=0.00");
            if (H.IsFinal(mode) && maxC > 0.005f) fails.Add("G4/corner: max d " + maxC);
        }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    // ---------------------------------------------------------------- G5 genuine unsupported states
    [Test]
    public void G5_GenuineUnsupportedStates_AreNotHiddenByIncidentalHits()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
        {
            // (a) no geometry at all
            Reset(); var a = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); string ra = a.Support(Vector3.zero, out _) ? "supported" : "unsupported";
            H.Rec("G5/all miss", "genuine unsupported", "no ground", "", "", "0", "0 of 5 samples hit", mode, ra, "unsupported"); H.Check(fails, "G5/all miss", mode, ra, "unsupported");
            // (b) exactly one side sample hits (small patch under the +x sample only)
            Reset(); H.Box(_keep, "patch+x", new Vector3(0.15f, -0.5f, 0), new Vector3(0.06f, 1, 0.06f)); Physics.SyncTransforms();
            var b = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); string rb = b.Support(Vector3.zero, out _) ? "supported" : "unsupported";
            H.Rec("G5/one side sample hits", "incidental single hit", "6 cm patch 15 cm east of the centre", "", "1.0", "0", "1 of 5 samples hit (+x)", mode, rb, "unsupported", "any-hit variant is expected to report supported (over-reach)"); H.Check(fails, "G5/one hit", mode, rb, "unsupported");
            // (c) two side samples on the SAME side hit (+x and +z)
            Reset(); H.Box(_keep, "patch+x", new Vector3(0.15f, -0.5f, 0), new Vector3(0.06f, 1, 0.06f)); H.Box(_keep, "patch+z", new Vector3(0, -0.5f, 0.15f), new Vector3(0.06f, 1, 0.06f)); Physics.SyncTransforms();
            var c = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); string rc = c.Support(Vector3.zero, out _) ? "supported" : "unsupported";
            H.Rec("G5/two adjacent side samples hit", "incidental hits on one side", "patches east and north of the centre", "", "1.0", "0", "2 of 5 samples hit (+x,+z)", mode, rc, "unsupported"); H.Check(fails, "G5/two same side", mode, rc, "unsupported");
            // (d) opposite pair at consistent heights (a bridged slit) and at inconsistent heights
            Reset(); H.Box(_keep, "patch+x", new Vector3(0.15f, -0.5f, 0), new Vector3(0.06f, 1, 0.06f)); H.Box(_keep, "patch-x", new Vector3(-0.15f, -0.5f, 0), new Vector3(0.06f, 1, 0.06f)); Physics.SyncTransforms();
            var d1 = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); string rd1 = d1.Support(Vector3.zero, out _) ? "supported" : "unsupported";
            H.Rec("G5/opposite pair, same height", "bridged gap", "patches east and west, both y=0", "", "1.0", "0", "2 of 5 samples hit (+x,-x)", mode, rd1, mode == "single" || mode == "baseline" ? "unsupported" : "supported", "bridge expected: supported");
            if (H.IsFinal(mode) && rd1 != "supported") fails.Add("G5/pair same height: " + rd1);
            Reset(); H.Box(_keep, "patch+x", new Vector3(0.15f, 0.45f - 0.5f, 0), new Vector3(0.06f, 1, 0.06f)); H.Box(_keep, "patch-x", new Vector3(-0.15f, -0.45f - 0.5f, 0), new Vector3(0.06f, 1, 0.06f)); Physics.SyncTransforms();
            var d2 = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); string rd2 = d2.Support(Vector3.zero, out _) ? "supported" : "unsupported";
            H.Rec("G5/opposite pair, inconsistent heights", "samples at inconsistent heights", "patches at y=+0.45 and y=-0.45 (both inside the +-0.5 window)", "", "1.0", "0", "2 of 5 samples hit, heights differ by 0.9 m", mode, rd2, "unsupported", "any-hit variant reports supported");
            H.Check(fails, "G5/inconsistent heights", mode, rd2, "unsupported");
            // (e) rig beyond the edge
            Reset(); H.Box(_keep, "plate", new Vector3(-50, -0.5f, 0), new Vector3(100, 1, 100)); Physics.SyncTransforms();
            foreach (float d in new[] { 0.5f, 1.0f })
            { var e = H.MakeRig(_keep, new Vector3(d, 0, 0), Vector3.zero, 1.36f, mode); string re = e.Support(e.go.transform.position, out _) ? "supported" : "unsupported"; H.Rec("G5/beyond edge " + d, "moves beyond the edge of the supported surface", "plate edge", "", "1.0", "0", "centre " + d + " m past the edge", mode, re, "unsupported"); H.Check(fails, "G5/beyond " + d, mode, re, "unsupported"); }
        }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    // ---------------------------------------------------------------- G6 footprint independence from rig-root offset and eye height, with a slit under the footprint
    [Test]
    public void G6_SlitUnderTheFootprint_AtDifferentRootHeadOffsets()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
            foreach (float off in new[] { 0f, 0.33f, 1.08f })
                foreach (float height in new[] { 1.0f, 1.6f })
                {
                    Reset(); H.SeamTiles(_keep, 0.08f, 0f);                    // slit along x through the origin, 8 cm wide
                    // footprint centre in the middle of the slit, root `off` metres away along the seam (so the root is over ground or slit alike)
                    var rh = H.MakeRig(_keep, new Vector3(-off, 0, 0), new Vector3(off, 0, 0), height, mode);
                    bool s = rh.Support(rh.go.transform.position, out _); string res = s ? "supported" : "unsupported"; string id = "G6/off" + off.ToString("F2") + "/h" + height.ToString("F2");
                    H.Rec(id, "slit under the capsule footprint, varied root-head offset and eye height", "8 cm slit", "0.08", "1.0", off.ToString("F2"), "footprint centre in the slit; capsule height " + height, mode, res, "supported", "baseline: single ray in the slit");
                    H.Check(fails, id, mode, res, "supported");
                }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }
}
