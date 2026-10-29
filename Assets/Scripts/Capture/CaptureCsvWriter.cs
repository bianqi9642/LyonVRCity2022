// CaptureCsvWriter.cs
// Crash-safe lazy CSV sink used by DataCaptureManager for every stream it owns.
//  - LAZY: file + header written only on the first actual row (a stream with no rows leaves no file).
//  - open/write/close per row (or per batch) -> at most one row/tick at risk on abnormal termination.
//  - one-shot failure flag per stream: first I/O error disables the stream + logs ONE LogError.
//  - a reusable StringBuilder for comma-joining (no per-row string[] allocation); Join() quotes any field containing , " or a line break.
// All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public sealed class CaptureCsvWriter
{
    readonly string _path;
    readonly string _header;
    readonly StringBuilder _sb = new StringBuilder(1024);
    bool _headerWritten;

    /// <summary>True once this stream hit an unrecoverable I/O error and stopped writing.</summary>
    public bool Failed { get; private set; }

    public string Path => _path;

    /// <summary>Bytes (UTF-8, including line terminators and the header) successfully written by this writer. Used for data-volume budgeting.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>Size of the header line (written once, with the first row).</summary>
    public long HeaderBytes => Encoding.UTF8.GetByteCount(_header) + s_newlineBytes;

    static readonly int s_newlineBytes = Environment.NewLine.Length;

    public CaptureCsvWriter(string path, string header)
    {
        _path = path;
        _header = header;
    }

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
        if (Failed) return;
        if (!EnsureHeader()) return;
        try
        {
            using (var w = new StreamWriter(_path, append: true))
                w.WriteLine(line);
            BytesWritten += Encoding.UTF8.GetByteCount(line) + s_newlineBytes;
        }
        catch (Exception e) { Fail(e); }
    }

    public void WriteLines(IReadOnlyList<string> lines)
    {
        if (Failed || lines == null || lines.Count == 0) return;
        if (!EnsureHeader()) return;
        try
        {
            long bytes = 0;
            using (var w = new StreamWriter(_path, append: true))
                for (int i = 0; i < lines.Count; i++)
                {
                    w.WriteLine(lines[i]);
                    bytes += Encoding.UTF8.GetByteCount(lines[i]) + s_newlineBytes;
                }
            BytesWritten += bytes;
        }
        catch (Exception e) { Fail(e); }
    }

    bool EnsureHeader()
    {
        if (_headerWritten) return !Failed;
        try
        {
            string dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(_path))
            {
                using (var w = new StreamWriter(_path, append: false))
                    w.WriteLine(_header);
                BytesWritten += Encoding.UTF8.GetByteCount(_header) + s_newlineBytes;
            }
            _headerWritten = true;
        }
        catch (Exception e) { Fail(e); }
        return !Failed;
    }

    void Fail(Exception e)
    {
        if (Failed) return;
        Failed = true;
        Debug.LogError($"[DataCapture] CSV stream disabled after I/O error ({System.IO.Path.GetFileName(_path)}): {e.Message}");
    }
}