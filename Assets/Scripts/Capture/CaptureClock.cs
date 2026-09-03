// CaptureClock.cs
// The single authoritative timebase for the whole data-capture layer (Architecture C, Revision 2).
// Every research stream and the session manifest MUST take timestamps from here and nowhere else.
//   t_abs     = Time.realtimeSinceStartupAsDouble   (monotonic, timeScale-independent; the merge key)
//   t_unix_ms = DateTimeOffset.UtcNow ms            (wall clock; video / cross-device alignment only)
// Do NOT introduce Time.time / Time.unscaledTime / per-recorder offsets as a primary timebase.
// All comments in English per project convention.

using System;
using System.Globalization;
using UnityEngine;

public static class CaptureClock
{
    /// <summary>Primary synchronisation clock. Monotonic seconds since process start, timeScale-independent.</summary>
    public static double TAbs => Time.realtimeSinceStartupAsDouble;

    /// <summary>Secondary wall-clock timestamp (Unix epoch milliseconds, UTC). For video / cross-device sync only.</summary>
    public static long TUnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string F4(float v)  => v.ToString("F4", CultureInfo.InvariantCulture);
    public static string F4(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
}