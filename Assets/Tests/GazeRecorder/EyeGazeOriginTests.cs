// EyeGazeOriginTests.cs
// Synthetic-transform tests for the gaze-origin coordinate fix (schema_version 10). They do two things:
//   1. Reproduce, from the SDK's own conversion formula (OVRCommon.ToWorldSpacePose: world = cameraToWorld * (headRef^-1 * eyePose)), the mechanism behind the +1.361 m origin offset:
//      a pure vertical translation between two tracking origins survives the conversion as a constant WORLD-vertical vector, whatever the head yaw / pitch (rotation cancels).
//   2. Verify EyeGazeOrigin.Resolve against rig eye anchors: a consistent provider origin is kept; a mismatched one (any direction, any size) is replaced by the anchor, never shifted by a
//      constant; per-eye origins stay independent; NaN / missing anchors are handled.
// These are SYNTHETIC validations of the maths and of the recorder column; they say nothing about the real Quest Pro coordinate system (see docs/gaze_origin_verification.md).

using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public class EyeGazeOriginMathTests
{
    static readonly Type OriginType = T("EyeGazeOrigin");

    // A rig (translated + yawed tracking space) and an HMD (translated + yawed + pitched in that space). Returns camera local-to-world and the head pose in tracking space.
    static void Setup(Vector3 rigPos, float rigYaw, Vector3 headPos, float headYaw, float headPitch, out Matrix4x4 cameraL2W, out Quaternion headRot)
    {
        Quaternion rigRot = Quaternion.Euler(0f, rigYaw, 0f);
        headRot = Quaternion.Euler(headPitch, headYaw, 0f);
        Matrix4x4 trackingToWorld = Matrix4x4.TRS(rigPos, rigRot, Vector3.one);
        cameraL2W = trackingToWorld * Matrix4x4.TRS(headPos, headRot, Vector3.one);   // OVRCameraRig: the camera is the head node pose mapped through the tracking space
    }

    static Vector3 Sdk(Matrix4x4 cam, Vector3 headPos, Quaternion headRot, Vector3 eyePosTracking) => (Vector3)CallStatic(OriginType, "SdkWorldSpaceOrigin", cam, headPos, headRot, eyePosTracking);

    [Test]
    public void SdkConversion_WithTwoTrackingOrigins_LeavesAConstantWorldVerticalOffset_IndependentOfHeadYawAndPitch()
    {
        // Head node in the EYE-LEVEL space the rig uses (y ~ 0); the eye pose is referenced to the FLOOR (y ~ head height above floor).
        float floorToEyeLevel = 1.361f;
        Vector3 rigPos = new Vector3(2393.2f, 166.86f, 807.3f);
        foreach (float rigYaw in new[] { 0f, 30f, -75f })
        foreach (float headYaw in new[] { 0f, 45f, -45f, 120f })
        foreach (float headPitch in new[] { 0f, 25f, -25f })
        {
            Vector3 headPos = new Vector3(0.08f, -0.12f, -0.09f);
            Setup(rigPos, rigYaw, headPos, headYaw, headPitch, out Matrix4x4 cam, out Quaternion headRot);
            Vector3 camPos = cam.MultiplyPoint3x4(Vector3.zero);
            // both eyes sit +-0.032 m to the side of the head node, in head orientation, and are expressed in the floor-referenced space
            Vector3 l = headPos + headRot * new Vector3(-0.032f, 0f, 0.01f) + Vector3.up * floorToEyeLevel;
            Vector3 r = headPos + headRot * new Vector3(+0.032f, 0f, 0.01f) + Vector3.up * floorToEyeLevel;
            Vector3 combined = (Sdk(cam, headPos, headRot, l) + Sdk(cam, headPos, headRot, r)) * 0.5f;
            Vector3 offsetWorld = combined - camPos;
            string ctx = $"rigYaw={rigYaw} headYaw={headYaw} headPitch={headPitch}";
            Assert.AreEqual(0f, offsetWorld.x, 0.02f, "no horizontal component: " + ctx);
            Assert.AreEqual(0f, offsetWorld.z, 0.02f, "no horizontal component: " + ctx);
            Assert.AreEqual(floorToEyeLevel, offsetWorld.y, 0.02f, "constant world-vertical offset = the origin difference, whatever the head pose: " + ctx);
        }
    }

    [Test]
    public void SdkConversion_WithOneConsistentTrackingOrigin_PlacesEachEyeAtItsOwnWorldPosition()
    {
        Vector3 rigPos = new Vector3(10f, 2f, -4f);
        Setup(rigPos, 50f, new Vector3(0.1f, 1.5f, 0.2f), -35f, 15f, out Matrix4x4 cam, out Quaternion headRot);
        Vector3 headPos = new Vector3(0.1f, 1.5f, 0.2f);
        Vector3 lTrack = headPos + headRot * new Vector3(-0.031f, 0f, 0f), rTrack = headPos + headRot * new Vector3(0.033f, 0f, 0f);
        Vector3 lWorld = Sdk(cam, headPos, headRot, lTrack), rWorld = Sdk(cam, headPos, headRot, rTrack);
        Matrix4x4 trackingToWorld = Matrix4x4.TRS(rigPos, Quaternion.Euler(0f, 50f, 0f), Vector3.one);
        Assert.AreEqual(0f, (lWorld - trackingToWorld.MultiplyPoint3x4(lTrack)).magnitude, 1e-4f, "left eye: tracking space -> world, exactly once");
        Assert.AreEqual(0f, (rWorld - trackingToWorld.MultiplyPoint3x4(rTrack)).magnitude, 1e-4f, "right eye");
        Assert.AreEqual(0.064f, (lWorld - rWorld).magnitude, 1e-4f, "independent left / right offsets are preserved (IPD)");
        // HeadSpace view of the same pose: cameraToWorld^-1 * world
        Vector3 inHead = cam.inverse.MultiplyPoint3x4(lWorld);
        Assert.AreEqual(0f, (headRot * new Vector3(-0.031f, 0f, 0f)).magnitude - inHead.magnitude, 1e-4f, "HeadSpace position = eye offset in head axes");
    }

    static EyeResult Resolve(Vector3 provider, Vector3? anchor, float max = 0.12f)
    {
        var args = new object[] { provider, anchor, max, Vector3.zero, 0f };
        var src = CallStatic(OriginType, "Resolve", args);
        return new EyeResult { source = src.ToString(), origin = (Vector3)args[3], deviation = (float)args[4] };
    }
    struct EyeResult { public string source; public Vector3 origin; public float deviation; }

    [Test]
    public void Resolve_KeepsAConsistentProviderOrigin_ExactlyAsReported()
    {
        Vector3 anchor = new Vector3(5f, 1.6f, 3f), provider = anchor + new Vector3(0.012f, -0.008f, 0.015f);
        var r = Resolve(provider, anchor);
        Assert.AreEqual("Provider", r.source);
        Assert.AreEqual(provider, r.origin, "the provider origin is used unchanged (no correction applied)");
        Assert.AreEqual(0.0211f, r.deviation, 1e-3f);
    }

    [Test]
    public void Resolve_ReplacesAMismatchedProviderOrigin_ByTheRigEyeAnchor_WhateverTheOffset_AndNeverAddsAConstant()
    {
        Vector3 anchor = new Vector3(5f, 1.6f, 3f);
        foreach (Vector3 offset in new[] { new Vector3(0f, 1.361f, 0f), new Vector3(0f, 0.9f, 0f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0f, -1.7f, 0f), new Vector3(0.2f, 0.2f, 0.2f) })
        {
            var r = Resolve(anchor + offset, anchor);
            Assert.AreEqual("EyeAnchor", r.source, "offset " + offset);
            Assert.AreEqual(anchor, r.origin, "the origin is the eye anchor itself, not the provider origin shifted by a constant: offset " + offset);
            Assert.AreEqual(offset.magnitude, r.deviation, 1e-4f);
        }
    }

    [Test]
    public void Resolve_HandlesNonFiniteProviderAndMissingAnchor()
    {
        Vector3 anchor = new Vector3(1f, 2f, 3f);
        var nan = Resolve(new Vector3(float.NaN, 0f, 0f), anchor);
        Assert.AreEqual("EyeAnchor", nan.source); Assert.AreEqual(anchor, nan.origin); Assert.IsTrue(float.IsPositiveInfinity(nan.deviation));
        var none = Resolve(new Vector3(4f, 5f, 6f), null);
        Assert.AreEqual("ProviderUnchecked", none.source); Assert.AreEqual(new Vector3(4f, 5f, 6f), none.origin, "without an anchor the provider origin passes through untouched"); Assert.IsTrue(float.IsNaN(none.deviation));
        var nanAnchor = Resolve(new Vector3(4f, 5f, 6f), new Vector3(float.NaN, 0f, 0f));
        Assert.AreEqual("ProviderUnchecked", nanAnchor.source);
    }

    [Test]
    public void PerEyeOrigins_StayIndependent_AndTheCombinedSourceNamesAreHonest()
    {
        Vector3 la = new Vector3(-0.032f, 1.6f, 0f), ra = new Vector3(0.032f, 1.6f, 0f);
        var l = Resolve(la + Vector3.up * 1.361f, la); var r = Resolve(ra + Vector3.up * 1.361f, ra);
        Assert.AreNotEqual(l.origin, r.origin, "left and right origins are different points");
        Assert.AreEqual(0.064f, (l.origin - r.origin).magnitude, 1e-4f);
        Type src = Type.GetType("EyeOriginSource, Assembly-CSharp");
        object P = Enum.Parse(src, "Provider"), A = Enum.Parse(src, "EyeAnchor");
        Assert.AreEqual("provider", CallStatic(OriginType, "CombinedName", true, P, true, P));
        Assert.AreEqual("eye_anchor", CallStatic(OriginType, "CombinedName", true, A, true, A));
        Assert.AreEqual("mixed", CallStatic(OriginType, "CombinedName", true, P, true, A));
        Assert.AreEqual("provider", CallStatic(OriginType, "CombinedName", true, P, false, A));
        Assert.AreEqual("eye_anchor", CallStatic(OriginType, "CombinedName", false, P, true, A));
        Assert.AreEqual("", CallStatic(OriginType, "CombinedName", false, P, false, A), "head-fallback ray: no eye origin involved");
    }

    [Test]
    public void DeviationStats_SummariseTheOffset_WithoutPerSampleLogging()
    {
        var stats = Activator.CreateInstance(Type.GetType("EyeGazeOrigin+DeviationStats, Assembly-CSharp"));
        Type src = Type.GetType("EyeOriginSource, Assembly-CSharp");
        Vector3 anchor = new Vector3(0f, 1.6f, 0f);
        for (int i = 0; i < 4; i++) Call(stats, "Note", Enum.Parse(src, "EyeAnchor"), anchor + new Vector3(0f, 1.361f, 0f), (Vector3?)anchor, 1.361f);
        Call(stats, "Note", Enum.Parse(src, "Provider"), anchor, (Vector3?)anchor, 0f);
        Call(stats, "Note", Enum.Parse(src, "ProviderUnchecked"), anchor, (Vector3?)null, float.NaN);
        Assert.AreEqual(5L, Get<long>(stats, "Checked")); Assert.AreEqual(4L, Get<long>(stats, "AnchorFallback")); Assert.AreEqual(1L, Get<long>(stats, "Provider")); Assert.AreEqual(1L, Get<long>(stats, "Unchecked"));
        Assert.AreEqual(1.361, Get<double>(stats, "MaxDeviation"), 1e-4);
        Vector3 mean = Get<Vector3>(stats, "MeanOffset");
        Assert.AreEqual(1.361f * 4f / 5f, mean.y, 1e-3f, "the mean offset exposes a constant vertical mismatch");
        Assert.AreEqual(0f, mean.x, 1e-6f);
    }
}

public partial class NpcLifecycleTests
{
    [UnityTest]
    public IEnumerator Gaze_AppendsTheOriginSourceColumnLast_AndTheSchemaIsV10()
    {
        var rec = MakeStandaloneGaze("origin.csv", out Transform head);
        yield return null;
        string path = Get<string>(rec, "outputPathOverride");
        Set(rec, "testLeftEye", Inj(0.9f, head.position + Vector3.left * 0.03f, Vector3.forward));
        Set(rec, "testRightEye", Inj(0.9f, head.position + Vector3.right * 0.03f, Vector3.forward));
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(0.3f);
        Call(rec, "StopRecording");
        var csv = Csv.Load(path);
        // schema v17: gaze_origin_source is one of the four compact provenance columns of gaze.csv (combined_valid, gaze_ray_source, gaze_origin_source, eye_tracking_state); the verbose fallback reason lives in gaze_diagnostics.csv
        Assert.IsTrue(csv.MergedV17); Assert.AreEqual("gaze_origin_source", csv.PrimaryHeader[10]); Assert.AreEqual("origin_fallback_reason", csv.DiagnosticHeader[40]); Assert.AreEqual("blink_state", csv.DiagnosticHeader[78]);
        Assert.AreEqual("eyes_closed_left_recovered", csv.PrimaryHeader[58]); Assert.AreEqual("face_closure_valid", csv.PrimaryHeader[60]);
        Assert.Greater(csv.Rows.Count, 2);
        foreach (var row in csv.Rows)
        {
            Assert.AreEqual(csv.Header.Count, row.Count, "header / row lockstep");
            Assert.AreEqual("provider_unchecked", csv.Get(row, "gaze_origin_source"), "injected samples have no rig anchor: the provider origin is passed through and says so");
        }
        Assert.AreEqual(17, GetStatic<int>(T("CaptureConstants"), "SchemaVersion"));
    }
}
