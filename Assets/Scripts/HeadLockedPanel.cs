// HeadLockedPanel.cs
//
// Keeps a UI panel at a fixed pose in the participant's VIEW (camera-relative), so its position in the headset's field of
// view does not change when the participant looks up, down or turns. Used only for the two small Experiment Phase
// reminder panels (Destination 1 / Destination 2 "shrunk" panels) created by UIManager.CreateAndAnchorShrunkPanel.
// Full-size interactive panels are NOT head-locked and never receive this component.
//
// Design
// ------
// - The panel is a child of the tracked HMD camera with a constant LOCAL pose. That is exact head-locking: the pose in
//   camera space (hence the viewport position) never depends on head pitch/yaw/roll.
// - The pose is (re)applied in LateUpdate AND in Application.onBeforeRender. The XR pose driver updates the camera late in
//   the frame; applying the local pose right before rendering guarantees nothing else (a stale parent, another script, a
//   layout rebuild) can leave the panel at a different place in the view for a rendered frame.
// - The camera is resolved from the actually active, enabled camera under the rig (preferring the tracked stereo camera),
//   not from a stale reference; if the camera changes the panel is re-parented to it.
// - No raycasting, no Update() polling of the head forward vector: nothing here depends on where the head points.
// - The Canvas, its raycasters, scale and appearance are untouched, so controller UI interaction behaves as before.
//
// All comments in English per project convention.

using UnityEngine;

[DisallowMultipleComponent]
public class HeadLockedPanel : MonoBehaviour
{
    [Tooltip("Preferred camera (the HMD camera). If null/disabled the active tracked camera is looked up.")]
    public Camera headCamera;

    [Tooltip("Pose of the panel in the camera's local space (constant).")]
    public Vector3 localPosition = Vector3.zero;
    public Quaternion localRotation = Quaternion.identity;
    public Vector3 localScale = Vector3.one;

    [Tooltip("Log a line when the camera/parent had to be re-resolved (never per frame).")]
    public bool enableDebugLogs = false;

    private Transform lockedParent;

    private void OnEnable()
    {
        Application.onBeforeRender += ApplyPose;
        ApplyPose();
    }

    private void OnDisable()
    {
        Application.onBeforeRender -= ApplyPose;
    }

    private void LateUpdate()
    {
        ApplyPose();
    }

    private void ApplyPose()
    {
        Camera cam = ResolveCamera();
        if (cam == null) return;

        Transform camT = cam.transform;
        if (transform.parent != camT)
        {
            if (enableDebugLogs)
                Debug.Log($"[HeadLockedPanel] '{name}' re-parented to camera '{cam.name}'.", this);
            transform.SetParent(camT, false);
        }
        lockedParent = camT;

        transform.localPosition = localPosition;
        transform.localRotation = localRotation;
        transform.localScale = localScale;
    }

    private Camera ResolveCamera()
    {
        if (headCamera != null && headCamera.isActiveAndEnabled) return headCamera;

        // The parent we are currently locked to, if it is still an enabled camera.
        if (lockedParent != null)
        {
            Camera c = lockedParent.GetComponent<Camera>();
            if (c != null && c.isActiveAndEnabled) { headCamera = c; return c; }
        }

        Camera main = Camera.main;
        if (main != null && main.isActiveAndEnabled) { headCamera = main; return main; }

        Camera[] all = Camera.allCameras;
        for (int i = 0; i < all.Length; i++)
        {
            Camera c = all[i];
            if (c != null && c.isActiveAndEnabled && c.stereoTargetEye != StereoTargetEyeMask.None)
            {
                headCamera = c;
                return c;
            }
        }
        return null;
    }
}
