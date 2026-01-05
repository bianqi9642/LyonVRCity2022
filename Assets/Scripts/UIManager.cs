using System;
using UnityEngine;

/// <summary>
/// UIManager specialized for the experiment with explicit slots per panel type.
/// 
/// IMPORTANT CHANGES:
/// - ShowPart3Destination1Panel / ShowPart3Destination2Panel simply show the full panel and return the PanelController.
///   They do NOT auto-create shrunk panels anymore.
/// - Use CreateAndAnchorShrunkPanel(...) to create a shrunk/corner widget (returns the instantiated GameObject).
///   This method parents the shrunk instance to the camera with worldPositionStays = false and sets local transform
///   so the size/position are stable and predictable.
/// - When instantiating a template, the manager finds PanelController on the clone or its children and passes either
///   a fallback message or null to preserve template text (existing behavior).
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
    public float panelDistance = 1.2f;
    public Vector3 panelLocalOffset = new Vector3(0f, -0.2f, 0f);

    [Header("Corner (shrunk) placement settings")]
    [Tooltip("Local position relative to camera for the TopRight corner (camera local space).")]
    public Vector3 topRightLocalPos = new Vector3(0.4f, 0.2f, 1.0f);

    [Tooltip("Local scale to apply to shrunk (corner) panel instances.")]
    public Vector3 shrunkLocalScale = new Vector3(0.22f, 0.22f, 0.22f);

    // cached transform of the camera (CenterEye)
    private Transform cameraTransform;

    private void Awake()
    {
        if (hmdCamera == null)
        {
            hmdCamera = Camera.main;
            if (hmdCamera == null)
            {
                Debug.LogWarning("UIManager: HMD camera not assigned and Camera.main is null. Panels will not position correctly.");
            }
        }

        if (hmdCamera != null) cameraTransform = hmdCamera.transform;
    }

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
    /// Show the Destination 1 full panel and return its PanelController for the caller to provide callback.
    /// Note: UIManager will NOT auto-create the shrunk panel now. Caller should call CreateAndAnchorShrunkPanel when needed.
    /// </summary>
    public PanelController ShowPart3Destination1Panel(Action onConfirm)
    {
        return ShowFullPanelAndReturnController(part3Dest1FullTemplate, "Destination 1", onConfirm);
    }

    /// <summary>
    /// Show the Destination 2 full panel and return its PanelController for the caller to provide callback.
    /// </summary>
    public PanelController ShowPart3Destination2Panel(Action onConfirm)
    {
        return ShowFullPanelAndReturnController(part3Dest2FullTemplate, "Destination 2", onConfirm);
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
            PositionPanelInFront(instance.transform, panelLocalOffset);
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
        PositionPanelInFront(instance.transform, panelLocalOffset);
    }

    /// <summary>
    /// Instantiate the provided template and return the PanelController so the caller can handle the confirm callback.
    /// The created full panel is positioned in front of the camera.
    /// </summary>
    private PanelController ShowFullPanelAndReturnController(GameObject template, string fallbackMessage, Action onConfirm)
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
            PositionPanelInFront(instance.transform, panelLocalOffset);
            onConfirm?.Invoke();
            return null;
        }

        bool templateHasText = false;
        if (pc.messageTextUI != null && !string.IsNullOrEmpty(pc.messageTextUI.text)) templateHasText = true;
        else if (pc.messageTextTMP != null && !string.IsNullOrEmpty(pc.messageTextTMP.text)) templateHasText = true;

        string textToPass = templateHasText ? null : fallbackMessage;

        pc.Setup(textToPass, onConfirm, showCancel: false);
        PositionPanelInFront(instance.transform, panelLocalOffset);
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
    /// Create a shrunk/corner panel from a shrunk template, anchor it to the camera and return the instance.
    /// The returned GameObject is parented to the camera and must be destroyed by the caller when no longer needed.
    /// Note: we parent with worldPositionStays=false so we can set localPosition/localScale reliably.
    /// </summary>
    public GameObject CreateAndAnchorShrunkPanel(GameObject shrunkTemplate, Corner corner)
    {
        if (shrunkTemplate == null)
        {
            Debug.LogWarning("UIManager.CreateAndAnchorShrunkPanel: shrunkTemplate is null.");
            return null;
        }
        if (cameraTransform == null)
        {
            Debug.LogWarning("UIManager.CreateAndAnchorShrunkPanel: cameraTransform is null.");
            return null;
        }

        // Instantiate under UIManager for organization, then parent to camera with worldPositionStays=false so we control local transform.
        GameObject instance = Instantiate(shrunkTemplate, this.transform);
        instance.SetActive(true);

        // Parent to camera but set worldPositionStays = false so local transform can be set deterministically.
        instance.transform.SetParent(cameraTransform, worldPositionStays: false);

        // Set local position/rotation/scale according to chosen corner
        switch (corner)
        {
            case Corner.TopRight:
                instance.transform.localPosition = topRightLocalPos;
                break;
            case Corner.TopLeft:
                instance.transform.localPosition = new Vector3(-topRightLocalPos.x, topRightLocalPos.y, topRightLocalPos.z);
                break;
            case Corner.BottomRight:
                instance.transform.localPosition = new Vector3(topRightLocalPos.x, -topRightLocalPos.y, topRightLocalPos.z);
                break;
            case Corner.BottomLeft:
                instance.transform.localPosition = new Vector3(-topRightLocalPos.x, -topRightLocalPos.y, topRightLocalPos.z);
                break;
        }

        instance.transform.localRotation = Quaternion.identity;
        instance.transform.localScale = shrunkLocalScale;

        return instance;
    }

    /// <summary>
    /// Position a panel transform in front of the camera using panelDistance and a camera-space local offset.
    /// The panel's rotation is set to face the camera.
    /// </summary>
    private void PositionPanelInFront(Transform panelTransform, Vector3 localOffset)
    {
        if (panelTransform == null)
        {
            Debug.LogWarning("UIManager.PositionPanelInFront: panelTransform is null.");
            return;
        }
        if (cameraTransform == null)
        {
            Debug.LogWarning("UIManager.PositionPanelInFront: cameraTransform is null. Assign HMD camera.");
            return;
        }

        Vector3 worldPos = cameraTransform.position + cameraTransform.forward * panelDistance + cameraTransform.TransformVector(localOffset);
        panelTransform.position = worldPos;

        // rotate to face the camera so the front of the panel faces the user
        panelTransform.rotation = Quaternion.LookRotation(panelTransform.position - cameraTransform.position);
    }

    #endregion
}
