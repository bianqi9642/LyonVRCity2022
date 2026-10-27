// RiverPointAvailability.cs
//
// Makes the participant-facing river teleport points exist ONLY while Part 3 runs, and gives each one an English
// instruction sign above it.
//
// Single rule: ExperimentManager.IsRiverTeleportAvailable (Part 3 actually running, End Panel not yet shown). The same
// property is what RiverTeleportPoint.TryTeleport() uses to ACCEPT or REJECT a river teleport, so what is shown and what
// is allowed cannot disagree.
//
// What it controls, per river point
//   * The point's anchor root (the TeleportationAnchor GameObject: visuals, collider, anchor, RiverTeleportPoint component)
//     is switched with SetActive. An inactive object has no renderer, no collider, no TeleportationAnchor registration and
//     receives no pointer / mouse callbacks - nothing is left that could still be targeted.
//   * The instruction sign is a child of that root, so it follows exactly the same visibility.
//   * Independence from the shared 'TeleportationArea' hierarchy: that root also holds the Practice / Grid / Organic anchors and
//     is saved inactive. Each river anchor root is detached once to the scene root (world pose kept) and kept inactive, so
//     river points never depend on, and never activate, that shared parent. Nothing else in it is touched.
//   * Everything is hidden in Awake - before the first frame renders - so there is no flash in Part 1 / Part 2. After
//     that it only reacts to the experiment events (part_start, part_end, End Panel shown): no per-frame polling.
//   * Visibility only. Whether a river teleport is ACCEPTED is decided where it executes, in
//     RiverTeleportPoint.TryTeleport(), from the same ExperimentManager rule.
//
// Instruction sign position: 'signOffset' (+ optional 'perPointExtraOffset') on THIS component, applied when the signs are created. The
// component is placed in the scene (GameObject 'RiverPointAvailability' at the Hierarchy root) so the values persist; it is created
// automatically with defaults only if the scene has none.
//
// Instruction sign: world-space UGUI Canvas (dark panel, white TextMeshPro text) above the point. It has NO raycaster, NO
// collider and no raycast-target graphic, so it cannot intercept the controller ray, the UI pointer or the point's own
// collider. A YawBillboard turns it about the vertical axis toward the HMD.
//
// The scheduled Part 2 cross-bank teleport (ExperimentManager.TeleportPlayerTo) does not use these objects and is unaffected.
//
// Created automatically after the scene loads if the scene has river points and no instance; no Scene edit is needed.
//
// All comments in English per project convention.

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;

[DisallowMultipleComponent]
public class RiverPointAvailability : MonoBehaviour
{
    public const string InstructionText = "Click here to teleport to the opposite riverbank.";

    [Header("References (explicit slot -> auto-find -> warn)")]
    [SerializeField] private ExperimentManager experimentManager;

    [Header("Instruction sign position (adjust here; applied when the signs are created at startup)")]
    [Tooltip("Offset of EVERY sign from its river point, in WORLD axes and metres (X = east/west, Y = up, Z = north/south), " +
             "measured from the river point's anchor root position. It is not rotated with the point or the camera. " +
             "Default (0, 3.25, 0) = 3.25 m straight above the point. Lower the sign: reduce Y. Moves ONLY the sign: the " +
             "river point, its anchor, its destination and the pairing are never touched.")]
    [SerializeField] private Vector3 signOffset = new Vector3(0f, 3.25f, 0f);

    [Tooltip("Optional EXTRA offset (world axes, metres) for individual signs, added to 'Sign Offset'. 'Key' is the point's " +
             "'ParentName/Name' as it appears in the Hierarchy at startup: LeftBank/South, LeftBank/North, RightBank/South, " +
             "RightBank/North. The four rows below are pre-filled with zero; set Y negative to lower just that sign.")]
    [SerializeField] private PointOffset[] perPointExtraOffset = new PointOffset[0];

    [Tooltip("World width of the sign (m). Its height is 30% of the width.")]
    [SerializeField] private float signWidthMetres = 4f;

    [System.Serializable]
    private struct PointOffset
    {
        public string key;
        public Vector3 extraOffset;
    }

    [Header("Debug")]
    [SerializeField] private bool enableDebugLogs = false;

    private readonly List<GameObject> roots = new List<GameObject>();   // one anchor root per river point (its sign is a child)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindObjectOfType<RiverPointAvailability>(true) != null) return;
        if (FindObjectOfType<RiverTeleportPoint>(true) == null) return;
        new GameObject("RiverPointAvailability").AddComponent<RiverPointAvailability>();
    }

    private void Awake()
    {
        if (experimentManager == null) experimentManager = FindObjectOfType<ExperimentManager>();
        if (experimentManager == null)
            Debug.LogWarning("[RiverPointAvailability] No ExperimentManager found - river points stay hidden.", this);

        foreach (RiverTeleportPoint rp in FindObjectsOfType<RiverTeleportPoint>(true))
        {
            TeleportationAnchor anchor = rp.GetComponentInParent<TeleportationAnchor>(true);
            GameObject root = anchor != null ? anchor.gameObject : rp.gameObject;
            if (anchor == null)
                Debug.LogWarning($"[RiverPointAvailability] '{rp.name}' has no TeleportationAnchor parent; only its own object is controlled.", rp);

            // Key for per-point overrides, read BEFORE the root is detached from its LeftBank / RightBank parent.
            string key = root.transform.parent != null ? root.transform.parent.name + "/" + root.name : root.name;

            // Hide FIRST (nothing is rendered or registered), then detach from the shared inactive hierarchy.
            root.SetActive(false);
            if (root.transform.parent != null) root.transform.SetParent(null, worldPositionStays: true);

            Vector3 offset = signOffset + ExtraOffsetFor(key);
            CreateSign(root.transform, offset);
            roots.Add(root);
            if (enableDebugLogs) Debug.Log($"[RiverPointAvailability] Sign for '{key}' at world offset {offset} from the point.", this);
        }

        if (experimentManager != null)
        {
            experimentManager.OnExperimentEvent += OnExperimentEvent;
            experimentManager.OnEndPanelShown += Refresh;
        }
        Apply(Available());
        if (enableDebugLogs)
            Debug.Log($"[RiverPointAvailability] Controlling {roots.Count} river point(s); hidden until Part 3.", this);
    }

    private void OnDestroy()
    {
        if (experimentManager != null)
        {
            experimentManager.OnExperimentEvent -= OnExperimentEvent;
            experimentManager.OnEndPanelShown -= Refresh;
        }
    }

    private void OnExperimentEvent(ExperimentEventInfo info)
    {
        if (info.type == "part_start" || info.type == "part_end") Refresh();
    }

    private void Refresh() { Apply(Available()); }

    private bool Available() => experimentManager != null && experimentManager.IsRiverTeleportAvailable;

    // The rule only changes together with the events subscribed above (part_start, part_end, End Panel shown), so no per-frame
    // polling is needed. SetActive is idempotent per object.
    private void Apply(bool available)
    {
        foreach (GameObject r in roots)
            if (r != null && r.activeSelf != available) r.SetActive(available);
        if (enableDebugLogs)
            Debug.Log($"[RiverPointAvailability] River points {(available ? "SHOWN" : "HIDDEN")} (part '{(experimentManager != null ? experimentManager.RunningPartName : "?")}').", this);
    }

    // ---------------------------------------------------------------- sign
    private Vector3 ExtraOffsetFor(string key)
    {
        if (perPointExtraOffset != null)
            foreach (PointOffset o in perPointExtraOffset)
                if (o.key == key) return o.extraOffset;
        return Vector3.zero;
    }

    private GameObject CreateSign(Transform pointRoot, Vector3 worldOffset)
    {
        const float pxW = 1000f, pxH = 300f;

        var go = new GameObject("RiverInstructionSign", typeof(RectTransform));
        go.transform.SetParent(pointRoot, false);

        var canvas = go.AddComponent<Canvas>();            // deliberately NO GraphicRaycaster / TrackedDeviceGraphicRaycaster
        canvas.renderMode = RenderMode.WorldSpace;

        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new Vector2(pxW, pxH);
        rt.pivot = new Vector2(0.5f, 0.5f);

        // world scale independent of the point root's own scale
        Vector3 parentScale = pointRoot.lossyScale;
        float worldPerPx = signWidthMetres / pxW;
        rt.localScale = new Vector3(worldPerPx / Mathf.Max(parentScale.x, 1e-4f),
                                    worldPerPx / Mathf.Max(parentScale.y, 1e-4f),
                                    worldPerPx / Mathf.Max(parentScale.z, 1e-4f));
        rt.localPosition = pointRoot.InverseTransformVector(worldOffset);   // world-axis offset, correct under the root's scale

        var bgGo = new GameObject("Background", typeof(RectTransform));
        bgGo.transform.SetParent(go.transform, false);
        var bg = bgGo.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.8f);
        bg.raycastTarget = false;
        Stretch((RectTransform)bgGo.transform, 0f);

        var txtGo = new GameObject("Text", typeof(RectTransform));
        txtGo.transform.SetParent(go.transform, false);
        var txt = txtGo.AddComponent<TextMeshProUGUI>();
        txt.text = InstructionText;
        txt.alignment = TextAlignmentOptions.Center;
        txt.fontSize = 76f;
        txt.color = Color.white;
        txt.enableWordWrapping = true;
        txt.raycastTarget = false;
        Stretch((RectTransform)txtGo.transform, 30f);

        go.AddComponent<YawBillboard>();
        return go;
    }

    private static void Stretch(RectTransform r, float margin)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
        r.offsetMin = new Vector2(margin, margin); r.offsetMax = new Vector2(-margin, -margin);
    }
}
