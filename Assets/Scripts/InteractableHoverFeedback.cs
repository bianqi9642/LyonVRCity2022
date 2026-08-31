// InteractableHoverFeedback.cs
//
// Role
// ----
// Purely cosmetic, HOVER-ONLY interaction affordance for objects that are selected through the
// existing UGUI EventSystem pointer path (the same path that delivers OnPointerClick to
// TargetSelectable). While a controller ray - or the mouse, in the Editor - is hovering the
// object's selectable Collider, this component applies a deliberately subtle brightness lift to
// the object's Renderer(s). The lift is removed the instant the last pointer leaves.
//
// It intentionally does NOT provide: a persistent highlight, outline, glow, marker, arrow, icon,
// label, flashing/pulsing/animated effect, or any correct/incorrect (green/red) distinction. It
// expresses nothing about the object's identity, its targetId, or whether it is the "correct"
// destination. The only message is: "my controller ray is currently pointing at something that
// can respond to selection".
//
// Integration
// -----------
// - Attach to the SAME GameObject as TargetSelectable (the GameObject that owns the Collider hit
//   by the XR Ray Interactor / TrackedDevicePhysicsRaycaster). It never references TargetSelectable,
//   ExperimentManager, or any experiment logic - the two components are fully independent.
// - Hover detection reuses the existing interaction pipeline through IPointerEnterHandler /
//   IPointerExitHandler. No extra raycasting, no Update()/LateUpdate() polling, no custom input,
//   no conversion to an XRI interactable.
// - The visual change is applied with a MaterialPropertyBlock, so NO .mat asset and NO shared
//   material is ever modified. Original appearance is restored on pointer exit, on OnDisable, and
//   on OnDestroy.
//
// Design rationale
// ----------------
// - Hover-only (never persistent) so the cue cannot act as a distance-visible "this is your
//   destination" beacon: it only appears once the participant has already aimed at the object,
//   and only at ray range.
// - Brightness via the base colour property (URP Lit '_BaseColor', with a '_Color' fallback for
//   built-in/Standard) rather than emission: a MaterialPropertyBlock cannot toggle the '_EMISSION'
//   keyword, and emission reads as a glow from a distance. A small albedo lift stays subtle.
// - Left + right controller rays (and the Editor mouse) are reference-counted, so the feedback
//   clears only when the LAST pointer leaves.
//
// Performance
// -----------
// - Event-driven only; no per-frame work. One MaterialPropertyBlock is allocated per Renderer at
//   Awake and reused. Hover apply/restore does no allocation. (A Renderer with an active
//   MaterialPropertyBlock opts out of the SRP Batcher for that Renderer only; negligible for a
//   handful of target objects.)
//
// All comments in English per project convention.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

[DisallowMultipleComponent]
public class InteractableHoverFeedback : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [Header("Renderers affected by the hover feedback")]
    [Tooltip("Renderers that brighten while this object is hovered. Leave EMPTY to auto-collect every " +
             "Renderer on this GameObject and its children at Awake. Populate explicitly to restrict the " +
             "effect to a subset (useful when real building geometry replaces the current test proxy).")]
    [SerializeField] private Renderer[] targetRenderers;

    [Tooltip("When auto-collecting (list left empty), also include Renderers on inactive child GameObjects.")]
    [SerializeField] private bool includeInactiveRenderers = true;

    [Header("Feedback strength")]
    [Tooltip("Colour property overridden on the material while hovered. URP Lit uses '_BaseColor'. " +
             "If the material lacks this property, the component falls back to '_Color' (built-in / Standard). " +
             "If neither exists, that Renderer is skipped and a warning is logged when debug logs are on.")]
    [SerializeField] private string colorProperty = "_BaseColor";

    [Tooltip("Fractional brightness increase applied to the base colour on hover (0.12 = +12%). " +
             "Keep this small: noticeable when the ray is on the object, but not a glow from a distance.")]
    [Range(0f, 0.5f)]
    [SerializeField] private float hoverBrightness = 0.30f;

    [Header("Debug")]
    [Tooltip("Log setup and hover on/off transitions. Never logs per-frame or per-sample data.")]
    [SerializeField] private bool enableDebugLogs = false;

    // Cached per-Renderer state so hover apply/restore never allocates and never touches a shared material.
    private sealed class RendererState
    {
        public Renderer Renderer;
        public MaterialPropertyBlock Block;
        public int PropertyId;
        public Color OriginalColor;
        public bool Supported;
    }

    private readonly List<RendererState> states = new List<RendererState>();

    // Distinct pointer ids (left ray, right ray, mouse, ...) currently hovering this object.
    private readonly HashSet<int> hoveringPointerIds = new HashSet<int>();

    private bool hoverApplied;

    private void Awake()
    {
        ResolveRenderers();
        BuildRendererStates();
    }

    // --- Setup -------------------------------------------------------------------------------------

    private void ResolveRenderers()
    {
        // Explicit inspector list wins.
        if (targetRenderers != null && targetRenderers.Length > 0)
        {
            return;
        }

        // Otherwise auto-collect from this GameObject and its children (future building geometry may be nested).
        targetRenderers = GetComponentsInChildren<Renderer>(includeInactiveRenderers);

        if ((targetRenderers == null || targetRenderers.Length == 0) && enableDebugLogs)
        {
            Debug.LogWarning($"[InteractableHoverFeedback] No Renderer found on '{name}' or its children; " +
                             "hover feedback will do nothing.", this);
        }
    }

    private void BuildRendererStates()
    {
        states.Clear();

        if (targetRenderers == null)
        {
            return;
        }

        int preferredId = Shader.PropertyToID(string.IsNullOrEmpty(colorProperty) ? "_BaseColor" : colorProperty);
        int fallbackId = Shader.PropertyToID("_Color");

        foreach (Renderer r in targetRenderers)
        {
            if (r == null)
            {
                continue;
            }

            RendererState state = new RendererState
            {
                Renderer = r,
                Block = new MaterialPropertyBlock(),
                Supported = false
            };

            // sharedMaterial is read-only access here and does NOT instantiate a material.
            Material mat = r.sharedMaterial;
            if (mat != null)
            {
                if (mat.HasProperty(preferredId))
                {
                    state.PropertyId = preferredId;
                    state.OriginalColor = mat.GetColor(preferredId);
                    state.Supported = true;
                }
                else if (mat.HasProperty(fallbackId))
                {
                    state.PropertyId = fallbackId;
                    state.OriginalColor = mat.GetColor(fallbackId);
                    state.Supported = true;
                }
            }

            if (!state.Supported && enableDebugLogs)
            {
                Debug.LogWarning($"[InteractableHoverFeedback] Renderer '{r.name}' material has neither " +
                                 $"'{colorProperty}' nor '_Color'; it will not react to hover.", this);
            }

            states.Add(state);
        }
    }

    // --- EventSystem hover callbacks (same pipeline that delivers OnPointerClick to TargetSelectable) ---

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (eventData == null)
        {
            return;
        }

        // Add() is idempotent, so a duplicate enter without an exit cannot double-apply.
        hoveringPointerIds.Add(eventData.pointerId);

        if (!hoverApplied)
        {
            ApplyHoverFeedback();
        }
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (eventData == null)
        {
            return;
        }

        hoveringPointerIds.Remove(eventData.pointerId);

        // Only clear once the LAST hovering pointer has left.
        if (hoverApplied && hoveringPointerIds.Count == 0)
        {
            ClearHoverFeedback();
        }
    }

    // --- Visual apply / restore ------------------------------------------------------------------

    private void ApplyHoverFeedback()
    {
        hoverApplied = true;

        float k = 1f + hoverBrightness;

        for (int i = 0; i < states.Count; i++)
        {
            RendererState s = states[i];
            if (!s.Supported || s.Renderer == null)
            {
                continue;
            }

            Color c = s.OriginalColor;
            Color hover = new Color(c.r * k, c.g * k, c.b * k, c.a);

            // Preserve any property block the Renderer might already carry, then override only our colour key.
            s.Renderer.GetPropertyBlock(s.Block);
            s.Block.SetColor(s.PropertyId, hover);
            s.Renderer.SetPropertyBlock(s.Block);
        }

        if (enableDebugLogs)
        {
            Debug.Log($"[InteractableHoverFeedback] Hover ON  ('{name}', {hoveringPointerIds.Count} pointer(s)).", this);
        }
    }

    private void ClearHoverFeedback()
    {
        hoverApplied = false;

        for (int i = 0; i < states.Count; i++)
        {
            RendererState s = states[i];
            if (!s.Supported || s.Renderer == null)
            {
                continue;
            }

            // Write the ORIGINAL value back rather than clearing the whole block, so we do not
            // disturb any unrelated per-renderer overrides that might exist now or in the future.
            s.Renderer.GetPropertyBlock(s.Block);
            s.Block.SetColor(s.PropertyId, s.OriginalColor);
            s.Renderer.SetPropertyBlock(s.Block);
        }

        if (enableDebugLogs)
        {
            Debug.Log($"[InteractableHoverFeedback] Hover OFF ('{name}').", this);
        }
    }

    // --- Safety cleanup -------------------------------------------------------------------------

    private void OnDisable()
    {
        // The object can be disabled mid-hover (e.g. ExperimentManager toggling targets between phases).
        // Drop all pointer state and guarantee the original appearance is restored.
        hoveringPointerIds.Clear();

        if (hoverApplied)
        {
            ClearHoverFeedback();
        }
    }

    private void OnDestroy()
    {
        // Safety net: restore before the Renderer is torn down if we are destroyed while hovered.
        if (hoverApplied)
        {
            ClearHoverFeedback();
        }
    }

#if UNITY_EDITOR
    // Keeps the auto-collected renderer list fresh while editing in the Editor; no runtime effect.
    private void OnValidate()
    {
        if (hoverBrightness < 0f)
        {
            hoverBrightness = 0f;
        }
    }
#endif
}