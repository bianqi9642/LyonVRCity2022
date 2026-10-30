// CaptureCsvWriter.cs
// Crash-safe lazy CSV sink used by DataCaptureManager for every stream it owns.
//  - LAZY: file + header written only on the first actual row (a stream with no rows leaves no file).
//  - open/write/close per row (or per batch) -> at most one row/tick at risk on abnormal termination.
//  - FAILURE POLICY (F-13): the first I/O error puts the stream in state Failed (never silently 'ok' again): the error is recorded (operation, type, message, time,
//    rows written before / dropped after), an unconditional Debug.LogError is issued once, and every later row is counted as dropped. After a cool-down the writer
//    tries a bounded number of recoveries (re-establish the file, terminate a possibly partial last line, probe-append); success => state Recovered with the
//    dropped-row boundary reported, MaxRecoveryAttempts failures => PermanentlyFailed. A failed batch is NEVER retried (a partial write could duplicate rows).
//    Each state change raises the Transition event (consumed by DataCaptureManager for capture_errors.csv and the manifest). A stream never affects another stream.
//  - a reusable StringBuilder for comma-joining (no per-row string[] allocation); Join() quotes any field containing , " or a line break.
// All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public enum WriterState { Operational, Failed, Recovered }

/// <summary>One meaningful state change of a stream writer (failure, failed recovery attempt, recovery, permanent failure).</summary>
public sealed class WriterTransition
{
    public string Stream, Operation, ErrorType, ErrorMessage, Status, Utc;
    public double TAbs;
    public long RowsWrittenBefore, RowsDropped;
    public int RecoveryAttempt;
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

    public void WriteLine(string line)
    {
        if (IsDisabled) return;
        Commit(null, line, 1);
    }

    public void WriteLines(IReadOnlyList<string> lines)
    {
        if (IsDisabled || lines == null || lines.Count == 0) return;
        Commit(lines, null, lines.Count);
    }

    void Commit(IReadOnlyList<string> lines, string single, int n)
    {
        RowsAttempted += n;
        if (State == WriterState.Failed && !TryRecover()) { RowsDroppedAfterFailure += n; return; }

        try
        {
            EnsureHeader();
            long bytes = 0;
            _batch.Length = 0;
            if (lines == null) { _batch.Append(single).Append(Environment.NewLine); bytes = Encoding.UTF8.GetByteCount(single) + s_newlineBytes; }
            else
                for (int i = 0; i < lines.Count; i++)
                {
                    _batch.Append(lines[i]).Append(Environment.NewLine);
                    bytes += Encoding.UTF8.GetByteCount(lines[i]) + s_newlineBytes;
                }
            CaptureIO.AppendText(_path, _batch.ToString());
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
        }
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

    void OnFailure(Exception e, string op, string status, int attempt)
    {
        WriteErrors++;
        State = WriterState.Failed;
        double t = CaptureClock.TAbs;
        if (double.IsNaN(FirstErrorT)) FirstErrorT = t;
        LastErrorT = t;
        Exception inner = e is CaptureIoException ? (e.InnerException ?? e) : e;
        LastErrorOperation = op;
        LastErrorType = inner.GetType().Name;
        LastError = LastErrorType + ": " + (inner.Message ?? "").Replace(",", " ").Replace("\n", " ").Replace("\r", " ");
        if (LastError.Length > 160) LastError = LastError.Substring(0, 160);

        // Unconditional, device-visible, once per transition that matters (not per dropped row).
        if (status == "failed")
            Debug.LogError($"[DataCapture] CSV stream disabled after I/O error ({System.IO.Path.GetFileName(_path)}): {op}: {inner.Message}");
        else if (status == "permanently_failed")
            Debug.LogError($"[DataCapture] CSV stream '{_name}' permanently failed after {attempt} recovery attempt(s) ({op}): {inner.Message}");
        else
            Debug.LogWarning($"[DataCapture] CSV stream '{_name}' recovery attempt {attempt} failed ({op}): {inner.Message}");

        Raise(new WriterTransition { Status = status, Operation = op, ErrorType = LastErrorType, ErrorMessage = LastError, RecoveryAttempt = attempt });
    }

    void Raise(WriterTransition t)
    {
        t.Stream = _name; t.TAbs = CaptureClock.TAbs; t.Utc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        t.RowsWrittenBefore = RowsWritten; t.RowsDropped = RowsFailed + RowsDroppedAfterFailure;
        if (_diagnostic) return;
        try { Transition?.Invoke(t); }
        catch (Exception ex) { Debug.LogError("[DataCapture] transition observer threw for stream '" + _name + "': " + ex.Message); }
    }
}
