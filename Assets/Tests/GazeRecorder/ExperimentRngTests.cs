// ExperimentRngTests.cs
// Tests for the experiment RNG (ExperimentRng.cs, ExperimentRngManager.cs, session_config.json experimentRngMasterSeed): PCG32 and seed-derivation test vectors, stream independence,
// value semantics, weighted selection, Master Seed policy, configuration compatibility and the "no uncontrolled randomness" source scan. The production types live in
// Assembly-CSharp and are reached by reflection (TestSupport.Reflect), like the other tests here. Editor tests (synthetic), not Quest tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using static Reflect;

public class ExperimentRngTests
{
    static readonly Type PcgType = T("Pcg32");
    static readonly Type SeedType = T("SeedDerivation");
    static readonly Type MgrType = T("ExperimentRngManager");
    static readonly Type PartType = T("RngPart");
    static readonly Type StreamType = T("RngStream");
    static readonly Type SetType = T("NpcRngSet");
    static readonly Type CountersType = T("ExperimentRngCounters");

    [SetUp] public void SetUp() { CallStatic(MgrType, "ResetForTests"); }
    [TearDown] public void TearDown() { CallStatic(MgrType, "ResetForTests"); }

    static object Part(string n) => Enum.Parse(PartType, n);
    static object Stream(string n) => Enum.Parse(StreamType, n);
    static object NewStream(ulong root, string part, string stream, long index) => Activator.CreateInstance(PcgType, root, Part(part), Stream(stream), index);
    static uint U(object s) => (uint)Call(s, "NextUInt");
    static float F(object s) => (float)Call(s, "NextFloat");
    static List<uint> Draw(object s, int n) { var l = new List<uint>(); for (int i = 0; i < n; i++) l.Add(U(s)); return l; }
    static object Fix(ulong seed) { CallStatic(MgrType, "ResetForTests"); RngSeams.SetConfig(seed); return null; }
    static ulong Effective => GetStatic<ulong>(MgrType, "EffectiveMasterSeed");
    static ulong StreamSeed(ulong root, string part, string stream, long index) => (ulong)CallStatic(SeedType, "StreamSeed", root, Part(part), Stream(stream), index);

    // ------------------------------------------------------------------------------------------------------------------------------ algorithm vectors

    [Test]
    public void Pcg32_MatchesTheReferenceOutput_ForTheDocumentedSeed()
    {
        object g = Activator.CreateInstance(PcgType, 42UL, 54UL);
        CollectionAssert.AreEqual(new uint[] { 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e }, Draw(g, 6), "pcg32 reference (initstate 42, initseq 54)");
    }

    [Test]
    public void SeedDerivation_AndDerivedStreams_MatchTheFrozenCrossRuntimeVectors()
    {
        Assert.AreEqual(0xe220a8397b1dcdafUL, (ulong)CallStatic(SeedType, "Mix", 0UL), "SplitMix64(0)");
        Assert.AreEqual(0x3416d3f507fd82e3UL, StreamSeed(12345, "Part3", "MovementAssignment", 7));
        CollectionAssert.AreEqual(new uint[] { 0xb7e72b1d, 0x5df2b1fb, 0x286ca041, 0x20cbe59b }, Draw(NewStream(12345, "Part3", "MovementAssignment", 7), 4));
        // Part-level stream (index -1) and a negative Master Seed
        Assert.AreEqual(0xebca185bde3bc825UL, StreamSeed(0xFFF0EDFAUL, "Part2", "PartSetup", -1));
        CollectionAssert.AreEqual(new uint[] { 0x6f6fa57b, 0x47e9819d, 0xdb773bc4, 0x8af435a2 }, Draw(NewStream(0xFFF0EDFAUL, "Part2", "PartSetup", -1), 4));
        // the same vectors are produced by docs/tools/experiment_rng.py
        Assert.AreEqual(RngAlgorithmVersion(), 1); Assert.AreEqual(GetStaticConst("SeedDerivationVersion"), 1);
    }

    static int RngAlgorithmVersion() => GetStaticConst("AlgorithmVersion");
    static int GetStaticConst(string name) => (int)T("RngConstants").GetField(name).GetRawConstantValue();

    // ------------------------------------------------------------------------------------------------------------------------------ determinism and independence

    [Test]
    public void SameMasterSeed_GivesIdenticalDerivedSeeds_AndIdenticalSequences()
    {
        foreach (string stream in Enum.GetNames(StreamType))
            foreach (string part in new[] { "None", "Part1", "Part2", "Part3" })
                foreach (long idx in new long[] { -1, 0, 1, 99 })
                {
                    Assert.AreEqual(StreamSeed(777, part, stream, idx), StreamSeed(777, part, stream, idx));
                    CollectionAssert.AreEqual(Draw(NewStream(777, part, stream, idx), 8), Draw(NewStream(777, part, stream, idx), 8), part + "/" + stream + "/" + idx);
                }
    }

    [Test]
    public void EveryInputChangesTheStream_AcrossStreamsPartsIndicesAndSeeds()
    {
        var seen = new HashSet<ulong>();
        int count = 0;
        foreach (ulong root in new[] { 1UL, 2UL, 0xFFFFFFFFUL })
            foreach (string part in new[] { "None", "Part1", "Part2", "Part3" })
                foreach (string stream in Enum.GetNames(StreamType))
                    foreach (long idx in new long[] { -1, 0, 1, 2, 1000 })
                    { seen.Add(StreamSeed(root, part, stream, idx)); count++; }
        Assert.AreEqual(count, seen.Count, "no two (seed, Part, stream, index) share a stream seed");
        // sequences look unrelated: two streams of the same seed agree on ~0 of 1000 values
        var a = Draw(NewStream(5, "Part3", "Appearance", 3), 1000); var b = Draw(NewStream(5, "Part3", "Destination", 3), 1000);
        Assert.Less(a.Zip(b, (x, y) => x == y ? 1 : 0).Sum(), 3);
    }

    [Test]
    public void DrawingFromOneStream_NeverShiftsAnother()
    {
        object ref1 = NewStream(31, "Part3", "Destination", 4);
        var expected = Draw(ref1, 20);
        object other = NewStream(31, "Part3", "Uncertainty", 4), dest = NewStream(31, "Part3", "Destination", 4);
        for (int i = 0; i < 500000; i++) U(other);       // a busy stream (per-frame hesitation draws)
        CollectionAssert.AreEqual(expected, Draw(dest, 20), "Destination is independent of how much Uncertainty drew");
    }

    [Test]
    public void DifferentParts_HaveIndependentStreams_AndOneNpcNeverShiftsAnother()
    {
        CollectionAssert.AreNotEqual(Draw(NewStream(9, "Part2", "Wandering", -1), 10), Draw(NewStream(9, "Part3", "Wandering", -1), 10));
        Fix(2468);
        object npcA = CallStatic(MgrType, "CreateNpcRng", Part("Part3"), 10L), npcB = CallStatic(MgrType, "CreateNpcRng", Part("Part3"), 11L);
        var b1 = Draw(Call(npcB, "Get", Stream("Uncertainty")), 12);
        Fix(2468);
        object a2 = CallStatic(MgrType, "CreateNpcRng", Part("Part3"), 10L), b2 = CallStatic(MgrType, "CreateNpcRng", Part("Part3"), 11L);
        for (int i = 0; i < 100000; i++) { U(Call(a2, "Get", Stream("Uncertainty"))); U(Call(a2, "Get", Stream("Appearance"))); }   // NPC 10 draws a lot, even before NPC 11 draws
        CollectionAssert.AreEqual(b1, Draw(Call(b2, "Get", Stream("Uncertainty")), 12), "NPC 11's sequence is the same whatever NPC 10 consumed");
        Assert.IsNotNull(npcA);
    }

    [Test]
    public void NpcStreams_AreReconstructableFromMasterSeedPartAndSpawnIndex()
    {
        Fix(4711);
        object set = CallStatic(MgrType, "CreateNpcRng", Part("Part2"), 5L);
        var viaManager = Draw(Call(set, "Get", Stream("Appearance")), 6);
        CollectionAssert.AreEqual(Draw(NewStream(4711, "Part2", "Appearance", 5), 6), viaManager, "stream seed = StreamSeed(MasterSeed, Part, stream, spawnIndex)");
        Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "CreateNpcRng", Part("Part3"), -3L), "a negative spawn index is invalid");
    }

    // ------------------------------------------------------------------------------------------------------------------------------ value semantics

    [Test]
    public void IntegerRange_HasExclusiveMax_IsUniformAndUnbiased_AndDegenerateRangesReturnMin()
    {
        object g = NewStream(1, "None", "PartSetup", -1);
        var counts = new int[7]; const int n = 70000;
        MethodInfoCache(out var rangeInt, out var rangeFloat);
        for (int i = 0; i < n; i++) { int v = (int)rangeInt.Invoke(g, new object[] { -2, 5 }); Assert.That(v, Is.InRange(-2, 4), "max is exclusive"); counts[v + 2]++; }
        for (int k = 0; k < 7; k++) Assert.AreEqual(n / 7.0, counts[k], 5 * Math.Sqrt(n / 7.0 * 6 / 7.0), "value " + (k - 2));
        Assert.AreEqual(3, (int)rangeInt.Invoke(g, new object[] { 3, 3 })); Assert.AreEqual(3, (int)rangeInt.Invoke(g, new object[] { 3, 1 }));
        // a span that is not a power of two (rejection path) stays unbiased
        var big = new int[3]; for (int i = 0; i < 30000; i++) big[(int)rangeInt.Invoke(g, new object[] { 0, 3 })]++;
        for (int k = 0; k < 3; k++) Assert.AreEqual(10000, big[k], 5 * Math.Sqrt(30000 / 3.0 * 2 / 3.0));
    }

    [Test]
    public void FloatRange_IsInclusive_UniformAndOrdersSwappedBounds()
    {
        object g = NewStream(2, "None", "PartSetup", -1);
        MethodInfoCache(out var rangeInt, out var rangeFloat);
        double sum = 0; float lo = float.MaxValue, hi = float.MinValue; const int n = 50000;
        for (int i = 0; i < n; i++) { float v = (float)rangeFloat.Invoke(g, new object[] { 2f, 4f }); sum += v; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        Assert.GreaterOrEqual(lo, 2f); Assert.LessOrEqual(hi, 4f);
        Assert.AreEqual(3.0, sum / n, 0.02, "uniform mean");
        Assert.Less(lo, 2.01f); Assert.Greater(hi, 3.99f);
        float s = (float)rangeFloat.Invoke(g, new object[] { 9f, 5f }); Assert.That(s, Is.InRange(5f, 9f), "swapped bounds are ordered");
        Assert.AreEqual(7f, (float)rangeFloat.Invoke(g, new object[] { 7f, 7f }));
    }

    static void MethodInfoCache(out System.Reflection.MethodInfo rangeInt, out System.Reflection.MethodInfo rangeFloat)
    {
        rangeInt = PcgType.GetMethod("Range", new[] { typeof(int), typeof(int) });
        rangeFloat = PcgType.GetMethod("Range", new[] { typeof(float), typeof(float) });
    }

    [Test]
    public void NextFloat_IsInHalfOpenUnitInterval_AndChanceIsExactAtTheBoundaries()
    {
        object g = NewStream(3, "None", "PartSetup", -1);
        for (int i = 0; i < 200000; i++) { float v = F(g); Assert.That(v, Is.GreaterThanOrEqualTo(0f).And.LessThan(1f)); }
        for (int i = 0; i < 5000; i++) { Assert.IsFalse((bool)Call(g, "Chance", 0f)); Assert.IsTrue((bool)Call(g, "Chance", 1f)); }
        int hits = 0; for (int i = 0; i < 40000; i++) if ((bool)Call(g, "Chance", 0.3f)) hits++;
        Assert.AreEqual(12000, hits, 5 * Math.Sqrt(40000 * 0.3 * 0.7));
    }

    [Test]
    public void UnitCircleAndSphere_StayInsideTheirShapes_WithUniformArea()
    {
        object g = NewStream(4, "None", "NavigationNudge", -1);
        var ci = PcgType.GetMethod("InsideUnitCircle"); var sp = PcgType.GetMethod("InsideUnitSphere");
        int inner = 0; const int n = 40000;
        for (int i = 0; i < n; i++)
        {
            var a = new object[] { 0f, 0f }; ci.Invoke(g, a); float x = (float)a[0], y = (float)a[1];
            Assert.LessOrEqual(x * x + y * y, 1.0001f);
            if (x * x + y * y < 0.25f) inner++;          // area share of the radius-0.5 disc = 0.25
            var b = new object[] { 0f, 0f, 0f }; sp.Invoke(g, b); Assert.LessOrEqual((float)b[0] * (float)b[0] + (float)b[1] * (float)b[1] + (float)b[2] * (float)b[2], 1.0001f);
        }
        Assert.AreEqual(n * 0.25, inner, 5 * Math.Sqrt(n * 0.25 * 0.75));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ weighted selection

    static int Weighted(object g, float[] w) => (int)PcgType.GetMethod("ChooseWeighted").Invoke(g, new object[] { w.Length, (Func<int, float>)(i => w[i]) });

    [Test]
    public void WeightedChoice_FollowsTheWeights_NeverPicksZeroWeight_AndTakesOneDraw()
    {
        object g = NewStream(6, "None", "Appearance", -1);
        var w = new[] { 0f, 1f, 0f, 3f, 0f, -2f, float.NaN };
        var counts = new int[w.Length]; const int n = 80000;
        for (int i = 0; i < n; i++) counts[Weighted(g, w)]++;
        foreach (int z in new[] { 0, 2, 4, 5, 6 }) Assert.AreEqual(0, counts[z], "option " + z + " has no positive weight");
        Assert.AreEqual(n * 0.25, counts[1], 5 * Math.Sqrt(n * 0.25 * 0.75)); Assert.AreEqual(n * 0.75, counts[3], 5 * Math.Sqrt(n * 0.25 * 0.75));
        Assert.AreEqual(-1, Weighted(g, new[] { 0f, 0f })); Assert.AreEqual(-1, Weighted(g, new float[0]));
        long before = Get<long>(g, "Draws"); Weighted(g, new[] { 1f, 1f, 1f }); Assert.AreEqual(before + 1, Get<long>(g, "Draws"), "exactly one draw per choice");
    }

    [Test]
    public void WeightedChoice_ExcludesZeroWeight_EvenWhenTheDrawnValueIsExactlyZero()
    {
        // Pcg32(initstate 7926344, initseq 54) has the first value 0x00000089, i.e. NextFloat() == 0 exactly (found offline): the case in which "r <= cumulative" would pick a
        // leading zero-weight option.
        object g = Activator.CreateInstance(PcgType, 7926344UL, 54UL);
        Assert.AreEqual(0f, F(Activator.CreateInstance(PcgType, 7926344UL, 54UL)), "the chosen stream really draws exactly 0 first");
        Assert.AreEqual(1, Weighted(g, new[] { 0f, 1f }), "a zero-weight first option is never selected");
        // the same property through the production AgentProfile selector
        var prof = ScriptableObject.CreateInstance(T("AgentProfile"));
        try
        {
            var opt = T("AgentProfile").GetNestedType("PartOption");
            Array arr = Array.CreateInstance(opt, 2);
            object o0 = Activator.CreateInstance(opt), o1 = Activator.CreateInstance(opt);
            Set(o0, "weight", 0f); Set(o1, "weight", 1f); arr.SetValue(o0, 0); arr.SetValue(o1, 1);
            object g2 = Activator.CreateInstance(PcgType, 7926344UL, 54UL);
            var choose = T("AgentProfile").GetMethod("ChooseWeightedOption", new[] { arr.GetType(), T("AgentGender"), PcgType });
            object male = Enum.Parse(T("AgentGender"), "Male");
            Assert.AreSame(o1, choose.Invoke(prof, new object[] { arr, male, g2 }));
        }
        finally { UnityEngine.Object.DestroyImmediate(prof); }
    }

    // ------------------------------------------------------------------------------------------------------------------------------ before / after the migration

    // Synthetic comparison of the distributions the project used to get from UnityEngine.Random with the new streams: same statistics, within sampling error. This shows the
    // *distributions* match for the call patterns the code uses (integer Range, float Range, weighted choice, probability test, insideUnitCircle); it says nothing about sequences.
    [Test]
    public void Migration_DistributionsMatchTheFormerUnityRandomUsage_WithinSamplingError()
    {
        const int n = 200000;
        UnityEngine.Random.InitState(20261110);
        object g = NewStream(8, "None", "Appearance", -1);
        MethodInfoCache(out var rangeInt, out var rangeFloat);
        var ci = PcgType.GetMethod("InsideUnitCircle");

        // 1. integer Range(0, 7): counts per value
        var cu = new int[7]; var cp = new int[7];
        for (int i = 0; i < n; i++) { cu[UnityEngine.Random.Range(0, 7)]++; cp[(int)rangeInt.Invoke(g, new object[] { 0, 7 })]++; }
        double sd = Math.Sqrt(n / 7.0 * 6 / 7.0);
        for (int k = 0; k < 7; k++) { Assert.AreEqual(n / 7.0, cu[k], 5 * sd); Assert.AreEqual(n / 7.0, cp[k], 5 * sd); Assert.AreEqual(cu[k], cp[k], 7 * sd * Math.Sqrt(2)); }

        // 2. float Range(0.5, 2): mean 1.25, sd (2-0.5)/sqrt(12)
        double mu = 0, mp = 0;
        for (int i = 0; i < n; i++) { mu += UnityEngine.Random.Range(0.5f, 2f); mp += (float)rangeFloat.Invoke(g, new object[] { 0.5f, 2f }); }
        double se = 1.5 / Math.Sqrt(12.0) / Math.Sqrt(n);
        Assert.AreEqual(1.25, mu / n, 5 * se); Assert.AreEqual(1.25, mp / n, 5 * se);

        // 3. weighted choice 0.25 / 0.5 / 0.25 with the former algorithm (Random.value * total, r <= cumulative) and the new one
        var w = new[] { 0.25f, 0.5f, 0.25f }; var wu = new int[3]; var wp = new int[3];
        for (int i = 0; i < n; i++)
        {
            float r = UnityEngine.Random.value * 1f, acc = 0f; int pick = 2;
            for (int k = 0; k < 3; k++) { acc += w[k]; if (r <= acc) { pick = k; break; } }
            wu[pick]++; wp[Weighted(g, w)]++;
        }
        for (int k = 0; k < 3; k++) { double sdw = Math.Sqrt(n * w[k] * (1 - w[k])); Assert.AreEqual(n * w[k], wu[k], 5 * sdw); Assert.AreEqual(n * w[k], wp[k], 5 * sdw); }

        // 4. probability test p = 0.2 (uncertain roll / gender)
        int hu = 0, hp = 0;
        for (int i = 0; i < n; i++) { if (UnityEngine.Random.value < 0.2f) hu++; if ((bool)Call(g, "Chance", 0.2f)) hp++; }
        double sdb = Math.Sqrt(n * 0.2 * 0.8); Assert.AreEqual(n * 0.2, hu, 5 * sdb); Assert.AreEqual(n * 0.2, hp, 5 * sdb);

        // 5. insideUnitCircle: E[r^2] = 1/2 for a uniform disc
        double qu = 0, qp = 0;
        for (int i = 0; i < n; i++)
        {
            Vector2 v = UnityEngine.Random.insideUnitCircle; qu += v.sqrMagnitude;
            var a = new object[] { 0f, 0f }; ci.Invoke(g, a); qp += (float)a[0] * (float)a[0] + (float)a[1] * (float)a[1];
        }
        double seq = Math.Sqrt(1.0 / 12.0) / Math.Sqrt(n);   // Var(r^2) = 1/3 - 1/4 = 1/12
        Assert.AreEqual(0.5, qu / n, 5 * seq); Assert.AreEqual(0.5, qp / n, 5 * seq);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ Master Seed policy

    [Test]
    public void FixedMasterSeed_IsUsedExactly_AndIsSharedAcrossParticipants()
    {
        Fix(20261110);
        Assert.AreEqual(20261110, Effective);
        Assert.AreEqual(20261110UL, GetStatic<ulong>(MgrType, "ConfiguredMasterSeed"));
        Assert.AreEqual("fixed", GetStatic<string>(MgrType, "SeedSource"));
        Assert.AreEqual("shared_fixed_seed", GetStatic<string>(MgrType, "SeedPolicy"));
        var first = Draw(CallStatic(MgrType, "GetPartStream", Part("Part3"), Stream("PartSetup")), 5);
        // a "new application initialisation" (and a different participant) with the same fixed seed: identical derived seeds and sequences
        Fix(20261110);
        Assert.AreEqual(20261110, Effective);
        CollectionAssert.AreEqual(first, Draw(CallStatic(MgrType, "GetPartStream", Part("Part3"), Stream("PartSetup")), 5));
        Assert.AreEqual(StreamSeed(20261110, "Part3", "PartSetup", -1), Get<ulong>(CallStatic(MgrType, "GetPartStream", Part("Part3"), Stream("PartSetup")), "StreamSeed"));
    }

    [Test]
    public void ZeroMasterSeed_GeneratesOneNonZeroSeedPerSession_AndNeverRegenerates()
    {
        Fix(0);
        ulong e = Effective;
        Assert.AreNotEqual(0, e);
        Assert.AreEqual("auto_generated", GetStatic<string>(MgrType, "SeedSource"));
        Assert.AreEqual("session_specific_auto_seed", GetStatic<string>(MgrType, "SeedPolicy"));
        Assert.AreEqual(0UL, GetStatic<ulong>(MgrType, "ConfiguredMasterSeed"), "the configured input is kept next to the effective seed");
        for (int i = 0; i < 20; i++) { CallStatic(MgrType, "GetPartStream", Part("Part2"), Stream("PartSetup")); CallStatic(MgrType, "CreateNpcRng", Part("Part3"), (long)i); }
        Assert.AreEqual(e, Effective, "the seed is generated exactly once and not regenerated when Parts / NPCs request streams");
        Fix(0);
        Assert.AreNotEqual(e, Effective, "a new session (reset) generates a new seed");
    }

    static void AssertEveryRngRequestFails()
    {
        Assert.Throws<InvalidOperationException>(() => CallStatic0("EnsureInitialized"));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "GetPartStream", Part("Part2"), Stream("SpawnPlacement")));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "CreateNpcRng", Part("Part3"), 0L));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => GetStatic<object>(MgrType, "UnassignedNpcRng"));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => GetStatic<ulong>(MgrType, "EffectiveMasterSeed"));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "CreateTargetSelectionStream", RngStreamValue("Target1Building"), "P001", 0, "GE", 0UL));
    }
    static void CallStatic0(string name) { try { MgrType.GetMethod(name).Invoke(null, null); } catch (System.Reflection.TargetInvocationException e) { throw e.InnerException; } }

    [Test]
    public void Initialisation_HappensOnFirstUse_BeforeAnyDraw()
    {
        Assert.IsFalse(GetStatic<bool>(MgrType, "IsInitialized"));
        Fix(55);
        Assert.AreEqual(55, Effective);
        Assert.IsTrue(GetStatic<bool>(MgrType, "IsInitialized"));
        Assert.AreEqual("valid", GetStatic<string>(MgrType, "ConfigurationValidation"));
    }

    // No RNG stream exists before the Master Seed is resolved and frozen; an unresolvable configuration is an explicit failure, never a guessed or automatic seed.
    [Test]
    public void UnresolvableConfiguration_FailsExplicitly_NoStreamAndNoAutomaticFallback()
    {
        CallStatic(MgrType, "ResetForTests");
        RngSeams.SetConfigThrowing(new InvalidOperationException("the external session configuration is invalid"));
        UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new Regex("could not be resolved"));
        AssertEveryRngRequestFails();
        Assert.AreEqual("Failed", GetStatic<object>(MgrType, "State").ToString());
        StringAssert.Contains("external session configuration is invalid", GetStatic<string>(MgrType, "FailureReason"));
        StringAssert.StartsWith("failed:", GetStatic<string>(MgrType, "ConfigurationValidation"));
    }

    [Test]
    public void EntropyFailure_PreventsInitialisation_AndNoClockFallbackExists()
    {
        Fix(0);
        RngSeams.SetEntropy(b => throw new System.Security.Cryptography.CryptographicException("no entropy"));
        UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new Regex("entropy source failed"));
        AssertEveryRngRequestFails();
        StringAssert.Contains("entropy", GetStatic<string>(MgrType, "FailureReason"));
        string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "ExperimentRngManager.cs"));
        foreach (string forbidden in new[] { "TickCount", "GetInstanceID", "DateTime.Now", "DateTime.UtcNow", "UnityEngine.Random.", "Stopwatch" })
            StringAssert.DoesNotContain(forbidden, string.Join("\n", source.Split('\n').Where(l => !l.TrimStart().StartsWith("//"))), "no clock / instance-id fallback for the automatic seed");
    }

    [Test]
    public void FixedSeed_NeverTouchesTheEntropySource_ExplicitZeroUsesItExactlyOnce()
    {
        int calls = 0;
        Fix(123);
        RngSeams.SetEntropy(b => { calls++; });
        Assert.AreEqual(123, Effective); Assert.AreEqual(0, calls, "a fixed seed needs no entropy");
        Assert.AreEqual("not_used_fixed_seed", GetStatic<string>(MgrType, "EntropySource"));
        Fix(0);
        RngSeams.SetEntropy(b => { calls++; b[0] = 7; });   // 8 bytes, little-endian: 0x0000000000000007
        Assert.AreEqual(7, Effective); Assert.AreEqual(1, calls);
        for (int i = 0; i < 10; i++) { CallStatic(MgrType, "GetPartStream", Part("Part2"), Stream("Wandering")); CallStatic(MgrType, "CreateNpcRng", Part("Part3"), (long)i); }
        Assert.AreEqual(7, Effective); Assert.AreEqual(1, calls, "generated exactly once, never again (Part start, NPC spawns)");
        StringAssert.StartsWith("os_csprng", GetStatic<string>(MgrType, "EntropySource"));
    }

    [Test]
    public void MasterSeed_CannotChangeAfterInitialisation()
    {
        Fix(5);
        Assert.AreEqual(5, Effective);
        var firstDraws = Draw(CallStatic(MgrType, "GetPartStream", Part("Part2"), Stream("Wandering")), 3);
        RngSeams.SetConfig(9);   // a later configuration change
        CallStatic(MgrType, "EnsureInitialized");
        Assert.AreEqual(5, Effective, "frozen: later configuration edits are ignored");
        Assert.AreEqual(5UL, GetStatic<ulong>(MgrType, "ConfiguredMasterSeed"));
        Assert.AreEqual(StreamSeed(5, "Part2", "Wandering", -1), Get<ulong>(CallStatic(MgrType, "GetPartStream", Part("Part2"), Stream("Wandering")), "StreamSeed"));
        Assert.IsNotNull(firstDraws);
    }

    [Test]
    public void InvalidPartOrStreamRequests_AreRejectedExplicitly()
    {
        Fix(1);
        Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "GetPartStream", Enum.ToObject(PartType, 9), Stream("PartSetup")));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "GetPartStream", Part("Part1"), Enum.ToObject(StreamType, 0)));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "GetPartStream", Part("Part1"), Enum.ToObject(StreamType, 99)));
        foreach (string sel in new[] { "Target1Building", "Target2Building" })
        {
            Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "GetPartStream", Part("Part3"), Stream(sel)), sel + " is a participant-level selection stream");
            Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "CreateTargetSelectionStream", Stream(sel), "", 3, "GE", 0UL), "participant ID is required");
            Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "CreateTargetSelectionStream", Stream(sel), "P001", -1, "GE", 0UL));
            Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "CreateTargetSelectionStream", Stream(sel), "P001", 3, " ", 0UL));
        }
        Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrType, "CreateTargetSelectionStream", Stream("Appearance"), "P001", 3, "GE", 0UL), "only the two selection streams may be requested");
        object npcSet = CallStatic(MgrType, "CreateNpcRng", Part("Part3"), 0L);
        Assert.Throws<System.Reflection.TargetInvocationException>(() => Call(npcSet, "Get", Stream("Target2Building")), "a selection stream is not an NPC stream");
        object set = CallStatic(MgrType, "CreateNpcRng", Part("Part3"), 0L);
        Assert.Throws<System.Reflection.TargetInvocationException>(() => Call(set, "Get", Enum.ToObject(StreamType, 42)));
    }

    [Test]
    public void StreamInventory_IsCompleteUniqueAndStable()
    {
        var values = Enum.GetValues(StreamType).Cast<object>().Select(v => (int)v).ToList();
        CollectionAssert.AllItemsAreUnique(values, "no duplicate stream identifier");
        Assert.AreEqual(Enumerable.Range(1, 12).ToList(), values.OrderBy(v => v).ToList(), "the numeric values 1..12 are part of the derivation and must not change (append only)");
        Fix(1);
        var inventory = (string[])CallStatic(MgrType, "StreamInventory");
        CollectionAssert.AreEquivalent(Enum.GetNames(StreamType), inventory);
        Assert.AreEqual(values.Max() + 1, GetStaticConst("StreamSlots"));
    }

    [Test]
    public void DrawCounters_CountPerPartAndStream_AndAppearInTheSummary()
    {
        Fix(5);
        object s = CallStatic(MgrType, "GetPartStream", Part("Part2"), Stream("PartSetup"));
        for (int i = 0; i < 7; i++) U(s);
        Assert.AreEqual(7L, (long)CallStatic(CountersType, "Get", Part("Part2"), Stream("PartSetup")));
        CollectionAssert.Contains((string[])CallStatic(MgrType, "DrawCountSummary"), "Part2:PartSetup=7");
        var seeds = (string[])CallStatic(MgrType, "PartStreamSeeds");
        CollectionAssert.Contains(seeds, "Part2:PartSetup=" + StreamSeed(5, "Part2", "PartSetup", -1).ToString("x16"));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ participant-specific Part 3 target building

    static readonly Type SelType = T("Part3TargetSelection");
    // dest: 1 = Destination 1 (stream Target1Building), 2 = Destination 2 (stream Target2Building)
    static object SelectDest(int dest, string pid, int group, string zone, params string[] ids)
    {
        var args = new object[] { dest, pid, group, zone, ids, null, null };
        bool ok = (bool)SelType.GetMethod("TrySelect").Invoke(null, args);
        return ok ? args[5] : args[6];   // Result or error text
    }
    static object Select(string pid, int group, string zone, params string[] ids) => SelectDest(1, pid, group, zone, ids);
    static bool Selected(object r) => !(r is string);
    static string SelId(object r) => Get<string>(r, "SelectedId");

    static ulong TSeed(ulong master, string stream, string pid, int group, string zone) => (ulong)CallStatic(SeedType, "TargetSelectionSeed", master, Part("Part3"), Stream(stream), pid, group, zone);

    // PURE selection tests (no scene, no XR): the derivation, the draw and its properties. docs/tools/test_experiment_rng.py asserts the same frozen values for the Python reference.
    [Test]
    public void TargetSelectionSeed_MatchesTheFrozenVectors_AndPythonReference()
    {
        Assert.AreEqual(0x5ffc4b9c13046ddeUL, (ulong)CallStatic(SeedType, "HashUtf8", "P001"));
        Assert.AreEqual(0x35d409ef71e2213fUL, TSeed(31415UL, "Target1Building", "P001", 4, "OE"));
        Assert.AreEqual(0x59a822d807652adcUL, TSeed(31415UL, "Target2Building", "P001", 4, "GU"));
        Assert.AreEqual(0x5d7492f2f69f46ecUL, TSeed(31415UL, "Target1Building", "P002", 4, "OE"));
        Assert.AreEqual(0xce47a73862d480c2UL, TSeed(31415UL, "Target2Building", "P002", 4, "GU"));
        Assert.AreEqual(0xfd3479745b31e27dUL, TSeed(0xFEDCBA9876543210UL, "Target1Building", "P001", 4, "OE"));
        Assert.AreEqual(0x86781eb11b77f237UL, TSeed(0xFEDCBA9876543210UL, "Target2Building", "P001", 4, "GU"));
        // the chosen landmarks of the frozen cases (the Python reference computes the same); each destination has its own stream
        Fix(31415);
        Assert.AreEqual("OE_B", SelId(SelectDest(1, "P001", 4, "OE", "OE_B", "OE_A")));
        Assert.AreEqual("GU_B", SelId(SelectDest(2, "P001", 4, "GU", "GU_B", "GU_A")));
        Assert.AreEqual("OE_B", SelId(SelectDest(1, "P002", 4, "OE", "OE_A", "OE_B")));
        Assert.AreEqual("GU_A", SelId(SelectDest(2, "P002", 4, "GU", "GU_A", "GU_B")));
        Assert.AreEqual("35d409ef71e2213f", Get<string>(SelectDest(1, "P001", 4, "OE", "OE_A", "OE_B"), "DerivedSeedHex"));
        Assert.AreEqual("59a822d807652adc", Get<string>(SelectDest(2, "P001", 4, "GU", "GU_A", "GU_B"), "DerivedSeedHex"));
        Fix(0xFEDCBA9876543210UL);
        Assert.AreEqual("OU_A", SelId(SelectDest(1, "P001", 2, "OU", "OU_B", "OU_A")));
        Assert.AreEqual("GE_B", SelId(SelectDest(2, "P001", 2, "GE", "GE_B", "GE_A")));
    }

    [Test]
    public void TargetSelection_TheTwoDestinationsHaveIndependentStreams_NeverSharingASeedOrAGenerator()
    {
        foreach (ulong master in new[] { 1UL, 31415UL, 0x8000000000000001UL })
            foreach (string pid in new[] { "P001", "P002", "P777" })
                Assert.AreNotEqual(TSeed(master, "Target1Building", pid, 4, "OE"), TSeed(master, "Target2Building", pid, 4, "OE"), "same participant, group and zone: the stream id alone separates the destinations");
        // a stream's seed depends on its OWN zone only: changing the other destination's zone cannot change it
        Assert.AreEqual(TSeed(5UL, "Target1Building", "P001", 1, "GU"), TSeed(5UL, "Target1Building", "P001", 1, "GU"));
        var npc = new HashSet<ulong>();
        foreach (string st in Enum.GetNames(StreamType).Where(n => n != "Target1Building" && n != "Target2Building")) for (long i = -1; i < 50; i++) npc.Add(StreamSeed(5UL, "Part3", st, i));
        CollectionAssert.DoesNotContain(npc, TSeed(5UL, "Target1Building", "P001", 1, "GU"));
        CollectionAssert.DoesNotContain(npc, TSeed(5UL, "Target2Building", "P001", 1, "GU"));
        // two draws of the same destination are the same sequence; the two destinations are different generator OBJECTS with different sequences
        Fix(2468);
        var a1 = new object[] { RngStreamValue("Target1Building"), "P001", 4, "OE", 0UL }; var a2 = new object[] { RngStreamValue("Target2Building"), "P001", 4, "OE", 0UL };
        object s1 = MgrType.GetMethod("CreateTargetSelectionStream").Invoke(null, a1), s2 = MgrType.GetMethod("CreateTargetSelectionStream").Invoke(null, a2), s1b = MgrType.GetMethod("CreateTargetSelectionStream").Invoke(null, new object[] { RngStreamValue("Target1Building"), "P001", 4, "OE", 0UL });
        Assert.AreNotSame(s1, s2); Assert.AreNotSame(s1, s1b);
        Assert.AreNotEqual((ulong)a1[4], (ulong)a2[4]);
        CollectionAssert.AreEqual(Draw(s1, 8), Draw(s1b, 8), "same inputs: same sequence");
        // advancing the Destination 1 stream cannot move the Destination 2 stream
        object t2a = MgrType.GetMethod("CreateTargetSelectionStream").Invoke(null, new object[] { RngStreamValue("Target2Building"), "P001", 4, "GU", 0UL });
        var expected = Draw(MgrType.GetMethod("CreateTargetSelectionStream").Invoke(null, new object[] { RngStreamValue("Target2Building"), "P001", 4, "GU", 0UL }), 8);
        object t1 = MgrType.GetMethod("CreateTargetSelectionStream").Invoke(null, new object[] { RngStreamValue("Target1Building"), "P001", 4, "OE", 0UL });
        for (int i = 0; i < 10000; i++) U(t1);
        CollectionAssert.AreEqual(expected, Draw(t2a, 8), "Target2Building is independent of how much Target1Building drew");
    }
    static object RngStreamValue(string n) => Enum.Parse(StreamType, n);

    [Test]
    public void TargetSelection_ForEveryTable5Group_EachTargetBelongsToItsOwnZone_AndTheTwoIdsDiffer()
    {
        var table = new (string start, string d1, string d2)[] { ("GE","GU","OE"), ("GE","OE","GU"), ("GE","OU","GE"), ("GU","GE","OU"), ("GU","OE","GU"), ("GU","OU","GE"),
                                                                  ("OE","GE","OU"), ("OE","GU","OE"), ("OE","OU","GE"), ("OU","GE","OU"), ("OU","GU","OE"), ("OU","OE","GU") };
        Func<string, string[]> landmarks = z => new[] { z + "_A", z + "_B" };
        Fix(0x0123456789ABCDEFUL);
        for (int g = 0; g < 12; g++)
        {
            Assert.AreNotEqual(table[g].d1, table[g].d2, "group " + g + ": Destination 1 and 2 are different zones (including groups 2, 4, 7, 9)");
            var seenPairs = new HashSet<string>();
            for (int p = 1; p <= 200; p++)
            {
                string pid = "P" + p.ToString("D3");
                object r1 = SelectDest(1, pid, g, table[g].d1, landmarks(table[g].d1)), r2 = SelectDest(2, pid, g, table[g].d2, landmarks(table[g].d2));
                Assert.IsTrue(Selected(r1) && Selected(r2));
                Assert.AreEqual(2, Get<string[]>(r1, "CandidateIds").Length); Assert.AreEqual(2, Get<string[]>(r2, "CandidateIds").Length, "exactly two eligible landmarks per zone");
                StringAssert.StartsWith(table[g].d1 + "_", SelId(r1)); StringAssert.StartsWith(table[g].d2 + "_", SelId(r2));
                CollectionAssert.Contains(landmarks(table[g].d1), SelId(r1)); CollectionAssert.Contains(landmarks(table[g].d2), SelId(r2));
                Assert.AreNotEqual(SelId(r1), SelId(r2), "Target 1 and Target 2 are different buildings");
                Assert.AreEqual(1, Get<int>(r1, "Destination")); Assert.AreEqual(2, Get<int>(r2, "Destination"));
                seenPairs.Add(SelId(r1) + "|" + SelId(r2));
            }
            Assert.AreEqual(4, seenPairs.Count, "group " + g + ": all four (Target 1, Target 2) pairs occur across participants (independent draws; no quota, no no-replacement)");
        }
    }

    [Test]
    public void TargetSelection_EachDestinationIsUniformOverItsTwoLandmarks_Independently()
    {
        Fix(777);
        foreach (int dest in new[] { 1, 2 })
        {
            int a = 0;
            for (int p = 0; p < 2000; p++) if (SelId(SelectDest(dest, "P" + p.ToString("D4"), 4, dest == 1 ? "OE" : "GU", "x_A", "x_B")) == "x_A") a++;
            Assert.AreEqual(1000.0, a, 5 * Math.Sqrt(2000 * 0.25), "destination " + dest + ": both landmarks are chosen about equally often");
        }
        // the two destinations of one participant are independent: the joint distribution is uniform over the four pairs
        var joint = new Dictionary<string, int>();
        for (int p = 0; p < 4000; p++) { string k = SelId(SelectDest(1, "Q" + p, 4, "OE", "a", "b")) + SelId(SelectDest(2, "Q" + p, 4, "GU", "c", "d")); joint[k] = joint.TryGetValue(k, out int c) ? c + 1 : 1; }
        Assert.AreEqual(4, joint.Count);
        foreach (var kv in joint) Assert.AreEqual(1000.0, kv.Value, 5 * Math.Sqrt(4000 * 0.25 * 0.75), kv.Key);
    }

    [Test]
    public void TargetSelection_SameMasterSeedParticipantAndGroup_GiveTheSameBuildingPair_AndReplayWithTheRecordedSeedReproducesIt()
    {
        Fix(8675309);
        string first = SelId(Select("P017", 7, "OE", "OE_A", "OE_B"));
        for (int i = 0; i < 10; i++) Assert.AreEqual(first, SelId(Select("P017", 7, "OE", "OE_A", "OE_B")));
        ulong recorded = Effective;
        Fix(recorded);   // "replaying a session with its recorded effective Master Seed" (a fresh initialisation)
        Assert.AreEqual(first, SelId(Select("P017", 7, "OE", "OE_B", "OE_A")), "same participant, group and eligible set (given in another order)");
        // many participants: both buildings occur (uniform, independent), and any particular participant is reproducible
        var counts = new Dictionary<string, int>();
        for (int p = 1; p <= 400; p++) { string id = SelId(Select("P" + p.ToString("D3"), 7, "OE", "OE_A", "OE_B")); counts[id] = counts.TryGetValue(id, out int c) ? c + 1 : 1; }
        Assert.AreEqual(200.0, counts["OE_A"], 5 * Math.Sqrt(400 * 0.25), "uniform choice between the two buildings across participants");
    }

    [Test]
    public void TargetSelection_ParticipantsGroupsZonesAndStreamsHaveSeparatelyDerivedSeeds()
    {
        Fix(99);
        var seeds = new HashSet<string>();
        int n = 0;
        foreach (string pid in new[] { "P001", "P002", "P010", "P100" })
            foreach (int group in new[] { 0, 1, 7, 11 })
                foreach (string region in new[] { "GE", "GU", "OE", "OU" })
                { seeds.Add(Get<string>(Select(pid, group, region, "a", "b"), "DerivedSeedHex")); n++; }
        Assert.AreEqual(n, seeds.Count, "every (participant, group, region) has its own stream seed");
        string underSeed99 = Get<string>(Select("P001", 1, "GE", "a", "b"), "DerivedSeedHex");
        Fix(100);
        Assert.AreNotEqual(underSeed99, Get<string>(Select("P001", 1, "GE", "a", "b"), "DerivedSeedHex"), "a different Master Seed derives a different stream");
        // a participant ID is encoded exactly: case, whitespace-free text and prefixes are all distinct
        Fix(99);
        var ids = new[] { "P001", "p001", "P0010", "P01", "001", "P001 " };
        Assert.AreEqual(ids.Length, ids.Select(i => Get<string>(Select(i, 3, "GE", "a", "b"), "DerivedSeedHex")).Distinct().Count());
    }

    [Test]
    public void TargetSelection_CandidateOrderIsStable_BuildingAlwaysBelongsToTheZone_AndInvalidInputsFailExplicitly()
    {
        Fix(4242);
        var region = new[] { "R_zeta", "R_alpha", "R_mid", "R_Beta" };
        string expected = SelId(Select("P020", 5, "OU", region));
        foreach (var perm in new[] { new[] { 3, 2, 1, 0 }, new[] { 1, 3, 0, 2 }, new[] { 2, 0, 3, 1 } })
            Assert.AreEqual(expected, SelId(Select("P020", 5, "OU", perm.Select(i => region[i]).ToArray())), "input order never matters");
        CollectionAssert.AreEqual(new[] { "R_Beta", "R_alpha", "R_mid", "R_zeta" }, Get<string[]>(Select("P020", 5, "OU", region), "CandidateIds"), "ordinal sort (uppercase before lowercase)");
        for (int p = 0; p < 200; p++) CollectionAssert.Contains(region, SelId(Select("P" + p, 5, "OU", region)), "never a building outside the assigned region");
        Assert.AreEqual(Get<string>(Select("P020", 5, "OU", region), "CandidateFingerprint"), Get<string>(Select("P020", 5, "OU", region.Reverse().ToArray()), "CandidateFingerprint"), "the fingerprint is order independent");

        Assert.IsFalse(Selected(Select("P020", 5, "OU")), "empty candidate list");
        Assert.IsFalse(Selected(Select("P020", 5, "OU", "x", "x")), "duplicate ids");
        Assert.IsFalse(Selected(Select("P020", 5, "OU", "x", " ")), "unusable id");
        Assert.IsFalse(Selected(Select("", 5, "OU", "x", "y")), "missing participant ID: no shared-stream fallback");
        Assert.IsFalse(Selected(Select(null, 5, "OU", "x", "y")));
        Assert.IsFalse(Selected(Select("P020", -1, "OU", "x", "y")), "invalid group");
        Assert.IsFalse(Selected(Select("P020", 5, "", "x", "y")), "undefined region");
        StringAssert.Contains("participant ID", (string)Select("", 5, "OU", "x", "y"));
        Assert.AreEqual("only", SelId(Select("P020", 5, "OU", "only")), "a single eligible building is the selection");
    }

    // The target-building draw is independent of the NPC streams, and the NPC streams are common across participants.
    [Test]
    public void TargetSelection_DoesNotShiftAnyNpcStream_AndNpcStreamsDoNotDependOnParticipantOrGroup()
    {
        Fix(2026);
        object clean = CallStatic(MgrType, "CreateNpcRng", Part("Part3"), 3L);
        var cleanDraws = new Dictionary<string, List<uint>>();
        foreach (string st in new[] { "SpawnPlacement", "MovementAssignment", "MovementAttributes", "Appearance", "Destination", "Wandering", "AttractorDwell", "Uncertainty", "NavigationNudge" })
            cleanDraws[st] = Draw(Call(clean, "Get", Stream(st)), 10);

        Fix(2026);
        object a = CallStatic(MgrType, "CreateNpcRng", Part("Part3"), 3L);
        long npcBefore = Enum.GetNames(StreamType).Where(n => n != "Target1Building" && n != "Target2Building").Sum(n => (long)CallStatic(CountersType, "Total", Stream(n)));
        for (int p = 1; p <= 50; p++) Select("P" + p, p % 12, "GE", "b1", "b2");           // many participants' selections between creating and using the NPC streams
        long npcAfter = Enum.GetNames(StreamType).Where(n => n != "Target1Building" && n != "Target2Building").Sum(n => (long)CallStatic(CountersType, "Total", Stream(n)));
        Assert.AreEqual(npcBefore, npcAfter, "target-building draws are counted under their own stream only");
        Assert.AreEqual(50L, (long)CallStatic(CountersType, "Total", Stream("Target1Building")), "each draw is counted under its own selection stream");
        Assert.AreEqual(0L, (long)CallStatic(CountersType, "Total", Stream("Target2Building")));
        foreach (var kv in cleanDraws) CollectionAssert.AreEqual(kv.Value, Draw(Call(a, "Get", Stream(kv.Key)), 10), kv.Key + ": identical whatever target selections happened (and for any participant / group)");
        Assert.Throws<System.Reflection.TargetInvocationException>(() => Call(a, "Get", Stream("Target1Building")), "Target1Building is not an NPC stream");
        Assert.Throws<System.Reflection.TargetInvocationException>(() => Call(a, "Get", Stream("Target2Building")), "Target2Building is not an NPC stream");
    }

    // ------------------------------------------------------------------------------------------------------------------------------ session_config.json

    static readonly Type Core = T("SessionConfigCore");
    static object Limits() { object l = Activator.CreateInstance(Core.GetNestedType("Limits")); Set(l, "Part2GroupCount", 2); Set(l, "Part3GroupCount", 12); Set(l, "BaselinePart3Destination1Max", 30f); Set(l, "BaselinePart3TotalMax", 60f); return l; }
    static object Ext(string extra) => Core.GetMethod("ResolveExternalFromText").Invoke(null, new object[] { "{ \"participantId\": \"P001\", \"selectedPart\": \"Part3\", \"part2GroupIndex\": 0, \"part3GroupIndex\": 1" + extra + " }", "/cfg/session_config.json", Limits(), null });

    [Test]
    public void SessionConfig_WithoutTheKey_RemainsValid_AndLeavesTheSeedToTheScene()
    {
        object snap = Ext("");
        Assert.IsTrue(Get<bool>(snap, "IsValid"), "a session_config.json without experimentRngMasterSeed stays valid");
        Assert.IsNull(Get<object>(Get<object>(snap, "Overrides"), "ExperimentRngMasterSeed"));
    }

    [Test]
    public void SessionConfig_ParsesTheMasterSeed_AsAHexString_AndRejectsEveryNumber()
    {
        foreach (var c in new (string text, ulong value)[] { ("0000000000000000", 0UL), ("0000000000000001", 1UL), ("00000000013528f6", 20261110UL), ("0123456789abcdef", 0x0123456789ABCDEFUL), ("FFFFFFFFFFFFFFFF", ulong.MaxValue) })
        {
            object snap = Ext(", \"experimentRngMasterSeed\": \"" + c.text + "\"");
            Assert.IsTrue(Get<bool>(snap, "IsValid"), "seed " + c.text);
            Assert.AreEqual(c.value, Get<ulong?>(Get<object>(snap, "Overrides"), "ExperimentRngMasterSeed"));
            CollectionAssert.Contains(Get<System.Collections.ObjectModel.ReadOnlyCollection<string>>(Get<object>(snap, "Overrides"), "Applied"), "experimentRngMasterSeed=" + c.value.ToString("x16"));
        }
        foreach (string bad in new[] { "\"abc\"", "1.5", "true", "null", "3000000000", "0", "20261110", "-5", "[1]" })
        {
            object snap = Ext(", \"experimentRngMasterSeed\": " + bad);
            Assert.IsFalse(Get<bool>(snap, "IsValid"), "invalid seed value " + bad);
            StringAssert.Contains("experimentRngMasterSeed", Get<string>(Get<object>(snap, "Identity"), "message"));
        }
        Assert.IsFalse(Get<bool>(Ext(", \"experimentRngMasterSeeed\": 1"), "IsValid"), "a typo in the key is still rejected (unknown field)");
        Assert.IsNull(Core.GetProperty("ExperimentRngMasterSeedFormat"), "no format classification exists");
    }

    // ------------------------------------------------------------------------------------------------------------------------------ no uncontrolled randomness in project code

    [Test]
    public void ProjectRuntimeScripts_DoNotUseUnityOrSystemRandom_OutsideTheRngCore()
    {
        string scripts = Path.Combine(Application.dataPath, "Scripts");
        var pattern = new Regex(@"\bUnityEngine\.Random\b|(?<![A-Za-z0-9_.])Random\.(Range|value|insideUnit|onUnit|rotation|InitState|state|ColorHSV)\b|new\s+(System\.)?Random\s*\(|Guid\.NewGuid\(\)\s*\.GetHashCode|Environment\.TickCount\s*\*\s*397");
        var offenders = new List<string>();
        foreach (string file in Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name == "ExperimentRng.cs" || name == "ExperimentRngManager.cs") continue;
            if (name == "CsvRowBuilder.cs") continue;   // a fixed-seed self-check of CSV number formatting (data formatting, not experiment randomness)
            int ln = 0;
            foreach (string raw in File.ReadAllLines(file))
            {
                ln++;
                string line = raw.TrimStart();
                if (line.StartsWith("//") || line.StartsWith("///") || line.StartsWith("*")) continue;
                int c = raw.IndexOf("//", StringComparison.Ordinal);
                string code = c >= 0 ? raw.Substring(0, c) : raw;
                code = Regex.Replace(code, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");   // string literals may mention the names (documentation text); only code counts
                if (pattern.IsMatch(code)) offenders.Add(name + ":" + ln + "  " + code.Trim());
            }
        }
        CollectionAssert.IsEmpty(offenders, "project-owned experiment/NPC code must draw from the experiment RNG streams only");
    }

    [Test]
    public void NpcStreamDerivation_NeverUsesParticipantSessionOrClock_AndTheRngCoreHasNoClockOrStringHashCode()
    {
        string core = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "ExperimentRng.cs"));
        string code = string.Join("\n", core.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        foreach (string forbidden in new[] { "DateTime", "TickCount", "GetHashCode", "Stopwatch", "UnityEngine.Random" })
            StringAssert.DoesNotContain(forbidden, code, "the RNG core must not use " + forbidden);
        // the NPC / Part stream derivation (StreamSeed) takes (root seed, Part, stream, index) and nothing else: no participant, session or group input
        int a = code.IndexOf("public static ulong StreamSeed(", StringComparison.Ordinal), b = code.IndexOf("public static ulong HashUtf8(", StringComparison.Ordinal);
        Assert.Greater(a, 0); Assert.Greater(b, a);
        string npc = code.Substring(a, b - a);
        foreach (string forbidden in new[] { "articipant", "ession", "roup" })
            StringAssert.DoesNotContain(forbidden, npc, "NPC streams must not depend on " + forbidden);
        var m = SeedType.GetMethod("StreamSeed");
        CollectionAssert.AreEqual(new[] { typeof(ulong), PartType, StreamType, typeof(long) }, m.GetParameters().Select(p => p.ParameterType).ToArray(), "StreamSeed(rootSeed, part, stream, index)");
    }

    // ================================================================================================================================ 64-BIT MASTER SEED (rng_config_version 2)

    static readonly Type FmtType = T("MasterSeedFormat");
    static object[] ParseHex(string text) { var a = new object[] { text, 0UL, "" }; bool ok = (bool)FmtType.GetMethod("TryParseHex").Invoke(null, a); return new object[] { ok, a[1], a[2] }; }
    static string ToHex(ulong v) => (string)FmtType.GetMethod("ToHex").Invoke(null, new object[] { v });

    [Test]
    public void MasterSeedHex_AcceptsExactlySixteenHexDigits_EitherCase_AndPreservesEveryBit()
    {
        var cases = new (string text, ulong value)[]
        {
            ("0000000000000000", 0UL), ("0000000000000001", 1UL), ("00000000ffffffff", 0x00000000FFFFFFFFUL), ("0000000100000000", 0x0000000100000000UL),
            ("7fffffffffffffff", 0x7FFFFFFFFFFFFFFFUL), ("8000000000000000", 0x8000000000000000UL), ("ffffffffffffffff", ulong.MaxValue),
            ("0123456789abcdef", 0x0123456789ABCDEFUL), ("0123456789ABCDEF", 0x0123456789ABCDEFUL), ("FEDCBA9876543210", 0xFEDCBA9876543210UL), ("fEdCbA9876543210", 0xFEDCBA9876543210UL),
        };
        foreach (var c in cases)
        {
            var r = ParseHex(c.text);
            Assert.IsTrue((bool)r[0], c.text); Assert.AreEqual(c.value, (ulong)r[1], c.text);
            Assert.AreEqual(c.text.ToLowerInvariant(), ToHex(c.value), "canonical output is 16 lowercase digits");
        }
    }

    [Test]
    public void MasterSeedHex_RejectsShortLongNonHexWhitespaceSignsAndPrefixes()
    {
        foreach (string bad in new[] { null, "", "0", "1", "123456789abcdef", "0123456789abcdef0", "0123456789abcdeg", "0123456789abcde ", " 0123456789abcdef", "0123456789abcdef\n",
                                       "0123456789abc def", "0x23456789abcdef", "-123456789abcdef", "+123456789abcdef", "0123456789abcdé", "0123456789abcd\u0660f" /* arabic-indic digit */ })
        {
            var r = ParseHex(bad);
            Assert.IsFalse((bool)r[0], "'" + bad + "' must be rejected");
            Assert.AreEqual(0UL, (ulong)r[1], "a rejected text yields no value");
            Assert.IsNotEmpty((string)r[2]);
        }
    }

    [Test]
    public void MasterSeedHex_ParsingAndFormatting_AreCultureIndependent()
    {
        var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "tr-TR", "ar-SA", "de-DE", "th-TH" })
            {
                try { System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(culture); } catch (System.Globalization.CultureNotFoundException) { continue; }
                var r = ParseHex("0123456789ABCDEF");
                Assert.IsTrue((bool)r[0], culture); Assert.AreEqual(0x0123456789ABCDEFUL, (ulong)r[1], culture);
                Assert.AreEqual("0123456789abcdef", ToHex(0x0123456789ABCDEFUL), culture);
                Assert.AreEqual("00000000000000ab", ToHex(0xABUL), culture);
            }
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = saved; }
    }

    // The derivation is unchanged by the removal of the 32-bit input: the frozen vectors of seeds whose upper half is zero (the values that used to be written as 32-bit numbers) still hold.
    [Test]
    public void SeedsWithAZeroUpperHalf_KeepTheirFrozenStreamsAndTargetSelections()
    {
        Assert.AreEqual(0x3416d3f507fd82e3UL, StreamSeed(12345UL, "Part3", "MovementAssignment", 7));
        Assert.AreEqual(0xebca185bde3bc825UL, StreamSeed(0xFFF0EDFAUL, "Part2", "PartSetup", -1));
        Assert.AreEqual(0x35d409ef71e2213fUL, TSeed(31415UL, "Target1Building", "P001", 4, "OE"));
        Assert.IsNull(FmtType.GetMethod("FromLegacyInt32"), "no legacy int32 conversion helper exists");
        Assert.IsNull(FmtType.GetField("FormatLegacyInt32")); Assert.IsNull(FmtType.GetField("FormatHex64")); Assert.IsNull(FmtType.GetField("FormatNone"));
        Assert.AreNotEqual(StreamSeed(0xFFFFFFFFUL, "Part1", "Destination", 0), StreamSeed(ulong.MaxValue, "Part1", "Destination", 0), "0x00000000ffffffff and 0xffffffffffffffff are different seeds");
    }

    // Frozen vectors for seeds whose upper 32 bits matter; docs/tools/test_experiment_rng.py asserts the SAME values for the Python reference.
    [Test]
    public void Full64BitSeeds_MatchTheFrozenCrossLanguageVectors()
    {
        var vectors = new (ulong seed, ulong streamSeed, uint[] pcg)[]
        {
            (0x0123456789ABCDEFUL, 0xf2bf7e15eba8a3aeUL, new uint[] { 0xf1e263c4, 0xac8a94eb, 0x3f69c154, 0xe2182729 }),
            (0x8000000000000000UL, 0xd12b2071557656fdUL, new uint[] { 0xa5d60f57, 0x3ab404ac, 0xbdeaa517, 0xbca9ad63 }),
            (0xFFFFFFFFFFFFFFFFUL, 0xb3ecaf7719f9038fUL, new uint[] { 0xd8b86df4, 0x0619fc42, 0x7e08b2ee, 0x632dc6a5 }),
            (0x0000000100000000UL, 0x3a6aff65bc3ade19UL, new uint[] { 0x6335d753, 0xbf9e17bc, 0x164906bf, 0xb5a280e1 }),
        };
        foreach (var v in vectors)
        {
            Assert.AreEqual(v.streamSeed, StreamSeed(v.seed, "Part3", "Appearance", 5), ToHex(v.seed));
            CollectionAssert.AreEqual(v.pcg, Draw(NewStream(v.seed, "Part3", "Appearance", 5), 4), "PCG32 outputs of " + ToHex(v.seed));
        }
        Assert.AreEqual(0xfd3479745b31e27dUL, TSeed(0xFEDCBA9876543210UL, "Target1Building", "P001", 4, "OE"));
    }

    [Test]
    public void SeedsDifferingOnlyInTheUpper32Bits_DeriveDifferentStreams_NothingIsTruncated()
    {
        const ulong low = 0x00000000DEADBEEFUL;
        var his = new ulong[] { 0, 1, 2, 0x80000000UL, 0xFFFFFFFFUL };
        var seeds = new HashSet<ulong>(); var firstDraws = new HashSet<uint>();
        foreach (ulong hi in his)
        {
            ulong root = (hi << 32) | low;
            seeds.Add(StreamSeed(root, "Part2", "Wandering", 3));
            firstDraws.Add(U(NewStream(root, "Part2", "Wandering", 3)));
            if (hi != 0) CollectionAssert.AreNotEqual(Draw(NewStream(root, "Part2", "Wandering", 3), 6), Draw(NewStream(low, "Part2", "Wandering", 3), 6), "upper half " + hi.ToString("x"));
        }
        Assert.AreEqual(his.Length, seeds.Count, "each upper-half value yields a distinct stream seed");
        Assert.AreEqual(his.Length, firstDraws.Count);
        // the target-building derivation uses the whole seed too
        var tb = new HashSet<ulong>();
        foreach (ulong hi in his) tb.Add(TSeed((hi << 32) | low, "Target1Building", "P001", 4, "OE"));
        Assert.AreEqual(his.Length, tb.Count);
    }

    [Test]
    public void FullWidthFixedSeed_IsFrozenAsIs_AndEveryDerivedStreamUsesItsFull64Bits()
    {
        foreach (ulong seed in new[] { 0x0000000100000000UL, 0x7FFFFFFFFFFFFFFFUL, 0x8000000000000000UL, ulong.MaxValue })
        {
            Fix(seed);
            Assert.AreEqual(seed, Effective); Assert.AreEqual(seed, GetStatic<ulong>(MgrType, "ConfiguredMasterSeed"));
            Assert.AreEqual("fixed", GetStatic<string>(MgrType, "SeedSource")); Assert.AreEqual("not_used_fixed_seed", GetStatic<string>(MgrType, "EntropySource"));
            Assert.AreEqual(StreamSeed(seed, "Part3", "SpawnPlacement", -1), Get<ulong>(CallStatic(MgrType, "GetPartStream", Part("Part3"), Stream("SpawnPlacement")), "StreamSeed"));
            CollectionAssert.AreEqual(Draw(NewStream(seed, "Part2", "Appearance", 9), 5), Draw(Call(CallStatic(MgrType, "CreateNpcRng", Part("Part2"), 9L), "Get", Stream("Appearance")), 5), "per-NPC streams use the full seed");
            // every stream of the inventory (except the participant-level one, which is derived separately) derives from the full seed
            foreach (string stream in Enum.GetNames(StreamType).Where(n => n != "Target1Building" && n != "Target2Building"))
                Assert.AreEqual(StreamSeed(seed, "Part3", stream, 4), Get<ulong>(NewStream(seed, "Part3", stream, 4), "StreamSeed"), stream);
            var args = new object[] { RngStreamValue("Target2Building"), "P001", 4, "OE", 0UL };
            var tb = MgrType.GetMethod("CreateTargetSelectionStream").Invoke(null, args);
            Assert.AreEqual(TSeed(seed, "Target2Building", "P001", 4, "OE"), (ulong)args[4]);
            Assert.IsNotNull(tb);
        }
    }

    [Test]
    public void AutomaticSeed_Uses64BitsOfEntropy_LittleEndian_AndIsNeverZero()
    {
        Fix(0);
        RngSeams.SetEntropy(b => { for (int i = 0; i < b.Length; i++) b[i] = (byte)(i + 1); Assert.AreEqual(8, b.Length, "all 64 bits are requested"); });
        Assert.AreEqual(0x0807060504030201UL, Effective, "byte 0 is the least significant, independent of the machine byte order");
        Assert.AreEqual("auto_generated", GetStatic<string>(MgrType, "SeedSource"));
        Assert.AreEqual("0807060504030201", ToHex(Effective));
        // an all-zero draw (probability 2^-64) is redrawn rather than patched: no bit is ever forced
        Fix(0); int calls = 0;
        RngSeams.SetEntropy(b => { calls++; if (calls == 2) b[7] = 0x80; });
        Assert.AreEqual(0x8000000000000000UL, Effective); Assert.AreEqual(2, calls);
        // an entropy source that only ever returns zeros fails initialisation instead of inventing a seed
        Fix(0); RngSeams.SetEntropy(b => { });
        UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new Regex("entropy source failed"));
        AssertEveryRngRequestFails();
    }

    [Test]
    public void AutomaticSeed_FromTheRealOsEntropySource_HasEntropyInTheUpperHalf_AndIsGeneratedOncePerSession()
    {
        int upperNonZero = 0, lowerNonZero = 0;
        var seen = new HashSet<ulong>();
        for (int i = 0; i < 32; i++)
        {
            Fix(0);
            ulong e = Effective;
            Assert.AreNotEqual(0UL, e);
            if ((e >> 32) != 0) upperNonZero++;
            if ((e & 0xFFFFFFFFUL) != 0) lowerNonZero++;
            seen.Add(e);
            for (int k = 0; k < 5; k++) { CallStatic(MgrType, "GetPartStream", Part("Part3"), Stream("Destination")); CallStatic(MgrType, "CreateNpcRng", Part("Part3"), (long)k); }
            Assert.AreEqual(e, Effective, "generated once per session");
        }
        Assert.AreEqual(32, seen.Count, "independent sessions get independent seeds");
        Assert.GreaterOrEqual(upperNonZero, 31, "the upper 32 bits come from entropy too (a 32-bit seed extended to 64 bits would leave them zero)");
        Assert.GreaterOrEqual(lowerNonZero, 31);
    }

    // The Part 3 MovementAssignment stream derives from the Master Seed ALONE: (Master Seed, Part, stream id, spawn index). No override, no extra input.
    [Test]
    public void Part3MovementAssignment_IsDerivedExclusivelyFromTheMasterSeed()
    {
        Assert.AreEqual(2, MgrType.GetMethod("CreateNpcRng").GetParameters().Length, "CreateNpcRng(part, spawnIndex): no override parameter");
        Assert.AreEqual(3, SetType.GetConstructors()[0].GetParameters().Length, "NpcRngSet(rootSeed, part, spawnIndex): one root");
        foreach (ulong seed in new[] { 1UL, 0x0123456789ABCDEFUL, 0x8000000000000000UL, ulong.MaxValue })
        {
            Fix(seed);
            for (long idx = 0; idx < 5; idx++)
            {
                var viaManager = Draw(Call(CallStatic(MgrType, "CreateNpcRng", Part("Part3"), idx), "Get", Stream("MovementAssignment")), 4);
                CollectionAssert.AreEqual(Draw(NewStream(seed, "Part3", "MovementAssignment", idx), 4), viaManager, "stream = f(Master Seed, Part3, MovementAssignment, spawn index " + idx + ")");
                Assert.AreEqual(Get<ulong>(NewStream(seed, "Part3", "MovementAssignment", idx), "StreamSeed"), StreamSeed(seed, "Part3", "MovementAssignment", idx));
            }
            // every stream of the NPC, MovementAssignment included, uses the same root
            foreach (string st in Enum.GetNames(StreamType).Where(n => n != "Target1Building" && n != "Target2Building" && n != "PartSetup"))
                Assert.AreEqual(StreamSeed(seed, "Part3", st, 3), Get<ulong>(Call(CallStatic(MgrType, "CreateNpcRng", Part("Part3"), 3L), "Get", Stream(st)), "StreamSeed"), st);
        }
    }

    [Test]
    public void Part3MovementAssignment_SameSeedAndIndexReproduce_DifferentSeedsDiffer_AndTheHighBitsParticipate()
    {
        var sel = T("Part3MovementStrategySelector");
        Func<ulong, long, string> predict = (seed, idx) => CallStatic(sel, "Predict", seed, Part("Part3"), idx, 0.5f).ToString();
        Func<ulong, string> sequence = seed => string.Join(",", Enumerable.Range(0, 200).Select(i => predict(seed, i) == "Part3AttractorItinerary" ? "A" : "W"));
        Assert.AreEqual(sequence(0x0123456789ABCDEFUL), sequence(0x0123456789ABCDEFUL), "same seed + index: same assignment");
        var seqs = new HashSet<string> { sequence(0x0123456789ABCDEFUL), sequence(0x0123456789ABCDEEUL), sequence(0x8123456789ABCDEFUL), sequence(0x0000000189ABCDEFUL), sequence(0x00000000FFFFFFFFUL), sequence(ulong.MaxValue) };
        Assert.AreEqual(6, seqs.Count, "different 64-bit seeds give different sequences (the upper bits participate)");
        // the assignment is the first value of the NPC's own stream, so the per-index decision is nested across probabilities
        foreach (long idx in new long[] { 0, 1, 17, 500 })
            Assert.AreEqual(CallStatic(sel, "FromValue", NewStreamFloat(0x8000000000000001UL, "Part3", "MovementAssignment", idx), 0.6f).ToString(), CallStatic(sel, "Predict", 0x8000000000000001UL, Part("Part3"), idx, 0.6f).ToString());
    }
    static float NewStreamFloat(ulong root, string part, string stream, long index) => F(NewStream(root, part, stream, index));

    [Test]
    public void SessionConfig_MasterSeedTokens_OnlyASixteenDigitHexStringIsAccepted()
    {
        Func<string, object> parse = literal => Ext(", \"experimentRngMasterSeed\": " + literal);
        Func<object, ulong?> seedOf = snap => Get<ulong?>(Get<object>(snap, "Overrides"), "ExperimentRngMasterSeed");
        foreach (var c in new (string literal, ulong value)[] { ("\"0123456789abcdef\"", 0x0123456789ABCDEFUL), ("\"0123456789ABCDEF\"", 0x0123456789ABCDEFUL), ("\"ffffffffffffffff\"", ulong.MaxValue),
                                                              ("\"8000000000000000\"", 0x8000000000000000UL), ("\"7fffffffffffffff\"", 0x7FFFFFFFFFFFFFFFUL), ("\"0000000100000000\"", 0x100000000UL),
                                                              ("\"00000000ffffffff\"", 0xFFFFFFFFUL), ("\"0000000000000001\"", 1UL), ("\"0000000000000000\"", 0UL) })
        {
            object snap = parse(c.literal);
            Assert.IsTrue(Get<bool>(snap, "IsValid"), c.literal); Assert.AreEqual(c.value, seedOf(snap), c.literal);
        }
        foreach (string bad in new[] { "\"0123456789abcde\"", "\"0123456789abcdef0\"", "\"0123456789abcdeg\"", "\" 0123456789abcdef\"", "\"0123456789abcdef \"", "\"0x23456789abcdef\"", "\"\"", "\"0\"", "\"12345\"",
                                       "0", "1", "777", "2147483647", "-1", "-5", "-2147483648", "1.5", "12345.0", "1e3", "1E10", "-0.0", "true", "false", "null", "[1]", "{}", "[\"0123456789abcdef\"]",
                                       "2147483648", "-2147483649", "4294967296", "9223372036854775807", "9223372036854775808", "18446744073709551615", "18446744073709551616" })
        {
            object snap = parse(bad);
            Assert.IsFalse(Get<bool>(snap, "IsValid"), "must be rejected: " + bad);
            StringAssert.Contains("experimentRngMasterSeed", Get<string>(Get<object>(snap, "Identity"), "message"));
            StringAssert.DoesNotContain("legacy", Get<string>(Get<object>(snap, "Identity"), "message"), "no legacy notation is offered");
        }
        // a JSON number is named as such and the accepted form is spelled out
        StringAssert.Contains("a JSON number is not accepted", Get<string>(Get<object>(parse("777"), "Identity"), "message"));
        StringAssert.Contains("16 hexadecimal", Get<string>(Get<object>(parse("777"), "Identity"), "message"));
    }

    [Test]
    public void SeedPipeline_SourceScan_NoNarrowingAndNoFloatingPointOrByteOrderDependence()
    {
        string root = Path.Combine(Application.dataPath, "Scripts");
        Func<string, string> code = file => string.Join("\n", File.ReadAllText(Path.Combine(root, file)).Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        string core = code("ExperimentRng.cs"), mgr = code("ExperimentRngManager.cs");
        foreach (string forbidden in new[] { "int rootSeed", "int masterSeed", "(int)rootSeed", "(int)masterSeed", "(uint)rootSeed", "(uint)masterSeed", "ToInt32", "double.Parse", "Convert.ToDouble" })
        { StringAssert.DoesNotContain(forbidden, core, forbidden); StringAssert.DoesNotContain(forbidden, mgr, forbidden); }
        StringAssert.DoesNotContain("BitConverter", mgr, "the automatic seed is assembled little-endian explicitly, never with the machine byte order");
        StringAssert.DoesNotContain("BitConverter", core);
        StringAssert.Contains("FromBytesLittleEndian", mgr);
        StringAssert.Contains("new byte[8]", mgr, "all 64 bits are requested from the entropy source");
        // the token reader works on the parser's token type and exact integer text: never through float / double / decimal
        string sc = File.ReadAllText(Path.Combine(root, "Capture", "SessionConfiguration.cs"));
        int a = sc.IndexOf("public static bool TryReadSeedToken(", StringComparison.Ordinal), b = sc.IndexOf("static void ReadPartConfig(", StringComparison.Ordinal);
        Assert.Greater(a, 0); Assert.Greater(b, a);
        string reader = sc.Substring(a, b - a);
        foreach (string forbidden in new[] { "(double)", "(float)", "double ", "float ", "Convert.To", "JTokenType.Float" })
            StringAssert.DoesNotContain(forbidden, reader, "seed token reader: " + forbidden);
        Assert.IsFalse(Regex.IsMatch(reader, @"\bdecimal\b"), "seed token reader must not use System.Decimal ('hexadecimal' in messages is fine)");
        // the manifest never serialises a 64-bit seed as a number
        string dcm = File.ReadAllText(Path.Combine(root, "Capture", "DataCaptureManager.cs"));
        StringAssert.Contains("public string configured_master_seed = \"\", effective_master_seed = \"\";", dcm);
    }

    // Architecture scan: the 32-bit Master Seed compatibility and the movement-assignment seed override are gone from the active code, the scene and the current configuration.
    [Test]
    public void SourceScan_NoLegacySeedCompatibilityOrMovementAssignmentOverrideRemains()
    {
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var banned = new Regex(@"part3MovementAssignmentSeed|Part3MovementAssignmentSeed|LegacyMovementAssignmentSeed|SpawnerPart3MovementSeed|spawner_part3_movement_assignment_seed|legacy_int32|LegacyInt32|FromLegacyInt32|legacy_int32_to_u64|legacy_overrides|legacyMovementAssignment|movementAssignmentRoot|configured_seed_format|ConfiguredSeedFormat|ExperimentRngMasterSeedFormat|experiment_rng_master_seed_format");
        var offenders = new List<string>();
        foreach (string f in Directory.GetFiles(Path.Combine(project, "Assets", "Scripts"), "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(f);
            if (banned.IsMatch(text)) offenders.Add(f.Substring(project.Length + 1));
        }
        string scene = Path.Combine(project, "Assets", "Scenes", "LyonVRVity35.unity");
        if (File.Exists(scene) && banned.IsMatch(File.ReadAllText(scene))) offenders.Add("Assets/Scenes/LyonVRVity35.unity");
        foreach (string rel in new[] { "Documentation/Configuration/session_config.example.full.json", "Documentation/Configuration/SESSION_CONFIGURATION_GUIDE.md", "docs/rng_system.md", "docs/npc_data_schema.md", "docs/npc_movement_mix.md", "CLAUDE.md",
                                       "docs/tools/experiment_rng.py", "docs/tools/npc_movement_mix.py" })
        {
            string f = Path.Combine(project, rel);
            if (File.Exists(f) && banned.IsMatch(File.ReadAllText(f))) offenders.Add(rel);
        }
        CollectionAssert.IsEmpty(offenders, "obsolete 32-bit / override seed references must not remain in active code, the scene or current configuration");
        // the current example configuration uses only the 16-hex-digit form
        string example = Path.Combine(project, "Documentation", "Configuration", "session_config.example.full.json");
        if (File.Exists(example)) StringAssert.Contains("\"experimentRngMasterSeed\": \"0000000000000000\"", File.ReadAllText(example));
        // the Master Seed type is ulong everywhere it is configured, recorded or derived from
        Assert.AreEqual(typeof(ulong), MgrType.GetProperty("EffectiveMasterSeed").PropertyType);
        Assert.AreEqual(typeof(ulong), MgrType.GetProperty("ConfiguredMasterSeed").PropertyType);
        Assert.AreEqual(typeof(string), T("ExperimentManager").GetField("experimentRngMasterSeed").FieldType, "the Inspector value is the serialized 16-hex-digit string");
        Assert.IsNull(T("SpawnerManager").GetField("part3MovementAssignmentSeed"));
    }
}
