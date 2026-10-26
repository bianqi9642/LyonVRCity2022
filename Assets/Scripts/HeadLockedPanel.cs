// HeadLockedPanel.cs
//
// Keeps a UI panel at a fixed pose in the participant's VIEW (camera-relative), so its position in the headset's field of
// view does not change when the participant looks up, down or turns. Used only for the two small Experiment Phase
// reminder panels (Destination 1 / Destination 2 "shrunk" panels) created by UIManager.CreateAndAnchorShrunkPanel.
// Full-size interactive panels are NOT head-locked and never receive this component.
//
// Design
// ------
// - The panel is a child of the tracked HMD camera with a constant LOCAL pose. A camera child is exactly head-locked: its
//   pose in camera space (hence its viewport position) cannot depend on head pitch/yaw/roll, and Unity moves it with the
//   camera automatically, so no per-frame positioning and no render callbacks are needed.
// - UIManager calls Configure() once with the final pose. That is the single source of truth for the pose; this component
//   does nothing until it has been configured (an unconfigured component must never write a default pose - that was the
//   cause of the panels disappearing: AddComponent applied a default zero pose/unit scale before UIManager could set the
//   real values, and UIManager then read that default back as the "desired" pose).
// - LateUpdate is only a cheap guard: if the camera became inactive/was replaced it re-resolves it and re-parents once, and
//   if something else changed the local pose it restores it. In normal operation it writes nothing.
// - The Canvas, its raycasters, scale and appearance are untouched, so controller UI interaction behaves as before.
//
// All comments in English per project convention.

using UnityEngine;

[DisallowMultipleComponent]
public class HeadLockedPanel : MonoBehaviour
{
    [Tooltip("Log a line when the camera/parent had to be re-resolved or the pose restored (never per frame).")]
    public bool enableDebugLogs = false;

    private Camera headCamera;
    private Vector3 localPosition;
    private Quaternion localRotation = Quaternion.identity;
    private Vector3 localScale = Vector3.one;
    private bool configured;

    /// <summary>
    /// Sets the camera and the constant camera-local pose and applies it immediately. Until this is called the component
    /// leaves the transform alone.
    /// </summary>
    public void Configure(Camera camera, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        headCamera = camera;
        localPosition = position;
        localRotation = rotation;
        localScale = scale;
        configured = true;
        Apply(headCamera != null && headCamera.isActiveAndEnabled ? headCamera : ResolveCamera());
    }

    private void LateUpdate()
    {
        if (!configured) return;

        Camera cam = headCamera;
        if (cam == null || !cam.isActiveAndEnabled) cam = ResolveCamera();
        if (cam == null) return;

        if (transform.parent == cam.transform &&
            transform.localPosition == localPosition &&
            transform.localRotation == localRotation &&
            transform.localScale == localScale)
            return;   // nothing to do (the normal case)

        Apply(cam);
    }

    private void Apply(Camera cam)
    {
        if (cam == null) return;

        Transform camT = cam.transform;
        if (transform.parent != camT)
        {
            if (enableDebugLogs)
                Debug.Log($"[HeadLockedPanel] '{name}' re-parented to camera '{cam.name}'.", this);
            transform.SetParent(camT, false);
        }
        else if (enableDebugLogs && configured && transform.localPosition != localPosition)
        {
            Debug.Log($"[HeadLockedPanel] '{name}' local pose was changed externally; restored.", this);
        }

        headCamera = cam;
        transform.localPosition = localPosition;
        transform.localRotation = localRotation;
        transform.localScale = localScale;
    }

    private Camera ResolveCamera()
    {
        Camera main = Camera.main;
        if (main != null && main.isActiveAndEnabled) return main;

        Camera[] all = Camera.allCameras;
        for (int i = 0; i < all.Length; i++)
        {
            Camera c = all[i];
            if (c != null && c.isActiveAndEnabled && c.stereoTargetEye != StereoTargetEyeMask.None)
                return c;
        }
        return null;
    }
}
