// GazeUiRegistry.cs
// Cached list of the Canvases (and their GraphicRaycasters) the gaze UI path tests - the "registered canvases" of GazeUiRaycaster. It exists so that no per-sample or per-frame scene search is needed:
// the experiment panels are instantiated at run time by UIManager, which reports each new panel hierarchy here (RegisterHierarchy); an authored GazeUIElement reports its own Canvas in OnEnable; and
// the recorder asks for ONE scene scan when a recording starts and after a scene load (RescanScene) as a safety net for canvases created by any other path.
//
// Lifecycle:
//   * Entries hold references only; a destroyed Canvas is detected by the Unity null check and pruned the next time the list is walked.
//   * Whether a Canvas is ACTIVE / ENABLED (SetActive(false) panels, Canvas.enabled = false while UIManager settles a panel's position, a disabled parent) is evaluated live by the raycaster on every
//     sample - never cached - so enable / disable needs no event.
//   * Static state is reset on a fresh play session (Enter Play Mode options with domain reload disabled) and flagged for a rescan after every scene load.
//
// Thread: main thread only. No allocation after warm-up.
// All comments in English per project convention.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class GazeUiRegistry
{
    /// <summary>One registered Canvas plus its GraphicRaycaster (null when the Canvas has none; re-resolved lazily).</summary>
    public sealed class Entry
    {
        public Canvas canvas;
        public GraphicRaycaster raycaster;
        public int canvasInstanceId;
    }

    static readonly List<Entry> s_entries = new List<Entry>(16);
    static readonly HashSet<int> s_ids = new HashSet<int>();
    static readonly List<Canvas> s_tmpCanvases = new List<Canvas>(16);
    static bool s_hooked;
    static bool s_rescanRequested = true;

    /// <summary>Registered entries. Walk it only through the raycaster (it prunes destroyed canvases); do not modify.</summary>
    public static List<Entry> Entries => s_entries;
    public static int Count => s_entries.Count;
    /// <summary>Number of full scene scans performed (diagnostic: stays tiny - one per recording start / scene load, never per frame).</summary>
    public static int SceneScans { get; private set; }
    /// <summary>True when a scene load happened since the last scan.</summary>
    public static bool RescanRequested => s_rescanRequested;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        s_entries.Clear(); s_ids.Clear(); s_hooked = false; s_rescanRequested = true; SceneScans = 0;
    }

    static void EnsureHooked()
    {
        if (s_hooked) return;
        s_hooked = true;
        SceneManager.sceneLoaded += (scene, mode) => { s_rescanRequested = true; };
    }

    /// <summary>Registers one Canvas (idempotent).</summary>
    public static void Register(Canvas canvas)
    {
        if (canvas == null) return;
        EnsureHooked();
        int id = canvas.GetInstanceID();
        if (!s_ids.Add(id)) return;
        var e = new Entry { canvas = canvas, canvasInstanceId = id };
        canvas.TryGetComponent(out e.raycaster);
        s_entries.Add(e);
    }

    /// <summary>Registers every Canvas under 'root' (inactive ones included). Called by UIManager for each panel it instantiates.</summary>
    public static void RegisterHierarchy(GameObject root)
    {
        if (root == null) return;
        root.GetComponentsInChildren<Canvas>(true, s_tmpCanvases);
        for (int i = 0; i < s_tmpCanvases.Count; i++) Register(s_tmpCanvases[i]);
        s_tmpCanvases.Clear();
    }

    /// <summary>One scene-wide scan for Canvases (inactive ones included). Called once when a recording starts and once after a scene load - never per frame.</summary>
    public static void RescanScene()
    {
        EnsureHooked();
        SceneScans++;
        s_rescanRequested = false;
        Canvas[] all = Object.FindObjectsOfType<Canvas>(true);
        for (int i = 0; i < all.Length; i++) Register(all[i]);
    }

    /// <summary>Removes entries whose Canvas was destroyed. Called by the raycaster while walking the list.</summary>
    public static void Prune()
    {
        for (int i = s_entries.Count - 1; i >= 0; i--)
        {
            if (s_entries[i].canvas != null) continue;
            s_ids.Remove(s_entries[i].canvasInstanceId);
            s_entries.RemoveAt(i);
        }
    }

    /// <summary>Test hook: forgets every entry.</summary>
    public static void ClearForTests()
    {
        s_entries.Clear(); s_ids.Clear(); s_rescanRequested = true;
    }
}
