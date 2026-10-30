// SessionIdentity.cs
// Participant-identity validation and session-identifier generation for the capture layer (F-14). Pure C#, no Unity dependencies except the file-name helpers.
//
// Participant ID: the study's own identifier, kept EXACTLY (only surrounding whitespace is trimmed) and never rewritten by a sanitiser. Any printable text is a
// valid ID unless it is empty, a known placeholder, too long, or contains characters that are unsafe in file names / paths or are control characters.
// There is deliberately no required prefix or digit count: "P012", "12", "A-07" are all valid. The single, central placeholder list below is what the project
// itself uses as defaults ("0" in the scene, "P000" in ExternalSessionController, "UNKNOWN" / "UNKNOWN_SUBJECT" in the recorders' fallbacks) plus obvious
// stand-ins; any all-zero ID (0, 00, P000, p0000 ...) is a placeholder too.
//
// Session ID: independent of the participant. "<UTC yyyyMMddTHHmmssZ>_<first 12 hex of the run GUID>". The run GUID (32 hex) is generated once per recording
// session and stored in the manifest; the session_id (= output folder name) is derived from it, so two sessions - even in the same second, even for the same
// participant, even for participant strings that would sanitise to the same text - can never share a folder name.
// All comments in English per project convention.

using System;
using System.IO;
using System.Text.RegularExpressions;

public static class SessionIdentity
{
    public enum Status { Valid, Missing, Invalid, Placeholder, UnresolvedConfig }

    public struct Result
    {
        public Status status;
        public string participantId;   // trimmed, exact; only meaningful when status == Valid
        public string message;         // actionable text; never contains other participants' IDs
        public bool IsValid => status == Status.Valid;
    }

    public const int MaxParticipantIdLength = 64;

    /// <summary>The ONE placeholder list (case-insensitive). Extend here, nowhere else.</summary>
    public static readonly string[] PlaceholderIds =
    {
        "0", "P000", "UNKNOWN", "UNKNOWN_SUBJECT", "DEFAULT", "NONE", "NULL", "TBD", "PARTICIPANT", "SUBJECT"
    };

    static readonly Regex AllZeros = new Regex("^[Pp]?0+$", RegexOptions.Compiled);
    static readonly char[] UnsafeChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    public static bool IsPlaceholder(string trimmed)
    {
        if (string.IsNullOrEmpty(trimmed)) return false;
        if (AllZeros.IsMatch(trimmed)) return true;
        foreach (var p in PlaceholderIds) if (string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static Result ValidateParticipantId(string raw)
    {
        var r = new Result();
        if (raw == null || raw.Trim().Length == 0)
        {
            r.status = Status.Missing; r.message = "Participant ID is empty. Set a real participant ID (Inspector 'participantId' or the external session_config.json) before recording."; return r;
        }
        string t = raw.Trim();
        if (IsPlaceholder(t))
        {
            r.status = Status.Placeholder; r.message = "Participant ID '" + t + "' is a placeholder/default value and cannot be used for a research recording. Set the participant's real ID."; return r;
        }
        if (t.Length > MaxParticipantIdLength)
        {
            r.status = Status.Invalid; r.message = "Participant ID is longer than " + MaxParticipantIdLength + " characters."; return r;
        }
        foreach (char c in t)
        {
            if (char.IsControl(c) || Array.IndexOf(UnsafeChars, c) >= 0 || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
            {
                r.status = Status.Invalid; r.message = "Participant ID contains a control character or one of / \\ : * ? \" < > | (not allowed: it is used in file names)."; return r;
            }
        }
        r.status = Status.Valid; r.participantId = t; r.message = ""; return r;
    }

    /// <summary>Session id / folder name from a UTC time and a 32-hex run GUID.</summary>
    public static string BuildSessionId(DateTime utc, string runGuidN)
        => utc.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "_" + (runGuidN ?? "").Substring(0, 12);
}
