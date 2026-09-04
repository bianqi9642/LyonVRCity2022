using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// ExperimentManager
///
/// Part 1 / Part 2: unchanged.
///
/// Part 3 (15-minute Main Experimental Phase):
///  - The participant's assigned group (1..12, from ExternalSessionController.part3GroupIndex, 0-based)
///    is looked up in a fixed table that yields (StartZone, Destination1Zone, Destination2Zone).
///  - StartZone selects one of four FIXED start Transforms. The participant is teleported there.
///  - Destination1Zone / Destination2Zone each have exactly two candidate buildings; exactly ONE per
///    destination is chosen RANDOMLY and INDEPENDENTLY per session and kept fixed (coroutine locals).
///  - VISIBILITY vs INTERACTABILITY vs TARGET ASSIGNMENT remain three separate concepts (unchanged).
///  - Timing: D1 search capped at part3Destination1MaxSeconds; whole phase capped at part3TotalMaxSeconds.
///
/// DATA CAPTURE (Revision 2):
///  - The old standalone Part 3 CSV ({subj}_Part3_Session_{UTC}.csv) has been REMOVED. AppendPart3Log(...)
///    no longer performs any file I/O; it now only raises OnExperimentEvent, which DataCaptureManager
///    records into the single authoritative events.csv.
///  - New OnExperimentEvent + a small set of additive RaiseEvt(...) hooks let the pure-observer
///    DataCaptureManager reconstruct part/phase structure, panels, teleports, target assignment,
///    candidate interactability and Part 3 outcomes.
///  - NONE of the Part 3 scoring/control logic changed: target assignment, random selection, D1/D2
///    logic, 'correct', d1Found, d2Found, timeout handling, the 10-minute D1 cap, the 15-minute total
///    cap, d2Ran, endReason, candidate interactability windows, panel gating, OnTrialStart/OnTrialEnd
///    and the coroutine yield structure are all identical to before.
///
/// NPC WARM-UP (additive):
///  - Each Part now runs an explicit NPC-only warm-up window at the very start, AFTER the participant is
///    teleported to the Part's start position but BEFORE the participant-facing Start panel is shown and
///    BEFORE OnTrialStart fires. During warm-up, SpawnerManager spawns/moves NPCs using its completely
///    normal runtime behaviour (unchanged spawnInterval/maxAgents/itinerary generation/AttractorDwell/
///    UncertainBehavior/MoveToTarget/pooling) via two public methods (BeginNpcWarmup/EndNpcWarmup) that only
///    toggle its existing spawning gate - no NPC movement/architecture code was touched.
///  - The participant literally cannot start the trial before warm-up ends, because the Start panel is not
///    shown (and therefore cannot be confirmed) until the warm-up wait completes.
///  - 'npcWarmupDuration' (Inspector-configurable, seconds) controls the wait; 0 disables the WAIT but NPC
///    presence itself is controlled by the per-Part 'npcEnabled' flag below (see PER-PART CONFIGURATION).
///  - Two observer events ("npc_warmup_start" / "npc_warmup_end") are raised through the existing
///    RaiseEvt(...) -> OnExperimentEvent pathway so DataCaptureManager can both log the window as an
///    events.csv marker AND suppress NPC telemetry (npc_state.csv / npc_events.csv) while it is open, so
///    warm-up NPC activity never contaminates the participant's NPC trial record. See
///    DataCaptureManager.NpcCaptureActive. These two events are raised ONLY when the current Part's
///    'npcEnabled' is true (see below) - an NPC-disabled Part never runs RunNpcWarmup() at all.
///
/// PER-PART CONFIGURATION (additive; supersedes the previous Part1-hardcoded "no NPC" rule):
///  - Each Part (part1Config/part2Config/part3Config, a PartCaptureConfig) now independently declares, via
///    the Inspector: whether NPCs exist at all (npcEnabled), whether NPC telemetry is written
///    (npcCaptureEnabled), whether gaze is recorded (gazeCaptureEnabled), and whether participant trajectory
///    is recorded (trajectoryCaptureEnabled). This is the SINGLE authoritative source for all four settings -
///    SpawnerManager (npcEnabled, via a direct read of CurrentPartConfig) and DataCaptureManager /
///    GazeRecorder (the three capture flags, via CurrentPartConfig) query it; none of them hold their own
///    independent copy. CurrentPartConfig is set once, at the very top of each RunPartX(), before part_start
///    is even raised.
///  - npcEnabled and npcCaptureEnabled are INDEPENDENT: npcEnabled=true + npcCaptureEnabled=false means NPCs
///    exist and move but are not recorded (this was Part2's original hardcoded behaviour, now just a
///    Part2Config default rather than special-cased code). npcEnabled=false + npcCaptureEnabled=true is
///    accepted (not blocked) but produces empty npc_state.csv/npc_events.csv content for that Part, since no
///    NPC can ever exist to capture; DataCaptureManager logs a one-time warning for this combination.
///  - Default values reproduce the exact pre-Stage-7 behaviour: Part1 = everything off; Part2 = NPCs on,
///    NPC capture off, gaze+trajectory on; Part3 = everything on.
///
/// END PANEL HARD STOP (additive):
///  - OnEndPanelShown is a new event, raised immediately before uiManager.ShowPartXEndPanel(...) is called
///    (i.e. at the exact moment identified by auditing the actual panel-show call sites - NOT at part_end,
///    which only fires later, after the participant confirms the panel). SpawnerManager subscribes to freeze
///    NPC movement (via MoveToTarget.PauseExternal + disabling UncertainBehavior - existing public APIs
///    only) and close the spawn gate; DataCaptureManager subscribes to hard-stop trajectory/gaze/NPC capture.
///    Both remain frozen/stopped through the entire End Panel wait, and only resume readiness for the NEXT
///    Part (SpawnerManager.ResetForNewPart(), called at the top of RunNpcWarmup()).
///  - part_end (raised after the participant confirms the End Panel) is UNCHANGED and still fires exactly
///    where it always did; this feature does not move it earlier and does not alter panel/UI semantics.
///
/// OnTrialStart / OnTrialEnd still fire exactly once each, for every Part including Part1.
/// </summary>
public class ExperimentManager : MonoBehaviour
{
    public event Action OnTrialStart;
    public event Action OnTrialEnd;

    /// <summary>
    /// NEW: raised exactly once per Part, immediately before that Part's End Panel is shown to the
    /// participant (before uiManager.ShowPartXEndPanel(...) is called) - i.e. strictly BEFORE part_end,
    /// which only fires later once the participant confirms the panel. This is the authoritative
    /// "hard stop" boundary: subscribers (SpawnerManager, DataCaptureManager) use it to freeze NPCs and stop
    /// all continuous data capture for the remainder of the End Panel's visible duration.
    /// </summary>
    public event Action OnEndPanelShown;

    /// <summary>Additive observer event for the data-capture layer (Revision 2). Never affects control flow.</summary>
    public event Action<ExperimentEventInfo> OnExperimentEvent;

    /// <summary>
    /// Per-Part configuration: whether NPCs exist, and whether each of the three continuous experimental
    /// capture streams is recorded, for one Part. This is the SINGLE authoritative source of truth for these
    /// four settings (Requirement: avoid duplicate sources of truth) - SpawnerManager, DataCaptureManager and
    /// GazeRecorder all read it via ExperimentManager.CurrentPartConfig rather than holding their own copies.
    /// </summary>
    [Serializable]
    public class PartCaptureConfig
    {
        [Tooltip("Whether the NPC population (SpawnerManager spawning/warm-up) exists at all during this " +
                 "Part. False means: no warm-up, no spawning, no NPCs ever appear - independent of the " +
                 "capture flags below.")]
        public bool npcEnabled = true;

        [Tooltip("Whether NPC telemetry (npc_state.csv / npc_events.csv) is written during this Part's " +
                 "trial. Independent of 'Npc Enabled': NPCs can exist and move (Npc Enabled = true) without " +
                 "being recorded (Npc Capture Enabled = false). If enabled while Npc Enabled is false, no " +
                 "NPCs will exist to capture - allowed, but DataCaptureManager logs a one-time warning.")]
        public bool npcCaptureEnabled = true;

        [Tooltip("Whether GazeRecorder writes gaze.csv rows during this Part's trial (OnTrialStart..OnTrialEnd, " +
                 "and never before part_start or during NPC warm-up).")]
        public bool gazeCaptureEnabled = true;

        [Tooltip("Whether participant trajectory.csv rows are written during this Part's trial " +
                 "(OnTrialStart..OnTrialEnd, and never before part_start or during NPC warm-up).")]
        public bool trajectoryCaptureEnabled = true;
    }

    /// <summary>Coarse Part 3 sub-phase label ("idle" / "d1_pending" / "d1_search" / "d2_pending" / "d2_search" / "ended").</summary>
    public string CurrentPhase { get; private set; } = "idle";

    [Header("References")]
    public UIManager uiManager;
    public ExternalSessionController sessionController;
    public Transform playerRig;
    public SpawnerManager spawner;
    public AgentProfile currentProfile;

    [Header("Part durations (seconds)")]
    public float part1Duration = 60f;
    public float part2FirstDuration = 60f;
    public float part2SecondDuration = 60f;

    [Header("Part 1 Configuration")]
    [Tooltip("NPC presence and per-stream capture for Part 1. Defaults reproduce the previous hard-coded " +
             "'Part1 = no NPC, no research capture' behaviour; change freely per Requirement 1.")]
    public PartCaptureConfig part1Config = new PartCaptureConfig
    {
        npcEnabled = false,
        npcCaptureEnabled = false,
        gazeCaptureEnabled = false,
        trajectoryCaptureEnabled = false
    };

    [Header("Part 2 Configuration")]
    [Tooltip("NPC presence and per-stream capture for Part 2. Defaults reproduce the previous behaviour: " +
             "NPCs on, NPC telemetry off, gaze + trajectory on.")]
    public PartCaptureConfig part2Config = new PartCaptureConfig
    {
        npcEnabled = true,
        npcCaptureEnabled = false,
        gazeCaptureEnabled = true,
        trajectoryCaptureEnabled = true
    };

    [Header("Part 3 Configuration")]
    [Tooltip("NPC presence and per-stream capture for Part 3. Defaults reproduce the previous behaviour: " +
             "everything on (full dataset).")]
    public PartCaptureConfig part3Config = new PartCaptureConfig
    {
        npcEnabled = true,
        npcCaptureEnabled = true,
        gazeCaptureEnabled = true,
        trajectoryCaptureEnabled = true
    };

    /// <summary>
    /// The PartCaptureConfig for whichever Part is currently running (set at the top of RunPart1/2/3, before
    /// part_start is raised). Null when no Part is running. This is the single value every other system
    /// (SpawnerManager, DataCaptureManager, GazeRecorder) reads - see the PER-PART CONFIGURATION class doc.
    /// </summary>
    public PartCaptureConfig CurrentPartConfig { get; private set; }

    /// <summary>
    /// True from the moment this Part's End Panel is shown (OnEndPanelShown raised) until the next Part's
    /// NPC lifecycle begins (SpawnerManager.ResetForNewPart(), called at the top of RunNpcWarmup()). Exposed
    /// for polling convenience; SpawnerManager/DataCaptureManager primarily react to the OnEndPanelShown
    /// event itself rather than polling this.
    /// </summary>
    public bool EndPanelActive { get; private set; }

    [Header("NPC Warm-up")]
    [Tooltip("Seconds of NPC-only warm-up run at the start of a Part that has 'Npc Enabled' = true in its " +
             "Part Configuration above, after the participant is teleported to the Part's start position but " +
             "BEFORE the participant-facing Start panel is shown and BEFORE OnTrialStart fires. During " +
             "warm-up, SpawnerManager spawns and moves NPCs using its completely normal runtime behaviour " +
             "(same spawnInterval, maxAgents, itinerary generation, AttractorDwell, UncertainBehavior, " +
             "MoveToTarget, pooling) so the NPC population has time to spawn and spread out through the " +
             "environment before the participant can begin the actual trial. This single duration applies to " +
             "every NPC-enabled Part - there is no separate per-Part duration field. Set to 0 to skip only " +
             "the WAIT (the Start panel is then shown immediately); NPC presence itself is still controlled " +
             "exclusively by 'Npc Enabled' above, never by this duration.")]
    public float npcWarmupDuration = 30f;

    [Header("Part1")]
    [Tooltip("Part1 start position (single).")]
    public Transform part1StartPosition;

    [Tooltip("List of target buildings for Part1. Each selection triggers a Continue panel.")]
    public GameObject[] part1TargetBuildings;

    [Header("Part2 - group configuration")]
    public Part2Group[] part2Groups;

    /// <summary>The four Phase 3 zones. GE = Grid Explored, GU = Grid Unexplored, OE = Organic Explored, OU = Organic Unexplored.</summary>
    public enum Part3Zone { GE, GU, OE, OU }

    /// <summary>Exactly two candidate destination buildings for one Phase 3 zone.</summary>
    [Serializable]
    public class Part3ZoneTargets
    {
        [Tooltip("First candidate destination building in this zone. Must have a Collider + TargetSelectable (and, for hover, InteractableHoverFeedback).")]
        public GameObject buildingA;

        [Tooltip("Second candidate destination building in this zone. Must have a Collider + TargetSelectable (and, for hover, InteractableHoverFeedback).")]
        public GameObject buildingB;
    }

    [Header("Part3 - fixed start points (one per zone)")]
    public Transform part3StartGE;
    public Transform part3StartGU;
    public Transform part3StartOE;
    public Transform part3StartOU;

    [Header("Part3 - candidate destination buildings (exactly 2 per zone)")]
    public Part3ZoneTargets part3ZoneGE = new Part3ZoneTargets();
    public Part3ZoneTargets part3ZoneGU = new Part3ZoneTargets();
    public Part3ZoneTargets part3ZoneOE = new Part3ZoneTargets();
    public Part3ZoneTargets part3ZoneOU = new Part3ZoneTargets();

    [Header("Part3 - timing")]
    [Tooltip("Maximum time (seconds) to search for Destination 1, measured from when its instruction panel is confirmed. Protocol: 600 (10 minutes).")]
    public float part3Destination1MaxSeconds = 600f;
    [Tooltip("Maximum total Phase 3 duration (seconds), measured from when the Start panel is confirmed. Protocol: 900 (15 minutes).")]
    public float part3TotalMaxSeconds = 900f;

    [Serializable]
    public class Part2Group
    {
        public Transform[] startPositions;
        public Transform[] teleportPositions;
    }

    /// <summary>One row of the fixed 12-group Phase 3 assignment table.</summary>
    private struct Part3GroupDef
    {
        public readonly Part3Zone StartZone;
        public readonly Part3Zone Destination1Zone;
        public readonly Part3Zone Destination2Zone;

        public Part3GroupDef(Part3Zone start, Part3Zone d1, Part3Zone d2)
        {
            StartZone = start;
            Destination1Zone = d1;
            Destination2Zone = d2;
        }
    }

    // Fixed group -> (start zone, destination 1 zone, destination 2 zone). Index 0 == Group 1.
    private static readonly Part3GroupDef[] Part3GroupTable =
    {
        new Part3GroupDef(Part3Zone.GE, Part3Zone.GU, Part3Zone.OE), // Group 1
        new Part3GroupDef(Part3Zone.GE, Part3Zone.OE, Part3Zone.GU), // Group 2
        new Part3GroupDef(Part3Zone.GE, Part3Zone.OU, Part3Zone.GE), // Group 3
        new Part3GroupDef(Part3Zone.GU, Part3Zone.GE, Part3Zone.OU), // Group 4
        new Part3GroupDef(Part3Zone.GU, Part3Zone.OE, Part3Zone.GU), // Group 5
        new Part3GroupDef(Part3Zone.GU, Part3Zone.OU, Part3Zone.GE), // Group 6
        new Part3GroupDef(Part3Zone.OE, Part3Zone.GE, Part3Zone.OU), // Group 7
        new Part3GroupDef(Part3Zone.OE, Part3Zone.GU, Part3Zone.OE), // Group 8
        new Part3GroupDef(Part3Zone.OE, Part3Zone.OU, Part3Zone.GE), // Group 9
        new Part3GroupDef(Part3Zone.OU, Part3Zone.GE, Part3Zone.OU), // Group 10
        new Part3GroupDef(Part3Zone.OU, Part3Zone.GU, Part3Zone.OE), // Group 11
        new Part3GroupDef(Part3Zone.OU, Part3Zone.OE, Part3Zone.GU), // Group 12
    };

    // internal state
    private Coroutine runningCoroutine;
    private bool targetWasSelected;
    private object lastSelectedTargetData;

    private enum RunningPart { None, Part1, Part2, Part3 }
    private RunningPart currentRunningPart = RunningPart.None;
    private bool part1ContinuePanelOpen = false;

    private void Awake()
    {
        if (uiManager == null) Debug.LogWarning("ExperimentManager: UIManager not assigned in inspector.");
        if (sessionController == null) Debug.LogWarning("ExperimentManager: ExternalSessionController not assigned in inspector.");
    }

    private void Start()
    {
        // Baseline: every Phase 3 candidate building must behave as an ordinary (non-interactable) building
        // until Phase 3 assigns it as a destination. Visibility is NOT touched here.
        SetAllPart3CandidatesInteractable(false);

        if (sessionController != null && sessionController.autoStart)
        {
            StartSelectedPart();
        }
    }

    public void StartSelectedPart()
    {
        if (sessionController == null)
        {
            Debug.LogError("ExperimentManager: Cannot StartSelectedPart because sessionController is null.");
            return;
        }

        switch (sessionController.selectedPart)
        {
            case ExternalSessionController.ExperimentPart.Part1: StartPart1(); break;
            case ExternalSessionController.ExperimentPart.Part2: StartPart2(); break;
            case ExternalSessionController.ExperimentPart.Part3: StartPart3(); break;
        }
    }

    public void RegisterTargetSelection(object targetData = null)
    {
        targetWasSelected = true;
        lastSelectedTargetData = targetData;
        Debug.Log("Target selected: " + (targetData ?? "unknown"));

        if (currentRunningPart == RunningPart.Part1 && !part1ContinuePanelOpen && uiManager != null)
        {
            part1ContinuePanelOpen = true;
            uiManager.ShowPart1ContinuePanel(() =>
            {
                part1ContinuePanelOpen = false;
            });
        }
    }

    #region Public API to start each part

    public void StartPart1()
    {
        if (runningCoroutine != null) StopCoroutine(runningCoroutine);
        runningCoroutine = StartCoroutine(RunPart1());
    }

    public void StartPart2()
    {
        if (runningCoroutine != null) StopCoroutine(runningCoroutine);
        runningCoroutine = StartCoroutine(RunPart2());
    }

    public void StartPart3()
    {
        if (runningCoroutine != null) StopCoroutine(runningCoroutine);
        runningCoroutine = StartCoroutine(RunPart3());
    }

    #endregion

    #region NPC warm-up (additive; see class-level doc comment)

    /// <summary>
    /// Runs the NPC-only warm-up window for the Part currently starting. Called uniformly by ALL THREE
    /// RunPartX() methods (no more Part1 special-casing at the call site) - NPC presence is now controlled
    /// exclusively by 'cfg.npcEnabled' (Requirement 3), not by which Part structurally calls this method.
    ///
    /// Always calls spawner.ResetForNewPart() FIRST, unconditionally, regardless of cfg/npcEnabled/duration -
    /// this clears any End-Panel hard-stop left over from whichever Part ran previously (see
    /// SpawnerManager.ResetForNewPart doc comment), and must happen before any warm-up/trial spawning
    /// decision for the new Part is evaluated.
    ///
    /// If cfg is null or cfg.npcEnabled is false: returns immediately - no defaultProfile assignment, no
    /// BeginNpcWarmup(), no npc_warmup_start/end events, no wait. This Part is NPC-free.
    ///
    /// Otherwise: assigns spawner.defaultProfile, then (if npcWarmupDuration > 0) opens SpawnerManager's
    /// warm-up spawning gate (BeginNpcWarmup), waits 'npcWarmupDuration' seconds of scaled time (consistent
    /// with the existing Part-duration timers below, which also use Time.deltaTime rather than realtime),
    /// then closes the gate (EndNpcWarmup) immediately before returning. Never shows a panel, never fires
    /// OnTrialStart/OnTrialEnd, and never touches NPC movement/NavMeshAgent/Animator state directly - it only
    /// toggles SpawnerManager's existing spawning gates via its public API and raises two observer events for
    /// DataCaptureManager. When npcWarmupDuration &lt;= 0 this only skips the WAIT (NPCs are still enabled
    /// for the Part and can spawn once OnTrialStart fires, via SpawnerManager's own trialActive gate).
    /// </summary>
    private IEnumerator RunNpcWarmup(PartCaptureConfig cfg, string partLabel)
    {
        if (spawner != null)
            spawner.ResetForNewPart();

        if (cfg == null || !cfg.npcEnabled)
            yield break;

        if (spawner != null)
            spawner.defaultProfile = currentProfile;

        if (npcWarmupDuration <= 0f)
            yield break;

        if (spawner != null)
            spawner.BeginNpcWarmup();

        RaiseEvt("npc_warmup_start", partLabel, numValue: npcWarmupDuration);

        float wt = 0f;
        while (wt < npcWarmupDuration)
        {
            wt += Time.deltaTime;
            yield return null;
        }

        RaiseEvt("npc_warmup_end", partLabel, numValue: wt);

        if (spawner != null)
            spawner.EndNpcWarmup();
    }

    #endregion

    #region Part implementations (coroutines)

    private IEnumerator RunPart1()
    {
        currentRunningPart = RunningPart.Part1;
        CurrentPartConfig = part1Config;
        EndPanelActive = false;
        RaiseEvt("part_start", "Part1");

        ConfigureTargetsForPart1();
        TeleportPlayerTo(part1StartPosition);

        // NPC presence is now controlled entirely by part1Config.npcEnabled (Requirement 1/3) - no more
        // Part1-specific special-casing here. With the shipped default (npcEnabled=false) this call returns
        // immediately after clearing any stale End-Panel hard-stop, exactly reproducing "Part1 is NPC-free".
        yield return RunNpcWarmup(part1Config, "Part1");

        bool started = false;
        RaiseEvt("panel_shown", "Part1", label: "part1_start");
        uiManager.ShowPart1StartPanel(() => { started = true; RaiseEvt("panel_confirmed", "Part1", label: "part1_start"); });
        yield return new WaitUntil(() => started);

        OnTrialStart?.Invoke();

        targetWasSelected = false;
        float t = 0f;
        while (t < part1Duration)
        {
            t += Time.deltaTime;
            yield return null;
        }

        OnTrialEnd?.Invoke();

        // Requirement 2: the End Panel hard stop. Raised BEFORE the panel is instantiated/shown, so
        // subscribers (SpawnerManager, DataCaptureManager) freeze NPCs/capture before the participant can
        // see anything - not after they confirm it (that remains part_end, raised further below, unchanged).
        EndPanelActive = true;
        OnEndPanelShown?.Invoke();

        bool confirmed = false;
        RaiseEvt("panel_shown", "Part1", label: "part1_end");
        uiManager.ShowPart1EndPanel(() => { confirmed = true; RaiseEvt("panel_confirmed", "Part1", label: "part1_end"); });
        yield return new WaitUntil(() => confirmed);

        RaiseEvt("part_end", "Part1");
        currentRunningPart = RunningPart.None;
    }

    private IEnumerator RunPart2()
    {
        currentRunningPart = RunningPart.Part2;
        CurrentPartConfig = part2Config;
        EndPanelActive = false;
        RaiseEvt("part_start", "Part2");

        int gIdx = (sessionController != null) ? sessionController.part2GroupIndex : 0;
        if (part2Groups == null || part2Groups.Length == 0)
        {
            Debug.LogError("ExperimentManager.RunPart2: No Part2 groups configured in inspector.");
            yield break;
        }
        gIdx = Mathf.Clamp(gIdx, 0, part2Groups.Length - 1);
        Part2Group group = part2Groups[gIdx];

        Transform chosenStart = ChooseRandomFromArray(group.startPositions);
        Transform chosenTeleport = ChooseRandomFromArray(group.teleportPositions);

        TeleportPlayerTo(chosenStart);

        yield return RunNpcWarmup(part2Config, "Part2");

        bool started = false;
        RaiseEvt("panel_shown", "Part2", label: "part2_start");
        uiManager.ShowPart2StartPanel(() => { started = true; RaiseEvt("panel_confirmed", "Part2", label: "part2_start"); });
        yield return new WaitUntil(() => started);

        OnTrialStart?.Invoke();

        targetWasSelected = false;
        float t = 0f;
        while (t < part2FirstDuration)
        {
            t += Time.deltaTime;
            yield return null;
        }

        bool teleported = false;
        RaiseEvt("panel_shown", "Part2", label: "part2_continue");
        uiManager.ShowPart2ContinuePanel(() =>
        {
            RaiseEvt("panel_confirmed", "Part2", label: "part2_continue");
            TeleportPlayerTo(chosenTeleport ?? (group.teleportPositions != null && group.teleportPositions.Length > 0 ? group.teleportPositions[0] : null));
            teleported = true;
        });

        yield return new WaitUntil(() => teleported);

        t = 0f;
        while (t < part2SecondDuration)
        {
            t += Time.deltaTime;
            yield return null;
        }

        OnTrialEnd?.Invoke();

        // Requirement 2: the End Panel hard stop (see RunPart1() for the full explanation).
        EndPanelActive = true;
        OnEndPanelShown?.Invoke();

        bool confirmed = false;
        RaiseEvt("panel_shown", "Part2", label: "part2_end");
        uiManager.ShowPart2EndPanel(() => { confirmed = true; RaiseEvt("panel_confirmed", "Part2", label: "part2_end"); });
        yield return new WaitUntil(() => confirmed);

        RaiseEvt("part_end", "Part2");
        currentRunningPart = RunningPart.None;
    }

    private IEnumerator RunPart3()
    {
        currentRunningPart = RunningPart.Part3;
        CurrentPartConfig = part3Config;
        EndPanelActive = false;
        RaiseEvt("part_start", "Part3");
        SetPhase("idle");

        // Clean slate: no candidate is interactable (covers a re-run after an interrupted previous Phase 3).
        SetAllPart3CandidatesInteractable(false);

        // --- 1. Resolve the participant's group (index 0..11 -> Group 1..12). ---
        int groupNumber = 1;
        if (sessionController != null)
        {
            int requested = sessionController.part3GroupIndex + 1;
            groupNumber = Mathf.Clamp(requested, 1, Part3GroupTable.Length);
            if (requested != groupNumber)
            {
                Debug.LogWarning($"[ExperimentManager] Part3 group index {sessionController.part3GroupIndex} is out of range (valid 0..{Part3GroupTable.Length - 1}); clamped to group {groupNumber}.");
            }
        }
        Part3GroupDef def = Part3GroupTable[groupNumber - 1];

        // --- 2. Resolve the fixed start point and the two destination zones. ---
        Transform startPoint = ResolvePart3Start(def.StartZone);
        Part3ZoneTargets d1ZoneTargets = ResolvePart3ZoneTargets(def.Destination1Zone);
        Part3ZoneTargets d2ZoneTargets = ResolvePart3ZoneTargets(def.Destination2Zone);

        if (startPoint == null)
        {
            Debug.LogError($"[ExperimentManager] Part3 start point for zone {def.StartZone} is not assigned in the inspector. The participant will NOT be moved for this session.");
        }

        // --- 3. Independently pick ONE building from each destination zone. Fixed for the whole session. ---
        GameObject destination1Building = ChoosePart3Building(d1ZoneTargets);
        GameObject destination2Building = ChoosePart3Building(d2ZoneTargets);

        if (destination1Building == null)
            Debug.LogError($"[ExperimentManager] Part3 Destination 1 zone {def.Destination1Zone} has no candidate buildings assigned.");
        if (destination2Building == null)
            Debug.LogError($"[ExperimentManager] Part3 Destination 2 zone {def.Destination2Zone} has no candidate buildings assigned.");

        string d1AssignedId = GetAssignedTargetId(destination1Building);
        string d2AssignedId = GetAssignedTargetId(destination2Building);
        bool d1RequireCorrect = !string.IsNullOrEmpty(d1AssignedId);
        bool d2RequireCorrect = !string.IsNullOrEmpty(d2AssignedId);
        if (!d1RequireCorrect)
            Debug.LogError("[ExperimentManager] Part3 Destination 1 building has no TargetSelectable.targetId; any building click will end Destination 1.");
        if (!d2RequireCorrect)
            Debug.LogError("[ExperimentManager] Part3 Destination 2 building has no TargetSelectable.targetId; any building click will end Destination 2.");

        // --- 3b. Generate the target-building preview images for BOTH destinations. ---
        if (uiManager != null)
        {
            uiManager.PrepareDestinationImages(destination1Building, destination2Building);
        }

        // --- 4. Hide the Part 1 test targets only (they must not be clickable during Phase 3). ---
        if (part1TargetBuildings != null)
        {
            foreach (var go in part1TargetBuildings)
                if (go != null) go.SetActive(false);
        }

        // --- 5. Move the participant to the fixed start point (existing mechanism). ---
        TeleportPlayerTo(startPoint);

        // --- 6. Persist the assignment immediately via the capture event (legacy CSV removed). ---
        AppendPart3Log("assigned", groupNumber, def, startPoint, destination1Building, destination2Building,
            selectedId: "", found: false, elapsedSeconds: 0.0, notes: "");

        // --- 6b. NPC warm-up: NPCs spawn/spread out via SpawnerManager's normal runtime behaviour, BEFORE
        // the Phase 3 Start panel is shown. Not part of the participant's trial - see DataCaptureManager.
        yield return RunNpcWarmup(part3Config, "Part3");

        // --- 7. Phase 3 Start panel. (All candidates are still non-interactable ordinary buildings.) ---
        bool started = false;
        RaiseEvt("panel_shown", "Part3", label: "part3_start");
        uiManager.ShowPart3StartPanel(() => { started = true; RaiseEvt("panel_confirmed", "Part3", label: "part3_start"); });
        yield return new WaitUntil(() => started);

        OnTrialStart?.Invoke();
        SetPhase("d1_pending");

        double phaseStartRealtime = Time.realtimeSinceStartupAsDouble;

        // ============================ DESTINATION 1 ============================
        bool dest1Confirmed = false;
        RaiseEvt("destination_shown", "Part3", label: "D1");
        RaiseEvt("panel_shown", "Part3", label: "part3_dest1");
        uiManager.ShowPart3Destination1Panel(() =>
        {
            dest1Confirmed = true;
            RaiseEvt("panel_confirmed", "Part3", label: "part3_dest1");
            RaiseEvt("destination_confirmed", "Part3", label: "D1");
        });
        yield return new WaitUntil(() => dest1Confirmed);
        SetPhase("d1_search");

        // Destination 1 assigned: make ONLY destination1Building interactable (TargetSelectable + hover).
        SetAllPart3CandidatesInteractable(false);
        SetCandidateInteractable(destination1Building, true);
        RaiseEvt("candidate_interactable_on", "Part3", label: "D1", building: destination1Building);

        GameObject dest1ShrunkInstance = null;
        if (uiManager != null && uiManagerHasShrunkTemplate())
            dest1ShrunkInstance = uiManager.CreateAndAnchorShrunkPanel(uiManager.part3Dest1ShrunkTemplate, UIManager.Corner.TopRight);

        targetWasSelected = false;
        lastSelectedTargetData = null;
        bool d1Found = false;
        double d1StartRealtime = Time.realtimeSinceStartupAsDouble;

        while (!d1Found
               && (Time.realtimeSinceStartupAsDouble - d1StartRealtime) < part3Destination1MaxSeconds
               && (Time.realtimeSinceStartupAsDouble - phaseStartRealtime) < part3TotalMaxSeconds)
        {
            if (targetWasSelected)
            {
                string clickedId = lastSelectedTargetData != null ? lastSelectedTargetData.ToString() : "";
                bool correct = d1RequireCorrect ? (clickedId == d1AssignedId) : true;

                AppendPart3Log("d1_click", groupNumber, def, startPoint, destination1Building, destination2Building,
                    selectedId: clickedId, found: correct,
                    elapsedSeconds: Time.realtimeSinceStartupAsDouble - d1StartRealtime,
                    notes: correct ? "correct" : "wrong_building");

                if (correct) d1Found = true;

                // Consume the click. A wrong-building click does NOT end the sub-phase and shows no feedback.
                targetWasSelected = false;
                lastSelectedTargetData = null;
            }
            yield return null;
        }

        string d1SelectedId = d1Found ? d1AssignedId : "";
        double d1Elapsed = Time.realtimeSinceStartupAsDouble - d1StartRealtime;
        bool totalTimeUpAfterD1 = (Time.realtimeSinceStartupAsDouble - phaseStartRealtime) >= part3TotalMaxSeconds;

        if (dest1ShrunkInstance != null) { Destroy(dest1ShrunkInstance); dest1ShrunkInstance = null; }

        // Destination 1 finished: the D1 target is a plain non-interactable building again.
        SetCandidateInteractable(destination1Building, false);
        RaiseEvt("candidate_interactable_off", "Part3", label: "D1", building: destination1Building);

        AppendPart3Log("d1_result", groupNumber, def, startPoint, destination1Building, destination2Building,
            selectedId: d1SelectedId, found: d1Found, elapsedSeconds: d1Elapsed,
            notes: d1Found ? "found" : (totalTimeUpAfterD1 ? "total_time_reached" : "d1_timeout_10min"));

        // ============================ DESTINATION 2 ============================
        bool d2Ran = false;
        bool d2Found = false;
        string d2SelectedId = "";
        double d2Elapsed = 0.0;

        if ((Time.realtimeSinceStartupAsDouble - phaseStartRealtime) < part3TotalMaxSeconds)
        {
            d2Ran = true;
            SetPhase("d2_pending");

            bool dest2Confirmed = false;
            RaiseEvt("destination_shown", "Part3", label: "D2");
            RaiseEvt("panel_shown", "Part3", label: "part3_dest2");
            uiManager.ShowPart3Destination2Panel(() =>
            {
                dest2Confirmed = true;
                RaiseEvt("panel_confirmed", "Part3", label: "part3_dest2");
                RaiseEvt("destination_confirmed", "Part3", label: "D2");
            });
            yield return new WaitUntil(() => dest2Confirmed);
            SetPhase("d2_search");

            // Destination 2 assigned: make ONLY destination2Building interactable.
            SetAllPart3CandidatesInteractable(false);
            SetCandidateInteractable(destination2Building, true);
            RaiseEvt("candidate_interactable_on", "Part3", label: "D2", building: destination2Building);

            GameObject dest2ShrunkInstance = null;
            if (uiManager != null && uiManagerHasShrunkTemplatePart2())
                dest2ShrunkInstance = uiManager.CreateAndAnchorShrunkPanel(uiManager.part3Dest2ShrunkTemplate, UIManager.Corner.TopRight);

            targetWasSelected = false;
            lastSelectedTargetData = null;
            double d2StartRealtime = Time.realtimeSinceStartupAsDouble;

            while (!d2Found
                   && (Time.realtimeSinceStartupAsDouble - phaseStartRealtime) < part3TotalMaxSeconds)
            {
                if (targetWasSelected)
                {
                    string clickedId = lastSelectedTargetData != null ? lastSelectedTargetData.ToString() : "";
                    bool correct = d2RequireCorrect ? (clickedId == d2AssignedId) : true;

                    AppendPart3Log("d2_click", groupNumber, def, startPoint, destination1Building, destination2Building,
                        selectedId: clickedId, found: correct,
                        elapsedSeconds: Time.realtimeSinceStartupAsDouble - d2StartRealtime,
                        notes: correct ? "correct" : "wrong_building");

                    if (correct) d2Found = true;

                    targetWasSelected = false;
                    lastSelectedTargetData = null;
                }
                yield return null;
            }

            d2SelectedId = d2Found ? d2AssignedId : "";
            d2Elapsed = Time.realtimeSinceStartupAsDouble - d2StartRealtime;

            if (dest2ShrunkInstance != null) { Destroy(dest2ShrunkInstance); dest2ShrunkInstance = null; }

            // Destination 2 finished: the D2 target is a plain non-interactable building again.
            SetCandidateInteractable(destination2Building, false);
            RaiseEvt("candidate_interactable_off", "Part3", label: "D2", building: destination2Building);
        }
        else
        {
            Debug.Log("[ExperimentManager] Part3: total 15-minute limit already reached after Destination 1; Destination 2 skipped.");
        }

        // ============================ END ============================
        double phaseTotalElapsed = Time.realtimeSinceStartupAsDouble - phaseStartRealtime;
        string endReason =
            (d1Found && d2Found) ? "both_destinations_found" :
            (phaseTotalElapsed >= part3TotalMaxSeconds) ? "total_time_15min_reached" :
            (!d2Ran) ? "d2_skipped_no_time" :
            (d2Found) ? "d2_found_d1_missed" :
            "d2_not_found";

        AppendPart3Log(d2Ran ? "d2_result" : "d2_skipped", groupNumber, def, startPoint, destination1Building, destination2Building,
            selectedId: d2SelectedId, found: d2Found, elapsedSeconds: d2Elapsed, notes: endReason);
        AppendPart3Log("phase_end", groupNumber, def, startPoint, destination1Building, destination2Building,
            selectedId: "", found: (d1Found && d2Found), elapsedSeconds: phaseTotalElapsed, notes: endReason);

        OnTrialEnd?.Invoke();
        SetPhase("ended");

        // Phase 3 is over: no Phase 3 target-specific interaction remains anywhere.
        SetAllPart3CandidatesInteractable(false);

        // Requirement 2: the End Panel hard stop (see RunPart1() for the full explanation).
        EndPanelActive = true;
        OnEndPanelShown?.Invoke();

        bool confirmed = false;
        RaiseEvt("panel_shown", "Part3", label: "part3_end");
        uiManager.ShowPart3EndPanel(() => { confirmed = true; RaiseEvt("panel_confirmed", "Part3", label: "part3_end"); });
        yield return new WaitUntil(() => confirmed);

        RaiseEvt("part_end", "Part3");
        currentRunningPart = RunningPart.None;
    }
    #endregion

    #region Utility / target configuration helpers

    private void ConfigureTargetsForPart1()
    {
        if (part1TargetBuildings != null)
        {
            foreach (var go in part1TargetBuildings)
            {
                if (go != null) go.SetActive(true);
            }
        }
    }

    private void TeleportPlayerTo(Transform dest)
    {
        if (dest == null || playerRig == null) return;

        // Data-capture observer hook (Revision 2): announce the teleport with the raw pre/post positions
        // BEFORE the assignment, so trajectory analysis can preserve the discontinuity. Mechanics unchanged.
        Vector3 fromPos = playerRig.position;
        RaiseEvt("teleport", stringValue: dest.name, fromPos: fromPos, toPos: dest.position);

        playerRig.position = dest.position;
        playerRig.rotation = dest.rotation;
    }

    private Transform ChooseRandomFromArray(Transform[] arr)
    {
        if (arr == null || arr.Length == 0) return null;
        int idx = UnityEngine.Random.Range(0, arr.Length);
        return arr[idx];
    }

    // ---- Part 3 helpers ------------------------------------------------------------------------

    private Transform ResolvePart3Start(Part3Zone zone)
    {
        switch (zone)
        {
            case Part3Zone.GE: return part3StartGE;
            case Part3Zone.GU: return part3StartGU;
            case Part3Zone.OE: return part3StartOE;
            case Part3Zone.OU: return part3StartOU;
            default: return null;
        }
    }

    private Part3ZoneTargets ResolvePart3ZoneTargets(Part3Zone zone)
    {
        switch (zone)
        {
            case Part3Zone.GE: return part3ZoneGE;
            case Part3Zone.GU: return part3ZoneGU;
            case Part3Zone.OE: return part3ZoneOE;
            case Part3Zone.OU: return part3ZoneOU;
            default: return null;
        }
    }

    /// <summary>Randomly and independently pick one of the two candidate buildings in a zone.</summary>
    private GameObject ChoosePart3Building(Part3ZoneTargets zone)
    {
        if (zone == null) return null;

        bool hasA = zone.buildingA != null;
        bool hasB = zone.buildingB != null;

        if (hasA && hasB)
            return (UnityEngine.Random.Range(0, 2) == 0) ? zone.buildingA : zone.buildingB;
        if (hasA) return zone.buildingA;
        if (hasB) return zone.buildingB;
        return null;
    }

    /// <summary>
    /// Return the TargetSelectable.targetId of the given building (checked on the object itself and its
    /// children), or null if there is no TargetSelectable. Never modifies TargetSelectable or building state.
    /// </summary>
    private string GetAssignedTargetId(GameObject building)
    {
        if (building == null) return null;

        TargetSelectable ts = building.GetComponent<TargetSelectable>();
        if (ts == null) ts = building.GetComponentInChildren<TargetSelectable>(true);

        return (ts != null) ? ts.targetId : null;
    }

    /// <summary>
    /// Enable or disable the two interaction components on ONE candidate building. Visibility is NEVER touched.
    /// </summary>
    private void SetCandidateInteractable(GameObject building, bool interactable)
    {
        if (building == null) return;

        TargetSelectable ts = building.GetComponent<TargetSelectable>();
        if (ts == null) ts = building.GetComponentInChildren<TargetSelectable>(true);
        if (ts != null && ts.enabled != interactable) ts.enabled = interactable;

        InteractableHoverFeedback hover = building.GetComponent<InteractableHoverFeedback>();
        if (hover == null) hover = building.GetComponentInChildren<InteractableHoverFeedback>(true);
        if (hover != null && hover.enabled != interactable) hover.enabled = interactable;
    }

    /// <summary>Apply SetCandidateInteractable to both candidates of one zone.</summary>
    private void SetZoneCandidatesInteractable(Part3ZoneTargets zone, bool interactable)
    {
        if (zone == null) return;
        SetCandidateInteractable(zone.buildingA, interactable);
        SetCandidateInteractable(zone.buildingB, interactable);
    }

    /// <summary>Apply SetCandidateInteractable to every Phase 3 candidate building in every zone.</summary>
    private void SetAllPart3CandidatesInteractable(bool interactable)
    {
        SetZoneCandidatesInteractable(part3ZoneGE, interactable);
        SetZoneCandidatesInteractable(part3ZoneGU, interactable);
        SetZoneCandidatesInteractable(part3ZoneOE, interactable);
        SetZoneCandidatesInteractable(part3ZoneOU, interactable);
    }

    private bool uiManagerHasShrunkTemplate()
    {
        return uiManager != null && uiManager.part3Dest1ShrunkTemplate != null;
    }

    private bool uiManagerHasShrunkTemplatePart2()
    {
        return uiManager != null && uiManager.part3Dest2ShrunkTemplate != null;
    }

    // ---- Part 3 outcome sink (Revision 2: raises OnExperimentEvent, NO file I/O) ------------------

    /// <summary>
    /// Revision 2: the standalone Part 3 CSV has been removed. This method performs NO file I/O.
    /// It only raises the structured OnExperimentEvent that DataCaptureManager records into events.csv.
    /// Every argument is a value already computed by the (unchanged) Part 3 scoring logic - this method
    /// only observes them.
    /// </summary>
    private void AppendPart3Log(string eventName, int groupNumber, Part3GroupDef def,
        Transform startPoint, GameObject d1Building, GameObject d2Building,
        string selectedId, bool found, double elapsedSeconds, string notes)
    {
        ExperimentEventInfo info = new ExperimentEventInfo
        {
            type = "part3_log",
            part3Event = eventName,
            part = "Part3",
            phase = CurrentPhase,
            label = "",
            stringValue = selectedId ?? "",
            numValue = elapsedSeconds,
            boolValue = found,
            notes = notes ?? "",
            groupNumber = groupNumber,
            startZone = def.StartZone.ToString(),
            d1Zone = def.Destination1Zone.ToString(),
            d2Zone = def.Destination2Zone.ToString(),
            startPointName = startPoint != null ? startPoint.name : "",
            d1Building = d1Building,
            d2Building = d2Building,
            d1TargetId = GetAssignedTargetId(d1Building) ?? "",
            d2TargetId = GetAssignedTargetId(d2Building) ?? ""
        };

        try { OnExperimentEvent?.Invoke(info); }
        catch (Exception ex) { Debug.LogWarning("[ExperimentManager] OnExperimentEvent (part3_log) handler threw: " + ex.Message); }
    }

    // ---- Data-capture event helpers -------------------------------------------------------------

    private void RaiseEvt(string type, string part = null, string label = "", string stringValue = "",
        double numValue = 0.0, bool boolValue = false, GameObject building = null,
        Vector3 fromPos = default, Vector3 toPos = default)
    {
        ExperimentEventInfo info = new ExperimentEventInfo
        {
            type = type,
            part3Event = "",
            part = string.IsNullOrEmpty(part) ? CurrentRunningPartString() : part,
            phase = CurrentPhase,
            label = label,
            stringValue = stringValue,
            numValue = numValue,
            boolValue = boolValue,
            notes = "",
            building = building,
            fromPos = fromPos,
            toPos = toPos
        };

        try { OnExperimentEvent?.Invoke(info); }
        catch (Exception ex) { Debug.LogWarning("[ExperimentManager] OnExperimentEvent (" + type + ") handler threw: " + ex.Message); }
    }

    private void SetPhase(string phase)
    {
        CurrentPhase = phase;
        RaiseEvt("phase_start", "Part3", label: phase);
    }

    private string CurrentRunningPartString()
    {
        switch (currentRunningPart)
        {
            case RunningPart.Part1: return "Part1";
            case RunningPart.Part2: return "Part2";
            case RunningPart.Part3: return "Part3";
            default: return "";
        }
    }

    #endregion
}

/// <summary>
/// Payload for ExperimentManager.OnExperimentEvent (Revision 2 data capture). Plain value type; carries
/// everything DataCaptureManager needs to write one or more events.csv rows without holding coroutine-local
/// state. Populated by ExperimentManager only.
/// </summary>
[Serializable]
public struct ExperimentEventInfo
{
    public string type;         // e.g. "part_start", "panel_shown", "teleport", "part3_log", ...
    public string part3Event;   // raw legacy Part 3 log event name when type == "part3_log", else ""
    public string part;         // "Part1" / "Part2" / "Part3" / ""
    public string phase;        // ExperimentManager.CurrentPhase at raise time
    public string label;        // "D1" / "D2" / a panel key / a phase name / ""
    public string stringValue;  // clicked targetId / teleport destination name / free string
    public double numValue;     // elapsed seconds / phase total elapsed / 0
    public bool boolValue;      // correct / found
    public string notes;        // legacy Part 3 "notes" / endReason string

    public int groupNumber;         // Part 3 group (1..12), else 0
    public string startZone;        // Part 3 start zone label
    public string d1Zone;           // Part 3 destination 1 zone label
    public string d2Zone;           // Part 3 destination 2 zone label
    public string startPointName;   // Part 3 start Transform name
    public GameObject d1Building;    // Part 3 assigned destination-1 building (coroutine local, captured here)
    public GameObject d2Building;    // Part 3 assigned destination-2 building
    public string d1TargetId;       // TargetSelectable.targetId of d1Building (provenance only)
    public string d2TargetId;       // TargetSelectable.targetId of d2Building (provenance only)

    public GameObject building;      // building referenced by candidate_interactable_on/off
    public Vector3 fromPos;          // teleport: player position before
    public Vector3 toPos;            // teleport: destination position
}
