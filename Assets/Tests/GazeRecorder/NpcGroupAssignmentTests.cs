// NpcGroupAssignmentTests.cs
// Scene-level tests (a partial of NpcLifecycleTests, so Make()/cleanup are shared) of
//   * the strict Master Seed configuration path (session_config.json -> ExternalSessionController -> ExperimentManager -> ExperimentRngManager),
//   * the group-controlled spatial conditions (Part 2 group -> start / teleport point; Part 3 group -> start point and target regions),
//   * the participant-specific target building inside the assigned region, and its manifest record.
// Production types are reached by reflection. Unity Editor PlayMode tests, not Quest tests.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    static readonly Type MgrT = T("ExperimentRngManager");

    // ------------------------------------------------------------------------------------------------------------------------------ builders

    // An ExperimentManager with the given Inspector seed and a session controller that reads either the Inspector (json == null) or the given session_config.json text.
    Component MakeEm(ulong inspectorSeed, string json) { return MakeEmRaw(inspectorSeed.ToString("x16"), json); }

    // The Inspector value as raw text (so invalid Inspector strings can be tested).
    Component MakeEmRaw(string inspectorText, string json)
    {
        var scGo = Make("GroupTestSessionController", active: false);
        var ctl = scGo.AddComponent(T("ExternalSessionController"));
        Set(ctl, "participantId", "ZZ-0001");
        Set(ctl, "selectedPart", Enum.Parse(T("ExternalSessionController").GetNestedType("ExperimentPart"), "Part3"));
        Set(ctl, "part3GroupIndex", 1);
        if (json != null)
        {
            string path = Path.Combine(_root, "session_config_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            Set(ctl, "useExternalJson", true);
            ctl.GetType().GetProperty("ExternalFilePathOverride").SetValue(ctl, path);
        }
        var em = Make("GroupTestExperimentManager").AddComponent(EmType);
        Set(em, "experimentRngMasterSeed", inspectorText);
        scGo.SetActive(true);
        return em;
    }

    static string SessionJson(string extra = "") => "{ \"participantId\": \"ZZ-0001\", \"selectedPart\": \"Part3\", \"part2GroupIndex\": 0, \"part3GroupIndex\": 1" + extra + " }";

    static object[] TryGetSeed(Component em)
    {
        var args = new object[] { 0UL, "", "" };   // (out ulong seed, out string source, out string error)
        bool ok = (bool)EmType.GetMethod("TryGetRngMasterSeedConfiguration").Invoke(em, args);
        return new object[] { ok, args[0], args[1], args[2] };   // ok, seed, source, error
    }

    // ------------------------------------------------------------------------------------------------------------------------------ Master Seed precedence and validation

    [UnityTest]
    public IEnumerator MasterSeedConfig_ValidJsonSeedOverridesTheInspector()
    {
        var em = MakeEm(5, SessionJson(", \"experimentRngMasterSeed\": \"0000000000000309\""));
        var r = TryGetSeed(em);
        Assert.IsTrue((bool)r[0]); Assert.AreEqual(777, (ulong)r[1]); Assert.AreEqual("EXTERNAL_JSON", (string)r[2]);
        Assert.AreEqual(777, GetStatic<ulong>(MgrT, "EffectiveMasterSeed"));
        Assert.AreEqual("fixed", GetStatic<string>(MgrT, "SeedSource"));
        yield return null;
    }

    [UnityTest]
    public IEnumerator MasterSeedConfig_MissingJsonKeyUsesTheInspectorValue()
    {
        var em = MakeEm(5, SessionJson());
        var r = TryGetSeed(em);
        Assert.IsTrue((bool)r[0]); Assert.AreEqual(5, (ulong)r[1]); Assert.AreEqual("SCENE_INHERITED", (string)r[2]);
        Assert.AreEqual(5, GetStatic<ulong>(MgrT, "EffectiveMasterSeed"), "an absent key is not an error: the Inspector value is the fixed seed");
        yield return null;
    }

    [UnityTest]
    public IEnumerator MasterSeedConfig_InternalSource_UsesTheInspectorValue()
    {
        var em = MakeEm(31337, null);
        var r = TryGetSeed(em);
        Assert.IsTrue((bool)r[0]); Assert.AreEqual(31337, (ulong)r[1]); Assert.AreEqual("INSPECTOR", (string)r[2]);
        yield return null;
    }

    [UnityTest]
    public IEnumerator MasterSeedConfig_ExplicitJsonZero_GeneratesExactlyOneAutomaticSeed_EvenWithANonZeroInspector()
    {
        var em = MakeEm(5, SessionJson(", \"experimentRngMasterSeed\": \"0000000000000000\""));
        var r = TryGetSeed(em);
        Assert.IsTrue((bool)r[0]); Assert.AreEqual(0, (ulong)r[1], "explicit 0 = the automatic-seed request, whatever the Inspector says"); Assert.AreEqual("EXTERNAL_JSON", (string)r[2]);
        ulong seed = GetStatic<ulong>(MgrT, "EffectiveMasterSeed");
        Assert.AreNotEqual(0, seed); Assert.AreNotEqual(5, seed);
        Assert.AreEqual("auto_generated", GetStatic<string>(MgrT, "SeedSource"));
        StringAssert.StartsWith("os_csprng", GetStatic<string>(MgrT, "EntropySource"));
        for (int i = 0; i < 5; i++) CallStatic(MgrT, "CreateNpcRng", Enum.Parse(T("RngPart"), "Part3"), (long)i);
        Assert.AreEqual(seed, GetStatic<ulong>(MgrT, "EffectiveMasterSeed"), "generated once");
        yield return null;
    }

    [UnityTest]
    public IEnumerator MasterSeedConfig_MissingJsonKeyAndInspectorZero_GeneratesOneAutomaticSeed()
    {
        var em = MakeEm(0, SessionJson());
        var r = TryGetSeed(em);
        Assert.IsTrue((bool)r[0]); Assert.AreEqual(0, (ulong)r[1]); Assert.AreEqual("SCENE_INHERITED", (string)r[2]);
        Assert.AreNotEqual(0, GetStatic<ulong>(MgrT, "EffectiveMasterSeed"));
        Assert.AreEqual("session_specific_auto_seed", GetStatic<string>(MgrT, "SeedPolicy"));
        yield return null;
    }

    // An explicitly supplied invalid value is a configuration error: the session is rejected, the Inspector value is NOT used and NO automatic seed is generated.
    IEnumerator InvalidSeedJson(string seedLiteral, string expectedInMessage)
    {
        LogAssert.ignoreFailingMessages = true;   // the controller logs the rejected configuration
        var em = MakeEm(5, SessionJson(", \"experimentRngMasterSeed\": " + seedLiteral));
        var r = TryGetSeed(em);
        Assert.IsFalse((bool)r[0], "seed " + seedLiteral + " must reject the configuration");
        StringAssert.Contains("experimentRngMasterSeed", (string)r[3]);
        if (expectedInMessage != null) StringAssert.Contains(expectedInMessage, (string)r[3]);
        // the RNG manager does not fall back to the Inspector (5) or to an automatic seed: it is Failed and every request throws
        Assert.Throws<System.Reflection.TargetInvocationException>(() => GetStatic<ulong>(MgrT, "EffectiveMasterSeed"));
        Assert.AreEqual("Failed", GetStatic<object>(MgrT, "State").ToString());
        Assert.Throws<System.Reflection.TargetInvocationException>(() => CallStatic(MgrT, "CreateNpcRng", Enum.Parse(T("RngPart"), "Part3"), 0L));
        // ExperimentManager refuses to start any Part
        var idAllows = EmType.GetMethod("IdentityAllowsStart", All);
        Assert.IsFalse((bool)idAllows.Invoke(em, new object[] { "StartSelectedPart" }));
        yield return null;
    }

    [UnityTest] public IEnumerator MasterSeedConfig_WrongType_String_IsRejected() { yield return InvalidSeedJson("\"abc\"", "16 hexadecimal"); }
    [UnityTest] public IEnumerator MasterSeedConfig_WrongType_Float_IsRejected() { yield return InvalidSeedJson("1.5", null); }
    [UnityTest] public IEnumerator MasterSeedConfig_WrongType_Bool_IsRejected() { yield return InvalidSeedJson("true", null); }
    [UnityTest] public IEnumerator MasterSeedConfig_Null_IsRejected() { yield return InvalidSeedJson("null", null); }
    [UnityTest] public IEnumerator MasterSeedConfig_NumericInteger_AboveInt32_IsRejected() { yield return InvalidSeedJson("3000000000", "number"); }
    [UnityTest] public IEnumerator MasterSeedConfig_NumericInteger_Int32Positive_IsRejected_NoLegacyPath() { yield return InvalidSeedJson("777", "number"); }
    [UnityTest] public IEnumerator MasterSeedConfig_NumericInteger_Zero_IsRejected_NoLegacyAutomaticZero() { yield return InvalidSeedJson("0", "number"); }
    [UnityTest] public IEnumerator MasterSeedConfig_NumericInteger_Negative_IsRejected() { yield return InvalidSeedJson("-5", "number"); }
    [UnityTest] public IEnumerator MasterSeedConfig_NumericInteger_BelowInt32_IsRejected() { yield return InvalidSeedJson("-2147483649", "number"); }

    [UnityTest] public IEnumerator MasterSeedConfig_Hex_TooShort_IsRejected() { yield return InvalidSeedJson("\"0123456789abcde\"", "16 hexadecimal"); }
    [UnityTest] public IEnumerator MasterSeedConfig_Hex_TooLong_IsRejected() { yield return InvalidSeedJson("\"0123456789abcdef0\"", "16 hexadecimal"); }
    [UnityTest] public IEnumerator MasterSeedConfig_Hex_NonHexCharacters_AreRejected() { yield return InvalidSeedJson("\"0123456789abcdeg\"", "hexadecimal digit"); }
    [UnityTest] public IEnumerator MasterSeedConfig_Hex_Whitespace_IsRejected() { yield return InvalidSeedJson("\" 0123456789abcdef\"", "16 hexadecimal"); }
    [UnityTest] public IEnumerator MasterSeedConfig_FloatWithZeroFraction_IsRejected() { yield return InvalidSeedJson("12345.0", null); }
    [UnityTest] public IEnumerator MasterSeedConfig_ExponentNumber_IsRejected() { yield return InvalidSeedJson("1e3", null); }
    [UnityTest] public IEnumerator MasterSeedConfig_Integer64BitInNumericForm_IsRejected_AndNamesTheHexForm() { yield return InvalidSeedJson("4294967296", "16 hexadecimal"); }
    [UnityTest] public IEnumerator MasterSeedConfig_IntegerAboveInt64_IsRejected() { yield return InvalidSeedJson("18446744073709551615", "16 hexadecimal"); }

    // ---- the only accepted form: a 16-digit hex string in JSON and in the serialized Inspector string ----

    [UnityTest]
    public IEnumerator MasterSeedConfig_HexJson_FullWidthSeed_IsFrozenExactly_AndOverridesTheInspector()
    {
        var em = MakeEm(5, SessionJson(", \"experimentRngMasterSeed\": \"FEDCBA9876543210\""));
        var r = TryGetSeed(em);
        Assert.IsTrue((bool)r[0]); Assert.AreEqual(0xFEDCBA9876543210UL, (ulong)r[1]); Assert.AreEqual("EXTERNAL_JSON", (string)r[2]);
        Assert.AreEqual(0xFEDCBA9876543210UL, GetStatic<ulong>(MgrT, "EffectiveMasterSeed"));
        Assert.AreEqual("fixed", GetStatic<string>(MgrT, "SeedSource"));
        yield return null;
    }

    [UnityTest]
    public IEnumerator MasterSeedConfig_HexJsonZero_IsTheAutomaticRequest_EvenWithANonZeroInspector()
    {
        var em = MakeEm(0x0123456789ABCDEFUL, SessionJson(", \"experimentRngMasterSeed\": \"0000000000000000\""));
        var r = TryGetSeed(em);
        Assert.IsTrue((bool)r[0]); Assert.AreEqual(0UL, (ulong)r[1]);
        ulong seed = GetStatic<ulong>(MgrT, "EffectiveMasterSeed");
        Assert.AreNotEqual(0UL, seed); Assert.AreNotEqual(0x0123456789ABCDEFUL, seed);
        Assert.AreEqual("auto_generated", GetStatic<string>(MgrT, "SeedSource"));
        yield return null;
    }

    [UnityTest]
    public IEnumerator MasterSeedConfig_NumericSeeds_NeverFallBackToTheInspector_OrAnAutomaticSeed()
    {
        LogAssert.ignoreFailingMessages = true;
        foreach (string literal in new[] { "777", "-5", "0", "2147483647", "-2147483648", "1.0", "true", "null" })
        {
            CallStatic(MgrT, "ResetForTests");
            var em = MakeEm(0x0123456789ABCDEFUL, SessionJson(", \"experimentRngMasterSeed\": " + literal));
            Assert.IsFalse((bool)TryGetSeed(em)[0], literal);
            // the first RNG request fails: it neither uses the Inspector value nor generates an automatic seed
            Assert.Throws<System.Reflection.TargetInvocationException>(() => GetStatic<ulong>(MgrT, "EffectiveMasterSeed"), literal);
            Assert.AreEqual("Failed", GetStatic<object>(MgrT, "State").ToString(), literal);
            Assert.IsFalse(GetStatic<bool>(MgrT, "IsInitialized"), literal);
        }
        yield return null;
    }

    [UnityTest]
    public IEnumerator MasterSeedConfig_Inspector_FullWidthHexSeed_IsUsedWhenTheJsonKeyIsAbsent()
    {
        var em = MakeEm(0x8000000000000001UL, SessionJson());
        var r = TryGetSeed(em);
        Assert.IsTrue((bool)r[0]); Assert.AreEqual(0x8000000000000001UL, (ulong)r[1]); Assert.AreEqual("SCENE_INHERITED", (string)r[2]);
        Assert.AreEqual(0x8000000000000001UL, GetStatic<ulong>(MgrT, "EffectiveMasterSeed"));
        yield return null;
    }

    [UnityTest]
    public IEnumerator MasterSeedConfig_Inspector_UppercaseHex_IsAccepted_AndNormalised()
    {
        var em = MakeEmRaw("0123456789ABCDEF", null);
        var r = TryGetSeed(em);
        Assert.IsTrue((bool)r[0]); Assert.AreEqual(0x0123456789ABCDEFUL, (ulong)r[1]); Assert.AreEqual("INSPECTOR", (string)r[2]);
        Assert.AreEqual("0123456789abcdef", GetStatic<ulong>(MgrT, "EffectiveMasterSeed").ToString("x16"));
        yield return null;
    }

    // An invalid Inspector value is an explicit error: no Part starts, nothing falls back to an automatic seed or to the JSON/default.
    [UnityTest]
    public IEnumerator MasterSeedConfig_Inspector_InvalidText_IsRejected_NeverAnAutomaticSeed()
    {
        LogAssert.ignoreFailingMessages = true;
        foreach (string bad in new[] { "0", "12345", "", null, "0123456789abcde", "0123456789abcdef0", "0123456789abcdeg", " 0123456789abcdef", "0x23456789abcdef" })
        {
            CallStatic(MgrT, "ResetForTests");
            var em = MakeEmRaw(bad, null);
            var r = TryGetSeed(em);
            Assert.IsFalse((bool)r[0], "'" + bad + "' must be rejected");
            StringAssert.Contains("experimentRngMasterSeed", (string)r[3]);
            Assert.Throws<System.Reflection.TargetInvocationException>(() => GetStatic<ulong>(MgrT, "EffectiveMasterSeed"));
            Assert.AreEqual("Failed", GetStatic<object>(MgrT, "State").ToString());
            Assert.IsFalse((bool)EmType.GetMethod("IdentityAllowsStart", All).Invoke(em, new object[] { "StartSelectedPart" }), "no Part starts");
        }
        yield return null;
    }

    // A valid JSON seed makes an invalid Inspector string irrelevant (the JSON wins and the Inspector is not consulted); an ABSENT key with an invalid Inspector string is rejected.
    [UnityTest]
    public IEnumerator MasterSeedConfig_InvalidInspector_JsonWins_ButAbsentKeyIsRejected()
    {
        LogAssert.ignoreFailingMessages = true;
        var em = MakeEmRaw("nonsense", SessionJson(", \"experimentRngMasterSeed\": \"00000000000000aa\""));
        Assert.IsTrue((bool)TryGetSeed(em)[0]);
        Assert.AreEqual(0xAAUL, GetStatic<ulong>(MgrT, "EffectiveMasterSeed"));
        CallStatic(MgrT, "ResetForTests");
        var em2 = MakeEmRaw("nonsense", SessionJson());
        Assert.IsFalse((bool)TryGetSeed(em2)[0]);
        yield return null;
    }

    // The migrated scene value: the serialized component in the shipped scene holds the 16-digit zero string (the former integer 0), i.e. the same effective value (automatic).
    [Test]
    public void MasterSeedConfig_ShippedScene_HoldsTheMigratedZeroSeed_AsAnExactSerializedString()
    {
        string yaml = File.ReadAllText(Path.Combine(Application.dataPath, "Scenes", "LyonVRVity35.unity"));
        int n = 0, from = 0;
        while ((from = yaml.IndexOf("\n  experimentRngMasterSeed:", from, StringComparison.Ordinal)) >= 0)
        {
            int end = yaml.IndexOf('\n', from + 1);
            Assert.AreEqual("  experimentRngMasterSeed: 0000000000000000", yaml.Substring(from + 1, end - from - 1).TrimEnd('\r'), "the migrated scene value");
            n++; from = end;
        }
        Assert.AreEqual(1, n, "exactly one ExperimentManager serializes the seed");
    }

    [UnityTest]
    public IEnumerator MasterSeedConfig_MisspelledSeedKey_IsDetectedWithAHint_NeverIgnored()
    {
        LogAssert.ignoreFailingMessages = true;
        foreach (string typo in new[] { "experimentRngMasterSeeed", "experimentRNGMasterSeed", "experimentRngMasterseed", "masterSeed", "rngSeed", "experimentRandomSeed" })
        {
            CallStatic(MgrT, "ResetForTests");
            var em = MakeEm(5, SessionJson(", \"" + typo + "\": 42"));
            var r = TryGetSeed(em);
            Assert.IsFalse((bool)r[0], typo + " must reject the configuration");
            StringAssert.Contains("did you mean 'experimentRngMasterSeed'", (string)r[3]);
            Assert.Throws<System.Reflection.TargetInvocationException>(() => GetStatic<ulong>(MgrT, "EffectiveMasterSeed"));
        }
        // an unrelated unknown key gets no RNG hint (it is rejected by the pre-existing strict key policy, which is unchanged)
        CallStatic(MgrT, "ResetForTests");
        var em2 = MakeEm(5, SessionJson(", \"colour\": 42"));
        var r2 = TryGetSeed(em2);
        Assert.IsFalse((bool)r2[0]); StringAssert.DoesNotContain("did you mean", (string)r2[3]);
        yield return null;
    }

    [UnityTest]
    public IEnumerator MasterSeedConfig_MalformedOrMissingJson_NeverStartsWithAnUncertainSeed()
    {
        LogAssert.ignoreFailingMessages = true;
        var em = MakeEm(5, "{ this is not json");
        Assert.IsFalse((bool)TryGetSeed(em)[0]);
        Assert.Throws<System.Reflection.TargetInvocationException>(() => GetStatic<ulong>(MgrT, "EffectiveMasterSeed"));
        yield return null;
    }

    [UnityTest]
    public IEnumerator MasterSeedConfig_RngUsedBeforeConfigurationIsResolved_StillResolvesFirst_AndNeverFreezesAGuess()
    {
        // The very first RNG request (before anything touched the session configuration) resolves the configuration itself: the JSON value is what gets frozen.
        var em = MakeEm(5, SessionJson(", \"experimentRngMasterSeed\": \"0000000000001267\""));
        Assert.IsFalse(GetStatic<bool>(MgrT, "IsInitialized"));
        CallStatic(MgrT, "GetPartStream", Enum.Parse(T("RngPart"), "None"), Enum.Parse(T("RngStream"), "SpawnPlacement"));   // e.g. the pool preparation
        Assert.AreEqual(4711, GetStatic<ulong>(MgrT, "EffectiveMasterSeed"));
        Assert.AreEqual("EXTERNAL_JSON", GetStatic<string>(MgrT, "ConfigSource"));
        yield return null;
    }

    // ------------------------------------------------------------------------------------------------------------------------------ group-controlled spatial conditions

    Transform Pt(string name) => Make(name).transform;

    void ConfigurePart2(Component em, int startsPerGroup = 1, int teleportsPerGroup = 1, bool nullGroup = false)
    {
        Type gt = EmType.GetNestedType("Part2Group");
        Array groups = Array.CreateInstance(gt, 2);
        for (int g = 0; g < 2; g++)
        {
            if (nullGroup && g == 1) { groups.SetValue(null, g); continue; }
            object grp = Activator.CreateInstance(gt);
            var starts = new Transform[startsPerGroup]; for (int i = 0; i < starts.Length; i++) starts[i] = Pt("P2_S" + g + (i == 0 ? "" : "_" + i));
            var tels = new Transform[teleportsPerGroup]; for (int i = 0; i < tels.Length; i++) tels[i] = Pt("P2_T" + g + (i == 0 ? "" : "_" + i));
            Set(grp, "startPositions", starts); Set(grp, "teleportPositions", tels);
            groups.SetValue(grp, g);
        }
        Set(em, "part2Groups", groups);
    }

    static readonly string[] Zones = { "GE", "GU", "OE", "OU" };

    // fault: "single" (one landmark), "sameObject" (A == B), "noSelectable", "triggerCollider", "disabledCollider", "sharedIdWithOtherZone", "objectInTwoZones" (TargetSelectable requires a Collider, so "no collider" can only appear as a disabled / trigger one) - injected into faultZone's second landmark
    void ConfigurePart3(Component em, string omitStartOf = null, string emptyRegion = null, bool duplicateIdsIn = false, string dupRegion = null, string faultZone = null, string fault = null)
    {
        foreach (string z in Zones)
        {
            if (z != omitStartOf) Set(em, "part3Start" + z, Pt("S_" + z));
            object zone = Activator.CreateInstance(EmType.GetNestedType("Part3ZoneTargets"));
            if (z != emptyRegion)
            {
                // deliberately inverted slot order for one region: the Inspector slot order must not matter
                GameObject a = MakeBuilding(z + "_A"), b = MakeBuilding(z + (duplicateIdsIn && z == dupRegion ? "_A" : "_B"), z + (duplicateIdsIn && z == dupRegion ? "_A" : "_B"));
                bool swap = z == "OE";
                Set(zone, "buildingA", swap ? b : a); Set(zone, "buildingB", swap ? a : b);
            }
            if (z == faultZone && fault != null)
            {
                GameObject a = Get<GameObject>(zone, "buildingA"), b = Get<GameObject>(zone, "buildingB");
                switch (fault)
                {
                    case "single": Set(zone, "buildingB", null); break;
                    case "sameObject": Set(zone, "buildingB", a); break;
                    case "noSelectable": UnityEngine.Object.DestroyImmediate(b.GetComponent(T("TargetSelectable"))); break;
                    case "triggerCollider": b.GetComponent<BoxCollider>().isTrigger = true; break;
                    case "disabledCollider": b.GetComponent<BoxCollider>().enabled = false; break;
                    case "sharedIdWithOtherZone": Set(b.GetComponent(T("TargetSelectable")), "targetId", "GE_A"); break;
                    case "objectInTwoZones": Set(zone, "buildingB", Get<GameObject>(Get<object>(em, "part3ZoneGE"), "buildingA")); break;
                }
            }
            Set(em, "part3Zone" + z, zone);
        }
    }

    GameObject MakeBuilding(string name, string id = null)
    {
        var go = Make(name);
        go.AddComponent<BoxCollider>();   // TargetSelectable requires a Collider
        var ts = go.AddComponent(T("TargetSelectable"));
        Set(ts, "targetId", id ?? name);
        return go;
    }

    static object[] ResolveP2(Component em, int group)
    {
        var args = new object[] { group, null, null };
        bool ok = (bool)EmType.GetMethod("TryResolvePart2Assignment", All).Invoke(em, args);
        return new object[] { ok, args[1], args[2] };
    }

    static object[] ResolveP3(Component em, int group, string participant)
    {
        var args = new object[] { group, participant, null, null };
        bool ok = (bool)EmType.GetMethod("TryResolvePart3Assignment", All).Invoke(em, args);
        return new object[] { ok, args[2], args[3] };
    }

    [UnityTest]
    public IEnumerator Part2Group_DesignatesTheStartAndTeleportPoint_WithoutAnyRngOrParticipantInvolvement()
    {
        var em = MakeEm(0, null); ConfigurePart2(em);
        CallStatic(MgrT, "ResetForTests");
        var g0 = ResolveP2(em, 0); var g1 = ResolveP2(em, 1);
        Assert.IsTrue((bool)g0[0] && (bool)g1[0], (string)g0[2] + (string)g1[2]);
        Assert.AreEqual("P2_S0", Get<string>(g0[1], "StartPointId")); Assert.AreEqual("P2_T0", Get<string>(g0[1], "TeleportPointId"));
        Assert.AreEqual("P2_S1", Get<string>(g1[1], "StartPointId")); Assert.AreEqual("P2_T1", Get<string>(g1[1], "TeleportPointId"));
        Assert.IsFalse(GetStatic<bool>(MgrT, "IsInitialized"), "resolving a Part 2 group never touched the experiment RNG: no Master Seed and no stream is involved");
        // repeated and under different Master Seeds / participant IDs: always the same point
        foreach (ulong seed in new[] { 1UL, 99UL, 0xFFFFFFFF00000007UL, 0UL })
        {
            CallStatic(MgrT, "ResetForTests");
            RngSeams.SetConfig(seed);
            CallStatic(MgrT, "EnsureInitialized");
            for (int i = 0; i < 5; i++) { Assert.AreEqual("P2_S0", Get<string>(ResolveP2(em, 0)[1], "StartPointId")); Assert.AreEqual("P2_S1", Get<string>(ResolveP2(em, 1)[1], "StartPointId")); }
        }
        yield return null;
    }

    [UnityTest]
    public IEnumerator Part2Group_InvalidOrAmbiguousAssignments_AreRejected_NoRandomFallback()
    {
        var em = MakeEm(0, null); ConfigurePart2(em);
        Assert.IsFalse((bool)ResolveP2(em, -1)[0]); Assert.IsFalse((bool)ResolveP2(em, 2)[0]); Assert.IsFalse((bool)ResolveP2(em, 99)[0]);
        StringAssert.Contains("outside 0..1", (string)ResolveP2(em, 2)[2]);
        ConfigurePart2(em, startsPerGroup: 2);
        var amb = ResolveP2(em, 0);
        Assert.IsFalse((bool)amb[0]); StringAssert.Contains("exactly ONE", (string)amb[2]);
        ConfigurePart2(em, teleportsPerGroup: 0);
        Assert.IsFalse((bool)ResolveP2(em, 0)[0]);
        ConfigurePart2(em, nullGroup: true);
        Assert.IsTrue((bool)ResolveP2(em, 0)[0]); Assert.IsFalse((bool)ResolveP2(em, 1)[0]);
        Set(em, "part2Groups", null);
        Assert.IsFalse((bool)ResolveP2(em, 0)[0]);
        yield return null;
    }

    [UnityTest]
    public IEnumerator Part3Group_FixesStartPointAndRegions_ForEveryGroup_IndependentOfSeedAndParticipant()
    {
        var em = MakeEm(0, null); ConfigurePart3(em);
        var tryZones = EmType.GetMethod("TryGetPart3GroupZones");
        Assert.AreEqual(12, (int)EmType.GetProperty("Part3GroupCount").GetValue(null));
        // Table 5 (authoritative): group -> (start zone, Destination 1 zone, Destination 2 zone). The two destination zones differ for ALL twelve groups (also groups 2, 4, 7 and 9).
        var table5 = new[] { "GE,GU,OE", "GE,OE,GU", "GE,OU,GE", "GU,GE,OU", "GU,OE,GU", "GU,OU,GE", "OE,GE,OU", "OE,GU,OE", "OE,OU,GE", "OU,GE,OU", "OU,GU,OE", "OU,OE,GU" };
        for (int g = 0; g < 12; g++)
        {
            var z = new object[] { g, "", "", "" };
            Assert.IsTrue((bool)tryZones.Invoke(null, z));
            string start = (string)z[1], d1 = (string)z[2], d2 = (string)z[3];
            Assert.AreEqual(table5[g], start + "," + d1 + "," + d2, "group " + g + " must match Table 5 exactly");
            Assert.AreNotEqual(d1, d2, "group " + g + ": Destination 1 zone != Destination 2 zone");
            string reference = null;
            if (g == 2 || g == 4 || g == 7 || g == 9) Assert.AreNotEqual(d1, d2, "group " + g + " is NOT a same-zone group (its start zone equals a destination zone, nothing more)");
            foreach (ulong seed in new[] { 11UL, 12UL, 0xFFFFFFFF00000003UL })
                foreach (string pid in new[] { "P001", "P002", "P003" })
                {
                    CallStatic(MgrT, "ResetForTests");
                    RngSeams.SetConfig(seed);
                    var r = ResolveP3(em, g, pid);
                    Assert.IsTrue((bool)r[0], "group " + g + ": " + (string)r[2]);
                    object info = r[1];
                    Assert.AreEqual(start, Get<string>(info, "StartZone")); Assert.AreEqual("S_" + start, Get<string>(info, "StartPointId"));
                    object D1 = Get<object>(info, "Destination1"), D2 = Get<object>(info, "Destination2");
                    Assert.AreEqual(d1, Get<string>(D1, "RegionId")); Assert.AreEqual(d2, Get<string>(D2, "RegionId"));
                    // the selected building belongs to the assigned region and only to it
                    Assert.That(Get<string>(D1, "SelectedId"), Is.EqualTo(d1 + "_A").Or.EqualTo(d1 + "_B"));
                    Assert.That(Get<string>(D2, "SelectedId"), Is.EqualTo(d2 + "_A").Or.EqualTo(d2 + "_B"));
                    CollectionAssert.AreEqual(new[] { d1 + "_A", d1 + "_B" }, Get<string[]>(D1, "CandidateIds"), "candidates sorted by id, whatever the Inspector slot order");
                    Assert.AreEqual(Get<string>(D1, "SelectedId"), Get<GameObject>(D1, "Building").name);
                    string signature = start + "|" + d1 + "|" + d2 + "|" + Get<string>(info, "StartPointId");
                    if (reference == null) reference = signature; else Assert.AreEqual(reference, signature, "start point and regions never depend on the Master Seed or the participant");
                }
        }
        yield return null;
    }

    [UnityTest]
    public IEnumerator Part3TargetBuilding_IsReproducibleWithTheRecordedSeed_AndIndependentOfTheNpcStreams()
    {
        var em = MakeEm(0, null); ConfigurePart3(em);
                CallStatic(MgrT, "ResetForTests");
        RngSeams.SetConfig(555);
        var first = ResolveP3(em, 4, "P042");
        string b1 = Get<string>(Get<object>(first[1], "Destination1"), "SelectedId"), b2 = Get<string>(Get<object>(first[1], "Destination2"), "SelectedId");
        string seed1 = Get<string>(Get<object>(first[1], "Destination1"), "DerivedSeedHex");
        // an NPC draws a lot in between; the selection is unaffected
        var npc = CallStatic(MgrT, "CreateNpcRng", Enum.Parse(T("RngPart"), "Part3"), 0L);
        for (int i = 0; i < 1000; i++) Call(Call(npc, "Get", Enum.Parse(T("RngStream"), "Destination")), "NextUInt");
        var again = ResolveP3(em, 4, "P042");
        Assert.AreEqual(b1, Get<string>(Get<object>(again[1], "Destination1"), "SelectedId")); Assert.AreEqual(b2, Get<string>(Get<object>(again[1], "Destination2"), "SelectedId"));
        // replay: a new application initialisation with the recorded effective seed
        ulong recorded = GetStatic<ulong>(MgrT, "EffectiveMasterSeed");
        CallStatic(MgrT, "ResetForTests");
        RngSeams.SetConfig(recorded);
        var replay = ResolveP3(em, 4, "P042");
        Assert.AreEqual(b1, Get<string>(Get<object>(replay[1], "Destination1"), "SelectedId")); Assert.AreEqual(b2, Get<string>(Get<object>(replay[1], "Destination2"), "SelectedId"));
        Assert.AreEqual(seed1, Get<string>(Get<object>(replay[1], "Destination1"), "DerivedSeedHex"));
        yield return null;
    }

    [UnityTest]
    public IEnumerator Part3Group_InvalidAssignments_AreRejectedBeforeThePartStarts_NoFallback()
    {
        LogAssert.ignoreFailingMessages = true;   // the deliberately duplicated ids are also reported by ExperimentManager.ValidateTargetIds at Start
        var em = MakeEm(0, null); ConfigurePart3(em);
        Assert.IsFalse((bool)ResolveP3(em, 12, "P001")[0]); Assert.IsFalse((bool)ResolveP3(em, -1, "P001")[0]);
        var noId = ResolveP3(em, 0, ""); Assert.IsFalse((bool)noId[0]); StringAssert.Contains("participant ID", (string)noId[2]);
        Assert.IsFalse((bool)ResolveP3(em, 0, null)[0]);
        Assert.IsFalse((bool)ResolveP3(em, 0, "   ")[0]);

        var em2 = MakeEm(0, null); ConfigurePart3(em2, omitStartOf: "GE");                       // group 0 starts in GE
        var noStart = ResolveP3(em2, 0, "P001"); Assert.IsFalse((bool)noStart[0]); StringAssert.Contains("start point", (string)noStart[2]);
        Assert.IsTrue((bool)ResolveP3(em2, 3, "P001")[0], "a group that does not use the missing start is unaffected");

        var em3 = MakeEm(0, null); ConfigurePart3(em3, emptyRegion: "GU");                       // group 0: D1 = GU
        var empty = ResolveP3(em3, 0, "P001"); Assert.IsFalse((bool)empty[0]); StringAssert.Contains("exactly two eligible landmarks, found 0", (string)empty[2]);

        var em4 = MakeEm(0, null); ConfigurePart3(em4, duplicateIdsIn: true, dupRegion: "OE");   // group 0: D2 = OE with two candidates sharing one id
        var dup = ResolveP3(em4, 0, "P001"); Assert.IsFalse((bool)dup[0]); StringAssert.Contains("share the id", (string)dup[2]);
        yield return null;
    }

    // Scene validation before part_start: every zone must be valid even if THIS group does not use it, and every defect gets a precise diagnostic. Never a fallback.
    [UnityTest]
    public IEnumerator Part3Scene_EveryZoneMustHaveExactlyTwoValidLandmarks_BeforePart3Starts()
    {
        LogAssert.ignoreFailingMessages = true;
        var cases = new (string zone, string fault, string expected)[]
        {
            ("GU", "single", "exactly two eligible landmarks, found 1"), ("OE", "sameObject", "the same object"), ("GU", "noSelectable", "no TargetSelectable"),
            ("OE", "triggerCollider", "no enabled non-trigger Collider"), ("GU", "disabledCollider", "no enabled non-trigger Collider"),
            ("OU", "sharedIdWithOtherZone", "is used in both zone"), ("OU", "objectInTwoZones", "assigned to both zone"),
            // GE is the START zone of group 0 and no destination of it: the defect is still found
            ("GE", "single", "exactly two eligible landmarks, found 1"), ("OU", "noSelectable", "no TargetSelectable"),
        };
        foreach (var c in cases)
        {
            var em = MakeEm(0, null); ConfigurePart3(em, faultZone: c.zone, fault: c.fault);
            CallStatic(MgrT, "ResetForTests"); RngSeams.SetConfig(1);
            var r = ResolveP3(em, 0, "P001");
            Assert.IsFalse((bool)r[0], c.zone + "/" + c.fault + " must refuse the Part");
            StringAssert.Contains(c.expected, (string)r[2], c.zone + "/" + c.fault);
            Assert.IsFalse(GetStatic<bool>(MgrT, "IsInitialized"), "a rejected scene consumes no random number: " + c.zone + "/" + c.fault);
        }
        var good = MakeEm(0, null); ConfigurePart3(good);
        CallStatic(MgrT, "ResetForTests"); RngSeams.SetConfig(1);
        Assert.IsTrue((bool)ResolveP3(good, 0, "P001")[0]);
        yield return null;
    }

    // For every Table 5 group and many participants (REAL resolver, synthetic scene objects): Target 1 in the Destination 1 zone, Target 2 in the Destination 2 zone, ids differ, two separate result objects.
    [UnityTest]
    public IEnumerator Part3Targets_ForEveryGroup_AreTwoIndependentSelections_InTheirOwnZones_WithDistinctIds()
    {
        var em = MakeEm(0, null); ConfigurePart3(em);
        var tryZones = EmType.GetMethod("TryGetPart3GroupZones");
        CallStatic(MgrT, "ResetForTests"); RngSeams.SetConfig(0x0123456789ABCDEFUL);
        for (int g = 0; g < 12; g++)
        {
            var z = new object[] { g, "", "", "" }; tryZones.Invoke(null, z);
            string d1 = (string)z[2], d2 = (string)z[3];
            var pairs = new HashSet<string>();
            for (int p = 1; p <= 120; p++)
            {
                var r = ResolveP3(em, g, "P" + p.ToString("D3")); Assert.IsTrue((bool)r[0], (string)r[2]);
                object D1 = Get<object>(r[1], "Destination1"), D2 = Get<object>(r[1], "Destination2");
                Assert.AreNotSame(D1, D2, "two separate destination objects");
                Assert.AreEqual(1, Get<int>(D1, "Destination")); Assert.AreEqual(2, Get<int>(D2, "Destination"));
                StringAssert.StartsWith(d1 + "_", Get<string>(D1, "SelectedId")); StringAssert.StartsWith(d2 + "_", Get<string>(D2, "SelectedId"));
                Assert.AreNotEqual(Get<string>(D1, "SelectedId"), Get<string>(D2, "SelectedId"), "group " + g + " participant " + p);
                Assert.AreNotSame(Get<GameObject>(D1, "Building"), Get<GameObject>(D2, "Building"));
                Assert.AreEqual(Get<string>(D1, "SelectedId"), Get<GameObject>(D1, "Building").GetComponent(T("TargetSelectable")).GetType().GetProperty("ResolvedTargetId").GetValue(Get<GameObject>(D1, "Building").GetComponent(T("TargetSelectable"))));
                Assert.AreNotEqual(Get<string>(D1, "DerivedSeedHex"), Get<string>(D2, "DerivedSeedHex"), "separate selection seeds");
                pairs.Add(Get<string>(D1, "SelectedId") + "|" + Get<string>(D2, "SelectedId"));
            }
            Assert.AreEqual(4, pairs.Count, "group " + g + ": different participants legitimately draw different AND identical pairs (no quota, no no-replacement)");
        }
        // changing the participant can change the selection; the same participant is stable
        string first = Get<string>(Get<object>(ResolveP3(em, 4, "P001")[1], "Destination1"), "SelectedId");
        Assert.AreEqual(first, Get<string>(Get<object>(ResolveP3(em, 4, "P001")[1], "Destination1"), "SelectedId"));
        Assert.IsTrue(Enumerable.Range(2, 40).Any(p => Get<string>(Get<object>(ResolveP3(em, 4, "P" + p.ToString("D3"))[1], "Destination1"), "SelectedId") != first));
        yield return null;
    }

    // ------------------------------------------------------------------------------------------------------------------------------ manifest

    [UnityTest]
    public IEnumerator Manifest_RecordsAssignmentsSeedsAsHexStrings_AbandonedAttemptsAndRngValidation()
    {
        Rig rig = null;
        var o = MixRig(0.5f, 4, speed: 12f);
        o.compact = true; o.createDcm = true; o.masterSeed = 31415;
        yield return BuildRig(o, r => rig = r);
        yield return WaitUntil(() => Get<int>(rig.spawner, "NpcSpawnedTotal") >= 4, 30f, "4 spawns");
        ConfigurePart2(rig.em); ConfigurePart3(rig.em);
        var p2 = ResolveP2(rig.em, 1); var p3 = ResolveP3(rig.em, 4, "P001");
        Assert.IsTrue((bool)p2[0] && (bool)p3[0], (string)p2[2] + (string)p3[2]);
        EmType.GetProperty("Part2Assignment").GetSetMethod(true).Invoke(rig.em, new[] { p2[1] });
        EmType.GetProperty("Part3Assignment").GetSetMethod(true).Invoke(rig.em, new[] { p3[1] });
        Call(rig.spawner, "NoteAbandonedAttempt", Enum.Parse(T("RngPart"), "Part3"), 7L, "no_valid_route");
        var m = ManifestOf(rig);
        var rng = (JObject)m["rng"]; var a = (JObject)m["assignments"];
        Assert.AreEqual("valid", (string)rng["rng_configuration_validation"]);
        Assert.AreEqual("not_used_fixed_seed", (string)rng["automatic_entropy_source"]);
        CollectionAssert.Contains(rng["stream_versions"].Select(t => (string)t).ToArray(), "Target1Building=1");
        CollectionAssert.Contains(rng["stream_versions"].Select(t => (string)t).ToArray(), "Target2Building=1");
        CollectionAssert.DoesNotContain(rng["stream_versions"].Select(t => (string)t).ToArray(), "TargetBuilding=1");
        Assert.AreEqual(1L, (long)rng["abandoned_spawn_attempt_count"]); Assert.AreEqual("Part3:7:no_valid_route", (string)rng["abandoned_spawn_attempts"][0]);
        Assert.AreEqual(1, (int)a["part2_group_index"]); Assert.AreEqual("P2_S1", (string)a["part2_start_point_id"]); Assert.AreEqual("P2_T1", (string)a["part2_teleport_point_id"]);
        Assert.AreEqual(4, (int)a["part3_group_index"]);
        object info = p3[1];
        object d1 = Get<object>(info, "Destination1"), d2 = Get<object>(info, "Destination2");
        Assert.AreEqual(Get<string>(info, "StartZone"), (string)a["part3_start_zone"]); Assert.AreEqual("S_" + Get<string>(info, "StartZone"), (string)a["part3_start_point_id"]);
        // Target selection record (version 2): one stream per destination; zones, ids, candidates, fingerprints and seeds
        Assert.AreEqual(2, (int)a["target_selection_version"]);
        Assert.AreEqual("Target1Building", (string)a["target1_stream"]); Assert.AreEqual("Target2Building", (string)a["target2_stream"]);
        Assert.AreEqual(Get<string>(d1, "RegionId"), (string)a["destination1_zone_id"]); Assert.AreEqual(Get<string>(d2, "RegionId"), (string)a["destination2_zone_id"]);
        Assert.AreNotEqual((string)a["destination1_zone_id"], (string)a["destination2_zone_id"]);
        Assert.AreEqual(Get<string>(d1, "SelectedId"), (string)a["target1_building_id"]); Assert.AreEqual(Get<string>(d2, "SelectedId"), (string)a["target2_building_id"]);
        Assert.AreNotEqual((string)a["target1_building_id"], (string)a["target2_building_id"]);
        Assert.AreEqual(Get<string>(d1, "CandidateFingerprint"), (string)a["destination1_candidate_fingerprint"]); Assert.AreEqual(Get<string>(d2, "CandidateFingerprint"), (string)a["destination2_candidate_fingerprint"]);
        Assert.AreEqual(2, a["destination1_candidate_ids"].Count()); Assert.AreEqual(2, a["destination2_candidate_ids"].Count());
        CollectionAssert.Contains(a["destination1_candidate_ids"].Select(t => (string)t).ToArray(), (string)a["target1_building_id"]);
        CollectionAssert.Contains(a["destination2_candidate_ids"].Select(t => (string)t).ToArray(), (string)a["target2_building_id"]);
        Assert.IsTrue((bool)a["destination_zones_distinct"] && (bool)a["target1_in_destination1_zone"] && (bool)a["target2_in_destination2_zone"] && (bool)a["target_building_ids_distinct"]);
        StringAssert.Contains("TargetSelectionSeed", (string)a["target_selection_derivation"]);
        Assert.IsNull(a["target_building_stream"], "the single shared stream no longer exists"); Assert.IsNull(a["destination1_region_id"]); Assert.IsNull(a["destination1_derived_seed"]);
        // 64-bit seeds are 16 lowercase hex digits (a JSON string), exactly the derived value - never a JSON number
        string hex1 = (string)a["target1_selection_seed"], hex2 = (string)a["target2_selection_seed"];
        Assert.AreEqual(JTokenType.String, a["target1_selection_seed"].Type); Assert.AreEqual(JTokenType.String, a["target2_selection_seed"].Type);
        StringAssert.IsMatch("^[0-9a-f]{16}$", hex1); StringAssert.IsMatch("^[0-9a-f]{16}$", hex2); Assert.AreNotEqual(hex1, hex2);
        Assert.AreEqual((ulong)CallStatic(T("SeedDerivation"), "TargetSelectionSeed", 31415UL, Enum.Parse(T("RngPart"), "Part3"), Enum.Parse(T("RngStream"), "Target1Building"), "P001", 4, Get<string>(d1, "RegionId")), Convert.ToUInt64(hex1, 16));
        Assert.AreEqual((ulong)CallStatic(T("SeedDerivation"), "TargetSelectionSeed", 31415UL, Enum.Parse(T("RngPart"), "Part3"), Enum.Parse(T("RngStream"), "Target2Building"), "P001", 4, Get<string>(d2, "RegionId")), Convert.ToUInt64(hex2, 16));
        Assert.AreEqual(Get<string>(d1, "DerivedSeedHex"), hex1); Assert.AreEqual(Get<string>(d2, "DerivedSeedHex"), hex2);
        foreach (string part in ((JArray)rng["part_stream_seeds"]).Select(t => (string)t)) StringAssert.IsMatch("^[A-Za-z0-9]+:[A-Za-z0-9]+=[0-9a-f]{16}$", part);
    }
}
