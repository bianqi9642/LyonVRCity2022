using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// ExperimentManager (updated)
/// - Part3 destination flow:
///   * Show Destination1 full -> when Confirmed, create shrunk panel and wait for selection or timeout;
///   * When selection or timeout occurs, destroy shrunk panel and show Destination2 full -> same flow;
///   * Finally, show End panel.
/// - Uses UIManager.CreateAndAnchorShrunkPanel to get a reference to the shrunk instance and destroy it when done.
/// - Full panels are still destroyed by PanelController.Setup when their confirm buttons are clicked.
/// </summary>
public class ExperimentManager : MonoBehaviour
{
    public event Action OnTrialStart;
    public event Action OnTrialEnd;

    [Header("References")]
    public UIManager uiManager;                        // assign in inspector (explicit-slot UIManager)
    public ExternalSessionController sessionController;// session config (participant ID, groups, selected part)
    public Transform playerRig;                        // XR Rig or CameraRoot that we teleport
    public SpawnerManager spawner;                     // optional - assign your spawner
    public AgentProfile currentProfile;                // your profile object

    [Header("Part durations (seconds)")]
    public float part1Duration = 60f;
    public float part2FirstDuration = 60f;
    public float part2SecondDuration = 60f;
    public float part3FirstDuration = 60f;
    public float part3SecondDuration = 60f;

    [Header("Part1")]
    [Tooltip("Part1 start position (single).")]
    public Transform part1StartPosition;

    [Tooltip("List of target buildings for Part1. Each selection triggers a Continue panel.")]
    public GameObject[] part1TargetBuildings;

    [Header("Part2 - group configuration")]
    public Part2Group[] part2Groups;

    [Header("Part3 - group configuration")]
    public Part3Group[] part3Groups;

    [Serializable]
    public class Part2Group
    {
        public Transform[] startPositions;
        public Transform[] teleportPositions;
    }

    [Serializable]
    public class Part3Group
    {
        public Transform[] startPositions;
        public GameObject[] targetBuildings1;
        public GameObject[] targetBuildings2;
    }

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

        int gIdx = (sessionController != null) ? sessionController.part3GroupIndex : 0;
        if (part3Groups == null || part3Groups.Length == 0)
        {
            Debug.LogError("ExperimentManager.RunPart3: No Part3 groups configured in inspector.");
            yield break;
        }
        gIdx = Mathf.Clamp(gIdx, 0, part3Groups.Length - 1);
        Part3Group group = part3Groups[gIdx];

        // choose random start and targets from group's lists
        Transform chosenStart = ChooseRandomFromArray(group.startPositions);
        GameObject chosenTarget1 = ChooseRandomFromArray(group.targetBuildings1);
        GameObject chosenTarget2 = ChooseRandomFromArray(group.targetBuildings2);

        // enable the group's targets
        ConfigureTargetsForPart3Group(gIdx);

        // teleport to chosen start
        TeleportPlayerTo(chosenStart);

        // show Part3 start panel
        bool started = false;
        uiManager.ShowPart3StartPanel(() => started = true);
        yield return new WaitUntil(() => started);

        if (spawner != null) spawner.defaultProfile = currentProfile;
        OnTrialStart?.Invoke();

        // DESTINATION 1: show full panel and wait for its confirm
        bool dest1Confirmed = false;
        PanelController dest1FullPC = uiManager.ShowPart3Destination1Panel(() => dest1Confirmed = true);
        yield return new WaitUntil(() => dest1Confirmed);

        // At this point the full panel has been confirmed (and PanelController destroyed by itself).
        // Create and anchor the shrunk panel; keep a reference so we can destroy it later.
        GameObject dest1ShrunkInstance = null;
        if (uiManager != null && uiManagerHasShrunkTemplate())
        {
            dest1ShrunkInstance = uiManager.CreateAndAnchorShrunkPanel(uiManager.part3Dest1ShrunkTemplate, UIManager.Corner.TopRight);
        }

        // Wait for selection or timeout for first destination
        targetWasSelected = false;
        float t = 0f;
        while (t < part3FirstDuration && !targetWasSelected)
        {
            t += Time.deltaTime;
            yield return null;
        }

        // Destroy shrunk panel when dest1 stage ends
        if (dest1ShrunkInstance != null)
        {
            Destroy(dest1ShrunkInstance);
            dest1ShrunkInstance = null;
        }

        // DESTINATION 2: show full panel and wait for confirm
        bool dest2Confirmed = false;
        PanelController dest2FullPC = uiManager.ShowPart3Destination2Panel(() => dest2Confirmed = true);
        yield return new WaitUntil(() => dest2Confirmed);

        // Create and anchor the shrunk panel for dest2
        GameObject dest2ShrunkInstance = null;
        if (uiManager != null && uiManagerHasShrunkTemplatePart2())
        {
            dest2ShrunkInstance = uiManager.CreateAndAnchorShrunkPanel(uiManager.part3Dest2ShrunkTemplate, UIManager.Corner.TopRight);
        }

        // Wait for selection or timeout for second destination
        targetWasSelected = false;
        t = 0f;
        while (t < part3SecondDuration && !targetWasSelected)
        {
            t += Time.deltaTime;
            yield return null;
        }

        // Destroy dest2 shrunk panel when finished
        if (dest2ShrunkInstance != null)
        {
            Destroy(dest2ShrunkInstance);
            dest2ShrunkInstance = null;
        }

        // finally show Part3 End Panel
        OnTrialEnd?.Invoke();
        bool confirmed = false;
        uiManager.ShowPart3EndPanel(() => confirmed = true);
        yield return new WaitUntil(() => confirmed);

        currentRunningPart = RunningPart.None;
    }

    #endregion

    #region Utility / target configuration helpers

    private void ConfigureTargetsForPart1()
    {
        // deactivate all part3 group targets first
        if (part3Groups != null)
        {
            foreach (var g in part3Groups)
            {
                if (g.targetBuildings1 != null)
                {
                    foreach (var go in g.targetBuildings1)
                    {
                        if (go != null) go.SetActive(false);
                    }
                }
                if (g.targetBuildings2 != null)
                {
                    foreach (var go in g.targetBuildings2)
                    {
                        if (go != null) go.SetActive(false);
                    }
                }
            }
        }

        // enable all part1 target buildings
        if (part1TargetBuildings != null)
        {
            foreach (var go in part1TargetBuildings)
            {
                if (go != null) go.SetActive(true);
            }
        }
    }

    private void ConfigureTargetsForPart3Group(int groupIndexToEnable)
    {
        // deactivate part1 targets
        if (part1TargetBuildings != null)
        {
            foreach (var go in part1TargetBuildings)
            {
                if (go != null) go.SetActive(false);
            }
        }

        if (part3Groups == null) return;

        for (int i = 0; i < part3Groups.Length; i++)
        {
            var g = part3Groups[i];

            if (g.targetBuildings1 != null)
            {
                foreach (var go in g.targetBuildings1)
                {
                    if (go == null) continue;
                    go.SetActive(i == groupIndexToEnable);
                }
            }

            if (g.targetBuildings2 != null)
            {
                foreach (var go in g.targetBuildings2)
                {
                    if (go == null) continue;
                    go.SetActive(i == groupIndexToEnable);
                }
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

    private GameObject ChooseRandomFromArray(GameObject[] arr)
    {
        if (arr == null || arr.Length == 0) return null;
        int idx = UnityEngine.Random.Range(0, arr.Length);
        return arr[idx];
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

    #endregion
}
