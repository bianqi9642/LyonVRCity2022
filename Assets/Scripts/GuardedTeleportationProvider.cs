// GuardedTeleportationProvider.cs
//
// Role
// ----
// Central, PRE-execution gate for every XRI teleport request (TeleportationAnchor, TeleportationArea, or any
// future BaseTeleportationInteractable). It replaces the stock TeleportationProvider on the active XR Origin
// ("Teleportation" object of the XR Interaction Setup instance) and overrides the one supported XRI 2.6.5
// extension point that sees a request before anything moves:
//
//     TeleportationProvider.QueueTeleportRequest(TeleportRequest)   // public virtual bool
//
// The only caller of that method in XRI 2.6.5 is BaseTeleportationInteractable.SendTeleportRequest, which
// treats a 'false' return as "request failed": no request is stored, the 'teleporting' event is not raised,
// and the XR Origin is never moved. So a rejected request leaves the rig exactly where it was.
//
// Why a provider and not 378 per-anchor edits
// -------------------------------------------
// All anchors locate their provider through ComponentLocatorUtility<TeleportationProvider>, which returns the
// (single) provider in the scene. Gating the provider therefore covers every existing anchor, every anchor
// that is activated or instantiated later, and TeleportationArea. The stock provider must NOT remain on the
// rig next to this one (the locator could return either) - the scene removes it via a prefab override.
//
// Landing position
// ----------------
// XRI does not move the rig root to the anchor. TeleportationProvider.Update() matches the origin orientation
// and then calls XROrigin.MoveCameraToWorldLocation(destination + up * cameraHeightInOrigin), i.e. it puts
// the participant's HEAD (camera) on the destination and the rig ROOT wherever the participant's physical
// offset from the root implies. This class reproduces that maths (ComputeLanding) and validates BOTH the head
// landing point and the rig-root landing point, not just the anchor transform. Ground alignment
// (AutoGroundAlign) only changes Y afterwards and the boundary test ignores Y.
//
// Policy (per running Part, read from ExperimentManager at request time - nothing is hard-coded to Part 2)
// ------------------------------------------------------------------------------------------------------
//   * Generic XRI teleport is not part of the documented protocol (the protocol's participant teleport is the
//     paired RiverTeleportPoint; scripted moves go through ExperimentManager.TeleportPlayerTo, which writes the
//     rig transform directly and never reaches this provider). The defaults are therefore the safest reading
//     of the existing code: Part 1 = Deny, Part 3 = Deny, no Part running = Deny, and Part 2 = allowed only
//     while a trial is running, no instruction panel is open, and the landing position lies inside the
//     Exploration Area (ExplorationBoundary.IsLandingPositionAllowed - the same geometry RiverTeleportPoint
//     uses via IsTeleportDestinationAllowed).
//   * Anchor-category allowance (added): the scene has three anchor groups whose positions match the Parts they serve -
//     Practice anchors surround the Part 1 start, Grid/Organic anchors surround the Part 3 start points. For a Part whose
//     policy is Deny, requests attributed to an allowed group (part1AllowedAnchors = Practice, part3AllowedAnchors =
//     Grid|Organic) are judged as AllowDuringTrial: trial running, no full panel open, not a river point. Other anchors,
//     TeleportationAreas and every other Part are unchanged (Part 2 keeps the Exploration Area test).
//   * Requests landing near a RiverTeleportPoint are rejected: river points are paired-teleport-only.
//   * Fails closed: missing ExperimentManager / ExplorationBoundary / XR Origin => request rejected.
//
// Recording (observer only, through the existing ExperimentManager.OnExperimentEvent -> DataCaptureManager path)
//   * Executed teleport  -> "teleport" event, label "xri_participant", value_str "XRI", from/to rig positions.
//     Same event type the scripted teleports use, so DataCaptureManager bumps its teleport epoch identically.
//   * Rejected request   -> "teleport_rejected" event, label "xri_participant", value_str = reason.
//
// All comments in English per project convention.

using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

[DisallowMultipleComponent]
[DefaultExecutionOrder(XRInteractionUpdateOrder.k_LocomotionProviders)]
public class GuardedTeleportationProvider : TeleportationProvider
{
    // Deny: every generic XRI teleport request is rejected.
    // AllowInsideExplorationArea: allowed only while a trial is running, no instruction panel is open, and both
    // the head and rig-root landing positions are inside the Exploration Area.
    // AllowDuringTrial: allowed while a trial is running and no instruction panel is open; no Exploration Area test
    // (that restriction belongs to Part 2 only). Appended last so serialized values 0 / 1 keep their meaning.
    public enum GenericTeleportPolicy
    {
        Deny = 0,
        AllowInsideExplorationArea = 1,
        AllowDuringTrial = 2
    }

    // Experiment-specific teleport-anchor groups (the three anchor sets in the scene). None = not a member of any
    // group (TeleportationArea requests, river-bank anchors, anchors outside the groups).
    [System.Flags]
    public enum AnchorCategory
    {
        None = 0,
        Practice = 1,
        Grid = 2,
        Organic = 4
    }

    public const string EventSourceLabel = "xri_participant";

    [Header("References (explicit slot -> auto-find -> warn)")]
    [Tooltip("Experiment state source. If empty, found with FindObjectOfType at OnEnable.")]
    [SerializeField] private ExperimentManager experimentManager;

    [Tooltip("Used to detect an open Start/Continue/End/Destination panel. If empty, found at OnEnable.")]
    [SerializeField] private UIManager uiManager;

    [Header("Policy for generic XRI teleport requests, by running Part")]
    [SerializeField] private GenericTeleportPolicy part1Policy = GenericTeleportPolicy.Deny;
    [SerializeField] private GenericTeleportPolicy part2Policy = GenericTeleportPolicy.AllowInsideExplorationArea;
    [SerializeField] private GenericTeleportPolicy part3Policy = GenericTeleportPolicy.Deny;
    [Tooltip("Applies when no Part is running (idle / between Parts).")]
    [SerializeField] private GenericTeleportPolicy noPartPolicy = GenericTeleportPolicy.Deny;

    [Header("State gating (applies to any policy other than Deny)")]
    [Tooltip("Reject requests outside OnTrialStart..OnTrialEnd (warm-up, Start panel, End panel).")]
    [SerializeField] private bool requireActiveTrial = true;

    [Tooltip("Reject requests while any full instruction panel (Start / Continue / End / Destination) is open.")]
    [SerializeField] private bool denyWhileFullPanelOpen = true;

    [Header("Anchor categories (Practice / Grid / Organic)")]
    [Tooltip("Name of the scene object that is the ancestor of every Practice anchor. An anchor belongs to a category " +
             "when one of its ancestors has exactly this name. Leave empty to disable the category.")]
    [SerializeField] private string practiceRootName = "PracticeTeleportPoint";
    [SerializeField] private string gridRootName = "GridTeleportPoint";
    [SerializeField] private string organicRootName = "OrganicTeleportPoint";

    [Tooltip("Part 1 (Practice): anchor categories that may be used when part1Policy is Deny. A matching request is judged " +
             "like AllowDuringTrial (trial running, no full panel open, not a river point); every other anchor stays denied.")]
    [SerializeField] private AnchorCategory part1AllowedAnchors = AnchorCategory.Practice;
    [Tooltip("Part 3 (Experiment): anchor categories that may be used when part3Policy is Deny. Same rule as Part 1.")]
    [SerializeField] private AnchorCategory part3AllowedAnchors = AnchorCategory.Grid | AnchorCategory.Organic;
    [Tooltip("Max distance (m) between a request's destination and an anchor's teleport transform for the request to be " +
             "attributed to that anchor. Anchors are >2.5 m apart in the scene.")]
    [SerializeField] private float anchorMatchRadius = 0.25f;

    [Header("River points")]
    [Tooltip("Generic requests whose destination is within this horizontal distance (m) of a RiverTeleportPoint are " +
             "rejected: river points teleport only through RiverTeleportPoint.Select() (pairing, cooldown, boundary, event).")]
    [SerializeField] private float riverPointExclusionRadius = 1f;

    [Tooltip("A request whose destination is on an ENABLED RiverTeleportPoint is not executed as a generic XRI " +
             "teleport (which would land on the selected point itself). It is handed to RiverTeleportPoint.Select() - " +
             "pairing, cooldown, boundary check, OnPlayerTeleported - and the XRI request is declined, so one participant " +
             "action causes exactly one operation. Requests near a river point that is not enabled stay rejected.")]
    [SerializeField] private bool routeRiverAnchorRequests = true;

    [Header("Diagnostics")]
    [Tooltip("Log one concise line per rejected request (rate-limited).")]
    [SerializeField] private bool logRejections = true;
    [SerializeField] private float rejectionLogCooldown = 0.5f;
    [Tooltip("Log one line per request outcome (accepted / executed / routed to a river point). One line per " +
             "participant action, never per frame.")]
    [SerializeField] private bool logRequests = true;
    [SerializeField] private bool enableDebugLogs = false;

    private bool trialActive;
    private bool subscribed;
    private float lastRejectionLogTime = -999f;
    private RiverTeleportPoint[] riverPoints;
    private TeleportationAnchor[] anchorCache;
    private AnchorCategory[] anchorCacheCategories;
    private string lastAnchorLabel = "";

    /// <summary>True from OnTrialStart to OnTrialEnd of the running Part.</summary>
    public bool TrialActive => trialActive;

    // ---------------------------------------------------------------- lifecycle

    private void OnEnable()
    {
        if (experimentManager == null) experimentManager = FindObjectOfType<ExperimentManager>();
        if (uiManager == null) uiManager = FindObjectOfType<UIManager>();

        if (experimentManager == null)
            Debug.LogWarning("[GuardedTeleportationProvider] No ExperimentManager found - all generic XRI teleports will be rejected.", this);

        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
        trialActive = false;
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (subscribed || experimentManager == null) return;
        experimentManager.OnTrialStart += HandleTrialStart;
        experimentManager.OnTrialEnd += HandleTrialEnd;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed || experimentManager == null) return;
        experimentManager.OnTrialStart -= HandleTrialStart;
        experimentManager.OnTrialEnd -= HandleTrialEnd;
        subscribed = false;
    }

    private void HandleTrialStart() { trialActive = true; }
    private void HandleTrialEnd() { trialActive = false; }

    // ---------------------------------------------------------------- the gate

    /// <summary>
    /// Pre-execution gate. Returning false means the base provider is never asked to store the request, so the
    /// XR Origin does not move and BaseTeleportationInteractable reports failure (no 'teleporting' event).
    /// </summary>
    public override bool QueueTeleportRequest(TeleportRequest teleportRequest)
    {
        // River points are paired-teleport-only: hand the action to RiverTeleportPoint.Select() and decline the
        // generic request, so the rig is never moved to the selected river point's own anchor position.
        if (routeRiverAnchorRequests && TryRouteToRiverPoint(teleportRequest))
            return false;

        if (!Evaluate(teleportRequest, out string reason, out Vector3 camLanding, out Vector3 rootLanding))
        {
            OnRequestRejected(teleportRequest, reason, camLanding, rootLanding);
            return false;
        }

        if (logRequests || enableDebugLogs)
        {
            Debug.Log($"[GuardedTeleportationProvider] Accepted XRI teleport request to {Fmt(teleportRequest.destinationPosition)} " +
                      $"(head {Fmt(camLanding)}, rig root {Fmt(rootLanding)}, part '{PartName()}', anchor '{lastAnchorLabel}').", this);
        }

        return base.QueueTeleportRequest(teleportRequest);
    }

    /// <summary>
    /// Wraps the stock Update only to observe completion: the stock provider clears 'validRequest' in the same
    /// call that moves the rig, so (had request) && (no request now) means the move just happened.
    /// </summary>
    protected override void Update()
    {
        bool hadRequest = validRequest;
        Transform rig = RigRoot();
        Vector3 before = rig != null ? rig.position : Vector3.zero;

        base.Update();

        if (hadRequest && !validRequest && rig != null)
        {
            ReportExecuted(before, rig.position);
        }
    }

    private bool Evaluate(TeleportRequest request, out string reason, out Vector3 camLanding, out Vector3 rootLanding)
    {
        camLanding = request.destinationPosition;
        rootLanding = request.destinationPosition;
        reason = "";

        if (experimentManager == null)
        {
            reason = "no_experiment_manager";
            return false;
        }

        string part = PartName();
        GenericTeleportPolicy policy = PolicyFor(part);

        // Anchor-category allowance: a Part whose generic policy is Deny may still permit specific anchor groups
        // (Practice in Part 1, Grid/Organic in Part 3). Such a request is judged under AllowDuringTrial, so the trial,
        // panel and river-point gates below still apply. Any other anchor stays denied.
        AnchorCategory category = ResolveAnchorCategory(request.destinationPosition, out lastAnchorLabel);
        if (policy == GenericTeleportPolicy.Deny && category != AnchorCategory.None &&
            (AllowedAnchorsFor(part) & category) != 0)
        {
            policy = GenericTeleportPolicy.AllowDuringTrial;
        }

        if (policy == GenericTeleportPolicy.Deny)
        {
            reason = string.IsNullOrEmpty(part) ? "policy_deny_no_part_running" : "policy_deny_" + part;
            return false;
        }

        if (requireActiveTrial && !trialActive)
        {
            reason = "trial_not_active";
            return false;
        }

        if (denyWhileFullPanelOpen && uiManager != null && uiManager.HasOpenFullPanel)
        {
            reason = "panel_open";
            return false;
        }

        if (IsNearRiverPoint(request.destinationPosition))
        {
            reason = "river_point_requires_paired_teleport";
            return false;
        }

        if (!ComputeLanding(request, out camLanding, out rootLanding))
        {
            reason = "no_xr_origin";
            return false;
        }

        if (policy == GenericTeleportPolicy.AllowInsideExplorationArea)
        {
            ExplorationBoundary boundary = ExplorationBoundary.Instance;
            if (boundary == null)
            {
                reason = "no_exploration_boundary";
                return false;
            }

            if (!boundary.IsLandingPositionAllowed(camLanding, out string why))
            {
                reason = "head_" + why;
                return false;
            }

            if (!boundary.IsLandingPositionAllowed(rootLanding, out why))
            {
                reason = "rig_" + why;
                return false;
            }
        }

        return true;
    }

    private GenericTeleportPolicy PolicyFor(string part)
    {
        switch (part)
        {
            case "Part1": return part1Policy;
            case "Part2": return part2Policy;
            case "Part3": return part3Policy;
            default: return noPartPolicy;
        }
    }

    private AnchorCategory AllowedAnchorsFor(string part)
    {
        switch (part)
        {
            case "Part1": return part1AllowedAnchors;
            case "Part3": return part3AllowedAnchors;
            default: return AnchorCategory.None;
        }
    }

    /// <summary>
    /// Attributes a request to a Practice / Grid / Organic anchor: the nearest TeleportationAnchor whose teleport
    /// transform lies within anchorMatchRadius of the request destination (XRI sets the destination to exactly that
    /// transform). The anchor list is cached and rebuilt once when no anchor matches (anchors may be activated later).
    /// Returns None for TeleportationArea requests and for anchors outside the three groups.
    /// </summary>
    private AnchorCategory ResolveAnchorCategory(Vector3 destination, out string label)
    {
        label = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (anchorCache == null || attempt == 1) BuildAnchorCache();

            int best = -1;
            float bestSqr = anchorMatchRadius * anchorMatchRadius;
            for (int i = 0; i < anchorCache.Length; i++)
            {
                TeleportationAnchor a = anchorCache[i];
                if (a == null) continue;
                Transform t = a.teleportAnchorTransform != null ? a.teleportAnchorTransform : a.transform;
                float sqr = (t.position - destination).sqrMagnitude;
                if (sqr <= bestSqr) { bestSqr = sqr; best = i; }
            }

            if (best >= 0)
            {
                label = anchorCache[best].name + "/" + anchorCacheCategories[best];
                return anchorCacheCategories[best];
            }
        }
        return AnchorCategory.None;
    }

    private void BuildAnchorCache()
    {
        anchorCache = FindObjectsOfType<TeleportationAnchor>(true);
        anchorCacheCategories = new AnchorCategory[anchorCache.Length];
        for (int i = 0; i < anchorCache.Length; i++)
            anchorCacheCategories[i] = CategoryOf(anchorCache[i]);
    }

    private AnchorCategory CategoryOf(TeleportationAnchor anchor)
    {
        for (Transform t = anchor.transform; t != null; t = t.parent)
        {
            if (!string.IsNullOrEmpty(practiceRootName) && t.name == practiceRootName) return AnchorCategory.Practice;
            if (!string.IsNullOrEmpty(gridRootName) && t.name == gridRootName) return AnchorCategory.Grid;
            if (!string.IsNullOrEmpty(organicRootName) && t.name == organicRootName) return AnchorCategory.Organic;
        }
        return AnchorCategory.None;
    }

    private string PartName()
    {
        return experimentManager != null ? experimentManager.RunningPartName : "";
    }

    /// <summary>
    /// If the request's destination coincides (within riverPointExclusionRadius, horizontally) with an enabled
    /// RiverTeleportPoint, performs that point's own paired teleport and returns true (caller declines the request).
    /// </summary>
    private bool TryRouteToRiverPoint(TeleportRequest request)
    {
        if (riverPointExclusionRadius <= 0f) return false;

        if (riverPoints == null)
            riverPoints = FindObjectsOfType<RiverTeleportPoint>(true);

        RiverTeleportPoint best = null;
        float bestSqr = riverPointExclusionRadius * riverPointExclusionRadius;
        for (int i = 0; i < riverPoints.Length; i++)
        {
            RiverTeleportPoint rp = riverPoints[i];
            if (rp == null || !rp.isActiveAndEnabled) continue;
            Vector3 d = rp.transform.position - request.destinationPosition;
            d.y = 0f;
            float sqr = d.sqrMagnitude;
            if (sqr <= bestSqr) { bestSqr = sqr; best = rp; }
        }

        if (best == null) return false;

        if (logRequests || enableDebugLogs)
            Debug.Log($"[GuardedTeleportationProvider] XRI request at {Fmt(request.destinationPosition)} routed to RiverTeleportPoint '{best.name}'.Select() (generic teleport declined).", this);

        Transform rig = RigRoot();
        Vector3 before = rig != null ? rig.position : Vector3.zero;
        best.Select();

        // RiverTeleportPoint.Select() returns void and refuses silently (cooldown, boundary, missing refs), so the
        // outcome is read from the rig: one line per participant action, never per frame.
        if (logRequests || enableDebugLogs)
        {
            ExplorationBoundary eb = ExplorationBoundary.Instance;
            Vector3 after = rig != null ? rig.position : Vector3.zero;
            bool moved = rig != null && (after - before).sqrMagnitude > 0.0001f;
            Debug.Log($"[GuardedTeleportationProvider] River route '{best.name}' result: {(moved ? "MOVED" : "NOT MOVED (cooldown, Exploration Area refusal or missing reference)")}. " +
                      $"rig {Fmt(before)} -> {Fmt(after)} part='{PartName()}' boundary={(eb != null ? (eb.ProtectionActive ? "live+protecting" : "live,idle") : "null")}.", this);
        }
        return true;
    }

    private bool IsNearRiverPoint(Vector3 destination)
    {
        if (riverPointExclusionRadius <= 0f) return false;

        if (riverPoints == null)
            riverPoints = FindObjectsOfType<RiverTeleportPoint>(true);

        float r2 = riverPointExclusionRadius * riverPointExclusionRadius;
        for (int i = 0; i < riverPoints.Length; i++)
        {
            RiverTeleportPoint rp = riverPoints[i];
            if (rp == null) continue;
            Vector3 d = rp.transform.position - destination;
            d.y = 0f;
            if (d.sqrMagnitude <= r2) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- landing maths (mirrors XRI 2.6.5)

    private Transform RigRoot()
    {
        XROrigin xrOrigin = system != null ? system.xrOrigin : null;
        return (xrOrigin != null && xrOrigin.Origin != null) ? xrOrigin.Origin.transform : null;
    }

    /// <summary>
    /// Reproduces TeleportationProvider.Update(): orientation matching (rotation about the rig pivot for the
    /// up-match, about the camera for the forward-match), then MoveCameraToWorldLocation(destination +
    /// up * CameraInOriginSpaceHeight). Returns where the head and the rig root will end up.
    /// </summary>
    private bool ComputeLanding(TeleportRequest request, out Vector3 headLanding, out Vector3 rootLanding)
    {
        headLanding = request.destinationPosition;
        rootLanding = request.destinationPosition;

        XROrigin xrOrigin = system != null ? system.xrOrigin : null;
        if (xrOrigin == null || xrOrigin.Camera == null || xrOrigin.Origin == null)
            return false;

        Transform cam = xrOrigin.Camera.transform;
        Transform origin = xrOrigin.Origin.transform;

        Vector3 offset = origin.position - cam.position; // rig root relative to the head
        Vector3 up = origin.up;
        Vector3 camForward = cam.forward;

        bool matchUp = false;
        bool matchForward = false;
        Vector3 destUp = Vector3.up;

        switch (request.matchOrientation)
        {
            case MatchOrientation.WorldSpaceUp:
                matchUp = true;
                destUp = Vector3.up;
                break;
            case MatchOrientation.TargetUp:
                matchUp = true;
                destUp = request.destinationRotation * Vector3.up;
                break;
            case MatchOrientation.TargetUpAndForward:
                matchUp = true;
                matchForward = true;
                destUp = request.destinationRotation * Vector3.up;
                break;
            case MatchOrientation.None:
            default:
                break;
        }

        if (matchUp)
        {
            if (origin.up != destUp)
            {
                Quaternion upRot = Quaternion.FromToRotation(origin.up, destUp);
                offset = upRot * offset;
                camForward = upRot * camForward;
            }
            up = destUp;

            if (matchForward)
            {
                Vector3 projected = Vector3.ProjectOnPlane(camForward, destUp).normalized;
                float angle = Vector3.SignedAngle(projected, request.destinationRotation * Vector3.forward, destUp);
                offset = Quaternion.AngleAxis(angle, destUp) * offset;
            }
        }

        headLanding = request.destinationPosition + up * xrOrigin.CameraInOriginSpaceHeight;
        rootLanding = headLanding + offset;
        return true;
    }

    // ---------------------------------------------------------------- reporting (observer only)

    private void OnRequestRejected(TeleportRequest request, string reason, Vector3 camLanding, Vector3 rootLanding)
    {
        Transform rig = RigRoot();
        Vector3 rigPos = rig != null ? rig.position : Vector3.zero;

        if (logRejections && Time.unscaledTime - lastRejectionLogTime >= rejectionLogCooldown)
        {
            lastRejectionLogTime = Time.unscaledTime;
            ExplorationBoundary eb = ExplorationBoundary.Instance;
            string area = eb != null
                ? $" boundary=live(protecting={eb.ProtectionActive}) areaVolumes(active/total)={eb.ActiveAreaVolumeCount}/{eb.AreaVolumeCount}"
                : " boundary=null";
            string rigNow = rig != null ? $" rigNow={Fmt(rig.position)}" : "";
            bool panel = uiManager != null && uiManager.HasOpenFullPanel;
            Debug.Log($"[GuardedTeleportationProvider] REJECTED XRI teleport: {reason}. dest={Fmt(request.destinationPosition)} " +
                      $"head={Fmt(camLanding)} rigRoot={Fmt(rootLanding)} part='{PartName()}' anchor='{lastAnchorLabel}' trialActive={trialActive} panelOpen={panel}{rigNow}{area}.", this);
        }

        if (experimentManager != null)
        {
            try { experimentManager.ReportParticipantTeleportRejected(EventSourceLabel, reason, rigPos, request.destinationPosition); }
            catch (System.Exception e) { Debug.LogWarning("[GuardedTeleportationProvider] Rejection report failed: " + e.Message, this); }
        }
    }

    private void ReportExecuted(Vector3 from, Vector3 to)
    {
        if (logRequests || enableDebugLogs)
            Debug.Log($"[GuardedTeleportationProvider] Executed XRI teleport: rig {Fmt(from)} -> {Fmt(to)}.", this);

        if (experimentManager == null) return;
        try { experimentManager.ReportParticipantTeleport(EventSourceLabel, from, to); }
        catch (System.Exception e) { Debug.LogWarning("[GuardedTeleportationProvider] Teleport report failed: " + e.Message, this); }
    }

    private static string Fmt(Vector3 v)
    {
        return "(" + v.x.ToString("F2") + ", " + v.y.ToString("F2") + ", " + v.z.ToString("F2") + ")";
    }
}
