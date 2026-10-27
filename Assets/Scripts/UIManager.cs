using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>
/// UIManager specialized for the experiment with explicit slots per panel type.
///
/// IMPORTANT CHANGES:
/// - ShowPart3Destination1Panel / ShowPart3Destination2Panel simply show the full panel and return the PanelController.
///   They do NOT auto-create shrunk panels anymore.
/// - Use CreateAndAnchorShrunkPanel(...) to create a shrunk/corner widget (returns the instantiated GameObject).
///   This method parents the shrunk instance to the head/camera with worldPositionStays = false and sets local
///   transform so the size/position are stable and predictable. The shrunk corner widget is INTENTIONALLY
///   head-locked (it follows the participant) - this is a HUD reminder, not a full instruction panel.
/// - When instantiating a template, the manager finds PanelController on the clone or its children and passes either
///   a fallback message or null to preserve template text (existing behavior).
/// - Full instruction panels are positioned ONCE, in world space, in front of the participant's CURRENT tracked
///   head pose at the moment they are shown (see PositionPanelInFront). They are NOT parented to the head and do
///   NOT follow the participant while visible. The head transform is resolved live per show (ResolveHeadTransform),
///   never cached, so each show/hide/show cycle re-anchors to the participant's new viewpoint.
/// - PHASE 3 TARGET IMAGE: ExperimentManager calls PrepareDestinationImages(d1Building, d2Building) once, at
///   Phase 3 start, passing the exact GameObjects it randomly selected. UIManager asks TargetBuildingPreviewGenerator
///   to render each one to a Texture2D and then displays that texture on a RawImage inside the matching
///   Destination full panel AND its shrunk panel. UIManager never selects or randomizes a destination.
/// </summary>
public class UIManager : MonoBehaviour
{
    public enum Corner { TopRight, TopLeft, BottomRight, BottomLeft }

    [Header("Core references")]
    [Tooltip("Assign HMD camera (e.g. XR CenterEye). If null, Camera.main will be used at Awake.")]
    public Camera hmdCamera;

    [Header("Explicit panel templates (drag GameObjects here - templates, not necessarily prefabs)")]
    public GameObject part1StartPanelTemplate;
    public GameObject part1ContinuePanelTemplate;
    public GameObject part1EndPanelTemplate;

    public GameObject part2StartPanelTemplate;
    public GameObject part2ContinuePanelTemplate;
    public GameObject part2EndPanelTemplate;

    public GameObject part3StartPanelTemplate;
    public GameObject part3Dest1FullTemplate;
    public GameObject part3Dest1ShrunkTemplate;
    public GameObject part3Dest2FullTemplate;
    public GameObject part3Dest2ShrunkTemplate;
    public GameObject part3EndPanelTemplate;

    [Header("Placement settings")]
    [Tooltip("Horizontal distance (m) from the HMD to the panel. The instruction canvases are 750x450 px at scale 0.01 " +
             "(7.5 m x 4.5 m), so they are sized for roughly 5 m; a much smaller distance makes them fill the whole view.")]
    public float panelDistance = 1.2f;
    [Tooltip("Horizontal offset applied in the facing frame. Its Y component is ignored for full panels: vertical " +
             "placement comes from 'interactiveAreaEyeOffset' (the old -0.2 m Y offset put only the panel CENTRE near " +
             "eye level, which left the buttons of a 4.5 m tall panel about 1.7 m below the eyes, i.e. below the floor).")]
    public Vector3 panelLocalOffset = new Vector3(0f, -0.2f, 0f);

    [Tooltip("Vertical position of the panel's interactive area (the centre of its enabled Buttons) relative to the " +
             "HMD eye height, in metres. 0 = eye level; negative = slightly below. Measured from the real button " +
             "rectangles at show time, so panel size, pivot and layout are accounted for.")]
    public float interactiveAreaEyeOffset = -0.1f;

    [Header("Head-anchored placement (all instruction canvases)")]
    [Tooltip("Explicit tracked-HMD transform used as the placement reference for every instruction panel. " +
             "Leave empty to fall back to 'hmdCamera', then Camera.main, then any active Camera.")]
    public Transform headTransformOverride;

    [Tooltip("Legacy flag, kept so existing scene references survive. Full panels are now ALWAYS placed on the " +
             "participant's horizontal heading (head yaw); pitch and roll never affect where a panel appears.")]
    public bool placeUsingHeadYawOnly = true;

    [Header("Corner (shrunk) placement settings")]
    [Tooltip("Local position relative to camera for the TopRight corner (camera local space).")]
    public Vector3 topRightLocalPos = new Vector3(0.4f, 0.2f, 1.0f);

    [Tooltip("Local scale to apply to shrunk (corner) panel instances.")]
    public Vector3 shrunkLocalScale = new Vector3(0.22f, 0.22f, 0.22f);

    [Header("Phase 3 target-building preview")]
    [Tooltip("Generator that renders an isolated image of the selected destination building. " +
             "If left empty it is located with FindObjectOfType at Awake.")]
    public TargetBuildingPreviewGenerator targetPreviewGenerator;

    [Header("Head pose readiness (full-size panels)")]
    [Tooltip("A full panel is positioned only once the HMD pose is valid and its height has been steady for this many " +
             "consecutive frames. Without this the first Start panel (shown in the first frames of a run) used a temporary " +
             "pre-tracking / still-settling pose and could end up too low.")]
    public int settledFramesRequired = 8;
    [Tooltip("Largest frame-to-frame change in head height (m) still counted as steady (tracking noise is ~1 mm).")]
    public float settledMaxHeightDelta = 0.003f;
    [Tooltip("Longest time (s, real time) a panel waits for a steady valid pose before being placed with the current pose.")]
    public float settleTimeout = 3f;

    [Header("Debug")]
    [Tooltip("Log one line per full-panel placement (never per frame).")]
    public bool enableDebugLogs = false;

    // Head pose sampling (updated once per frame, see SampleHead).
    private Transform sampledHead;
    private float sampledHeadY;
    private int stableFrames;
    private bool headTracked;
    private int lastSampleFrame = -1;

    // Runtime images for the current Phase 3 session. Owned by this component; destroyed on regeneration and OnDestroy.
    private Texture2D destination1Image;
    private Texture2D destination2Image;

    private void Awake()
    {
        if (hmdCamera == null)
        {
            hmdCamera = Camera.main;
            if (hmdCamera == null)
            {
                Debug.LogWarning("UIManager: HMD camera not assigned and Camera.main is null at Awake. " +
                                 "A head transform will be resolved on demand when a panel is shown.");
            }
        }

        if (targetPreviewGenerator == null)
        {
            targetPreviewGenerator = FindObjectOfType<TargetBuildingPreviewGenerator>();
        }
    }

    private void OnDestroy()
    {
        DestroyDestinationImages();
    }

    private void Update()
    {
        SampleHead();
    }

    #region Head pose readiness

    /// <summary>True when the HMD pose is valid and its height has been steady for 'settledFramesRequired' frames.</summary>
    public bool IsHeadPoseSettled
    {
        get
        {
            SampleHead();
            return headTracked && stableFrames >= settledFramesRequired;
        }
    }

    // Once per frame: is the head tracked, and has its world height stopped changing? "Steady" also covers a rig that is
    // still being grounded/teleported (AutoGroundAlign lerp, gravity), not only XR tracking start-up.
    private void SampleHead()
    {
        if (lastSampleFrame == Time.frameCount) return;
        lastSampleFrame = Time.frameCount;

        Transform head = ResolveHeadTransform(false);
        if (head == null) { stableFrames = 0; headTracked = false; return; }

        headTracked = ReadHeadTracked(false, out _);
        float y = head.position.y;
        if (head != sampledHead) { sampledHead = head; stableFrames = 0; }
        else if (headTracked && Mathf.Abs(y - sampledHeadY) <= settledMaxHeightDelta) stableFrames++;
        else stableFrames = 0;
        sampledHeadY = y;
    }

    // Tracking validity from the XR input subsystem. No active XR device (Editor/desktop) counts as valid. If a device is
    // active but does not report the head node, validity cannot be determined and the steady-height test alone decides.
    private static bool ReadHeadTracked(bool describe, out string state)
    {
        state = null;
        if (!XRSettings.isDeviceActive)
        {
            if (describe) state = "no active XR device (desktop/Editor)";
            return true;
        }

        InputDevice device = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!device.isValid)
        {
            if (describe) state = "XR device active, head node not reported (validity unknown)";
            return true;
        }

        if (device.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState ts))
        {
            if (describe) state = "trackingState=" + ts;
            return (ts & InputTrackingState.Position) != 0;
        }
        if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked))
        {
            if (describe) state = "isTracked=" + tracked;
            return tracked;
        }
        if (describe) state = "head device valid, tracking feature unavailable";
        return true;
    }

    #endregion

    /// <summary>
    /// Resolve the participant's CURRENT tracked head transform at the moment a panel is shown.
    /// Priority: explicit headTransformOverride -> hmdCamera -> Camera.main -> first active Camera in the scene.
    /// Resolved live (never cached) so a late-spawned or re-wired rig is still picked up, and so each
    /// show/hide/show cycle re-anchors to wherever the participant is actually looking.
    /// Returns null if nothing usable exists (callers must handle this without breaking the experiment).
    /// </summary>
    private Transform ResolveHeadTransform(bool warn = true)
    {
        if (headTransformOverride != null) return headTransformOverride;
        if (hmdCamera != null && hmdCamera.isActiveAndEnabled) return hmdCamera.transform;

        Camera cam = Camera.main;
        if (cam == null)
        {
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].isActiveAndEnabled)
                {
                    cam = all[i];
                    break;
                }
            }
        }

        if (cam != null)
        {
            hmdCamera = cam; // remember for next time; does not overwrite an explicit inspector assignment
            return cam.transform;
        }

        if (warn)
            Debug.LogWarning("UIManager.ResolveHeadTransform: no head/camera transform available. " +
                             "Panel will be left at its instantiated pose.");
        return null;
    }

    /// <summary>
    /// True while a full instruction panel (Start / Continue / End / Destination) instantiated by this manager is
    /// open. Full panels are active children of this transform that carry a PanelController; the inactive scene
    /// templates and the head-anchored shrunk HUD copies (re-parented to the head) are not counted.
    /// Read-only; used by GuardedTeleportationProvider to refuse teleports while the participant must respond.
    /// </summary>
    public bool HasOpenFullPanel
    {
        get
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child.gameObject.activeSelf && child.GetComponentInChildren<PanelController>() != null)
                    return true;
            }
            return false;
        }
    }

    #region Phase 3 target-image API

    /// <summary>
    /// Called ONCE by ExperimentManager at Phase 3 start, with the exact GameObjects it randomly selected
    /// for Destination 1 and Destination 2 (both still inactive / off-screen at this point). UIManager
    /// renders a clean isolated image of each and holds them for the D1/D2 full + shrunk panels.
    /// UIManager performs NO selection or randomization of its own.
    /// </summary>
    public void PrepareDestinationImages(GameObject destination1Building, GameObject destination2Building)
    {
        DestroyDestinationImages();

        if (targetPreviewGenerator == null)
        {
            targetPreviewGenerator = FindObjectOfType<TargetBuildingPreviewGenerator>();
        }

        if (targetPreviewGenerator == null)
        {
            Debug.LogWarning("UIManager.PrepareDestinationImages: no TargetBuildingPreviewGenerator found. " +
                             "Destination panels will show text only.");
            return;
        }

        destination1Image = (destination1Building != null) ? targetPreviewGenerator.CapturePreview(destination1Building) : null;
        destination2Image = (destination2Building != null) ? targetPreviewGenerator.CapturePreview(destination2Building) : null;
    }

    private void DestroyDestinationImages()
    {
        if (destination1Image != null) { Destroy(destination1Image); destination1Image = null; }
        if (destination2Image != null) { Destroy(destination2Image); destination2Image = null; }
    }

    /// <summary>
    /// Find the first RawImage in the instantiated panel (the one added to the destination templates) and
    /// show 'tex' on it. If 'tex' is null the RawImage GameObject is hidden so no broken white box appears.
    /// </summary>
    private void ApplyDestinationImage(GameObject panelInstance, Texture2D tex)
    {
        if (panelInstance == null) return;

        RawImage raw = panelInstance.GetComponentInChildren<RawImage>(true);
        if (raw == null)
        {
            if (tex != null)
                Debug.LogWarning("UIManager.ApplyDestinationImage: panel '" + panelInstance.name +
                                 "' has no RawImage child; add one to the destination panel template.");
            return;
        }

        if (tex != null)
        {
            raw.texture = tex;
            raw.color = Color.white;
            if (!raw.gameObject.activeSelf) raw.gameObject.SetActive(true);
        }
        else
        {
            raw.gameObject.SetActive(false);
        }
    }

    #endregion

    #region Part 1 APIs

    public void ShowPart1StartPanel(Action onStart)
    {
        ShowFullPanel(part1StartPanelTemplate, "Press Start to begin.", onStart);
    }

    public void ShowPart1ContinuePanel(Action onContinue)
    {
        ShowFullPanel(part1ContinuePanelTemplate, "Continue (confirm)", onContinue);
    }

    public void ShowPart1EndPanel(Action onConfirm)
    {
        ShowFullPanel(part1EndPanelTemplate, "This stage has ended. Press confirm to continue.", onConfirm);
    }

    #endregion

    #region Part 2 APIs

    public void ShowPart2StartPanel(Action onStart)
    {
        ShowFullPanel(part2StartPanelTemplate, "Press Start to begin.", onStart);
    }

    public void ShowPart2ContinuePanel(Action onContinue)
    {
        ShowFullPanel(part2ContinuePanelTemplate, "Press Teleport to continue.", onContinue);
    }

    public void ShowPart2EndPanel(Action onConfirm)
    {
        ShowFullPanel(part2EndPanelTemplate, "This stage has ended. Press confirm to continue.", onConfirm);
    }

    #endregion

    #region Part 3 APIs

    public void ShowPart3StartPanel(Action onStart)
    {
        ShowFullPanel(part3StartPanelTemplate, "Press Start to begin.", onStart);
    }

    /// <summary>
    /// Show the Destination 1 full panel (with the prepared D1 building image) and return its PanelController.
    /// UIManager will NOT auto-create the shrunk panel. Caller calls CreateAndAnchorShrunkPanel when needed.
    /// </summary>
    public PanelController ShowPart3Destination1Panel(Action onConfirm)
    {
        return ShowFullPanelAndReturnController(part3Dest1FullTemplate, "Destination 1", onConfirm, destination1Image);
    }

    /// <summary>
    /// Show the Destination 2 full panel (with the prepared D2 building image) and return its PanelController.
    /// </summary>
    public PanelController ShowPart3Destination2Panel(Action onConfirm)
    {
        return ShowFullPanelAndReturnController(part3Dest2FullTemplate, "Destination 2", onConfirm, destination2Image);
    }

    public void ShowPart3EndPanel(Action onConfirm)
    {
        ShowFullPanel(part3EndPanelTemplate, "This stage has ended. Press confirm to continue.", onConfirm);
    }

    #endregion

    #region Core implementation helpers

    /// <summary>
    /// Instantiate the provided template GameObject (must not be null) and position it in front of the camera.
    /// This helper does not return the PanelController.
    /// </summary>
    private void ShowFullPanel(GameObject template, string fallbackMessage, Action onConfirm)
    {
        if (template == null)
        {
            Debug.LogWarning("UIManager.ShowFullPanel: template is null. No panel shown.");
            onConfirm?.Invoke(); // avoid deadlock if missing
            return;
        }

        GameObject instance = InstantiateTemplate(template);
        if (instance == null) return;

        // Try to find a PanelController on the instance itself or in its children.
        PanelController pc = instance.GetComponent<PanelController>();
        if (pc == null) pc = instance.GetComponentInChildren<PanelController>();

        if (pc == null)
        {
            Debug.LogWarning("UIManager.ShowFullPanel: instantiated panel has no PanelController component (search included children).");
            PlaceFullPanel(instance.transform);
            return;
        }

        // Determine whether to preserve template text.
        bool templateHasText = false;
        if (pc.messageTextUI != null && !string.IsNullOrEmpty(pc.messageTextUI.text)) templateHasText = true;
        else if (pc.messageTextTMP != null && !string.IsNullOrEmpty(pc.messageTextTMP.text)) templateHasText = true;

        string textToPass = templateHasText ? null : fallbackMessage;

        // Wire up the confirm callback; PanelController will destroy the instance on confirm
        pc.Setup(textToPass, onConfirm, showCancel: false);

        // position the panel in front of the HMD
        PlaceFullPanel(instance.transform);
    }

    /// <summary>
    /// Instantiate the provided template and return the PanelController so the caller can handle the confirm callback.
    /// The created full panel is positioned in front of the camera. If 'destinationImage' is non-null it is applied
    /// to the first RawImage in the instantiated panel (used by the Phase 3 destination panels).
    /// </summary>
    private PanelController ShowFullPanelAndReturnController(GameObject template, string fallbackMessage, Action onConfirm, Texture2D destinationImage = null)
    {
        if (template == null)
        {
            Debug.LogWarning("UIManager.ShowFullPanelAndReturnController: template is null.");
            onConfirm?.Invoke();
            return null;
        }

        GameObject instance = InstantiateTemplate(template);
        if (instance == null) return null;

        PanelController pc = instance.GetComponent<PanelController>();
        if (pc == null) pc = instance.GetComponentInChildren<PanelController>();

        if (pc == null)
        {
            Debug.LogWarning("UIManager.ShowFullPanelAndReturnController: no PanelController found.");
            ApplyDestinationImage(instance, destinationImage);
            PlaceFullPanel(instance.transform);
            onConfirm?.Invoke();
            return null;
        }

        bool templateHasText = false;
        if (pc.messageTextUI != null && !string.IsNullOrEmpty(pc.messageTextUI.text)) templateHasText = true;
        else if (pc.messageTextTMP != null && !string.IsNullOrEmpty(pc.messageTextTMP.text)) templateHasText = true;

        string textToPass = templateHasText ? null : fallbackMessage;

        pc.Setup(textToPass, onConfirm, showCancel: false);
        ApplyDestinationImage(instance, destinationImage);   // before placement: it can change what is active in the layout
        PlaceFullPanel(instance.transform);
        return pc;
    }

    /// <summary>
    /// Instantiate a runtime copy of the template GameObject and parent it under this UIManager GameObject.
    /// Returns the instantiated GameObject (active).
    /// </summary>
    private GameObject InstantiateTemplate(GameObject template)
    {
        if (template == null) return null;

        // Instantiate a copy under this transform for organization.
        GameObject instance = Instantiate(template, this.transform);
        instance.SetActive(true); // ensure active (templates might be deactivated in scene)
        return instance;
    }

    /// <summary>
    /// Create a shrunk/corner panel from a shrunk template, anchor it to the participant's head/camera and return
    /// the instance. The returned GameObject is parented to the head transform and must be destroyed by the caller
    /// when no longer needed. This corner widget INTENTIONALLY follows the head (HUD reminder) - unlike the full
    /// instruction panels, which are world-stationary.
    /// If the shrunk template is one of the Phase 3 destination-shrunk templates, the matching prepared destination
    /// image is applied to the first RawImage in the instance so the shrunk HUD shows the same picture as the full panel.
    /// Note: we parent with worldPositionStays=false so we can set localPosition/localScale reliably.
    /// </summary>
    public GameObject CreateAndAnchorShrunkPanel(GameObject shrunkTemplate, Corner corner)
    {
        if (shrunkTemplate == null)
        {
            Debug.LogWarning("UIManager.CreateAndAnchorShrunkPanel: shrunkTemplate is null.");
            return null;
        }

        Transform head = ResolveHeadTransform();
        if (head == null)
        {
            Debug.LogWarning("UIManager.CreateAndAnchorShrunkPanel: no head transform resolved. Shrunk panel not created.");
            return null;
        }

        // Instantiate under UIManager first (HeadLockedPanel.Configure detaches it right away).
        GameObject instance = Instantiate(shrunkTemplate, this.transform);
        instance.SetActive(true);

        // Offset (lateral, height, horizontal distance) for the chosen corner, computed BEFORE any component can touch the transform.
        Vector3 localPos;
        switch (corner)
        {
            case Corner.TopLeft:    localPos = new Vector3(-topRightLocalPos.x, topRightLocalPos.y, topRightLocalPos.z); break;
            case Corner.BottomRight: localPos = new Vector3(topRightLocalPos.x, -topRightLocalPos.y, topRightLocalPos.z); break;
            case Corner.BottomLeft: localPos = new Vector3(-topRightLocalPos.x, -topRightLocalPos.y, topRightLocalPos.z); break;
            default:                localPos = topRightLocalPos; break;   // TopRight
        }

        // HeadLockedPanel is the single owner of this panel's pose. It follows the camera's position and horizontal heading
        // (yaw) but ignores pitch and roll: x = lateral offset, y = height above the camera, z = horizontal distance. It
        // does nothing until configured and detaches the panel from UIManager (so it never counts as an open full panel).
        // Only the two shrunk Destination panels are created here; the full-size panels never get this component.
        HeadLockedPanel lockPanel = instance.GetComponent<HeadLockedPanel>();
        if (lockPanel == null) lockPanel = instance.AddComponent<HeadLockedPanel>();
        lockPanel.enableDebugLogs = enableDebugLogs;
        lockPanel.Configure(head.GetComponent<Camera>(), localPos, shrunkLocalScale);

        if (enableDebugLogs)
        {
            float elevation = Mathf.Atan2(localPos.y, localPos.z) * Mathf.Rad2Deg;
            Debug.Log($"[UIManager] Shrunk panel '{shrunkTemplate.name}' follows camera '{head.name}' in yaw only at offset {localPos} " +
                      $"(lateral, height, horizontal distance), scale {shrunkLocalScale} ({elevation:F1} deg above the level line of sight); " +
                      $"active={instance.activeInHierarchy}.", this);
        }

        // Show the matching destination image on the shrunk HUD (same texture as the full panel).
        if (shrunkTemplate == part3Dest1ShrunkTemplate) ApplyDestinationImage(instance, destination1Image);
        else if (shrunkTemplate == part3Dest2ShrunkTemplate) ApplyDestinationImage(instance, destination2Image);

        return instance;
    }

    /// <summary>
    /// Places a full instruction panel. If the HMD pose is already valid and steady (every panel after the first frames of
    /// a run) it is positioned immediately. Otherwise - typically the first Start panel, shown in the first frames while XR
    /// tracking starts and the rig is still being grounded - the panel's Canvas is hidden, the placement waits for a valid
    /// steady pose (bounded by 'settleTimeout'), and only then is the panel positioned and shown. It is positioned exactly
    /// once and never moves afterwards.
    /// </summary>
    private void PlaceFullPanel(Transform panel)
    {
        if (panel == null) return;

        if (IsHeadPoseSettled)
        {
            PositionPanelInFront(panel, panelLocalOffset, "immediate");
            return;
        }

        StartCoroutine(PlaceWhenSettled(panel));
    }

    private IEnumerator PlaceWhenSettled(Transform panel)
    {
        // Hide (not deactivate) the root canvases: the panel stays "open" for HasOpenFullPanel but cannot be seen or hit.
        Canvas[] canvases = panel.GetComponentsInChildren<Canvas>(true);
        bool[] wasEnabled = new bool[canvases.Length];
        for (int i = 0; i < canvases.Length; i++)
        {
            wasEnabled[i] = canvases[i].enabled;
            if (canvases[i].isRootCanvas) canvases[i].enabled = false;
        }

        float start = Time.unscaledTime;
        while (panel != null && !IsHeadPoseSettled && Time.unscaledTime - start < settleTimeout)
            yield return null;

        if (panel == null) yield break;   // destroyed while waiting

        bool timedOut = !IsHeadPoseSettled;
        PositionPanelInFront(panel, panelLocalOffset, timedOut ? "after settle TIMEOUT" : $"after settle ({Time.unscaledTime - start:F2} s)");

        for (int i = 0; i < canvases.Length; i++)
            if (canvases[i] != null) canvases[i].enabled = wasEnabled[i];
    }

    /// <summary>
    /// Position a full instruction panel ONCE, in world space, in front of the participant's CURRENT head pose, using the
    /// horizontal heading only (pitch/roll never move it) and the centre of its enabled Buttons as the height reference.
    /// The panel is NOT parented to the head, so it stays fixed in the world for as long as it is visible. 'localOffset'
    /// (panelLocalOffset) is applied in the facing frame (horizontal components only).
    /// </summary>
    private void PositionPanelInFront(Transform panelTransform, Vector3 localOffset, string phase)
    {
        if (panelTransform == null)
        {
            Debug.LogWarning("UIManager.PositionPanelInFront: panelTransform is null.");
            return;
        }

        Transform head = ResolveHeadTransform();
        if (head == null)
        {
            Debug.LogWarning("UIManager.PositionPanelInFront: no head transform resolved. " +
                             "Panel left at its instantiated pose.");
            return;
        }

        // Horizontal heading only: pitch/roll of the head must never move a newly shown panel up or down.
        Vector3 forward = head.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-6f)
        {
            // Participant looking straight up/down - fall back to head-space up projected to horizontal.
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
        }
        forward.Normalize();

        Quaternion facing = Quaternion.LookRotation(forward, Vector3.up);

        // Orientation first, then a provisional position at eye height. The layout is rebuilt AFTER the panel is active and
        // oriented, so button rectangles are measured in their final layout, then the height is corrected from them.
        Vector3 horizontalOffset = facing * new Vector3(localOffset.x, 0f, localOffset.z);
        Vector3 worldPos = head.position + forward * panelDistance + horizontalOffset;
        panelTransform.SetPositionAndRotation(worldPos, facing);

        float eyeY = head.position.y;
        float targetY = eyeY + interactiveAreaEyeOffset;
        bool measured = TryGetInteractiveAreaCentre(panelTransform, out Vector3 areaCentre, out _);
        float before = measured ? areaCentre.y : worldPos.y;
        worldPos.y += targetY - before;
        panelTransform.position = worldPos;

        if (enableDebugLogs)
        {
            bool afterMeasured = TryGetInteractiveAreaCentre(panelTransform, out Vector3 afterCentre, out float afterBottom);
            ReadHeadTracked(true, out string trackingDesc);
            float floorY = head.root.position.y;   // rig origin (floor) height
            bool usable = afterMeasured && afterBottom - floorY >= 0.3f && Mathf.Abs(afterCentre.y - eyeY) <= 0.6f;
            string msg = $"[UIManager] Panel '{panelTransform.name}' placed ({phase}): camera='{head.name}' pose[{trackingDesc}, steadyFrames={stableFrames}] " +
                         $"headPos={head.position} heading={Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg:F0} deg; " +
                         $"buttonCentreY before={before:F2} after={(afterMeasured ? afterCentre.y : float.NaN):F2} (target {targetY:F2}); " +
                         $"buttonBottom above floor={(afterMeasured ? afterBottom - floorY : float.NaN):F2} m; canvasPos={panelTransform.position}; " +
                         $"parent={(panelTransform.parent != null ? panelTransform.parent.name : "none")} lossyScale={panelTransform.lossyScale}.";
            if (usable) Debug.Log(msg, this);
            else Debug.LogWarning(msg + " Interactive area is NOT within the expected usable height range.", this);
        }
    }

    /// <summary>
    /// World-space centre of the union of the rectangles of all enabled, interactable Buttons under the panel.
    /// Forces a layout rebuild first so Layout Groups have already positioned the children. Returns false if the panel
    /// has no such Button (callers then fall back to the panel origin).
    /// </summary>
    private bool TryGetInteractiveAreaCentre(Transform panelTransform, out Vector3 centre, out float bottomY)
    {
        centre = panelTransform.position;
        bottomY = centre.y;

        RectTransform panelRect = panelTransform as RectTransform;
        if (panelRect != null) LayoutRebuilder.ForceRebuildLayoutImmediate(panelRect);

        Button[] buttons = panelTransform.GetComponentsInChildren<Button>(false);
        bool any = false;
        Bounds bounds = new Bounds();
        Vector3[] corners = new Vector3[4];

        for (int i = 0; i < buttons.Length; i++)
        {
            Button b = buttons[i];
            if (b == null || !b.isActiveAndEnabled || !b.interactable) continue;
            RectTransform rt = b.transform as RectTransform;
            if (rt == null) continue;

            rt.GetWorldCorners(corners);
            for (int c = 0; c < 4; c++)
            {
                if (!any) { bounds = new Bounds(corners[c], Vector3.zero); any = true; }
                else bounds.Encapsulate(corners[c]);
            }
        }

        if (any) { centre = bounds.center; bottomY = bounds.min.y; }
        return any;
    }

    #endregion
}