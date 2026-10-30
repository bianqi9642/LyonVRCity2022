// ManifestFileWriter.cs
// Crash-resilient persistence of session_manifest.json (F-19). A reader of the final path must never see a half-written file, and a failed update must
// never destroy the last good manifest.
//
// WriteAtomic(finalPath, json):
//   1. write the complete JSON (UTF-8, no BOM) to a unique temp file in the SAME directory, flush to the device (FileStream.Flush(true)), close;
//   2. read it back and validate it (strict JSON-object scan + manifest_revision present) - an invalid temp file is deleted and nothing else is touched;
//   3. if a final file exists: preserve it - an INVALID one is renamed to *.corrupt-<n> (evidence), a VALID one is copied to *.bak (best effort);
//   4. install: final missing -> File.Move(tmp, final) (single rename); final present -> File.Replace(tmp, final, null) (single rename on POSIX, ReplaceFile on NTFS).
//      Only if the runtime reports the replace as unsupported (NotSupported / PlatformNotSupported) the documented fallback File.Copy(tmp, final, overwrite) is used:
//      that one is NOT atomic, so the validated temp file and the .bak are kept and Recover() can restore the newest complete generation.
//      The final file is never deleted before its replacement exists. Any other failure leaves the previous final untouched.
//   5. on success all stale *.tmp of this manifest are removed.
// Recover(finalPath): among final, .bak and *.tmp pick the VALID file with the highest manifest_revision (ties: final > bak > tmp), preserve an invalid final, install the
// winner. A newer valid generation is never overwritten by an older one.
// Durability: Flush(true) asks the OS to push the data to the device; rename/replace make the swap atomic w.r.t. process termination. Neither is a guarantee against
// sudden power loss or a lying storage controller; this class does not claim one. All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

public static class ManifestFileWriter
{
    public const string BackupSuffix = ".bak";

    public sealed class WriteResult
    {
        public bool Ok;
        public string Stage = "";            // failing step (manifest_tmp_create / _write / _flush / _validate / _backup / _replace) or "" on success
        public string Error = "";            // exception type + sanitised message
        public string InstallMode = "";      // move | replace | copy_fallback
        public bool BackupUpdated;
        public string PreservedCorrupt = ""; // file name of an invalid previous final that was set aside, if any
    }

    public sealed class RecoveryReport
    {
        public bool ValidFound, FinalWasValid, Installed;
        public string Source = "";           // final | backup | tmp:<file>
        public int Revision = -1;
        public string PreservedCorrupt = "";
    }

    static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
    static readonly Regex RevisionRe = new Regex("\"manifest_revision\"\\s*:\\s*(-?\\d+)", RegexOptions.Compiled);
    static int s_tmpCounter;

    public static string BackupPath(string finalPath) => finalPath + BackupSuffix;

    static string NewTmpPath(string finalPath) => finalPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + "." + (++s_tmpCounter) + ".tmp";

    static IEnumerable<string> TmpFiles(string finalPath)
    {
        string dir = Path.GetDirectoryName(finalPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
        foreach (var f in Directory.GetFiles(dir, Path.GetFileName(finalPath) + ".*.tmp")) yield return f;
    }

    // ---------------------------------------------------------------- validation

    /// <summary>Strict check that the text is ONE complete JSON object (balanced braces/brackets, terminated strings) carrying an integer manifest_revision.</summary>
    public static bool IsValidManifestJson(string json, out int revision)
    {
        revision = -1;
        if (string.IsNullOrWhiteSpace(json)) return false;
        string t = json.Trim();
        if (t[0] != '{' || t[t.Length - 1] != '}') return false;
        int depth = 0; bool inStr = false, esc = false;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                else if (c < 0x20) return false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') { depth--; if (depth < 0) return false; if (depth == 0 && i != t.Length - 1) return false; }
        }
        if (inStr || depth != 0) return false;
        var m = RevisionRe.Match(t);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out revision)) return false;
        return true;
    }

    static bool TryReadValid(string path, out string json, out int revision)
    {
        json = null; revision = -1;
        try { if (!File.Exists(path)) return false; json = File.ReadAllText(path, Encoding.UTF8); }
        catch { return false; }
        return IsValidManifestJson(json, out revision);
    }

    // ---------------------------------------------------------------- write

    public static WriteResult WriteAtomic(string finalPath, string json)
    {
        var r = new WriteResult();
        string tmp = NewTmpPath(finalPath);
        bool tmpWritten = false;
        try
        {
            // 1. temp file in the same directory
            FileStream fs = null;
            try
            {
                CaptureIO.Step(tmp, "manifest_tmp_create", () => fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None));
                byte[] bytes = Utf8NoBom.GetBytes(json);
                CaptureIO.Step(tmp, "manifest_tmp_write", () => fs.Write(bytes, 0, bytes.Length));
                CaptureIO.Step(tmp, "manifest_tmp_flush", () => fs.Flush(true));
                tmpWritten = true;
            }
            finally { fs?.Dispose(); }

            // 2. validate what is really on disk
            CaptureIO.Step(tmp, "manifest_validate", () =>
            {
                string back = File.ReadAllText(tmp, Encoding.UTF8);
                if (!IsValidManifestJson(back, out _)) throw new InvalidDataException("temporary manifest is not a complete JSON object");
            });

            // 3. preserve what is there
            bool finalExists = File.Exists(finalPath);
            if (finalExists)
            {
                if (TryReadValid(finalPath, out _, out _))
                {
                    try { CaptureIO.Step(finalPath, "manifest_backup", () => File.Copy(finalPath, BackupPath(finalPath), true)); r.BackupUpdated = true; }
                    catch { /* best effort: a missed backup must not block the update; the temp file stays until install succeeds */ }
                }
                else
                {
                    string corrupt = finalPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
                    CaptureIO.Step(finalPath, "manifest_backup", () => File.Move(finalPath, corrupt));
                    r.PreservedCorrupt = Path.GetFileName(corrupt);
                    finalExists = false;
                }
            }

            // 4. install
            if (!finalExists)
            {
                CaptureIO.Step(finalPath, "manifest_replace", () => File.Move(tmp, finalPath));
                r.InstallMode = "move";
            }
            else
            {
                try
                {
                    CaptureIO.Step(finalPath, "manifest_replace", () => File.Replace(tmp, finalPath, null));
                    r.InstallMode = "replace";
                }
                catch (CaptureIoException ce) when (ce.InnerException is NotSupportedException || ce.InnerException is PlatformNotSupportedException)
                {
                    CaptureIO.Step(finalPath, "manifest_replace_fallback", () => { File.Copy(tmp, finalPath, true); File.Delete(tmp); });
                    r.InstallMode = "copy_fallback";
                }
            }

            // 5. housekeeping
            foreach (var stale in TmpFiles(finalPath)) { try { File.Delete(stale); } catch { } }
            r.Ok = true;
        }
        catch (Exception e)
        {
            r.Ok = false;
            r.Stage = e is CaptureIoException ce ? ce.Operation : "manifest_write";
            Exception inner = e is CaptureIoException ? (e.InnerException ?? e) : e;
            r.Error = inner.GetType().Name + ": " + (inner.Message ?? "").Replace(",", " ").Replace("\n", " ").Replace("\r", " ");
            if (r.Error.Length > 160) r.Error = r.Error.Substring(0, 160);
            // An unvalidated temp file is garbage; a validated one is a complete newer generation and stays for Recover().
            if (!tmpWritten || r.Stage.StartsWith("manifest_tmp") || r.Stage == "manifest_validate") { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
        return r;
    }

    // ---------------------------------------------------------------- recovery

    public static RecoveryReport Recover(string finalPath)
    {
        var rep = new RecoveryReport();
        var cands = new List<(string source, string path, int rank, string json, int rev)>();
        if (TryReadValid(finalPath, out string fj, out int fr)) { cands.Add(("final", finalPath, 0, fj, fr)); rep.FinalWasValid = true; }
        if (TryReadValid(BackupPath(finalPath), out string bj, out int br)) cands.Add(("backup", BackupPath(finalPath), 1, bj, br));
        foreach (var t in TmpFiles(finalPath)) if (TryReadValid(t, out string tj, out int tr)) cands.Add(("tmp:" + Path.GetFileName(t), t, 2, tj, tr));
        if (cands.Count == 0) return rep;

        var best = cands[0];
        foreach (var c in cands) if (c.rev > best.rev || (c.rev == best.rev && c.rank < best.rank)) best = c;
        rep.ValidFound = true; rep.Source = best.source; rep.Revision = best.rev;
        if (best.source == "final") return rep;   // nothing newer: leave it exactly as it is

        // The final is missing, invalid, or OLDER than a complete generation: set an invalid one aside, install the winner atomically.
        if (File.Exists(finalPath) && !rep.FinalWasValid)
        {
            string corrupt = finalPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            try { File.Move(finalPath, corrupt); rep.PreservedCorrupt = Path.GetFileName(corrupt); } catch { }
        }
        var w = WriteAtomic(finalPath, best.json);
        rep.Installed = w.Ok;
        return rep;
    }
}
