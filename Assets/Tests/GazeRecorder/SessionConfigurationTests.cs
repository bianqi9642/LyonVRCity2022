// SessionConfigurationTests.cs
// Tests for the unified session configuration (SessionConfiguration.cs, ExternalSessionController.cs).
//   * SessionConfigurationTests          - the pure parse/validate/resolve rules (SessionConfigCore). No Unity objects: these run anywhere, including a plain
//                                          mono/NUnit host.
//   * SessionConfigurationComponentTests - the ExternalSessionController component (source selection, atomicity, immutability) and the Part 3 table. They
//                                          need UnityEngine and therefore only run inside the Unity Test Runner.
// The runtime scripts live in the predefined Assembly-CSharp, which an assembly definition cannot reference, so the production types are reached by reflection
// (see TestSupport.cs). No production logic is re-implemented here.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using static Reflect;

public class SessionConfigurationTests
{
    static readonly Type Core = T("SessionConfigCore");

    // ---------------------------------------------------------------------------------------------------------------------------- helpers

    static object Limits(int p2 = 2, int p3 = 12, float d1 = 30f, float total = 60f)
    {
        object l = Activator.CreateInstance(Core.GetNestedType("Limits"));
        Set(l, "Part2GroupCount", p2); Set(l, "Part3GroupCount", p3);
        Set(l, "BaselinePart3Destination1Max", d1); Set(l, "BaselinePart3TotalMax", total);
        return l;
    }

    static object Ext(string json, object limits = null, Func<string, string> hasher = null)
        => Core.GetMethod("ResolveExternalFromText").Invoke(null, new object[] { json, "/cfg/session_config.json", limits ?? Limits(), hasher });

    static object Int(string id, int part, int g2, int g3, bool autoStart = false, object limits = null)
        => Core.GetMethod("ResolveInternal").Invoke(null, new object[] { id, part, g2, g3, autoStart, limits ?? Limits() });

    static bool Valid(object snap) => Get<bool>(snap, "IsValid");
    static string Status(object snap) => Get<object>(Get<object>(snap, "Identity"), "status").ToString();
    static string Msg(object snap) => Get<string>(Get<object>(snap, "Identity"), "message");

    static string Json(string id = "P001", string part = "Part3", int g2 = 0, int g3 = 1, string extra = "")
        => "{ \"participantId\": \"" + id + "\", \"selectedPart\": \"" + part + "\", \"part2GroupIndex\": " + g2 + ", \"part3GroupIndex\": " + g3 + extra + " }";

    // ---------------------------------------------------------------------------------------------------------------------------- participant id

    [Test]
    public void ParticipantId_RealFormatsAccepted_StringPreservedExactly()
    {
        foreach (string id in new[] { "P001", "P002", "P061", "P007", "007", "12", "A-07" })
        {
            object s = Ext(Json(id: id));
            Assert.IsTrue(Valid(s), id + ": " + Msg(s));
            Assert.AreEqual(id, Get<string>(s, "ParticipantId"), "leading zeros / text preserved, never converted to a number");
        }
        Assert.AreEqual("P050", Get<string>(Ext(Json(id: "  P050  ")), "ParticipantId"), "only surrounding whitespace is trimmed");
    }

    [Test]
    public void ParticipantId_PlaceholdersAndEmptyRejected_InBothSources()
    {
        foreach (string id in new[] { "0", "P000", "", "   ", "UNKNOWN" })
        {
            Assert.IsFalse(Valid(Ext(Json(id: id))), "external '" + id + "'");
            Assert.IsFalse(Valid(Int(id, 3, 0, 1)), "internal '" + id + "'");
        }
        Assert.IsTrue(Valid(Int("P001", 3, 0, 1)));
        Assert.AreEqual("P061", Get<string>(Int("P061", 2, 0, 0), "ParticipantId"));
    }

    [Test]
    public void ParticipantId_MustBeAJsonString_NotANumber()
    {
        object s = Ext("{ \"participantId\": 1, \"selectedPart\": \"Part1\", \"part2GroupIndex\": 0, \"part3GroupIndex\": 0 }");
        Assert.IsFalse(Valid(s));
        StringAssert.Contains("participantId", Msg(s));
        StringAssert.Contains("JSON string", Msg(s));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- selected part

    [Test]
    public void SelectedPart_ThreeValuesAccepted_UnknownAndWrongTypesRejected()
    {
        Assert.AreEqual(1, Get<int>(Ext(Json(part: "Part1")), "PartNumber"));
        Assert.AreEqual(2, Get<int>(Ext(Json(part: "Part2")), "PartNumber"));
        Assert.AreEqual(3, Get<int>(Ext(Json(part: "Part3")), "PartNumber"));
        Assert.AreEqual(3, Get<int>(Ext(Json(part: "part3")), "PartNumber"), "case-insensitive, as before");
        foreach (string bad in new[] { "Part4", "PartX", "", "3", "Part 3" })
        {
            object s = Ext(Json(part: bad));
            Assert.IsFalse(Valid(s), "'" + bad + "'");
            StringAssert.Contains("selectedPart", Msg(s));
        }
        Assert.IsFalse(Valid(Ext("{ \"participantId\": \"P001\", \"selectedPart\": 3, \"part2GroupIndex\": 0, \"part3GroupIndex\": 0 }")), "number instead of string");
        Assert.IsFalse(Valid(Int("P001", 0, 0, 0)), "internal: 0 is not a Part");
        Assert.IsFalse(Valid(Int("P001", 4, 0, 0)), "internal: 4 is not a Part");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- group indices

    [Test]
    public void Part2GroupIndex_ValidatedAgainstTheActualGroupCount_NeverClamped()
    {
        Assert.IsTrue(Valid(Ext(Json(part: "Part2", g2: 0))));
        Assert.IsTrue(Valid(Ext(Json(part: "Part2", g2: 1))));
        foreach (int bad in new[] { 2, 5, -1 })
        {
            object s = Ext(Json(part: "Part2", g2: bad));
            Assert.IsFalse(Valid(s), "index " + bad);
            StringAssert.Contains("part2GroupIndex", Msg(s));
            StringAssert.Contains("0..1", Msg(s), "the message names the real range");
            Assert.IsFalse(Valid(Int("P001", 2, bad, 0)), "internal index " + bad + " is rejected too (no silent clamp)");
        }
        Assert.IsTrue(Valid(Ext(Json(part: "Part2", g2: 2), Limits(p2: 3))), "a scene with 3 groups accepts index 2: the count is read, not hard-coded");
        Assert.IsTrue(Valid(Ext(Json(part: "Part2", g2: 7), Limits(p2: 0))), "unknown/unconfigured count: only the lower bound is checked here (ExperimentManager refuses at Part start)");
    }

    [Test]
    public void Part3GroupIndex_ZeroToEleven()
    {
        Assert.IsTrue(Valid(Ext(Json(part: "Part3", g3: 0))));
        Assert.IsTrue(Valid(Ext(Json(part: "Part3", g3: 1))));
        Assert.IsTrue(Valid(Ext(Json(part: "Part3", g3: 11))));
        foreach (int bad in new[] { 12, 100, -1 })
        {
            object s = Ext(Json(part: "Part3", g3: bad));
            Assert.IsFalse(Valid(s), "index " + bad);
            StringAssert.Contains("part3GroupIndex", Msg(s));
            StringAssert.Contains("0..11", Msg(s));
            Assert.IsFalse(Valid(Int("P001", 3, 0, bad)), "internal index " + bad);
        }
        Assert.AreEqual(1, Get<int>(Ext(Json(part: "Part3", g3: 1)), "Part3GroupIndex"));
        Assert.AreEqual(1, Get<int>(Ext(Json(part: "Part3", g3: 1)), "ActiveGroupIndex"));
    }

    [Test]
    public void IrrelevantGroupIndices_NeverBlockTheSelectedPart()
    {
        // Part 1: neither index matters (the sample file used 99/99 to prove it)
        Assert.IsTrue(Valid(Ext(Json(part: "Part1", g2: 99, g3: 99))));
        Assert.IsTrue(Valid(Int("P001", 1, 99, 99)));
        Assert.AreEqual(-1, Get<int>(Ext(Json(part: "Part1", g2: 99, g3: 99)), "ActiveGroupIndex"));
        // Part 2 ignores part3GroupIndex, Part 3 ignores part2GroupIndex
        object p2 = Ext(Json(part: "Part2", g2: 1, g3: 99));
        Assert.IsTrue(Valid(p2), Msg(p2)); Assert.AreEqual(1, Get<int>(p2, "ActiveGroupIndex"));
        Assert.IsTrue(Valid(Int("P001", 2, 1, 99)));
        object p3 = Ext(Json(part: "Part3", g2: 99, g3: 4));
        Assert.IsTrue(Valid(p3), Msg(p3)); Assert.AreEqual(4, Get<int>(p3, "ActiveGroupIndex"));
        Assert.IsTrue(Valid(Int("P001", 3, 99, 4)));
        // ... but both must still be integers
        Assert.IsFalse(Valid(Ext("{ \"participantId\": \"P001\", \"selectedPart\": \"Part1\", \"part2GroupIndex\": \"zero\", \"part3GroupIndex\": 0 }")));
    }

    [Test]
    public void GroupIndices_MustBeIntegers()
    {
        foreach (string bad in new[] { "1.5", "\"1\"", "true", "null", "1.0" })
        {
            object s = Ext("{ \"participantId\": \"P001\", \"selectedPart\": \"Part3\", \"part2GroupIndex\": 0, \"part3GroupIndex\": " + bad + " }");
            Assert.IsFalse(Valid(s), bad);
            StringAssert.Contains("part3GroupIndex", Msg(s));
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------- structure / schema

    [Test]
    public void EveryRequiredField_MustBePresent()
    {
        foreach (string key in new[] { "participantId", "selectedPart", "part2GroupIndex", "part3GroupIndex" })
        {
            var doc = Newtonsoft.Json.Linq.JObject.Parse(Json());
            doc.Remove(key);
            object s = Ext(doc.ToString());
            Assert.IsFalse(Valid(s), key);
            StringAssert.Contains("missing required field '" + key + "'", Msg(s));
        }
    }

    [Test]
    public void MalformedEmptyDuplicateAndUnknownFields_AreRejected()
    {
        foreach (string bad in new[] { "{ this is not json", "", "   ", "[]", "\"x\"", Json() + " trailing", "{ \"participantId\": \"P001\", \"participantId\": \"P002\", \"selectedPart\": \"Part1\", \"part2GroupIndex\": 0, \"part3GroupIndex\": 0 }" })
        {
            object s = Ext(bad);
            Assert.IsFalse(Valid(s), "'" + bad + "'");
            Assert.AreEqual("UnresolvedConfig", Status(s));
        }
        object unk = Ext(Json(extra: ", \"part3GroupIdx\": 3"));
        Assert.IsFalse(Valid(unk), "a typo'd key must not be silently ignored");
        StringAssert.Contains("unknown field 'part3GroupIdx'", Msg(unk));
    }

    [Test]
    public void SchemaVersion_OptionalForLegacyFiles_ButAnUnsupportedVersionIsRejected()
    {
        object legacy = Ext(Json() );
        Assert.IsTrue(Valid(legacy)); Assert.AreEqual(1, Get<int>(legacy, "SchemaVersion")); Assert.IsFalse(Get<bool>(legacy, "SchemaVersionDeclared"));
        object v1 = Ext(Json(extra: ", \"schemaVersion\": 1"));
        Assert.IsTrue(Valid(v1)); Assert.IsTrue(Get<bool>(v1, "SchemaVersionDeclared"));
        foreach (string bad in new[] { "2", "0", "\"1\"", "1.5" }) Assert.IsFalse(Valid(Ext(Json(extra: ", \"schemaVersion\": " + bad))), bad);
        // the pre-existing file format (exactly the five original keys) still loads unchanged
        object old = Ext("{ \"participantId\":\"P012\", \"selectedPart\": \"Part2\", \"part2GroupIndex\": 1, \"part3GroupIndex\": 0, \"autoStart\": true }");
        Assert.IsTrue(Valid(old)); Assert.IsTrue(Get<bool>(old, "AutoStart")); Assert.AreEqual("P012", Get<string>(old, "ParticipantId"));
    }

    [Test]
    public void AutoStart_DefaultsToFalse_AndMustBeABoolean()
    {
        Assert.IsFalse(Get<bool>(Ext(Json()), "AutoStart"));
        Assert.IsTrue(Get<bool>(Ext(Json(extra: ", \"autoStart\": true")), "AutoStart"));
        Assert.IsFalse(Valid(Ext(Json(extra: ", \"autoStart\": \"yes\""))));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- atomic / no fallback / immutable

    [Test]
    public void InvalidExternalConfiguration_AppliesNothing_AndIsNotAnInternalFallback()
    {
        // everything valid except the Part 3 index; the (valid) id, part and overrides must not leak into the snapshot
        object s = Ext(Json(id: "P077", part: "Part3", g3: 12, extra: ", \"autoStart\": true, \"timing\": { \"npcWarmupDuration\": 5 }"));
        Assert.IsFalse(Valid(s));
        Assert.AreEqual("UnresolvedConfig", Status(s));
        Assert.AreEqual("", Get<string>(s, "ParticipantId"));
        Assert.IsFalse(Get<bool>(s, "AutoStart"));
        Assert.IsFalse(Get<bool>(Get<object>(s, "Overrides"), "Any"), "no override was applied");
        Assert.AreEqual("external_json", Get<string>(s, "Source"), "stays an external-source failure; never re-labelled 'inspector'");
        StringAssert.Contains("/cfg/session_config.json", Msg(s), "the diagnostic names the file");
        StringAssert.Contains("part3GroupIndex", Msg(s), "... and the failed field");
    }

    [Test]
    public void MissingFile_IsUnresolved_WithTheExactPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "sc_missing_" + Guid.NewGuid().ToString("N") + ".json");
        object s = Core.GetMethod("ResolveExternalFromFile").Invoke(null, new object[] { path, Limits(), null });
        Assert.IsFalse(Valid(s)); Assert.AreEqual("UnresolvedConfig", Status(s));
        StringAssert.Contains(path, Msg(s));
        Assert.IsFalse(File.Exists(path), "the loader never creates a configuration file");
    }

    [Test]
    public void ResolvedSnapshot_IsImmutable_AndNotAffectedByLaterFileChanges()
    {
        string path = Path.Combine(Path.GetTempPath(), "sc_immut_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, Json(id: "P010", part: "Part2", g2: 1));
            object s = Core.GetMethod("ResolveExternalFromFile").Invoke(null, new object[] { path, Limits(), (Func<string, string>)(x => "h" + x.Length) });
            Assert.IsTrue(Valid(s), Msg(s));
            File.WriteAllText(path, Json(id: "P011", part: "Part3", g3: 5));   // mid-session edit by the experimenter
            Assert.AreEqual("P010", Get<string>(s, "ParticipantId")); Assert.AreEqual(2, Get<int>(s, "PartNumber")); Assert.AreEqual(1, Get<int>(s, "Part2GroupIndex"));
            Assert.AreEqual(path, Get<string>(s, "SourcePath"));
            Assert.IsTrue(Get<string>(s, "Hash").StartsWith("h"), "hash comes from the text that was applied");
            foreach (object o in new[] { s, Get<object>(s, "Overrides") })
                foreach (PropertyInfo p in o.GetType().GetProperties(Reflect.All))
                    if (p.SetMethod != null) Assert.IsFalse(p.SetMethod.IsPublic, o.GetType().Name + "." + p.Name + " has a public setter");
            foreach (string fld in new[] { "Overrides", "Warnings" })
                foreach (FieldInfo f in s.GetType().GetFields(Reflect.All)) Assert.IsFalse(f.IsPublic && f.Name == fld, "no public mutable field");
            Assert.IsInstanceOf(typeof(System.Collections.ObjectModel.ReadOnlyCollection<string>), Get<object>(s, "Warnings"));
            Assert.IsInstanceOf(typeof(System.Collections.ObjectModel.ReadOnlyCollection<string>), Get<object>(Get<object>(s, "Overrides"), "Applied"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---------------------------------------------------------------------------------------------------------------------------- internal source

    [Test]
    public void InternalSource_UsesTheGivenInspectorValuesOnly()
    {
        object s = Int("P005", 3, 1, 7, autoStart: true);
        Assert.IsTrue(Valid(s)); Assert.AreEqual("inspector", Get<string>(s, "Source"));
        Assert.AreEqual("P005", Get<string>(s, "ParticipantId")); Assert.AreEqual(3, Get<int>(s, "PartNumber"));
        Assert.AreEqual(1, Get<int>(s, "Part2GroupIndex")); Assert.AreEqual(7, Get<int>(s, "Part3GroupIndex")); Assert.IsTrue(Get<bool>(s, "AutoStart"));
        Assert.AreEqual("", Get<string>(s, "Hash")); Assert.AreEqual("", Get<string>(s, "SourcePath"));
        Assert.IsFalse(Get<bool>(Get<object>(s, "Overrides"), "Any"));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- optional overrides

    [Test]
    public void Timing_AndPartConfigOverrides_AreParsed_AndRecordedAsProvenance()
    {
        object s = Ext(Json(part: "Part3", g3: 1, extra:
            ", \"timing\": { \"part1Duration\": 45, \"part3Destination1MaxSeconds\": 300.5, \"part3TotalMaxSeconds\": 600, \"npcWarmupDuration\": 0 }" +
            ", \"partConfig\": { \"part2\": { \"npcEnabled\": false }, \"part3\": { \"npcDirectedMovement\": true, \"npcCaptureEnabled\": false } }"));
        Assert.IsTrue(Valid(s), Msg(s));
        object ov = Get<object>(s, "Overrides");
        Assert.AreEqual(45f, Get<float?>(ov, "Part1Duration").Value);
        Assert.AreEqual(300.5f, Get<float?>(ov, "Part3Destination1MaxSeconds").Value);
        Assert.AreEqual(0f, Get<float?>(ov, "NpcWarmupDuration").Value, "0 = skip the warm-up wait");
        Assert.IsFalse(Get<float?>(ov, "Part2FirstDuration").HasValue, "unspecified stays null = keep the scene value");
        var applied = Get<System.Collections.ObjectModel.ReadOnlyCollection<string>>(ov, "Applied");
        CollectionAssert.Contains(applied, "timing.part1Duration=45");
        CollectionAssert.Contains(applied, "partConfig.part2.npcEnabled=false");
        CollectionAssert.Contains(applied, "partConfig.part3.npcCaptureEnabled=false");
        Assert.AreEqual(7, applied.Count);
        object p3 = Call(ov, "GetPartFlags", 3);
        Assert.AreEqual(true, Get<bool?>(p3, "npcDirectedMovement").Value); Assert.AreEqual(false, Get<bool?>(p3, "npcCaptureEnabled").Value);
        Assert.IsFalse(Get<bool?>(p3, "npcEnabled").HasValue);
    }

    [Test]
    public void InvalidOverrides_AreRejected()
    {
        foreach (string bad in new[]
        {
            ", \"timing\": { \"part1Duration\": 0 }", ", \"timing\": { \"part1Duration\": -5 }", ", \"timing\": { \"part1Duration\": \"60\" }",
            ", \"timing\": { \"part1Duration\": 100000 }", ", \"timing\": { \"npcWarmupDuration\": -1 }", ", \"timing\": { \"part1Dur\": 5 }", ", \"timing\": 5",
            ", \"partConfig\": { \"part4\": {} }", ", \"partConfig\": { \"part1\": { \"npcEnabled\": 1 } }", ", \"partConfig\": { \"part1\": { \"npcEnabeld\": true } }",
        })
            Assert.IsFalse(Valid(Ext(Json(extra: bad))), bad);
        // Destination 1 may not exceed the total (effective pair, using the scene baseline for the unspecified one); only checked for Part 3
        Assert.IsFalse(Valid(Ext(Json(part: "Part3", extra: ", \"timing\": { \"part3Destination1MaxSeconds\": 61 }"))), "61 > scene total 60");
        Assert.IsFalse(Valid(Ext(Json(part: "Part3", extra: ", \"timing\": { \"part3Destination1MaxSeconds\": 900, \"part3TotalMaxSeconds\": 600 }"))));
        Assert.IsTrue(Valid(Ext(Json(part: "Part3", extra: ", \"timing\": { \"part3Destination1MaxSeconds\": 600, \"part3TotalMaxSeconds\": 900 }"))));
        Assert.IsTrue(Valid(Ext(Json(part: "Part2", extra: ", \"timing\": { \"part3Destination1MaxSeconds\": 900 }"))), "irrelevant pair is not cross-checked for Part 2");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- effective settings (frozen)

    static readonly Type SceneType = T("SceneSettings");
    static readonly Type FlagsType = T("PartFlagValues");

    static object Flags(bool en, bool dir, bool cap, bool gaze, bool traj) => Activator.CreateInstance(FlagsType, en, dir, cap, gaze, traj);

    // The saved-scene values as of this work (illustrative test input): durations 15/15/15/30/60/10 and the three Part flag sets.
    static object Scene(float p1 = 15f, bool allowWrong = false)
    {
        object sc = Activator.CreateInstance(SceneType);
        Set(sc, "Part1Duration", p1); Set(sc, "Part2FirstDuration", 15f); Set(sc, "Part2SecondDuration", 15f);
        Set(sc, "Part3Destination1MaxSeconds", 30f); Set(sc, "Part3TotalMaxSeconds", 60f); Set(sc, "NpcWarmupDuration", 10f);
        var arr = Array.CreateInstance(FlagsType, 3);
        arr.SetValue(Flags(false, true, false, false, false), 0);
        arr.SetValue(Flags(true, false, false, true, true), 1);
        arr.SetValue(Flags(true, true, true, true, true), 2);
        Set(sc, "PartFlags", arr);
        Set(sc, "AllowWrongCandidateSelection", allowWrong); Set(sc, "RestrictRiverTeleportToPart3", true);
        Set(sc, "SpawnerMaxAgents", 5000); Set(sc, "SpawnerPoolPrewarm", 5000); Set(sc, "SpawnerSpawnInterval", 0.1f);
        Set(sc, "SpawnerDwellTimeMin", 5f); Set(sc, "SpawnerDwellTimeMax", 15f);
        Set(sc, "AgentProfileName", "Agent Profile"); Set(sc, "AgentProfileHash", "abc123");
        return sc;
    }

    static object Freeze(object snap, object scene) => snap.GetType().GetMethod("FreezeEffective").Invoke(snap, new[] { scene });

    static string Entry(object eff, string key, string what)
    {
        foreach (object e in (System.Collections.IEnumerable)Get<object>(eff, "Entries"))
            if (Get<string>(e, "Key") == key) return Get<string>(e, what);
        Assert.Fail("setting entry missing: " + key); return null;
    }

    [Test]
    public void Effective_NotAvailableUntilFrozen_AndNeverForAnInvalidConfiguration()
    {
        object ok = Int("P001", 3, 0, 1);
        Assert.IsNull(Get<object>(ok, "Effective"), "nothing is exposed before the freeze");
        Assert.IsNotNull(Freeze(ok, Scene()));
        Assert.IsNotNull(Get<object>(ok, "Effective"));
        object bad = Ext(Json(g3: 12));
        Assert.IsNull(Freeze(bad, Scene()), "an invalid configuration yields no effective settings");
        Assert.IsNull(Get<object>(bad, "Effective"));
    }

    [Test]
    public void Effective_InternalMode_AllValuesComeFromTheInspector()
    {
        object eff = Freeze(Int("P001", 2, 1, 0, autoStart: true), Scene());
        Assert.AreEqual(15f, Get<float>(eff, "Part1Duration")); Assert.AreEqual(60f, Get<float>(eff, "Part3TotalMaxSeconds")); Assert.AreEqual(10f, Get<float>(eff, "NpcWarmupDuration"));
        foreach (object e in (System.Collections.IEnumerable)Get<object>(eff, "Entries")) Assert.AreEqual("INSPECTOR", Get<string>(e, "Source"), Get<string>(e, "Key"));
        Assert.AreEqual("true", Entry(eff, "auto_start", "Value"));
        Assert.AreEqual("false", Entry(eff, "part1_npc_enabled", "Value")); Assert.AreEqual("false", Entry(eff, "part2_npc_directed_movement", "Value"));
        Assert.AreEqual("true", Entry(eff, "part3_npc_capture_enabled", "Value"));
        Assert.AreEqual("false", Entry(eff, "allow_wrong_candidate_selection", "Value")); Assert.AreEqual("5000", Entry(eff, "spawner_max_agents", "Value"));
        Assert.AreEqual("Agent Profile", Entry(eff, "agent_profile_name", "Value"));
    }

    [Test]
    public void Effective_ExternalMode_MissingOptionalFieldsAreSceneInherited_ProvidedOnesAreExternalJson()
    {
        object snap = Ext(Json(part: "Part3", g3: 1, extra: ", \"timing\": { \"part1Duration\": 45 }, \"partConfig\": { \"part2\": { \"npcEnabled\": false } }"));
        object eff = Freeze(snap, Scene());
        Assert.AreEqual(45f, Get<float>(eff, "Part1Duration"), "the JSON value wins");
        Assert.AreEqual("EXTERNAL_JSON", Entry(eff, "part1_duration_seconds", "Source"));
        Assert.AreEqual("false", Entry(eff, "part2_npc_enabled", "Value")); Assert.AreEqual("EXTERNAL_JSON", Entry(eff, "part2_npc_enabled", "Source"));
        Assert.AreEqual(60f, Get<float>(eff, "Part3TotalMaxSeconds"), "absent: the Scene value is captured");
        Assert.AreEqual("SCENE_INHERITED", Entry(eff, "part3_total_max_seconds", "Source"));
        Assert.AreEqual("SCENE_INHERITED", Entry(eff, "part2_gaze_capture_enabled", "Source"));
        Assert.AreEqual("SCENE_INHERITED", Entry(eff, "allow_wrong_candidate_selection", "Source"), "Scene-owned, never JSON-controlled");
        Assert.AreEqual("SCENE_INHERITED", Entry(eff, "spawner_max_agents", "Source"));
        Assert.AreEqual("DOCUMENTED_DEFAULT", Entry(eff, "auto_start", "Source"), "no autoStart key: the documented default false, not an Inspector value");
        Assert.AreEqual("EXTERNAL_JSON", Entry(Freeze(Ext(Json(extra: ", \"autoStart\": true")), Scene()), "auto_start", "Source"));
    }

    [Test]
    public void Effective_InternalAndExternal_ResolveTheSameValuesWhenEquivalent()
    {
        object a = Freeze(Int("P001", 3, 0, 1), Scene());
        object b = Freeze(Ext(Json()), Scene());
        foreach (object e in (System.Collections.IEnumerable)Get<object>(a, "Entries"))
        {
            string k = Get<string>(e, "Key");
            if (k == "auto_start") continue;   // Inspector autoStart=false here vs the JSON default false: same value, different source (checked above)
            Assert.AreEqual(Get<string>(e, "Value"), Entry(b, k, "Value"), k);
        }
    }

    [Test]
    public void Effective_IsWriteOnce_LaterSceneEditsAndSecondFreezesChangeNothing()
    {
        object snap = Int("P001", 1, 0, 0);
        object scene = Scene(p1: 15f);
        object eff = Freeze(snap, scene);
        Set(scene, "Part1Duration", 999f);                       // the live Scene edited after the freeze
        object again = Freeze(snap, Scene(p1: 1f, allowWrong: true));   // a second freeze with different Scene values
        Assert.AreSame(eff, again);
        Assert.AreEqual(15f, Get<float>(again, "Part1Duration")); Assert.AreEqual("false", Entry(again, "allow_wrong_candidate_selection", "Value"));
        foreach (PropertyInfo p in eff.GetType().GetProperties(Reflect.All))
            if (p.SetMethod != null) Assert.IsFalse(p.SetMethod.IsPublic, "EffectiveSettings." + p.Name + " has a public setter");
        Assert.IsInstanceOf(typeof(System.Collections.ObjectModel.ReadOnlyCollection<>).MakeGenericType(T("SettingEntry")), Get<object>(eff, "Entries"));
    }

    [Test]
    public void GroupNumber_EqualsGroupIndex_ZeroBased_NoPlusOne()
    {
        for (int i = 0; i < 12; i++)
        {
            object s = Ext(Json(part: "Part3", g3: i));
            Assert.AreEqual(i, Get<int>(s, "ActiveGroupIndex"), "Part 3 index " + i);
            string t = (string)Core.GetMethod("FormatSummary").Invoke(null, new object[] { s, null });
            StringAssert.Contains("Part 3 Group: " + i + "\n", t.Replace("\r", ""));
        }
        for (int i = 0; i < 2; i++) Assert.AreEqual(i, Get<int>(Ext(Json(part: "Part2", g2: i)), "ActiveGroupIndex"));
        Assert.AreEqual(-1, Get<int>(Ext(Json(part: "Part1")), "ActiveGroupIndex"), "Part 1 has no group: -1 (not 0)");
        StringAssert.Contains("0..11", Msg(Ext(Json(part: "Part3", g3: 12))));
        StringAssert.Contains("0..1", Msg(Ext(Json(part: "Part2", g2: 2))));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- diagnostics

    [Test]
    public void Summary_ReportsSourceFileAndResolvedValues_OrTheBlockingError()
    {
        object ok = Ext(Json(part: "Part3", g3: 1));
        string t = (string)Core.GetMethod("FormatSummary").Invoke(null, new object[] { ok, new[] { "Start Zone: GE", "D1 Zone: OE", "D2 Zone: GU" } });
        foreach (string need in new[] { "SESSION CONFIGURATION", "Source: External JSON", "File: /cfg/session_config.json", "Participant ID: P001", "Selected Part: Part3",
                                        "Part 3 Group: 1", "Start Zone: GE", "D1 Zone: OE", "D2 Zone: GU", "Status: VALID" })
            StringAssert.Contains(need, t);
        StringAssert.DoesNotContain("Part 2 Group", t, "an inactive group is not reported as a condition");
        StringAssert.DoesNotContain("Group Number", t, "Group Number == Group Index: there is only one number, and it is zero-based");

        object p2 = Int("P001", 2, 0, 5);
        string t2 = (string)Core.GetMethod("FormatSummary").Invoke(null, new object[] { p2, null });
        StringAssert.Contains("Source: Internal / Inspector", t2); StringAssert.Contains("Part 2 Group: 0", t2); StringAssert.DoesNotContain("Part 3 Group", t2);

        object bad = Ext(Json(g3: 12));
        string t3 = (string)Core.GetMethod("FormatSummary").Invoke(null, new object[] { bad, null });
        StringAssert.Contains("Status: INVALID", t3); StringAssert.Contains("BLOCKED", t3); StringAssert.Contains("part3GroupIndex", t3); StringAssert.Contains("/cfg/session_config.json", t3);
    }

    [Test]
    public void SampleFiles_InTheRepository_AreValidAgainstTheSchema()
    {
        string dir = FindDocsDir();
        if (dir == null) Assert.Ignore("Documentation/Configuration not found from the test working directory.");
        foreach (string f in Directory.GetFiles(dir, "session_config*.json"))
        {
            object s = Ext(File.ReadAllText(f));
            Assert.IsTrue(Valid(s), Path.GetFileName(f) + ": " + Msg(s));
        }
    }

    static string FindDocsDir()
    {
        string d = Directory.GetCurrentDirectory();
        for (int i = 0; i < 6 && d != null; i++, d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "Documentation", "Configuration");
            if (Directory.Exists(c)) return c;
        }
        return null;
    }
}

// Needs UnityEngine: runs in the Unity Test Runner only.
public class SessionConfigurationComponentTests
{
    static readonly Type ScType = T("ExternalSessionController");
    static readonly Type PartType = ScType.GetNestedType("ExperimentPart");
    UnityEngine.GameObject _go;
    string _dir;

    [SetUp] public void SetUp() { _dir = Path.Combine(Path.GetTempPath(), "sc_comp_" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(_dir); }
    [TearDown] public void TearDown() { if (_go != null) UnityEngine.Object.DestroyImmediate(_go); if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    string Write(string json) { string p = Path.Combine(_dir, "session_config.json"); File.WriteAllText(p, json); return p; }

    // Configured like a serialized scene object: fields set while the object is inactive, then activated (Awake resolves).
    object Make(bool useExternal, string pid, string part, int g2, int g3, string path = null)
    {
        _go = new UnityEngine.GameObject("SC"); _go.SetActive(false);
        object sc = _go.AddComponent(ScType);
        Set(sc, "participantId", pid); Set(sc, "selectedPart", Enum.Parse(PartType, part)); Set(sc, "part2GroupIndex", g2); Set(sc, "part3GroupIndex", g3);
        Set(sc, "useExternalJson", useExternal);
        if (path != null) ScType.GetProperty("ExternalFilePathOverride").SetValue(sc, path);
        _go.SetActive(true);
        return sc;
    }

    static string Pid(object sc) => Get<string>(sc, "ActiveParticipantId");
    static string PartOf(object sc) => Get<object>(sc, "ActivePart").ToString();

    [Test]
    public void InternalMode_ReadsInspectorOnly_EvenWhenAConflictingExternalFileExists()
    {
        string p = Write("{ \"participantId\": \"P999\", \"selectedPart\": \"Part3\", \"part2GroupIndex\": 1, \"part3GroupIndex\": 9 }");
        object sc = Make(false, "P001", "Part2", 0, 1, p);
        Assert.AreEqual("P001", Pid(sc)); Assert.AreEqual("Part2", PartOf(sc));
        Assert.AreEqual(0, Get<int>(sc, "ActivePart2GroupIndex")); Assert.AreEqual(1, Get<int>(sc, "ActivePart3GroupIndex"));
        Assert.AreEqual("inspector", Get<string>(sc, "ConfigSource"));
        Assert.IsTrue(Get<bool>(Call(sc, "ValidateIdentity"), "IsValid"));
    }

    [Test]
    public void ExternalMode_ReadsJsonOnly_InspectorValuesAreIgnored()
    {
        string p = Write("{ \"schemaVersion\": 1, \"participantId\": \"P061\", \"selectedPart\": \"Part3\", \"part2GroupIndex\": 1, \"part3GroupIndex\": 9, \"autoStart\": true }");
        object sc = Make(true, "P001", "Part2", 0, 1, p);
        Assert.AreEqual("P061", Pid(sc)); Assert.AreEqual("Part3", PartOf(sc));
        Assert.AreEqual(1, Get<int>(sc, "ActivePart2GroupIndex")); Assert.AreEqual(9, Get<int>(sc, "ActivePart3GroupIndex")); Assert.IsTrue(Get<bool>(sc, "ActiveAutoStart"));
        Assert.AreEqual("external_json", Get<string>(sc, "ConfigSource")); Assert.AreEqual(16, Get<string>(sc, "ConfigHash").Length);
    }

    [Test]
    public void ExternalMode_InvalidOrMissingFile_BlocksAndNeverFallsBackToTheInspector()
    {
        UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new Regex("Session configuration rejected"));
        object missing = Make(true, "P001", "Part2", 0, 1, Path.Combine(_dir, "nope.json"));
        Assert.IsFalse(Get<bool>(Call(missing, "ValidateIdentity"), "IsValid")); Assert.AreEqual("", Pid(missing));
        StringAssert.Contains("nope.json", Get<string>(missing, "ConfigError"));
        Assert.IsFalse(Get<bool>(missing, "ActiveAutoStart"));
        UnityEngine.Object.DestroyImmediate(_go);

        string p = Write("{ \"participantId\": \"P002\", \"selectedPart\": \"Part3\", \"part2GroupIndex\": 0, \"part3GroupIndex\": 12 }");
        UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new Regex("Session configuration rejected"));
        object bad = Make(true, "P001", "Part2", 0, 1, p);
        Assert.IsFalse(Get<bool>(Call(bad, "ValidateIdentity"), "IsValid")); Assert.AreEqual("", Pid(bad));
        Assert.AreEqual("P001", Get<string>(bad, "participantId"), "the Inspector value is neither used nor modified");
        StringAssert.Contains("part3GroupIndex", Get<string>(bad, "ConfigError"));
    }

    [Test]
    public void ResolvedConfiguration_IsFrozen_InspectorAndFileEditsDoNotChangeTheRunningSession()
    {
        string p = Write("{ \"participantId\": \"P020\", \"selectedPart\": \"Part2\", \"part2GroupIndex\": 1, \"part3GroupIndex\": 0 }");
        object ext = Make(true, "P001", "Part1", 0, 0, p);
        Write("{ \"participantId\": \"P021\", \"selectedPart\": \"Part3\", \"part2GroupIndex\": 0, \"part3GroupIndex\": 3 }");
        Set(ext, "participantId", "HACK"); Set(ext, "part3GroupIndex", 4);
        Assert.AreEqual("P020", Pid(ext)); Assert.AreEqual("Part2", PartOf(ext)); Assert.AreEqual(1, Get<int>(ext, "ActivePart2GroupIndex"));
        UnityEngine.Object.DestroyImmediate(_go);

        object inspector = Make(false, "P030", "Part3", 0, 2);
        Set(inspector, "participantId", "P031"); Set(inspector, "selectedPart", Enum.Parse(PartType, "Part1")); Set(inspector, "part3GroupIndex", 8);
        Assert.AreEqual("P030", Pid(inspector)); Assert.AreEqual("Part3", PartOf(inspector)); Assert.AreEqual(2, Get<int>(inspector, "ActivePart3GroupIndex"));
        Assert.AreEqual("P030", Get<object>(Call(inspector, "ValidateIdentity"), "participantId"), "ValidateIdentity reports the frozen value");
    }

    static readonly Type EmType = T("ExperimentManager");

    // ESC + EM in either activation order. Both are created inactive, configured, then activated (like a serialized scene).
    object[] MakeSession(bool escFirst, string pid = "P001", string part = "Part1")
    {
        var scGo = new UnityEngine.GameObject("SC"); scGo.SetActive(false);
        object sc = scGo.AddComponent(ScType);
        Set(sc, "participantId", pid); Set(sc, "selectedPart", Enum.Parse(PartType, part));
        _go = scGo;
        var emGo = new UnityEngine.GameObject("EM"); emGo.SetActive(false);
        object em = emGo.AddComponent(EmType);
        Set(em, "sessionController", sc);
        Set(em, "part1Duration", 33f); Set(em, "npcWarmupDuration", 7f); Set(em, "allowWrongCandidateSelection", true);
        _extra.Add(emGo);
        if (escFirst) { scGo.SetActive(true); emGo.SetActive(true); } else { emGo.SetActive(true); scGo.SetActive(true); }
        return new[] { sc, em };
    }
    readonly System.Collections.Generic.List<UnityEngine.GameObject> _extra = new System.Collections.Generic.List<UnityEngine.GameObject>();
    [TearDown] public void TearDownExtra() { foreach (var g in _extra) if (g != null) UnityEngine.Object.DestroyImmediate(g); _extra.Clear(); }

    [Test]
    public void Controller_IsPendingUntilInitialized_ThenFinal_AndInitializeIsIdempotent()
    {
        _go = new UnityEngine.GameObject("SC"); _go.SetActive(false);
        object sc = _go.AddComponent(ScType);
        Set(sc, "participantId", "P001"); Set(sc, "selectedPart", Enum.Parse(PartType, "Part2"));
        Assert.AreEqual("Pending", Get<object>(sc, "State").ToString(), "reading State never resolves");
        Assert.AreEqual("Valid", Call(sc, "Initialize").ToString());
        Set(sc, "participantId", "CHANGED"); Set(sc, "part2GroupIndex", 1);
        Assert.AreEqual("Valid", Call(sc, "Initialize").ToString());
        Assert.AreEqual("P001", Pid(sc)); Assert.AreEqual(0, Get<int>(sc, "ActivePart2GroupIndex"));
    }

    [Test]
    public void ExperimentManager_FreezesTheEffectiveSettingsOnce_InEitherActivationOrder()
    {
        foreach (bool escFirst in new[] { true, false })
        {
            object[] a = MakeSession(escFirst);
            object em = a[1];
            Assert.IsTrue((bool)Call(em, "EnsureSessionOverridesApplied"), "escFirst=" + escFirst);
            Assert.IsTrue((bool)Call(em, "EnsureSessionOverridesApplied"), "idempotent");
            object eff = Get<object>(em, "Effective");
            Assert.AreEqual(33f, Get<float>(eff, "Part1Duration")); Assert.AreEqual(7f, Get<float>(eff, "NpcWarmupDuration")); Assert.IsTrue(Get<bool>(eff, "AllowWrongCandidateSelection"));
            // the Scene fields edited AFTER the freeze, and the raw Part config object, no longer reach the running session
            Set(em, "part1Duration", 1f); Set(em, "allowWrongCandidateSelection", false);
            object liveCfg = Get<object>(em, "part1Config"); Set(liveCfg, "npcEnabled", true);
            Assert.AreEqual(33f, Get<float>(Get<object>(em, "Effective"), "Part1Duration"));
            object frozen = Call(em, "GetEffectivePartConfig", 1);
            Assert.AreNotSame(liveCfg, frozen); Assert.IsFalse(Get<bool>(frozen, "npcEnabled"), "frozen copy of the Scene value at freeze time");
            Assert.AreSame(Get<object>(Get<object>(a[0], "Resolved"), "Effective"), eff, "the controller's snapshot holds the same frozen settings");
            TearDownExtra(); UnityEngine.Object.DestroyImmediate(_go); _go = null;
        }
    }

    [Test]
    public void ExperimentManager_FailsClosed_WithoutAController_AndWithAnInvalidConfiguration()
    {
        var emGo = new UnityEngine.GameObject("EM"); _extra.Add(emGo);
        object em = emGo.AddComponent(EmType);
        UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new Regex("No ExternalSessionController"));
        Assert.IsFalse((bool)Call(em, "EnsureSessionOverridesApplied"));
        Assert.IsNull(Get<object>(em, "Effective"));
        UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new Regex("StartSelectedPart refused - no ExternalSessionController"));
        Call(em, "StartSelectedPart");
        Assert.AreEqual("", Get<string>(em, "RunningPartName"), "no Part started");
        TearDownExtra();

        UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new Regex("Session configuration rejected"));
        object[] a = MakeSession(true, pid: "P000");
        Assert.IsFalse((bool)Call(a[1], "EnsureSessionOverridesApplied"));
        Assert.IsNull(Get<object>(a[1], "Effective")); Assert.IsNull(Call(a[1], "GetEffectivePartConfig", 1));
        Assert.IsNull(Get<object>(Get<object>(a[0], "Resolved"), "Effective"));
    }

    [Test]
    public void Part3GroupTable_MatchesTheDocumentedMapping()
    {
        Type em = T("ExperimentManager");
        Assert.AreEqual(12, GetStatic<int>(em, "Part3GroupCount"));
        string[] expect = { "GE,GU,OE", "GE,OE,GU", "GE,OU,GE", "GU,GE,OU", "GU,OE,GU", "GU,OU,GE", "OE,GE,OU", "OE,GU,OE", "OE,OU,GE", "OU,GE,OU", "OU,GU,OE", "OU,OE,GU" };
        for (int i = 0; i < 12; i++)
        {
            object[] a = { i, null, null, null };
            Assert.IsTrue((bool)em.GetMethod("TryGetPart3GroupZones").Invoke(null, a));
            Assert.AreEqual(expect[i], a[1] + "," + a[2] + "," + a[3], "index " + i);
        }
        Assert.IsFalse((bool)em.GetMethod("TryGetPart3GroupZones").Invoke(null, new object[] { 12, null, null, null }));
    }
}
