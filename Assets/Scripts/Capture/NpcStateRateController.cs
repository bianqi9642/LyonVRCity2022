// NpcStateRateController.cs
// Pure (engine-independent) controller that picks the interval between COMPLETE npc_state.csv population snapshots so that the state
// stream stays near a configured data budget (decimal MB per minute). It is the only lever the budget has: it never removes NPCs from a
// snapshot, and it never touches lifecycle events, per-lifetime attributes or session metadata (those are separate, unthrottled streams).
//
// Control rule (documented in docs/npc_data_schema.md):
//   - The budget is MEASURED, not guessed: every snapshot reports the bytes really appended to npc_state.csv. The average bytes per row is
//     an exponential moving average of those measurements; the predicted size of the next snapshot is (active NPCs) x (avg row bytes).
//   - required interval  T_req = predicted snapshot bytes x 60 / (budget x 1e6)  [seconds].
//   - Rising demand reacts at once: if T_req exceeds the current interval, the interval jumps to T_req x 1.05 (5 % safety margin).
//   - Falling demand is smoothed: the interval only decays (x 0.7 per snapshot) once the target is more than 20 % below it, so it cannot
//     oscillate from tick to tick. It never goes below the configured base interval nor above the configured maximum interval.
//   - If even the maximum interval cannot meet the budget, 'Unachievable' is raised and the real observed rate is reported honestly.
//   - Budget 0 = unlimited: the interval is always exactly the base interval.
// The observed rate is bytes written in a rolling window (RateWindowSeconds) normalised to MB/min.
// All comments in English per project convention.

using System;
using System.Collections.Generic;

public sealed class NpcStateRateController
{
    public const double RateWindowSeconds = 10.0;
    public const float  SafetyMargin = 1.05f;
    public const float  DecayFactor = 0.7f;
    public const float  DecayBand = 0.8f;
    public const double RowBytesEmaAlpha = 0.2;

    public enum Status { Unlimited, Met, Approached, Exceeded, ExceededUnachievable }

    public float BaseInterval { get; private set; } = 0.1f;
    public float MaxInterval  { get; private set; } = 2f;
    public double BudgetMBPerMinute { get; private set; }

    /// <summary>Interval to wait before the next complete snapshot (seconds). Always finite and within [BaseInterval, MaxInterval].</summary>
    public float EffectiveInterval { get; private set; } = 0.1f;

    public bool BudgetEnabled => BudgetMBPerMinute > 0.0;
    public bool Unachievable { get; private set; }

    // ---- statistics ----
    public long Snapshots { get; private set; }
    public long Rows { get; private set; }
    public long Bytes { get; private set; }
    public int  LastActiveNpcs { get; private set; }
    public int  MaxActiveNpcs { get; private set; }
    public double FirstSnapshotT { get; private set; } = double.NaN;
    public double LastSnapshotT { get; private set; } = double.NaN;
    public float  MinEffectiveInterval { get; private set; } = float.NaN;
    public float  MaxEffectiveIntervalSeen { get; private set; } = float.NaN;
    public long   ThrottledSnapshots { get; private set; }     // snapshots taken while the interval was above base
    public long   ExceededSnapshots { get; private set; }       // snapshots where the rolling rate was above budget x 1.05
    public double AvgRowBytes { get; private set; }
    public double ObservedMBPerMinute { get; private set; }
    public double PeakObservedMBPerMinute { get; private set; }
    public Status BudgetStatus { get; private set; } = Status.Unlimited;

    readonly Queue<KeyValuePair<double, long>> _window = new Queue<KeyValuePair<double, long>>();
    long _windowBytes;

    /// <summary>Mean snapshot frequency derived from the snapshot timestamps (not from the configured interval); NaN until two snapshots exist.</summary>
    public double ActualHz => Snapshots >= 2 && LastSnapshotT > FirstSnapshotT ? (Snapshots - 1) / (LastSnapshotT - FirstSnapshotT) : double.NaN;

    /// <summary>Sanitises an Inspector interval: NaN / infinite / non-positive -> fallback; positive but below MinInterval -> MinInterval.</summary>
    public const float MinInterval = 0.01f;
    public static float SanitizeBaseInterval(float v, float fallback, out bool changed)
    {
        changed = false;
        if (float.IsNaN(v) || float.IsInfinity(v) || v <= 0f) { changed = true; return fallback; }
        if (v < MinInterval) { changed = true; return MinInterval; }
        return v;
    }

    /// <summary>Sanitises the budget: NaN / infinite / negative -> 0 (unlimited, reported as changed so the caller can warn).</summary>
    public static float SanitizeBudget(float v, out bool changed)
    {
        changed = false;
        if (float.IsNaN(v) || float.IsInfinity(v) || v < 0f) { changed = true; return 0f; }
        return v;
    }

    /// <summary>Sanitises the maximum interval: invalid or below the base interval -> the base interval (no throttling room).</summary>
    public static float SanitizeMaxInterval(float v, float baseInterval, float fallback, out bool changed)
    {
        changed = false;
        if (float.IsNaN(v) || float.IsInfinity(v) || v <= 0f) { changed = true; v = fallback; }
        if (v < baseInterval) { changed = true; v = baseInterval; }
        return v;
    }

    /// <summary>Applies already-sanitised settings. Keeps the current interval when still inside the new range.</summary>
    public void Configure(float baseInterval, float maxInterval, double budgetMBPerMinute)
    {
        BaseInterval = baseInterval;
        MaxInterval = Math.Max(maxInterval, baseInterval);
        BudgetMBPerMinute = budgetMBPerMinute > 0.0 && !double.IsNaN(budgetMBPerMinute) && !double.IsInfinity(budgetMBPerMinute) ? budgetMBPerMinute : 0.0;
        if (!BudgetEnabled) { EffectiveInterval = BaseInterval; Unachievable = false; BudgetStatus = Status.Unlimited; }
        else EffectiveInterval = Math.Min(Math.Max(EffectiveInterval, BaseInterval), MaxInterval);
    }

    /// <summary>Forgets the rolling window and the interval state (statistics are kept). Used when sampling pauses and resumes.</summary>
    public void ResetState()
    {
        _window.Clear();
        _windowBytes = 0;
        EffectiveInterval = BaseInterval;
        Unachievable = false;
        ObservedMBPerMinute = 0.0;
        BudgetStatus = BudgetEnabled ? Status.Met : Status.Unlimited;
    }

    /// <summary>
    /// Reports one COMPLETE snapshot and returns the interval to wait before the next one.
    /// <paramref name="rows"/> = NPC rows in the snapshot (== active NPCs), <paramref name="bytes"/> = bytes appended for them (header excluded),
    /// <paramref name="nextPopulation"/> = the number of active NPCs the next snapshot is expected to contain.
    /// </summary>
    public float OnSnapshot(double tNow, int rows, long bytes, int nextPopulation)
    {
        Snapshots++; Rows += rows; Bytes += bytes;
        LastActiveNpcs = rows; if (rows > MaxActiveNpcs) MaxActiveNpcs = rows;
        if (double.IsNaN(FirstSnapshotT)) FirstSnapshotT = tNow;
        LastSnapshotT = tNow;

        if (rows > 0 && bytes > 0)
        {
            double perRow = (double)bytes / rows;
            AvgRowBytes = AvgRowBytes <= 0.0 ? perRow : AvgRowBytes + RowBytesEmaAlpha * (perRow - AvgRowBytes);
        }

        // rolling observed rate
        _window.Enqueue(new KeyValuePair<double, long>(tNow, bytes)); _windowBytes += bytes;
        while (_window.Count > 1 && tNow - _window.Peek().Key > RateWindowSeconds) _windowBytes -= _window.Dequeue().Value;
        double span = (tNow - _window.Peek().Key) + EffectiveInterval;
        ObservedMBPerMinute = span > 0.0 ? _windowBytes / span * 60.0 / 1e6 : 0.0;
        if (ObservedMBPerMinute > PeakObservedMBPerMinute) PeakObservedMBPerMinute = ObservedMBPerMinute;

        if (!BudgetEnabled)
        {
            EffectiveInterval = BaseInterval;
            Unachievable = false;
            BudgetStatus = Status.Unlimited;
        }
        else
        {
            double predictedBytes = Math.Max(0, nextPopulation) * Math.Max(AvgRowBytes, 1.0);
            double tReq = predictedBytes * 60.0 / (BudgetMBPerMinute * 1e6);
            float target = (float)Math.Min(Math.Max(tReq * SafetyMargin, BaseInterval), MaxInterval);
            Unachievable = tReq * SafetyMargin > MaxInterval;

            if (tReq > EffectiveInterval) EffectiveInterval = target;                                   // rising demand: at once
            else if (tReq * SafetyMargin < EffectiveInterval * DecayBand)                              // falling demand: smoothed, with a dead band
                EffectiveInterval = Math.Max(target, EffectiveInterval * DecayFactor);
            EffectiveInterval = Math.Min(Math.Max(EffectiveInterval, BaseInterval), MaxInterval);

            if (ObservedMBPerMinute > BudgetMBPerMinute * 1.05) ExceededSnapshots++;
            if (Unachievable && ObservedMBPerMinute > BudgetMBPerMinute) BudgetStatus = Status.ExceededUnachievable;
            else if (ObservedMBPerMinute > BudgetMBPerMinute * 1.05) BudgetStatus = Status.Exceeded;
            else if (ObservedMBPerMinute >= BudgetMBPerMinute * 0.9) BudgetStatus = Status.Approached;
            else BudgetStatus = Status.Met;
        }

        if (EffectiveInterval > BaseInterval * 1.0001f) ThrottledSnapshots++;
        if (float.IsNaN(MinEffectiveInterval) || EffectiveInterval < MinEffectiveInterval) MinEffectiveInterval = EffectiveInterval;
        if (float.IsNaN(MaxEffectiveIntervalSeen) || EffectiveInterval > MaxEffectiveIntervalSeen) MaxEffectiveIntervalSeen = EffectiveInterval;
        return EffectiveInterval;
    }

    public static string StatusName(Status s)
    {
        switch (s)
        {
            case Status.Unlimited: return "unlimited";
            case Status.Met: return "met";
            case Status.Approached: return "approached";
            case Status.Exceeded: return "exceeded";
            default: return "exceeded_unachievable";
        }
    }
}
