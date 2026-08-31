using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// ExperimentManager
///
/// Part 1 / Part 2: unchanged.
///
/// Part 3 (15-minute Main Experimental Phase) - redesigned:
///  - The participant's assigned group (1..12, from ExternalSessionController.part3GroupIndex, 0-based)
///    is looked up in a fixed table that yields (StartZone, Destination1Zone, Destination2Zone),
///    where each zone is one of GE / GU / OE / OU.
///  - StartZone selects one of four FIXED start Transforms (part3StartGE/GU/OE/OU). The participant is
///    explicitly teleported there via the existing TeleportPlayerTo(...) mechanism. Phase 3 never leaves
///    the participant at a previous position.
///  - Destination1Zone / Destination2Zone each have exactly two candidate buildings
///    (part3ZoneXX.buildingA / buildingB). Exactly ONE building per destination is chosen RANDOMLY and
///    INDEPENDENTLY per session (UnityEngine.Random), then kept fixed for the whole session (stored in
///    coroutine locals; never re-rolled).
///  - VISIBILITY vs INTERACTABILITY vs TARGET ASSIGNMENT are three separate concepts:
///      * Visibility: candidate buildings are permanent, always-visible scene objects. This script NEVER
///        calls SetActive / disables a Renderer / changes a Layer on them. Both A and B stay visible at
///        all times.
///      * Interactability: at any moment ONLY the currently-assigned destination building has its
///        TargetSelectable AND InteractableHoverFeedback components enabled. Every other candidate - and
///        both candidates outside their sub-phase, and every candidate before/after Phase 3 - has those
///        two components disabled, so it behaves EXACTLY like an ordinary building: visible, the ray
///        passes over it, no target hover feedback, and clicking it calls nothing.
///      * Target assignment: the random pick decides which building becomes interactable. It does NOT
///        decide which building is visible.
///  - A Destination sub-phase ends when the participant clicks the assigned building. As a defensive
///    check the clicked targetId is also compared against the assignment, and every click is logged.
///  - Timing: Destination 1 search is capped at part3Destination1MaxSeconds (protocol: 600 s / 10 min),
///    measured from the moment its instruction panel is confirmed; if it is found earlier the phase
///    proceeds immediately. The whole phase is capped at part3TotalMaxSeconds (protocol: 900 s / 15 min),
///    measured from the moment the Start panel is confirmed. Destination 2 never runs past that ceiling.
///  - A self-contained per-session CSV (subject_Part3_Session_UTC.csv in persistentDataPath) records the
///    group, zones, chosen buildings and outcomes. It does NOT touch GazeRecorder / TrajectoryRecorder.
///
/// OnTrialStart / OnTrialEnd still fire exactly once each (after the Start panel is confirmed, and once
/// the timed phase ends), so the recorders are unaffected.
/// </summary>
public class ExperimentManager : MonoBehaviour
{
    public event Action OnTrialStart;
    public event Action OnTrialEnd;

    [Header("References")]
    public UIManager uiManager;                        // assign in inspector (explicit-slot UIManager)
    public ExternalSessionController sessionController;// session config (participant ID, groups, selected part)
    public Transform playerRig;                        // XR Rig / XR Origin that we teleport
    public SpawnerManager spawner;                     // optional - assign your spawner
    public AgentProfile currentProfile;                // your profile object

    [Header("Part durations (seconds)")]
    public float part1Duration = 60f;
    public float part2FirstDuration = 60f;
    public float part2SecondDuration = 60f;

    [Header("Part1")]
    [Tooltip("Part1 start position (single).")]
    public Transform part1StartPosition;

    [Tooltip("List of target buildings for Part1. Each selection triggers a Continue panel.")]
    public GameObject[] part1TargetBuildings;

    [Header("Part2 - group configuration")]
    public Part2Group[] part2Groups;

    // ---------------------------------------------------------------------------------------------
    // Part 3 configuration (15-minute Main Experimental Phase)
    // ---------------------------------------------------------------------------------------------

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
    [Tooltip("Fixed Transform for a participant starting in the Grid Explored zone (scene object 'StartPointGE').")]
    public Transform part3StartGE;
    [Tooltip("Fixed Transform for a participant starting in the Grid Unexplored zone (scene object 'StartPointGU').")]
    public Transform part3StartGU;
    [Tooltip("Fixed Transform for a participant starting in the Organic Explored zone (scene object 'StartPointOE').")]
    public Transform part3StartOE;
    [Tooltip("Fixed Transform for a participant starting in the Organic Unexplored zone (scene object 'StartPointOU').")]
    public Transform part3StartOU;

    [Header("Part3 - candidate destination buildings (exactly 2 per zone)")]
    public Part3ZoneTargets part3ZoneGE = new Part3ZoneTargets();
    public Part3ZoneTargets part3ZoneGU = new Part3ZoneTargets();
    public Part3ZoneTargets part3ZoneOE = new Part3ZoneTargets();
    public Part3ZoneTargets part3ZoneOU = new Part3ZoneTargets();

    [Header("Part3 - timing")]
    [Tooltip("Maximum time (seconds) to search for Destination 1, measured from when its instruction panel is confirmed. Protocol: 600 (10 minutes). If found earlier, Destination 2 starts immediately.")]
    public float part3Destination1MaxSeconds = 600f;
    [Tooltip("Maximum total Phase 3 duration (seconds), measured from when the Start panel is confirmed. Protocol: 900 (15 minutes). Destination 2 never runs past this ceiling.")]
    public float part3TotalMaxSeconds = 900f;

    [Header("Part3 - logging")]
    [Tooltip("If true, write a per-session Phase 3 assignment/outcome CSV to Application.persistentDataPath.")]
    public bool part3WriteSessionLog = true;

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
    // Taken verbatim from the study protocol - do not reorder or alter.
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
    private string part3LogPath;

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
        // until Phase 3 assigns it as a destination. This is unconditional so Part 1 / Part 2 / Practice /
        // Exploration interaction is never affected by these buildings. Visibility is NOT touched here.
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
            case ExternalSessionController.ExperimentPart.Part1:
                StartPart1();
                break;
            case ExternalSessionController.ExperimentPart.Part2:
                StartPart2();
                break;
            case ExternalSessionController.ExperimentPart.Part3:
                StartPart3();
                break;
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

    #region Part implementations (coroutines)

    private IEnumerator RunPart1()
    {
        currentRunningPart = RunningPart.Part1;

        ConfigureTargetsForPart1();
        TeleportPlayerTo(part1StartPosition);

        bool started = false;
        uiManager.ShowPart1StartPanel(() => started = true);
        yield return new WaitUntil(() => started);

        if (spawner != null) spawner.defaultProfile = currentProfile;
        OnTrialStart?.Invoke();

        targetWasSelected = false;
        float t = 0f;
        while (t < part1Duration)
        {
            t += Time.deltaTime;
            yield return null;
        }

        OnTrialEnd?.Invoke();
        bool confirmed = false;
        uiManager.ShowPart1EndPanel(() => confirmed = true);
        yield return new WaitUntil(() => confirmed);

        currentRunningPart = RunningPart.None;
    }

    private IEnumerator RunPart2()
    {
        currentRunningPart = RunningPart.Part2;

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

        bool started = false;
        uiManager.ShowPart2StartPanel(() => started = true);
        yield return new WaitUntil(() => started);

        if (spawner != null) spawner.defaultProfile = currentProfile;
        OnTrialStart?.Invoke();

        targetWasSelected = false;
        float t = 0f;
        while (t < part2FirstDuration)
        {
            t += Time.deltaTime;
            yield return null;
        }

        bool teleported = false;
        uiManager.ShowPart2ContinuePanel(() =>
        {
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
        bool confirmed = false;
        uiManager.ShowPart2EndPanel(() => confirmed = true);
        yield return new WaitUntil(() => confirmed);

        currentRunningPart = RunningPart.None;
    }

    private IEnumerator RunPart3()
    {
        currentRunningPart = RunningPart.Part3;

        // Clean slate: no candidate is interactable (covers a re-run after an interrupted previous Phase 3).
        // Visibility is never touched.
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

        // --- 3. Independently pick ONE building from each destination zone. Fixed for the whole session.
        //        NOTE: candidate buildings are NEVER activated/deactivated/re-layered by this script - they
        //        are permanent, always-visible scene objects. Selection only assigns which one is the
        //        interactable target for its sub-phase. ---
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

        // --- 3b. Generate the target-building preview images for BOTH destinations. UIManager ->
        //         TargetBuildingPreviewGenerator renders each of these exact GameObjects (no second
        //         randomization) to a static Texture2D for the Destination full + shrunk panels.
        //         Synchronous; restores all state; independent of interactability; does not affect the
        //         VR view and does not make any building disappear. ---
        if (uiManager != null)
        {
            uiManager.PrepareDestinationImages(destination1Building, destination2Building);
        }

        // --- 4. Hide the Part 1 test targets only (they must not be clickable during Phase 3).
        //        Phase 3 candidate buildings are left exactly as authored - always visible. ---
        if (part1TargetBuildings != null)
        {
            foreach (var go in part1TargetBuildings)
                if (go != null) go.SetActive(false);
        }

        // --- 5. Move the participant to the fixed start point (existing mechanism). ---
        TeleportPlayerTo(startPoint);

        // --- 6. Persist the assignment immediately (crash-safe). ---
        if (part3WriteSessionLog)
        {
            part3LogPath = BuildPart3LogPath();
            AppendPart3Log("assigned", groupNumber, def, startPoint, destination1Building, destination2Building,
                selectedId: "", found: false, elapsedSeconds: 0.0, notes: "");
        }

        // --- 7. Phase 3 Start panel. (All candidates are still non-interactable ordinary buildings.) ---
        bool started = false;
        uiManager.ShowPart3StartPanel(() => started = true);
        yield return new WaitUntil(() => started);

        if (spawner != null) spawner.defaultProfile = currentProfile;
        OnTrialStart?.Invoke();

        double phaseStartRealtime = Time.realtimeSinceStartupAsDouble;

        // ============================ DESTINATION 1 ============================
        bool dest1Confirmed = false;
        uiManager.ShowPart3Destination1Panel(() => dest1Confirmed = true);
        yield return new WaitUntil(() => dest1Confirmed);

        // Destination 1 assigned: make ONLY destination1Building interactable (TargetSelectable + hover).
        // Everything else stays a plain visible building.
        SetAllPart3CandidatesInteractable(false);
        SetCandidateInteractable(destination1Building, true);

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

                if (part3WriteSessionLog)
                {
                    AppendPart3Log("d1_click", groupNumber, def, startPoint, destination1Building, destination2Building,
                        selectedId: clickedId, found: correct,
                        elapsedSeconds: Time.realtimeSinceStartupAsDouble - d1StartRealtime,
                        notes: correct ? "correct" : "wrong_building");
                }

                if (correct) d1Found = true;

                // Consume the click. A wrong-building click does NOT end the sub-phase and shows no feedback.
                // (With only destination1Building interactable, this defensive branch should never be hit.)
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

        if (part3WriteSessionLog)
        {
            AppendPart3Log("d1_result", groupNumber, def, startPoint, destination1Building, destination2Building,
                selectedId: d1SelectedId, found: d1Found, elapsedSeconds: d1Elapsed,
                notes: d1Found ? "found" : (totalTimeUpAfterD1 ? "total_time_reached" : "d1_timeout_10min"));
        }

        // ============================ DESTINATION 2 ============================
        bool d2Ran = false;
        bool d2Found = false;
        string d2SelectedId = "";
        double d2Elapsed = 0.0;

        if ((Time.realtimeSinceStartupAsDouble - phaseStartRealtime) < part3TotalMaxSeconds)
        {
            d2Ran = true;

            bool dest2Confirmed = false;
            uiManager.ShowPart3Destination2Panel(() => dest2Confirmed = true);
            yield return new WaitUntil(() => dest2Confirmed);

            // Destination 2 assigned: make ONLY destination2Building interactable.
            SetAllPart3CandidatesInteractable(false);
            SetCandidateInteractable(destination2Building, true);

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

                    if (part3WriteSessionLog)
                    {
                        AppendPart3Log("d2_click", groupNumber, def, startPoint, destination1Building, destination2Building,
                            selectedId: clickedId, found: correct,
                            elapsedSeconds: Time.realtimeSinceStartupAsDouble - d2StartRealtime,
                            notes: correct ? "correct" : "wrong_building");
                    }

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

        if (part3WriteSessionLog)
        {
            AppendPart3Log(d2Ran ? "d2_result" : "d2_skipped", groupNumber, def, startPoint, destination1Building, destination2Building,
                selectedId: d2SelectedId, found: d2Found, elapsedSeconds: d2Elapsed, notes: endReason);
            AppendPart3Log("phase_end", groupNumber, def, startPoint, destination1Building, destination2Building,
                selectedId: "", found: (d1Found && d2Found), elapsedSeconds: phaseTotalElapsed, notes: endReason);
        }

        OnTrialEnd?.Invoke();

        // Phase 3 is over: no Phase 3 target-specific interaction remains anywhere. Every candidate is a
        // plain visible building again.
        SetAllPart3CandidatesInteractable(false);

        bool confirmed = false;
        uiManager.ShowPart3EndPanel(() => confirmed = true);
        yield return new WaitUntil(() => confirmed);

        currentRunningPart = RunningPart.None;
    }
    #endregion

    #region Utility / target configuration helpers

    private void ConfigureTargetsForPart1()
    {
        // Phase 3 candidate buildings are permanent, always-visible scene objects and are NOT touched here.
        // Enable all Part 1 target buildings.
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
    /// children), or null if there is no TargetSelectable. Used only as a defensive check that the clicked
    /// building is the assigned one - this method never modifies TargetSelectable or any building state.
    /// </summary>
    private string GetAssignedTargetId(GameObject building)
    {
        if (building == null) return null;

        TargetSelectable ts = building.GetComponent<TargetSelectable>();
        if (ts == null) ts = building.GetComponentInChildren<TargetSelectable>(true);

        return (ts != null) ? ts.targetId : null;
    }

    /// <summary>
    /// Enable or disable the two interaction components on ONE candidate building. Visibility is NEVER
    /// touched: the GameObject stays active, the Renderer stays enabled, the Collider stays enabled, and
    /// the Layer is unchanged.
    ///
    /// interactable == true  : TargetSelectable.enabled = true  (EventSystem delivers OnPointerClick ->
    ///                         Select() -> RegisterTargetSelection), InteractableHoverFeedback.enabled =
    ///                         true (subtle hover brightness on the controller ray).
    /// interactable == false : both components disabled. The EventSystem skips any component whose
    ///                         isActiveAndEnabled is false, so the building fires no target-selection
    ///                         event and shows no target hover feedback - it behaves exactly like an
    ///                         ordinary building. InteractableHoverFeedback.OnDisable restores its
    ///                         original appearance if it was mid-hover.
    ///
    /// TargetSelectable has no OnEnable/OnDisable and no event registration; its Start() (which resolves
    /// experimentManager) runs on first enable and is never re-run, so toggling 'enabled' is safe.
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

    // small helpers to avoid compile error in this file (we rely on uiManager's public fields)
    private bool uiManagerHasShrunkTemplate()
    {
        return uiManager != null && uiManager.part3Dest1ShrunkTemplate != null;
    }

    private bool uiManagerHasShrunkTemplatePart2()
    {
        return uiManager != null && uiManager.part3Dest2ShrunkTemplate != null;
    }

    // ---- Part 3 per-session log --------------------------------------------------------------

    private string BuildPart3LogPath()
    {
        string subject = (sessionController != null && !string.IsNullOrEmpty(sessionController.participantId))
            ? sessionController.participantId
            : "UNKNOWN_SUBJECT";

        foreach (char c in Path.GetInvalidFileNameChars())
            subject = subject.Replace(c, '_');

        string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        return Path.Combine(Application.persistentDataPath, $"{subject}_Part3_Session_{timestamp}.csv");
    }

    private void AppendPart3Log(string eventName, int groupNumber, Part3GroupDef def,
        Transform startPoint, GameObject d1Building, GameObject d2Building,
        string selectedId, bool found, double elapsedSeconds, string notes)
    {
        if (string.IsNullOrEmpty(part3LogPath)) return;

        try
        {
            bool writeHeader = !File.Exists(part3LogPath);

            // Open / write / close per row for crash safety, mirroring the recorders.
            using (var writer = new StreamWriter(part3LogPath, append: true))
            {
                if (writeHeader)
                {
                    writer.WriteLine(string.Join(",", new string[]
                    {
                        "event", "utc_time", "time_since_startup_s", "subject_id", "trial_id",
                        "part3_group_number", "start_zone", "start_point",
                        "d1_zone", "d1_building", "d2_zone", "d2_building",
                        "selected_id", "found", "elapsed_s", "notes"
                    }));
                }

                string subject = (sessionController != null) ? sessionController.participantId : "UNKNOWN_SUBJECT";

                string[] fields =
                {
                    Csv(eventName),
                    Csv(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                    Time.realtimeSinceStartupAsDouble.ToString("F3", CultureInfo.InvariantCulture),
                    Csv(subject),
                    "Part3",
                    groupNumber.ToString(CultureInfo.InvariantCulture),
                    Csv(def.StartZone.ToString()),
                    Csv(startPoint != null ? startPoint.name : ""),
                    Csv(def.Destination1Zone.ToString()),
                    Csv(d1Building != null ? d1Building.name : ""),
                    Csv(def.Destination2Zone.ToString()),
                    Csv(d2Building != null ? d2Building.name : ""),
                    Csv(selectedId),
                    found ? "1" : "0",
                    elapsedSeconds.ToString("F3", CultureInfo.InvariantCulture),
                    Csv(notes)
                };
                writer.WriteLine(string.Join(",", fields));
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ExperimentManager] Failed to write Part3 session log: " + ex.Message);
        }
    }

    /// <summary>Lightweight CSV field sanitiser: strip separators/newlines (same idea as GazeRecorder.SafeCsvField).</summary>
    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }

    #endregion
}