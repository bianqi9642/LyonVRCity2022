// RiverTeleportPoint.cs
// Attach this component to EACH teleport point placed near the riverbank (both west-bank and east-bank points).
// Each point is paired 1:1 with a teleport point on the opposite bank via the 'pairedPoint' field (drag & drop
// in the Inspector). When the participant selects this point (click / XR-interactor select), the player rig
// is instantly moved (teleported) to the position/rotation of 'pairedPoint'.
//
// Design notes:
// - Mirrors the existing TargetSelectable.cs pattern already used in this project (IPointerClickHandler +
//   OnMouseDown for Editor testing + a public Select() method that XR interactors can call directly).
// - The player rig reference is resolved the same way TargetSelectable resolves ExperimentManager.
// - A short cooldown prevents a "teleport ping-pong".
// - This script does NOT change AutoGroundAlign or MoveToTarget/NavMeshAgent state; it only repositions the
//   player rig Transform.
//
// Exploration Area boundary (Part 2), additive:
// - When ExplorationBoundary protection is active (default: during Part 2) this point refuses to teleport the
//   participant if the destination lies outside the Exploration Area. Purely a guard clause in Select();
//   outside Part 2, or if no ExplorationBoundary exists, or if 'respectExplorationBoundary' is false, the
//   teleport behaves exactly as before. The scripted experiment teleport (ExperimentManager.TeleportPlayerTo)
//   is unaffected.
//
// All comments are in English per project convention.

using System;
using UnityEngine;
using UnityEngine.EventSystems;

[RequireComponent(typeof(Collider))]
public class RiverTeleportPoint : MonoBehaviour, IPointerClickHandler
{
    [Header("Pairing (drag the matching point on the opposite bank)")]
    [Tooltip("The teleport point on the OTHER bank that this point maps to 1:1. " +
             "Drag the paired GameObject's Transform here in the Inspector.")]
    public Transform pairedPoint;

    [Header("Player reference")]
    [Tooltip("Optional: assign the XR Rig / CameraRoot to teleport directly. " +
             "If left empty, this script will try to use ExperimentManager.playerRig at Start().")]
    public Transform playerRig;

    [Tooltip("Optional: assign the scene's ExperimentManager. If left empty, it will be auto-found via " +
             "FindObjectOfType at Start() (same pattern as TargetSelectable).")]
    public ExperimentManager experimentManager;

    [Header("Teleport behaviour")]
    [Tooltip("Small vertical offset added after teleporting, useful if the paired point's pivot sits exactly " +
             "on the ground and you want to avoid clipping.")]
    public float verticalOffset = 0f;

    [Tooltip("If true, the player's rotation is also set to match pairedPoint's rotation. " +
             "If false, only position is changed and the player keeps their current facing.")]
    public bool matchPairedRotation = false;

    [Tooltip("Minimum time (seconds) between two teleports triggered by this script, to avoid immediate " +
             "back-and-forth teleporting if points are placed very close to each other.")]
    public float teleportCooldown = 0.5f;

    [Header("Exploration Area boundary (Part 2)")]
    [Tooltip("If true, this teleport point consults ExplorationBoundary before moving the participant. " +
             "While boundary protection is active (default: during Part 2) a teleport whose destination is " +
             "outside the Exploration Area is refused. Has no effect outside the gated part, or if there is " +
             "no ExplorationBoundary in the scene.")]
    public bool respectExplorationBoundary = true;

    [Tooltip("Gate diagnostic logging for this component (project convention). Only the new boundary-related " +
             "log is gated by this; the existing teleport log is unchanged.")]
    public bool enableDebugLogs = false;

    // Fired after a successful teleport; other systems (e.g. the data-capture layer) can subscribe to this
    // to record river-crossing events without modifying this script.
    // Args: (fromPoint, toPoint, playerPositionBeforeTeleport, playerPositionAfterTeleport).
    // The extra Vector3 args were added for Revision 2 data capture; the teleport mechanics are unchanged.
    public event Action<RiverTeleportPoint, RiverTeleportPoint, Vector3, Vector3> OnPlayerTeleported;

    private float lastTeleportTime = -999f;
    private float lastBlockedLogTime = -999f;

    private void Start()
    {
        if (experimentManager == null)
        {
            experimentManager = FindObjectOfType<ExperimentManager>();
        }

        // Resolve playerRig from ExperimentManager if not explicitly assigned in the Inspector.
        if (playerRig == null && experimentManager != null)
        {
            playerRig = experimentManager.playerRig;
        }

        if (playerRig == null)
        {
            Debug.LogWarning($"RiverTeleportPoint ({gameObject.name}): no playerRig assigned and none found via ExperimentManager.");
        }

        if (pairedPoint == null)
        {
            Debug.LogWarning($"RiverTeleportPoint ({gameObject.name}): pairedPoint is not assigned in the Inspector.");
        }
    }

    // Called by UI/XR pointer click systems (mirrors TargetSelectable.OnPointerClick).
    public void OnPointerClick(PointerEventData eventData)
    {
        Select();
    }

    // For quick testing with a mouse in the Editor (requires a Collider, same as TargetSelectable).
    private void OnMouseDown()
    {
        Select();
    }

    /// <summary>
    /// Public entry point. Can be called by XR Interactors, UI buttons, or other scripts,
    /// in addition to the pointer-click handlers above.
    /// </summary>
    public void Select()
    {
        // Part 2: participant-initiated river teleports are disabled (protocol). Every participant entry point - the
        // pointer click, OnMouseDown and the Guard's XRI routing - ends here, so this single check covers them all.
        // The scheduled cross-bank teleport does not use this method. Decided from the experiment state only.
        if (experimentManager == null) experimentManager = FindObjectOfType<ExperimentManager>();
        if (experimentManager != null && experimentManager.IsParticipantRiverTeleportBlocked)
        {
            if (Time.unscaledTime - lastBlockedLogTime > 0.5f)
            {
                lastBlockedLogTime = Time.unscaledTime;
                Debug.Log($"RiverTeleportPoint ({gameObject.name}): participant river teleport rejected - {ExperimentManager.RiverTeleportDisabledReason}.");
                Vector3 rigPos = playerRig != null ? playerRig.position : Vector3.zero;
                try { experimentManager.ReportParticipantTeleportRejected("river_point", ExperimentManager.RiverTeleportDisabledReason, rigPos, transform.position); }
                catch (Exception e) { Debug.LogWarning("RiverTeleportPoint: rejection report failed: " + e.Message); }
            }
            return;
        }

        if (pairedPoint == null)
        {
            Debug.LogWarning($"RiverTeleportPoint ({gameObject.name}): cannot teleport, pairedPoint is null.");
            return;
        }

        if (playerRig == null)
        {
            Debug.LogWarning($"RiverTeleportPoint ({gameObject.name}): cannot teleport, playerRig is null.");
            return;
        }

        // Cooldown guard to avoid immediate re-trigger.
        if (Time.time - lastTeleportTime < teleportCooldown)
        {
            return;
        }

        // Exploration Area boundary (Part 2): refuse a participant-triggered teleport whose destination lies
        // outside the allowed area while boundary protection is active. No-op if the system is absent or
        // protection is not currently active (IsTeleportDestinationAllowed returns true in those cases).
        if (respectExplorationBoundary && ExplorationBoundary.Instance != null)
        {
            Vector3 destinationWorldPos = pairedPoint.position + Vector3.up * verticalOffset;
            if (!ExplorationBoundary.Instance.IsTeleportDestinationAllowed(destinationWorldPos))
            {
                if (enableDebugLogs)
                {
                    Debug.Log($"RiverTeleportPoint ({gameObject.name}): teleport to '{pairedPoint.name}' " +
                              $"blocked - destination is outside the Exploration Area.");
                }
                return;
            }
        }

        TeleportPlayerTo(pairedPoint);
        lastTeleportTime = Time.time;
    }

    /// <summary>
    /// Performs the actual teleport: sets the player rig position (and optionally rotation)
    /// to the destination transform, matching the direct-set style already used by
    /// ExperimentManager.TeleportPlayerTo(...) elsewhere in the project.
    /// </summary>
    private void TeleportPlayerTo(Transform destination)
    {
        Vector3 fromPos = playerRig.position;
        Vector3 targetPos = destination.position + Vector3.up * verticalOffset;
        playerRig.position = targetPos;

        if (matchPairedRotation)
        {
            playerRig.rotation = destination.rotation;
        }

        Debug.Log($"RiverTeleportPoint: Teleported player from '{gameObject.name}' to '{destination.name}' at {targetPos}.");

        // Notify any listeners (e.g. the data-capture layer) that a river crossing occurred. The raw
        // pre/post player positions are passed so trajectory analysis can preserve the discontinuity exactly.
        RiverTeleportPoint destinationPoint = destination.GetComponent<RiverTeleportPoint>();
        OnPlayerTeleported?.Invoke(this, destinationPoint, fromPos, targetPos);
    }

    // Draw a line in the Scene view between paired points to make 1:1 mapping easy to verify visually.
    private void OnDrawGizmosSelected()
    {
        if (pairedPoint == null) return;

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, pairedPoint.position);
        Gizmos.DrawWireSphere(pairedPoint.position, 0.25f);
    }
}