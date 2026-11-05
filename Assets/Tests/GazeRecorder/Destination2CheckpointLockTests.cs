// Destination2CheckpointLockTests.cs
// Tests for the participant lock at the Part 3 Destination 2 panel (ExperimentManager.LockParticipantForCheckpoint / ReleaseParticipantCheckpointLock).
//
// WHY: in session 20261105T170113Z_67e1fc797de5 the Destination 2 panel (part3_dest2) stayed open for 303 s while the participant kept walking (307 m) and used a river point; the panel is placed ONCE in world
// space, so it was left behind and the Part never continued. The panel is a mandatory checkpoint (RunPart3 waits for its Continue click; the D2 search clock starts there), so it now takes the same
// ParticipantControlEnabled gate the Start panel uses.
//
// WHAT IS REAL AND WHAT IS NOT
//   * REAL production components (Assembly-CSharp, reached by reflection): ExperimentManager (lock / release helpers, SetParticipantControl, IsRiverTeleportAvailable), ParticipantControlLock, GuardedTeleportationProvider,
//     PanelController, NpcSimulationGate; the XRI ActionBasedContinuousTurnProvider as the locked provider.
//   * SYNTHETIC: no scene, no XR Origin, no city. The RunPart3 coroutine itself is NOT executed here (it needs the whole scene); its ORDER (lock before the panel is shown, release in the Continue callback,
//     safety release after the wait, no lock in the Start / D1 sections, BeginTrial unchanged) is pinned by source-structure tests, which say so.
//   * NOT covered: Quest Pro / Quest Link runtime behaviour (Omnideck provider, controller rays on the real panel, panel visibility). Needs a device pass.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using static Reflect;

public class Destination2CheckpointLockTests
{
    readonly List<UnityEngine.Object> _objs = new List<UnityEngine.Object>();
    [TearDown] public void TearDown() { foreach (var o in _objs) if (o != null) UnityEngine.Object.DestroyImmediate(o); _objs.Clear(); EventLog.Clear(); }

    // ---------------------------------------------------------------- captured experiment events (the real OnExperimentEvent)
    sealed class Capture { public List<(string type, string label, string value)> Events = new List<(string, string, string)>(); public int ControlChanges; public List<bool> ControlValues = new List<bool>();
        public void Handle<TInfo>(TInfo info) { var t = typeof(TInfo); Events.Add(((string)t.GetField("type").GetValue(info), (string)t.GetField("label").GetValue(info), (string)t.GetField("stringValue").GetValue(info))); }
        public void Changed(bool v) { ControlChanges++; ControlValues.Add(v); } }
    static readonly List<Capture> EventLog = new List<Capture>();

    (Component em, Capture cap) NewManager()
    {
        var go = new GameObject("ExperimentManagerForTest"); go.SetActive(false); _objs.Add(go);   // inactive: Awake / OnEnable never run
        var em = go.AddComponent(T("ExperimentManager")); var cap = new Capture(); EventLog.Add(cap);
        var ev = em.GetType().GetEvent("OnExperimentEvent"); var m = typeof(Capture).GetMethod("Handle").MakeGenericMethod(T("ExperimentEventInfo"));
        ev.AddEventHandler(em, Delegate.CreateDelegate(ev.EventHandlerType, cap, m));
        em.GetType().GetEvent("OnParticipantControlChanged").AddEventHandler(em, (Action<bool>)cap.Changed);
        return (em, cap);
    }
    static void SetControl(Component em, bool v) => Call(em, "SetParticipantControl", v);
    static bool Control(Component em) => Get<bool>(em, "ParticipantControlEnabled");
    static void SetPrivate(object o, string field, object v) { var f = o.GetType().GetField(field, All); Assert.IsNotNull(f, field); f.SetValue(o, f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, v) : v); }
    static void SetEndPanelActive(Component em, bool v) { var f = em.GetType().GetField("<EndPanelActive>k__BackingField", All); Assert.IsNotNull(f); f.SetValue(em, v); }
    static void Lock(Component em) => Call(em, "LockParticipantForCheckpoint", "D2", "d2_panel_shown");
    static void Release(Component em) => Call(em, "ReleaseParticipantCheckpointLock", "D2", "d2_continue_confirmed");

    // ---------------------------------------------------------------- lock / release semantics (real ExperimentManager)

    [Test]
    public void Lock_DisablesParticipantControl_Once_AndRecordsALockedEvent()
    {
        var (em, cap) = NewManager(); SetControl(em, true); cap.ControlChanges = 0;
        Lock(em); Lock(em);   // idempotent
        Assert.IsFalse(Control(em), "the D2 panel closes the single participant-control gate");
        Assert.AreEqual(1, cap.ControlChanges, "one change notification, however often it is requested");
        var locked = cap.Events.Where(e => e.type == "participant_control").ToList();
        Assert.AreEqual(1, locked.Count); Assert.AreEqual("D2", locked[0].label); StringAssert.StartsWith("locked;reason=d2_panel_shown", locked[0].value);
    }

    [Test]
    public void Release_ReenablesParticipantControl_ExactlyOnce_AndIsIdempotent()
    {
        var (em, cap) = NewManager(); SetControl(em, true); Lock(em); cap.ControlChanges = 0;
        Release(em); Release(em); Release(em);
        Assert.IsTrue(Control(em)); Assert.AreEqual(1, cap.ControlChanges, "the state is released exactly once (a duplicate callback / the safety net changes nothing)");
        var ev = cap.Events.Where(e => e.type == "participant_control").Select(e => e.value).ToList();
        Assert.AreEqual(2, ev.Count); StringAssert.StartsWith("unlocked;reason=d2_continue_confirmed", ev[1]);
    }

    [Test]
    public void Lock_NeverTouchesAnotherStatesLock_StartPanelOrBeforeTheTrial()
    {
        var (em, cap) = NewManager();   // ParticipantControlEnabled is false before the Start click (WaitingForStart)
        cap.ControlChanges = 0; Lock(em); Release(em);
        Assert.IsFalse(Control(em), "a release without a checkpoint lock must never open the Start-panel lock"); Assert.AreEqual(0, cap.ControlChanges);
        Assert.AreEqual(0, cap.Events.Count(e => e.type == "participant_control"));
    }

    [Test]
    public void Release_NeverReopensTheEndPanelOrAnEndedSession()
    {
        var (em, _) = NewManager(); SetControl(em, true); Lock(em);
        SetEndPanelActive(em, true); SetControl(em, false);   // what HandleEndPanelDisplayed does
        Release(em); Assert.IsFalse(Control(em), "End panel visible: the lock belongs to the End panel");
        var (em2, _) = NewManager(); SetControl(em2, true); Lock(em2);
        var f = em2.GetType().GetField("<SessionEnded>k__BackingField", All); f.SetValue(em2, true); SetControl(em2, false);
        Release(em2); Assert.IsFalse(Control(em2), "session ended: terminal lock stays");
        var (em3, cap3) = NewManager(); SetControl(em3, true); SetEndPanelActive(em3, true); Lock(em3);
        Assert.IsTrue(Control(em3), "no checkpoint lock is taken once the End panel is up"); Assert.AreEqual(0, cap3.Events.Count(e => e.type == "participant_control"));
    }

    [Test]
    public void BeginTrial_DropsAStaleCheckpointLock_Structure()
    {
        // BeginTrial clears the flag before SetParticipantControl(true), so a new trial can never inherit a checkpoint lock (structure test: BeginTrial needs the whole scene).
        string bt = Between("private void BeginTrial()", "// End of the timed trial");
        Assert.Less(bt.IndexOf("_checkpointLockActive = false;", StringComparison.Ordinal), bt.IndexOf("SetParticipantControl(true);", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- what the lock actually switches off (real consumers of the gate)

    Type XriTurn => Type.GetType("UnityEngine.XR.Interaction.Toolkit.ActionBasedContinuousTurnProvider, Unity.XR.Interaction.Toolkit");

    [Test]
    public void ParticipantControlLock_Disables_ContinuousProviders_WhileLocked_AndRestoresThePriorState()
    {
        Assert.IsNotNull(XriTurn, "XRI ActionBasedContinuousTurnProvider type not found");
        var (em, _) = NewManager(); SetControl(em, true);
        var rig = new GameObject("Rig"); _objs.Add(rig);
        var move = (Behaviour)rig.AddComponent(XriTurn);                                   // a continuous provider that is ON
        var offOnPurpose = (Behaviour)new GameObject("SnapStandIn").AddComponent(XriTurn); _objs.Add(offOnPurpose.gameObject); offOnPurpose.enabled = false;   // e.g. the Snap Turn provider
        var lockGo = new GameObject("LockGo"); lockGo.SetActive(false); _objs.Add(lockGo);
        var plc = lockGo.AddComponent(T("ParticipantControlLock")); Set(plc, "experimentManager", em); lockGo.SetActive(true);
        Assert.IsTrue(move.enabled, "unlocked: untouched");
        Lock(em); Assert.IsFalse(move.enabled, "D2 panel: continuous provider is off");
        Release(em); Assert.IsTrue(move.enabled, "released: back to exactly the pre-lock state"); Assert.IsFalse(offOnPurpose.enabled, "a provider disabled on purpose stays disabled");
        // head tracking, UI and interactors are not LocomotionProviders: not touched
        var cam = new GameObject("Cam").AddComponent<Camera>(); _objs.Add(cam.gameObject); Lock(em); Assert.IsTrue(cam.enabled); Release(em);
    }

    [Test]
    public void ParticipantControlLock_Excludes_TheTeleportProviderItself_TeleportIsGatedAtTheSource()
    {
        var (em, _) = NewManager(); SetControl(em, true);
        var go = new GameObject("TpGo"); go.SetActive(false); _objs.Add(go); var tp = (Behaviour)go.AddComponent(T("GuardedTeleportationProvider")); Set(tp, "experimentManager", em); go.SetActive(true);
        var lockGo = new GameObject("LockGo"); lockGo.SetActive(false); _objs.Add(lockGo); var plc = lockGo.AddComponent(T("ParticipantControlLock")); Set(plc, "experimentManager", em); lockGo.SetActive(true);
        Lock(em); Assert.IsTrue(tp.enabled, "the provider stays enabled; it refuses requests itself (next test)"); Release(em);
    }

    object NewTeleportRequest(Vector3 dest) { var t = Type.GetType("UnityEngine.XR.Interaction.Toolkit.TeleportRequest, Unity.XR.Interaction.Toolkit"); Assert.IsNotNull(t); object r = Activator.CreateInstance(t); t.GetField("destinationPosition").SetValue(r, dest); return r; }

    [Test]
    public void GuardedTeleportationProvider_RejectsEveryXriTeleport_WhileTheD2LockIsActive_AndNoLongerForThatReasonAfterRelease()
    {
        var (em, cap) = NewManager(); SetControl(em, true); SetPrivate(em, "currentRunningPart", 3);   // Part3 running
        var go = new GameObject("TpGo"); go.SetActive(false); _objs.Add(go); var tp = (Component)go.AddComponent(T("GuardedTeleportationProvider")); Set(tp, "experimentManager", em); go.SetActive(true);
        Lock(em);
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
        bool ok = (bool)Call(tp, "QueueTeleportRequest", NewTeleportRequest(new Vector3(10, 0, 10)));
        Assert.IsFalse(ok, "locked: the request is declined before anything can move");
        var rej = cap.Events.Where(e => e.type == "teleport_rejected").Select(e => e.value).ToList(); CollectionAssert.Contains(rej, "participant_locked");
        Release(em); cap.Events.Clear();
        ok = (bool)Call(tp, "QueueTeleportRequest", NewTeleportRequest(new Vector3(10, 0, 10)));
        var after = cap.Events.Where(e => e.type == "teleport_rejected").Select(e => e.value).ToList();
        CollectionAssert.DoesNotContain(after, "participant_locked", "after the release the gate is no longer the reason (ordinary policy applies again)");
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
    }

    [Test]
    public void RiverTeleport_IsUnavailable_WhileTheD2LockIsActive_AndAvailableAfterRelease()
    {
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;   // RestrictRiverTeleport resolves the session settings and logs the missing ExternalSessionController of this synthetic setup
        var (em, _) = NewManager(); SetControl(em, true); SetPrivate(em, "currentRunningPart", 3);
        Assert.IsTrue(Get<bool>(em, "IsRiverTeleportAvailable"), "Part 3 running, control enabled");
        Lock(em); Assert.IsFalse(Get<bool>(em, "IsRiverTeleportAvailable"), "the river-point rule reads ParticipantControlEnabled (RiverTeleportPoint.TryTeleport and RiverPointAvailability use it)");
        Release(em); Assert.IsTrue(Get<bool>(em, "IsRiverTeleportAvailable"));
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
    }

    // ---------------------------------------------------------------- the panel stays usable; NPC systems are independent

    [Test]
    public void PanelContinueButton_Works_WhileLocked_AndConfirmsOnce()
    {
        var (em, _) = NewManager(); SetControl(em, true); Lock(em); Assert.IsFalse(Control(em));
        var panel = new GameObject("Panel"); _objs.Add(panel); var pc = (Component)panel.AddComponent(T("PanelController")); var btnGo = new GameObject("Button"); btnGo.transform.SetParent(panel.transform);
        var btn = btnGo.AddComponent<Button>(); Set(pc, "confirmButton", btn);
        int confirms = 0; Call(pc, "Setup", null, (Action)(() => { confirms++; Release(em); }), false);
        btn.onClick.Invoke(); btn.onClick.Invoke();   // the second click would hit a destroyed panel in the scene; here the listener is still wired: the release must still be a no-op
        Assert.GreaterOrEqual(confirms, 1); Assert.IsTrue(Control(em), "the Continue interaction succeeded under the lock and released it");
        Assert.AreEqual(1, EventLog.Last().Events.Count(e => e.type == "participant_control" && e.value.StartsWith("unlocked")), "one unlock event however many callbacks");
    }

    [Test]
    public void ParticipantLock_DoesNotTouchTheNpcSimulationGate()
    {
        var (em, _) = NewManager(); SetControl(em, true); var gate = T("NpcSimulationGate");
        bool before = GetStatic<bool>(gate, "Paused");
        Lock(em); Assert.AreEqual(before, GetStatic<bool>(gate, "Paused")); Release(em); Assert.AreEqual(before, GetStatic<bool>(gate, "Paused"));
        var helpers = Between("private void LockParticipantForCheckpoint", "// Start of the timed trial");
        foreach (var banned in new[] { "spawner", "Npc", "timeScale", "Audio", "Gaze", "Pause" }) StringAssert.DoesNotContain(banned, helpers, "the lock helpers must stay participant-only: " + banned);
    }

    // ---------------------------------------------------------------- source-structure tests (RunPart3 is not executed here: it needs the whole scene)
    static string Source() { var p = Path.Combine(Application.dataPath, "Scripts", "ExperimentManager.cs"); if (!File.Exists(p)) Assert.Ignore("ExperimentManager.cs not part of this project: " + p); return File.ReadAllText(p); }
    static string Between(string a, string b) { var s = Source(); int i = s.IndexOf(a, StringComparison.Ordinal), j = s.IndexOf(b, i + a.Length, StringComparison.Ordinal); Assert.Greater(i, 0, a); Assert.Greater(j, i, b); return s.Substring(i, j - i); }

    [Test]
    public void RunPart3_LocksBeforeTheD2PanelIsShown_ReleasesInTheContinueCallback_AndHasASafetyRelease()
    {
        string d2 = Between("// ============================ DESTINATION 2 ============================", "// ============================ END ============================");
        int phase = d2.IndexOf("SetPhase(\"d2_pending\")", StringComparison.Ordinal), lockAt = d2.IndexOf("LockParticipantForCheckpoint(\"D2\"", StringComparison.Ordinal), show = d2.IndexOf("uiManager.ShowPart3Destination2Panel(", StringComparison.Ordinal);
        int stamp = d2.IndexOf("p3Timing.RecordD2Continue(CaptureClock.TAbs);\n                dest2Confirmed = true;", StringComparison.Ordinal);
        int cbRelease = d2.IndexOf("ReleaseParticipantCheckpointLock(\"D2\", \"d2_continue_confirmed\");   // same moment", StringComparison.Ordinal);
        int wait = d2.IndexOf("yield return new WaitUntil(() => dest2Confirmed);", StringComparison.Ordinal), safety = d2.IndexOf("ReleaseParticipantCheckpointLock(\"D2\"", wait, StringComparison.Ordinal), search = d2.IndexOf("SetPhase(\"d2_search\")", StringComparison.Ordinal);
        Assert.Greater(lockAt, phase, "lock after the phase is set"); Assert.Less(lockAt, show, "lock BEFORE the panel is shown (the placement may wait for a steady head pose)");
        Assert.Greater(stamp, 0, "the D2 Continue clock is stamped first in the callback"); Assert.Greater(cbRelease, stamp, "released in the callback, after the clock stamp");
        Assert.Greater(safety, wait); Assert.Less(safety, search, "safety release between the wait and the next phase"); StringAssert.Contains("if (lockParticipantWhileDestination2Panel)", d2.Substring(lockAt - 60, 60));
        Assert.AreEqual(1, Regex.Matches(d2, "ShowPart3Destination2Panel\\(").Count); Assert.AreEqual(1, Regex.Matches(d2, "SetPhase\\(\"d2_search\"\\)").Count, "the state advance stays a single statement");
    }

    [Test]
    public void StartPanelAndDestination1_Sections_AreUnchanged_NoCheckpointLock_BeginTrialStillOpensControlAfterTheNpcResume()
    {
        string start = Between("uiManager.ShowPart3StartPanel(() => { startClickT", "SetPhase(\"d1_pending\")"), d1 = Between("// ============================ DESTINATION 1 ============================", "// ============================ DESTINATION 2 ============================");
        foreach (var s in new[] { start, d1 }) { StringAssert.DoesNotContain("LockParticipantForCheckpoint", s); StringAssert.DoesNotContain("ReleaseParticipantCheckpointLock", s); StringAssert.DoesNotContain("SetParticipantControl", s); }
        string bt = Between("private void BeginTrial()", "// End of the timed trial");
        Assert.Less(bt.IndexOf("ResumeNpcBehaviourForTrialStart();", StringComparison.Ordinal), bt.IndexOf("SetParticipantControl(true);", StringComparison.Ordinal), "NPC resume, then participant control, then OnTrialStart - as before");
        Assert.Less(bt.IndexOf("SetParticipantControl(true);", StringComparison.Ordinal), bt.IndexOf("OnTrialStart?.Invoke();", StringComparison.Ordinal));
        Assert.AreEqual(2, Regex.Matches(Source(), @"^\s*SetParticipantControl\(true\);", RegexOptions.Multiline).Count, "BeginTrial and the checkpoint release are the only statements that open participant control");
    }

    [Test]
    public void DestinationLockOption_DefaultsToOn_AndTheSceneDoesNotOverrideIt()
    {
        var em = NewManager().em; Assert.IsTrue(Get<bool>(em, "lockParticipantWhileDestination2Panel"), "default: locked at the D2 panel");
        var scene = Path.Combine(Application.dataPath, "Scenes", "LyonVRVity35.unity"); if (!File.Exists(scene)) { Assert.Pass("scene file not part of this project: field default applies"); return; }
        var m = Regex.Match(File.ReadAllText(scene), @"^\s*lockParticipantWhileDestination2Panel: (\d)", RegexOptions.Multiline);
        Assert.IsFalse(m.Success && m.Groups[1].Value == "0", "the scene must not switch the lock off");
    }
}
