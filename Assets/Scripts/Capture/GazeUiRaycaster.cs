// GazeUiRaycaster.cs
// The UI half of gaze detection: which Canvas Graphic does the GAZE ray hit? (The physical half is the unchanged Physics.Raycast in GazeRecorder.SamplingLoop.)
//
// Why not GraphicRaycaster.Raycast(PointerEventData)?  A PointerEventData carries a SCREEN position and the GraphicRaycaster derives its ray from the event camera
// (eventCamera.ScreenPointToRay). A gaze ray starts at the eye origin (provider / eye-anchor, a few cm off the rendered camera) and has its own direction, so no screen position reproduces it, and
// swapping Canvas.worldCamera per sample would mutate the scene's UI. This class therefore uses the SAME primitives the GraphicRaycaster itself uses, on the gaze ray - exactly the pattern of XRI's
// TrackedDeviceGraphicRaycaster (ray -> plane of each Graphic's RectTransform -> RectTransformUtility.RectangleContainsScreenPoint with the hit point projected through the event camera ->
// Graphic.Raycast, which evaluates every ICanvasRaycastFilter: CanvasGroup.blocksRaycasts / ignoreParentGroups, Mask / RectMask2D, Image alpha hit test):
//   * the Graphics of a Canvas come from GraphicRegistry (enabled Graphics only; inactive objects and disabled components are never in it),
//   * Graphic.raycastTarget = false is ignored (unless an opt-in GazeUIElement.hitWhenNotRaycastTarget says otherwise), raycastPadding, canvasRenderer.cull and Graphic.depth == -1 (not drawn) are honoured,
//   * a Graphic whose effective alpha (Graphic.color.a x CanvasRenderer colour / alpha x inherited CanvasGroup alpha) is ~0, an empty Text / TMP label, or a Canvas on a layer the event camera does not render is
//     INVISIBLE and is never returned - uGUI would still raycast it, but the participant cannot see it. One deliberate exception: the transparent HIT AREA Image of a visible control (the XRI starter-assets
//     TextButton, used by every experiment panel, draws its look with non-raycast children) is accepted while another Graphic under the same Selectable is visible (ControlLooksVisible),
//   * the hit is always on a specific Graphic's rectangle - a hit on the Canvas plane outside every Graphic is "no UI hit",
//   * ordering mirrors EventSystem's RaycastComparer: sorting layer, sorting order, then Graphic.depth inside one Canvas / distance between Canvases.
//
// Supported render modes: World Space (event camera = Canvas.worldCamera, else the fallback camera, else Camera.main - the camera GraphicRaycaster itself would use) and Screen Space - Camera with a
// camera assigned (the engine places such a Canvas in the world at plane distance, so the same geometry applies). Screen Space - Overlay is NOT supported: it has no position in the world and is not
// composited into the headset image by the project's OpenXR rendering (see docs/gaze_ui_detection.md); such Canvases are skipped and counted.
//
// Performance: no allocation after warm-up (reused cache, no LINQ, no per-sample FindObject); cost = O(registered canvases x their raycastable graphics) of cheap plane maths. Identity strings are cached per Graphic.
// All comments in English per project convention.

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Result of one gaze-ray test against the registered Canvases (raw, independent of the physical ray).</summary>
public struct GazeUiHit
{
    public bool hit;
    public string canvasId, canvasName, renderMode;
    public int canvasSortingOrder;
    public string elementId, elementName, category, roiLabel;
    public bool hasGazeElement;
    public string interactable;      // "1" / "0" / "" (no Selectable above the Graphic)
    public Vector3 point;
    public float distance;
    public int graphicInstanceId;
    public int graphicsUnderRay;     // eligible Graphics the ray passes through (visible, raycast-targetable, not excluded), the winner included
    public string statusNote;        // "" or a short reason a canvas was skipped that could matter to the reader (diagnostic only)
}

public sealed class GazeUiRaycaster
{
    [System.Serializable]
    public struct Options
    {
        public float maxDistance;
        public float minVisibleAlpha;
        public bool requireGraphicRaycaster;
        public bool supportScreenSpaceCamera;
        public Camera fallbackCamera;
        public static Options Default => new Options { maxDistance = 50f, minVisibleAlpha = 0.01f, requireGraphicRaycaster = true, supportScreenSpaceCamera = true, fallbackCamera = null };
    }

    // ---- counters (cumulative; written to the manifest) ----
    public long Calls, Hits, CanvasesTested, GraphicsTested;
    public long CanvasesSkippedOverlay, CanvasesSkippedNoEventCamera, CanvasesSkippedNoGraphicRaycaster, CanvasesSkippedLayerNotRendered, CanvasesSkippedInactive, CanvasesSkippedScreenSpaceCameraDisabled;
    public long ProjectionFailures;

    sealed class Identity { public string id, name, category, roi; public bool hasElement; }
    readonly Dictionary<int, Identity> _identity = new Dictionary<int, Identity>(64);
    readonly StringBuilder _sb = new StringBuilder(96);

    /// <summary>Tests the gaze ray (origin + normalised direction) against every registered Canvas and returns the topmost eligible Graphic hit.</summary>
    public bool Raycast(Vector3 origin, Vector3 direction, in Options opt, out GazeUiHit result)
    {
        result = default; result.interactable = ""; result.statusNote = "";
        Calls++;
        if (direction.sqrMagnitude < 1e-12f) return false;
        direction.Normalize();

        var entries = GazeUiRegistry.Entries;
        bool haveBest = false; BestCandidate best = default; int under = 0;
        bool pruneNeeded = false;

        for (int ci = 0; ci < entries.Count; ci++)
        {
            GazeUiRegistry.Entry e = entries[ci];
            Canvas canvas = e.canvas;
            if (canvas == null) { pruneNeeded = true; continue; }
            if (!canvas.isActiveAndEnabled) { CanvasesSkippedInactive++; continue; }
            Canvas root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            if (!root.isActiveAndEnabled) { CanvasesSkippedInactive++; continue; }

            RenderMode mode = root.renderMode;
            if (mode == RenderMode.ScreenSpaceOverlay) { CanvasesSkippedOverlay++; continue; }

            // GraphicRaycaster: the project's definition of a Canvas that takes part in UI interaction (and the source of ignoreReversedGraphics).
            if (e.raycaster == null) canvas.TryGetComponent(out e.raycaster);
            if (e.raycaster == null && opt.requireGraphicRaycaster) { CanvasesSkippedNoGraphicRaycaster++; continue; }
            if (e.raycaster != null && !e.raycaster.isActiveAndEnabled) { CanvasesSkippedNoGraphicRaycaster++; continue; }
            bool ignoreReversed = e.raycaster == null || e.raycaster.ignoreReversedGraphics;

            Camera cam;
            if (mode == RenderMode.ScreenSpaceCamera)
            {
                if (!opt.supportScreenSpaceCamera) { CanvasesSkippedScreenSpaceCameraDisabled++; continue; }
                cam = root.worldCamera;   // a Screen Space - Camera Canvas without a camera behaves as Overlay: nothing is rendered for the headset
                if (cam == null) { CanvasesSkippedNoEventCamera++; continue; }
            }
            else
            {
                cam = root.worldCamera != null ? root.worldCamera : (opt.fallbackCamera != null ? opt.fallbackCamera : Camera.main);
                if (cam == null) { CanvasesSkippedNoEventCamera++; continue; }
            }
            if (cam.pixelWidth <= 0 || cam.pixelHeight <= 0) { CanvasesSkippedNoEventCamera++; continue; }   // an unusable viewport cannot project the hit point: fail safe, no UI hit
            if ((cam.cullingMask & (1 << canvas.gameObject.layer)) == 0) { CanvasesSkippedLayerNotRendered++; continue; }   // the participant cannot see this Canvas

            IList<Graphic> graphics = GraphicRegistry.GetGraphicsForCanvas(canvas);
            if (graphics == null || graphics.Count == 0) continue;
            CanvasesTested++;

            // sorting fields of the Canvas that actually orders this one (an un-overridden nested Canvas is ordered by its root)
            Canvas sortCanvas = canvas.overrideSorting ? canvas : root;
            int sortLayer = SortingLayer.GetLayerValueFromID(sortCanvas.sortingLayerID), sortOrder = sortCanvas.sortingOrder;

            for (int gi = 0; gi < graphics.Count; gi++)
            {
                Graphic g = graphics[gi];
                if (g == null) continue;
                GraphicsTested++;

                GazeUIElement el = null;
                if (!g.raycastTarget)
                {
                    // raycastTarget = false is ignored, unless the element explicitly opted in for gaze (e.g. a non-interactive target-preview image)
                    el = FindElement(g, out _);
                    if (el == null || !el.hitWhenNotRaycastTarget) continue;
                }
                if (!g.isActiveAndEnabled) continue;
                CanvasRenderer cr = g.canvasRenderer;
                if (cr == null || cr.cull || g.depth == -1) continue;   // culled by a RectMask2D / not drawn by the Canvas
                bool transparent = Alpha(g) < opt.minVisibleAlpha;   // Graphic colour alpha, CanvasRenderer colour / cross-fade alpha, or an ancestor CanvasGroup's alpha
                if (transparent && g.GetComponentInParent<Selectable>() == null) continue;   // an invisible Graphic is never a target - except the hit area of a visible control, decided below once the ray really is on it
                if (IsEmptyLabel(g)) continue;

                Transform t = g.transform;
                Vector3 n = t.forward;
                if (ignoreReversed && Vector3.Dot(t.position - origin, n) < 0f) continue;   // back face (same rule as GraphicRaycaster.ignoreReversedGraphics, evaluated from the gaze origin)
                float denom = Vector3.Dot(n, direction);
                if (Mathf.Abs(denom) < 1e-6f) continue;                                       // ray parallel to the Graphic's plane
                float dist = Vector3.Dot(n, t.position - origin) / denom;
                if (dist < 0f || dist > opt.maxDistance) continue;                            // behind the origin / beyond the ray length

                Vector3 p = origin + direction * dist;
                Vector3 sp = cam.WorldToScreenPoint(p);
                if (sp.z <= 0f || float.IsNaN(sp.x) || float.IsNaN(sp.y) || float.IsInfinity(sp.x) || float.IsInfinity(sp.y)) { ProjectionFailures++; continue; }
                if (!RectTransformUtility.RectangleContainsScreenPoint(g.rectTransform, sp, cam, g.raycastPadding)) continue;
                if (!g.Raycast(sp, cam)) continue;   // CanvasGroup.blocksRaycasts / Mask / RectMask2D / alpha hit test
                if (transparent && !ControlLooksVisible(g, opt.minVisibleAlpha)) continue;   // a transparent hit area counts only while the control it belongs to has something visible

                if (el == null) el = FindElement(g, out _);
                if (el != null && !el.recordAsTarget) continue;   // explicitly not a gaze target: the ray continues to what lies beneath

                under++;
                var c = new BestCandidate { graphic = g, canvas = canvas, root = root, element = el, dist = dist, depth = g.depth, sortLayer = sortLayer, sortOrder = sortOrder, point = p, mode = mode };
                if (!haveBest || Better(in c, in best)) { best = c; haveBest = true; }
            }
        }
        if (pruneNeeded) GazeUiRegistry.Prune();
        if (!haveBest) return false;

        Hits++;
        Fill(ref result, in best);
        result.graphicsUnderRay = under;
        return true;
    }

    struct BestCandidate
    {
        public Graphic graphic; public Canvas canvas, root; public GazeUIElement element;
        public float dist; public int depth, sortLayer, sortOrder; public Vector3 point; public RenderMode mode;
    }

    // EventSystem RaycastComparer order: sorting layer, sorting order, then Graphic.depth within one Canvas hierarchy and distance between Canvases.
    static bool Better(in BestCandidate a, in BestCandidate b)
    {
        if (a.sortLayer != b.sortLayer) return a.sortLayer > b.sortLayer;
        if (a.sortOrder != b.sortOrder) return a.sortOrder > b.sortOrder;
        if (a.root == b.root && a.depth != b.depth) return a.depth > b.depth;
        if (!Mathf.Approximately(a.dist, b.dist)) return a.dist < b.dist;
        return a.depth > b.depth;
    }

    static float Alpha(Graphic g)
    {
        CanvasRenderer cr = g.canvasRenderer;
        return g.color.a * cr.GetColor().a * cr.GetAlpha() * cr.GetInheritedAlpha();
    }

    static readonly List<Graphic> s_tmpGraphics = new List<Graphic>(16);

    // XRI-style controls (the starter-assets TextButton) use a fully transparent, raycast-target Image as the HIT AREA and draw the visible look with non-raycast child Graphics (outline, label). Looking at such a
    // button is looking at a visible control, so its transparent hit area is accepted - but only when some OTHER active, drawn, non-empty Graphic under the same Selectable is visible. A transparent control with
    // nothing visible (or one faded out by a CanvasGroup, whose alpha is inherited by its children) is not a target. Runs only for a transparent Graphic the ray really is on (rare), no allocation.
    static bool ControlLooksVisible(Graphic hitArea, float minAlpha)
    {
        Selectable sel = hitArea.GetComponentInParent<Selectable>();
        if (sel == null) return false;
        sel.GetComponentsInChildren(false, s_tmpGraphics);
        bool visible = false;
        for (int i = 0; i < s_tmpGraphics.Count && !visible; i++)
        {
            Graphic o = s_tmpGraphics[i];
            if (o == hitArea || o == null || !o.isActiveAndEnabled || o.depth == -1 || o.canvasRenderer.cull || IsEmptyLabel(o)) continue;
            visible = Alpha(o) >= minAlpha;
        }
        s_tmpGraphics.Clear();
        return visible;
    }

    static bool IsEmptyLabel(Graphic g)
    {
        if (g is Text tx) return string.IsNullOrEmpty(tx.text);
        if (g is TMPro.TMP_Text tm) return string.IsNullOrEmpty(tm.text);
        return false;
    }

    // The GazeUIElement that identifies this Graphic: on the Graphic's own GameObject, else on the nearest ancestor (inside the same root Canvas) that opted in with applyToChildren.
    static GazeUIElement FindElement(Graphic g, out bool inherited)
    {
        inherited = false;
        if (g.TryGetComponent(out GazeUIElement own)) return own;
        Transform t = g.transform.parent;
        for (int i = 0; i < 12 && t != null; i++, t = t.parent)
        {
            if (t.TryGetComponent(out GazeUIElement anc) && anc.applyToChildren) { inherited = true; return anc; }
            if (t.TryGetComponent(out Canvas c) && c.isRootCanvas) break;
        }
        return null;
    }

    void Fill(ref GazeUiHit r, in BestCandidate b)
    {
        Graphic g = b.graphic;
        int gid = g.GetInstanceID();
        if (!_identity.TryGetValue(gid, out Identity id) || (id.hasElement != (b.element != null)))
        {
            if (_identity.Count > 256) _identity.Clear();   // bounded: panels are re-instantiated per phase
            id = BuildIdentity(g, b.root, b.element);
            _identity[gid] = id;
        }
        r.hit = true;
        r.canvasId = CanvasId(b.root); r.canvasName = Clean(b.root.name); r.renderMode = b.mode == RenderMode.WorldSpace ? "WorldSpace" : "ScreenSpaceCamera";
        r.canvasSortingOrder = b.sortOrder;
        r.elementId = id.id; r.elementName = id.name; r.category = id.category; r.roiLabel = id.roi; r.hasGazeElement = id.hasElement;
        // A Panel-level element (GazeUiCategory.Panel) identifies the whole panel: the control the ray happened to cross inside it must not leak into the record (interactable stays blank).
        Selectable sel = (b.element != null && b.element.category == GazeUiCategory.Panel) ? null : g.GetComponentInParent<Selectable>();
        r.interactable = sel == null ? "" : (sel.IsInteractable() ? "1" : "0");   // live: a disabled Button changes
        r.point = b.point; r.distance = b.dist; r.graphicInstanceId = gid;
    }

    Identity BuildIdentity(Graphic g, Canvas root, GazeUIElement el)
    {
        var id = new Identity { hasElement = el != null };
        GazeUiCategory cat = el != null ? el.category : GazeUiCategory.Auto;
        if (cat == GazeUiCategory.Auto) cat = AutoCategory(g, root);
        id.category = CategoryName(cat);
        id.roi = AoiClassifier.UiRoiLabel(id.category, el != null ? el.roiLabel : null);
        id.name = (el != null && !string.IsNullOrEmpty(el.displayName)) ? el.displayName : Clean(g.gameObject.name);
        id.id = (el != null && !string.IsNullOrEmpty(el.stableId)) ? el.stableId : DerivedId(g, root);
        return id;
    }

    static GazeUiCategory AutoCategory(Graphic g, Canvas root)
    {
        if (g.gameObject == root.gameObject) return GazeUiCategory.Panel;                    // the Canvas's own background Image
        if (g.GetComponentInParent<Selectable>() != null) return GazeUiCategory.Button;      // the graphic of a Button / Toggle / other control
        if (g is Text || g is TMPro.TMP_Text) return GazeUiCategory.Text;
        return GazeUiCategory.Image;                                                          // RawImage / Image that is neither background nor control
    }

    public static string CategoryName(GazeUiCategory c)
    {
        switch (c)
        {
            case GazeUiCategory.Panel: return "panel";
            case GazeUiCategory.Button: return "button";
            case GazeUiCategory.Text: return "text";
            case GazeUiCategory.Image: return "image";
            case GazeUiCategory.QuestionnaireOption: return "questionnaire_option";
            case GazeUiCategory.DestinationPanel: return "destination_panel";
            case GazeUiCategory.TargetPreview: return "target_preview";
            case GazeUiCategory.ContinueControl: return "continue_control";
            default: return "other";
        }
    }

    string DerivedId(Graphic g, Canvas root)
    {
        _sb.Length = 0;
        _sb.Append(CanvasId(root)).Append('/');
        if (g.transform == root.transform) { _sb.Append("$canvas"); return _sb.ToString(); }
        // path from the root Canvas's child down to the Graphic
        int start = _sb.Length;
        Transform t = g.transform;
        for (int i = 0; i < 16 && t != null && t != root.transform; i++, t = t.parent)
            _sb.Insert(start, Clean(t.name) + (i == 0 ? "" : "/"));
        return _sb.ToString();
    }

    static string CanvasId(Canvas root) => Clean(root.name);

    // "Name(Clone)" -> "Name": instantiated panels keep a stable id across instances.
    static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        while (s.EndsWith("(Clone)")) s = s.Substring(0, s.Length - 7);
        return s;
    }
}
