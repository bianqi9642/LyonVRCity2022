// GroundRecoveryEventTests.cs
// PlayMode test of the GroundFallRecovery recording contract through the REAL ExperimentManager.ReportParticipantFallRecovery -> RaiseEvt -> DataCaptureManager -> events.csv path:
// exactly one "ground_recovery" row per recovery with from / to positions and the numeric vertical speed, the existing events.csv schema unchanged, NO "teleport" row and NO
// teleport_epoch increment (a recovery is not a teleport). Editor test - not a Quest test.

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    [UnityTest]
    public IEnumerator GroundRecovery_Event_IsOneRow_KeepsEventsSchema_AndDoesNotTouchTheTeleportEpoch()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
        RaiseExperimentEvent(rig, "part_start", "Part3");
        yield return null;
        int epoch0 = Get<int>(rig.dcm, "_teleportEpoch");
        Call(rig.em, "ReportParticipantFallRecovery", "fell_below_safe_y", new Vector3(2152.29f, 165.7f, 497.9f), new Vector3(2152.29f, 167.03f, 500.55f), -6.02, "reason=fell_below_safe_y;last_safe=2152.29|166.98|500.55;vy=-6.02");
        Call(rig.em, "ReportParticipantFallRecovery", "abrupt_drop_landing", new Vector3(1f, 2f, 3f), new Vector3(1f, 5f, 0f), -4.5, "reason=abrupt_drop_landing");
        yield return null;

        var evs = Events(rig);
        AssertWidths(evs, "events.csv");
        var rows = Rows(evs, "event_type", "ground_recovery");
        Assert.AreEqual(2, rows.Count, "one events.csv row per recovery");
        Assert.AreEqual("fell_below_safe_y", evs.Get(rows[0], "label")); Assert.AreEqual("abrupt_drop_landing", evs.Get(rows[1], "label"));
        Assert.AreEqual("-6.0200", evs.Get(rows[0], "value_num"), "vertical speed in value_num");
        Assert.AreEqual("2152.2900", evs.Get(rows[0], "from_pos_x")); Assert.AreEqual("165.7000", evs.Get(rows[0], "from_pos_y")); Assert.AreEqual("500.5500", evs.Get(rows[0], "to_pos_z"));
        StringAssert.Contains("last_safe=2152.29|166.98|500.55", evs.Get(rows[0], "value_str"));
        Assert.AreEqual(0, Rows(evs, "event_type", "teleport").Count, "a recovery is not written as a teleport");
        Assert.AreEqual(0, Rows(evs, "event_type", "river_crossing").Count);
        Assert.AreEqual(epoch0, Get<int>(rig.dcm, "_teleportEpoch"), "teleport_epoch must keep meaning 'teleports only'");
        Assert.AreEqual("interaction_id", evs.Header[evs.Header.Count - 1], "events.csv columns unchanged (interaction_id is still the last column)");
    }
}
