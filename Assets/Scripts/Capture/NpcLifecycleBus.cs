// NpcLifecycleBus.cs
// Observer channel between the NPC simulation (SpawnerManager, PedestrianAgent) and the data-capture layer
// (DataCaptureManager). The simulation raises one NpcLifecycleEvent at the moment a lifecycle transition really happens and
// stamps it with CaptureClock at THAT moment; the recorder only formats and writes it. It never feeds back into the simulation.
//
// Why a bus and not only the existing SpawnerManager.AgentSpawned / AgentDespawned events:
//   - Prewarm creation happens in SpawnerManager.Start(), which can run before any recorder exists. Events raised while no
//     recorder is attached are kept in a bounded queue and replayed IN ORDER with their ORIGINAL timestamps when the recorder
//     attaches. Nothing is ever written under a placeholder session.
//   - PedestrianAgent must not depend on DataCaptureManager. The static bus keeps that dependency one-way.
//
// MASTER SWITCH: the recorder installs a gate (SetGate) that answers "is NPC capture enabled right now?" - it is DataCaptureManager's
// resolution of the per-Part npcCaptureEnabled setting. While the gate says no, Raise() discards the event on the spot: it is neither
// delivered, nor queued, nor replayed later (only a counter is kept). The simulation never sees the gate; it only raises events.
//
// Observation only: sink exceptions are caught and never reach NPC code. All comments in English per project convention.

using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>One NPC lifecycle transition, captured at the instant it occurred.</summary>
public struct NpcLifecycleEvent
{
    public string type;            // CaptureConstants.NpcEvt* value
    public double tAbs;            // CaptureClock.TAbs at the moment of the transition
    public long   tUnixMs;         // CaptureClock.TUnixMs at the same moment
    public PedestrianAgent agent;  // may be null if the agent was destroyed before the event was processed
    public string agentId;         // stable pooled-object ID (PedestrianAgent.AgentID)
    public string lifetimeId;      // "<AgentID>-S<n>" or "" when the event is not tied to an active lifetime
    public int    lifetimeIndex;   // n above, 0 when not tied to a lifetime
    public Vector3 pos;            // NPC position at the event (for a teleport: BEFORE the move)
    public float  yaw;
    public bool   hasDest;
    public Vector3 dest;           // meaning depends on type: nav/target destination, or the position AFTER a teleport
    public int    itineraryIndex;  // -1 when not applicable
    public string targetId;        // itinerary target name, "" when not applicable
    public string value;           // free "key=value;key=value" detail (no commas)
}

public static class NpcLifecycleBus
{
    /// <summary>Maximum number of events held while no recorder is attached. Beyond it events are dropped and counted.</summary>
    public const int MaxPending = 50000;

    static Action<NpcLifecycleEvent> s_sink;
    static Func<bool> s_gate;
    static int s_suppressed;
    static readonly List<NpcLifecycleEvent> s_pending = new List<NpcLifecycleEvent>(256);
    static int s_dropped;
    static bool s_sinkErrorLogged;

    public static bool HasSink => s_sink != null;
    public static int PendingCount => s_pending.Count;
    /// <summary>Events lost because the pending queue was full while no recorder was attached.</summary>
    public static int DroppedPending => s_dropped;
    /// <summary>Events discarded because NPC capture was disabled (master switch off). Never replayed.</summary>
    public static int SuppressedByGate => s_suppressed;
    /// <summary>True when a gate is installed. Without a gate (no recorder yet) events are queued until one attaches.</summary>
    public static bool HasGate => s_gate != null;
    /// <summary>Whether an event raised now would be kept (gate absent = queue for the recorder; gate present = its answer).</summary>
    public static bool WouldRecord { get { try { return s_gate == null || s_gate(); } catch { return false; } } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { ResetForTests(); }

    /// <summary>Clears the sink, the pending queue and the counters (play-mode entry and tests).</summary>
    public static void ResetForTests()
    {
        s_sink = null;
        s_gate = null;
        s_pending.Clear();
        s_dropped = 0;
        s_suppressed = 0;
        s_sinkErrorLogged = false;
    }

    /// <summary>
    /// Installs the master-switch gate. Anything already queued while capture is disabled is discarded now (never replayed); the gate is
    /// then consulted for every event at the moment it is raised.
    /// </summary>
    public static void SetGate(Func<bool> gate)
    {
        s_gate = gate;
        if (gate != null && s_pending.Count > 0 && !SafeGate())
        {
            s_suppressed += s_pending.Count;
            s_pending.Clear();
        }
    }

    public static void ClearGate(Func<bool> gate)
    {
        if (s_gate == gate) s_gate = null;
    }

    static bool SafeGate()
    {
        try { return s_gate == null || s_gate(); }
        catch { return false; }   // a failing gate must never turn recording ON
    }

    public static void Raise(NpcLifecycleEvent e)
    {
        if (!SafeGate()) { s_suppressed++; return; }
        if (s_sink != null) { Deliver(s_sink, e); return; }
        if (s_pending.Count < MaxPending) s_pending.Add(e);
        else s_dropped++;
    }

    /// <summary>Attaches the recorder and replays every event raised before it existed, in order, with its original timestamps.</summary>
    public static void Attach(Action<NpcLifecycleEvent> sink)
    {
        if (sink == null) return;
        s_sink = sink;
        // Delivering may raise new events (it must not, but stay safe): drain by index.
        for (int i = 0; i < s_pending.Count; i++) Deliver(sink, s_pending[i]);
        s_pending.Clear();
    }

    public static void Detach(Action<NpcLifecycleEvent> sink)
    {
        if (s_sink == sink) s_sink = null;
    }

    static void Deliver(Action<NpcLifecycleEvent> sink, NpcLifecycleEvent e)
    {
        try { sink(e); }
        catch (Exception ex)
        {
            if (!s_sinkErrorLogged)
            {
                s_sinkErrorLogged = true;
                Debug.LogError("[NpcLifecycleBus] recorder threw while handling '" + e.type + "' (further errors suppressed): " + ex);
            }
        }
    }
}
