// HeadLockedPanel.cs
//
// Hybrid "follow yaw, ignore pitch" placement for the two small Experiment Phase reminder panels (Destination 1 / 2 "shrunk"
// panels created by UIManager.CreateAndAnchorShrunkPanel). Full-size interactive panels never get this component.
//
// Why not a child of the camera (the previous version)
//   A child of the camera inherits its FULL rotation. The configured offset (0, 2, 5) is rotated with the head, so the panel
//   sat at a constant place in the view at every pitch - it followed pitch and roll as well as yaw.
//
// What it does now (the panel is NOT parented to the camera)
//   forward  = camera.forward projected onto the horizontal plane (yaw only); if the head looks (almost) straight up or down
//              the projection is ~zero, and the last valid horizontal forward is used instead (never NaN, never a jump).
//   position = camera.position + forward * distance + right * lateral + world-up * height
//   rotation = LookRotation(forward, world-up)      -> upright, facing along the heading; the Canvas front is readable from
//                                                      behind it, the same convention UIManager uses for full-size panels.
//   * yaw   : forward turns with the head, so the panel keeps its horizontal place in the view.
//   * pitch : ignored by construction; looking up/down moves the panel up/down in the view (intended).
//   * roll  : ignored (rotation is built from a horizontal forward and world up).
//   * head translation: the position follows camera.position, keeping the configured distance and height.
//
// Update stage: the pose is applied in Application.onBeforeRender. The XR pose driver moves the camera late in the frame
// (Update and Before Render); this component subscribes after it (scene load) so it reads the final camera pose of the frame
// that is about to be rendered. Applying in LateUpdate would use the previous frame's tracking pose. Work per frame: one
// projection and one SetPositionAndRotation; no allocation, no logging.
//
// The panel is detached from UIManager's transform: UIManager.HasOpenFullPanel counts PanelController children of UIManager
// as "open full panels" (which blocks generic teleports), and this HUD must not count.
//
// All comments in English per project convention.

using UnityEngine;

[DisallowMultipleComponent]
public class HeadLockedPanel : MonoBehaviour
{
    [Tooltip("Log a line when the camera had to be re-resolved (never per frame).")]
    public bool enableDebugLogs = false;

    private Camera headCamera;
    private float lateral, height, distance;     // metres: along horizontal right / world up / horizontal forward
    private Vector3 worldScale = Vector3.one;
    private Vector3 lastForward = Vector3.forward;
    private bool configured;

    /// <summary>
    /// Sets the reference camera and the offsets (x = lateral along the horizontal right, y = height above the camera along
    /// world up, z = horizontal distance in the heading direction) and the panel scale, and applies them once. Until this is
    /// called the component leaves the transform alone.
    /// </summary>
    public void Configure(Camera camera, Vector3 offset, Vector3 scale)
    {
        headCamera = camera;
        lateral = offset.x; height = offset.y; distance = offset.z;
        worldScale = scale;

        if (transform.parent != null) transform.SetParent(null, worldPositionStays: true);
        transform.localScale = worldScale;
        configured = true;

        Camera cam = ActiveCamera();
        if (cam != null)
        {
            Vector3 f = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (f.sqrMagnitude > 1e-4f) lastForward = f.normalized;
        }
        Apply();
    }

    private void OnEnable() { Application.onBeforeRender += Apply; }
    private void OnDisable() { Application.onBeforeRender -= Apply; }

    private void Apply()
    {
        if (!configured) return;
        Camera cam = ActiveCamera();
        if (cam == null) return;

        Transform c = cam.transform;
        Vector3 f = Vector3.ProjectOnPlane(c.forward, Vector3.up);
        if (f.sqrMagnitude > 1e-4f) lastForward = f.normalized;     // otherwise keep the last valid heading
        Vector3 fwd = lastForward;
        Vector3 right = Vector3.Cross(Vector3.up, fwd);              // horizontal right of the heading

        Vector3 pos = c.position + fwd * distance + right * lateral + Vector3.up * height;
        transform.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd, Vector3.up));
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
