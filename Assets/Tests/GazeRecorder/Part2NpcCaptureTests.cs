// Part2NpcCaptureTests.cs
// Part 2 (wandering itinerary) with the NPC master switch ON must use the same shared pipeline as Part 3: NPC lifetimes of NPCs that already exist are registered,
// npc_state / npc_events / npc_lifetimes are written, pooled objects get distinct spawn-instance IDs, and a gaze ray resolves an NPC identity ONLY when it really hits a
// registered NPC. These tests drive the production code through reflection in the scratch/PlayMode harness; they are not a Quest test and write only below the test folder.

using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    [UnityTest]
    public IEnumerator Part2_MasterSwitchOn_UsesTheSharedPipeline_AndGazeResolvesNpcIdentityOnlyOnRealHits()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { part2 = true, npcCaptureEnabled = true, prewarm = 2, maxAgents = 2, npcSpeed = 1f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "Part 2 NPCs");
        yield return new WaitForSecondsRealtime(0.4f);

        // (1) effective config + registration of NPCs that already exist BEFORE any trial / gaze recording
        Assert.IsTrue(Get<bool>(rig.dcm, "NpcRecordingEnabled"));
        var lt = Lifetimes(rig);
        AssertWidths(lt, "npc_lifetimes.csv");
        Assert.GreaterOrEqual(lt.Rows.Count, 2, "every NPC alive before the trial has a registered lifetime");
        var agents = ActiveAgents(rig);
        var firstIds = new HashSet<string>();
        foreach (var pa in agents) { Assert.IsTrue(Lid(pa).StartsWith(Aid(pa) + "-S"), "composite lifetime ID"); firstIds.Add(Lid(pa)); }
        Assert.Greater(NpcState(rig).Rows.Count, 0, "npc_state rows exist without any trial gate");
        Assert.Greater(NpcEvents(rig).Rows.Count, 0);
        AssertWidths(NpcState(rig), "npc_state.csv"); AssertWidths(NpcEvents(rig), "npc_events.csv");

        // (2) gaze: first a real hit on a registered NPC, then ray misses
        Component pa0 = agents[0]; string agent = Aid(pa0); string lifetime = Lid(pa0);
        var nav = pa0.GetComponent<NavMeshAgent>();
        Call(pa0.GetComponent(MoveType), "Stop");
        var head = Make("Head"); head.transform.position = new Vector3(0f, 0.9f, -3f);
        Call(rig.em, "BeginTrial");
        var rec = Make("Gaze").AddComponent(RecorderType);
        Set(rec, "headTransform", head.transform); Set(rec, "sampleRate", 60f); Set(rec, "enableDebugLogs", false); Set(rec, "experimentManager", rig.em);
        yield return null;
        int unresolvedBefore = GetStatic<int>(AoiType, "UnresolvedNpcHits");
        Call(rec, "StartRecording");
        float until = Time.realtimeSinceStartup + 0.5f;
        while (Time.realtimeSinceStartup < until) { nav.Warp(new Vector3(0f, 0.1f, 0f)); yield return null; }
        head.transform.rotation = Quaternion.Euler(-85f, 0f, 0f);   // look at the sky: nothing to hit
        until = Time.realtimeSinceStartup + 0.4f;
        while (Time.realtimeSinceStartup < until) { nav.Warp(new Vector3(0f, 0.1f, 0f)); yield return null; }
        Call(rec, "StopRecording");

        var gaze = Csv.Load(rig.File("gaze.csv"));
        AssertWidths(gaze, "gaze.csv");
        int hits = 0, misses = 0;
        foreach (var r in gaze.Rows)
        {
            bool npcHit = gaze.Get(r, "hit_semantic_class") == "NPC";
            if (npcHit)
            {
                hits++;
                Assert.AreEqual("1", gaze.Get(r, "ray_did_hit"));
                Assert.AreEqual(agent, gaze.Get(r, "hit_npc_id")); Assert.AreEqual(lifetime, gaze.Get(r, "hit_npc_spawn_instance_id"));
                Assert.AreEqual("Part2", gaze.Get(r, "part"));
            }
            else
            {
                if (gaze.Get(r, "ray_did_hit") == "0") misses++;
                Assert.AreEqual("", gaze.Get(r, "hit_npc_id"), "no NPC identity without an NPC hit");
                Assert.AreEqual("", gaze.Get(r, "hit_npc_spawn_instance_id"));
            }
        }
        Assert.Greater(hits, 3, "the real hit is attributed"); Assert.Greater(misses, 3, "ray misses carry no identity");
        Assert.AreEqual(unresolvedBefore, GetStatic<int>(AoiType, "UnresolvedNpcHits"), "NPCs registered before capture resolve immediately (no unresolved hits)");

        // (3) pooled object, second lifetime: a distinct, composite spawn-instance ID
        Call(pa0, "ForceFinish");
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "pool reuse");
        var all = new HashSet<string>(firstIds);
        foreach (var pa in ActiveAgents(rig)) if (Aid(pa) == agent) { Assert.AreEqual(agent + "-S2", Lid(pa)); Assert.IsFalse(firstIds.Contains(Lid(pa))); }
        yield return new WaitForSecondsRealtime(0.3f);
        Assert.Greater(Lifetimes(rig).Rows.Count, lt.Rows.Count, "the new lifetime is registered");
        Assert.IsTrue(all.Count >= 2);
    }
}
