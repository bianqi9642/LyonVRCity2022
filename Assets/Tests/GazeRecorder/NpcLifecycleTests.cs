// NpcLifecycleTests.cs
// PlayMode tests for the NPC lifetime identity / lifecycle recording work (schema v2). They drive the PRODUCTION SpawnerManager,
// PedestrianAgent, MoveToTarget, ExperimentManager, DataCaptureManager, AoiClassifier and GazeRecorder (through reflection, see TestSupport.cs) on a
// runtime-built NavMesh and read the files they really write. Output goes to a unique temp directory (DataCaptureManager.outputRootOverride), never to
// persistentDataPath. These are Unity Editor tests: they simulate the experiment lifecycle by calling ExperimentManager's own methods; they are NOT a
// Meta Quest test.

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
    static readonly Type PedType = T("PedestrianAgent");
    static readonly Type DcmType = T("DataCaptureManager");
    static readonly Type SpawnerType = T("SpawnerManager");
    static readonly Type EmType = T("ExperimentManager");
    static readonly Type MoveType = T("MoveToTarget");
    static readonly Type AoiType = T("AoiClassifier");
    static readonly Type BusType = T("NpcLifecycleBus");
    static readonly Type EvtType = T("NpcLifecycleEvent");
    static readonly Type CfgType = Type.GetType("ExperimentManager+PartCaptureConfig, Assembly-CSharp");
    static readonly Type RecorderType = T("GazeRecorder");

    static readonly Regex LifetimeIdRe = new Regex(@"^NPC-\d{6,}-S[1-9]\d*$");
    static readonly Regex Fixed4 = new Regex(@"^-?\d+\.\d{4}$");

    readonly List<UnityEngine.Object> _spawned = new List<UnityEngine.Object>();
    NavMeshDataInstance _navInstance;
    string _root;

    // ------------------------------------------------------------------ fixture

    [SetUp]
    public void SetUp()
    {
        CallStatic(BusType, "ResetForTests");
        CallStatic(AoiType, "ResetDiagnostics");
        _root = Path.Combine(Path.GetTempPath(), "NpcLifecycleTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // Objects are destroyed first and given a frame (their OnDestroy writes/flushes), only then are the NavMesh and the folder removed, so one
    // test can never log into the next one.
    [UnityTearDown]
    public IEnumerator TearDown()
    {
        foreach (var o in _spawned) if (o != null) UnityEngine.Object.Destroy(o);
        _spawned.Clear();
        yield return null;
        yield return null;
        if (_navInstance.valid) NavMesh.RemoveNavMeshData(_navInstance);
        CallStatic(BusType, "ResetForTests");
        try { Directory.Delete(_root, true); } catch { }
    }

    sealed class Rig
    {
        public Component em, dcm, spawner, session, factory;
        public object cfg;
        public GameObject template;
        public Transform[] entrances, exits, wandering;
        public string Folder => Get<string>(dcm, "SessionFolder");
        public string File(string name) => Path.Combine(Folder, name);
    }

    sealed class RigOptions
    {
        public int prewarm = 4, maxAgents = 4;
        public bool part2 = false;                  // wandering itinerary instead of attractor itinerary
        public bool npcCaptureEnabled = true;       // PartCaptureConfig.npcCaptureEnabled
        public bool selectedPartFallback = false;   // CurrentPartConfig stays null until after the first frame; DCM must resolve the SELECTED Part's config
        public float samplingInterval = 0.1f;       // DataCaptureManager.npcStateSamplingIntervalSeconds
        public float budgetMB = 0f;                 // DataCaptureManager.npcStateDataBudgetMBPerMinute
        public float maxInterval = 2f;              // DataCaptureManager.npcStateMaxIntervalSeconds
        public bool openSimulation = true;          // ResetForNewPart + BeginNpcSimulation (the Part's NPC lifecycle)
        public bool createDcm = true;
        public string entranceName = "Entrance_A";
        public string exitName = "Exit_A";
        public float spawnInterval = 0.05f;
        public float npcSpeed = 12f;
        public float entranceRadius = 0f;           // SpawnerManager.entranceSpawnRadius (scene value: 5 -> distributed entrance placement)
        public bool attractor = false;              // one attractor between entrance and exit, with a short dwell
        public bool compact = false;                // entrance and exit 6 m apart so an itinerary finishes naturally within seconds
        public bool withUncertain = false;          // UncertainBehavior on the template (all random probabilities 0: only forced transitions happen)
        public bool uncertainEnabled = true;        // its initial enabled flag (AgentFactory re-rolls it when a profile + factory are present)
        public bool withAppearance = false;         // AppearanceController with hair + top SkinnedMeshRenderers
        public UnityEngine.Object profile;          // AgentProfile applied through the production AgentFactory (null = none)
    }

    GameObject Make(string name, bool active = true)
    {
        var go = new GameObject(name);
        go.SetActive(active);
        _spawned.Add(go);
        return go;
    }

    void BuildNavMesh()
    {
        var src = new NavMeshBuildSource
        {
            shape = NavMeshBuildSourceShape.Box,
            size = new Vector3(240f, 0.2f, 240f),
            transform = Matrix4x4.identity,
            area = 0
        };
        var data = NavMeshBuilder.BuildNavMeshData(NavMesh.GetSettingsByID(0), new List<NavMeshBuildSource> { src },
            new Bounds(Vector3.zero, new Vector3(260f, 20f, 260f)), Vector3.zero, Quaternion.identity);
        _navInstance = NavMesh.AddNavMeshData(data);
    }

    Transform[] Points(string prefix, params Vector3[] where)
    {
        var list = new Transform[where.Length];
        for (int i = 0; i < where.Length; i++)
        {
            var go = Make(where.Length == 1 ? prefix : prefix + "_" + i);
            go.transform.position = where[i];
            list[i] = go.transform;
        }
        return list;
    }

    GameObject MakeTemplate(float speed, RigOptions o = null)
    {
        var t = Make("NpcTemplate", active: false);   // inactive: no Awake on the template, so clones start with an empty agentID
        var col = t.AddComponent<CapsuleCollider>();
        col.radius = 0.3f; col.height = 1.8f; col.center = new Vector3(0f, 0.9f, 0f);
        var nav = t.AddComponent<NavMeshAgent>();
        nav.speed = speed; nav.acceleration = 60f; nav.angularSpeed = 720f; nav.radius = 0.3f; nav.height = 1.8f;
        t.AddComponent(MoveType);
        t.AddComponent(PedType);
        if (o != null && o.withUncertain)
        {
            var ub = t.AddComponent(T("UncertainBehavior"));
            Set(ub, "moveController", t.GetComponent(MoveType));
            Set(ub, "agent", nav);
            foreach (string f in new[] { "pauseProbPerSec", "hesitateProbPerSec", "slowProbPerSec", "detourProbPerSec" }) Set(ub, f, 0f);   // only forced transitions
            Set(ub, "enableLogging", false);
            ((Behaviour)ub).enabled = o.uncertainEnabled;
        }
        if (o != null && o.withAppearance)
        {
            var ac = t.AddComponent(T("AppearanceController"));
            foreach (string part in new[] { "hair", "top" })
            {
                var child = new GameObject(part + "Renderer");
                child.transform.SetParent(t.transform, false);
                Set(ac, part + "Renderer", child.AddComponent<SkinnedMeshRenderer>());
            }
        }
        return t;
    }

    // Builds the scene objects in the order a real session has them. Everything is created in ONE frame, so all Start() calls follow
    // (DataCaptureManager first thanks to its [DefaultExecutionOrder(-100)]).
    IEnumerator BuildRig(RigOptions o, Action<Rig> done)
    {
        BuildNavMesh();
        var rig = new Rig();
        rig.entrances = Points("En", o.compact ? new Vector3(-3f, 0.1f, 0f) : new Vector3(-30f, 0.1f, -10f));
        rig.entrances[0].name = o.entranceName;
        rig.exits = Points("Ex", o.compact ? new Vector3(3f, 0.1f, 0f) : new Vector3(30f, 0.1f, 10f));
        rig.exits[0].name = o.exitName;
        rig.wandering = Points("W", new Vector3(0f, 0.1f, -20f), new Vector3(0f, 0.1f, 20f));
        rig.template = MakeTemplate(o.npcSpeed, o);

        var emGo = Make("TestExperimentManager");
        rig.em = emGo.AddComponent(EmType);
        object cfg = Activator.CreateInstance(CfgType);
        Set(cfg, "npcEnabled", true);
        Set(cfg, "npcDirectedMovement", !o.part2);
        Set(cfg, "npcCaptureEnabled", o.npcCaptureEnabled);
        Set(cfg, "gazeCaptureEnabled", true);
        Set(cfg, "trajectoryCaptureEnabled", true);
        if (o.selectedPartFallback)
        {
            // Before the first Part starts: the Part config lives only in the Inspector fields; the selected Part decides.
            Set(rig.em, o.part2 ? "part2Config" : "part3Config", cfg);
            var scGo = Make("TestSessionController");
            rig.session = scGo.AddComponent(T("ExternalSessionController"));
            Set(rig.session, "participantId", "TEST");
            Set(rig.session, "selectedPart", Enum.Parse(T("ExternalSessionController").GetNestedType("ExperimentPart"), o.part2 ? "Part2" : "Part3"));
        }
        else EmType.GetField("<CurrentPartConfig>k__BackingField", All).SetValue(rig.em, cfg);
        rig.cfg = cfg;

        if (o.createDcm)
        {
            var dcmGo = Make("TestDataCaptureManager");
            rig.dcm = dcmGo.AddComponent(DcmType);
            Set(rig.dcm, "outputRootOverride", _root);
            Set(rig.dcm, "npcStateSamplingIntervalSeconds", o.samplingInterval);
            Set(rig.dcm, "npcStateDataBudgetMBPerMinute", o.budgetMB);
            Set(rig.dcm, "npcStateMaxIntervalSeconds", o.maxInterval);
            Set(rig.dcm, "writeFullBatiRegistry", false);
            Set(rig.dcm, "enableDebugLogs", false);
        }

        var spGo = Make("TestSpawnerManager");
        rig.spawner = spGo.AddComponent(SpawnerType);
        if (o.profile != null)
        {
            rig.factory = Make("TestAgentFactory").AddComponent(T("AgentFactory"));
            Set(rig.spawner, "agentFactory", rig.factory);
            Set(rig.spawner, "defaultProfile", o.profile);
        }
        Set(rig.spawner, "agentPrefab", rig.template);
        Set(rig.spawner, "entrances", rig.entrances);
        Set(rig.spawner, "exits", rig.exits);
        Set(rig.spawner, "wanderingPoints", rig.wandering);
        Set(rig.spawner, "maxAgents", o.maxAgents);
        Set(rig.spawner, "poolPrewarm", o.prewarm);
        Set(rig.spawner, "spawnInterval", o.spawnInterval);
        Set(rig.spawner, "usePooling", true);
        Set(rig.spawner, "entranceSpawnRadius", o.entranceRadius);
        if (o.attractor)
        {
            var attractorType = T("AttractorInfo");
            var info = Activator.CreateInstance(attractorType);
            var att = Make("Attractor_A");
            att.transform.position = new Vector3(0f, 0.1f, 0f);
            Set(info, "transform", att.transform);
            Set(info, "capacity", 0);
            var arr = Array.CreateInstance(attractorType, 1);
            arr.SetValue(info, 0);
            Set(rig.spawner, "attractors", arr);
            Set(rig.spawner, "dwellTimeSec", new Vector2(0.5f, 0.5f));
        }
        Set(rig.spawner, "navmeshWaitTimeout", 1f);
        Set(rig.spawner, "minAttractorsPerAgent", o.attractor ? 1 : 0);
        Set(rig.spawner, "maxAttractorsPerAgent", o.attractor ? 1 : 0);
        Set(rig.spawner, "minWanderingPointsPerAgent", 1);
        Set(rig.spawner, "maxWanderingPointsPerAgent", 1);
        Set(rig.spawner, "enableDebugLogs", false);

        yield return null;   // Start() of every component above

        if (o.selectedPartFallback)
            EmType.GetField("<CurrentPartConfig>k__BackingField", All).SetValue(rig.em, rig.cfg);   // RunPartN: the Part's own config takes over

        // The Part starts (ExperimentManager.RunPartN does exactly this): part_start, then the NPC lifecycle opens.
        string part = o.part2 ? "Part2" : "Part3";
        Call(rig.em, "RaiseEvt", "part_start", part, "", "", 0.0, false, null, Vector3.zero, Vector3.zero);
        if (o.openSimulation)
        {
            Call(rig.spawner, "ResetForNewPart");
            Call(rig.spawner, "BeginNpcSimulation");
        }
        done(rig);
    }

    static IEnumerator WaitUntil(Func<bool> cond, float timeoutSeconds, string what)
    {
        float t0 = Time.realtimeSinceStartup;
        while (!cond())
        {
            if (Time.realtimeSinceStartup - t0 > timeoutSeconds) Assert.Fail("timeout waiting for: " + what);
            yield return null;
        }
    }

    static int Live(Rig r) => Get<int>(r.spawner, "liveAgents");
    static bool PoolReady(Rig r) => Get<bool>(r.spawner, "poolInitializationComplete");
    static List<Component> ActiveAgents(Rig r)
    {
        var l = new List<Component>();
        foreach (var go in Get<IReadOnlyList<GameObject>>(r.spawner, "ActiveAgents")) l.Add(go.GetComponent(PedType));
        return l;
    }
    static string Lid(Component pa) => Get<string>(pa, "LifetimeId");
    static string Aid(Component pa) => Get<string>(pa, "AgentID");

    static string F4(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    Csv Events(Rig r) => Csv.Load(r.File("events.csv"));
    Csv NpcEvents(Rig r) => Csv.Load(r.File("npc_events.csv"));
    Csv NpcState(Rig r) => Csv.Load(r.File("npc_state.csv"));
    Csv Lifetimes(Rig r) => Csv.Load(r.File("npc_lifetimes.csv"));

    static void AssertWidths(Csv c, string name)
    {
        Assert.IsTrue(c.Exists, name + " does not exist");
        foreach (var row in c.Rows) Assert.AreEqual(c.Header.Count, row.Count, name + ": row width differs from header");
    }

    // ====================================================================================== Test A

    [Test]
    public void LifetimeIds_ArePerAgentId_SequentialAndIdempotent()
    {
        var itinerary = new List<Transform> { Make("i0").transform, Make("i1").transform };
        var ped = Type.GetType("PedestrianAgent, Assembly-CSharp");
        var initialize = ped.GetMethod("Initialize", new[] { typeof(List<Transform>), SpawnerType, typeof(float) });
        Assert.IsNotNull(initialize);

        Component NewAgent(string name) { var go = Make(name); return go.AddComponent(PedType); }   // Awake assigns the stable AgentID
        Component a = NewAgent("A"), b = NewAgent("B");
        string aId = Aid(a), bId = Aid(b);
        Assert.AreNotEqual(aId, bId, "two pooled objects have different AgentIDs");
        Assert.IsTrue(Regex.IsMatch(aId, @"^NPC-\d{6}$"));

        Assert.AreEqual("", Lid(a), "no lifetime before the first spawn (prewarm / pool creation consumes no index)");
        Assert.AreEqual(0, Get<int>(a, "LifetimeIndex"));

        var seen = new List<string>();
        for (int n = 1; n <= 3; n++)
        {
            initialize.Invoke(a, new object[] { itinerary, null, 1f });
            Assert.AreEqual(aId + "-S" + n, Lid(a), "lifetime " + n);
            initialize.Invoke(a, new object[] { itinerary, null, 1f });   // repeated Initialize inside the same lifetime
            Assert.AreEqual(aId + "-S" + n, Lid(a), "repeated Initialize must not allocate a second ID");
            seen.Add(Lid(a));
            Call(a, "ForceFinish");
            Assert.AreEqual("", Lid(a), "an ended lifetime never resolves again");
            Assert.AreEqual(aId + "-S" + n, Get<string>(a, "LastLifetimeId"), "the ended ID stays readable for its closing events");
            Assert.IsFalse(Get<bool>(a, "HasOpenLifetime"));
        }
        CollectionAssert.AllItemsAreUnique(seen);
        CollectionAssert.AreEqual(new[] { aId + "-S1", aId + "-S2", aId + "-S3" }, seen, "old lifetime IDs are never rewritten");

        initialize.Invoke(b, new object[] { itinerary, null, 1f });
        Assert.AreEqual(bId + "-S1", Lid(b));
        Assert.IsTrue(Lid(b).EndsWith("-S1") && seen[0].EndsWith("-S1"));
        Assert.AreNotEqual(seen[0], Lid(b), "two different objects can both be on lifetime 1 without an identity collision");
    }

    // ====================================================================================== Test B: prewarm vs live NPC

    [UnityTest]
    public IEnumerator Prewarm_RecordsCreation_ButCreatesNoActiveLifetimeOrPositions()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 4, openSimulation = false }, r => rig = r);
        yield return WaitUntil(() => PoolReady(rig), 10f, "prewarm to finish");
        yield return new WaitForSecondsRealtime(0.4f);

        Assert.AreEqual(0, Live(rig), "no NPC is live: prewarmed objects are inactive pool members");
        var ev = NpcEvents(rig);
        AssertWidths(ev, "npc_events.csv");
        var created = new List<List<string>>(ev.Where("event_type", "prewarm_created"));
        Assert.AreEqual(4, created.Count, "one prewarm_created per pooled object");
        var ids = new HashSet<string>();
        foreach (var row in created)
        {
            Assert.IsTrue(Regex.IsMatch(ev.Get(row, "npc_id"), @"^NPC-\d{6}$"));
            Assert.IsTrue(ids.Add(ev.Get(row, "npc_id")), "each pooled object is created once");
            Assert.AreEqual("", ev.Get(row, "npc_spawn_instance_id"), "a prewarm record has no lifetime ID");
            Assert.AreEqual("", ev.Get(row, "lifetime_index"));
            StringAssert.Contains("active_npc=0", ev.Get(row, "value_str"));
        }
        Assert.IsFalse(Lifetimes(rig).Exists && Lifetimes(rig).Rows.Count > 0, "no lifetime row for a prewarmed, never-spawned object");
        Assert.IsFalse(NpcState(rig).Exists, "no position rows for inactive pooled objects");
        Assert.AreEqual(0, new List<List<string>>(NpcEvents(rig).Where("event_type", "npc_spawn")).Count);
        Assert.AreEqual(0, new List<List<string>>(Events(rig).Where("event_type", "npc_spawn")).Count);
    }

    // ====================================================================================== Tests B + C: spawns before the trial

    [UnityTest]
    public IEnumerator SpawnsDuringStartPanelWait_AreRecordedWithTrueSpawnTime_AndTrialStartAddsNothing()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 4, maxAgents = 4 }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 3, 10f, "NPCs to spawn while no trial is open");
        yield return new WaitForSecondsRealtime(0.6f);

        // Start-panel period: no trial has begun.
        var agents = ActiveAgents(rig);
        var lt = Lifetimes(rig);
        var evs = Events(rig);
        var nev = NpcEvents(rig);
        AssertWidths(lt, "npc_lifetimes.csv"); AssertWidths(evs, "events.csv"); AssertWidths(nev, "npc_events.csv");
        Assert.AreEqual(agents.Count, lt.Rows.Count, "one lifetime row per live NPC, written before any trial");

        foreach (var pa in agents)
        {
            string id = Lid(pa);
            Assert.IsTrue(LifetimeIdRe.IsMatch(id), id);
            var spawnRows = new List<List<string>>(evs.Where("npc_spawn_instance_id", id));
            int spawns = 0;
            foreach (var r in spawnRows) if (evs.Get(r, "event_type") == "npc_spawn") spawns++;
            Assert.AreEqual(1, spawns, "exactly one events.csv npc_spawn for " + id);
            var row = new List<List<string>>(lt.Where("npc_spawn_instance_id", id));
            Assert.AreEqual(1, row.Count);
            Assert.AreEqual(F4(Get<double>(pa, "LifetimeSpawnTAbs")), lt.Get(row[0], "spawn_t_abs"), "the true spawn time, not the registration time");
            Assert.AreEqual("spawn", lt.Get(row[0], "registration"));
            Assert.AreEqual("pre_trial", lt.Get(row[0], "capture_period_at_spawn"));
            Assert.AreEqual("pool", lt.Get(row[0], "spawn_source"));
            Assert.AreEqual(Aid(pa), lt.Get(row[0], "npc_id"));
        }

        // Positions are already being recorded before the trial (default mode), under the same lifetime IDs.
        var st = NpcState(rig);
        AssertWidths(st, "npc_state.csv");
        Assert.Greater(st.Rows.Count, 0, "positions of active NPCs are recorded before the trial");
        foreach (var r in st.Rows)
        {
            Assert.AreEqual("pre_trial", st.Get(r, "capture_period"));
            Assert.IsTrue(LifetimeIdRe.IsMatch(st.Get(r, "npc_spawn_instance_id")), "every state row carries a lifetime ID: " + st.Get(r, "npc_spawn_instance_id"));
        }

        // The trial starts: the census must not create lifetimes, spawn rows or change spawn times.
        int lifetimeRowsBefore = lt.Rows.Count;
        Call(rig.em, "BeginTrial");
        yield return new WaitForSecondsRealtime(0.4f);
        var lt2 = Lifetimes(rig);
        // NPCs may also have spawned meanwhile; compare only the lifetimes that existed before.
        foreach (var r in lt.Rows)
        {
            var again = new List<List<string>>(lt2.Where("npc_spawn_instance_id", lt.Get(r, "npc_spawn_instance_id")));
            Assert.AreEqual(1, again.Count, "no duplicate lifetime row after trial start");
            Assert.AreEqual(lt.Get(r, "spawn_t_abs"), lt2.Get(again[0], "spawn_t_abs"), "spawn time unchanged by trial start");
            Assert.AreEqual("spawn", lt2.Get(again[0], "registration"));
        }
        var ev2 = Events(rig);
        var counts = new Dictionary<string, int>();
        foreach (var r in ev2.Rows)
            if (ev2.Get(r, "event_type") == "npc_spawn")
            {
                string k = ev2.Get(r, "npc_spawn_instance_id");
                counts[k] = counts.ContainsKey(k) ? counts[k] + 1 : 1;
            }
        foreach (var kv in counts) Assert.AreEqual(1, kv.Value, "duplicate npc_spawn for " + kv.Key);

        var st2 = NpcState(rig);
        bool sawTrial = false;
        foreach (var r in st2.Rows) if (st2.Get(r, "capture_period") == "trial") sawTrial = true;
        Assert.IsTrue(sawTrial, "positions continue into the trial window");
        Assert.GreaterOrEqual(lifetimeRowsBefore, 1);
    }

    // ====================================================================================== Test C: census / replay

    [UnityTest]
    public IEnumerator Census_RegistersAnUnrecordedActiveLifetime_WithOriginalSpawnTime_AndIsIdempotent()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 3, maxAgents = 3 }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 1, 10f, "first NPC");

        // Make the recorder blind: detach its sink and drop everything queued meanwhile (an NPC whose spawn event never reached it).
        CallStatic(BusType, "ResetForTests");
        yield return WaitUntil(() => Live(rig) >= 3, 10f, "NPCs to spawn while the recorder is detached");
        CallStatic(BusType, "ResetForTests");   // discard the queued spawn events for good

        var agents = ActiveAgents(rig);
        var unrecorded = new List<Component>();
        var lt0 = Lifetimes(rig);
        foreach (var pa in agents)
            if (new List<List<string>>(lt0.Where("npc_spawn_instance_id", Lid(pa))).Count == 0) unrecorded.Add(pa);
        Assert.Greater(unrecorded.Count, 0, "precondition: some active lifetimes are unrecorded");

        Call(rig.em, "BeginTrial");   // OnTrialStart -> synchronous census
        var lt = Lifetimes(rig);
        foreach (var pa in unrecorded)
        {
            var row = new List<List<string>>(lt.Where("npc_spawn_instance_id", Lid(pa)));
            Assert.AreEqual(1, row.Count, "registered by the census: " + Lid(pa));
            Assert.AreEqual("late", lt.Get(row[0], "registration"));
            Assert.AreEqual(F4(Get<double>(pa, "LifetimeSpawnTAbs")), lt.Get(row[0], "spawn_t_abs"), "original spawn time retained");
            Assert.AreNotEqual(lt.Get(row[0], "spawn_t_abs"), lt.Get(row[0], "registered_t_abs"), "registration time is recorded separately");
            Assert.Less(double.Parse(lt.Get(row[0], "spawn_t_abs"), CultureInfo.InvariantCulture),
                        double.Parse(lt.Get(row[0], "registered_t_abs"), CultureInfo.InvariantCulture));
        }

        // Repeating the census (second trial start / every mismatch) changes nothing.
        int before = Events(rig).Rows.Count;
        string[] idsBefore = new string[unrecorded.Count];
        for (int i = 0; i < unrecorded.Count; i++) idsBefore[i] = Lid(unrecorded[i]);
        Call(rig.dcm, "CensusActiveNpcs");
        Call(rig.dcm, "CensusActiveNpcs");
        var ev = Events(rig);
        foreach (var id in idsBefore)
        {
            int n = 0;
            foreach (var r in ev.Where("npc_spawn_instance_id", id)) if (ev.Get(r, "event_type") == "npc_spawn") n++;
            Assert.AreEqual(1, n, "census is idempotent for " + id);
        }
        for (int i = 0; i < unrecorded.Count; i++) Assert.AreEqual(idsBefore[i], Lid(unrecorded[i]), "the census never allocates a new lifetime");
        yield return null;
    }

    [UnityTest]
    public IEnumerator RecorderAttachingLate_ReplaysBufferedLifecycleEvents_WithOriginalTimestamps()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 3, maxAgents = 3, createDcm = false }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "NPCs to spawn with no recorder");

        Assert.Greater(GetStatic<int>(BusType, "PendingCount"), 0, "events are queued while no recorder exists (nothing written to a placeholder session)");
        double firstSpawn = Get<double>(ActiveAgents(rig)[0], "LifetimeSpawnTAbs");
        double createdAt = Get<double>(ActiveAgents(rig)[0], "ObjectCreatedTAbs");
        string firstId = Lid(ActiveAgents(rig)[0]);
        yield return new WaitForSecondsRealtime(0.3f);

        var dcmGo = Make("LateDcm");
        rig.dcm = dcmGo.AddComponent(DcmType);
        Set(rig.dcm, "outputRootOverride", _root);
        Set(rig.dcm, "writeFullBatiRegistry", false);
        Set(rig.dcm, "enableDebugLogs", false);
        yield return null;   // Start(): session created, then Attach() replays the queue
        yield return new WaitForSecondsRealtime(0.2f);

        var ev = NpcEvents(rig);
        AssertWidths(ev, "npc_events.csv");
        var created = new List<List<string>>(ev.Where("event_type", "prewarm_created"));
        Assert.AreEqual(3, created.Count, "prewarm events replayed");
        var sessionStart = new List<List<string>>(Events(rig).Where("event_type", "session_start"));
        double sessionStartT = double.Parse(Events(rig).Get(sessionStart[0], "t_abs"), CultureInfo.InvariantCulture);
        foreach (var r in created)
            Assert.Less(double.Parse(ev.Get(r, "t_abs"), CultureInfo.InvariantCulture), sessionStartT, "replayed prewarm rows keep their ORIGINAL time (before the session existed)");
        bool createdTimeKept = false;
        foreach (var r in created) if (ev.Get(r, "t_abs") == F4(createdAt)) createdTimeKept = true;
        Assert.IsTrue(createdTimeKept, "the prewarm_created row carries the exact creation time recorded on the object");

        var lt = Lifetimes(rig);
        var row = new List<List<string>>(lt.Where("npc_spawn_instance_id", firstId));
        Assert.AreEqual(1, row.Count);
        Assert.AreEqual(F4(firstSpawn), lt.Get(row[0], "spawn_t_abs"), "original spawn time survives the late attach");
        Assert.AreEqual("spawn", lt.Get(row[0], "registration"), "replayed in order: still a normal registration, not a census late-bind");
        Assert.AreEqual(0, GetStatic<int>(BusType, "PendingCount"));
        Assert.AreEqual(0, GetStatic<int>(BusType, "DroppedPending"));
    }

    // ====================================================================================== Tests A + D: reuse and linkage

    static void ForceFinishAll(Rig r) { foreach (var pa in ActiveAgents(r)) Call(pa, "ForceFinish"); }

    [UnityTest]
    public IEnumerator Reuse_GivesNewLifetimeIds_AndKeepsTrajectoriesAndEventsSeparate()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 2, maxAgents = 2, npcSpeed = 3f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 2, 10f, "two live NPCs");
        yield return new WaitForSecondsRealtime(0.5f);

        var firstGeneration = new Dictionary<string, string>();   // AgentID -> lifetime ID
        foreach (var pa in ActiveAgents(rig)) firstGeneration[Aid(pa)] = Lid(pa);
        Assert.AreEqual(2, firstGeneration.Count);
        foreach (var kv in firstGeneration) Assert.AreEqual(kv.Key + "-S1", kv.Value);

        for (int round = 2; round <= 3; round++)
        {
            ForceFinishAll(rig);
            Assert.AreEqual(0, Live(rig));
            yield return WaitUntil(() => Live(rig) == 2, 10f, "respawn after round " + round);
            yield return new WaitForSecondsRealtime(0.5f);
            foreach (var pa in ActiveAgents(rig))
                Assert.AreEqual(Aid(pa) + "-S" + round, Lid(pa), "the same pooled GameObject gets lifetime " + round);
        }

        var lt = Lifetimes(rig); var st = NpcState(rig); var nev = NpcEvents(rig); var evs = Events(rig);
        AssertWidths(lt, "npc_lifetimes.csv"); AssertWidths(st, "npc_state.csv"); AssertWidths(nev, "npc_events.csv"); AssertWidths(evs, "events.csv");

        // Lifetimes: unique IDs, indices 1..3 per AgentID, old IDs untouched.
        var allIds = new HashSet<string>();
        var byAgent = new Dictionary<string, List<int>>();
        foreach (var r in lt.Rows)
        {
            string id = lt.Get(r, "npc_spawn_instance_id");
            Assert.IsTrue(LifetimeIdRe.IsMatch(id), id);
            Assert.IsTrue(allIds.Add(id), "lifetime ID assigned twice: " + id);
            string agent = lt.Get(r, "npc_id");
            Assert.AreEqual(agent + "-S" + lt.Get(r, "lifetime_index"), id);
            if (!byAgent.ContainsKey(agent)) byAgent[agent] = new List<int>();
            byAgent[agent].Add(int.Parse(lt.Get(r, "lifetime_index"), CultureInfo.InvariantCulture));
        }
        Assert.AreEqual(2, byAgent.Count);
        foreach (var kv in byAgent) CollectionAssert.AreEqual(new[] { 1, 2, 3 }, kv.Value, kv.Key);
        foreach (var kv in firstGeneration) Assert.IsTrue(allIds.Contains(kv.Value), "first-generation ID still recorded: " + kv.Value);

        // Per lifetime: spawn < every state sample < despawn < next spawn (same object); each row joins exactly one lifetime.
        foreach (var r in lt.Rows)
        {
            string id = lt.Get(r, "npc_spawn_instance_id");
            double spawn = double.Parse(lt.Get(r, "spawn_t_abs"), CultureInfo.InvariantCulture);

            var despawn = new List<List<string>>();
            foreach (var d in nev.Where("npc_spawn_instance_id", id)) if (nev.Get(d, "event_type") == "npc_despawn") despawn.Add(d);
            int idx = int.Parse(lt.Get(r, "lifetime_index"), CultureInfo.InvariantCulture);
            if (idx < 3)
            {
                Assert.AreEqual(1, despawn.Count, "one despawn for " + id);
                double dt = double.Parse(nev.Get(despawn[0], "t_abs"), CultureInfo.InvariantCulture);
                Assert.Greater(dt, spawn, id);
                var itin = new List<List<string>>();
                foreach (var d in nev.Where("npc_spawn_instance_id", id)) if (nev.Get(d, "event_type") == "npc_itinerary_complete") itin.Add(d);
                Assert.AreEqual(1, itin.Count, "one itinerary completion for " + id);
                Assert.LessOrEqual(double.Parse(nev.Get(itin[0], "t_abs"), CultureInfo.InvariantCulture), dt);
                foreach (var s in st.Where("npc_spawn_instance_id", id))
                {
                    double ts = double.Parse(st.Get(s, "t_abs"), CultureInfo.InvariantCulture);
                    Assert.GreaterOrEqual(ts, spawn - 0.0001, "state sample before spawn: " + id);
                    Assert.LessOrEqual(ts, dt + 0.0001, "state sample after despawn: " + id);
                    Assert.AreEqual(lt.Get(r, "npc_id"), st.Get(s, "npc_id"));
                    Assert.AreEqual(lt.Get(r, "lifetime_index"), st.Get(s, "lifetime_index"));
                }
            }
            var spawnEvents = 0;
            foreach (var d in nev.Where("npc_spawn_instance_id", id)) if (nev.Get(d, "event_type") == "npc_spawn") spawnEvents++;
            Assert.AreEqual(1, spawnEvents);
        }

        // Trajectories of successive lifetimes of the same object are separate: sample_seq restarts at 1 per lifetime and is gap-free.
        foreach (var r in lt.Rows)
        {
            string id = lt.Get(r, "npc_spawn_instance_id");
            int expected = 1;
            foreach (var s in st.Where("npc_spawn_instance_id", id))
                Assert.AreEqual(expected++, int.Parse(st.Get(s, "sample_seq"), CultureInfo.InvariantCulture), "sample_seq for " + id);
        }

        // Order of the object-level events for one reuse: pool_acquired -> npc_spawn, and npc_despawn -> npc_pool_return.
        foreach (var kv in byAgent)
        {
            var seq = new List<string>();
            foreach (var e in nev.Rows)
                if (nev.Get(e, "npc_id") == kv.Key)
                {
                    string t = nev.Get(e, "event_type");
                    if (t == "prewarm_created" || t == "pool_acquired" || t == "npc_spawn" || t == "npc_despawn" || t == "npc_pool_return") seq.Add(t);
                }
            Assert.AreEqual("prewarm_created", seq[0], kv.Key);
            for (int i = 1; i + 3 < seq.Count; i += 4)
            {
                Assert.AreEqual("pool_acquired", seq[i]);
                Assert.AreEqual("npc_spawn", seq[i + 1]);
            }
            Assert.IsFalse(seq.Contains("prewarm_created") && seq.FindLastIndex(x => x == "prewarm_created") != 0, "prewarm_created happens once per object");
        }

        // Movement events carry the lifetime they belong to.
        bool sawMovement = false;
        foreach (var e in nev.Rows)
        {
            string t = nev.Get(e, "event_type");
            if (t == "npc_movement_start" || t == "npc_target_set" || t == "npc_arrived")
            {
                sawMovement = true;
                Assert.IsTrue(LifetimeIdRe.IsMatch(nev.Get(e, "npc_spawn_instance_id")), t + " must carry a lifetime ID");
                Assert.IsTrue(allIds.Contains(nev.Get(e, "npc_spawn_instance_id")));
            }
        }
        Assert.IsTrue(sawMovement, "movement events were recorded");
    }

    [UnityTest]
    public IEnumerator NaturalItineraryCompletion_ClosesTheLifetime_AndThePooledObjectIsReusedUnderANewId()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 25f, compact = true }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "first NPC");
        string agent = Aid(ActiveAgents(rig)[0]);
        string first = Lid(ActiveAgents(rig)[0]);
        Assert.AreEqual(agent + "-S1", first);

        // Walk entrance -> exit naturally (no ForceFinish); then the same object must come back as S2.
        yield return WaitUntil(() => ActiveAgents(rig).Count == 1 && Lid(ActiveAgents(rig)[0]) == agent + "-S2", 40f, "natural completion + reuse as S2");

        var nev = NpcEvents(rig);
        var reasons = new List<string>();
        foreach (var r in nev.Where("npc_spawn_instance_id", first))
            if (nev.Get(r, "event_type") == "npc_itinerary_complete") reasons.Add(nev.Get(r, "value_str"));
        Assert.AreEqual(1, reasons.Count);
        StringAssert.Contains("reason=itinerary_complete", reasons[0]);
        bool arrived = false, move = false;
        foreach (var r in nev.Where("npc_spawn_instance_id", first))
        {
            if (nev.Get(r, "event_type") == "npc_movement_start") move = true;
            if (nev.Get(r, "event_type") == "npc_arrived") arrived = true;
        }
        Assert.IsTrue(move, "movement start recorded for the first lifetime");
        Assert.IsTrue(arrived, "arrival at the exit recorded for the first lifetime");
    }

    [UnityTest]
    public IEnumerator DwellArrivalAndEntranceTeleport_AreRecordedAtTheirRealMoment_UnderTheLifetimeId()
    {
        // Scene-like Part 3 configuration: distributed entrance placement (radius 5) and one attractor with a dwell.
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, compact = true, attractor = true, entranceRadius = 5f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "first NPC");
        string id = Lid(ActiveAgents(rig)[0]);
        yield return WaitUntil(() => ActiveAgents(rig).Count == 1 && Lid(ActiveAgents(rig)[0]).EndsWith("-S2"), 60f, "first lifetime to complete");

        var nev = NpcEvents(rig);
        var order = new List<string>();
        var times = new Dictionary<string, double>();
        foreach (var r in nev.Where("npc_spawn_instance_id", id))
        {
            string t = nev.Get(r, "event_type");
            if (t == "npc_spawn" || t == "npc_entrance_teleport" || t == "npc_movement_start" || t == "npc_arrived" || t == "npc_dwell_start" ||
                t == "npc_dwell_end" || t == "npc_itinerary_complete" || t == "npc_despawn")
            {
                order.Add(t);
                if (!times.ContainsKey(t)) times[t] = double.Parse(nev.Get(r, "t_abs"), CultureInfo.InvariantCulture);
            }
        }
        // spawn first, then the entrance placement, then movement; arrival -> dwell start -> dwell end; the lifetime closes last.
        Assert.AreEqual("npc_spawn", order[0]);
        Assert.Less(order.IndexOf("npc_spawn"), order.IndexOf("npc_entrance_teleport"));
        Assert.Less(order.IndexOf("npc_entrance_teleport"), order.IndexOf("npc_movement_start") < 0 ? int.MaxValue : order.IndexOf("npc_movement_start") + 1);
        Assert.Contains("npc_dwell_start", order);
        Assert.Contains("npc_dwell_end", order);
        Assert.Less(order.IndexOf("npc_dwell_start"), order.IndexOf("npc_dwell_end"));
        Assert.Less(order.IndexOf("npc_dwell_end"), order.IndexOf("npc_itinerary_complete"));
        Assert.Less(order.IndexOf("npc_itinerary_complete"), order.IndexOf("npc_despawn"));
        double dwell = times["npc_dwell_end"] - times["npc_dwell_start"];
        Assert.AreEqual(0.5, dwell, 0.15, "dwell duration measured from the real transition times (configured 0.5 s)");
        Assert.Greater(times["npc_entrance_teleport"], times["npc_spawn"], "entrance teleport is a separate, later moment than the spawn");

        var lt = Lifetimes(rig);
        var row = new List<List<string>>(lt.Where("npc_spawn_instance_id", id))[0];
        Assert.AreEqual("0.5000", lt.Get(row, "dwell_time_s"));
        Assert.AreEqual("Attractor_A", lt.Get(row, "first_target_id") == "Attractor_A" ? "Attractor_A" : lt.Get(row, "itinerary_ids").Split('|')[1]);
        Assert.AreEqual(3, int.Parse(lt.Get(row, "itinerary_count"), CultureInfo.InvariantCulture), "entrance, attractor, exit");
    }

    // ====================================================================================== end of trial/session

    [UnityTest]
    public IEnumerator TrialEndAndSessionEnd_WithLiveNpcs_MarkOpenLifetimes_AndWriteSchemaMetadata()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 3, maxAgents = 3, npcSpeed = 1f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 3, 10f, "three live NPCs");
        Call(rig.em, "BeginTrial");
        yield return new WaitForSecondsRealtime(0.4f);
        Call(rig.em, "EndTrial");                              // trial ends, NPCs keep living
        yield return new WaitForSecondsRealtime(0.3f);
        var ids = new List<string>();
        foreach (var pa in ActiveAgents(rig)) ids.Add(Lid(pa));
        Assert.AreEqual(3, ids.Count);

        // The End panel becomes visible: NPCs are frozen (no despawn), capture hard-stops, open lifetimes are marked.
        Call(rig.em, "HandleEndPanelDisplayed", "Part3", true);
        yield return new WaitForSecondsRealtime(0.4f);
        int rowsAtStop = NpcState(rig).Rows.Count;
        yield return new WaitForSecondsRealtime(0.6f);
        Assert.AreEqual(rowsAtStop, NpcState(rig).Rows.Count, "no position rows after the End panel");

        var nev = NpcEvents(rig);
        var open = new HashSet<string>();
        foreach (var r in nev.Where("event_type", "npc_lifetime_open_at_end")) open.Add(nev.Get(r, "npc_spawn_instance_id"));
        foreach (var id in ids) Assert.IsTrue(open.Contains(id), "open-at-end marker for " + id);
        Assert.AreEqual(ids.Count, open.Count);

        Call(rig.em, "TerminateSession", "Part3");
        yield return null;
        string manifest = File.ReadAllText(rig.File("session_manifest.json"));
        StringAssert.Contains("\"schema_version\": 3", manifest);
        StringAssert.Contains("agent-lifetime-v1", manifest);
        StringAssert.Contains("(session_id, npc_spawn_instance_id)", manifest);
        StringAssert.Contains("master_switch", manifest);
        StringAssert.Contains("\"run_guid\"", manifest);
        StringAssert.Contains("\"end_reason\": \"end_confirmed\"", manifest);
        StringAssert.Contains("\"npc_lifetimes_registered\": 3", manifest);
        AssertWidths(Events(rig), "events.csv");
        foreach (var r in Events(rig).Rows) Assert.AreEqual(Get<string>(rig.dcm, "SessionId"), Events(rig).Get(r, "session_id"));
    }

    // ====================================================================================== Test E: gaze

    [UnityTest]
    public IEnumerator Gaze_ResolvesTheCurrentLifetime_NeverAStaleOne()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.1f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        Component pa = ActiveAgents(rig)[0];
        string agent = Aid(pa);
        var npcCollider = pa.GetComponent<CapsuleCollider>();

        // Stand the NPC in front of a head and let the REAL GazeRecorder look at it.
        var nav = pa.GetComponent<NavMeshAgent>();
        nav.Warp(new Vector3(0f, 0.1f, 0f));
        var head = Make("Head");
        head.transform.position = new Vector3(0f, 0.9f, -3f);
        head.transform.rotation = Quaternion.identity;
        Call(rig.em, "BeginTrial");
        var recGo = Make("Gaze");
        var rec = recGo.AddComponent(RecorderType);
        Set(rec, "headTransform", head.transform);
        Set(rec, "sampleRate", 60f);
        Set(rec, "enableDebugLogs", false);
        Set(rec, "experimentManager", rig.em);
        yield return null;

        // Classifier-level checks across lifetimes of the SAME collider (the cache must never serve a stale lifetime).
        object Classify() => CallStatic(AoiType, "Classify", true, npcCollider, rig.em, rig.dcm);
        string ClassifiedId(object info) => (string)info.GetType().GetField("npcSpawnInstanceId").GetValue(info);

        yield return null;
        // Keep the NPC from walking out of the ray during the test.
        Call(pa.GetComponent(MoveType), "Stop");
        string s1 = Lid(pa);
        Assert.AreEqual(agent + "-S1", ClassifiedId(Classify()));

        Call(pa, "ForceFinish");   // lifetime S1 ends; the GameObject (and collider) is still physically there this frame
        Assert.AreEqual("", ClassifiedId(Classify()), "after despawn the hit is UNRESOLVED (blank), never the old ID");
        Assert.GreaterOrEqual(GetStatic<int>(AoiType, "UnresolvedNpcHits"), 1, "the unresolved hit is counted");

        yield return WaitUntil(() => Live(rig) == 1, 10f, "respawn");
        pa = ActiveAgents(rig)[0];
        Assert.AreEqual(agent, Aid(pa));
        Assert.AreEqual(agent + "-S2", Lid(pa));
        Assert.AreEqual(agent + "-S2", ClassifiedId(Classify()), "after reuse the same collider resolves to the NEW lifetime");

        // End to end through GazeRecorder.StartRecording: gaze.csv carries the same ID as npc_state / npc_events.
        nav = pa.GetComponent<NavMeshAgent>();
        Call(rec, "StartRecording");
        float until = Time.realtimeSinceStartup + 0.6f;
        while (Time.realtimeSinceStartup < until)
        {
            nav.Warp(new Vector3(0f, 0.1f, 0f));   // hold the NPC in the gaze ray
            yield return null;
        }
        Call(rec, "StopRecording");

        var gaze = Csv.Load(rig.File("gaze.csv"));
        AssertWidths(gaze, "gaze.csv");
        Assert.Greater(gaze.Rows.Count, 0);
        int resolved = 0;
        foreach (var r in gaze.Rows)
        {
            string id = gaze.Get(r, "hit_npc_spawn_instance_id");
            if (gaze.Get(r, "hit_semantic_class") == "NPC")
            {
                Assert.AreEqual(agent, gaze.Get(r, "hit_npc_id"));
                Assert.AreNotEqual(s1, id, "no stale lifetime in gaze");
                if (id == agent + "-S2") resolved++;
            }
        }
        Assert.Greater(resolved, 0, "gaze rows resolve the live lifetime");
        Assert.AreEqual(Get<string>(rig.dcm, "SessionId"), gaze.Get(gaze.Rows[0], "session_id"));
    }

    // ====================================================================================== Test F: schema integrity

    [UnityTest]
    public IEnumerator AllNpcStreams_AreCultureIndependent_AndParseToTheirHeaderWidth_WithSpecialCharactersInNames()
    {
        var saved = CultureInfo.CurrentCulture; var savedUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
            Assert.AreEqual(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);

            Rig rig = null;
            yield return BuildRig(new RigOptions { prewarm = 2, maxAgents = 2, entranceName = "En,tr\"ance\nA", exitName = "Ex\"it,B", npcSpeed = 4f }, r => rig = r);
            yield return WaitUntil(() => Live(rig) == 2, 10f, "NPCs");
            yield return new WaitForSecondsRealtime(0.4f);
            Call(rig.em, "BeginTrial");
            yield return new WaitForSecondsRealtime(0.4f);
            ForceFinishAll(rig);
            yield return WaitUntil(() => Live(rig) == 2, 10f, "reuse");
            yield return new WaitForSecondsRealtime(0.4f);
            Call(rig.em, "EndTrial");

            foreach (var name in new[] { "events.csv", "npc_state.csv", "npc_events.csv", "npc_lifetimes.csv" })
            {
                var c = Csv.Load(rig.File(name));
                AssertWidths(c, name);
                Assert.Greater(c.Rows.Count, 0, name);
            }

            var st = NpcState(rig);
            foreach (var col in new[] { "t_abs", "pos_x", "pos_y", "pos_z", "yaw", "vel_x", "speed", "rot_x", "rot_w" })
                foreach (var r in st.Rows) Assert.IsTrue(Fixed4.IsMatch(st.Get(r, col)), "npc_state " + col + " = '" + st.Get(r, col) + "'");
            foreach (var r in st.Rows)
                Assert.IsTrue(LifetimeIdRe.IsMatch(st.Get(r, "npc_spawn_instance_id")));

            var nev = NpcEvents(rig);
            foreach (var r in nev.Rows)
            {
                Assert.IsTrue(Fixed4.IsMatch(nev.Get(r, "t_abs")), nev.Get(r, "t_abs"));
                string sid = nev.Get(r, "npc_spawn_instance_id");
                if (sid.Length > 0) Assert.IsTrue(LifetimeIdRe.IsMatch(sid), sid);
            }
            var lt = Lifetimes(rig);
            foreach (var r in lt.Rows)
            {
                Assert.AreEqual("En tr\"ance A", lt.Get(r, "entrance_id"), "comma / newline sanitised to spaces, the quote preserved through CSV quoting");
                StringAssert.Contains("Ex\"it B", lt.Get(r, "final_target_id"));
                Assert.IsTrue(Fixed4.IsMatch(lt.Get(r, "spawn_t_abs")));
                Assert.IsTrue(Fixed4.IsMatch(lt.Get(r, "configured_speed")) || lt.Get(r, "configured_speed") == "", "blank = no profile applied (unknown), never 0");
            }
            // Same identity semantics in every stream.
            var ids = new HashSet<string>();
            foreach (var r in lt.Rows) ids.Add(lt.Get(r, "npc_spawn_instance_id"));
            foreach (var r in st.Rows) Assert.IsTrue(ids.Contains(st.Get(r, "npc_spawn_instance_id")), "state row without a lifetime row");
            var evs = Events(rig);
            foreach (var r in evs.Where("event_type", "npc_spawn")) Assert.IsTrue(ids.Contains(evs.Get(r, "npc_spawn_instance_id")));
        }
        finally { CultureInfo.CurrentCulture = saved; CultureInfo.CurrentUICulture = savedUi; }
    }

    // ====================================================================================== Test G: sampler cost / volume

    [UnityTest]
    public IEnumerator Sampler_WithALargePopulation_WritesOneBatchPerTick_AndReportsCost()
    {
        const int N = 300;
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = N, maxAgents = N, spawnInterval = 0f, npcSpeed = 1f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= N, 90f, N + " live NPCs");
        yield return new WaitForSecondsRealtime(0.5f);

        var sw = new Stopwatch();
        long bytesBefore = new FileInfo(rig.File("npc_state.csv")).Length;
        const int ticks = 20;
        double worstMs = 0, totalMs = 0;
        for (int i = 0; i < ticks; i++)
        {
            sw.Restart();
            Call(rig.dcm, "SampleNpcs");
            sw.Stop();
            totalMs += sw.Elapsed.TotalMilliseconds;
            worstMs = Math.Max(worstMs, sw.Elapsed.TotalMilliseconds);
        }
        long bytes = new FileInfo(rig.File("npc_state.csv")).Length - bytesBefore;
        double perRow = (double)bytes / (ticks * N);
        UnityEngine.Debug.Log($"[NpcLifecycleTests][perf] population={N} avgTickMs={totalMs / ticks:F2} worstTickMs={worstMs:F2} bytesPerRow={perRow:F0} " +
                              $"bytesPerTick={bytes / ticks} MBperMinuteAt10Hz={bytes / (double)ticks * 10 * 60 / 1e6:F1}");

        var st = NpcState(rig);
        AssertWidths(st, "npc_state.csv");
        var lastTick = new Dictionary<string, int>();
        foreach (var r in st.Rows) { string id = st.Get(r, "npc_spawn_instance_id"); lastTick[id] = lastTick.ContainsKey(id) ? lastTick[id] + 1 : 1; }
        Assert.AreEqual(N, lastTick.Count, "every active NPC is sampled (none dropped)");
        Assert.AreEqual(N, new List<List<string>>(Lifetimes(rig).Rows).Count, "one lifetime row per NPC");
    }
}
