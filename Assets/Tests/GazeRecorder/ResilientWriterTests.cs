// ResilientWriterTests.cs
// Fault-injection tests of the OPT-IN resilient mode of CaptureCsvWriter (the writer behind gaze_diagnostics.csv): a temporary file lock must not lose rows, must not block the main thread,
// must keep the row order and exactly-once semantics, and a persistent failure must be bounded and reported. Real files in a unique temp folder, deterministic time through
// CaptureClock.TestSource and deterministic failures through CaptureIO.FaultInjector (plus one test with a REAL FileShare.None lock where the runtime enforces sharing).
// The production types live in Assembly-CSharp and are reached by reflection (see TestSupport.cs). Editor / scratch tests - not Quest tests.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public class ResilientWriterTests
{
    static readonly Type WriterType = T("CaptureCsvWriter");
    static readonly Type PolicyType = T("WriterRetryPolicy");
    static readonly Type CaptureIoType = T("CaptureIO");

    string _root;
    double _now;
    readonly List<object> _transitions = new List<object>();
    static readonly List<object> s_sink = new List<object>();
    static void Sink<TEvt>(TEvt t) { s_sink.Add(t); }

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "ResilientWriterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _now = 1000.0;
        T("CaptureClock").GetField("TestSource").SetValue(null, (Func<double>)(() => _now));
        s_sink.Clear();
        CallStatic(WriterType, "ResetOwnershipForTests");
    }

    [TearDown]
    public void TearDown()
    {
        Fault(null);
        T("CaptureClock").GetField("TestSource").SetValue(null, null);
        CallStatic(WriterType, "ResetOwnershipForTests");
        try { Directory.Delete(_root, true); } catch { }
    }

    // ------------------------------------------------------------------ helpers

    static void Fault(Func<string, string, Exception> f) { CaptureIoType.GetField("FaultInjector").SetValue(null, f); }

    object Policy(double budget = 600, int maxRows = 20000, double initial = 0.5, double max = 5.0, double persistent = 60, long maxBytes = 16L * 1024 * 1024)
    {
        var p = Activator.CreateInstance(PolicyType);
        Set(p, "MaxRecoveryDurationSeconds", budget); Set(p, "MaxPendingRows", maxRows); Set(p, "InitialBackoffSeconds", initial); Set(p, "MaxBackoffSeconds", max);
        Set(p, "PersistentFailureBudgetSeconds", persistent); Set(p, "MaxPendingBytes", maxBytes);
        return p;
    }

    object NewWriter(string file, object policy, string name = "gaze_diagnostics")
    {
        var w = Activator.CreateInstance(WriterType, Path.Combine(_root, file), "frame,v", name, false);
        if (policy != null) Call(w, "EnableResilience", policy);
        var ev = WriterType.GetEvent("Transition");
        ev.AddEventHandler(w, Delegate.CreateDelegate(ev.EventHandlerType, typeof(ResilientWriterTests).GetMethod(nameof(Sink), All).MakeGenericMethod(T("WriterTransition"))));
        return w;
    }

    string P(string file) => Path.Combine(_root, file);
    static Exception Sharing(string path) => new IOException("Sharing violation on path " + path);
    static string State(object w) => Get<object>(w, "State").ToString();
    static long L(object w, string n) => Get<long>(w, n);
    static void ExpectOutageLogs() { LogAssert.Expect(LogType.Error, new Regex("CSV stream disabled after I/O error")); }

    // rows are "<index>,x"; returns the data lines of the file (header excluded)
    List<string> DataLines(string file)
    {
        var lines = new List<string>(File.ReadAllLines(P(file)));
        Assert.AreEqual("frame,v", lines[0], "header");
        Assert.AreEqual(1, lines.FindAll(l => l == "frame,v").Count, "the header is written exactly once");
        lines.RemoveAt(0); return lines;
    }

    static void AssertSequence(List<string> lines, int count, string what)
    {
        Assert.AreEqual(count, lines.Count, what + ": row count");
        for (int i = 0; i < count; i++) Assert.AreEqual(i + ",x", lines[i], what + ": row " + i + " (order + exactly once)");
    }

    struct RunResult { public int rows; public double maxCallSeconds; public int openProbesWhileLocked; public long maxPending; }

    // Offers rows at 20 Hz for `total` seconds of simulated time; the file is locked (every open fails with a sharing violation) from t0+lockStart for lockSeconds.
    RunResult Run(object w, string file, double lockStart, double lockSeconds, double total, bool close = true)
    {
        string path = P(file); double t0 = _now; int probes = 0; var res = new RunResult();
        Fault((p, op) =>
        {
            if (p != path || op != "open") return null;
            bool locked = _now >= t0 + lockStart && _now < t0 + lockStart + lockSeconds;
            if (locked) probes++;
            return locked ? Sharing(path) : null;
        });
        var sw = new Stopwatch(); int i = 0;
        while (_now < t0 + total)
        {
            sw.Restart(); Call(w, "WriteLine", i + ",x"); sw.Stop();
            res.maxCallSeconds = Math.Max(res.maxCallSeconds, sw.Elapsed.TotalSeconds); res.maxPending = Math.Max(res.maxPending, L(w, "PendingRows"));
            i++; _now += 0.05;
        }
        res.rows = i; res.openProbesWhileLocked = probes;
        if (close) { Fault(null); Call(w, "Close"); }
        return res;
    }

    // ------------------------------------------------------------------ F1: temporary locks (2 s, 6.17 s, 10 s) - every row recovered, in order, once

    [TestCase(2.0)]
    [TestCase(6.17)]
    [TestCase(10.0)]
    public void TemporaryLock_IsTolerated_AllRowsWrittenOnceInOrder_WithoutBlocking(double lockSeconds)
    {
        var w = NewWriter("diag.csv", Policy());
        ExpectOutageLogs();
        var r = Run(w, "diag.csv", 1.0, lockSeconds, lockSeconds + 8.0);
        Assert.AreEqual("Recovered", State(w)); Assert.IsFalse(Get<bool>(w, "PermanentlyFailed"));
        AssertSequence(DataLines("diag.csv"), r.rows, "lock " + lockSeconds + " s");
        Assert.AreEqual(0L, L(w, "RowsLost"), "nothing lost"); Assert.AreEqual(0L, L(w, "PendingRows"), "queue drained");
        Assert.AreEqual((long)r.rows, L(w, "RowsWritten")); Assert.AreEqual((long)r.rows, L(w, "RowsAttempted"));
        Assert.Greater(L(w, "RowsRecoveredFromBuffer"), 0L, "rows were written FROM the queue");
        Assert.AreEqual(1, Get<int>(w, "OutageEpisodes")); Assert.GreaterOrEqual(Get<double>(w, "LongestOutageSeconds"), lockSeconds - 0.1);
        Assert.Less(r.maxCallSeconds, 0.25, "no call blocks the caller (a refused open returns immediately; nothing sleeps)");
        Assert.Greater(Get<double>(w, "RecoveredT"), 1000.0 + 1.0 + lockSeconds - 0.1);
        Assert.AreEqual("SharingViolation", Get<object>(w, "LastErrorKind").ToString());
        Assert.AreEqual(1, s_sink.FindAll(t => Get<string>(t, "Status") == "recovered").Count);
        Assert.AreEqual(1, s_sink.FindAll(t => Get<string>(t, "Status") == "failed").Count, "one 'failed' transition per outage, not per row");
    }

    [Test]
    public void LegacyPolicy_CannotSurviveALockLongerThanItsBudget_TheObservedFailureMechanism()
    {
        // The legacy budget is 3 recovery attempts, 2.0 s apart, measured from the previous failure: about 6.0-6.2 s depending on when the rows that trigger the attempts arrive (P4 Part 3: 6.17 s lock lost,
        // three other sessions survived 6.14-6.16 s by a hair). A 6.5 s lock is safely past that boundary and shows the mechanism deterministically.
        var w = NewWriter("legacy.csv", null);   // no EnableResilience: the original 3 x 2 s policy
        ExpectOutageLogs();
        LogAssert.Expect(LogType.Warning, new Regex("recovery attempt 1 failed")); LogAssert.Expect(LogType.Warning, new Regex("recovery attempt 2 failed"));
        LogAssert.Expect(LogType.Error, new Regex("permanently failed"));
        var r = Run(w, "legacy.csv", 1.0, 6.5, 14.0);
        Assert.IsTrue(Get<bool>(w, "PermanentlyFailed"), "the lock outlasts 3 attempts x 2 s");
        Assert.IsFalse(Get<bool>(w, "Resilient"));
        Assert.Less(L(w, "RowsWritten"), (long)r.rows, "rows were lost");
        Assert.Greater(L(w, "RowsDroppedAfterFailure"), 0L);
    }

    [Test]
    public void Backoff_DoesNotHammerTheFile_DuringALock()
    {
        var w = NewWriter("diag.csv", Policy());
        ExpectOutageLogs();
        var r = Run(w, "diag.csv", 1.0, 10.0, 18.0);
        Assert.GreaterOrEqual(r.openProbesWhileLocked, 4); Assert.LessOrEqual(r.openProbesWhileLocked, 8, "open attempts follow the back-off schedule (0.5, 1, 2, 4 s ...), not one per row (200 rows were offered)");
        Assert.LessOrEqual(Get<int>(w, "RecoveryAttempts"), 8);
    }

    // ------------------------------------------------------------------ F2: persistent lock - bounded buffer, accurate loss counts, degraded state

    [Test]
    public void PersistentLock_BoundedBuffer_AccurateLossCounts_FailedState_NoFurtherAttempts()
    {
        var w = NewWriter("diag.csv", Policy(budget: 30, maxRows: 100));
        ExpectOutageLogs(); LogAssert.Expect(LogType.Error, new Regex("retry queue is full")); LogAssert.Expect(LogType.Error, new Regex("permanently failed"));
        var r = Run(w, "diag.csv", 1.0, 1e9, 60.0, close: false);
        Assert.LessOrEqual(r.maxPending, 100L, "the queue never exceeds its capacity");
        Assert.IsTrue(Get<bool>(w, "PermanentlyFailed")); Assert.AreEqual("Failed", State(w));
        Assert.Greater(L(w, "RowsOverflowDropped"), 0L);
        long attempts = L(w, "RowsAttempted");
        Assert.AreEqual(attempts, L(w, "RowsWritten") + L(w, "RowsFailed") + L(w, "RowsDroppedAfterFailure") + L(w, "PendingRows"), "attempted = written + failed + dropped + pending");
        Assert.AreEqual(L(w, "RowsDroppedAfterFailure"), L(w, "RowsLost")); Assert.AreEqual(0L, L(w, "PendingRows"), "after the budget the queue is released");
        int rec = Get<int>(w, "RecoveryAttempts"); _now += 100; Fault(null);
        Call(w, "WriteLine", "late,x");
        Assert.AreEqual(rec, Get<int>(w, "RecoveryAttempts"), "no further attempts after a permanent failure");
        Assert.That(L(w, "RowsWritten"), Is.InRange(19L, 21L), "only the rows written before the lock are claimed as written");
        Assert.AreEqual((int)L(w, "RowsWritten"), DataLines("diag.csv").Count, "the file holds exactly the rows claimed as written");
        Assert.IsTrue(Get<string>(w, "LastError").Contains("IOException"));
        Call(w, "Close");
    }

    [Test]
    public void PersistentKinds_GetAShortBudget_AndAreClassified()
    {
        Assert.AreEqual("SharingViolation", CallStatic(WriterType, "Classify", new IOException("The process cannot access the file because it is being used by another process")).ToString());
        Assert.AreEqual("SharingViolation", CallStatic(WriterType, "Classify", new IOException("x", unchecked((int)0x80070020))).ToString());
        Assert.AreEqual("DiskFull", CallStatic(WriterType, "Classify", new IOException("There is not enough space on the disk")).ToString());
        Assert.AreEqual("Permission", CallStatic(WriterType, "Classify", new UnauthorizedAccessException("denied")).ToString());
        Assert.AreEqual("PathOrDirectory", CallStatic(WriterType, "Classify", new DirectoryNotFoundException("gone")).ToString());
        Assert.AreEqual("Disposed", CallStatic(WriterType, "Classify", new ObjectDisposedException("fs")).ToString());
        Assert.AreEqual("TransientIo", CallStatic(WriterType, "Classify", new IOException("something odd")).ToString());
        var pol = Policy(budget: 600, persistent: 10);
        Assert.AreEqual(600.0, (double)Call(pol, "BudgetFor", Enum.Parse(T("WriterFailureKind"), "SharingViolation")));
        Assert.AreEqual(10.0, (double)Call(pol, "BudgetFor", Enum.Parse(T("WriterFailureKind"), "Permission")));

        // a permission failure is given up after the SHORT budget although the transient budget is 600 s
        var w = NewWriter("perm.csv", pol); string path = P("perm.csv");
        Call(w, "WriteLine", "0,x");
        Fault((p, op) => p == path && op == "open" ? new UnauthorizedAccessException("denied") : null);
        ExpectOutageLogs(); LogAssert.Expect(LogType.Error, new Regex("permanently failed"));
        for (int i = 1; i < 400; i++) { _now += 0.05; Call(w, "WriteLine", i + ",x"); }   // 20 s
        Assert.IsTrue(Get<bool>(w, "PermanentlyFailed")); Assert.AreEqual("Permission", Get<object>(w, "LastErrorKind").ToString());
        Assert.AreEqual(1L, L(w, "RowsWritten"), "the one row written before the failure is kept");
        Fault(null); Call(w, "Close");
    }

    // ------------------------------------------------------------------ F3: lifecycle

    [Test]
    public void Lifecycle_NormalWrites_LazyHeader_Close_Reopen_RepeatedInitialisation()
    {
        var w = NewWriter("diag.csv", Policy());
        Assert.IsFalse(File.Exists(P("diag.csv")), "lazy: no file before the first row"); Assert.AreEqual("Operational", State(w));
        for (int i = 0; i < 20; i++) { Call(w, "WriteLine", i + ",x"); _now += 0.05; }
        Assert.AreEqual(20L, L(w, "RowsWritten")); Assert.AreEqual(0L, L(w, "PendingRows")); Assert.AreEqual("Operational", State(w));
        Assert.Throws<InvalidOperationException>(() => { try { Call(w, "EnableResilience", Policy()); } catch (System.Reflection.TargetInvocationException e) { throw e.InnerException; } }, "the mode cannot change after rows were offered");
        Call(w, "Close"); Assert.IsTrue(Get<bool>(w, "ClosedCleanly"));
        Call(w, "WriteLine", "99,x"); Assert.AreEqual(1L, L(w, "RowsRejectedAfterClose"), "nothing is appended after Close");
        // reopening: a NEW writer for the same file continues it without a second header
        var w2 = NewWriter("diag.csv", Policy());
        for (int i = 20; i < 30; i++) { Call(w2, "WriteLine", i + ",x"); _now += 0.05; }
        Call(w2, "Close");
        AssertSequence(DataLines("diag.csv"), 30, "reopened file");
    }

    [Test]
    public void SingleOwner_ASecondOpenWriterForTheSameFile_IsRefused_AndCounted()
    {
        var a = NewWriter("diag.csv", Policy()); var b = NewWriter("diag.csv", Policy(), "gaze_diagnostics_dup");
        Call(a, "WriteLine", "0,x");
        LogAssert.Expect(LogType.Error, new Regex("another open writer already owns"));
        Call(b, "WriteLine", "BAD,x"); Call(b, "WriteLine", "BAD,x");
        Assert.AreEqual(2L, L(b, "RowsDroppedAfterFailure")); Assert.AreEqual(0L, L(b, "RowsWritten")); Assert.AreEqual("DuplicateWriter", Get<object>(b, "LastErrorKind").ToString());
        Call(a, "WriteLine", "1,x"); Call(a, "Close");
        AssertSequence(DataLines("diag.csv"), 2, "only the owner wrote");
        var c = NewWriter("diag.csv", Policy()); Call(c, "WriteLine", "2,x"); Call(c, "Close");   // after Close the ownership is released
        AssertSequence(DataLines("diag.csv"), 3, "after release a new owner can write");
    }

    [Test]
    public void FailedAppend_PartialTail_IsCutOff_AndTheBatchIsWrittenExactlyOnce()
    {
        var w = NewWriter("diag.csv", Policy()); string path = P("diag.csv");
        for (int i = 0; i < 5; i++) Call(w, "WriteLine", i + ",x");
        bool fail = true;
        Fault((p, op) => fail && p == path && op == "flush" ? new IOException("Sharing violation (flush)") : null);   // the bytes of row 5 reach the file, the flush step then reports an error
        ExpectOutageLogs(); _now += 0.05; Call(w, "WriteLine", "5,x");
        Assert.AreEqual("Failed", State(w)); Assert.IsTrue(Get<bool>(w, "PartialOutputPossible")); Assert.AreEqual(1L, L(w, "PendingRows"));
        fail = false; _now += 0.7; Call(w, "WriteLine", "6,x");
        Assert.AreEqual("Recovered", State(w)); Assert.AreEqual(1, Get<int>(w, "PartialTailsRepaired")); Assert.IsFalse(Get<bool>(w, "PartialOutputPossible"));
        Call(w, "Close");
        AssertSequence(DataLines("diag.csv"), 7, "after a partial append");
        Assert.AreEqual(0L, L(w, "RowsLost"));
    }

    [Test]
    public void Pump_FlushesWithoutNewRows_AndHonoursTheBackoff()
    {
        var w = NewWriter("diag.csv", Policy()); string path = P("diag.csv"); bool locked = true; int probes = 0;
        Fault((p, op) => { if (p == path && op == "open" && locked) { probes++; return Sharing(path); } return null; });
        ExpectOutageLogs();
        Call(w, "WriteLine", "0,x"); _now += 0.05; Call(w, "WriteLine", "1,x");
        int before = probes; Call(w, "Pump"); Assert.AreEqual(before, probes, "Pump inside the back-off performs no I/O");
        locked = false; _now += 1.0; Call(w, "Pump");   // e.g. application focus regained after a pause: no new row is needed
        Assert.AreEqual("Recovered", State(w)); Assert.AreEqual(0L, L(w, "PendingRows"));
        Call(w, "Close"); AssertSequence(DataLines("diag.csv"), 2, "pump");
    }

    [Test]
    public void Close_MakesOneFinalAttempt_AndCountsWhatItCannotStore_WithoutTransitions()
    {
        // (a) the lock is gone by Close: everything queued is written
        var w = NewWriter("a.csv", Policy()); string pa = P("a.csv"); bool locked = true;
        Fault((p, op) => p == pa && op == "open" && locked ? Sharing(pa) : null);
        ExpectOutageLogs();
        for (int i = 0; i < 10; i++) { Call(w, "WriteLine", i + ",x"); _now += 0.05; }
        Assert.AreEqual(10L, L(w, "PendingRows")); locked = false;
        Call(w, "Close");
        Assert.AreEqual(0L, L(w, "RowsLostAtClose")); Assert.AreEqual(10L, L(w, "RowsWritten")); AssertSequence(DataLines("a.csv"), 10, "final drain");
        // (b) still locked at Close: the loss is counted, the state is failed, no transition fires during the close
        var v = NewWriter("b.csv", Policy()); string pb = P("b.csv");
        Fault((p, op) => p == pb && op == "open" ? Sharing(pb) : null);
        ExpectOutageLogs(); LogAssert.Expect(LogType.Error, new Regex("closed with 7 row"));
        for (int i = 0; i < 7; i++) { Call(v, "WriteLine", i + ",x"); _now += 0.05; }
        int tr = s_sink.Count;
        Call(v, "Close");
        Assert.AreEqual(7L, L(v, "RowsLostAtClose")); Assert.AreEqual(7L, L(v, "RowsLost")); Assert.IsTrue(Get<bool>(v, "PermanentlyFailed")); Assert.AreEqual("Failed", State(v));
        Assert.AreEqual(tr, s_sink.Count, "Close raises no transition (the final manifest carries the state)");
        Assert.AreEqual(L(v, "RowsAttempted"), L(v, "RowsWritten") + L(v, "RowsLost") + L(v, "PendingRows"));
    }

    [Test]
    public void SecondOutage_AfterRecovery_AndAFailureWhileDrainingABacklog_AreHandled()
    {
        var pol = Policy(initial: 0.5); Set(pol, "MaxRowsPerDrain", 10);
        var w = NewWriter("diag.csv", pol); string path = P("diag.csv"); bool locked = false; bool armed = false; int appendBudget = 0;
        Fault((p, op) =>
        {
            if (p != path) return null;
            if (op == "open" && locked) return Sharing(path);
            if (op == "append" && armed) { if (appendBudget > 0) appendBudget--; else { armed = false; return Sharing(path); } }
            return null;
        });
        int i = 0; void Row() { Call(w, "WriteLine", i + ",x"); i++; _now += 0.05; }
        for (int k = 0; k < 10; k++) Row();
        locked = true; ExpectOutageLogs(); for (int k = 0; k < 40; k++) Row();             // outage 1 (2 s): a 40-row backlog
        locked = false; _now += 2.0; Row();                                                // first chunk of the backlog (<= 10 rows per call: bounded catch-up cost)
        Assert.AreEqual("Recovered", State(w)); Assert.Greater(L(w, "PendingRows"), 0L, "the rest of the backlog drains over the next rows");
        for (int k = 0; k < 6; k++) Row();
        Assert.AreEqual(0L, L(w, "PendingRows"), "backlog fully drained");
        locked = true; LogAssert.Expect(LogType.Error, new Regex("CSV stream disabled after I/O error")); for (int k = 0; k < 40; k++) Row();   // outage 2: another 40-row backlog
        locked = false; armed = true; appendBudget = 1;                                    // the first 10-row chunk is written, the second chunk fails again
        _now += 2.0; Row();
        Assert.AreEqual("Recovered", State(w)); Assert.Greater(L(w, "PendingRows"), 0L);
        LogAssert.Expect(LogType.Error, new Regex("CSV stream disabled after I/O error"));
        Row();
        Assert.AreEqual("Failed", State(w), "a failure while draining the backlog starts a new outage"); Assert.Greater(L(w, "PendingRows"), 0L);
        _now += 3.0; for (int k = 0; k < 5; k++) Row();
        Call(w, "Close");
        Assert.AreEqual(3, Get<int>(w, "OutageEpisodes")); Assert.AreEqual(0L, L(w, "RowsLost"));
        AssertSequence(DataLines("diag.csv"), i, "outages and a failure while draining");
    }

    // ------------------------------------------------------------------ F4: independent streams

    [Test]
    public void Streams_AreIndependent_DiagnosticsLockDoesNotTouchGaze_AndGazeFailureIsRecordedSeparately()
    {
        var gaze = NewWriter("gaze.csv", null, "gaze"); var diag = NewWriter("diag.csv", Policy()); string pd = P("diag.csv"), pg = P("gaze.csv");
        bool diagLocked = false, gazeLocked = false;
        Fault((p, op) => op == "open" && ((p == pd && diagLocked) || (p == pg && gazeLocked)) ? Sharing(p) : null);
        int i = 0; void Row() { Call(gaze, "WriteLine", i + ",x"); Call(diag, "WriteLine", i + ",x"); i++; _now += 0.05; }
        for (int k = 0; k < 10; k++) Row();
        diagLocked = true; ExpectOutageLogs(); for (int k = 0; k < 220; k++) Row();      // 11 s diagnostics lock
        Assert.AreEqual("Operational", State(gaze)); Assert.AreEqual((long)i, L(gaze, "RowsWritten"), "gaze kept every row while diagnostics was locked");
        Assert.AreEqual("Failed", State(diag)); Assert.Greater(L(diag, "PendingRows"), 0L);
        diagLocked = false; _now += 3.0; Row(); Assert.AreEqual("Recovered", State(diag));
        // now the other way round: gaze locks, diagnostics keeps writing
        gazeLocked = true; LogAssert.Expect(LogType.Error, new Regex("CSV stream disabled after I/O error")); for (int k = 0; k < 20; k++) Row();
        Assert.AreEqual("Failed", State(gaze)); Assert.AreEqual("Recovered", State(diag), "a failing gaze stream does not disturb the diagnostics stream");
        Assert.AreEqual(0L, L(diag, "RowsLost"));
        gazeLocked = false; Fault(null); Call(diag, "Close");
        AssertSequence(DataLines("diag.csv"), i, "diagnostics");
        Assert.AreEqual("gaze", Get<string>(s_sink.Find(t => Get<string>(t, "Stream") == "gaze"), "Stream"), "the gaze failure is recorded under its own stream name");
        Assert.IsTrue(s_sink.Exists(t => Get<string>(t, "Stream") == "gaze_diagnostics"));
    }

    // ------------------------------------------------------------------ performance of the healthy path

    [Test]
    public void HealthyPath_Cost_OfTheResilientWriter_IsComparableToTheLegacyWriter()
    {
        string row = new string('x', 560);   // a gaze_diagnostics row is about 0.6 KB
        double Measure(object w, int n)
        {
            for (int i = 0; i < 200; i++) Call(w, "WriteLine", row);                    // warm-up (JIT, file creation)
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) Call(w, "WriteLine", row);
            return sw.Elapsed.TotalMilliseconds / n;
        }
        var legacy = NewWriter("perf_legacy.csv", null, "perf_legacy"); var res = NewWriter("perf_res.csv", Policy(), "perf_res");
        double a1 = Measure(legacy, 3000), b1 = Measure(res, 3000), a2 = Measure(legacy, 3000), b2 = Measure(res, 3000);
        double legacyMs = Math.Min(a1, a2), resMs = Math.Min(b1, b2);
        UnityEngine.Debug.Log($"[ResilientWriterTests] healthy-path cost per row (incl. reflection call overhead): legacy {legacyMs * 1000:0.0} us, resilient {resMs * 1000:0.0} us");
        Assert.Less(resMs, legacyMs * 1.5 + 0.05, "the resilient fast path costs about the same as the legacy path (one open/append/close per row either way)");
        Call(legacy, "Close"); Call(res, "Close");
    }

    // ------------------------------------------------------------------ real lock, where the runtime enforces sharing

    [Test]
    public void RealFileLock_FileShareNone_ForSixSeconds_IsToleratedWhereTheRuntimeEnforcesSharing()
    {
        string path = P("real.csv"); var w = NewWriter("real.csv", Policy());
        Call(w, "WriteLine", "0,x");
        FileStream held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        bool enforced;
        try { using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { } enforced = false; }
        catch (IOException) { enforced = true; }
        if (!enforced) { held.Dispose(); Assert.Ignore("this runtime does not enforce FileShare between handles of one process; the injected-lock tests cover the policy"); }
        ExpectOutageLogs();
        int i = 1; double lockedUntil = _now + 6.17;
        while (_now < lockedUntil) { Call(w, "WriteLine", i + ",x"); i++; _now += 0.05; }
        Assert.AreEqual("Failed", State(w)); Assert.Greater(L(w, "PendingRows"), 0L);
        held.Dispose();   // lock released after 6.17 s
        _now += 3.0; Call(w, "WriteLine", i + ",x"); i++;
        Assert.AreEqual("Recovered", State(w)); Call(w, "Close");
        AssertSequence(DataLines("real.csv"), i, "real lock");
        Assert.AreEqual(0L, L(w, "RowsLost"));
    }
}
