// NpcRngIntegrationTests.cs
// Rig-level tests of the experiment RNG on real NPC spawns (a partial of NpcLifecycleTests, so the production rig - SpawnerManager, AgentFactory, AppearanceController,
// PedestrianAgent, UncertainBehavior, DataCaptureManager on a runtime NavMesh - is reused; everything is reached by reflection). Unity Editor tests, not Quest tests.
//   * An NPC's initial attributes (movement assignment, dwell time, speed multiplier, uncertain roll, gender) equal the values RECONSTRUCTED from (Master Seed, Part, spawn index).
//   * Directed movement off / Part 1 / Part 2 / no Part take no movement-assignment value; the session never touches UnityEngine.Random.
//   * The manifest rng block and the npc_lifetimes rng_* columns record the effective seed, algorithm, versions and draw counts.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    static readonly Type PcgStreamType = T("Pcg32");

    static object Stream(object npcSet, string id) => Call(npcSet, "Get", Enum.Parse(T("RngStream"), id));
    static float StreamFloat(object stream) => (float)Call(stream, "NextFloat");
    static float StreamRange(object stream, float a, float b) => (float)PcgStreamType.GetMethod("Range", new[] { typeof(float), typeof(float) }).Invoke(stream, new object[] { a, b });

    UnityEngine.Object RngProfile(float female, float uncertainProbability)
    {
        var prof = MakeProfile("RngProfile", female, false);
        Set(prof, "uncertainProbability", uncertainProbability);
        Set(prof, "speed", 1.5f);
        var so = T("AgentProfile").GetNestedType("SpeedOption");
        Array opts = Array.CreateInstance(so, 3);
        float[][] spec = { new[] { 0.6f, 0.25f }, new[] { 1.4f, 0.5f }, new[] { 2.2f, 0.25f } };
        for (int i = 0; i < 3; i++) { object o = Activator.CreateInstance(so); Set(o, "multiplier", spec[i][0]); Set(o, "weight", spec[i][1]); opts.SetValue(o, i); }
        Set(prof, "speedOptions", opts);
        return prof;
    }

    // Every initial attribute of every NPC is the value of ITS stream, i.e. reconstructable from the Master Seed, the Part and the spawn index alone.
    [UnityTest]
    public IEnumerator Rng_NpcInitialAttributes_AreReconstructableFromMasterSeedPartAndSpawnIndex()
    {
        const ulong master = 777;
        Rig rig = null;
        var o = MixRig(0.5f, 30, speed: 1.5f);
        o.masterSeed = master;                          // the Master Seed is the only root, also of the movement assignment
        o.withAppearance = true; o.withUncertain = true; o.uncertainEnabled = false; o.profile = RngProfile(0.5f, 0.5f);
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 30, 30f, "30 NPCs");
        Assert.AreEqual(master, GetStatic<ulong>(T("ExperimentRngManager"), "EffectiveMasterSeed"));
        var agents = ActiveAgents(rig);
        var dwell = Get<Vector2>(rig.spawner, "dwellTimeSec");
        int attractors = 0, wanderers = 0, female = 0, uncertain = 0;
        foreach (var a in agents)
        {
            long idx = Get<long>(a, "RngSpawnIndex");
            Assert.AreEqual("Part3", Get<object>(a, "RngPart").ToString());
            object set = Activator.CreateInstance(T("NpcRngSet"), master, Enum.Parse(T("RngPart"), "Part3"), idx);
            // movement assignment: first value of the MovementAssignment stream
            string expectedMode = Predict(master, idx, 0.5f);
            Assert.AreEqual(expectedMode, ModeOf(a), "movement mode of spawn " + idx);
            if (expectedMode == AttrMode) attractors++; else wanderers++;
            // MovementAttributes stream order in SpawnAgent / AgentFactory: dwell time (attractor only), speed multiplier, uncertain roll
            object ma = Stream(set, "MovementAttributes");
            if (expectedMode == AttrMode) Assert.AreEqual(StreamRange(ma, dwell.x, dwell.y), Get<float>(a, "DwellSeconds"), 1e-5f, "dwell time of spawn " + idx);
            float mult = (float)T("AgentProfile").GetMethod("ChooseWeightedSpeedMultiplier", new[] { PcgStreamType }).Invoke(o.profile, new object[] { ma });
            Assert.AreEqual(mult, Get<float>(a, "SpeedMultiplier"), 1e-5f, "speed multiplier of spawn " + idx);
            bool unc = StreamFloat(ma) < 0.5f;
            Assert.AreEqual(unc, Get<bool>(a, "UncertainBehaviorEnabled"), "uncertain roll of spawn " + idx);
            if (unc) uncertain++;
            // appearance stream: the gender is its first value
            object gender = T("AgentProfile").GetMethod("ChooseGender", new[] { PcgStreamType }).Invoke(o.profile, new object[] { Stream(set, "Appearance") });
            Assert.AreEqual(gender.ToString(), Get<object>(a.GetComponent(AppearanceType), "CurrentGender").ToString(), "gender of spawn " + idx);
            if (gender.ToString() == "Female") female++;
        }
        Assert.Greater(attractors, 3); Assert.Greater(wanderers, 3); Assert.Greater(female, 3); Assert.Greater(uncertain, 3);   // the checks above are not vacuous
    }

    // Only explicit Part 3 with directed movement takes a movement-assignment value, as counted by the experiment RNG itself.
    IEnumerator AssignmentValueCount(string running, bool wanderingConfig, bool expectValues)
    {
        Rig rig = null;
        var o = MixRig(0.5f, 10); o.runningPart = running; o.part2 = wanderingConfig; o.masterSeed = 5;
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 10, 30f, "10 NPCs");
        long values = (long)CallStatic(T("ExperimentRngCounters"), "Total", Enum.Parse(T("RngStream"), "MovementAssignment"));
        if (expectValues) Assert.AreEqual((long)Get<int>(rig.spawner, "NpcSpawnedTotal"), values, "one MovementAssignment value per Part 3 spawn");
        else Assert.AreEqual(0L, values, "running=" + running + " wandering config=" + wanderingConfig + ": no movement-assignment value is consumed");
    }

    [UnityTest] public IEnumerator Rng_Part3DirectedMovementOff_ConsumesNoMovementAssignmentValue() { yield return AssignmentValueCount("Part3", true, false); }
    [UnityTest] public IEnumerator Rng_Part1_ConsumesNoMovementAssignmentValue() { yield return AssignmentValueCount("Part1", false, false); }
    [UnityTest] public IEnumerator Rng_Part2_ConsumesNoMovementAssignmentValue() { yield return AssignmentValueCount("Part2", true, false); }
    [UnityTest] public IEnumerator Rng_NoActivePart_ConsumesNoMovementAssignmentValue() { yield return AssignmentValueCount(null, false, false); }
    [UnityTest] public IEnumerator Rng_Part3Directed_ConsumesExactlyOneMovementAssignmentValuePerSpawn() { yield return AssignmentValueCount("Part3", false, true); }

    // Nothing in the NPC simulation (pool prewarm, spawn, profile roll, routes, placement, hesitation, nudges) touches UnityEngine.Random.
    [UnityTest]
    public IEnumerator Rng_NpcSimulation_NeverTouchesUnityRandom()
    {
        UnityEngine.Random.InitState(424242);
        string before = JsonUtility.ToJson(UnityEngine.Random.state);
        Rig rig = null;
        var o = MixRig(0.5f, 20, speed: 3f);
        o.masterSeed = 11; o.withAppearance = true; o.withUncertain = true; o.uncertainEnabled = true; o.profile = RngProfile(0.5f, 1f);
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 20, 30f, "20 NPCs");
        yield return new WaitForSecondsRealtime(1.0f);
        Assert.Greater((long)CallStatic(T("ExperimentRngCounters"), "Total", Enum.Parse(T("RngStream"), "Uncertainty")), 0L, "uncertain NPCs drew from their Uncertainty streams");
        Assert.AreEqual(before, JsonUtility.ToJson(UnityEngine.Random.state), "UnityEngine.Random state is untouched by NPC spawning, routes, appearance and behaviour");
    }

    // Manifest + lifetimes: the effective seed, algorithm and versions, stream inventory, draw counts, and the per-NPC rng_* columns.
    [UnityTest]
    public IEnumerator Rng_Manifest_RecordsEffectiveSeedAlgorithmVersionsAndDrawCounts()
    {
        Rig rig = null;
        var o = MixRig(0.5f, 6, speed: 12f);
        o.compact = true; o.createDcm = true; o.masterSeed = 31415;
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Get<int>(rig.spawner, "NpcSpawnedTotal") >= 20, 60f, "20 spawns");
        Set(rig.spawner, "maxAgents", 0);
        yield return WaitUntil(() => Live(rig) == 0, 30f, "drain");
        yield return new WaitForSecondsRealtime(0.3f);
        var m = ManifestOf(rig);
        var r2 = (JObject)m["rng"];
        Assert.IsNotNull(r2, "the manifest has an rng block");
        Assert.AreEqual("PCG32-XSH-RR", (string)r2["rng_algorithm"]); Assert.AreEqual(1, (int)r2["rng_algorithm_version"]);
        Assert.AreEqual("SplitMix64-chain", (string)r2["rng_seed_derivation"]); Assert.AreEqual(1, (int)r2["rng_seed_derivation_version"]); Assert.AreEqual(3, (int)r2["rng_config_version"]); Assert.AreEqual("uint64_hex16", (string)r2["master_seed_representation"]);
        Assert.AreEqual("0000000000007ab7", (string)r2["configured_master_seed"]); Assert.AreEqual("0000000000007ab7", (string)r2["effective_master_seed"]); Assert.IsNull(r2["configured_seed_format"]);
        Assert.AreEqual("fixed", (string)r2["seed_source"]); Assert.AreEqual("shared_fixed_seed", (string)r2["seed_policy"]);
        CollectionAssert.AreEquivalent(Enum.GetNames(T("RngStream")), r2["stream_inventory"].Select(t => (string)t).ToArray());
        Assert.IsNull(r2["legacy_overrides"], "the legacy override list no longer exists");
        Assert.IsTrue(r2["stream_draw_counts"].Select(t => (string)t).Any(t => t.StartsWith("Part3:MovementAssignment=")), "draw-count summary");
        StringAssert.Contains("rng_spawn_index", (string)r2["npc_stream_seed_rule"]);
        var entries = ((JArray)m["setting_entries"]).Cast<JObject>().ToList();
        Assert.AreEqual("0000000000007ab7", (string)entries.First(e => (string)e["key"] == "experiment_rng_master_seed_effective")["value"]);
        Assert.AreEqual("0000000000007ab7", (string)entries.First(e => (string)e["key"] == "experiment_rng_master_seed_configured")["value"]);
        Assert.IsNull(entries.FirstOrDefault(e => (string)e["key"] == "spawner_part3_movement_assignment_seed"), "no separate movement-assignment seed exists");
        Assert.IsNull(entries.FirstOrDefault(e => (string)e["key"] == "experiment_rng_master_seed_format"));
        Assert.AreEqual(17, (int)m["schema_version"]);

        var lt = Lifetimes(rig);
        AssertWidths(lt, "npc_lifetimes.csv");
        Assert.IsTrue(lt.Rows.All(r => lt.Get(r, "rng_part") == "Part3" && long.Parse(lt.Get(r, "rng_spawn_index"), CultureInfo.InvariantCulture) >= 0));
        // the recorded fields reproduce the assignment: movement_mode == Predict(Master Seed, Part 3, rng_spawn_index, 0.5)
        foreach (var r in lt.Rows) Assert.AreEqual(Predict(31415, long.Parse(lt.Get(r, "rng_spawn_index"), CultureInfo.InvariantCulture), 0.5f), lt.Get(r, "movement_mode"));
    }

    [UnityTest]
    public IEnumerator Rng_Manifest_AutomaticSeed_IsGeneratedOnceAndRecordedAsTheEffectiveSeed()
    {
        Rig rig = null;
        var o = MixRig(1f, 3, speed: 12f);
        o.compact = true; o.createDcm = true; o.masterSeed = 0;
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Get<int>(rig.spawner, "NpcSpawnedTotal") >= 3, 30f, "3 spawns");
        ulong effective = GetStatic<ulong>(T("ExperimentRngManager"), "EffectiveMasterSeed");
        Assert.AreNotEqual(0, effective);
        var m = ManifestOf(rig);
        var r2 = (JObject)m["rng"];
        Assert.AreEqual("0000000000000000", (string)r2["configured_master_seed"]); Assert.AreEqual(effective.ToString("x16"), (string)r2["effective_master_seed"]);
        Assert.AreEqual("auto_generated", (string)r2["seed_source"]); Assert.AreEqual("session_specific_auto_seed", (string)r2["seed_policy"]);
        Assert.IsNull(r2["legacy_overrides"]);
        var e = ((JArray)m["setting_entries"]).Cast<JObject>().First(x => (string)x["key"] == "experiment_rng_master_seed_effective");
        Assert.AreEqual(effective.ToString("x16"), (string)e["value"]); Assert.AreEqual("AUTO_GENERATED", (string)e["source"]);
        Assert.AreEqual(effective, GetStatic<ulong>(T("ExperimentRngManager"), "EffectiveMasterSeed"), "not regenerated by anything that ran in between");
    }

    // A full-width (upper 32 bits set) Master Seed reaches every NPC stream and the manifest unchanged, as 16 lowercase hex digits.
    [UnityTest]
    public IEnumerator Rng_Manifest_FullWidth64BitSeed_IsRecordedAsHex_AndDrivesTheNpcStreams()
    {
        const ulong master = 0xFEDCBA9876543210UL;
        Rig rig = null;
        var o = MixRig(0.5f, 6, speed: 12f);
        o.compact = true; o.createDcm = true; o.masterSeed = master;
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Get<int>(rig.spawner, "NpcSpawnedTotal") >= 12, 60f, "12 spawns");
        Set(rig.spawner, "maxAgents", 0);
        yield return WaitUntil(() => Live(rig) == 0, 30f, "drain");
        yield return new WaitForSecondsRealtime(0.3f);
        Assert.AreEqual(master, GetStatic<ulong>(T("ExperimentRngManager"), "EffectiveMasterSeed"));
        var m = ManifestOf(rig);
        var r2 = (JObject)m["rng"];
        Assert.AreEqual(3, (int)r2["rng_config_version"]); Assert.AreEqual("uint64_hex16", (string)r2["master_seed_representation"]);
        Assert.AreEqual(JTokenType.String, r2["effective_master_seed"].Type, "a 64-bit seed is never a JSON number");
        Assert.AreEqual("fedcba9876543210", (string)r2["configured_master_seed"]); Assert.AreEqual("fedcba9876543210", (string)r2["effective_master_seed"]);
        Assert.IsNull(r2["configured_seed_format"]); Assert.AreEqual("fixed", (string)r2["seed_source"]);
        foreach (var t in r2["part_stream_seeds"]) StringAssert.IsMatch("^(None|Part[1-3]):[A-Za-z]+=[0-9a-f]{16}$", (string)t);
        var entries = ((JArray)m["setting_entries"]).Cast<JObject>().ToList();
        Assert.AreEqual("fedcba9876543210", (string)entries.First(e => (string)e["key"] == "experiment_rng_master_seed_effective")["value"]);
        Assert.AreEqual("fedcba9876543210", (string)entries.First(e => (string)e["key"] == "experiment_rng_master_seed_configured")["value"]);
        var lt = Lifetimes(rig);
        Assert.Greater(lt.Rows.Count, 0);
        foreach (var r in lt.Rows) Assert.AreEqual(Predict(master, long.Parse(lt.Get(r, "rng_spawn_index"), CultureInfo.InvariantCulture), 0.5f), lt.Get(r, "movement_mode"), "movement mode re-derived from the full 64-bit seed");
    }
}
