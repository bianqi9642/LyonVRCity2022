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

    // Per-stream observation windows: the longest frame and the number of frames seen since the stream last took its window. A sampling loop takes its window at every sample, so for a gap
    // between two samples it knows whether Unity ran frames in between and how long the longest one was (a frame that merely happens to be short at the moment of the sample proves nothing).
    public const int WindowStreams = 2;
    readonly double[] _winMax = new double[WindowStreams];
    readonly int[] _winFrames = new int[WindowStreams];
    public void TakeWindow(int stream, out double maxFrameDt, out int frames)
    {
        maxFrameDt = _winFrames[stream] > 0 ? _winMax[stream] : double.NaN; frames = _winFrames[stream];
        _winMax[stream] = 0.0; _winFrames[stream] = 0;
    }

    /// <summary>A frame too long for the frame statistics (a freeze / suspension, DataCaptureManager excludes them from the mean) is still evidence for the gap it caused: windows only.</summary>
    public void OnStallFrame(double unscaledDt)
    {
        if (!(unscaledDt > 0.0) || double.IsInfinity(unscaledDt)) return;
        for (int i = 0; i < WindowStreams; i++) { if (unscaledDt > _winMax[i]) _winMax[i] = unscaledDt; _winFrames[i]++; }
    }

    public void OnFrame(double unscaledDt)
    {
        if (!(unscaledDt > 0.0) || double.IsInfinity(unscaledDt)) return;
        for (int i = 0; i < WindowStreams; i++) { if (unscaledDt > _winMax[i]) _winMax[i] = unscaledDt; _winFrames[i]++; }
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

/// <summary>The cause a single unexpected sampling gap can be attributed to, from measured evidence only.</summary>
public enum GapCause { Frame, Scheduler, Io, Mixed, Unknown }

public static class RateDiagnosis
{
    /// <summary>A cause explains a gap by itself when it accounts for at least this share of the gap's duration.</summary>
    public const double DominantShare = 0.5;
    /// <summary>A cause contributes materially to a gap (and, for two causes, makes it "mixed") from this share of the gap's duration.</summary>
    public const double MaterialShare = 0.25;
    /// <summary>At stream level a cause is material when it explains at least this share of the attributed gaps.</summary>
    public const double StreamMaterialShare = 0.25;

    /// <summary>
    /// Attributes ONE gap (seconds between two consecutive written samples) from three independent measurements of the interval:
    ///   frameMax / frames - the longest Unity frame and the number of frames seen inside the gap (FrameTimingStats window): no frames = no update opportunity;
    ///   prevWrite         - how long the previous row's synchronous write (open + append + close) blocked the main thread;
    /// Io        : the write alone consumed at least half the gap (the write also lengthens the frame, so it is subtracted from the frame evidence - a slow write is I/O, not a "frame hitch");
    /// Frame     : frame time NOT explained by the write consumed at least half the gap (Unity did not give the sampler a chance to run);
    /// Mixed     : both consumed at least a quarter each;
    /// Scheduler : frames ran normally and writes were quick, yet the sample was dispatched late (the scheduler / coroutine wait is the remaining cause);
    /// Unknown   : no frame evidence (frames == 0 or NaN), so no cause can be established.
    /// </summary>
    public static GapCause AttributeGap(double gap, double frameMax, int frames, double prevWrite)
    {
        if (!(gap > 0.0) || double.IsNaN(frameMax) || frames <= 0) return GapCause.Unknown;
        double io = Math.Min(Math.Max(prevWrite, 0.0), gap);
        double frameExcess = Math.Max(0.0, frameMax - Math.Max(prevWrite, 0.0));
        bool ioMaterial = io >= MaterialShare * gap, frameMaterial = frameExcess >= MaterialShare * gap;
        if (ioMaterial && frameMaterial) return GapCause.Mixed;
        if (io >= DominantShare * gap) return GapCause.Io;
        if (frameExcess >= DominantShare * gap) return GapCause.Frame;
        return GapCause.Scheduler;
    }

    /// <summary>
    /// Stream-level diagnosis: healthy | frame_limited | scheduler_limited | io_limited | mixed | unknown (+ the reason). The rate shortfall is read from the gaps that were attributed with the
    /// evidence above; nothing is inferred from "the rate is low" alone.
    ///   healthy           observed rate >= 90 % of nominal;
    ///   no attributed gaps but a rate below nominal: frame_limited only when the MEAN frame rate itself is below nominal and the stream keeps up with it (>= 80 % of the frame rate, one
    ///                     sample per frame is the ceiling of a coroutine); io_limited only with writer failures / dropped records; otherwise unknown;
    ///   with attributed gaps: each cause's share counts "mixed" gaps for both frame and io; unknown gaps >= half of all -> unknown; one material cause (share >= 25 %) -> that cause;
    ///                     two or more -> mixed; none -> unknown. Writer failures / dropped records are I/O evidence in their own right.
    /// </summary>
    public static string ClassifyStream(double nominalHz, double observedHz, double frameMeanHz, long gapsFrame, long gapsScheduler, long gapsIo, long gapsMixed, long gapsUnknown, long writerRowsDropped, out string reason)
    {
        reason = "";
        if (double.IsNaN(observedHz) || observedHz <= 0.0 || nominalHz <= 0.0) { reason = "insufficient_samples"; return "unknown"; }
        if (observedHz >= 0.9 * nominalHz) { reason = "observed_rate_within_10_percent_of_nominal"; return "healthy"; }
        long attributed = gapsFrame + gapsScheduler + gapsIo + gapsMixed + gapsUnknown;
        bool ioEvidence = writerRowsDropped > 0;
        if (attributed == 0)
        {
            if (!double.IsNaN(frameMeanHz) && frameMeanHz > 0.0 && frameMeanHz < nominalHz && observedHz >= 0.8 * frameMeanHz) { reason = "mean_frame_rate_below_nominal_and_sampler_keeps_up"; return "frame_limited"; }
            if (ioEvidence) { reason = "writer_dropped_records"; return "io_limited"; }
            reason = "rate_below_nominal_without_attributable_gaps"; return "unknown";
        }
        if ((double)gapsUnknown / attributed >= DominantShare && !ioEvidence) { reason = "gaps_without_frame_evidence"; return "unknown"; }
        double frameShare = (double)(gapsFrame + gapsMixed) / attributed, ioShare = (double)(gapsIo + gapsMixed) / attributed, schedShare = (double)gapsScheduler / attributed;
        bool fm = frameShare >= StreamMaterialShare, im = ioShare >= StreamMaterialShare || ioEvidence, sm = schedShare >= StreamMaterialShare;
        int n = (fm ? 1 : 0) + (im ? 1 : 0) + (sm ? 1 : 0);
        if (n >= 2) { reason = "more_than_one_cause_is_material"; return "mixed"; }
        if (fm) { reason = "gaps_explained_by_long_frames"; return "frame_limited"; }
        if (im) { reason = ioEvidence ? "writer_dropped_records" : "gaps_explained_by_slow_writes"; return "io_limited"; }
        if (sm) { reason = "frames_and_writes_normal_but_dispatch_late"; return "scheduler_limited"; }
        reason = "no_cause_is_material"; return "unknown";
    }

    /// <summary>Coarse stream-less form (rate numbers only), kept for callers without gap evidence: healthy | frame_limited | unknown.</summary>
    public static string Classify(double nominalHz, double observedHz, double frameMeanHz)
    {
        return ClassifyStream(nominalHz, observedHz, frameMeanHz, 0, 0, 0, 0, 0, 0, out _);
    }
}
