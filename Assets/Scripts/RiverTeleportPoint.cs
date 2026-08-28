// RiverTeleportPoint.cs
// Attach this component to EACH teleport point placed near the riverbank (both west-bank and east-bank points).
// Each point is paired 1:1 with a teleport point on the opposite bank via the 'pairedPoint' field (drag & drop
// in the Inspector). When the participant selects this point (click / XR-interactor select), the player rig
// is instantly moved (teleported) to the position/rotation of 'pairedPoint'.
//
// Design notes:
// - Mirrors the existing TargetSelectable.cs pattern already used in this project (IPointerClickHandler +
//   OnMouseDown for Editor testing + a public Select() method that XR interactors can call directly),
//   so it fits the established interaction style without introducing a new input pathway.
// - The player rig reference is resolved the same way TargetSelectable resolves ExperimentManager: assign it
//   explicitly in the Inspector if possible, otherwise it is auto-found via ExperimentManager.playerRig at Start().
// - A short cooldown prevents a "teleport ping-pong" in case both points on either bank are placed close enough
//   that arriving at pairedPoint immediately re-triggers pairedPoint's own collider/click handler.
// - This script does NOT change AutoGroundAlign or MoveToTarget/NavMeshAgent state; it only repositions the
//   player rig Transform, consistent with how ExperimentManager.TeleportPlayerTo(...) already teleports the
//   player between Part2 group start/teleport positions.
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

    // Fired after a successful teleport; other systems (e.g. a future data logger) can subscribe to this
    // to record river-crossing events without modifying this script.
    public event Action<RiverTeleportPoint, RiverTeleportPoint> OnPlayerTeleported;

    private float lastTeleportTime = -999f;

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

        // Cooldown guard to avoid immediate re-trigger (e.g. if the destination point's own collider
        // overlaps the player right after arrival).
        if (Time.time - lastTeleportTime < teleportCooldown)
        {
            return;
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
        Vector3 targetPos = destination.position + Vector3.up * verticalOffset;
        playerRig.position = targetPos;

        if (matchPairedRotation)
        {
            playerRig.rotation = destination.rotation;
        }

        Debug.Log($"RiverTeleportPoint: Teleported player from '{gameObject.name}' to '{destination.name}' at {targetPos}.");

        // Notify any listeners (e.g. a future TrajectoryRecorder-style logger) that a river crossing occurred.
        // 'this' is the point the player teleported FROM; destination's own RiverTeleportPoint (if present)
        // is looked up so listeners get both ends of the crossing.
        RiverTeleportPoint destinationPoint = destination.GetComponent<RiverTeleportPoint>();
        OnPlayerTeleported?.Invoke(this, destinationPoint);
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