// GazeUiRecordingTests.cs  (PlayMode, SYNTHETIC; part of the shared NpcLifecycleTests harness)
// End-to-end tests of the gaze_ui_hits.csv companion stream through the REAL GazeRecorder sampling loop and the REAL DataCaptureManager (session folder, manifest, finalisation): key alignment with
// gaze.csv, independent UI + physical records, the hit-priority / occlusion policy, no-hit rows, head-fallback rows, "UI off" and "the physical channel is unchanged by the UI path".
// All geometry is created here. The eyes are scripted (EyeInjection test seam), so this is NOT a Quest Pro / eye-tracker test; see docs/gaze_ui_detection.md for what only a headset can show.
// Harness geometry (OpenTrialWithGaze): head at (0, 1, -5) looking +z, a 50 x 50 x 0.2 opaque cube ("wall") at z = 0 on the Default layer.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using static Reflect;

public partial class NpcLifecycleTests
{
    // ---- builders ----
    GameObject GuiCanvas(string name, float z, float y = 1f, Vector2? size = null, bool withButton = true, string panelId = null)
    {
        var go = Make(name); go.layer = 0;
        var c = go.AddComponent<Canvas>(); c.renderMode = RenderMode.WorldSpace; go.AddComponent<GraphicRaycaster>();
        var rt = (RectTransform)go.transform; rt.sizeDelta = size ?? new Vector2(4, 3); rt.position = new Vector3(0, y, z); rt.rotation = Quaternion.identity; rt.localScale = Vector3.one;
        go.AddComponent<Image>();
        if (withButton)
        {
            var b = new GameObject("Btn", typeof(RectTransform)); b.transform.SetParent(go.transform, false); ((RectTransform)b.transform).sizeDelta = new Vector2(1, 0.5f); b.AddComponent<Image>(); b.AddComponent<Button>();
        }
        if (panelId != null)
        {
            var el = go.AddComponent(T("GazeUIElement")); Set(el, "stableId", panelId); Set(el, "displayName", panelId); Set(el, "category", Enum.ToObject(T("GazeUiCategory"), 1)); Set(el, "roiLabel", panelId); Set(el, "applyToChildren", true);
        }
        return go;
    }

    Camera GuiCamera(Vector3 at)
    {
        var go = Make("GuiEventCam"); go.transform.position = at; var cam = go.AddComponent<Camera>(); cam.enabled = false; return cam;
    }

    // Like OpenTrialWithGaze, but everything (recorder options, scripted eyes, panels, wall variant) is configured BEFORE the trial starts: the recorder writes its first row synchronously inside BeginTrial,
    // so a late configuration would leave one row recorded under the default setup. A camera render assigns Graphic.depth (the "drawn" marker); -nographics batch mode renders nothing by itself.
    IEnumerator GuiSession(Action<Rig, Component, Transform> setup, bool eyes, float seconds, Action<Rig, Component, Transform> done)
    {
        CallStatic(T("GazeUiRegistry"), "ClearForTests");
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, r => rig = r);
        var head = Make("Head").transform; head.position = new Vector3(0f, 1f, -5f);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); _spawned.Add(wall); wall.transform.position = new Vector3(0f, 1f, 0f); wall.transform.localScale = new Vector3(50f, 50f, 0.2f);
        var rec = MakeGaze(rig, head, 40f);
        yield return null;
        if (eyes) { Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward)); }
        var evCam = GuiCamera(head.position); Set(rec, "uiEventCamera", evCam);
        Set(rec, "roiLayerMask", (LayerMask)123);   // the value the experiment scene uses
        setup?.Invoke(rig, rec, head);
        Canvas.ForceUpdateCanvases(); evCam.enabled = true; evCam.Render(); evCam.enabled = false;
        Call(rig.em, "BeginTrial");
        yield return new WaitForSecondsRealtime(seconds);
        Call(rec, "StopRecording");
        done(rig, rec, head);
    }

    // the harness wall of OpenTrialWithGaze: the only primitive cube among the spawned objects
    GameObject GuiWall() => _spawned.OfType<GameObject>().First(o => o != null && o.GetComponent<MeshRenderer>() != null && o.name.StartsWith("Cube"));

    static string[] GuiColumns() => (string[])T("GazeRecorder").GetField("UiHitColumns").GetValue(null);

    // ====================================================================== independent channels, priority, keys, manifest, finalisation
    [UnityTest]
    public IEnumerator GazeUi_InFrontOfTheWall_IsPrimary_BothChannelsRecorded_KeysAlignWithGazeCsv_ManifestAndFinalisationCoverTheFile()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return GuiSession((r, c, h) => GuiCanvas("PanelFront", -3f), true, 0.5f, (r, c, h) => { rig = r; rec = c; head = h; });

        var gaze = Csv.LoadRaw(rig.File("gaze.csv")); var ui = Csv.LoadRaw(rig.File("gaze_ui_hits.csv"));
        Assert.IsTrue(ui.Exists, "the companion file is written next to gaze.csv"); Assert.Greater(ui.Rows.Count, 5);
        CollectionAssert.AreEqual(GuiColumns(), ui.Header, "exact v1 header"); Assert.AreEqual(30, ui.Header.Count);
        foreach (var r in ui.Rows) Assert.AreEqual(30, r.Count, "every row has exactly the header's width");
        Assert.AreEqual(gaze.Rows.Count, ui.Rows.Count, "one companion row per gaze row");
        for (int i = 0; i < gaze.Rows.Count; i++)
            foreach (var k in new[] { "t_abs", "frame_index", "session_id" }) Assert.AreEqual(gaze.Get(gaze.Rows[i], k), ui.Get(ui.Rows[i], k), k + " row " + i);
        Assert.IsTrue(ui.Rows.All(r => ui.Get(r, "ui_ray_evaluated") == "1" && ui.Get(r, "ui_hit") == "1"), "every row, including the first (written synchronously by BeginTrial), carries a real eye ray and hits the panel");
        var u = ui.Rows[ui.Rows.Count / 2]; var g = gaze.Rows[ui.Rows.Count / 2];
        // raw UI channel: the Button Graphic of the panel in front
        Assert.AreEqual("", ui.Get(u, "ui_ray_skip_reason"));
        Assert.AreEqual("PanelFront", ui.Get(u, "ui_canvas_id")); Assert.AreEqual("WorldSpace", ui.Get(u, "ui_canvas_render_mode")); Assert.AreEqual("PanelFront/Btn", ui.Get(u, "ui_element_id")); Assert.AreEqual("button", ui.Get(u, "ui_element_category"));
        Assert.AreEqual("UI_BUTTON", ui.Get(u, "ui_roi_label")); Assert.AreEqual("0", ui.Get(u, "ui_element_has_gaze_component")); Assert.AreEqual("1", ui.Get(u, "ui_interactable"));
        Assert.AreEqual(2f, float.Parse(ui.Get(u, "ui_hit_distance"), System.Globalization.CultureInfo.InvariantCulture), 0.01f, "head z=-5, panel z=-3"); Assert.AreEqual("-3.0000", ui.Get(u, "ui_hit_point_z"));
        // raw physical channel of the SAME ray (the wall 5 m ahead) - recorded independently, and identical to what gaze.csv says
        Assert.AreEqual("1", ui.Get(u, "world_hit")); Assert.AreEqual(gaze.Get(g, "hit_instance_id"), ui.Get(u, "world_hit_instance_id")); Assert.AreEqual(gaze.Get(g, "hit_semantic_class"), ui.Get(u, "world_hit_semantic_class"));
        Assert.AreEqual(4.9f, float.Parse(ui.Get(u, "world_hit_distance"), System.Globalization.CultureInfo.InvariantCulture), 0.05f, "wall front face at z = -0.1"); Assert.AreEqual("", ui.Get(u, "world_occluder_established"), "UI is in front: no occlusion question");
        Assert.AreEqual("1", gaze.Get(g, "ray_did_hit")); Assert.AreEqual("CanvasUI", ui.Get(u, "primary_target_class")); Assert.AreEqual("ui_in_front", ui.Get(u, "primary_target_basis")); Assert.AreEqual("PanelFront/Btn", ui.Get(u, "primary_target_id")); Assert.AreEqual("UI_BUTTON", ui.Get(u, "primary_target_roi"));
        Assert.IsTrue(gaze.Rows.All(r => gaze.Get(r, "ray_did_hit") == "1"), "the physical channel still reports the wall on every row (the panel has no collider)");

        // manifest + finalisation include the companion file
        Call(rig.dcm, "OnSessionEnded");
        var m = JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual(17, (int)m["schema_version"], "the session schema stays 17: the new file is optional and has its own version");
        var fl = ((JArray)m["file_list"]).Select(t => (string)t).ToList(); CollectionAssert.Contains(fl, "gaze.csv"); CollectionAssert.Contains(fl, "gaze_ui_hits.csv");
        CollectionAssert.Contains(((JArray)m["capture_streams_closed"]).Select(t => (string)t).ToList(), "gaze_ui_hits"); Assert.AreEqual("Closed", (string)m["capture_lifecycle_state"]);
        var gm = m["gaze_ui_hits"]; Assert.AreEqual(1, (int)gm["schema_version"]); Assert.AreEqual("gaze_ui_hits.csv", (string)gm["file"]); Assert.AreEqual("ok", (string)gm["status"]); Assert.IsTrue((bool)gm["enabled"]);
        Assert.AreEqual(ui.Rows.Count, (long)gm["rows_written"]); Assert.AreEqual(ui.Rows.Count, (long)gm["rows_ui_hit"]); Assert.AreEqual(ui.Rows.Count, (long)gm["primary_canvas_ui"]); Assert.AreEqual(30, ((JArray)gm["columns"]).Count);
        Assert.GreaterOrEqual((int)gm["canvases_registered"], 1); Assert.AreEqual("ok", (string)m["health_gaze_ui_hits"]["status"]); Assert.AreEqual(ui.Rows.Count, (long)m["health_gaze_ui_hits"]["rows_written"]);
        StringAssert.Contains("NOT supported", (string)gm["render_mode_support"]);
    }

    [UnityTest]
    public IEnumerator GazeUi_PanelLevelMarker_IsTheRecordedIdentity_NotTheCrossedButton()
    {
        Rig rig = null;
        yield return GuiSession((r, c, h) => GuiCanvas("PanelFront", -3f, panelId: "PART1_START_PANEL"), true, 0.5f, (r, c, h) => rig = r);   // the ray crosses the panel's Button
        var gaze = Csv.LoadRaw(rig.File("gaze.csv")); var ui = Csv.LoadRaw(rig.File("gaze_ui_hits.csv"));
        Assert.AreEqual(gaze.Rows.Count, ui.Rows.Count); Assert.Greater(ui.Rows.Count, 5);
        CollectionAssert.AreEqual(GuiColumns(), ui.Header, "no new column: the panel identity reuses the existing fields");
        foreach (var r in ui.Rows)
        {
            Assert.AreEqual("1", ui.Get(r, "ui_hit")); Assert.AreEqual("PART1_START_PANEL", ui.Get(r, "ui_element_id")); Assert.AreEqual("PART1_START_PANEL", ui.Get(r, "ui_roi_label")); Assert.AreEqual("panel", ui.Get(r, "ui_element_category"));
            Assert.AreEqual("1", ui.Get(r, "ui_element_has_gaze_component")); Assert.AreEqual("", ui.Get(r, "ui_interactable")); Assert.AreEqual("PART1_START_PANEL", ui.Get(r, "primary_target_id")); Assert.AreEqual("PART1_START_PANEL", ui.Get(r, "primary_target_roi"));
            Assert.AreEqual("CanvasUI", ui.Get(r, "primary_target_class"));
        }
    }

    [UnityTest]
    public IEnumerator GazeUi_BehindAnOpaqueWall_IsNotPrimary_RawUiHitIsKept_WorldOccludes()
    {
        Rig rig = null;
        yield return GuiSession((r, c, h) => GuiCanvas("PanelBehind", 3f), true, 0.5f, (r, c, h) => rig = r);   // 3 m BEHIND the wall (wall front face at z = -0.1)
        var ui = Csv.LoadRaw(rig.File("gaze_ui_hits.csv"));
        Assert.Greater(ui.Rows.Count, 5);
        Assert.IsTrue(ui.Rows.All(r => ui.Get(r, "ui_hit") == "1" && ui.Get(r, "world_hit") == "1" && ui.Get(r, "world_occluder_established") == "1"), "the raw UI channel still sees the panel (geometry only); the wall is an established occluder");
        Assert.IsTrue(ui.Rows.All(r => ui.Get(r, "primary_target_class") == "WorldObject" && ui.Get(r, "primary_target_basis") == "world_occludes_ui"), "UI behind an opaque physical object never wins");
        Call(rig.dcm, "OnSessionEnded"); var m = JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual(ui.Rows.Count, (long)m["gaze_ui_hits"]["rows_ui_behind_world_hit"]); Assert.AreEqual(0L, (long)m["gaze_ui_hits"]["primary_canvas_ui"]);
    }

    [UnityTest]
    public IEnumerator GazeUi_BehindAnOccluderThatCannotBeEstablished_PrimaryIsUncertain_BothChannelsKept()
    {
        Rig rig = null;
        yield return GuiSession((r, c, h) => { GuiWall().GetComponent<Collider>().isTrigger = true; GuiCanvas("PanelBehindTrigger", 3f); }, true, 0.5f, (r, c, h) => rig = r);   // the wall becomes a trigger volume
        var ui = Csv.LoadRaw(rig.File("gaze_ui_hits.csv"));
        Assert.Greater(ui.Rows.Count, 5);
        Assert.IsTrue(ui.Rows.All(r => ui.Get(r, "ui_hit") == "1" && ui.Get(r, "world_hit") == "1" && ui.Get(r, "world_occluder_established") == "0"), "queriesHitTriggers is on in this project: the trigger is hit but is not an established occluder");
        Assert.IsTrue(ui.Rows.All(r => ui.Get(r, "primary_target_class") == "Uncertain" && ui.Get(r, "primary_target_basis") == "occlusion_not_established" && ui.Get(r, "primary_target_roi") == ""));
    }

    [UnityTest]
    public IEnumerator GazeUi_InactiveOrInvisibleUi_IsNeverPrimary_AndNoUiHitRowsAreConsistentAndReadable()
    {
        Rig rig = null;
        yield return GuiSession((r, c, h) =>
        {
            var inactive = GuiCanvas("PanelInactive", -4f); var faded = GuiCanvas("PanelFaded", -3f); faded.AddComponent<CanvasGroup>().alpha = 0f;
            var clear = GuiCanvas("PanelClear", -2.5f); foreach (var gr in clear.GetComponentsInChildren<Graphic>()) gr.color = new Color(1, 1, 1, 0);   // Graphic.color alpha 0: invisible hit areas
            Canvas.ForceUpdateCanvases(); inactive.SetActive(false);
        }, true, 0.5f, (r, c, h) => rig = r);
        var gaze = Csv.LoadRaw(rig.File("gaze.csv")); var ui = Csv.LoadRaw(rig.File("gaze_ui_hits.csv"));
        Assert.AreEqual(gaze.Rows.Count, ui.Rows.Count); Assert.Greater(ui.Rows.Count, 5);
        foreach (var r in ui.Rows)
        {
            Assert.AreEqual("1", ui.Get(r, "ui_ray_evaluated")); Assert.AreEqual("0", ui.Get(r, "ui_hit"), "inactive / fully transparent UI is never a hit");
            foreach (var c in new[] { "ui_canvas_id", "ui_canvas_name", "ui_canvas_render_mode", "ui_canvas_sorting_order", "ui_element_id", "ui_element_name", "ui_element_category", "ui_roi_label", "ui_element_has_gaze_component", "ui_interactable", "ui_hit_point_x", "ui_hit_point_y", "ui_hit_point_z", "ui_hit_distance", "ui_graphics_under_ray" })
                Assert.AreEqual("", ui.Get(r, c), c + " is blank when there is no UI hit");
            Assert.AreEqual("1", ui.Get(r, "world_hit")); Assert.AreEqual("WorldObject", ui.Get(r, "primary_target_class")); Assert.AreEqual("world_only", ui.Get(r, "primary_target_basis"));
        }
        Call(rig.dcm, "OnSessionEnded"); var m = JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual("ok", (string)m["gaze_ui_hits"]["status"], "a file with no UI hits is still a healthy file"); Assert.AreEqual(0L, (long)m["gaze_ui_hits"]["rows_ui_hit"]); Assert.AreEqual(ui.Rows.Count, (long)m["gaze_ui_hits"]["primary_world_object"]);
    }

    [UnityTest]
    public IEnumerator GazeUi_HeadFallbackRows_AreNotEvaluated_ByDefault_AndEvaluatedWhenOptedIn([Values(false, true)] bool optIn)
    {
        Rig rig = null;
        // no scripted eyes and no OVREyeGaze: every row is the head-forward fallback ray
        yield return GuiSession((r, c, h) => { Set(c, "recordViewFrame", false); Set(c, "uiRaycastOnHeadFallback", optIn); GuiCanvas("PanelFb", -3f); }, false, 0.4f, (r, c, h) => rig = r);
        var gaze = Csv.LoadRaw(rig.File("gaze.csv")); var ui = Csv.LoadRaw(rig.File("gaze_ui_hits.csv"));
        Assert.Greater(ui.Rows.Count, 3); Assert.AreEqual(gaze.Rows.Count, ui.Rows.Count);
        Assert.IsTrue(gaze.Rows.All(r => gaze.Get(r, "combined_valid") == "0" && gaze.Get(r, "gaze_ray_source") == "head_fallback"));
        foreach (var r in ui.Rows)
        {
            if (!optIn)
            {
                Assert.AreEqual("0", ui.Get(r, "ui_ray_evaluated")); Assert.AreEqual("head_fallback_ray", ui.Get(r, "ui_ray_skip_reason")); Assert.AreEqual("", ui.Get(r, "ui_hit"), "not evaluated is blank, never 'looked and found nothing'");
                Assert.AreEqual("ui_not_evaluated", ui.Get(r, "primary_target_basis")); Assert.AreEqual("WorldObject", ui.Get(r, "primary_target_class"), "the physical result stands");
            }
            else
            {
                Assert.AreEqual("1", ui.Get(r, "ui_ray_evaluated")); Assert.AreEqual("1", ui.Get(r, "ui_hit")); Assert.AreEqual("CanvasUI", ui.Get(r, "primary_target_class"));
            }
        }
        Call(rig.dcm, "OnSessionEnded"); var m = JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual(optIn ? 0L : ui.Rows.Count, (long)m["gaze_ui_hits"]["rows_ui_not_evaluated"]);
    }

    [UnityTest]
    public IEnumerator GazeUi_Disabled_WritesNoCompanionFile_AndGazeCsvIsUnchanged()
    {
        Rig rig = null;
        yield return GuiSession((r, c, h) => { Set(c, "enableUiGaze", false); GuiCanvas("PanelOff", -3f); }, true, 0.5f, (r, c, h) => rig = r);
        Assert.IsFalse(File.Exists(rig.File("gaze_ui_hits.csv")), "enableUiGaze = false: no companion file");
        var raw = Csv.LoadRaw(rig.File("gaze.csv")); var diag = Csv.LoadRaw(rig.File("gaze_diagnostics.csv"));
        CollectionAssert.AreEqual((string[])T("GazeRecorder").GetField("PrimaryColumns").GetValue(null), raw.Header); Assert.AreEqual(61, raw.Header.Count); Assert.AreEqual(85, diag.Header.Count);
        Call(rig.dcm, "OnSessionEnded"); var m = JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
        CollectionAssert.DoesNotContain(((JArray)m["file_list"]).Select(t => (string)t).ToList(), "gaze_ui_hits.csv"); Assert.AreEqual("not_enabled", (string)m["gaze_ui_hits"]["status"]); Assert.IsFalse((bool)m["gaze_ui_hits"]["enabled"]);
    }

    // ====================================================================== the physical channel is untouched
    [UnityTest]
    public IEnumerator GazeUi_PhysicalChannel_IsIdentical_WithAndWithoutTheUiPath_ForBuildingLikeGroundTriggerAndMaskedTargets([Values("default_layer_opaque", "ground_layer", "trigger_collider", "ignore_raycast_layer_masked")] string variant)
    {
        // Same scene, same scripted ray; three recordings of the same target variant: UI off, UI on without a panel, UI on WITH a panel in front. The hit_* / ray_did_hit columns must be identical.
        string[] cols = { "ray_did_hit", "hit_point_x", "hit_point_y", "hit_point_z", "roi_hit_distance", "hit_collider_name", "hit_object_name", "hit_layer", "hit_tag", "hit_semantic_class", "hit_building_id", "hit_npc_id", "hit_is_interactable" };
        var results = new List<string>();
        for (int pass = 0; pass < 3; pass++)
        {
            Rig rig = null; int p = pass;
            yield return GuiSession((r, c, h) =>
            {
                var wall = GuiWall();
                switch (variant)
                {
                    case "ground_layer": wall.layer = LayerMask.NameToLayer("Ground"); break;
                    case "trigger_collider": wall.GetComponent<Collider>().isTrigger = true; break;
                    case "ignore_raycast_layer_masked": wall.layer = 2; break;   // layer 2 is not in the scene's roiLayerMask (123): nothing is hit
                }
                Set(c, "enableUiGaze", p != 0);
                if (p == 2) GuiCanvas("PanelPhys", -3f);
            }, true, 0.4f, (r, c, h) => rig = r);
            var gaze = Csv.LoadRaw(rig.File("gaze.csv")); Assert.Greater(gaze.Rows.Count, 5);
            string Key(List<string> r) => string.Join("|", cols.Select(c => gaze.Get(r, c)));
            string first = Key(gaze.Rows[0]);
            Assert.IsTrue(gaze.Rows.All(r => Key(r) == first), variant + " pass " + pass + ": the physical result is constant over the recording");
            results.Add(first);
            Call(rig.dcm, "OnSessionEnded");
            foreach (var o in _spawned.Where(o => o != null).ToList()) UnityEngine.Object.Destroy(o);
            _spawned.Clear();
            yield return null;
        }
        Assert.AreEqual(results[0], results[1], variant + ": UI path on (no panel) does not change the physical columns");
        Assert.AreEqual(results[0], results[2], variant + ": a panel in front does not change the physical columns (it has no collider)");
        if (variant == "ignore_raycast_layer_masked") StringAssert.StartsWith("0|", results[0], "masked layer: no hit");
        else StringAssert.StartsWith("1|", results[0], "the wall is hit");
    }
}
