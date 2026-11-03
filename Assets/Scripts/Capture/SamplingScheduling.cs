// SamplingScheduling.cs
// Pure C# helpers for the high-frequency capture loops (gaze, trajectory). No Unity types, so the production classes are unit-testable with synthetic timestamps.
//
// SampleScheduler
//   The loops used to "yield return new WaitForSecondsRealtime(interval)" after every sample. That wait resumes at the first frame AFTER the interval elapsed, so each sample
//   inherits a full frame of overshoot and the achieved rate drifts BELOW the nominal one even when the frame rate is high enough (20 Hz nominal at 90 fps ~ 18 Hz, at 60 fps
//   ~ 15 Hz). The scheduler keeps an absolute deadline grid instead: the next deadline is the previous deadline + interval, so the overshoot of one sample shortens the
//   next wait and the long-run mean converges to the nominal rate whenever the frame rate allows it.
//   It never bursts: if the loop is already late by a whole interval or more (hitch, frame rate below nominal) the grid is re-anchored to "now" and the missed ticks are
//   only COUNTED (Skipped) - no catch-up samples are fabricated. The returned wait is clamped to [0, interval] so a jumping test clock cannot stall a loop.
//
// FrameTimingStats
//   Frame-duration statistics (unscaled delta time) collected while a capture window is open. They answer "is the sampling rate limited by the render/update rate?":
//   a coroutine cannot run more than once per frame, so observed_hz can never exceed the frame rate.
//
// RateDiagnosis
//   Classifies why a stream's achieved rate differs from its nominal rate, from measured quantities only.
// All comments in English per project convention.

using System;

public sealed class SampleScheduler
{
    public double Interval { get; private set; }
    public long Ticks { get; private set; }
    public long SkippedTicks { get; private set; }
    public double MaxLateness { get; private set; }

    /// <summary>Smallest wait handed to WaitForSecondsRealtime (never 0: a zero wait could resume inside the same frame).</summary>
    public const double MinWait = 0.001;

    double _next = double.NaN;

    public SampleScheduler(double intervalSeconds) { Interval = intervalSeconds > 0.0 ? intervalSeconds : 1.0 / 30.0; }

    public void SetInterval(double intervalSeconds) { if (intervalSeconds > 0.0) Interval = intervalSeconds; }

    /// <summary>Forget the deadline grid (after a pause, a closed capture window or a stop): the next sample starts a new grid.</summary>
    public void Reset() { _next = double.NaN; }

    /// <summary>Call right after a sample taken at <paramref name="now"/>; returns the seconds to wait before the next sample (0..Interval).</summary>
    public double WaitAfterSample(double now)
    {
        Ticks++;
        if (double.IsNaN(_next)) _next = now;
        _next += Interval;
        double late = now - _next;
        if (late >= Interval)
        {
            // A whole interval or more behind (hitch, or frame rate below nominal): re-anchor, no burst; the missed ticks are only counted.
            SkippedTicks += (long)Math.Floor(late / Interval);
            if (late > MaxLateness) MaxLateness = late;
            _next = now + Interval;
            return Interval;
        }
        if (late > MaxLateness) MaxLateness = late;
        double wait = _next - now;
        if (wait > Interval) { _next = now + Interval; wait = Interval; }   // clock moved backwards / test clock jump: never wait longer than one interval
        return Math.Max(wait, MinWait);   // a strictly positive wait guarantees the loop never resumes twice in the same frame
    }
}

public sealed class FrameTimingStats
{
    public const int BufferSize = 2048;
    public long Frames { get; private set; }
    public double SumDt { get; private set; }
    public double MaxDt { get; private set; }
    public long Over100ms { get; private set; }

    readonly double[] _ring = new double[BufferSize];
    int _count, _head;

    public void OnFrame(double unscaledDt)
    {
        if (!(unscaledDt > 0.0) || double.IsInfinity(unscaledDt)) return;
        Frames++; SumDt += unscaledDt;
        if (unscaledDt > MaxDt) MaxDt = unscaledDt;
        if (unscaledDt > 0.1) Over100ms++;
        _ring[_head] = unscaledDt; _head = (_head + 1) % BufferSize; if (_count < BufferSize) _count++;
    }

    public bool Available => Frames >= 10;
    public double MeanHz => Available && SumDt > 0.0 ? Frames / SumDt : double.NaN;
    public double MedianDt => Percentile(0.5);
    public double P95Dt => Percentile(0.95);

    double Percentile(double p)
    {
        if (!Available || _count == 0) return double.NaN;
        var copy = new double[_count];
        Array.Copy(_ring, copy, _count);
        Array.Sort(copy);
        double idx = p * (_count - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return copy[lo] + (copy[hi] - copy[lo]) * (idx - lo);
    }
}

public static class RateDiagnosis
{
    /// <summary>
    /// insufficient_samples | nominal_achieved | frame_rate_limited | scheduling_or_io_limited.
    /// frame_rate_limited = the stream runs at (about) one sample per frame and the frame rate itself is below the nominal rate - the sampler cannot do better.
    /// scheduling_or_io_limited = well below BOTH nominal and the frame rate: samples were lost to blocked work, hitches or a scheduling defect.
    /// </summary>
    public static string Classify(double nominalHz, double observedHz, double frameMeanHz)
    {
        if (double.IsNaN(observedHz) || observedHz <= 0.0 || nominalHz <= 0.0) return "insufficient_samples";
        if (observedHz >= 0.9 * nominalHz) return "nominal_achieved";
        if (!double.IsNaN(frameMeanHz) && frameMeanHz > 0.0 && frameMeanHz < nominalHz && observedHz >= 0.8 * frameMeanHz) return "frame_rate_limited";
        return "scheduling_or_io_limited";
    }
}
