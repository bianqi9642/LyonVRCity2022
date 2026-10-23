using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Attach to target buildings. When clicked/selected, it notifies the ExperimentManager.
/// Works with:
/// - IPointerClickHandler (if using UI pointer/raycast)
/// - OnMouseDown in editor for quick testing (requires collider)
/// - You may call Select() from XR Interaction events if you use an XR Interactor.
///
/// Data-capture (Revision 2): a static <see cref="Clicked"/> event lets the data-capture layer observe
/// every raw building click (with GameObject identity) without changing this component's behaviour. The
/// call to ExperimentManager.RegisterTargetSelection(targetId) is completely unchanged.
/// </summary>
[RequireComponent(typeof(Collider))]
public class TargetSelectable : MonoBehaviour, IPointerClickHandler
{
    [Tooltip("Unique identity of this building. Must equal this GameObject's name (the scene serialises it " +
             "explicitly; Reset() fills it in when the component is added). Empty falls back to the GameObject name " +
             "at selection time - it is never a shared placeholder.")]
    public string targetId = "";
    public ExperimentManager experimentManager; // assign in inspector or find at Start()

    /// <summary>
    /// Raised whenever Select() runs, BEFORE the existing ExperimentManager notification. Observation only:
    /// subscribers must not throw and must not alter experiment state. Used by DataCaptureManager.
    /// </summary>
    public static event System.Action<TargetSelectable> Clicked;

    /// <summary>
    /// The identity used for scoring and notifications: the serialised targetId, or the GameObject name when it is
    /// empty. Unlike the old shared "targetA" default, this can never make two different buildings look identical.
    /// </summary>
    public string ResolvedTargetId => string.IsNullOrEmpty(targetId) ? gameObject.name : targetId;

    // Editor: a newly added component starts with its own GameObject's name, not a shared placeholder.
    private void Reset()
    {
        targetId = gameObject.name;
    }

    private void Start()
    {
        if (experimentManager == null)
        {
            experimentManager = FindObjectOfType<ExperimentManager>();
        }
    }

    // Called by IPointerClickHandler systems (UI pointer, XR UI pointer configured to raycast)
    public void OnPointerClick(PointerEventData eventData)
    {
        Select();
    }

    // For quick testing with mouse in editor
    private void OnMouseDown()
    {
        Select();
    }

    // Public method that can be called by XR Interactors or other systems
    public void Select()
    {
        // Data-capture observer hook (never changes control flow).
        try { Clicked?.Invoke(this); }
        catch (System.Exception e) { Debug.LogWarning("[TargetSelectable] Clicked observer threw: " + e.Message); }

        // Notify ExperimentManager. You can pass richer data instead of just an id.
        if (experimentManager != null)
            experimentManager.RegisterTargetSelection(ResolvedTargetId);
        else
            Debug.LogWarning("No ExperimentManager assigned to TargetSelectable");
    }
}