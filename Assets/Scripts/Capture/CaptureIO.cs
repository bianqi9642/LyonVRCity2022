// CaptureIO.cs
// The ONLY place where the capture layer touches the file system for its CSV streams and its manifest. Every physical step is tagged with an operation name
// (open, header, append, flush, manifest_*), and a failure is always raised as CaptureIoException carrying that operation, so callers can record WHAT failed.
// FaultInjector is a deterministic failure-injection seam for tests (null in production): it is asked before every step and may return an exception to throw.
// Durability note: CSV appends are flushed to the OS (FileStream.Flush) but not fsync'ed, to keep per-tick cost low; the manifest is flushed to the device
// (Flush(true)). Neither is a guarantee against power loss. All comments in English per project convention.

using System;
using System.IO;
using System.Text;

public sealed class CaptureIoException : IOException
{
    public string Operation { get; }
    public CaptureIoException(string operation, Exception inner) : base(inner != null ? inner.Message : operation, inner) { Operation = operation; }
}

public static class CaptureIO
{
    /// <summary>Test seam ONLY: (path, operation) -> exception to throw at that step, or null to proceed. Null in production.</summary>
    public static Func<string, string, Exception> FaultInjector;

    static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    static void Fault(string path, string op)
    {
        Exception e = FaultInjector?.Invoke(path, op);
        if (e != null) throw e;
    }

    /// <summary>Runs one tagged step; any exception leaves as CaptureIoException(op).</summary>
    public static void Step(string path, string op, Action body)
    {
        try { Fault(path, op); body(); }
        catch (CaptureIoException) { throw; }
        catch (Exception e) { throw new CaptureIoException(op, e); }
    }

    public static bool FileExists(string path) => File.Exists(path);

    /// <summary>True when the file exists and has at least one byte (a header write that failed after creating the file leaves an empty file).</summary>
    public static bool FileHasContent(string path) { try { return File.Exists(path) && new FileInfo(path).Length > 0; } catch { return false; } }

    public static void CreateDirectory(string dir) => Step(dir, "open", () => Directory.CreateDirectory(dir));

    /// <summary>Creates (or truncates) a file and writes the text: steps open, [writeOp], flush.</summary>
    public static void WriteNewFile(string path, string text, string writeOp, bool flushToDisk)
    {
        FileStream fs = null;
        try
        {
            Step(path, "open", () => fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
            byte[] bytes = Utf8NoBom.GetBytes(text);
            Step(path, writeOp, () => fs.Write(bytes, 0, bytes.Length));
            Step(path, "flush", () => fs.Flush(flushToDisk));
        }
        finally { fs?.Dispose(); }
    }

    /// <summary>Appends text: steps open, append, flush. An empty text is a pure writability probe.</summary>
    public static void AppendText(string path, string text, string writeOp = "append")
    {
        FileStream fs = null;
        try
        {
            Step(path, "open", () => fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
            byte[] bytes = Utf8NoBom.GetBytes(text);
            Step(path, writeOp, () => fs.Write(bytes, 0, bytes.Length));
            Step(path, "flush", () => fs.Flush(false));
        }
        finally { fs?.Dispose(); }
    }

    /// <summary>True when the file is non-empty and its last byte is a line feed (or the file does not exist / is empty).</summary>
    public static bool EndsWithNewlineOrEmpty(string path)
    {
        if (!File.Exists(path)) return true;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (fs.Length == 0) return true;
            fs.Seek(-1, SeekOrigin.End);
            return fs.ReadByte() == '\n';
        }
    }
}
