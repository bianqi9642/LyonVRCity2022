// GazeUiRealSceneTests.cs  (EditMode, Editor-only assembly)  -  REAL SCENE, no headset
// Audit + test of the Canvas UI gaze path against the REAL experiment panels: the 12 panel templates wired into UIManager in Assets/Scenes/LyonVRVity35.unity (Part 1/2/3 start / continue / end, Destination 1 / 2 full
// and shrunk). Each template is instantiated (exactly what UIManager.InstantiateTemplate does), placed in front of a test camera, and a dense grid of gaze rays is cast with the real GazeUiRaycaster.
//
// WHAT IT ESTABLISHES (offline): every template Canvas is World Space with a GraphicRaycaster and has no Collider (so the physical gaze ray cannot see it); each of the 12 templates carries exactly ONE
// GazeUIElement on its root (panel-level identity, applyToChildren) and none on any child; runtime clones inherit it; every visible Graphic of every panel - background, heading, modal text, BuildingImage
// (raycastTarget = false), the transparent TextButton hit area, outline, label - resolves to that one panel identity; nothing outside the panel hits; inactive / destroyed / re-activated / shrunk / moved
// panels behave. It prints the Canvas audit table used in docs/gaze_ui_detection.md.
// WHAT IT DOES NOT ESTABLISH: what the Quest renders, whether the participant's eye really falls there, the head-locked follow of the shrunk HUD, or XR controller delivery (headset only).
// The scene is opened ADDITIVELY and closed without saving; Ignored when the scene file is absent or already open in the Editor. Category "RealScene".
//   -runTests -testPlatform EditMode -assemblyNames GazeRecorder.EditModeTests -testFilter GazeUiRealSceneTests

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[Category("RealScene")]
public class GazeUiRealSceneTests
{
    const string ScenePath = "Assets/Scenes/LyonVRVity35.unity";
    static readonly string[] Slots =
    {
        "part1StartPanelTemplate", "part1ContinuePanelTemplate", "part1EndPanelTemplate", "part2StartPanelTemplate", "part2ContinuePanelTemplate", "part2EndPanelTemplate",
        "part3StartPanelTemplate", "part3Dest1FullTemplate", "part3Dest1ShrunkTemplate", "part3Dest2FullTemplate", "part3Dest2ShrunkTemplate", "part3EndPanelTemplate"
    };

    static Type TypeOf(string n) { var t = Type.GetType(n + ", Assembly-CSharp"); Assert.IsNotNull(t, n); return t; }

    Scene _scene; bool _opened; Component _ui;
    readonly Dictionary<string, GameObject> _templates = new Dictionary<string, GameObject>();
    readonly List<GameObject> _made = new List<GameObject>();
    Camera _cam; object _ray; GameObject _placed;

    [OneTimeSetUp]
    public void Open()
    {
        if (!File.Exists(ScenePath)) Assert.Ignore("scene file not present: " + ScenePath);
        for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).path == ScenePath) Assert.Ignore("the experiment scene is open in the Editor; close it (or run in batch mode)");
        bool ignore = UnityEngine.TestTools.LogAssert.ignoreFailingMessages; UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
        try { _scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive); _opened = true; } finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = ignore; }
        _ui = (Component)_scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(TypeOf("UIManager"), true)).FirstOrDefault();
        Assert.IsNotNull(_ui, "UIManager not found in the scene");
        var so = new SerializedObject(_ui);
        foreach (string slot in Slots)
        {
            var go = so.FindProperty(slot).objectReferenceValue as GameObject;
            Assert.IsNotNull(go, slot + " is not assigned"); _templates[slot] = go;
        }
    }

    [OneTimeTearDown]
    public void Close() { if (_opened) { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true; try { EditorSceneManager.CloseScene(_scene, true); } finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false; } } }

    [SetUp]
    public void SetUp()
    {
        TypeOf("GazeUiRegistry").GetMethod("ClearForTests").Invoke(null, null);
        _ray = Activator.CreateInstance(TypeOf("GazeUiRaycaster"));
        var cg = new GameObject("RealSceneUiCam"); _made.Add(cg); _cam = cg.AddComponent<Camera>(); _cam.enabled = false; cg.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        Assume.That(_cam.pixelWidth > 0, "no usable camera viewport");
    }

    [TearDown]
    public void TearDown() { _placed = null; foreach (var g in _made) if (g != null) UnityEngine.Object.DestroyImmediate(g); _made.Clear(); }

    // a placed copy of a template, like UIManager.InstantiateTemplate + PositionPanelInFront (5 m ahead, facing the camera)
    GameObject Place(string slot)
    {
        if (_placed != null) UnityEngine.Object.DestroyImmediate(_placed);   // one panel at a time: leftovers would overlap the next one at the same place
        var go = UnityEngine.Object.Instantiate(_templates[slot]); go.SetActive(true); _made.Add(go); _placed = go;
        go.name = _templates[slot].name + "(Clone)";
        go.transform.SetPositionAndRotation(new Vector3(0f, 0f, 5f), Quaternion.identity);
        TypeOf("GazeUiRegistry").GetMethod("RegisterHierarchy").Invoke(null, new object[] { go });
        Canvas.ForceUpdateCanvases();
        foreach (var rt in go.GetComponentsInChildren<RectTransform>(true)) LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        Canvas.ForceUpdateCanvases(); Render();
        return go;
    }

    void Render() { _cam.enabled = true; _cam.Render(); _cam.enabled = false; }   // a camera render assigns Graphic.depth, the "has been drawn" marker GraphicRaycaster itself requires

    bool Cast(Vector3 dir, out object hit)
    {
        var optT = Type.GetType("GazeUiRaycaster+Options, Assembly-CSharp");
        object opt = optT.GetProperty("Default", BindingFlags.Static | BindingFlags.Public).GetValue(null);
        optT.GetField("fallbackCamera").SetValue(opt, _cam);
        var args = new object[] { Vector3.zero, dir, opt, null };
        bool r = (bool)_ray.GetType().GetMethod("Raycast").Invoke(_ray, args);
        hit = args[3]; return r;
    }
    static string F2(object o) => o.GetType().GetField("distance").GetValue(o).ToString();
    static string F(object o, string n) => (string)o.GetType().GetField(n).GetValue(o);

    // every Graphic's world-space centre as a ray target, plus a grid over the root rectangle
    List<Vector3> GridTargets(GameObject root, int nx = 41, int ny = 25)
    {
        var rt = (RectTransform)root.transform; var c = new Vector3[4]; rt.GetWorldCorners(c);
        var l = new List<Vector3>();
        for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) l.Add(Vector3.Lerp(Vector3.Lerp(c[0], c[3], (i + 0.5f) / nx), Vector3.Lerp(c[1], c[2], (i + 0.5f) / nx), (j + 0.5f) / ny));
        foreach (var g in root.GetComponentsInChildren<Graphic>(false)) { var r = g.rectTransform; l.Add(r.TransformPoint(r.rect.center)); }
        return l;
    }

    [Test]
    public void AllTwelvePanels_AreWorldSpaceCanvases_WithGraphicRaycaster_AndNoCollider_AuditTable()
    {
        var lines = new List<string> { "template | render mode | worldCamera | sorting | GraphicRaycaster | root components | CanvasGroup | colliders | graphics (raycastTarget) | template active" };
        foreach (string slot in Slots)
        {
            var t = _templates[slot]; var canvas = t.GetComponent<Canvas>(); Assert.IsNotNull(canvas, slot + ": the template root is a Canvas");
            Assert.AreEqual(RenderMode.WorldSpace, canvas.renderMode, slot); Assert.IsNull(canvas.worldCamera, slot + ": no event camera is assigned in the asset (GraphicRaycaster falls back to Camera.main)");
            Assert.IsNotNull(t.GetComponent<GraphicRaycaster>(), slot + ": GraphicRaycaster"); Assert.AreEqual(0, canvas.sortingOrder); Assert.IsFalse(canvas.overrideSorting);
            Assert.AreEqual(0, t.GetComponentsInChildren<Collider>(true).Length, slot + ": no Collider anywhere in the panel - the physical gaze ray cannot hit a panel");
            Assert.AreEqual(0, t.GetComponentsInChildren<CanvasGroup>(true).Length, slot + ": no CanvasGroup");
            Assert.AreEqual(0, t.GetComponentsInChildren<Canvas>(true).Count(c => c != canvas), slot + ": no nested canvas");
            var gs = t.GetComponentsInChildren<Graphic>(true);
            lines.Add(string.Join(" | ", new[] { t.name, canvas.renderMode.ToString(), "none", canvas.sortingOrder.ToString(), "yes", string.Join("+", t.GetComponents<Component>().Select(m => m == null ? "MISSING_SCRIPT" : m.GetType().Name).Where(n => n != "RectTransform" && n != "CanvasRenderer")), "none", "0",
                string.Join(", ", gs.Select(g => g.name + "[" + g.GetType().Name + (g.raycastTarget ? ":T" : ":f") + (g.gameObject.activeSelf ? "" : ":inactive") + "]")), t.activeSelf ? "yes" : "no" }));
        }
        Debug.Log("[GazeUiRealScene] Canvas audit\n" + string.Join("\n", lines));
    }

    // slot -> the panel-level identity configured on the template root
    static readonly Dictionary<string, string> PanelIds = new Dictionary<string, string>
    {
        { "part1StartPanelTemplate", "PART1_START_PANEL" }, { "part1ContinuePanelTemplate", "PART1_CONTINUE_PANEL" }, { "part1EndPanelTemplate", "PART1_END_PANEL" },
        { "part2StartPanelTemplate", "PART2_START_PANEL" }, { "part2ContinuePanelTemplate", "PART2_CONTINUE_PANEL" }, { "part2EndPanelTemplate", "PART2_END_PANEL" },
        { "part3StartPanelTemplate", "PART3_START_PANEL" }, { "part3EndPanelTemplate", "PART3_END_PANEL" },
        { "part3Dest1FullTemplate", "DESTINATION1_PANEL" }, { "part3Dest2FullTemplate", "DESTINATION2_PANEL" },
        { "part3Dest1ShrunkTemplate", "DESTINATION1_SHRUNK_PANEL" }, { "part3Dest2ShrunkTemplate", "DESTINATION2_SHRUNK_PANEL" }
    };

    Type MarkerType => TypeOf("GazeUIElement");
    static T Field<T>(Component c, string n) => (T)c.GetType().GetField(n).GetValue(c);

    [Test]
    public void Configuration_EachPanelTemplateHasExactlyOneRootMarker_NoChildMarker_UniqueStableIds_PanelCategory()
    {
        Assert.AreEqual(Slots.Length, PanelIds.Count); CollectionAssert.AreEquivalent(Slots, PanelIds.Keys, "every UIManager panel slot has a configured identity");
        var ids = new HashSet<string>();
        foreach (string slot in Slots)
        {
            var t = _templates[slot];
            var all = t.GetComponentsInChildren(MarkerType, true);
            Assert.AreEqual(1, all.Length, slot + ": exactly one GazeUIElement in the whole panel");
            Assert.AreEqual(t, all[0].gameObject, slot + ": the marker is on the panel ROOT, not on a child Graphic");
            var m = all[0];
            Assert.AreEqual(PanelIds[slot], Field<string>(m, "stableId")); Assert.AreEqual(PanelIds[slot], Field<string>(m, "roiLabel"), "panel-level ROI label"); Assert.IsFalse(string.IsNullOrEmpty(Field<string>(m, "displayName")));
            Assert.AreEqual(1, (int)Field<object>(m, "category"), slot + ": category Panel (recorded as panel)"); Assert.IsTrue(Field<bool>(m, "recordAsTarget")); Assert.IsTrue(Field<bool>(m, "applyToChildren"), slot + ": children resolve to the panel");
            Assert.IsFalse(Field<bool>(m, "hitWhenNotRaycastTarget"), slot + ": the root background already covers the panel; no raycast override");
            Assert.IsTrue(ids.Add(Field<string>(m, "stableId")), slot + ": the id is unique");
            Assert.IsTrue(Field<string>(m, "stableId").EndsWith("_PANEL"));
        }
        Assert.AreEqual(12, ids.Count);
    }

    [Test]
    public void Configuration_RuntimeClones_InheritTheMarkerAndIdentity()
    {
        foreach (string slot in Slots)
        {
            var clone = Place(slot);
            var all = clone.GetComponentsInChildren(MarkerType, true);
            Assert.AreEqual(1, all.Length, slot + ": the clone carries exactly one marker"); Assert.AreEqual(PanelIds[slot], Field<string>(all[0], "stableId"));
            Assert.IsTrue(Cast(Vector3.forward, out var h)); Assert.AreEqual(PanelIds[slot], F(h, "elementId"));
        }
    }

    [Test]
    public void AllVisibleAreasOfEveryPanel_ResolveToTheOnePanelIdentity_ButtonTextAndPreviewAreNotSeparateTargets()
    {
        foreach (string slot in Slots)
        {
            var go = Place(slot); string id = PanelIds[slot];
            var rects = new List<(string what, Vector3 point)>();
            foreach (var g in go.GetComponentsInChildren<Graphic>(false))
            {
                var r = g.rectTransform; var c = new Vector3[4]; r.GetWorldCorners(c);
                rects.Add((g.name + " centre", r.TransformPoint(r.rect.center)));
                for (int k = 0; k < 4; k++) rects.Add((g.name + " corner " + k, Vector3.Lerp(c[k], r.TransformPoint(r.rect.center), 0.06f)));   // just inside each corner
            }
            foreach (var target in GridTargets(go)) rects.Add(("grid", target));
            int hits = 0; var misses = new List<string>();
            var rootRt = (RectTransform)go.transform; int skipped = 0;
            foreach (var (what, pt) in rects)
            {
                // the visible panel area is the root rectangle: a child Graphic's RECT may overhang it (the shrunk HUD's heading rect is 12 units taller than the 50-unit root; its glyphs are top-aligned inside the root)
                Vector3 local = rootRt.InverseTransformPoint(pt); if (!rootRt.rect.Contains(new Vector2(local.x, local.y))) { skipped++; continue; }
                if (!Cast(pt.normalized, out var h)) { misses.Add(what); continue; }
                hits++;
                Assert.AreEqual(id, F(h, "elementId"), slot + " / " + what); Assert.AreEqual(id, F(h, "roiLabel"), slot + " / " + what); Assert.AreEqual("panel", F(h, "category"), slot + " / " + what);
                Assert.AreEqual("", F(h, "interactable"), slot + " / " + what + ": no child control leaks into the record");
            }
            Assert.AreEqual(0, misses.Count, slot + ": every Graphic centre / corner inside the panel rectangle is a hit; misses: " + string.Join(", ", misses));
            Assert.LessOrEqual(skipped, 4, slot + ": only overhanging rect corners are skipped");
            Assert.Greater(hits, 20);
            // outside the panel: no hit
            var rt = (RectTransform)go.transform; var cc = new Vector3[4]; rt.GetWorldCorners(cc);
            Assert.IsFalse(Cast((cc[2] + (cc[2] - cc[0]) * 0.05f).normalized, out _), slot + ": beyond the top-right corner");
            Assert.IsFalse(Cast((cc[0] - (cc[2] - cc[0]) * 0.05f).normalized, out _), slot + ": beyond the bottom-left corner");
            // exactly one identity overall
            var seen = new HashSet<string>(); foreach (var target in GridTargets(go)) if (Cast(target.normalized, out var h2)) seen.Add(F(h2, "elementId"));
            CollectionAssert.AreEquivalent(new[] { id }, seen, slot);
        }
    }

    [Test]
    public void DestinationPanels_BuildingPreviewImageArea_IsAHitOnThePanel_DespiteRaycastTargetFalse()
    {
        foreach (string slot in new[] { "part3Dest1FullTemplate", "part3Dest2FullTemplate", "part3Dest1ShrunkTemplate", "part3Dest2ShrunkTemplate" })
        {
            var go = Place(slot); var img = go.GetComponentsInChildren<RawImage>(false).Single(r => r.name == "BuildingImage");
            Assert.IsFalse(img.raycastTarget, "unchanged: the preview image itself is not a raycast target");
            Assert.IsTrue(Cast(img.rectTransform.TransformPoint(img.rectTransform.rect.center).normalized, out var h), slot); Assert.AreEqual(PanelIds[slot], F(h, "elementId"));
        }
    }

    [Test]
    public void Lifecycle_ReactivationNoDuplicates_TwoActivePanelsSeparate_ShrunkRepositioned_DestroyedRemoved_InactiveNotHit()
    {
        var reg = TypeOf("GazeUiRegistry");
        var d1 = Place("part3Dest1FullTemplate"); var d1Canvas = d1.GetComponent<Canvas>();
        Assert.AreEqual(1, (int)reg.GetProperty("Count").GetValue(null));
        for (int i = 0; i < 3; i++)
        {
            d1.SetActive(false); Assert.IsFalse(Cast(Vector3.forward, out _), "inactive panel: no hit");
            d1.SetActive(true); Canvas.ForceUpdateCanvases(); Render(); reg.GetMethod("RegisterHierarchy").Invoke(null, new object[] { d1 });
            Assert.IsTrue(Cast(Vector3.forward, out var h)); Assert.AreEqual("DESTINATION1_PANEL", F(h, "elementId"));
        }
        Assert.AreEqual(1, (int)reg.GetProperty("Count").GetValue(null), "re-activation never duplicates the registry entry");
        // second, simultaneously active panel (a different template) off to the side
        var d2 = UnityEngine.Object.Instantiate(_templates["part3Dest2FullTemplate"]); d2.SetActive(true); _made.Add(d2); d2.transform.SetPositionAndRotation(new Vector3(2.5f, 0f, 4f), Quaternion.identity);
        reg.GetMethod("RegisterHierarchy").Invoke(null, new object[] { d2 }); Canvas.ForceUpdateCanvases(); Render();
        Assert.IsTrue(Cast(new Vector3(-3f, 0f, 5f).normalized, out var a)); Assert.AreEqual("DESTINATION1_PANEL", F(a, "elementId"));   // left part of D1, not covered by the nearer D2
        Assert.IsTrue(Cast(new Vector3(2.5f, 0f, 4f).normalized, out var b)); Assert.AreEqual("DESTINATION2_PANEL", F(b, "elementId"));
        // shrunk HUD: scaled and moved like UIManager.CreateAndAnchorShrunkPanel / HeadLockedPanel
        var s1 = UnityEngine.Object.Instantiate(_templates["part3Dest1ShrunkTemplate"]); s1.SetActive(true); _made.Add(s1); s1.transform.localScale = Vector3.one * 0.0022f;
        reg.GetMethod("RegisterHierarchy").Invoke(null, new object[] { s1 });
        for (int i = 0; i < 4; i++)
        {
            var sp = new Vector3(-0.5f - i * 0.1f, 0.3f + 0.05f * i, 1.5f + 0.2f * i); s1.transform.SetPositionAndRotation(sp, Quaternion.LookRotation(new Vector3(sp.x, 0f, sp.z))); Canvas.ForceUpdateCanvases(); Render();   // facing the viewer like HeadLockedPanel
            Assert.IsTrue(Cast(s1.transform.position.normalized, out var h), "shrunk step " + i); Assert.AreEqual("DESTINATION1_SHRUNK_PANEL", F(h, "elementId"), "shrunk step " + i + " at " + s1.transform.position.ToString("F2") + " dist " + F2(h) + " depths " + string.Join(",", s1.GetComponentsInChildren<Graphic>(false).Select(g => g.name + ":" + g.depth)) + " scale " + s1.transform.lossyScale.x + " active " + s1.activeInHierarchy);
        }
        Assert.AreEqual(3, (int)reg.GetProperty("Count").GetValue(null));
        UnityEngine.Object.DestroyImmediate(d2); Assert.IsTrue(Cast(new Vector3(2.5f, 0f, 4f).normalized, out var after)); Assert.AreEqual("DESTINATION1_PANEL", F(after, "elementId"), "the destroyed panel is gone; only the panel behind it remains"); Assert.AreEqual(2, (int)reg.GetProperty("Count").GetValue(null), "a destroyed clone is pruned");
    }

    [Test]
    public void PanelIsInvisibleToThePhysicalRay_ButNotToTheUiRay()
    {
        var go = Place("part3Dest1FullTemplate");
        Assert.IsFalse(Physics.Raycast(Vector3.zero, Vector3.forward, 50f, ~0), "no Collider on the panel: Physics.Raycast passes through it");
        Assert.IsTrue(Cast(Vector3.forward, out var h)); StringAssert.StartsWith(go.name.Replace("(Clone)", ""), F(h, "canvasId"));
    }

    [Test]
    public void DisabledRootCanvas_WhileUiManagerSettlesAPanel_IsNotHit_AndHitAgainOnceReEnabled()
    {
        var go = Place("part1StartPanelTemplate"); var canvas = go.GetComponent<Canvas>();
        Assert.IsTrue(Cast(Vector3.forward, out _));
        canvas.enabled = false; Canvas.ForceUpdateCanvases(); Render();   // exactly what UIManager.PlaceWhenSettled does
        Assert.IsFalse(Cast(Vector3.forward, out _), "a hidden panel cannot be looked at");
        canvas.enabled = true; Canvas.ForceUpdateCanvases(); Render();
        Assert.IsTrue(Cast(Vector3.forward, out _));
    }
}
