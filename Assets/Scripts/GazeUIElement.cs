// GazeUIElement.cs
//
// OPT-IN marker for a Canvas Graphic that should be reported by the gaze UI path (GazeUiRaycaster) with a chosen stable id, category and ROI label.
// It is NOT required: every visible, raycast-target Graphic on a registered Canvas is detected and gets an id derived from its hierarchy path and a category derived from
// its components (Button / Text / TMP / RawImage / Image / canvas background). Add this component only where that automatic identity is not good enough - e.g. the target-building
// preview image of a destination panel, a questionnaire option, or the Continue control. Do not add it indiscriminately and never to parent containers whose children are the
// real targets (a container that carries this component with applyToChildren = false identifies only itself).
//
// Integration: GazeUiRaycaster reads this component while resolving WHICH Graphic the gaze ray hit; GazeRecorder writes the result to gaze_ui_hits.csv; AoiClassifier.UiRoiLabel turns it
// into the ROI label. This component has no Update loop and does not change any UI behaviour.
//
// All comments in English per project convention.

using UnityEngine;

/// <summary>UI category of a gaze-detectable Canvas element (recorded as text in gaze_ui_hits.csv).</summary>
public enum GazeUiCategory
{
    /// <summary>Derive the category from the Graphic's components (default).</summary>
    Auto = 0,
    Panel = 1,
    Button = 2,
    Text = 3,
    Image = 4,
    QuestionnaireOption = 5,
    DestinationPanel = 6,
    TargetPreview = 7,
    ContinueControl = 8,
    Other = 9
}

[DisallowMultipleComponent]
[AddComponentMenu("Experiment/Gaze UI Element")]
public class GazeUIElement : MonoBehaviour
{
    [Header("Identity")]
    [Tooltip("Stable identifier written to gaze_ui_hits.csv (ui_element_id). Empty = '<canvas name>/<hierarchy path>' derived from the scene hierarchy ('(Clone)' suffixes removed).")]
    public string stableId = "";

    [Tooltip("Human-readable name (ui_element_name). Empty = the GameObject name.")]
    public string displayName = "";

    [Tooltip("UI category. Auto = derived from the Graphic's components.")]
    public GazeUiCategory category = GazeUiCategory.Auto;

    [Tooltip("ROI label (ui_roi_label). Empty = 'UI_<CATEGORY>' (e.g. UI_BUTTON, UI_TARGET_PREVIEW).")]
    public string roiLabel = "";

    [Header("Behaviour")]
    [Tooltip("If false, this Graphic is NEVER reported as a gaze target (decorative background, spacer): the ray continues to the next Graphic underneath. A container that is not a target " +
             "must set this to false rather than shadow the controls it contains.")]
    public bool recordAsTarget = true;

    [Tooltip("If true, this component also identifies Graphics BELOW it in the hierarchy that have no GazeUIElement of their own (e.g. a button root whose Image / label children are the " +
             "hit Graphics). Default false: a container never replaces its children's identity.")]
    public bool applyToChildren = false;

    [Tooltip("If true, a Graphic with Graphic.raycastTarget = false is still gaze-detectable (e.g. a non-interactive target-preview RawImage). Default false: raycastTarget = false is ignored. " +
             "Only the gaze path is affected - the Graphic still ignores pointer / controller rays.")]
    public bool hitWhenNotRaycastTarget = false;

    void OnEnable()
    {
        // Makes sure the Canvas this element lives on is known to the gaze UI path even when it was not created through UIManager.
        Canvas c = GetComponentInParent<Canvas>();
        if (c != null) GazeUiRegistry.Register(c);
    }
}
