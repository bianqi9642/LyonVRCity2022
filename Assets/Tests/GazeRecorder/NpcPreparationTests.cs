// NpcPreparationTests.cs
// PlayMode tests for the NPC preparation lifecycle (schema_version 10): pool preparation -> fixed behaviour warm-up -> NPCs held (visible, raycastable) while the Start panel is read
// -> resumed by the participant's Start in the same transition as the trial. They drive the PRODUCTION SpawnerManager / ExperimentManager / MoveToTarget / PedestrianAgent /
// UncertainBehavior / DataCaptureManager through reflection on a runtime-built NavMesh and read the files that are really written (shared fixture: NpcLifecycleTests).
// Editor tests - NOT a Quest test: they establish logic, not frame-rate or on-device behaviour. Output goes below a unique temp folder only.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    static readonly Type GateType = T("NpcSimulationGate");

    static bool GatePaused() => GetStatic<bool>(GateType, "Paused");
    static string PrepState(Rig r) => Get<string>(Get<object>(r.em, "NpcPreparation"), "state");
    static T PrepGet<T>(Rig r, string field) => Get<T>(Get<object>(r.em, "NpcPreparation"), field);
    static bool Held(Component pa) => Get<bool>(pa.GetComponent(MoveType), "IsSimulationHeld");
    static string StateOf(Component pa) => Get<object>(pa, "state").ToString();

    // Part 2 with REAL RunPart2 and panel templates; the test "presses" the Start button.
    IEnumerator BuildRealPart2(RigOptions o, Action<Rig, Component> done)
    {
        var camGo = new GameObject("Cam"); camGo.tag = "MainCamera"; camGo.AddComponent<Camera>(); _spawned.Add(camGo);
        o.part2 = true; o.selectedPartFallback = true; o.realPart = true;
        Rig rig = null;
        yield return BuildRig(o, r => rig = r);
        var uiGo = Make("UI"); var ui = uiGo.AddComponent(T("UIManager"));
        Set(ui, "part2StartPanelTemplate", PanelTemplate("StartTpl", out _)); Set(ui, "part2ContinuePanelTemplate", PanelTemplate("ContinueTpl", out _)); Set(ui, "part2EndPanelTemplate", PanelTemplate("EndTpl", out _));
        var rigT = Make("PlayerRig").transform; var p0 = Make("Start0").transform; var p1 = Make("Tele0").transform; p1.position = new Vector3(3f, 0f, 3f);
        var groupType = Type.GetType("ExperimentManager+Part2Group, Assembly-CSharp");
        var group = Activator.CreateInstance(groupType); Set(group, "startPositions", new[] { p0 }); Set(group, "teleportPositions", new[] { p1 });
        var groups = Array.CreateInstance(groupType, 1); groups.SetValue(group, 0);
        Set(rig.em, "uiManager", ui); Set(rig.em, "playerRig", rigT); Set(rig.em, "spawner", rig.spawner); Set(rig.em, "part2Groups", groups);
        yield return null;
        done(rig, ui);
    }

    static Vector3[] Positions(List<Component> agents) { var p = new Vector3[agents.Count]; for (int i = 0; i < p.Length; i++) p[i] = agents[i].transform.position; return p; }

    // ====================================================================================== real Part 2 flow

    [UnityTest]
    public IEnumerator Part2_RealFlow_PoolThenWarmup_NpcsFrozenWhileStartPanelIsRead_ResumedWithTrialStart()
    {
        Rig rig = null; Component ui = null;
        yield return BuildRealPart2(new RigOptions
        {
            prewarm = 200, maxAgents = 200, spawnInterval = 0.05f, npcSpeed = 3f, npcCaptureEnabled = true, prewarmBudgetMs = 0.001f,
            emSetup = em => { Set(em, "npcWarmupDuration", 0.7f); Set(em, "part2FirstDuration", 0.5f); Set(em, "part2SecondDuration", 0.5f); }
        }, (r, u) => { rig = r; ui = u; });
        Call(rig.em, "StartPart2");
        // The Start panel must not exist before the pool is ready AND the warm-up has run; sample the preparation state on every frame until it appears.
        var seen = new List<string>();
        float t0 = Time.realtimeSinceStartup;
        while (OpenPanelButton(ui) == null)
        {
            string s = PrepState(rig); if (seen.Count == 0 || seen[seen.Count - 1] != s) seen.Add(s);
            if (Time.realtimeSinceStartup - t0 > 20f) Assert.Fail("timeout waiting for the Part 2 Start panel; states=" + string.Join(">", seen));
            yield return null;
        }
        string atPanel = PrepState(rig);
        Assert.AreEqual("PausedAwaitingParticipantStart", atPanel, "the Start panel appears only after pool preparation and the behaviour warm-up; states=" + string.Join(">", seen));
        CollectionAssert.Contains(seen, "PoolPreparing", "pool preparation was a real, separate stage");
        CollectionAssert.Contains(seen, "NpcGenerationRunning");
        Assert.Less(seen.IndexOf("PoolPreparing"), seen.IndexOf("NpcGenerationRunning"), "pool preparation precedes NPC generation");
        // the full state sequence, including the one-frame states
        Assert.AreEqual("NotStarted>PoolPreparing>PoolReady>NpcGenerationRunning>NpcGenerationCompleted>PausedAwaitingParticipantStart", PrepGet<string>(rig, "state_trail"));
        Assert.AreEqual(200, Get<int>(rig.spawner, "PoolPrewarmCreated"), "the whole pool was prepared before the panel");
        Assert.IsTrue(Get<bool>(rig.spawner, "IsPoolReady"));
        Assert.IsTrue(GatePaused()); Assert.IsTrue(Get<bool>(rig.spawner, "NpcBehaviourPaused"));

        // Generation timeline on the shared clock: the timer started only after the pool was ready, immediately (the watchdog timeout is not a waiting period).
        double poolDone = PrepGet<double>(rig, "pool_preparation_completed_t_abs"), genStart = PrepGet<double>(rig, "npc_generation_started_t_abs"), genEnd = PrepGet<double>(rig, "npc_generation_completed_t_abs");
        Assert.GreaterOrEqual(genStart, poolDone - 1e-6, "generation starts after pool readiness");
        Assert.Less(genStart - poolDone, 0.5, "generation starts immediately after the pool is ready (the 600 s watchdog is never waited out)");
        Assert.Less(PrepGet<double>(rig, "pool_preparation_duration_seconds"), 30.0);
        Assert.Greater(PrepGet<double>(rig, "pool_preparation_duration_seconds"), 0.0);
        Assert.AreEqual(200, PrepGet<int>(rig, "pool_preparation_created_count")); Assert.AreEqual(200, PrepGet<int>(rig, "pool_preparation_target_count"));
        Assert.GreaterOrEqual(genEnd - genStart, 0.69, "the configured 0.7 s generation duration really elapsed");
        Assert.AreEqual(0.7, PrepGet<double>(rig, "npc_generation_actual_duration_seconds"), 0.15, "actual duration is measured and distinct from the configured one");
        // time-based, not population-based: spawning ran for the duration at spawnInterval (0.05 s) and the count is only recorded
        int genSpawns = PrepGet<int>(rig, "npc_generation_spawn_count");
        Assert.GreaterOrEqual(genSpawns, 3); Assert.LessOrEqual(genSpawns, (int)(0.7 / 0.05) + 4, "no more spawns than the interval allows");
        int activeAtEnd = PrepGet<int>(rig, "active_npcs_at_generation_end");
        Assert.GreaterOrEqual(activeAtEnd, genSpawns); Assert.AreEqual(genSpawns, activeAtEnd, "nothing despawned: the spawn gate opens exactly at generation start, so active == spawned in the window");
        var firstLifetimes = Lifetimes(rig);
        double firstSpawn = double.MaxValue; foreach (var r0 in firstLifetimes.Rows) firstSpawn = Math.Min(firstSpawn, D(firstLifetimes.Get(r0, "spawn_t_abs")));
        Assert.GreaterOrEqual(firstSpawn, poolDone - 1e-3, "no NPC existed before the pool was ready");
        foreach (var r1 in firstLifetimes.Rows) Assert.AreEqual("Part2WanderingItinerary", firstLifetimes.Get(r1, "movement_mode"), "Part 2 keeps its own (wandering) movement mode");
        int spawnTotalAtPause = Get<int>(rig.spawner, "NpcSpawnedTotal");

        // NPCs spawned and behaved during the warm-up, then every one of them is held, visible and raycastable.
        var agents = ActiveAgents(rig);
        Assert.GreaterOrEqual(agents.Count, 3, "NPCs spawned during the warm-up");
        Assert.AreEqual(agents.Count, PrepGet<int>(rig, "active_npcs_at_pause"));
        Assert.AreEqual(agents.Count, PrepGet<int>(rig, "active_npcs_at_generation_end"));
        var ids = agents.ConvertAll(Lid); var pos = Positions(agents); int live = Live(rig);
        foreach (var pa in agents)
        {
            Assert.IsTrue(Held(pa), "held " + Aid(pa));
            Assert.IsTrue(pa.gameObject.activeInHierarchy, "stays active"); Assert.IsTrue(pa.GetComponent<Collider>().enabled, "collider stays enabled");
            Assert.IsTrue(pa.GetComponent<NavMeshAgent>().isStopped, "NavMeshAgent stopped");
        }
        int stateRowsBefore = NpcState(rig).Rows.Count;
        yield return new WaitForSecondsRealtime(0.6f);
        Assert.AreEqual(live, Live(rig), "no spawn or despawn while paused");
        Assert.AreEqual(spawnTotalAtPause, Get<int>(rig.spawner, "NpcSpawnedTotal"), "no NPC is spawned after the generation period ended");
        for (int i = 0; i < agents.Count; i++)
        {
            Assert.Less((agents[i].transform.position - pos[i]).magnitude, 0.01f, "stationary: " + Aid(agents[i]));
            Assert.AreEqual(ids[i], Lid(agents[i]), "identity unchanged while paused");
        }
        Assert.Greater(NpcState(rig).Rows.Count, stateRowsBefore, "npc_state keeps sampling the stationary NPCs");
        // A real raycast hits a held NPC (gaze rays would too).
        var target = agents[0].transform.position + Vector3.up * 0.9f;
        Assert.IsTrue(Physics.Raycast(target + Vector3.left * 3f, Vector3.right, out RaycastHit hit, 6f) && hit.collider.GetComponentInParent(PedType) != null, "a held NPC is raycastable");

        // The participant presses Start: NPCs resume and the trial opens in the same transition.
        OpenPanelButton(ui).onClick.Invoke();
        yield return WaitUntil(() => PrepState(rig) == "Running", 2f, "resume");
        Assert.IsFalse(GatePaused()); Assert.IsFalse(Get<bool>(rig.spawner, "NpcBehaviourPaused"));
        foreach (var pa in agents) Assert.IsFalse(Held(pa), "released " + Aid(pa));
        yield return new WaitForSecondsRealtime(0.6f);
        int moved = 0; for (int i = 0; i < agents.Count; i++) if ((agents[i].transform.position - pos[i]).magnitude > 0.2f) moved++;
        Assert.GreaterOrEqual(moved, 1, "NPC movement continues after the Start click");
        Assert.AreEqual(1, PrepGet<int>(rig, "pause_count")); Assert.AreEqual(1, PrepGet<int>(rig, "resume_count"));
        Assert.AreEqual(agents.Count, PrepGet<int>(rig, "active_npcs_at_resume"));

        // A duplicate Start callback changes nothing.
        Call(rig.em, "ResumeNpcBehaviourForTrialStart");
        Assert.AreEqual(1, PrepGet<int>(rig, "resume_count"), "no second resume");

        // Event timeline in events.csv: each marker exactly once, in order; resume and trial_start are one transition.
        yield return null;
        var evs = Events(rig);
        string[] order = { "npc_pool_preparation_started", "npc_pool_preparation_completed", "npc_warmup_start", "npc_warmup_end", "npc_behaviour_paused", "npc_behaviour_resumed", "trial_start" };
        double prev = -1;
        foreach (var name in order)
        {
            var rows = Rows(evs, "event_type", name);
            Assert.AreEqual(1, rows.Count, name + " exactly once");
            double t = D(evs.Get(rows[0], "t_abs")); Assert.GreaterOrEqual(t, prev - 1e-6, name + " in order"); prev = t;
        }
        double tResume = D(evs.Get(Rows(evs, "event_type", "npc_behaviour_resumed")[0], "t_abs")), tTrial = D(evs.Get(Rows(evs, "event_type", "trial_start")[0], "t_abs"));
        Assert.Less(Math.Abs(tTrial - tResume), 0.05, "resume and trial start are coordinated");
        Assert.AreEqual(agents.Count.ToString(), ((int)D(evs.Get(Rows(evs, "event_type", "npc_behaviour_paused")[0], "value_num"))).ToString(), "active NPC count recorded at pause");
        AssertEventsCsvIsParticipantOnly(evs);

        // Manifest block.
        yield return WaitUntil(() => OpenPanelButton(ui) != null, 5f, "the Continue panel");
        Call(rig.dcm, "WriteManifestEnd", "test");
        var m = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(rig.File("session_manifest.json")));
        var np = m["npc_preparation"];
        Assert.AreEqual(17, (int)m["schema_version"]);
        Assert.AreEqual("Part2", (string)np["part"]); Assert.AreEqual(200, (int)np["pool_preparation_target_count"]); Assert.AreEqual(200, (int)np["pool_preparation_created_count"]);
        Assert.AreEqual("ready", (string)np["pool_preparation_status"]);
        Assert.AreEqual(0.7, (double)np["npc_generation_configured_duration_seconds"], 1e-6); Assert.IsTrue((bool)np["hold_until_start"]);
        Assert.AreEqual(genSpawns, (int)np["npc_generation_spawn_count"]); Assert.AreEqual(activeAtEnd, (int)np["active_npcs_at_generation_end"]);
        Assert.Greater((double)np["npc_behaviour_paused_t_abs"], 0); Assert.Greater((double)np["npc_behaviour_resumed_t_abs"], (double)np["npc_behaviour_paused_t_abs"]);
        Assert.Greater((double)np["participant_start_t_abs"], (double)np["npc_behaviour_paused_t_abs"]);
        Assert.IsFalse(string.IsNullOrEmpty((string)m["npc_random_sources"]));
    }

    // ====================================================================================== different behavioural states

    [UnityTest]
    public IEnumerator Pause_HoldsNpcsInDifferentStates_AndResumePreservesDestinationsAndDwell()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 8, maxAgents = 12, spawnInterval = 0.35f, npcSpeed = 1.2f, compact = true, attractor = true, withUncertain = true, npcCaptureEnabled = true }, r => rig = r);
        Set(rig.spawner, "dwellTimeSec", new Vector2(1.5f, 1.5f));
        yield return WaitUntil(() => PoolReady(rig), 5f, "pool");

        // wait for at least one dwelling NPC and one walking NPC
        Component dwelling = null, walking = null;
        yield return WaitUntil(() =>
        {
            dwelling = null; walking = null;
            foreach (var pa in ActiveAgents(rig)) { if (StateOf(pa) == "WaitingAtAttractor") dwelling = dwelling ?? pa; else walking = walking ?? pa; }
            return dwelling != null && walking != null && Live(rig) >= 3;
        }, 25f, "a dwelling and a walking NPC");

        // force an UncertainBehavior pause on one more NPC (a third behavioural state)
        Component uncertain = null;
        foreach (var pa in ActiveAgents(rig)) if (pa != dwelling && pa != walking) { uncertain = pa; break; }
        if (uncertain != null) Call(uncertain.GetComponent(T("UncertainBehavior")), "TriggerPauseNow", 4f);
        yield return null;

        var agents = ActiveAgents(rig);
        var before = new Dictionary<Component, (Vector3 pos, string state, Vector3 dest, string label)>();
        foreach (var pa in agents) before[pa] = (pa.transform.position, StateOf(pa), pa.GetComponent<NavMeshAgent>().destination, pa.GetComponent(T("UncertainBehavior")) != null ? Get<string>(pa.GetComponent(T("UncertainBehavior")), "StateLabel") : "");
        int live = Live(rig);
        double tPause = Time.realtimeSinceStartupAsDouble;
        int held = (int)Call(rig.spawner, "PauseNpcBehaviour", "test");
        Assert.AreEqual(agents.Count, held);
        Assert.AreEqual(held, (int)Call(rig.spawner, "PauseNpcBehaviour", "again"), "pausing twice is idempotent");

        // hold longer than the dwell (1.5 s) and the forced pause (4 s is longer, 2.2 s here is enough to prove the dwell timer did not elapse)
        yield return new WaitForSecondsRealtime(2.2f);
        Assert.AreEqual(live, Live(rig), "no spawn while paused");
        foreach (var pa in agents)
        {
            var b = before[pa];
            Assert.Less((pa.transform.position - b.pos).magnitude, 0.01f, "stationary " + Aid(pa));
            Assert.AreEqual(b.state, StateOf(pa), "behavioural state unchanged while paused: " + Aid(pa));
            Assert.Less(((pa.GetComponent<NavMeshAgent>().destination) - b.dest).magnitude, 0.01f, "destination unchanged: " + Aid(pa));
        }
        Assert.AreEqual("WaitingAtAttractor", StateOf(dwelling), "the dwell timer did not elapse during the pause");
        var rayHit = Physics.Raycast(walking.transform.position + Vector3.up * 0.9f + Vector3.left * 3f, Vector3.right, out RaycastHit h, 6f);
        Assert.IsTrue(rayHit && h.collider.GetComponentInParent(PedType) != null, "a held NPC stays raycastable");

        // resume: walking NPC moves on toward the SAME destination; the dwelling NPC finishes its REMAINING dwell and moves on
        Call(rig.spawner, "ResumeNpcBehaviour", "test");
        Assert.AreEqual(0, (int)Call(rig.spawner, "ResumeNpcBehaviour", "again"), "resuming twice releases nothing the second time");
        Assert.IsFalse(GatePaused());
        Vector3 walkStart = walking.transform.position;
        yield return WaitUntil(() => (walking.transform.position - walkStart).magnitude > 0.2f || walking == null, 3f, "walking NPC moves again");
        yield return WaitUntil(() => dwelling == null || StateOf(dwelling) != "WaitingAtAttractor", 2.5f, "the dwelling NPC finishes its dwell after the resume");
    }

    // ====================================================================================== guards

    [UnityTest]
    public IEnumerator EarlyStart_DuringWarmup_IsCountedAndReported_NeverAcceptedAsAFinishedWarmup()
    {
        Rig rig = null; Component ui = null;
        yield return BuildRealPart2(new RigOptions { prewarm = 3, maxAgents = 3, spawnInterval = 0.05f, npcSpeed = 1f, emSetup = em => { Set(em, "npcWarmupDuration", 1.5f); } }, (r, u) => { rig = r; ui = u; });
        Call(rig.em, "StartPart2");
        yield return WaitUntil(() => PrepState(rig) == "NpcGenerationRunning", 10f, "NPC generation");
        LogAssert.Expect(LogType.Error, new Regex("Trial start while NPC preparation is 'NpcGenerationRunning'"));
        Call(rig.em, "ResumeNpcBehaviourForTrialStart");   // what an early Start would do
        Assert.AreEqual(1, PrepGet<int>(rig, "early_start_attempts"));
        Assert.AreEqual("NpcGenerationRunning", PrepState(rig), "the generation period is not skipped");
        Assert.IsFalse(GatePaused(), "nothing was paused or resumed");
        Assert.IsNull(OpenPanelButton(ui), "no Start panel during the warm-up");
        yield return WaitUntil(() => OpenPanelButton(ui) != null, 10f, "the Start panel after the warm-up");
        Assert.AreEqual("PausedAwaitingParticipantStart", PrepState(rig));
    }

    // ====================================================================================== pool: no timeout, explicit validation

    [Test]
    public void NoPoolTimeoutSettingRemains_AnywhereInTheProductionTypes()
    {
        foreach (var f in EmType.GetFields(All)) StringAssert.DoesNotContain("Timeout", f.Name, "ExperimentManager." + f.Name);
        foreach (var f in T("NpcPreparationInfo").GetFields(All)) StringAssert.DoesNotContain("timeout", f.Name.ToLowerInvariant(), "NpcPreparationInfo." + f.Name);
    }

    [UnityTest]
    public IEnumerator PoolWithFewerObjectsThanPoolPrewarm_IsIncomplete_BlocksGenerationStartPanelAndTrial()
    {
        Rig rig = null; Component ui = null;
        yield return BuildRealPart2(new RigOptions { prewarm = 4, maxAgents = 4, spawnInterval = 0.05f, npcSpeed = 1f, emSetup = em => { Set(em, "npcWarmupDuration", 0.3f); } }, (r, u) => { rig = r; ui = u; });
        yield return WaitUntil(() => PoolReady(rig), 10f, "pool ready");
        Set(rig.spawner, "poolPrewarmCreated", 3);   // created 3 of poolPrewarm 4
        LogAssert.Expect(LogType.Error, new Regex("NPC pool is not complete: 3/4"));
        LogAssert.Expect(LogType.Error, new Regex("Part2 aborted before the Start panel"));
        Call(rig.em, "StartPart2");
        yield return WaitUntil(() => PrepState(rig) == "Failed", 10f, "the incomplete-pool failure");
        yield return new WaitForSecondsRealtime(0.8f);
        Assert.AreEqual("failed_incomplete", PrepGet<string>(rig, "pool_preparation_status"));
        Assert.IsNull(OpenPanelButton(ui), "no Start panel"); Assert.AreEqual(0, Get<int>(rig.spawner, "NpcSpawnedTotal"), "no NPC generated");
        var evs = Events(rig);
        Assert.AreEqual(0, Rows(evs, "event_type", "npc_warmup_start").Count, "generation never started");
        Assert.AreEqual(1, Rows(evs, "event_type", "npc_preparation_part_aborted").Count);
        LogAssert.Expect(LogType.Error, new Regex("Trial start refused: NPC preparation failed"));
        Call(rig.em, "BeginTrial");
        Assert.AreEqual(0, Rows(Events(rig), "event_type", "trial_start").Count, "no trial");
    }

    [UnityTest]
    public IEnumerator PoolCreationError_IsAnExplicitFailure_NotASilentStall()
    {
        Rig rig = null; Component ui = null;
        LogAssert.Expect(LogType.Error, new Regex("NPC pool creation failed after 0/4 objects"));
        yield return BuildRealPart2(new RigOptions { prewarm = 4, maxAgents = 4, spawnInterval = 0.05f, npcSpeed = 1f, noAgentPrefab = true, emSetup = em => { Set(em, "npcWarmupDuration", 0.3f); } }, (r, u) => { rig = r; ui = u; });
        yield return WaitUntil(() => Get<bool>(rig.spawner, "PoolFailed"), 10f, "the reported pool failure");
        Assert.IsFalse(Get<bool>(rig.spawner, "IsPoolReady")); StringAssert.Contains("Exception", Get<string>(rig.spawner, "PoolFailureReason"));
        LogAssert.Expect(LogType.Error, new Regex("NPC pool preparation failed \\(0/4 objects\\)"));
        LogAssert.Expect(LogType.Error, new Regex("Part2 aborted before the Start panel"));
        Call(rig.em, "StartPart2");
        yield return WaitUntil(() => PrepState(rig) == "Failed", 10f, "the Part's failure");
        Assert.AreEqual("failed_error", PrepGet<string>(rig, "pool_preparation_status")); StringAssert.Contains("Exception", PrepGet<string>(rig, "pool_preparation_error"));
        Assert.IsNull(OpenPanelButton(ui)); Assert.AreEqual(0, Get<int>(rig.spawner, "NpcSpawnedTotal"));
    }

    [UnityTest]
    public IEnumerator MissingSpawner_FailsThePreparation_AndThePart()
    {
        Rig rig = null; Component ui = null;
        yield return BuildRealPart2(new RigOptions { prewarm = 2, maxAgents = 2, emSetup = em => { Set(em, "npcWarmupDuration", 0.3f); } }, (r, u) => { rig = r; ui = u; });
        Set(rig.em, "spawner", null);
        LogAssert.Expect(LogType.Error, new Regex("no SpawnerManager in the scene"));
        LogAssert.Expect(LogType.Error, new Regex("Part2 aborted before the Start panel"));
        Call(rig.em, "StartPart2");
        yield return WaitUntil(() => PrepState(rig) == "Failed", 10f, "failure");
        Assert.AreEqual("failed_no_spawner", PrepGet<string>(rig, "pool_preparation_status")); Assert.IsNull(OpenPanelButton(ui));
    }

    [UnityTest]
    public IEnumerator SlowPool_HasNoDeadline_StaysPreparing_ExposesProgress_AndIsCancelledWithThePart()
    {
        Rig rig = null; Component ui = null;
        yield return BuildRealPart2(new RigOptions { prewarm = 100000, maxAgents = 3, spawnInterval = 0.05f, npcSpeed = 1f, prewarmBudgetMs = 1f, emSetup = em => { Set(em, "npcWarmupDuration", 0.3f); } }, (r, u) => { rig = r; ui = u; });
        Call(rig.em, "StartPart2");
        yield return WaitUntil(() => PrepState(rig) == "PoolPreparing", 10f, "pool preparation");
        yield return new WaitForSecondsRealtime(1.5f);   // far longer than any generation duration: nothing falls through
        Assert.AreEqual("PoolPreparing", PrepState(rig), "a slow pool is neither failed nor skipped");
        Assert.IsNull(OpenPanelButton(ui)); Assert.AreEqual(0, Get<int>(rig.spawner, "NpcSpawnedTotal"));
        int created = PrepGet<int>(rig, "pool_preparation_created_count");
        Assert.Greater(created, 0, "live creation progress is exposed"); Assert.Greater(PrepGet<double>(rig, "pool_preparation_last_progress_t_abs"), 0.0);
        Assert.Less(created, 100000);
        Assert.AreEqual(0, Rows(Events(rig), "event_type", "npc_warmup_start").Count);
        // the Part is cancelled during preparation
        Call(rig.em, "TerminateSession", "Part2");
        yield return null; yield return null;
        Assert.AreEqual("Cancelled", PrepState(rig)); Assert.AreEqual("PoolPreparing", PrepGet<string>(rig, "state_trail").Split('>')[PrepGet<string>(rig, "state_trail").Split('>').Length - 2], "it was still preparing when cancelled");
    }

    // ====================================================================================== lifecycle: unworn / focus / suspension

    [UnityTest]
    public IEnumerator Generation_ContinuesWhileTheHeadsetIsUnworn_AndPausesAutomatically_ThenTheStartPanelWaitsForTheHeadset()
    {
        Rig rig = null; Component ui = null;
        yield return BuildRealPart2(new RigOptions { prewarm = 10, maxAgents = 100, spawnInterval = 0.05f, npcSpeed = 4f, emSetup = em => { Set(em, "npcWarmupDuration", 0.9f); } }, (r, u) => { rig = r; ui = u; });
        bool worn = false;   // the participant is not wearing the headset at all, from before the Part starts: no gaze, no controllers, no head tracking in this test
        rig.em.GetType().GetField("HeadsetPresenceProbe").SetValue(rig.em, (Func<bool?>)(() => worn));
        Call(rig.em, "StartPart2");
        yield return WaitUntil(() => PrepState(rig) == "NpcGenerationRunning", 10f, "NPC generation");
        Call(rig.em, "NoteHmdMounted", false);              // OVRManager.HMDUnmounted during the generation period
        Call(rig.em, "OnApplicationFocus", false);          // focus lost as well - Unity keeps executing
        yield return WaitUntil(() => PrepState(rig) == "PausedAwaitingParticipantStart", 10f, "the automatic pause while unworn");
        Call(rig.em, "OnApplicationFocus", true);

        // generation ran at full speed while unworn / unfocused, and the NPCs are already frozen
        Assert.AreEqual(0.9, PrepGet<double>(rig, "npc_generation_actual_duration_seconds"), 0.15);
        Assert.Less(PrepGet<double>(rig, "npc_generation_non_executing_seconds"), 0.15, "an unworn headset / lost focus costs no generation time while Unity executes");
        Assert.AreEqual(0, PrepGet<int>(rig, "npc_generation_unreported_stalls"));
        Assert.GreaterOrEqual(PrepGet<double>(rig, "npc_generation_hmd_unmounted_seconds"), 0.6, "the unworn interval is reported, not hidden"); Assert.AreEqual(1, PrepGet<int>(rig, "npc_generation_hmd_unmount_count"));
        Assert.GreaterOrEqual(PrepGet<double>(rig, "npc_generation_focus_lost_seconds"), 0.6); Assert.AreEqual(0, PrepGet<int>(rig, "npc_generation_app_pause_count"));
        Assert.GreaterOrEqual(PrepGet<int>(rig, "npc_generation_spawn_count"), 8, "spawning continued at the configured interval");
        var agents = ActiveAgents(rig); Assert.GreaterOrEqual(agents.Count, 8);
        foreach (var pa in agents) { Assert.IsTrue(Held(pa)); Assert.IsTrue(pa.GetComponent<Collider>().enabled); }

        // still waiting for the participant: no Start panel (it would be placed wherever the unworn headset lies), but the environment is ready and frozen
        yield return new WaitForSecondsRealtime(0.8f);
        Assert.IsNull(OpenPanelButton(ui), "the Start panel waits for the headset");
        Assert.AreEqual("PausedAwaitingParticipantStart", PrepState(rig)); Assert.IsTrue(GatePaused());
        int spawned = Get<int>(rig.spawner, "NpcSpawnedTotal");
        worn = true; Call(rig.em, "NoteHmdMounted", true);   // the participant puts the headset on
        yield return WaitUntil(() => OpenPanelButton(ui) != null, 5f, "the Start panel once the headset is worn");
        Assert.GreaterOrEqual(PrepGet<double>(rig, "start_panel_headset_wait_seconds"), 0.7);
        Assert.AreEqual(spawned, Get<int>(rig.spawner, "NpcSpawnedTotal"), "nothing spawned while waiting");

        // Start: NPCs resume FIRST, then the trial opens, same frame; nothing is respawned and no new generation period starts
        var pos = Positions(ActiveAgents(rig)); var ids = ActiveAgents(rig).ConvertAll(Lid);
        OpenPanelButton(ui).onClick.Invoke();
        yield return WaitUntil(() => PrepState(rig) == "Running", 3f, "resume");
        yield return new WaitForSecondsRealtime(0.5f);
        var evs = Events(rig);
        int iResume = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "npc_behaviour_resumed"), iTrial = evs.Rows.FindIndex(r => evs.Get(r, "event_type") == "trial_start");
        Assert.GreaterOrEqual(iResume, 0); Assert.Greater(iTrial, iResume, "NPCs are resumed before OnTrialStart");
        Assert.AreEqual(1, Rows(evs, "event_type", "npc_warmup_start").Count, "Start does not begin another generation period");
        for (int i = 0; i < ids.Count; i++) Assert.AreEqual(ids[i], Lid(ActiveAgents(rig)[i]), "the same NPCs, not respawned");
        int moved = 0; var after = ActiveAgents(rig); for (int i = 0; i < ids.Count; i++) if ((after[i].transform.position - pos[i]).magnitude > 0.2f) moved++;
        Assert.GreaterOrEqual(moved, 1, "retained NPC behaviour continues after Start");
    }

    [UnityTest]
    public IEnumerator GenuineSuspension_IsNotCountedAsGeneratedNpcTime_WhetherOrNotTheCallbacksReportIt()
    {
        foreach (bool reported in new[] { false, true })
        {
            Rig rig = null; Component ui = null;
            yield return BuildRealPart2(new RigOptions { prewarm = 3, maxAgents = 40, spawnInterval = 0.05f, npcSpeed = 1f, emSetup = em => { Set(em, "npcWarmupDuration", 0.8f); } }, (r, u) => { rig = r; ui = u; });
            Call(rig.em, "StartPart2");
            yield return WaitUntil(() => PrepState(rig) == "NpcGenerationRunning", 10f, "NPC generation");
            yield return new WaitForSecondsRealtime(0.25f);
            // Unity does not execute for 1.6 s (what an OS suspension does). The main thread really stalls, so there is exactly one huge frame gap.
            if (reported) Call(rig.em, "OnApplicationPause", true);
            System.Threading.Thread.Sleep(1600);
            if (reported) Call(rig.em, "OnApplicationPause", false);
            yield return WaitUntil(() => OpenPanelButton(ui) != null, 10f, "the Start panel");
            double actual = PrepGet<double>(rig, "npc_generation_actual_duration_seconds"), wall = PrepGet<double>(rig, "npc_generation_wall_clock_seconds"), nonExec = PrepGet<double>(rig, "npc_generation_non_executing_seconds");
            Assert.AreEqual(0.8, actual, 0.2, "executed generation time is the configured duration: the suspension was not counted as simulated time (reported=" + reported + ")");
            Assert.GreaterOrEqual(wall, 0.8 + 1.0, "wall clock includes the suspension");
            Assert.GreaterOrEqual(nonExec, 1.0, "the non-executing interval is recorded"); Assert.AreEqual(wall - actual, nonExec, 0.01);
            Assert.GreaterOrEqual(PrepGet<double>(rig, "npc_generation_max_frame_gap_seconds"), 1.5);
            Assert.AreEqual(reported ? 0 : 1, PrepGet<int>(rig, "npc_generation_unreported_stalls"), "a suspension without callbacks is flagged, one with callbacks is not");
            Assert.AreEqual(reported ? 1 : 0, PrepGet<int>(rig, "npc_generation_app_pause_count"));
            if (reported) Assert.GreaterOrEqual(PrepGet<double>(rig, "npc_generation_app_pause_seconds"), 1.5);
            // the remaining duration was simulated AFTER the resume: NPCs spawned over executed time only
            Assert.LessOrEqual(PrepGet<int>(rig, "npc_generation_spawn_count"), (int)(0.8 / 0.05) + 6);
            // tear this rig down before the second iteration builds its own
            foreach (var o in _spawned) if (o != null) UnityEngine.Object.Destroy(o);
            _spawned.Clear();
            yield return null; yield return null;
            foreach (var leftover in UnityEngine.Object.FindObjectsOfType(PedType)) UnityEngine.Object.Destroy(((Component)leftover).gameObject);
            yield return null;
            CallStatic(GateType, "Reset");
            if (_navInstance.valid) NavMesh.RemoveNavMeshData(_navInstance);
            CallStatic(BusType, "ResetForTests");
        }
    }

    [UnityTest]
    public IEnumerator Session_EndedWhileAwaitingStart_IsCancelled_AndAStartedSessionIsEnded()
    {
        Rig rig = null; Component ui = null;
        yield return BuildRealPart2(new RigOptions { prewarm = 3, maxAgents = 5, spawnInterval = 0.05f, npcSpeed = 1f, emSetup = em => { Set(em, "npcWarmupDuration", 0.3f); } }, (r, u) => { rig = r; ui = u; });
        Call(rig.em, "StartPart2");
        yield return WaitUntil(() => OpenPanelButton(ui) != null, 10f, "the Start panel");
        Assert.AreEqual("PausedAwaitingParticipantStart", PrepState(rig));
        Call(rig.em, "TerminateSession", "Part2");
        Assert.AreEqual("Cancelled", PrepState(rig), "the session ended before the participant started");
        Assert.IsFalse(GatePaused(), "a cancelled preparation never leaves the NPCs frozen");
    }

    // ====================================================================================== Part 3 uses the same mechanism; cleanup

    [UnityTest]
    public IEnumerator Part3_UsesTheSameMechanism_AndANewPartReleasesAPausedPopulation()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 6, maxAgents = 6, spawnInterval = 0.05f, npcSpeed = 1.5f, compact = true, attractor = true, openSimulation = false, npcCaptureEnabled = true,
            emSetup = em => { Set(em, "npcWarmupDuration", 0.5f); } }, r => rig = r);
        Set(rig.em, "spawner", rig.spawner);
        Call(rig.em, "EnsureSessionOverridesApplied");
        Assert.IsTrue(Get<bool>(Get<object>(rig.em, "CurrentPartConfig"), "npcDirectedMovement"), "Part 3 keeps its directed (attractor) movement");

        yield return (IEnumerator)Call(rig.em, "RunNpcWarmup", Get<object>(rig.em, "CurrentPartConfig"), "Part3");
        Assert.AreEqual("PausedAwaitingParticipantStart", PrepState(rig));
        Assert.IsTrue(GatePaused());
        var agents = ActiveAgents(rig);
        Assert.Greater(agents.Count, 0);
        foreach (var pa in agents) Assert.IsTrue(Held(pa));

        // the same Start transition every Part uses
        Call(rig.em, "BeginTrial");
        Assert.AreEqual("Running", PrepState(rig)); Assert.IsFalse(GatePaused());
        foreach (var pa in agents) Assert.IsFalse(Held(pa));

        // pause again, then a new Part starts: the population is never left frozen
        Call(rig.spawner, "PauseNpcBehaviour", "test");
        Assert.IsTrue(GatePaused());
        Call(rig.spawner, "ResetForNewPart");
        Assert.IsFalse(GatePaused()); Assert.IsFalse(Get<bool>(rig.spawner, "NpcBehaviourPaused"));
        foreach (var pa in ActiveAgents(rig)) Assert.IsFalse(Held(pa));
    }

    [UnityTest]
    public IEnumerator SimulationGate_SimTimeDoesNotAdvanceWhilePaused_AndIsIdempotent()
    {
        CallStatic(GateType, "Reset");
        float a = GetStatic<float>(GateType, "SimTime");
        Assert.IsTrue((bool)CallStatic(GateType, "SetPaused", true));
        Assert.IsFalse((bool)CallStatic(GateType, "SetPaused", true), "second pause changes nothing");
        float frozen = GetStatic<float>(GateType, "SimTime");
        yield return new WaitForSecondsRealtime(0.3f);
        Assert.AreEqual(frozen, GetStatic<float>(GateType, "SimTime"), 1e-4f, "behaviour time is frozen while paused");
        Assert.IsTrue((bool)CallStatic(GateType, "SetPaused", false));
        Assert.IsFalse((bool)CallStatic(GateType, "SetPaused", false));
        yield return new WaitForSecondsRealtime(0.2f);
        Assert.Greater(GetStatic<float>(GateType, "SimTime"), frozen + 0.15f, "behaviour time runs again after the resume");
        Assert.GreaterOrEqual(GetStatic<float>(GateType, "PausedTotalSeconds"), 0.29f);
        CallStatic(GateType, "Reset");
    }
}
