// CaptureCsvWriter.cs
// Crash-safe lazy CSV sink used by DataCaptureManager for every stream it owns.
//  - LAZY: file + header written only on the first actual row (a stream with no rows leaves no file).
//  - open/write/close per row (or per batch) -> at most one row/tick at risk on abnormal termination.
//  - one-shot failure flag per stream: first I/O error disables the stream + logs ONE LogError.
//  - a reusable StringBuilder for comma-joining (no per-row string[] allocation).
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

    public CaptureCsvWriter(string path, string header)
    {
        _path = path;
        _header = header;
    }

    /// <summary>Comma-join already-formatted fields into one CSV line (no quoting; sanitise upstream).</summary>
    public string Join(IReadOnlyList<string> fields)
    {
        _sb.Length = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) _sb.Append(',');
            _sb.Append(fields[i] ?? string.Empty);
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
        }
        catch (Exception e) { Fail(e); }
    }

    public void WriteLines(IReadOnlyList<string> lines)
    {
        if (Failed || lines == null || lines.Count == 0) return;
        if (!EnsureHeader()) return;
        try
        {
            using (var w = new StreamWriter(_path, append: true))
                for (int i = 0; i < lines.Count; i++)
                    w.WriteLine(lines[i]);
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