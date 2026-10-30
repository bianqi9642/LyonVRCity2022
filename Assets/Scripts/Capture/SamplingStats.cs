// SamplingStats.cs
// Online, allocation-free-per-sample measurement of the ACHIEVED sampling timing of one high-frequency stream (gaze, trajectory), computed from the stream's own
// monotonic timestamps (CaptureClock.TAbs) - never from the configured coroutine interval. Pure C#: no Unity types, so the production class is unit-testable with
// synthetic timestamps.
//
// Model
//   * A sample is registered only AFTER its row was written successfully (OnSample).
//   * Consecutive samples inside one EPOCH define an interval. An epoch ends at an INTENTIONAL gap: application pause, trial/capture window closed, recorder stopped
//     (BreakEpoch). The interval across a break is never counted, so a legitimate pause cannot masquerade as a low sensor rate.
//   * An in-epoch interval longer than GapThreshold (= max(GapFactor x nominal interval, MinGapSeconds)) is an UNEXPECTED GAP (writer stall, frame hitch, missed
//     ticks). It stays in the interval statistics (it is real timing) and is counted separately.
//   * Intervals <= 0 (duplicate or out-of-order timestamps) are counted in NonMonotonic and excluded.
//   * mean / observed rate are exact (running sums). median / p95 are computed from a bounded ring buffer of the most recent in-epoch intervals (BufferSize).
//   * With fewer than MinIntervalsForEstimate intervals the estimate is reported as unavailable (NaN here; the manifest stores available=false), never as 0.
// All comments in English per project convention.

using System;

public sealed class SamplingStats
{
    public const double GapFactor = 3.0;
    public const double MinGapSeconds = 0.1;
    public const int MinIntervalsForEstimate = 10;
    public const int BufferSize = 4096;

    public double NominalHz { get; private set; }
    public double NominalInterval => NominalHz > 0.0 ? 1.0 / NominalHz : double.NaN;
    public double GapThreshold => Math.Max(GapFactor * (NominalHz > 0.0 ? 1.0 / NominalHz : 0.0), MinGapSeconds);

    public long Samples { get; private set; }
    public long Intervals { get; private set; }
    public long NonMonotonic { get; private set; }
    public long UnexpectedGaps { get; private set; }
    public long IntentionalBreaks { get; private set; }
    public int Epochs { get; private set; }
    public double FirstT { get; private set; } = double.NaN;
    public double LastT { get; private set; } = double.NaN;
    public double MaxInterval { get; private set; } = double.NaN;
    public double SumInterval { get; private set; }

    readonly double[] _ring = new double[BufferSize];
    int _ringCount, _ringHead;
    bool _inEpoch;

    public SamplingStats(double nominalHz) { NominalHz = nominalHz; }

    public void SetNominalHz(double hz) { NominalHz = hz; }

    /// <summary>Registers one successfully written sample at monotonic time <paramref name="tAbs"/> (seconds).</summary>
    public void OnSample(double tAbs)
    {
        Samples++;
        if (double.IsNaN(FirstT)) FirstT = tAbs;
        if (!_inEpoch) { _inEpoch = true; Epochs++; }
        else
        {
            double dt = tAbs - LastT;
            if (dt <= 0.0) NonMonotonic++;
            else
            {
                Intervals++; SumInterval += dt;
                if (double.IsNaN(MaxInterval) || dt > MaxInterval) MaxInterval = dt;
                if (dt > GapThreshold) UnexpectedGaps++;
                _ring[_ringHead] = dt; _ringHead = (_ringHead + 1) % BufferSize; if (_ringCount < BufferSize) _ringCount++;
            }
        }
        LastT = tAbs;
    }

    /// <summary>Marks an INTENTIONAL gap (pause, capture window closed, recorder stopped). The next sample starts a new epoch. Idempotent.</summary>
    public void BreakEpoch()
    {
        if (!_inEpoch) return;
        _inEpoch = false;
        IntentionalBreaks++;
    }

    public bool EstimateAvailable => Intervals >= MinIntervalsForEstimate;
    public double MeanInterval => Intervals > 0 ? SumInterval / Intervals : double.NaN;
    /// <summary>Achieved rate = 1 / mean in-epoch interval = intervals / sum(intervals). NaN while the estimate is unavailable.</summary>
    public double ObservedHz => EstimateAvailable && SumInterval > 0.0 ? Intervals / SumInterval : double.NaN;

    public double MedianInterval => Percentile(0.5);
    public double P95Interval => Percentile(0.95);

    double Percentile(double p)
    {
        if (!EstimateAvailable || _ringCount == 0) return double.NaN;
        var copy = new double[_ringCount];
        Array.Copy(_ring, copy, _ringCount);   // ring slots 0.._ringCount-1 are all populated whether or not it has wrapped
        Array.Sort(copy);
        double idx = p * (_ringCount - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return copy[lo] + (copy[hi] - copy[lo]) * (idx - lo);
    }
}
