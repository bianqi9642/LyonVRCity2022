// NpcPerformanceTests.cs
// Equivalence tests for the NPC-capture performance work (CsvRowBuilder, CaptureCsvWriter.WriteRows / CaptureIO.AppendBuilder, NpcLineOfSight.Query near/far split, cached head bone).
// Each optimisation must produce EXACTLY what the former implementation produced; these tests compare the new code against the former behaviour (re-stated here as the oracle) on randomised input.
// The production types live in Assembly-CSharp, so they are reached by reflection like the other tests of this assembly.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public class NpcPerformanceTests
{
    static readonly Type RowType = T("CsvRowBuilder");
    static readonly Type WriterType = T("CaptureCsvWriter");
    static readonly Type LosType = T("NpcLineOfSight");

    string _dir;
    readonly List<UnityEngine.Object> _objs = new List<UnityEngine.Object>();

    [SetUp] public void SetUp() { _dir = Path.Combine(Path.GetTempPath(), "NpcPerformanceTests_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); T("CaptureIO").GetField("FaultInjector").SetValue(null, null); }

    [TearDown]
    public void TearDown()
    {
        T("CaptureIO").GetField("FaultInjector").SetValue(null, null);
        foreach (var o in _objs) if (o != null) UnityEngine.Object.DestroyImmediate(o);
        _objs.Clear();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---------------------------------------------------------------- CsvRowBuilder == List<string> + CaptureCsvWriter.Join

    static float RandomFloat(System.Random r, int kind)
    {
        switch (kind)
        {
            case 0: return (float)((r.NextDouble() - 0.5) * 10000.0);             // positions
            case 1: return (float)((r.NextDouble() - 0.5) * 2e-4);                // tiny values around zero (rounds to -0.0000 / 0.0000)
            case 2: return (r.Next(-4000, 4000)) / 32f;                           // exact binary fractions (rounding ties at the 4th decimal)
            case 3: return (float)((r.NextDouble() - 0.5) * 720.0);               // yaw-like
            case 4: return r.Next(0, 2) == 0 ? 0f : -0f;                          // zero and negative zero
            case 5: return (float)((r.NextDouble() - 0.5) * 2e9);                 // very large
            case 6: return r.Next(0, 4) == 0 ? float.NaN : (r.Next(0, 2) == 0 ? float.PositiveInfinity : float.NegativeInfinity);
            default: return (float)(r.NextDouble() * 5.0);                        // speeds
        }
    }

    static readonly string[] TextPool = { "", null, "walking", "Dwell", "EG18", "a,b", "he said \"hi\"", "line\nbreak", "cr\rhere", "café", "NPC-004835-S3", "x\"", "," };

    [Test]
    public void CsvRowBuilder_ProducesExactlyTheFormerListJoinOutput_ForRandomFieldsIncludingEdgeCases()
    {
        var rnd = new System.Random(20261108);
        object writer = CallStatic(WriterType, "Disabled", "oracle");   // any writer instance: Join does not touch the file
        object rb = Activator.CreateInstance(RowType, 16);               // tiny initial capacity: the StringBuilder is forced into many chunks
        var expected = new StringBuilder();
        var fields = new List<string>();
        int rows = 4000;
        for (int row = 0; row < rows; row++)
        {
            fields.Clear();
            for (int k = 0; k < 12; k++)
            {
                int kind = rnd.Next(0, 4);
                if (kind == 0) { float v = RandomFloat(rnd, rnd.Next(0, 8)); fields.Add(v.ToString("F4", CultureInfo.InvariantCulture)); Call(rb, "F4", v); }
                else if (kind == 1) { int v = rnd.Next(-100000, 100000); fields.Add(v.ToString(CultureInfo.InvariantCulture)); Call(rb, "Int", v); }
                else if (kind == 2) { string v = TextPool[rnd.Next(TextPool.Length)]; fields.Add(v); Call(rb, "Field", v); }
                else { string v = TextPool[rnd.Next(TextPool.Length)]; fields.Add(v); string esc = (string)CallStatic(RowType, "Escape", v); Call(rb, "Verbatim", esc); }
            }
            expected.Append((string)Call(writer, "Join", fields)).Append(Environment.NewLine);
            Call(rb, "EndRow");
        }
        Assert.AreEqual(rows, Get<int>(rb, "Rows"));
        Assert.AreEqual(expected.ToString(), Get<StringBuilder>(rb, "Text").ToString(), "the row builder must write exactly what List<string> + Join wrote");
    }


    [Test]
    public void CsvRowBuilder_F4_EqualsTheRuntimeFormatter_On600000Values_AndSelfCheckEnablesTheFastPath()
    {
        var rnd = new System.Random(99);
        object rb = Activator.CreateInstance(RowType, 1 << 16);
        var expected = new StringBuilder();
        int checkedFast = 0;
        for (int k = 0; k < 600000; k++)
        {
            float v;
            switch (k % 10)
            {
                case 0: v = (float)((rnd.NextDouble() - 0.5) * 10000.0); break;
                case 1: v = (float)((rnd.NextDouble() - 0.5) * 2e-3); break;
                case 2: v = rnd.Next(-40000, 40000) / 32f; break;
                case 3: v = rnd.Next(-400000, 400000) / 128f; break;
                case 4: v = (float)((rnd.NextDouble() - 0.5) * 720.0); break;
                case 5: v = (float)(rnd.NextDouble() * 5.0); break;
                case 6: v = BitConverter.Int32BitsToSingle(rnd.Next(0, int.MaxValue)) * (rnd.Next(0, 2) == 0 ? 1f : -1f); if (float.IsNaN(v)) v = 0f; break;
                case 7: v = rnd.Next(0, 3) == 0 ? -0f : (rnd.Next(0, 2) == 0 ? 0f : (float)((rnd.NextDouble() - 0.5) * 1e-3)); break;
                case 8: v = (float)Math.Round((rnd.NextDouble() - 0.5) * 2000.0, 4); break;
                default:
                    {   // exact 8-significant-digit ties
                        int j = rnd.Next(1, 7); long odd = 2L * rnd.Next(0, 1 << (j - 1)) + 1; double frac = odd / (double)(1L << j);
                        double lo = Math.Pow(10, 7 - j), hi = Math.Pow(10, 8 - j); double x = Math.Floor(lo + rnd.NextDouble() * (hi - lo)) + frac;
                        v = (float)x; if ((double)v != x) v = (float)((rnd.NextDouble() - 0.5) * 100.0); if (rnd.Next(0, 2) == 0) v = -v; break;
                    }
            }
            Call(rb, "F4", v); Call(rb, "EndRow");
            expected.Append(v.ToString("F4", CultureInfo.InvariantCulture)).Append(Environment.NewLine);
            checkedFast++;
        }
        Assert.AreEqual(expected.ToString(), Get<StringBuilder>(rb, "Text").ToString(), "every F4 field must equal float.ToString(\"F4\", InvariantCulture)");
        TestContext.Out.WriteLine("F4 compared: " + checkedFast + " values; fast path active in this runtime: " + GetStatic<bool>(RowType, "FastF4Active"));
        Assert.IsTrue(GetStatic<bool>(RowType, "FastF4Active"), "on the Unity 2022.3 Mono runtime the start-up self-check must accept the emulation (if it does not, the stock formatter is used and the output is still identical)");
    }

    [Test]
    public void CsvRowBuilder_Clear_StartsAFreshSnapshot()
    {
        object rb = Activator.CreateInstance(RowType, 16);
        Call(rb, "Field", "a"); Call(rb, "F4", 1.5f); Call(rb, "EndRow");
        Call(rb, "Clear");
        Assert.AreEqual(0, Get<int>(rb, "Rows")); Assert.AreEqual(0, Get<StringBuilder>(rb, "Text").Length);
        Call(rb, "Int", 7); Call(rb, "EndRow");
        Assert.AreEqual("7" + Environment.NewLine, Get<StringBuilder>(rb, "Text").ToString(), "no leading comma after Clear");
    }

    // ---------------------------------------------------------------- WriteRows == WriteLines (bytes, counters, failure policy)

    object NewWriter(string name) => Activator.CreateInstance(WriterType, Path.Combine(_dir, name + ".csv"), "h1,h2,h3", name, false);

    [Test]
    public void WriteRows_WritesTheSameBytesAndCounters_AsWriteLines_IncludingNonAsciiAcrossCopyBlocks()
    {
        var lines = new List<string>();
        // row 0: character index 16383 of the builder is the HIGH surrogate of an astral character and index 16384 its LOW surrogate, i.e. the pair straddles the 16 K-char copy block of CaptureIO.AppendBuilder
        lines.Add(new string('x', 16383) + "\U0001F600,caf\u00e9");
        for (int i = 1; i < 300; i++) lines.Add(i + "," + new string('y', i % 50) + ",\u00e9\U0001F600");
        lines.Add("1.0000,\"a,b\",z");

        object w1 = NewWriter("lines"); Call(w1, "WriteLines", lines);
        object rb = Activator.CreateInstance(RowType, 16);
        foreach (string l in lines) { Call(rb, "Verbatim", l); Call(rb, "EndRow"); }   // each whole line as one verbatim field: identical text, no extra quoting
        object w2 = NewWriter("rows"); Call(w2, "WriteRows", Get<StringBuilder>(rb, "Text"), Get<int>(rb, "Rows"));

        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(_dir, "lines.csv")), File.ReadAllBytes(Path.Combine(_dir, "rows.csv")), "file bytes (header + rows) must be identical");
        Assert.AreEqual(Get<long>(w1, "RowsWritten"), Get<long>(w2, "RowsWritten"));
        Assert.AreEqual(Get<long>(w1, "BytesWritten"), Get<long>(w2, "BytesWritten"), "BytesWritten (used by the npc_state data budget) must be the real byte count in both paths");
        Assert.AreEqual(new FileInfo(Path.Combine(_dir, "rows.csv")).Length, Get<long>(w2, "BytesWritten") + 0, "BytesWritten = header + rows = file length");
        Assert.AreEqual(Get<long>(w1, "RowsAttempted"), Get<long>(w2, "RowsAttempted"));
    }

    [Test]
    public void WriteRows_FailurePolicy_IsTheSameAsWriteLines_AndAFailedBatchIsNeverRetried()
    {
        var lines = new List<string> { "a,1", "b,2", "c,3" };
        object rb = Activator.CreateInstance(RowType, 16);
        foreach (string l in lines) { Call(rb, "Verbatim", l); Call(rb, "EndRow"); }

        object w1 = NewWriter("fail_lines"), w2 = NewWriter("fail_rows");
        LogAssert.ignoreFailingMessages = true;   // the writer logs one error when a stream fails (unconditional by design)
        T("CaptureIO").GetField("FaultInjector").SetValue(null, new Func<string, string, Exception>((path, op) => op == "append" && path.Contains("fail_") ? new IOException("injected") : null));
        Call(w1, "WriteLines", lines);
        Call(w2, "WriteRows", Get<StringBuilder>(rb, "Text"), Get<int>(rb, "Rows"));
        T("CaptureIO").GetField("FaultInjector").SetValue(null, null);
        LogAssert.ignoreFailingMessages = false;

        Assert.AreEqual(Get<object>(w1, "State").ToString(), Get<object>(w2, "State").ToString());
        Assert.AreEqual(Get<long>(w1, "RowsFailed"), Get<long>(w2, "RowsFailed"));
        Assert.AreEqual(Get<long>(w1, "WriteErrors"), Get<long>(w2, "WriteErrors"));
        Assert.AreEqual(Get<long>(w1, "RowsWritten"), Get<long>(w2, "RowsWritten"));
        Assert.AreEqual(Get<string>(w1, "LastErrorOperation"), Get<string>(w2, "LastErrorOperation"));
    }

    [Test]
    public void WriteRows_AfterClose_AndWithNoRows_BehavesLikeWriteLines()
    {
        object rb = Activator.CreateInstance(RowType, 16);
        object w = NewWriter("closed");
        Call(w, "WriteRows", Get<StringBuilder>(rb, "Text"), 0);   // nothing to write: no file, no counters
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "closed.csv")), "an empty batch must not create a file (lazy writer)");
        Call(rb, "Verbatim", "q"); Call(rb, "EndRow");
        Call(w, "Close");
        Call(w, "WriteRows", Get<StringBuilder>(rb, "Text"), 1);
        Assert.AreEqual(1L, Get<long>(w, "RowsRejectedAfterClose"));
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "closed.csv")));
    }

    // ---------------------------------------------------------------- NpcLineOfSight.Query == QueryExhaustive

    Transform MakeNpc(Vector3 pos, string name)
    {
        var root = new GameObject(name); _objs.Add(root); root.transform.position = pos;
        for (int i = 0; i < 6; i++)   // limb-like capsules around the body, some above and some beside the eye point
        {
            var c = new GameObject("limb" + i); c.transform.SetParent(root.transform, false);
            c.transform.localPosition = new Vector3((i % 3 - 1) * 0.25f, 0.4f + i * 0.28f, (i % 2) * 0.12f);
            var cc = c.AddComponent<CapsuleCollider>(); cc.radius = 0.1f; cc.height = 0.5f;
        }
        return root.transform;
    }

    static int Code(object status) => (int)status;

    [UnityTest]
    public IEnumerator Los_NearFarSplit_ReturnsTheReferenceStatus_OnRandomScenes()
    {
        var rnd = new System.Random(42);
        var city = new GameObject("city"); _objs.Add(city);
        for (int i = 0; i < 80; i++)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube); b.transform.SetParent(city.transform, false);
            b.transform.position = new Vector3((float)(rnd.NextDouble() * 120 - 60), (float)(rnd.NextDouble() * 6), (float)(rnd.NextDouble() * 120 - 60));
            b.transform.localScale = new Vector3(0.1f + (float)rnd.NextDouble() * 6f, 0.5f + (float)rnd.NextDouble() * 8f, 0.1f + (float)rnd.NextDouble() * 6f);
        }
        // thin walls placed right around the 3 m split of typical queries
        for (int i = 0; i < 12; i++)
        {
            var w = GameObject.CreatePrimitive(PrimitiveType.Cube); w.transform.SetParent(city.transform, false);
            w.transform.position = new Vector3((float)(rnd.NextDouble() * 10 - 5), 1.5f, (float)(rnd.NextDouble() * 10 - 5)); w.transform.localScale = new Vector3(2f, 3f, 0.02f);
        }
        var npcs = new List<Transform>();
        for (int i = 0; i < 10; i++) npcs.Add(MakeNpc(new Vector3((float)(rnd.NextDouble() * 20 - 10), 0f, (float)(rnd.NextDouble() * 20 - 10)), "npc" + i));
        var participant = new GameObject("participant"); _objs.Add(participant);
        participant.transform.position = new Vector3(0f, 0f, 0f);
        var head = new GameObject("head"); head.transform.SetParent(participant.transform, false); head.transform.localPosition = new Vector3(0f, 1.7f, 0f);
        var pcol = participant.AddComponent<CapsuleCollider>(); pcol.center = new Vector3(0f, 0.9f, 0f); pcol.height = 1.8f; pcol.radius = 0.25f;
        yield return null;
        Physics.SyncTransforms();

        int blocked = 0, clear = 0, other = 0, total = 0;
        for (int q = 0; q < 4000; q++)
        {
            participant.transform.position = new Vector3((float)(rnd.NextDouble() * 100 - 50), 0f, (float)(rnd.NextDouble() * 100 - 50));
            Physics.SyncTransforms();
            Transform npc = npcs[rnd.Next(npcs.Count)];
            Vector3 eye = npc.position + new Vector3((float)(rnd.NextDouble() - 0.5) * 0.3f, 1.4f + (float)rnd.NextDouble() * 0.3f, (float)(rnd.NextDouble() - 0.5) * 0.3f);
            Transform pRoot = (q % 3 == 0) ? null : participant.transform;
            object a = CallStatic(LosType, "Query", (Vector3?)eye, npc, head.transform, pRoot, 1 << 0);
            object b = CallStatic(LosType, "QueryExhaustive", (Vector3?)eye, npc, head.transform, pRoot, 1 << 0);
            Assert.AreEqual(Code(b), Code(a), "query " + q + ": near/far split must return the reference status (reference " + b + ", split " + a + ")");
            total++; string n = b.ToString(); if (n == "Blocked") blocked++; else if (n == "Clear") clear++; else other++;
        }
        Assert.Greater(blocked, total / 20, "the scenes must exercise 'blocked'"); Assert.Greater(clear, total / 20, "the scenes must exercise 'clear'");
        TestContext.Out.WriteLine("LOS equivalence: total=" + total + " blocked=" + blocked + " clear=" + clear + " other=" + other);
    }

    [UnityTest]
    public IEnumerator Los_NoParticipantOrOrigin_AndZeroLengthSegment_AreUnchanged()
    {
        var npc = MakeNpc(Vector3.zero, "npc"); var tgt = new GameObject("t"); _objs.Add(tgt); tgt.transform.position = new Vector3(0f, 1.5f, 10f);
        yield return null;
        Assert.AreEqual(Code(CallStatic(LosType, "QueryExhaustive", (Vector3?)null, npc, tgt.transform, null, 1)), Code(CallStatic(LosType, "Query", (Vector3?)null, npc, tgt.transform, null, 1)));
        Assert.AreEqual(Code(CallStatic(LosType, "QueryExhaustive", (Vector3?)Vector3.one, npc, null, null, 1)), Code(CallStatic(LosType, "Query", (Vector3?)Vector3.one, npc, null, null, 1)));
        tgt.transform.position = Vector3.one;
        Assert.AreEqual(Code(CallStatic(LosType, "QueryExhaustive", (Vector3?)Vector3.one, npc, tgt.transform, null, 1)), Code(CallStatic(LosType, "Query", (Vector3?)Vector3.one, npc, tgt.transform, null, 1)), "zero-length segment");
    }
}
