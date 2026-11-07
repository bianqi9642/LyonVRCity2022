// Part2ContinueLockFlowTests.cs
// Integration tests of the Part 2 Continue-panel participant lock through the REAL ExperimentManager.RunPart2 coroutine (real UIManager + PanelController templates whose Continue buttons the
// test presses, real DataCaptureManager event writer) - the same harness as the Part 2 timing test (EventSemanticsTests) and the Destination 2 lock flow tests (partial class).
// What is real: RunPart2 order of operations, ParticipantControlEnabled transitions, the panels and their buttons, IsRiverTeleportAvailable, the scripted Continue teleport, the events.csv rows.
// What is not: no XR Origin / headset / city / Omnideck / controller rays; ParticipantControlLock's providers are covered separately in Destination2CheckpointLockTests (same gate).
// Editor only. The Part 3 behaviour is pinned by Destination2LockFlowTests (unchanged).

using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    sealed class P2 { public Rig rig; public Component ui; public Transform playerRig, tele; }

    IEnumerator StartPart2ForLockTests(float firstSeconds, float secondSeconds, Action<P2> done)
    {
        Rig rig = null;
        var camGo = new GameObject("Cam"); camGo.tag = "MainCamera"; camGo.AddComponent<Camera>(); _spawned.Add(camGo);
        yield return BuildRig(new RigOptions { part2 = true, selectedPartFallback = true, realPart = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.3f, npcCaptureEnabled = false,
            emSetup = em => { Set(em, "part2FirstDuration", firstSeconds); Set(em, "part2SecondDuration", secondSeconds); Set(em, "npcWarmupDuration", 0f); } }, r => rig = r);
        var ui = MakeUi("part2StartPanelTemplate", "part2ContinuePanelTemplate", "part2EndPanelTemplate");
        var rigT = Make("PlayerRig").transform; var p0 = Make("Start0").transform; var p1 = Make("Tele0").transform; p1.position = new Vector3(12f, 0f, 7f);
        var groupType = Type.GetType("ExperimentManager+Part2Group, Assembly-CSharp");
        var group = Activator.CreateInstance(groupType); Set(group, "startPositions", new[] { p0 }); Set(group, "teleportPositions", new[] { p1 });
        var groups = Array.CreateInstance(groupType, 1); groups.SetValue(group, 0);
        Set(rig.em, "uiManager", ui); Set(rig.em, "playerRig", rigT); Set(rig.em, "spawner", rig.spawner); Set(rig.em, "part2Groups", groups);
        yield return null;
        Call(rig.em, "StartPart2");
        done(new P2 { rig = rig, ui = ui, playerRig = rigT, tele = p1 });
    }

    // Drives Start -> task 1 -> Continue panel and returns at the moment the Continue panel is open. states: [0] Start panel, [1] task 1, [2] Continue panel.
    IEnumerator ToP2ContinuePanel(P2 p, Action<bool[]> statesOut)
    {
        var rig = p.rig; var st = new bool[3];
        yield return WaitUntil(() => OpenPanelButton(p.ui) != null, 6f, "Part 2 Start panel"); st[0] = Ctl(rig.em);
        OpenPanelButton(p.ui).onClick.Invoke(); yield return null; yield return null;
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_task1", 3f, "p2_task1"); yield return null; st[1] = Ctl(rig.em);
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_continue_pending", 6f, "p2_continue_pending");
        yield return WaitUntil(() => OpenPanelButton(p.ui) != null, 3f, "Continue panel"); yield return null; st[2] = Ctl(rig.em);
        statesOut(st);
    }

    [UnityTest]
    public IEnumerator Part2Flow_ParticipantControl_Task1Free_ContinuePanelLocked_ReleasedByTheContinue_ExactlyOnce_TeleportUnchanged()
    {
        P2 p = null; yield return StartPart2ForLockTests(0.6f, 3f, x => p = x); var rig = p.rig; bool[] st = null;
        yield return ToP2ContinuePanel(p, s => st = s);
        Assert.IsFalse(st[0], "Start panel: locked (unchanged)");
        Assert.IsTrue(st[1], "A: normal Part 2 task 1: participant control is enabled (unchanged)");
        Assert.IsFalse(st[2], "B/C: Continue panel: locked from the moment it is shown");
        Assert.IsFalse(Get<bool>(rig.em, "IsRiverTeleportAvailable"), "river points follow the gate");
        Assert.IsFalse(NpcGate(), "NPC behaviour is not paused by the participant lock");
        Assert.IsNotNull(OpenPanelButton(p.ui), "the Continue button stays available (UI interaction is not part of the gate)");
        yield return new WaitForSecondsRealtime(0.4f); Assert.IsFalse(Ctl(rig.em), "C: stays locked for as long as the panel is open");
        Assert.AreEqual(Vector3.zero, p.playerRig.position, "nothing moved the rig before Continue");

        OpenPanelButton(p.ui).onClick.Invoke(); yield return null; yield return null;   // D: Continue
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_task2", 3f, "p2_task2");
        Assert.IsTrue(Ctl(rig.em), "D: released by the Continue"); Assert.IsTrue(Get<bool>(rig.em, "IsRiverTeleportAvailable"));
        var evs = Events(rig);
        if (Rows(evs, "event_type", "teleport_rejected").Count == 0)
            Assert.AreEqual(p.tele.position.x, p.playerRig.position.x, 0.01f, "the scripted Continue teleport still lands on the configured position (unchanged)");
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "ended", 8f, "end"); yield return PressPanel(p.ui, "End panel"); yield return new WaitForSecondsRealtime(0.2f);
        Assert.IsFalse(Ctl(rig.em), "End panel / session end: locked (unchanged)");

        evs = Events(rig); var ce = ControlEvents(evs);
        CollectionAssert.AreEqual(new[] { "P2|locked;reason=part2_continue_panel_shown", "P2|unlocked;reason=part2_continue_confirmed" }, ce, "one lock and one unlock event, both for P2");
        Assert.AreEqual(1, Rows(evs, "event_type", "panel_confirmed").Count(r => evs.Get(r, "label") == "part2_continue"), "the Continue callback ran exactly once");
        Assert.AreEqual(1, Rows(evs, "event_type", "phase_start").Count(r => evs.Get(r, "label") == "p2_task2"), "the experiment state advances exactly once");
        // order: phase p2_continue_pending -> locked -> panel_shown -> panel_confirmed -> unlocked / phase p2_task2
        int iPhase = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "phase_start" && evs.Get(r, "label") == "p2_continue_pending");
        int iLock = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "participant_control" && evs.Get(r, "value_str").StartsWith("locked"));
        int iShown = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "panel_shown" && evs.Get(r, "label") == "part2_continue");
        int iConf = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "panel_confirmed" && evs.Get(r, "label") == "part2_continue");
        int iUnl = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "participant_control" && evs.Get(r, "value_str").StartsWith("unlocked"));
        Assert.Less(iPhase, iLock); Assert.Less(iLock, iShown); Assert.Less(iShown, iConf); Assert.Less(iConf, iUnl);
        AssertEventsCsvIsParticipantOnly(evs);
    }

    [UnityTest]
    public IEnumerator Part2Flow_OptionOff_KeepsThePreviousBehaviour_NoLock_NoControlEvents()
    {
        P2 p = null; yield return StartPart2ForLockTests(0.6f, 1f, x => p = x); var rig = p.rig; Set(rig.em, "lockParticipantWhilePart2ContinuePanel", false); bool[] st = null;
        yield return ToP2ContinuePanel(p, s => st = s);
        Assert.IsTrue(st[2], "option off: control stays enabled at the Continue panel (previous behaviour)");
        OpenPanelButton(p.ui).onClick.Invoke(); yield return null; yield return null; yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_task2", 3f, "p2_task2");
        Assert.AreEqual(0, ControlEvents(Events(rig)).Count);
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "ended", 6f, "end"); yield return PressPanel(p.ui, "End panel");
    }

    [UnityTest]
    public IEnumerator Part2Flow_DuplicateContinueEvent_TeleportsAndReleasesOnlyOnce()
    {
        P2 p = null; yield return StartPart2ForLockTests(0.6f, 3f, x => p = x); var rig = p.rig; bool[] st = null;
        yield return ToP2ContinuePanel(p, s => st = s); Assert.IsFalse(st[2]);
        var btn = OpenPanelButton(p.ui); btn.onClick.Invoke(); btn.onClick.Invoke();   // two events in the same frame, before the panel is destroyed
        yield return null; yield return null;
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_task2", 3f, "p2_task2");
        var evs = Events(rig);
        Assert.AreEqual(1, Rows(evs, "event_type", "panel_confirmed").Count(r => evs.Get(r, "label") == "part2_continue"), "the duplicate Continue event is ignored");
        Assert.AreEqual(1, ControlEvents(evs).Count(c => c.Contains("unlocked")), "released once");
        Assert.IsTrue(Ctl(rig.em));
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "ended", 8f, "end"); yield return PressPanel(p.ui, "End panel");
    }

    [UnityTest]
    public IEnumerator Part2Flow_SessionEndedWhileLocked_TheContinueNeverReopensControl()
    {
        P2 p = null; yield return StartPart2ForLockTests(0.6f, 3f, x => p = x); var rig = p.rig; bool[] st = null;
        yield return ToP2ContinuePanel(p, s => st = s); Assert.IsFalse(st[2]);
        Call(rig.em, "TerminateSession", "Part2");   // legitimate shutdown / cancel while the checkpoint lock is held
        Assert.IsFalse(Ctl(rig.em));
        var btn = OpenPanelButton(p.ui); if (btn != null) btn.onClick.Invoke();
        yield return null; yield return null;
        Assert.IsFalse(Ctl(rig.em), "a release after the session ended never re-opens participant control");
        Assert.AreEqual(0, ControlEvents(Events(rig)).Count(c => c.Contains("unlocked")), "no unlock is reported for a terminal state");
    }

    [UnityTest]
    public IEnumerator Part2Flow_ExistingLockAlreadyActive_IsNotAddedToOrReleasedByTheContinueLock()
    {
        P2 p = null; yield return StartPart2ForLockTests(30f, 3f, x => p = x); var rig = p.rig;
        yield return WaitUntil(() => OpenPanelButton(p.ui) != null, 6f, "Part 2 Start panel");
        Assert.IsFalse(Ctl(rig.em), "Start panel: locked by the Start-panel state");
        Call(rig.em, "LockParticipantForCheckpoint", "P2", "test");        // control already disabled: nothing to add
        Call(rig.em, "ReleaseParticipantCheckpointLock", "P2", "test");    // and nothing to release
        Assert.IsFalse(Ctl(rig.em), "a checkpoint helper never opens a lock that belongs to another state");
        Assert.AreEqual(0, ControlEvents(Events(rig)).Count);
        OpenPanelButton(p.ui).onClick.Invoke(); yield return null; yield return null;
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_task1", 3f, "p2_task1");
        Assert.IsTrue(Ctl(rig.em), "the trial start still releases the Start-panel lock (unchanged)");
    }

    [Test]
    public void Part2Flow_LockOption_DefaultsOn_AndPart3OptionIsUnchanged()
    {
        var go = new GameObject("EM_Defaults"); _spawned.Add(go);
        go.SetActive(false);   // do not run Awake/Start: only the serialized defaults are inspected
        var em = go.AddComponent(T("ExperimentManager"));
        Assert.IsTrue(Get<bool>(em, "lockParticipantWhilePart2ContinuePanel"));
        Assert.IsTrue(Get<bool>(em, "lockParticipantWhileDestination2Panel"));
    }
}
