// UnexploredTeleportAvailability.cs
//
// During Part 2 the participant must not be able to see or use any teleportation point of the UNEXPLORED groups
//     TeleportationArea/OrganicTeleportPoint/Unexplored      and      TeleportationArea/GridTeleportPoint/Unexplored.
// Outside Part 2 (Part 1, Part 3, idle) every one of those objects is left exactly as the scene has it.
//
// What a point is made of (Teleport Anchor Variant prefab): root 'Teleport Anchor' (TeleportationAnchor + the destination child 'Anchor'), child 'Visuals' (MeshRenderer + the MeshCollider that the XRI interactor
// ray hits). The restriction switches off, per unexplored point and ONLY at component level:
//   * every XRBaseInteractable under the point (the TeleportationAnchor; a TeleportationArea would be covered too) -> the interactable unregisters from the XRInteractionManager: no hover, no select, no teleport request;
//   * every Renderer under the point (the visual marker and any other graphic);
//   * every Collider under the point that is NOT on the Ground layer (the marker's interaction collider; a collider that also provides ground contact is never touched).
// The GameObjects stay active, transforms and the destination child are untouched, nothing is destroyed, and no other part of the 'TeleportationArea' hierarchy is touched (it is saved inactive; this component
// never activates it).
//
// Restoration: the first time a component is switched off its ORIGINAL enabled value is stored (TeleportStateLedger) and written back exactly when the restriction ends. Components that were already disabled stay disabled;
// nothing is blindly set to enabled. Repeated entry is idempotent (the original is never overwritten by the restricted value).
//
// Phase integration (no polling, no per-frame work): ExperimentManager.OnExperimentEvent 'part_start' (info.part == Part2 -> restrict; any other Part -> restore) and 'part_end' / OnSessionEnded (restore). 'part_start' is raised
// synchronously at the top of RunPart2, before the participant has control, so there is no window in which an unexplored point can be selected. The initial state is taken from ExperimentManager.RunningPartName when the component is
// enabled (experiment started directly in Part 2, scene reload). The component is created automatically after the scene loads (like ParticipantControlLock / RiverPointAvailability): no scene edit is required.
// Backstop: GuardedTeleportationProvider rejects a request whose destination belongs to an Unexplored anchor while Part 2 runs (a target selected before Part 2 cannot complete).
// River teleport points, the scripted Part 2 cross-bank teleport and the Part 3 rules are not touched.
// All comments in English per project convention.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

[DisallowMultipleComponent]
public class UnexploredTeleportAvailability : MonoBehaviour
{
    public const string GroupName = "Unexplored";

    [Header("References (explicit slot -> auto-find -> warn)")]
    [SerializeField] private ExperimentManager experimentManager;

    [Header("What is restricted")]
    [Tooltip("The Part during which unexplored points are hidden and unavailable (ExperimentManager.RunningPartName).")]
    [SerializeField] private string restrictedPart = "Part2";
    [Tooltip("Name of the group object below each area root whose anchors are unexplored.")]
    [SerializeField] private string groupName = GroupName;
    [Tooltip("Area roots that contain an Unexplored group. An anchor is affected only when it lies below an object named 'Unexplored' that itself lies below one of these.")]
    [SerializeField] private string[] areaRootNames = { "GridTeleportPoint", "OrganicTeleportPoint" };
    [Tooltip("Colliders on these layers are NEVER disabled (they may provide ground contact). Default: layer 3 'Ground'.")]
    [SerializeField] private LayerMask neverDisableLayers = 1 << 3;

    [Header("Diagnostics")]
    [SerializeField] private bool enableDebugLogs = false;

    readonly TeleportStateLedger ledger = new TeleportStateLedger();
    readonly Dictionary<int, Component> components = new Dictionary<int, Component>();
    bool subscribed;

    /// <summary>True while the unexplored points are currently hidden / unavailable.</summary>
    public bool Restricted { get; private set; }
    /// <summary>Number of components currently switched off by this component (0 when not restricted).</summary>
    public int ManagedComponentCount => ledger.Count;
    /// <summary>Number of unexplored anchors found at the last restriction.</summary>
    public int AffectedAnchorCount { get; private set; }

    /// <summary>True when 'anchor' (or any transform) lies below an object named groupName that lies below one of areaRoots.</summary>
    public static bool IsUnexplored(Transform t, string groupName, string[] areaRoots)
    {
        bool inGroup = false;
        for (Transform p = t; p != null; p = p.parent)
        {
            if (p.name == groupName) inGroup = true;
            else if (inGroup && areaRoots != null && System.Array.IndexOf(areaRoots, p.name) >= 0) return true;
        }
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindObjectOfType<UnexploredTeleportAvailability>(true) != null) return;
        if (FindObjectOfType<ExperimentManager>(true) == null) return;
        new GameObject("UnexploredTeleportAvailability").AddComponent<UnexploredTeleportAvailability>();
    }

    private void OnEnable()
    {
        if (experimentManager == null) experimentManager = FindObjectOfType<ExperimentManager>();
        if (experimentManager == null)
        {
            Debug.LogWarning("[UnexploredTeleportAvailability] No ExperimentManager found - unexplored teleport points are left unchanged.", this);
            return;
        }
        if (!subscribed)
        {
            experimentManager.OnExperimentEvent += OnExperimentEvent;
            experimentManager.OnSessionEnded += OnSessionEnded;
            subscribed = true;
        }
        SetRestricted(experimentManager.RunningPartName == restrictedPart);   // started directly in Part 2 / scene reloaded while Part 2 runs
    }

    private void OnDisable()
    {
        Unsubscribe();
        SetRestricted(false);   // never leave anything switched off behind
    }

    private void OnDestroy() { Unsubscribe(); }

    private void Unsubscribe()
    {
        if (!subscribed || experimentManager == null) { subscribed = false; return; }
        experimentManager.OnExperimentEvent -= OnExperimentEvent;
        experimentManager.OnSessionEnded -= OnSessionEnded;
        subscribed = false;
    }

    private void OnExperimentEvent(ExperimentEventInfo info)
    {
        if (info.type == CaptureConstants.EvtPartStart) SetRestricted(info.part == restrictedPart);
        else if (info.type == CaptureConstants.EvtPartEnd) SetRestricted(false);
    }

    private void OnSessionEnded() { SetRestricted(false); }

    /// <summary>Idempotent: true = hide and disable the unexplored points, false = write the original states back.</summary>
    public void SetRestricted(bool restricted)
    {
        if (restricted) Restrict();
        else Restore();
    }

    private void Restrict()
    {
        int anchors = 0;
        foreach (TeleportationAnchor a in FindObjectsOfType<TeleportationAnchor>(true))
        {
            if (a == null || !IsUnexplored(a.transform, groupName, areaRootNames)) continue;
            anchors++;
            foreach (XRBaseInteractable i in a.GetComponentsInChildren<XRBaseInteractable>(true)) Switch(i, i.enabled);
            foreach (Renderer r in a.GetComponentsInChildren<Renderer>(true)) Switch(r, r.enabled);
            foreach (Collider c in a.GetComponentsInChildren<Collider>(true))
                if (((1 << c.gameObject.layer) & neverDisableLayers.value) == 0) Switch(c, c.enabled);
        }
        AffectedAnchorCount = anchors;
        Restricted = true;
        if (enableDebugLogs) Debug.Log($"[UnexploredTeleportAvailability] Restricted {anchors} unexplored anchors ({ledger.Count} components switched off).", this);
    }

    private void Switch(Component c, bool currentEnabled)
    {
        int id = c.GetInstanceID();
        if (ledger.Capture(id, currentEnabled)) components[id] = c;   // the original is stored once, never overwritten by the restricted value
        SetEnabled(c, false);
    }

    private void Restore()
    {
        if (ledger.Count > 0)
        {
            foreach (KeyValuePair<int, bool> kv in ledger.Snapshot())
                if (components.TryGetValue(kv.Key, out Component c) && c != null) SetEnabled(c, kv.Value);   // exactly the stored original
            if (enableDebugLogs) Debug.Log($"[UnexploredTeleportAvailability] Restored {ledger.Count} components to their original state.", this);
            ledger.Clear(); components.Clear();
        }
        Restricted = false;
    }

    private static void SetEnabled(Component c, bool value)
    {
        if (c is Behaviour b) b.enabled = value;
        else if (c is Renderer r) r.enabled = value;
        else if (c is Collider col) col.enabled = value;
    }
}
