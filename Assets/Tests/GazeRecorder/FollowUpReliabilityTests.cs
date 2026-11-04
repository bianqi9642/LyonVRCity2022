// FollowUpReliabilityTests.cs
// Tests for the schema-v12 follow-up: M2 (the FINAL manifest reports Closed with a real closure timestamp), N2 (cause=detour_return for a verified return from a completed UncertainBehavior
// detour), G1 (tracking-origin evidence and the frame statistics that tell a tracking-origin offset from a head-model offset). Editor tests: they establish logic and the files the production
// classes really write; they say NOTHING about the Quest Pro coordinate system (see docs/gaze_origin_verification.md for what needs hardware).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using static Reflect;

// ================================================================================== N2: pure classifier (ONE classifier, no Part)

public class DestinationCausePureTests
{
    static readonly Type DC = T("DestinationCause");

    static object Inp(bool live = false, bool itin = false, int started = 0, int completed = 0, int consumed = 0, bool restored = true, string identity = "Unknown", Vector3? resume = null, Vector3? observed = null)
    {
        Type t = DC.GetNestedType("Inputs"); object o = Activator.CreateInstance(t);
        void S(string f, object v) => t.GetField(f).SetValue(o, v);
        S("DetourActiveNow", live); S("ItineraryTargetMatches", itin); S("DetoursStarted", started); S("DetoursCompleted", completed); S("DetoursCompletedConsumed", consumed);
        S("RestoredPreDetourTarget", restored); S("ResumeTransformIdentity", Enum.Parse(DC.GetNestedType("ResumeIdentity"), identity));
        S("ResumeTarget", resume ?? new Vector3(10f, 0f, 10f)); S("ObservedDestination", observed ?? new Vector3(10f, 0f, 10f));
        return o;
    }

    static (string cause, int consumed) Cls(object inp)
    {
        var args = new object[] { inp, 0 };
        string c = (string)DC.GetMethod("Classify").Invoke(null, args);
        return (c, (int)args[1]);
    }

    [Test]
    public void LiveDetour_IsDetour_AndAnEarlierCompletionIsConsumedByThisFirstObservedChange()
    {
        var r = Cls(Inp(live: true, started: 2, completed: 1));
        Assert.AreEqual("detour", r.cause);
        Assert.AreEqual(1, r.consumed);
    }

    [Test]
    public void VerifiedReturn_IsDetourReturn_ByPositionFallback_AndTheCompletionExplainsOnlyOneChange()
    {
        var r = Cls(Inp(started: 1, completed: 1, resume: new Vector3(40f, 3f, 12f), observed: new Vector3(40.4f, 0f, 12.5f)));   // within the snap tolerance, y ignored
        Assert.AreEqual("detour_return", r.cause);
        Assert.AreEqual(1, r.consumed);
        var next = Cls(Inp(started: 1, completed: 1, consumed: r.consumed, resume: new Vector3(40f, 0f, 12f), observed: new Vector3(40f, 0f, 12f)));
        Assert.AreEqual("other", next.cause, "a completion explains ONE change; stale evidence is never reused");
    }

    [Test]
    public void CrossPart_EquivalentEvidence_GivesTheSameCause_WanderingPointOrAttractorStop_WhateverTheGenericItineraryRuleSays()
    {
        // Part 2-like: the owner re-issued the SAME Transform (identity match); the destination also happens to be the current itinerary entry.
        var wander = Cls(Inp(itin: true, started: 1, completed: 1, identity: "Match", resume: new Vector3(100f, 0f, 100f), observed: new Vector3(100.8f, 0f, 100.2f)));
        // Part 3-like: the saved POSITION was re-issued (no Transform identity); the destination is an attractor stop that is NOT the plane pivot (itinerary match false).
        var attractor = Cls(Inp(itin: false, started: 1, completed: 1, identity: "Unknown", resume: new Vector3(55f, 0f, 70f), observed: new Vector3(55.3f, 0f, 70.1f)));
        // Part 3-like waypoint (an entrance / exit transform): itinerary geometry matches too.
        var waypoint = Cls(Inp(itin: true, started: 1, completed: 1, identity: "Unknown", resume: new Vector3(55f, 0f, 70f), observed: new Vector3(55f, 0f, 70f)));
        Assert.AreEqual("detour_return", wander.cause); Assert.AreEqual("detour_return", attractor.cause); Assert.AreEqual("detour_return", waypoint.cause);
        Assert.AreEqual(wander.cause, attractor.cause, "the cause does not depend on the destination type or on the Part");
    }

    [Test]
    public void ANormalNewTarget_IsItineraryTarget_NotAReturn_EvenRightAfterACompletedDetour()
    {
        Assert.AreEqual("itinerary_target", Cls(Inp(itin: true)).cause, "no detour at all");
        Assert.AreEqual("itinerary_target", Cls(Inp(itin: true, started: 1, completed: 1, restored: false)).cause, "the owner picked a different target: a normal new target, not a restoration");
        Assert.AreEqual("itinerary_target", Cls(Inp(itin: true, started: 1, completed: 1, consumed: 1)).cause, "the completion was already used");
        Assert.AreEqual("other", Cls(Inp(itin: false, started: 1, completed: 1, restored: false)).cause);
    }

    [Test]
    public void TransformIdentity_IsStrongerThanGeometry_InBothDirections()
    {
        Assert.AreEqual("itinerary_target", Cls(Inp(itin: true, started: 1, completed: 1, identity: "Mismatch")).cause, "the mover follows another Transform now, even though the position equals the resume position");
        Assert.AreEqual("detour_return", Cls(Inp(started: 1, completed: 1, identity: "Match", resume: new Vector3(0f, 0f, 0f), observed: new Vector3(9f, 0f, 9f))).cause, "same Transform: identity decides, the snap position is irrelevant");
    }

    [Test]
    public void NoEvidence_NeverProducesDetourReturn()
    {
        Assert.AreEqual("other", Cls(Inp(started: 1, completed: 0)).cause, "a detour that did not complete");
        Assert.AreEqual("other", Cls(Inp(started: 0, completed: 1)).cause, "a completion without a started detour");
        Assert.AreEqual("other", Cls(Inp(started: 1, completed: 1, restored: false)).cause, "no verified restoration");
        var far = Cls(Inp(started: 1, completed: 1, resume: new Vector3(0f, 0f, 0f), observed: new Vector3(30f, 0f, 0f)));
        Assert.AreEqual("other", far.cause, "the destination is not the recorded resume target");
        Assert.AreEqual(1, far.consumed, "the first change after the completion consumes it even when it does not match");
    }

    [Test]
    public void ToleranceIsTheDocumentedNavMeshSnapRule_NotAnArbitraryLargeThreshold()
    {
        float tol = GetStatic<float>(DC, "DetourReturnToleranceMeters");
        Assert.AreEqual(1.5f, tol);
        Assert.AreEqual("detour_return", Cls(Inp(started: 1, completed: 1, observed: new Vector3(10f + tol - 0.01f, 0f, 10f))).cause);
        Assert.AreEqual("other", Cls(Inp(started: 1, completed: 1, observed: new Vector3(10f + tol + 0.1f, 0f, 10f))).cause);
    }

    [Test]
    public void TwoSuccessiveDetours_EachReturnIsLabelledOnce()
    {
        var a = Cls(Inp(started: 1, completed: 1, consumed: 0));
        Assert.AreEqual("detour_return", a.cause);
        var b = Cls(Inp(started: 2, completed: 2, consumed: a.consumed));
        Assert.AreEqual("detour_return", b.cause);
        Assert.AreEqual(2, b.consumed);
    }

    [Test]
    public void TheClassifierSourceHasNoPartCheck()
    {
        string src = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Capture", "DestinationCause.cs"));
        foreach (string forbidden in new[] { "Part2", "Part3", "IsWandering", "movementMode", "selectedPart" })
            StringAssert.DoesNotContain(forbidden, src.Substring(src.IndexOf("public static class DestinationCause", StringComparison.Ordinal)), "the classifier must not branch on the Part");
    }
}

// ================================================================================== G1: pure geometry

public class TrackingOriginPureTests
{
    static readonly Type Origin = T("EyeGazeOrigin");
    static readonly Type Stats = Type.GetType("EyeGazeOrigin+DeviationStats, Assembly-CSharp");
    static readonly Type Tss = Type.GetType("EyeGazeOrigin+TrackingSpaceStats, Assembly-CSharp");

    static string Rel(string ovrp, string unity) => (string)CallStatic(Origin, "TrackingOriginsRelation", ovrp, unity);

    [Test]
    public void TrackingOriginsRelation_DistinguishesDeviceFromFloorKinds_AndSaysUnknownWhenNotMeasured()
    {
        Assert.AreEqual("same_kind", Rel("EyeLevel", "Device"));
        Assert.AreEqual("same_kind", Rel("FloorLevel", "Floor"));
        Assert.AreEqual("same_kind", Rel("Stage", "Floor"));
        Assert.AreEqual("differ", Rel("FloorLevel", "Device"), "the candidate mechanism: Unity at Device (eye-level) while OVRPlugin keeps a floor-referenced origin");
        Assert.AreEqual("differ", Rel("EyeLevel", "Floor"));
        Assert.AreEqual("unknown", Rel("unavailable", "Device"));
        Assert.AreEqual("unknown", Rel("FloorLevel", "Unknown"));
        Assert.AreEqual("unknown", Rel("", "Device"));
    }

    // Both eyes' provider origins offset by `world` from the anchors, for a head that yaws / pitches / translates; rig yaw fixed.
    static object Collect(Func<Quaternion, Vector3> offsetForHead, int n = 60)
    {
        var stats = Activator.CreateInstance(Stats);
        Quaternion rig = Quaternion.Euler(0f, 20f, 0f);
        for (int i = 0; i < n; i++)
        {
            Quaternion head = Quaternion.Euler(-25f + 50f * (i % 7) / 6f, -180f + 360f * i / n, 0f);
            Vector3 anchor = new Vector3(0.1f * i, 1.6f + 0.002f * i, -0.05f * i);
            Vector3 provider = anchor + offsetForHead(head);
            Call(stats, "NoteInFrames", provider, anchor, head, rig);
        }
        return stats;
    }

    [Test]
    public void FrameStatistics_ATrackingOriginOffset_IsConstantInWorldAndRigAxes_ButNotInHeadAxes()
    {
        Vector3 originOffset = new Vector3(0f, 1.3614f, 0f);
        var stats = Collect(head => originOffset);
        Vector3 wm = Get<object>(stats, "OffsetWorld").Mean(), ws = Get<object>(stats, "OffsetWorld").Std();
        Vector3 hs = Get<object>(stats, "OffsetHead").Std();
        Assert.AreEqual(1.3614f, wm.y, 1e-4f); Assert.Less(ws.y, 1e-4f); Assert.Less(ws.x, 1e-4f);
        Assert.Less(Get<object>(stats, "OffsetRig").Std().y, 1e-4f, "constant in the rig's own axes too (a vertical vector is unaffected by the rig's yaw)");
        Assert.Greater(Mathf.Max(hs.x, hs.z), 0.1f, "expressed in head axes it follows the head (pitch / yaw rotate it): the signature of a tracking-origin offset, not a head-model offset");
        Assert.IsTrue(Get<bool>(stats, "FramesRecorded"));
    }

    [Test]
    public void FrameStatistics_AHeadModelOffset_IsConstantInHeadAxes_ButNotInWorldAxes()
    {
        Vector3 headOffset = new Vector3(0.01f, -0.02f, 0.03f);   // a few centimetres in the head frame
        var stats = Collect(head => head * headOffset);
        Assert.Less(Get<object>(stats, "OffsetHead").Std().magnitude, 1e-4f, "constant in head axes");
        Assert.Greater(Get<object>(stats, "OffsetWorld").Std().magnitude, 0.005f, "but it moves in world axes as the head rotates");
        Assert.AreEqual(0.0374f, Get<object>(stats, "OffsetHead").Mean().magnitude, 1e-3f);
    }

    [Test]
    public void FrameStatistics_IgnoreNonFiniteSamples_AndStartEmpty()
    {
        var stats = Activator.CreateInstance(Stats);
        Assert.IsFalse(Get<bool>(stats, "FramesRecorded"));
        Call(stats, "NoteInFrames", new Vector3(float.NaN, 0f, 0f), Vector3.zero, Quaternion.identity, Quaternion.identity);
        Call(stats, "NoteInFrames", Vector3.zero, new Vector3(0f, float.PositiveInfinity, 0f), Quaternion.identity, Quaternion.identity);
        Assert.IsFalse(Get<bool>(stats, "FramesRecorded"), "a non-finite origin never enters the statistics");
    }

    // The SDK conversion with ONE consistent tracking origin: head translation and rotation must move provider and anchor identically.
    [Test]
    public void ConsistentTrackingOrigin_ProviderAndAnchorCoincide_UnderHeadTranslationAndRotation_AndEyeSeparationIsPreserved()
    {
        float halfIpd = 0.0337f;   // the half separation seen in the recorded rig anchors; only used to build both eyes, never as an expected eye height
        Quaternion rigRot = Quaternion.Euler(0f, 33f, 0f); Vector3 rigPos = new Vector3(2393f, 166.9f, 807f);
        foreach (Vector3 headPos in new[] { Vector3.zero, new Vector3(0.4f, -0.1f, 0.7f), new Vector3(-1.2f, 0.05f, 2.5f) })
        foreach (float yaw in new[] { 0f, 77f, -140f })
        foreach (float pitch in new[] { 0f, 30f, -30f })
        {
            Quaternion headRot = Quaternion.Euler(pitch, yaw, 0f);
            Matrix4x4 cam = Matrix4x4.TRS(rigPos, rigRot, Vector3.one) * Matrix4x4.TRS(headPos, headRot, Vector3.one);
            Vector3 lT = headPos + headRot * new Vector3(-halfIpd, 0f, 0f), rT = headPos + headRot * new Vector3(halfIpd, 0f, 0f);
            Vector3 lW = (Vector3)CallStatic(Origin, "SdkWorldSpaceOrigin", cam, headPos, headRot, lT), rW = (Vector3)CallStatic(Origin, "SdkWorldSpaceOrigin", cam, headPos, headRot, rT);
            Matrix4x4 t2w = Matrix4x4.TRS(rigPos, rigRot, Vector3.one);
            Assert.AreEqual(0f, (lW - t2w.MultiplyPoint3x4(lT)).magnitude, 1e-3f, "provider = anchor when both live in one tracking origin");
            Assert.AreEqual(0f, (rW - t2w.MultiplyPoint3x4(rT)).magnitude, 1e-3f);
            Assert.AreEqual(2f * halfIpd, (rW - lW).magnitude, 1e-3f, "eye separation survives any head pose (float32 at ~2400 m coordinates: ~0.2 mm resolution)");
        }
    }

    [Test]
    public void MismatchedTrackingOrigin_IsDetectedByTheValidation_FallsBackToTheAnchor_AndLeavesDirectionsUntouched()
    {
        Quaternion rigRot = Quaternion.Euler(0f, -50f, 0f); Vector3 rigPos = new Vector3(5f, 100f, -7f);
        Quaternion headRot = Quaternion.Euler(12f, 95f, 0f); Vector3 headPos = new Vector3(0.1f, -0.15f, 0.2f);
        Matrix4x4 cam = Matrix4x4.TRS(rigPos, rigRot, Vector3.one) * Matrix4x4.TRS(headPos, headRot, Vector3.one);
        Vector3 eyeT = headPos + headRot * new Vector3(-0.03f, 0f, 0f);
        Vector3 shifted = eyeT + Vector3.up * 1.3614f;   // the same eye reported in a second, vertically displaced tracking origin (test value, not a constant used by production code)
        Vector3 anchor = Matrix4x4.TRS(rigPos, rigRot, Vector3.one).MultiplyPoint3x4(eyeT);
        Vector3 provider = (Vector3)CallStatic(Origin, "SdkWorldSpaceOrigin", cam, headPos, headRot, shifted);
        Assert.AreEqual(1.3614f, (provider - anchor).y, 1e-3f, "the mismatch appears as a world-vertical offset");
        var args = new object[] { provider, (Vector3?)anchor, 0.12f, null, null };
        object src = Origin.GetMethod("Resolve").Invoke(null, args);
        Assert.AreEqual("EyeAnchor", src.ToString(), "an inconsistent provider origin is never used");
        Assert.AreEqual(0f, ((Vector3)args[3] - anchor).magnitude, 1e-6f, "the fallback is the anchor itself - no constant is added");

        // direction: rotation is unaffected by a translation between tracking origins and stays a unit vector in the same world frame as the head
        Quaternion eyeRotTracking = headRot * Quaternion.Euler(-5f, 8f, 0f);
        Quaternion worldRot = cam.rotation * (Quaternion.Inverse(headRot) * eyeRotTracking);
        Vector3 dir = worldRot * Vector3.forward;
        Assert.AreEqual(1f, dir.magnitude, 1e-5f);
        Vector3 inHead = Quaternion.Inverse(cam.rotation) * dir;
        Vector3 expectedInHead = Quaternion.Euler(-5f, 8f, 0f) * Vector3.forward;
        Assert.AreEqual(0f, (inHead - expectedInHead).magnitude, 1e-4f, "origin and direction share one frame: head-frame gaze equals the eye-in-head rotation, whatever the rig yaw or the origin shift");
    }

    [Test]
    public void TrackingSpaceStats_HeadHeightDelta_IsRecordedOnlyWhenBothHeadsAreAvailable()
    {
        var s = Activator.CreateInstance(Tss);
        var note = Tss.GetMethod("Note", new[] { typeof(bool), typeof(string), typeof(string), typeof(bool), typeof(string), typeof(string), typeof(double?), typeof(double?) });
        note.Invoke(s, new object[] { true, "FloorLevel", "Device", true, "OpenXR", "OpenXR", (double?)1.2514, (double?)-0.11 });
        note.Invoke(s, new object[] { true, "FloorLevel", "Device", true, "OpenXR", "OpenXR", (double?)1.2614, (double?)-0.10 });
        note.Invoke(s, new object[] { true, "FloorLevel", "Device", true, "OpenXR", "OpenXR", (double?)null, (double?)-0.10 });
        Assert.AreEqual(3L, Get<long>(s, "Probes")); Assert.AreEqual(2L, Get<long>(s, "ProbesWithBothHeads"));
        Assert.AreEqual("differ", Get<string>(s, "Relation"));
        Vector3 mean = Get<object>(s, "HeadDelta").Mean();
        Assert.AreEqual(1.3614f, mean.y, 1e-3f, "the same head node read from two origins differs by the origin height: the independent cross-check of the provider-minus-anchor offset");
    }
}

// ================================================================================== G1 v13: the rendered view frame, heights above ground

public class ViewFrameTests
{
    readonly List<GameObject> _objs = new List<GameObject>();
    [TearDown] public void TearDown() { foreach (var o in _objs) if (o != null) UnityEngine.Object.DestroyImmediate(o); _objs.Clear(); }

    GameObject G(string name, Transform parent = null) { var g = new GameObject(name); _objs.Add(g); if (parent != null) g.transform.SetParent(parent, false); return g; }

    // A walkable ground surface (box whose TOP is at groundY) on the Default layer.
    GameObject Ground(float groundY, Vector3 centerXZ, string name = "Ground")
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube); _objs.Add(g); g.name = name;
        g.transform.position = new Vector3(centerXZ.x, groundY - 0.5f, centerXZ.z); g.transform.localScale = new Vector3(200f, 1f, 200f);
        return g;
    }

    sealed class Xr { public Transform baseT, centre, left, right, camOffset, view; }

    // XR Origin base on the ground; an OVR-like tree (CenterEye + eye anchors, NO camera offset) and an XRI-like tree (Camera Floor Offset -> Main Camera). Both follow the SAME tracked head
    // (headLocal = pose relative to the tracking origin). cameraOffsetY is a TEST input standing for whatever the scene configures; it is never read from production code.
    Xr Build(Vector3 basePos, float yawDeg, Vector3 headLocal, float pitchDeg, float cameraOffsetY, float halfIpd = 0.0337f)
    {
        var x = new Xr();
        x.baseT = G("XR Origin").transform; x.baseT.SetPositionAndRotation(basePos, Quaternion.Euler(0f, yawDeg, 0f));
        var track = G("TrackingSpace", G("OVRCameraRig", x.baseT).transform).transform;
        Quaternion rot = Quaternion.Euler(pitchDeg, 0f, 0f);
        x.centre = G("CenterEyeAnchor", track).transform; x.centre.localPosition = headLocal; x.centre.localRotation = rot;
        x.left = G("LeftEyeAnchor", track).transform; x.left.localPosition = headLocal + rot * new Vector3(-halfIpd, 0f, 0f); x.left.localRotation = rot;
        x.right = G("RightEyeAnchor", track).transform; x.right.localPosition = headLocal + rot * new Vector3(+halfIpd, 0f, 0f); x.right.localRotation = rot;
        x.camOffset = G("Camera Offset", x.baseT).transform; x.camOffset.localPosition = new Vector3(0f, cameraOffsetY, 0f);
        x.view = G("Main Camera", x.camOffset).transform; x.view.localPosition = headLocal; x.view.localRotation = rot;
        return x;
    }

    static object ProbeOf(Xr x, int groundMask = 1)
    {
        var p = Activator.CreateInstance(T("ViewFrameProbe"));
        Set(p, "View", x.view); Set(p, "CentreEye", x.centre); Set(p, "LeftEye", x.left); Set(p, "RightEye", x.right); Set(p, "GroundMask", groundMask);
        return p;
    }

    static object Read(object probe, out bool ok)
    {
        var args = new object[] { null };
        ok = (bool)probe.GetType().GetMethod("Read").Invoke(probe, args);
        return args[0];
    }

    [Test]
    public void FloorMode_DifferentParticipantHeights_GiveDifferentHmdWorldY_AndGroundRelativeHeightsEqualThePose_OnAnyGroundElevation()
    {
        foreach (float groundY in new[] { 168.0f, 190.5f, 12.25f })
        {
            var ground = Ground(groundY, new Vector3(2000f, 0f, 500f));
            foreach (float tracked in new[] { 1.52f, 1.68f, 1.83f })   // three different participants, as the runtime would report them relative to its floor
            {
                var x = Build(new Vector3(2000f, groundY, 500f), 20f, new Vector3(0.1f, tracked, -0.05f), 0f, cameraOffsetY: 0f);   // Floor mode: Camera Floor Offset = 0
                Physics.SyncTransforms();
                var p = ProbeOf(x); var s = Read(p, out bool ok);
                Assert.IsTrue(ok);
                Assert.AreEqual(groundY + tracked, Get<Vector3>(s, "hmdPos").y, 1e-4f, "world Y follows the participant's tracked height");
                Assert.IsTrue(Get<bool>(s, "groundOk")); Assert.AreEqual(groundY, Get<float>(s, "groundY"), 1e-4f);
                Assert.AreEqual(tracked, Get<Vector3>(s, "hmdPos").y - Get<float>(s, "groundY"), 1e-4f, "height above ground = tracked height, on any virtual ground elevation");
                Assert.AreEqual(0f, Get<Vector3>(s, "viewMinusCentre").magnitude, 1e-4f, "no Camera Floor Offset in Floor mode: the view and the OVR centre eye coincide");
            }
            UnityEngine.Object.DestroyImmediate(ground);
        }
    }

    [Test]
    public void DeviceMode_FixedCameraOffset_MakesEveryParticipantTheSameHeight_TheProblemThisAuditFound()
    {
        // Device (eye-level) tracking: the runtime pose is relative to the recentre point, so a tall and a short participant both report ~0 and the configured offset decides the rendered height.
        const float configuredOffset = 1.25f;   // test value standing for the scene's Camera Floor Offset; not used by production code
        Ground(100f, Vector3.zero);
        var tall = Build(new Vector3(0f, 100f, 0f), 0f, new Vector3(0f, 0.00f, 0f), 0f, configuredOffset);
        var shortP = Build(new Vector3(0f, 100f, 0f), 0f, new Vector3(0f, 0.00f, 0f), 0f, configuredOffset);
        Physics.SyncTransforms();
        float hTall = Get<Vector3>(Read(ProbeOf(tall), out _), "hmdPos").y - 100f, hShort = Get<Vector3>(Read(ProbeOf(shortP), out _), "hmdPos").y - 100f;
        Assert.AreEqual(hTall, hShort, 1e-5f, "two physically different participants are rendered at the same height");
        Assert.AreEqual(configuredOffset, hTall, 1e-4f, "...and that height is the configured offset");
        // the OVR anchor tree misses the offset: the gap is exactly the Camera Floor Offset
        var s = Read(ProbeOf(tall), out _);
        Assert.AreEqual(configuredOffset, Get<Vector3>(s, "viewMinusCentre").y, 1e-5f);
        Assert.AreEqual(0f, Get<Vector3>(s, "viewMinusCentre").x, 1e-5f);
    }

    [Test]
    public void EyeHeightSource_IsDecidedFromLiveXrFacts_Only()
    {
        Type ft = T("XrOriginFacts"); Type es = T("EyeHeightSource");
        object F(bool present, string cur, bool hasObj, float y) { object f = Activator.CreateInstance(ft); void S(string n, object v) => ft.GetField(n).SetValue(f, v); S("OriginPresent", present); S("CurrentMode", cur); S("RequestedMode", "NotSpecified"); S("HasOffsetObject", hasObj); S("OffsetObjectLocalY", y); return f; }
        string D(object f) => (string)CallStatic(es, "Decide", f, 1e-3f);
        Assert.AreEqual("fixed_camera_y_offset_not_participant_specific", D(F(true, "Device", true, 1.25f)));
        Assert.AreEqual("runtime_floor_referenced", D(F(true, "Floor", true, 0f)));
        Assert.AreEqual("runtime_floor_referenced", D(F(true, "Floor", false, 0f)));
        Assert.AreEqual("rig_pose_only_no_xr_origin", D(F(false, "", false, 0f)));
        Assert.AreEqual("indeterminate", D(F(true, "Floor", true, 0.7f)), "Floor mode WITH a non-zero offset would add a duplicate height: not a clean floor reference");
        Assert.AreEqual("indeterminate", D(F(true, "Device", true, 0f)));
        StringAssert.StartsWith("fixed_camera_y_offset_not_participant_specific", D(F(true, "", true, 1.25f)));
    }

    [Test]
    public void HeadLocalEyeAnchors_AreIndependentOfWorldHeight_AndSymmetric()
    {
        foreach (float pitch in new[] { 0f, 25f, -30f })
        foreach (float yaw in new[] { 0f, 77f, -140f })
        foreach (float height in new[] { 1.3f, 1.75f })
        {
            Ground(0f, Vector3.zero);
            var x = Build(new Vector3(40f, 0f, -12f), yaw, new Vector3(0.2f, height, 0.1f), pitch, cameraOffsetY: 0.9f);
            Physics.SyncTransforms();
            var s = Read(ProbeOf(x), out bool ok); Assert.IsTrue(ok);
            Vector3 l = Get<Vector3>(s, "leftLocal"), r = Get<Vector3>(s, "rightLocal");
            Assert.AreEqual(-0.0337f, l.x, 1e-4f); Assert.AreEqual(+0.0337f, r.x, 1e-4f); Assert.AreEqual(0f, l.y, 1e-4f, "head-local eye Y stays ~0 whatever the world height"); Assert.AreEqual(0f, r.y, 1e-4f); Assert.AreEqual(0f, l.z, 1e-4f);
            Vector3 lw = Get<Vector3>(s, "leftWorld"), rw = Get<Vector3>(s, "rightWorld");
            Assert.AreEqual(0.0674f, (rw - lw).magnitude, 1e-4f, "eye separation (IPD) is preserved in the view frame");
            Assert.AreEqual(Get<Vector3>(s, "hmdPos").y, (lw.y + rw.y) * 0.5f, 1e-4f, "eye world heights straddle the HMD reference");
            foreach (var o in new List<GameObject>(_objs)) { if (o != null) UnityEngine.Object.DestroyImmediate(o); } _objs.Clear();
        }
    }

    [Test]
    public void ProviderFrame_AgreesWithTheViewFrameAnchors_AndDiffersFromTheRawRigAnchorsByExactlyTheCameraOffset()
    {
        // OVREyeGaze converts with Camera.main = the VIEW camera: world = view.l2w * (headRef^-1 * eyePose). With the eye pose given consistently with the tracked head, the provider lands at view + head-local.
        const float off = 1.25f;
        Ground(0f, Vector3.zero);
        var x = Build(new Vector3(5f, 0f, 5f), 33f, new Vector3(0.05f, -0.06f, 0.02f), 12f, off);
        Physics.SyncTransforms();
        var s = Read(ProbeOf(x), out _);
        Matrix4x4 viewL2W = x.view.localToWorldMatrix;
        Vector3 headRefPos = x.centre.localPosition; Quaternion headRefRot = x.centre.localRotation;   // tracking-space head pose
        Vector3 eyeTracking = x.left.localPosition;                                                         // the same eye in tracking space
        Vector3 provider = (Vector3)CallStatic(T("EyeGazeOrigin"), "SdkWorldSpaceOrigin", viewL2W, headRefPos, headRefRot, eyeTracking);
        Vector3 rigAnchor = Get<Vector3>(s, "leftRigWorld"), viewAnchor = Get<Vector3>(s, "leftWorld");
        Assert.AreEqual(off, (provider - rigAnchor).y, 1e-4f, "provider - RAW rig anchor = the Camera Floor Offset (the 1.36144 m of the audit, here with the test's own value)");
        Assert.AreEqual(0f, (provider - viewAnchor).magnitude, 1e-4f, "provider - view-frame anchor ~ 0: the frames agree");
        var args = new object[] { provider, (Vector3?)viewAnchor, 0.12f, null, null };
        Assert.AreEqual("Provider", T("EyeGazeOrigin").GetMethod("Resolve").Invoke(null, args).ToString(), "validated against the view-frame anchor the provider origin is accepted");
        var args2 = new object[] { provider, (Vector3?)rigAnchor, 0.12f, null, null };
        Assert.AreEqual("EyeAnchor", T("EyeGazeOrigin").GetMethod("Resolve").Invoke(null, args2).ToString(), "validated against the raw rig anchor it is (wrongly) rejected - the v10-v12 behaviour");
    }

    [Test]
    public void MovingTheXrOriginBase_KeepsTheTrackedHeadAndRecomputesHeightsAgainstTheNewGround()
    {
        var gA = Ground(168f, new Vector3(0f, 0f, 0f), "GroundA"); var gB = Ground(190f, new Vector3(1000f, 0f, 0f), "GroundB");
        var x = Build(new Vector3(0f, 168f, 0f), 0f, new Vector3(0f, 1.7f, 0f), 0f, cameraOffsetY: 0f);
        Physics.SyncTransforms();
        var s0 = Read(ProbeOf(x), out _);
        Assert.AreEqual(1.7f, Get<Vector3>(s0, "hmdPos").y - Get<float>(s0, "groundY"), 1e-4f);
        // teleport: only the BASE moves / turns (as ExperimentManager.TeleportPlayerTo and XRI teleports do); the camera is never moved directly
        x.baseT.SetPositionAndRotation(new Vector3(1000f, 190f, 0f), Quaternion.Euler(0f, 135f, 0f));
        Physics.SyncTransforms();
        var s1 = Read(ProbeOf(x), out _);
        Assert.AreEqual(Get<Vector3>(s0, "leftLocal").x, Get<Vector3>(s1, "leftLocal").x, 1e-3f, "head-local eyes unchanged by the teleport (float32 at ~1000 m coordinates)");
        Assert.AreEqual(190f, Get<float>(s1, "groundY"), 1e-4f, "the new virtual ground is found below the head");
        Assert.AreEqual(1.7f, Get<Vector3>(s1, "hmdPos").y - Get<float>(s1, "groundY"), 1e-4f, "the tracked height survives the teleport");
        Assert.AreEqual(x.camOffset.position.y, x.baseT.position.y, 1e-5f);
    }

    [Test]
    public void GroundHeight_IgnoresNpcAndParticipantColliders_AndIsUnavailableWithoutGround()
    {
        Ground(10f, Vector3.zero);
        var npc = GameObject.CreatePrimitive(PrimitiveType.Cube); _objs.Add(npc); npc.tag = "Npc"; npc.transform.position = new Vector3(0f, 11.5f, 0f); npc.transform.localScale = new Vector3(2f, 1f, 2f);   // an NPC standing under the head
        var x = Build(new Vector3(0f, 10f, 0f), 0f, new Vector3(0f, 1.7f, 0f), 0f, 0f);
        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule); _objs.Add(body); body.transform.SetParent(x.baseT, false); body.transform.localPosition = new Vector3(0f, 0.9f, 0f);   // the participant's own capsule
        Physics.SyncTransforms();
        var s = Read(ProbeOf(x), out _);
        Assert.IsTrue(Get<bool>(s, "groundOk")); Assert.AreEqual(10f, Get<float>(s, "groundY"), 1e-4f, "neither the NPC nor the participant's body is ground");
        var sNone = Read(ProbeOf(x, groundMask: 0), out _);
        Assert.IsFalse(Get<bool>(sNone, "groundOk"), "no ground layer configured: the height is unavailable, never guessed");
        var farUp = Build(new Vector3(0f, 500f, 0f), 0f, new Vector3(0f, 1.7f, 0f), 0f, 0f); Physics.SyncTransforms();
        Assert.IsFalse(Get<bool>(Read(ProbeOf(farUp), out _), "groundOk"), "ground farther than the ray length: unavailable");
    }

    [Test]
    public void ProductionCode_ContainsNoEyeHeightConstant()
    {
        string dir = Path.Combine(Application.dataPath, "Scripts");
        foreach (string f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f) == "TargetBuildingPreviewGenerator.cs") continue;   // editor thumbnail camera suggestion for building previews: not a participant measurement
            foreach (string raw in File.ReadAllLines(f))
            {
                string code = raw; int c = code.IndexOf("//", StringComparison.Ordinal); if (c >= 0) code = code.Substring(0, c);
                Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(code, @"1\.3614\d*f?\b"), f + " has a 1.3614 literal in code: " + raw.Trim());
                Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(code, @"(?i)eyeHeight\s*=\s*[0-9]"), f + " assigns a literal eye height: " + raw.Trim());
            }
        }
    }
}

// ================================================================================== v14: the per-eye pipeline (pure geometry + SDK source facts)

public class EyePipelineTests
{
    static readonly Type Math = T("EyePipelineMath");

    // Tracking-space rig: the SAME physical head/eye in OVRPlugin tracking space. originShift moves the whole OVRPlugin space (a different tracking origin than Unity's).
    static void Eye(Vector3 headPos, Quaternion headRot, Vector3 eyeLocalInHead, Vector3 originShift, out Vector3 headRef, out Vector3 eyeTracking)
    {
        headRef = headPos + originShift;
        eyeTracking = headPos + headRot * eyeLocalInHead + originShift;
    }

    static (Vector3 pos, Quaternion rot) HeadSpace(Vector3 headRef, Quaternion headRot, Vector3 eyeTracking, Quaternion eyeRot)
    {
        var a = new object[] { headRef, headRot, eyeTracking, eyeRot, null, null };
        Math.GetMethod("ToHeadSpace").Invoke(null, a);
        return ((Vector3)a[4], (Quaternion)a[5]);
    }

    [Test]
    public void HeadSpaceThenPlacedAtTheRenderedCamera_EqualsTheSdkWorldSpaceFormula_AndIsImmuneToATrackingOriginMismatch()
    {
        // The rendered camera (Unity space, Camera Offset included) and the OVRPlugin-space head/eye poses of the SAME head. cameraOffsetY is a test input.
        Quaternion headRot = Quaternion.Euler(14f, 62f, 0f);
        Vector3 basePos = new Vector3(2000f, 168f, 500f); Quaternion baseRot = Quaternion.Euler(0f, 35f, 0f);
        float cameraOffsetY = 1.1f; Vector3 headInUnity = new Vector3(0.12f, 0.25f, -0.2f);
        Matrix4x4 viewL2W = Matrix4x4.TRS(basePos, baseRot, Vector3.one) * Matrix4x4.TRS(new Vector3(0f, cameraOffsetY, 0f) + headInUnity, headRot, Vector3.one);
        Vector3 viewPos = viewL2W.MultiplyPoint3x4(Vector3.zero); Quaternion viewRot = viewL2W.rotation;
        Vector3 eyeLocal = new Vector3(-0.031f, 0.002f, 0.012f);
        foreach (Vector3 shift in new[] { Vector3.zero, new Vector3(0f, 1.3f, 0f), new Vector3(0.4f, -0.8f, 2f) })   // OVRPlugin origin = Unity origin, or displaced by an arbitrary vector
        {
            Eye(headInUnity, headRot, eyeLocal, shift, out Vector3 headRef, out Vector3 eyeTr);
            var hs = HeadSpace(headRef, headRot, eyeTr, headRot * Quaternion.Euler(3f, -5f, 0f));
            Assert.AreEqual(0f, (hs.pos - eyeLocal).magnitude, 1e-4f, "head-space eye position is the eye relative to the head: no origin / offset / world in it");
            Vector3 composed = (Vector3)Math.GetMethod("ComposeOrigin").Invoke(null, new object[] { viewPos, viewRot, hs.pos });
            Vector3 sdk = (Vector3)CallStatic(T("EyeGazeOrigin"), "SdkWorldSpaceOrigin", viewL2W, headRef, headRot, eyeTr);
            Assert.AreEqual(0f, (composed - sdk).magnitude, 2e-3f, "composition == OVREyeGaze WorldSpace = Camera.main.localToWorld * (headRef^-1 * eyePose)  (float32 at ~2000 m)");
            Vector3 expected = viewPos + viewRot * eyeLocal;
            Assert.AreEqual(0f, (composed - expected).magnitude, 2e-3f, "the eye sits where the rendered head puts it, whatever the OVRPlugin origin was");
            // the raw route (tracking-space pose through a tracking-to-world matrix) inherits the mismatch
            Vector3 rawRoute = Matrix4x4.TRS(basePos, baseRot, Vector3.one).MultiplyPoint3x4(new Vector3(0f, cameraOffsetY, 0f) + eyeTr);
            Assert.AreEqual(shift.magnitude, (rawRoute - expected).magnitude, 5e-3f, "raw tracking-space route is off by exactly the origin mismatch: why head-space composition is the recorded path");
        }
    }

    [Test]
    public void CameraOffset_IsAppliedExactlyOnce_ByTheRenderedCameraPose()
    {
        const float off = 1.17f;
        Matrix4x4 viewL2W = Matrix4x4.TRS(new Vector3(0f, 100f + off + 0.3f, 0f), Quaternion.identity, Vector3.one);   // base 100 + Camera Offset + tracked 0.3
        Vector3 viewPos = viewL2W.MultiplyPoint3x4(Vector3.zero);
        Vector3 eyeHead = new Vector3(0.03f, 0f, 0f);
        Vector3 once = (Vector3)Math.GetMethod("ComposeOrigin").Invoke(null, new object[] { viewPos, Quaternion.identity, eyeHead });
        Assert.AreEqual(100f + off + 0.3f, once.y, 1e-3f, "base + offset + tracked height, the offset counted once (it is already inside the camera pose)");
        Assert.Greater(System.Math.Abs(once.y - (100f + 2 * off + 0.3f)), off * 0.9f, "a second application of the offset would be a visible error");
    }

    [Test]
    public void HeadSpaceEye_IsInvariantToTeleportRecenterAndHeadRotation_ButTheComposedWorldRayFollowsTheHead()
    {
        Vector3 eyeLocal = new Vector3(0.0335f, -0.001f, 0.011f); Vector3 dirLocal = Quaternion.Euler(-4f, 7f, 0f) * Vector3.forward;
        var worldByYaw = new Dictionary<float, Vector3>();
        foreach (float yaw in new[] { 0f, 77f, -140f })
        foreach (Vector3 newBase in new[] { new Vector3(0f, 0f, 0f), new Vector3(1900f, 190f, 40f) })   // teleport / recentre: the tracking pose of the head is a different one, the eye relative to it is not
        {
            Quaternion headRot = Quaternion.Euler(10f, yaw, 0f); Vector3 headPos = new Vector3(0.3f, 1.4f, -0.2f) + newBase * 0.0f;
            Eye(headPos, headRot, eyeLocal, new Vector3(0f, 0.9f, 0f), out Vector3 headRef, out Vector3 eyeTr);
            var hs = HeadSpace(headRef, headRot, eyeTr, headRot * Quaternion.LookRotation(dirLocal));
            Assert.AreEqual(0f, (hs.pos - eyeLocal).magnitude, 1e-4f);
            Assert.AreEqual(0f, ((hs.rot * Vector3.forward) - dirLocal.normalized).magnitude, 1e-4f);
            Quaternion viewRot = Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(10f, 0f, 0f);
            Vector3 dirWorld = (Vector3)Math.GetMethod("ComposeDirection").Invoke(null, new object[] { viewRot, hs.rot * Vector3.forward });
            Assert.AreEqual(1f, dirWorld.magnitude, 1e-5f, "world direction stays a unit vector");
            Assert.AreEqual(0f, Vector3.Angle(dirWorld, viewRot * dirLocal), 1e-3f, "and follows the head rotation");
            if (worldByYaw.TryGetValue(yaw, out Vector3 same)) Assert.AreEqual(0f, Vector3.Angle(dirWorld, same), 1e-3f, "teleporting does not change the head-space eye");
            worldByYaw[yaw] = dirWorld;
        }
        Assert.Greater(Vector3.Angle(worldByYaw[0f], worldByYaw[77f]), 30f, "turning the head turns the world ray");
    }

    [Test]
    public void AngleAndIpdHelpers_AreHonest()
    {
        Assert.AreEqual(90f, (float)Math.GetMethod("AngleDeg").Invoke(null, new object[] { Vector3.forward, Vector3.right }), 1e-4f);
        Assert.IsTrue(float.IsNaN((float)Math.GetMethod("AngleDeg").Invoke(null, new object[] { Vector3.zero, Vector3.right })), "a zero vector has no direction");
        bool U(float v) => (bool)Math.GetMethod("IsUsableIpd").Invoke(null, new object[] { v });
        Assert.IsFalse(U(0f)); Assert.IsFalse(U(float.NaN)); Assert.IsFalse(U(0.3f)); Assert.IsTrue(U(0.058f)); Assert.IsTrue(U(0.0674f)); Assert.IsTrue(U(0.072f));
    }

    [Test]
    public void ProductionCode_HasNoHardcodedIpdOrEyeOffset()
    {
        string dir = Path.Combine(Application.dataPath, "Scripts");
        var rx = new System.Text.RegularExpressions.Regex(@"\b0\.0(33[0-9]*|67[0-9]*|6[0-9])f?\b");
        foreach (string f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(f);
            foreach (string raw in File.ReadAllLines(f))
            {
                string code = raw; int c = code.IndexOf("//", StringComparison.Ordinal); if (c >= 0) code = code.Substring(0, c);
                if (!code.Contains("Vector3(") && !code.ToLowerInvariant().Contains("ipd")) continue;
                Assert.IsFalse(rx.IsMatch(code) && System.Text.RegularExpressions.Regex.IsMatch(code, @"(?i)(ipd|eye|pupil)"), name + " has a literal IPD / eye offset: " + raw.Trim());
            }
        }
    }

    static string SdkFile(string rel)
    {
        string p = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "PackageCache", "com.meta.xr.sdk.core@201.0.0", "Scripts", rel));
        if (!File.Exists(p)) Assert.Ignore("Meta XR SDK Core 201.0.0 source not found at " + p);
        return File.ReadAllText(p);
    }

    [Test]
    public void SdkSource_DisableEyeAnchorCameras_OnlyTogglesCameras_AnchorsKeepUpdating()
    {
        string src = SdkFile("OVRCameraRig.cs");
        int a = src.IndexOf("protected virtual void UpdateAnchors(", StringComparison.Ordinal), b = src.IndexOf("protected virtual void OnBeforeRenderCallback", StringComparison.Ordinal);
        Assert.Greater(a, 0); Assert.Greater(b, a);
        string update = src.Substring(a, b - a);
        StringAssert.DoesNotContain("disableEyeAnchorCameras", update, "the anchor update does not look at the flag");
        StringAssert.Contains("centerEyeAnchor.localPosition", update); StringAssert.Contains("leftEyeAnchor.localPosition", update); StringAssert.Contains("rightEyeAnchor.localPosition", update);
        int d = src.IndexOf("if (disableEyeAnchorCameras)", StringComparison.Ordinal);
        Assert.Greater(d, 0); string block = src.Substring(d, 260);
        StringAssert.Contains("_centerEyeCamera.enabled = false", block); StringAssert.Contains("_leftEyeCamera.enabled = false", block);
        StringAssert.Contains("UpdatedAnchors", src);
    }

    [Test]
    public void SdkSource_AnchorsAndHeadSpaceUseOvrPluginPoses_WhenOvrPluginIsInitialised_AndEyeGazeModesAreAsDocumented()
    {
        string common = SdkFile("OVRCommon.cs");
        int p = common.IndexOf("case NodeStatePropertyType.Position:", StringComparison.Ordinal); Assert.Greater(p, 0);
        string pos = common.Substring(p, 420);
        StringAssert.Contains("OVRManager.XRDevice.Oculus", pos); StringAssert.Contains("OVRPlugin.GetNodePose", pos);
        string mgr = SdkFile("OVRManager.cs");
        int q = mgr.IndexOf("void SetCurrentXRDevice()", StringComparison.Ordinal); Assert.Greater(q, 0);
        StringAssert.Contains("if (OVRPlugin.initialized)", mgr.Substring(q, 400)); StringAssert.Contains("loadedXRDevice = XRDevice.Oculus", mgr.Substring(q, 400));
        string eg = SdkFile(Path.Combine("Movement", "OVREyeGaze.cs"));
        StringAssert.Contains("TrackingSpace: Track eye is relative to OVRCameraRig. This is raw pose information from VR tracking space.", eg);
        StringAssert.Contains("pose.ToHeadSpacePose()", eg); StringAssert.Contains("pose.ToWorldSpacePose(Camera.main)", eg);
        StringAssert.Contains("OVRPlugin.GetEyeGazesState(OVRPlugin.Step.Render", eg);
    }
}

// Tiny reflection helpers for the nested Welford3 statistics (a public nested class of the production type).
static class WelfordExt
{
    public static Vector3 Mean(this object w) => Get<Vector3>(w, "Mean");
    public static Vector3 Std(this object w) => Get<Vector3>(w, "Std");
}

// ================================================================================== integration (production components, real files)

public partial class NpcLifecycleTests
{
    static JObject ManifestFile(Rig rig) => JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
    static double Num(JToken t) => t.Type == JTokenType.String ? double.Parse((string)t, CultureInfo.InvariantCulture) : (double)t;
    static readonly string[] AllCsv = { "events.csv", "gaze.csv", "trajectory.csv", "npc_state.csv", "npc_events.csv", "npc_lifetimes.csv", "npc_profiles.csv" };
    Dictionary<string, long> Sizes(Rig rig) => AllCsv.ToDictionary(f => f, f => File.Exists(rig.File(f)) ? new FileInfo(rig.File(f)).Length : -1L);

    static void SetProbe(object writer, Delegate d) => writer.GetType().GetField("CloseProbe").SetValue(writer, d);
    static Delegate ProbeOf(Action<object> a)
    {
        Type wt = T("CaptureCsvWriter"); var p = System.Linq.Expressions.Expression.Parameter(wt, "w");
        return System.Linq.Expressions.Expression.Lambda(typeof(Action<>).MakeGenericType(wt),
            System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(a), System.Linq.Expressions.Expression.Convert(p, typeof(object))), p).Compile();
    }
    static Delegate ThrowingProbe(string message)
    {
        Type wt = T("CaptureCsvWriter"); var p = System.Linq.Expressions.Expression.Parameter(wt, "w");
        var body = System.Linq.Expressions.Expression.Throw(System.Linq.Expressions.Expression.Constant(new InvalidOperationException(message)));
        return System.Linq.Expressions.Expression.Lambda(typeof(Action<>).MakeGenericType(wt), body, p).Compile();
    }

    // ---------------------------------------------------------------------------------------------- M2

    [UnityTest]
    public IEnumerator M2_FinalManifest_ReportsClosed_WithARealTimestamp_AfterEveryStreamClosed_AndIsSealed()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 2, maxAgents = 2, npcCaptureEnabled = true, npcSpeed = 1f }, (r, c, h) => { rig = r; rec = c; head = h; });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return WaitUntil(() => Live(rig) >= 1, 10f, "an NPC");
        yield return new WaitForSecondsRealtime(0.3f);
        Call(rig.dcm, "AppendNpcEventLine", "npc_spawn", CaptureClockNow(), 1700000000000L, "NPC-QUEUED", "NPC-QUEUED-S1", 1, Vector3.zero, 0f, false, Vector3.zero, "", "", "queued_before_end", -1, "");
        Assert.AreEqual("Open", Get<object>(rig.dcm, "CaptureState").ToString());

        Call(rig.dcm, "OnSessionEnded");
        double nowAfter = CaptureClockNow();

        // The recorded combination of the earlier defect: every CSV stream closed, manifest still 'Closing' / -1. After a successful finalization it cannot occur.
        foreach (var w in new[] { "_events", "_traj", "_npcState", "_npcEvents", "_npcLifetimes", "_npcProfiles" })
            Assert.IsTrue(Get<bool>(Get<object>(rig.dcm, w), "IsClosed"), w + " closed");
        Assert.IsTrue(Get<bool>(Get<object>(rec, "Writer"), "IsClosed"), "the gaze stream is closed too");
        var m = ManifestFile(rig);
        Assert.AreEqual("Closed", (string)m["capture_lifecycle_state"], "REGRESSION: the persisted state must not stay Closing once the streams are closed");
        double closedT = Num(m["capture_closed_t_abs"]);
        Assert.AreNotEqual(-1.0, closedT, "REGRESSION: the closure timestamp must not stay -1");
        Assert.GreaterOrEqual(closedT, 0.0);
        Assert.LessOrEqual(closedT, nowAfter + 1e-6, "same clock as t_abs: not a future / Unix time");

        var ev = Events(rig); var last = ev.Rows[ev.Rows.Count - 1];
        Assert.AreEqual("session_end", ev.Get(last, "event_type"));
        double sessionEndT = D(ev.Get(last, "t_abs"));
        Assert.GreaterOrEqual(closedT, sessionEndT, "the closure is chronologically after the terminal event");
        Assert.GreaterOrEqual(closedT, Num(m["t_abs_at_end"]) - 1e-6);
        Assert.AreEqual("end_confirmed", (string)m["end_reason"], "the end reason is the session's, not overwritten");
        Assert.AreEqual(0, ((JArray)m["capture_streams_failed"]).Count);
        var closedNames = ((JArray)m["capture_streams_closed"]).Select(t => (string)t).ToList();
        CollectionAssert.IsSubsetOf(new[] { "events", "trajectory", "npc_events", "gaze" }, closedNames);
        Assert.AreEqual("", (string)m["capture_finalization_error"]);
        Assert.IsTrue(NpcEvents(rig).Rows.Any(r => NpcEvents(rig).Get(r, "npc_id") == "NPC-QUEUED"), "records accepted before the close were flushed before the terminal event");
        Assert.AreEqual(1, EventTypes(rig).Count(t => t == "session_end"));

        // sealed: late metadata callbacks neither change the manifest nor touch a closed stream
        string manifestBefore = File.ReadAllText(rig.File("session_manifest.json")); var sizesBefore = Sizes(rig);
        Call(rig.dcm, "OnApplicationFocus", false); Call(rig.dcm, "OnApplicationPause", true); Call(rig.dcm, "OnApplicationPause", false);
        Call(rig.dcm, "WriteManifestEnd", "late"); Call(rig.dcm, "WriteManifestCheckpoint", "late"); Call(rig.dcm, "WriteManifest");
        Call(rig.dcm, "OnSessionEnded"); Call(rig.dcm, "OnApplicationQuit"); Call(rig.dcm, "OnSessionEnded");
        yield return new WaitForSecondsRealtime(0.3f);
        Assert.AreEqual(manifestBefore, File.ReadAllText(rig.File("session_manifest.json")), "late app_focus / app_pause / duplicate finalization never alter the final manifest (state, end reason, closure time)");
        Assert.AreEqual(closedT, Num(ManifestFile(rig)["capture_closed_t_abs"]), "duplicate finalization does not move the closure timestamp");
        CollectionAssert.AreEqual(sizesBefore, Sizes(rig), "no stream accepted anything after the close");
        Assert.AreEqual(1, EventTypes(rig).Count(t => t == "session_end"), "no duplicate terminal event");
    }

    [UnityTest]
    public IEnumerator M2_DuringClosing_OtherThreadsAreRefused_AndTheFinalManifestWritesNothingToTheClosedStreams()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = true, npcSpeed = 1f }, (r, c, h) => { rig = r; rec = c; head = h; });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(0.4f);

        long lateBefore = 0, lateDuring = -1; string stateDuring = null; Dictionary<string, long> sizesAtLastClose = null; long eventsSizeAtProbe = -1, eventsSizeAfterRefused = -2;
        // the probe runs INSIDE the close step: state is Closing, session_end is already written; the gate must refuse a foreign thread, and the final manifest has not been written yet
        var trajWriter = Get<object>(rig.dcm, "_traj");
        var probe = new Action<object>(w =>
        {
            stateDuring = Get<object>(rig.dcm, "CaptureState").ToString();
            lateBefore = Get<long>(rig.dcm, "LateEventsRejected");
            eventsSizeAtProbe = new FileInfo(rig.File("events.csv")).Length;
            var th = new Thread(() => { try { WriteEvt(rig.dcm, "foreign_thread_event", 99999.0); } catch { } }); th.Start(); th.Join(2000);
            lateDuring = Get<long>(rig.dcm, "LateEventsRejected");
            eventsSizeAfterRefused = new FileInfo(rig.File("events.csv")).Length;
        });
        SetProbe(trajWriter, ProbeOf(probe));
        var snapshotProbe = new Action<object>(w => sizesAtLastClose = Sizes(rig));
        object lastClosed = Get<object>(rig.dcm, "_registry") ?? Get<object>(rig.dcm, "_npcProfiles");   // the last writer CloseSessionStreams closes: its probe sees the file sizes just before the final manifest is persisted
        SetProbe(lastClosed, ProbeOf(snapshotProbe));

        Call(rig.dcm, "OnSessionEnded");
        Assert.AreEqual("Closing", stateDuring, "while the streams are being closed the state is Closing");
        Assert.Greater(lateDuring, lateBefore, "an external submission during Closing was refused and counted");
        Assert.AreEqual(eventsSizeAtProbe, eventsSizeAfterRefused, "...and wrote nothing to events.csv");
        Assert.IsNotNull(sizesAtLastClose);
        CollectionAssert.AreEqual(sizesAtLastClose, Sizes(rig), "persisting the FINAL manifest wrote nothing to any experimental stream: they were closed before it");
        Assert.AreEqual("Closed", (string)ManifestFile(rig)["capture_lifecycle_state"]);
        Assert.IsFalse(EventTypes(rig).Contains("foreign_thread_event"));
    }

    [UnityTest]
    public IEnumerator M2_ASimulatedCloseFailure_IsNeverReportedAsClosed_AndKeepsTheOriginalEndReason()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = true, npcSpeed = 1f }, (r, c, h) => { rig = r; rec = c; head = h; });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(0.3f);
        var npcEvents = Get<object>(rig.dcm, "_npcEvents");
        SetProbe(npcEvents, ThrowingProbe("simulated close failure"));

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[DataCapture\] Session capture could NOT be closed cleanly"));
        Call(rig.dcm, "OnSessionEnded");

        Assert.AreEqual("Closing", Get<object>(rig.dcm, "CaptureState").ToString(), "a failed close must not become Closed");
        var m = ManifestFile(rig);
        Assert.AreEqual("Closing", (string)m["capture_lifecycle_state"]);
        Assert.AreEqual(-1.0, Num(m["capture_closed_t_abs"]), "no closure timestamp is invented");
        CollectionAssert.Contains(((JArray)m["capture_streams_failed"]).Select(t => (string)t).ToList(), "npc_events");
        CollectionAssert.Contains(((JArray)m["capture_streams_closed"]).Select(t => (string)t).ToList(), "events", "the streams that DID close are still reported");
        StringAssert.Contains("simulated close failure", (string)m["capture_finalization_error"]);
        Assert.AreEqual("end_confirmed", (string)m["end_reason"], "the original end reason survives");
        Assert.IsTrue(Get<bool>(npcEvents, "IsClosed"), "the stream whose close failed still refuses records");
        Assert.IsFalse(Get<bool>(npcEvents, "ClosedCleanly"));

        // no experimental record is accepted afterwards, and repeated finalization is a no-op
        var ev = EventTypes(rig); Assert.AreEqual("session_end", ev[ev.Length - 1]); Assert.AreEqual(1, ev.Count(t => t == "session_end"));
        var sizes = Sizes(rig); string manifest = File.ReadAllText(rig.File("session_manifest.json"));
        WriteEvt(rig.dcm, "teleport", CaptureClockNow()); Call(rig.dcm, "OnApplicationPause", true); Call(rig.dcm, "OnSessionEnded"); Call(rig.dcm, "OnApplicationQuit");
        yield return new WaitForSecondsRealtime(0.2f);
        CollectionAssert.AreEqual(sizes, Sizes(rig)); Assert.AreEqual(manifest, File.ReadAllText(rig.File("session_manifest.json")));
    }

    [UnityTest]
    public IEnumerator M2_ApplicationQuit_And_Destroy_BothPersistTheClosedState()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, r => rig = r);
        yield return null;
        Call(rig.dcm, "OnApplicationQuit");
        var m = ManifestFile(rig);
        Assert.AreEqual("Closed", (string)m["capture_lifecycle_state"]); Assert.AreEqual("app_quit", (string)m["end_reason"]);
        Assert.GreaterOrEqual(Num(m["capture_closed_t_abs"]), 0.0);
        Call(rig.dcm, "OnDestroy"); Call(rig.dcm, "OnSessionEnded");   // later termination paths change nothing
        Assert.AreEqual("app_quit", (string)ManifestFile(rig)["end_reason"]);

        Rig rig2 = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, r => rig2 = r);
        yield return null;
        UnityEngine.Object.Destroy(rig2.dcm);
        yield return null; yield return null;
        var m2 = ManifestFile(rig2);
        Assert.AreEqual("Closed", (string)m2["capture_lifecycle_state"]); Assert.AreEqual("on_destroy", (string)m2["end_reason"]);
        Assert.GreaterOrEqual(Num(m2["capture_closed_t_abs"]), 0.0);
    }

    // ---------------------------------------------------------------------------------------------- N2 (detour return, both Parts)

    List<string> Causes(Rig rig, string lifetimeId) { var n = NpcEvents(rig); return LifeEvents(n, lifetimeId).Where(r => n.Get(r, "event_type") == "npc_dest_change").Select(r => ValueField(n.Get(r, "value_str"), "cause")).ToList(); }
    List<List<string>> DestEvents(Rig rig, string lifetimeId) { var n = NpcEvents(rig); return LifeEvents(n, lifetimeId).Where(r => n.Get(r, "event_type") == "npc_dest_change").ToList(); }

    IEnumerator DetourNpc(bool part2, Action<Rig, Component, Component, NavMeshAgent> done)
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { part2 = part2, withUncertain = true, uncertainEnabled = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.5f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        Component pa = ActiveAgents(rig)[0]; var ub = pa.GetComponent(T("UncertainBehavior"));
        Set(ub, "detourDuration", 30f);
        // a detour point the NPC cannot reach by itself within the test (the test decides how the detour ends: forced arrival / forced timer / exhausted retries / never)
        Set(ub, "detourRadius", 9f); Set(ub, "minDetourDistance", 6f); Set(ub, "detourNavMeshSampleDistance", 4f); Set(ub, "detourSampleMaxAttempts", 60);
        foreach (string f in new[] { "pauseProbPerSec", "hesitateProbPerSec", "slowProbPerSec", "detourProbPerSec" }) Set(ub, f, 0f);   // only the detours this test forces
        yield return WaitUntil(() => Causes(rig, Lid(pa)).Contains("initial_assignment"), 10f, "the initial assignment");
        yield return new WaitForSecondsRealtime(0.3f);
        done(rig, pa, ub, pa.GetComponent<NavMeshAgent>());
    }

    // Starts a detour and ends it the requested way ("arrived" | "timer" | "retries_exhausted" | "none"). Returns once the resulting destination change had time to be sampled.
    IEnumerator RunDetour(Rig rig, Component pa, Component ub, NavMeshAgent nav, string how, Action beforeEnd = null)
    {
        Call(ub, "EnterDetour");
        Assert.AreEqual("detour", Get<string>(ub, "StateLabel"), "precondition: a detour point was accepted");
        yield return new WaitForSecondsRealtime(0.5f);
        if (how == "none") yield break;
        beforeEnd?.Invoke();
        if (how == "arrived") { nav.Warp(Get<Vector3>(ub, "detourTarget")); Call(ub, "OnMoveToArrivedForDetour"); }
        else if (how == "timer") Set(ub, "stateTimer", 0f);
        else if (how == "retries_exhausted")
        {
            int max = Get<int>(ub, "detourArrivalMaxRetries");
            for (int k = 0; k <= max + 1; k++) Call(ub, "OnMoveToArrivedForDetour");   // false arrivals: the NPC is far from the detour point
            Assert.AreEqual("pause", Get<string>(ub, "StateLabel"), "retries exhausted -> the production fallback is a pause");
            Set(ub, "stateTimer", 0f);
        }
        yield return new WaitForSecondsRealtime(0.8f);
    }

    // The common assertions of a verified return: the cause sequence, the end reason, ONE return row that goes back to the pre-detour target and keeps its identity, no duplicates over more ticks.
    IEnumerator AssertVerifiedReturn(Rig rig, Component pa, Component ub, string id, Vector3 destA, string targetIdA, string how)
    {
        Assert.AreEqual(1, Get<int>(ub, "DetoursCompleted")); Assert.AreEqual(how, Get<string>(ub, "LastDetourEndReason")); Assert.IsTrue(Get<bool>(ub, "LastDetourRestoredPreDetourTarget"));
        var nev = NpcEvents(rig); var rows = DestEvents(rig, id);
        CollectionAssert.AreEqual(new[] { "initial_assignment", "detour", "detour_return" }, rows.Select(r => ValueField(nev.Get(r, "value_str"), "cause")).ToArray(), "expected sequence");
        var ret = rows[2]; string v = nev.Get(ret, "value_str");
        StringAssert.Contains("detour_end=" + how, v); StringAssert.Contains("prev_dest=", v);
        Assert.Less(Math.Abs(D(nev.Get(ret, "dest_x")) - destA.x), 1.6, "the return goes back to the pre-detour destination"); Assert.Less(Math.Abs(D(nev.Get(ret, "dest_z")) - destA.z), 1.6);
        Assert.AreEqual(targetIdA, nev.Get(ret, "target_id"), "the destination's identity (target_id) is the same entry as before the detour");
        yield return new WaitForSecondsRealtime(0.7f);   // more sampler ticks
        Assert.AreEqual(3, DestEvents(rig, id).Count, "no duplicate return and no further change while nothing changes");
        Assert.AreEqual(1, Causes(rig, id).Count(c => c == "detour_return"), "one completion, one return event");
    }

    IEnumerator VerifiedReturnCase(bool part2, string how)
    {
        Rig rig = null; Component pa = null, ub = null; NavMeshAgent nav = null;
        yield return DetourNpc(part2, (r, p, u, n) => { rig = r; pa = p; ub = u; nav = n; });
        string id = Lid(pa); Vector3 destA = nav.destination; string targetIdA = Get<string>(pa, "CurrentTargetName");
        Assert.AreEqual(0, Get<int>(ub, "DetoursStarted"));
        yield return RunDetour(rig, pa, ub, nav, how);
        Assert.AreEqual(1, Get<int>(ub, "DetoursStarted"));
        yield return AssertVerifiedReturn(rig, pa, ub, id, destA, targetIdA, how);
        // an unrelated change afterwards is NOT a return
        CallTyped(pa.GetComponent(MoveType), "MoveTo", new[] { typeof(Vector3) }, destA + new Vector3(25f, 0f, -18f));
        yield return new WaitForSecondsRealtime(0.5f);
        Assert.AreEqual("other", Causes(rig, id).Last(), "a later unrelated change whose cause is unknown stays 'other'");
        Assert.AreEqual(1, Causes(rig, id).Count(c => c == "detour_return"));
    }

    [UnityTest] public IEnumerator Part3_DetourEndedByArrival_IsDetourReturn() { yield return VerifiedReturnCase(false, "arrived"); }
    [UnityTest] public IEnumerator Part3_DetourEndedByTimer_IsDetourReturn() { yield return VerifiedReturnCase(false, "timer"); }
    [UnityTest] public IEnumerator Part3_DetourEndedByExhaustedRetries_IsDetourReturn() { yield return VerifiedReturnCase(false, "retries_exhausted"); }
    [UnityTest] public IEnumerator Part2_DetourEndedByArrival_IsDetourReturn_NotItineraryTarget() { yield return VerifiedReturnCase(true, "arrived"); }
    [UnityTest] public IEnumerator Part2_DetourEndedByTimer_IsDetourReturn_NotItineraryTarget() { yield return VerifiedReturnCase(true, "timer"); }
    [UnityTest] public IEnumerator Part2_DetourEndedByExhaustedRetries_IsDetourReturn() { yield return VerifiedReturnCase(true, "retries_exhausted"); }

    [UnityTest]
    public IEnumerator Part2_TheRestoredWanderingTarget_KeepsItsIdentity_AndTheMoverFollowsTheSameTransform()
    {
        Rig rig = null; Component pa = null, ub = null; NavMeshAgent nav = null;
        yield return DetourNpc(true, (r, p, u, n) => { rig = r; pa = p; ub = u; nav = n; });
        var mt = pa.GetComponent(MoveType);
        var before = Get<Transform>(mt, "targetTransform");
        Assert.IsNotNull(before, "Part 2 follows an itinerary Transform");
        yield return RunDetour(rig, pa, ub, nav, "timer");
        Assert.AreSame(before, Get<Transform>(mt, "targetTransform"), "the owner restored the SAME wandering Transform");
        Assert.AreEqual("owner", Get<string>(ub, "LastDetourResumeSource"));
        Assert.AreSame(before, Get<Transform>(ub, "LastDetourResumeTransform"));
    }

    [UnityTest]
    public IEnumerator Part2_ANewWanderingTarget_WithoutADetour_RemainsItineraryTarget()
    {
        Rig rig = null; Component pa = null, ub = null; NavMeshAgent nav = null;
        yield return DetourNpc(true, (r, p, u, n) => { rig = r; pa = p; ub = u; nav = n; });
        string id = Lid(pa);
        Call(pa, "AdvanceWanderingItinerary");   // normal progression to the next itinerary entry
        yield return new WaitForSecondsRealtime(0.6f);
        CollectionAssert.AreEqual(new[] { "initial_assignment", "itinerary_target" }, Causes(rig, id).ToArray());
        Assert.AreEqual(0, Get<int>(ub, "DetoursStarted"));
    }

    [UnityTest]
    public IEnumerator Part2_AfterADetour_AnOwnerThatPicksADifferentTarget_IsANewTarget_NotAReturn()
    {
        Rig rig = null; Component pa = null, ub = null; NavMeshAgent nav = null;
        yield return DetourNpc(true, (r, p, u, n) => { rig = r; pa = p; ub = u; nav = n; });
        string id = Lid(pa);
        yield return RunDetour(rig, pa, ub, nav, "timer", () => Set(pa, "currentIndex", Get<int>(pa, "currentIndex") + 1));   // the owner re-targets while the detour is running
        Assert.AreEqual(1, Get<int>(ub, "DetoursCompleted"));
        Assert.IsFalse(Get<bool>(ub, "LastDetourRestoredPreDetourTarget"), "what was restored is not the pre-detour destination");
        CollectionAssert.AreEqual(new[] { "initial_assignment", "detour", "itinerary_target" }, Causes(rig, id).ToArray());
    }

    IEnumerator UnfinishedCase(bool part2)
    {
        Rig rig = null; Component pa = null, ub = null; NavMeshAgent nav = null;
        yield return DetourNpc(part2, (r, p, u, n) => { rig = r; pa = p; ub = u; nav = n; });
        string id = Lid(pa);
        yield return RunDetour(rig, pa, ub, nav, "none");
        yield return new WaitForSecondsRealtime(0.5f);
        Assert.AreEqual(0, Get<int>(ub, "DetoursCompleted")); Assert.AreEqual(1, Get<int>(ub, "DetoursStarted"));
        CollectionAssert.AreEqual(new[] { "initial_assignment", "detour" }, Causes(rig, id).ToArray(), "a detour that has not completed never yields a return");
    }
    [UnityTest] public IEnumerator Part3_AnUnfinishedDetour_ProducesNoReturn() { yield return UnfinishedCase(false); }
    [UnityTest] public IEnumerator Part2_AnUnfinishedDetour_ProducesNoReturn() { yield return UnfinishedCase(true); }

    IEnumerator PooledReuseCase(bool part2)
    {
        Rig rig = null; Component pa = null, ub = null; NavMeshAgent nav = null;
        yield return DetourNpc(part2, (r, p, u, n) => { rig = r; pa = p; ub = u; nav = n; });
        string life1 = Lid(pa), agentId = Aid(pa);
        yield return RunDetour(rig, pa, ub, nav, "none");   // lifetime 1 ends in the middle of a detour
        Call(pa, "ForceFinish");
        yield return WaitUntil(() => ActiveAgents(rig).Exists(a => Aid(a) == agentId && Lid(a) != life1 && Lid(a) != ""), 10f, "the pooled object to start a new lifetime");
        CollectionAssert.DoesNotContain(Causes(rig, life1), "detour_return", "an uncompleted detour never yields a return");
        var pa2 = ActiveAgents(rig).Find(a => Aid(a) == agentId); string life2 = Lid(pa2); var ub2 = pa2.GetComponent(T("UncertainBehavior"));
        Assert.AreNotEqual(life1, life2);
        Assert.AreEqual(0, Get<int>(ub2, "DetoursStarted")); Assert.AreEqual(0, Get<int>(ub2, "DetoursCompleted")); Assert.IsFalse(Get<bool>(ub2, "LastDetourRestoredPreDetourTarget"));
        Assert.AreEqual("", Get<string>(ub2, "LastDetourEndReason"));
        yield return WaitUntil(() => Causes(rig, life2).Contains("initial_assignment"), 10f, "the new lifetime's initial assignment");
        yield return new WaitForSecondsRealtime(0.6f);
        var c2 = Causes(rig, life2);
        Assert.AreEqual("initial_assignment", c2[0]);
        CollectionAssert.DoesNotContain(c2, "detour_return", "no leaked detour evidence in the new lifetime");
        CollectionAssert.DoesNotContain(c2, "detour", "no detour happened in the new lifetime (ordinary leg changes, e.g. itinerary_target, are allowed)");
    }
    [UnityTest] public IEnumerator PooledReuse_Part3_StartsWithAFreshDetourHistory() { yield return PooledReuseCase(false); }
    [UnityTest] public IEnumerator PooledReuse_Part2_StartsWithAFreshDetourHistory() { yield return PooledReuseCase(true); }

    // ---------------------------------------------------------------------------------------------- G1 v13 (gaze rows, manifest, trajectory)

    // XR-like hierarchy for the recorder: base -> OVR tree (CenterEye + eye anchors) and base -> Camera Offset -> Main Camera. Returns the recorder with every reference wired.
    Component MakeViewGaze(string file, float cameraOffsetY, float headY, float groundY, out Transform baseT, out Transform viewT, out Transform centreT)
    {
        var ground = GameObject.CreatePrimitive(PrimitiveType.Cube); _spawned.Add(ground); ground.name = "TestGround";
        ground.transform.position = new Vector3(0f, groundY - 0.5f, 0f); ground.transform.localScale = new Vector3(300f, 1f, 300f);
        baseT = Make("XR Origin").transform; baseT.position = new Vector3(0f, groundY, 0f);
        var ovr = Make("OVRCameraRig").transform; ovr.SetParent(baseT, false);
        centreT = Make("CenterEyeAnchor").transform; centreT.SetParent(ovr, false); centreT.localPosition = new Vector3(0f, headY, 0f);
        var l = Make("LeftEyeAnchor").transform; l.SetParent(ovr, false); l.localPosition = new Vector3(-0.0337f, headY, 0f);
        var r = Make("RightEyeAnchor").transform; r.SetParent(ovr, false); r.localPosition = new Vector3(+0.0337f, headY, 0f);
        var off = Make("Camera Offset").transform; off.SetParent(baseT, false); off.localPosition = new Vector3(0f, cameraOffsetY, 0f);
        viewT = Make("Main Camera").transform; viewT.SetParent(off, false); viewT.localPosition = new Vector3(0f, headY, 0f);
        var rec = Make("ViewGaze").AddComponent(RecorderType);
        Set(rec, "headTransform", centreT); Set(rec, "sampleRate", 100f); Set(rec, "enableDebugLogs", false); Set(rec, "enableROIRaycast", false);
        Set(rec, "subjectId", "S"); Set(rec, "trialId", "T"); Set(rec, "outputPathOverride", Path.Combine(_root, file));
        Set(rec, "viewCamera", viewT); Set(rec, "centerEyeAnchor", centreT); Set(rec, "leftEyeAnchor", l); Set(rec, "rightEyeAnchor", r);
        Set(rec, "groundLayerMask", (LayerMask)1);
        return rec;
    }

    [UnityTest]
    public IEnumerator G1_GazeRows_RecordTheRenderedViewEyesAndGroundRelativeHeights_WhileHeadColumnsStayTheOvrTree()
    {
        var rec = MakeViewGaze("view.csv", cameraOffsetY: 0.9f, headY: 0.4f, groundY: 168f, out Transform baseT, out Transform view, out Transform centre);
        yield return null; Physics.SyncTransforms();
        Set(rec, "testLeftEye", Inj(0.9f, view.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, view.position, Vector3.forward));
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.3f); Call(rec, "StopRecording");
        var csv = Csv.Load(Get<string>(rec, "outputPathOverride"));
        Assert.AreEqual(143, csv.Header.Count);   // 133 + 10 schema-v16 face columns
        Assert.Greater(csv.Rows.Count, 2);
        foreach (var row in csv.Rows)
        {
            Assert.AreEqual(csv.Header.Count, row.Count);
            double D(string c) => double.Parse(csv.Get(row, c), CultureInfo.InvariantCulture);
            Assert.AreEqual(168.0 + 0.9 + 0.4, D("hmd_world_y"), 2e-3, "rendered camera = base + camera offset + tracked pose");
            Assert.AreEqual(168.0 + 0.4, D("head_pos_y"), 2e-3, "the pre-existing head_pos columns are still the OVR centre eye (no camera offset)");
            Assert.AreEqual(0.0, D("left_eye_head_local_y"), 2e-3, "head-local eye Y is independent of any world height");
            Assert.AreEqual(-0.0337, D("left_eye_head_local_x"), 1e-3); Assert.AreEqual(+0.0337, D("right_eye_head_local_x"), 1e-3);
            Assert.AreEqual(168.0 + 0.9 + 0.4, D("left_eye_world_y"), 2e-3); Assert.AreEqual(D("left_eye_world_y"), D("right_eye_world_y"), 1e-4);
            Assert.AreEqual(168.0, D("ground_y"), 2e-3);
            Assert.AreEqual(0.9 + 0.4, D("hmd_height_above_ground"), 2e-3, "height above the actual ground, not a constant");
            Assert.AreEqual(D("hmd_height_above_ground"), D("left_eye_height_above_ground"), 1e-3);
        }
    }

    [UnityTest]
    public IEnumerator G1_WithoutARenderedViewOrAnchors_TheViewColumnsAreBlank_NotFabricated()
    {
        var rec = MakeStandaloneGaze("noview.csv", out Transform head);
        yield return null;
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.25f); Call(rec, "StopRecording");
        var csv = Csv.Load(Get<string>(rec, "outputPathOverride"));
        Assert.Greater(csv.Rows.Count, 1);
        foreach (var row in csv.Rows)
            foreach (string c in new[] { "hmd_world_x", "hmd_world_y", "left_eye_world_y", "left_eye_head_local_y", "ground_y", "hmd_height_above_ground", "right_eye_height_above_ground" })
                Assert.AreEqual("", csv.Get(row, c), c + " must be blank when the view frame cannot be resolved");
    }

    [UnityTest]
    public IEnumerator G1_HeadFallbackRay_StartsAtTheRenderedView()
    {
        var rec = MakeViewGaze("fb.csv", 0.9f, 0.4f, 168f, out _, out Transform view, out _);
        yield return null; Physics.SyncTransforms();
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.25f); Call(rec, "StopRecording");   // no eye samples at all -> head fallback
        var csv = Csv.Load(Get<string>(rec, "outputPathOverride")); Assert.Greater(csv.Rows.Count, 1);
        foreach (var row in csv.Rows)
        {
            Assert.AreEqual("head_fallback", csv.Get(row, "gaze_ray_source"));
            Assert.AreEqual(view.position.y, double.Parse(csv.Get(row, "ray_origin_y"), CultureInfo.InvariantCulture), 2e-3, "the fallback ray no longer starts a Camera Floor Offset below the viewpoint");
        }
    }

    [UnityTest]
    public IEnumerator G1_Manifest_ViewFrameBlock_ReportsTheOffsetSource_AndNoParticipantCalibration()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        var ground = GameObject.CreatePrimitive(PrimitiveType.Cube); _spawned.Add(ground); ground.transform.position = new Vector3(0f, -0.5f, 0f); ground.transform.localScale = new Vector3(300f, 1f, 300f);
        var off = Make("CamOff").transform; off.position = new Vector3(0f, 1.25f, 0f);
        var view = Make("ViewCam").transform; view.SetParent(off, false); view.localPosition = new Vector3(0f, 0.05f, -4f);
        var l = Make("L").transform; l.position = head.position + Vector3.left * 0.03f; var r2 = Make("R").transform; r2.position = head.position + Vector3.right * 0.03f;
        Set(rec, "viewCamera", view); Set(rec, "centerEyeAnchor", head); Set(rec, "leftEyeAnchor", l); Set(rec, "rightEyeAnchor", r2); Set(rec, "groundLayerMask", (LayerMask)1);
        Type ft = T("XrOriginFacts"); object f = Activator.CreateInstance(ft);
        void S(string n, object v) => ft.GetField(n).SetValue(f, v);
        S("OriginPresent", true); S("RequestedMode", "NotSpecified"); S("CurrentMode", "Device"); S("CameraYOffsetProperty", 1.25f); S("HasOffsetObject", true); S("OffsetObjectLocalY", 1.25f); S("OffsetObjectPath", "XR Origin/Camera Offset"); S("ViewCameraPath", "XR Origin/Camera Offset/Main Camera"); S("CentreEyePath", "OVRCameraRig/CenterEyeAnchor");
        SetConstDelegate(rec, "testXrFacts", f); Set(rec, "_nextXrFactsT", 0.0);
        Set(Get<object>(rec, "ViewProbe"), "_nextSearchT", 0f);   // the trial already sampled once before these references existed: lift the 1 s scene-search throttle
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Physics.SyncTransforms();
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.5f); Call(rec, "StopRecording");
        var vf = ManifestOf(rig)["eye_tracking"]["view_frame"];
        Assert.AreEqual("fixed_camera_y_offset_not_participant_specific", (string)vf["eye_height_source"]);
        Assert.IsFalse((bool)vf["eye_height_is_participant_specific"]);
        Assert.AreEqual("Device", (string)vf["xr_origin_current_mode"]); Assert.AreEqual(1.25, Num(vf["camera_floor_offset_local_y_m"]), 1e-4); Assert.AreEqual(1.25, Num(vf["xr_origin_camera_y_offset_property_m"]), 1e-4);
        Assert.Greater((long)vf["samples"], 2L); Assert.Greater((long)vf["ground_samples"], 2L);
        Assert.AreEqual("none", (string)vf["participant_height_calibration"], "no participant-specific calibration exists, and none is invented");
        Assert.AreEqual(1.25 + 0.05 - head.position.y, ((JArray)vf["view_minus_ovr_centre_eye_mean_m"])[1].Value<double>(), 1e-3, "the measured world offset between the rendered camera and the OVR centre eye");
    }

    [UnityTest]
    public IEnumerator G1_Trajectory_AppendsTheRenderedViewColumns_AndLosAimsAtTheRenderedView()
    {
        var camGo = new GameObject("Main Camera"); _spawned.Add(camGo); camGo.tag = "MainCamera"; camGo.AddComponent<Camera>(); camGo.transform.position = new Vector3(0f, 7.5f, -3f);
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = true }, r => rig = r);
        yield return null; yield return null;
        var hdr = GetStatic<string>(T("CaptureConstants"), "TrajectoryHeader").Split(',');
        Assert.AreEqual("view_height_above_ground", hdr[hdr.Length - 1]); Assert.AreEqual("view_pos_x", hdr[hdr.Length - 4]); Assert.AreEqual("phase", hdr[hdr.Length - 5], "existing columns did not move");
        Assert.AreEqual(camGo.transform, Get<Transform>(rig.dcm, "ViewTransform"), "the LOS target / view columns use the rendered camera (Camera.main)");
    }

    // ---------------------------------------------------------------------------------------------- v14 (provider pipeline through the real recorder)

    static object MakeRaw(bool available, double time, bool lValid, Vector3 lPos, Vector3 lDir, bool rValid, Vector3 rPos, Vector3 rDir)
    {
        Type st = T("RawGazeState"), et = T("RawGazeEye");
        object Eye(bool v, Vector3 p, Vector3 d) { object e = Activator.CreateInstance(et); et.GetField("valid").SetValue(e, v); et.GetField("confidence").SetValue(e, 0.9f); et.GetField("rawPos").SetValue(e, p); et.GetField("rawDir").SetValue(e, d); return e; }
        object r = Activator.CreateInstance(st);
        st.GetField("available").SetValue(r, available); st.GetField("time").SetValue(r, time);
        st.GetField("left").SetValue(r, Eye(lValid, lPos, lDir)); st.GetField("right").SetValue(r, Eye(rValid, rPos, rDir));
        return r;
    }

    Component PipelineRec(string file, float headY, out Transform view)
    {
        var rec = MakeViewGaze(file, cameraOffsetY: 0.9f, headY: headY, groundY: 168f, out _, out view, out Transform centre);
        Set(rec, "testLeftEye", Inj(0.9f, view.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, view.position, Vector3.forward));
        SetConstDelegate(rec, "testHeadReference", (new Vector3(0f, headY, 0f), Quaternion.identity));   // head node pose in tracking space
        return rec;
    }

    [UnityTest]
    public IEnumerator V14_RawProviderAndHeadSpaceColumns_ArePerEye_AndTheHeadFrameDoesNotContainTheWorldHeightOrTheCameraOffset()
    {
        var rec = PipelineRec("pipe1.csv", 0.4f, out _);
        yield return null; Physics.SyncTransforms();
        // provider: left eye 2 mm above / 10 mm in front of the left view anchor, right eye 1 mm below; separation deliberately different from the anchors'
        SetConstDelegate(rec, "testRawGaze", MakeRaw(true, 1234.5, true, new Vector3(-0.0320f, 0.4f + 0.002f, 0.010f), Quaternion.Euler(-3f, 4f, 0f) * Vector3.forward,
                                                      true, new Vector3(+0.0330f, 0.4f - 0.001f, 0.012f), Quaternion.Euler(-3f, 6f, 0f) * Vector3.forward));
        SetConstDelegate(rec, "testRuntimeIpd", 0.0642f);
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.35f); Call(rec, "StopRecording");
        var csv = Csv.Load(Get<string>(rec, "outputPathOverride")); Assert.Greater(csv.Rows.Count, 3);
        Assert.AreEqual("provider_time_s", csv.Header[csv.Header.Count - 37 - 10]); Assert.AreEqual("origin_fallback_reason", csv.Header[csv.Header.Count - 1 - 10]); Assert.AreEqual("face_data_source", csv.Header[csv.Header.Count - 1]);   // 10 schema-v16 face columns follow
        foreach (var row in csv.Rows)
        {
            Assert.AreEqual(csv.Header.Count, row.Count);
            double D(string c) => double.Parse(csv.Get(row, c), CultureInfo.InvariantCulture);
            Assert.AreEqual(1234.5, D("provider_time_s"), 1e-4);
            Assert.AreEqual(0.4 + 0.002, D("left_raw_tracking_pos_y"), 1e-4, "raw = OVRPlugin tracking space");
            Assert.AreEqual(0.002, D("left_gaze_head_pos_y"), 1e-4, "head space: the 0.4 m tracked height is gone");
            Assert.AreEqual(-0.0320, D("left_gaze_head_pos_x"), 1e-4); Assert.AreEqual(+0.0330, D("right_gaze_head_pos_x"), 1e-4); Assert.AreEqual(-0.001, D("right_gaze_head_pos_y"), 1e-4);
            Assert.AreNotEqual(D("left_gaze_head_dir_y"), D("right_gaze_head_dir_x"), "left and right are read independently");
            double ln = Math.Sqrt(D("left_gaze_head_dir_x") * D("left_gaze_head_dir_x") + D("left_gaze_head_dir_y") * D("left_gaze_head_dir_y") + D("left_gaze_head_dir_z") * D("left_gaze_head_dir_z"));
            Assert.AreEqual(1.0, ln, 2e-4, "head-space direction is a unit vector");
            // provider eye minus the stereo VIEW anchor, in head space: left (-0.0320,0.002,0.010) vs (-0.0337,0,0)
            Assert.AreEqual(Math.Sqrt(0.0017 * 0.0017 + 0.002 * 0.002 + 0.010 * 0.010), D("left_gaze_vs_view_anchor_head_m"), 2e-4);
            Assert.AreEqual(0.0674, D("view_anchor_separation_m"), 2e-4, "the stereo VIEW separation is its own quantity (here set by the test rig)");
            Assert.AreEqual(Math.Sqrt(0.065 * 0.065 + 0.003 * 0.003 + 0.002 * 0.002), D("gaze_head_separation_m"), 2e-4, "the PROVIDER eye separation differs from the view separation and from the IPD value");
            Assert.AreEqual(0.0642, D("runtime_user_ipd_m"), 1e-5, "the runtime user IPD is passed through as reported, not replaced by a constant");
        }
        Assert.AreEqual("0", csv.Get(csv.Rows[csv.Rows.Count - 1], "provider_time_changed"), "the same provider timestamp = the previous provider sample reused");
    }

    [UnityTest]
    public IEnumerator V14_IpdAndViewSeparationFollowTheirInputs_NothingIsHardcoded()
    {
        var rec = PipelineRec("pipe2.csv", 0.4f, out Transform view);
        yield return null; Physics.SyncTransforms();
        SetConstDelegate(rec, "testRawGaze", MakeRaw(false, 0, false, default, default, false, default, default));
        SetConstDelegate(rec, "testRuntimeIpd", 0.0600f);
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.25f);
        SetConstDelegate(rec, "testRuntimeIpd", 0.0710f);
        var anchors = GameObject.FindObjectsOfType<Transform>().Where(t => t.name == "LeftEyeAnchor" || t.name == "RightEyeAnchor").ToArray();
        foreach (var a in anchors) a.localPosition = new Vector3(a.name == "LeftEyeAnchor" ? -0.0300f : +0.0300f, a.localPosition.y, 0f);   // the headset IPD slider moved
        Set(Get<object>(rec, "ViewProbe"), "_nextSearchT", 0f);
        Set(rec, "_nextIpdT", 0.0);
        yield return new WaitForSecondsRealtime(1.2f); Call(rec, "StopRecording");   // the IPD read is cached for 1 s
        var csv = Csv.Load(Get<string>(rec, "outputPathOverride"));
        var ipds = csv.Rows.Select(r => csv.Get(r, "runtime_user_ipd_m")).Distinct().ToList();
        CollectionAssert.IsSubsetOf(new[] { "0.06000", "0.07100" }, ipds); Assert.AreEqual(2, ipds.Count, "both settings were reported");
        var seps = csv.Rows.Select(r => csv.Get(r, "view_anchor_separation_m")).Distinct().ToList();
        CollectionAssert.Contains(seps, "0.06740"); CollectionAssert.Contains(seps, "0.06000");
        foreach (var r in csv.Rows) { Assert.AreEqual("", csv.Get(r, "left_raw_tracking_pos_x"), "no raw provider: blank, not fabricated"); Assert.AreEqual("", csv.Get(r, "provider_time_s")); Assert.AreEqual("", csv.Get(r, "gaze_head_separation_m")); }
        var m = Get<object>(rec, "Pipeline");
        Assert.IsFalse(Get<bool>(m, "raw_provider_read_available"));
    }

    [UnityTest]
    public IEnumerator V14_InvalidEye_LeavesOnlyThatEyeBlank_ProviderTimeAdvanceIsDetected_AndAnchorFramesAreRecorded()
    {
        var rec = PipelineRec("pipe3.csv", 0.4f, out _);
        yield return null; Physics.SyncTransforms();
        Vector3 d = Vector3.forward;
        SetConstDelegate(rec, "testRawGaze", MakeRaw(true, 10.0, false, new Vector3(9f, 9f, 9f), d, true, new Vector3(0.033f, 0.4f, 0.01f), d));
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.2f);
        Call(rec, "OnAnchorsUpdated", new object[] { null });
        int frameAtUpdate = Time.frameCount;
        SetConstDelegate(rec, "testRawGaze", MakeRaw(true, 10.5, false, new Vector3(9f, 9f, 9f), d, true, new Vector3(0.033f, 0.4f, 0.01f), d));
        yield return new WaitForSecondsRealtime(0.25f); Call(rec, "StopRecording");
        var csv = Csv.Load(Get<string>(rec, "outputPathOverride"));
        foreach (var r in csv.Rows)
        {
            Assert.AreEqual("", csv.Get(r, "left_raw_tracking_pos_x"), "an invalid provider eye is blank, its garbage position is never recorded");
            Assert.AreEqual("", csv.Get(r, "left_gaze_head_pos_x")); Assert.AreNotEqual("", csv.Get(r, "right_gaze_head_pos_x"));
            Assert.AreEqual("", csv.Get(r, "gaze_head_separation_m"), "a separation needs both eyes");
        }
        var changed = csv.Rows.Select(r => csv.Get(r, "provider_time_changed")).ToList();
        CollectionAssert.Contains(changed, "1", "the provider timestamp advanced once"); CollectionAssert.Contains(changed, "0");
        var frames = csv.Rows.Select(r => csv.Get(r, "anchors_updated_frame")).Where(x => x != "").Distinct().ToList();
        Assert.AreEqual(1, frames.Count); Assert.AreEqual(frameAtUpdate.ToString(CultureInfo.InvariantCulture), frames[0]);
        var pm = Get<object>(rec, "Pipeline");
        Assert.Greater(Get<long>(pm, "rows_provider_time_advanced"), 0L); Assert.Greater(Get<long>(pm, "rows_provider_time_repeated"), 0L); Assert.Greater(Get<long>(pm, "anchors_frame_lag_max"), 0L, "rows after the update report how many frames behind the anchors are");
        Assert.AreEqual(1L, Get<long>(pm, "anchors_updated_events"));
    }

    [UnityTest]
    public IEnumerator V14_FallbackReason_IsRecorded_WhenNoEyeIsValid()
    {
        var rec = MakeViewGaze("pipe4.csv", 0.9f, 0.4f, 168f, out _, out _, out _);
        yield return null; Physics.SyncTransforms();
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.25f); Call(rec, "StopRecording");
        var csv = Csv.Load(Get<string>(rec, "outputPathOverride")); Assert.Greater(csv.Rows.Count, 1);
        foreach (var r in csv.Rows) { Assert.AreEqual("head_fallback", csv.Get(r, "gaze_ray_source")); Assert.AreEqual("no_valid_eye", csv.Get(r, "origin_fallback_reason")); }
    }

    [UnityTest]
    public IEnumerator V14_Manifest_EyePipelineBlock_StatesTheArchitecture_AndWhatIsNotExposed()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        var l = Make("L").transform; l.position = head.position + Vector3.left * 0.0337f; var r2 = Make("R").transform; r2.position = head.position + Vector3.right * 0.0337f;
        var view = Make("ViewCam").transform; view.position = head.position + Vector3.up * 0.5f;
        Set(rec, "viewCamera", view); Set(rec, "centerEyeAnchor", head); Set(rec, "leftEyeAnchor", l); Set(rec, "rightEyeAnchor", r2);
        Set(Get<object>(rec, "ViewProbe"), "_nextSearchT", 0f);
        SetConstDelegate(rec, "testHeadReference", (head.position, Quaternion.identity));
        SetConstDelegate(rec, "testRawGaze", MakeRaw(true, 5.0, true, head.position + new Vector3(-0.03f, 0f, 0f), Vector3.forward, true, head.position + new Vector3(0.03f, 0f, 0f), Vector3.forward));
        SetConstDelegate(rec, "testRuntimeIpd", 0.0655f);
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.4f); Call(rec, "StopRecording");
        var ep = ManifestOf(rig)["eye_tracking"]["eye_pipeline"];
        StringAssert.Contains("XRI Main Camera renders", (string)ep["architecture"]);
        StringAssert.Contains("WorldSpace", (string)ep["primary_gaze_path"]);
        Assert.IsTrue((bool)ep["raw_provider_read_available"]); Assert.IsTrue((bool)ep["runtime_user_ipd_available"]);
        Assert.AreEqual(0.0655, Num(ep["runtime_user_ipd_m"]["mean"]), 1e-5);
        Assert.AreEqual(0.0674, Num(ep["view_anchor_separation_m"]["mean"]), 2e-4, "measured from the anchors given, not a constant");
        Assert.AreEqual(0.06, Num(ep["gaze_head_separation_m"]["mean"]), 2e-4);
        StringAssert.StartsWith("not_exposed", (string)ep["eye_tracking_calibration"]); StringAssert.StartsWith("not_measured", (string)ep["physical_pupil_position"]);
        Assert.AreEqual(16, (int)ManifestOf(rig)["schema_version"]);
    }

    // ---------------------------------------------------------------------------------------------- G1

    [UnityTest]
    public IEnumerator G1_Manifest_CarriesTheTrackingSpaceEvidence_AndIsSampledAtALowRate()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Type factsT = Type.GetType("EyeGazeOrigin+TrackingSpaceFacts, Assembly-CSharp");
        object facts = Activator.CreateInstance(factsT);
        void F(string n, object v) => factsT.GetField(n).SetValue(facts, v);
        F("OvrPluginInitialized", true); F("OpenXrActive", true); F("OvrPluginOrigin", "FloorLevel"); F("UnityXrMode", "Device"); F("LoadedXrDevice", "OpenXR"); F("NativeXrApi", "OpenXR");
        F("OvrPluginHeadY", (double?)1.2514); F("UnityHeadY", (double?)-0.11);
        SetConstDelegate(rec, "testTrackingFacts", facts);
        Set(rec, "_nextTrackingProbeT", 0.0);   // the trial already started recording: force the next sample to probe with the injected facts
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(0.5f);
        Call(rec, "StopRecording");
        var ts = ManifestOf(rig)["eye_tracking"]["tracking_space"];
        Assert.IsTrue((bool)ts["captured"]);
        Assert.AreEqual("differ", (string)ts["tracking_origins_relation"]);
        Assert.AreEqual("FloorLevel", (string)ts["ovrplugin_tracking_origin"]); Assert.AreEqual("Device", (string)ts["unity_xr_tracking_origin_mode"]);
        Assert.IsTrue((bool)ts["head_delta_available"]);
        Assert.AreEqual(1.3614, Num(ts["head_height_delta_mean_m"]), 1e-3);
        Assert.LessOrEqual((long)ts["probes"], 3L, "about one probe per two seconds, never per sample");
        StringAssert.Contains("not a hardware verification", (string)ts["note"]);
        // the frame arrays exist (empty here: injected samples carry no anchors) and the NaN-free sentinel convention holds
        Assert.AreEqual(0L, (long)ManifestOf(rig)["eye_tracking"]["origin_frames_samples"]);
    }
}
