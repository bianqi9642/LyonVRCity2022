// Part3TargetFlowTests.cs
// Integration tests of the two-target flow through the REAL ExperimentManager.RunPart3 coroutine (real UIManager + PanelController templates whose Continue buttons the test presses, real
// TargetSelectable buildings, real DataCaptureManager event writer): the same harness as the F-08 / F-11 / Destination 2 tests (NpcLifecycleTests.StartPart3, partial class).
//
// (In this code base the experimental phase with Destination 1 / Destination 2, the 10-minute D1 limit and the 15-minute total limit is "Part 3".)
//
// WHAT IS REAL: the RunPart3 order of operations, the group / zone / landmark validation before part_start, the two target identities (Target1BuildingId / Target2BuildingId), the explicit
//               active-target state at each transition, the panels and their Continue buttons, TargetSelectable gating (Select() / IPointerClickHandler delivery), the events.csv rows.
// WHAT IS SYNTHETIC: the buildings are boxes in an empty test scene; the "ray" is a Physics.Raycast followed by ExecuteEvents.ExecuteHierarchy(IPointerClickHandler) - the delivery path the
//               EventSystem uses for a UI / XR pointer click. There is NO XR Origin, headset, controller, XRRayInteractor or Quest here: those are covered only by the real-scene audit
//               (Part3TargetsRealSceneTests, EditMode) and still need an on-device check. Editor only.

using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    static Component TsOfBuilding(GameObject g) => g.GetComponent(T("TargetSelectable"));
    static string Phase(Rig rig) => Get<string>(rig.dcm, "CurrentPhase");

    // Drives Start panel -> d1_pending and returns the two assigned buildings.
    IEnumerator ToD1Pending(P3 p, Action<GameObject, GameObject> assigned)
    {
        var rig = p.rig;
        yield return PressPanel(p.ui, "Start panel");
        yield return WaitUntil(() => Phase(rig) == "d1_pending", 3f, "d1_pending");
        GameObject d1 = null, d2 = null;
        yield return WaitUntil(() => { d1 = Get<GameObject>(rig.dcm, "AssignedBuildingD1"); d2 = Get<GameObject>(rig.dcm, "AssignedBuildingD2"); return d1 != null && d2 != null; }, 3f, "assigned destinations");
        assigned(d1, d2);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ state, panels and transitions

    [UnityTest]
    public IEnumerator Part3Targets_TwoSeparateTargets_PanelsReferenceTheirOwnBuilding_AndEveryTransitionSetsTheActiveTargetExplicitly()
    {
        P3 p = null; yield return StartPart3(60f, 120f, x => p = x); var rig = p.rig;
        GameObject d1 = null, d2 = null;
        yield return ToD1Pending(p, (a, b) => { d1 = a; d2 = b; });

        string t1 = Get<string>(rig.em, "Target1BuildingId"), t2 = Get<string>(rig.em, "Target2BuildingId");
        Assert.AreEqual(Get<string>(TsOfBuilding(d1), "ResolvedTargetId"), t1, "Target 1 = the Destination 1 building"); Assert.AreEqual(Get<string>(TsOfBuilding(d2), "ResolvedTargetId"), t2, "Target 2 = the Destination 2 building");
        Assert.AreNotEqual(t1, t2); Assert.AreNotSame(d1, d2);
        StringAssert.StartsWith("BAT_GU_", t1, "group 0: Destination 1 zone is GU"); StringAssert.StartsWith("BAT_OE_", t2, "group 0: Destination 2 zone is OE");
        Assert.AreSame(d1, Get<GameObject>(p.ui, "Destination1PreviewBuilding"), "the Destination 1 panel is prepared for Target 1");
        Assert.AreSame(d2, Get<GameObject>(p.ui, "Destination2PreviewBuilding"), "the Destination 2 panel is prepared for Target 2");
        Assert.AreEqual("", Get<string>(rig.em, "ActivePart3Destination")); Assert.AreEqual("", Get<string>(rig.em, "ActivePart3TargetId"), "no search is open while the D1 panel is read");
        Assert.IsFalse(((Behaviour)TsOfBuilding(d1)).enabled); Assert.IsFalse(((Behaviour)TsOfBuilding(d2)).enabled, "no landmark is selectable before the D1 Continue");

        yield return PressPanel(p.ui, "D1 panel");
        yield return WaitUntil(() => Phase(rig) == "d1_search", 3f, "d1_search"); yield return null;
        Assert.AreEqual("D1", Get<string>(rig.em, "ActivePart3Destination")); Assert.AreEqual(t1, Get<string>(rig.em, "ActivePart3TargetId"));
        Assert.IsTrue(((Behaviour)TsOfBuilding(d1)).enabled, "only Target 1 is selectable (plus its zone mate: the harness enables allowWrongCandidateSelection)"); Assert.IsFalse(((Behaviour)TsOfBuilding(d2)).enabled);

        // a Destination 2 landmark can never complete Destination 1; neither can the other landmark of the Destination 1 zone
        Call(TsOfBuilding(d2), "Select"); yield return null; yield return null;
        Assert.AreEqual("d1_search", Phase(rig), "Target 2 is not Target 1");
        GameObject mate = p.all.First(g => g.name.StartsWith("BAT_GU_") && g != d1);
        Call(TsOfBuilding(mate), "Select"); yield return null; yield return null;
        Assert.AreEqual("d1_search", Phase(rig), "the other landmark of the same zone is wrong");
        GameObject otherZone = p.all.First(g => g.name.StartsWith("BAT_GE_"));
        Call(TsOfBuilding(otherZone), "Select"); yield return null; yield return null;
        Assert.AreEqual("d1_search", Phase(rig), "a building of an unrelated zone is wrong");

        // the correct building, with a duplicate callback in the same frame, completes Destination 1 exactly once
        Call(TsOfBuilding(d1), "Select"); Call(TsOfBuilding(d1), "Select");
        yield return WaitUntil(() => Phase(rig) == "d2_pending", 4f, "D1 found -> d2_pending");
        Assert.AreEqual("", Get<string>(rig.em, "ActivePart3Destination")); Assert.AreEqual("", Get<string>(rig.em, "ActivePart3TargetId"), "no stale Target 1 reference is kept between the destinations");
        Assert.AreEqual(t1, Get<string>(rig.em, "Target1BuildingId")); Assert.AreEqual(t2, Get<string>(rig.em, "Target2BuildingId"), "the two identities never change");
        Assert.IsFalse(((Behaviour)TsOfBuilding(d1)).enabled, "Target 1 is a plain building again");

        yield return PressPanel(p.ui, "D2 panel");
        yield return WaitUntil(() => Phase(rig) == "d2_search", 3f, "d2_search"); yield return null; yield return null;
        Assert.AreEqual("D2", Get<string>(rig.em, "ActivePart3Destination")); Assert.AreEqual(t2, Get<string>(rig.em, "ActivePart3TargetId"), "the active target is set explicitly to Target 2");
        Assert.AreNotEqual(t1, Get<string>(rig.em, "ActivePart3TargetId"));
        Assert.IsTrue(((Behaviour)TsOfBuilding(d2)).enabled); Assert.IsFalse(((Behaviour)TsOfBuilding(d1)).enabled);

        // Target 1 cannot complete Destination 2 (even when the click reaches the manager)
        Call(TsOfBuilding(d1), "Select"); yield return null; yield return null; yield return null;
        Assert.AreEqual("d2_search", Phase(rig), "a Target 1 hit is not a Target 2 hit");
        Call(TsOfBuilding(d2), "Select"); Call(TsOfBuilding(d2), "Select");
        yield return WaitUntil(() => Phase(rig) == "ended", 4f, "end");
        Call(TsOfBuilding(d2), "Select"); yield return null; yield return null;    // a repeated event after completion
        Assert.AreEqual("", Get<string>(rig.em, "ActivePart3Destination")); Assert.AreEqual("", Get<string>(rig.em, "ActivePart3TargetId"));
        yield return PressPanel(p.ui, "End panel"); yield return new WaitForSecondsRealtime(0.2f);

        var evs = Events(rig);
        var correct = Rows(evs, "event_type", "correct_selection");
        Assert.AreEqual(new[] { "D1", "D2" }, correct.Select(r => evs.Get(r, "label")).ToArray(), "exactly one completion per destination, in order; completing Target 1 did not complete Target 2");
        Assert.AreEqual(t1, evs.Get(correct[0], "target_id")); Assert.AreEqual(t2, evs.Get(correct[1], "target_id"));
        Assert.AreEqual(1, Rows(evs, "event_type", "phase_start").Count(r => evs.Get(r, "label") == "d2_pending"), "the transition to Destination 2 happened exactly once");
        Assert.AreEqual(1, Rows(evs, "event_type", "phase_start").Count(r => evs.Get(r, "label") == "d2_search"));
        Assert.AreEqual(4, Rows(evs, "event_type", "incorrect_selection").Count, "the wrong clicks were scored as incorrect (4: Target 2 and the zone mate and an unrelated landmark during D1, Target 1 during D2)");
    }

    [UnityTest]
    public IEnumerator Part3Targets_D1Timeout_ActivatesTarget2WithItsOwnId_AndTheTotalLimitStillEndsThePhase()
    {
        P3 p = null; yield return StartPart3(1.5f, 4.5f, x => p = x); var rig = p.rig;
        GameObject d1 = null, d2 = null;
        yield return ToD1Pending(p, (a, b) => { d1 = a; d2 = b; });
        string t1 = Get<string>(rig.em, "Target1BuildingId"), t2 = Get<string>(rig.em, "Target2BuildingId");
        yield return PressPanel(p.ui, "D1 panel");
        yield return WaitUntil(() => Phase(rig) == "d1_search", 3f, "d1_search");
        Assert.AreEqual(t1, Get<string>(rig.em, "ActivePart3TargetId"));
        // the 'D1 limit' elapses without a hit -> Destination 2 with Target 2
        yield return WaitUntil(() => Phase(rig) == "d2_pending", 6f, "D1 timeout -> d2_pending");
        Assert.AreEqual("", Get<string>(rig.em, "ActivePart3TargetId"), "the timeout leaves no active target");
        yield return PressPanel(p.ui, "D2 panel");
        yield return WaitUntil(() => Phase(rig) == "d2_search", 3f, "d2_search"); yield return null;
        Assert.AreEqual("D2", Get<string>(rig.em, "ActivePart3Destination")); Assert.AreEqual(t2, Get<string>(rig.em, "ActivePart3TargetId"), "after the timeout the active target is Target 2, not Target 1");
        Call(TsOfBuilding(d1), "Select"); yield return null; yield return null;
        Assert.AreEqual("d2_search", Phase(rig), "the Target 1 building does not complete Destination 2");
        // the total limit (4.5 s overall, 1.5 s spent on D1) still ends the phase
        yield return WaitUntil(() => Phase(rig) == "ended", 8f, "total limit -> ended");
        yield return PressPanel(p.ui, "End panel"); yield return new WaitForSecondsRealtime(0.2f);
        var evs = Events(rig);
        var timeouts = Rows(evs, "event_type", "phase_timeout");
        Assert.AreEqual(new[] { "D1", "D2" }, timeouts.Select(r => evs.Get(r, "label")).ToArray(), "D1 timed out once, then the total budget ended D2");
        Assert.AreEqual(0, Rows(evs, "event_type", "correct_selection").Count, "nothing was found");
        Assert.AreEqual(1, Rows(evs, "event_type", "phase_start").Count(r => evs.Get(r, "label") == "d2_search"), "Destination 2 started exactly once");
        Assert.IsNotNull(d2);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ scene validation before part_start

    IEnumerator RefusedStart(string what, Action<P3> breakScene)
    {
        LogAssert.ignoreFailingMessages = true;   // RunPart3 logs the precise refusal as an error
        P3 p = null; yield return StartPart3(60f, 120f, x => p = x, breakScene);
        yield return new WaitForSecondsRealtime(0.3f);
        Assert.IsNull(Get<object>(p.rig.em, "Part3Assignment"), what + ": no assignment is kept");
        Assert.AreEqual("", Get<string>(p.rig.em, "RunningPartName"), what + ": the Part did not start");
        Assert.AreEqual(0, Rows(Events(p.rig), "event_type", "part_start").Count, what + ": no part_start was raised");
        Assert.AreEqual("", Get<string>(p.rig.em, "Target1BuildingId")); Assert.AreEqual("", Get<string>(p.rig.em, "Target2BuildingId"));
        Assert.IsFalse(OpenPanelButton(p.ui) != null, what + ": no panel was shown");
    }

    [UnityTest] public IEnumerator Part3Start_Refused_WhenALandmarkHasNoTargetSelectable() { yield return RefusedStart("no TargetSelectable", p => UnityEngine.Object.DestroyImmediate(TsOfBuilding(p.all.First(g => g.name == "BAT_GU_B")))); }
    [UnityTest] public IEnumerator Part3Start_Refused_WhenALandmarkOfAnUnusedZoneHasNoTargetSelectable() { yield return RefusedStart("unused zone", p => UnityEngine.Object.DestroyImmediate(TsOfBuilding(p.all.First(g => g.name == "BAT_OU_A")))); }
    [UnityTest] public IEnumerator Part3Start_Refused_WhenALandmarkColliderIsDisabled() { yield return RefusedStart("collider disabled", p => p.all.First(g => g.name == "BAT_OE_A").GetComponent<Collider>().enabled = false); }
    [UnityTest] public IEnumerator Part3Start_Refused_WhenALandmarkColliderIsATrigger() { yield return RefusedStart("trigger collider", p => p.all.First(g => g.name == "BAT_GE_B").GetComponent<Collider>().isTrigger = true); }
    [UnityTest] public IEnumerator Part3Start_Refused_WhenTwoLandmarksShareAnId() { yield return RefusedStart("shared id", p => Set(TsOfBuilding(p.all.First(g => g.name == "BAT_OU_B")), "targetId", Get<string>(TsOfBuilding(p.all.First(g => g.name == "BAT_GU_A")), "ResolvedTargetId"))); }
    [UnityTest]
    public IEnumerator Part3Start_Refused_WhenAZoneHasOnlyOneLandmark()
    {
        yield return RefusedStart("one landmark", p => { var z = Get<object>(p.rig.em, "part3ZoneOE"); Set(z, "buildingB", null); });
    }
    [UnityTest]
    public IEnumerator Part3Start_Refused_WhenADestinationPanelIsMissing()
    {
        yield return RefusedStart("no Destination 2 panel", p => Set(p.ui, "part3Dest2FullTemplate", null));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ synthetic pointer pipeline (NOT an XR ray)

    // "controller ray" = Physics.Raycast along +z from the landmark's x; "pointer click" = ExecuteEvents.ExecuteHierarchy(IPointerClickHandler), which skips disabled components exactly like the EventSystem.
    static GameObject RayHit(GameObject landmark)
    {
        Vector3 o = new Vector3(landmark.transform.position.x, landmark.transform.position.y, 0f);
        return Physics.Raycast(new Ray(o, Vector3.forward), out RaycastHit hit, 100f, ~0, QueryTriggerInteraction.Ignore) ? hit.collider.gameObject : null;
    }
    static GameObject PointerClick(GameObject hit)
    {
        var ped = new PointerEventData(null) { button = PointerEventData.InputButton.Left, clickCount = 1 };
        return hit == null ? null : ExecuteEvents.ExecuteHierarchy(hit, ped, ExecuteEvents.pointerClickHandler);
    }

    [UnityTest]
    public IEnumerator Part3Targets_SyntheticPointerPath_RayToColliderToTargetSelectable_CompletesOnlyTheActiveTarget_ExactlyOnce()
    {
        P3 p = null; yield return StartPart3(60f, 120f, x => p = x); var rig = p.rig;
        GameObject d1 = null, d2 = null;
        yield return ToD1Pending(p, (a, b) => { d1 = a; d2 = b; });
        string t1 = Get<string>(rig.em, "Target1BuildingId"), t2 = Get<string>(rig.em, "Target2BuildingId");
        Physics.SyncTransforms();
        Assert.AreSame(d1, RayHit(d1), "the ray reaches the Target 1 collider"); Assert.AreSame(d2, RayHit(d2), "the ray reaches the Target 2 collider");
        Assert.AreEqual(t1, Get<string>(TsOfBuilding(RayHit(d1)), "ResolvedTargetId"), "ResolvedTargetId of the hit object = the selected Target 1 id");

        // before the D1 Continue nothing is selectable: the pointer finds no enabled handler
        Assert.IsNull(PointerClick(RayHit(d1)), "TargetSelectable is disabled: no pointer click is delivered"); Assert.IsNull(PointerClick(RayHit(d2)));
        yield return PressPanel(p.ui, "D1 panel");
        yield return WaitUntil(() => Phase(rig) == "d1_search", 3f, "d1_search"); yield return null; yield return null;

        // Destination 2 landmark: no handler (disabled) -> no event, no completion
        Assert.IsNull(PointerClick(RayHit(d2))); yield return null;
        Assert.AreEqual("d1_search", Phase(rig));
        // the other landmark of the D1 zone IS clickable (allowWrongCandidateSelection) but wrong
        GameObject mate = p.all.First(g => g.name.StartsWith("BAT_GU_") && g != d1); Physics.SyncTransforms();
        Assert.IsNotNull(PointerClick(RayHit(mate))); yield return null; yield return null;
        Assert.AreEqual("d1_search", Phase(rig), "the zone mate does not complete Destination 1");
        // Target 1: completes Destination 1
        Assert.IsNotNull(PointerClick(RayHit(d1)));
        yield return WaitUntil(() => Phase(rig) == "d2_pending", 4f, "D1 found -> d2_pending");
        Assert.IsNull(PointerClick(RayHit(d1)), "after completion Target 1 no longer receives pointer clicks (repeated events cannot complete anything again)");

        yield return PressPanel(p.ui, "D2 panel");
        yield return WaitUntil(() => Phase(rig) == "d2_search", 3f, "d2_search"); yield return null; yield return null;
        Assert.IsNull(PointerClick(RayHit(d1)), "Target 1 is not interactable during Destination 2"); yield return null;
        Assert.AreEqual("d2_search", Phase(rig));
        Assert.IsNotNull(PointerClick(RayHit(d2)));
        yield return WaitUntil(() => Phase(rig) == "ended", 4f, "end");
        Assert.IsNull(PointerClick(RayHit(d2)), "a repeated ray event after completion is not delivered"); yield return null;
        yield return PressPanel(p.ui, "End panel"); yield return new WaitForSecondsRealtime(0.2f);

        var evs = Events(rig);
        var clicks = Rows(evs, "event_type", "building_click");
        Assert.AreEqual(new[] { Get<string>(TsOfBuilding(mate), "ResolvedTargetId"), t1, t2 }, clicks.Select(r => evs.Get(r, "target_id")).ToArray(), "only the delivered pointer clicks were recorded: the zone mate, Target 1, Target 2");
        var correct = Rows(evs, "event_type", "correct_selection");
        Assert.AreEqual(new[] { "D1", "D2" }, correct.Select(r => evs.Get(r, "label")).ToArray()); Assert.AreEqual(new[] { t1, t2 }, correct.Select(r => evs.Get(r, "target_id")).ToArray());
        Assert.AreEqual(1, Rows(evs, "event_type", "incorrect_selection").Count, "the zone mate");
    }
}
