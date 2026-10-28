// ParticipantControlLock.cs
//
// Locks the participant's VIRTUAL movement while ExperimentManager.ParticipantControlEnabled is false: before the Start panel is
// confirmed (WaitingForStart), from the moment the End panel appears, and after the session ended. Physical HMD tracking is not
// touched (the TrackedPoseDriver / XR Origin / camera hierarchy are left alone), and UI interaction is not touched either, so the
// Start and End buttons stay usable.
//
// What it locks (event-driven, no per-frame work)
//   Every XRI LocomotionProvider found in the scene EXCEPT teleportation providers: continuous move (DynamicMoveProvider,
//   OmnideckContinousMoveProvider), continuous / snap turn, climb, grab move. Each is switched off with 'enabled = false' and,
//   on unlock, restored to exactly the state it had when it was locked - so a provider that was disabled on purpose (the Snap
//   Turn provider) stays disabled. The ActionBasedControllerManager only toggles input ACTIONS, never these components, so it
//   cannot re-enable a locked provider.
//
// What it does NOT handle here (gated at the source instead, all by the same flag)
//   - Teleportation (generic XRI anchors/areas and river points): GuardedTeleportationProvider.Evaluate and
//     RiverTeleportPoint.TryTeleport / ExperimentManager.IsRiverTeleportAvailable reject while locked.
//   - Building selection: TargetSelectable.Select ignores clicks while locked.
//   - Scripted Part transitions: ExperimentManager.TeleportPlayerTo / StartPartN refuse after the session ended.
//
// The NPC simulation is NOT controlled here; it has its own lifecycle (SpawnerManager).
//
// Created automatically at scene load if the scene does not contain one.
//
// All comments in English per project convention.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

[DisallowMultipleComponent]
public class ParticipantControlLock : MonoBehaviour
{
    [Header("References (explicit slot -> auto-find -> warn)")]
    [SerializeField] private ExperimentManager experimentManager;

    [Header("Diagnostics")]
    [SerializeField] private bool enableDebugLogs = false;

    // Providers this component switched off, with the enabled state to restore. Empty while unlocked.
    private readonly Dictionary<LocomotionProvider, bool> locked = new Dictionary<LocomotionProvider, bool>();
    private static readonly List<LocomotionProvider> scratch = new List<LocomotionProvider>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindObjectOfType<ParticipantControlLock>(true) != null) return;
        if (FindObjectOfType<ExperimentManager>(true) == null) return;
        new GameObject("ParticipantControlLock").AddComponent<ParticipantControlLock>();
    }

    private void OnEnable()
    {
        if (experimentManager == null) experimentManager = FindObjectOfType<ExperimentManager>();
        if (experimentManager == null)
        {
            Debug.LogWarning("[ParticipantControlLock] No ExperimentManager found - participant movement is not gated.", this);
            return;
        }

        experimentManager.OnParticipantControlChanged += OnControlChanged;
        Apply(experimentManager.ParticipantControlEnabled);
    }

    private void OnDisable()
    {
        if (experimentManager != null) experimentManager.OnParticipantControlChanged -= OnControlChanged;
    }

    private void OnDestroy()
    {
        if (experimentManager != null) experimentManager.OnParticipantControlChanged -= OnControlChanged;
    }

    private void OnControlChanged(bool controlEnabled) { Apply(controlEnabled); }

    private void Apply(bool controlEnabled)
    {
        if (controlEnabled) Unlock(); else Lock();
    }

    private void Lock()
    {
        scratch.Clear();
        scratch.AddRange(FindObjectsOfType<LocomotionProvider>(true));   // includes inactive objects and late-added providers

        int count = 0;
        for (int i = 0; i < scratch.Count; i++)
        {
            LocomotionProvider p = scratch[i];
            if (p == null || p is TeleportationProvider) continue;   // teleport is gated in GuardedTeleportationProvider
            if (locked.ContainsKey(p)) { p.enabled = false; continue; }   // re-assert for a provider that was re-enabled meanwhile

            locked[p] = p.enabled;
            p.enabled = false;
            count++;
        }
        if (enableDebugLogs) Debug.Log($"[ParticipantControlLock] Locked {count} locomotion provider(s) ({locked.Count} held).", this);
    }

    private void Unlock()
    {
        int count = 0;
        foreach (KeyValuePair<LocomotionProvider, bool> kv in locked)
        {
            if (kv.Key == null) continue;
            kv.Key.enabled = kv.Value;   // back to exactly the pre-lock state
            count++;
        }
        locked.Clear();
        if (enableDebugLogs) Debug.Log($"[ParticipantControlLock] Restored {count} locomotion provider(s).", this);
    }
}
