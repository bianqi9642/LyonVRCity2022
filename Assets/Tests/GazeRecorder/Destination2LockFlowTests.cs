// Destination2LockFlowTests.cs
// Integration tests of the Destination 2 checkpoint lock through the REAL ExperimentManager.RunPart3 coroutine (real UIManager + PanelController templates whose Continue buttons the
// test presses, real TargetSelectable, real DataCaptureManager event writer) - the same harness as the F-08 / F-11 tests (NpcLifecycleTests.StartPart3, partial class).
// What is real: RunPart3 order of operations, ParticipantControlEnabled transitions, the panels and their buttons, TargetSelectable.Select gating, IsRiverTeleportAvailable, the events.csv rows.
// What is not: no XR Origin / headset / city / Omnideck / controller rays, and ParticipantControlLock's providers are covered separately in Destination2CheckpointLockTests. Editor only.

using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    static bool Ctl(Component em) => Get<bool>(em, "ParticipantControlEnabled");
    static System.Collections.Generic.List<string> ControlEvents(Csv evs) => evs.Rows.Where(r => evs.Get(r, "event_type") == "participant_control").Select(r => evs.Get(r, "label") + "|" + evs.Get(r, "value_str")).ToList();

    // Drives Start -> D1 -> (D1 found) -> D2 panel and returns at the moment the D2 panel is open.
    IEnumerator ToD2Panel(P3 p, Action<GameObject> d2Out, Action<bool[]> statesOut)
    {
        var rig = p.rig; var st = new bool[5];
        yield return WaitUntil(() => OpenPanelButton(p.ui) != null, 6f, "Start panel"); st[0] = Ctl(rig.em);                   // [0] Start panel open
        OpenPanelButton(p.ui).onClick.Invoke(); yield return null; yield return null;
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d1_pending", 3f, "d1_pending");
        yield return WaitUntil(() => OpenPanelButton(p.ui) != null, 3f, "D1 panel"); st[1] = Ctl(rig.em);                      // [1] D1 panel open
        GameObject d1 = null, d2 = null;
        yield return WaitUntil(() => { d1 = Get<GameObject>(rig.dcm, "AssignedBuildingD1"); d2 = Get<GameObject>(rig.dcm, "AssignedBuildingD2"); return d1 != null && d2 != null; }, 3f, "assigned destinations");
        OpenPanelButton(p.ui).onClick.Invoke(); yield return null; yield return null;
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d1_search", 3f, "d1_search"); st[2] = Ctl(rig.em);   // [2] D1 search
        yield return null;
        Call(d1.GetComponent(T("TargetSelectable")), "Select");
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d2_pending", 4f, "D1 found -> d2_pending");
        yield return WaitUntil(() => OpenPanelButton(p.ui) != null, 3f, "D2 panel"); yield return null; st[3] = Ctl(rig.em);   // [3] D2 panel open
        d2Out(d2); statesOut(st);
    }

    [UnityTest]
    public IEnumerator Part3Flow_ParticipantControl_StartLocked_D1Free_D2Locked_ReleasedByTheD2Continue_ExactlyOnce()
    {
        P3 p = null; yield return StartPart3(60f, 120f, x => p = x); var rig = p.rig; GameObject d2 = null; bool[] st = null;
        yield return ToD2Panel(p, g => d2 = g, s => st = s);
        Assert.IsFalse(st[0], "Start panel: locked (unchanged)"); Assert.IsTrue(st[1], "D1 panel: participant control is enabled (unchanged: released at BeginTrial, not at the D1 Continue)");
        Assert.IsTrue(st[2], "D1 search: enabled (unchanged)"); Assert.IsFalse(st[3], "D2 panel: locked from the moment it is shown");
        Assert.IsFalse(Get<bool>(rig.em, "IsRiverTeleportAvailable"), "river points follow the gate"); Assert.IsFalse(NpcGate(), "NPC behaviour is not paused by the participant lock");
        int clicksBefore = Rows(Events(rig), "event_type", "building_click").Count; Call(d2.GetComponent(T("TargetSelectable")), "Select"); yield return null;
        Assert.AreEqual(clicksBefore, Rows(Events(rig), "event_type", "building_click").Count, "building selection is ignored while locked");
        OpenPanelButton(p.ui).onClick.Invoke(); yield return null; yield return null;           // the D2 Continue interaction works under the lock
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d2_search", 3f, "d2_search");
        Assert.IsTrue(Ctl(rig.em), "released by the D2 Continue"); Assert.IsTrue(Get<bool>(rig.em, "IsRiverTeleportAvailable"));
        yield return null; yield return null;
        Call(d2.GetComponent(T("TargetSelectable")), "Select");   // correct D2 -> ended
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "ended", 4f, "end"); yield return PressPanel(p.ui, "End panel"); yield return new WaitForSecondsRealtime(0.2f);
        Assert.IsFalse(Ctl(rig.em), "End panel / session end: locked (unchanged)");
        var evs = Events(rig); var ce = ControlEvents(evs);
        CollectionAssert.AreEqual(new[] { "D2|locked;reason=d2_panel_shown", "D2|unlocked;reason=d2_continue_confirmed" }, ce, "one lock and one unlock event, both for D2");
        Assert.AreEqual(1, Rows(evs, "event_type", "part3_d2_continue_confirmed").Count); Assert.AreEqual(1, Rows(evs, "event_type", "phase_start").Count(r => evs.Get(r, "label") == "d2_search"), "the experiment state advances exactly once");
        // order: phase d2_pending -> locked -> destination_shown -> panel_shown ... part3_d2_continue_confirmed ... unlocked -> phase d2_search
        int iPhase = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "phase_start" && evs.Get(r, "label") == "d2_pending"), iLock = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "participant_control" && evs.Get(r, "value_str").StartsWith("locked"));
        int iShown = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "panel_shown" && evs.Get(r, "label") == "part3_dest2"), iConf = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "part3_d2_continue_confirmed");
        int iUnl = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "participant_control" && evs.Get(r, "value_str").StartsWith("unlocked")), iSearch = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "phase_start" && evs.Get(r, "label") == "d2_search");
        Assert.Less(iPhase, iLock); Assert.Less(iLock, iShown); Assert.Less(iShown, iConf); Assert.Less(iConf, iUnl); Assert.Less(iUnl, iSearch);
        AssertEventsCsvIsParticipantOnly(evs);
    }

    static bool NpcGate() => GetStatic<bool>(T("NpcSimulationGate"), "Paused");

    [UnityTest]
    public IEnumerator Part3Flow_OptionOff_KeepsThePreviousBehaviour_NoLock_NoControlEvents()
    {
        P3 p = null; yield return StartPart3(60f, 120f, x => p = x); var rig = p.rig; Set(rig.em, "lockParticipantWhileDestination2Panel", false); GameObject d2 = null; bool[] st = null;
        yield return ToD2Panel(p, g => d2 = g, s => st = s);
        Assert.IsTrue(st[3], "option off: control stays enabled at the D2 panel (previous behaviour)");
        int before = Rows(Events(rig), "event_type", "building_click").Count; Call(d2.GetComponent(T("TargetSelectable")), "Select"); yield return null; yield return null;
        Assert.AreEqual(before + 1, Rows(Events(rig), "event_type", "building_click").Count, "previous behaviour: a click at the D2 panel is recorded");
        OpenPanelButton(p.ui).onClick.Invoke(); yield return null; yield return null; yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d2_search", 3f, "d2_search");
        Assert.AreEqual(0, ControlEvents(Events(rig)).Count);
        Call(d2.GetComponent(T("TargetSelectable")), "Select"); yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "ended", 4f, "end"); yield return PressPanel(p.ui, "End panel");
    }

    [UnityTest]
    public IEnumerator Part3Flow_SessionEndedWhileLocked_TheD2ContinueNeverReopensControl()
    {
        P3 p = null; yield return StartPart3(60f, 120f, x => p = x); var rig = p.rig; GameObject d2 = null; bool[] st = null;
        yield return ToD2Panel(p, g => d2 = g, s => st = s); Assert.IsFalse(st[3]);
        Call(rig.em, "TerminateSession", "Part3");   // legitimate shutdown while the checkpoint lock is held
        Assert.IsFalse(Ctl(rig.em));
        var btn = OpenPanelButton(p.ui); if (btn != null) btn.onClick.Invoke();
        yield return null; yield return null;
        Assert.IsFalse(Ctl(rig.em), "a release after the session ended never re-opens participant control");
        Assert.AreEqual(0, ControlEvents(Events(rig)).Count(c => c.Contains("unlocked")), "no unlock is reported for a terminal state");
    }
}
