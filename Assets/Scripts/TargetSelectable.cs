using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Attach to target buildings. When clicked/selected, it notifies the ExperimentManager.
/// Works with:
/// - IPointerClickHandler (if using UI pointer/raycast)
/// - OnMouseDown in editor for quick testing (requires collider)
/// - You may call Select() from XR Interaction events if you use an XR Interactor.
/// </summary>
[RequireComponent(typeof(Collider))]
public class TargetSelectable : MonoBehaviour, IPointerClickHandler
{
    public string targetId = "targetA"; // optional id
    public ExperimentManager experimentManager; // assign in inspector or find at Start()

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
        // Notify ExperimentManager. You can pass richer data instead of just an id.
        if (experimentManager != null)
            experimentManager.RegisterTargetSelection(targetId);
        else
            Debug.LogWarning("No ExperimentManager assigned to TargetSelectable");
    }
}
