// HeadLockedPanel.cs
//
// Translation-only follow for the two small Experiment Phase reminder panels (Destination 1 / 2 "shrunk" panels created by
// UIManager.CreateAndAnchorShrunkPanel). Full-size interactive panels never get this component.
//
// Behaviour
//   * At Configure() the panel is placed once at its intended start position, using the existing convention: from the camera,
//     along the camera's HORIZONTAL heading at that moment - distance (z) ahead, lateral (x) to the right, height (y) up.
//     At a level head this is the old camera-local offset (0, 2, 5). The panel's world rotation is set once to face that
//     heading (upright) and then NEVER changes.
//   * The pose at that moment is the reference: initialCameraPosition, initialPanelPosition, initialPanelRotation.
//   * Afterwards, every frame:
//         panel.position = initialPanelPosition + (camera.position - initialCameraPosition)
//         panel.rotation = initialPanelRotation
//     so the panel moves by exactly the camera's world-space displacement on X, Y and Z, and head yaw, pitch and roll have no
//     effect on it at all (no billboarding, no view-locking, no yaw-following). Turning the head can therefore move the panel
//     across the field of view or out of it; that is the intended design.
//   * The reference is captured once, in Configure(), and never re-captured, so there is no drift. The panel is not parented to
//     the camera (it would inherit its rotation) and not to UIManager (UIManager.HasOpenFullPanel counts its PanelController
//     children as open full panels and would block generic teleports).
//
// Update stage: Application.onBeforeRender. The XR pose driver moves the camera late in the frame (Update and Before Render); this
// component subscribes after it, so it reads the final camera pose of the frame about to be rendered (LateUpdate would use the
// previous frame's tracking pose and the panel would lag the head). The subscription is made in OnEnable and removed in
// OnDisable, so each instance has exactly one callback and none survives its destruction. Per frame: one subtraction and one
// SetPositionAndRotation; no allocation, no logging.
//
// All comments in English per project convention.

using UnityEngine;

[DisallowMultipleComponent]
public class HeadLockedPanel : MonoBehaviour
{
    [Tooltip("Log a line when the camera had to be re-resolved (never per frame).")]
    public bool enableDebugLogs = false;

    private Camera headCamera;
    private Vector3 initialCameraPosition;
    private Vector3 initialPanelPosition;
    private Quaternion initialPanelRotation = Quaternion.identity;
    private bool configured;

    /// <summary>
    /// Places the panel at its start position (offset: x = lateral to the right, y = height above the camera, z = distance along
    /// the camera's horizontal heading), fixes its world rotation to that heading, captures the reference pose and scale, and
    /// starts the translation-only follow. Until this is called the component leaves the transform alone.
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

        initialCameraPosition = c.position;
        initialPanelPosition = c.position + fwd * offset.z + right * offset.x + Vector3.up * offset.y;
        initialPanelRotation = Quaternion.LookRotation(fwd, Vector3.up);

        if (transform.parent != null) transform.SetParent(null, worldPositionStays: true);
        transform.localScale = scale;
        transform.SetPositionAndRotation(initialPanelPosition, initialPanelRotation);
        configured = true;
    }

    private void OnEnable() { Application.onBeforeRender += Follow; }
    private void OnDisable() { Application.onBeforeRender -= Follow; }

    private void Follow()
    {
        if (!configured) return;
        Camera cam = ActiveCamera();
        if (cam == null) return;
        transform.SetPositionAndRotation(initialPanelPosition + (cam.transform.position - initialCameraPosition), initialPanelRotation);
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
