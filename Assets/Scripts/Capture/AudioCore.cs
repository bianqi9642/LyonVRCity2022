// AudioCore.cs
// The pure, unit-testable core of the Part 3 think-aloud audio capture: WAV container, PCM conversion, the microphone ring-buffer cursor and the sample-index <-> t_abs clock map.
// No Unity scene access; the only Unity type used is Mathf-free arithmetic, so everything here runs in a plain test.
//
// CONVENTIONS (one set, used by the recorder, the index files, the manifest and the downstream script)
//   * A SAMPLE FRAME is one sample per channel. sample_index counts frames of the WAV file (0-based, never reset, never counts lost frames).
//   * A timestamp t_abs(sample_index) is the instant the frame STARTED (a frame occupies [t, t + 1/sample_rate)). Intervals are half-open: [t0, t1).
//   * t_abs is CaptureClock.TAbs (Time.realtimeSinceStartupAsDouble) like every other stream. The microphone has its own sample clock: the mapping is an affine fit per continuous
//     SEGMENT (a segment ends at an interruption or an overrun), estimated from periodic anchors (sample_index, t_abs), never assumed to be exactly nominal.
// All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// ---------------------------------------------------------------------------------------------------------------------------------- WAV

public sealed class WavValidation
{
    public bool valid;
    public string problem = "";
    public int sampleRate, channels, bitsPerSample;
    public long declaredDataBytes, actualDataBytes, frames, fileBytes;
    public double durationSec;
}

public static class WavFormat
{
    public const int HeaderSize = 44;

    /// <summary>The canonical 44-byte PCM WAV header for dataBytes of sample data.</summary>
    public static byte[] BuildHeader(int sampleRate, int channels, int bitsPerSample, long dataBytes)
    {
        var h = new byte[HeaderSize];
        int blockAlign = channels * bitsPerSample / 8;
        Put(h, 0, "RIFF"); PutU32(h, 4, (uint)Math.Min(0xFFFFFFFFL, 36 + dataBytes)); Put(h, 8, "WAVE");
        Put(h, 12, "fmt "); PutU32(h, 16, 16); PutU16(h, 20, 1); PutU16(h, 22, (ushort)channels); PutU32(h, 24, (uint)sampleRate);
        PutU32(h, 28, (uint)(sampleRate * blockAlign)); PutU16(h, 32, (ushort)blockAlign); PutU16(h, 34, (ushort)bitsPerSample);
        Put(h, 36, "data"); PutU32(h, 40, (uint)Math.Min(0xFFFFFFFFL, dataBytes));
        return h;
    }

    static void Put(byte[] b, int at, string s) { for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i]; }
    static void PutU16(byte[] b, int at, ushort v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
    static void PutU32(byte[] b, int at, uint v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
    static uint GetU32(byte[] b, int at) => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
    static ushort GetU16(byte[] b, int at) => (ushort)(b[at] | (b[at + 1] << 8));

    /// <summary>Reads and checks the header of a WAV file, comparing the declared data length with the bytes actually present. Never throws.</summary>
    public static WavValidation Validate(string path)
    {
        var v = new WavValidation();
        try
        {
            if (!File.Exists(path)) { v.problem = "file_missing"; return v; }
            var fi = new FileInfo(path); v.fileBytes = fi.Length;
            if (fi.Length < HeaderSize) { v.problem = "shorter_than_header"; return v; }
            var h = new byte[HeaderSize];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { if (fs.Read(h, 0, HeaderSize) != HeaderSize) { v.problem = "header_read_failed"; return v; } }
            if (Encoding.ASCII.GetString(h, 0, 4) != "RIFF" || Encoding.ASCII.GetString(h, 8, 4) != "WAVE") { v.problem = "bad_riff_wave_signature"; return v; }
            if (Encoding.ASCII.GetString(h, 12, 4) != "fmt " || GetU16(h, 20) != 1) { v.problem = "not_pcm_fmt"; return v; }
            v.channels = GetU16(h, 22); v.sampleRate = (int)GetU32(h, 24); v.bitsPerSample = GetU16(h, 34);
            if (Encoding.ASCII.GetString(h, 36, 4) != "data") { v.problem = "data_chunk_not_at_44"; return v; }
            v.declaredDataBytes = GetU32(h, 40); v.actualDataBytes = fi.Length - HeaderSize;
            int blockAlign = v.channels * v.bitsPerSample / 8;
            if (v.channels < 1 || v.sampleRate < 1 || v.bitsPerSample != 16 || blockAlign < 1) { v.problem = "unsupported_format"; return v; }
            if (GetU16(h, 32) != blockAlign || GetU32(h, 28) != (uint)(v.sampleRate * blockAlign)) { v.problem = "inconsistent_block_align_or_byte_rate"; return v; }
            if (GetU32(h, 4) != 36 + v.declaredDataBytes) { v.problem = "riff_size_mismatch"; return v; }
            if (v.declaredDataBytes != v.actualDataBytes) { v.problem = "declared_data_length_differs_from_file"; return v; }
            if (v.actualDataBytes % blockAlign != 0) { v.problem = "data_not_a_whole_number_of_frames"; return v; }
            v.frames = v.actualDataBytes / blockAlign; v.durationSec = v.frames / (double)v.sampleRate; v.valid = true;
        }
        catch (Exception e) { v.problem = "exception:" + e.GetType().Name; }
        return v;
    }

    /// <summary>
    /// Recovers the sample data of an interrupted (".partial") recording into a NEW valid WAV (the partial file is never modified). The frame count comes from the bytes physically present,
    /// not from the possibly stale header, truncated to a whole number of frames.
    /// </summary>
    public static bool TryRecoverPartial(string partialPath, string outPath, int sampleRate, int channels, out long frames)
    {
        frames = 0;
        try
        {
            if (!File.Exists(partialPath) || File.Exists(outPath)) return false;
            int blockAlign = channels * 2;
            long dataBytes = new FileInfo(partialPath).Length - HeaderSize;
            if (dataBytes < blockAlign) return false;
            frames = dataBytes / blockAlign; long keep = frames * blockAlign;
            using (var src = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dst = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write))
            {
                var hdr = BuildHeader(sampleRate, channels, 16, keep); dst.Write(hdr, 0, hdr.Length);
                src.Seek(HeaderSize, SeekOrigin.Begin);
                var buf = new byte[64 * 1024]; long left = keep;
                while (left > 0) { int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left)); if (n <= 0) break; dst.Write(buf, 0, n); left -= n; }
                dst.Flush(true);
            }
            return true;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>Incremental PCM16 WAV writer. Writes to "&lt;final&gt;.partial" with a header that is refreshed at every Flush(), so an interrupted file is always recoverable; Finish() renames it
/// to the final name only after the closing header is written and the file validates. Never overwrites an existing file. Not thread-safe: owned by one thread (see AudioWriteQueue).</summary>
public sealed class WavFileWriter : IDisposable
{
    public string FinalPath { get; private set; }
    public string PartialPath { get; private set; }
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int BlockAlign => Channels * 2;
    public long FramesAppended { get; private set; }
    /// <summary>Frames guaranteed on disk AND declared by the header (advanced by Flush()).</summary>
    public long FramesFlushed { get; private set; }
    public bool Failed { get; private set; }
    public string Error { get; private set; } = "";
    public bool IsOpen => _fs != null;
    /// <summary>Test seam (null in production): throw after this many data bytes have been appended, to simulate a full disk / I/O error.</summary>
    public long TestFailAfterBytes = -1;
    /// <summary>Test seam (null in production): make the final rename fail.</summary>
    public bool TestFailRename;

    FileStream _fs;

    public bool Open(string finalPath, int sampleRate, int channels)
    {
        try
        {
            FinalPath = finalPath; PartialPath = finalPath + ".partial"; SampleRate = sampleRate; Channels = channels;
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath));
            if (File.Exists(FinalPath) || File.Exists(PartialPath)) return Fail("output_exists_refusing_to_overwrite");
            _fs = new FileStream(PartialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            var h = WavFormat.BuildHeader(sampleRate, channels, 16, 0); _fs.Write(h, 0, h.Length); _fs.Flush(true);
            return true;
        }
        catch (Exception e) { return Fail(e.GetType().Name + ":" + e.Message); }
    }

    bool Fail(string why) { Failed = true; if (Error.Length == 0) Error = why; return false; }

    /// <summary>Appends whole frames (little-endian PCM16, interleaved). A count that is not a whole number of frames is refused.</summary>
    public bool Append(byte[] data, int count)
    {
        if (Failed || _fs == null) return false;
        try
        {
            if (count % BlockAlign != 0) return Fail("append_not_whole_frames");
            if (TestFailAfterBytes >= 0 && (FramesAppended * BlockAlign + count) > TestFailAfterBytes) throw new IOException("simulated write failure");
            _fs.Write(data, 0, count); FramesAppended += count / BlockAlign;
            return true;
        }
        catch (Exception e) { return Fail(e.GetType().Name + ":" + e.Message); }
    }

    /// <summary>Makes everything appended so far durable and refreshes the header, so the .partial file is a valid WAV of exactly FramesFlushed frames.</summary>
    public bool Flush()
    {
        if (Failed || _fs == null) return false;
        try
        {
            _fs.Flush(true);
            long end = _fs.Position;
            var h = WavFormat.BuildHeader(SampleRate, Channels, 16, FramesAppended * BlockAlign);
            _fs.Seek(0, SeekOrigin.Begin); _fs.Write(h, 0, h.Length); _fs.Flush(true); _fs.Seek(end, SeekOrigin.Begin);
            FramesFlushed = FramesAppended;
            return true;
        }
        catch (Exception e) { return Fail(e.GetType().Name + ":" + e.Message); }
    }

    /// <summary>Normal completion: final header, close, validate, atomic rename. Returns the validation of the FINAL file (valid=false on any failure; the partial file is then kept).</summary>
    public WavValidation Finish()
    {
        var bad = new WavValidation { problem = Error.Length > 0 ? Error : "not_open" };
        if (_fs == null) return bad;
        if (Failed) { Dispose(); return bad; }
        if (!Flush()) { Dispose(); return new WavValidation { problem = Error }; }
        Dispose();
        var vp = WavFormat.Validate(PartialPath);
        if (!vp.valid) { Error = "partial_failed_validation:" + vp.problem; Failed = true; return vp; }
        try
        {
            if (TestFailRename) throw new IOException("simulated rename failure");
            if (File.Exists(FinalPath)) { Failed = true; Error = "final_exists_at_rename"; return new WavValidation { problem = Error }; }
            File.Move(PartialPath, FinalPath);
        }
        catch (Exception e) { Failed = true; Error = "rename_failed:" + e.GetType().Name; return new WavValidation { problem = Error }; }
        var vf = WavFormat.Validate(FinalPath);
        if (!vf.valid) { Failed = true; Error = "final_failed_validation:" + vf.problem; }
        return vf;
    }

    public void Dispose() { if (_fs != null) { try { _fs.Dispose(); } catch (Exception) { } _fs = null; } }
}

/// <summary>Moves PCM writing off the main thread: a bounded queue drained by one writer thread that also flushes on a timer. Synchronous mode (no thread) is available for deterministic tests.</summary>
public sealed class AudioWriteQueue
{
    readonly WavFileWriter _w;
    readonly System.Threading.Thread _thread;
    readonly Queue<byte[]> _q = new Queue<byte[]>();
    readonly Queue<int> _n = new Queue<int>();
    readonly object _lock = new object();
    readonly Stack<byte[]> _pool = new Stack<byte[]>();
    readonly int _chunkBytes, _capacity;
    bool _completing, _done;
    readonly double _flushSeconds;
    public long DroppedByBacklog { get; private set; }
    public bool Threaded => _thread != null;

    public AudioWriteQueue(WavFileWriter w, bool threaded, int chunkBytes, int capacityChunks = 512, double flushSeconds = 1.0)
    {
        _w = w; _chunkBytes = chunkBytes; _capacity = capacityChunks; _flushSeconds = flushSeconds;
        if (threaded) { _thread = new System.Threading.Thread(Run) { IsBackground = true, Name = "Part3AudioWriter" }; _thread.Start(); }
    }

    /// <summary>A reusable buffer of exactly the chunk size (no allocation per frame in steady state).</summary>
    public byte[] Rent() { lock (_lock) { return _pool.Count > 0 ? _pool.Pop() : new byte[_chunkBytes]; } }

    /// <summary>Hands a filled buffer over. When the queue is full (writer far behind) the chunk is dropped and counted, never blocked on.</summary>
    public bool Enqueue(byte[] buf, int count)
    {
        if (_thread == null) { bool ok = _w.Append(buf, count); Return(buf); return ok; }
        lock (_lock)
        {
            if (_completing || _q.Count >= _capacity) { DroppedByBacklog += count / _w.BlockAlign; _pool.Push(buf); return false; }
            _q.Enqueue(buf); _n.Enqueue(count); System.Threading.Monitor.Pulse(_lock);
        }
        return true;
    }

    void Return(byte[] b) { lock (_lock) { _pool.Push(b); } }

    void Run()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); double lastFlush = 0;
        while (true)
        {
            byte[] b = null; int n = 0; bool finishNow = false;
            lock (_lock)
            {
                if (_q.Count == 0 && !_completing) System.Threading.Monitor.Wait(_lock, 200);
                if (_q.Count > 0) { b = _q.Dequeue(); n = _n.Dequeue(); }
                else if (_completing) finishNow = true;
            }
            if (b != null) { _w.Append(b, n); Return(b); }
            double now = sw.Elapsed.TotalSeconds;
            if (now - lastFlush >= _flushSeconds) { _w.Flush(); lastFlush = now; }
            if (finishNow) break;
        }
        lock (_lock) { _done = true; System.Threading.Monitor.PulseAll(_lock); }
    }

    /// <summary>Drains everything queued, then stops the thread. Returns false if the writer thread did not finish within the timeout.</summary>
    public bool Complete(int timeoutMs = 5000)
    {
        if (_thread == null) return true;
        lock (_lock) { _completing = true; System.Threading.Monitor.PulseAll(_lock); }
        return _thread.Join(timeoutMs);
    }

    public int Pending { get { lock (_lock) { return _q.Count; } } }
}

// ---------------------------------------------------------------------------------------------------------------------------------- PCM

public static class PcmConvert
{
    /// <summary>Interleaved float samples [-1,1] to little-endian PCM16. Returns the number of clipped samples (|x| &gt;= 1). NaN becomes 0. Nothing else is applied (no gain, no filtering).</summary>
    public static int FloatToPcm16(float[] src, int srcOffsetSamples, byte[] dst, int dstOffsetBytes, int sampleCount)
    {
        int clipped = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            float x = src[srcOffsetSamples + i];
            int v;
            if (float.IsNaN(x)) v = 0;
            else if (x >= 1f) { v = 32767; clipped++; }
            else if (x <= -1f) { v = -32768; clipped++; }
            else v = (int)Math.Round(x * 32767f);
            int o = dstOffsetBytes + i * 2; dst[o] = (byte)(v & 0xFF); dst[o + 1] = (byte)((v >> 8) & 0xFF);
        }
        return clipped;
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------- microphone ring cursor

/// <summary>
/// Turns Microphone.GetPosition() readings of a LOOPING ring into "how many new frames, where in the ring, how many were overwritten before we read them". The position wraps at the ring length
/// and cannot reveal complete laps, so the elapsed time (shared CaptureClock) is used to detect them. Frames are never fabricated: lost frames are only counted.
/// </summary>
public sealed class MicCursor
{
    public struct Drain
    {
        public int ringStart;        // first ring frame to read
        public int frames;           // frames to read (may wrap past the ring end; the reader splits)
        public long lostFrames;      // frames overwritten before they could be read (0 normally)
        public long absStart;        // captured-frame index (including lost frames) of the first frame to read
    }

    public readonly int RingFrames, SampleRate;
    int _lastPos; double _lastT; bool _begun;
    /// <summary>Frames the microphone has produced since the first sample, including frames that were lost.</summary>
    public long CapturedFrames { get; private set; }
    public long LostFrames { get; private set; }
    public int Overruns { get; private set; }

    public MicCursor(int ringFrames, int sampleRate) { RingFrames = ringFrames; SampleRate = sampleRate; }

    /// <summary>Anchors the cursor: ring position posAtStart was reached at tAbs.</summary>
    public void Begin(int posAtStart, double tAbs) { _lastPos = posAtStart; _lastT = tAbs; _begun = true; }

    public Drain Advance(int pos, double tAbs)
    {
        if (!_begun) Begin(pos, tAbs);
        int n = RingFrames;
        int delta = pos - _lastPos; if (delta < 0) delta += n;
        double expected = Math.Max(0.0, (tAbs - _lastT) * SampleRate);
        long total = delta;
        if (expected > delta + 0.5 * n)   // one or more complete laps happened that the position alone cannot show
        {
            long laps = (long)Math.Round((expected - delta) / n);
            if (laps > 0) total += laps * n;
        }
        var d = new Drain();
        long readable = Math.Min(total, (long)n);
        d.lostFrames = total - readable;
        d.frames = (int)readable;
        d.ringStart = d.lostFrames > 0 ? pos : _lastPos;   // after an overrun only the last full ring is intact: it starts at the write position
        d.absStart = CapturedFrames + d.lostFrames;
        CapturedFrames += total; LostFrames += d.lostFrames; if (d.lostFrames > 0) Overruns++;
        _lastPos = pos; _lastT = tAbs;
        return d;
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------- sample <-> time

/// <summary>Piecewise-affine map between the WAV frame index and t_abs. Each continuous SEGMENT has its own least-squares line through its (sample_index, t_abs) anchors.</summary>
public sealed class AudioClockMap
{
    public struct Anchor { public int id, segment; public long sampleIndex; public double tAbs; public int micPosition; public double dspTime; }

    public sealed class Segment
    {
        public int id; public long firstSample = -1, lastSample; public double nominalRate;
        public readonly List<Anchor> anchors = new List<Anchor>();
        // fit: t = t0 + (s - s0) * secondsPerSample
        public bool fitted; public double s0, t0, secondsPerSample;
        public double residualRmsSec;
        public double RateHz => secondsPerSample > 0 ? 1.0 / secondsPerSample : nominalRate;
        public double DriftPpm => nominalRate > 0 ? (RateHz / nominalRate - 1.0) * 1e6 : 0.0;
    }

    public readonly double NominalRate;
    public readonly List<Segment> Segments = new List<Segment>();
    int _nextAnchorId;
    public int AnchorCount { get; private set; }
    /// <summary>
    /// True after Freeze(): the map is final. No anchor or segment can be added any more, every segment holds the fit of ALL its anchors, and TimeOf / TimeOfIn are pure functions of
    /// the sample index. Index timestamps are written only from a frozen map, so two rows sharing a sample boundary can never see two different mappings.
    /// </summary>
    public bool Frozen { get; private set; }

    public AudioClockMap(double nominalRate) { NominalRate = nominalRate; }

    /// <summary>Stops anchor collection and fits every segment once from its complete anchor list (the same estimator the live map used). Idempotent.</summary>
    public void Freeze()
    {
        if (Frozen) return;
        for (int i = 0; i < Segments.Count; i++) Fit(Segments[i]);
        Frozen = true;
    }

    /// <summary>The segment with this id, or null.</summary>
    public Segment SegmentById(int id) => id >= 0 && id < Segments.Count ? Segments[id] : null;

    /// <summary>t_abs of the START of frame sampleIndex under the mapping of ONE named segment (NaN when that segment has no fit). Used for chunk boundaries, so the exclusive end of the last chunk of a
    /// segment is mapped by the same function as its own start - never by the next segment that happens to begin at that sample.</summary>
    public double TimeOfIn(int segmentId, long sampleIndex)
    {
        var s = SegmentById(segmentId);
        if (s == null || !s.fitted) return double.NaN;
        return s.t0 + (sampleIndex - s.s0) * s.secondsPerSample;
    }

    /// <summary>Starts a new continuous segment whose first frame has index firstSample (after an interruption / overrun). Returns the segment.</summary>
    public Segment BeginSegment(long firstSample)
    {
        if (Frozen) throw new InvalidOperationException("the audio clock map is frozen");
        var s = new Segment { id = Segments.Count, firstSample = firstSample, lastSample = firstSample, nominalRate = NominalRate };
        Segments.Add(s); return s;
    }

    public Segment Current => Segments.Count > 0 ? Segments[Segments.Count - 1] : null;

    /// <summary>Adds an anchor to the current segment: the frame BOUNDARY sampleIndex (the start of frame sampleIndex) was reached at tAbs.</summary>
    public Anchor AddAnchor(long sampleIndex, double tAbs, int micPosition, double dspTime)
    {
        if (Frozen) throw new InvalidOperationException("the audio clock map is frozen");
        var seg = Current ?? BeginSegment(sampleIndex);
        var a = new Anchor { id = _nextAnchorId++, segment = seg.id, sampleIndex = sampleIndex, tAbs = tAbs, micPosition = micPosition, dspTime = dspTime };
        seg.anchors.Add(a); seg.lastSample = Math.Max(seg.lastSample, sampleIndex); AnchorCount++;
        Fit(seg); return a;
    }

    public void ExtendSegment(long lastSample) { if (Frozen) return; var s = Current; if (s != null) s.lastSample = Math.Max(s.lastSample, lastSample); }

    void Fit(Segment s)
    {
        int n = s.anchors.Count;
        if (n == 0) { s.fitted = false; return; }
        s.s0 = s.anchors[0].sampleIndex; s.t0 = s.anchors[0].tAbs;
        if (n < 2 || s.anchors[n - 1].sampleIndex == s.anchors[0].sampleIndex) { s.secondsPerSample = 1.0 / s.nominalRate; s.fitted = true; s.residualRmsSec = 0; return; }
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++) { double x = s.anchors[i].sampleIndex - s.s0, y = s.anchors[i].tAbs - s.t0; sx += x; sy += y; sxx += x * x; sxy += x * y; }
        double den = n * sxx - sx * sx;
        double slope = den > 0 ? (n * sxy - sx * sy) / den : 1.0 / s.nominalRate;
        double icpt = (sy - slope * sx) / n;
        // Fewer than ~8 anchors cannot separate drift from jitter: keep the nominal rate and only estimate the offset (documented in the manifest).
        if (n < 8) { slope = 1.0 / s.nominalRate; icpt = sy / n - slope * (sx / n); }
        s.secondsPerSample = slope; s.t0 = s.t0 + icpt; s.fitted = true;
        double ss = 0; for (int i = 0; i < n; i++) { double r = (s.t0 + (s.anchors[i].sampleIndex - s.s0) * slope) - s.anchors[i].tAbs; ss += r * r; }
        s.residualRmsSec = Math.Sqrt(ss / n);
    }

    Segment SegmentOf(long sample)
    {
        Segment best = null;
        for (int i = 0; i < Segments.Count; i++) { var s = Segments[i]; if (s.firstSample >= 0 && sample >= s.firstSample) best = s; else break; }
        return best ?? (Segments.Count > 0 ? Segments[0] : null);
    }

    /// <summary>t_abs of the START of frame sampleIndex (NaN when nothing is known).</summary>
    public double TimeOf(long sampleIndex)
    {
        var s = SegmentOf(sampleIndex);
        if (s == null || !s.fitted) return double.NaN;
        return s.t0 + (sampleIndex - s.s0) * s.secondsPerSample;
    }

    /// <summary>The frame index whose start is the last one not after tAbs. A time inside a gap between segments clamps to the end of the previous segment; before the first sample: -1.</summary>
    public long SampleAt(double tAbs)
    {
        long result = -1;
        for (int i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i]; if (!s.fitted || s.firstSample < 0) continue;
            double start = s.t0 + (s.firstSample - s.s0) * s.secondsPerSample;
            if (tAbs < start) break;
            long f = s.firstSample + (long)Math.Floor((tAbs - start) / s.secondsPerSample);
            result = Math.Min(f, s.lastSample);
        }
        return result;
    }

    /// <summary>First-sample time from the first position reading: the frame at ring position 0 was captured pos/rate seconds before the reading at tAbs.</summary>
    public static double FirstSampleTime(double tReading, int ringPosition, int sampleRate) => tReading - ringPosition / (double)sampleRate;
}
