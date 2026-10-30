// ReliabilityTests.cs
// PlayMode tests for schema_version 7: F-10 (NPC line of sight), F-13 (stream-failure diagnostics, via CaptureIO.FaultInjector), F-14 (identity validation, config
// precedence, unique sessions, fail-closed) and F-19 (atomic, recoverable session_manifest.json). They exercise the PRODUCTION NpcLineOfSight, CaptureCsvWriter,
// CaptureIO, ManifestFileWriter, SessionIdentity, ExternalSessionController, ExperimentManager and DataCaptureManager through reflection, with real files in a
// unique temp folder and deterministic fault injection. Editor/scratch tests - not Android/Quest tests, and no abrupt-process-termination test.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    static readonly Type WriterType = T("CaptureCsvWriter");
    static readonly Type ManifestWriterType = T("ManifestFileWriter");
    static readonly Type LosType = T("NpcLineOfSight");

    // ------------------------------------------------------------------ small helpers

    static string ManJson(int rev, string tag = "x") => "{\n  \"session_id\": \"s\",\n  \"tag\": \"" + tag + "\",\n  \"manifest_revision\": " + rev + "\n}";
    static int RevOf(string path) { var m = Regex.Match(File.ReadAllText(path), "\"manifest_revision\"\\s*:\\s*(\\d+)"); return m.Success ? int.Parse(m.Groups[1].Value) : -1; }
    static bool ValidManifest(string path) { object[] a = { File.Exists(path) ? File.ReadAllText(path) : "", 0 }; return (bool)ManifestWriterType.GetMethod("IsValidManifestJson").Invoke(null, a); }
    static object WriteManifest(string path, string json) => ManifestWriterType.GetMethod("WriteAtomic").Invoke(null, new object[] { path, json });
    static bool Ok(object r) => Get<bool>(r, "Ok");

    static readonly List<object> s_transitions = new List<object>();
    static void OnTransitionObj<TEvt>(TEvt t) { s_transitions.Add(t); }

    object NewWriter(string file, string name = "t_stream")
    {
        var w = Activator.CreateInstance(WriterType, Path.Combine(_root, file), "a,b", name, false);
        var ev = WriterType.GetEvent("Transition");
        ev.AddEventHandler(w, Delegate.CreateDelegate(ev.EventHandlerType, typeof(NpcLifecycleTests).GetMethod(nameof(OnTransitionObj), All).MakeGenericMethod(T("WriterTransition"))));
        return w;
    }

    static Exception Io(string m) => new IOException(m);
    static string WriterState(object w) => Get<object>(w, "State").ToString();
    static void SetClock(Func<double> f) { T("CaptureClock").GetField("TestSource").SetValue(null, f); }

    // ====================================================================================== F-19: manifest persistence

    [Test]
    public void Manifest_Atomic_FirstWriteReplaceAndBackup()
    {
        string path = Path.Combine(_root, "session_manifest.json");
        var r1 = WriteManifest(path, ManJson(1));
        Assert.IsTrue(Ok(r1)); Assert.AreEqual("move", Get<string>(r1, "InstallMode"), "no final yet: a single rename");
        Assert.IsTrue(ValidManifest(path)); Assert.AreEqual(1, RevOf(path));

        var r2 = WriteManifest(path, ManJson(2));
        Assert.IsTrue(Ok(r2)); Assert.AreEqual("replace", Get<string>(r2, "InstallMode"), "File.Replace is available on this runtime");
        Assert.AreEqual(2, RevOf(path)); Assert.IsTrue(Get<bool>(r2, "BackupUpdated"));
        Assert.AreEqual(1, RevOf(path + ".bak"), "the previous valid generation is kept as .bak");
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.tmp").Length, "no temp file left behind");
        Assert.IsFalse(Encoding_HasBom(path), "UTF-8 without BOM");
    }
    static bool Encoding_HasBom(string p) { var b = File.ReadAllBytes(p); return b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF; }

    [Test]
    public void Manifest_Atomic_AFailureAtAnyStage_KeepsThePreviousValidManifest()
    {
        string path = Path.Combine(_root, "session_manifest.json");
        Assert.IsTrue(Ok(WriteManifest(path, ManJson(5))));
        string before = File.ReadAllText(path);
        foreach (string stage in new[] { "manifest_tmp_create", "manifest_tmp_write", "manifest_tmp_flush", "manifest_validate", "manifest_replace" })
        {
            CaptureIoFault((p, op) => op == stage ? Io("injected at " + stage) : null);
            var r = WriteManifest(path, ManJson(6));
            CaptureIoFault(null);
            Assert.IsFalse(Ok(r), stage); Assert.AreEqual(stage, Get<string>(r, "Stage"), "the failing step is reported");
            StringAssert.Contains("IOException", Get<string>(r, "Error"));
            Assert.AreEqual(before, File.ReadAllText(path), stage + ": the previous manifest is byte-identical");
            Assert.IsTrue(ValidManifest(path), stage);
            if (stage != "manifest_replace") Assert.AreEqual(0, Directory.GetFiles(_root, "*.tmp").Length, stage + ": an unusable temp file is removed");
        }
        // a failed BACKUP copy (valid final) is best effort and must not block the update
        CaptureIoFault((p, op) => op == "manifest_backup" ? Io("no backup") : null);
        var rb = WriteManifest(path, ManJson(7)); CaptureIoFault(null);
        Assert.IsTrue(Ok(rb)); Assert.IsFalse(Get<bool>(rb, "BackupUpdated")); Assert.AreEqual(7, RevOf(path));
        // success cleans the validated temp file that the failed replace had left behind
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.tmp").Length);
    }

    [Test]
    public void Manifest_Recovery_FromBackup_FromANewerTempGeneration_AndNeverFromAnOlderOne()
    {
        string path = Path.Combine(_root, "session_manifest.json");
        Assert.IsTrue(Ok(WriteManifest(path, ManJson(1)))); Assert.IsTrue(Ok(WriteManifest(path, ManJson(2))));   // final=2, bak=1

        // (a) truncated final + valid backup: the backup is installed, the corrupt file is preserved
        File.WriteAllText(path, "{\n  \"session_id\": \"s\",\n  \"manifest_rev");
        Assert.IsFalse(ValidManifest(path));
        var rep = ManifestWriterType.GetMethod("Recover").Invoke(null, new object[] { path });
        Assert.IsTrue(Get<bool>(rep, "ValidFound")); Assert.AreEqual("backup", Get<string>(rep, "Source")); Assert.IsTrue(Get<bool>(rep, "Installed"));
        Assert.AreEqual(1, RevOf(path)); Assert.IsTrue(ValidManifest(path));
        Assert.AreEqual(1, Directory.GetFiles(_root, "session_manifest.json.corrupt-*").Length, "the corrupt evidence is preserved");

        // (b) a complete NEWER generation left as a temp file (replace never happened) wins over the older final
        File.WriteAllText(path + ".deadbeef.1.tmp", ManJson(9, "newer"));
        var rep2 = ManifestWriterType.GetMethod("Recover").Invoke(null, new object[] { path });
        StringAssert.StartsWith("tmp:", Get<string>(rep2, "Source")); Assert.AreEqual(9, RevOf(path));
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.tmp").Length);

        // (c) an older backup must never overwrite a newer valid final
        File.WriteAllText(path + ".bak", ManJson(3));
        var rep3 = ManifestWriterType.GetMethod("Recover").Invoke(null, new object[] { path });
        Assert.AreEqual("final", Get<string>(rep3, "Source")); Assert.AreEqual(9, RevOf(path), "newer valid final untouched");

        // (d) a half-written temp file is not a manifest
        File.WriteAllText(path + ".cafebabe.2.tmp", "{\"manifest_revision\": 99, \"x\": ");
        var rep4 = ManifestWriterType.GetMethod("Recover").Invoke(null, new object[] { path });
        Assert.AreEqual("final", Get<string>(rep4, "Source")); Assert.AreEqual(9, RevOf(path));

        // (e) nothing valid at all
        foreach (var f in Directory.GetFiles(_root)) File.Delete(f);
        File.WriteAllText(path, "garbage");
        Assert.IsFalse(Get<bool>(ManifestWriterType.GetMethod("Recover").Invoke(null, new object[] { path }), "ValidFound"));
    }

    [Test]
    public void Manifest_Atomic_ReplaceNotSupported_UsesTheRecoverableCopyFallback_AndStaleInvalidFinalIsPreserved()
    {
        string path = Path.Combine(_root, "session_manifest.json");
        Assert.IsTrue(Ok(WriteManifest(path, ManJson(1))));
        CaptureIoFault((p, op) => op == "manifest_replace" ? new NotSupportedException("no File.Replace on this platform") : null);
        var r = WriteManifest(path, ManJson(2)); CaptureIoFault(null);
        Assert.IsTrue(Ok(r)); Assert.AreEqual("copy_fallback", Get<string>(r, "InstallMode"), "documented non-atomic fallback; .bak is kept for recovery");
        Assert.AreEqual(2, RevOf(path)); Assert.AreEqual(1, RevOf(path + ".bak"));

        // an invalid existing final is set aside, never silently overwritten
        File.WriteAllText(path, "{ truncated");
        var r2 = WriteManifest(path, ManJson(3));
        Assert.IsTrue(Ok(r2)); Assert.AreEqual(3, RevOf(path)); StringAssert.StartsWith("session_manifest.json.corrupt-", Get<string>(r2, "PreservedCorrupt"));
    }

    [Test]
    public void Manifest_JsonValidator_RejectsTruncatedAndIncompleteText()
    {
        bool V(string s) { object[] a = { s, 0 }; return (bool)ManifestWriterType.GetMethod("IsValidManifestJson").Invoke(null, a); }
        Assert.IsTrue(V(ManJson(4)));
        Assert.IsFalse(V("")); Assert.IsFalse(V("{")); Assert.IsFalse(V(ManJson(4).Substring(0, ManJson(4).Length - 2)));
        Assert.IsFalse(V("{\"a\": 1}"), "no manifest_revision"); Assert.IsFalse(V("[1,2]")); Assert.IsFalse(V("{\"manifest_revision\": 1} trailing"));
        Assert.IsFalse(V("{\"manifest_revision\": 1, \"s\": \"unterminated}"));
    }

    [UnityTest]
    public IEnumerator Manifest_Dcm_KeepsTheLastValidManifest_CountsAndReportsFailures_AndSurvivesRapidUpdates()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
        string path = rig.File("session_manifest.json");
        for (int i = 0; i < 30; i++) Call(rig.dcm, "WriteManifestCheckpoint", "rapid_" + i);   // rapid update requests
        Assert.IsTrue(ValidManifest(path)); int rev = RevOf(path); Assert.GreaterOrEqual(rev, 30);
        var m0 = JsonUtility.FromJson<ManifestProbe2>(File.ReadAllText(path)); Assert.AreEqual(rev, m0.manifest_revision);

        // the replace fails: previous valid manifest stays, failure is counted, logged and recorded in capture_errors.csv
        string before = File.ReadAllText(path);
        CaptureIoFault((p, op) => op == "manifest_replace" ? Io("disk full") : null);
        LogAssert.Expect(LogType.Error, new Regex("Failed to update session_manifest.json"));
        Call(rig.dcm, "WriteManifestCheckpoint", "during_failure");
        Call(rig.dcm, "WriteManifestCheckpoint", "during_failure_2");   // no second error log for the same ongoing failure
        Assert.AreEqual(before, File.ReadAllText(path), "previous complete manifest is untouched");
        CaptureIoFault(null);
        LogAssert.Expect(LogType.Warning, new Regex("session_manifest.json is writable again"));
        Call(rig.dcm, "WriteManifestCheckpoint", "recovered");
        var m1 = JsonUtility.FromJson<ManifestProbe2>(File.ReadAllText(path));
        Assert.Greater(m1.manifest_revision, rev); Assert.AreEqual(2, m1.manifest_write_failures, "both failed attempts are counted, even though the second was not logged again");
        Assert.AreEqual(0, Directory.GetFiles(rig.Folder, "*.tmp").Length);
        var errs = Csv.Load(rig.File("capture_errors.csv")); AssertWidths(errs, "capture_errors.csv");
        var man = Rows(errs, "stream_name", "session_manifest");
        Assert.AreEqual(2, man.Count, "one 'failed' and one 'recovered' transition, not one row per attempt");
        Assert.AreEqual("failed", errs.Get(man[0], "status")); Assert.AreEqual("manifest_replace", errs.Get(man[0], "operation")); Assert.AreEqual("recovered", errs.Get(man[1], "status"));
        Assert.AreEqual(Get<string>(rig.dcm, "SessionId"), errs.Get(man[0], "session_id"));

        // abrupt termination during a staged update: the replace never happens; Recover() yields the newest complete generation
        CaptureIoFault((p, op) => op == "manifest_replace" ? Io("process killed") : null);
        LogAssert.Expect(LogType.Error, new Regex("Failed to update session_manifest.json"));
        Call(rig.dcm, "WriteManifestCheckpoint", "killed_mid_update");
        CaptureIoFault(null);
        int staged = Directory.GetFiles(rig.Folder, "session_manifest.json.*.tmp").Length; Assert.GreaterOrEqual(staged, 1, "a validated newer generation is waiting");
        var rep = ManifestWriterType.GetMethod("Recover").Invoke(null, new object[] { path });
        Assert.IsTrue(ValidManifest(path)); Assert.Greater(RevOf(path), m1.manifest_revision, "recovery installed the staged newer generation");
        Assert.IsTrue(Get<bool>(rep, "ValidFound"));
    }
    [Serializable] public class ManifestProbe2 { public int manifest_revision, manifest_write_failures; public string manifest_last_install_mode; }

    // ====================================================================================== F-13: stream failures

    [Test]
    public void Writer_FailureAtEachOperation_IsExplicit_CountedAndNeverLooksHealthy()
    {
        foreach (string op in new[] { "open", "header", "append", "flush" })
        {
            string file = "w_" + op + ".csv"; string full = Path.Combine(_root, file);
            var w = NewWriter(file, "s_" + op);
            s_transitions.Clear();
            CaptureIoFault((p, o) => p == full && o == op ? Io("injected " + op) : null);
            LogAssert.Expect(LogType.Error, new Regex("CSV stream disabled after I/O error"));
            Call(w, "WriteLine", "1,1");
            CaptureIoFault(null);
            Assert.AreEqual("Failed", WriterState(w), op); Assert.IsTrue(Get<bool>(w, "Failed"));
            Assert.AreEqual(1L, Get<long>(w, "RowsAttempted")); Assert.AreEqual(0L, Get<long>(w, "RowsWritten")); Assert.AreEqual(1L, Get<long>(w, "RowsFailed"));
            Assert.AreEqual(1L, Get<long>(w, "WriteErrors")); Assert.AreEqual(op, Get<string>(w, "LastErrorOperation")); Assert.AreEqual("IOException", Get<string>(w, "LastErrorType"));
            Assert.IsFalse(double.IsNaN(Get<double>(w, "FirstErrorT"))); Assert.IsTrue(double.IsNaN(Get<double>(w, "FirstRowT")), "no success yet");
            Assert.AreEqual(op == "append" || op == "flush", Get<bool>(w, "PartialOutputPossible"));
            Assert.AreEqual(1, s_transitions.Count); Assert.AreEqual("failed", Get<string>(s_transitions[0], "Status")); Assert.AreEqual(op, Get<string>(s_transitions[0], "Operation"));
            // later rows within the cool-down: dropped and counted, no second error, nothing written
            Call(w, "WriteLine", "2,2"); Call(w, "WriteLine", "3,3");
            Assert.AreEqual(3L, Get<long>(w, "RowsAttempted")); Assert.AreEqual(2L, Get<long>(w, "RowsDroppedAfterFailure")); Assert.AreEqual(1L, Get<long>(w, "WriteErrors")); Assert.AreEqual(1, s_transitions.Count);
        }
    }

    [Test]
    public void Writer_FailsAfterSuccesses_KeepsHistory_ThenRecoversWithBoundedAttempts_AndOtherStreamsAreIndependent()
    {
        double now = 100; SetClock(() => now);
        var good = NewWriter("good.csv", "good");
        string bad = Path.Combine(_root, "bad.csv");
        var w = NewWriter("bad.csv", "bad");
        for (int i = 0; i < 5; i++) { Call(w, "WriteLine", i + ",x"); Call(good, "WriteLine", i + ",y"); now += 0.1; }
        Assert.AreEqual(5L, Get<long>(w, "RowsWritten")); double lastOk = Get<double>(w, "LastRowT");

        s_transitions.Clear();
        CaptureIoFault((p, o) => p == bad && o == "append" ? Io("disk full") : null);
        LogAssert.Expect(LogType.Error, new Regex("CSV stream disabled after I/O error"));
        now += 0.1; Call(w, "WriteLine", "5,x"); Call(good, "WriteLine", "5,y");
        Assert.AreEqual("Failed", WriterState(w)); Assert.AreEqual(5L, Get<long>(w, "RowsWritten"), "earlier rows are kept"); Assert.AreEqual(lastOk, Get<double>(w, "LastRowT"));
        Assert.AreEqual("Operational", WriterState(good)); Assert.AreEqual(6L, Get<long>(good, "RowsWritten"), "an independent stream keeps writing");

        // inside the cool-down nothing is attempted; each offered row is dropped
        now += 0.5; Call(w, "WriteLine", "6,x"); Call(w, "WriteLine", "7,x");
        Assert.AreEqual(2L, Get<long>(w, "RowsDroppedAfterFailure")); Assert.AreEqual(0, Get<int>(w, "RecoveryAttempts"));

        // after the cool-down a recovery attempt fails (still faulty) - a warning, not another error
        now += 2.5;
        LogAssert.Expect(LogType.Warning, new Regex("recovery attempt 1 failed"));
        Call(w, "WriteLine", "8,x");
        Assert.AreEqual(1, Get<int>(w, "RecoveryAttempts")); Assert.AreEqual("Failed", WriterState(w)); Assert.AreEqual(3L, Get<long>(w, "RowsDroppedAfterFailure"));

        // storage returns: next attempt succeeds, the partial last line is terminated, rows resume, the gap is reported
        CaptureIoFault(null); now += 2.5;
        LogAssert.Expect(LogType.Warning, new Regex("recovered after 2 attempt"));
        Call(w, "WriteLine", "9,x");
        Assert.AreEqual("Recovered", WriterState(w)); Assert.IsTrue(Get<bool>(w, "Recovered")); Assert.AreEqual(6L, Get<long>(w, "RowsWritten"), "5 before + the row after recovery");
        Assert.AreEqual(1L, Get<long>(w, "RowsFailed")); Assert.AreEqual(3L, Get<long>(w, "RowsDroppedAfterFailure"));
        Assert.AreEqual(Get<long>(w, "RowsAttempted"), Get<long>(w, "RowsWritten") + Get<long>(w, "RowsFailed") + Get<long>(w, "RowsDroppedAfterFailure"), "attempted = written + failed + dropped");
        var statuses = new List<string>(); foreach (var t in s_transitions) statuses.Add(Get<string>(t, "Status"));
        CollectionAssert.AreEqual(new[] { "failed", "recovery_failed", "recovered" }, statuses);
        var lines = File.ReadAllLines(bad); Assert.AreEqual("a,b", lines[0]); Assert.AreEqual(1, Array.FindAll(lines, l => l == "a,b").Length, "the header is not written twice");
        Assert.AreEqual("9,x", lines[lines.Length - 1]);

        // an unrecoverable stream stops after MaxRecoveryAttempts and says so
        var dead = NewWriter("dead.csv", "dead"); string deadPath = Path.Combine(_root, "dead.csv");
        Call(dead, "WriteLine", "1,1");
        CaptureIoFault((p, o) => p == deadPath && o == "open" ? Io("gone") : null);
        LogAssert.Expect(LogType.Error, new Regex("CSV stream disabled after I/O error"));
        Call(dead, "WriteLine", "2,2");
        for (int k = 1; k <= 3; k++)
        {
            now += 2.5;
            if (k < 3) LogAssert.Expect(LogType.Warning, new Regex("recovery attempt " + k + " failed")); else LogAssert.Expect(LogType.Error, new Regex("permanently failed"));
            Call(dead, "WriteLine", "x,x");
        }
        Assert.IsTrue(Get<bool>(dead, "PermanentlyFailed")); int attempts = Get<int>(dead, "RecoveryAttempts"); Assert.AreEqual(3, attempts);
        now += 10; Call(dead, "WriteLine", "y,y"); Assert.AreEqual(attempts, Get<int>(dead, "RecoveryAttempts"), "no further attempts, no log flood"); Assert.AreEqual("Failed", WriterState(dead));
        CaptureIoFault(null);
    }

    [UnityTest]
    public IEnumerator Stream_Failure_UpdatesTheManifest_WritesCaptureErrors_AndLeavesOtherStreamsRunning()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 2, maxAgents = 2, npcSpeed = 0.5f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 2, 10f, "NPCs");
        yield return new WaitForSecondsRealtime(0.5f);
        int stateRowsBefore = NpcState(rig).Rows.Count; int evBefore = Events(rig).Rows.Count; int npcEvBefore = NpcEvents(rig).Rows.Count;
        Assert.Greater(stateRowsBefore, 0);

        string statePath = rig.File("npc_state.csv"), errPath = rig.File("capture_errors.csv");
        CaptureIoFault((p, o) => p == statePath && o == "append" ? Io("device unavailable") : null);
        LogAssert.Expect(LogType.Error, new Regex("CSV stream disabled after I/O error \\(npc_state.csv\\)"));
        yield return new WaitForSecondsRealtime(0.5f);
        yield return null;
        var h = JsonUtility.FromJson<ManifestProbe>(File.ReadAllText(rig.File("session_manifest.json")));   // written by the failure checkpoint, not by the failed stream
        Assert.AreEqual("write_error", h.health_npc_state.status); Assert.AreEqual("failed", h.health_npc_state.writer_state); Assert.IsFalse(h.npc_state_ok);
        Assert.AreEqual(stateRowsBefore, (int)h.health_npc_state.rows_written, "rows written before the failure are kept"); Assert.Greater(h.health_npc_state.rows_failed + h.health_npc_state.rows_dropped_after_failure, 0L);
        Assert.AreEqual("append", h.health_npc_state.last_error_operation); Assert.AreEqual("IOException", h.health_npc_state.last_error_type); Assert.Greater(h.health_npc_state.first_error_t_abs, 0.0);
        Assert.IsTrue(h.health_npc_state.partial_output_possible);
        Assert.AreEqual("ok", h.health_npc_events.status); Assert.AreEqual("ok", h.health_events.status);
        Assert.Greater(Events(rig).Rows.Count, evBefore - 1); Assert.GreaterOrEqual(NpcEvents(rig).Rows.Count, npcEvBefore, "independent streams continue");
        bool sawState = false; foreach (var t in Get<IReadOnlyList<object>>(rig.dcm, "FailureRegistry")) if (Get<string>(t, "Stream") == "npc_state" && Get<string>(t, "Status") == "failed") sawState = true;
        Assert.IsTrue(sawState, "in-memory failure registry");

        // storage returns: the diagnostic channel works, and the stream recovers after its cool-down with the gap documented
        CaptureIoFault(null);
        double jump = 3.0; SetClock(() => Time.realtimeSinceStartupAsDouble + jump);
        LogAssert.Expect(LogType.Warning, new Regex("CSV stream 'npc_state' recovered"));
        yield return new WaitForSecondsRealtime(0.6f);
        Call(rig.dcm, "WriteManifestEnd", "after_failure");
        var h2 = JsonUtility.FromJson<ManifestProbe>(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual("recovered", h2.health_npc_state.status, "never silently back to ok"); Assert.IsFalse(h2.npc_state_ok);
        Assert.Greater(h2.health_npc_state.rows_written, (long)stateRowsBefore); Assert.AreEqual("recovered", h2.health_npc_state.writer_state);
        Assert.AreEqual(h2.health_npc_state.rows_attempted, h2.health_npc_state.rows_written + h2.health_npc_state.rows_failed + h2.health_npc_state.rows_dropped_after_failure);
        var errs = Csv.Load(errPath); AssertWidths(errs, "capture_errors.csv");
        var failedRows = Rows(errs, "status", "failed"); Assert.AreEqual(1, failedRows.Count, "one 'failed' row for the transition, not one per dropped row");
        Assert.AreEqual("npc_state", errs.Get(failedRows[0], "stream_name")); Assert.AreEqual("append", errs.Get(failedRows[0], "operation")); Assert.AreEqual("IOException", errs.Get(failedRows[0], "error_type"));
        Assert.AreEqual(stateRowsBefore, int.Parse(errs.Get(failedRows[0], "rows_written_before_failure")));
        var recovered = Rows(errs, "status", "recovered"); Assert.AreEqual(1, recovered.Count); Assert.AreEqual("npc_state", errs.Get(recovered[0], "stream_name"));
        foreach (var r in errs.Rows) Assert.AreEqual(Get<string>(rig.dcm, "SessionId"), errs.Get(r, "session_id"));
        AssertEventsCsvIsParticipantOnly(Events(rig));
        foreach (var r in Events(rig).Rows) Assert.AreNotEqual("capture_error", Events(rig).Get(r, "event_type"), "failures are not events.csv rows");
        foreach (var r in NpcEvents(rig).Rows) StringAssert.DoesNotContain("npc_state.csv", NpcEvents(rig).Get(r, "value_str"));
    }

    [UnityTest]
    public IEnumerator DiagnosticChannel_WhenItFailsToo_DoesNotRecurse_AndTheFailureIsStillRegisteredAndLogged()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.5f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        yield return new WaitForSecondsRealtime(0.4f);
        int before = NpcState(rig).Rows.Count; Assert.Greater(before, 0);
        string statePath = rig.File("npc_state.csv"), errPath = rig.File("capture_errors.csv");
        CaptureIoFault((p, o) => (p == statePath && o == "append") || (p == errPath && o == "append") ? Io("same disk") : null);
        LogAssert.Expect(LogType.Error, new Regex("CSV stream disabled after I/O error \\(npc_state.csv\\)"));
        LogAssert.Expect(LogType.Error, new Regex("CSV stream disabled after I/O error \\(capture_errors.csv\\)"));
        yield return new WaitForSecondsRealtime(0.5f);
        var h = JsonUtility.FromJson<ManifestProbe>(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual("write_error", h.health_npc_state.status, "the manifest (a different file path) still carries the failure");
        bool inRegistry = false; foreach (var t in Get<IReadOnlyList<object>>(rig.dcm, "FailureRegistry")) if (Get<string>(t, "Stream") == "npc_state") inRegistry = true;
        Assert.IsTrue(inRegistry, "in-memory registry holds it although no disk channel could");
        CaptureIoFault(null);
    }

    // ====================================================================================== F-14: identity

    [Test]
    public void Identity_ParticipantIdValidation_AcceptsRealFormats_RejectsPlaceholdersAndUnsafeText()
    {
        object V(string s) => T("SessionIdentity").GetMethod("ValidateParticipantId").Invoke(null, new object[] { s });
        string St(object r) => Get<object>(r, "status").ToString();
        foreach (var ok in new[] { "P012", "12", "A-07", "S 3", "p_9", "2024.17", "  P013  ", "ZZ-0001" }) Assert.AreEqual("Valid", St(V(ok)), ok);
        Assert.AreEqual("P013", Get<string>(V("  P013  "), "participantId"), "trimmed, otherwise exact");
        Assert.AreEqual("Missing", St(V(null))); Assert.AreEqual("Missing", St(V(""))); Assert.AreEqual("Missing", St(V("   \t ")));
        foreach (var ph in new[] { "0", "00", "P000", "p0000", "UNKNOWN", "unknown_subject", "Default", "NONE", "tbd" }) Assert.AreEqual("Placeholder", St(V(ph)), ph);
        foreach (var bad in new[] { "a/b", "a\\b", "a:b", "x\ny", "x\u0007", "a|b", "a*b", new string('x', 65) }) Assert.AreEqual("Invalid", St(V(bad)), "'" + bad + "'");
        Assert.AreEqual("", Get<string>(V("0"), "participantId") ?? "");
        StringAssert.DoesNotContain("P012", Get<string>(V("0"), "message"));
    }

    sealed class IdRig { public Component sc, dcm, em; public string root; }

    // A minimal session: controller (+ optional ExperimentManager, DataCaptureManager) created INACTIVE, configured, then activated - like a serialized scene.
    IdRig MakeIdRig(string participant, bool useJson = false, string jsonName = null, bool autoStart = false, bool withEm = false, bool withDcm = true, string root = null)
    {
        var r = new IdRig { root = root ?? Path.Combine(_root, "out_" + Guid.NewGuid().ToString("N").Substring(0, 6)) };
        Directory.CreateDirectory(r.root);
        var scGo = Make("IdController", active: false); r.sc = scGo.AddComponent(T("ExternalSessionController"));
        Set(r.sc, "participantId", participant); Set(r.sc, "autoStart", autoStart);
        Set(r.sc, "selectedPart", Enum.Parse(T("ExternalSessionController").GetNestedType("ExperimentPart"), "Part3"));
        if (useJson) { Set(r.sc, "useExternalJson", true); Set(r.sc, "externalFileName", jsonName); }
        if (withEm)
        {
            var emGo = Make("IdEm", active: false); r.em = emGo.AddComponent(T("ExperimentManager")); Set(r.em, "sessionController", r.sc);
        }
        LogAssert.ignoreFailingMessages = false;
        scGo.SetActive(true);
        if (withEm) r.em.gameObject.SetActive(true);
        if (withDcm) { var d = Make("IdDcm"); r.dcm = d.AddComponent(DcmType); Set(r.dcm, "_sessionController", r.sc); if (withEm) Set(r.dcm, "_experimentManager", r.em); Set(r.dcm, "outputRootOverride", r.root); Set(r.dcm, "writeFullBatiRegistry", false); Set(r.dcm, "enableDebugLogs", false); }
        return r;
    }

    static string JsonPath(string name) => Path.Combine(Application.persistentDataPath, name);

    [UnityTest]
    public IEnumerator Identity_InvalidIdentity_FailsClosed_NoFolder_NoFiles_NoReplay_NoAutoStart()
    {
        foreach (string id in new[] { "0", "P000", "", "   ", "bad/id" })
        {
            // NPC lifecycle events queued BEFORE the recorder decides must not survive an invalid identity
            CallStatic(BusType, "ResetForTests");
            var ev = Activator.CreateInstance(EvtType); EvtType.GetField("type").SetValue(ev, "prewarm_created");
            for (int i = 0; i < 4; i++) CallStatic(BusType, "Raise", ev);
            Assert.AreEqual(4, GetStatic<int>(BusType, "PendingCount"));

            LogAssert.Expect(LogType.Error, new Regex("Session configuration rejected"));
            LogAssert.Expect(LogType.Error, new Regex("RECORDING BLOCKED"));
            LogAssert.Expect(LogType.Error, new Regex("StartSelectedPart refused"));
            var r = MakeIdRig(id, autoStart: true, withEm: true);
            yield return null; yield return null;
            Assert.IsTrue(Get<bool>(r.dcm, "RecordingBlocked"), "'" + id + "'"); StringAssert.Contains("invalid participant/session configuration", Get<string>(r.dcm, "BlockReason"));
            Assert.AreEqual("", Get<string>(r.dcm, "SessionFolder")); Assert.AreEqual("", Get<string>(r.dcm, "SessionId"));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(r.root).Length, "no research folder, CSV or manifest under an invalid identity");
            Assert.AreEqual(0, GetStatic<int>(BusType, "PendingCount"), "queued NPC events were discarded, not kept for a later identity");
            Assert.IsFalse(Get<bool>(r.dcm, "NpcRecordingEnabled"));
            // auto-start was requested but the Part never began
            Assert.AreEqual("", Get<string>(r.em, "RunningPartName"), "no participant trial starts");
            Assert.IsNull(Get<object>(r.em, "CurrentPartConfig"));
            var res = Call(r.sc, "ValidateIdentity"); Assert.IsFalse(Get<bool>(res, "IsValid"));
            // a recorder started later is also refused (no legacy fall-back file)
            var rec = Make("G").AddComponent(RecorderType); Set(rec, "enableDebugLogs", false);
            LogAssert.Expect(LogType.Error, new Regex("StartRecording refused: recording is blocked"));
            Call(rec, "StartRecording"); Assert.IsFalse(Get<bool>(rec, "IsSampling"));
            TearDownRigObjects(); yield return null; yield return null;
        }
    }

    [UnityTest]
    public IEnumerator Identity_NoSessionController_IsAMissingIdentitySource_AndBlocks()
    {
        var d = Make("OrphanDcm", active: false); var dcm = d.AddComponent(DcmType);
        string root = Path.Combine(_root, "orphan"); Directory.CreateDirectory(root);
        Set(dcm, "outputRootOverride", root); Set(dcm, "enableDebugLogs", false);
        LogAssert.Expect(LogType.Error, new Regex("RECORDING BLOCKED"));
        d.SetActive(true);
        yield return null;
        Assert.IsTrue(Get<bool>(dcm, "RecordingBlocked")); Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
    }

    [UnityTest]
    public IEnumerator Identity_ExternalJson_IsStrict_NoSilentFallback_AndAStaleFileIsIgnoredWhenDisabled()
    {
        string name = "session_config_test_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json"; string jp = JsonPath(name);
        try
        {
            // (1) selected but missing: unresolved, even though the Inspector value is a perfectly valid ID
            LogAssert.Expect(LogType.Error, new Regex("Session configuration rejected \\(UnresolvedConfig\\)")); LogAssert.Expect(LogType.Error, new Regex("RECORDING BLOCKED"));
            var a = MakeIdRig("P-INSPECTOR-OK", useJson: true, jsonName: name);
            yield return null;
            Assert.AreEqual("UnresolvedConfig", Get<object>(Call(a.sc, "ValidateIdentity"), "status").ToString()); Assert.IsTrue(Get<bool>(a.dcm, "RecordingBlocked"));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(a.root).Length);
            TearDownRigObjects(); yield return null; yield return null;

            // (2) malformed / (3) missing required fields / unknown part / placeholder id / bad index
            var bads = new Dictionary<string, string>
            {
                { "malformed", "{ this is not json" },
                { "missing_field", "{\"participantId\":\"P050\",\"selectedPart\":\"Part2\",\"part2GroupIndex\":0}" },
                { "unknown_part", "{\"participantId\":\"P050\",\"selectedPart\":\"PartX\",\"part2GroupIndex\":0,\"part3GroupIndex\":0}" },
                { "placeholder", "{\"participantId\":\"P000\",\"selectedPart\":\"Part2\",\"part2GroupIndex\":0,\"part3GroupIndex\":0}" },
                { "bad_index", "{\"participantId\":\"P050\",\"selectedPart\":\"Part3\",\"part2GroupIndex\":0,\"part3GroupIndex\":12}" }
            };
            foreach (var kv in bads)
            {
                File.WriteAllText(jp, kv.Value);
                LogAssert.Expect(LogType.Error, new Regex("Session configuration rejected")); LogAssert.Expect(LogType.Error, new Regex("RECORDING BLOCKED"));
                var b = MakeIdRig("P-INSPECTOR-OK", useJson: true, jsonName: name);
                yield return null;
                Assert.IsFalse(Get<bool>(Call(b.sc, "ValidateIdentity"), "IsValid"), kv.Key); Assert.IsTrue(Get<bool>(b.dcm, "RecordingBlocked"), kv.Key);
                Assert.AreEqual("P-INSPECTOR-OK", Get<string>(b.sc, "participantId"), kv.Key + ": the Inspector value is neither used nor modified");
                Assert.AreEqual(0, Directory.GetFileSystemEntries(b.root).Length, kv.Key);
                TearDownRigObjects(); yield return null; yield return null;
            }

            // (4) valid external file wins over a different Inspector participant; hash + source recorded
            File.WriteAllText(jp, "{\"participantId\":\" P050 \",\"selectedPart\":\"Part2\",\"part2GroupIndex\":1,\"part3GroupIndex\":0,\"autoStart\":false}");
            var c = MakeIdRig("P-INSPECTOR-OTHER", useJson: true, jsonName: name);
            yield return null;
            Assert.IsTrue(Get<bool>(Call(c.sc, "ValidateIdentity"), "IsValid")); Assert.AreEqual("P050", Get<string>(c.sc, "participantId")); Assert.AreEqual("external_json", Get<string>(c.sc, "ConfigSource"));
            Assert.AreEqual(16, Get<string>(c.sc, "ConfigHash").Length);
            Assert.IsFalse(Get<bool>(c.dcm, "RecordingBlocked"));
            var man = JsonUtility.FromJson<IdManifest>(File.ReadAllText(Path.Combine(Get<string>(c.dcm, "SessionFolder"), "session_manifest.json")));
            Assert.AreEqual("P050", man.participant_id); Assert.AreEqual("external_json", man.config_source); Assert.AreEqual(Get<string>(c.sc, "ConfigHash"), man.config_hash); Assert.AreEqual(name, man.config_file_name);
            Assert.AreEqual("Part2", man.selected_part);
            TearDownRigObjects(); yield return null; yield return null;

            // (5) external mode OFF with a stale/unrelated file present: the file is ignored, the Inspector configuration is validated on its own
            File.WriteAllText(jp, "{\"participantId\":\"P099\",\"selectedPart\":\"Part1\",\"part2GroupIndex\":0,\"part3GroupIndex\":0}");
            var d = MakeIdRig("P-INSPECTOR-OK", useJson: false, jsonName: name);
            yield return null;
            Assert.AreEqual("P-INSPECTOR-OK", Get<string>(d.sc, "participantId")); Assert.AreEqual("inspector", Get<string>(d.sc, "ConfigSource")); Assert.AreEqual("", Get<string>(d.sc, "ConfigHash"));
            Assert.IsFalse(Get<bool>(d.dcm, "RecordingBlocked"));
            Assert.AreEqual("Part3", JsonUtility.FromJson<IdManifest>(File.ReadAllText(Path.Combine(Get<string>(d.dcm, "SessionFolder"), "session_manifest.json"))).selected_part, "not the stale file's Part1");
        }
        finally { try { File.Delete(jp); } catch { } }
    }
    [Serializable] public class IdManifest { public string participant_id, config_source, config_hash, config_file_name, selected_part, session_id, run_guid; }

    [UnityTest]
    public IEnumerator Session_Folders_AreUniquePerRun_IndependentOfParticipantText_AndNeverReused()
    {
        string root = Path.Combine(_root, "shared_out");
        // same participant, two sessions started in the same frame (same second) + a participant string that sanitises like another one
        var a = MakeIdRig("P 1", root: root); var b = MakeIdRig("P_1", root: root); var c = MakeIdRig("P 1", root: root);
        yield return null;
        var ids = new List<string>(); foreach (var r in new[] { a, b, c }) ids.Add(Get<string>(r.dcm, "SessionId"));
        CollectionAssert.AllItemsAreUnique(ids); foreach (var id in ids) StringAssert.IsMatch(@"^\d{8}T\d{6}Z_[0-9a-f]{12}$", id);
        foreach (var r in new[] { a, b, c }) { Assert.IsFalse(Get<bool>(r.dcm, "RecordingBlocked")); Assert.IsTrue(Directory.Exists(Get<string>(r.dcm, "SessionFolder"))); }
        Assert.AreEqual(3, Directory.GetDirectories(root).Length);
        string P(Rig_ r) => JsonUtility.FromJson<IdManifest>(File.ReadAllText(Path.Combine(Get<string>(r.dcm, "SessionFolder"), "session_manifest.json"))).participant_id;
        Assert.AreEqual("P 1", P(new Rig_(a))); Assert.AreEqual("P_1", P(new Rig_(b))); Assert.AreEqual("P 1", P(new Rig_(c)), "multiple sessions of one participant share the exact participant_id");
        var m = JsonUtility.FromJson<IdManifest>(File.ReadAllText(Path.Combine(Get<string>(a.dcm, "SessionFolder"), "session_manifest.json")));
        Assert.AreEqual(Get<string>(a.dcm, "SessionId"), m.session_id); StringAssert.EndsWith(m.run_guid.Substring(0, 12), m.session_id, "session_id is derived from the run GUID");
        TearDownRigObjects(); yield return null; yield return null;

        // forced collision (fixed clock + fixed GUID): the repeated draw is rejected and redrawn; when EVERY draw collides the session is refused, nothing is reused
        string root2 = Path.Combine(_root, "collide_out"); Directory.CreateDirectory(root2);
        var fixedNow = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc); string fixedGuid = "0123456789abcdef0123456789abcdef"; int calls = 0;
        var first = MakeIdRig("P 2", root: root2, withDcm: false);
        Component NewDcm(string n, Func<string> guids) { var g = Make(n, active: false); var d = g.AddComponent(DcmType); Set(d, "outputRootOverride", root2); Set(d, "enableDebugLogs", false); Set(d, "runGuidSource", guids); Set(d, "utcNowSource", (Func<DateTime>)(() => fixedNow)); Set(d, "_sessionController", first.sc); g.SetActive(true); return d; }
        var dA = NewDcm("DcmA", () => fixedGuid); yield return null;
        string folderA = Get<string>(dA, "SessionFolder"); Assert.IsFalse(Get<bool>(dA, "RecordingBlocked")); StringAssert.EndsWith("_0123456789ab", folderA);
        var dB = NewDcm("DcmB", () => (++calls <= 2) ? fixedGuid : "fedcba9876543210fedcba9876543210"); yield return null;
        Assert.IsFalse(Get<bool>(dB, "RecordingBlocked")); Assert.AreNotEqual(folderA, Get<string>(dB, "SessionFolder"));
        StringAssert.EndsWith("_fedcba987654", Get<string>(dB, "SessionFolder")); Assert.AreEqual(3, calls, "two colliding draws were rejected, the third accepted");
        LogAssert.Expect(LogType.Error, new Regex("RECORDING BLOCKED.*unique session folder"));
        var dC = NewDcm("DcmC", () => "fedcba9876543210fedcba9876543210"); yield return null;
        Assert.IsTrue(Get<bool>(dC, "RecordingBlocked"), "an existing session directory is never reused"); Assert.AreEqual(2, Directory.GetDirectories(root2).Length);
        Assert.AreEqual(0, Directory.GetFiles(Get<string>(dA, "SessionFolder"), "*.tmp").Length);
    }
    sealed class Rig_ { public Component dcm; public Rig_(IdRig r) { dcm = r.dcm; } }

    [UnityTest]
    public IEnumerator Identity_ValidSession_ReplaysEarlyPrewarmEvents_UnderTheValidatedIdentity()
    {
        // events raised before the recorder exists are queued, then replayed (original timestamps) only after the identity validated
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 3, maxAgents = 3, createDcm = false }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "NPCs without a recorder");
        Assert.Greater(GetStatic<int>(BusType, "PendingCount"), 0);
        var d = Make("LateDcm"); rig.dcm = d.AddComponent(DcmType); Set(rig.dcm, "outputRootOverride", _root); Set(rig.dcm, "writeFullBatiRegistry", false); Set(rig.dcm, "enableDebugLogs", false);
        yield return null; yield return new WaitForSecondsRealtime(0.2f);
        Assert.IsFalse(Get<bool>(rig.dcm, "RecordingBlocked")); Assert.AreEqual(3, CountType(NpcEvents(rig), "prewarm_created"));
        foreach (var r in NpcEvents(rig).Rows) Assert.AreEqual(Get<string>(rig.dcm, "SessionId"), NpcEvents(rig).Get(r, "session_id"));
    }

    // ====================================================================================== F-10: line of sight

    sealed class Scn
    {
        public GameObject root, npc, rig, head;
        public List<GameObject> all = new List<GameObject>();
        public GameObject Box(string n, Vector3 pos, Vector3 size, Transform parent = null, bool trigger = false)
        {
            var g = new GameObject(n); g.transform.SetParent(parent ?? root.transform, false); g.transform.position = pos;
            var b = g.AddComponent<BoxCollider>(); b.size = size; b.isTrigger = trigger; all.Add(g); return g;
        }
    }

    Scn NewScn(float distance = 10f)
    {
        var s = new Scn { root = new GameObject("Scn") };
        s.npc = new GameObject("Npc"); s.npc.transform.SetParent(s.root.transform, false);
        var cap = s.npc.AddComponent<CapsuleCollider>(); cap.center = new Vector3(0f, 0.9f, 0f); cap.height = 1.8f; cap.radius = 0.3f;   // root collider; the eye (y 1.62) is INSIDE it
        s.rig = new GameObject("PlayerRig"); s.rig.transform.SetParent(s.root.transform, false); s.rig.transform.position = new Vector3(0f, 0f, distance);
        s.head = new GameObject("CenterEyeAnchor"); s.head.transform.SetParent(s.rig.transform, false); s.head.transform.position = new Vector3(0f, 1.6f, distance);
        Physics.SyncTransforms();
        return s;
    }

    static LosStatusProbe Los(Scn s, Vector3? eye, Transform target, Transform pRoot = null)
    {
        Physics.SyncTransforms();
        object res = LosType.GetMethod("Query").Invoke(null, new object[] { eye, s.npc.transform, target, pRoot ?? s.rig.transform, ~0 });
        return new LosStatusProbe { name = res.ToString() };
    }
    sealed class LosStatusProbe { public string name; }
    static readonly Vector3 Eye = new Vector3(0f, 1.62f, 0f);

    [Test]
    public void Los_Semantics_ClearBlockedAndUnavailable_AreDistinct_AndSelfAndParticipantColliderAreNotObstacles()
    {
        // 1 clear; 3 NPC root collider contains the eye
        var s = NewScn();
        Assert.AreEqual("Clear", Los(s, Eye, s.head.transform).name);
        // 4 many child colliders of the NPC on the ray (head, torso, arms ... incl. deep children)
        var deep = new GameObject("limb"); deep.transform.SetParent(s.npc.transform, false); var deeper = new GameObject("hand"); deeper.transform.SetParent(deep.transform, false);
        s.Box("child1", new Vector3(0f, 1.6f, 0.25f), Vector3.one * 0.3f, s.npc.transform); s.Box("child2", new Vector3(0f, 1.6f, 0.6f), Vector3.one * 0.3f, deeper.transform);
        var sph = new GameObject("headSphere"); sph.transform.SetParent(s.npc.transform, false); sph.transform.position = new Vector3(0f, 1.62f, 0.1f); sph.AddComponent<SphereCollider>().radius = 0.15f;
        Assert.AreEqual("Clear", Los(s, Eye, s.head.transform).name, "self colliders at any depth are not obstacles");
        // 5 the participant's own head sphere and body capsule are the endpoint, not an obstacle
        var hs = new GameObject("HeadSphere"); hs.transform.SetParent(s.head.transform, false); hs.transform.position = s.head.transform.position + new Vector3(0, 0, -0.05f); hs.AddComponent<SphereCollider>().radius = 0.2f;
        var body = new GameObject("Body"); body.transform.SetParent(s.rig.transform, false); body.transform.position = new Vector3(0f, 0.9f, 9.5f); var bc = body.AddComponent<CapsuleCollider>(); bc.height = 3.4f; bc.radius = 0.3f;
        Assert.AreEqual("Clear", Los(s, Eye, s.head.transform).name, "participant colliders do not block");
        // 2 a real wall between
        var wall = s.Box("Wall", new Vector3(0f, 2f, 5f), new Vector3(20f, 6f, 0.5f));
        Assert.AreEqual("Blocked", Los(s, Eye, s.head.transform).name);
        // 6 an NPC child collider in FRONT of the wall does not mask it
        Assert.AreEqual("Blocked", Los(s, Eye, s.head.transform).name);
        UnityEngine.Object.DestroyImmediate(wall);
        // 9 trigger colliders are ignored (documented policy)
        s.Box("Trigger", new Vector3(0f, 2f, 5f), new Vector3(20f, 6f, 0.5f), null, trigger: true);
        Assert.AreEqual("Clear", Los(s, Eye, s.head.transform).name);
        // other NPCs DO occlude (they are physical obstacles); a collider that is NOT under this NPC's root
        var other = new GameObject("OtherNpc"); other.transform.SetParent(s.root.transform, false); other.transform.position = new Vector3(0f, 0.9f, 4f); var oc = other.AddComponent<CapsuleCollider>(); oc.height = 3.4f; oc.radius = 0.4f;
        Assert.AreEqual("Blocked", Los(s, Eye, s.head.transform).name);
        // 7 / 8 unavailable inputs are never "blocked"
        Assert.AreEqual("MissingParticipantTarget", Los(s, Eye, null).name);
        Assert.AreEqual("MissingNpcOrigin", Los(s, null, s.head.transform).name);
        Assert.AreEqual("InvalidGeometry", Los(s, s.head.transform.position, s.head.transform).name, "origin == target");
        UnityEngine.Object.DestroyImmediate(s.root);
    }

    [Test]
    public void Los_HitsAreNotAssumedSorted_AndABufferOverflowIsIndeterminateNotAGuess()
    {
        // 10: the genuine obstacle is created FIRST and lies FAR; many self / participant colliders are created later and lie nearer
        var s = NewScn();
        s.Box("FarWall", new Vector3(0f, 2f, 8f), new Vector3(20f, 6f, 0.2f));
        for (int i = 0; i < 20; i++) s.Box("self" + i, new Vector3(0f, 1.62f, 0.3f + i * 0.2f), new Vector3(0.1f, 0.1f, 0.1f), s.npc.transform);
        Assert.AreEqual("Blocked", Los(s, Eye, s.head.transform).name, "found regardless of return order");
        UnityEngine.Object.DestroyImmediate(s.root);

        // reverse creation order, wall NEAR
        s = NewScn();
        for (int i = 0; i < 20; i++) s.Box("self" + i, new Vector3(0f, 1.62f, 3f + i * 0.2f), new Vector3(0.1f, 0.1f, 0.1f), s.npc.transform);
        s.Box("NearWall", new Vector3(0f, 2f, 1f), new Vector3(20f, 6f, 0.2f));
        Assert.AreEqual("Blocked", Los(s, Eye, s.head.transform).name);
        UnityEngine.Object.DestroyImmediate(s.root);

        // 11: more self hits than the large buffer holds -> cannot be decided -> indeterminate, never "clear"
        s = NewScn(40f);
        for (int i = 0; i < 300; i++) s.Box("self" + i, new Vector3(0f, 1.62f, 0.2f + i * 0.1f), new Vector3(0.05f, 0.05f, 0.05f), s.npc.transform);
        Assert.AreEqual("HitBufferOverflow", Los(s, Eye, s.head.transform).name);
        UnityEngine.Object.DestroyImmediate(s.root);
        // 300 obstacles: an obstacle is among the returned hits -> conclusively blocked
        s = NewScn(40f);
        for (int i = 0; i < 300; i++) s.Box("obs" + i, new Vector3(0f, 1.62f, 0.2f + i * 0.1f), new Vector3(0.05f, 0.05f, 0.05f));
        Assert.AreEqual("Blocked", Los(s, Eye, s.head.transform).name);
        UnityEngine.Object.DestroyImmediate(s.root);
    }

    [Test]
    public void Los_NpcPrefabLikeHierarchy_NinePlusColliders_IsNoLongerSelfBlocked_WhileTheOldQueryWas()
    {
        // The shipped Npc.prefab has 9 CapsuleColliders, 1 SphereCollider and 2 BoxColliders, all non-trigger on layer Default, under a humanoid skeleton.
        var s = NewScn();
        for (int i = 0; i < 8; i++) { var g = new GameObject("limb" + i); g.transform.SetParent(s.npc.transform, false); g.transform.position = new Vector3((i % 2 == 0 ? -0.15f : 0.15f), 0.3f + 0.15f * i, 0.05f * i); var c = g.AddComponent<CapsuleCollider>(); c.radius = 0.12f; c.height = 0.5f; }
        var hd = new GameObject("Head"); hd.transform.SetParent(s.npc.transform, false); hd.transform.position = new Vector3(0f, 1.62f, 0.02f); hd.AddComponent<SphereCollider>().radius = 0.12f;
        for (int i = 0; i < 2; i++) s.Box("box" + i, new Vector3(0f, 1.1f + 0.4f * i, 0.1f), Vector3.one * 0.25f, s.npc.transform);
        Physics.SyncTransforms();
        int selfColliders = s.npc.GetComponentsInChildren<Collider>().Length; Assert.GreaterOrEqual(selfColliders, 12);

        // eye point fallback without an Animator: 90 % of the collider height (root capsule top 1.8 m) above the root
        object[] a = { s.npc.transform, null, null, null };
        Assert.IsTrue((bool)LosType.GetMethod("TryGetNpcEyePoint").Invoke(null, a)); var eye = (Vector3)a[3];
        Assert.AreEqual(1.62f, eye.y, 0.05f); Assert.AreEqual(0f, eye.x, 1e-4f);
        object[] none = { null, null, null, null }; Assert.IsFalse((bool)LosType.GetMethod("TryGetNpcEyePoint").Invoke(null, none), "no NPC transform -> no origin");

        Assert.AreEqual("Clear", Los(s, eye, s.head.transform).name, "all self colliders ignored");
        // the pre-F-10 query: a linecast from the participant's head to a point 1.6 m above the NPC pivot (INSIDE its own colliders)
        bool oldBlocked = Physics.Linecast(s.head.transform.position, s.npc.transform.position + Vector3.up * 1.6f, ~0, QueryTriggerInteraction.Ignore);
        Assert.IsTrue(oldBlocked, "the old query was blocked by the NPC's own collider on a completely clear line");
        s.Box("Wall", new Vector3(0f, 2f, 6f), new Vector3(20f, 6f, 0.5f));
        Assert.AreEqual("Blocked", Los(s, eye, s.head.transform).name);
        UnityEngine.Object.DestroyImmediate(s.root);
    }

    [UnityTest]
    public IEnumerator Los_InNpcState_ReportsClearBlockedAndBlank_WithStatus_AndTheManifestCountsThem()
    {
        var eyeGo = new GameObject("CenterEyeAnchor"); _spawned.Add(eyeGo);   // found by DataCaptureManager as the HMD
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 2, maxAgents = 2, npcSpeed = 0.3f, spawnInterval = 0.02f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 2, 10f, "NPCs");
        foreach (var pa in ActiveAgents(rig)) Call(pa.GetComponent(MoveType), "Stop");
        yield return new WaitForSecondsRealtime(0.3f);
        var npcs = ActiveAgents(rig); Vector3 p = npcs[0].transform.position;

        // A) clear: the participant's head 6 m away in the open (NPC colliders are in between only as the NPC's own)
        eyeGo.transform.position = p + new Vector3(6f, 1.6f, 0f); Physics.SyncTransforms();
        int a0 = NpcState(rig).Rows.Count; Call(rig.dcm, "SampleNpcs"); var st = NpcState(rig);
        int clearRows = 0; for (int i = a0; i < st.Rows.Count; i++) { var r = st.Rows[i]; if (st.Get(r, "npc_spawn_instance_id") == Lid(npcs[0])) { Assert.AreEqual("clear", st.Get(r, "los_status")); Assert.AreEqual("1", st.Get(r, "los_clear_to_participant")); clearRows++; } }
        Assert.AreEqual(1, clearRows);

        // B) blocked: a wall between this NPC and the head
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); _spawned.Add(wall); wall.transform.position = p + new Vector3(3f, 2f, 0f); wall.transform.localScale = new Vector3(0.3f, 6f, 6f); Physics.SyncTransforms();
        int b0 = NpcState(rig).Rows.Count; Call(rig.dcm, "SampleNpcs"); st = NpcState(rig);
        bool sawBlocked = false;
        for (int i = b0; i < st.Rows.Count; i++) { var r = st.Rows[i]; if (st.Get(r, "npc_spawn_instance_id") == Lid(npcs[0])) { Assert.AreEqual("blocked", st.Get(r, "los_status")); Assert.AreEqual("0", st.Get(r, "los_clear_to_participant")); sawBlocked = true; } }
        Assert.IsTrue(sawBlocked);
        UnityEngine.Object.DestroyImmediate(wall); Physics.SyncTransforms();

        // C) no participant head: blank, never 0
        UnityEngine.Object.DestroyImmediate(eyeGo); eyeGo = null;
        var centerEyeField = rig.dcm.GetType().GetField("_centerEye", All); centerEyeField.SetValue(rig.dcm, null);
        int c0 = NpcState(rig).Rows.Count; Call(rig.dcm, "SampleNpcs"); st = NpcState(rig);
        for (int i = c0; i < st.Rows.Count; i++) { Assert.AreEqual("", st.Get(st.Rows[i], "los_clear_to_participant")); Assert.AreEqual("missing_participant_target", st.Get(st.Rows[i], "los_status")); }
        AssertWidths(st, "npc_state.csv");

        Call(rig.dcm, "WriteManifestEnd", "test");
        var m = JsonUtility.FromJson<LosManifest>(File.ReadAllText(rig.File("session_manifest.json")));
        UnityEngine.Debug.Log($"[ReliabilityTests][los] controlled-scene LOS distribution over {m.npc_los_clear + m.npc_los_blocked + m.npc_los_unavailable} samples: clear={m.npc_los_clear} blocked={m.npc_los_blocked} unavailable={m.npc_los_unavailable} (missing_participant_target={m.npc_los_missing_participant_target})");
        Assert.Greater(m.npc_los_clear, 0L); Assert.Greater(m.npc_los_blocked, 0L); Assert.Greater(m.npc_los_unavailable, 0L); Assert.AreEqual(m.npc_los_unavailable, m.npc_los_missing_participant_target);
    }
    [Serializable] public class LosManifest { public long npc_los_clear, npc_los_blocked, npc_los_unavailable, npc_los_missing_participant_target; }
}
