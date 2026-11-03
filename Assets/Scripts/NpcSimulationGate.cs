// NpcSimulationGate.cs
// The ONE switch for "NPC behaviour is paused" (fixed behaviour warm-up -> frozen while the participant reads the Start panel -> resumed by the Start click).
// SpawnerManager.PauseNpcBehaviour / ResumeNpcBehaviour are the only writers; the NPC components read it:
//   * MoveToTarget       - holds the NavMeshAgent (SetSimulationHold) and cannot arrive / resample while held.
//   * UncertainBehavior  - its state timers and per-second random rolls do not advance (no RNG is consumed while paused).
//   * PedestrianAgent    - attractor dwell, capacity polling and dwell facing run on SimTime, which does not advance while paused.
// Why not Time.timeScale = 0: timeScale also stops the experiment clock consumers, UI, sampling coroutines and recorders; the pause must touch NPC behaviour ONLY. Rendering,
// colliders, identity / lifetime registration, gaze rays and every recorder keep running while paused (NPCs stay visible and raycastable).
// SimTime = Time.time minus the total time spent paused, so a dwell that had N seconds left when the pause began has exactly N seconds left when it ends.
// Static state is reset on subsystem registration (Editor "enter play mode without domain reload") and by SpawnerManager on Part reset / shutdown.
// All comments in English per project convention.

using UnityEngine;

public static class NpcSimulationGate
{
    /// <summary>True while NPC behaviour is paused.</summary>
    public static bool Paused { get; private set; }

    static float _pausedSince;
    static float _pausedTotal;

    /// <summary>Behaviour time: Time.time minus the time spent paused (frozen while paused).</summary>
    public static float SimTime => (Paused ? _pausedSince : Time.time) - _pausedTotal;

    /// <summary>Total seconds spent paused since the last Reset (diagnostics).</summary>
    public static float PausedTotalSeconds => _pausedTotal + (Paused ? Time.time - _pausedSince : 0f);

    /// <summary>Sets the pause state. Returns true only when the state actually changed (idempotent: a repeated call changes nothing).</summary>
    public static bool SetPaused(bool paused)
    {
        if (paused == Paused) return false;
        if (paused) { _pausedSince = Time.time; Paused = true; }
        else { _pausedTotal += Time.time - _pausedSince; Paused = false; }
        return true;
    }

    /// <summary>Clears the pause and the accumulated paused time (new Part, session shutdown, tests).</summary>
    public static void Reset() { Paused = false; _pausedSince = 0f; _pausedTotal = 0f; }

    /// <summary>Equivalent of WaitForSeconds(seconds) on behaviour time: does not elapse while paused.</summary>
    public static WaitUntil WaitSimSeconds(float seconds)
    {
        float end = SimTime + Mathf.Max(0f, seconds);
        return new WaitUntil(() => SimTime >= end);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetOnLoad() { Reset(); }
}
