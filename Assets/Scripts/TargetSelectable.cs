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

    // ---- interaction identity (schema v6) ----
    // The authoritative point where a physical click is accepted is Select(): it allocates ONE session-scoped, monotonically increasing id ("C000123"),
    // keeps it in LastInteractionId for the Clicked observers (building_click), and hands it to ExperimentManager.RegisterTargetSelection so the scored
    // outcome (correct/incorrect_selection) carries the same id. One physical click can reach Select() through more than one callback (OnPointerClick AND
    // OnMouseDown in the Editor, several interactors): a second call on the same building in the same frame, or from inside a running Select(), is the same
    // click and is ignored (counted in DuplicateClicksIgnored). Separate clicks - different frames - always get different ids.
    static long s_interactionCounter;
    /// <summary>Clicks ignored as duplicate callbacks of an already accepted click (diagnostic; also in the session manifest).</summary>
    public static int DuplicateClicksIgnored { get; private set; }
    /// <summary>Id of the most recent accepted click on this building ("" before the first).</summary>
    public string LastInteractionId { get; private set; } = "";
    int lastSelectFrame = -1;
    bool inSelect;

    /// <summary>Restarts the id sequence at C000001 (called when a capture session starts, and by tests).</summary>
    public static void ResetInteractionCounter() { s_interactionCounter = 0; DuplicateClicksIgnored = 0; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { ResetInteractionCounter(); }

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
        // Participant locked (before Start, End panel visible, session ended): a building click does nothing and is not recorded.
        if (experimentManager != null && !experimentManager.ParticipantControlEnabled) return;

        // Duplicate callback of the same physical click (see the interaction identity note above).
        if (inSelect || lastSelectFrame == Time.frameCount) { DuplicateClicksIgnored++; return; }
        inSelect = true;
        try
        {
            lastSelectFrame = Time.frameCount;
            LastInteractionId = "C" + (++s_interactionCounter).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

            // Data-capture observer hook (never changes control flow).
            try { Clicked?.Invoke(this); }
            catch (System.Exception e) { Debug.LogWarning("[TargetSelectable] Clicked observer threw: " + e.Message); }

            // Notify ExperimentManager. You can pass richer data instead of just an id.
            if (experimentManager != null)
                experimentManager.RegisterTargetSelection(ResolvedTargetId, LastInteractionId);
            else
                Debug.LogWarning("No ExperimentManager assigned to TargetSelectable");
        }
        finally { inSelect = false; }
    }
}