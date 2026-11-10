using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ExperimentManager
///
/// Part 1 / Part 2: unchanged.
///
/// Part 3 (15-minute Main Experimental Phase):
///  - The participant's assigned group (0..11, from ExternalSessionController.ActivePart3GroupIndex; Group Number == Group Index; an out-of-range index refuses the Part, it is never clamped)
///    is looked up in a fixed table that yields (StartZone, Destination1Zone, Destination2Zone).
///  - StartZone selects one of four FIXED start Transforms. The participant is teleported there.
///  - Destination1Zone / Destination2Zone each have exactly two candidate buildings; exactly ONE per
///    destination is chosen RANDOMLY and INDEPENDENTLY per session and kept fixed (coroutine locals).
///  - VISIBILITY vs INTERACTABILITY vs TARGET ASSIGNMENT remain three separate concepts (unchanged).
///  - Timing (valid exposure, see Part3ValidTiming): the D1 search is capped at part3Destination1MaxSeconds counted from the D1
///    Continue click; the D1 + D2 searches together are capped at part3TotalMaxSeconds, D2 receiving the budget left after the
///    MEASURED interval 1. Reading the destination panels never consumes either budget.
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
///    (npcCaptureEnabled), whether gaze is recorded (gazeCaptureEnabled), whether participant trajectory
///    is recorded (trajectoryCaptureEnabled), and which itinerary type NPCs walk: the Part 3 attractor
///    itinerary or the Part 2 wandering-point itinerary (npcDirectedMovement). This is the SINGLE authoritative source for
///    all five settings - SpawnerManager and DataCaptureManager / GazeRecorder all read it via
///    ExperimentManager.CurrentPartConfig rather than holding their own copies. CurrentPartConfig is set
///    once, at the very top of each RunPartX(), before part_start is even raised.
///  - npcEnabled and npcCaptureEnabled are INDEPENDENT: npcEnabled=true + npcCaptureEnabled=false means NPCs
///    exist and move but are not recorded (this was Part2's original hardcoded behaviour, now just a
///    Part2Config default rather than special-cased code). npcEnabled=false + npcCaptureEnabled=true is
///    accepted (not blocked) but produces empty npc_state.csv/npc_events.csv content for that Part, since no
///    NPC can ever exist to capture; DataCaptureManager logs a one-time warning for this combination.
///  - npcDirectedMovement: selects the itinerary type SpawnerManager builds for each NPC (the serialized bool is
///    kept for scene compatibility; SpawnerManager maps it to NpcMovementMode). When true (Part3's default):
///    Part3AttractorItinerary, entrance -> attractors -> exit. When false (Part2's default):
///    Part2WanderingItinerary, entrance -> SpawnerManager.wanderingPoints -> exit, with no dwell. Both are
///    finite itineraries generated once per NPC life. Only meaningful when npcEnabled is also true.
///  - Defaults: Part1 = everything off; Part2 = NPCs on (wandering itinerary), NPC capture off, gaze+trajectory
///    on; Part3 = everything on (attractor itinerary, as it always was).
///
/// END PANEL HARD STOP (additive):
///  - OnEndPanelShown is raised from HandleEndPanelDisplayed, i.e. at the moment the End panel actually becomes visible
///    (UIManager 'onShown': right after the one-time placement, or after the settle wait ends) - NOT at EndTrial(), and NOT at
///    part_end, which only fires later, after the participant confirms the panel. SpawnerManager subscribes to freeze
///    NPC movement (via MoveToTarget.PauseExternal + disabling UncertainBehavior - existing public APIs
///    only) and close the spawn gate; DataCaptureManager subscribes to hard-stop trajectory/gaze/NPC capture.
///    Both remain frozen/stopped through the entire End Panel wait, and only resume readiness for the NEXT
///    Part (SpawnerManager.ResetForNewPart(), called at the top of RunNpcWarmup()).
///  - part_end (raised after the participant confirms the End Panel) is UNCHANGED and still fires exactly
///    where it always did; this feature does not move it earlier and does not alter panel/UI semantics.
///
/// SESSION LIFECYCLE (additive; the End-panel moment below now applies in every NPC mode):
///  - ParticipantControlEnabled is true from OnTrialStart until the End panel actually becomes VISIBLE (HandleEndPanelDisplayed,
///    called by UIManager at the display moment). EndTrial()/OnTrialEnd do not lock by themselves. Before the Start panel is
///    confirmed and from the End panel on, the participant cannot move, turn, teleport or select buildings
///    (ParticipantControlLock, GuardedTeleportationProvider, RiverTeleportPoint, TargetSelectable all read it); the Start/End
///    buttons stay usable.
///  - The NPC simulation does not use that flag: it is open from the Part's NPC lifecycle (spawner.BeginNpcSimulation) and stops
///    when the End panel becomes visible (OnEndPanelShown -> SpawnerManager stops spawning and freezes every agent for good).
///    Confirming the panel (TerminateSession -> OnSessionEnded) finalises the session: DataCaptureManager writes session_end and
///    no Part can be started any more. A Part also cannot be started while the End panel is visible.
///
/// PART 3 VALID EXPOSURE INTERVALS (measurement only; both maximum-duration mechanisms are untouched):
///  - Search intervals, like the Part 1 / Part 2 timers (they exclude panel display and reading time):
///    interval_1 = D1 search end - D1 Continue confirmed;  interval_2 = D2 search end - D2 Continue confirmed.
///    A search end is the target-selection click (found) or the frame time at which the timeout branch was taken.
///  - Written to events.csv as event_type 'part3_valid_interval' (label interval_1 / interval_2) by HandleEndPanelDisplayed, plus
///    the endpoint events part3_d1_continue_confirmed, part3_d1_search_end, part3_d2_continue_confirmed, part3_d2_search_end and the
///    separate UI events part3_d2_panel_visible, end_panel_visible (diagnostics; never interval endpoints).
///    An endpoint that was never reached yields status=unavailable with a reason, never an invented duration.
///
/// OnTrialStart / OnTrialEnd still fire exactly once each, for every Part including Part1.
/// </summary>
public class ExperimentManager : MonoBehaviour
{
    public event Action OnTrialStart;
    public event Action OnTrialEnd;

    /// <summary>
    /// Raised exactly once per Part, at the moment that Part's End Panel becomes visible to the participant
    /// (HandleEndPanelDisplayed) - i.e. strictly BEFORE part_end, which only fires later once the participant confirms the
    /// panel, and after the participant lock was applied. This is the authoritative "hard stop" boundary: subscribers
    /// (SpawnerManager, DataCaptureManager) use it to freeze NPCs and stop all continuous data capture for the remainder of
    /// the End Panel's visible duration.
    /// </summary>
    public event Action OnEndPanelShown;

    // ---- Session lifecycle: participant control vs. NPC simulation (additive) -----------------------------------------------
    // Three logical states, kept deliberately separate from the NPC simulation:
    //   WaitingForStart  from Part start until the participant confirms the Start panel   participant locked
    //   Running          OnTrialStart .. OnTrialEnd                                        participant control enabled
    //   Ended            End panel shown (locked) ... End button confirmed (session terminal)
    // ParticipantControlEnabled is the single permission flag for virtual locomotion, turning, teleportation (generic XRI and
    // river points) and world selection. It is NOT used by the NPC system: NPCs run from the Part's NPC lifecycle until
    // SessionEnded (see SpawnerManager.BeginNpcSimulation / HandleSessionEnded).
    //
    // OnParticipantControlChanged fires on every change; OnSessionEnded fires exactly once, after part_end was raised.

    /// <summary>True only while the timed trial is running (OnTrialStart..OnTrialEnd). False before Start, from the End Panel on, and after the session ended.</summary>
    public bool ParticipantControlEnabled { get; private set; }

    /// <summary>True once the participant confirmed the End panel. Terminal: no Part can be started afterwards.</summary>
    public bool SessionEnded { get; private set; }

    /// <summary>Raised whenever <see cref="ParticipantControlEnabled"/> changes (argument = new value).</summary>
    public event Action<bool> OnParticipantControlChanged;

    /// <summary>Raised exactly once when the End panel is confirmed (after part_end). Subscribers freeze their systems.</summary>
    public event Action OnSessionEnded;

    /// <summary>Additive observer event for the data-capture layer (Revision 2). Never affects control flow.</summary>
    public event Action<ExperimentEventInfo> OnExperimentEvent;

    /// <summary>
    /// Per-Part configuration: whether NPCs exist, whether they move directed or undirected, and whether each
    /// of the three continuous experimental capture streams is recorded, for one Part. This is the SINGLE
    /// authoritative source of truth for these five settings (Requirement: avoid duplicate sources of truth) -
    /// SpawnerManager, DataCaptureManager and GazeRecorder all read it via ExperimentManager.CurrentPartConfig
    /// rather than holding their own copies.
    /// </summary>
    [Serializable]
    public class PartCaptureConfig
    {
        [Tooltip("Whether the NPC population (SpawnerManager spawning/warm-up) exists at all during this " +
                 "Part. False means: no warm-up, no spawning, no NPCs ever appear - independent of the " +
                 "capture flags below.")]
        public bool npcEnabled = true;

        [Tooltip("Itinerary type for NPCs: true = Part 3 attractor itinerary (entrance -> attractors -> exit, " +
                 "with dwell), false = Part 2 wandering itinerary (entrance -> SpawnerManager.wanderingPoints -> " +
                 "exit, no dwell). Both are generated once per NPC. Only meaningful while 'Npc Enabled' is true.")]
        public bool npcDirectedMovement = true;

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

    [Header("Exploration boundary lifecycle")]
    [Tooltip("If the ExplorationBoundary object is enabled but sits under an inactive parent (EnvironmentConfig/Participant), " +
             "move only that object to the scene root at Awake so the boundary initialises. Turn off if the boundary is " +
             "permanently placed under an active parent in the scene.")]
    public bool ensureExplorationBoundaryActive = true;

    [Header("Part 3 think-aloud audio")]
    [Tooltip("Record the participant's microphone continuously from the Part 3 Start click to the Part 3 trial end (WAV + sample/time index, synchronised to CaptureClock). Part 3 only; Parts 1 and 2 never record audio.")]
    public bool part3AudioRecording = true;
    [Tooltip("If true (default) Part 3 cannot start while the microphone recording cannot be established: the participant is shown why and the Start is retried. If false the failure is recorded in the manifest and Part 3 continues WITHOUT audio.")]
    public bool part3AudioRequired = true;

    [Header("Face tracking start-up (schema v17; Parts 2 and 3, i.e. the Parts whose gaze capture is enabled)")]
    [Tooltip("Start OVRFaceExpressions at the beginning of the Part and wait (bounded) until a valid Visual eyelid frame exists BEFORE the Start panel, so the first formal gaze row already carries eyelid data. " +
             "Nothing is recorded by this step: the gaze file only opens at the participant's Start click. Off = face tracking is started by the legacy lazy path (first rows of the trial may lack eyelid data).")]
    public bool faceTrackingPreflight = true;
    [Tooltip("Instead of before the Start panel, wait for face tracking after the participant's Start click and before the trial begins (use when starting the face camera before the Start click is not acceptable for the study).")]
    public bool faceTrackingStartAfterStartClick = false;
    [Tooltip("Upper bound (seconds) of the readiness wait. A runtime without face tracking support ends it immediately.")]
    public float faceTrackingMaxWaitSeconds = 5f;
    [Tooltip("true = a failed start-up shows a retry panel (like the microphone gate); false (default) = the Part continues, the failure is recorded in the manifest and eyelid fields stay blank.")]
    public bool faceTrackingRequired = false;

    [Header("River teleport availability")]
    [Tooltip("Participant-facing river teleport points (visuals, anchors, instruction signs) exist and work ONLY while Part 3 " +
             "is running (part_start until its trial ends / End Panel). Part 1, Part 2 and idle: hidden and rejected. " +
             "The scheduled cross-bank teleport in Part 2 is TeleportPlayerTo() and is not affected.")]
    public bool restrictRiverTeleportToPart3 = true;

    [Header("Part durations (seconds)")]
    public float part1Duration = 60f;
    public float part2FirstDuration = 60f;
    public float part2SecondDuration = 60f;
    [Tooltip("Maximum Destination 1 search time (seconds), counted from the moment the Destination 1 panel's Continue is confirmed " +
             "(reading that panel is excluded). When it is reached - or earlier, when Target 1 is selected - the Destination 2 panel appears. " +
             "It can never exceed 'Part3 Total Max Seconds'. Protocol: 600 (10 minutes).")]
    public float part3Destination1MaxSeconds = 600f;
    [Tooltip("Maximum combined ACTIVE search time (seconds): interval 1 (D1 Continue -> end of the D1 search) + interval 2 (D2 Continue -> " +
             "end of the D2 search). Destination 2 receives only what is left after the MEASURED interval 1: max(0, total - interval 1). " +
             "Panel placement, panel reading and other UI waiting never consume this budget. Protocol: 900 (15 minutes).")]
    public float part3TotalMaxSeconds = 900f;

    [Header("Part 1 Configuration")]
    [Tooltip("NPC presence, movement mode and per-stream capture for Part 1. Defaults reproduce the previous " +
             "hard-coded 'Part1 = no NPC, no research capture' behaviour; change freely.")]
    public PartCaptureConfig part1Config = new PartCaptureConfig
    {
        npcEnabled = false,
        npcDirectedMovement = true,
        npcCaptureEnabled = false,
        gazeCaptureEnabled = false,
        trajectoryCaptureEnabled = false
    };

    [Header("Part 2 Configuration")]
    [Tooltip("NPC presence, movement mode and per-stream capture for Part 2. Defaults: NPCs on, wandering " +
             "itinerary (entrance -> wandering points -> exit), NPC telemetry off, gaze + trajectory on.")]
    public PartCaptureConfig part2Config = new PartCaptureConfig
    {
        npcEnabled = true,
        npcDirectedMovement = false,
        npcCaptureEnabled = false,
        gazeCaptureEnabled = true,
        trajectoryCaptureEnabled = true
    };

    [Header("Part 3 Configuration")]
    [Tooltip("NPC presence, movement mode and per-stream capture for Part 3. Defaults reproduce the previous " +
             "behaviour: everything on, DIRECTED itinerary movement (full dataset).")]
    public PartCaptureConfig part3Config = new PartCaptureConfig
    {
        npcEnabled = true,
        npcDirectedMovement = true,
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

    [Header("Participant control at the Part 3 Destination 2 panel")]
    [Tooltip("If true (default), the participant is locked (continuous move, turn providers, XRI and river-point teleports, building selection - the same ParticipantControlEnabled gate the Start panel uses) " +
             "from the moment the Part 3 Destination 2 panel is shown until its Continue button is pressed. Head tracking, controller rays and the panel UI stay usable; NPC spawning / movement, audio, " +
             "gaze / trajectory capture and the experiment clocks are not touched (the D2 search budget only starts at the Continue click anyway). Why: the full panels are placed ONCE in world space, " +
             "so a participant who keeps walking leaves the Destination 2 panel behind and the Part cannot continue. False = previous behaviour (free continuous movement while the panel is open; " +
             "generic XRI teleports are still refused by GuardedTeleportationProvider 'panel_open').")]
    public bool lockParticipantWhileDestination2Panel = true;

    [Header("Participant control at the Part 2 Continue panel")]
    [Tooltip("If true (default), the participant is locked (the same ParticipantControlEnabled gate and checkpoint-lock helpers as the Part 3 Destination 2 panel: continuous move, turn providers, " +
             "XRI and river-point teleports, building selection) from the moment the Part 2 Continue panel is shown until its Continue button is pressed. Head tracking, controller rays and the panel UI " +
             "stay usable; NPCs, audio, gaze / trajectory capture and the experiment clocks are not touched. The scripted Continue teleport to the next area is unchanged and is not gated by this flag. " +
             "False = previous behaviour (free movement while the panel is open).")]
    public bool lockParticipantWhilePart2ContinuePanel = true;

    [Header("Session lifecycle")]
    [Tooltip("Log one line when the session ends (End panel confirmed). Never logged per frame.")]
    public bool enableLifecycleLogs = true;

    [Header("NPC Warm-up")]
    [Tooltip("NPC GENERATION DURATION (the experimental control variable; time-based, NOT a population target). Seconds of ELAPSED time, counted from the moment the NPC pool is ready, during which " +
             "SpawnerManager spawns NPCs at 'spawnInterval' and they behave normally (application suspension is not counted). When it expires spawning stops and every active NPC is paused " +
             "(see 'Hold Npcs Until Start') until the participant presses Start. The number of NPCs that exist then results from this duration and the spawn interval only. Applies to every " +
             "NPC-enabled Part (no per-Part duration). 0 = no generation period (NPCs run from pool readiness; nothing is held). Pool creation time is never part of it. " +
             "Presence of NPCs is still controlled by 'Npc Enabled' in the Part configuration.")]
    public float npcWarmupDuration = 30f;

    [Header("NPC preparation (pool -> behaviour warm-up -> Start-gated pause)")]
    [Tooltip("If true (default), spawning STOPS and the NPC simulation is PAUSED when the NPC generation duration ('Npc Warmup Duration') ends: every NPC stays visible and raycastable but stationary while the participant " +
             "reads the Start panel, and the Start click resumes them in the same transition that opens the trial. If false, NPCs keep walking while the Start panel is read (previous behaviour). " +
             "Ignored when 'Npc Warmup Duration' is 0. Applies to every Part with NPCs enabled.")]
    public bool holdNpcsUntilStart = true;

    [Tooltip("If true (default), the Start panel is withheld until the headset is WORN (OVRPlugin.userPresent) so that it is placed in front of the participant's head, not wherever an unworn headset " +
             "lies (panels are positioned once, at show time). Only the panel is delayed: pool preparation, NPC generation and the NPC pause run automatically whether or not the headset is worn. " +
             "When presence is unknown (no runtime, Editor) the panel is shown immediately.")]
    public bool waitForHeadsetBeforeStartPanel = true;

    /// <summary>Test seam: replaces the OVRPlugin headset-presence reading (true = worn, false = unworn, null = unknown). Never set in production.</summary>
    [System.NonSerialized] public System.Func<bool?> HeadsetPresenceProbe;

    /// <summary>Preparation state + manifest evidence of the Part currently running (see NpcPreparationInfo). Written to session_manifest.json as npc_preparation.</summary>
    public NpcPreparationInfo NpcPreparation { get; } = new NpcPreparationInfo();

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

    [Header("Part3 - selection protocol")]
    [Tooltip("Existing protocol (default, false): while a destination is being searched ONLY its assigned building is " +
             "selectable, so a wrong-building selection cannot occur. If true, the other candidate of the SAME zone " +
             "also becomes selectable for the duration of that destination's search window, a selection of it is " +
             "scored 'wrong_building' (does not resolve the destination; the search continues), and it is disabled " +
             "again when the window closes. Part 1 targets and all other buildings stay unselectable either way.")]
    public bool allowWrongCandidateSelection = false;

    [Header("Part3 - candidate destination buildings (exactly 2 per zone)")]
    public Part3ZoneTargets part3ZoneGE = new Part3ZoneTargets();
    public Part3ZoneTargets part3ZoneGU = new Part3ZoneTargets();
    public Part3ZoneTargets part3ZoneOE = new Part3ZoneTargets();
    public Part3ZoneTargets part3ZoneOU = new Part3ZoneTargets();

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

    // Fixed group -> (start zone, destination 1 zone, destination 2 zone). Group Number == Group Index (0..11).
    private static readonly Part3GroupDef[] Part3GroupTable =
    {
        new Part3GroupDef(Part3Zone.GE, Part3Zone.GU, Part3Zone.OE), // Group 0
        new Part3GroupDef(Part3Zone.GE, Part3Zone.OE, Part3Zone.GU), // Group 1
        new Part3GroupDef(Part3Zone.GE, Part3Zone.OU, Part3Zone.GE), // Group 2
        new Part3GroupDef(Part3Zone.GU, Part3Zone.GE, Part3Zone.OU), // Group 3
        new Part3GroupDef(Part3Zone.GU, Part3Zone.OE, Part3Zone.GU), // Group 4
        new Part3GroupDef(Part3Zone.GU, Part3Zone.OU, Part3Zone.GE), // Group 5
        new Part3GroupDef(Part3Zone.OE, Part3Zone.GE, Part3Zone.OU), // Group 6
        new Part3GroupDef(Part3Zone.OE, Part3Zone.GU, Part3Zone.OE), // Group 7
        new Part3GroupDef(Part3Zone.OE, Part3Zone.OU, Part3Zone.GE), // Group 8
        new Part3GroupDef(Part3Zone.OU, Part3Zone.GE, Part3Zone.OU), // Group 9
        new Part3GroupDef(Part3Zone.OU, Part3Zone.GU, Part3Zone.OE), // Group 10
        new Part3GroupDef(Part3Zone.OU, Part3Zone.OE, Part3Zone.GU), // Group 11
    };

    /// <summary>Number of Part 3 groups in the authoritative assignment table (valid zero-based group indices are 0..Part3GroupCount-1).</summary>
    public static int Part3GroupCount => Part3GroupTable.Length;

    /// <summary>The (start, destination 1, destination 2) zones of a zero-based Part 3 group index, for configuration diagnostics. False if the index is out of range.</summary>
    public static bool TryGetPart3GroupZones(int groupIndex, out string startZone, out string destination1Zone, out string destination2Zone)
    {
        startZone = destination1Zone = destination2Zone = "";
        if (groupIndex < 0 || groupIndex >= Part3GroupTable.Length) return false;
        Part3GroupDef d = Part3GroupTable[groupIndex];
        startZone = d.StartZone.ToString(); destination1Zone = d.Destination1Zone.ToString(); destination2Zone = d.Destination2Zone.ToString();
        return true;
    }

    // internal state
    private Coroutine runningCoroutine;
    private bool targetWasSelected;
    private object lastSelectedTargetData;

    // Part 3 selection window: every accepted building selection is queued (in click order) while a destination's
    // search window is open and scored once per entry by the Part 3 loops. Outside a window nothing is queued, so
    // stale or unrelated clicks can never be scored later.
    // Each entry carries the click's own realtime stamp (taken in RegisterTargetSelection, i.e. in the click callback): when the
    // target is found, that stamp - not the later frame in which the loop notices it - is the search endpoint.
    private struct PendingSelection { public string id; public double realtime; public string interactionId; }
    // interaction ids already queued for scoring: one physical click is scored at most once even if its callback runs again
    private readonly HashSet<string> queuedInteractions = new HashSet<string>();
    private readonly Queue<PendingSelection> pendingSelections = new Queue<PendingSelection>();
    private bool selectionWindowOpen;

    private enum RunningPart { None, Part1, Part2, Part3 }
    private RunningPart currentRunningPart = RunningPart.None;
    private bool part1ContinuePanelOpen = false;

    // End-panel display transition (all Parts). HandleEndPanelDisplayed runs once per Part, at the moment the End panel becomes
    // visible: it locks the participant, raises OnEndPanelShown (NPC freeze, capture hard stop, river-point refresh) and, for
    // Part 3, emits the valid-interval results. Reset at the top of every RunPartN.
    private bool endPanelDisplayHandled;
    private double endPanelVisibleRealtime = double.NaN;   // NaN = the display moment was not observed

    // Part 3 valid-exposure timing (see Part3ValidTiming): drives the D1 / D2 search limits AND the interval results from the same
    // recorded endpoints. Created at the top of RunPart3 from the two Inspector maxima. Clock: CaptureClock.TAbs,
    // the same as events.csv t_abs.
    private Part3ValidTiming p3Timing;
    private bool p3D2Ran;

    private void Awake()
    {
        if (uiManager == null) Debug.LogWarning("ExperimentManager: UIManager not assigned in inspector.");
        // The session configuration is NOT frozen here: Awake order across GameObjects is unspecified and the Inspector values are final only once every Awake
        // has run. EnsureSessionOverridesApplied() (below) freezes them on first need - at the latest from Start(), always before a Part, the NPC gate or the
        // manifest reads them.
        EnsureExplorationBoundaryInitialised();
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------
    // Frozen session configuration. The effective settings (JSON overrides merged with the Scene-owned values below, each with provenance) are captured ONCE from the
    // resolved ExternalSessionController snapshot and the current Scene values; every runtime read afterwards (durations, per-Part NPC/capture flags,
    // wrong-candidate protocol, river-teleport restriction) goes through them, so later Inspector edits or file changes cannot alter the running session.
    // The public Inspector fields stay the Scene-owned source that is read at the moment of freezing.
    // ---------------------------------------------------------------------------------------------------------------------------------------------
    private EffectiveSettings _effective;
    private readonly PartCaptureConfig[] _frozenPartConfigs = new PartCaptureConfig[3];
    private bool _configDependencyErrorLogged;

    /// <summary>The session's controller: the Inspector reference, else the scene's single ExternalSessionController. Null = no configuration source exists.</summary>
    private ExternalSessionController Controller
    {
        get
        {
            if (sessionController == null)
            {
                sessionController = FindObjectOfType<ExternalSessionController>(true);
                if (sessionController != null)
                    Debug.LogWarning("[ExperimentManager] sessionController is not assigned in the Inspector; using the ExternalSessionController found in the scene.");
            }
            return sessionController;
        }
    }

    /// <summary>
    /// Idempotent. Resolves the session configuration (if not yet) and freezes the effective settings exactly once. Returns true when the frozen settings are
    /// available; false (fail closed, nothing applied) when there is no ExternalSessionController or the configuration is Invalid. Safe to call from any component
    /// and at any time, in any Awake/Start order.
    /// </summary>
    public bool EnsureSessionOverridesApplied()
    {
        if (_effective != null) return true;

        ExternalSessionController c = Controller;
        if (c == null)
        {
            if (!_configDependencyErrorLogged)
            {
                _configDependencyErrorLogged = true;
                Debug.LogError("[ExperimentManager] No ExternalSessionController is assigned or present in the scene: the session configuration cannot be resolved. " +
                               "Part start, auto-start and recording are blocked.");
            }
            return false;
        }
        if (c.Initialize() != ExternalSessionController.ConfigState.Valid) return false;

        EffectiveSettings e = c.FreezeEffective(CaptureSceneSettings());
        if (e == null) return false;
        for (int i = 0; i < 3; i++)
        {
            PartFlagValues f = e.GetPartFlags(i + 1);
            _frozenPartConfigs[i] = new PartCaptureConfig
            {
                npcEnabled = f.NpcEnabled, npcDirectedMovement = f.NpcDirectedMovement, npcCaptureEnabled = f.NpcCaptureEnabled,
                gazeCaptureEnabled = f.GazeCaptureEnabled, trajectoryCaptureEnabled = f.TrajectoryCaptureEnabled
            };
        }
        _effective = e;
        return true;
    }

    /// <summary>The frozen effective settings, or null while the configuration is Invalid / unavailable (this call freezes them on first use).</summary>
    public EffectiveSettings Effective => EnsureSessionOverridesApplied() ? _effective : null;

    /// <summary>
    /// Whether Part 3 uses directed (attractor) movement: the frozen Part 3 config once the session settings are frozen (no side effect: this does not freeze them), otherwise the
    /// Inspector Part 3 config; true when neither exists (the established default). Read-only; SpawnerManager uses it to derive the effective Part 3 attractor probability
    /// (0 when false) that scales the Part 2 accessory weights.
    /// </summary>
    public bool Part3NpcDirectedMovement
    {
        get
        {
            PartCaptureConfig c = _effective != null ? _frozenPartConfigs[2] : part3Config;
            return c == null || c.npcDirectedMovement;
        }
    }

    /// <summary>The frozen PartCaptureConfig of Part 1..3 (a private copy: Inspector edits do not reach it), or null while the configuration is unavailable.</summary>
    public PartCaptureConfig GetEffectivePartConfig(int partNumber)
        => partNumber >= 1 && partNumber <= 3 && EnsureSessionOverridesApplied() ? _frozenPartConfigs[partNumber - 1] : null;

    private static PartFlagValues FlagsOf(PartCaptureConfig c)
        => c == null ? default(PartFlagValues) : new PartFlagValues(c.npcEnabled, c.npcDirectedMovement, c.npcCaptureEnabled, c.gazeCaptureEnabled, c.trajectoryCaptureEnabled);

    // Reads the Scene-owned values at the moment of freezing (the only place the Inspector fields are read for the running session).
    private SceneSettings CaptureSceneSettings()
    {
        var ss = new SceneSettings
        {
            Part1Duration = part1Duration, Part2FirstDuration = part2FirstDuration, Part2SecondDuration = part2SecondDuration,
            Part3Destination1MaxSeconds = part3Destination1MaxSeconds, Part3TotalMaxSeconds = part3TotalMaxSeconds, NpcWarmupDuration = Mathf.Max(0f, npcWarmupDuration),
            AllowWrongCandidateSelection = allowWrongCandidateSelection, RestrictRiverTeleportToPart3 = restrictRiverTeleportToPart3,
        };
        ss.PartFlags[0] = FlagsOf(part1Config); ss.PartFlags[1] = FlagsOf(part2Config); ss.PartFlags[2] = FlagsOf(part3Config);

        SpawnerManager sp = spawner != null ? spawner : FindObjectOfType<SpawnerManager>(true);
        if (sp != null)
        {
            ss.SpawnerMaxAgents = sp.maxAgents; ss.SpawnerPoolPrewarm = sp.poolPrewarm; ss.SpawnerSpawnInterval = sp.spawnInterval;
            ss.SpawnerDwellTimeMin = sp.dwellTimeSec.x; ss.SpawnerDwellTimeMax = sp.dwellTimeSec.y;
            ss.SpawnerPart3AttractorProbability = sp.Part3AttractorMovementProbability;
            ss.SpawnerPart3MovementSeed = sp.Part3MovementAssignmentSeedEffective;
        }
        // The profile the NPC system will actually use: RunNpcWarmup assigns currentProfile to the spawner when a Part with NPCs starts.
        AgentProfile prof = currentProfile != null ? currentProfile : (sp != null ? sp.defaultProfile : null);
        if (prof != null)
        {
            ss.AgentProfileName = prof.name;
            try { ss.AgentProfileHash = AgentProfileSnapshot.Describe(prof, out _); } catch (Exception) { ss.AgentProfileHash = ""; }
        }
        return ss;
    }

    private bool RestrictRiverTeleport => EnsureSessionOverridesApplied() ? _effective.RestrictRiverTeleportToPart3 : true;   // unavailable config: most restrictive

    /// <summary>
    /// The ExplorationBoundary component lives under EnvironmentConfig/Participant/..., organisation objects that are
    /// saved inactive on purpose (they hold hidden marker meshes and NPC placeholders). A component inside an inactive
    /// hierarchy never receives Awake/OnEnable/Start, so ExplorationBoundary.Instance stayed null: Part 2 walls were never
    /// enabled, RiverTeleportPoint skipped its area test, and GuardedTeleportationProvider rejected every Part 2 XRI
    /// teleport as "no_exploration_boundary". This runs before any Part starts and, only when the boundary object itself is
    /// enabled but inactive in the hierarchy, re-parents JUST that object (with its wall colliders) to the scene root,
    /// keeping its world pose. The organisation parents, the Area Volume colliders (hidden meshes) and every other
    /// inactive object stay as authored. No-op when the boundary is already live (e.g. the hierarchy was activated in the
    /// Editor) or when none exists.
    /// </summary>
    private void EnsureExplorationBoundaryInitialised()
    {
        if (!ensureExplorationBoundaryActive) return;

        ExplorationBoundary[] found = FindObjectsOfType<ExplorationBoundary>(true);
        for (int i = 0; i < found.Length; i++)
        {
            ExplorationBoundary eb = found[i];
            if (eb == null) continue;
            GameObject go = eb.gameObject;
            if (go.activeInHierarchy || !go.activeSelf) continue; // already live, or deliberately disabled itself

            string oldPath = go.transform.parent != null ? go.transform.parent.name + "/" + go.name : go.name;
            go.transform.SetParent(null, worldPositionStays: true);
            Debug.Log($"[ExperimentManager] ExplorationBoundary '{oldPath}' was inactive in the hierarchy; moved to the scene root " +
                      $"(world pose kept) so it initialises before Part start. Instance available: {ExplorationBoundary.Instance != null}.", this);
        }
    }

    private void Start()
    {
        // Baseline: every Phase 3 candidate building must behave as an ordinary (non-interactable) building
        // until Phase 3 assigns it as a destination. Visibility is NOT touched here.
        SetAllPart3CandidatesInteractable(false);

        ValidateTargetIds();

        // Freeze the effective settings now (before any auto-start, trial or recording read) and auto-start only a Valid configuration.
        EnsureSessionOverridesApplied();
        ExternalSessionController c = Controller;
        if (c != null && c.ActiveAutoStart)
        {
            StartSelectedPart();
        }
    }

    // F-14: no participant Part (and therefore no auto-start) begins while the session identity/configuration is invalid. NPC simulation of a scene without a
    // running Part is unaffected; only the participant trial is held back. FAIL CLOSED: without any ExternalSessionController there is no configuration source and
    // no Part can start; the effective settings must also be frozen before a Part runs.
    private bool IdentityAllowsStart(string what)
    {
        ExternalSessionController c = Controller;
        if (c == null)
        {
            Debug.LogError($"ExperimentManager: {what} refused - no ExternalSessionController is assigned or present (the session configuration cannot be resolved).");
            return false;
        }
        SessionIdentity.Result r = c.ValidateIdentity();
        if (!r.IsValid)
        {
            Debug.LogError($"ExperimentManager: {what} refused - invalid session identity/configuration ({r.status}): {r.message}");
            return false;
        }
        if (!EnsureSessionOverridesApplied())
        {
            Debug.LogError($"ExperimentManager: {what} refused - the effective session settings could not be frozen.");
            return false;
        }
        return true;
    }

    public void StartSelectedPart()
    {
        if (!IdentityAllowsStart("StartSelectedPart")) return;

        switch (Controller.ActivePart)
        {
            case ExternalSessionController.ExperimentPart.Part1: StartPart1(); break;
            case ExternalSessionController.ExperimentPart.Part2: StartPart2(); break;
            case ExternalSessionController.ExperimentPart.Part3: StartPart3(); break;
        }
    }

    public void RegisterTargetSelection(object targetData = null, string interactionId = "")
    {
        targetWasSelected = true;
        lastSelectedTargetData = targetData;
        Debug.Log("Target selected: " + (targetData ?? "unknown"));

        if (selectionWindowOpen)
        {
            // One interaction id is scored at most once (a repeated callback for the same physical click is ignored here).
            if (!string.IsNullOrEmpty(interactionId) && !queuedInteractions.Add(interactionId)) return;
            pendingSelections.Enqueue(new PendingSelection { id = targetData != null ? targetData.ToString() : "", realtime = CaptureClock.TAbs, interactionId = interactionId ?? "" });
        }

        if (currentRunningPart == RunningPart.Part1 && !part1ContinuePanelOpen && uiManager != null)
        {
            part1ContinuePanelOpen = true;
            uiManager.ShowPart1ContinuePanel(() =>
            {
                part1ContinuePanelOpen = false;
            });
        }
    }

    #region Participant teleport observer API (additive; used by GuardedTeleportationProvider)

    /// <summary>
    /// "Part1" / "Part2" / "Part3" while a Part is running (from part_start until its End Panel is confirmed), else "".
    /// Read-only view of the existing private state; does not change the state machine.
    /// </summary>
    public string RunningPartName => CurrentRunningPartString();

    /// <summary>Rejection reason reported when a participant tries to use a river point outside Part 3.</summary>
    public const string RiverTeleportDisabledReason = "river_teleport_disabled_outside_part3";

    /// <summary>
    /// The single rule for river teleport points: available only while Part 3 is actually running and its End Panel has not
    /// been shown (running state, not the configured 'selectedPart'). Used by RiverPointAvailability (visibility of the
    /// points and their signs) AND by RiverTeleportPoint.TryTeleport (execution), so visibility and permission cannot
    /// disagree. The scheduled Part 2 cross-bank transition is NOT a river-point interaction.
    /// </summary>
    public bool IsRiverTeleportAvailable =>
        ParticipantControlEnabled && (!RestrictRiverTeleport || (currentRunningPart == RunningPart.Part3 && !EndPanelActive));

    /// <summary>
    /// A participant-initiated XRI teleport was EXECUTED. Raised through the existing "teleport" event so the
    /// data-capture layer treats it like the scripted teleports (same event type and from/to fields); the label
    /// distinguishes the source. Observation only.
    /// </summary>
    public void ReportParticipantTeleport(string sourceLabel, Vector3 fromPos, Vector3 toPos)
    {
        RaiseEvt("teleport", label: sourceLabel, stringValue: "XRI", fromPos: fromPos, toPos: toPos);
    }

    /// <summary>
    /// A participant-initiated XRI teleport request was REJECTED before the rig moved. Raised as a distinct
    /// "teleport_rejected" event (never as "teleport"), so rejected attempts cannot be mistaken for displacement.
    /// </summary>
    public void ReportParticipantTeleportRejected(string sourceLabel, string reason, Vector3 rigPos, Vector3 attemptedPos)
    {
        RaiseEvt("teleport_rejected", label: sourceLabel, stringValue: reason, fromPos: rigPos, toPos: attemptedPos);
    }

    #endregion

    #region Session lifecycle helpers

    private void SetParticipantControl(bool enabled)
    {
        if (ParticipantControlEnabled == enabled) return;
        ParticipantControlEnabled = enabled;
        OnParticipantControlChanged?.Invoke(enabled);
    }

    // ---- Mandatory-checkpoint lock (Part 3 Destination 2 panel) -------------------------------------------------------------------
    // Reuses the ONE participant-control gate (SetParticipantControl -> OnParticipantControlChanged -> ParticipantControlLock providers,
    // GuardedTeleportationProvider, RiverTeleportPoint, RiverPointAvailability, TargetSelectable). Only the participant is affected: no NPC, audio,
    // capture, UI-input or timing system reads this flag. Idempotent; never overrides a lock that belongs to the Start panel, the End panel or a
    // finished session (those own the flag in their states), and a release can never re-open a terminal state.
    private bool _checkpointLockActive;

    private void LockParticipantForCheckpoint(string label, string reason)
    {
        if (_checkpointLockActive || SessionEnded || EndPanelActive || !ParticipantControlEnabled) return;   // already locked by another state, or terminal: nothing to add
        _checkpointLockActive = true;
        SetParticipantControl(false);
        RaiseEvt("participant_control", CurrentRunningPartString(), label: label, stringValue: "locked;reason=" + reason);
    }

    private void ReleaseParticipantCheckpointLock(string label, string reason)
    {
        if (!_checkpointLockActive) return;
        _checkpointLockActive = false;
        if (SessionEnded || EndPanelActive) return;   // the End panel / session end own the lock now
        SetParticipantControl(true);
        RaiseEvt("participant_control", CurrentRunningPartString(), label: label, stringValue: "unlocked;reason=" + reason);
    }

    // Start of the timed trial: participant control is enabled first so listeners of OnTrialStart already see the Running state.
    private void BeginTrial()
    {
        if (SessionEnded) return;
        _checkpointLockActive = false;   // a new trial never inherits a checkpoint lock (SetParticipantControl(true) below is the release)
        if (NpcPreparation.applicable && NpcPreparation.state == NpcPreparationState.Failed.ToString())
        {
            Debug.LogError("[ExperimentManager] Trial start refused: NPC preparation failed.", this);
            return;
        }
        ResumeNpcBehaviourForTrialStart();   // NPC resume and trial start are one transition: same frame, resume first
        SetParticipantControl(true);
        OnTrialStart?.Invoke();
    }

    // End of the timed trial: raises OnTrialEnd (capture scope) only. Participant control is deliberately NOT changed here: it stays
    // enabled until the End panel actually becomes visible (HandleEndPanelDisplayed), so ending the timer / the search loop never
    // locks the participant by itself. In the normal case the End panel is shown synchronously right after this call.
    private void EndTrial()
    {
        // Part 3 think-aloud audio ends at the SAME boundary as every other recorder's trial scope: the audio interval is [first audio frame, this instant).
        if (currentRunningPart == RunningPart.Part3 && _part3Audio != null)
        {
            try { _part3Audio.StopAtBoundary(CaptureClock.TAbs, "part3_trial_end"); }
            catch (Exception ex) { Debug.LogError("[ExperimentManager] Part 3 audio stop threw: " + ex); }
        }
        OnTrialEnd?.Invoke();
    }

    // ---- Part 3 think-aloud audio orchestration (Part 3 only; the recorder itself lives in the capture layer) ----
    private Part3AudioRecorder _part3Audio;

    private Part3AudioRecorder ResolvePart3Audio()
    {
        if (!part3AudioRecording) return null;
        DataCaptureManager dcm = FindObjectOfType<DataCaptureManager>();
        Part3AudioRecorder a = dcm != null ? dcm.EnsureAudioRecorder() : null;
        if (a == null) Debug.LogError("[ExperimentManager] Part 3 audio recording is enabled but no usable DataCaptureManager session exists: no audio will be recorded.", this);
        return a;
    }

    // Preflight until the audio system is ready. A failure is shown to the participant / experimenter and retried; with part3AudioRequired = false it is only recorded and Part 3 proceeds without audio.
    private IEnumerator Part3AudioPreflightGate(Part3AudioRecorder audio)
    {
        while (!SessionEnded)
        {
            yield return audio.RunPreflight();
            if (audio.IsReady) yield break;
            if (!part3AudioRequired) { Debug.LogError("[ExperimentManager] Part 3 audio preflight failed (" + audio.FailureReason + "); part3AudioRequired is false, continuing WITHOUT audio.", this); _part3Audio = null; yield break; }
            yield return Part3AudioBlockedWait("Microphone recording is not available (" + audio.FailureReason + "). Please inform the experimenter, fix the microphone, then press to retry.");
        }
    }

    private IEnumerator Part3AudioStartGate(Part3AudioRecorder audio, double clickT)
    {
        while (!SessionEnded)
        {
            yield return audio.StartRecording(clickT);
            if (audio.IsRecording) yield break;
            if (!part3AudioRequired) { Debug.LogError("[ExperimentManager] Part 3 audio start failed (" + audio.FailureReason + "); part3AudioRequired is false, continuing WITHOUT audio.", this); _part3Audio = null; yield break; }
            yield return Part3AudioBlockedWait("The audio recording could not start (" + audio.FailureReason + "). Please inform the experimenter, then press to retry.");
            clickT = CaptureClock.TAbs;   // the retry is a new Start click
            if (audio.State != AudioCaptureState.Ready) yield return audio.RunPreflight();
        }
    }

    // ---- face tracking start-up (schema v17): creates OVRFaceExpressions early and waits (bounded) for the first valid Visual frame. Records nothing. ----
    private GazeRecorder FaceGaze()
    {
        if (CurrentPartConfig == null || !CurrentPartConfig.gazeCaptureEnabled) return null;
        return FindObjectOfType<GazeRecorder>();
    }

    // At the start of a Part: only creates / starts the component (no wait), so the runtime has the whole NPC warm-up to start tracking.
    private void FaceTrackingBegin()
    {
        if (!faceTrackingPreflight || faceTrackingStartAfterStartClick) return;
        GazeRecorder g = FaceGaze(); if (g == null) return;
        try { g.BeginFaceTrackingStartup("preflight"); } catch (Exception ex) { Debug.LogError("[ExperimentManager] face tracking start-up threw: " + ex.Message, this); }
    }

    private IEnumerator FaceTrackingReadyGate(string startPoint)
    {
        bool wanted = startPoint == "preflight" ? (faceTrackingPreflight && !faceTrackingStartAfterStartClick) : (faceTrackingPreflight && faceTrackingStartAfterStartClick);
        if (!wanted) yield break;
        GazeRecorder g = FaceGaze(); if (g == null) yield break;
        while (!SessionEnded)
        {
            yield return g.AwaitFaceTrackingReady(Mathf.Max(0.1f, faceTrackingMaxWaitSeconds), startPoint);
            if (g.FaceStartup.ready || !faceTrackingRequired) yield break;
            yield return Part3AudioBlockedWait("Face tracking is not available (" + g.FaceStartup.failure_reason + "). Please inform the experimenter, then press to retry.");
        }
    }

    // Visible block: a panel with the reason; its button is the retry. Without a UI the wait is a timed retry (never a silent continue).
    private IEnumerator Part3AudioBlockedWait(string message)
    {
        if (uiManager == null) { yield return new WaitForSeconds(2f); yield break; }
        bool retry = false;
        uiManager.ShowPart3StartPanel(() => retry = true, message);
        yield return new WaitUntil(() => retry || SessionEnded);
        yield return new WaitForSeconds(0.5f);   // never a tight loop, even if the panel template is missing and auto-confirms
    }

    // The End panel is on screen (UIManager 'onShown'), or - safety net - was confirmed without the display being observed.
    // The single place where, in this order: the participant is locked (movement, turning, teleports, river points, building
    // selection - every consumer reads ParticipantControlEnabled), EndPanelActive is set, OnEndPanelShown is raised (SpawnerManager
    // stops spawning and freezes every NPC; DataCaptureManager stops continuous capture; RiverPointAvailability refreshes), and the
    // visible-moment events are written. The UI path is untouched, so the panel stays clickable. Idempotent: the first call wins.
    private void HandleEndPanelDisplayed(string part, bool displayObserved)
    {
        if (endPanelDisplayHandled) return;
        endPanelDisplayHandled = true;

        double nowRt = CaptureClock.TAbs;   // first statement: nothing below may shift the recorded endpoint
        if (displayObserved)
        {
            endPanelVisibleRealtime = nowRt;
            if (currentRunningPart == RunningPart.Part3 && p3Timing != null) p3Timing.RecordEndPanelVisible(nowRt);   // UI diagnostic only; interval 2 already ended with the D2 search
        }

        SetParticipantControl(false);
        EndPanelActive = true;

        try { OnEndPanelShown?.Invoke(); }
        catch (Exception ex) { Debug.LogError("[ExperimentManager] OnEndPanelShown subscriber threw: " + ex); }

        if (displayObserved)
            RaiseEvt("end_panel_visible", part, label: part.ToLowerInvariant() + "_end");
        else
            Debug.LogWarning($"[ExperimentManager] {part}: the End panel was confirmed without its display being observed; the participant lock and NPC shutdown were applied only now and 'end_panel_visible' was not recorded.", this);

        if (currentRunningPart == RunningPart.Part3) EmitPart3ValidIntervals();
    }

    // Called as the last statement of RunPartN, after the End panel was confirmed and part_end was raised (so the capture layer
    // has the part_end row). Idempotent: a repeated confirm or stale callback cannot end the session twice.
    private void TerminateSession(string part)
    {
        if (SessionEnded) return;
        SessionEnded = true;
        if (NpcPreparation.applicable)
        {
            string st = NpcPreparation.state;
            bool started = st == NpcPreparationState.Running.ToString() || st == NpcPreparationState.Ended.ToString();
            if (st != NpcPreparationState.Failed.ToString()) SetNpcPreparation(started ? NpcPreparationState.Ended : NpcPreparationState.Cancelled);
        }
        SetParticipantControl(false);
        runningCoroutine = null;
        if (enableLifecycleLogs) Debug.Log($"[ExperimentManager] Session ended after {part}: participant locked, NPC simulation stopping, no further Part can start.", this);
        OnSessionEnded?.Invoke();
    }

    private bool RefuseStartAfterEnd(string what)
    {
        // The End panel being visible is already terminal for the participant lock and the NPC simulation, so a Part cannot be
        // (re)started from that moment either - it would run with a dead NPC system.
        if (!SessionEnded && !EndPanelActive) return false;
        Debug.LogWarning($"[ExperimentManager] {what} refused: the End panel is visible or the session has ended.", this);
        return true;
    }

    // Writes the Part 3 valid-interval results (events.csv: event_type 'part3_valid_interval', label interval_1 / interval_2 /
    // total_valid_exposure, value_num = seconds when measured, value_str = key=value pairs). Each interval is a SEARCH interval:
    // it ends at the search endpoint (target-selection click, or the timeout branch), never at the next panel's visibility, and the
    // numbers come from the SAME Part3ValidTiming endpoints that decided the search limits and the D2 budget. The UI-transition gaps
    // (search end -> next panel visible, D2 panel reading time) are carried as separate diagnostics keys. An interval whose endpoint
    // was never reached is written as status=unavailable with a reason and NO duration; a measured zero-length interval is
    // status=complete (value_num is blank for an exact 0, so always read the status first). Called once, from
    // HandleEndPanelDisplayed - the durable search-end rows (part3_d1_search_end / part3_d2_search_end) were written earlier.
    private void EmitPart3ValidIntervals()
    {
        Part3ValidTiming t = p3Timing;
        if (t == null) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;

        // Interval 1: D1 Continue clicked -> D1 search end.
        string reason1 =
            double.IsNaN(t.D1ContinueRealtime) ? "d1_continue_not_recorded" :
            double.IsNaN(t.D1SearchEndRealtime) ? "d1_search_end_not_recorded" : "";
        double i1 = EmitPart3Interval("interval_1", "d1_continue_confirmed", "d1_search_end",
            t.D1ContinueRealtime, t.D1SearchEndRealtime, reason1,
            $"d1_end={t.D1EndReason};d1_max_s={t.D1MaxSeconds.ToString("F3", ci)};total_max_s={t.TotalMaxSeconds.ToString("F3", ci)};d1_search_end_to_d2_panel_visible_s={Fs(t.D1EndToD2PanelVisibleSeconds)}");

        // Interval 2: D2 Continue clicked -> D2 search end.
        string reason2 =
            !p3D2Ran ? "d2_skipped_total_budget_exhausted" :
            double.IsNaN(t.D2ContinueRealtime) ? "d2_continue_not_recorded" :
            double.IsNaN(t.D2SearchEndRealtime) ? "d2_search_end_not_recorded" : "";
        string d2End = p3D2Ran ? t.D2EndReason : "skipped";
        double i2 = EmitPart3Interval("interval_2", "d2_continue_confirmed", "d2_search_end",
            t.D2ContinueRealtime, t.D2SearchEndRealtime, reason2,
            $"d2_end={d2End};d2_budget_s={Fs(t.D2BudgetSeconds)};total_max_s={t.TotalMaxSeconds.ToString("F3", ci)};d2_panel_reading_s={Fs(t.D2PanelReadingSeconds)};d2_search_end_to_end_panel_visible_s={Fs(t.D2EndToEndPanelVisibleSeconds)}");

        // Total valid search duration = interval 1 + interval 2, only when both were really measured.
        if (!double.IsNaN(i1) && !double.IsNaN(i2))
        {
            double total = i1 + i2;
            RaiseEvt("part3_valid_interval", "Part3", label: "total_valid_exposure", numValue: total,
                stringValue: $"status=complete;interval_1_s={i1.ToString("F6", ci)};interval_2_s={i2.ToString("F6", ci)};duration_s={total.ToString("F6", ci)};total_max_s={t.TotalMaxSeconds.ToString("F3", ci)}");
        }
        else
        {
            string why = double.IsNaN(i1) && double.IsNaN(i2) ? "interval_1_and_interval_2_unavailable" : (double.IsNaN(i1) ? "interval_1_unavailable" : "interval_2_unavailable");
            RaiseEvt("part3_valid_interval", "Part3", label: "total_valid_exposure",
                stringValue: $"status=unavailable;reason={why};total_max_s={t.TotalMaxSeconds.ToString("F3", ci)}");
        }
    }

    // Invariant-culture seconds with 6 decimals; "" for NaN (unavailable) so a missing value is never printed as a number.
    private static string Fs(double v) => double.IsNaN(v) ? "" : v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

    // value_str for part3_d1_search_end / part3_d2_search_end: the search endpoint, why it happened, and the interval it closes.
    private static string SearchEndText(string reason, double startT, double endT, double interval, double nextBudget)
    {
        return $"reason={reason};start_t_abs={Fs(startT)};end_t_abs={Fs(endT)};interval_s={Fs(interval)};d2_budget_s={Fs(nextBudget)}";
    }

    // Returns the measured duration, or NaN when the interval is unavailable.
    private double EmitPart3Interval(string label, string startName, string endName, double start, double end, string unavailableReason, string extra)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        double duration = end - start;
        if (string.IsNullOrEmpty(unavailableReason) && duration < 0.0) unavailableReason = "endpoints_out_of_order";

        string text;
        double num = 0.0;
        if (string.IsNullOrEmpty(unavailableReason))
        {
            num = duration;
            text = $"status=complete;start={startName};end={endName};start_t_abs={start.ToString("F6", ci)};end_t_abs={end.ToString("F6", ci)};duration_s={duration.ToString("F6", ci)};{extra}";
        }
        else
        {
            string s = double.IsNaN(start) ? "" : start.ToString("F6", ci);
            string e = double.IsNaN(end) ? "" : end.ToString("F6", ci);
            text = $"status=unavailable;reason={unavailableReason};start={startName};end={endName};start_t_abs={s};end_t_abs={e};{extra}";
        }
        RaiseEvt("part3_valid_interval", "Part3", label: label, stringValue: text, numValue: num);
        return string.IsNullOrEmpty(unavailableReason) ? duration : double.NaN;
    }

    #endregion

    #region Public API to start each part

    public void StartPart1()
    {
        if (RefuseStartAfterEnd("StartPart1")) return;
        if (!IdentityAllowsStart("StartPart1")) return;
        if (runningCoroutine != null) { StopCoroutine(runningCoroutine); CancelNpcPreparationIfInProgress(); }
        runningCoroutine = StartCoroutine(RunPart1());
    }

    public void StartPart2()
    {
        if (RefuseStartAfterEnd("StartPart2")) return;
        if (!IdentityAllowsStart("StartPart2")) return;
        if (runningCoroutine != null) { StopCoroutine(runningCoroutine); CancelNpcPreparationIfInProgress(); }
        runningCoroutine = StartCoroutine(RunPart2());
    }

    public void StartPart3()
    {
        if (RefuseStartAfterEnd("StartPart3")) return;
        if (!IdentityAllowsStart("StartPart3")) return;
        if (runningCoroutine != null) { StopCoroutine(runningCoroutine); CancelNpcPreparationIfInProgress(); }
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
    /// warm-up spawning gate (BeginNpcWarmup), waits 'npcWarmupDuration' seconds on the shared experiment clock (see WaitExperimentSeconds),
    /// then closes the gate (EndNpcWarmup) immediately before returning. Never shows a panel, never fires
    /// OnTrialStart/OnTrialEnd, and never touches NPC movement/NavMeshAgent/Animator state directly - it only
    /// toggles SpawnerManager's existing spawning gates via its public API and raises two observer events for
    /// DataCaptureManager. When npcWarmupDuration &lt;= 0 this only skips the WAIT (NPCs are still enabled
    /// for the Part and can spawn once OnTrialStart fires, via SpawnerManager's own trialActive gate).
    /// cfg.npcDirectedMovement decides, inside SpawnerManager.SpawnAgent(), whether spawned NPCs get the Part 3
    /// attractor itinerary or the Part 2 wandering-point itinerary - this method does not need to know which.
    /// </summary>
    private IEnumerator RunNpcWarmup(PartCaptureConfig cfg, string partLabel)
    {
        float generationDuration = _effective != null ? Mathf.Max(0f, _effective.NpcWarmupDuration) : 0f;
        if (spawner != null)
            spawner.ResetForNewPart();   // also releases any NPC pause left over from an earlier Part / Session

        NpcPreparationInfo prep = NpcPreparation;
        prep.ResetForPart(partLabel);

        if (cfg == null || !cfg.npcEnabled)
            yield break;

        // The effective preparation settings are frozen here, once per Part (npcWarmupDuration itself comes from the frozen session settings).
        prep.applicable = true;
        prep.hold_until_start = holdNpcsUntilStart;
        prep.npc_generation_configured_duration_seconds = generationDuration;

        if (spawner == null)
        {
            SetNpcPreparation(NpcPreparationState.Failed, "failed_no_spawner");
            Debug.LogError("[ExperimentManager] NPC preparation failed: no SpawnerManager in the scene (the Part has NPCs enabled). The Part cannot start.", this);
            RaiseEvt("npc_pool_preparation_failed", partLabel, stringValue: "no_spawner");
            yield break;
        }

        spawner.defaultProfile = currentProfile;
        // NOTE: the NPC simulation (SpawnerManager.BeginNpcSimulation, which opens the spawn gate) is opened only AFTER the pool has been verified below, so an incomplete or failed pool can
        // never produce an NPC.

        // ---- Stage 1: POOL PREPARATION. Starts automatically; ends when the pool is complete, creation reports a failure, or the Part is cancelled. No time limit: a pool that is simply ----
        // slow or stuck stays PoolPreparing (progress is in the manifest and the log) and is never silently skipped.
        prep.pool_preparation_target_count = spawner.PoolPrewarmTarget;
        bool alreadyReady = spawner.IsPoolReady;
        SetNpcPreparation(alreadyReady ? NpcPreparationState.PoolReady : NpcPreparationState.PoolPreparing, alreadyReady ? "ready" : "preparing");
        double poolClockStart = double.IsNaN(spawner.PoolPreparationStartTAbs) ? CaptureClock.TAbs : spawner.PoolPreparationStartTAbs;
        prep.pool_preparation_started_t_abs = poolClockStart;
        RaiseEvt("npc_pool_preparation_started", partLabel, numValue: spawner.PoolPrewarmTarget, stringValue: alreadyReady ? "already_ready" : "waiting_for_pool");
        double nextProgressLog = CaptureClock.TAbs + 30.0;
        while (spawner != null && !spawner.IsPoolReady && !spawner.PoolFailed)
        {
            if (SessionEnded) { if (prep.state != NpcPreparationState.Cancelled.ToString()) SetNpcPreparation(NpcPreparationState.Cancelled, "cancelled"); yield break; }
            prep.pool_preparation_created_count = spawner.PoolPrewarmCreated;
            prep.pool_preparation_last_progress_t_abs = double.IsNaN(spawner.PoolLastProgressTAbs) ? -1 : spawner.PoolLastProgressTAbs;
            if (enableLifecycleLogs && CaptureClock.TAbs >= nextProgressLog)   // a status line every 30 s while preparing; diagnostic only, never a decision
            {
                nextProgressLog = CaptureClock.TAbs + 30.0;
                double since = double.IsNaN(spawner.PoolLastProgressTAbs) ? -1 : CaptureClock.TAbs - spawner.PoolLastProgressTAbs;
                Debug.Log($"[ExperimentManager] NPC pool preparation in progress: {spawner.PoolPrewarmCreated}/{spawner.PoolPrewarmTarget} objects, last object created {since:F1}s ago.", this);
            }
            yield return null;
        }
        if (spawner == null)
        {
            SetNpcPreparation(NpcPreparationState.Failed, "failed_no_spawner");
            Debug.LogError("[ExperimentManager] NPC preparation failed: the SpawnerManager was destroyed during pool preparation.", this);
            RaiseEvt("npc_pool_preparation_failed", partLabel, stringValue: "no_spawner");
            yield break;
        }
        prep.pool_preparation_created_count = spawner.PoolPrewarmCreated;
        prep.pool_preparation_last_progress_t_abs = double.IsNaN(spawner.PoolLastProgressTAbs) ? -1 : spawner.PoolLastProgressTAbs;
        if (spawner.PoolFailed)
        {
            prep.pool_preparation_error = spawner.PoolFailureReason;
            SetNpcPreparation(NpcPreparationState.Failed, "failed_error");
            Debug.LogError($"[ExperimentManager] NPC pool preparation failed ({spawner.PoolPrewarmCreated}/{spawner.PoolPrewarmTarget} objects): {spawner.PoolFailureReason}. NPC generation does not start and the participant cannot start.", this);
            RaiseEvt("npc_pool_preparation_failed", partLabel, numValue: spawner.PoolPrewarmCreated, stringValue: "error");
            yield break;
        }
        if (spawner.PoolPrewarmCreated != spawner.PoolPrewarmTarget)
        {
            SetNpcPreparation(NpcPreparationState.Failed, "failed_incomplete");
            Debug.LogError($"[ExperimentManager] NPC pool is not complete: {spawner.PoolPrewarmCreated}/{spawner.PoolPrewarmTarget} objects. NPC generation does not start and the participant cannot start.", this);
            RaiseEvt("npc_pool_preparation_failed", partLabel, numValue: spawner.PoolPrewarmCreated, stringValue: "incomplete");
            yield break;
        }
        prep.pool_preparation_completed_t_abs = double.IsNaN(spawner.PoolPreparationEndTAbs) ? CaptureClock.TAbs : spawner.PoolPreparationEndTAbs;
        prep.pool_preparation_duration_seconds = prep.pool_preparation_started_t_abs >= 0 ? prep.pool_preparation_completed_t_abs - prep.pool_preparation_started_t_abs : -1;
        SetNpcPreparation(NpcPreparationState.PoolReady, "ready");
        RaiseEvt("npc_pool_preparation_completed", partLabel, numValue: prep.pool_preparation_duration_seconds, stringValue: "created=" + spawner.PoolPrewarmCreated);

        // The verified pool opens the NPC simulation: from here until the session ends spawning and movement are independent of the Start panel, the trial window and the End panel.
        spawner.BeginNpcSimulation();

        if (generationDuration <= 0f)
            yield break;   // no generation period configured and therefore nothing to hold: NPCs run from pool readiness on

        // ---- Stage 2: NPC GENERATION (starts only now, automatically, whether or not anyone wears the headset). Spawning at spawnInterval and normal behaviour for 'generationDuration' ----
        // seconds of EXECUTED time. Executed time = sum of per-frame unscaled deltas, each capped at Time.maximumDeltaTime - the cap the NPC simulation itself obeys, so the timer advances exactly as
        // far as the simulation did. Unworn headset / lost focus with Unity still executing does not slow it; an interval in which Unity was not executing (OS suspension) contributes at most one
        // capped step and is reported as non-executing time, never as generated NPC time. The duration is the only stopping condition (no population target).
        spawner.BeginNpcWarmup();
        SetNpcPreparation(NpcPreparationState.NpcGenerationRunning, "ready");
        double genStart = CaptureClock.TAbs;
        int spawnedAtStart = spawner.NpcSpawnedTotal;
        double pauseSecAtStart = AppPauseSecondsNow, focusSecAtStart = FocusLostSecondsNow, unmountSecAtStart = HmdUnmountedSecondsNow;
        int pauseCountAtStart = _appPauseCount, unmountCountAtStart = _hmdUnmountCount;
        prep.npc_generation_started_t_abs = genStart;
        RaiseEvt("npc_warmup_start", partLabel, numValue: generationDuration);   // marks the generation start (event name kept for compatibility)

        double elapsed = 0, lastFrame = genStart, maxGap = 0;
        int stalls = 0, pauseCountLastFrame = _appPauseCount;
        while (true)
        {
            yield return null;
            double now = CaptureClock.TAbs;
            double gap = now - lastFrame; lastFrame = now;
            double cap = Time.maximumDeltaTime;
            elapsed += gap < cap ? gap : cap;
            if (gap > maxGap) maxGap = gap;
            if (gap > cap && !(_appPauseActive || _appPauseCount != pauseCountLastFrame)) stalls++;
            pauseCountLastFrame = _appPauseCount;
            if (elapsed >= generationDuration || SessionEnded) break;
        }
        if (SessionEnded) { if (prep.state != NpcPreparationState.Cancelled.ToString()) SetNpcPreparation(NpcPreparationState.Cancelled); yield break; }

        // Generation over, in ONE synchronous step (no yield): close the generation gate, then stop spawning + hold every NPC, so no spawn can slip in between.
        double genEnd = CaptureClock.TAbs;
        RaiseEvt("npc_warmup_end", partLabel, numValue: elapsed);   // marks the generation end (value = EXECUTED generation seconds)
        spawner.EndNpcWarmup();
        prep.npc_generation_completed_t_abs = genEnd;
        prep.npc_generation_actual_duration_seconds = elapsed;
        prep.npc_generation_wall_clock_seconds = genEnd - genStart;
        prep.npc_generation_non_executing_seconds = Math.Max(0.0, (genEnd - genStart) - elapsed);
        prep.npc_generation_max_frame_gap_seconds = maxGap;
        prep.npc_generation_unreported_stalls = stalls;
        prep.npc_generation_app_pause_count = _appPauseCount - pauseCountAtStart;
        prep.npc_generation_app_pause_seconds = AppPauseSecondsNow - pauseSecAtStart;
        prep.npc_generation_focus_lost_seconds = FocusLostSecondsNow - focusSecAtStart;
        prep.npc_generation_hmd_unmount_count = _hmdUnmountCount - unmountCountAtStart;
        prep.npc_generation_hmd_unmounted_seconds = HmdUnmountedSecondsNow - unmountSecAtStart;
        prep.npc_generation_spawn_count = spawner.NpcSpawnedTotal - spawnedAtStart;
        prep.active_npcs_at_generation_end = spawner.ActiveAgents.Count;
        SetNpcPreparation(NpcPreparationState.NpcGenerationCompleted);

        // ---- Stage 3: START-GATED PAUSE. The Start panel is shown by the caller right after this returns; the Start click resumes the NPCs (BeginTrial). ----
        if (holdNpcsUntilStart && !SessionEnded)
        {
            int held = spawner.PauseNpcBehaviour("generation_complete");
            prep.npc_behaviour_paused_t_abs = CaptureClock.TAbs;
            prep.active_npcs_at_pause = held;
            prep.pause_reason = "generation_complete";
            prep.pause_count++;
            SetNpcPreparation(NpcPreparationState.PausedAwaitingParticipantStart);
            RaiseEvt("npc_behaviour_paused", partLabel, numValue: held, stringValue: "generation_complete");
        }
        else
        {
            SetNpcPreparation(NpcPreparationState.Running);   // generation done, no hold configured: NPCs keep walking while the Start panel is read
        }
    }

    // ---- application lifecycle DIAGNOSTICS (they never change the generation timer; see RunNpcWarmup). The three events are different things and are kept apart: ----
    //   * headset unworn (OVRManager.HMDUnmounted): Unity keeps executing for as long as the system does not pause the app;
    //   * focus lost (OnApplicationFocus(false)): Unity keeps executing;
    //   * application pause (OnApplicationPause(true)): the system pauses the app - on Quest this follows an unmount only after the headset's Auto Sleep delay - and Unity stops executing frames.
    private double _appPauseTotal, _focusLostTotal, _hmdUnmountedTotal;
    private double _appPauseSince = -1, _focusLostSince = -1, _hmdUnmountedSince = -1;
    private int _appPauseCount, _hmdUnmountCount;
    private bool _appPauseActive => _appPauseSince >= 0;
    private double AppPauseSecondsNow => _appPauseTotal + (_appPauseSince >= 0 ? CaptureClock.TAbs - _appPauseSince : 0.0);
    private double FocusLostSecondsNow => _focusLostTotal + (_focusLostSince >= 0 ? CaptureClock.TAbs - _focusLostSince : 0.0);
    private double HmdUnmountedSecondsNow => _hmdUnmountedTotal + (_hmdUnmountedSince >= 0 ? CaptureClock.TAbs - _hmdUnmountedSince : 0.0);

    private void OnApplicationPause(bool paused)
    {
        if (paused) { if (_appPauseSince < 0) { _appPauseSince = CaptureClock.TAbs; _appPauseCount++; } }
        else if (_appPauseSince >= 0) { _appPauseTotal += CaptureClock.TAbs - _appPauseSince; _appPauseSince = -1; }
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus) { if (_focusLostSince < 0) _focusLostSince = CaptureClock.TAbs; }
        else if (_focusLostSince >= 0) { _focusLostTotal += CaptureClock.TAbs - _focusLostSince; _focusLostSince = -1; }
    }

    private void OnHmdMounted() { NoteHmdMounted(true); }
    private void OnHmdUnmounted() { NoteHmdMounted(false); }
    private void NoteHmdMounted(bool mounted)
    {
        if (!mounted) { if (_hmdUnmountedSince < 0) { _hmdUnmountedSince = CaptureClock.TAbs; _hmdUnmountCount++; } }
        else if (_hmdUnmountedSince >= 0) { _hmdUnmountedTotal += CaptureClock.TAbs - _hmdUnmountedSince; _hmdUnmountedSince = -1; }
    }

    private void OnEnable() { OVRManager.HMDMounted += OnHmdMounted; OVRManager.HMDUnmounted += OnHmdUnmounted; }
    private void OnDisable() { OVRManager.HMDMounted -= OnHmdMounted; OVRManager.HMDUnmounted -= OnHmdUnmounted; }

    // A Part / session that disappears while the preparation is still in progress is Cancelled, never left looking "in progress".
    private void CancelNpcPreparationIfInProgress()
    {
        NpcPreparationInfo prep = NpcPreparation;
        if (!prep.applicable) return;
        string st = prep.state;
        if (st == NpcPreparationState.PoolPreparing.ToString() || st == NpcPreparationState.PoolReady.ToString() || st == NpcPreparationState.NpcGenerationRunning.ToString() ||
            st == NpcPreparationState.NpcGenerationCompleted.ToString() || st == NpcPreparationState.PausedAwaitingParticipantStart.ToString())
            SetNpcPreparation(NpcPreparationState.Cancelled, "cancelled");
    }

    private void OnDestroy() { OVRManager.HMDMounted -= OnHmdMounted; OVRManager.HMDUnmounted -= OnHmdUnmounted; CancelNpcPreparationIfInProgress(); }

    private bool? HeadsetWorn()
    {
        if (HeadsetPresenceProbe != null) return HeadsetPresenceProbe();
        try { return OVRPlugin.initialized ? (bool?)OVRPlugin.userPresent : null; } catch { return null; }
    }

    // The Start panel is a participant-facing UI placed ONCE in front of the head pose it sees when it is shown. When generation finished while the headset was unworn, showing it at once would
    // place it wherever the unworn headset lies. So the panel (only the panel) waits until the headset is worn; the NPCs are already paused and visible by then. Unknown presence = no wait.
    private IEnumerator WaitForHeadsetBeforeStartPanel()
    {
        if (!waitForHeadsetBeforeStartPanel || !NpcPreparation.applicable) yield break;
        if (HeadsetWorn() != false) yield break;
        double t0 = CaptureClock.TAbs;
        if (enableLifecycleLogs) Debug.Log("[ExperimentManager] NPC environment ready and paused; the Start panel waits until the headset is worn.", this);
        while (HeadsetWorn() == false && !SessionEnded) yield return null;
        NpcPreparation.start_panel_headset_wait_seconds = CaptureClock.TAbs - t0;
    }

    // True (and the Part is abandoned before ANY Start panel) when the NPC preparation failed: the participant must never be able to start without the prepared NPC population.
    private bool AbortPartIfNpcPreparationFailed(string part)
    {
        NpcPreparationInfo prep = NpcPreparation;
        if (!prep.applicable || prep.state != NpcPreparationState.Failed.ToString()) return false;
        Debug.LogError($"[ExperimentManager] {part} aborted before the Start panel: NPC preparation failed ({prep.pool_preparation_status}). Fix the NPC pool configuration and start the Part again.", this);
        RaiseEvt("npc_preparation_part_aborted", part, stringValue: prep.pool_preparation_status);
        currentRunningPart = RunningPart.None;
        runningCoroutine = null;
        return true;
    }

    // Single place that changes the preparation state (name string is what the manifest records).
    private void SetNpcPreparation(NpcPreparationState s, string poolStatus = null)
    {
        NpcPreparationInfo prep = NpcPreparation;
        prep.state = s.ToString();
        if (prep.state_trail.Length < 400) prep.state_trail += ">" + prep.state;
        if (poolStatus != null) prep.pool_preparation_status = poolStatus;
    }

    // The Start click's NPC side, called from BeginTrial() - the one path every Part's participant start goes through - immediately before OnTrialStart, in the same frame.
    // Idempotent: a duplicate start callback finds the NPCs already released and does nothing.
    private void ResumeNpcBehaviourForTrialStart()
    {
        NpcPreparationInfo prep = NpcPreparation;
        if (!prep.applicable) return;
        double now = CaptureClock.TAbs;
        if (prep.participant_start_t_abs < 0) prep.participant_start_t_abs = now;

        if (prep.state == NpcPreparationState.PausedAwaitingParticipantStart.ToString())
        {
            int released = spawner != null ? spawner.ResumeNpcBehaviour("participant_start") : 0;
            prep.npc_behaviour_resumed_t_abs = now;
            prep.active_npcs_at_resume = released;
            prep.resume_reason = "participant_start";
            prep.resume_count++;
            SetNpcPreparation(NpcPreparationState.Running);
            RaiseEvt("npc_behaviour_resumed", prep.part, numValue: released, stringValue: "participant_start");
        }
        else if (prep.state == NpcPreparationState.PoolPreparing.ToString() || prep.state == NpcPreparationState.NpcGenerationRunning.ToString())
        {
            // Not reachable through the Part flows (the Start panel is only shown after RunNpcWarmup returned). Counted and reported, never silently accepted as a finished generation.
            prep.early_start_attempts++;
            Debug.LogError($"[ExperimentManager] Trial start while NPC preparation is '{prep.state}': the generation period was not completed (early_start_attempts={prep.early_start_attempts}).", this);
        }
        else if (prep.state == NpcPreparationState.PoolReady.ToString())
        {
            SetNpcPreparation(NpcPreparationState.Running);   // no generation period configured: NPCs were never paused
        }
    }

    #endregion

    #region Experiment clock

    /// <summary>
    /// THE experiment timer for every Part (F-08). Waits until 'seconds' of CaptureClock time (Time.realtimeSinceStartupAsDouble: monotonic, independent
    /// of Time.timeScale, not clamped by Time.maximumDeltaTime) have elapsed since the call, checked once per frame. Parts 1 and 2 used to accumulate
    /// Time.deltaTime, which Unity clamps to maximumDeltaTime, so a severe frame hitch shortened the measured duration; Part 3 already used realtime.
    /// POLICY: application suspension COUNTS as elapsed experiment time (wall clock, same as Part 3's D1/D2 limits, which this method matches). The
    /// suspension is not hidden: app_pause / app_focus rows with their own t_abs are in events.csv, so the participant-active duration can be reconstructed
    /// as (elapsed - sum of suspended intervals). Durations and thresholds themselves are unchanged. Movement, animation and physics keep using Time.deltaTime.
    /// </summary>
    private static IEnumerator WaitExperimentSeconds(double seconds)
    {
        double start = CaptureClock.TAbs;
        while (CaptureClock.TAbs - start < seconds) yield return null;
    }

    #endregion

    #region Part implementations (coroutines)

    private IEnumerator RunPart1()
    {
        if (!EnsureSessionOverridesApplied()) { Debug.LogError("ExperimentManager.RunPart1 refused: the effective session settings are not available."); yield break; }

        currentRunningPart = RunningPart.Part1;
        CurrentPartConfig = _frozenPartConfigs[0];
        EndPanelActive = false;
        endPanelDisplayHandled = false;
        endPanelVisibleRealtime = double.NaN;
        RaiseEvt("part_start", "Part1");

        ConfigureTargetsForPart1();
        TeleportPlayerTo(part1StartPosition);

        // NPC presence is now controlled entirely by part1Config.npcEnabled (Requirement 1/3) - no more
        // Part1-specific special-casing here. With the shipped default (npcEnabled=false) this call returns
        // immediately after clearing any stale End-Panel hard-stop, exactly reproducing "Part1 is NPC-free".
        yield return RunNpcWarmup(CurrentPartConfig, "Part1");
        if (AbortPartIfNpcPreparationFailed("Part1")) yield break;

        yield return WaitForHeadsetBeforeStartPanel();
        bool started = false;
        RaiseEvt("panel_shown", "Part1", label: "part1_start");
        uiManager.ShowPart1StartPanel(() => { started = true; RaiseEvt("panel_confirmed", "Part1", label: "part1_start"); });
        yield return new WaitUntil(() => started);

        BeginTrial();

        targetWasSelected = false;
        yield return WaitExperimentSeconds(_effective.Part1Duration);

        EndTrial();

        // End Panel hard stop: the participant lock, EndPanelActive and OnEndPanelShown (NPC freeze, capture stop) all happen in
        // HandleEndPanelDisplayed, which UIManager calls at the moment the panel becomes visible (the confirm callback repeats
        // the call as an idempotent safety net) - not at EndTrial() and not after the participant confirms (part_end below).
        bool confirmed = false;
        RaiseEvt("panel_shown", "Part1", label: "part1_end");
        uiManager.ShowPart1EndPanel(
            () => { HandleEndPanelDisplayed("Part1", false); confirmed = true; RaiseEvt("panel_confirmed", "Part1", label: "part1_end"); },
            () => HandleEndPanelDisplayed("Part1", true));
        yield return new WaitUntil(() => confirmed);

        RaiseEvt("part_end", "Part1");
        currentRunningPart = RunningPart.None;
        TerminateSession("Part1");
    }

    private IEnumerator RunPart2()
    {
        if (!EnsureSessionOverridesApplied()) { Debug.LogError("ExperimentManager.RunPart2 refused: the effective session settings are not available."); yield break; }

        // The group index comes from the resolved (validated) session configuration and is NEVER clamped: an index outside the configured groups would silently
        // run a different experimental condition, so the Part is refused before anything starts. Part 2 Group Number == Group Index (0..part2Groups.Length-1).
        int gIdx = Controller.ActivePart2GroupIndex;
        if (part2Groups != null && part2Groups.Length > 0 && (gIdx < 0 || gIdx >= part2Groups.Length))
        {
            Debug.LogError($"ExperimentManager.RunPart2 refused: part2GroupIndex {gIdx} is outside 0..{part2Groups.Length - 1} (the scene has {part2Groups.Length} Part 2 groups). Fix the session configuration.");
            yield break;
        }

        currentRunningPart = RunningPart.Part2;
        CurrentPartConfig = _frozenPartConfigs[1];
        EndPanelActive = false;
        endPanelDisplayHandled = false;
        endPanelVisibleRealtime = double.NaN;
        RaiseEvt("part_start", "Part2");

        if (part2Groups == null || part2Groups.Length == 0)
        {
            Debug.LogError("ExperimentManager.RunPart2: No Part2 groups configured in inspector.");
            yield break;
        }
        Part2Group group = part2Groups[gIdx];

        Transform chosenStart = ChooseRandomFromArray(group.startPositions);
        Transform chosenTeleport = ChooseRandomFromArray(group.teleportPositions);

        TeleportPlayerTo(chosenStart);
        FaceTrackingBegin();   // schema v17: see Part 3

        yield return RunNpcWarmup(CurrentPartConfig, "Part2");
        if (AbortPartIfNpcPreparationFailed("Part2")) yield break;

        yield return WaitForHeadsetBeforeStartPanel();
        yield return FaceTrackingReadyGate("preflight");
        // Phase labels for Part 2 (metadata only - no timing or flow depends on them): the Start panel is being read until the participant confirms.
        SetPhase("p2_start_pending", "Part2");
        bool started = false;
        RaiseEvt("panel_shown", "Part2", label: "part2_start");
        uiManager.ShowPart2StartPanel(() => { started = true; RaiseEvt("panel_confirmed", "Part2", label: "part2_start"); });
        yield return new WaitUntil(() => started);
        yield return FaceTrackingReadyGate("after_start_click");
        if (SessionEnded) yield break;

        // The phase is set BEFORE the trial opens: recorders start sampling inside BeginTrial() (the first gaze row is written synchronously), so the very first
        // sample of the trial already carries the correct phase instead of the previous one.
        SetPhase("p2_task1", "Part2");   // active task, first timed period (part2FirstDuration)
        BeginTrial();

        targetWasSelected = false;
        yield return WaitExperimentSeconds(_effective.Part2FirstDuration);

        bool teleported = false;
        SetPhase("p2_continue_pending", "Part2");   // Continue panel being read: the trial is open and recorded, but this is NOT task time
        // Mandatory checkpoint (same mechanism as the Part 3 Destination 2 panel): lock the participant BEFORE the panel is shown, release only at the Continue click.
        if (lockParticipantWhilePart2ContinuePanel) LockParticipantForCheckpoint("P2", "part2_continue_panel_shown");
        RaiseEvt("panel_shown", "Part2", label: "part2_continue");
        uiManager.ShowPart2ContinuePanel(() =>
        {
            if (teleported) return;   // a duplicate Continue event must not teleport or restart the second period twice
            RaiseEvt("panel_confirmed", "Part2", label: "part2_continue");
            TeleportPlayerTo(chosenTeleport ?? (group.teleportPositions != null && group.teleportPositions.Length > 0 ? group.teleportPositions[0] : null));
            teleported = true;
            SetPhase("p2_task2", "Part2");   // active task, second timed period (part2SecondDuration), from the confirm click / teleport
            ReleaseParticipantCheckpointLock("P2", "part2_continue_confirmed");   // after the scripted teleport and phase change: the next area starts unlocked
        });

        yield return new WaitUntil(() => teleported);
        ReleaseParticipantCheckpointLock("P2", "part2_continue_confirmed");   // safety net only (idempotent): the callback above already released it

        yield return WaitExperimentSeconds(_effective.Part2SecondDuration);

        EndTrial();
        SetPhase("ended", "Part2");

        // End Panel hard stop at the moment the panel becomes visible (see RunPart1() for the full explanation).
        bool confirmed = false;
        RaiseEvt("panel_shown", "Part2", label: "part2_end");
        uiManager.ShowPart2EndPanel(
            () => { HandleEndPanelDisplayed("Part2", false); confirmed = true; RaiseEvt("panel_confirmed", "Part2", label: "part2_end"); },
            () => HandleEndPanelDisplayed("Part2", true));
        yield return new WaitUntil(() => confirmed);

        RaiseEvt("part_end", "Part2");
        currentRunningPart = RunningPart.None;
        TerminateSession("Part2");
    }

    private IEnumerator RunPart3()
    {
        if (!EnsureSessionOverridesApplied()) { Debug.LogError("[ExperimentManager] RunPart3 refused: the effective session settings are not available."); yield break; }

        // Resolve the group first and refuse (never clamp) an out-of-range index: clamping would silently run another group's start/destinations.
        // The zero-based Group Index IS the Group Number (0..11) and indexes Part3GroupTable directly.
        int groupIndex = Controller.ActivePart3GroupIndex;
        if (groupIndex < 0 || groupIndex >= Part3GroupTable.Length)
        {
            Debug.LogError($"[ExperimentManager] RunPart3 refused: part3GroupIndex {groupIndex} is outside 0..{Part3GroupTable.Length - 1}. Fix the session configuration.");
            yield break;
        }

        currentRunningPart = RunningPart.Part3;
        CurrentPartConfig = _frozenPartConfigs[2];
        EndPanelActive = false;
        endPanelDisplayHandled = false;
        endPanelVisibleRealtime = double.NaN;
        p3Timing = new Part3ValidTiming(_effective.Part3Destination1MaxSeconds, _effective.Part3TotalMaxSeconds);
        p3D2Ran = false;
        RaiseEvt("part_start", "Part3");
        SetPhase("idle");

        // Clean slate: no candidate is interactable (covers a re-run after an interrupted previous Phase 3).
        SetAllPart3CandidatesInteractable(false);
        selectionWindowOpen = false;
        pendingSelections.Clear();

        // --- 1. Resolve the participant's group (Group Index == Group Number, 0..11). ---
        Part3GroupDef def = Part3GroupTable[groupIndex];

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
        // The expected answer is the assigned building's own identity (TargetSelectable.ResolvedTargetId == the
        // building's GameObject name). A selection is correct only when its id EQUALS the stage's expected id; if a
        // destination has no resolvable id nothing can be correct (it is never "any click counts").
        if (string.IsNullOrEmpty(d1AssignedId))
            Debug.LogError("[ExperimentManager] Part3 Destination 1 building has no TargetSelectable id; no selection can be scored correct for Destination 1.");
        if (string.IsNullOrEmpty(d2AssignedId))
            Debug.LogError("[ExperimentManager] Part3 Destination 2 building has no TargetSelectable id; no selection can be scored correct for Destination 2.");
        if (!string.IsNullOrEmpty(d1AssignedId) && d1AssignedId == d2AssignedId)
            Debug.LogError("[ExperimentManager] Part3 Destination 1 and Destination 2 resolve to the same building id '" + d1AssignedId + "'; the stages cannot be scored independently.");

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
        FaceTrackingBegin();   // schema v17: face tracking starts now, during the NPC warm-up (nothing is recorded)

        // --- 6. Persist the assignment immediately via the capture event (legacy CSV removed). ---
        AppendPart3Log("assigned", groupIndex, def, startPoint, destination1Building, destination2Building,
            selectedId: "", found: false, elapsedSeconds: 0.0, notes: "");

        // --- 6b. NPC warm-up: NPCs spawn/spread out via SpawnerManager's normal runtime behaviour, BEFORE
        // the Phase 3 Start panel is shown. Not part of the participant's trial - see DataCaptureManager.
        yield return RunNpcWarmup(CurrentPartConfig, "Part3");
        if (AbortPartIfNpcPreparationFailed("Part3")) yield break;

        // --- 7a. Think-aloud audio preflight (permission, device, capture format, writable output). Part 3 only; nothing is recorded yet. ---
        _part3Audio = ResolvePart3Audio();
        if (_part3Audio != null) yield return Part3AudioPreflightGate(_part3Audio);

        // --- 7. Phase 3 Start panel. (All candidates are still non-interactable ordinary buildings.) ---
        yield return WaitForHeadsetBeforeStartPanel();
        yield return FaceTrackingReadyGate("preflight");   // schema v17: bounded wait for the first valid Visual eyelid frame; a no-op when disabled / not applicable
        bool started = false;
        double startClickT = double.NaN;
        RaiseEvt("panel_shown", "Part3", label: "part3_start");
        uiManager.ShowPart3StartPanel(() => { startClickT = CaptureClock.TAbs; started = true; RaiseEvt("panel_confirmed", "Part3", label: "part3_start"); });
        yield return new WaitUntil(() => started);

        // --- 7b. The microphone must be delivering samples BEFORE the participant may act: the Start click starts the recording, BeginTrial() waits for the first sample. ---
        if (_part3Audio != null) yield return Part3AudioStartGate(_part3Audio, startClickT);
        if (SessionEnded) yield break;
        yield return FaceTrackingReadyGate("after_start_click");   // only when faceTrackingStartAfterStartClick; the trial (and the first gaze row) has not begun yet
        if (SessionEnded) yield break;

        // Phase first, then the trial: the first sample written inside BeginTrial() must already be labelled d1_pending (panel reading), not 'idle'.
        SetPhase("d1_pending");
        if (_part3Audio != null && _part3Audio.IsRecording) _part3Audio.NoteExperimentStart(CaptureClock.TAbs);
        BeginTrial();

        // Diagnostic only (phase_end elapsed time, a wall-clock trial window that INCLUDES panel reading). It no longer limits any
        // search: the D1 / D2 limits come from p3Timing, started by the D1 / D2 Continue callbacks.
        double trialStartRealtime = CaptureClock.TAbs;

        // ============================ DESTINATION 1 ============================
        bool dest1Confirmed = false;
        RaiseEvt("destination_shown", "Part3", label: "D1");
        RaiseEvt("panel_shown", "Part3", label: "part3_dest1");
        uiManager.ShowPart3Destination1Panel(() =>
        {
            // Valid interval 1 starts at the actual Continue click: stamped before anything else in this callback.
            bool firstD1Confirm = p3Timing.RecordD1Continue(CaptureClock.TAbs);
            dest1Confirmed = true;
            if (firstD1Confirm) RaiseEvt("part3_d1_continue_confirmed", "Part3", label: "D1");
            RaiseEvt("panel_confirmed", "Part3", label: "part3_dest1");
            RaiseEvt("destination_confirmed", "Part3", label: "D1");
        });
        yield return new WaitUntil(() => dest1Confirmed);
        SetPhase("d1_search");

        // Destination 1 assigned: make ONLY destination1Building interactable (TargetSelectable + hover).
        SetAllPart3CandidatesInteractable(false);
        SetCandidateInteractable(destination1Building, true);
        RaiseEvt("candidate_interactable_on", "Part3", label: "D1", building: destination1Building);
        GameObject d1Alternate = _effective.AllowWrongCandidateSelection ? GetAlternateCandidate(d1ZoneTargets, destination1Building) : null;
        if (d1Alternate != null)
        {
            SetCandidateInteractable(d1Alternate, true);
            RaiseEvt("candidate_interactable_on", "Part3", label: "D1", building: d1Alternate);
        }

        GameObject dest1ShrunkInstance = null;
        if (uiManager != null && uiManagerHasShrunkTemplate())
            dest1ShrunkInstance = uiManager.CreateAndAnchorShrunkPanel(uiManager.part3Dest1ShrunkTemplate, UIManager.Corner.TopRight);

        targetWasSelected = false;
        lastSelectedTargetData = null;
        pendingSelections.Clear();
        selectionWindowOpen = true;
        bool d1Found = false;
        // The D1 search clock is the D1 Continue click recorded by the callback (this coroutine only resumes a frame later).
        // Safety net: if the click was somehow not recorded, start the clock now so the search is still bounded.
        p3Timing.RecordD1Continue(CaptureClock.TAbs);
        double d1StartRealtime = p3Timing.D1ContinueRealtime;

        // Limit: min(part3Destination1MaxSeconds, part3TotalMaxSeconds) from the D1 Continue click (Part3ValidTiming.D1SearchOpen).
        // Queued selections are swept BEFORE the deadline test, so a click made inside the window but only noticed on the frame that
        // closes it still counts. Endpoint: the click's own stamp when found; otherwise d1Now, the frame time that failed the test.
        double d1Now = CaptureClock.TAbs;
        double d1EndRealtime = double.NaN;
        while (true)
        {
            // Score every queued selection in click order. correct <=> selected id == this stage's expected id.
            // Once Destination 1 is found, the remaining queued selections of that frame are discarded, so a
            // destination can be resolved (and its success event emitted) at most once.
            while (!d1Found && pendingSelections.Count > 0)
            {
                PendingSelection sel = pendingSelections.Dequeue();
                if (!p3Timing.D1SelectionInWindow(sel.realtime)) continue;   // clicked after the search limit: not part of the search
                string clickedId = sel.id;
                bool correct = !string.IsNullOrEmpty(d1AssignedId) && clickedId == d1AssignedId;

                AppendPart3Log("d1_click", groupIndex, def, startPoint, destination1Building, destination2Building,
                    selectedId: clickedId, found: correct,
                    elapsedSeconds: sel.realtime - d1StartRealtime,
                    notes: correct ? "correct" : "wrong_building", interactionId: sel.interactionId);

                if (correct) { d1Found = true; d1EndRealtime = sel.realtime; }
                // A wrong-building selection does NOT end the sub-phase and shows no feedback.
            }
            if (d1Found || !p3Timing.D1SearchOpen(d1Now)) break;
            yield return null;
            d1Now = CaptureClock.TAbs;
        }
        if (!d1Found) d1EndRealtime = d1Now;   // timeout branch: the moment it was taken

        // Interval 1 ends HERE (search endpoint), not when the D2 panel appears; this also fixes the D2 budget from that same value.
        p3Timing.RecordD1SearchEnd(d1EndRealtime, d1Found);
        RaiseEvt("part3_d1_search_end", "Part3", label: "D1", numValue: p3Timing.Interval1Seconds, stringValue: SearchEndText(
            p3Timing.D1EndReason, p3Timing.D1ContinueRealtime, p3Timing.D1SearchEndRealtime, p3Timing.Interval1Seconds, p3Timing.D2BudgetSeconds));

        selectionWindowOpen = false;
        pendingSelections.Clear();
        targetWasSelected = false;
        lastSelectedTargetData = null;

        string d1SelectedId = d1Found ? d1AssignedId : "";
        double d1Elapsed = p3Timing.Interval1Seconds;   // the same value the D2 budget was derived from
        // Does any of the total budget remain for Destination 2? (If not, D2 is skipped, as before.)
        bool budgetRemainsAfterD1 = p3Timing.D2MayRun;
        bool totalTimeUpAfterD1 = !budgetRemainsAfterD1;

        if (dest1ShrunkInstance != null) { Destroy(dest1ShrunkInstance); dest1ShrunkInstance = null; }

        // Destination 1 finished: the D1 target is a plain non-interactable building again.
        SetCandidateInteractable(destination1Building, false);
        RaiseEvt("candidate_interactable_off", "Part3", label: "D1", building: destination1Building);
        if (d1Alternate != null)
        {
            SetCandidateInteractable(d1Alternate, false);
            RaiseEvt("candidate_interactable_off", "Part3", label: "D1", building: d1Alternate);
        }

        AppendPart3Log("d1_result", groupIndex, def, startPoint, destination1Building, destination2Building,
            selectedId: d1SelectedId, found: d1Found, elapsedSeconds: d1Elapsed,
            notes: d1Found ? "found" : (totalTimeUpAfterD1 ? "total_time_reached" : "d1_timeout_10min"),
            terminationReason: p3Timing.D1EndReason);

        // ============================ DESTINATION 2 ============================
        bool d2Ran = false;
        bool d2Found = false;
        string d2SelectedId = "";
        double d2Elapsed = 0.0;

        if (budgetRemainsAfterD1)
        {
            d2Ran = true;
            p3D2Ran = true;
            SetPhase("d2_pending");

            // Mandatory checkpoint: the D2 panel is positioned once in world space and the Part cannot continue without its Continue click, so the participant is
            // locked from the moment it is shown (BEFORE it becomes visible: the placement may wait up to settleTimeout) until that click. See lockParticipantWhileDestination2Panel.
            if (lockParticipantWhileDestination2Panel) LockParticipantForCheckpoint("D2", "d2_panel_shown");

            bool dest2Confirmed = false;
            RaiseEvt("destination_shown", "Part3", label: "D2");
            RaiseEvt("panel_shown", "Part3", label: "part3_dest2");
            uiManager.ShowPart3Destination2Panel(() =>
            {
                // Valid interval 2 starts at the actual Continue click: stamped before anything else in this callback.
                bool firstD2Confirm = p3Timing.RecordD2Continue(CaptureClock.TAbs);
                dest2Confirmed = true;
                if (firstD2Confirm) RaiseEvt("part3_d2_continue_confirmed", "Part3", label: "D2");
                RaiseEvt("panel_confirmed", "Part3", label: "part3_dest2");
                RaiseEvt("destination_confirmed", "Part3", label: "D2");
                ReleaseParticipantCheckpointLock("D2", "d2_continue_confirmed");   // same moment the D2 search clock starts (stamped first, above)
            },
            () =>
            {
                // UI event only: the D2 panel is visible. It is NOT an interval endpoint (interval 1 already ended with the D1
                // search) and it changes no budget; the gap since the D1 search end is logged for transition analysis.
                if (p3Timing.RecordD2PanelVisible(CaptureClock.TAbs))
                {
                    double gap = p3Timing.D1EndToD2PanelVisibleSeconds;
                    RaiseEvt("part3_d2_panel_visible", "Part3", label: "D2", numValue: double.IsNaN(gap) ? (double?)null : gap,
                        stringValue: $"d1_search_end_to_panel_visible_s={Fs(gap)};panel_visible_t_abs={Fs(p3Timing.D2PanelVisibleRealtime)}");
                }
            });
            yield return new WaitUntil(() => dest2Confirmed);
            ReleaseParticipantCheckpointLock("D2", "d2_continue_confirmed");   // safety net only (idempotent): the callback above already released it
            SetPhase("d2_search");

            // Destination 2 assigned: make ONLY destination2Building interactable.
            SetAllPart3CandidatesInteractable(false);
            SetCandidateInteractable(destination2Building, true);
            RaiseEvt("candidate_interactable_on", "Part3", label: "D2", building: destination2Building);
            GameObject d2Alternate = _effective.AllowWrongCandidateSelection ? GetAlternateCandidate(d2ZoneTargets, destination2Building) : null;
            if (d2Alternate != null)
            {
                SetCandidateInteractable(d2Alternate, true);
                RaiseEvt("candidate_interactable_on", "Part3", label: "D2", building: d2Alternate);
            }

            GameObject dest2ShrunkInstance = null;
            if (uiManager != null && uiManagerHasShrunkTemplatePart2())
                dest2ShrunkInstance = uiManager.CreateAndAnchorShrunkPanel(uiManager.part3Dest2ShrunkTemplate, UIManager.Corner.TopRight);

            targetWasSelected = false;
            lastSelectedTargetData = null;
            pendingSelections.Clear();
            selectionWindowOpen = true;
            // The D2 search clock is the D2 Continue click recorded by the callback; its budget is the REMAINING total budget
            // (max(0, total - interval 1), fixed when the D1 search ended), not a fresh full one, and panel placement / reading
            // time before the click is not part of it.
            p3Timing.RecordD2Continue(CaptureClock.TAbs);   // safety net only; the callback already recorded it
            double d2StartRealtime = p3Timing.D2ContinueRealtime;

            // Same loop shape and endpoint rule as Destination 1.
            double d2Now = CaptureClock.TAbs;
            double d2EndRealtime = double.NaN;
            while (true)
            {
                // Same rule as Destination 1, against Destination 2's OWN expected id: a building that was correct
                // for Destination 1 is not correct here, and a destination resolves at most once.
                while (!d2Found && pendingSelections.Count > 0)
                {
                    PendingSelection sel = pendingSelections.Dequeue();
                    if (!p3Timing.D2SelectionInWindow(sel.realtime)) continue;
                    string clickedId = sel.id;
                    bool correct = !string.IsNullOrEmpty(d2AssignedId) && clickedId == d2AssignedId;

                    AppendPart3Log("d2_click", groupIndex, def, startPoint, destination1Building, destination2Building,
                        selectedId: clickedId, found: correct,
                        elapsedSeconds: sel.realtime - d2StartRealtime,
                        notes: correct ? "correct" : "wrong_building", interactionId: sel.interactionId);

                    if (correct) { d2Found = true; d2EndRealtime = sel.realtime; }
                }
                if (d2Found || !p3Timing.D2SearchOpen(d2Now)) break;
                yield return null;
                d2Now = CaptureClock.TAbs;
            }
            if (!d2Found) d2EndRealtime = d2Now;   // budget expired: the moment the timeout branch was taken

            // Interval 2 ends HERE (search endpoint), not when the End panel appears.
            p3Timing.RecordD2SearchEnd(d2EndRealtime, d2Found);
            RaiseEvt("part3_d2_search_end", "Part3", label: "D2", numValue: p3Timing.Interval2Seconds, stringValue: SearchEndText(
                p3Timing.D2EndReason, p3Timing.D2ContinueRealtime, p3Timing.D2SearchEndRealtime, p3Timing.Interval2Seconds, p3Timing.D2BudgetSeconds));

            selectionWindowOpen = false;
            pendingSelections.Clear();
            targetWasSelected = false;
            lastSelectedTargetData = null;

            d2SelectedId = d2Found ? d2AssignedId : "";
            d2Elapsed = p3Timing.Interval2Seconds;

            if (dest2ShrunkInstance != null) { Destroy(dest2ShrunkInstance); dest2ShrunkInstance = null; }

            // Destination 2 finished: the D2 target is a plain non-interactable building again.
            SetCandidateInteractable(destination2Building, false);
            RaiseEvt("candidate_interactable_off", "Part3", label: "D2", building: destination2Building);
            if (d2Alternate != null)
            {
                SetCandidateInteractable(d2Alternate, false);
                RaiseEvt("candidate_interactable_off", "Part3", label: "D2", building: d2Alternate);
            }
        }
        else
        {
            Debug.Log("[ExperimentManager] Part3: the total valid-exposure budget was already spent by the Destination 1 search; Destination 2 skipped.");
        }

        // ============================ END ============================
        double phaseTotalElapsed = CaptureClock.TAbs - trialStartRealtime;   // wall clock incl. panel reading; diagnostic
        // A search that ends without its target can only have ended because the total valid-exposure budget ran out (D2 is skipped
        // or limited by it); the label text is kept for downstream compatibility.
        bool totalBudgetReached = !d2Ran || !d2Found;
        string endReason =
            (d1Found && d2Found) ? "both_destinations_found" :
            totalBudgetReached ? "total_time_15min_reached" :
            "d2_found_d1_missed";

        // The structured reason (not the legacy note text) is what DataCaptureManager classifies as a timeout. A skipped D2 never ran,
        // so it carries NoBudgetRemaining, which Part3TerminationReason.IsTimeout rejects.
        AppendPart3Log(d2Ran ? "d2_result" : "d2_skipped", groupIndex, def, startPoint, destination1Building, destination2Building,
            selectedId: d2SelectedId, found: d2Found, elapsedSeconds: d2Elapsed, notes: endReason,
            terminationReason: d2Ran ? p3Timing.D2EndReason : Part3TerminationReason.NoBudgetRemaining);
        AppendPart3Log("phase_end", groupIndex, def, startPoint, destination1Building, destination2Building,
            selectedId: "", found: (d1Found && d2Found), elapsedSeconds: phaseTotalElapsed, notes: endReason);

        EndTrial();
        SetPhase("ended");

        // Phase 3 is over: no Phase 3 target-specific interaction remains anywhere.
        SetAllPart3CandidatesInteractable(false);

        // End Panel hard stop at the moment the panel becomes visible (see RunPart1() for the full explanation). For Part 3 the same
        // transition also records the End-panel endpoint of valid interval 2 and writes both interval results.
        bool confirmed = false;
        RaiseEvt("panel_shown", "Part3", label: "part3_end");
        uiManager.ShowPart3EndPanel(
            () => { HandleEndPanelDisplayed("Part3", false); confirmed = true; RaiseEvt("panel_confirmed", "Part3", label: "part3_end"); },
            () => HandleEndPanelDisplayed("Part3", true));
        yield return new WaitUntil(() => confirmed);

        RaiseEvt("part_end", "Part3");
        currentRunningPart = RunningPart.None;
        TerminateSession("Part3");
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
        if (SessionEnded) return;   // terminal session: no scripted relocation from a stale callback

        // Part 2 invariant: no movement may leave the Exploration Area. The scheduled/scripted teleports are the
        // experiment's own moves (start position, cross-bank transition), so they are validated against the SAME
        // boundary query as participant teleports instead of bypassing it. Fail closed when the destination is invalid.
        if (currentRunningPart == RunningPart.Part2)
        {
            ExplorationBoundary eb = ExplorationBoundary.Instance;
            if (eb == null)
            {
                Debug.LogWarning($"[ExperimentManager] Scripted Part 2 teleport to '{dest.name}' could not be validated: no ExplorationBoundary instance.", this);
            }
            else if (!eb.IsLandingPositionAllowed(dest.position, out string why))
            {
                Debug.LogError($"[ExperimentManager] Scripted Part 2 teleport to '{dest.name}' at {dest.position} REJECTED ({why}): " +
                               "the destination is outside the Exploration Area. Fix the Part 2 group start/teleport positions.", this);
                RaiseEvt("teleport_rejected", label: "scripted", stringValue: "scripted_" + why, fromPos: playerRig.position, toPos: dest.position);
                return;
            }
        }

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

        // ResolvedTargetId is exactly what TargetSelectable.Select() reports on a click, so expected and selected
        // ids are produced by the same rule.
        return (ts != null) ? ts.ResolvedTargetId : null;
    }

    /// <summary>The other candidate of the same zone (the legitimate choice set), or null if there is none.</summary>
    private GameObject GetAlternateCandidate(Part3ZoneTargets zone, GameObject assigned)
    {
        if (zone == null || assigned == null) return null;
        if (zone.buildingA == assigned) return zone.buildingB;
        if (zone.buildingB == assigned) return zone.buildingA;
        return null;
    }

    /// <summary>
    /// One-time sanity check of building identities: every TargetSelectable must resolve to a unique, non-empty id
    /// (the id equals the building's GameObject name). Diagnostic only; never changes state.
    /// </summary>
    private void ValidateTargetIds()
    {
        TargetSelectable[] all = FindObjectsOfType<TargetSelectable>(true);
        var seen = new Dictionary<string, TargetSelectable>();
        for (int i = 0; i < all.Length; i++)
        {
            string id = all[i].ResolvedTargetId;
            if (string.IsNullOrEmpty(id))
            {
                Debug.LogError("[ExperimentManager] TargetSelectable on '" + all[i].name + "' has an empty id.", all[i]);
                continue;
            }
            if (!string.IsNullOrEmpty(all[i].targetId) && all[i].targetId != all[i].name)
                Debug.LogWarning("[ExperimentManager] TargetSelectable id '" + all[i].targetId + "' differs from its GameObject name '" + all[i].name + "'.", all[i]);
            if (seen.TryGetValue(id, out TargetSelectable other))
                Debug.LogError("[ExperimentManager] Duplicate TargetSelectable id '" + id + "' on '" + other.name + "' and '" + all[i].name + "': building identity is ambiguous.", all[i]);
            else
                seen[id] = all[i];
        }
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
    private void AppendPart3Log(string eventName, int groupIndex, Part3GroupDef def,
        Transform startPoint, GameObject d1Building, GameObject d2Building,
        string selectedId, bool found, double elapsedSeconds, string notes, string terminationReason = "", string interactionId = "")
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
            hasNum = eventName != "assigned",   // click / result / skipped / phase_end carry an elapsed time (0 is a real value); 'assigned' carries none
            interactionId = interactionId ?? "",
            boolValue = found,
            notes = notes ?? "",
            groupNumber = groupIndex,   // zero-based: Group Number == Group Index
            hasGroup = true,
            startZone = def.StartZone.ToString(),
            d1Zone = def.Destination1Zone.ToString(),
            d2Zone = def.Destination2Zone.ToString(),
            startPointName = startPoint != null ? startPoint.name : "",
            d1Building = d1Building,
            d2Building = d2Building,
            d1TargetId = GetAssignedTargetId(d1Building) ?? "",
            d2TargetId = GetAssignedTargetId(d2Building) ?? "",
            terminationReason = terminationReason ?? ""
        };

        try { OnExperimentEvent?.Invoke(info); }
        catch (Exception ex) { Debug.LogWarning("[ExperimentManager] OnExperimentEvent (part3_log) handler threw: " + ex.Message); }
    }

    // ---- Data-capture event helpers -------------------------------------------------------------

    private void RaiseEvt(string type, string part = null, string label = "", string stringValue = "",
        double? numValue = null, bool boolValue = false, GameObject building = null,
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
            numValue = numValue ?? 0.0,
            hasNum = numValue.HasValue,       // F-07: a numeric value is present only when the caller supplied one (a real 0 is a value)
            boolValue = boolValue,
            notes = "",
            building = building,
            fromPos = fromPos,
            toPos = toPos
        };

        try { OnExperimentEvent?.Invoke(info); }
        catch (Exception ex) { Debug.LogWarning("[ExperimentManager] OnExperimentEvent (" + type + ") handler threw: " + ex.Message); }
    }

    private void SetPhase(string phase, string part = "Part3")
    {
        CurrentPhase = phase;
        RaiseEvt("phase_start", part, label: phase);
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
    public double numValue;     // elapsed seconds / phase total elapsed (meaningful only when hasNum)
    public bool hasNum;         // true when numValue is a real value (including a real 0); false = no numeric value -> blank value_num
    public string interactionId; // building_click / selection outcome link ("C000123"); "" when not applicable
    public bool boolValue;      // correct / found
    public string notes;        // legacy Part 3 "notes" / endReason string

    public int groupNumber;         // Part 3 group, zero-based (0..11; Group Number == Group Index). Meaningful ONLY when hasGroup is true (0 is a real Group 0)
    public bool hasGroup;           // true for part3_log events (they carry a group); false = not applicable (serialised as -1)
    public string startZone;        // Part 3 start zone label
    public string d1Zone;           // Part 3 destination 1 zone label
    public string d2Zone;           // Part 3 destination 2 zone label
    public string startPointName;   // Part 3 start Transform name
    public GameObject d1Building;    // Part 3 assigned destination-1 building (coroutine local, captured here)
    public GameObject d2Building;    // Part 3 assigned destination-2 building
    public string d1TargetId;       // TargetSelectable.targetId of d1Building (provenance only)
    public string d2TargetId;       // TargetSelectable.targetId of d2Building (provenance only)
    public string terminationReason; // Part 3 d1_result / d2_result / d2_skipped: a Part3TerminationReason value, else ""

    public GameObject building;      // building referenced by candidate_interactable_on/off
    public Vector3 fromPos;          // teleport: player position before
    public Vector3 toPos;            // teleport: destination position
}

/// <summary>
/// Structured Part 3 search-termination reasons (the single vocabulary used by <see cref="Part3ValidTiming"/>, the
/// part3_d1_search_end / part3_d2_search_end rows and the d1_result / d2_result / d2_skipped events) and the one rule that decides
/// whether a reason is a genuine timeout. DataCaptureManager emits phase_timeout from <see cref="IsTimeout"/>, never from the free-text notes.
/// </summary>
public static class Part3TerminationReason
{
    public const string TargetFound = "target_found";                       // the correct building was selected (either destination)
    public const string D1CapReached = "d1_cap_reached";                    // D1 searched for part3Destination1MaxSeconds without success
    public const string TotalBudgetExhausted = "total_budget_exhausted";    // D1 searched for the whole part3TotalMaxSeconds without success
    public const string RemainingBudgetExhausted = "remaining_budget_exhausted"; // D2 used up max(0, total - interval 1) without success
    public const string NoBudgetRemaining = "no_budget_remaining";          // D2 never started: D1 spent the whole total budget

    /// <summary>
    /// True only for a search that actually RAN and then stopped because its time limit expired. Found targets, a D2 that never started
    /// (NoBudgetRemaining), and null / empty / unknown reasons (including "skipped") are not timeouts.
    /// </summary>
    public static bool IsTimeout(string reason)
    {
        return reason == D1CapReached || reason == TotalBudgetExhausted || reason == RemainingBudgetExhausted;
    }

    /// <summary>
    /// The exact rule DataCaptureManager applies to a Part 3 result event (part3Event = d1_result / d2_result / d2_skipped): a timeout
    /// row is due only for a timeout reason on an event that is not a skipped destination (a skipped D2 never ran, whatever reason it carries).
    /// </summary>
    public static bool ShouldEmitTimeout(string part3Event, string reason)
    {
        return part3Event != "d2_skipped" && IsTimeout(reason);
    }
}

/// <summary>
/// Part 3 valid-exposure timing: the SINGLE source of truth for both the search limits (what ends a search) and the interval
/// results (what is logged). A plain class with no Unity dependencies - every method takes a monotonic time
/// (ExperimentManager passes CaptureClock.TAbs values), so the rules can be driven by a simulated clock.
///
/// Intervals measure ACTIVE SEARCH time only, like the Part 1 / Part 2 duration timers (BeginTrial -> loop exit -> EndTrial, which
/// never include a panel's display or reading time):
///   interval 1 = D1 search end - D1 Continue confirmed
///   interval 2 = D2 search end - D2 Continue confirmed
///   total valid search duration = interval 1 + interval 2
/// A "search end" is the moment the search stopped, never the visibility of the next panel:
///   - target found: the time of the participant's selection click (captured in the click callback);
///   - timeout / budget: the time at which the search loop took its timeout branch (the frame time that failed the loop test).
///
///   part3Destination1MaxSeconds  limits the D1 search, counted from the D1 Continue click.
///   part3TotalMaxSeconds         limits interval 1 + interval 2 together:
///                                  - the D1 search can never run longer than the total budget either (limit = min(D1 max, total));
///                                  - the D2 search budget = max(0, total - interval 1), fixed the moment the D1 search ends from
///                                    that SAME recorded interval 1. D2 panel placement, reading time and any other wait before the
///                                    D2 Continue click do not reduce it, and D2 never gets a fresh full budget.
/// Panel-visibility stamps (D2 panel, End panel) are kept only as UI diagnostics; they never feed an interval or a budget.
/// Every endpoint is recorded at most once. NaN means "never reached"; no duration is ever derived from a missing endpoint.
/// </summary>
public sealed class Part3ValidTiming
{
    public readonly double D1MaxSeconds;
    public readonly double TotalMaxSeconds;

    public double D1ContinueRealtime { get; private set; } = double.NaN;
    public double D1SearchEndRealtime { get; private set; } = double.NaN;
    public string D1EndReason { get; private set; } = "";

    public double D2PanelVisibleRealtime { get; private set; } = double.NaN;   // UI diagnostic only
    public double D2ContinueRealtime { get; private set; } = double.NaN;
    public double D2SearchEndRealtime { get; private set; } = double.NaN;
    public string D2EndReason { get; private set; } = "";

    public double EndPanelVisibleRealtime { get; private set; } = double.NaN;   // UI diagnostic only

    /// <summary>Seconds the D2 search may last, counted from the D2 Continue click. NaN until the D1 search has ended.</summary>
    public double D2BudgetSeconds { get; private set; } = double.NaN;

    public Part3ValidTiming(double d1MaxSeconds, double totalMaxSeconds)
    {
        D1MaxSeconds = d1MaxSeconds;
        TotalMaxSeconds = totalMaxSeconds;
    }

    // ---- Destination 1 ---------------------------------------------------------------------------------------------------

    /// <summary>D1 Continue clicked. Starts both the interval-1 clock and the D1 search clock. First call wins.</summary>
    public bool RecordD1Continue(double now)
    {
        if (!double.IsNaN(D1ContinueRealtime)) return false;
        D1ContinueRealtime = now;
        return true;
    }

    /// <summary>The D1 search lasts at most min(D1 max, total budget) from the D1 Continue click.</summary>
    public double D1SearchLimitSeconds => Math.Min(D1MaxSeconds, TotalMaxSeconds);

    /// <summary>True while the D1 search may continue (a Continue click was recorded and its limit is not reached).</summary>
    public bool D1SearchOpen(double now) => !double.IsNaN(D1ContinueRealtime) && (now - D1ContinueRealtime) < D1SearchLimitSeconds;

    /// <summary>A selection counts for D1 only if it was made inside the search window [D1 Continue, D1 Continue + limit).</summary>
    public bool D1SelectionInWindow(double selectionRealtime) =>
        !double.IsNaN(D1ContinueRealtime) && selectionRealtime >= D1ContinueRealtime && (selectionRealtime - D1ContinueRealtime) < D1SearchLimitSeconds;

    /// <summary>Why a D1 search stops without its target: total_budget_exhausted | d1_cap_reached (found: target_found).</summary>
    public string D1ReasonFor(bool targetFound)
    {
        if (targetFound) return Part3TerminationReason.TargetFound;
        return TotalMaxSeconds <= D1MaxSeconds ? Part3TerminationReason.TotalBudgetExhausted : Part3TerminationReason.D1CapReached;
    }

    /// <summary>
    /// The D1 search ended at 'endRealtime' (selection time when found, loop timeout time otherwise). First call wins. This also
    /// fixes the D2 budget from the interval just recorded: max(0, total - interval 1).
    /// </summary>
    public bool RecordD1SearchEnd(double endRealtime, bool targetFound)
    {
        if (!double.IsNaN(D1SearchEndRealtime)) return false;
        D1SearchEndRealtime = double.IsNaN(D1ContinueRealtime) ? endRealtime : Math.Max(endRealtime, D1ContinueRealtime);
        D1EndReason = D1ReasonFor(targetFound);
        double i1 = Interval1Seconds;
        if (!double.IsNaN(i1)) D2BudgetSeconds = Math.Max(0.0, TotalMaxSeconds - i1);
        return true;
    }

    /// <summary>True when the D1 search left some of the total budget for Destination 2 (the D2 panel and search may follow).</summary>
    public bool D2MayRun => !double.IsNaN(D2BudgetSeconds) && D2BudgetSeconds > 0.0;

    // ---- Destination 2 ---------------------------------------------------------------------------------------------------

    /// <summary>The D2 panel became visible. Diagnostic stamp only: it is not an interval endpoint and changes no budget. First call wins.</summary>
    public bool RecordD2PanelVisible(double now)
    {
        if (!double.IsNaN(D2PanelVisibleRealtime)) return false;
        D2PanelVisibleRealtime = now;
        return true;
    }

    /// <summary>D2 Continue clicked: starts the interval-2 clock and the D2 search clock. First call wins.</summary>
    public bool RecordD2Continue(double now)
    {
        if (!double.IsNaN(D2ContinueRealtime)) return false;
        D2ContinueRealtime = now;
        return true;
    }

    /// <summary>True while the D2 search may continue: a Continue click and a budget exist, and the budget is not yet spent.</summary>
    public bool D2SearchOpen(double now) =>
        !double.IsNaN(D2ContinueRealtime) && !double.IsNaN(D2BudgetSeconds) && (now - D2ContinueRealtime) < D2BudgetSeconds;

    /// <summary>A selection counts for D2 only if it was made inside [D2 Continue, D2 Continue + remaining budget).</summary>
    public bool D2SelectionInWindow(double selectionRealtime) =>
        !double.IsNaN(D2ContinueRealtime) && !double.IsNaN(D2BudgetSeconds) && selectionRealtime >= D2ContinueRealtime && (selectionRealtime - D2ContinueRealtime) < D2BudgetSeconds;

    /// <summary>Why a D2 search stops without its target: no_budget_remaining | remaining_budget_exhausted (found: target_found).</summary>
    public string D2ReasonFor(bool targetFound)
    {
        if (targetFound) return Part3TerminationReason.TargetFound;
        return D2BudgetSeconds <= 0.0 ? Part3TerminationReason.NoBudgetRemaining : Part3TerminationReason.RemainingBudgetExhausted;
    }

    /// <summary>The D2 search ended at 'endRealtime' (selection time when found, loop timeout time otherwise). First call wins.</summary>
    public bool RecordD2SearchEnd(double endRealtime, bool targetFound)
    {
        if (!double.IsNaN(D2SearchEndRealtime)) return false;
        D2SearchEndRealtime = double.IsNaN(D2ContinueRealtime) ? endRealtime : Math.Max(endRealtime, D2ContinueRealtime);
        D2EndReason = D2ReasonFor(targetFound);
        return true;
    }

    // ---- End panel -------------------------------------------------------------------------------------------------------

    /// <summary>The End panel became visible. Diagnostic stamp only (drives the participant lock / NPC shutdown elsewhere). First call wins.</summary>
    public bool RecordEndPanelVisible(double now)
    {
        if (!double.IsNaN(EndPanelVisibleRealtime)) return false;
        EndPanelVisibleRealtime = now;
        return true;
    }

    // ---- Results (NaN = endpoint missing or out of order; never a fabricated number) ----------------------------------------

    public double Interval1Seconds => Span(D1ContinueRealtime, D1SearchEndRealtime);
    public double Interval2Seconds => Span(D2ContinueRealtime, D2SearchEndRealtime);

    /// <summary>interval 1 + interval 2, only when both were measured.</summary>
    public double TotalValidSeconds
    {
        get
        {
            double a = Interval1Seconds, b = Interval2Seconds;
            return double.IsNaN(a) || double.IsNaN(b) ? double.NaN : a + b;
        }
    }

    // UI-transition diagnostics (kept apart from the intervals so each can be calculated independently from the log).
    public double D1EndToD2PanelVisibleSeconds => Span(D1SearchEndRealtime, D2PanelVisibleRealtime);
    public double D2PanelReadingSeconds => Span(D2PanelVisibleRealtime, D2ContinueRealtime);
    public double D2EndToEndPanelVisibleSeconds => Span(D2SearchEndRealtime, EndPanelVisibleRealtime);

    private static double Span(double start, double end)
    {
        if (double.IsNaN(start) || double.IsNaN(end)) return double.NaN;
        double d = end - start;
        return d < 0.0 ? double.NaN : d;
    }
}
