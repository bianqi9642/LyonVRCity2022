// CsvRowBuilder.cs
// Allocation-light CSV row assembly for the high-volume npc_state.csv stream (one row per active NPC per snapshot).
//
// WHY: the previous path built each row as a List<string> of ~44 freshly allocated field strings (every float went through float.ToString("F4")), comma-joined them into another string, kept all rows of a
// snapshot in a List<string>, then CaptureCsvWriter appended them all into a StringBuilder, turned that into one multi-megabyte string and encoded that into one multi-megabyte byte[] (two large-object-heap
// allocations per snapshot). At 5 000 NPCs that is ~10^5 small strings and ~5 MB of large allocations per snapshot.
//
// WHAT: the SAME text is appended straight into one reusable StringBuilder. Equivalence rules (verified by tests against the previous implementation, byte for byte):
//   * a numeric field is formatted by the SAME runtime formatter as before (float.TryFormat(..., "F4", InvariantCulture) is the span form of float.ToString("F4", InvariantCulture)); only the temporary string is gone;
//   * a text field follows CaptureCsvWriter.Join exactly: null/empty -> nothing, a field containing , " CR or LF is quoted with doubled quotes, anything else verbatim;
//   * fields are separated by ',' and a row ends with Environment.NewLine, exactly like CaptureCsvWriter.WriteLine/WriteLines.
// No field, column or value is added, removed or reordered. All comments in English per project convention.

using System;
using System.Globalization;
using System.Text;

public sealed class CsvRowBuilder
{
    static readonly char[] s_special = { ',', '"', '\n', '\r' };

    readonly StringBuilder _sb;
    bool _first = true;

    /// <summary>Rows completed since the last Clear().</summary>
    public int Rows { get; private set; }

    /// <summary>The accumulated rows (each terminated by Environment.NewLine). Owned by the builder; valid until Clear().</summary>
    public StringBuilder Text => _sb;

    public CsvRowBuilder(int initialCapacity = 1 << 16) { _sb = new StringBuilder(initialCapacity); }

    public void Clear() { _sb.Length = 0; Rows = 0; _first = true; }

    void Sep() { if (_first) _first = false; else _sb.Append(','); }

    /// <summary>A text field, quoted only when it needs to be (same rule as CaptureCsvWriter.Join).</summary>
    public void Field(string v)
    {
        Sep();
        if (string.IsNullOrEmpty(v)) return;
        if (v.IndexOfAny(s_special) < 0) _sb.Append(v);
        else _sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
    }

    /// <summary>A field the caller has already escaped (a value shared by every row of a snapshot is escaped once, not per row).</summary>
    public void Verbatim(string escaped)
    {
        Sep();
        if (!string.IsNullOrEmpty(escaped)) _sb.Append(escaped);
    }

    /// <summary>The escaped form of a text value, as Field would write it (for Verbatim).</summary>
    public static string Escape(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.IndexOfAny(s_special) < 0 ? v : "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>float.ToString("F4", InvariantCulture), appended without the temporary string (and, when the runtime is verified to format the way TryFormatF4 emulates, without the slow native formatter).</summary>
    public void F4(float v)
    {
        Sep();
        Span<char> buf = stackalloc char[48];
        int n;
        if (s_fastF4 && TryFormatF4(v, buf, out n)) _sb.Append(buf.Slice(0, n));
        else if (v.TryFormat(buf, out n, "F4", CultureInfo.InvariantCulture)) _sb.Append(buf.Slice(0, n));
        else _sb.Append(v.ToString("F4", CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------
    // Fast "F4" for the Unity Mono runtime. Measured on Unity 2022.3.53f1 (Mono): float.ToString("F4") costs ~0.47 us (it goes through a native snprintf) and ~25 floats are written per NPC row.
    // That runtime prints a float as its SEVEN SIGNIFICANT DECIMAL DIGITS (not its exact binary value) and then applies the 4-fraction-digit format to that digit string:
    //   2173.457275f -> "2173.4570", 12345.6789f -> "12345.6800", 0.03125f -> "0.0313" (a second, half-up rounding on the digit string), a negative that prints as zero has no sign ("-0.00001f" -> "0.0000").
    // TryFormatF4 reproduces exactly that with integer arithmetic (the product |v| * 10^k is exact in double for the supported range). It is never trusted blindly:
    //   * it only handles 1e-4 <= |v| < 1e7 and |v| == 0; everything else (tiny, huge, NaN, infinity) returns false and the caller uses the real formatter;
    //   * s_fastF4 is decided ONCE per process by SelfCheck(): the emulation must equal float.ToString("F4") on a fixed pseudo-random battery that includes exact rounding ties, negatives and
    //     zero. On any runtime that formats differently (e.g. IL2CPP / a newer .NET) the battery fails, the fast path is switched off and the stock formatter is used, so the output can never differ.
    // The equivalence is also covered by NpcPerformanceTests (600 000 values compared with the real formatter).
    // ---------------------------------------------------------------------------------------------------------------------------------------------------------
    static readonly double[] s_p10 = { 1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12 };
    static readonly long[] s_l10 = { 1L, 10L, 100L, 1000L, 10000L, 100000L, 1000000L, 10000000L, 100000000L, 1000000000L };
    static readonly bool s_fastF4 = SelfCheck();

    /// <summary>True when the fast F4 path is active in this process (it equalled the stock formatter on the start-up battery).</summary>
    public static bool FastF4Active => s_fastF4;

    public static bool TryFormatF4(float v, Span<char> dst, out int n)
    {
        n = 0;
        float a = Math.Abs(v);
        if (a == 0f) { WriteF4(dst, 0, false, out n); return true; }
        if (!(a >= 1e-4f && a < 1e7f)) return false;
        double da = a;
        int e;
        if (da >= 1e6) e = 6; else if (da >= 1e5) e = 5; else if (da >= 1e4) e = 4; else if (da >= 1e3) e = 3; else if (da >= 1e2) e = 2; else if (da >= 10) e = 1; else if (da >= 1) e = 0;
        else if (da >= 1e-1) e = -1; else if (da >= 1e-2) e = -2; else if (da >= 1e-3) e = -3; else e = -4;
        double scaled = da * s_p10[6 - e];              // 24-bit mantissa x 5^k (k <= 10) < 2^53: exact
        double fl = Math.Floor(scaled);
        double frac = scaled - fl;
        long m = (long)fl;
        if (frac >= 0.5) m++;                            // 7 significant digits, ties up
        if (m >= 10000000L) { m /= 10; e++; }
        int f7 = 6 - e;                                  // fraction digits present in the 7-digit string
        long q;
        if (f7 <= 4) q = m * s_l10[4 - f7];
        else { long div = s_l10[f7 - 4]; q = (m + div / 2) / div; }   // second rounding, half-up on the digit string
        WriteF4(dst, q, v < 0f && q != 0, out n);
        return true;
    }

    static void WriteF4(Span<char> dst, long q, bool neg, out int n)
    {
        long ip = q / 10000, fp = q % 10000;
        int pos = 0;
        if (neg) dst[pos++] = '-';
        if (ip == 0) dst[pos++] = '0';
        else
        {
            int len = 0; long t = ip; while (t > 0) { len++; t /= 10; }
            int end = pos + len; t = ip;
            for (int i = end - 1; i >= pos; i--) { dst[i] = (char)('0' + (int)(t % 10)); t /= 10; }
            pos = end;
        }
        dst[pos++] = '.';
        dst[pos++] = (char)('0' + (int)(fp / 1000));
        dst[pos++] = (char)('0' + (int)(fp / 100 % 10));
        dst[pos++] = (char)('0' + (int)(fp / 10 % 10));
        dst[pos++] = (char)('0' + (int)(fp % 10));
        n = pos;
    }

    static bool SelfCheck()
    {
        try
        {
            var r = new System.Random(20261108);
            Span<char> buf = stackalloc char[48];
            for (int k = 0; k < 40000; k++)
            {
                float v;
                switch (k % 8)
                {
                    case 0: v = (float)((r.NextDouble() - 0.5) * 10000.0); break;                       // positions
                    case 1: v = (float)((r.NextDouble() - 0.5) * 720.0); break;                         // yaw
                    case 2: v = r.Next(-40000, 40000) / 32f; break;                                     // exact binary fractions (rounding ties)
                    case 3: v = (float)(r.NextDouble() * 5.0); break;                                   // speeds
                    case 4: v = (float)(Math.Round((r.NextDouble() - 0.5) * 2000.0, 4)); break;         // values that already have 4 decimals
                    case 5: v = r.Next(0, 3) == 0 ? -0f : (float)((r.NextDouble() - 0.5) * 1e-3); break; // zero / tiny, either sign
                    case 6:
                        {   // exact 8-significant-digit values ending in 5: the rounding tie of the 7-digit step
                            int j = r.Next(1, 7); long odd = 2L * r.Next(0, 1 << (j - 1)) + 1;
                            double frac = odd / (double)(1L << j), lo = Math.Pow(10, 7 - j), hi = Math.Pow(10, 8 - j);
                            double x = Math.Floor(lo + r.NextDouble() * (hi - lo)) + frac;
                            v = (float)x; if ((double)v != x) v = (float)((r.NextDouble() - 0.5) * 100.0);
                            if (r.Next(0, 2) == 0) v = -v;
                            break;
                        }
                    default: v = BitConverter.Int32BitsToSingle(r.Next(0, int.MaxValue)); if (float.IsNaN(v)) v = 1f; if (r.Next(0, 2) == 0) v = -v; break;   // arbitrary floats (mostly out of the fast range)
                }
                if (TryFormatF4(v, buf, out int n))
                {
                    string expect = v.ToString("F4", CultureInfo.InvariantCulture);
                    if (!MemoryExtensions.SequenceEqual<char>(buf.Slice(0, n), expect.AsSpan())) return false;
                }
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>int.ToString(InvariantCulture), appended without the temporary string.</summary>
    public void Int(int v)
    {
        Sep();
        Span<char> buf = stackalloc char[16];
        if (v.TryFormat(buf, out int n, default, CultureInfo.InvariantCulture)) _sb.Append(buf.Slice(0, n));
        else _sb.Append(v.ToString(CultureInfo.InvariantCulture));
    }

    public void EndRow() { _sb.Append(Environment.NewLine); _first = true; Rows++; }
}
