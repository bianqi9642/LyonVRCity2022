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
    /// <summary>Subset of UnexpectedGaps whose interval is mostly explained by ONE long frame (the frame in which the sample was taken lasted at least half of the interval): the sampler could not run any sooner.</summary>
    public long GapsFrameLimited { get; private set; }
    /// <summary>Unexpected gaps attributed (RateDiagnosis.AttributeGap) to slow writes, to late dispatch with healthy frames, to both frame time and writes, or to nothing establishable.</summary>
    public long GapsIoLimited { get; private set; }
    public long GapsSchedulerLimited { get; private set; }
    public long GapsMixed { get; private set; }
    public long GapsUnknown { get; private set; }
    /// <summary>Duration of the stream's own synchronous row writes (what the sampler spent in the writer).</summary>
    public long Writes { get; private set; }
    public double WriteSecondsSum { get; private set; }
    public double WriteSecondsMax { get; private set; }
    double _prevWriteSeconds;
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
    public void OnSample(double tAbs) => OnSampleWithFrame(tAbs, double.NaN);

    /// <summary>As OnSample (a distinct name: the tests reach OnSample by reflection), with the duration of the frame the sample was taken in (unscaled delta time) so a gap can be attributed to frame time.</summary>
    /// <summary>
    /// As OnSampleWithFrame, with the evidence that separates the causes of a gap: the longest frame and the frame count seen since the previous sample (FrameTimingStats.TakeWindow) and
    /// how long THIS row's write took (the next gap is judged against it). NaN / 0 frames = no frame evidence.
    /// </summary>
    public void OnSampleWithEvidence(double tAbs, double frameWindowMax, int framesInWindow, double writeSeconds)
    {
        double prevWrite = _prevWriteSeconds;
        if (!double.IsNaN(writeSeconds) && writeSeconds >= 0.0) { Writes++; WriteSecondsSum += writeSeconds; if (writeSeconds > WriteSecondsMax) WriteSecondsMax = writeSeconds; _prevWriteSeconds = writeSeconds; }
        else _prevWriteSeconds = 0.0;
        _evidenceFrameMax = frameWindowMax; _evidenceFrames = framesInWindow; _evidencePrevWrite = prevWrite; _haveEvidence = true;
        OnSampleWithFrame(tAbs, frameWindowMax);
        _haveEvidence = false;
    }
    bool _haveEvidence; double _evidenceFrameMax, _evidencePrevWrite; int _evidenceFrames;

    public void OnSampleWithFrame(double tAbs, double frameDt)
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
                if (dt > GapThreshold)
                {
                    UnexpectedGaps++;
                    if (_haveEvidence)
                    {
                        switch (RateDiagnosis.AttributeGap(dt, _evidenceFrameMax, _evidenceFrames, _evidencePrevWrite))
                        {
                            case GapCause.Frame: GapsFrameLimited++; break;
                            case GapCause.Io: GapsIoLimited++; break;
                            case GapCause.Scheduler: GapsSchedulerLimited++; break;
                            case GapCause.Mixed: GapsMixed++; break;
                            default: GapsUnknown++; break;
                        }
                    }
                    else if (!double.IsNaN(frameDt) && frameDt >= 0.5 * dt) GapsFrameLimited++;   // legacy entry point (frame duration only)
                    else GapsUnknown++;
                }
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
