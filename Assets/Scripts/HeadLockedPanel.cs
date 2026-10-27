// HeadLockedPanel.cs
//
// Follow behaviour for the two small Experiment Phase reminder panels (Destination 1 / 2 "shrunk" panels created by
// UIManager.CreateAndAnchorShrunkPanel). Full-size interactive panels never get this component.
//
// Behaviour
//   * Physical head TRANSLATION is followed on all three axes.
//   * Physical head ROTATION (yaw, pitch, roll) has no effect on the panel at all.
//   * ARTIFICIAL turning (controller snap turn / continuous turn, or any other rotation of the XR rig) rotates the panel about the
//     vertical axis together with the participant, so the panel keeps the same place relative to the player's virtual heading.
//
// How the two causes are separated (confirmed against the active rig, "XR Origin (XR Rig)" inside "XR Interaction Setup"):
//   * The HMD pose is applied by the camera's TrackedPoseDriver as a LOCAL pose under Origin -> Camera Offset -> Main Camera, so
//     physical head motion only changes the camera's local pose.
//   * XRI's SnapTurnProviderBase / ContinuousTurnProviderBase turn the player with XROrigin.RotateAroundCameraUsingOriginUp,
//     which rotates the ORIGIN GameObject (XROrigin.Origin = the "XR Origin (XR Rig)" root, which is also
//     ExperimentManager.playerRig) around the camera's world position. The camera's world rotation therefore changes for both
//     causes and cannot tell them apart, but the Origin's rotation changes ONLY for artificial turns (and scripted relocations).
//   * So everything is expressed in the Origin's coordinate frame:
//         cameraInRig = rig.InverseTransformPoint(camera.position)   // changes only with physical head translation
//         panelInRig  = initialPanelInRig + (cameraInRig - initialCameraInRig)
//         panel.position = rig.TransformPoint(panelInRig)            // carries the artificial turn / relocation of the rig
//         panel.rotation = AngleAxis(rigYaw - initialRigYaw, up) * initialPanelRotation
//     The camera's own rotation is never read. A snap turn is a rotation about the camera, which leaves cameraInRig unchanged, so
//     it is not mistaken for head translation; rigYaw is the Origin's horizontal heading only, so any pitch / roll of the Origin
//     cannot tilt the panel, and the result is recomputed from the absolute rig pose each frame (no accumulated drift).
//
// Initial placement (unchanged): from the camera, along the camera's HORIZONTAL heading at Configure() time - distance (z) ahead,
// lateral (x) to the right, height (y) up. At a level head this is the old camera-local offset (0, 2, 5). The panel's rotation is
// set once to face that heading (upright). The reference (initial rig-frame positions, rig yaw, panel rotation) is captured once
// in Configure() and never re-captured. The panel is not parented to the camera or the rig (it would inherit rotation) and not to
// UIManager (UIManager.HasOpenFullPanel counts its PanelController children as open full panels and would block generic teleports).
//
// If no XR Origin can be found (non-XRI rig, Editor test setup) the frame falls back to the world, i.e. the panel follows head
// translation only and artificial turns cannot be observed; a warning says so.
//
// Update stage: Application.onBeforeRender. The XR pose driver moves the camera late in the frame (Update and Before Render); this
// component subscribes after it, so it reads the final camera pose of the frame about to be rendered (LateUpdate would use the
// previous frame's tracking pose and the panel would lag the head). The subscription is made in OnEnable and removed in
// OnDisable, so each instance has exactly one callback and none survives its destruction. Per frame: a few transform operations
// and one SetPositionAndRotation; no allocation, no logging.
//
// All comments in English per project convention.

using Unity.XR.CoreUtils;
using UnityEngine;

[DisallowMultipleComponent]
public class HeadLockedPanel : MonoBehaviour
{
    [Tooltip("Log a line when the camera had to be re-resolved (never per frame).")]
    public bool enableDebugLogs = false;

    private Camera headCamera;
    private Transform turnRig;                  // XROrigin.Origin; null = world frame (no artificial turn observable)
    private Vector3 initialCameraInRig;
    private Vector3 initialPanelInRig;
    private float initialRigYaw;
    private float lastRigYaw;
    private Quaternion initialPanelRotation = Quaternion.identity;
    private bool hadRig;                       // a rig was found at Configure(); if it is later destroyed the panel holds still
    private bool configured;

    /// <summary>
    /// Places the panel at its start position (offset: x = lateral to the right, y = height above the camera, z = distance along
    /// the camera's horizontal heading), fixes its rotation to that heading, captures the reference pose in the XR rig's frame,
    /// and starts the follow. Until this is called the component leaves the transform alone.
    /// </summary>
    public void Configure(Camera camera, Vector3 offset, Vector3 scale)
    {
        headCamera = camera;
        Camera cam = ActiveCamera();
        if (cam == null)
        {
            Debug.LogWarning($"[HeadLockedPanel] '{name}': no active camera, panel not placed.", this);
            return;
        }

        Transform c = cam.transform;
        Vector3 fwd = Vector3.ProjectOnPlane(c.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(c.up, Vector3.up);   // looking straight up/down
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);

        Vector3 panelStart = c.position + fwd * offset.z + right * offset.x + Vector3.up * offset.y;
        initialPanelRotation = Quaternion.LookRotation(fwd, Vector3.up);

        turnRig = ResolveTurnRig(cam);
        if (turnRig == null)
            Debug.LogWarning($"[HeadLockedPanel] '{name}': no XR Origin found above camera '{cam.name}'; the panel will follow head " +
                             "translation but not controller turning.", this);
        hadRig = turnRig != null;
        initialCameraInRig = ToRig(c.position);
        initialPanelInRig = ToRig(panelStart);
        initialRigYaw = RigYaw();
        lastRigYaw = initialRigYaw;

        if (transform.parent != null) transform.SetParent(null, worldPositionStays: true);
        transform.localScale = scale;
        transform.SetPositionAndRotation(panelStart, initialPanelRotation);
        configured = true;
    }

    private void OnEnable() { Application.onBeforeRender += Follow; }
    private void OnDisable() { Application.onBeforeRender -= Follow; }

    private void Follow()
    {
        if (!configured || (hadRig && turnRig == null)) return;
        Camera cam = ActiveCamera();
        if (cam == null) return;

        // Head translation in the rig frame (independent of the camera's rotation and of artificial rig turns).
        Vector3 panelInRig = initialPanelInRig + (ToRig(cam.transform.position) - initialCameraInRig);

        // Artificial yaw only: the rig's horizontal heading change since Configure().
        float yawDelta = Mathf.DeltaAngle(initialRigYaw, RigYaw());
        Quaternion rotation = Quaternion.AngleAxis(yawDelta, Vector3.up) * initialPanelRotation;

        transform.SetPositionAndRotation(FromRig(panelInRig), rotation);
    }

    private Vector3 ToRig(Vector3 world) { return turnRig != null ? turnRig.InverseTransformPoint(world) : world; }
    private Vector3 FromRig(Vector3 local) { return turnRig != null ? turnRig.TransformPoint(local) : local; }

    // Horizontal heading (degrees about world up) of the rig; the last valid value is kept if the rig's forward is vertical.
    private float RigYaw()
    {
        if (turnRig == null) return 0f;
        Vector3 f = turnRig.forward;
        if (f.x * f.x + f.z * f.z < 1e-6f) return lastRigYaw;
        lastRigYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        return lastRigYaw;
    }

    // The transform that XRI turn providers rotate: XROrigin.Origin; for a non-XRI setup, ExperimentManager.playerRig if the
    // camera sits under it.
    private static Transform ResolveTurnRig(Camera cam)
    {
        XROrigin origin = cam.GetComponentInParent<XROrigin>();
        if (origin != null && origin.Origin != null) return origin.Origin.transform;

        ExperimentManager em = FindObjectOfType<ExperimentManager>();
        if (em != null && em.playerRig != null && cam.transform.IsChildOf(em.playerRig)) return em.playerRig;
        return null;
    }

    private Camera ActiveCamera()
    {
        if (headCamera != null && headCamera.isActiveAndEnabled) return headCamera;

        Camera main = Camera.main;
        if (main != null && main.isActiveAndEnabled)
        {
            if (enableDebugLogs) Debug.Log($"[HeadLockedPanel] '{name}' switched to camera '{main.name}'.", this);
            headCamera = main;
            return main;
        }
        return null;
    }
}
