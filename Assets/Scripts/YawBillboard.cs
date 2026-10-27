// YawBillboard.cs
//
// Turns a world-space sign about the vertical axis only, so its front faces the participant's HMD camera. Pitch and roll of
// the head never tilt the sign. Used for the river-point instruction signs created by RiverPointAvailability; it is a
// world-associated sign (it stays above its point), not a head-locked HUD.
//
// All comments in English per project convention.

using UnityEngine;

[DisallowMultipleComponent]
public class YawBillboard : MonoBehaviour
{
    private Camera cam;

    private void LateUpdate()
    {
        if (cam == null || !cam.isActiveAndEnabled) cam = Camera.main;
        if (cam == null) return;

        // Canvas front is visible from -Z looking along +Z, so the sign's forward points AWAY from the viewer.
        Vector3 away = transform.position - cam.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude < 1e-4f) return;
        transform.rotation = Quaternion.LookRotation(away, Vector3.up);
    }
}
