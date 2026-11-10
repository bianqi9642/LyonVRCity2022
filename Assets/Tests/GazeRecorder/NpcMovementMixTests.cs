// NpcMovementMixTests.cs
// Tests for the Part 3 NPC movement mixture: every NPC spawned in Part 3 independently uses the EXISTING attractor movement with probability a
// (SpawnerManager.part3AttractorMovementProbability, Part3MovementStrategySelector) and the EXISTING wandering-point movement otherwise; a Part 3 wandering
// NPC never wears the accessory, an attractor NPC uses its Agent Profile's accessory options unchanged.
// A partial of NpcLifecycleTests so the production rig (SpawnerManager, PedestrianAgent, MoveToTarget, ExperimentManager, DataCaptureManager, AgentFactory,
// AppearanceController on a runtime NavMesh) is reused; everything is driven through reflection (see TestSupport.cs).
//   * Pure selector tests need no scene: boundaries, statistics, reproducibility, independence from any history, isolation from UnityEngine.Random, cost.
//   * Rig tests spawn real NPCs and check: the realised strategies equal the seeded draw sequence, itineraries / destinations, hold behaviour, part
//     isolation, mid-Part changes, pool reuse, accessory state (logical and renderer), and the recorded data.
// These are Unity Editor tests (synthetic NavMesh), not Quest Link or Quest Pro tests.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    static readonly Type SelectorType = T("Part3MovementStrategySelector");
    static readonly Type AppearanceType = T("AppearanceController");
    const string AttrMode = "Part3AttractorItinerary", WandMode = "Part2WanderingItinerary";

    static object NewSelector(int seed) => Activator.CreateInstance(SelectorType, seed);
    static string Draw(object selector, float p) => Call(selector, "Choose", p).ToString();
    static string ModeOf(Component pa) => Get<object>(pa, "MovementMode").ToString();

    static List<string> ExpectedModes(int seed, float p, int n)
    {
        object s = NewSelector(seed);
        var l = new List<string>();
        for (int i = 0; i < n; i++) l.Add(Draw(s, p));
        return l;
    }

    // ====================================================================================== pure selector

    [Test]
    public void Mix_ZeroAndOne_AreExact_AndOutOfRangeValuesAreClamped()
    {
        object zero = NewSelector(1), one = NewSelector(2);
        for (int i = 0; i < 5000; i++)
        {
            Assert.AreEqual(WandMode, Draw(zero, 0f), "0.0 = every new NPC wanders");
            Assert.AreEqual(AttrMode, Draw(one, 1f), "1.0 = every new NPC uses attractors");
        }
        Assert.AreEqual(AttrMode, Draw(NewSelector(3), 7.5f), "> 1 is clamped to 1");
        Assert.AreEqual(WandMode, Draw(NewSelector(3), -2f), "< 0 is clamped to 0");
        Assert.AreEqual(AttrMode, Draw(NewSelector(3), float.NaN), "NaN falls back to the default (all attractor)");
        Assert.AreEqual(1f, (float)CallStatic(SelectorType, "SanitizeProbability", float.NaN));
        Assert.AreEqual(0f, (float)CallStatic(SelectorType, "SanitizeProbability", -0.001f));
        Assert.AreEqual(1f, (float)CallStatic(SelectorType, "SanitizeProbability", 1.001f));
        Assert.AreEqual(0.37f, (float)CallStatic(SelectorType, "SanitizeProbability", 0.37f));
        Assert.AreEqual(1f, (float)SelectorType.GetField("DefaultAttractorProbability").GetRawConstantValue(), "the default keeps the pre-mixture behaviour");
    }

    // Independent Bernoulli(a) per spawn: the count of attractors in n draws is Binomial(n, a). No exact counts are demanded; 5 standard deviations is the bound
    // (the seeds are fixed, so the outcome is deterministic and cannot flake).
    [Test]
    public void Mix_IntermediateProbabilities_GiveBinomialCounts_NotExactCounts()
    {
        const int n = 10000;
        var report = new List<string>();
        foreach (float p in new[] { 0.1f, 0.25f, 0.5f, 0.75f, 0.9f })
        {
            var counts = new List<int>();
            foreach (int seed in new[] { 11, 22, 33, 44, 55 })
            {
                object s = NewSelector(seed);
                int k = 0;
                for (int i = 0; i < n; i++) if (Draw(s, p) == AttrMode) k++;
                counts.Add(k);
                double sigma = Math.Sqrt(n * p * (1 - p));
                Assert.LessOrEqual(Math.Abs(k - n * p), 5 * sigma, "p=" + p + " seed=" + seed + " attractors=" + k + " of " + n);
            }
            Assert.Greater(counts.Distinct().Count(), 1, "the realised counts vary from seed to seed (random sampling, not a fixed quota) at p=" + p);
            report.Add("p=" + p.ToString("F2", CultureInfo.InvariantCulture) + " attractor shares " + string.Join("/", counts.Select(c => (c / (double)n).ToString("F3", CultureInfo.InvariantCulture))));
        }
        TestContext.Out.WriteLine("per-spawn draws, 10000 each: " + string.Join("; ", report));
    }

    // The draw does not depend on the previous outcomes: P(attractor | previous attractor) = P(attractor | previous wandering) = a.
    [Test]
    public void Mix_ConsecutiveDraws_AreIndependent()
    {
        const int n = 40000; const float p = 0.5f;
        object s = NewSelector(777);
        string prev = Draw(s, p);
        int afterA = 0, a = 0, afterW = 0, w = 0;
        for (int i = 0; i < n; i++)
        {
            string cur = Draw(s, p);
            if (prev == AttrMode) { a++; if (cur == AttrMode) afterA++; } else { w++; if (cur == AttrMode) afterW++; }
            prev = cur;
        }
        Assert.AreEqual(p, afterA / (double)a, 0.03, "P(attractor | previous attractor)");
        Assert.AreEqual(p, afterW / (double)w, 0.03, "P(attractor | previous wandering)");
    }

    [Test]
    public void Mix_SameSeed_ReproducesTheSequence_DifferentSeeds_AreIndependent()
    {
        var a = ExpectedModes(4242, 0.5f, 2000); var b = ExpectedModes(4242, 0.5f, 2000);
        CollectionAssert.AreEqual(a, b, "same seed -> identical assignment sequence");
        var c = ExpectedModes(4243, 0.5f, 2000);
        double agree = a.Zip(c, (x, y) => x == y ? 1 : 0).Sum() / 2000.0;
        Assert.AreEqual(0.5, agree, 0.06, "two different seeds agree about half of the time (independent streams), not always");
        Assert.AreNotEqual(string.Join(",", a), string.Join(",", c));
        Assert.AreEqual(1234, Get<int>(NewSelector(1234), "Seed"));
        Assert.AreEqual(55, (int)CallStatic(SelectorType, "ResolveSeed", 55, 9), "an explicit seed is used as is");
    }

    // One value is drawn per decision whatever the probability is, so the stream position does not depend on the setting, and a lower probability picks a
    // subset of the attractor spawns of a higher one (the same r is compared with a smaller a).
    [Test]
    public void Mix_OneDrawPerDecision_AndAttractorSetsAreNestedAcrossProbabilities()
    {
        var p25 = ExpectedModes(99, 0.25f, 3000); var p50 = ExpectedModes(99, 0.5f, 3000); var p75 = ExpectedModes(99, 0.75f, 3000);
        for (int i = 0; i < 3000; i++)
        {
            if (p25[i] == AttrMode) Assert.AreEqual(AttrMode, p50[i], "i=" + i);
            if (p50[i] == AttrMode) Assert.AreEqual(AttrMode, p75[i], "i=" + i);
        }
        object s = NewSelector(5);
        foreach (float p in new[] { 0f, 0.5f, 1f, 0.3f }) Draw(s, p);
        Assert.AreEqual(4L, Get<long>(s, "Draws"), "exactly one draw per decision, even at 0 and 1");
    }

    // No active-population correction exists: whatever the assignment counters and the live population look like, the next draw is the same.
    [Test]
    public void Mix_ActivePopulationAndCounters_NeverInfluenceTheNextDraw()
    {
        object clean = NewSelector(31337), skewed = NewSelector(31337);
        var expected = new List<string>(); var actual = new List<string>();
        for (int i = 0; i < 500; i++)
        {
            expected.Add(Draw(clean, 0.5f));
            // report a wildly unbalanced history to the second selector: a quota/deficit design would now correct towards wandering
            for (int k = 0; k < 40; k++) Call(skewed, "NoteAssigned", Enum.Parse(T("NpcMovementMode"), AttrMode));
            actual.Add(Draw(skewed, 0.5f));
        }
        CollectionAssert.AreEqual(expected, actual, "the draw sequence is a function of the seed only");
        var s = NewSelector(8);
        for (int i = 0; i < 20; i++) Draw(s, 0.5f);
        var next = NewSelector(8);
        for (int i = 0; i < 20; i++) Draw(next, 0.5f);
        Call(s, "ResetCounts");
        Assert.AreEqual(Draw(next, 0.5f), Draw(s, 0.5f), "ResetCounts (a new Part) clears the counters but does not rewind or reseed the stream");
        Assert.AreEqual(0L, Get<long>(s, "AssignedTotal"));
    }

    [Test]
    public void Mix_ConsumesNoUnityRandomNumbers_SoRouteDwellAndAppearanceDrawsAreUnperturbed()
    {
        UnityEngine.Random.InitState(20261109);
        var expected = new[] { UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value };
        UnityEngine.Random.InitState(20261109);
        object s = NewSelector(1);
        for (int i = 0; i < 5000; i++) Draw(s, 0.37f);
        var actual = new[] { UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value };
        CollectionAssert.AreEqual(expected, actual, "the selector never touches UnityEngine.Random");
    }

    [Test]
    public void Mix_ChooseCost_IsSmall_AndAllocatesNothing()
    {
        var sel = NewSelector(1); var p = Expression.Parameter(typeof(float), "p");
        var choose = Expression.Lambda<Func<float, int>>(Expression.Convert(Expression.Call(Expression.Constant(sel), SelectorType.GetMethod("Choose"), p), typeof(int)), p).Compile();
        choose(0.5f);   // warm-up
        const int calls = 400000;
        int gc0 = GC.CollectionCount(0);
        var sw = Stopwatch.StartNew(); int sink = 0;
        for (int i = 0; i < calls; i++) sink += choose(0.5f);
        sw.Stop();
        Assert.AreEqual(gc0, GC.CollectionCount(0), "no garbage is produced by " + calls + " decisions");
        double ns = sw.Elapsed.TotalMilliseconds * 1e6 / calls;
        TestContext.Out.WriteLine("Choose ns/call: " + ns.ToString("F1", CultureInfo.InvariantCulture) + " (sink " + sink + ")");
        Assert.Less(ns, 1000.0, "one decision is far below a microsecond");
    }

    // ====================================================================================== the Inspector fields

    [Test]
    public void Spawner_AttractorProbability_DefaultIsOne_HasRangeTooltipHeader_OnValidateClamps_AndTheOldFieldIsGone()
    {
        var sp = Make("MixSpawnerFieldTest", active: false).AddComponent(SpawnerType);   // inactive: no Awake / Start
        var field = SpawnerType.GetField("part3AttractorMovementProbability");
        Assert.IsNotNull(field, "the single authoritative parameter is a serialised public field");
        Assert.IsNull(SpawnerType.GetField("part3AttractorMovementProportion"), "the old active-population field no longer exists (one parameter only)");
        Assert.AreEqual(1f, Get<float>(sp, "part3AttractorMovementProbability"), "default = every new NPC uses attractors = the pre-mixture Part 3 behaviour");
        Assert.AreEqual(1f, Get<float>(sp, "Part3AttractorMovementProbability"));
        Assert.AreEqual(0, Get<int>(sp, "part3MovementAssignmentSeed"), "seed 0 = derive from the clock");

        var range = (UnityEngine.RangeAttribute)Attribute.GetCustomAttribute(field, typeof(UnityEngine.RangeAttribute));
        Assert.IsNotNull(range); Assert.AreEqual(0f, range.min); Assert.AreEqual(1f, range.max);
        var tip = (TooltipAttribute)Attribute.GetCustomAttribute(field, typeof(TooltipAttribute));
        Assert.IsNotNull(tip);
        foreach (string needle in new[] { "PROBABILITY", "0.0 = every new NPC wanders", "0.5 = each new NPC has a 50% chance", "1.0 = every new NPC uses attractors", "never wear the accessory", "Part 2 MOVEMENT ignores" })
            StringAssert.Contains(needle, tip.tooltip);
        var header = (HeaderAttribute)Attribute.GetCustomAttribute(field, typeof(HeaderAttribute));
        Assert.IsNotNull(header); Assert.AreEqual("Part 3 NPC Movement Configuration", header.header);
        Assert.IsTrue(field.IsPublic && !field.IsStatic, "serialised by Unity");

        foreach (var c in new[] { new[] { 7f, 1f }, new[] { -3f, 0f }, new[] { 0.4f, 0.4f }, new[] { float.NaN, 1f } })
        {
            Set(sp, "part3AttractorMovementProbability", c[0]);
            Call(sp, "OnValidate");
            Assert.AreEqual(c[1], Get<float>(sp, "part3AttractorMovementProbability"), "OnValidate clamp of " + c[0]);
        }
        Set(sp, "part3AttractorMovementProbability", 5f);
        Assert.AreEqual(1f, Get<float>(sp, "Part3AttractorMovementProbability"), "the selector always sees a clamped value even if OnValidate has not run");
    }

    // ====================================================================================== spawned NPCs

    RigOptions MixRig(float p, int n, float speed = 1.5f) => new RigOptions
    {
        prewarm = n, maxAgents = n, spawnInterval = 0.02f, npcSpeed = speed, attractor = true, attractorProbability = p, createDcm = false, runningPart = "Part3"
    };

    static string[] ItineraryNames(Component pa) => Get<IReadOnlyList<Transform>>(pa, "ItineraryTransformsView").Select(t => t != null ? t.name : "<null>").ToArray();

    // Each NPC follows ITS strategy only: its itinerary, dwell / look-ahead and destination never belong to the other strategy.
    void AssertStrategyIsolation(Rig rig, List<Component> agents)
    {
        Vector3 attractorPos = new Vector3(0f, 0.1f, 0f);
        foreach (var pa in agents)
        {
            string[] route = ItineraryNames(pa);
            var mt = pa.GetComponent(MoveType);
            Transform tt = Get<Transform>(mt, "targetTransform"); Vector3 tp = Get<Vector3>(mt, "targetPosition");
            string target = tt != null ? tt.name : "";
            bool targetsAttractor = target == "Attractor_A" || (tt == null && (tp - attractorPos).magnitude < 3f);
            bool targetsWandering = target.StartsWith("W_");
            if (ModeOf(pa) == AttrMode)
            {
                CollectionAssert.AreEqual(new[] { "Entrance_A", "Attractor_A", "Exit_A" }, route, "attractor NPC route");
                Assert.AreEqual(0.5f, Get<float>(pa, "DwellSeconds"), 1e-4f, "attractor NPCs keep the dwell sampled at spawn");
                Assert.IsFalse(targetsWandering, "an attractor NPC is never sent to a wandering point (" + Aid(pa) + " -> " + target + ")");
            }
            else
            {
                Assert.AreEqual(3, route.Length); Assert.AreEqual("Entrance_A", route[0]); Assert.IsTrue(route[1].StartsWith("W_"), "wandering stop " + route[1]); Assert.AreEqual("Exit_A", route[2]);
                Assert.AreEqual(0f, Get<float>(pa, "DwellSeconds"), "wandering NPCs never dwell");
                Assert.IsFalse(targetsAttractor, "a wandering NPC is never sent to an attractor (" + Aid(pa) + " -> " + target + ")");
                Assert.Greater(Get<float>(pa, "WanderLookAheadDistance"), 0f);
            }
            var nav = pa.GetComponent<NavMeshAgent>();
            if (nav.hasPath)
            {
                Vector3 dest = nav.destination;
                if (ModeOf(pa) == AttrMode) foreach (var w in rig.wandering) Assert.Greater((dest - w.position).magnitude, 1f, "attractor NPC destination is not a wandering point");
                else Assert.Greater((dest - attractorPos).magnitude, 1f, "wandering NPC destination is not the attractor");
            }
        }
    }

    // The realised strategies of real spawns are EXACTLY the seeded draw sequence: one independent draw per spawn, nothing corrects it afterwards.
    IEnumerator SpawnAndMatchDraws(float p, int n)
    {
        Rig rig = null;
        yield return BuildRig(MixRig(p, n), r => rig = r);
        yield return WaitUntil(() => Live(rig) == n, 30f, n + " NPCs");
        yield return new WaitForSecondsRealtime(0.5f);   // the first legs are issued
        var agents = ActiveAgents(rig);
        Assert.AreEqual(n, agents.Count);
        CollectionAssert.AreEqual(ExpectedModes(20261109, p, n), agents.Select(ModeOf).ToList(), "p=" + p + ": spawn i uses the i-th draw of the seeded stream");
        int attractor = agents.Count(a => ModeOf(a) == AttrMode);
        Assert.AreEqual((long)attractor, Get<long>(rig.spawner, "AssignedAttractorMovementTotal")); Assert.AreEqual((long)(n - attractor), Get<long>(rig.spawner, "AssignedWanderingMovementTotal"));
        Assert.AreEqual((long)n, Get<long>(rig.spawner, "Part3MovementDraws"), "one draw per spawn");
        if (p == 0f) Assert.AreEqual(0, attractor);
        if (p == 1f) Assert.AreEqual(n, attractor);
        AssertStrategyIsolation(rig, agents);
        TestContext.Out.WriteLine("p=" + p + " realised attractors " + attractor + "/" + n);
    }

    [UnityTest] public IEnumerator MixSpawn_Probability000_AllNpcsWander() { yield return SpawnAndMatchDraws(0f, 25); }
    [UnityTest] public IEnumerator MixSpawn_Probability010_FollowsTheSeededDraws() { yield return SpawnAndMatchDraws(0.1f, 30); }
    [UnityTest] public IEnumerator MixSpawn_Probability025_FollowsTheSeededDraws() { yield return SpawnAndMatchDraws(0.25f, 30); }
    [UnityTest] public IEnumerator MixSpawn_Probability050_FollowsTheSeededDraws() { yield return SpawnAndMatchDraws(0.5f, 30); }
    [UnityTest] public IEnumerator MixSpawn_Probability075_FollowsTheSeededDraws() { yield return SpawnAndMatchDraws(0.75f, 30); }
    [UnityTest] public IEnumerator MixSpawn_Probability090_FollowsTheSeededDraws() { yield return SpawnAndMatchDraws(0.9f, 30); }
    [UnityTest] public IEnumerator MixSpawn_Probability100_AllAttractor_TheDefault() { yield return SpawnAndMatchDraws(1f, 25); }

    // The assignment belongs to the lifetime: it does not change while the NPC lives, and it does not move when later NPCs join.
    [UnityTest]
    public IEnumerator MixSpawn_StrategyIsFixedForTheLifetime_AndLaterSpawnsDoNotReassignEarlierNpcs()
    {
        Rig rig = null;
        yield return BuildRig(MixRig(0.5f, 20), r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 8, 20f, "8 NPCs");
        var firstWave = ActiveAgents(rig).Take(8).ToList();
        var modes = firstWave.ToDictionary(a => Lid(a), ModeOf);
        var routes = firstWave.ToDictionary(a => Lid(a), a => string.Join(">", ItineraryNames(a)));
        yield return WaitUntil(() => Live(rig) == 20, 20f, "the rest of the population");
        yield return new WaitForSecondsRealtime(1.0f);   // they have reached waypoints in the meantime
        foreach (var a in firstWave)
        {
            Assert.AreEqual(modes[Lid(a)], ModeOf(a), "mode of " + Lid(a) + " is unchanged by later spawns and by reaching waypoints");
            Assert.AreEqual(routes[Lid(a)], string.Join(">", ItineraryNames(a)), "itinerary of " + Lid(a) + " is fixed");
        }
    }

    // Changing the Inspector value in the middle of a Part affects only NPCs spawned afterwards.
    [UnityTest]
    public IEnumerator MixSpawn_ChangingTheProbabilityMidPart_AffectsOnlyLaterSpawns()
    {
        Rig rig = null;
        yield return BuildRig(MixRig(1f, 8), r => rig = r);
        yield return WaitUntil(() => Live(rig) == 8, 20f, "8 attractor NPCs");
        var first = ActiveAgents(rig); var firstIds = first.Select(Lid).ToList();
        foreach (var a in first) Assert.AreEqual(AttrMode, ModeOf(a));
        Set(rig.spawner, "part3AttractorMovementProbability", 0f);
        Set(rig.spawner, "maxAgents", 16);
        yield return WaitUntil(() => Live(rig) == 16, 20f, "8 more NPCs");
        var all = ActiveAgents(rig);
        for (int i = 0; i < 8; i++) { Assert.AreEqual(firstIds[i], Lid(all[i])); Assert.AreEqual(AttrMode, ModeOf(all[i]), "already-active NPC " + i + " keeps its strategy"); }
        for (int i = 8; i < 16; i++) Assert.AreEqual(WandMode, ModeOf(all[i]), "NPC " + i + " spawned after the change uses the new probability");
    }

    // ====================================================================================== hold (pause) behaviour

    [UnityTest]
    public IEnumerator MixSpawn_HeldNpcsOfBothStrategiesStayStill_AndResumeToTheirOwnStrategy()
    {
        Rig rig = null;
        yield return BuildRig(MixRig(0.5f, 12, speed: 3f), r => rig = r);
        yield return WaitUntil(() => Live(rig) == 12, 20f, "12 NPCs");
        yield return new WaitForSecondsRealtime(0.4f);
        int held = (int)Call(rig.spawner, "PauseNpcBehaviour", "mix_test");
        Assert.AreEqual(12, held);
        var agents = ActiveAgents(rig);
        var pos = agents.Select(a => a.transform.position).ToArray();
        var modes = agents.Select(ModeOf).ToArray();
        Assert.Greater(modes.Count(m => m == AttrMode), 0); Assert.Greater(modes.Count(m => m == WandMode), 0, "both strategies are present while held");
        yield return new WaitForSecondsRealtime(0.6f);
        Assert.AreEqual(12, Live(rig), "no spawn while held");
        for (int i = 0; i < agents.Count; i++)
        {
            Assert.Less((agents[i].transform.position - pos[i]).magnitude, 0.02f, modes[i] + " NPC " + Aid(agents[i]) + " stays still while the simulation is held");
            Assert.IsTrue(agents[i].GetComponent(MoveType) != null && Get<bool>(agents[i].GetComponent(MoveType), "IsSimulationHeld"));
        }
        Call(rig.spawner, "ResumeNpcBehaviour", "mix_test");
        yield return new WaitForSecondsRealtime(0.8f);
        int movedAttractor = 0, movedWandering = 0;
        for (int i = 0; i < agents.Count; i++)
        {
            Assert.AreEqual(modes[i], ModeOf(agents[i]), "the assignment survives the hold");
            if ((agents[i].transform.position - pos[i]).magnitude > 0.3f) { if (modes[i] == AttrMode) movedAttractor++; else movedWandering++; }
        }
        Assert.Greater(movedAttractor, 0, "attractor NPCs walk on after the hold"); Assert.Greater(movedWandering, 0, "wandering NPCs walk on after the hold");
    }

    // ====================================================================================== part isolation

    IEnumerator Part2IgnoresSetting(float p)
    {
        Rig rig = null;
        var o = MixRig(p, 10); o.part2 = true; o.runningPart = "Part2";
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 10, 20f, "Part 2 NPCs");
        foreach (var a in ActiveAgents(rig)) Assert.AreEqual(WandMode, ModeOf(a), "Part 2 stays wandering whatever the Part 3 probability is (p=" + p + ")");
        Assert.AreEqual(0L, Get<long>(rig.spawner, "AssignedAttractorMovementTotal") + Get<long>(rig.spawner, "AssignedWanderingMovementTotal"), "Part 2 spawns are not Part 3 decisions");
        Assert.AreEqual(0L, Get<long>(rig.spawner, "Part3MovementDraws"), "Part 2 never consumes a draw of the Part 3 stream");
    }

    [UnityTest] public IEnumerator MixIsolation_Part2Configuration_IgnoresProbabilityZero() { yield return Part2IgnoresSetting(0f); }
    [UnityTest] public IEnumerator MixIsolation_Part2Configuration_IgnoresProbabilityOne() { yield return Part2IgnoresSetting(1f); }

    [UnityTest]
    public IEnumerator MixIsolation_RunningPart2_DoesNotApplyTheMixture_EvenWithAnAttractorConfiguration()
    {
        Rig rig = null;
        yield return BuildRig(MixRig(0f, 10), r => rig = r);   // cfg selects attractor movement; the probability is 0 (would be all wandering)
        yield return WaitUntil(() => Live(rig) == 10, 20f, "NPCs");
        long drawsBefore = Get<long>(rig.spawner, "Part3MovementDraws");
        // The same rig, but the ExperimentManager reports Part 2 as the running Part: new spawns must ignore the mixture.
        Type runningType = EmType.GetNestedType("RunningPart", All);
        EmType.GetField("currentRunningPart", All).SetValue(rig.em, Enum.Parse(runningType, "Part2"));
        Assert.AreEqual("Part2", Get<string>(rig.em, "RunningPartName"));
        Set(rig.spawner, "maxAgents", 20);
        yield return WaitUntil(() => Live(rig) == 20, 20f, "ten more NPCs");
        var late = ActiveAgents(rig).Skip(10).ToList();
        foreach (var a in late) Assert.AreEqual(AttrMode, ModeOf(a), "Part 2 as the running Part never reads the Part 3 probability: the configured (attractor) itinerary is used unchanged");
        Assert.AreEqual(drawsBefore, Get<long>(rig.spawner, "Part3MovementDraws"), "no draw was taken for them");
    }

    // The existing per-Part master switch (npcDirectedMovement = false -> wandering movement) still wins; no draw is taken.
    [UnityTest]
    public IEnumerator MixIsolation_PartConfiguredForWandering_StaysAllWandering_EvenAtProbabilityOne()
    {
        Rig rig = null;
        yield return BuildRig(MixRig(1f, 6), r => rig = r);
        long drawsBefore = Get<long>(rig.spawner, "Part3MovementDraws");
        Set(rig.cfg, "npcDirectedMovement", false);
        Set(rig.spawner, "maxAgents", 12);
        yield return WaitUntil(() => Live(rig) == 12, 20f, "NPCs");
        foreach (var a in ActiveAgents(rig).Skip(6)) Assert.AreEqual(WandMode, ModeOf(a));
        Assert.AreEqual(drawsBefore, Get<long>(rig.spawner, "Part3MovementDraws"));
    }

    // ====================================================================================== pooling and lifetimes

    // The same pooled object is initialised for alternating strategies: nothing of the previous strategy survives.
    [Test]
    public void MixPooling_OneObject_SwitchesStrategyBetweenLifetimes_WithoutStaleState()
    {
        var itinerary = new List<Transform> { Make("i0").transform, Make("i1").transform, Make("i2").transform };
        var init = PedType.GetMethod("Initialize", new[] { typeof(List<Transform>), SpawnerType, T("NpcMovementMode"), typeof(float), typeof(float) });
        Assert.IsNotNull(init, "the existing mode-aware Initialize overload");
        var agent = Make("PooledNpc").AddComponent(PedType);
        object attr = Enum.Parse(T("NpcMovementMode"), AttrMode), wand = Enum.Parse(T("NpcMovementMode"), WandMode);
        foreach (var life in new[] { attr, wand, attr, wand })
        {
            bool isAttr = life.ToString() == AttrMode;
            init.Invoke(agent, new object[] { itinerary, null, life, isAttr ? 3f : 0f, isAttr ? 0f : 1.5f });
            Assert.AreEqual(life.ToString(), ModeOf(agent));
            Assert.AreEqual(isAttr ? 3f : 0f, Get<float>(agent, "DwellSeconds"), "dwell belongs to the current lifetime only");
            if (!isAttr) Assert.AreEqual(1.5f, Get<float>(agent, "WanderLookAheadDistance"), "wandering lifetimes get the look-ahead passed at Initialize (Part 2-only state)");
        }
    }

    // ====================================================================================== accessory (rig level)

    // A profile whose accessory option is a bracelet with weight 1 ("100% accessory"): any NPC without a bracelet got none because of the Part 3 rule.
    UnityEngine.Object BraceletProfile(float braceletWeight, float noneWeight, float speed = 1.5f)
    {
        var prof = MakeProfile("AccProfile", 0.5f, false);
        Set(prof, "speed", speed);   // AgentFactory applies the profile speed (it overrides the template's NavMeshAgent speed)
        var opts = new List<object>();
        if (braceletWeight > 0f) opts.Add(MakeOption(MeshNamed("BraceletMesh"), new[] { Mat("BraceletMat") }, braceletWeight, "Unisex"));
        if (noneWeight > 0f) opts.Add(MakeOption(null, new Material[0], noneWeight, "Unisex"));
        SetOptions(prof, "accessoriesOptions", opts.ToArray());
        return prof;
    }

    static SkinnedMeshRenderer AccessoryRenderer(Component pa) => Get<SkinnedMeshRenderer>(pa.GetComponent(AppearanceType), "accessoriesRenderer");
    static bool HasBracelet(Component pa) => AccessoryRenderer(pa).sharedMesh != null;
    static int AccessoryOption(Component pa) => Get<int>(Get<object>(Get<object>(pa.GetComponent(AppearanceType), "LastApplied"), "accessories"), "optionIndex");

    [UnityTest]
    public IEnumerator MixAccessory_Part3WanderingNpcsWearNone_AttractorNpcsUseTheProfile()
    {
        Rig rig = null;
        var o = MixRig(0.5f, 30); o.withAppearance = true; o.withAccessories = true; o.profile = BraceletProfile(1f, 0f); o.runningPart = "Part3";
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 30, 30f, "30 NPCs");
        var agents = ActiveAgents(rig);
        int wanderers = 0, attractors = 0;
        foreach (var a in agents)
        {
            if (ModeOf(a) == WandMode)
            {
                wanderers++;
                Assert.IsFalse(HasBracelet(a), "a Part 3 wandering NPC wears no accessory even though its profile accessory share is 100% (" + Aid(a) + ")");
                Assert.IsNull(AccessoryRenderer(a).sharedMaterials[0], "no leftover material");
                Assert.AreEqual(-3, AccessoryOption(a), "recorded as deliberately suppressed");
            }
            else
            {
                attractors++;
                Assert.IsTrue(HasBracelet(a), "a Part 3 attractor NPC follows its profile (100% accessory) (" + Aid(a) + ")");
                Assert.AreEqual(0, AccessoryOption(a));
            }
        }
        Assert.Greater(wanderers, 0); Assert.Greater(attractors, 0);
    }

    // Profile accessory share 0 / 50 / 100 % for attractor NPCs: the profile's own rule decides; wandering NPCs wear none whatever the share is.
    IEnumerator AccessoryShare(float braceletWeight, float noneWeight)
    {
        Rig rig = null;
        var o = MixRig(0.5f, 60); o.withAppearance = true; o.withAccessories = true; o.profile = BraceletProfile(braceletWeight, noneWeight); o.runningPart = "Part3";
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 60, 40f, "60 NPCs");
        var agents = ActiveAgents(rig);
        var attractor = agents.Where(a => ModeOf(a) == AttrMode).ToList(); var wanderers = agents.Where(a => ModeOf(a) == WandMode).ToList();
        Assert.Greater(attractor.Count, 10); Assert.Greater(wanderers.Count, 10);
        foreach (var a in wanderers) Assert.IsFalse(HasBracelet(a), "wandering NPC wears none (profile share " + braceletWeight / (braceletWeight + noneWeight) + ")");
        double share = braceletWeight / (braceletWeight + noneWeight);
        int with = attractor.Count(HasBracelet);
        double sigma = Math.Sqrt(attractor.Count * share * (1 - share));
        Assert.LessOrEqual(Math.Abs(with - attractor.Count * share), 5 * sigma + 0.5, "attractor NPCs with a bracelet: " + with + "/" + attractor.Count + " for profile share " + share);
        if (share == 0.0) Assert.AreEqual(0, with);
        if (share == 1.0) Assert.AreEqual(attractor.Count, with);
    }

    [UnityTest] public IEnumerator MixAccessory_ProfileShare000_NobodyWearsOne() { yield return AccessoryShare(0f, 1f); }
    [UnityTest] public IEnumerator MixAccessory_ProfileShare050_OnlyAttractorNpcsAreEligible() { yield return AccessoryShare(1f, 1f); }
    [UnityTest] public IEnumerator MixAccessory_ProfileShare100_EveryAttractorNpcWearsOne() { yield return AccessoryShare(1f, 0f); }

    // Part 2 keeps its accessory assignment: a Part 2 wandering NPC still draws from the profile (100% bracelet here).
    [UnityTest]
    public IEnumerator MixAccessory_Part2WanderingNpcs_KeepTheirProfileAccessory()
    {
        Rig rig = null;
        var o = MixRig(1f, 10); o.part2 = true; o.withAppearance = true; o.withAccessories = true; o.profile = BraceletProfile(1f, 0f);   // p = 1: Part 2 accessory weights unscaled
        o.runningPart = "Part2";
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 10, 20f, "Part 2 NPCs");
        foreach (var a in ActiveAgents(rig)) { Assert.AreEqual(WandMode, ModeOf(a)); Assert.IsTrue(HasBracelet(a), "Part 2 accessory assignment is unchanged"); Assert.AreEqual(0, AccessoryOption(a)); }
    }

    // Pool reuse across Parts: Part 3 (mixed) -> Part 2 -> Part 3 wandering only -> Part 3 attractor only, always on the same few pooled objects.
    [UnityTest]
    public IEnumerator MixAccessory_PooledObjects_ReEvaluateTheRuleForEveryLifetime_AcrossPartChanges()
    {
        Rig rig = null;
        var o = MixRig(0.5f, 4, speed: 12f);
        o.compact = true; o.createDcm = true; o.withAppearance = true; o.withAccessories = true; o.profile = BraceletProfile(1f, 0f, speed: 12f); o.runningPart = "Part3";
        yield return BuildRig(o, r => rig = r);

        int Total() => Get<int>(rig.spawner, "NpcSpawnedTotal");
        Type runningType = EmType.GetNestedType("RunningPart", All);
        void SetRunning(string part) => EmType.GetField("currentRunningPart", All).SetValue(rig.em, Enum.Parse(runningType, part));
        // Stops spawning, waits until every NPC has finished (a clean phase boundary), switches the configuration, then spawns again.
        IEnumerator NextPhase(Action prepare)
        {
            Set(rig.spawner, "maxAgents", 0);
            yield return WaitUntil(() => Live(rig) == 0, 30f, "population to drain");
            prepare();
            Set(rig.spawner, "maxAgents", 4);
        }

        var bounds = new List<int>();
        bounds.Add(Total());                                               // phase 1: Part 3, p = 0.5
        yield return WaitUntil(() => Total() - bounds[0] >= 24, 60f, "phase 1 spawns");
        yield return NextPhase(() => { SetRunning("Part2"); Set(rig.cfg, "npcDirectedMovement", false); Set(rig.spawner, "part3AttractorMovementProbability", 1f); });   // phase 2: Part 2 (p = 1: accessory weights unscaled)
        bounds.Add(Total());
        yield return WaitUntil(() => Total() - bounds[1] >= 10, 60f, "phase 2 spawns");
        yield return NextPhase(() => { SetRunning("Part3"); Set(rig.cfg, "npcDirectedMovement", true); Set(rig.spawner, "part3AttractorMovementProbability", 0f); });   // phase 3: Part 3, p = 0
        bounds.Add(Total());
        yield return WaitUntil(() => Total() - bounds[2] >= 10, 60f, "phase 3 spawns");
        yield return NextPhase(() => { Set(rig.spawner, "part3AttractorMovementProbability", 1f); });   // phase 4: Part 3, p = 1
        bounds.Add(Total());
        yield return WaitUntil(() => Total() - bounds[3] >= 10, 60f, "phase 4 spawns");
        Set(rig.spawner, "maxAgents", 0);
        yield return WaitUntil(() => Live(rig) == 0, 30f, "final drain");
        bounds.Add(Total());
        yield return new WaitForSecondsRealtime(0.4f);

        var lt = Lifetimes(rig);
        AssertWidths(lt, "npc_lifetimes.csv");
        Assert.AreEqual(Total(), lt.Rows.Count, "one lifetime row per spawn");
        for (int phase = 0; phase < 4; phase++)
        {
            var rows = lt.Rows.Skip(bounds[phase]).Take(bounds[phase + 1] - bounds[phase]).ToList();
            Assert.Greater(rows.Count, 8, "phase " + (phase + 1));
            foreach (var r in rows)
            {
                string mode = lt.Get(r, "movement_mode"), mesh = lt.Get(r, "app_accessories_mesh"), opt = lt.Get(r, "app_accessories_option");
                switch (phase)
                {
                    case 0:   // Part 3, mixed
                        if (mode == WandMode) { Assert.AreEqual("", mesh, "wandering -> no accessory"); Assert.AreEqual("-3", opt); }
                        else { Assert.AreEqual("BraceletMesh", mesh, "attractor -> profile accessory (100%)"); Assert.AreEqual("0", opt); }
                        break;
                    case 1:   // Part 2: unchanged rule, even on objects that were suppressed in phase 1
                        Assert.AreEqual(WandMode, mode); Assert.AreEqual("BraceletMesh", mesh, "Part 2 wandering NPC keeps its profile accessory"); Assert.AreEqual("0", opt);
                        break;
                    case 2:   // Part 3, p = 0: wandering only, none
                        Assert.AreEqual(WandMode, mode); Assert.AreEqual("", mesh, "Part 2 -> Part 3 wandering: no accessory"); Assert.AreEqual("-3", opt);
                        break;
                    default:  // Part 3, p = 1: attractor only, the profile decides again
                        Assert.AreEqual(AttrMode, mode); Assert.AreEqual("BraceletMesh", mesh, "wandering -> attractor: profile accessory again"); Assert.AreEqual("0", opt);
                        break;
                }
            }
        }
        // the four pooled objects really served every phase, and some of them both strategies
        var byNpc = lt.Rows.GroupBy(r => lt.Get(r, "npc_id")).ToList();
        Assert.IsTrue(byNpc.All(g => g.Count() >= 4), "the four pooled objects were reused");
        Assert.IsTrue(byNpc.Any(g => g.Select(r => lt.Get(r, "movement_mode")).Distinct().Count() == 2), "some pooled object served both strategies");
    }


    // ====================================================================================== accessory scope: explicit Part 3 only

    // Which movement outcome a scope case expects: every NPC attractor, every NPC wandering, or the probability mixture.
    enum Expect { AllAttractor, AllWandering, Mixed }

    // Spawns real NPCs under a given running Part and checks the accessory of each against the ONE rule:
    //   no accessory  <=>  running Part is exactly Part 3  AND  the NPC's movement mode is the wandering itinerary
    // (option -3 only then). Every other NPC follows the profile (100% bracelet here, so a missing bracelet could only come from the suppression).
    IEnumerator AccessoryScope(string running, bool wanderingConfig, float p, Expect expect)
    {
        Rig rig = null;
        var o = MixRig(p, 16); o.withAppearance = true; o.withAccessories = true; o.profile = BraceletProfile(1f, 0f);
        o.runningPart = running; o.part2 = wanderingConfig;   // part2 = true  <=>  npcDirectedMovement = false (the Part's wandering configuration)
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 16, 30f, "16 NPCs");
        var agents = ActiveAgents(rig);
        int wanderers = agents.Count(a => ModeOf(a) == WandMode), attractors = agents.Count - wanderers;
        if (expect == Expect.AllAttractor) Assert.AreEqual(0, wanderers);
        if (expect == Expect.AllWandering) Assert.AreEqual(0, attractors);
        if (expect == Expect.Mixed) { Assert.Greater(wanderers, 0); Assert.Greater(attractors, 0); }
        foreach (var a in agents)
        {
            bool suppressed = running == "Part3" && ModeOf(a) == WandMode;
            Assert.AreEqual(!suppressed, HasBracelet(a), "visible accessory: running=" + running + " mode=" + ModeOf(a));
            Assert.AreEqual(suppressed ? -3 : 0, AccessoryOption(a), "recorded accessory marker: running=" + running + " mode=" + ModeOf(a));
            Assert.AreEqual(!suppressed, AccessoryRenderer(a).sharedMaterials.Length > 0 && AccessoryRenderer(a).sharedMaterials[0] != null, "material state");
        }
    }

    [UnityTest] public IEnumerator Scope_Part1_AttractorConfiguration_KeepsProfileAccessory() { yield return AccessoryScope("Part1", false, 0f, Expect.AllAttractor); }
    [UnityTest] public IEnumerator Scope_Part1_WanderingConfiguration_KeepsProfileAccessory() { yield return AccessoryScope("Part1", true, 0f, Expect.AllWandering); }
    [UnityTest] public IEnumerator Scope_Part2_WanderingConfiguration_KeepsProfileAccessory() { yield return AccessoryScope("Part2", true, 1f, Expect.AllWandering); }
    [UnityTest] public IEnumerator Scope_Part2_OtherMode_AttractorConfiguration_KeepsProfileAccessory() { yield return AccessoryScope("Part2", false, 1f, Expect.AllAttractor); }
    [UnityTest] public IEnumerator Scope_Part3_Wandering_NoDirectedMovement_AllNpcsWearNone() { yield return AccessoryScope("Part3", true, 1f, Expect.AllWandering); }
    [UnityTest] public IEnumerator Scope_Part3_ProbabilityZero_AllWandering_NoAccessory() { yield return AccessoryScope("Part3", false, 0f, Expect.AllWandering); }
    [UnityTest] public IEnumerator Scope_Part3_ProbabilityOne_AllAttractor_ProfileAccessory() { yield return AccessoryScope("Part3", false, 1f, Expect.AllAttractor); }
    [UnityTest] public IEnumerator Scope_Part3_Mixed_OnlyWanderingNpcsLoseTheAccessory() { yield return AccessoryScope("Part3", false, 0.5f, Expect.Mixed); }
    [UnityTest] public IEnumerator Scope_NoActivePart_WanderingConfiguration_KeepsProfileAccessory() { yield return AccessoryScope(null, true, 0f, Expect.AllWandering); }
    [UnityTest] public IEnumerator Scope_NoActivePart_AttractorConfiguration_KeepsProfileAccessory() { yield return AccessoryScope(null, false, 1f, Expect.AllAttractor); }
    [UnityTest] public IEnumerator Scope_NoActivePart_AttractorConfigurationWithMixedProbability_NoSelectorCall_AllAttractor() { yield return AccessoryScope(null, false, 0.5f, Expect.AllAttractor); }

    // The decision itself, for every (running Part, movement mode) pair, and with no ExperimentManager at all (unknown Part): only (Part3, wandering) suppresses.
    [UnityTest]
    public IEnumerator Scope_TruthTable_OnlyExplicitPart3WithWanderingModeSuppresses_UnknownPartNeverDoes()
    {
        Rig rig = null;
        var o = MixRig(0.5f, 0); o.maxAgents = 0;
        yield return BuildRig(o, r => rig = r);
        Type runningType = EmType.GetNestedType("RunningPart", All);
        object attr = Enum.Parse(T("NpcMovementMode"), AttrMode), wand = Enum.Parse(T("NpcMovementMode"), WandMode);
        foreach (string running in new[] { "None", "Part1", "Part2", "Part3" })
        {
            EmType.GetField("currentRunningPart", All).SetValue(rig.em, Enum.Parse(runningType, running));
            Assert.AreEqual(running == "Part3", (bool)Call(rig.spawner, "ShouldSuppressAccessory", wand), "running=" + running + " + wandering");
            Assert.IsFalse((bool)Call(rig.spawner, "ShouldSuppressAccessory", attr), "running=" + running + " + attractor never suppresses");
        }
        Set(rig.spawner, "experimentManager", null);
        Assert.IsFalse((bool)Call(rig.spawner, "ShouldSuppressAccessory", wand), "unknown Part (no ExperimentManager): the Part 3 rule is not assumed");
        Assert.IsFalse((bool)Call(rig.spawner, "ShouldSuppressAccessory", attr));
    }

    // ====================================================================================== pool transitions across Part scopes

    // One pooled population of 4 objects walks through the required Part / movement transitions. The profile's accessory options are switched between phases
    // (100% wristband, or 100% none) so each "previous lifetime" is a known state; visible state of the LIVE NPCs and the recorded rows must agree every time.
    [UnityTest]
    public IEnumerator Scope_PooledObjects_ThroughAllPartTransitions_NeverKeepStaleAccessoryState()
    {
        Rig rig = null;
        var o = MixRig(1f, 4, speed: 12f);
        o.compact = true; o.createDcm = true; o.withAppearance = true; o.withAccessories = true; o.profile = BraceletProfile(1f, 0f, speed: 12f); o.runningPart = "Part3";
        yield return BuildRig(o, r => rig = r);
        var prof = Get<UnityEngine.Object>(rig.spawner, "defaultProfile");
        Type runningType = EmType.GetNestedType("RunningPart", All);

        int Total() => Get<int>(rig.spawner, "NpcSpawnedTotal");
        void SetProfile(bool bracelet)
        {
            var opts = new List<object>();
            if (bracelet) opts.Add(MakeOption(MeshNamed("BraceletMesh"), new[] { Mat("BraceletMat") }, 1f, "Unisex")); else opts.Add(MakeOption(null, new Material[0], 1f, "Unisex"));
            SetOptions(prof, "accessoriesOptions", opts.ToArray());
        }
        // name, running Part, npcDirectedMovement, probability, profile has a wristband, expectation about the NEW lifetimes (wears?, marker)
        var phases = new (string name, string running, bool directed, float p, bool profileBracelet, bool suppressed)[]
        {
            ("P3 attractor (with wristband)",          "Part3", true,  1f, true,  false),
            ("P3 attractor->wandering",                "Part3", true,  0f, true,  true),
            ("P3 wandering->P2 wandering",             "Part2", false, 1f, true,  false),
            ("P2 with wristband->P3 wandering",        "Part3", true,  0f, true,  true),
            ("P3 wandering->P3 attractor, no wristband","Part3", true,  1f, false, false),
            ("P3 attractor w/o wristband->wandering",  "Part3", true,  0f, true,  true),
            ("P3 wandering->P3 attractor (profile)",   "Part3", true,  1f, true,  false),
            ("P3 attractor->P2 wandering",             "Part2", false, 1f, true,  false),
            ("P2 with wristband->no active Part",      "None",  false, 0f, true,  false),
            ("no active Part->P3 attractor",           "Part3", true,  1f, true,  false),
            ("P3 attractor->Part 1",                   "Part1", false, 0f, true,  false),
        };
        var bounds = new List<int>();
        for (int i = 0; i < phases.Length; i++)
        {
            var ph = phases[i];
            Set(rig.spawner, "maxAgents", 0);
            yield return WaitUntil(() => Live(rig) == 0, 30f, "population to drain before '" + ph.name + "'");
            EmType.GetField("currentRunningPart", All).SetValue(rig.em, Enum.Parse(runningType, ph.running));
            Set(rig.cfg, "npcDirectedMovement", ph.directed); Set(rig.spawner, "part3AttractorMovementProbability", ph.p); SetProfile(ph.profileBracelet);
            bounds.Add(Total());
            Set(rig.spawner, "maxAgents", 4);
            yield return WaitUntil(() => Total() - bounds[i] >= 6 && Live(rig) == 4, 60f, "phase '" + ph.name + "' spawns");
            // visible state of the live NPCs of this phase (all spawned after the drain, so all belong to it)
            foreach (var a in ActiveAgents(rig))
            {
                Assert.AreEqual(ph.profileBracelet && !ph.suppressed, HasBracelet(a), ph.name + ": visible wristband (mesh) of " + Aid(a));
                Assert.AreEqual(ph.profileBracelet && !ph.suppressed, AccessoryRenderer(a).sharedMaterials.Length > 0 && AccessoryRenderer(a).sharedMaterials[0] != null, ph.name + ": material state");
                Assert.IsTrue(AccessoryRenderer(a).gameObject.activeSelf, ph.name + ": accessory GameObject activation is never toggled");
                Assert.AreEqual(ph.suppressed ? -3 : 0, AccessoryOption(a), ph.name + ": applied-profile marker");
            }
        }
        Set(rig.spawner, "maxAgents", 0);
        yield return WaitUntil(() => Live(rig) == 0, 30f, "final drain");
        bounds.Add(Total());
        yield return new WaitForSecondsRealtime(0.4f);

        var lt = Lifetimes(rig);
        AssertWidths(lt, "npc_lifetimes.csv");
        Assert.AreEqual(Total(), lt.Rows.Count);
        for (int i = 0; i < phases.Length; i++)
        {
            var ph = phases[i];
            var rows = lt.Rows.Skip(bounds[i]).Take(bounds[i + 1] - bounds[i]).ToList();
            Assert.GreaterOrEqual(rows.Count, 6, ph.name);
            foreach (var r in rows)
            {
                string mesh = lt.Get(r, "app_accessories_mesh"), opt = lt.Get(r, "app_accessories_option");
                Assert.AreEqual(ph.suppressed ? "-3" : "0", opt, ph.name + ": recorded marker (-3 only for explicit Part 3 wandering)");
                Assert.AreEqual(ph.profileBracelet && !ph.suppressed ? "BraceletMesh" : "", mesh, ph.name + ": recorded mesh agrees with the visible state");
                if (ph.running != "Part3") Assert.AreNotEqual("-3", opt, ph.name + ": -3 never appears outside explicit Part 3");
            }
        }
        // the same four pooled objects served every phase
        Assert.IsTrue(lt.Rows.GroupBy(r => lt.Get(r, "npc_id")).All(g => g.Count() >= phases.Length), "the four pooled objects were reused across all phases");
    }

    // ====================================================================================== manifest: obsolete key

    // No maintained script writes or reads the retired active-population key or field (searches the maintained scripts, not the tests that assert its absence).
    [Test]
    public void MixManifest_ObsoleteProportionKeyAndFieldAppearNowhereInMaintainedScripts()
    {
        string scripts = System.IO.Path.Combine(Application.dataPath, "Scripts");
        var offenders = new List<string>();
        foreach (string file in System.IO.Directory.GetFiles(scripts, "*.cs", System.IO.SearchOption.AllDirectories))
        {
            string text = System.IO.File.ReadAllText(file);
            if (text.Contains("attractor_movement_proportion") || text.Contains("AttractorMovementProportion") || text.Contains("NpcMovementMixAllocator"))
                offenders.Add(file);
        }
        CollectionAssert.IsEmpty(offenders, "stale references to the retired allocation design");
    }


    // ====================================================================================== movement selector scope: explicit Part 3 only

    // The selector's own call counter (Part3MovementDraws = number of Choose calls) is the observable: it must stay 0 everywhere except explicit Part 3 with directed movement.
    IEnumerator SelectorScope(string running, bool wanderingConfig, bool expectCalls)
    {
        Rig rig = null;
        var o = MixRig(0.5f, 12); o.runningPart = running; o.part2 = wanderingConfig;
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 12, 30f, "12 NPCs");
        int spawned = Get<int>(rig.spawner, "NpcSpawnedTotal");
        long calls = Get<long>(rig.spawner, "Part3MovementDraws");
        if (expectCalls) Assert.AreEqual((long)spawned, calls, "explicit Part 3 with directed movement: exactly one selector call per spawn");
        else
        {
            Assert.AreEqual(0L, calls, "running=" + running + " wandering config=" + wanderingConfig + ": the Part 3 selector is never called");
            Assert.AreEqual(0L, Get<long>(rig.spawner, "AssignedAttractorMovementTotal") + Get<long>(rig.spawner, "AssignedWanderingMovementTotal"));
        }
        foreach (var a in ActiveAgents(rig))
            if (!expectCalls) Assert.AreEqual(wanderingConfig ? WandMode : AttrMode, ModeOf(a), "existing movement assignment of the configured itinerary");
    }

    [UnityTest] public IEnumerator SelectorScope_Part1_DirectedConfiguration_NoSelectorCall() { yield return SelectorScope("Part1", false, false); }
    [UnityTest] public IEnumerator SelectorScope_Part1_WanderingConfiguration_NoSelectorCall() { yield return SelectorScope("Part1", true, false); }
    [UnityTest] public IEnumerator SelectorScope_Part2_DirectedConfiguration_NoSelectorCall() { yield return SelectorScope("Part2", false, false); }
    [UnityTest] public IEnumerator SelectorScope_Part2_WanderingConfiguration_NoSelectorCall() { yield return SelectorScope("Part2", true, false); }
    [UnityTest] public IEnumerator SelectorScope_NoActivePart_DirectedConfiguration_NoSelectorCall() { yield return SelectorScope(null, false, false); }
    [UnityTest] public IEnumerator SelectorScope_NoActivePart_WanderingConfiguration_NoSelectorCall() { yield return SelectorScope(null, true, false); }
    [UnityTest] public IEnumerator SelectorScope_Part3_DirectedMovementOff_AllWandering_NoSelectorCall() { yield return SelectorScope("Part3", true, false); }
    [UnityTest] public IEnumerator SelectorScope_Part3_DirectedMovementOn_OneSelectorCallPerSpawn() { yield return SelectorScope("Part3", false, true); }

    // No ExperimentManager at all (unknown Part): the existing fallback (attractor itinerary) is returned and the selector is not called.
    [UnityTest]
    public IEnumerator SelectorScope_UnknownPart_NoExperimentManager_NoSelectorCall()
    {
        Rig rig = null;
        var o = MixRig(0.5f, 0); o.maxAgents = 0;
        yield return BuildRig(o, r => rig = r);
        Set(rig.spawner, "experimentManager", null);
        var m = SpawnerType.GetMethod("DecideMovementMode", All);
        for (int i = 0; i < 5; i++)
        {
            var args = new object[] { null };
            Assert.AreEqual(AttrMode, m.Invoke(rig.spawner, args).ToString());
            Assert.AreEqual(false, args[0], "no draw was applied");
        }
        Assert.AreEqual(0L, Get<long>(rig.spawner, "Part3MovementDraws"));
    }

    // Spawning under Part 1 / Part 2 / no Part leaves the selector's private sequence untouched: the first Part 3 spawns afterwards are exactly the first draws of the seed.
    [UnityTest]
    public IEnumerator SelectorScope_NonPart3Spawning_LeavesThePrivateSequenceUntouched()
    {
        Rig rig = null;
        var o = MixRig(0.5f, 6); o.runningPart = "Part1";
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 6, 20f, "Part 1 NPCs");
        Assert.AreEqual(0L, Get<long>(rig.spawner, "Part3MovementDraws"));
        EmType.GetField("currentRunningPart", All).SetValue(rig.em, Enum.Parse(EmType.GetNestedType("RunningPart", All), "Part2"));
        Set(rig.spawner, "maxAgents", 12);
        yield return WaitUntil(() => Live(rig) == 12, 20f, "Part 2 NPCs");
        Assert.AreEqual(0L, Get<long>(rig.spawner, "Part3MovementDraws"));
        EmType.GetField("currentRunningPart", All).SetValue(rig.em, Enum.Parse(EmType.GetNestedType("RunningPart", All), "Part3"));
        Set(rig.spawner, "maxAgents", 30);
        yield return WaitUntil(() => Live(rig) == 30, 30f, "Part 3 NPCs");
        var part3 = ActiveAgents(rig).Skip(12).Select(ModeOf).ToList();
        CollectionAssert.AreEqual(ExpectedModes(20261109, 0.5f, part3.Count), part3, "the stream starts at draw 1 of the seed");
        Assert.AreEqual((long)part3.Count, Get<long>(rig.spawner, "Part3MovementDraws"));
    }

    // ====================================================================================== Part 2 accessory weights: adjusted to match the overall Part 3 distribution

    static readonly Type AdjusterType = T("AccessoryWeightAdjuster");

    // A profile shaped like the production one: 8 wristband options + one None option last. Returns the profile and the original weights.
    UnityEngine.Object WeightedProfile(float[] wristbands, float none, float speed = 1.5f)
    {
        var prof = MakeProfile("WeightedProfile", 0.5f, false);
        Set(prof, "speed", speed);
        var opts = new List<object>();
        for (int i = 0; i < wristbands.Length; i++) opts.Add(MakeOption(MeshNamed("BraceletMesh"), new[] { Mat("BraceletMat") }, wristbands[i], "Unisex"));
        opts.Add(MakeOption(null, new Material[0], none, "Unisex"));
        SetOptions(prof, "accessoriesOptions", opts.ToArray());
        return prof;
    }

    static float[] WeightsOf(UnityEngine.Object prof) => ((Array)Get<object>(prof, "accessoriesOptions")).Cast<object>().Select(o => Get<float>(o, "weight")).ToArray();
    static float[] AdjustedFor(UnityEngine.Object prof, float a) => ((Array)AdjusterType.GetMethod("GetOptions").Invoke(null, new object[] { prof, a })).Cast<object>().Select(o => Get<float>(o, "weight")).ToArray();

    // Several profile shapes: the production one (8 x 0.05 + 0.6), unequal wristbands, a total that is not 1, a None weight of 0, an all-wristband heavy profile.
    static IEnumerable<(string name, float[] wrist, float none)> Shapes()
    {
        yield return ("production-like", Enumerable.Repeat(0.05f, 8).ToArray(), 0.6f);
        yield return ("unequal", new[] { 0.10f, 0.02f, 0.03f, 0.05f, 0.05f, 0.15f, 0.01f, 0.09f }, 0.5f);
        yield return ("total not 1", new[] { 1f, 2f, 3f, 4f, 1f, 1f, 2f, 1f }, 10f);
        yield return ("no None weight", Enumerable.Repeat(0.125f, 8).ToArray(), 0f);
        yield return ("mostly wristbands", Enumerable.Repeat(0.11f, 8).ToArray(), 0.12f);
    }

    [Test]
    public void Part2Weights_ElementsZeroToSevenScale_ElementEightTakesTheRemainder_TotalAndAssetUnchanged()
    {
        foreach (var sh in Shapes())
        {
            var prof = WeightedProfile(sh.wrist, sh.none);
            float[] original = WeightsOf(prof);
            Assert.AreEqual(9, original.Length);
            double W = original.Sum(x => (double)x);
            foreach (float a in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                float[] adj = AdjustedFor(prof, a);
                Assert.AreEqual(9, adj.Length, sh.name);
                for (int i = 0; i < 8; i++) Assert.AreEqual(a * original[i], adj[i], 1e-6f, sh.name + " a=" + a + " element " + i);
                Assert.AreEqual(W - adj.Take(8).Sum(x => (double)x), adj[8], 1e-5, sh.name + " a=" + a + ": element 8 receives the remaining weight");
                Assert.AreEqual(W, adj.Sum(x => (double)x), 1e-5, sh.name + " a=" + a + ": total weight unchanged");
                Assert.IsTrue(adj.All(x => !float.IsNaN(x) && !float.IsInfinity(x) && x >= 0f), sh.name + ": finite and non-negative");
                if (a == 0f) { Assert.IsTrue(adj.Take(8).All(x => x == 0f)); Assert.AreEqual(W, adj[8], 1e-5, "a = 0: everything goes to None"); }
                if (a == 1f) CollectionAssert.AreEqual(original, adj, sh.name + ": a = 1 leaves the weights unchanged");
                CollectionAssert.AreEqual(original, WeightsOf(prof), sh.name + " a=" + a + ": the shared profile asset is not modified");
            }
        }
    }

    [Test]
    public void Part2Weights_AreRecomputedFromTheOriginals_NeverCompound()
    {
        var prof = WeightedProfile(Enumerable.Repeat(0.05f, 8).ToArray(), 0.6f);
        var expectedHalf = Enumerable.Repeat(0.025f, 8).Concat(new[] { 0.8f }).ToArray();
        for (int i = 0; i < 5; i++) CollectionAssert.AreEqual(expectedHalf, AdjustedFor(prof, 0.5f), new FloatCmp(), "repeat " + i);
        CollectionAssert.AreEqual(Enumerable.Repeat(0.0125f, 8).Concat(new[] { 0.9f }).ToArray(), AdjustedFor(prof, 0.25f), new FloatCmp(), "a different a is computed from the originals, not from the previous result");
        // editing the original weights is noticed (the cache is validated against the current originals): wristband 0 becomes 0.2, W = 1.15
        var opts = (Array)Get<object>(prof, "accessoriesOptions");
        Set(opts.GetValue(0), "weight", 0.2f);
        float[] changed = AdjustedFor(prof, 0.5f);
        Assert.AreEqual(0.1f, changed[0], 1e-6f); Assert.AreEqual(0.025f, changed[1], 1e-6f);
        Assert.AreEqual(1.15 - (0.1 + 7 * 0.025), changed[8], 1e-5);
        Assert.AreEqual(1.15, changed.Sum(x => (double)x), 1e-5);
        CollectionAssert.AreEqual(new[] { 0.2f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.6f }, WeightsOf(prof), new FloatCmp());
    }

    class FloatCmp : System.Collections.IComparer
    {
        public int Compare(object x, object y) => Math.Abs((float)x - (float)y) < 1e-6f ? 0 : ((float)x < (float)y ? -1 : 1);
    }

    [Test]
    public void Part2Weights_ProfilesWithAnUnexpectedStructure_KeepTheirOriginalWeights()
    {
        var bad = new List<UnityEngine.Object>();
        var noNone = MakeProfile("NoNone", 0.5f, false); SetOptions(noNone, "accessoriesOptions", new[] { MakeOption(MeshNamed("BraceletMesh"), new[] { Mat("BraceletMat") }, 1f, "Unisex"), MakeOption(MeshNamed("BraceletMesh"), new[] { Mat("BraceletMat") }, 1f, "Unisex") });
        var twoNone = MakeProfile("TwoNone", 0.5f, false); SetOptions(twoNone, "accessoriesOptions", new[] { MakeOption(MeshNamed("BraceletMesh"), new[] { Mat("BraceletMat") }, 1f, "Unisex"), MakeOption(null, new Material[0], 1f, "Unisex"), MakeOption(null, new Material[0], 1f, "Unisex") });
        var gendered = MakeProfile("Gendered", 0.5f, false); SetOptions(gendered, "accessoriesOptions", new[] { MakeOption(MeshNamed("BraceletMesh"), new[] { Mat("BraceletMat") }, 1f, "MaleOnly"), MakeOption(null, new Material[0], 1f, "Unisex") });
        var negative = MakeProfile("Negative", 0.5f, false); SetOptions(negative, "accessoriesOptions", new[] { MakeOption(MeshNamed("BraceletMesh"), new[] { Mat("BraceletMat") }, -1f, "Unisex"), MakeOption(null, new Material[0], 1f, "Unisex") });
        var onlyNone = MakeProfile("OnlyNone", 0.5f, false); SetOptions(onlyNone, "accessoriesOptions", new[] { MakeOption(null, new Material[0], 1f, "Unisex") });
        foreach (var prof in new[] { noNone, twoNone, gendered, negative, onlyNone })
        {
            var src = Get<object>(prof, "accessoriesOptions");
            var got = AdjusterType.GetMethod("GetOptions").Invoke(null, new object[] { prof, 0.5f });
            Assert.AreSame(src, got, prof.name + ": a differently structured profile is never index-adjusted");
        }
        // an empty list and a null profile are passed through
        Assert.IsNull(AdjusterType.GetMethod("GetOptions").Invoke(null, new object[] { null, 0.5f }));
        // the structured profile is adjusted, a = 1 returns the very same array
        var ok = WeightedProfile(Enumerable.Repeat(0.05f, 8).ToArray(), 0.6f);
        Assert.AreSame(Get<object>(ok, "accessoriesOptions"), AdjusterType.GetMethod("GetOptions").Invoke(null, new object[] { ok, 1f }));
        Assert.AreNotSame(Get<object>(ok, "accessoriesOptions"), AdjusterType.GetMethod("GetOptions").Invoke(null, new object[] { ok, 0.5f }));
    }

    // Exact matching: for every profile shape and every a, the probability Part 2 gives each element equals the overall Part 3 probability.
    [Test]
    public void Part2Weights_MatchTheOverallPart3Distribution_ForEveryElementAndProfile()
    {
        foreach (var sh in Shapes())
        {
            var prof = WeightedProfile(sh.wrist, sh.none);
            double[] w = WeightsOf(prof).Select(x => (double)x).ToArray(); double W = w.Sum();
            foreach (float a in new[] { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 1f })
            {
                double[] adj = AdjustedFor(prof, a).Select(x => (double)x).ToArray();
                for (int i = 0; i < 9; i++)
                {
                    double part2 = adj[i] / adj.Sum();
                    // Part 3 overall: attractor NPCs (probability a) draw from the ORIGINAL weights; wandering NPCs (1 - a) always get None.
                    double part3 = a * (w[i] / W) + (i == 8 ? (1 - a) : 0.0);
                    Assert.AreEqual(part3, part2, 1e-6, sh.name + " a=" + a + " element " + i);
                    if (i < 8) Assert.AreEqual(a * w[i] / W, part2, 1e-6, "wristband formula");
                    else Assert.AreEqual(1 - a * (1 - w[8] / W), part2, 1e-6, "None formula");
                }
            }
        }
    }

    // Part 3 attractor weights are the original ones whatever a is: nothing in the adjuster is applied outside Part 2.
    [Test]
    public void Part2Weights_ScaleAndProbabilityInputsAreSanitised()
    {
        // NaN / out-of-range inputs are clamped (the directed-off rule a_eff = 0 is covered at rig level)
        var sel = T("Part3MovementStrategySelector");
        Assert.AreEqual(1f, (float)sel.GetMethod("SanitizeProbability").Invoke(null, new object[] { float.NaN }));
        Assert.AreEqual(0f, AdjusterType.GetMethod("SanitizeScale").Invoke(null, new object[] { -1f }));
        Assert.AreEqual(1f, AdjusterType.GetMethod("SanitizeScale").Invoke(null, new object[] { 7f }));
    }

    // ---------------- rig level (real spawns)

    // The Part 3 directed switch lives in the Part 3 configuration; the Part 2 accessory scale reads it through ExperimentManager.Part3NpcDirectedMovement.
    static void ConfigurePart3Directed(object em, bool directed)
    {
        object cfg3 = Activator.CreateInstance(CfgType);
        Set(cfg3, "npcEnabled", true); Set(cfg3, "npcDirectedMovement", directed);
        Set(em, "part3Config", cfg3);
        // once the session settings are frozen (from Start on) ExperimentManager reads the frozen copy of the Part 3 config: keep it in step
        var frozen = (Array)EmType.GetField("_frozenPartConfigs", All).GetValue(em);
        if (EmType.GetField("_effective", All).GetValue(em) != null && frozen.GetValue(2) != null) Set(frozen.GetValue(2), "npcDirectedMovement", directed);
    }

    IEnumerator Part2WristbandShare(float p, bool part3Directed, double expectedShare)
    {
        Rig rig = null;
        var o = MixRig(p, 120); o.part2 = true; o.runningPart = "Part2"; o.withAppearance = true; o.withAccessories = true;
        o.profile = WeightedProfile(Enumerable.Repeat(0.05f, 8).ToArray(), 0.6f);
        o.emSetup = em => ConfigurePart3Directed(em, part3Directed);
        yield return BuildRig(o, r => rig = r);
        Assert.AreEqual(part3Directed, Get<bool>(rig.em, "Part3NpcDirectedMovement"));
        Assert.AreEqual(part3Directed ? p : 0f, Get<float>(rig.spawner, "EffectivePart3AttractorProbability"), 1e-6f);
        yield return WaitUntil(() => Live(rig) == 120, 60f, "120 Part 2 NPCs");
        var agents = ActiveAgents(rig);
        int with = agents.Count(HasBracelet);
        double sigma = Math.Sqrt(120 * expectedShare * (1 - expectedShare));
        Assert.LessOrEqual(Math.Abs(with - 120 * expectedShare), 5 * sigma + 0.5, "Part 2 wristband share " + with + "/120, expected " + expectedShare);
        if (expectedShare == 0.0) Assert.AreEqual(0, with);
        foreach (var a in agents)
        {
            Assert.AreEqual(WandMode, ModeOf(a), "Part 2 movement is unchanged");
            int opt = AccessoryOption(a);
            Assert.AreNotEqual(-3, opt, "-3 is only for explicit Part 3 wandering suppression");
            Assert.AreEqual(opt != 8, HasBracelet(a), "element 8 is the normal weighted None option (no mesh), every other element has the mesh");
        }
        Assert.AreEqual(0L, Get<long>(rig.spawner, "Part3MovementDraws"));
        // the shared profile was not modified by 120 spawns
        CollectionAssert.AreEqual(Enumerable.Repeat(0.05f, 8).Concat(new[] { 0.6f }).ToArray(), WeightsOf(o.profile), new FloatCmp());
    }

    [UnityTest] public IEnumerator Part2Accessory_Probability000_NoWristbands() { yield return Part2WristbandShare(0f, true, 0.0); }
    [UnityTest] public IEnumerator Part2Accessory_Probability050_WristbandShareIsHalfTheProfileShare() { yield return Part2WristbandShare(0.5f, true, 0.2); }
    [UnityTest] public IEnumerator Part2Accessory_Probability100_ProfileWeightsUnchanged() { yield return Part2WristbandShare(1f, true, 0.4); }
    [UnityTest] public IEnumerator Part2Accessory_Part3DirectedMovementOff_EffectiveProbabilityZero_NoWristbands() { yield return Part2WristbandShare(1f, false, 0.0); }

    // Part 3 with the same profile: attractor NPCs keep the original weights (incl. a normal None selection recorded as element 8), wanderers are forced to -3, and the
    // overall wristband share equals the Part 2 share for the same a.
    [UnityTest]
    public IEnumerator Part3Accessory_AttractorsKeepOriginalWeights_NoneIsNotMinusThree_OverallShareMatchesPart2()
    {
        Rig rig = null;
        var o = MixRig(0.5f, 120); o.withAppearance = true; o.withAccessories = true; o.profile = WeightedProfile(Enumerable.Repeat(0.05f, 8).ToArray(), 0.6f);
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 120, 60f, "120 Part 3 NPCs");
        var agents = ActiveAgents(rig);
        var attractors = agents.Where(a => ModeOf(a) == AttrMode).ToList(); var wanderers = agents.Where(a => ModeOf(a) == WandMode).ToList();
        Assert.Greater(attractors.Count, 30); Assert.Greater(wanderers.Count, 30);
        foreach (var w in wanderers) { Assert.AreEqual(-3, AccessoryOption(w)); Assert.IsFalse(HasBracelet(w)); }
        foreach (var a in attractors) { int opt = AccessoryOption(a); Assert.GreaterOrEqual(opt, 0); Assert.LessOrEqual(opt, 8); Assert.AreEqual(opt != 8, HasBracelet(a)); }
        Assert.IsTrue(attractors.Any(a => AccessoryOption(a) == 8), "attractor NPCs select the None element normally (recorded as 8, not -3)");
        double share = 0.4; int with = attractors.Count(HasBracelet);
        Assert.LessOrEqual(Math.Abs(with - attractors.Count * share), 5 * Math.Sqrt(attractors.Count * share * (1 - share)) + 0.5, "attractor wristband share follows the ORIGINAL profile weights (0.4)");
        int overall = agents.Count(HasBracelet);
        Assert.LessOrEqual(Math.Abs(overall - 120 * 0.2), 5 * Math.Sqrt(120 * 0.2 * 0.8) + 0.5, "overall Part 3 wristband share = a * 0.4 = 0.2");
    }

    // Pool reuse across Part 2 (scaled), Part 3 (original / forced None), Part 1 and no Part: the profile is [wristband weight 1, None weight 0], so every outcome is
    // deterministic: Part 2 with a = 0 selects the normal None element (index 1, never -3), all other normal contexts select the wristband.
    [UnityTest]
    public IEnumerator Part2Accessory_PooledObjects_ScaledOnlyInPart2_NeverStale_MinusThreeOnlyForPart3Wandering()
    {
        Rig rig = null;
        var o = MixRig(1f, 4, speed: 12f);
        o.compact = true; o.createDcm = true; o.withAppearance = true; o.withAccessories = true; o.profile = WeightedProfile(new[] { 1f }, 0f, speed: 12f);
        yield return BuildRig(o, r => rig = r);
        Type runningType = EmType.GetNestedType("RunningPart", All);
        int Total() => Get<int>(rig.spawner, "NpcSpawnedTotal");
        // name, running Part, Part 3 directed, npcDirectedMovement of the running Part, probability, expected option (0 wristband, 1 normal None, -3 forced)
        var phases = new (string name, string running, bool p3Directed, bool directed, float p, int option)[]
        {
            ("P3 attractor",                         "Part3", true,  true,  1f, 0),
            ("P3 attractor -> P2 (a = 0)",           "Part2", true,  false, 0f, 1),
            ("P2 -> P3 attractor",                   "Part3", true,  true,  1f, 0),
            ("P3 attractor -> P3 wandering",         "Part3", true,  true,  0f, -3),
            ("P3 wandering -> P2 (a = 0)",           "Part2", true,  false, 0f, 1),
            ("P2 (a = 0) -> no active Part",         "None",  true,  false, 0f, 0),
            ("no active Part -> P2 (a = 0)",         "Part2", true,  false, 0f, 1),
            ("P2 (a = 0) -> Part 1",                 "Part1", true,  false, 0f, 0),
            ("Part 1 -> P2 (a = 1, unchanged)",      "Part2", true,  false, 1f, 0),
            ("P2, Part 3 directed OFF (a_eff = 0)",  "Part2", false, false, 1f, 1),
            ("P2 (a_eff = 0) -> P3 wandering",       "Part3", true,  false, 1f, -3),
        };
        var bounds = new List<int>();
        for (int i = 0; i < phases.Length; i++)
        {
            var ph = phases[i];
            Set(rig.spawner, "maxAgents", 0);
            yield return WaitUntil(() => Live(rig) == 0, 30f, "drain before '" + ph.name + "'");
            EmType.GetField("currentRunningPart", All).SetValue(rig.em, Enum.Parse(runningType, ph.running));
            ConfigurePart3Directed(rig.em, ph.p3Directed);
            Set(rig.cfg, "npcDirectedMovement", ph.directed); Set(rig.spawner, "part3AttractorMovementProbability", ph.p);
            bounds.Add(Total());
            Set(rig.spawner, "maxAgents", 4);
            yield return WaitUntil(() => Total() - bounds[i] >= 6 && Live(rig) == 4, 60f, "phase '" + ph.name + "' spawns");
            foreach (var a in ActiveAgents(rig))
            {
                Assert.AreEqual(ph.option, AccessoryOption(a), ph.name + ": applied option of " + Aid(a));
                Assert.AreEqual(ph.option == 0, HasBracelet(a), ph.name + ": visible wristband");
                Assert.AreEqual(ph.option == 0, AccessoryRenderer(a).sharedMaterials.Length > 0 && AccessoryRenderer(a).sharedMaterials[0] != null, ph.name + ": material state");
                Assert.IsTrue(AccessoryRenderer(a).gameObject.activeSelf, ph.name + ": accessory GameObject activation");
            }
        }
        Set(rig.spawner, "maxAgents", 0);
        yield return WaitUntil(() => Live(rig) == 0, 30f, "final drain");
        bounds.Add(Total());
        yield return new WaitForSecondsRealtime(0.4f);
        var lt = Lifetimes(rig);
        AssertWidths(lt, "npc_lifetimes.csv");
        for (int i = 0; i < phases.Length; i++)
        {
            var ph = phases[i];
            foreach (var r in lt.Rows.Skip(bounds[i]).Take(bounds[i + 1] - bounds[i]))
            {
                Assert.AreEqual(ph.option.ToString(), lt.Get(r, "app_accessories_option"), ph.name + ": recorded marker");
                Assert.AreEqual(ph.option == 0 ? "BraceletMesh" : "", lt.Get(r, "app_accessories_mesh"), ph.name + ": recorded mesh agrees with the visible state");
            }
        }
        Assert.IsTrue(lt.Rows.GroupBy(r => lt.Get(r, "npc_id")).All(g => g.Count() >= phases.Length), "the same pooled objects served every phase");
    }

    // ====================================================================================== recorded data

    [UnityTest]
    public IEnumerator MixRecording_LifetimesReproduceTheSeededAssignments_AndTheManifestRecordsProbabilityAndSeed()
    {
        Rig rig = null;
        var o = MixRig(0.4f, 6, speed: 12f);
        o.compact = true; o.createDcm = true; o.movementSeed = 987654;
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Get<int>(rig.spawner, "NpcSpawnedTotal") >= 50, 60f, "50 spawns");
        Set(rig.spawner, "maxAgents", 0);
        yield return WaitUntil(() => Live(rig) == 0, 30f, "drain");
        yield return new WaitForSecondsRealtime(0.3f);

        var lt = Lifetimes(rig);
        AssertWidths(lt, "npc_lifetimes.csv");
        int total = lt.Rows.Count;
        Assert.AreEqual(Get<int>(rig.spawner, "NpcSpawnedTotal"), total, "one lifetime row per spawn");
        var recorded = lt.Rows.Select(r => lt.Get(r, "movement_mode")).ToList();
        CollectionAssert.AreEqual(ExpectedModes(987654, 0.4f, total), recorded, "npc_lifetimes.csv movement_mode, in spawn order, is exactly the seeded draw sequence: no balancing, even with churn");
        var rowsA = lt.Rows.Where(r => lt.Get(r, "movement_mode") == AttrMode).ToList();
        var rowsW = lt.Rows.Where(r => lt.Get(r, "movement_mode") == WandMode).ToList();
        Assert.Greater(rowsA.Count, 5); Assert.Greater(rowsW.Count, 5);
        Assert.AreEqual(Get<long>(rig.spawner, "AssignedAttractorMovementTotal"), (long)rowsA.Count);
        Assert.AreEqual(Get<long>(rig.spawner, "AssignedWanderingMovementTotal"), (long)rowsW.Count);
        foreach (var r in rowsA) { StringAssert.Contains("Attractor_A", lt.Get(r, "itinerary_ids")); Assert.AreNotEqual("", lt.Get(r, "dwell_time_s")); Assert.AreEqual("", lt.Get(r, "wander_look_ahead_m")); }
        foreach (var r in rowsW) { StringAssert.DoesNotContain("Attractor_A", lt.Get(r, "itinerary_ids")); Assert.AreEqual("", lt.Get(r, "dwell_time_s")); Assert.AreNotEqual("", lt.Get(r, "wander_look_ahead_m")); }

        var m = ManifestOf(rig);
        var entries = ((JArray)m["setting_entries"]).Cast<JObject>().ToList();
        var prob = entries.FirstOrDefault(e => (string)e["key"] == "spawner_part3_attractor_movement_probability");
        Assert.IsNotNull(prob, "the configured per-spawn probability is in the manifest's setting_entries");
        Assert.AreEqual("0.4", (string)prob["value"]); Assert.AreEqual("INSPECTOR", (string)prob["source"]);
        var seed = entries.FirstOrDefault(e => (string)e["key"] == "spawner_part3_movement_assignment_seed");
        Assert.IsNotNull(seed); Assert.AreEqual("987654", (string)seed["value"]);
        Assert.IsNull(entries.FirstOrDefault(e => (string)e["key"] == "spawner_part3_attractor_movement_proportion"), "the retired active-population key is never written again");
        StringAssert.Contains("Part 3 movement-strategy draw: private System.Random seed=987654", (string)m["npc_random_sources"]);
        Assert.AreEqual(17, (int)m["schema_version"], "no schema bump: new setting_entries keys and no new column");
    }
}
