// CaptureCsvWriter.cs
// Crash-safe lazy CSV sink used by DataCaptureManager for every stream it owns.
//  - LAZY: file + header written only on the first actual row (a stream with no rows leaves no file).
//  - open/write/close per row (or per batch) -> at most one row/tick at risk on abnormal termination.
//  - FAILURE POLICY (F-13): the first I/O error puts the stream in state Failed (never silently 'ok' again): the error is recorded (operation, type, message, time,
//    rows written before / dropped after), an unconditional Debug.LogError is issued once, and every later row is counted as dropped. After a cool-down the writer
//    tries a bounded number of recoveries (re-establish the file, terminate a possibly partial last line, probe-append); success => state Recovered with the
//    dropped-row boundary reported, MaxRecoveryAttempts failures => PermanentlyFailed. A failed batch is NEVER retried (a partial write could duplicate rows).
//    Each state change raises the Transition event (consumed by DataCaptureManager for capture_errors.csv and the manifest). A stream never affects another stream.
//  - OPT-IN RESILIENT MODE (WriterRetryPolicy, default OFF => everything above is exactly the legacy behaviour): a failed commit does NOT discard rows. The failed batch and every later row are kept in an
//    ORDERED, BOUNDED in-memory queue; the writer retries with a time-based exponential back-off (never a sleep: an attempt is one open/append piggy-backed on the next row or on Pump()/Close(), and a refused open
//    returns immediately), after a failed append the partial tail of its own batch is cut off (so a retried batch is written exactly once) and rows are flushed in their original order. When the outage outlasts the
//    policy's budget the stream is PermanentlyFailed and the rows it could not store are COUNTED as lost (never hidden). Only one resilient writer may own a file at a time.
//  - a reusable StringBuilder for comma-joining (no per-row string[] allocation); Join() quotes any field containing , " or a line break.
// All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public enum WriterState { Operational, Failed, Recovered }

/// <summary>What kind of I/O failure ended a commit; drives the retry budget of a resilient writer.</summary>
public enum WriterFailureKind { None, SharingViolation, TransientIo, PathOrDirectory, Permission, DiskFull, Disposed, DuplicateWriter, Unknown }

/// <summary>
/// Retry / buffering policy of a resilient CaptureCsvWriter. Memory model: a gaze_diagnostics row is ~0.6 KB at 20 Hz = ~12 KB/s, so the default 20 000 rows / 16 MB cover ~16 minutes of outage and never exceed 16 MB.
/// The default transient budget (10 minutes) is far above the longest observed lock (6.17 s) and below a typical session length; persistent-looking failures (permission, full disk, missing directory) get a short budget.
/// </summary>
public sealed class WriterRetryPolicy
{
    public double InitialBackoffSeconds = 0.5;
    public double BackoffMultiplier = 2.0;
    public double MaxBackoffSeconds = 5.0;
    /// <summary>Longest outage (seconds since its first failure) a sharing violation / transient I/O error is retried for.</summary>
    public double MaxRecoveryDurationSeconds = 600.0;
    /// <summary>Same limit for failures that look persistent (permission, disk full, directory, disposed handle).</summary>
    public double PersistentFailureBudgetSeconds = 60.0;
    public int MaxPendingRows = 20000;
    public long MaxPendingBytes = 16L * 1024 * 1024;
    /// <summary>Upper bound of rows written by one drain attempt (keeps the main-thread cost of catching up small).</summary>
    public int MaxRowsPerDrain = 1000;
    /// <summary>Failed attempts reported as transitions (capture_errors.csv / log): the first N, then every 10th.</summary>
    public int MaxReportedAttempts = 12;

    public double BackoffFor(int attempt)
    {
        double d = InitialBackoffSeconds * Math.Pow(Math.Max(1.0, BackoffMultiplier), Math.Max(0, attempt - 1));
        return Math.Min(d, MaxBackoffSeconds);
    }
    public double BudgetFor(WriterFailureKind k)
    {
        switch (k)
        {
            case WriterFailureKind.SharingViolation:
            case WriterFailureKind.TransientIo:
            case WriterFailureKind.Unknown: return MaxRecoveryDurationSeconds;
            default: return Math.Min(PersistentFailureBudgetSeconds, MaxRecoveryDurationSeconds);
        }
    }
    public string Describe() => $"backoff {InitialBackoffSeconds:0.##}s x{BackoffMultiplier:0.##} max {MaxBackoffSeconds:0.##}s; budget {MaxRecoveryDurationSeconds:0}s transient / {PersistentFailureBudgetSeconds:0}s persistent; buffer {MaxPendingRows} rows / {MaxPendingBytes / (1024 * 1024)} MB";
    /// <summary>Production default for the gaze diagnostics stream.</summary>
    public static WriterRetryPolicy Default() => new WriterRetryPolicy();
}

/// <summary>One meaningful state change of a stream writer (failure, failed recovery attempt, recovery, permanent failure).</summary>
public sealed class WriterTransition
{
    public string Stream, Operation, ErrorType, ErrorMessage, Status, Utc;
    public double TAbs;
    public long RowsWrittenBefore, RowsDropped;
    public int RecoveryAttempt;
    /// <summary>Resilient writers only: rows waiting in the retry queue and failure class at the moment of the transition (not part of capture_errors.csv).</summary>
    public long RowsPending;
    public string FailureKind = "";
}

public sealed class CaptureCsvWriter
{
    public const int MaxRecoveryAttempts = 3;
    public const double RecoveryCooldownSeconds = 2.0;

    readonly string _path;
    readonly string _header;
    readonly string _name;
    readonly bool _diagnostic;
    readonly StringBuilder _sb = new StringBuilder(1024);
    readonly StringBuilder _batch = new StringBuilder(2048);
    bool _headerWritten;

    /// <summary>Stream name used in diagnostics (the file name without extension unless given).</summary>
    public string Name => _name;
    /// <summary>A writer without a path writes nothing and counts nothing (recording blocked, e.g. invalid identity).</summary>
    public bool IsDisabled => _path == null;

    public WriterState State { get; private set; } = WriterState.Operational;
    /// <summary>True while the stream is NOT operational (state Failed). Rows offered in this state are counted as dropped.</summary>
    public bool Failed => State == WriterState.Failed;
    public bool PermanentlyFailed { get; private set; }
    public bool Recovered { get; private set; }
    /// <summary>True when a failed append/flush may have left an incomplete last line in the file.</summary>
    public bool PartialOutputPossible { get; private set; }
    public bool HeaderWritten => _headerWritten;

    public string Path => _path;

    // ---- counters (each incremented only at the point it becomes true) ----
    /// <summary>Rows offered to this writer (written + failed + dropped).</summary>
    public long RowsAttempted { get; private set; }
    /// <summary>Data rows (header excluded) successfully appended.</summary>
    public long RowsWritten { get; private set; }
    /// <summary>Rows of the batch whose append raised the error (their persistence is unknown/partial).</summary>
    public long RowsFailed { get; private set; }
    /// <summary>Rows offered while the stream was Failed (never attempted on disk).</summary>
    public long RowsDroppedAfterFailure { get; private set; }
    public long WriteErrors { get; private set; }
    public int RecoveryAttempts { get; private set; }
    /// <summary>t_abs of the first / most recent SUCCESSFUL row write.</summary>
    public double FirstRowT { get; private set; } = double.NaN;
    public double LastRowT { get; private set; } = double.NaN;
    public double FirstErrorT { get; private set; } = double.NaN;
    public double LastErrorT { get; private set; } = double.NaN;
    public string LastErrorOperation { get; private set; } = "";
    public string LastErrorType { get; private set; } = "";
    /// <summary>Short sanitised description of the last I/O error ("" if none): exception type + message, no paths.</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Raised on every state change. Not raised by diagnostic writers (they must never report themselves recursively).</summary>
    public event Action<WriterTransition> Transition;

    // ---- resilient mode (null policy = legacy behaviour) ----
    /// <summary>Retry / buffering policy; null (default) keeps the legacy behaviour (drop after failure, 3 recoveries).</summary>
    public WriterRetryPolicy Policy { get; private set; }
    public bool Resilient => Policy != null;
    /// <summary>Rows (and bytes) held in the ordered retry queue right now: accepted, not yet on disk, not lost.</summary>
    public long PendingRows { get; private set; }
    public long PendingBytes { get; private set; }
    public long MaxPendingRowsSeen { get; private set; }
    /// <summary>Rows that were written AFTER having waited in the retry queue (each exactly once, in order).</summary>
    public long RowsRecoveredFromBuffer { get; private set; }
    /// <summary>Rows refused because the bounded queue was full (a subset of RowsDroppedAfterFailure).</summary>
    public long RowsOverflowDropped { get; private set; }
    /// <summary>Rows still queued when Close() could not store them (a subset of RowsDroppedAfterFailure).</summary>
    public long RowsLostAtClose { get; private set; }
    /// <summary>Rows the stream could not store in total (dropped after failure + rows of a failed batch of unknown persistence).</summary>
    public long RowsLost => RowsDroppedAfterFailure + RowsFailed;
    public int OutageEpisodes { get; private set; }
    public double LongestOutageSeconds { get; private set; }
    /// <summary>t_abs of the most recent recovery (NaN if none) and of the most recent successful write.</summary>
    public double RecoveredT { get; private set; } = double.NaN;
    public double LastSuccessT => LastRowT;
    public WriterFailureKind LastErrorKind { get; private set; } = WriterFailureKind.None;
    /// <summary>Partial tails of this writer's own failed appends that were cut off before the batch was re-sent (each prevented a duplicated / malformed row).</summary>
    public int PartialTailsRepaired { get; private set; }
    public bool LengthMismatchSeen { get; private set; }

    sealed class PendingItem { public string Text; public int Rows; public long Bytes; }
    readonly List<PendingItem> _pending = new List<PendingItem>();
    int _pendingHead;
    double _nextRetryT, _outageStartT = double.NaN;
    int _episodeAttempts;
    bool _uncertainTail;           // a failed append/flush may have left bytes of the head batch on disk
    long _expectedLength = -1;     // bytes this writer knows to be in the file (header + committed rows); -1 = not yet known
    bool _overflowLogged, _claimed;

    /// <summary>Switches this writer to resilient mode. Only allowed before the first row is offered.</summary>
    public void EnableResilience(WriterRetryPolicy policy)
    {
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        if (RowsAttempted > 0) throw new InvalidOperationException("resilient mode must be enabled before the first row is offered");
        Policy = policy;
    }

    // single-owner guard: one OPEN resilient writer per file path (a second instance for the same path would interleave rows)
    static readonly Dictionary<string, CaptureCsvWriter> s_owners = new Dictionary<string, CaptureCsvWriter>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Test seam: forget every claim.</summary>
    public static void ResetOwnershipForTests() { s_owners.Clear(); }
    string OwnerKey() { try { return System.IO.Path.GetFullPath(_path); } catch { return _path; } }
    bool ClaimOwnership()
    {
        if (_claimed) return true;
        string k = OwnerKey();
        if (s_owners.TryGetValue(k, out CaptureCsvWriter other) && other != this && !other.IsClosed) return false;
        s_owners[k] = this; _claimed = true; return true;
    }
    void ReleaseOwnership()
    {
        if (!_claimed) return;
        string k = OwnerKey();
        if (s_owners.TryGetValue(k, out CaptureCsvWriter o) && o == this) s_owners.Remove(k);
        _claimed = false;
    }

    /// <summary>Bytes (UTF-8, including line terminators and the header) successfully written by this writer. Used for data-volume budgeting.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>Size of the header line (written once, with the first row).</summary>
    public long HeaderBytes => Encoding.UTF8.GetByteCount(_header ?? "") + s_newlineBytes;

    static readonly int s_newlineBytes = Environment.NewLine.Length;

    public CaptureCsvWriter(string path, string header, string streamName = null, bool diagnostic = false)
    {
        _path = path;
        _header = header;
        _diagnostic = diagnostic;
        _name = streamName ?? (path != null ? System.IO.Path.GetFileNameWithoutExtension(path) : "disabled");
    }

    /// <summary>A writer that accepts and discards everything without counting (used while recording is blocked).</summary>
    public static CaptureCsvWriter Disabled(string name) => new CaptureCsvWriter(null, "", name);

    static readonly char[] s_special = { ',', '"', '\n', '\r' };

    /// <summary>
    /// Comma-join already-formatted fields into one CSV line. Upstream sanitising (commas / line breaks become spaces) is unchanged; as a
    /// last line of defence any field that still contains a comma, double quote or line break is quoted RFC 4180 style (quotes doubled), so
    /// it can never add a column or merge two. Ordinary fields are written verbatim.
    /// </summary>
    public string Join(IReadOnlyList<string> fields)
    {
        _sb.Length = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) _sb.Append(',');
            string v = fields[i];
            if (string.IsNullOrEmpty(v)) continue;
            if (v.IndexOfAny(s_special) < 0) _sb.Append(v);
            else _sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
        }
        return _sb.ToString();
    }

    /// <summary>True after Close(): the stream belongs to a finished session and accepts no further record.</summary>
    public bool IsClosed { get; private set; }
    /// <summary>Records offered after Close() and refused (never counted as attempted / written / failed).</summary>
    public long RowsRejectedAfterClose { get; private set; }
    /// <summary>Closes the stream for good. This writer opens, appends and closes the file on every commit, so no handle or buffer is left behind; what Close() adds is the guarantee that
    /// nothing can be appended to a finished session's file afterwards, whatever component calls. Idempotent.</summary>
    public void Close()
    {
        if (IsClosed) return;
        if (Policy != null) FinalDrain();   // one last attempt for rows still waiting in the retry queue; anything it cannot store is counted as lost
        IsClosed = true;   // refuse further records whatever happens next: a failed close must never leave the file appendable
        ReleaseOwnership();
        try { CloseProbe?.Invoke(this); }
        catch (Exception e) { CloseError = e.GetType().Name + ": " + e.Message; }
    }
    /// <summary>Null after a successful Close(); the failure text when the close step itself failed (the stream still refuses records).</summary>
    public string CloseError { get; private set; }
    /// <summary>True only when Close() ran and completed without error.</summary>
    public bool ClosedCleanly => IsClosed && CloseError == null;
    /// <summary>Optional hook run inside Close() (a place to verify or flush a handle; also the seam deterministic tests use to simulate a failing close). Null in production.</summary>
    public Action<CaptureCsvWriter> CloseProbe;

    // Duration of the last / the worst commit (the synchronous open + append + close): the writer's own cost, used to tell I/O pressure from frame and scheduler delays.
    public double LastCommitSeconds { get; private set; }
    public double MaxCommitSeconds { get; private set; }
    public double SumCommitSeconds { get; private set; }
    public long Commits { get; private set; }

    public void WriteLine(string line)
    {
        if (IsDisabled) return;
        if (IsClosed) { RowsRejectedAfterClose++; return; }
        Commit(null, line, 1, null);
    }

    /// <summary>
    /// One commit for rows that were already assembled (comma-joined, each terminated by Environment.NewLine) in a StringBuilder, e.g. by CsvRowBuilder. Identical file content, counters, failure policy and
    /// fault-injection steps as WriteLines with the same rows; it only avoids building one big string and one big byte[] per commit.
    /// </summary>
    public void WriteRows(StringBuilder rows, int rowCount)
    {
        if (IsDisabled || rows == null || rowCount <= 0) return;
        if (IsClosed) { RowsRejectedAfterClose += rowCount; return; }
        Commit(null, null, rowCount, rows);
    }

    public void WriteLines(IReadOnlyList<string> lines)
    {
        if (IsDisabled || lines == null || lines.Count == 0) return;
        if (IsClosed) { RowsRejectedAfterClose += lines.Count; return; }
        Commit(lines, null, lines.Count, null);
    }

    void Commit(IReadOnlyList<string> lines, string single, int n, StringBuilder builder)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { CommitCore(lines, single, n, builder); }
        finally
        {
            double sec = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) / (double)System.Diagnostics.Stopwatch.Frequency;
            LastCommitSeconds = sec; SumCommitSeconds += sec; Commits++;
            if (sec > MaxCommitSeconds) MaxCommitSeconds = sec;
        }
    }

    void CommitCore(IReadOnlyList<string> lines, string single, int n, StringBuilder builder)
    {
        RowsAttempted += n;
        if (Policy != null) { CommitResilient(lines, single, n, builder); return; }
        if (State == WriterState.Failed && !TryRecover()) { RowsDroppedAfterFailure += n; return; }

        try
        {
            EnsureHeader();
#if NPC_PERF_DIAG
            long _pd0 = NpcPerfDiag.Now;
#endif
            long bytes = 0;
            if (builder != null)
            {
                bytes = CaptureIO.AppendBuilder(_path, builder);
#if NPC_PERF_DIAG
                if (_name != null && _name.StartsWith("npc_state")) { NpcPerfDiag.AppendTicks += NpcPerfDiag.Now - _pd0; NpcPerfDiag.Bytes += bytes; }
#endif
            }
            else
            {
                _batch.Length = 0;
                if (lines == null) { _batch.Append(single).Append(Environment.NewLine); bytes = Encoding.UTF8.GetByteCount(single) + s_newlineBytes; }
                else
                    for (int i = 0; i < lines.Count; i++)
                    {
                        _batch.Append(lines[i]).Append(Environment.NewLine);
                        bytes += Encoding.UTF8.GetByteCount(lines[i]) + s_newlineBytes;
                    }
    #if NPC_PERF_DIAG
                long _pd1 = NpcPerfDiag.Now;
    #endif
                CaptureIO.AppendText(_path, _batch.ToString());
    #if NPC_PERF_DIAG
                if (_name != null && _name.StartsWith("npc_state")) { NpcPerfDiag.BatchBuildTicks += _pd1 - _pd0; NpcPerfDiag.AppendTicks += NpcPerfDiag.Now - _pd1; NpcPerfDiag.Bytes += bytes; }
    #endif
            }
            BytesWritten += bytes;
            RowsWritten += n;
            double t = CaptureClock.TAbs;
            if (double.IsNaN(FirstRowT)) FirstRowT = t;
            LastRowT = t;
        }
        catch (Exception e)
        {
            string op = e is CaptureIoException ce ? ce.Operation : "append";
            RowsFailed += n;
            if (op == "append" || op == "flush") PartialOutputPossible = true;   // some bytes of this batch may be on disk
            OnFailure(e, op, "failed", 0);
        }
    }

    void EnsureHeader()
    {
        if (_headerWritten) return;
        string dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) CaptureIO.CreateDirectory(dir);

        if (!CaptureIO.FileHasContent(_path))
        {
            CaptureIO.WriteNewFile(_path, _header + Environment.NewLine, "header", false);
            BytesWritten += Encoding.UTF8.GetByteCount(_header) + s_newlineBytes;
            if (Policy != null) _expectedLength = Encoding.UTF8.GetByteCount(_header + Environment.NewLine);
        }
        else if (Policy != null && _expectedLength < 0) _expectedLength = CaptureIO.FileLength(_path);   // a pre-existing file: its length is the baseline
        _headerWritten = true;
    }

    // Bounded recovery: cool-down, then re-establish the file (header if the file is missing), terminate a possibly partial last line and probe-append.
    // Returns true when the stream is operational again and the current rows may be written.
    bool TryRecover()
    {
        if (PermanentlyFailed) return false;
        if (CaptureClock.TAbs - LastErrorT < RecoveryCooldownSeconds) return false;
        RecoveryAttempts++;
        try
        {
            if (_headerWritten && !CaptureIO.FileHasContent(_path)) _headerWritten = false;   // the file vanished: a new file needs its header
            EnsureHeader();
            if (!CaptureIO.EndsWithNewlineOrEmpty(_path)) CaptureIO.AppendText(_path, Environment.NewLine);   // closes a partial row; that row stays malformed, later rows are intact
            else CaptureIO.AppendText(_path, "");                                                                // probe: can we append at all?
        }
        catch (Exception e)
        {
            string op = e is CaptureIoException ce ? ce.Operation : "append";
            bool last = RecoveryAttempts >= MaxRecoveryAttempts;
            if (last) PermanentlyFailed = true;
            OnFailure(e, op, last ? "permanently_failed" : "recovery_failed", RecoveryAttempts);
            return false;
        }
        State = WriterState.Recovered;
        Recovered = true;
        Raise(new WriterTransition { Status = "recovered", Operation = "recover", ErrorType = "", ErrorMessage = "stream operational again; the dropped-row count is the gap", RecoveryAttempt = RecoveryAttempts });
        Debug.LogWarning($"[DataCapture] CSV stream '{_name}' recovered after {RecoveryAttempts} attempt(s); {RowsDroppedAfterFailure + RowsFailed} row(s) were lost in the gap.");
        return true;
    }

    void OnFailure(Exception e, string op, string status, int attempt, bool quiet = false)
    {
        WriteErrors++;
        State = WriterState.Failed;
        LastErrorKind = Classify(e);
        double t = CaptureClock.TAbs;
        if (double.IsNaN(FirstErrorT)) FirstErrorT = t;
        LastErrorT = t;
        Exception inner = e is CaptureIoException ? (e.InnerException ?? e) : e;
        LastErrorOperation = op;
        LastErrorType = inner.GetType().Name;
        LastError = LastErrorType + ": " + (inner.Message ?? "").Replace(",", " ").Replace("\n", " ").Replace("\r", " ");
        if (LastError.Length > 160) LastError = LastError.Substring(0, 160);

        if (quiet) return;   // a resilient writer's later failed attempts are counted but only the first few (then every 10th) are reported
        // Unconditional, device-visible, once per transition that matters (not per dropped row).
        if (status == "failed")
            Debug.LogError($"[DataCapture] CSV stream disabled after I/O error ({System.IO.Path.GetFileName(_path)}): {op}: {inner.Message}");
        else if (status == "permanently_failed")
            Debug.LogError($"[DataCapture] CSV stream '{_name}' permanently failed after {attempt} recovery attempt(s) ({op}): {inner.Message}");
        else
            Debug.LogWarning($"[DataCapture] CSV stream '{_name}' recovery attempt {attempt} failed ({op}): {inner.Message}");

        Raise(new WriterTransition { Status = status, Operation = op, ErrorType = LastErrorType, ErrorMessage = LastError, RecoveryAttempt = attempt });
    }

    // ======================================================================== resilient mode ========================================================================

    /// <summary>Classifies a failure (the CaptureIoException wrapper is looked through). Windows: HRESULT low word 32 / 33 = sharing / lock violation, 39 / 112 = disk full.</summary>
    public static WriterFailureKind Classify(Exception e)
    {
        Exception x = e is CaptureIoException ? (e.InnerException ?? e) : e;
        if (x == null) return WriterFailureKind.Unknown;
        if (x is UnauthorizedAccessException) return WriterFailureKind.Permission;
        if (x is DirectoryNotFoundException || x is DriveNotFoundException || x is PathTooLongException) return WriterFailureKind.PathOrDirectory;
        if (x is ObjectDisposedException) return WriterFailureKind.Disposed;
        if (x is IOException)
        {
            int hr = x.HResult & 0xFFFF;
            string m = (x.Message ?? "").ToLowerInvariant();
            if (hr == 32 || hr == 33 || m.Contains("sharing violation") || m.Contains("being used by another process") || m.Contains("lock violation")) return WriterFailureKind.SharingViolation;
            if (hr == 39 || hr == 112 || m.Contains("not enough space") || m.Contains("disk full") || m.Contains("no space left")) return WriterFailureKind.DiskFull;
            return WriterFailureKind.TransientIo;
        }
        return WriterFailureKind.Unknown;
    }

    string Materialise(IReadOnlyList<string> lines, string single, StringBuilder builder)
    {
        if (builder != null) return builder.ToString();   // rows already terminated by Environment.NewLine
        if (lines == null) return single + Environment.NewLine;
        _batch.Length = 0;
        for (int i = 0; i < lines.Count; i++) _batch.Append(lines[i]).Append(Environment.NewLine);
        return _batch.ToString();
    }

    void CommitResilient(IReadOnlyList<string> lines, string single, int n, StringBuilder builder)
    {
        if (!ClaimOwnership())
        {
            // a second writer for the same file would interleave rows: refuse, count, say so once
            LastErrorKind = WriterFailureKind.DuplicateWriter; State = WriterState.Failed; RowsDroppedAfterFailure += n;
            if (WriteErrors == 0)
            {
                WriteErrors++; FirstErrorT = LastErrorT = CaptureClock.TAbs; LastErrorOperation = "claim"; LastErrorType = "DuplicateWriter"; LastError = "DuplicateWriter: another open writer owns this file";
                Debug.LogError($"[DataCapture] CSV stream '{_name}' refused: another open writer already owns {System.IO.Path.GetFileName(_path)}; its rows are dropped and counted.");
                Raise(new WriterTransition { Status = "failed", Operation = "claim", ErrorType = LastErrorType, ErrorMessage = LastError, RecoveryAttempt = 0 });
            }
            return;
        }
        if (PermanentlyFailed) { RowsDroppedAfterFailure += n; return; }
        string text = Materialise(lines, single, builder);
        long bytes = Encoding.UTF8.GetByteCount(text);

        if (PendingRows == 0 && State != WriterState.Failed)
        {
            // healthy fast path: one direct append, exactly like the legacy writer
            if (TryDirect(text, bytes, n, out Exception err, out string op)) return;
            Enqueue(text, bytes, n, true);               // the failed batch is KEPT (it is the head of the queue)
            _uncertainTail = op == "append" || op == "flush";
            if (_uncertainTail) PartialOutputPossible = true;
            BeginOutage(err, op);
            return;
        }
        Enqueue(text, bytes, n, false);                  // order is preserved: new rows queue behind the pending ones
        if (State == WriterState.Failed && CaptureClock.TAbs < _nextRetryT) return;   // not due yet: no I/O at all on this call
        TryDrain(false);
    }

    bool TryDirect(string text, long bytes, int n, out Exception err, out string op)
    {
        err = null; op = null;
        try
        {
            EnsureHeader();
            CaptureIO.AppendText(_path, text);
        }
        catch (Exception e) { err = e; op = e is CaptureIoException ce ? ce.Operation : "append"; return false; }
        CountWritten(bytes, n);
        return true;
    }

    void CountWritten(long bytes, int rows)
    {
        BytesWritten += bytes; RowsWritten += rows;
        if (_expectedLength >= 0) _expectedLength += bytes;
        double t = CaptureClock.TAbs;
        if (double.IsNaN(FirstRowT)) FirstRowT = t;
        LastRowT = t;
    }

    bool Enqueue(string text, long bytes, int n, bool force)
    {
        if (!force && (PendingRows + n > Policy.MaxPendingRows || PendingBytes + bytes > Policy.MaxPendingBytes))
        {
            RowsDroppedAfterFailure += n; RowsOverflowDropped += n;   // newest rows are refused: the queue keeps an unbroken, ordered prefix
            if (!_overflowLogged) { _overflowLogged = true; Debug.LogError($"[DataCapture] CSV stream '{_name}' retry queue is full ({PendingRows} rows): further rows are dropped and counted (rows_overflow_dropped)."); }
            return false;
        }
        _pending.Add(new PendingItem { Text = text, Rows = n, Bytes = bytes });
        PendingRows += n; PendingBytes += bytes;
        if (PendingRows > MaxPendingRowsSeen) MaxPendingRowsSeen = PendingRows;
        return true;
    }

    // First failure of an outage (from the fast path): State=Failed, one reported transition, first retry scheduled.
    void BeginOutage(Exception e, string op)
    {
        double now = CaptureClock.TAbs;
        _outageStartT = now; _episodeAttempts = 0; OutageEpisodes++;
        _nextRetryT = now + Policy.BackoffFor(1);
        OnFailure(e, op, "failed", 0);
    }

    // One bounded recovery attempt: reconcile a partial tail, ensure the header, then write queued rows in order (at most MaxRowsPerDrain per call; `final` ignores the cap).
    void TryDrain(bool final)
    {
        double now = CaptureClock.TAbs;
        if (State == WriterState.Failed) { _episodeAttempts++; RecoveryAttempts++; }   // continuing to drain a backlog after a recovery is not a recovery attempt
        string op = "append";
        try
        {
            if (_uncertainTail) { op = "open"; ReconcileTail(); }
            if (_headerWritten && !CaptureIO.FileHasContent(_path)) { _headerWritten = false; _expectedLength = -1; }   // the file vanished: a new file needs its header
            op = "header"; EnsureHeader();
            int writtenThisCall = 0;
            while (_pendingHead < _pending.Count && (final || writtenThisCall < Policy.MaxRowsPerDrain))
            {
                var sb = _drainBuilder; sb.Length = 0;
                int take = 0, rows = 0; long bytes = 0;
                for (int i = _pendingHead; i < _pending.Count; i++)
                {
                    var it = _pending[i];
                    if (rows > 0 && !final && rows + it.Rows > Policy.MaxRowsPerDrain) break;
                    sb.Append(it.Text); rows += it.Rows; bytes += it.Bytes; take++;
                }
                try { CaptureIO.AppendText(_path, sb.ToString()); }
                catch (Exception e) { op = e is CaptureIoException ce ? ce.Operation : "append"; _uncertainTail = op == "append" || op == "flush"; if (_uncertainTail) PartialOutputPossible = true; throw; }
                CountWritten(bytes, rows); RowsRecoveredFromBuffer += rows;
                for (int i = 0; i < take; i++) _pending[_pendingHead + i] = null;
                _pendingHead += take; PendingRows -= rows; PendingBytes -= bytes; writtenThisCall += rows;
                if (State == WriterState.Failed) MarkRecovered(now);   // the first successful write of an outage: the stream is operational again (a remaining backlog drains on the next rows)
            }
            if (_pendingHead >= _pending.Count) { _pending.Clear(); _pendingHead = 0; }
            else if (_pendingHead > 4096) { _pending.RemoveRange(0, _pendingHead); _pendingHead = 0; }
            if (State == WriterState.Failed) MarkRecovered(now);   // nothing was queued (probe only)
        }
        catch (Exception e)
        {
            op = e is CaptureIoException ce2 ? ce2.Operation : op;
            OnAttemptFailed(e, op, now);
        }
    }

    readonly StringBuilder _drainBuilder = new StringBuilder(64 * 1024);
    bool _closing;

    void MarkRecovered(double now)
    {
        State = WriterState.Recovered; Recovered = true; RecoveredT = now;
        double outage = double.IsNaN(_outageStartT) ? 0 : now - _outageStartT; if (outage > LongestOutageSeconds) LongestOutageSeconds = outage;
        _nextRetryT = 0; _outageStartT = double.NaN;
        int attempts = _episodeAttempts; _episodeAttempts = 0;
        Raise(new WriterTransition { Status = "recovered", Operation = "recover", ErrorType = "", RecoveryAttempt = attempts,
            ErrorMessage = $"stream operational again after {outage:0.0}s; queued rows are written in order; lost so far {RowsLost}, still queued {PendingRows}" });
        Debug.LogWarning($"[DataCapture] CSV stream '{_name}' recovered after {attempts} attempt(s) and {outage:0.0}s; {RowsRecoveredFromBuffer} row(s) written from the retry queue, {RowsLost} row(s) lost.");
    }

    void OnAttemptFailed(Exception e, string op, double now)
    {
        if (State != WriterState.Failed)
        {
            // a failure while a backlog was still draining after a recovery: a NEW outage (the rows not yet written stay queued, in order)
            _outageStartT = now; _episodeAttempts = 0; OutageEpisodes++; _nextRetryT = now + Policy.BackoffFor(1);
            OnFailure(e, op, "failed", 0);
            return;
        }
        WriterFailureKind kind = Classify(e);
        double elapsed = double.IsNaN(_outageStartT) ? 0 : now - _outageStartT;
        bool exhausted = elapsed >= Policy.BudgetFor(kind);
        bool report = exhausted || _episodeAttempts <= Policy.MaxReportedAttempts || _episodeAttempts % 10 == 0;
        if (exhausted)
        {
            PermanentlyFailed = true;
            long lost = PendingRows; DiscardPending(); RowsDroppedAfterFailure += lost;
            OnFailure(e, op, "permanently_failed", _episodeAttempts);
            return;
        }
        _nextRetryT = now + Policy.BackoffFor(_episodeAttempts + 1);
        OnFailure(e, op, "recovery_failed", _episodeAttempts, !report);
    }

    void DiscardPending() { _pending.Clear(); _pendingHead = 0; PendingRows = 0; PendingBytes = 0; }

    // The failed append may have left a partial tail of ITS OWN batch in the file; cut it off so the whole batch can be re-sent exactly once.
    void ReconcileTail()
    {
        long actual = CaptureIO.FileLength(_path);
        if (actual < 0) { _headerWritten = false; _expectedLength = -1; _uncertainTail = false; PartialOutputPossible = false; return; }   // the file is gone: nothing partial remains
        if (_expectedLength >= 0 && actual > _expectedLength) { CaptureIO.TruncateTo(_path, _expectedLength); PartialTailsRepaired++; }
        else if (_expectedLength >= 0 && actual < _expectedLength) { LengthMismatchSeen = true; _expectedLength = actual; }   // truncated by someone else: reported, cannot be repaired
        else if (_expectedLength < 0) { LengthMismatchSeen = true; _expectedLength = actual; }
        _uncertainTail = false; PartialOutputPossible = false;
    }

    /// <summary>Gives a waiting retry a chance without offering a row (called from focus / resume / lifecycle points). Never sleeps; at most one open/append attempt, and only when the back-off is due.</summary>
    public void Pump()
    {
        if (Policy == null || IsClosed || IsDisabled || PermanentlyFailed) return;
        if (PendingRows == 0 && State != WriterState.Failed) return;
        if (State == WriterState.Failed && CaptureClock.TAbs < _nextRetryT) return;
        TryDrain(false);
    }

    // Close(): one last attempt, ignoring the back-off. Rows that still cannot be stored are lost and COUNTED (RowsLostAtClose); the stream then reports a failed final state.
    void FinalDrain()
    {
        if (PermanentlyFailed || IsDisabled) return;
        if (PendingRows == 0 && State != WriterState.Failed) return;
        _closing = true;   // no transition events while the session is being finalised (the manifest carries the final state)
        TryDrain(true);
        if (PendingRows > 0 || State == WriterState.Failed)
        {
            long lost = PendingRows; DiscardPending();
            RowsDroppedAfterFailure += lost; RowsLostAtClose = lost; PermanentlyFailed = true; State = WriterState.Failed;
            Debug.LogError($"[DataCapture] CSV stream '{_name}' closed with {lost} row(s) that could not be written (they are counted as lost).");
        }
    }

    void Raise(WriterTransition t)
    {
        t.Stream = _name; t.TAbs = CaptureClock.TAbs; t.Utc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        t.RowsWrittenBefore = RowsWritten; t.RowsDropped = RowsFailed + RowsDroppedAfterFailure;
        t.RowsPending = PendingRows; t.FailureKind = LastErrorKind.ToString();
        if (_diagnostic || _closing) return;
        try { Transition?.Invoke(t); }
        catch (Exception ex) { Debug.LogError("[DataCapture] transition observer threw for stream '" + _name + "': " + ex.Message); }
    }
}
