// SpawnerManager.cs
// Extended SpawnerManager that supports:
// - Sampling attractors as areas/positions (if attractor transforms provide AttractorArea or via NavMesh sampling).
// - Applying AgentProfile via optional AgentFactory when creating or reusing agents.
// - Passing position-based itineraries to PedestrianAgent.InitializePositions(...).
// - Minimal, non-invasive changes so existing behaviors remain unchanged if new features are not used.
//
// This version fixes compile errors caused by previous MoveToTarget refactor that removed the
// public 'useNavMeshAgent' field. All references to mt.useNavMeshAgent have been removed and
// logging/decisions now rely on the actual NavMeshAgent state (nav != null && nav.enabled && nav.isOnNavMesh).
//
// NEW (trial-state gating): SpawnLoop() previously ran continuously for the lifetime of the scene,
// independent of ExperimentManager.OnTrialStart/OnTrialEnd. This meant pedestrian presence before a
// trial officially started (or after it ended) was not delineated from "in-trial" pedestrian presence,
// which could confound the recorded trial window with a continuous, ungated pedestrian stimulus.
// SpawnerManager now optionally subscribes to ExperimentManager.OnTrialStart/OnTrialEnd and:
//   - only spawns NEW agents while a trial is active (gates SpawnLoop/SpawnAgent), and
//   - optionally pauses/resumes movement of agents that are already alive when a trial ends/starts,
//     and/or force-finishes them, so that "trial recording window" and "pedestrian stimulus window"
//     can be made to coincide exactly.
// This is opt-in via 'gateSpawningByTrialState' (default true) so it fixes the confound by default,
// but can be switched off to restore the previous continuous-spawning behaviour if that is intentional
// (e.g. for ambient background population that is not meant to be the manipulated stimulus).
//
// NEW (Quest logging fix): SpawnerManager previously logged unconditionally and verbosely on every
// spawn/despawn (LogAgentItinerary/LogAgentItineraryPositions build a multi-line StringBuilder per spawn),
// with no build-time or runtime gate - unlike MoveToTarget, which already has an 'enableDebugLogs' toggle.
// On Quest, this string-heavy per-spawn logging is a known performance drain that scales with
// maxAgents/spawnInterval. SpawnerManager now has its own 'enableDebugLogs' flag (mirroring MoveToTarget's),
// defaulting to true so existing Editor workflows are unaffected, and every Debug.Log/LogWarning/LogError
// call in this script is gated behind it so it can be switched off for Quest builds.
//
// All comments in English.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

[System.Serializable]
public class AttractorInfo
{
    public Transform transform;
    [Tooltip("Max number of agents that can be 'inside' the attraction (for close proximity). If <=0, unlimited.")]
    public int capacity = 0;
    // runtime tracking
    [HideInInspector] public List<PedestrianAgent> occupants = new List<PedestrianAgent>();
}

public class SpawnerManager : MonoBehaviour
{
    [Header("References")]
    // NEW: spawn start point (drag a transform in inspector). If provided, agents will spawn at/near this transform.
    // The itinerary's first entry (itinerary[0]) will be chosen randomly from `entrances` regardless of this startTransform.
    public Transform startTransform;

    public Transform[] entrances;
    public Transform[] exits;
    public AttractorInfo[] attractors;

    [Header("Prefab & Spawn")]
    public GameObject agentPrefab;
    public AgentFactory agentFactory;           // optional factory to create/apply profile
    public AgentProfile defaultProfile;         // optional default profile applied at spawn
    public float spawnInterval = 0.5f; // seconds between spawns
    public int maxAgents = 200;

    [Header("Itinerary / Behavior")]
    public int minAttractorsPerAgent = 1;
    public int maxAttractorsPerAgent = 4;
    [Tooltip("Probability to stop at any given attractor (0..1) when itineraries are probabilistic")]
    public float stopProbability = 0.7f;
    public Vector2 dwellTimeSec = new Vector2(5f, 15f); // min/max dwell

    [Header("Attractor sampling")]
    [Tooltip("If true, when an attractor has an area (AttractorArea component) we'll sample a point inside it and pass position-based itinerary to agents. " +
             "If false, itinerary uses Transform references only.")]
    public bool sampleAttractorsAsPositions = true;

    [Header("Spawn Sampling")]
    public float spawnSampleRadius = 1.0f; // sample radius around entrance on the horizontal plane
    public int spawnSampleMaxAttempts = 8;

    // New fields to control ground projection behaviour:
    [Tooltip("Layer(s) considered ground for projection raycasts. Default = Everything.")]
    public LayerMask groundLayers = ~0;
    [Tooltip("Maximum distance used when raycasting down to find ground from a sampling height.")]
    public float raycastDownDistance = 20f;
    [Tooltip("Small upward offset applied after a ground hit to avoid clipping the ground.")]
    public float groundOffset = 0.1f;
    [Tooltip("Vertical offsets (relative to entrance Y) to try when NavMesh.SamplePosition fails to find a NavMesh from the projected ground point.")]
    public float[] verticalOffsets = new float[] { 2.0f, 1.0f, 0.5f, 0.0f, -1.0f };

    [Header("Pooling")]
    public bool usePooling = true;
    public int poolPrewarm = 50;

    // time to wait for NavMesh availability before giving up (seconds)
    public float navmeshWaitTimeout = 5f;

    [Header("Trial State Gating (NEW)")]
    [Tooltip("Optional: assign the scene's ExperimentManager. If left empty, it will be auto-found via " +
             "FindObjectOfType at Start() (same pattern used elsewhere in this project, e.g. TargetSelectable).")]
    public ExperimentManager experimentManager;

    [Tooltip("If true, SpawnLoop() only spawns new agents while a trial is active (between " +
             "ExperimentManager.OnTrialStart and OnTrialEnd). If false, spawning is continuous for the " +
             "lifetime of the scene, matching the previous (ungated) behaviour.")]
    public bool gateSpawningByTrialState = true;

    [Tooltip("If true, agents that are already alive when a trial ends are paused (frozen in place, " +
             "animator frozen) via MoveToTarget.PauseExternal(...), and resumed via ResumeExternal() " +
             "when the next trial starts. If false, already-spawned agents keep moving/finishing their " +
             "current itinerary even outside the trial window.")]
    public bool pauseLiveAgentsOutsideTrial = true;

    [Tooltip("If true, in addition to pausing, agents that are still alive when a trial ends are " +
             "force-finished (PedestrianAgent.ForceFinish()) and returned to the pool / destroyed, so " +
             "the scene is fully clear of pedestrians outside the trial window. If false, paused agents " +
             "simply wait, frozen, until the next trial resumes them.")]
    public bool despawnLiveAgentsOnTrialEnd = false;

    [Header("Debug")]
    [Tooltip("NEW: mirrors MoveToTarget.enableDebugLogs. When true (default), SpawnerManager logs " +
             "per-spawn/per-despawn itinerary details and warnings/errors via Debug.Log(...). Disable this " +
             "for Quest/device builds to avoid the string-building + logging cost scaling with " +
             "maxAgents/spawnInterval - it is purely a diagnostic aid and has no effect on spawn behaviour.")]
    public bool enableDebugLogs = true;

    // internal
    private List<GameObject> pool = new List<GameObject>();
    private int liveAgents = 0;

    // NEW: tracks currently-alive (spawned, not yet finished) agent GameObjects so they can be
    // paused/resumed/force-finished as a group when trial state changes.
    private List<GameObject> activeAgents = new List<GameObject>();

    // ---------------------------------------------------------------------------------------------
    // Data-capture (Revision 2) - OBSERVER hooks. These do NOT change spawning, pooling, itinerary,
    // movement, timing, or randomisation. They only expose the currently-alive agent list and fire
    // spawn/despawn notifications AFTER the internal list has already been mutated.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Read-only view of the currently-alive (spawned, not finished) agent GameObjects.</summary>
    public IReadOnlyList<GameObject> ActiveAgents => activeAgents;

    /// <summary>Raised right after an agent has been added to the active list (spawn / pooled reuse).</summary>
    public event System.Action<PedestrianAgent> AgentSpawned;

    /// <summary>Raised right after an agent has been removed from the active list (finished / force-finished).</summary>
    public event System.Action<PedestrianAgent> AgentDespawned;

    private void RaiseAgentSpawned(PedestrianAgent pa)
    {
        try { AgentSpawned?.Invoke(pa); }
        catch (System.Exception e) { if (enableDebugLogs) Debug.LogWarning("[SpawnerManager] AgentSpawned observer threw: " + e.Message); }
    }

    private void RaiseAgentDespawned(PedestrianAgent pa)
    {
        try { AgentDespawned?.Invoke(pa); }
        catch (System.Exception e) { if (enableDebugLogs) Debug.LogWarning("[SpawnerManager] AgentDespawned observer threw: " + e.Message); }
    }

    // NEW: whether a trial is currently active. When gateSpawningByTrialState == false this flag
    // is ignored entirely (SpawnLoop behaves exactly as it did before this change).
    private bool trialActive = false;

    // state to ensure we don't spawn before prewarm/initialization finished
    bool poolInitializationComplete = false;

    void Start()
    {
        // NEW: resolve ExperimentManager and subscribe to trial start/end events if gating is enabled.
        if (gateSpawningByTrialState)
        {
            if (experimentManager == null)
            {
                experimentManager = FindObjectOfType<ExperimentManager>();
            }

            if (experimentManager != null)
            {
                experimentManager.OnTrialStart += HandleTrialStart;
                experimentManager.OnTrialEnd += HandleTrialEnd;
            }
            else
            {
                // No ExperimentManager found: fail safe by disabling gating rather than blocking all
                // spawning forever (which would silently break scenes that don't use ExperimentManager).
                if (enableDebugLogs) Debug.LogWarning("[SpawnerManager] gateSpawningByTrialState is true but no ExperimentManager " +
                    "was found in the scene. Falling back to continuous spawning (gating disabled).");
                gateSpawningByTrialState = false;
            }
        }

        // Start sequence that waits/attempts NavMesh prewarm and only then starts spawn loop.
        StartCoroutine(InitializePoolThenSpawn());
    }

    void OnDestroy()
    {
        // NEW: unsubscribe to avoid referencing a destroyed SpawnerManager from ExperimentManager's event.
        if (experimentManager != null)
        {
            experimentManager.OnTrialStart -= HandleTrialStart;
            experimentManager.OnTrialEnd -= HandleTrialEnd;
        }
    }

    /// <summary>
    /// NEW: called when ExperimentManager fires OnTrialStart. Opens the spawning gate and, if configured,
    /// resumes any agents that were paused when the previous trial ended.
    /// </summary>
    private void HandleTrialStart()
    {
        trialActive = true;
        if (enableDebugLogs) Debug.Log("[SpawnerManager] Trial started -> NPC spawning enabled.");

        if (pauseLiveAgentsOutsideTrial)
        {
            ResumeAllActiveAgents();
        }
    }

    /// <summary>
    /// NEW: called when ExperimentManager fires OnTrialEnd. Closes the spawning gate so no new agents
    /// are created outside the trial window, and optionally pauses and/or clears agents that are still
    /// alive from the trial that just ended.
    /// </summary>
    private void HandleTrialEnd()
    {
        trialActive = false;
        if (enableDebugLogs) Debug.Log("[SpawnerManager] Trial ended -> NPC spawning disabled.");

        if (despawnLiveAgentsOnTrialEnd)
        {
            ForceFinishAllActiveAgents();
        }
        else if (pauseLiveAgentsOutsideTrial)
        {
            PauseAllActiveAgents();
        }
    }

    /// <summary>
    /// NEW: freezes movement (and animator) of every currently-alive agent via MoveToTarget.PauseExternal,
    /// without changing their itinerary state, so they can be resumed exactly where they left off.
    /// </summary>
    private void PauseAllActiveAgents()
    {
        for (int i = 0; i < activeAgents.Count; i++)
        {
            GameObject go = activeAgents[i];
            if (go == null) continue;
            MoveToTarget mt = go.GetComponent<MoveToTarget>();
            if (mt != null)
            {
                mt.PauseExternal(true, true); // haltMovement=true, freezeAnimator=true
            }
        }
    }

    /// <summary>
    /// NEW: resumes movement of every currently-alive agent previously paused by PauseAllActiveAgents.
    /// </summary>
    private void ResumeAllActiveAgents()
    {
        for (int i = 0; i < activeAgents.Count; i++)
        {
            GameObject go = activeAgents[i];
            if (go == null) continue;
            MoveToTarget mt = go.GetComponent<MoveToTarget>();
            if (mt != null)
            {
                mt.ResumeExternal();
            }
        }
    }

    /// <summary>
    /// NEW: immediately terminates every currently-alive agent's itinerary (PedestrianAgent.ForceFinish()),
    /// which triggers the normal onAgentFinished -> OnAgentFinished(...) pooling/destroy pathway. Iterates
    /// over a copy of the list because OnAgentFinished mutates activeAgents while we iterate.
    /// </summary>
    private void ForceFinishAllActiveAgents()
    {
        List<GameObject> snapshot = new List<GameObject>(activeAgents);
        for (int i = 0; i < snapshot.Count; i++)
        {
            GameObject go = snapshot[i];
            if (go == null) continue;
            PedestrianAgent pa = go.GetComponent<PedestrianAgent>();
            if (pa != null)
            {
                pa.ForceFinish();
            }
        }
    }

    IEnumerator InitializePoolThenSpawn()
    {
        // Wait for NavMesh readiness and prewarm pool if configured
        if (usePooling)
        {
            yield return StartCoroutine(WaitForNavMeshThenPrewarm());
        }
        else
        {
            // Non-pooling: we still want to briefly wait for NavMesh availability (bounded by timeout),
            // so our first spawns have a chance of sampling NavMesh. This is non-blocking if NavMesh is ready.
            yield return StartCoroutine(WaitForNavMeshAvailability());
        }

        poolInitializationComplete = true;
        // start main spawn loop after prewarm completes
        StartCoroutine(SpawnLoop());
    }

    IEnumerator WaitForNavMeshAvailability()
    {
        float start = Time.time;
        bool navAvailable = false;

        Vector3 sampleOrigin = Vector3.zero;
        // Prefer user-provided startTransform as sampling origin if present, else fallback to first entrance.
        if (startTransform != null)
            sampleOrigin = startTransform.position;
        else if (entrances != null && entrances.Length > 0 && entrances[0] != null)
            sampleOrigin = entrances[0].position;

        while (Time.time - start < navmeshWaitTimeout)
        {
            Vector3 s = SampleNavMeshNear(sampleOrigin, spawnSampleRadius, spawnSampleMaxAttempts);
            if (s != Vector3.zero)
            {
                navAvailable = true;
                break;
            }
            if (entrances != null)
            {
                foreach (var e in entrances)
                {
                    if (e == null) continue;
                    Vector3 ss = SampleNavMeshNear(e.position, spawnSampleRadius, 2);
                    if (ss != Vector3.zero)
                    {
                        navAvailable = true;
                        break;
                    }
                }
            }
            if (navAvailable) break;
            yield return null;
        }

        if (!navAvailable)
            if (enableDebugLogs) Debug.LogWarning($"SpawnerManager: NavMesh not found near entrances/startTransform within {navmeshWaitTimeout}s. Spawning will proceed but agents may be fallback to transform mode.");
        yield break;
    }

    IEnumerator WaitForNavMeshThenPrewarm()
    {
        float start = Time.time;
        bool navAvailable = false;

        // If startTransform exists prefer it as the sampling origin, otherwise use first entrance or world origin
        Vector3 sampleOrigin = Vector3.zero;
        if (startTransform != null)
            sampleOrigin = startTransform.position;
        else if (entrances != null && entrances.Length > 0 && entrances[0] != null)
            sampleOrigin = entrances[0].position;

        while (Time.time - start < navmeshWaitTimeout)
        {
            Vector3 s = SampleNavMeshNear(sampleOrigin, spawnSampleRadius, spawnSampleMaxAttempts);
            if (s != Vector3.zero)
            {
                navAvailable = true;
                break;
            }
            // try each entrance quickly if available
            if (entrances != null)
            {
                foreach (var e in entrances)
                {
                    if (e == null) continue;
                    Vector3 ss = SampleNavMeshNear(e.position, spawnSampleRadius, 2);
                    if (ss != Vector3.zero)
                    {
                        navAvailable = true;
                        break;
                    }
                }
            }
            if (navAvailable) break;

            // wait a frame before retrying
            yield return null;
        }

        if (!navAvailable)
            if (enableDebugLogs) Debug.LogWarning($"SpawnerManager: NavMesh not found near sample origin within {navmeshWaitTimeout}s. Prewarm will still run but any prewarmed agents that cannot find NavMesh will have their NavMeshAgent disabled as a fallback.");

        // now prewarm (coroutine)
        yield return StartCoroutine(PrewarmPoolCoroutine());
    }

    IEnumerator PrewarmPoolCoroutine()
    {
        // choose a sampling origin: prefer the user-provided startTransform, then the first entrance, otherwise world origin
        Vector3 sampleOrigin = Vector3.zero;
        if (startTransform != null)
            sampleOrigin = startTransform.position;
        else if (entrances != null && entrances.Length > 0 && entrances[0] != null)
            sampleOrigin = entrances[0].position;

        float sampleRadius = Mathf.Max(0.1f, spawnSampleRadius);

        for (int i = 0; i < poolPrewarm; i++)
        {
            // instantiate the prefab (Awake will run here)
            GameObject go = Instantiate(agentPrefab, sampleOrigin + Random.insideUnitSphere * 0.5f, Quaternion.identity);

            // attempt to place on NavMesh near sampleOrigin using improved sampling
            Vector3 navPos = SampleNavMeshNear(sampleOrigin, sampleRadius, spawnSampleMaxAttempts);
            NavMeshAgent nav = go.GetComponent<NavMeshAgent>();

            bool placedOnNav = false;
            if (navPos != Vector3.zero)
            {
                // place instance on navmesh
                go.transform.position = navPos;
                if (nav != null)
                {
                    // ensure agent enabled and warp to navpos
                    if (!nav.enabled) nav.enabled = true;
                    bool warped = nav.Warp(navPos);
                    // ResetPath only when agent is active & on NavMesh
                    if (warped && nav.isOnNavMesh)
                    {
                        nav.ResetPath();
                        placedOnNav = true;
                    }
                }
                else
                {
                    placedOnNav = false;
                }
            }
            else
            {
                // Could not find NavMesh: disable the NavMeshAgent component to avoid exceptions in Awake/OnDisable/OnDestroy
                if (nav != null)
                {
                    nav.enabled = false;
                }
                // move slightly out of sight instead of extremely far
                go.transform.position = sampleOrigin + Vector3.down * 5f;
                placedOnNav = false;
            }

            // Apply profile to prewarmed instance if factory/profile available
            if (agentFactory != null && defaultProfile != null)
            {
                agentFactory.ApplyProfileToInstance(go, defaultProfile);
            }

            // Ensure MoveToTarget component knows the runtime agent reference.
            // Note: MoveToTarget was refactored to be NavMeshAgent-only and no longer exposes useNavMeshAgent;
            // here we only set the agent reference so the mover can use it.
            MoveToTarget mt = go.GetComponent<MoveToTarget>();
            if (mt != null)
            {
                mt.agent = (nav != null) ? nav : mt.agent;
                // previously code set mt.useNavMeshAgent; that field was removed in the NavMesh-only MoveToTarget.
                // Decisions about whether the agent is on navmesh should be based on nav.enabled && nav.isOnNavMesh below.
            }

            // Try to fetch PedestrianAgent.AgentID (Awake already executed)
            PedestrianAgent pa = go.GetComponent<PedestrianAgent>();
            string agentIdStr = pa != null ? pa.AgentID : "n/a";

            // Logging: prewarm instance info (include unique AgentID for traceability)
            string navState = (nav != null) ? $"nav.enabled={nav.enabled} nav.isOnNavMesh={nav.isOnNavMesh}" : "no NavMeshAgent";
            if (enableDebugLogs) Debug.Log($"[SpawnerManager][Prewarm] idx={i} placedOnNav={placedOnNav} {navState} pos={go.transform.position} prefab={agentPrefab.name} AgentID={agentIdStr}");

            // Deactivate and add to pool
            go.SetActive(false);
            pool.Add(go);

            // yield occasionally to avoid editor freeze
            if (i % 8 == 0) yield return null;
        }

        yield break;
    }

    IEnumerator SpawnLoop()
    {
        while (true)
        {
            // Only allow spawning once initialization (prewarm/wait) is complete.
            if (!poolInitializationComplete)
            {
                yield return null;
                continue;
            }

            // NEW: when gating is enabled, only spawn new agents while a trial is active. The loop keeps
            // running (so it re-checks every 'spawnInterval') rather than stopping, so spawning resumes
            // automatically as soon as the next OnTrialStart fires.
            bool allowedToSpawn = (!gateSpawningByTrialState) || trialActive;

            if (allowedToSpawn && liveAgents < maxAgents)
            {
                SpawnAgent();
            }
            yield return new WaitForSeconds(spawnInterval);
        }
    }

    void SpawnAgent()
    {
        // If neither a startTransform nor entrances are available, we cannot spawn.
        if (startTransform == null && (entrances == null || entrances.Length == 0) || agentPrefab == null)
            return;

        // Choose a random entrance to be the *first itinerary entry*.
        int entranceIndex = -1;
        Transform chosenEntrance = null;
        if (entrances != null && entrances.Length > 0)
        {
            entranceIndex = Random.Range(0, entrances.Length);
            chosenEntrance = entrances[entranceIndex];
        }

        // Determine spawn sampling origin:
        // - If user provided startTransform, sample around it (this is the new dragged-in start point).
        // - Otherwise fallback to the chosen entrance position (legacy behavior).
        Vector3 samplingOrigin = Vector3.zero;
        if (startTransform != null)
            samplingOrigin = startTransform.position;
        else if (chosenEntrance != null)
            samplingOrigin = chosenEntrance.position;

        // sample NavMesh near sampling origin using improved sampling
        Vector3 spawnPos = SampleNavMeshNear(samplingOrigin, spawnSampleRadius, spawnSampleMaxAttempts);
        if (spawnPos == Vector3.zero)
        {
            // defensive fallback: place at samplingOrigin rather than return and leave a stray prefab instanced later
            spawnPos = samplingOrigin;
            if (enableDebugLogs) Debug.LogWarning("SpawnerManager: failed to sample NavMesh near spawn origin. Using sampling origin position as fallback for spawn position.");
        }

        GameObject go = null;

        if (usePooling && pool.Count > 0)
        {
            // reuse from pool
            int last = pool.Count - 1;
            go = pool[last];
            pool.RemoveAt(last);

            // reactivate and place on navmesh if possible
            NavMeshAgent nav = go.GetComponent<NavMeshAgent>();
            MoveToTarget mt = go.GetComponent<MoveToTarget>();
            PedestrianAgent agentScript = go.GetComponent<PedestrianAgent>();

            // Attempt to sample & warp to a nearby NavMesh position (this is defensive; pooled objects were prewarmed but ensure correctness)
            NavMeshHit hit;
            bool placedOnNavMesh = false;
            if (NavMesh.SamplePosition(spawnPos, out hit, spawnSampleRadius * 1.5f, NavMesh.AllAreas))
            {
                if (nav != null)
                {
                    if (!nav.enabled) nav.enabled = true;
                    bool warped = nav.Warp(hit.position);
                    if (warped && nav.isOnNavMesh)
                    {
                        // set transform to sampled nav position
                        go.transform.position = hit.position;
                        // reset path safely only if agent is on navmesh
                        nav.ResetPath();
                        placedOnNavMesh = true;
                    }
                }
                else
                {
                    // no NavMeshAgent: just place transform
                    go.transform.position = hit.position;
                    placedOnNavMesh = false;
                }
            }
            else
            {
                // If no sample found, place at spawnPos (which at least is near sampling origin) and disable nav as fallback
                go.transform.position = spawnPos;
                if (nav != null)
                {
                    nav.enabled = false;
                }
                placedOnNavMesh = false;
                if (enableDebugLogs) Debug.LogWarning("SpawnerManager: pooled agent couldn't be sampled on NavMesh at spawnPos; NavMeshAgent disabled as fallback.");
            }

            // Apply profile to pooled instance if factory/profile available
            if (agentFactory != null && defaultProfile != null)
            {
                agentFactory.ApplyProfileToInstance(go, defaultProfile);
            }
            else if (defaultProfile != null)
            {
                // best-effort apply NavMeshAgent speed/shape if no factory is present
                if (nav != null)
                {
                    nav.radius = defaultProfile.agentRadius;
                    nav.height = defaultProfile.agentHeight;
                    nav.speed = defaultProfile.speed;
                    nav.acceleration = defaultProfile.acceleration;
                    nav.angularSpeed = defaultProfile.angularSpeed;
                    nav.avoidancePriority = Mathf.Clamp(defaultProfile.avoidancePriority, 0, 99);
                }
            }

            // Ensure MoveToTarget knows runtime agent by setting the agent reference.
            if (mt != null)
            {
                mt.agent = nav != null ? nav : mt.agent;
                // NOTE: MoveToTarget no longer exposes a 'useNavMeshAgent' boolean; it is NavMesh-only.
                // Decisions about whether the agent is active/on-navmesh should be based on the NavMeshAgent component state.
            }

            // activate
            go.transform.rotation = Quaternion.identity;
            go.SetActive(true);

            if (agentScript == null)
            {
                if (enableDebugLogs) Debug.LogError("Agent prefab missing PedestrianAgent script");
                Destroy(go);
                return;
            }

            // Build itinerary - choose between Transform-based or Position-based itineraries depending on configuration
            bool usingPositions = sampleAttractorsAsPositions && AtLeastOneAttractorHasArea();
            float dwell = Random.Range(dwellTimeSec.x, dwellTimeSec.y);

            if (usingPositions)
            {
                // Build position itinerary and optional parallel AttractorInfo list.
                // IMPORTANT: BuildPositionItinerary will insert the chosen entrance position as positions[0].
                List<Vector3> posItin;
                List<AttractorInfo> posAttractorInfos;
                BuildPositionItinerary(chosenEntrance, out posItin, out posAttractorInfos);

                // Configure MoveToTarget start/target for position-mode
                if (mt != null)
                {
                    // Start transform should be the user-provided startTransform (if present). If not present fallback to chosen entrance.
                    mt.startTransform = (startTransform != null) ? startTransform : chosenEntrance;
                    mt.startPosition = go.transform.position;

                    mt.targetTransform = null;
                    if (posItin != null && posItin.Count > 0)
                        mt.targetPosition = posItin[0];
                    else
                        mt.targetPosition = go.transform.position;
                }

                // Try to get AgentID for logging
                string aid = agentScript != null ? agentScript.AgentID : "n/a";

                // Log
                LogAgentItineraryPositions(go, entranceIndex, chosenEntrance, posItin, mt, go.GetComponent<NavMeshAgent>(), dwell);
                if (enableDebugLogs) Debug.Log($"[SpawnerManager] Spawned pooled agent AgentID={aid} (pooled reuse) at Start={(startTransform!=null?startTransform.name:"samplingOrigin")} pos={go.transform.position}");
                // Initialize agent with positions + optional attractor infos
                agentScript.InitializePositions(posItin, posAttractorInfos, this, dwell);
            }
            else
            {
                // Build transform-based itinerary
                // BuildItinerary will insert the chosen entrance as itinerary[0].
                List<Transform> itinerary = BuildItinerary(chosenEntrance);

                // --- ensure MoveToTarget start/target reflect StartTransform -> first itinerary entry ---
                if (mt != null)
                {
                    mt.startTransform = (startTransform != null) ? startTransform : chosenEntrance;

                    if (itinerary != null && itinerary.Count > 0 && itinerary[0] != null)
                        mt.targetTransform = itinerary[0];
                    else
                    {
                        if (itinerary != null && itinerary.Count > 0)
                            mt.targetTransform = itinerary[itinerary.Count - 1];
                        else
                            mt.targetTransform = null;
                    }

                    mt.startPosition = go.transform.position;
                }
                // --- end ---

                // Log itinerary
                LogAgentItinerary(go, entranceIndex, chosenEntrance, itinerary, mt, go.GetComponent<NavMeshAgent>(), dwell);

                // Also log AgentID explicitly
                string aid = agentScript != null ? agentScript.AgentID : "n/a";
                if (enableDebugLogs) Debug.Log($"[SpawnerManager] Spawned pooled agent AgentID={aid} (pooled reuse) at Start={(startTransform!=null?startTransform.name:"samplingOrigin")} pos={go.transform.position}");

                agentScript.Initialize(itinerary, this, dwell);
            }

            liveAgents++;
            activeAgents.Add(go); // NEW: track for trial-state pause/resume/despawn
            agentScript.onAgentFinished += OnAgentFinished;
            RaiseAgentSpawned(agentScript); // data-capture observer (after list mutation)
        }
        else
        {
            // instantiate new on spawnPos
            go = Instantiate(agentPrefab, spawnPos, Quaternion.identity);

            // After instantiate, ensure NavMeshAgent is placed on NavMesh before invoking any NavMeshAgent methods on it.
            PedestrianAgent agentScript = go.GetComponent<PedestrianAgent>();
            if (agentScript == null)
            {
                if (enableDebugLogs) Debug.LogError("Agent prefab missing PedestrianAgent script");
                Destroy(go);
                return;
            }

            NavMeshAgent nav = go.GetComponent<NavMeshAgent>();
            MoveToTarget mt = go.GetComponent<MoveToTarget>();

            // Try to place nav agent on NavMesh if possible
            NavMeshHit hit;
            bool placedOnNav = false;
            if (nav != null)
            {
                if (NavMesh.SamplePosition(spawnPos, out hit, spawnSampleRadius * 1.5f, NavMesh.AllAreas))
                {
                    if (!nav.enabled) nav.enabled = true;
                    bool warped = nav.Warp(hit.position);
                    if (warped && nav.isOnNavMesh)
                    {
                        go.transform.position = hit.position;
                        nav.ResetPath();
                        placedOnNav = true;
                    }
                    else
                    {
                        // Warp failed defensively: keep nav disabled to avoid later exceptions
                        nav.enabled = false;
                        go.transform.position = spawnPos;
                        if (enableDebugLogs) Debug.LogWarning("SpawnerManager: Instantiated agent but warp to sampled NavMesh position failed; disabling NavMeshAgent as fallback.");
                    }
                }
                else
                {
                    // Could not sample NavMesh near spawnPos: disable agent to avoid exceptions
                    nav.enabled = false;
                    go.transform.position = spawnPos;
                    if (enableDebugLogs) Debug.LogWarning("SpawnerManager: Instantiated agent but could not sample NavMesh near spawnPos; NavMeshAgent disabled on this instance as fallback.");
                }
            }
            else
            {
                // no NavMeshAgent on prefab - just place transform
                go.transform.position = spawnPos;
            }

            // Apply profile to freshly instantiated instance
            if (agentFactory != null && defaultProfile != null)
            {
                // Let factory create/apply settings: but factory.CreateAgent instantiates a prefab itself.
                // Here we already instantiated. So call ApplyProfileToInstance.
                agentFactory.ApplyProfileToInstance(go, defaultProfile);
            }
            else if (defaultProfile != null && nav != null)
            {
                // best-effort apply profile values to nav if no factory provided
                nav.radius = defaultProfile.agentRadius;
                nav.height = defaultProfile.agentHeight;
                nav.speed = defaultProfile.speed;
                nav.acceleration = defaultProfile.acceleration;
                nav.angularSpeed = defaultProfile.angularSpeed;
                nav.avoidancePriority = Mathf.Clamp(defaultProfile.avoidancePriority, 0, 99);
            }

            // Ensure MoveToTarget component is updated for runtime agent
            if (mt != null)
            {
                mt.agent = nav != null ? nav : mt.agent;
                // NOTE: MoveToTarget no longer exposes 'useNavMeshAgent' flag.
            }

            // Build itinerary and initialize (respect position-based sampling option)
            bool usingPositions = sampleAttractorsAsPositions && AtLeastOneAttractorHasArea();
            float dwell = Random.Range(dwellTimeSec.x, dwellTimeSec.y);

            if (usingPositions)
            {
                // Build position itinerary: BuildPositionItinerary inserts entrance position at index 0.
                List<Vector3> posItin;
                List<AttractorInfo> posAttractorInfos;
                BuildPositionItinerary(chosenEntrance, out posItin, out posAttractorInfos);

                if (mt != null)
                {
                    // Start transform should be the user-provided startTransform if supplied.
                    mt.startTransform = (startTransform != null) ? startTransform : chosenEntrance;
                    mt.startPosition = go.transform.position;
                    mt.targetTransform = null;
                    if (posItin != null && posItin.Count > 0)
                        mt.targetPosition = posItin[0];
                    else
                        mt.targetPosition = go.transform.position;
                }

                // Log with AgentID
                string aid = agentScript != null ? agentScript.AgentID : "n/a";
                LogAgentItineraryPositions(go, entranceIndex, chosenEntrance, posItin, mt, nav, dwell);
                if (enableDebugLogs) Debug.Log($"[SpawnerManager] Spawned new agent AgentID={aid} at Start={(startTransform!=null?startTransform.name:"samplingOrigin")} pos={go.transform.position}");

                agentScript.InitializePositions(posItin, posAttractorInfos, this, dwell);
            }
            else
            {
                // Build transform-based itinerary; BuildItinerary will insert the chosen entrance as first entry.
                List<Transform> itinerary = BuildItinerary(chosenEntrance);

                if (mt != null)
                {
                    mt.startTransform = (startTransform != null) ? startTransform : chosenEntrance;

                    if (itinerary != null && itinerary.Count > 0 && itinerary[0] != null)
                        mt.targetTransform = itinerary[0];
                    else
                    {
                        if (itinerary != null && itinerary.Count > 0)
                            mt.targetTransform = itinerary[itinerary.Count - 1];
                        else
                            mt.targetTransform = null;
                    }

                    mt.startPosition = go.transform.position;
                }

                // Log itinerary and AgentID
                string aid = agentScript != null ? agentScript.AgentID : "n/a";
                LogAgentItinerary(go, entranceIndex, chosenEntrance, itinerary, mt, nav, dwell);
                if (enableDebugLogs) Debug.Log($"[SpawnerManager] Spawned new agent AgentID={aid} at Start={(startTransform!=null?startTransform.name:"samplingOrigin")} pos={go.transform.position}");

                agentScript.Initialize(itinerary, this, dwell);
            }

            liveAgents++;
            activeAgents.Add(go); // NEW: track for trial-state pause/resume/despawn
            agentScript.onAgentFinished += OnAgentFinished;
            RaiseAgentSpawned(agentScript); // data-capture observer (after list mutation)
        }
    }

    /// <summary>
    /// Log itinerary when using position-based itineraries.
    /// </summary>
    void LogAgentItineraryPositions(GameObject go, int entranceIndex, Transform entrance, List<Vector3> positions, MoveToTarget mt, NavMeshAgent nav, float dwell)
    {
        // NEW: bail out before doing any string work if logging is disabled - this is the actual
        // performance-relevant fix, since building the StringBuilder below is the expensive part,
        // not just the Debug.Log(...) call at the end.
        if (!enableDebugLogs) return;

        // Try to include the unique AgentID in logs if present
        PedestrianAgent pa = go.GetComponent<PedestrianAgent>();
        string agentId = pa != null ? pa.AgentID : "n/a";

        string goId = $"{go.name} AgentID={agentId}";
        string entranceName = entrance != null ? entrance.name : "null";
        string entranceInfo = $"Entrance[{entranceIndex}]={entranceName}";

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine($"[SpawnerManager][AgentSpawn] {goId} spawnInfo: {entranceInfo} dwell={dwell:F2}s liveAgents={liveAgents}");
        if (positions == null || positions.Count == 0)
        {
            sb.AppendLine("  Position itinerary: EMPTY");
        }
        else
        {
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 p = positions[i];
                // Check if this index corresponds to an exit (last)
                if (i == positions.Count - 1)
                {
                    sb.AppendLine($"  Itinerary[{i}] = EXIT   pos={p}");
                }
                else if (i == 0)
                {
                    // First entry is the entrance position (we log that explicitly)
                    sb.AppendLine($"  Itinerary[{i}] = ENTRANCE pos={p} (entrance name='{entranceName}')");
                }
                else
                {
                    // Try to find matching attractor index for logging clarity (best-effort)
                    int attractorIdx = -1;
                    if (attractors != null)
                    {
                        for (int ai = 0; ai < attractors.Length; ai++)
                        {
                            var a = attractors[ai];
                            if (a != null && a.transform != null)
                            {
                                // compare horizontal proximity as heuristic
                                if (Vector3.SqrMagnitude(a.transform.position - new Vector3(p.x, a.transform.position.y, p.z)) < 0.5f * 0.5f)
                                {
                                    attractorIdx = ai;
                                    break;
                                }
                            }
                        }
                    }

                    if (attractorIdx >= 0)
                        sb.AppendLine($"  Itinerary[{i}] = ATTRACTOR [attractors[{attractorIdx}]] approxPos={p}");
                    else
                        sb.AppendLine($"  Itinerary[{i}] = ATTRACTOR pos={p}");
                }
            }
        }

        string navInfo = nav != null ? $"NavMeshAgent(enabled={nav.enabled}, isOnNavMesh={nav.isOnNavMesh}, speed={nav.speed:F2})" : "NavMeshAgent=null";
        // MoveToTarget no longer exposes useNavMeshAgent; report whether MoveToTarget has an agent assigned.
        string mtInfo = mt != null ? $"MoveToTarget(start={(mt.startTransform!=null?mt.startTransform.name:"null")}, agentAssigned={(mt.agent!=null? "yes":"no")}, targetPos={(positions!=null && positions.Count>0?positions[0].ToString():"null")})" : "MoveToTarget=null";

        sb.AppendLine($"  Runtime: {navInfo} | {mtInfo} | prefab={agentPrefab.name}");
        if (enableDebugLogs) Debug.Log(sb.ToString());
    }

    /// <summary>
    /// Existing log function for transform-based itineraries retained.
    /// </summary>
    void LogAgentItinerary(GameObject go, int entranceIndex, Transform entrance, List<Transform> itinerary, MoveToTarget mt, NavMeshAgent nav, float dwell)
    {
        // NEW: bail out before doing any string work if logging is disabled - see the matching comment
        // in LogAgentItineraryPositions() above for why this early-return (not just gating Debug.Log)
        // is the part that actually matters for performance.
        if (!enableDebugLogs) return;

        // Try to include the unique AgentID in logs if present
        PedestrianAgent pa = go.GetComponent<PedestrianAgent>();
        string agentId = pa != null ? pa.AgentID : "n/a";
        string goId = $"{go.name} AgentID={agentId}";
        string entranceName = entrance != null ? entrance.name : "null";
        string entranceInfo = $"Entrance[{entranceIndex}]={entranceName}";

        // Build itinerary description
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine($"[SpawnerManager][AgentSpawn] {goId} spawnInfo: {entranceInfo} dwell={dwell:F2}s liveAgents={liveAgents}");

        if (itinerary == null || itinerary.Count == 0)
        {
            sb.AppendLine("  Itinerary: EMPTY");
        }
        else
        {
            for (int i = 0; i < itinerary.Count; i++)
            {
                Transform t = itinerary[i];
                if (t == null)
                {
                    sb.AppendLine($"  Itinerary[{i}] = NULL");
                    continue;
                }

                // If this is first entry treat it as entrance for clarity
                if (i == 0)
                {
                    sb.AppendLine($"  Itinerary[{i}] = ENTRANCE name='{t.name}'");
                    continue;
                }

                // Check if this transform is one of the exits
                int exitIdx = -1;
                if (exits != null)
                {
                    for (int xi = 0; xi < exits.Length; xi++)
                    {
                        if (exits[xi] == t) { exitIdx = xi; break; }
                    }
                }

                if (exitIdx >= 0)
                {
                    sb.AppendLine($"  Itinerary[{i}] = EXIT   [exits[{exitIdx}]] name='{t.name}'");
                    continue;
                }

                // Otherwise check attractors
                int attractorIdx = -1;
                if (attractors != null)
                {
                    for (int ai = 0; ai < attractors.Length; ai++)
                    {
                        var a = attractors[ai];
                        if (a != null && a.transform == t)
                        {
                            attractorIdx = ai;
                            break;
                        }
                    }
                }

                if (attractorIdx >= 0)
                {
                    sb.AppendLine($"  Itinerary[{i}] = ATTRACTOR [attractors[{attractorIdx}]] name='{t.name}' capacity={(attractors[attractorIdx] != null ? attractors[attractorIdx].capacity.ToString() : "n/a")}");
                    continue;
                }

                // Unknown transform (could be custom, or reused)
                sb.AppendLine($"  Itinerary[{i}] = UNKNOWN name='{t.name}'");
            }
        }

        // Print runtime nav and MoveToTarget info
        string navInfo = nav != null ? $"NavMeshAgent(enabled={nav.enabled}, isOnNavMesh={nav.isOnNavMesh}, speed={nav.speed:F2})" : "NavMeshAgent=null";
        // Replace useNavMesh with agentAssigned status for MoveToTarget
        string mtInfo = mt != null ? $"MoveToTarget(start={(mt.startTransform!=null?mt.startTransform.name:"null")}, target={(mt.targetTransform!=null?mt.targetTransform.name:"null")}, agentAssigned={(mt.agent!=null? "yes":"no")})" : "MoveToTarget=null";

        sb.AppendLine($"  Runtime: {navInfo} | {mtInfo} | prefab={agentPrefab.name}");

        // Output the composed log
        if (enableDebugLogs) Debug.Log(sb.ToString());
    }

    /// <summary>
    /// Sample a position for a given AttractorInfo:
    /// - If the attractor transform provides an AttractorArea component, use that object's sampling method.
    /// - Otherwise attempt NavMesh sampling near the attractor transform position (or fallback to transform.position).
    /// </summary>
    Vector3 SampleAttractorPosition(AttractorInfo a)
    {
        if (a == null || a.transform == null) return Vector3.zero;

        // If AttractorArea component exists, prefer its sampling API
        AttractorArea area = a.transform.GetComponent<AttractorArea>();
        if (area != null)
        {
            Vector3 p = area.SamplePointOnNavMesh();
            if (enableDebugLogs) Debug.Log($"[AttractorArea] SamplePointOnNavMesh() returning {p} (caller: {Time.frameCount})");
            if (p != Vector3.zero) return p;
        }

        // Otherwise attempt NavMesh sampling near the attractor transform
        Vector3 fallbackSample = SampleNavMeshNear(a.transform.position, spawnSampleRadius, spawnSampleMaxAttempts);
        if (fallbackSample != Vector3.zero) return fallbackSample;

        // final fallback: use the transform's position
        return a.transform.position;
    }

    /// <summary>
    /// Build a position-based itinerary (list of world positions) and a parallel list of AttractorInfo for capacity checks.
    /// The returned posAttractorInfos has the same length as positions; entries corresponding to non-attractor (e.g. entrance or exit) are null.
    ///
    /// IMPORTANT: The 'entrance' parameter is the chosen entrance transform which will be inserted as positions[0].
    /// The rest of the logic (n attractors then exit) is preserved.
    /// </summary>
    void BuildPositionItinerary(Transform entrance, out List<Vector3> positions, out List<AttractorInfo> posAttractorInfos)
    {
        positions = new List<Vector3>();
        posAttractorInfos = new List<AttractorInfo>();

        int num = Random.Range(minAttractorsPerAgent, maxAttractorsPerAgent + 1);

        if (attractors != null && attractors.Length > 0)
        {
            List<int> indices = new List<int>();
            for (int i = 0; i < attractors.Length; i++) indices.Add(i);
            for (int k = 0; k < num && indices.Count > 0; k++)
            {
                int iSel = Random.Range(0, indices.Count);
                int idx = indices[iSel];
                indices.RemoveAt(iSel); // avoid visiting same attractor twice
                var a = attractors[idx];
                if (a != null)
                {
                    Vector3 p = SampleAttractorPosition(a);
                    positions.Add(p);
                    posAttractorInfos.Add(a);
                }
            }
        }

        // finally pick an exit
        if (exits != null && exits.Length > 0)
        {
            Transform exit = exits[Random.Range(0, exits.Length)];
            positions.Add(exit.position);
            posAttractorInfos.Add(null); // exit has no AttractorInfo
        }

        // Insert the chosen entrance position at the start of the itinerary so itinerary[0] = entrance
        if (entrance != null)
        {
            positions.Insert(0, entrance.position);
            posAttractorInfos.Insert(0, null); // entrance is not an attractor; store null in parallel list
        }
        else
        {
            // If entrance is null (no entrances configured), we do nothing; itinerary will just be attractors+exit
        }
    }

    /// <summary>
    /// Existing transform-based itinerary builder retained (for backward compatibility).
    /// MODIFIED: the chosen 'entrance' transform is inserted as the first itinerary element (itinerary[0]).
    /// The rest of the logic (n attractors then exit) is preserved.
    /// </summary>
    List<Transform> BuildItinerary(Transform entrance)
    {
        List<Transform> list = new List<Transform>();

        int num = Random.Range(minAttractorsPerAgent, maxAttractorsPerAgent + 1);

        if (attractors != null && attractors.Length > 0)
        {
            List<int> indices = new List<int>();
            for (int i = 0; i < attractors.Length; i++) indices.Add(i);
            for (int k = 0; k < num && indices.Count > 0; k++)
            {
                int iSel = Random.Range(0, indices.Count);
                int idx = indices[iSel];
                indices.RemoveAt(iSel); // avoid visiting same attractor twice
                if (attractors[idx] != null && attractors[idx].transform != null)
                    list.Add(attractors[idx].transform);
            }
        }

        // finally pick an exit
        if (exits != null && exits.Length > 0)
        {
            Transform exit = exits[Random.Range(0, exits.Length)];
            list.Add(exit);
        }

        // Insert the chosen entrance as the first element so itinerary[0] is an entrance.
        if (entrance != null)
            list.Insert(0, entrance);

        return list;
    }

    public void OnAgentFinished(PedestrianAgent agent)
    {
        agent.onAgentFinished -= OnAgentFinished;

        liveAgents--;
        activeAgents.Remove(agent.gameObject); // NEW: stop tracking this agent for trial-state pause/despawn
        RaiseAgentDespawned(agent); // data-capture observer (after list mutation)
        
        if (usePooling)
        {
            GameObject go = agent.gameObject;

            // Defensive: if the NavMeshAgent is present but not in a good state, ensure it's left enabled if on NavMesh.
            NavMeshAgent nav = go.GetComponent<NavMeshAgent>();
            if (nav != null)
            {
                if (!nav.enabled && nav.isOnNavMesh)
                {
                    nav.enabled = true;
                }
            }

            // ensure MoveToTarget state is consistent
            MoveToTarget mt = go.GetComponent<MoveToTarget>();
            if (mt != null)
            {
                mt.agent = nav != null ? nav : mt.agent;
                // previously code set mt.useNavMeshAgent; that field no longer exists.
                // The MoveToTarget component is expected to be NavMeshAgent-only; runtime behavior will reflect nav state.
            }

            // Log agent finishing and pooling info (include AgentID)
            string agentId = agent != null ? agent.AgentID : "n/a";
            string goId = $"{go.name} AgentID={agentId}";
            string navState = nav != null ? $"nav.enabled={nav.enabled} nav.isOnNavMesh={nav.isOnNavMesh}" : "no NavMeshAgent";
            if (enableDebugLogs) Debug.Log($"[SpawnerManager][OnAgentFinished] {goId} returning to pool. liveAgents={liveAgents} {navState}");

            go.SetActive(false);
            pool.Add(go);
        }
        else
        {
            Destroy(agent.gameObject);
        }
    }

    public bool TryEnterAttractor(AttractorInfo info, PedestrianAgent agent)
    {
        if (info == null || info.transform == null) return true;
        if (info.capacity <= 0)
        {
            if (!info.occupants.Contains(agent)) info.occupants.Add(agent);
            return true;
        }
        if (info.occupants.Count < info.capacity)
        {
            if (!info.occupants.Contains(agent)) info.occupants.Add(agent);
            return true;
        }
        return false;
    }

    public void ReleaseAttractor(AttractorInfo info, PedestrianAgent agent)
    {
        if (info == null) return;
        if (info.occupants.Contains(agent)) info.occupants.Remove(agent);
    }

    public AttractorInfo FindAttractorInfo(Transform t)
    {
        if (attractors == null) return null;
        foreach (var a in attractors)
            if (a != null && a.transform == t) return a;
        return null;
    }

    /// <summary>
    /// Helper that returns true if at least one configured attractor has an AttractorArea component.
    /// Used to decide whether position-based sampling makes sense.
    /// </summary>
    bool AtLeastOneAttractorHasArea()
    {
        if (attractors == null) return false;
        foreach (var a in attractors)
        {
            if (a == null || a.transform == null) continue;
            if (a.transform.GetComponent<AttractorArea>() != null) return true;
        }
        return false;
    }

    /// <summary>
    /// Improved sampling routine (kept from original script).
    /// </summary>
    Vector3 SampleNavMeshNear(Vector3 origin, float radius, int attempts)
    {
        NavMeshHit navHit;

        // Defensive clamps
        attempts = Mathf.Max(1, attempts);
        radius = Mathf.Max(0.01f, radius);

        for (int i = 0; i < attempts; i++)
        {
            // Sample a random point on the horizontal plane around the origin.
            Vector2 r2 = Random.insideUnitCircle * radius;
            Vector3 candidate = new Vector3(origin.x + r2.x, origin.y, origin.z + r2.y);

            // 1) Try to project the candidate down onto geometry (if colliders exist).
            Vector3 projected;
            bool projectedToGround = TryProjectToGround(candidate, out projected);

            if (projectedToGround)
            {
                // Slightly lift the point to avoid embedding into the mesh
                Vector3 probe = projected + Vector3.up * groundOffset;

                // Attempt NavMesh sampling at projected ground location first.
                if (NavMesh.SamplePosition(probe, out navHit, radius * 0.5f + 0.1f, NavMesh.AllAreas))
                {
                    return navHit.position;
                }

                // If direct sample fails, try small vertical offsets around the projected point (use verticalOffsets)
                foreach (float vo in verticalOffsets)
                {
                    Vector3 offsetProbe = new Vector3(probe.x, projected.y + vo, probe.z);
                    if (NavMesh.SamplePosition(offsetProbe, out navHit, Mathf.Max(0.2f, radius * 0.5f + Mathf.Abs(vo)), NavMesh.AllAreas))
                    {
                        return navHit.position;
                    }
                }
            }
            else
            {
                // 2) If ground projection failed (no colliders), fallback to sampling NavMesh at multiple vertical offsets
                // relative to the origin.y (so the sampling covers possible NavMesh heights).
                foreach (float vo in verticalOffsets)
                {
                    Vector3 offsetProbe = new Vector3(candidate.x, origin.y + vo, candidate.z);

                    // We sample with a small vertical tolerance; this helps if the NavMesh is slightly above/below the candidate
                    if (NavMesh.SamplePosition(offsetProbe, out navHit, radius * 0.5f + Mathf.Abs(vo) + 0.1f, NavMesh.AllAreas))
                    {
                        return navHit.position;
                    }
                }
            }
        }

        // As a last resort, try origin directly (project it to ground if possible)
        Vector3 originProjected;
        if (TryProjectToGround(origin, out originProjected))
        {
            if (NavMesh.SamplePosition(originProjected + Vector3.up * groundOffset, out navHit, Mathf.Max(0.2f, radius * 0.5f), NavMesh.AllAreas))
                return navHit.position;

            foreach (float vo in verticalOffsets)
            {
                Vector3 offsetProbe = new Vector3(originProjected.x, originProjected.y + vo, originProjected.z);
                if (NavMesh.SamplePosition(offsetProbe, out navHit, Mathf.Max(0.2f, radius * 0.5f + Mathf.Abs(vo)), NavMesh.AllAreas))
                    return navHit.position;
            }
        }
        else
        {
            // Try origin with vertical offsets even if no projection
            foreach (float vo in verticalOffsets)
            {
                Vector3 offsetProbe = new Vector3(origin.x, origin.y + vo, origin.z);
                if (NavMesh.SamplePosition(offsetProbe, out navHit, Mathf.Max(0.2f, radius * 0.5f + Mathf.Abs(vo)), NavMesh.AllAreas))
                    return navHit.position;
            }
        }

        // nothing found
        return Vector3.zero;
    }

    bool TryProjectToGround(Vector3 samplePoint, out Vector3 groundPoint)
    {
        groundPoint = Vector3.zero;

        // Compose a reasonable start height so the raycast can pass over small obstacles.
        Vector3 rayStart = samplePoint + Vector3.up * (raycastDownDistance * 0.5f + 1f);
        float rayDistance = raycastDownDistance;

        RaycastHit hit;
        if (Physics.Raycast(rayStart, Vector3.down, out hit, rayDistance, groundLayers, QueryTriggerInteraction.Ignore))
        {
            groundPoint = hit.point;
            return true;
        }

        return false;
    }
}