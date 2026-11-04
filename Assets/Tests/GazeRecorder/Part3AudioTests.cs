// Part3AudioTests.cs
// Tests for the Part 3 think-aloud audio capture (schema v15): the pure core (WAV writer, PCM conversion, microphone ring cursor, clock map, index checks), the recorder's state machine driven by a SIMULATED
// microphone and a simulated CaptureClock, and the integration with DataCaptureManager / ExperimentManager (events, manifest, session closing).
// These are Editor tests with a synthetic microphone: they establish logic and file formats, NOT that a Quest Pro / Quest Link microphone works (that needs hardware).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

// The production CaptureClock lives in Assembly-CSharp: reached by reflection like every other production type.
static class AudioTestClock
{
    public static void Set(Func<double> f) { T("CaptureClock").GetField("TestSource").SetValue(null, f); }
    public static double Now => GetStatic<double>(T("CaptureClock"), "TAbs");
}

// ================================================================================================ pure core

public class Part3AudioPureTests
{
    static readonly Type Wav = T("WavFormat"), Writer = T("WavFileWriter"), Pcm = T("PcmConvert"), Cursor = T("MicCursor"), Map = T("AudioClockMap"), Queue = T("AudioWriteQueue"), IdxCheck = T("AudioIndexCheck");
    string _dir;

    [SetUp] public void SetUp() { _dir = Path.Combine(Application.temporaryCachePath, "p3audio_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }
    [TearDown] public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    static byte[] Bytes(params short[] v) { var b = new byte[v.Length * 2]; for (int i = 0; i < v.Length; i++) { b[2 * i] = (byte)(v[i] & 0xFF); b[2 * i + 1] = (byte)((v[i] >> 8) & 0xFF); } return b; }
    static object NewWriter() => Activator.CreateInstance(Writer);
    static object Validate(string p) => CallStatic(Wav, "Validate", p);

    [Test]
    public void WavHeader_IsTheCanonical44ByteRiffPcmHeader()
    {
        var h = (byte[])CallStatic(Wav, "BuildHeader", 48000, 2, 16, 96000L);
        Assert.AreEqual(44, h.Length);
        Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(h, 0, 4)); Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(h, 8, 4)); Assert.AreEqual("fmt ", System.Text.Encoding.ASCII.GetString(h, 12, 4)); Assert.AreEqual("data", System.Text.Encoding.ASCII.GetString(h, 36, 4));
        Assert.AreEqual(36u + 96000u, BitConverter.ToUInt32(h, 4)); Assert.AreEqual(16u, BitConverter.ToUInt32(h, 16)); Assert.AreEqual((ushort)1, BitConverter.ToUInt16(h, 20));
        Assert.AreEqual((ushort)2, BitConverter.ToUInt16(h, 22)); Assert.AreEqual(48000u, BitConverter.ToUInt32(h, 24)); Assert.AreEqual(48000u * 4u, BitConverter.ToUInt32(h, 28));
        Assert.AreEqual((ushort)4, BitConverter.ToUInt16(h, 32)); Assert.AreEqual((ushort)16, BitConverter.ToUInt16(h, 34)); Assert.AreEqual(96000u, BitConverter.ToUInt32(h, 40));
    }

    [Test]
    public void Pcm16Conversion_RoundsClipsAndKeepsTheChannelInterleaving()
    {
        var src = new float[] { 0f, 0.5f, -0.5f, 1f, -1f, 2f, float.NaN, 0.25f };   // stereo frames: (0,0.5) (-0.5,1) (-1,2) (NaN,0.25)
        var dst = new byte[src.Length * 2];
        var a = new object[] { src, 0, dst, 0, src.Length };
        int clipped = (int)Pcm.GetMethod("FloatToPcm16").Invoke(null, a);
        Assert.AreEqual(3, clipped, "1.0, -1.0 and 2.0 are clipped; nothing else is");
        Assert.AreEqual(Bytes(0, 16384, -16384, 32767, -32768, 32767, 0, 8192), dst, "0.5*32767 rounds to 16384; NaN -> 0; order L,R,L,R is preserved");
    }

    [Test]
    public void WavWriter_IncrementalWrites_FlushRefreshesTheHeader_FinishValidatesAndRenames_AndNeverOverwrites()
    {
        string path = Path.Combine(_dir, "a", "x.wav"); var w = NewWriter();
        Assert.IsTrue((bool)Call(w, "Open", path, 48000, 1));
        Assert.IsTrue(File.Exists(path + ".partial")); Assert.IsFalse(File.Exists(path), "the final name does not exist until validation");
        Assert.IsTrue((bool)Call(w, "Append", Bytes(1, 2, 3, 4), 8));
        Assert.IsTrue((bool)Call(w, "Flush"));
        var vp = Validate(path + ".partial");
        Assert.IsTrue(Get<bool>(vp, "valid"), "after a flush the partial file is a valid WAV of exactly the flushed frames: " + Get<string>(vp, "problem")); Assert.AreEqual(4L, Get<long>(vp, "frames"));
        Call(w, "Append", Bytes(5, 6), 4);
        Assert.AreEqual(6L, Get<long>(w, "FramesAppended")); Assert.AreEqual(4L, Get<long>(w, "FramesFlushed"));
        var vf = Call(w, "Finish");
        Assert.IsTrue(Get<bool>(vf, "valid"), Get<string>(vf, "problem")); Assert.AreEqual(6L, Get<long>(vf, "frames")); Assert.AreEqual(48000, Get<int>(vf, "sampleRate")); Assert.AreEqual(1, Get<int>(vf, "channels")); Assert.AreEqual(16, Get<int>(vf, "bitsPerSample"));
        Assert.AreEqual(6.0 / 48000.0, Get<double>(vf, "durationSec"), 1e-12);
        Assert.IsTrue(File.Exists(path)); Assert.IsFalse(File.Exists(path + ".partial"));
        var w2 = NewWriter(); Assert.IsFalse((bool)Call(w2, "Open", path, 48000, 1), "an existing file is never silently overwritten"); StringAssert.Contains("output_exists", Get<string>(w2, "Error"));
    }

    [Test]
    public void WavWriter_MisalignedAppendIsRefused_AndAWriteFailureIsReportedNotHidden()
    {
        var w = NewWriter(); Assert.IsTrue((bool)Call(w, "Open", Path.Combine(_dir, "b.wav"), 48000, 2));
        Assert.IsFalse((bool)Call(w, "Append", new byte[6], 6), "6 bytes is not a whole number of stereo PCM16 frames"); Assert.IsTrue(Get<bool>(w, "Failed"));
        var w2 = NewWriter(); Call(w2, "Open", Path.Combine(_dir, "c.wav"), 48000, 1); Set(w2, "TestFailAfterBytes", 6L);
        Assert.IsTrue((bool)Call(w2, "Append", new byte[4], 4)); Assert.IsFalse((bool)Call(w2, "Append", new byte[4], 4)); Assert.IsTrue(Get<bool>(w2, "Failed")); StringAssert.Contains("simulated write failure", Get<string>(w2, "Error"));
        var v = Call(w2, "Finish"); Assert.IsFalse(Get<bool>(v, "valid"), "a failed writer never reports a valid file"); Assert.IsFalse(File.Exists(Path.Combine(_dir, "c.wav")));
    }

    [Test]
    public void PartialRecovery_RebuildsAValidWavFromTheBytesThatReachedTheDisk_WithoutTouchingThePartialFile()
    {
        string path = Path.Combine(_dir, "r.wav"); var w = NewWriter(); Call(w, "Open", path, 16000, 2);
        Call(w, "Append", new byte[4 * 100], 4 * 100); Call(w, "Flush"); Call(w, "Append", new byte[4 * 30], 4 * 30);   // 30 more frames after the last header refresh
        Call(w, "Dispose");                                                                                             // process "dies": no Finish, stale header
        var stale = Validate(path + ".partial"); Assert.IsFalse(Get<bool>(stale, "valid"), "a crashed recording is NOT valid as it stands (header declares fewer frames than the file holds)");
        long before = new FileInfo(path + ".partial").Length;
        var a = new object[] { path + ".partial", Path.Combine(_dir, "recovered.wav"), 16000, 2, 0L };
        Assert.IsTrue((bool)Wav.GetMethod("TryRecoverPartial").Invoke(null, a)); Assert.AreEqual(130L, (long)a[4]);
        var rec = Validate(Path.Combine(_dir, "recovered.wav")); Assert.IsTrue(Get<bool>(rec, "valid")); Assert.AreEqual(130L, Get<long>(rec, "frames"));
        Assert.AreEqual(before, new FileInfo(path + ".partial").Length, "recovery is non-destructive");
        File.AppendAllText(path + ".partial", "x"); var a2 = new object[] { path + ".partial", Path.Combine(_dir, "recovered2.wav"), 16000, 2, 0L };
        Wav.GetMethod("TryRecoverPartial").Invoke(null, a2); Assert.AreEqual(130L, (long)a2[4], "a torn final frame is dropped, never half-written");
    }

    [Test]
    public void WriteQueue_Threaded_KeepsOrderAndCounts_AndCompletesBeforeFinish()
    {
        string path = Path.Combine(_dir, "q.wav"); var w = NewWriter(); Call(w, "Open", path, 8000, 1);
        var q = Activator.CreateInstance(Queue, w, true, 2000, 512, 0.05);
        long total = 0;
        for (int i = 0; i < 200; i++) { var b = (byte[])Call(q, "Rent"); for (int k = 0; k < 1000; k++) b[k] = (byte)(i & 0xFF); Call(q, "Enqueue", b, 1000); total += 500; }
        Assert.IsTrue((bool)Call(q, "Complete", 5000), "the writer thread drains and stops");
        var v = Call(w, "Finish"); Assert.IsTrue(Get<bool>(v, "valid"), Get<string>(v, "problem")); Assert.AreEqual(total, Get<long>(v, "frames"));
        var all = File.ReadAllBytes(path); for (int i = 0; i < 200; i++) Assert.AreEqual((byte)(i & 0xFF), all[44 + i * 1000], "chunk order preserved");
    }

    // ---- microphone ring: a simulated producer with a known signal ----
    sealed class Producer
    {
        public int ring, rate; public long produced;
        public Producer(int ring, int rate) { this.ring = ring; this.rate = rate; }
        public int Pos => (int)(produced % ring);
        public float At(long abs) => ((abs % 977) - 488) / 1000f;
        public float[] ReadRing(int start, int count) { var r = new float[count]; for (int i = 0; i < count; i++) { int slot = (start + i) % ring; long k = (produced - 1 - slot); long abs = k < 0 ? -1 : slot + (k / ring) * ring; r[i] = abs < 0 ? 0f : At(abs); } return r; }
    }

    static (int ringStart, int frames, long lost, long absStart) Adv(object c, int pos, double t)
    {
        var d = Call(c, "Advance", pos, t); Type dt = d.GetType();
        return ((int)dt.GetField("ringStart").GetValue(d), (int)dt.GetField("frames").GetValue(d), (long)dt.GetField("lostFrames").GetValue(d), (long)dt.GetField("absStart").GetValue(d));
    }

    [Test]
    public void MicCursor_SequentialDrainsAndWrapAround_ReconstructTheSourceExactly_WithAContinuousAbsoluteIndex()
    {
        var p = new Producer(4800, 48000); var c = Activator.CreateInstance(Cursor, 4800, 48000); Call(c, "Begin", 0, 0.0);
        var got = new List<float>(); long nextAbs = 0; double t = 0;
        for (int step = 0; step < 90; step++)   // 90 drains of ~25 ms = 2.25 s: the 0.1 s ring wraps ~22 times
        {
            t += 0.025; p.produced = (long)Math.Round(t * 48000);
            var d = Adv(c, p.Pos, t);
            Assert.AreEqual(0L, d.lost); Assert.AreEqual(nextAbs, d.absStart, "absolute index is continuous across ring wraps");
            for (int off = 0; off < d.frames; off += 1000) { int n = Math.Min(1000, d.frames - off); int s = (d.ringStart + off) % 4800; int first = Math.Min(n, 4800 - s); got.AddRange(p.ReadRing(s, first)); if (n > first) got.AddRange(p.ReadRing(0, n - first)); }
            nextAbs += d.frames;
        }
        Assert.AreEqual(p.produced, nextAbs); Assert.AreEqual(p.produced, got.Count);
        for (int i = 0; i < got.Count; i++) if (got[i] != p.At(i)) Assert.Fail("sample " + i + " differs from the source");
        Assert.AreEqual(0, Get<int>(c, "Overruns"));
    }

    [Test]
    public void MicCursor_DelayedDrain_WithinTheRing_LosesNothing_ButBeyondTheRing_CountsTheOverwrittenFramesExactly()
    {
        int ring = 48000; var p = new Producer(ring, 48000); var c = Activator.CreateInstance(Cursor, ring, 48000); Call(c, "Begin", 0, 0.0);
        p.produced = 40000; var d1 = Adv(c, p.Pos, 40000.0 / 48000); Assert.AreEqual(40000, d1.frames); Assert.AreEqual(0L, d1.lost);
        // 0.9 s later: delta 43200 < ring, no loss
        p.produced += 43200; var d2 = Adv(c, p.Pos, 40000.0 / 48000 + 0.9); Assert.AreEqual(43200, d2.frames); Assert.AreEqual(0L, d2.lost);
        // 2.5 s later (ring is 1 s): 120000 produced, only the last 48000 are intact
        p.produced += 120000; var d3 = Adv(c, p.Pos, 40000.0 / 48000 + 0.9 + 2.5);
        Assert.AreEqual(ring, d3.frames, "only a full ring can be read"); Assert.AreEqual(120000L - ring, d3.lost, "the overwritten frames are counted exactly, not invented");
        Assert.AreEqual(83200L + (120000 - ring), d3.absStart, "the first readable frame keeps its true absolute index");
        Assert.AreEqual(p.Pos, d3.ringStart, "after an overrun the intact data start at the write position");
        Assert.AreEqual(1, Get<int>(c, "Overruns")); Assert.AreEqual(120000L - ring, Get<long>(c, "LostFrames")); Assert.AreEqual(p.produced, Get<long>(c, "CapturedFrames"));
        var got = p.ReadRing(d3.ringStart, d3.frames); for (int i = 0; i < got.Length; i++) if (got[i] != p.At(d3.absStart + i)) Assert.Fail("post-overrun sample " + i + " has the wrong absolute index");
    }

    [Test]
    public void MicCursor_RandomisedDrains_NeverMisplaceASample()
    {
        var rnd = new System.Random(12345); int ring = 24000; var p = new Producer(ring, 48000); var c = Activator.CreateInstance(Cursor, ring, 48000); Call(c, "Begin", 0, 0.0);
        double t = 0; long consumed = 0, lost = 0;
        for (int i = 0; i < 400; i++)
        {
            t += rnd.NextDouble() < 0.05 ? 0.6 + rnd.NextDouble() : 0.005 + rnd.NextDouble() * 0.04;   // occasional 0.6-1.6 s stalls (ring = 0.5 s)
            p.produced = (long)Math.Round(t * 48000);
            var d = Adv(c, p.Pos, t); lost += d.lost; Assert.AreEqual(consumed + lost, d.absStart, "absStart = frames read so far + frames lost so far");
            var got = new List<float>(); int s = d.ringStart; int first = Math.Min(d.frames, ring - s); got.AddRange(p.ReadRing(s, first)); if (d.frames > first) got.AddRange(p.ReadRing(0, d.frames - first));
            for (int k = 0; k < got.Count; k++) if (got[k] != p.At(d.absStart + k)) Assert.Fail("iteration " + i + ": sample " + k + " misplaced");
            consumed += d.frames;
        }
        Assert.AreEqual(p.produced, consumed + lost, "every produced frame is either read or counted as lost");
    }

    [Test]
    public void ClockMap_FitsAFixedOffsetAndAMeasurableDrift_PerSegment_AndInvertsIt()
    {
        var m = Activator.CreateInstance(Map, 48000.0); Call(m, "BeginSegment", 0L);
        double drift = 1.0 + 80e-6, off = 100.25; var rnd = new System.Random(7);
        for (int i = 0; i <= 60; i++) { long s = i * 48000L; double t = off + s / (48000.0 * drift) + (rnd.NextDouble() - 0.5) * 0.004; Call(m, "AddAnchor", s, t, i, 0.0); }
        Assert.AreEqual(61, Get<int>(m, "AnchorCount"));
        var seg = Get<object>(m, "Current");
        Assert.AreEqual(80.0, Get<double>(seg, "DriftPpm"), 6.0, "the fitted rate ratio recovers the injected clock drift (anchors have ±2 ms jitter)");
        double t10 = (double)Call(m, "TimeOf", 10 * 48000L); Assert.AreEqual(off + 10 * 48000.0 / (48000.0 * drift), t10, 0.003);
        long back = (long)Call(m, "SampleAt", t10); Assert.AreEqual(10 * 48000L, back, 200L, "SampleAt inverts TimeOf within a few ms");
        double prev = double.NegativeInfinity; for (long s = 0; s < 60 * 48000L; s += 4799) { double t = (double)Call(m, "TimeOf", s); Assert.Greater(t, prev, "monotonic"); prev = t; }
        Assert.Less(Get<double>(seg, "residualRmsSec"), 0.003);
    }

    [Test]
    public void ClockMap_ASegmentBreak_KeepsTheSampleAxisContinuous_AndJumpsOnlyTheTimeAxis()
    {
        var m = Activator.CreateInstance(Map, 48000.0); Call(m, "BeginSegment", 0L);
        Call(m, "AddAnchor", 0L, 10.0, 0, 0.0); Call(m, "AddAnchor", 48000L, 11.0, 0, 0.0);
        Call(m, "BeginSegment", 48000L); Call(m, "AddAnchor", 48000L, 15.0, 0, 0.0); Call(m, "AddAnchor", 96000L, 16.0, 0, 0.0);   // a 4 s gap in the time axis, none in the sample axis
        Assert.AreEqual(10.5, (double)Call(m, "TimeOf", 24000L), 1e-6); Assert.AreEqual(15.5, (double)Call(m, "TimeOf", 72000L), 1e-6);
        Assert.AreEqual(2, Get<System.Collections.IList>(m, "Segments").Count);
        long inGap = (long)Call(m, "SampleAt", 13.0); Assert.AreEqual(48000L, inGap, "a time inside the gap clamps to the end of the previous segment (no sample is invented)");
        Assert.AreEqual(-1L, (long)Call(m, "SampleAt", 5.0), "before the first sample");
    }

    [Test]
    public void FirstSampleTime_IsTheReadingTimeMinusThePositionOverTheRate()
    {
        Assert.AreEqual(100.0 - 2400.0 / 48000.0, (double)CallStatic(Map, "FirstSampleTime", 100.0, 2400, 48000), 1e-12);
    }

    // ---- index writer helpers shared by the checker tests ----
    const string IdxHdr = "session_id,part_id,audio_recording_id,chunk_index,sample_index_start,sample_index_end_exclusive,t_abs_start_est,t_abs_end_est,sample_rate_hz,channels,frames_written,duration_sec,microphone_position,clock_anchor_id,dropped_frames,buffer_overrun_count,write_status,segment";
    static string D6(double v) => v.ToString("F6", CultureInfo.InvariantCulture);
    static string IRow(int idx, long s, long e, double t0, double t1, string status = "ok", int seg = 0) => $"S,Part3,R,{idx},{s},{e},{D6(t0)},{D6(t1)},48000,1,{e - s},0,0,0,0,0,{status},{seg}";
    string WriteIdx(params string[] rows) { string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".csv"); File.WriteAllLines(p, new[] { IdxHdr }.Concat(rows)); return p; }
    object Chk(string p, long frames, string anchors = null) => CallStatic(IdxCheck, "Check", p, frames, anchors);

    [Test]
    public void IndexCheck_AcceptsAContiguousIndex_AndRejectsGapsOverlapsAndFrameMismatches()
    {
        Assert.IsTrue(Get<bool>(Chk(WriteIdx(IRow(0, 0, 100, 1, 2), IRow(1, 100, 250, 2, 3)), 250), "consistent"));
        Assert.IsFalse(Get<bool>(Chk(WriteIdx(IRow(0, 0, 100, 1, 2), IRow(1, 120, 250, 2, 3)), 250), "consistent"), "a sample-index gap");
        Assert.IsFalse(Get<bool>(Chk(WriteIdx(IRow(0, 0, 100, 1, 2), IRow(1, 90, 250, 2, 3)), 250), "consistent"), "a sample-index overlap");
        Assert.IsFalse(Get<bool>(Chk(WriteIdx(IRow(0, 0, 100, 1, 2), IRow(1, 100, 250, 1.5, 3)), 250), "consistent"), "time going backwards");
        var bad = Chk(WriteIdx(IRow(0, 0, 100, 1, 2)), 250); Assert.IsFalse(Get<bool>(bad, "consistent")); StringAssert.Contains("differ_from_wav", Get<string>(bad, "problem"));
    }

    [Test]
    public void IndexCheck_ClassifiesDiscrepancies_FloatFormattingIsAccepted_ButAnyRealOverlapIsAMappingDefect_NotUncertainty()
    {
        // 1 microsecond difference = formatting of two 6-decimal numbers: accepted, classified float_representation
        var f = Chk(WriteIdx(IRow(0, 0, 100, 1, 2.000001), IRow(1, 100, 250, 2.000000, 3)), 250);
        Assert.IsTrue(Get<bool>(f, "consistent")); Assert.AreEqual("float_representation", Get<string>(f, "classification"));
        // the audited defect: adjacent chunks whose shared boundary was mapped twice (a 4.13 ms overlap) - this MUST fail now, whatever the clock uncertainty is
        var o = Chk(WriteIdx(IRow(0, 0, 100, 1, 2.00413), IRow(1, 100, 250, 2.0, 3)), 250);
        Assert.IsFalse(Get<bool>(o, "consistent")); StringAssert.Contains("time_overlap_at_row_2", Get<string>(o, "problem")); Assert.AreEqual("timestamp_mapping_defect", Get<string>(o, "classification"));
        // a time gap between contiguous chunks of one segment is equally a defect (it would mean two mappings)
        var g = Chk(WriteIdx(IRow(0, 0, 100, 1, 2.0), IRow(1, 100, 250, 2.004, 3)), 250);
        Assert.IsFalse(Get<bool>(g, "consistent")); StringAssert.Contains("time_gap_at_row_2", Get<string>(g, "problem"));
        // a reversed interval inside one row
        Assert.IsFalse(Get<bool>(Chk(WriteIdx(IRow(0, 0, 100, 2, 1)), 100), "consistent"));
        // chunk indices must be consecutive
        var ci = Chk(WriteIdx(IRow(0, 0, 100, 1, 2), IRow(2, 100, 250, 2, 3)), 250); StringAssert.Contains("chunk_index_not_consecutive", Get<string>(ci, "problem"));
        // a segment change needs an explicit gap row; with the gap row the discontinuity is accepted and classified as such
        var noGap = Chk(WriteIdx(IRow(0, 0, 100, 1, 2), IRow(1, 100, 250, 2, 3, "ok", 1)), 250); StringAssert.Contains("segment_change_without_gap_row", Get<string>(noGap, "problem"));
        var withGap = Chk(WriteIdx(IRow(0, 0, 100, 1, 2), IRow(1, 100, 100, 2, 5, "gap_interruption", 1), IRow(2, 100, 250, 5, 6, "ok", 1)), 250);
        Assert.IsTrue(Get<bool>(withGap, "consistent"), Get<string>(withGap, "problem")); Assert.AreEqual("actual_discontinuity", Get<string>(withGap, "classification")); Assert.AreEqual(1, Get<int>(withGap, "gapRows"));
        // a gap row that contains samples, or a non-gap row that is empty, is wrong
        Assert.IsFalse(Get<bool>(Chk(WriteIdx(IRow(0, 0, 100, 1, 2), IRow(1, 100, 150, 2, 3, "gap_overrun", 1)), 150), "consistent"));
        Assert.IsFalse(Get<bool>(Chk(WriteIdx(IRow(0, 0, 100, 1, 2), IRow(1, 100, 100, 2, 2)), 100), "consistent"));
    }

    [Test]
    public void ClockMap_Freeze_FinalisesTheFit_RefusesLateAnchors_AndGivesAdjacentChunksTheSameBoundaryTime()
    {
        double drift = 1.0 - 90e-6, off = 500.0; var rnd = new System.Random(3);
        var live = Activator.CreateInstance(Map, 48000.0); Call(live, "BeginSegment", 0L);
        var provisional = new List<double>();   // what the live map said about sample 48000 each time a new anchor arrived
        for (int i = 0; i <= 30; i++)
        {
            long smp = i * 24000L; double t = off + smp / (48000.0 * drift) + (rnd.NextDouble() - 0.5) * 0.010;
            Call(live, "AddAnchor", smp, t, 0, 0.0);
            if (i >= 8) provisional.Add((double)Call(live, "TimeOf", 48000L));
        }
        Assert.Greater(provisional.Max() - provisional.Min(), 1e-4, "the live map is a moving target: the same boundary gets different times while anchors keep arriving (the audited defect)");
        Assert.IsFalse(Get<bool>(live, "Frozen")); Call(live, "Freeze"); Assert.IsTrue(Get<bool>(live, "Frozen"));
        double endOfChunk1 = (double)Call(live, "TimeOfIn", 0, 48000L), startOfChunk2 = (double)Call(live, "TimeOfIn", 0, 48000L);
        Assert.AreEqual(endOfChunk1, startOfChunk2, "one frozen function: identical for the exclusive end of chunk 1 and the start of chunk 2");
        Assert.AreEqual(endOfChunk1, (double)Call(live, "TimeOf", 48000L), "TimeOf and TimeOfIn agree inside a segment");
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => Call(live, "AddAnchor", 99L, 1.0, 0, 0.0)); Assert.IsInstanceOf<InvalidOperationException>(ex.InnerException);
        Assert.Throws<System.Reflection.TargetInvocationException>(() => Call(live, "BeginSegment", 99L));
        Assert.AreEqual(-90.0, Get<double>(Get<object>(live, "Current"), "DriftPpm"), 25.0, "the final fit recovers the injected negative drift to within what 10 ms of anchor jitter over 15 s allows");
    }

    [Test]
    public void ClockMap_InjectedPositiveAndNegativeDrift_AreEstimatedPerSegment_AndMultiSegmentBoundariesAreExplicit()
    {
        foreach (double ppm in new[] { 150.0, -150.0 })
        {
            var m = Activator.CreateInstance(Map, 48000.0); Call(m, "BeginSegment", 0L);
            for (int i = 0; i <= 40; i++) { long s = i * 48000L; Call(m, "AddAnchor", s, 10.0 + s / (48000.0 * (1.0 + ppm * 1e-6)), 0, 0.0); }
            Call(m, "Freeze"); Assert.AreEqual(ppm, Get<double>(Get<object>(m, "Current"), "DriftPpm"), 1.0, "noise-free anchors: the drift is recovered exactly");
        }
        // two segments with different offsets and rates: each chunk end is mapped inside its own segment, the time jump is only in the gap
        var mm = Activator.CreateInstance(Map, 48000.0); Call(mm, "BeginSegment", 0L);
        for (int i = 0; i <= 20; i++) Call(mm, "AddAnchor", i * 48000L, 100.0 + i * 1.0000, 0, 0.0);
        Call(mm, "BeginSegment", 960000L);
        for (int i = 0; i <= 20; i++) Call(mm, "AddAnchor", 960000L + i * 48000L, 130.0 + i * 1.0001, 0, 0.0);
        Call(mm, "Freeze");
        Assert.AreEqual(120.0, (double)Call(mm, "TimeOfIn", 0, 960000L), 1e-6, "the exclusive end of the last chunk of segment 0 is mapped by segment 0");
        Assert.AreEqual(130.0, (double)Call(mm, "TimeOfIn", 1, 960000L), 1e-6, "the first chunk of segment 1 is mapped by segment 1: the 10 s difference is the explicit gap");
        Assert.AreEqual(2, Get<System.Collections.IList>(mm, "Segments").Count);
    }

    [Test]
    public void IndexCheck_ReproducesEveryChunkTimeFromThePersistedAnchors_AndDetectsATamperedTime()
    {
        var m = Activator.CreateInstance(Map, 48000.0); Call(m, "BeginSegment", 0L); var rnd = new System.Random(11);
        var anchors = new List<string> { "session_id,audio_recording_id,clock_anchor_id,segment,sample_index,t_abs,microphone_buffer_position,sample_rate_hz,dsp_time" };
        for (int i = 0; i <= 12; i++) { long s = i * 48000L; double t = 50.0 + s / (48000.0 * 1.0001) + (rnd.NextDouble() - 0.5) * 0.006; Call(m, "AddAnchor", s, t, 0, 0.0); anchors.Add($"S,R,{i},0,{s},{t.ToString("F9", CultureInfo.InvariantCulture)},0,48000,0"); }
        Call(m, "Freeze");
        string ap = Path.Combine(_dir, "anch.csv"); File.WriteAllLines(ap, anchors);
        var rows = new List<string>(); for (int c = 0; c < 12; c++) rows.Add(IRow(c, c * 48000L, (c + 1) * 48000L, (double)Call(m, "TimeOfIn", 0, c * 48000L), (double)Call(m, "TimeOfIn", 0, (c + 1) * 48000L)));
        var ok = Chk(WriteIdx(rows.ToArray()), 12 * 48000L, ap); Assert.IsTrue(Get<bool>(ok, "consistent"), Get<string>(ok, "problem")); Assert.Less(Get<double>(ok, "maxReproErrorSec"), 2e-6);
        // shift every time by 3 ms (still perfectly chained rows): the index is internally consistent but is NOT the frozen mapping of the persisted anchors
        var shifted = new List<string>(); for (int c = 0; c < 12; c++) shifted.Add(IRow(c, c * 48000L, (c + 1) * 48000L, (double)Call(m, "TimeOfIn", 0, c * 48000L) + 0.003, (double)Call(m, "TimeOfIn", 0, (c + 1) * 48000L) + 0.003));
        var bad = Chk(WriteIdx(shifted.ToArray()), 12 * 48000L, ap); Assert.IsFalse(Get<bool>(bad, "consistent")); StringAssert.Contains("not_reproducible_from_anchors", Get<string>(bad, "problem"));
        // a missing first anchor (the audited defect) breaks the id sequence and is refused
        var noFirst = new List<string>(anchors); noFirst.RemoveAt(1); string ap2 = Path.Combine(_dir, "anch2.csv"); File.WriteAllLines(ap2, noFirst);
        var nf = Chk(WriteIdx(rows.ToArray()), 12 * 48000L, ap2); Assert.IsFalse(Get<bool>(nf, "consistent")); StringAssert.Contains("anchor_ids_not_consecutive_from_0", Get<string>(nf, "problem"));
        Assert.IsFalse(Get<bool>(Chk(WriteIdx(rows.ToArray()), 12 * 48000L, Path.Combine(_dir, "nope.csv")), "consistent"), "missing anchors file");
    }
}

// ================================================================================================ recorder lifecycle (simulated microphone and clock)

public class Part3AudioRecorderTests
{
    sealed class FakeMic
    {
        public double clock = 1000.0, driftPpm = 0; public int rate = 48000, channels = 1, ring = 48000 * 3;
        public double startedAt = -1; public double startDelay = 0.05; public bool failStart, running, stalled, permissionOk = true, requestResult = true; public string[] devices = { "Fake Headset Mic" };
        public int startCalls, endCalls; public double stalledAt = -1; public long frozenProduced = -1;
        public long Produced() { if (!running) return 0; if (stalled) { if (frozenProduced < 0) frozenProduced = Math.Max(0L, (long)((stalledAt - startedAt - startDelay) * rate)); return frozenProduced; } return Math.Max(0L, (long)((clock - startedAt - startDelay) * rate * (1.0 + driftPpm * 1e-6))); }
        public static float At(long abs) => ((abs % 977) - 488) / 1000f;
        public object Api()
        {
            var a = Activator.CreateInstance(T("MicrophoneApi")); Type t = a.GetType();
            t.GetField("Devices").SetValue(a, (Func<string[]>)(() => devices));
            t.GetField("GetCaps").SetValue(a, (Func<string, int[]>)(d => new[] { 8000, 48000 }));
            t.GetField("Start").SetValue(a, (Func<string, bool, int, int, bool>)((d, loop, len, f) => { startCalls++; if (failStart) return false; running = true; stalled = false; frozenProduced = -1; startedAt = clock; ring = len * rate; return true; }));
            t.GetField("IsRecording").SetValue(a, (Func<string, bool>)(d => running));
            t.GetField("GetPosition").SetValue(a, (Func<string, int>)(d => (int)(Produced() % ring)));
            t.GetField("ClipFrames").SetValue(a, (Func<int>)(() => ring)); t.GetField("ClipChannels").SetValue(a, (Func<int>)(() => channels)); t.GetField("ClipFrequency").SetValue(a, (Func<int>)(() => rate));
            t.GetField("GetData").SetValue(a, (Func<float[], int, bool>)((buf, off) =>
            {
                long prod = Produced(); int frames = buf.Length / channels;
                for (int i = 0; i < frames; i++) { int slot = (off + i) % ring; long k = prod - 1 - slot; long abs = k < 0 ? -1 : slot + (k / ring) * ring; for (int c = 0; c < channels; c++) buf[i * channels + c] = abs < 0 ? 0f : At(abs) * (c + 1); }
                return true;
            }));
            t.GetField("End").SetValue(a, (Action<string>)(d => { running = false; endCalls++; }));
            return a;
        }
        public object Perm()
        {
            var p = Activator.CreateInstance(T("MicPermissionApi")); Type t = p.GetType();
            t.GetField("HasPermission").SetValue(p, (Func<bool>)(() => permissionOk));
            t.GetField("Request").SetValue(p, (Action<Action<bool>>)(done => { if (requestResult) permissionOk = true; done(requestResult); }));
            t.GetField("Applicable").SetValue(p, true);
            return p;
        }
    }

    FakeMic _m; string _dir; GameObject _go; Component _rec; List<(string type, double t, double? num, string str)> _events;

    [SetUp]
    public void SetUp()
    {
        _m = new FakeMic(); _dir = Path.Combine(Application.temporaryCachePath, "p3rec_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir);
        AudioTestClock.Set(() => _m.clock); _events = new List<(string, double, double?, string)>();
        _go = new GameObject("Part3AudioRecorderTest"); _rec = _go.AddComponent(T("Part3AudioRecorder"));
        Set(_rec, "mic", _m.Api()); Set(_rec, "permission", _m.Perm());
        Set(_rec, "sessionFolderProvider", (Func<string>)(() => _dir)); Set(_rec, "sessionIdProvider", (Func<string>)(() => "SESSION1"));
        Set(_rec, "eventSink", (Action<string, double, double?, string>)((ty, t, n, s) => _events.Add((ty, t, n, s))));
        Set(_rec, "useBackgroundWriter", false); Set(_rec, "minFreeStorageMegabytes", 0L); Set(_rec, "ringSeconds", 3);
        ((Behaviour)_rec).enabled = false;   // Update() must not run on its own: the test drives Tick() with the simulated clock
        LogAssert.ignoreFailingMessages = true;   // failure paths log errors on purpose (they are asserted through state, events and the manifest)
        Call(_rec, "Configure", true);
    }

    [TearDown] public void TearDown() { LogAssert.ignoreFailingMessages = false; AudioTestClock.Set(null); if (_go != null) UnityEngine.Object.DestroyImmediate(_go); try { Directory.Delete(_dir, true); } catch { } }

    // Runs a coroutine to completion, advancing the simulated clock by 'dt' per yield (a frame).
    void Run(IEnumerator e, double dt = 1.0 / 90)
    {
        LogAssert.ignoreFailingMessages = true;   // failure paths log errors on purpose (asserted through state, events and the manifest)
        int guard = 0; while (e.MoveNext()) { _m.clock += dt; Call(_rec, "Tick", _m.clock); if (++guard > 200000) Assert.Fail("coroutine did not finish"); }
    }
    void Step(double seconds, double dt = 1.0 / 90) { LogAssert.ignoreFailingMessages = true; double end = _m.clock + seconds; while (_m.clock < end) { _m.clock += dt; Call(_rec, "Tick", _m.clock); } }
    string StateName => Get<object>(_rec, "State").ToString();
    object Man => Get<object>(_rec, "Manifest");
    string AudioDir => Path.Combine(_dir, "audio");
    List<string[]> Index() { var l = File.ReadAllLines(Path.Combine(AudioDir, "audio_index.csv")); return l.Skip(1).Where(x => x.Length > 0).Select(x => x.Split(',')).ToList(); }
    void Ready() { Run((IEnumerator)Call(_rec, "RunPreflight")); Assert.AreEqual("Ready", StateName, Get<string>(_rec, "FailureReason")); }
    void StartOk(double clickBack = 0.0) { double click = _m.clock - clickBack; Run((IEnumerator)Call(_rec, "StartRecording", click)); Assert.AreEqual("Recording", StateName, Get<string>(_rec, "FailureReason")); }

    [Test]
    public void Success_RecordsTheWholeInterval_AsAValidWav_WithAMonotonicIndex_AndAlignedBoundaries()
    {
        Ready();
        double click = _m.clock; StartOk();
        double first = Get<double>(Man, "first_sample_t_abs_est");
        Assert.GreaterOrEqual(first, Get<double>(Man, "audio_start_requested_t_abs"), "no sample predates the request"); Assert.GreaterOrEqual(first, click);
        Assert.AreEqual((first - click) * 1000.0, Get<double>(Man, "audio_start_latency_ms"), 1e-6, "the latency is the measured gap between the Start click and the first sample");
        Assert.AreEqual(0.05, first - click, 0.03, "the simulated device starts 50 ms after Microphone.Start");
        Step(5.0);
        double tEnd = _m.clock;
        Assert.IsTrue((bool)Call(_rec, "StopAtBoundary", tEnd, "part3_trial_end"));
        var man = Man;
        Assert.AreEqual("completed", Get<string>(man, "status")); Assert.IsTrue(Get<bool>(man, "finalization_success")); Assert.IsTrue(Get<bool>(man, "index_consistent"), Get<string>(man, "index_validation"));
        var v = CallStatic(T("WavFormat"), "Validate", Path.Combine(AudioDir, "part3_audio.wav")); Assert.IsTrue(Get<bool>(v, "valid"), Get<string>(v, "problem"));
        long frames = Get<long>(v, "frames"); Assert.AreEqual(Get<long>(man, "total_frames"), frames);
        Assert.AreEqual((tEnd - first) * 48000.0, frames, 400.0, "the file covers [first sample, trial end) to within a few ms");
        Assert.IsFalse(File.Exists(Path.Combine(AudioDir, "part3_audio.wav.partial")), "the partial file was renamed only after validation");
        // content: the recorded stream is the source stream, sample for sample
        var bytes = File.ReadAllBytes(Path.Combine(AudioDir, "part3_audio.wav")); int bad = 0;
        for (int i = 0; i < 20000; i++) { short s = (short)(bytes[44 + 2 * i] | (bytes[45 + 2 * i] << 8)); short e = (short)Math.Round(FakeMic.At(i) * 32767f); if (s != e) bad++; }
        Assert.AreEqual(0, bad, "PCM16 of the first 20000 frames equals the simulated source");
        // index: contiguous, ends at the file length; times ordered; last time ~ tEnd
        var rows = Index(); Assert.Greater(rows.Count, 3);
        Assert.AreEqual(frames.ToString(), rows.Last()[5], "the last chunk ends at the final frame");
        double lastT = double.Parse(rows.Last()[7], CultureInfo.InvariantCulture); Assert.AreEqual(tEnd, lastT, 0.03, "the end of the audio coincides with the Part 3 end boundary");
        Assert.Less(first, tEnd);
        // lifecycle events in order, each on the shared clock
        var types = _events.Select(e => e.type).ToList();
        CollectionAssert.AreEqual(new[] { "audio_permission_checked", "audio_permission_granted", "audio_recording_requested", "audio_recording_started", "audio_recording_stop_requested", "audio_recording_stopped", "audio_file_finalized" }, types);
        for (int i = 1; i < _events.Count; i++) Assert.GreaterOrEqual(_events[i].t, _events[i - 1].t - 1e-9, "events are time ordered");
        Assert.AreEqual(Get<double>(man, "first_sample_t_abs_est"), first); Assert.AreEqual(tEnd, Get<double>(man, "end_t_abs"));
        Assert.AreEqual(48000, Get<int>(man, "sample_rate_hz")); Assert.AreEqual(1, Get<int>(man, "channels")); Assert.Greater(Get<int>(man, "clock_anchor_count"), 4);
        Assert.Greater(Get<double>(man, "rms_dbfs"), -100.0, "signal diagnostics present"); Assert.AreEqual(0, Get<int>(man, "buffer_overruns"));
        Assert.AreEqual("Closed", StateName);
    }

    [Test]
    public void Mapping_ConvertsAnyTAbsToTheRightSample_WithinTheStatedUncertainty()
    {
        Ready(); StartOk(); Step(8.0); Call(_rec, "StopAtBoundary", _m.clock, "end");
        var man = Man; var seg = ((JArray)JObject.Parse(JsonUtility.ToJson(man))["segments"])[0];
        double t0 = (double)seg["t_first_est"], rate = (double)seg["rate_hz_fit"];
        double tq = _m.clock - 3.0; long expected = (long)Math.Round((tq - t0) * rate);
        // what the simulated device actually put at that instant: frame index = (t - startedAt - startDelay) * rate
        long truth = (long)Math.Round((tq - (_m.startedAt + _m.startDelay)) * 48000);
        Assert.AreEqual(truth, expected, 480, "the fitted mapping lands within 10 ms of the device's real frame for an arbitrary t_abs");
        Assert.Less(Get<double>(man, "timing_uncertainty_ms"), 30.0); StringAssert.Contains("input latency", Get<string>(man, "timing_uncertainty_note"));
    }

    List<string[]> AnchorRows() { var l = File.ReadAllLines(Path.Combine(AudioDir, "audio_anchors.csv")); return l.Skip(1).Where(x => x.Length > 0).Select(x => x.Split(',')).ToList(); }

    [Test]
    public void FrozenIndex_EveryAdjacentRowChainsExactly_OnOneMapping_AndTheStopBoundaryIsUnchanged()
    {
        Ready(); StartOk(); Step(12.0); double tEnd = _m.clock;
        Assert.IsTrue((bool)Call(_rec, "StopAtBoundary", tEnd, "part3_trial_end"));
        var rows = Index(); Assert.Greater(rows.Count, 10);
        for (int i = 1; i < rows.Count; i++) Assert.AreEqual(rows[i - 1][7], rows[i][6], "row " + i + ": the exclusive end of chunk n and the start of chunk n+1 are the same string (same sample, same frozen mapping)");
        for (int i = 0; i < rows.Count; i++) Assert.AreEqual(i.ToString(), rows[i][3], "chunk indices are consecutive");
        Assert.IsTrue(Get<bool>(Man, "index_consistent"), Get<string>(Man, "index_validation"));
        Assert.AreEqual("final_frozen_affine_per_segment_v1", Get<string>(Man, "time_mapping")); Assert.IsTrue(Get<bool>(Man, "time_mapping_frozen"));
        Assert.AreEqual(0.0, Get<double>(Man, "index_max_boundary_mismatch_s"), 1e-12); Assert.AreEqual("none", Get<string>(Man, "index_timestamp_classification"));
        // the audio stop boundary is untouched by the mapping work: end_t_abs is the requested boundary and the file ends there to within the drain granularity
        Assert.AreEqual(tEnd, Get<double>(Man, "end_t_abs")); Assert.AreEqual(tEnd, double.Parse(rows.Last()[7], CultureInfo.InvariantCulture), 0.03);
        Assert.IsFalse(File.Exists(Path.Combine(AudioDir, "audio_index.csv.partial")), "the journal is superseded by the final index once it validated");
        Assert.AreEqual("Closed", StateName);
    }

    [Test]
    public void FirstAnchor_IsPersisted_TheCountsAgree_AndEveryRowIsReproducibleFromTheAnchorsFile()
    {
        Ready(); StartOk(); Step(10.0); Call(_rec, "StopAtBoundary", _m.clock, "end");
        var an = AnchorRows(); Assert.Greater(an.Count, 5);
        Assert.AreEqual("0", an[0][2], "the first anchor has id 0 and is in the file"); Assert.AreEqual("0", an[0][4], "...at sample index 0");
        for (int i = 0; i < an.Count; i++) Assert.AreEqual(i.ToString(), an[i][2], "anchor ids are consecutive from 0: nothing is held back in memory");
        Assert.AreEqual(an.Count, Get<int>(Man, "clock_anchor_count"), "the manifest count is the number of persisted rows");
        var meta = JObject.Parse(File.ReadAllText(Path.Combine(AudioDir, "audio_metadata.json"))); Assert.AreEqual(an.Count, (int)meta["clock_anchor_count"], "audio_metadata.json agrees with the anchors file");
        int segAnchors = ((JArray)meta["segments"]).Sum(x => (int)x["anchors"]); Assert.AreEqual(an.Count, segAnchors, "the per-segment fit used exactly the persisted anchors");
        var chk = CallStatic(T("AudioIndexCheck"), "Check", Path.Combine(AudioDir, "audio_index.csv"), Get<long>(Man, "total_frames"), Path.Combine(AudioDir, "audio_anchors.csv"));
        Assert.IsTrue(Get<bool>(chk, "consistent"), Get<string>(chk, "problem")); Assert.Less(Get<double>(chk, "maxReproErrorSec"), 2e-6);
        Assert.AreEqual(Get<string>(Man, "failure_reason"), (string)meta["failure_reason"], "metadata and manifest tell the same story");
    }

    [Test]
    public void ClockDrift_Positive_And_Negative_AreEstimated_AndNeverProduceAnOverlap([Values(200.0, -200.0)] double ppm)
    {
        _m.driftPpm = ppm;   // the microphone's sample clock runs fast / slow relative to CaptureClock
        Ready(); StartOk(); Step(40.0); Call(_rec, "StopAtBoundary", _m.clock, "end");
        var seg = ((JArray)JObject.Parse(JsonUtility.ToJson(Man))["segments"])[0];
        Assert.AreEqual(ppm, (double)seg["drift_ppm"], 40.0, "the final fit recovers the injected drift (anchors have a few ms of read granularity)");
        var rows = Index(); for (int i = 1; i < rows.Count; i++) Assert.AreEqual(rows[i - 1][7], rows[i][6], "no overlap and no hole, whatever the drift");
        Assert.IsTrue(Get<bool>(Man, "index_consistent"), Get<string>(Man, "index_validation")); Assert.AreEqual("Closed", StateName);
    }

    [Test]
    public void AnchorPersistenceFailure_IsAGenuineFinalizationFailure_NotHidden()
    {
        Ready(); StartOk(); Step(4.0);
        Set(_rec, "_anchorRowsPersisted", Get<long>(_rec, "_anchorRowsPersisted") - 1);   // one anchor never reached the file
        Assert.IsFalse((bool)Call(_rec, "StopAtBoundary", _m.clock, "end"));
        Assert.AreEqual("FinalizationFailed", StateName); StringAssert.Contains("anchors_persisted_", Get<string>(_rec, "FailureReason"));
        Assert.IsTrue(Get<bool>(Man, "finalization_success"), "the WAV itself is valid; only the anchor persistence failed"); Assert.AreEqual("incomplete", Get<string>(Man, "status"));
        CollectionAssert.Contains(_events.Select(e => e.type).ToList(), "audio_recording_failed");
    }

    [Test]
    public void GenuineTimeReversalInTheIndexFile_IsDetectedByTheChecker_AfterAValidRecording()
    {
        Ready(); StartOk(); Step(6.0); Call(_rec, "StopAtBoundary", _m.clock, "end");
        string path = Path.Combine(AudioDir, "audio_index.csv"); var lines = File.ReadAllLines(path).ToList(); var f = lines[3].Split(','); f[6] = (double.Parse(f[6], CultureInfo.InvariantCulture) - 0.004).ToString("F6", CultureInfo.InvariantCulture); lines[3] = string.Join(",", f);
        string tampered = Path.Combine(_dir, "tampered_index.csv"); File.WriteAllLines(tampered, lines);
        var r = CallStatic(T("AudioIndexCheck"), "Check", tampered, Get<long>(Man, "total_frames"), Path.Combine(AudioDir, "audio_anchors.csv"));
        Assert.IsFalse(Get<bool>(r, "consistent")); Assert.AreEqual("timestamp_mapping_defect", Get<string>(r, "classification"));
    }

    [Test]
    public void PermissionDenied_FailsThePreflight_VisiblyAndWithoutAnyAudioFile()
    {
        _m.permissionOk = false; _m.requestResult = false;
        Run((IEnumerator)Call(_rec, "RunPreflight"));
        Assert.AreEqual("PermissionDenied", StateName); Assert.AreEqual("microphone_permission_denied", Get<string>(_rec, "FailureReason"));
        Assert.IsFalse(Directory.Exists(AudioDir) && Directory.GetFiles(AudioDir).Length > 0, "nothing is written"); Assert.AreEqual("failed", Get<string>(Man, "status"));
        CollectionAssert.Contains(_events.Select(e => e.type).ToList(), "audio_recording_failed"); Assert.AreEqual(0, _m.startCalls, "no microphone was started without permission");
        // a later grant works
        _m.requestResult = true; _m.permissionOk = false; Run((IEnumerator)Call(_rec, "RunPreflight")); Assert.AreEqual("Ready", StateName);
        CollectionAssert.Contains(_events.Select(e => e.type).ToList(), "audio_permission_granted");
    }

    [Test]
    public void NoMicrophone_And_WrongPreferredDevice_AndWrongRequiredName_AreRefused_WithoutFallback()
    {
        _m.devices = new string[0]; Run((IEnumerator)Call(_rec, "RunPreflight")); Assert.AreEqual("DeviceUnavailable", StateName); Assert.AreEqual("no_microphone_device", Get<string>(_rec, "FailureReason"));
        _m.devices = new[] { "Realtek Desk Mic" }; Set(_rec, "preferredDevice", "Quest Pro Mic"); Run((IEnumerator)Call(_rec, "RunPreflight")); Assert.AreEqual("preferred_device_not_found", Get<string>(_rec, "FailureReason"), "an unrelated input is never substituted");
        Set(_rec, "preferredDevice", ""); Set(_rec, "requiredDeviceNameContains", "Quest"); Run((IEnumerator)Call(_rec, "RunPreflight")); Assert.AreEqual("device_name_does_not_match_required_text", Get<string>(_rec, "FailureReason"));
        Assert.AreEqual(0, _m.startCalls);
    }

    [Test]
    public void ProbeThatDeliversNoSamples_FailsThePreflight_AndTheProbeIsNeverRead()
    {
        _m.startDelay = 60.0;   // the device never produces within the probe window
        int reads = 0; var api = Get<object>(_rec, "mic"); var inner = (Func<float[], int, bool>)api.GetType().GetField("GetData").GetValue(api);
        api.GetType().GetField("GetData").SetValue(api, (Func<float[], int, bool>)((b, o) => { reads++; return inner(b, o); }));
        Run((IEnumerator)Call(_rec, "RunPreflight")); Assert.AreEqual("DeviceUnavailable", StateName); StringAssert.Contains("no_samples", Get<string>(_rec, "FailureReason"));
        Assert.AreEqual(0, reads, "preflight never reads microphone data: no pre-experiment audio is retained"); Assert.AreEqual(1, _m.endCalls);
    }

    [Test]
    public void StartFailure_Timeout_AndLatencyLimit_LeaveNoFile_AndARetryWithAFreshClickSucceeds()
    {
        Ready();
        _m.failStart = true; Run((IEnumerator)Call(_rec, "StartRecording", _m.clock)); Assert.AreEqual("StartFailed", StateName); Assert.AreEqual("microphone_start_failed", Get<string>(_rec, "FailureReason"));
        Assert.IsFalse(File.Exists(Path.Combine(AudioDir, "part3_audio.wav.partial")), "the aborted attempt's file is removed");
        _m.failStart = false; _m.startDelay = 30.0; Set(_rec, "startTimeoutSeconds", 1f);
        Run((IEnumerator)Call(_rec, "StartRecording", _m.clock)); Assert.AreEqual("StartFailed", StateName); Assert.AreEqual("no_microphone_samples_within_start_timeout", Get<string>(_rec, "FailureReason"));
        _m.startDelay = 0.5; Set(_rec, "startTimeoutSeconds", 3f); Set(_rec, "maxStartLatencySeconds", 1f);
        Run((IEnumerator)Call(_rec, "StartRecording", _m.clock - 2.0)); Assert.AreEqual("StartFailed", StateName); StringAssert.StartsWith("start_latency_exceeded", Get<string>(_rec, "FailureReason"));
        Assert.IsFalse(Directory.GetFiles(AudioDir).Any(f => f.EndsWith(".partial")), "no stray partial after a latency failure");
        _m.startDelay = 0.05; Run((IEnumerator)Call(_rec, "StartRecording", _m.clock)); Assert.AreEqual("Recording", StateName, "the retry (a new Start click) uses the same file name: nothing was left behind");
        Assert.AreEqual(4, Get<int>(Man, "start_attempts"));
    }

    [Test]
    public void DuplicateStartStopAndLateCallbacks_AreIdempotent()
    {
        Ready(); StartOk(); long starts = _m.startCalls;
        Run((IEnumerator)Call(_rec, "StartRecording", _m.clock)); Assert.AreEqual(starts, _m.startCalls, "a duplicate Start callback does not start a second recording"); Assert.AreEqual("Recording", StateName);
        Step(1.0); double tEnd = _m.clock;
        Assert.IsTrue((bool)Call(_rec, "StopAtBoundary", tEnd, "a")); long frames = Get<long>(Man, "total_frames"); int evCount = _events.Count;
        Assert.IsTrue((bool)Call(_rec, "StopAtBoundary", tEnd + 5, "b")); Assert.IsTrue((bool)Call(_rec, "FinalizeForSession", "session")); Step(1.0);
        Call(_rec, "OnApplicationPause", true); Call(_rec, "OnApplicationPause", false);
        Assert.AreEqual(frames, Get<long>(Man, "total_frames"), "nothing is recorded after the boundary"); Assert.AreEqual(evCount, _events.Count, "no duplicate or late events");
        Assert.AreEqual(tEnd, Get<double>(Man, "end_t_abs"), "the end boundary is the first one");
        Run((IEnumerator)Call(_rec, "StartRecording", _m.clock)); Assert.AreEqual(starts, _m.startCalls, "a finished recording cannot be restarted");
    }

    [Test]
    public void SessionEndBeforePart3End_FinalisesTheFile_ButMarksTheRecordingIncomplete()
    {
        Ready(); StartOk(); Step(2.0);
        Assert.IsTrue((bool)Call(_rec, "FinalizeForSession", "end_confirmed"));
        var man = Man; Assert.AreEqual("incomplete", Get<string>(man, "status")); StringAssert.Contains("session_ended_before_part3_end", Get<string>(man, "failure_reason"));
        var v = CallStatic(T("WavFormat"), "Validate", Path.Combine(AudioDir, "part3_audio.wav")); Assert.IsTrue(Get<bool>(v, "valid"), "the data that exist are a valid WAV"); Assert.Greater(Get<long>(v, "frames"), 80000L);
        Assert.AreEqual("Incomplete", StateName);
    }

    [Test]
    public void PauseAndResume_RecordsTheGapExplicitly_NeverFabricatesSamples_AndKeepsTheSampleAxisContinuous()
    {
        Ready(); StartOk(); Step(2.0);
        Call(_rec, "OnApplicationPause", true);
        _m.running = false; _m.clock += 3.0;                                  // the app is suspended: no ticks, the microphone stops
        Call(_rec, "OnApplicationPause", false); Step(0.2); Step(2.0);
        Assert.AreEqual("Recording", StateName, "the recorder restarted the microphone after the resume");
        double tEnd = _m.clock; Call(_rec, "StopAtBoundary", tEnd, "end");
        var man = Man; Assert.AreEqual("completed_with_gaps", Get<string>(man, "status")); Assert.AreEqual(1, Get<int>(man, "interruption_count")); Assert.AreEqual(3.0, Get<double>(man, "interruption_total_s"), 0.35);
        var gaps = Get<string[]>(man, "gaps"); Assert.AreEqual(1, gaps.Length); StringAssert.Contains("app_pause", gaps[0]);
        var rows = Index(); Assert.IsTrue(rows.Any(r => r[16] == "gap_interruption"), "the gap is a row of the index");
        var gap = rows.First(r => r[16] == "gap_interruption"); Assert.AreEqual(0L, long.Parse(gap[10]), "the gap contains no frames");
        double gs = double.Parse(gap[6], CultureInfo.InvariantCulture), ge = double.Parse(gap[7], CultureInfo.InvariantCulture); Assert.AreEqual(3.0, ge - gs, 0.4);
        var v = CallStatic(T("WavFormat"), "Validate", Path.Combine(AudioDir, "part3_audio.wav")); Assert.IsTrue(Get<bool>(v, "valid"));
        double recordedSeconds = Get<long>(v, "frames") / 48000.0; Assert.AreEqual(2.0 + 2.2, recordedSeconds, 0.5, "only the time the microphone really ran is in the file (the 3 s gap is not filled)");
        Assert.AreEqual(2, ((JArray)JObject.Parse(JsonUtility.ToJson(man))["segments"]).Count, "two continuous segments, each with its own time fit");
        CollectionAssert.Contains(_events.Select(e => e.type).ToList(), "audio_recording_interrupted"); CollectionAssert.Contains(_events.Select(e => e.type).ToList(), "audio_recording_resumed");
        Assert.IsTrue(Get<bool>(man, "index_consistent"), Get<string>(man, "index_validation"));
    }

    [Test]
    public void DeviceLoss_IsDetected_ARestartResumesIt_AndAnUnrecoverableLossEndsIncomplete()
    {
        Ready(); StartOk(); Step(1.0);
        _m.running = false; Step(0.5);   // microphone disappears
        Step(1.5);
        Assert.AreEqual("Recording", StateName); Assert.AreEqual(1, Get<int>(_rec, "InterruptionCount"));
        // now an unrecoverable loss
        _m.running = false; _m.failStart = true; Step(4.0);
        Assert.AreEqual("RecordingInterrupted", StateName, "after the bounded restart attempts the recorder stays interrupted, visibly");
        Call(_rec, "StopAtBoundary", _m.clock, "end");
        var man = Man; Assert.AreEqual("incomplete", Get<string>(man, "status")); StringAssert.Contains("ended_while_interrupted", Get<string>(man, "failure_reason")); Assert.AreEqual(2, Get<int>(man, "interruption_count"));
        Assert.IsTrue(Get<bool>(man, "finalization_success"), "the data captured so far are still finalised");
    }

    [Test]
    public void StalledInput_IsAnInterruption_NotSilentlyIgnored()
    {
        Ready(); StartOk(); Step(1.0);
        _m.stalledAt = _m.clock; _m.stalled = true; Step(2.5);   // IsRecording stays true but the position stops moving
        CollectionAssert.Contains(_events.Select(e => e.type).ToList(), "audio_recording_interrupted");
        StringAssert.Contains("input_stalled", _events.First(e => e.type == "audio_recording_interrupted").str);
        Assert.AreEqual(1, Get<int>(_rec, "InterruptionCount"));
    }

    [Test]
    public void BufferOverrun_IsDetectedAndCounted_TheSampleIndexStaysContinuous_AndNoSamplesAreInvented()
    {
        Ready(); StartOk(); Step(1.0);
        long before = Get<long>(Man, "total_frames");
        _m.clock += 7.0; Call(_rec, "Tick", _m.clock);   // the main thread stalled for 7 s; the ring holds 3 s
        Step(1.0);
        double tEnd = _m.clock; Call(_rec, "StopAtBoundary", tEnd, "end");
        var man = Man; Assert.AreEqual(1, Get<int>(man, "buffer_overruns")); Assert.AreEqual(4.0 * 48000, Get<long>(man, "lost_frames_overrun"), 2000, "7 s elapsed, 3 s kept -> about 4 s lost");
        Assert.AreEqual("completed_with_gaps", Get<string>(man, "status"));
        var rows = Index(); var gap = rows.First(r => r[16] == "gap_overrun"); Assert.AreEqual(Get<long>(man, "lost_frames_overrun").ToString(), gap[14]); Assert.AreEqual(0L, long.Parse(gap[10]));
        Assert.IsTrue(Get<bool>(man, "index_consistent"), Get<string>(man, "index_validation"));
        var v = CallStatic(T("WavFormat"), "Validate", Path.Combine(AudioDir, "part3_audio.wav")); Assert.IsTrue(Get<bool>(v, "valid"));
        Assert.AreEqual((1.0 + 3.0 + 1.0) * 48000, Get<long>(v, "frames"), 4000, "1 s before + the 3 s ring that survived + 1 s after; the lost 4 s are absent");
        // the surviving ring really is the data that followed the gap: compare a sample just after the gap with the source
        CollectionAssert.Contains(_events.Select(e => e.type).ToList(), "audio_buffer_overrun");
    }

    [Test]
    public void FileWriteFailure_IsVisible_TheRecordingIsMarkedIncomplete_AndTheFailureIsReported()
    {
        Set(_rec, "testFailWriterAfterBytes", 60000L);
        Ready(); StartOk(); Step(3.0);
        CollectionAssert.Contains(_events.Select(e => e.type).ToList(), "audio_recording_failed"); Assert.AreEqual("WriteFailed", StateName);
        Call(_rec, "StopAtBoundary", _m.clock, "end");
        var man = Man; Assert.AreEqual("incomplete", Get<string>(man, "status")); Assert.IsFalse(Get<bool>(man, "finalization_success")); Assert.IsFalse(Get<bool>(_rec, "FinalizationOk"));
        Assert.IsFalse(File.Exists(Path.Combine(AudioDir, "part3_audio.wav")), "a failed recording never gets the final (successful) name");
        Assert.IsTrue(File.Exists(Path.Combine(AudioDir, "part3_audio.wav.partial")), "the partial data are preserved for recovery");
    }

    [Test]
    public void FinalizationFailure_IsNotReportedAsSuccess_AndKeepsThePartialFile()
    {
        Set(_rec, "testFailWriterRename", true);
        Ready(); StartOk(); Step(1.5);
        Assert.IsFalse((bool)Call(_rec, "StopAtBoundary", _m.clock, "end")); Assert.AreEqual("FinalizationFailed", StateName); Assert.IsFalse(Get<bool>(_rec, "FinalizationOk"));
        StringAssert.Contains("finalization_failed", Get<string>(_rec, "FailureReason")); Assert.IsTrue(File.Exists(Path.Combine(AudioDir, "part3_audio.wav.partial")));
        CollectionAssert.Contains(_events.Select(e => e.type).ToList(), "audio_recording_failed"); Assert.IsFalse(_events.Any(e => e.type == "audio_file_finalized"));
    }

    [Test]
    public void ProcessDeathMidRecording_LeavesARecoverablePartialFile_AndNeverAFileThatLooksComplete()
    {
        Ready(); StartOk(); Step(3.0);
        string partial = Path.Combine(AudioDir, "part3_audio.wav.partial"); Assert.IsTrue(File.Exists(partial)); Assert.IsFalse(File.Exists(Path.Combine(AudioDir, "part3_audio.wav")));
        string snap = Path.Combine(_dir, "snapshot.partial");
        using (var src = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var dst = File.Create(snap)) src.CopyTo(dst);   // the disk state a killed process would leave
        var meta = JObject.Parse(File.ReadAllText(Path.Combine(AudioDir, "audio_metadata.json"))); Assert.AreEqual("recording", (string)meta["status"], "the metadata never claims completion while recording");
        var a = new object[] { snap, Path.Combine(_dir, "rec.wav"), 48000, 1, 0L };
        Assert.IsTrue((bool)T("WavFormat").GetMethod("TryRecoverPartial").Invoke(null, a)); Assert.Greater((long)a[4], 48000L, "at least a second of audio is recoverable");
        Assert.IsTrue(Get<bool>(CallStatic(T("WavFormat"), "Validate", Path.Combine(_dir, "rec.wav")), "valid"));
    }

    [Test]
    public void Disabled_Recorder_NeverStartsAnything()
    {
        Call(_rec, "Configure", false);
        Run((IEnumerator)Call(_rec, "RunPreflight")); Assert.AreEqual("Disabled", StateName); Assert.AreEqual(0, _m.startCalls);
        Assert.IsTrue((bool)Call(_rec, "StopAtBoundary", _m.clock, "x"), "stopping a recorder that never recorded is a no-op success");
        Assert.AreEqual("not_enabled", Get<string>(Man, "status"));
    }
}

// ================================================================================================ integration with the capture layer and ExperimentManager

public partial class NpcLifecycleTests
{
    // A simulated microphone on the REAL CaptureClock (the DataCaptureManager and its recorders keep running on real time).
    sealed class LiveMic
    {
        public double startedAt = -1, startDelay = 0.03; public bool running; public int rate = 48000, ring = 48000 * 5; public bool failStart; public string[] devices = { "Live Fake Mic" };
        long Produced() => running ? Math.Max(0L, (long)((AudioTestClock.Now - startedAt - startDelay) * rate)) : 0;
        public object Api()
        {
            var a = Activator.CreateInstance(T("MicrophoneApi")); Type t = a.GetType();
            t.GetField("Devices").SetValue(a, (Func<string[]>)(() => devices)); t.GetField("GetCaps").SetValue(a, (Func<string, int[]>)(d => new[] { 8000, 48000 }));
            t.GetField("Start").SetValue(a, (Func<string, bool, int, int, bool>)((d, loop, len, f) => { if (failStart) return false; running = true; startedAt = AudioTestClock.Now; ring = len * rate; return true; }));
            t.GetField("IsRecording").SetValue(a, (Func<string, bool>)(d => running)); t.GetField("GetPosition").SetValue(a, (Func<string, int>)(d => (int)(Produced() % ring)));
            t.GetField("ClipFrames").SetValue(a, (Func<int>)(() => ring)); t.GetField("ClipChannels").SetValue(a, (Func<int>)(() => 1)); t.GetField("ClipFrequency").SetValue(a, (Func<int>)(() => rate));
            t.GetField("GetData").SetValue(a, (Func<float[], int, bool>)((buf, off) => { long prod = Produced(); for (int i = 0; i < buf.Length; i++) { int slot = (off + i) % ring; long k = prod - 1 - slot; long abs = k < 0 ? -1 : slot + (k / ring) * ring; buf[i] = abs < 0 ? 0f : ((abs % 977) - 488) / 1000f; } return true; }));
            t.GetField("End").SetValue(a, (Action<string>)(d => running = false));
            return a;
        }
    }

    Component WireAudio(Rig rig, LiveMic lm)
    {
        var rec = (Component)Call(rig.dcm, "EnsureAudioRecorder");
        Assert.IsNotNull(rec, "the capture manager creates the recorder (no scene edit)");
        Set(rec, "mic", lm.Api()); Set(rec, "minFreeStorageMegabytes", 0L);
        return rec;
    }

    void SetRunningPart3(Rig rig, Component rec)
    {
        Set(rig.em, "_part3Audio", rec);
        Set(rig.em, "currentRunningPart", Enum.Parse(T("ExperimentManager").GetNestedType("RunningPart", All), "Part3"));
    }

    [UnityTest]
    public IEnumerator Audio_FullPart3Sequence_StartGateRecords_EndTrialStops_SessionClosesOnlyAfterTheAudioFiles_AndSessionEndStaysLast()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false, audio = true }, r => rig = r);
        yield return null;
        var lm = new LiveMic(); var rec = WireAudio(rig, lm); SetRunningPart3(rig, rec);

        // the same two gates RunPart3 uses, on the real components
        yield return (IEnumerator)Call(rig.em, "Part3AudioPreflightGate", rec);
        Assert.AreEqual("Ready", Get<object>(rec, "State").ToString());
        double click = AudioTestClock.Now;
        yield return (IEnumerator)Call(rig.em, "Part3AudioStartGate", rec, click);
        Assert.IsTrue(Get<bool>(rec, "IsRecording"), "BeginTrial would only run now: the microphone is confirmed first");
        Call(rec, "NoteExperimentStart", AudioTestClock.Now);
        yield return new WaitForSecondsRealtime(1.6f);
        Call(rig.em, "EndTrial");                                                   // the single Part 3 trial-end boundary
        double endT = Get<double>(Get<object>(rec, "Manifest"), "end_t_abs");
        Assert.Greater(endT, click); Assert.AreEqual("Closed", Get<object>(rec, "State").ToString());

        var audioDir = rig.File("audio");
        Assert.IsTrue(File.Exists(Path.Combine(audioDir, "part3_audio.wav"))); Assert.IsFalse(File.Exists(Path.Combine(audioDir, "part3_audio.wav.partial")));

        Call(rig.dcm, "OnSessionEnded");                                           // End panel confirmed: the existing finalisation
        var m = JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual(17, (int)m["schema_version"]);
        Assert.AreEqual("Closed", (string)m["capture_lifecycle_state"], "the session closed with the audio stream finalised");
        CollectionAssert.Contains(((JArray)m["capture_streams_closed"]).Select(t => (string)t).ToList(), "audio"); Assert.AreEqual(0, ((JArray)m["capture_streams_failed"]).Count);
        var a = m["audio_capture"]; Assert.AreEqual("completed", (string)a["status"]); Assert.IsTrue((bool)a["finalization_success"]); Assert.IsTrue((bool)a["index_consistent"]);
        Assert.AreEqual("audio/part3_audio.wav", (string)a["file"]); Assert.Greater((long)a["total_frames"], 48000L); Assert.AreEqual(click, (double)a["start_button_clicked_t_abs"], 1e-6);
        CollectionAssert.Contains(((JArray)m["file_list"]).Select(t => (string)t).ToList(), "audio/part3_audio.wav");

        var ev = Events(rig); var types = ev.Rows.Select(r => ev.Get(r, "event_type")).ToList();
        Assert.AreEqual("session_end", types.Last(), "session_end is still the final events.csv row");
        foreach (var e in new[] { "audio_permission_checked", "audio_recording_requested", "audio_recording_started", "audio_recording_stop_requested", "audio_recording_stopped", "audio_file_finalized" })
            { Assert.GreaterOrEqual(types.IndexOf(e), 0, e + " is present"); Assert.Less(types.IndexOf(e), types.Count - 1, e + " precedes session_end"); }
        Assert.AreEqual(1, types.Count(t => t == "audio_recording_started"), "one recording instance");
        double tStarted = D(ev.Get(ev.Rows[types.IndexOf("audio_recording_started")], "t_abs")), tStop = D(ev.Get(ev.Rows[types.IndexOf("audio_recording_stopped")], "t_abs"));
        Assert.AreEqual(Get<double>(Get<object>(rec, "Manifest"), "first_sample_t_abs_est") > 0, true); Assert.Greater(tStop, tStarted);
        Assert.AreEqual(1, types.Count(t => t == "audio_file_finalized"));
    }

    [UnityTest]
    public IEnumerator Audio_SessionEndingBeforeThePart3End_IsRecordedAsIncomplete_AndSessionEndIsStillLast()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false, audio = true }, r => rig = r);
        yield return null;
        var lm = new LiveMic(); var rec = WireAudio(rig, lm); SetRunningPart3(rig, rec);
        yield return (IEnumerator)Call(rig.em, "Part3AudioPreflightGate", rec); yield return (IEnumerator)Call(rig.em, "Part3AudioStartGate", rec, AudioTestClock.Now);
        yield return new WaitForSecondsRealtime(0.8f);
        Call(rig.dcm, "OnApplicationQuit");                                          // abnormal termination path, Part 3 never ended
        var m = JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual("incomplete", (string)m["audio_capture"]["status"]); StringAssert.Contains("session_ended_before_part3_end", (string)m["audio_capture"]["failure_reason"]);
        Assert.IsTrue(File.Exists(Path.Combine(rig.File("audio"), "part3_audio.wav")), "the data that were captured are a finalised, valid file");
        Assert.AreEqual("Closed", (string)m["capture_lifecycle_state"], "closing the streams succeeded; the recording is flagged incomplete, not the closure");
        var ev = Events(rig); Assert.AreEqual("session_end", ev.Rows.Last()[ev.Header.IndexOf("event_type")]);
    }

    [UnityTest]
    public IEnumerator Audio_FinalizationFailure_KeepsTheSessionInClosing_AndNamesTheAudioStream()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false, audio = true }, r => rig = r);
        yield return null;
        var lm = new LiveMic(); var rec = WireAudio(rig, lm); Set(rec, "testFailWriterRename", true); SetRunningPart3(rig, rec);
        yield return (IEnumerator)Call(rig.em, "Part3AudioPreflightGate", rec); yield return (IEnumerator)Call(rig.em, "Part3AudioStartGate", rec, AudioTestClock.Now);
        yield return new WaitForSecondsRealtime(0.6f);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Part3AudioRecorder\] finalization_failed"));
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"Session capture could NOT be closed cleanly"));
        Call(rig.em, "EndTrial"); Call(rig.dcm, "OnSessionEnded");
        var m = JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual("Closing", (string)m["capture_lifecycle_state"], "a failed audio finalisation must not produce a Closed session");
        CollectionAssert.Contains(((JArray)m["capture_streams_failed"]).Select(t => (string)t).ToList(), "audio");
        Assert.IsFalse((bool)m["audio_capture"]["finalization_success"]);
        var ev = Events(rig); Assert.AreEqual("session_end", ev.Rows.Last()[ev.Header.IndexOf("event_type")]);
    }

    [UnityTest]
    public IEnumerator Audio_RequiredGate_BlocksThePart3Start_WhenTheMicrophoneFails_AndOptionalLetsItContinueWithoutAudio()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false, audio = true }, r => rig = r);
        yield return null;
        var lm = new LiveMic { devices = new string[0] }; var rec = WireAudio(rig, lm); SetRunningPart3(rig, rec);
        LogAssert.ignoreFailingMessages = true;
        bool finished = false; var gate = (IEnumerator)Call(rig.em, "Part3AudioPreflightGate", rec);
        rig.em.GetType().GetMethod("StartCoroutine", new[] { typeof(IEnumerator) }).Invoke(rig.em, new object[] { Wrap(gate, () => finished = true) });
        yield return new WaitForSecondsRealtime(2.5f);
        Assert.IsFalse(finished, "required audio + no microphone: the Part 3 start stays blocked (retrying), it does not continue silently");
        Assert.AreEqual("DeviceUnavailable", Get<object>(rec, "State").ToString());
        // optional: continues, visibly recorded as failed, and the recorder is dropped from the flow
        Set(rig.em, "part3AudioRequired", false); bool finished2 = false;
        rig.em.GetType().GetMethod("StartCoroutine", new[] { typeof(IEnumerator) }).Invoke(rig.em, new object[] { Wrap((IEnumerator)Call(rig.em, "Part3AudioPreflightGate", rec), () => finished2 = true) });
        yield return new WaitForSecondsRealtime(0.5f);
        Assert.IsTrue(finished2); Assert.IsNull(Get<object>(rig.em, "_part3Audio")); Assert.AreEqual("failed", Get<string>(Get<object>(rec, "Manifest"), "status"));
        LogAssert.ignoreFailingMessages = false;
    }

    static IEnumerator Wrap(IEnumerator inner, Action done) { while (inner.MoveNext()) yield return inner.Current; done(); }

    [UnityTest]
    public IEnumerator Audio_IsPart3Only_ASessionThatNeverRunsPart3_HasNoRecorderNoFilesAndAnExplicitNotEnabledStatus()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { part2 = true, prewarm = 1, maxAgents = 1, npcCaptureEnabled = false, audio = true }, r => rig = r);
        yield return new WaitForSecondsRealtime(0.4f);
        Assert.IsNull(Get<object>(rig.dcm, "AudioRecorder"), "nothing creates the recorder outside the Part 3 flow");
        Call(rig.dcm, "OnSessionEnded");
        Assert.IsFalse(Directory.Exists(rig.File("audio")), "no audio directory");
        var m = JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual("not_enabled", (string)m["audio_capture"]["status"]); Assert.AreEqual("Closed", (string)m["capture_lifecycle_state"]);
        string src = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "ExperimentManager.cs"));
        int p1 = src.IndexOf("private IEnumerator RunPart1()", StringComparison.Ordinal), p2 = src.IndexOf("private IEnumerator RunPart2()", StringComparison.Ordinal), p3 = src.IndexOf("private IEnumerator RunPart3()", StringComparison.Ordinal);
        Assert.IsFalse(src.Substring(p1, p2 - p1).Contains("Part3Audio") || src.Substring(p2, p3 - p2).Contains("Part3Audio"), "Parts 1 and 2 never reference the audio recorder");
    }
}
