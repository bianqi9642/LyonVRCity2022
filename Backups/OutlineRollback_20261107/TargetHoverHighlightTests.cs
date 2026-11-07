// TargetHoverHighlightTests.cs
// Tests for the enhanced Target Building hover highlight (InteractableHoverFeedback: outline + warm colour lift in Part 1 / Part 3).
//
// WHAT IS REAL AND WHAT IS NOT
//   * REAL production components (Assembly-CSharp, reached by reflection): InteractableHoverFeedback, TargetSelectable, ExperimentManager (RunningPartName, ParticipantControlEnabled, its events),
//     the real "Hidden/Lyon/TargetHoverOutline" shader asset, URP/Lit materials, MaterialPropertyBlocks, the UGUI ExecuteEvents hierarchy dispatch (child collider -> parent handler).
//   * SYNTHETIC: cube "buildings", an inactive ExperimentManager whose state the test drives (part, control, events), pointer events constructed by hand. No XR rig, no real ray, no city.
//   * NOT covered here: what the outline LOOKS like (needs the Editor Game view / a headset), single-pass-instanced rendering on a Quest, Quest GPU cost. See the report.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using static Reflect;

public class TargetHoverHighlightTests
{
    const float LegacyBrightness = 0.30f;
    readonly List<UnityEngine.Object> _objs = new List<UnityEngine.Object>();
    [TearDown] public void TearDown() { foreach (var o in _objs) if (o != null) UnityEngine.Object.DestroyImmediate(o); _objs.Clear(); }

    // ------------------------------------------------------------------ harness
    Component NewManager(int part, bool control)
    {
        var go = new GameObject("EM_Hover"); go.SetActive(false); _objs.Add(go);   // inactive: Awake / OnEnable never run; its events and state still work
        var em = go.AddComponent(T("ExperimentManager"));
        SetPart(em, part); Call(em, "SetParticipantControl", control);
        return em;
    }
    static void SetPart(Component em, int part) { var f = em.GetType().GetField("currentRunningPart", All); Assert.IsNotNull(f); f.SetValue(em, Enum.ToObject(f.FieldType, part)); }
    static void SetControl(Component em, bool v) => Call(em, "SetParticipantControl", v);
    static void Raise(Component em, string type)
    {
        var info = Activator.CreateInstance(T("ExperimentEventInfo")); T("ExperimentEventInfo").GetField("type").SetValue(info, type);
        var del = (Delegate)em.GetType().GetField("OnExperimentEvent", All).GetValue(em); del?.DynamicInvoke(info);
    }
    static void RaiseSessionEnded(Component em) { var del = (Delegate)em.GetType().GetField("OnSessionEnded", All).GetValue(em); del?.DynamicInvoke(); }

    sealed class Building { public GameObject root; public List<MeshRenderer> renderers = new List<MeshRenderer>(); public Material mat; public Component hover; public GameObject childCollider; }

    Building MakeBuilding(string name, Component em, int renderersCount = 3, Vector3? at = null)
    {
        var b = new Building(); b.root = new GameObject(name); _objs.Add(b.root); b.root.SetActive(false);
        b.root.transform.position = at ?? Vector3.zero;
        b.mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); _objs.Add(b.mat); b.mat.SetColor("_BaseColor", new Color(0.8f, 0.7f, 0.6f, 1f));
        for (int i = 0; i < renderersCount; i++)
        {
            var c = GameObject.CreatePrimitive(PrimitiveType.Cube); c.name = name + "_part" + i; c.transform.SetParent(b.root.transform, false); c.transform.localPosition = new Vector3(i * 1.5f, 0, 0);
            var mr = c.GetComponent<MeshRenderer>(); mr.sharedMaterial = b.mat; b.renderers.Add(mr);
            if (i == 0) b.childCollider = c;   // the cube's BoxCollider is the collider a ray would hit: a CHILD of the building root
        }
        b.hover = b.root.AddComponent(T("InteractableHoverFeedback"));
        if (em != null) Set(b.hover, "experimentManager", em);
        b.root.SetActive(true);
        return b;
    }

    static PointerEventData Ptr(int id) => new PointerEventData(null) { pointerId = id };
    static void Enter(Building b, int id = 0) => ((IPointerEnterHandler)b.hover).OnPointerEnter(Ptr(id));
    static void Exit(Building b, int id = 0) => ((IPointerExitHandler)b.hover).OnPointerExit(Ptr(id));

    static IEnumerable<MeshRenderer> Outlines(Building b) => b.root.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.gameObject.name == "__HoverOutline");
    static bool AnyOutlineEnabled(Building b) => Outlines(b).Any(r => r.enabled);
    static bool HasBlock(Renderer r) => r.HasPropertyBlock();
    static Color BlockColor(Renderer r) { var mpb = new MaterialPropertyBlock(); r.GetPropertyBlock(mpb); return mpb.GetColor("_BaseColor"); }
    static void AssertClose(Color a, Color b, float tol = 1e-4f) { Assert.AreEqual(a.r, b.r, tol); Assert.AreEqual(a.g, b.g, tol); Assert.AreEqual(a.b, b.b, tol); }
    static void AssertPristine(Building b)
    {
        foreach (var r in b.renderers) { Assert.IsFalse(HasBlock(r), r.name + ": no property block left behind"); Assert.AreSame(b.mat, r.sharedMaterial, "the shared material is untouched"); }
        Assert.IsFalse(AnyOutlineEnabled(b), "no outline stays visible");
        AssertClose(new Color(0.8f, 0.7f, 0.6f), b.mat.GetColor("_BaseColor"));
    }

    // ------------------------------------------------------------------ A. hover activation
    [Test]
    public void A_Part1_Hover_HighlightsEveryRendererOfTheWholeBuilding_AndNothingElse()
    {
        var em = NewManager(1, true); var target = MakeBuilding("Target", em, 3); var other = MakeBuilding("Other", em, 2, new Vector3(30, 0, 0));
        Assert.IsNotNull(Shader.Find("Hidden/Lyon/TargetHoverOutline"), "the outline shader asset is found by name (Resources folder)");
        Enter(target);
        foreach (var r in target.renderers)
        {
            Assert.IsTrue(HasBlock(r), r.name + " gets the colour lift");
            var c = BlockColor(r); Assert.Greater(c.r, 0.8f); Assert.Greater(c.g, 0.7f); Assert.Greater(c.b, 0.6f - 1e-4f);
            Assert.Greater(c.r - 0.8f, c.b - 0.6f, "the lift is WARM (red gains more than blue), a tint and not a plain white-out");
        }
        var outlines = Outlines(target).ToList(); Assert.AreEqual(target.renderers.Count, outlines.Count, "one outline helper per building renderer"); Assert.IsTrue(outlines.All(o => o.enabled));
        Assert.IsTrue(outlines.All(o => o.GetComponent<Collider>() == null), "an outline helper is never a collider: it cannot be hit by a ray");
        Assert.IsTrue(outlines.All(o => o.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off && !o.receiveShadows));
        AssertPristine(other); Assert.AreEqual(0, Outlines(other).Count(), "an unrelated building is not touched and gets no helper");
    }

    // ------------------------------------------------------------------ B. hover exit
    [Test]
    public void B_Exit_RestoresTheExactOriginalAppearance_NoPermanentHighlight()
    {
        var em = NewManager(1, true); var b = MakeBuilding("T", em);
        Enter(b); Assert.IsTrue(AnyOutlineEnabled(b)); Exit(b);
        AssertPristine(b);
    }

    // ------------------------------------------------------------------ C. selection interaction
    [Test]
    public void C_Selecting_WhileHovered_KeepsSelectionBehaviour_AndHoverExitDoesNotTouchIt()
    {
        CallStatic(T("TargetSelectable"), "ResetInteractionCounter");
        var em = NewManager(1, true); var b = MakeBuilding("SelTarget", em);
        b.root.SetActive(false); var ts = b.root.AddComponent(T("TargetSelectable")); Set(ts, "targetId", "SelTarget"); Set(ts, "experimentManager", em); b.root.SetActive(true);
        Enter(b);
        Call(ts, "Select");
        Assert.AreEqual("C000001", Get<string>(ts, "LastInteractionId"), "the selection still allocates its interaction id");
        Assert.AreEqual("SelTarget", Get<string>(ts, "ResolvedTargetId"), "the target id is unchanged");
        Assert.IsTrue(AnyOutlineEnabled(b), "a click does not end the hover");
        Assert.AreEqual(1, b.root.GetComponents(T("TargetSelectable")).Length);
        Exit(b); AssertPristine(b);
        Assert.AreEqual("C000001", Get<string>(ts, "LastInteractionId"), "hover exit does not touch the selection state");
        Assert.AreEqual("SelTarget", Get<string>(ts, "ResolvedTargetId"));
    }

    // ------------------------------------------------------------------ D. phases
    [Test]
    public void D_Part1_And_Part3_UseTheEnhancedLook_Part2_KeepsTheLegacyLook_WithoutOutline()
    {
        foreach (int part in new[] { 1, 3 })
        {
            var em = NewManager(part, true); var b = MakeBuilding("P" + part, em);
            Enter(b); Assert.IsTrue(AnyOutlineEnabled(b), "Part " + part + ": outline shown"); Exit(b); AssertPristine(b);
        }
        var em2 = NewManager(2, true); var b2 = MakeBuilding("P2", em2);
        Enter(b2);
        Assert.AreEqual(0, Outlines(b2).Count(), "Part 2: the new outline is not even created");
        foreach (var r in b2.renderers) { var c = BlockColor(r); Assert.AreEqual(0.8f * (1f + LegacyBrightness), c.r, 1e-3f, "Part 2: exactly the previous brightness lift"); Assert.AreEqual(0.6f * (1f + LegacyBrightness), c.b, 1e-3f); }
        Exit(b2); AssertPristine(b2);
        var em0 = NewManager(0, true); var b0 = MakeBuilding("NoPart", em0); Enter(b0); Assert.AreEqual(0, Outlines(b0).Count(), "no Part running (Editor): legacy look only"); Exit(b0);
        var b00 = MakeBuilding("NoManager", null); Enter(b00); Assert.AreEqual(0, Outlines(b00).Count(), "no ExperimentManager at all: legacy look only"); Exit(b00); AssertPristine(b00);
    }

    [Test]
    public void D_SwitchingPhaseWhileHovering_ClearsOrSwitchesTheLookImmediately()
    {
        var em = NewManager(1, true); var b = MakeBuilding("T", em);
        Enter(b); Assert.IsTrue(AnyOutlineEnabled(b));
        SetPart(em, 2); Raise(em, "part_start");                       // Part 1 -> Part 2 while the ray stays on the building
        Assert.IsFalse(AnyOutlineEnabled(b), "leaving the eligible phase removes the outline"); Assert.AreEqual(0.8f * (1f + LegacyBrightness), BlockColor(b.renderers[0]).r, 1e-3f, "Part 2 look = legacy lift");
        SetPart(em, 3); Raise(em, "part_start"); Assert.IsTrue(AnyOutlineEnabled(b), "entering Part 3 with the ray still on the building shows the enhanced look");
        SetControl(em, false);                                          // Destination 2 panel / Start panel / End panel: participant locked
        Assert.IsFalse(AnyOutlineEnabled(b), "locked in Part 3: no hover look (a click would be ignored)"); Assert.IsFalse(HasBlock(b.renderers[0]));
        SetControl(em, true); Assert.IsTrue(AnyOutlineEnabled(b), "unlocked again: the still-hovered building is highlighted again");
        SetControl(em, false); SetPart(em, 0); Raise(em, "part_end"); SetControl(em, true);
        Assert.IsFalse(AnyOutlineEnabled(b), "after the Part ended only the legacy look can apply"); Exit(b); AssertPristine(b);
    }

    [Test]
    public void D_SessionEnd_ClearsTheHighlight()
    {
        var em = NewManager(1, true); var b = MakeBuilding("T", em);
        Enter(b); Assert.IsTrue(AnyOutlineEnabled(b));
        SetControl(em, false); RaiseSessionEnded(em);
        Assert.IsFalse(AnyOutlineEnabled(b)); Assert.IsFalse(HasBlock(b.renderers[0]));
    }

    // ------------------------------------------------------------------ E. multiple rays and edge cases
    [Test]
    public void E_TwoRays_OneLeaves_TheBuildingStaysHighlighted_UntilTheLastLeaves()
    {
        var em = NewManager(3, true); var b = MakeBuilding("T", em);
        Enter(b, 0); Enter(b, 1); Enter(b, 1);   // duplicate enter of the same pointer is harmless
        Exit(b, 0); Assert.IsTrue(AnyOutlineEnabled(b), "the other ray is still on the building"); Assert.IsTrue(HasBlock(b.renderers[0]));
        Exit(b, 1); AssertPristine(b);
        Exit(b, 1); AssertPristine(b);           // a stray extra exit is harmless
    }

    [Test]
    public void E_RapidEnterExit_DoesNotAccumulateOrLeaveAnythingBehind()
    {
        var em = NewManager(1, true); var b = MakeBuilding("T", em); int objectsBefore = Resources.FindObjectsOfTypeAll<GameObject>().Length;
        Enter(b); int helpers = Outlines(b).Count(); int afterFirst = Resources.FindObjectsOfTypeAll<GameObject>().Length; Exit(b);
        for (int i = 0; i < 25; i++) { Enter(b); Exit(b); }
        Assert.AreEqual(helpers, Outlines(b).Count(), "the outline helpers are created once, not per hover");
        Assert.AreEqual(afterFirst, Resources.FindObjectsOfTypeAll<GameObject>().Length, "no objects accumulate");
        Assert.Greater(afterFirst, objectsBefore - 1);
        AssertPristine(b);
    }

    [Test]
    public void E_RayHitsAChildCollider_TheWholeBuildingIsHighlighted()
    {
        var em = NewManager(1, true); var b = MakeBuilding("T", em, 3);
        var ped = Ptr(0);
        // The EventSystem delivers the enter event to the hit collider's GameObject and bubbles it up to the nearest handler (the building root).
        ExecuteEvents.ExecuteHierarchy(b.childCollider, ped, ExecuteEvents.pointerEnterHandler);
        Assert.IsTrue(b.renderers.All(HasBlock), "all renderers of the building, not only the child that owns the collider");
        ExecuteEvents.ExecuteHierarchy(b.childCollider, ped, ExecuteEvents.pointerExitHandler);
        AssertPristine(b);
    }

    [Test]
    public void E_BuildingDisabledWhileHovered_IsRestored_AndNoStaleHoverRemains()
    {
        var em = NewManager(1, true); var b = MakeBuilding("T", em);
        Enter(b); ((Behaviour)b.hover).enabled = false;            // ExperimentManager.SetCandidateInteractable(false)
        AssertPristine(b);
        ((Behaviour)b.hover).enabled = true; Assert.IsFalse(HasBlock(b.renderers[0]), "re-enabling does not resurrect a stale hover");
        Enter(b); b.root.SetActive(false); Assert.IsFalse(HasBlock(b.renderers[0]), "deactivating the building clears the highlight"); b.root.SetActive(true); AssertPristine(b);
    }

    [Test]
    public void E_DestroyedWhileHovered_RestoresRenderers_AndRemovesTheOutlineHelpers()
    {
        var em = NewManager(1, true); var b = MakeBuilding("T", em);
        var shared = b.renderers[0].gameObject; Enter(b); Assert.AreEqual(3, Outlines(b).Count());
        UnityEngine.Object.DestroyImmediate(b.hover);              // the component goes away (scene unload destroys the whole tree the same way)
        Assert.AreEqual(0, Outlines(b).Count(), "the helpers die with the component"); Assert.IsFalse(HasBlock(b.renderers[0]));
        var b2 = MakeBuilding("U", em); Enter(b2); UnityEngine.Object.DestroyImmediate(b2.root);   // whole building destroyed while hovered: must not throw
        Assert.IsTrue(shared != null);
    }

    [Test]
    public void E_RayLeavesAfterTheExperimentManagerIsDestroyed_DoesNotThrow()
    {
        var em = NewManager(1, true); var b = MakeBuilding("T", em);
        Enter(b); UnityEngine.Object.DestroyImmediate(em.gameObject);
        Assert.DoesNotThrow(() => Exit(b)); AssertPristine(b);
    }

    // ------------------------------------------------------------------ F. material preservation
    [Test]
    public void F_SharedMaterialsAreNeverModified_NoMaterialInstancesAccumulate()
    {
        var em = NewManager(3, true); var b = MakeBuilding("T", em);
        string before = string.Join("|", b.renderers.Select(r => r.sharedMaterial.GetInstanceID()));
        Enter(b); Exit(b);                                           // first hover creates the (shared) outline material once
        int mats = Resources.FindObjectsOfTypeAll<Material>().Length;
        for (int i = 0; i < 30; i++) { Enter(b); Exit(b); }
        Assert.AreEqual(mats, Resources.FindObjectsOfTypeAll<Material>().Length, "no material instance per hover");
        Assert.AreEqual(before, string.Join("|", b.renderers.Select(r => r.sharedMaterial.GetInstanceID())), "same shared material objects");
        Assert.AreEqual(1, Outlines(b).Select(o => o.sharedMaterial).Distinct().Count(), "all outlines share ONE material");
        AssertPristine(b);
    }

    [Test]
    public void F_ARendererThatAlreadyCarriedAPropertyBlock_GetsItBackExactly()
    {
        var em = NewManager(1, true); var b = MakeBuilding("T", em);
        var r0 = b.renderers[0]; var mine = new MaterialPropertyBlock(); mine.SetFloat("_TestMarker", 0.5f); r0.SetPropertyBlock(mine);
        Enter(b); Exit(b);
        Assert.IsTrue(HasBlock(r0), "a block that existed before the hover is restored, not deleted");
        var back = new MaterialPropertyBlock(); r0.GetPropertyBlock(back);
        Assert.AreEqual(0.5f, back.GetFloat("_TestMarker"), 1e-6f); Assert.IsFalse(back.HasColor("_BaseColor"), "the hover colour does not stay in the restored block");
        Assert.IsFalse(HasBlock(b.renderers[1]) || HasBlock(b.renderers[2]), "the others return to having no block");
    }

    [Test]
    public void F_EmissionIsOnlyWrittenWhereTheMaterialRendersIt()
    {
        var em = NewManager(1, true); var b = MakeBuilding("T", em);   // URP/Lit with _EMISSION OFF, like every BATI material
        Enter(b); var mpb = new MaterialPropertyBlock(); b.renderers[0].GetPropertyBlock(mpb);
        Assert.IsFalse(mpb.HasColor("_EmissionColor"), "no pretend emission on a material whose keyword is off");
        Exit(b);
        var b3 = NewEmissive(em); Enter(b3); b3.renderers[0].GetPropertyBlock(mpb);
        Assert.IsTrue(mpb.HasColor("_EmissionColor")); Assert.Greater(mpb.GetColor("_EmissionColor").r, 0f, "emission lift is applied where it really renders");
        Exit(b3); AssertPristine2(b3);
    }

    Building NewEmissive(Component em)
    {
        var b = new Building(); b.root = new GameObject("Emissive"); _objs.Add(b.root); b.root.SetActive(false);
        b.mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); _objs.Add(b.mat); b.mat.EnableKeyword("_EMISSION"); b.mat.SetColor("_EmissionColor", Color.black);
        var c = GameObject.CreatePrimitive(PrimitiveType.Cube); c.transform.SetParent(b.root.transform, false); var mr = c.GetComponent<MeshRenderer>(); mr.sharedMaterial = b.mat; b.renderers.Add(mr);
        b.hover = b.root.AddComponent(T("InteractableHoverFeedback")); Set(b.hover, "experimentManager", em); b.root.SetActive(true); return b;
    }
    static void AssertPristine2(Building b) { foreach (var r in b.renderers) Assert.IsFalse(HasBlock(r)); Assert.IsFalse(AnyOutlineEnabled(b)); }

    [Test]
    public void F_TheSceneWideContractIsUnchanged_NoPerFrameWork_NoGlobalRenderingDependency()
    {
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "Scripts/InteractableHoverFeedback.cs"));
        StringAssert.DoesNotContain("void Update(", src); StringAssert.DoesNotContain("void LateUpdate(", src); StringAssert.DoesNotContain("void FixedUpdate(", src);
        StringAssert.DoesNotContain("renderer.material ", src); StringAssert.DoesNotContain(".material.", src);   // never instantiates a material
        StringAssert.DoesNotContain("Camera.main", src);
    }
}
