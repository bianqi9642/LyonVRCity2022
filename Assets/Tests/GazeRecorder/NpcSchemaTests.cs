// NpcSchemaTests.cs
// PlayMode tests for the schema-v3 NPC recording work: (1) npc_dest_change / retired npc_leg_start semantics, (2) UncertainBehavior state in npc_state.csv and
// transitions in npc_events.csv, (3) the realised AgentProfile appearance in npc_lifetimes.csv (+ npc_profiles.csv), (4) a storage-layout measurement.
// Same approach as NpcLifecycleTests: production classes through reflection on a runtime NavMesh, real files in a unique temp folder. Editor tests - not a Quest test.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    // ------------------------------------------------------------------ helpers

    static object CallTyped(object o, string name, Type[] types, params object[] args)
    {
        var m = o.GetType().GetMethod(name, All, null, types, null);
        Assert.IsNotNull(m, "method not found: " + o.GetType().Name + "." + name);
        return m.Invoke(o, args);
    }

    static List<List<string>> Rows(Csv c, string col, string value) => new List<List<string>>(c.Where(col, value));

    // npc_events rows of one lifetime, in file order
    static List<List<string>> LifeEvents(Csv nev, string lifetimeId)
    {
        var l = new List<List<string>>();
        foreach (var r in nev.Rows) if (nev.Get(r, "npc_spawn_instance_id") == lifetimeId) l.Add(r);
        return l;
    }

    static string ValueField(string valueStr, string key)
    {
        foreach (var kv in valueStr.Split(';'))
        {
            int i = kv.IndexOf('=');
            if (i > 0 && kv.Substring(0, i) == key) return kv.Substring(i + 1);
        }
        return null;
    }

    static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    // last npc_state row of a lifetime (highest sample_seq), or null
    static List<string> LastStateRow(Csv st, string lifetimeId)
    {
        List<string> best = null; int bestSeq = -1;
        foreach (var r in st.Rows)
            if (st.Get(r, "npc_spawn_instance_id") == lifetimeId) { int q = int.Parse(st.Get(r, "sample_seq")); if (q > bestSeq) { bestSeq = q; best = r; } }
        return best;
    }

    // ====================================================================================== dest_change / leg_start

    [UnityTest]
    public IEnumerator DestChange_OnItineraryRun_IsOneRowPerChange_NoLegStart_AndFollowsTheExactTargetSetEvent()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { compact = true, attractor = true, prewarm = 1, maxAgents = 1, npcSpeed = 1f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        string id = Lid(ActiveAgents(rig)[0]);
        yield return WaitUntil(() => File.Exists(rig.File("npc_events.csv")) && Rows(NpcEvents(rig), "event_type", "npc_itinerary_complete").Count > 0, 40f, "itinerary to complete");
        yield return new WaitForSecondsRealtime(0.3f);

        var nev = NpcEvents(rig);
        AssertWidths(nev, "npc_events.csv");
        var life = LifeEvents(nev, id);
        var types = new List<string>(); foreach (var r in life) types.Add(nev.Get(r, "event_type"));
        UnityEngine.Debug.Log("[NpcSchemaTests][itinerary] " + string.Join(" > ", types));

        Assert.AreEqual(0, types.FindAll(t => t == "npc_leg_start").Count, "npc_leg_start is retired: it duplicated npc_dest_change");
        var dest = life.FindAll(r => nev.Get(r, "event_type") == "npc_dest_change");
        var target = life.FindAll(r => nev.Get(r, "event_type") == "npc_target_set");
        Assert.AreEqual(1, types.FindAll(t => t == "npc_movement_start").Count, "the first movement starts once per lifetime");
        Assert.GreaterOrEqual(target.Count, 2, "attractor leg and exit leg were issued");
        Assert.GreaterOrEqual(dest.Count, 2, "and each was observed as a destination change");
        Assert.LessOrEqual(dest.Count, target.Count, "an observed destination change needs a cause: never more rows than legs issued");

        // no two rows with the same (time, type) for one lifetime
        var seen = new HashSet<string>();
        foreach (var r in life) Assert.IsTrue(seen.Add(nev.Get(r, "t_abs") + "|" + nev.Get(r, "event_type")), "duplicate event row " + nev.Get(r, "event_type") + " @" + nev.Get(r, "t_abs"));

        // each dest_change: itinerary cause, a real jump, a preceding npc_target_set for the same target (exact time <= observation time)
        double prevX = double.NaN, prevZ = double.NaN;
        foreach (var r in dest)
        {
            string value = nev.Get(r, "value_str");
            Assert.AreEqual("itinerary_target", ValueField(value, "cause"), value);
            Assert.Greater(D(ValueField(value, "jump_m")), 0.5, "a destination change is a jump of more than 0.5 m");
            double x = D(nev.Get(r, "dest_x")), z = D(nev.Get(r, "dest_z"));
            if (!double.IsNaN(prevX)) Assert.Greater(Math.Sqrt((x - prevX) * (x - prevX) + (z - prevZ) * (z - prevZ)), 0.5, "consecutive rows have different destinations");
            prevX = x; prevZ = z;
            string tid = nev.Get(r, "target_id");
            bool matched = target.Exists(t => nev.Get(t, "target_id") == tid && D(nev.Get(t, "t_abs")) <= D(nev.Get(r, "t_abs")) + 1e-6);
            Assert.IsTrue(matched, "npc_target_set for " + tid + " exists at or before the observation");
        }
        // the itinerary event order is spawn < movement_start < target_set ... < itinerary_complete
        Assert.Less(types.IndexOf("npc_spawn"), types.IndexOf("npc_movement_start"));
        Assert.Less(types.IndexOf("npc_movement_start"), types.IndexOf("npc_itinerary_complete"));
    }

    [UnityTest]
    public IEnumerator DestChange_Part2Wandering_FollowsTheSameRules()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { part2 = true, compact = true, prewarm = 1, maxAgents = 1, npcSpeed = 6f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        string id = Lid(ActiveAgents(rig)[0]);
        yield return WaitUntil(() => File.Exists(rig.File("npc_events.csv")) && Rows(NpcEvents(rig), "event_type", "npc_itinerary_complete").Count > 0, 40f, "wander itinerary to complete");
        yield return new WaitForSecondsRealtime(0.3f);
        var nev = NpcEvents(rig); var life = LifeEvents(nev, id);
        var seen = new HashSet<string>();
        int dest = 0;
        foreach (var r in life)
        {
            Assert.AreNotEqual("npc_leg_start", nev.Get(r, "event_type"));
            Assert.IsTrue(seen.Add(nev.Get(r, "t_abs") + "|" + nev.Get(r, "event_type")), "duplicate " + nev.Get(r, "event_type"));
            if (nev.Get(r, "event_type") == "npc_dest_change") { dest++; StringAssert.StartsWith("cause=", nev.Get(r, "value_str")); }
        }
        Assert.GreaterOrEqual(dest, 1);
    }

    [UnityTest]
    public IEnumerator DestChange_UnchangedOrRepeatedDestination_WritesNothing_AndOneGenuineChangeWritesOneRow()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { compact = true, attractor = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        Component pa = ActiveAgents(rig)[0];
        string id = Lid(pa);
        var mt = pa.GetComponent(MoveType);
        var nav = pa.GetComponent<NavMeshAgent>();
        // let the first leg get observed
        yield return WaitUntil(() => File.Exists(rig.File("npc_events.csv")) && LifeEvents(NpcEvents(rig), id).Exists(r => NpcEvents(rig).Get(r, "event_type") == "npc_dest_change"), 10f, "first destination change");
        int Count(string type) { var n = NpcEvents(rig); return LifeEvents(n, id).FindAll(r => n.Get(r, "event_type") == type).Count; }
        int before = Count("npc_dest_change");

        // repeated callbacks / retries towards the SAME destination
        Vector3 same = nav.destination;
        for (int i = 0; i < 5; i++) { CallTyped(mt, "MoveTo", new[] { typeof(Vector3) }, same); yield return null; }
        yield return new WaitForSecondsRealtime(0.45f);
        Assert.AreEqual(before, Count("npc_dest_change"), "re-issuing the same destination is not a destination change");
        Assert.AreEqual(0, Count("npc_leg_start"));

        // one genuine change to a far, different point
        Vector3 far = new Vector3(20f, 0.1f, 20f);
        CallTyped(mt, "MoveTo", new[] { typeof(Vector3) }, far);
        yield return new WaitForSecondsRealtime(0.45f);
        var n2 = NpcEvents(rig);
        var rows = LifeEvents(n2, id).FindAll(r => n2.Get(r, "event_type") == "npc_dest_change");
        Assert.AreEqual(before + 1, rows.Count, "exactly one new row for one change");
        var last = rows[rows.Count - 1];
        Assert.AreEqual("other", ValueField(n2.Get(last, "value_str"), "cause"), "not the itinerary target, not a detour");
        Assert.Less(Math.Abs(D(n2.Get(last, "dest_x")) - far.x), 1.5);
    }

    // ====================================================================================== uncertain behaviour

    [UnityTest]
    public IEnumerator Uncertain_StateColumnsFollowTheProductionStateMachine_AndEveryTransitionIsOneEvent()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { withUncertain = true, uncertainEnabled = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.2f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        Component pa = ActiveAgents(rig)[0];
        string id = Lid(pa);
        var ub = pa.GetComponent(T("UncertainBehavior"));
        yield return new WaitForSecondsRealtime(0.4f);
        double tForce = Time.realtimeSinceStartupAsDouble;   // earlier transitions (e.g. the production suspend while the NPC arrives at its entrance) are not part of this scripted sequence

        string StateNow() { Call(rig.dcm, "SampleNpcs"); var st = NpcState(rig); var r = LastStateRow(st, id); return st.Get(r, "uncertain_state"); }
        List<string> Row() { var st = NpcState(rig); return LastStateRow(st, id); }

        Assert.AreEqual("walking", StateNow());
        Assert.AreEqual("1", NpcState(rig).Get(Row(), "uncertain_enabled"));
        Assert.AreEqual("", NpcState(rig).Get(Row(), "uncertain_look_phase"), "look phase only exists inside Hesitate");

        Call(ub, "TriggerPauseNow", 30f);
        Assert.AreEqual("pause", StateNow(), "the sample taken right after entering Pause carries Pause");

        Call(ub, "TriggerHesitateNow", 30f);
        Assert.AreEqual("hesitate", StateNow());
        StringAssert.IsMatch("^(idle|turning|holding|returning)$", NpcState(rig).Get(Row(), "uncertain_look_phase"));

        Set(ub, "stateTimer", 0f);          // let the production Update() expire the state by its own timer
        yield return null; yield return null;
        Assert.AreEqual("walking", StateNow());

        Call(ub, "NotifyMainTargetArrived");   // arrival at the main waypoint parks the uncertain logic
        Assert.AreEqual("suspended_arrived", StateNow());
        Call(ub, "ResumeAfterArrival");
        Assert.AreEqual("walking", StateNow());

        // events: exactly one row per label change, in order, none duplicated
        yield return null;   // npc_events rows are flushed once per frame (LateUpdate)
        var nev = NpcEvents(rig);
        var tr = LifeEvents(nev, id).FindAll(r => nev.Get(r, "event_type") == "npc_uncertain_state" && D(nev.Get(r, "t_abs")) >= tForce - 1e-3);
        var seq = new List<string>();
        foreach (var r in tr) seq.Add(ValueField(nev.Get(r, "value_str"), "from") + ">" + ValueField(nev.Get(r, "value_str"), "to"));
        CollectionAssert.AreEqual(new[] { "walking>pause", "pause>hesitate", "hesitate>walking", "walking>suspended_arrived", "suspended_arrived>walking" }, seq);
        foreach (var r in tr) StringAssert.IsMatch(@"^from=[a-z_]+;to=[a-z_]+;planned_s=\d+\.\d{2}$", nev.Get(r, "value_str"));
        AssertWidths(NpcState(rig), "npc_state.csv"); AssertWidths(nev, "npc_events.csv");
    }

    [UnityTest]
    public IEnumerator Uncertain_Detour_IsRecordedAsDetourState_AndItsDestinationChangeIsAttributed()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { withUncertain = true, uncertainEnabled = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.5f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        Component pa = ActiveAgents(rig)[0];
        string id = Lid(pa);
        var ub = pa.GetComponent(T("UncertainBehavior"));
        Set(ub, "detourDuration", 30f);   // keep the detour open long enough to be sampled
        yield return new WaitForSecondsRealtime(1.0f);
        var n0 = NpcEvents(rig);
        int before = LifeEvents(n0, id).FindAll(r => n0.Get(r, "event_type") == "npc_dest_change").Count;

        var stBefore = NpcState(rig);
        string idxBefore = stBefore.Get(LastStateRow(stBefore, id), "itinerary_index");
        Call(ub, "EnterDetour");
        Assert.AreEqual("detour", Get<string>(ub, "StateLabel"), "precondition: the production code accepted a detour point");
        yield return new WaitForSecondsRealtime(0.5f);

        var st = NpcState(rig);
        var row = LastStateRow(st, id);
        Assert.AreEqual("detour", st.Get(row, "uncertain_state"));
        var nev = NpcEvents(rig);
        var dest = LifeEvents(nev, id).FindAll(r => nev.Get(r, "event_type") == "npc_dest_change");
        Assert.Greater(dest.Count, before, "the detour moved the NavMeshAgent destination");
        Assert.AreEqual("detour", ValueField(nev.Get(dest[dest.Count - 1], "value_str"), "cause"));
        Assert.IsTrue(LifeEvents(nev, id).Exists(r => nev.Get(r, "event_type") == "npc_uncertain_state" && ValueField(nev.Get(r, "value_str"), "to") == "detour"));
        // uncertainty acts on execution and on the short-term route, never on the itinerary: the itinerary index is not touched by a detour
        Assert.AreEqual(idxBefore, st.Get(row, "itinerary_index"), "a detour does not move the itinerary index");
    }

    [UnityTest]
    public IEnumerator DirectNpc_IsIdentified_AndPoolReuse_NeverInheritsAnUncertainState()
    {
        // (a) the component exists but is OFF: configured direct mover -> uncertain_enabled=0, state blank
        Rig rig = null;
        yield return BuildRig(new RigOptions { withUncertain = true, uncertainEnabled = false, prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        yield return new WaitForSecondsRealtime(0.4f);
        var st = NpcState(rig);
        Assert.Greater(st.Rows.Count, 0);
        foreach (var r in st.Rows) { Assert.AreEqual("0", st.Get(r, "uncertain_enabled")); Assert.AreEqual("", st.Get(r, "uncertain_state")); Assert.AreEqual("", st.Get(r, "uncertain_look_phase")); }
        TearDownRigObjects(); yield return null; yield return null;

        // (b) no UncertainBehavior at all -> blank (not applicable), never "0" or a state
        Rig rig2 = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig2 = r);
        yield return WaitUntil(() => Live(rig2) == 1, 10f, "an NPC");
        yield return new WaitForSecondsRealtime(0.4f);
        var st2 = NpcState(rig2);
        foreach (var r in st2.Rows) { Assert.AreEqual("", st2.Get(r, "uncertain_enabled")); Assert.AreEqual("", st2.Get(r, "uncertain_state")); }
        TearDownRigObjects(); yield return null; yield return null;

        // (c) pool reuse: lifetime 1 ends while paused; lifetime 2 starts clean and the stale state never reaches the file
        Rig rig3 = null;
        yield return BuildRig(new RigOptions { withUncertain = true, uncertainEnabled = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig3 = r);
        yield return WaitUntil(() => Live(rig3) == 1, 10f, "an NPC");
        Component pa = ActiveAgents(rig3)[0];
        string s1 = Lid(pa);
        var ub = pa.GetComponent(T("UncertainBehavior"));
        yield return new WaitForSecondsRealtime(0.3f);
        Call(ub, "TriggerPauseNow", 30f);
        Call(rig3.dcm, "SampleNpcs");
        Assert.AreEqual("pause", NpcState(rig3).Get(LastStateRow(NpcState(rig3), s1), "uncertain_state"));
        Call(pa, "ForceFinish");
        yield return WaitUntil(() => Live(rig3) == 1, 10f, "respawn of the same object");
        pa = ActiveAgents(rig3)[0];
        string s2 = Lid(pa);
        Assert.AreNotEqual(s1, s2);
        Assert.AreEqual("walking", Get<string>(pa.GetComponent(T("UncertainBehavior")), "StateLabel"), "ResetForReuse cleared the previous life's state");
        yield return new WaitForSecondsRealtime(0.4f);
        var st3 = NpcState(rig3);
        bool any = false;
        foreach (var r in st3.Rows)
            if (st3.Get(r, "npc_spawn_instance_id") == s2) { any = true; Assert.AreEqual("walking", st3.Get(r, "uncertain_state"), "no stale 'pause' in the new lifetime"); }
        Assert.IsTrue(any);
        // and the reset itself is a lifetime boundary, not a transition event of the new lifetime
        var nev = NpcEvents(rig3);
        foreach (var r in LifeEvents(nev, s2).FindAll(r => nev.Get(r, "event_type") == "npc_uncertain_state"))
        {
            StringAssert.DoesNotContain("pause", nev.Get(r, "value_str"), "no transition of the new lifetime mentions the previous lifetime's Pause");
            StringAssert.DoesNotContain("=;", nev.Get(r, "value_str"));
        }
        Assert.AreEqual(0, LifeEvents(nev, s1).FindAll(r => nev.Get(r, "event_type") == "npc_uncertain_state" && D(nev.Get(r, "t_abs")) > double.Parse(nev.Get(Rows(nev, "npc_spawn_instance_id", s2)[0], "t_abs"), CultureInfo.InvariantCulture)).Count,
            "nothing is attributed to lifetime 1 after lifetime 2 began");
    }

    // ====================================================================================== applied profile / appearance

    static UnityEngine.Object MakeProfile(string name, float femaleProbability, bool forceUncertain, params object[] unused)
    {
        var p = ScriptableObject.CreateInstance(T("AgentProfile"));
        p.name = name;
        Set(p, "speed", 3f); Set(p, "femaleProbability", femaleProbability); Set(p, "forceUncertain", forceUncertain); Set(p, "uncertainProbability", 0f);
        return p;
    }

    static Type OptType => Type.GetType("AgentProfile+PartOption, Assembly-CSharp");

    static object MakeOption(Mesh mesh, Material[] mats, float weight, string restriction)
    {
        var o = Activator.CreateInstance(OptType);
        Set(o, "mesh", mesh); Set(o, "materials", mats); Set(o, "weight", weight);
        var f = OptType.GetField("genderRestriction");
        f.SetValue(o, Enum.Parse(f.FieldType, restriction));
        return o;
    }

    static void SetOptions(UnityEngine.Object profile, string field, params object[] options)
    {
        var arr = Array.CreateInstance(OptType, options.Length);
        for (int i = 0; i < options.Length; i++) arr.SetValue(options[i], i);
        Set(profile, field, arr);
    }

    Material Mat(string name)
    {
        var sh = Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader");
        if (sh == null) Assert.Ignore("no shader available to build test materials in this player");
        var m = new Material(sh) { name = name };
        _spawned.Add(m);
        return m;
    }

    Mesh MeshNamed(string name) { var m = new Mesh { name = name }; _spawned.Add(m); return m; }

    static string Stripped(Material m) { string n = m.name; return n.EndsWith(" (Instance)", StringComparison.Ordinal) ? n.Substring(0, n.Length - 11) : n; }

    [UnityTest]
    public IEnumerator AppliedProfile_IsRecordedAsRealised_DiffersByProfile_AndSurvivesPoolReuse()
    {
        var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");   // decimal comma: the numeric columns must not change
        try
        {
            var profA = MakeProfile("ProfileA", 1f, true);
            SetOptions(profA, "hairOptions", MakeOption(MeshNamed("HairA"), new[] { Mat("MatHairA") }, 1f, "FemaleOnly"));
            SetOptions(profA, "topOptions", MakeOption(MeshNamed("TopA"), new[] { Mat("TopA1"), Mat("TopA2"), Mat("TopA3") }, 1f, "Unisex"));
            var profB = MakeProfile("ProfileB, with \"quotes\"", 0f, false);   // CSV-hostile name on purpose
            SetOptions(profB, "hairOptions", MakeOption(MeshNamed("HairB_male"), new[] { Mat("MatHairB") }, 1f, "MaleOnly"),
                                             MakeOption(MeshNamed("HairB_female"), new[] { Mat("MatHairB") }, 1f, "FemaleOnly"));
            SetOptions(profB, "topOptions", MakeOption(MeshNamed("TopB"), new[] { Mat("TopB1") }, 1f, "Unisex"));
            _spawned.Add(profA); _spawned.Add(profB);

            Rig rig = null;
            yield return BuildRig(new RigOptions { withAppearance = true, withUncertain = true, uncertainEnabled = false, profile = profA, prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
            yield return WaitUntil(() => Live(rig) == 1, 10f, "lifetime 1");
            Component pa = ActiveAgents(rig)[0];
            string s1 = Lid(pa);
            var ac = pa.GetComponent(T("AppearanceController"));
            var hairR = Get<SkinnedMeshRenderer>(ac, "hairRenderer"); var topR = Get<SkinnedMeshRenderer>(ac, "topRenderer");
            string liveTop1 = Stripped(topR.sharedMaterials[0]);
            Assert.AreEqual("HairA", hairR.sharedMesh.name);
            yield return new WaitForSecondsRealtime(0.3f);

            var lt = Lifetimes(rig);
            AssertWidths(lt, "npc_lifetimes.csv");
            var r1 = Rows(lt, "npc_spawn_instance_id", s1)[0];
            string rowText1 = string.Join("\u0001", r1);
            Assert.AreEqual("ProfileA", lt.Get(r1, "profile_name"));
            Assert.AreEqual("3.0000", lt.Get(r1, "configured_speed")); Assert.AreEqual("3.0000", lt.Get(r1, "profile_base_speed")); Assert.AreEqual("1.0000", lt.Get(r1, "speed_multiplier"));
            Assert.AreEqual("Female", lt.Get(r1, "gender"));
            Assert.AreEqual("1", lt.Get(r1, "uncertain_behavior_enabled"));
            Assert.AreEqual("1", lt.Get(r1, "profile_force_uncertain"));
            Assert.AreEqual("1.0000", lt.Get(r1, "profile_female_probability"), "culture-independent numbers");
            StringAssert.Contains("pause_p_s=0", lt.Get(r1, "uncertain_config"));
            Assert.AreEqual("0", lt.Get(r1, "app_hair_option")); Assert.AreEqual("HairA", lt.Get(r1, "app_hair_mesh")); Assert.AreEqual("MatHairA", lt.Get(r1, "app_hair_materials"));
            Assert.AreEqual("0", lt.Get(r1, "app_top_option")); Assert.AreEqual("TopA", lt.Get(r1, "app_top_mesh"));
            Assert.AreEqual(liveTop1, lt.Get(r1, "app_top_materials"), "the randomly drawn material is recorded as realised (it matches what the renderer really shows)");
            CollectionAssert.Contains(new[] { "TopA1", "TopA2", "TopA3" }, lt.Get(r1, "app_top_materials"));
            foreach (string part in new[] { "body", "bottom", "shoes", "accessories" })
            { Assert.AreEqual("", lt.Get(r1, "app_" + part + "_option")); Assert.AreEqual("", lt.Get(r1, "app_" + part + "_mesh")); Assert.AreEqual("", lt.Get(r1, "app_" + part + "_materials")); }
            string hash1 = lt.Get(r1, "profile_content_hash"), look1 = lt.Get(r1, "appearance_hash");
            Assert.AreEqual(16, hash1.Length); Assert.AreEqual(16, look1.Length);

            object applied1 = Get<object>(ac, "LastApplied");

            // pool reuse of the SAME object under another profile
            Set(rig.spawner, "defaultProfile", profB);
            Call(pa, "ForceFinish");
            yield return WaitUntil(() => Live(rig) == 1, 10f, "lifetime 2");
            pa = ActiveAgents(rig)[0];
            string s2 = Lid(pa);
            Assert.AreEqual(Aid(pa) + "-S2", s2);
            Assert.AreEqual("HairB_male", hairR.sharedMesh.name, "the object really was re-skinned");
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.AreNotSame(applied1, Get<object>(ac, "LastApplied"), "a re-skin publishes a NEW record; the earlier lifetime's record is never mutated");
            Assert.AreEqual("HairA", Get<string>(Get<object>(applied1, "hair"), "MeshName"), "the old lifetime's frozen record still describes HairA");

            lt = Lifetimes(rig);
            AssertWidths(lt, "npc_lifetimes.csv");
            var r1b = Rows(lt, "npc_spawn_instance_id", s1)[0];
            Assert.AreEqual(rowText1, string.Join("\u0001", r1b), "the record of lifetime 1 is untouched by the re-skin");
            var r2 = Rows(lt, "npc_spawn_instance_id", s2)[0];
            Assert.AreEqual("ProfileB  with \"quotes\"", lt.Get(r2, "profile_name"), "profile name survives CSV quoting (commas become spaces upstream, quotes are RFC-escaped)");
            Assert.AreEqual("Male", lt.Get(r2, "gender"));
            Assert.AreEqual("0", lt.Get(r2, "uncertain_behavior_enabled"));
            Assert.AreEqual("", lt.Get(r2, "uncertain_config"), "no uncertain parameters are claimed for a lifetime without uncertain behaviour");
            Assert.AreEqual("0", lt.Get(r2, "app_hair_option"), "MaleOnly option 0; the FemaleOnly option 1 was not eligible"); Assert.AreEqual("HairB_male", lt.Get(r2, "app_hair_mesh"));
            Assert.AreEqual("TopB", lt.Get(r2, "app_top_mesh")); Assert.AreEqual("TopB1", lt.Get(r2, "app_top_materials"));
            Assert.AreNotEqual(hash1, lt.Get(r2, "profile_content_hash"));
            Assert.AreNotEqual(look1, lt.Get(r2, "appearance_hash"), "different applied looks are distinguishable");

            // profile registry: one row per distinct content, with a JSON description that carries the option names
            var pr = Csv.Load(rig.File("npc_profiles.csv"));
            AssertWidths(pr, "npc_profiles.csv");
            Assert.AreEqual(2, pr.Rows.Count);
            var ja = Rows(pr, "profile_content_hash", hash1)[0];
            StringAssert.Contains("\"HairA\"", pr.Get(ja, "profile_json"));
            StringAssert.Contains("\"TopA3\"", pr.Get(ja, "profile_json"), "options that were NOT drawn are described too");
            Assert.AreEqual(Get<string>(rig.dcm, "SessionId"), pr.Get(ja, "session_id"));
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = culture; }
    }

    [Test]
    public void ProfileContentHash_DependsOnContent_NotOnTheInstance()
    {
        var a = MakeProfile("SameName", 0.5f, false); var b = MakeProfile("SameName", 0.5f, false);
        _spawned.Add(a); _spawned.Add(b);
        foreach (var p in new[] { a, b }) SetOptions(p, "hairOptions", MakeOption(MeshNamed("H"), new[] { Mat("M") }, 1f, "Unisex"));
        string Hash(UnityEngine.Object p) { var args = new object[] { p, null }; return (string)T("AgentProfileSnapshot").GetMethod("Describe").Invoke(null, args); }
        string ha = Hash(a), hb = Hash(b);
        Assert.AreEqual(ha, hb, "same content -> same hash, whatever the instance");
        var opts = (Array)Get<object>(a, "hairOptions"); Set(opts.GetValue(0), "weight", 2f);
        Assert.AreNotEqual(ha, Hash(a), "an edited weight changes the hash");
        Assert.AreEqual(Hash(a), Hash(a), "deterministic");
    }

    [UnityTest]
    public IEnumerator MasterSwitchOff_WithAProfile_StillWritesNoLifetimeOrProfileRows()
    {
        var prof = MakeProfile("ProfileOff", 0.5f, false);
        SetOptions(prof, "hairOptions", MakeOption(MeshNamed("HairX"), new[] { Mat("MatX") }, 1f, "Unisex"));
        _spawned.Add(prof);
        Rig rig = null;
        yield return BuildRig(new RigOptions { withAppearance = true, profile = prof, npcCaptureEnabled = false, part2 = true, selectedPartFallback = true, prewarm = 2, maxAgents = 2, npcSpeed = 1f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "NPCs");
        yield return new WaitForSecondsRealtime(0.5f);
        foreach (string f in NpcFiles) Assert.IsFalse(File.Exists(rig.File(f)), f + " must not exist");
        foreach (var pa in ActiveAgents(rig)) Assert.IsNotNull(pa.GetComponent(T("AppearanceController")), "the simulation still applied the profile");
    }

    // ====================================================================================== storage layout (measurement, not a pass/fail claim about hardware)

    [Test]
    public void StorageLayout_OneFilePerTick_Versus_OneFilePerLifetime_Measurement()
    {
        string dir = Path.Combine(_root, "layout"); Directory.CreateDirectory(dir);
        const int N = 300, TICKS = 20;
        string row = new string('x', 300);
        var sw = new Stopwatch();

        // A: one long-format file, one open/append/close per tick for the whole population (what DataCaptureManager does)
        sw.Restart();
        string one = Path.Combine(dir, "npc_state.csv");
        for (int t = 0; t < TICKS; t++)
            using (var w = new StreamWriter(one, true)) for (int i = 0; i < N; i++) w.WriteLine(row);
        double aMs = sw.Elapsed.TotalMilliseconds / TICKS;

        // B: one file per lifetime, one open/append/close per lifetime per tick
        sw.Restart();
        for (int t = 0; t < TICKS; t++)
            for (int i = 0; i < N; i++)
                using (var w = new StreamWriter(Path.Combine(dir, "NPC-" + i.ToString("D6") + "-S1.csv"), true)) w.WriteLine(row);
        double bMs = sw.Elapsed.TotalMilliseconds / TICKS;

        UnityEngine.Debug.Log($"[NpcSchemaTests][layout] {N} NPCs: single long-format file {aMs:F2} ms/tick ({N} rows); per-lifetime files {bMs:F2} ms/tick ({N} files, 1 row each); " +
                              $"{Directory.GetFiles(dir).Length} files in the folder after {TICKS} ticks");
        Assert.AreEqual(N + 1, Directory.GetFiles(dir).Length);
        Assert.AreEqual(N * TICKS, File.ReadAllLines(one).Length);
    }
}
