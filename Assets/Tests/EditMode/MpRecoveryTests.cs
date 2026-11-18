// MpRecoveryTests.cs  (EditMode, Editor-only; ISOLATED REVIEW CLONE ONLY)
// GroundFallRecovery BEHAVIOUR tests (teleport / void displacement / fall / recovery) on deterministic synthetic geometry, driven through the production Tick(now) with a controlled clock
// at the recorded frame time (0.11 s, about 9 FPS). Dynamic scenarios move a real CharacterController (Move + XRI-style accumulated vertical velocity) over one-sided MeshColliders.
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using H = MpHarness;

[Category("MpRecovery")]
public class MpRecoveryTests
{
    readonly List<GameObject> _keep = new List<GameObject>();
    void Reset() { foreach (var g in _keep) if (g != null) UnityEngine.Object.DestroyImmediate(g); _keep.Clear(); }
    [TearDown] public void Cleanup() { Reset(); }
    [OneTimeTearDown] public void FlushRows() { H.Flush("recovery"); }
    const float DT = 0.11f;

    // run a teleport: rig stands at `from` for 6 frames, is moved to `to`, observed for `seconds`. returns (undone, seconds until the undo)
    static (bool undone, float afterS, string reason) Teleport(H.RigHandle rh, Vector3 from, Vector3 to, ref float t, float seconds = 2.6f)
    {
        rh.go.transform.position = from; Physics.SyncTransforms();
        for (int i = 0; i < 6; i++) { t += DT; H.Step(rh, t); }
        int c0 = H.Count(rh.c); float tj = t + DT;
        rh.go.transform.position = to; Physics.SyncTransforms();
        for (float s = 0; s < seconds; s += DT) { t += DT; H.Step(rh, t); if (H.Count(rh.c) > c0) return (true, t - tj, H.Reason(rh.c)); }
        return (false, float.NaN, "");
    }

    // the P3 street: two tiles, 9 cm slit along x through z = 0; origin and head on the south tile
    [Test]
    public void R1_ValidTeleportAcrossTheSlit_IsNotUndone()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
            foreach (bool capsuleUnderHead in new[] { true, false })
            {
                Reset(); H.SeamTiles(_keep, 0.09f, 0f);
                int undone = 0, n = 0; var bad = new List<string>(); float t = 0;
                for (int k = -12; k <= 12; k++)
                {
                    float u = k * 0.01f;                                  // lateral position of the destination ROOT relative to the slit centre line
                    var head = capsuleUnderHead ? new Vector3(-0.23f, 0, -0.24f) : Vector3.zero;
                    var rh = H.MakeRig(_keep, new Vector3(-20, 0, -3), head, 1.0f, mode); n++;
                    var r = Teleport(rh, new Vector3(-20, 0, -3), new Vector3(-1.33f, 0, u), ref t);
                    if (r.undone) { undone++; bad.Add(u.ToString("F2")); }
                    UnityEngine.Object.DestroyImmediate(rh.go); _keep.Remove(rh.go);
                }
                string id = "R1/capsule under " + (capsuleUnderHead ? "head" : "root");
                string res = "undone=" + undone + "/" + n; string expect = capsuleUnderHead ? "undone=0/" + n : "undone=0/" + n;
                H.Rec(id, "valid 18.67 m teleport, destination root at the slit (P3-like)", "9 cm slit, capsule " + (capsuleUnderHead ? "under head (0.33 m from the root)" : "under root"), "0.09", "1.0", capsuleUnderHead ? "0.33" : "0", "25 lateral root positions -0.12..+0.12 m", mode, res, expect, "undone at u(m): " + string.Join("|", bad));
                H.Check(fails, id, mode, res, expect);
            }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    [Test]
    public void R2_UnsupportedOrigin_IsNeverTurnedIntoASupportedOne()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
        {
            // (a) origin genuinely unsupported: plate x <= 0, origin 1 m beyond the edge; a safe position exists (the rig stood on the plate first)
            Reset(); H.Box(_keep, "plate", new Vector3(-50, -0.5f, 0), new Vector3(100, 1, 100)); H.Box(_keep, "far", new Vector3(500, -0.5f, 0), new Vector3(100, 1, 100)); Physics.SyncTransforms();
            var rh = H.MakeRig(_keep, new Vector3(-1, 0, 0), Vector3.zero, 1.36f, mode); float t = 0;
            for (int i = 0; i < 8; i++) { t += DT; H.Step(rh, t); }
            rh.go.transform.position = new Vector3(1.0f, 0, 0); Physics.SyncTransforms(); for (int i = 0; i < 3; i++) { t += DT; H.Step(rh, t); }       // 1 m beyond the edge: no support, below the fall limit not reached
            bool originSupported = rh.Support(new Vector3(1.0f, 0, 0), out _);
            H.Rec("R2/origin beyond edge", "unsupported origin", "plate edge", "", "1.0", "0", "origin 1 m beyond the edge", mode, originSupported ? "supported" : "unsupported", "unsupported"); H.Check(fails, "R2/origin", mode, originSupported ? "supported" : "unsupported", "unsupported");
            // teleport from that unsupported origin to supported ground far away: kept; and into a void: outcome recorded (baseline = patched expected)
            int c0 = H.Count(rh.c); rh.go.transform.position = new Vector3(500, 0, 0); Physics.SyncTransforms(); for (float s = 0; s < 2f; s += DT) { t += DT; H.Step(rh, t); }
            string toGround = H.Count(rh.c) > c0 ? "undone" : "kept"; H.Rec("R2/teleport from unsupported origin to ground", "teleport from an unsupported origin", "plate edge + far plate", "", "1.0", "0", "destination supported", mode, toGround, "kept"); H.Check(fails, "R2/to ground", mode, toGround, "kept");
            Reset(); H.Box(_keep, "plate", new Vector3(-50, -0.5f, 0), new Vector3(100, 1, 100)); Physics.SyncTransforms();
            var r2 = H.MakeRig(_keep, new Vector3(-1, 0, 0), Vector3.zero, 1.36f, mode); t = 0; for (int i = 0; i < 8; i++) { t += DT; H.Step(r2, t); }
            r2.go.transform.position = new Vector3(1.0f, 0, 0); Physics.SyncTransforms(); for (int i = 0; i < 3; i++) { t += DT; H.Step(r2, t); }
            c0 = H.Count(r2.c); r2.go.transform.position = new Vector3(500, 0, 0); Physics.SyncTransforms(); for (float s = 0; s < 2f; s += DT) { t += DT; H.Step(r2, t); }
            string toVoid = H.Count(r2.c) > c0 ? "undone" : "kept"; H.Rec("R2/teleport from unsupported origin into a void", "teleport from an unsupported origin into a void", "plate edge, nothing at the destination", "", "1.0", "0", "origin and destination unsupported, safe position exists", mode, toVoid, "recorded (identical in baseline and patched)");
            // (b) the P3 24th analogue: origin ROOT inside the slit (capsule on ground), destination root inside the slit: origin is supported for the capsule in the patched logic
            Reset(); H.SeamTiles(_keep, 0.09f, 0f);
            var r3 = H.MakeRig(_keep, new Vector3(-20, 0, 0), Vector3.zero, 1.0f, mode); t = 0;
            var rr = Teleport(r3, new Vector3(-20, 0, -3), new Vector3(-20, 0, 0), ref t, 0.5f);       // stand on the south tile first (safe position), then move 3 m into the slit
            for (int i = 0; i < 15; i++) { t += DT; H.Step(r3, t); }
            var r24 = Teleport(r3, new Vector3(-20, 0, 0), new Vector3(-1.33f, 0, 0.0f), ref t, 2.6f);
            H.Rec("R2/24th analogue: origin and destination in the slit", "origin in the slit (capsule on ground)", "9 cm slit, root in the slit at origin and destination", "0.09", "1.0", "0", "origin root u=0, destination root u=0", mode, r24.undone ? "undone" : "kept", "kept (capsule on ground at both ends)", "baseline: the origin counts as unsupported");
            H.Check(fails, "R2/24th analogue", mode, r24.undone ? "undone" : "kept", "kept");
        }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    [Test]
    public void R3_GenuineVoidDestination_IsUndoneAfterTheGracePeriod()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
        {
            Reset(); H.Box(_keep, "plate", new Vector3(-50, -0.5f, 0), new Vector3(100, 1, 100)); Physics.SyncTransforms();
            foreach (var (name, dest) in new[] { ("empty space 400 m away", new Vector3(400, 0, 0)), ("0.5 m beyond an edge", new Vector3(0.5f, 0, 0)), ("1.0 m beyond an edge", new Vector3(1.0f, 0, 0)) })
            {
                var rh = H.MakeRig(_keep, new Vector3(-10, 0, 0), Vector3.zero, 1.36f, mode); float t = 0;
                var r = Teleport(rh, new Vector3(-10, 0, 0), dest, ref t);
                string res = r.undone ? "undone after " + r.afterS.ToString("F2") + " s (" + r.reason + ")" : "kept";
                H.Rec("R3/" + name, "genuine unsupported destination", "plate + void", "", "1.0", "0", name, mode, res, "undone after 0.50-0.70 s (void_displacement)");
                bool ok = r.undone && r.reason == "void_displacement" && r.afterS >= 0.5f && r.afterS <= 0.75f;
                if (H.IsFinal(mode) && !ok) fails.Add("R3/" + name + ": " + res);
                UnityEngine.Object.DestroyImmediate(rh.go); _keep.Remove(rh.go);
            }
        }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    [Test]
    public void R5_RapidConsecutiveHopsAlongTheSlit_AreAllKept()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
            foreach (bool head in new[] { true, false })
            {
                Reset(); H.SeamTiles(_keep, 0.09f, 0f);
                var rh = H.MakeRig(_keep, new Vector3(-80, 0, -3), head ? new Vector3(-0.23f, 0, -0.24f) : Vector3.zero, 1.0f, mode); float t = 0;
                rh.go.transform.position = new Vector3(-80, 0, -3); for (int i = 0; i < 10; i++) { t += DT; H.Step(rh, t); }
                int undone = 0; float x = -80;
                for (int hop = 0; hop < 4; hop++)
                {
                    x += 18.67f; int c0 = H.Count(rh.c); rh.go.transform.position = new Vector3(x, 0, 0.02f); Physics.SyncTransforms();
                    for (float s = 0; s < 2.1f; s += DT) { t += DT; H.Step(rh, t); }
                    if (H.Count(rh.c) > c0) { undone++; x -= 18.67f; }
                }
                string id = "R5/capsule under " + (head ? "head" : "root"); string res = "undone=" + undone + "/4";
                H.Rec(id, "rapid consecutive teleports (P3 hops 24-27)", "9 cm slit, destination roots in the slit every 18.67 m, 2.1 s apart", "0.09", "1.0", head ? "0.33" : "0", "4 hops", mode, res, "undone=0/4");
                H.Check(fails, id, mode, res, "undone=0/4");
            }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    [Test]
    public void R7_LargeVerticalMovementWithoutTeleport()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
        {
            Reset(); H.Box(_keep, "low", new Vector3(0, -0.5f, 0), new Vector3(40, 1, 40)); H.Box(_keep, "high", new Vector3(60, 3f - 0.5f, 0), new Vector3(40, 1, 40)); H.Box(_keep, "deep", new Vector3(-60, -4.5f - 0.5f, 0), new Vector3(40, 1, 40)); Physics.SyncTransforms();
            var rh = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); float t = 0;
            // +3 m in one frame onto a platform that has ground (e.g. a lift): supported destination
            var up = Teleport(rh, Vector3.zero, new Vector3(0, 3f, 0) + new Vector3(60, 0, 0) - new Vector3(60, 0, 0), ref t, 1.5f);   // (0,3,0): nothing there -> void above the low plate
            H.Rec("R7/+3 m with nothing at the new height", "large vertical movement", "low plate; nothing at y=3 above it", "", "", "0", "rig moved +3 m", mode, up.undone ? "undone" : "kept", "undone (no support at the new height)");
            H.Check(fails, "R7/up void", mode, up.undone ? "undone" : "kept", "undone");
            var rh2 = H.MakeRig(_keep, new Vector3(60, 3, 0), Vector3.zero, 1.36f, mode); t = 0;
            var down = Teleport(rh2, new Vector3(60, 3, 0), new Vector3(0, 0, 0), ref t, 1.5f);
            H.Rec("R7/-3 m onto a supported lower floor", "large vertical movement", "high platform to low plate", "", "", "0", "rig moved -3 m onto ground", mode, down.undone ? "undone" : "kept", "kept");
            H.Check(fails, "R7/down ground", mode, down.undone ? "undone" : "kept", "kept");
        }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    // ---------------------------------------------------------------- dynamic: a real CharacterController on one-sided terrain meshes (the P4-like quay)
    void Quay()
    {
        var quads = new List<Vector3[]>(); var nrm = new List<Vector3>();
        quads.Add(new[] { new Vector3(-60, 0, -60), new Vector3(0, 0, -60), new Vector3(0, 0, 60), new Vector3(-60, 0, 60) }); nrm.Add(Vector3.up);
        quads.Add(new[] { new Vector3(0, 0, -60), new Vector3(0.41f, -4.7f, -60), new Vector3(0.41f, -4.7f, 60), new Vector3(0, 0, 60) }); nrm.Add(new Vector3(0.996f, 0.087f, 0));
        quads.Add(new[] { new Vector3(0.41f, -4.7f, -60), new Vector3(60, -4.7f, -60), new Vector3(60, -4.7f, 60), new Vector3(0.41f, -4.7f, 60) }); nrm.Add(Vector3.up);
        H.MeshSurface(_keep, "quay", quads, nrm); Physics.SyncTransforms();
    }

    class SimResult { public int recoveries; public string firstReason; public float firstAt = float.NaN; public Vector3 end; public float minY; public int frames; public List<string> log = new List<string>(); }

    // XRI-style locomotion: vertical velocity accumulates while the controller is not grounded and is zeroed only when it is (ContinuousMoveProviderBase.MoveRig)
    SimResult Sim(H.RigHandle rh, Vector3 start, Func<int, Vector3> horizontalPerFrame, int frames, Action<int, H.RigHandle> pre = null)
    {
        var res = new SimResult(); float t = 0, vy = 0; res.minY = start.y; rh.go.transform.position = start; Physics.SyncTransforms();
        for (int i = 0; i < frames; i++)
        {
            pre?.Invoke(i, rh);
            if (rh.cc.isGrounded) vy = 0f; else vy += Physics.gravity.y * DT;
            Vector3 motion = horizontalPerFrame(i) + Vector3.up * vy * DT;
            rh.cc.Move(motion);
            t += DT; int c0 = H.Count(rh.c); Vector3 before = rh.go.transform.position; H.Step(rh, t);
            if (H.Count(rh.c) > c0) { res.recoveries++; if (res.firstReason == null) { res.firstReason = H.Reason(rh.c); res.firstAt = t; } res.log.Add(t.ToString("F2") + "s " + H.Reason(rh.c) + " " + before.ToString("F2") + " -> " + rh.go.transform.position.ToString("F2")); }
            res.minY = Mathf.Min(res.minY, rh.go.transform.position.y); res.frames++;
        }
        res.end = rh.go.transform.position; return res;
    }

    [Test]
    public void R4_GenuineFallNextToTheQuay_IsRecovered_AndNotRepeatedIntoTheFace()
    {
        var fails = new List<string>();
        foreach (var mode in H.Modes)
            foreach (var (label, offsetX) in new[] { ("capsule under root", 0f), ("capsule 1.09 m from root", 1.09f) })
            {
                // (A) walk off the plateau edge toward the lower floor at 1.4 m/s, 9 FPS
                Reset(); Quay();
                var rh = H.MakeRig(_keep, new Vector3(-6, 0.0f, 0), new Vector3(-offsetX, 0, 0), 1.36f, mode);
                var a = Sim(rh, new Vector3(-6, 0.05f, 0), i => i < 60 ? new Vector3(1.4f * DT, 0, 0) : Vector3.zero, 160);
                bool onPlateau = a.end.x < -0.4f && Mathf.Abs(a.end.y) < 0.3f; bool onLower = a.end.y < -4f;
                string resA = "recoveries=" + a.recoveries + ";end=" + a.end.ToString("F2") + ";first=" + a.firstReason;
                H.Rec("R4A/" + label, "genuine drop beside a 4.7 m quay face (walking off the edge)", "one-sided TIN-like meshes: plateau, 85 deg face, lower floor", "", "0.087", offsetX.ToString("F2"), "CharacterController, XRI-style gravity, 0.11 s frames", mode, resA, "ends on the plateau or on supported lower ground, never falling", string.Join(" | ", a.log));
                if (H.IsFinal(mode) && a.minY < -60f) fails.Add("R4A/" + label + " fell away: " + resA);
                // (B) the P4 mechanism: the rig is moved into the terrain volume behind the face (what the old AutoGroundAlign snap did) and falls through the one-sided meshes
                Reset(); Quay();
                var rb = H.MakeRig(_keep, new Vector3(-3, 0.05f, 0), new Vector3(-offsetX, 0, 0), 1.36f, mode);
                int[] snapAt = { 60 };
                var b = Sim(rb, new Vector3(-3, 0.05f, 0), i => i < 15 ? new Vector3(1.4f * DT, 0, 0) : Vector3.zero, 200, (i, h) => { if (i == 45) { h.cc.enabled = false; h.go.transform.position = new Vector3(0.2f, -1.5f, 0); h.cc.enabled = true; Physics.SyncTransforms(); } });
                bool recovered = b.recoveries >= 1; bool endsSafe = b.end.x < -0.4f && Mathf.Abs(b.end.y) < 0.4f; bool noLoop = b.recoveries <= 3; bool didNotFallAway = b.end.y > -30f;
                string resB = "recoveries=" + b.recoveries + ";end=" + b.end.ToString("F2") + ";min_y=" + b.minY.ToString("F1") + ";first=" + b.firstReason + "@" + b.firstAt.ToString("F2");
                H.Rec("R4B/" + label, "fall through the terrain volume behind the quay face (P4 mechanism)", "one-sided meshes; rig moved 1.5 m below the plateau behind the face at frame 45", "", "0.087", offsetX.ToString("F2"), "CharacterController, XRI-style gravity, 0.11 s frames, 200 frames", mode, resB, "recovered, ends on the plateau, <= 3 recoveries", string.Join(" | ", b.log));
                if (H.IsFinal(mode) && !(recovered && endsSafe && noLoop && didNotFallAway)) fails.Add("R4B/" + label + ": " + resB);
                // recovery-induced displacement must not be classified as a jump: after the run no jump is pending
                bool pending = H.GetPriv<bool>(rb.c, "pendingJump");
                H.Rec("R4B/" + label + "/no pending jump after recovery", "recovery-induced displacement", "after R4B", "", "", offsetX.ToString("F2"), "pendingJump flag at the end", mode, pending ? "pendingJump=true" : "pendingJump=false", "pendingJump=false");
                if (H.IsFinal(mode) && pending) fails.Add("R4B/" + label + ": pendingJump at the end");
            }
        Reset(); Assert.IsEmpty(fails, string.Join("\n", fails));
    }
}
