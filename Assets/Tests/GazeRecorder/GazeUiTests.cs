// GazeUiTests.cs  (PlayMode, SYNTHETIC)
// Tests for the Canvas UI gaze path (GazeUiRaycaster / GazeUiRegistry / GazeTargetResolver / GazeRecorder gaze_ui_hits.csv). Everything is built from scene objects created here with deterministic geometry:
// they establish the LOGIC (which Graphic is hit, what is ignored, how the two channels are resolved, what is written), NOT what the Quest renders or what a participant's eyes do. The real experiment panels are
// covered separately by Assets/Tests/EditMode/GazeUiRealSceneTests.cs; headset behaviour is not covered by any automated test (see docs/gaze_ui_detection.md).
// The production types live in Assembly-CSharp; they are reached by reflection (Reflect), exactly like the other tests of this assembly.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using static Reflect;

[Category("Synthetic")]
public class GazeUiRaycasterTests
{
    static readonly Type RayT = T("GazeUiRaycaster"), RegT = T("GazeUiRegistry"), OptT = Type.GetType("GazeUiRaycaster+Options, Assembly-CSharp");
    readonly List<UnityEngine.Object> _spawned = new List<UnityEngine.Object>();
    object _ray; Camera _cam;

    [SetUp]
    public void SetUp()
    {
        CallStatic(RegT, "ClearForTests");
        _ray = Activator.CreateInstance(RayT);
        var cg = new GameObject("UiTestCam"); _spawned.Add(cg);
        _cam = cg.AddComponent<Camera>(); _cam.enabled = false; _cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        Assume.That(_cam.pixelWidth > 0 && _cam.pixelHeight > 0, "no usable camera viewport in this runtime (pixel size " + _cam.pixelWidth + "x" + _cam.pixelHeight + ")");
    }

    [TearDown]
    public void TearDown() { foreach (var o in _spawned) if (o != null) UnityEngine.Object.DestroyImmediate(o); _spawned.Clear(); CallStatic(RegT, "ClearForTests"); }

    // ---- scene builders ----
    GameObject NewCanvas(string name, Vector3 pos, Vector2 size, RenderMode mode = RenderMode.WorldSpace, int layer = 0, bool raycaster = true)
    {
        var go = new GameObject(name, typeof(RectTransform)); _spawned.Add(go); go.layer = layer;
        var c = go.AddComponent<Canvas>(); c.renderMode = mode;
        if (raycaster) go.AddComponent<GraphicRaycaster>();
        var rt = (RectTransform)go.transform; rt.sizeDelta = size; rt.position = pos; rt.rotation = Quaternion.identity; rt.localScale = Vector3.one;
        return go;
    }

    static T Child<T>(Transform parent, string name, Vector2 anchored, Vector2 size, bool raycast = true) where T : Graphic
    {
        var go = new GameObject(name, typeof(RectTransform)); go.layer = parent.gameObject.layer; go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform; rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f); rt.anchoredPosition = anchored; rt.sizeDelta = size;
        var g = go.AddComponent<T>(); g.raycastTarget = raycast;
        return g;
    }

    // Graphic.depth (the "has been drawn" marker GraphicRaycaster itself requires) is assigned when a camera renders the Canvas, so every geometry change is followed by one render of the test camera
    // (works in -nographics batch mode). _cam is enabled only for that call.
    void Refresh()
    {
        Canvas.ForceUpdateCanvases();
        if (_cam == null) return;
        _cam.enabled = true; _cam.Render(); _cam.enabled = false;
    }

    void Ready(params GameObject[] canvases) { foreach (var c in canvases) CallStatic(RegT, "RegisterHierarchy", c); Refresh(); }

    object Opts(Action<object> tweak = null) { var o = GetStatic<object>(OptT, "Default"); Set(o, "fallbackCamera", _cam); tweak?.Invoke(o); return o; }

    bool Cast(Vector3 origin, Vector3 dir, out object hit, object opts = null)
    {
        var args = new object[] { origin, dir, opts ?? Opts(), null };
        bool r = (bool)RayT.GetMethod("Raycast").Invoke(_ray, args);
        hit = args[3]; return r;
    }
    string S(object hit, string f) => Get<string>(hit, f);
    long Counter(string name) => Get<long>(_ray, name);

    // ====================================================================== World Space: what is hit
    [Test]
    public void WorldSpace_Button_IsTheHitGraphic_NotTheCanvasBackground_WithIdCategoryAndPoint()
    {
        var c = NewCanvas("PanelA", new Vector3(0, 0, 3), new Vector2(4, 3));
        var bg = c.AddComponent<Image>();   // the canvas's own background, raycast target
        var btn = Child<Image>(c.transform, "TextButton", Vector2.zero, new Vector2(1, 0.5f)); btn.gameObject.AddComponent<Button>();
        Ready(c);
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out var h));
        Assert.AreEqual("PanelA/TextButton", S(h, "elementId")); Assert.AreEqual("TextButton", S(h, "elementName")); Assert.AreEqual("button", S(h, "category")); Assert.AreEqual("UI_BUTTON", S(h, "roiLabel"));
        Assert.AreEqual("WorldSpace", S(h, "renderMode")); Assert.AreEqual("PanelA", S(h, "canvasId")); Assert.AreEqual("1", S(h, "interactable"));
        Assert.AreEqual(3f, Get<float>(h, "distance"), 1e-3f); Assert.AreEqual(new Vector3(0, 0, 3), Get<Vector3>(h, "point"));
        Assert.AreEqual(2, Get<int>(h, "graphicsUnderRay"), "background + button are both under the ray; the topmost wins");
        // off the button, on the background: the panel background is the hit (category panel)
        Assert.IsTrue(Cast(Vector3.zero, new Vector3(1.2f, 0, 3).normalized, out h));
        Assert.AreEqual("PanelA/$canvas", S(h, "elementId")); Assert.AreEqual("panel", S(h, "category"));
        Assert.AreEqual("", S(h, "interactable"), "no Selectable above the background");
        // outside the canvas rectangle: nothing
        Assert.IsFalse(Cast(Vector3.zero, new Vector3(2.5f, 0, 3).normalized, out h));
    }

    [Test]
    public void Text_RaycastTargetTrue_IsHit_AsText_And_EmptyLabel_OrRaycastTargetFalse_AreIgnored()
    {
        var c = NewCanvas("PanelT", new Vector3(0, 0, 3), new Vector2(4, 3));
        var tx = Child<Text>(c.transform, "Label", Vector2.zero, new Vector2(2, 1)); tx.text = "hello";
        Ready(c);
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out var h)); Assert.AreEqual("text", S(h, "category")); Assert.AreEqual("PanelT/Label", S(h, "elementId"));
        tx.text = ""; Refresh();
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _), "an empty label draws nothing: invisible, never a target");
        tx.text = "hello"; tx.raycastTarget = false; Refresh();
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _), "raycastTarget = false is ignored");
    }

    [Test]
    public void RawImage_IsHit_AsImage()
    {
        var c = NewCanvas("PanelR", new Vector3(0, 0, 3), new Vector2(4, 3));
        Child<RawImage>(c.transform, "BuildingImage", Vector2.zero, new Vector2(1, 1));
        Ready(c);
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out var h)); Assert.AreEqual("image", S(h, "category")); Assert.AreEqual("UI_IMAGE", S(h, "roiLabel"));
    }

    [Test]
    public void TextMeshPro_IsHit_AsText_WhenTheTmpTypeIsAvailable()
    {
        var tmpT = Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
        if (tmpT == null) Assert.Ignore("TextMeshPro not available in this project");
        var c = NewCanvas("PanelTmp", new Vector3(0, 0, 3), new Vector2(4, 3));
        var go = new GameObject("TmpLabel", typeof(RectTransform)); go.transform.SetParent(c.transform, false); ((RectTransform)go.transform).sizeDelta = new Vector2(2, 1);
        var tmp = (Graphic)go.AddComponent(tmpT); tmp.raycastTarget = true; tmpT.GetProperty("text").SetValue(tmp, "gaze me");
        Ready(c);
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out var h)); Assert.AreEqual("text", S(h, "category")); Assert.AreEqual("PanelTmp/TmpLabel", S(h, "elementId"));
    }

    [Test]
    public void RaycastTargetFalse_OnTopOfABackground_FallsThroughToTheBackground()
    {
        var c = NewCanvas("PanelF", new Vector3(0, 0, 3), new Vector2(4, 3)); c.AddComponent<Image>();
        Child<Image>(c.transform, "Decor", Vector2.zero, new Vector2(2, 2), raycast: false);
        Ready(c);
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out var h)); Assert.AreEqual("PanelF/$canvas", S(h, "elementId"), "the non-raycast-target Image is not a target; the panel behind it is");
    }

    // ====================================================================== inactive / disabled / blocked / invisible
    [Test]
    public void InactiveCanvas_InactiveChild_DisabledCanvas_AreNeverHit()
    {
        var c = NewCanvas("PanelI", new Vector3(0, 0, 3), new Vector2(4, 3)); c.AddComponent<Image>();
        var btn = Child<Image>(c.transform, "Btn", Vector2.zero, new Vector2(1, 1));
        Ready(c);
        Assert.AreEqual("PanelI/Btn", S(Hit(), "elementId"));
        btn.gameObject.SetActive(false); Refresh();
        Assert.AreEqual("PanelI/$canvas", S(Hit(), "elementId"), "an inactive child is not a target");
        btn.gameObject.SetActive(true); c.GetComponent<Canvas>().enabled = false; Refresh();   // what UIManager does while a panel waits for a settled head pose
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _), "a disabled Canvas is not hit");
        c.GetComponent<Canvas>().enabled = true; Refresh();
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out _));
        c.SetActive(false);
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _), "an inactive Canvas is not hit");
    }

    object Hit() { Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out var h)); return h; }

    [Test]
    public void CanvasGroup_BlocksRaycasts_False_IsRespected_And_IgnoreParentGroups_Overrides()
    {
        var c = NewCanvas("PanelG", new Vector3(0, 0, 3), new Vector2(4, 3)); c.AddComponent<Image>();
        var holder = Child<Image>(c.transform, "Holder", Vector2.zero, new Vector2(2, 2)); var grp = holder.gameObject.AddComponent<CanvasGroup>();
        var inner = Child<Image>(holder.transform, "Inner", Vector2.zero, new Vector2(1, 1));
        Ready(c);
        Assert.AreEqual("PanelG/Holder/Inner", S(Hit(), "elementId"));
        grp.blocksRaycasts = false; Refresh();
        Assert.AreEqual("PanelG/$canvas", S(Hit(), "elementId"), "the whole group subtree is skipped; the ray continues to the background");
        inner.gameObject.AddComponent<CanvasGroup>().ignoreParentGroups = true; Refresh();
        Assert.AreEqual("PanelG/Holder/Inner", S(Hit(), "elementId"), "ignoreParentGroups re-enables the child");
    }

    [Test]
    public void InvisibleGraphics_ZeroAlpha_ZeroCanvasGroupAlpha_AreNeverReturned()
    {
        var c = NewCanvas("PanelV", new Vector3(0, 0, 3), new Vector2(4, 3)); c.AddComponent<Image>();
        var btn = Child<Image>(c.transform, "Btn", Vector2.zero, new Vector2(1, 1));
        Ready(c);
        btn.color = new Color(1, 1, 1, 0); Refresh();
        Assert.AreEqual("PanelV/$canvas", S(Hit(), "elementId"), "an alpha-0 Image is an invisible hit area: uGUI would raycast it, the participant cannot see it");
        btn.color = Color.white; var grp = c.AddComponent<CanvasGroup>(); grp.alpha = 0f; Refresh();
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _), "a whole Canvas faded out by its CanvasGroup is invisible");
    }

    [Test]
    public void TransparentHitArea_OfAVisibleControl_IsTheButton_ButNotWhenNothingUnderTheControlIsVisible()
    {
        // the XRI TextButton pattern: a fully transparent raycast-target Image (hit area) on the Button, visible look = non-raycast children
        var c = NewCanvas("PanelB", new Vector3(0, 0, 3), new Vector2(4, 3)); c.AddComponent<Image>();
        var area = Child<Image>(c.transform, "TextButton", Vector2.zero, new Vector2(2, 1)); area.color = new Color(1, 1, 1, 0); area.gameObject.AddComponent<Button>();
        var look = Child<Image>(area.transform, "Look", Vector2.zero, new Vector2(2, 1), raycast: false);
        Ready(c);
        Assert.AreEqual("PanelB/TextButton", S(Hit(), "elementId"), "the visible control (its look is a non-raycast child) is the hit, not the background behind it"); Assert.AreEqual("button", S(Hit(), "category"));
        look.color = new Color(1, 1, 1, 0); Refresh();
        Assert.AreEqual("PanelB/$canvas", S(Hit(), "elementId"), "a control with nothing visible is not a target");
        look.color = Color.white; var grp = area.gameObject.AddComponent<CanvasGroup>(); grp.alpha = 0f; Refresh();
        Assert.AreEqual("PanelB/$canvas", S(Hit(), "elementId"), "a control faded out by its CanvasGroup is not a target (the alpha is inherited by the look)");
    }

    // ====================================================================== plane vs graphic, geometry
    [Test]
    public void CanvasPlaneIntersection_IsNotAGraphicHit()
    {
        var c = NewCanvas("PanelP", new Vector3(0, 0, 3), new Vector2(4, 3));   // no background: only a small Image far to the left
        Child<Image>(c.transform, "Corner", new Vector2(-1.5f, 0), new Vector2(0.5f, 0.5f));
        Ready(c);
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _), "the ray crosses the Canvas plane inside its rectangle but touches no Graphic");
        Assert.IsTrue(Cast(Vector3.zero, new Vector3(-1.5f, 0, 3).normalized, out var h)); Assert.AreEqual("PanelP/Corner", S(h, "elementId"));
    }

    [Test]
    public void Geometry_BehindOrigin_BeyondMaxDistance_Parallel_AndBackFace_AreNotHit()
    {
        var c = NewCanvas("PanelX", new Vector3(0, 0, 3), new Vector2(4, 3)); c.AddComponent<Image>();
        Ready(c);
        Assert.IsFalse(Cast(Vector3.zero, Vector3.back, out _), "behind the origin");
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _, Opts(o => Set(o, "maxDistance", 2.5f))), "beyond the ray length");
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out _, Opts(o => Set(o, "maxDistance", 3.5f))));
        Assert.IsFalse(Cast(new Vector3(0, 0, 3), Vector3.right, out _), "ray parallel to the Canvas plane");
        c.transform.rotation = Quaternion.Euler(0, 180, 0); Refresh();
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _), "the back of a canvas is ignored (GraphicRaycaster.ignoreReversedGraphics)");
        c.GetComponent<GraphicRaycaster>().ignoreReversedGraphics = false;
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out _), "unless the raycaster says reversed graphics count");
    }

    // ====================================================================== sorting / overlap
    [Test]
    public void SiblingOverlap_LaterSiblingWins_AndNearerCanvasWinsAtEqualSorting_AndSortingOrderOverridesDistance()
    {
        var a = NewCanvas("CanvasNear", new Vector3(0, 0, 3), new Vector2(4, 3)); var near = Child<Image>(a.transform, "NearImg", Vector2.zero, new Vector2(2, 2));
        var first = Child<Image>(a.transform, "First", Vector2.zero, new Vector2(1, 1)); var second = Child<Image>(a.transform, "Second", Vector2.zero, new Vector2(1, 1));
        var b = NewCanvas("CanvasFar", new Vector3(0, 0, 5), new Vector2(4, 3)); Child<Image>(b.transform, "FarImg", Vector2.zero, new Vector2(2, 2));
        Ready(a, b);
        Assert.AreEqual("CanvasNear/Second", S(Hit(), "elementId"), "inside one Canvas the later sibling (higher depth) is on top");
        Assert.GreaterOrEqual(Get<int>(Hit(), "graphicsUnderRay"), 3, "several Graphics lie under the ray; exactly one is returned");
        a.SetActive(false); Refresh();
        Assert.AreEqual("CanvasFar/FarImg", S(Hit(), "elementId"));
        a.SetActive(true); Refresh();
        Assert.AreEqual("CanvasNear/Second", S(Hit(), "elementId"), "equal sorting: the nearer Canvas wins");
        var cb = b.GetComponent<Canvas>(); cb.overrideSorting = true; cb.sortingOrder = 10; Refresh();
        Assert.AreEqual("CanvasFar/FarImg", S(Hit(), "elementId"), "a higher sorting order wins over distance (EventSystem order)");
        Assert.AreEqual(10, Get<int>(Hit(), "canvasSortingOrder"));
    }

    // ====================================================================== render modes / camera
    [Test]
    public void ScreenSpaceOverlay_IsUnsupported_NotHit_AndCounted()
    {
        var c = NewCanvas("OverlayPanel", Vector3.zero, new Vector2(4, 3), RenderMode.ScreenSpaceOverlay); c.AddComponent<Image>();
        Ready(c);
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _)); Assert.AreEqual(1L, Counter("CanvasesSkippedOverlay"));
    }

    [Test]
    public void ScreenSpaceCamera_WithCamera_IsTestedLikeWorldSpace_WithoutCamera_FailsSafe()
    {
        var c = NewCanvas("SscPanel", Vector3.zero, new Vector2(4, 3), RenderMode.ScreenSpaceCamera);
        var canvas = c.GetComponent<Canvas>(); canvas.worldCamera = null; c.AddComponent<Image>(); Ready(c);
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _)); Assert.AreEqual(1L, Counter("CanvasesSkippedNoEventCamera") + Counter("CanvasesSkippedOverlay"), "no camera assigned = behaves as Overlay: nothing is rendered for the headset; skipped and counted (no-camera " + Counter("CanvasesSkippedNoEventCamera") + ", overlay " + Counter("CanvasesSkippedOverlay") + ")");
        // with a camera the engine places the Canvas in the world at plane distance in front of that camera while it renders: the geometry test then applies as for World Space
        canvas.worldCamera = _cam; canvas.planeDistance = 3f; _cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        Refresh();
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out var h), "Screen Space - Camera with an assigned camera is supported"); Assert.AreEqual("ScreenSpaceCamera", S(h, "renderMode"));
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _, Opts(o => Set(o, "supportScreenSpaceCamera", false))), "can be switched off");
    }

    [Test]
    public void MissingOrUnusableEventCamera_FailsSafely_NoExceptionNoHit()
    {
        var c = NewCanvas("PanelC", new Vector3(0, 0, 3), new Vector2(4, 3)); c.AddComponent<Image>(); Ready(c);
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out _));
        var noCam = GetStatic<object>(OptT, "Default");   // fallbackCamera = null and no Camera.main in this scene
        if (Camera.main == null)
        {
            Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _, noCam)); Assert.GreaterOrEqual(Counter("CanvasesSkippedNoEventCamera"), 1L);
        }
        _cam.cullingMask = 0;   // a camera that renders none of the Canvas's layers: the participant cannot see it
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _)); Assert.GreaterOrEqual(Counter("CanvasesSkippedLayerNotRendered"), 1L);
        _cam.cullingMask = ~0;
        UnityEngine.Object.DestroyImmediate(_cam.gameObject);   // destroyed event camera must not throw
        Assert.DoesNotThrow(() => Cast(Vector3.zero, Vector3.forward, out _, GetStatic<object>(OptT, "Default")));
    }

    [Test]
    public void CanvasWithoutGraphicRaycaster_IsSkipped_ByDefault_AndTestedWhenNotRequired()
    {
        var c = NewCanvas("PanelNR", new Vector3(0, 0, 3), new Vector2(4, 3), raycaster: false); c.AddComponent<Image>(); Ready(c);
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _)); Assert.AreEqual(1L, Counter("CanvasesSkippedNoGraphicRaycaster"));
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out _, Opts(o => Set(o, "requireGraphicRaycaster", false))));
    }

    // ====================================================================== GazeUIElement (opt-in)
    [Test]
    public void GazeUIElement_CustomIdentity_RecordAsTargetFalse_HitWhenNotRaycastTarget_ApplyToChildren_AndContainerNeverReplacesChild()
    {
        var c = NewCanvas("PanelE", new Vector3(0, 0, 3), new Vector2(4, 3));
        var bg = c.AddComponent<Image>(); var bgEl = c.AddComponent(T("GazeUIElement")); Set(bgEl, "recordAsTarget", false);   // decorative background: never a target
        var preview = Child<RawImage>(c.transform, "BuildingImage", new Vector2(1, 0), new Vector2(1, 1), raycast: false);
        var pEl = preview.gameObject.AddComponent(T("GazeUIElement")); Set(pEl, "stableId", "d1_preview"); Set(pEl, "displayName", "Destination 1 preview"); Set(pEl, "category", Enum.ToObject(T("GazeUiCategory"), 7)); Set(pEl, "roiLabel", "ROI_TARGET_1"); Set(pEl, "hitWhenNotRaycastTarget", true);
        var cont = Child<Image>(c.transform, "Row", new Vector2(-1, 0), new Vector2(1.2f, 1.2f)); var contEl = cont.gameObject.AddComponent(T("GazeUIElement")); Set(contEl, "stableId", "row_container"); Set(contEl, "applyToChildren", false);
        var childBtn = Child<Image>(cont.transform, "OptionA", Vector2.zero, new Vector2(0.5f, 0.5f)); childBtn.gameObject.AddComponent<Button>();
        Ready(c);
        // 1. recordAsTarget=false background is skipped; with nothing else under the ray -> no hit
        Assert.IsFalse(Cast(Vector3.zero, new Vector3(0, 1.2f, 3).normalized, out _), "a non-target background does not catch the ray");
        // 2. preview: raycastTarget=false but opted in -> hit, custom id / name / category / ROI
        Assert.IsTrue(Cast(Vector3.zero, new Vector3(1, 0, 3).normalized, out var h));
        Assert.AreEqual("d1_preview", S(h, "elementId")); Assert.AreEqual("Destination 1 preview", S(h, "elementName")); Assert.AreEqual("target_preview", S(h, "category")); Assert.AreEqual("ROI_TARGET_1", S(h, "roiLabel")); Assert.IsTrue(Get<bool>(h, "hasGazeElement"));
        // 3. a container without applyToChildren never replaces its child: the child control is the hit with its OWN derived identity
        Assert.IsTrue(Cast(Vector3.zero, new Vector3(-1, 0, 3).normalized, out h));
        Assert.AreEqual("PanelE/Row/OptionA", S(h, "elementId")); Assert.AreEqual("button", S(h, "category")); Assert.IsFalse(Get<bool>(h, "hasGazeElement"));
        // 4. the container area outside the child is the container itself (its own stable id)
        Assert.IsTrue(Cast(Vector3.zero, new Vector3(-1.5f, 0, 3).normalized, out h)); Assert.AreEqual("row_container", S(h, "elementId"));
        // 5. applyToChildren: the child takes the container's identity
        Set(contEl, "applyToChildren", true); CallStatic(RegT, "ClearForTests"); Ready(c); var fresh = Activator.CreateInstance(RayT); _ray = fresh;
        Assert.IsTrue(Cast(Vector3.zero, new Vector3(-1, 0, 3).normalized, out h)); Assert.AreEqual("row_container", S(h, "elementId"), "applyToChildren = true: a button's child graphics report the control");
    }

    // ====================================================================== Panel-level identity (one GazeUIElement on the panel root, applyToChildren)
    // A panel like the experiment's ModalSingleButton clones: root Image background, text labels and a preview RawImage that are NOT raycast targets, and a button whose Image is a transparent hit area.
    GameObject PanelWithMarker(string name, string id, Vector3 pos)
    {
        var c = NewCanvas(name, pos, new Vector2(4, 3));
        var bg = c.AddComponent<Image>(); bg.color = new Color(1, 1, 1, 0.75f);
        var el = c.AddComponent(T("GazeUIElement")); Set(el, "stableId", id); Set(el, "displayName", id + " name"); Set(el, "category", Enum.ToObject(T("GazeUiCategory"), 1)); Set(el, "roiLabel", id); Set(el, "applyToChildren", true);
        var head = Child<Text>(c.transform, "Header Text", new Vector2(0, 1.0f), new Vector2(3, 0.5f), raycast: false); head.text = "Header";
        var modal = Child<Text>(c.transform, "Modal Text", new Vector2(-0.8f, 0.2f), new Vector2(1.6f, 0.6f), raycast: false); modal.text = "Message";
        Child<RawImage>(c.transform, "BuildingImage", new Vector2(1.2f, 0.2f), new Vector2(1.2f, 1.0f), raycast: false);
        var btn = Child<Image>(c.transform, "TextButton", new Vector2(0, -1.0f), new Vector2(2, 0.5f)); btn.color = new Color(1, 1, 1, 0); btn.gameObject.AddComponent<Button>();
        Child<Image>(btn.transform, "Outline", Vector2.zero, new Vector2(2, 0.5f), raycast: false);
        var lab = Child<Text>(btn.transform, "Label", Vector2.zero, new Vector2(1, 0.3f), raycast: false); lab.text = "Confirm";
        return c;
    }

    // world point of a local anchored position on a panel at z = 3 (canvas centre at pos)
    static Vector3 At(GameObject panel, float x, float y) => panel.transform.position + new Vector3(x, y, 0f);

    [Test]
    public void Panel_EveryVisibleArea_ResolvesToTheOnePanelIdentity_NoChildBecomesASeparateTarget_OutsideIsNoHit()
    {
        var p = PanelWithMarker("PanelRoot", "DESTINATION1_PANEL", new Vector3(0, 0, 3)); Ready(p);
        var areas = new Dictionary<string, Vector3>
        {
            { "background", At(p, -1.8f, -0.4f) }, { "header text", At(p, 0, 1.0f) }, { "modal text", At(p, -0.8f, 0.2f) }, { "building preview image", At(p, 1.2f, 0.2f) },
            { "button hit area", At(p, 0, -1.0f) }, { "button outline / label", At(p, 0.3f, -1.0f) }
        };
        foreach (var kv in areas)
        {
            Assert.IsTrue(Cast(Vector3.zero, kv.Value.normalized, out var h), kv.Key + " is a hit");
            Assert.AreEqual("DESTINATION1_PANEL", S(h, "elementId"), kv.Key); Assert.AreEqual("DESTINATION1_PANEL", S(h, "roiLabel"), kv.Key); Assert.AreEqual("DESTINATION1_PANEL name", S(h, "elementName"), kv.Key);
            Assert.AreEqual("panel", S(h, "category"), kv.Key); Assert.AreEqual("", S(h, "interactable"), kv.Key + ": the control crossed inside the panel does not leak into the record"); Assert.IsTrue(Get<bool>(h, "hasGazeElement"), kv.Key);
            Assert.AreEqual("PanelRoot", S(h, "canvasId"), kv.Key);
        }
        Assert.IsFalse(Cast(Vector3.zero, At(p, 2.6f, 0).normalized, out _), "outside the panel rectangle");
        Assert.IsFalse(Cast(Vector3.zero, At(p, 0, 2.0f).normalized, out _), "above the panel");
        Assert.AreEqual(1, p.GetComponentsInChildren(T("GazeUIElement"), true).Length, "exactly one marker, on the root");
    }

    [Test]
    public void Panel_InactiveIsNotHit_ReactivationDoesNotDuplicateRegistryEntries_DestroyedPanelIsRemoved()
    {
        var p = PanelWithMarker("PanelLife", "PART2_START_PANEL", new Vector3(0, 0, 3)); Ready(p);
        Assert.AreEqual(1, GetStatic<int>(RegT, "Count"));
        for (int i = 0; i < 4; i++)
        {
            p.SetActive(false); Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _), "inactive panel is not hit (cycle " + i + ")");
            p.SetActive(true); Refresh(); CallStatic(RegT, "RegisterHierarchy", p);   // UIManager registers on every instantiate; GazeUIElement.OnEnable registers on every activation
            Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out var h)); Assert.AreEqual("PART2_START_PANEL", S(h, "elementId"));
        }
        Assert.AreEqual(1, GetStatic<int>(RegT, "Count"), "repeated activation / registration never creates a second entry");
        p.GetComponent<Canvas>().enabled = false; Refresh(); Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _), "a disabled canvas (UIManager settle phase) is not hit");
        UnityEngine.Object.DestroyImmediate(p);
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _)); Assert.AreEqual(0, GetStatic<int>(RegT, "Count"), "a destroyed clone is pruned from the registry");
    }

    [Test]
    public void Panel_RuntimeClone_InheritsTheSingleMarker_AndIdentity_AndTwoActivePanelsStaySeparate()
    {
        var template = PanelWithMarker("PanelTemplate", "DESTINATION1_PANEL", new Vector3(-3, 0, 3)); template.SetActive(false);
        var other = PanelWithMarker("PanelOther", "DESTINATION2_PANEL", new Vector3(3, 0, 3));
        var clone = UnityEngine.Object.Instantiate(template); _spawned.Add(clone); clone.name = "PanelTemplate(Clone)"; clone.SetActive(true);
        Ready(clone, other);
        Assert.AreEqual(1, clone.GetComponentsInChildren(T("GazeUIElement"), true).Length, "the clone carries exactly one marker");
        Assert.AreEqual("DESTINATION1_PANEL", Get<string>(clone.GetComponent(T("GazeUIElement")), "stableId"));
        Assert.IsTrue(Cast(Vector3.zero, At(clone, 0, 1.0f).normalized, out var h)); Assert.AreEqual("DESTINATION1_PANEL", S(h, "elementId")); Assert.AreEqual("PanelTemplate", S(h, "canvasId"), "(Clone) is stripped from the canvas id; the panel id comes from the marker");
        Assert.IsTrue(Cast(Vector3.zero, At(other, 1.2f, 0.2f).normalized, out h)); Assert.AreEqual("DESTINATION2_PANEL", S(h, "elementId"), "a simultaneously active panel keeps its own identity");
        // overlapping panels: the nearer one is the hit
        other.transform.position = new Vector3(-3, 0, 2); Refresh();
        Assert.IsTrue(Cast(Vector3.zero, At(clone, 0, 1.0f).normalized, out h)); Assert.AreEqual("DESTINATION2_PANEL", S(h, "elementId"));
    }

    [Test]
    public void Panel_ShrunkRepositionedAndHeadLockedMotion_StaysDetectable_WithTheSameIdentity()
    {
        var p = PanelWithMarker("PanelShrunk", "DESTINATION2_SHRUNK_PANEL", new Vector3(0.4f, 0.2f, 1.0f)); p.transform.localScale = Vector3.one * 0.22f; Ready(p);   // the UIManager corner-HUD scale
        Assert.IsTrue(Cast(Vector3.zero, p.transform.position.normalized, out var h)); Assert.AreEqual("DESTINATION2_SHRUNK_PANEL", S(h, "elementId"));
        for (int i = 0; i < 5; i++)   // HeadLockedPanel moves the transform every frame; identity lives on the marker, not on the position
        {
            p.transform.position = new Vector3(0.4f + 0.3f * i, 0.2f - 0.1f * i, 1.0f + 0.2f * i); p.transform.rotation = Quaternion.Euler(0, 15 * i, 0); Refresh();
            Assert.IsTrue(Cast(Vector3.zero, (p.transform.position + p.transform.right * 0.2f * 0.22f).normalized, out h), "step " + i); Assert.AreEqual("DESTINATION2_SHRUNK_PANEL", S(h, "elementId"), "step " + i);
        }
    }

    // ====================================================================== registry lifecycle
    [Test]
    public void Registry_RegistersOnce_PrunesDestroyed_AndRescanFindsUnregisteredCanvases()
    {
        var c = NewCanvas("PanelL", new Vector3(0, 0, 3), new Vector2(4, 3)); c.AddComponent<Image>();
        CallStatic(RegT, "RegisterHierarchy", c); CallStatic(RegT, "RegisterHierarchy", c);
        Assert.AreEqual(1, GetStatic<int>(RegT, "Count"), "idempotent");
        UnityEngine.Object.DestroyImmediate(c); Refresh();
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _)); Assert.AreEqual(0, GetStatic<int>(RegT, "Count"), "a destroyed Canvas is pruned while walking");
        var late = NewCanvas("PanelLate", new Vector3(0, 0, 3), new Vector2(4, 3)); late.AddComponent<Image>(); Refresh();   // created outside UIManager, never registered
        Assert.IsFalse(Cast(Vector3.zero, Vector3.forward, out _));
        int scans = GetStatic<int>(RegT, "SceneScans"); CallStatic(RegT, "RescanScene");
        Assert.AreEqual(scans + 1, GetStatic<int>(RegT, "SceneScans")); Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out var h)); Assert.AreEqual("PanelLate/$canvas", S(h, "elementId"));
    }

    [Test]
    public void CloneSuffix_IsStripped_FromCanvasAndElementIds()
    {
        var c = NewCanvas("ExperimentPhaseDestination1(Clone)", new Vector3(0, 0, 3), new Vector2(4, 3)); c.AddComponent<Image>();
        var b = Child<Image>(c.transform, "TextButton", Vector2.zero, new Vector2(1, 1)); b.gameObject.AddComponent<Button>(); Ready(c);
        Assert.IsTrue(Cast(Vector3.zero, Vector3.forward, out var h)); Assert.AreEqual("ExperimentPhaseDestination1", S(h, "canvasId")); Assert.AreEqual("ExperimentPhaseDestination1/TextButton", S(h, "elementId"));
    }
}

// ====================================================================================================== hit-priority policy (pure)
[Category("Synthetic")]
public class GazeTargetResolverTests
{
    static readonly Type R = T("GazeTargetResolver");
    static (string cls, string basis) Resolve(bool uiEval, bool uiHit, float uiD, bool wHit, float wD, bool occ)
    {
        var p = CallStatic(R, "Resolve", uiEval, uiHit, uiD, wHit, wD, occ);
        return (Get<string>(p, "cls"), Get<string>(p, "basis"));
    }

    [Test]
    public void Resolve_Table()
    {
        Assert.AreEqual(("NoHit", "ui_not_evaluated"), Resolve(false, false, 0, false, 0, false));
        Assert.AreEqual(("WorldObject", "ui_not_evaluated"), Resolve(false, false, 0, true, 10, false), "head-fallback row: the physical result stands, UI was not evaluated");
        Assert.AreEqual(("NoHit", "no_hit"), Resolve(true, false, 0, false, 0, false));
        Assert.AreEqual(("WorldObject", "world_only"), Resolve(true, false, 0, true, 10, false));
        Assert.AreEqual(("CanvasUI", "ui_only"), Resolve(true, true, 3, false, 0, false));
        Assert.AreEqual(("CanvasUI", "ui_in_front"), Resolve(true, true, 3, true, 10, false), "UI in front of the world wins (visibility was established by the UI raycaster)");
        Assert.AreEqual(("CanvasUI", "ui_in_front"), Resolve(true, true, 3.0005f, true, 3f, false), "coplanar within 1 mm: the UI is drawn over the surface");
        Assert.AreEqual(("WorldObject", "world_occludes_ui"), Resolve(true, true, 10, true, 3, true), "UI behind an established opaque occluder never wins");
        Assert.AreEqual(("Uncertain", "occlusion_not_established"), Resolve(true, true, 10, true, 3, false), "occlusion cannot be established: primary is uncertain, both channels are kept");
    }

    [Test]
    public void IsEstablishedOccluder_OpaqueRendererNonTrigger_True_TriggerColliderOnlyTransparentShadowsOnly_False()
    {
        var made = new List<GameObject>();
        try
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); made.Add(cube);
            Assert.IsTrue((bool)CallStatic(R, "IsEstablishedOccluder", cube.GetComponent<Collider>()), "opaque, enabled renderer + solid collider");
            cube.GetComponent<Collider>().isTrigger = true; Assert.IsFalse((bool)CallStatic(R, "IsEstablishedOccluder", cube.GetComponent<Collider>()), "trigger volumes are not occluders");
            cube.GetComponent<Collider>().isTrigger = false; cube.GetComponent<Renderer>().enabled = false; Assert.IsFalse((bool)CallStatic(R, "IsEstablishedOccluder", cube.GetComponent<Collider>()), "invisible collider (disabled renderer)");
            cube.GetComponent<Renderer>().enabled = true;
            var ghost = new GameObject("ColliderOnly"); made.Add(ghost); ghost.AddComponent<BoxCollider>(); Assert.IsFalse((bool)CallStatic(R, "IsEstablishedOccluder", ghost.GetComponent<Collider>()), "no renderer at all");
            var glass = GameObject.CreatePrimitive(PrimitiveType.Cube); made.Add(glass); var mat = new Material(glass.GetComponent<Renderer>().sharedMaterial) { renderQueue = 3000 }; glass.GetComponent<Renderer>().sharedMaterial = mat;
            Assert.IsFalse((bool)CallStatic(R, "IsEstablishedOccluder", glass.GetComponent<Collider>()), "transparent queue: not an established opaque occluder");
            UnityEngine.Object.DestroyImmediate(mat);
            cube.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly; Assert.IsFalse((bool)CallStatic(R, "IsEstablishedOccluder", cube.GetComponent<Collider>()), "shadows-only renderer is not visible");
            Assert.IsFalse((bool)CallStatic(R, "IsEstablishedOccluder", new object[] { null }));
        }
        finally { foreach (var g in made) if (g != null) UnityEngine.Object.DestroyImmediate(g); }
    }

    [Test]
    public void AoiClassifier_UiRoiExtension_UsesTheExistingVocabulary()
    {
        var a = T("AoiClassifier");
        Assert.AreEqual("UI", GetStatic<string>(a, "UiSemanticClass"), "the same class name as the existing collider priority 7");
        Assert.AreEqual("UI_BUTTON", (string)CallStatic(a, "UiRoiLabel", "button", ""));
        Assert.AreEqual("MY_ROI", (string)CallStatic(a, "UiRoiLabel", "button", "MY_ROI"));
        Assert.AreEqual("UI_OTHER", (string)CallStatic(a, "UiRoiLabel", "", null));
        Assert.AreEqual("UI_X", (string)CallStatic(a, "PrimaryRoi", "CanvasUI", "UI_X", "BUILDING"));
        Assert.AreEqual("BUILDING", (string)CallStatic(a, "PrimaryRoi", "WorldObject", "UI_X", "BUILDING"));
        Assert.AreEqual("", (string)CallStatic(a, "PrimaryRoi", "Uncertain", "UI_X", "BUILDING"));
    }
}
