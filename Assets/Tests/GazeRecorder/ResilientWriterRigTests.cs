// ResilientWriterRigTests.cs
// Integration tests of the resilient gaze_diagnostics.csv writer with the REAL GazeRecorder and DataCaptureManager (same rig as the other NpcLifecycleTests): a file-sharing lock on the diagnostics file
// is injected through CaptureIO.FaultInjector; gaze.csv must keep recording, the diagnostics rows must be recovered (or the loss counted), and session_manifest.json must say so in the diagnostics stream's
// OWN health block without touching health_gaze. Editor / scratch tests - not Quest tests. Real-time waits are short (the policy's back-off starts at 0.5 s).

using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    [Serializable] public class StreamProbe
    {
        public string status = "", writer_state = "", last_error_kind = "", retry_policy = "";
        public long rows_written, rows_lost, rows_pending, rows_recovered_from_buffer, rows_attempted, rows_lost_at_close;
        public int outage_episodes;
        public bool resilient_writer;
        public double longest_outage_s = -1, recovered_t_abs = -1, first_error_t_abs = -1;
    }
    [Serializable] public class DiagManifestProbe
    {
        public bool gaze_ok;
        public StreamProbe health_gaze = new StreamProbe(), health_gaze_diagnostics = new StreamProbe();
        public string[] capture_streams_failed = new string[0], capture_streams_write_degraded = new string[0], capture_streams_write_failed = new string[0];
    }

    IEnumerator StartGazeOnRig(Action<Rig, object> done, Action<object> configure)
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.1f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        var head = Make("Head"); head.transform.position = new Vector3(0f, 0.9f, -3f); head.transform.rotation = Quaternion.identity;
        Call(rig.em, "BeginTrial");
        var recGo = Make("Gaze"); var rec = recGo.AddComponent(RecorderType);
        Set(rec, "headTransform", head.transform); Set(rec, "sampleRate", 20f); Set(rec, "enableDebugLogs", false); Set(rec, "experimentManager", rig.em);
        configure?.Invoke(rec);
        yield return null;
        Call(rec, "StartRecording");
        done(rig, rec);
    }

    static IEnumerator WaitReal(float seconds) { float until = Time.realtimeSinceStartup + seconds; while (Time.realtimeSinceStartup < until) yield return null; }

    DiagManifestProbe ReadDiagManifest(Rig rig, string reason)
    {
        Call(rig.dcm, "WriteManifestEnd", reason);
        return JsonUtility.FromJson<DiagManifestProbe>(File.ReadAllText(rig.File("session_manifest.json")));
    }

    [UnityTest]
    public IEnumerator DiagnosticsLock_OnTheRealRecorder_IsRecovered_GazeContinues_AndTheManifestRecordsTheRecovery()
    {
        Rig rig = null; object rec = null;
        yield return StartGazeOnRig((r, c) => { rig = r; rec = c; }, null);
        yield return WaitReal(0.5f);
        string diagPath = rig.File("gaze_diagnostics.csv");
        Assert.IsTrue(File.Exists(diagPath), "diagnostics rows are being written");
        long gazeBefore = Get<long>(Get<object>(rec, "Writer"), "RowsWritten");

        CaptureIoFault((p, o) => p == diagPath && o == "open" ? new IOException("Sharing violation on path " + p) : null);
        LogAssert.Expect(LogType.Error, new Regex(@"CSV stream disabled after I/O error \(gaze_diagnostics\.csv\)"));
        yield return WaitReal(1.2f);                                   // about 24 gaze rows while the diagnostics file is locked
        var diagW = Get<object>(rec, "DiagnosticsWriter");
        Assert.AreEqual("Failed", WriterState(diagW)); Assert.Greater(Get<long>(diagW, "PendingRows"), 0L, "the failed row and its successors are queued, not dropped");
        Assert.Greater(Get<long>(Get<object>(rec, "Writer"), "RowsWritten"), gazeBefore, "gaze.csv kept recording during the diagnostics outage");
        Assert.AreEqual("Operational", WriterState(Get<object>(rec, "Writer")), "the primary stream is independent");

        CaptureIoFault(null);                                          // the lock is released
        yield return WaitReal(2.0f);                                   // the next back-off attempt succeeds
        Call(rec, "StopRecording");
        Assert.AreEqual("Recovered", WriterState(diagW)); Assert.AreEqual(0L, Get<long>(diagW, "RowsLost"));

        var gaze = Csv.Load(rig.File("gaze.csv")); var diag = Csv.Load(diagPath);
        Assert.Greater(gaze.Rows.Count, 20);
        Assert.AreEqual(gaze.Rows.Count, diag.Rows.Count, "one diagnostics row per gaze row, none lost");
        for (int i = 0; i < gaze.Rows.Count; i++)
            Assert.AreEqual(gaze.Get(gaze.Rows[i], "frame_index"), diag.Get(diag.Rows[i], "frame_index"), "row " + i + ": same frame, same order, exactly once");

        var m = ReadDiagManifest(rig, "after_diag_recovery");
        Assert.AreEqual("recovered", m.health_gaze_diagnostics.status); Assert.AreEqual("recovered", m.health_gaze_diagnostics.writer_state);
        Assert.IsTrue(m.health_gaze_diagnostics.resilient_writer); Assert.AreEqual(0L, m.health_gaze_diagnostics.rows_lost); Assert.AreEqual(0L, m.health_gaze_diagnostics.rows_pending);
        Assert.Greater(m.health_gaze_diagnostics.rows_recovered_from_buffer, 0L); Assert.AreEqual(1, m.health_gaze_diagnostics.outage_episodes);
        Assert.AreEqual("SharingViolation", m.health_gaze_diagnostics.last_error_kind); Assert.Greater(m.health_gaze_diagnostics.first_error_t_abs, 0.0); Assert.Greater(m.health_gaze_diagnostics.recovered_t_abs, m.health_gaze_diagnostics.first_error_t_abs);
        Assert.AreEqual("ok", m.health_gaze.status, "gaze.csv is not marked failed because diagnostics failed"); Assert.IsTrue(m.gaze_ok);
        CollectionAssert.Contains(m.capture_streams_write_degraded, "gaze_diagnostics"); CollectionAssert.DoesNotContain(m.capture_streams_write_degraded, "gaze");
        CollectionAssert.IsEmpty(m.capture_streams_write_failed, "recovered without loss is degraded history, not a failed stream");
    }

    [UnityTest]
    public IEnumerator DiagnosticsLock_PastTheBudget_IsReportedAsFailedInItsOwnStream_WhileGazeStaysOk()
    {
        Rig rig = null; object rec = null;
        yield return StartGazeOnRig((r, c) => { rig = r; rec = c; }, c => { Set(c, "diagnosticsRetryMaxSeconds", 1f); Set(c, "diagnosticsMaxPendingRows", 100); });
        yield return WaitReal(0.4f);
        string diagPath = rig.File("gaze_diagnostics.csv");
        CaptureIoFault((p, o) => p == diagPath && o == "open" ? new IOException("Sharing violation on path " + p) : null);
        LogAssert.Expect(LogType.Error, new Regex(@"CSV stream disabled after I/O error \(gaze_diagnostics\.csv\)"));
        LogAssert.Expect(LogType.Error, new Regex(@"CSV stream 'gaze_diagnostics' permanently failed"));
        yield return WaitReal(2.6f);                                   // attempts at +0.5 s and +1.5 s: the 1 s budget is exhausted
        long gazeNow = Get<long>(Get<object>(rec, "Writer"), "RowsWritten");
        yield return WaitReal(0.5f);
        Assert.Greater(Get<long>(Get<object>(rec, "Writer"), "RowsWritten"), gazeNow, "gaze.csv is still recording after the diagnostics stream gave up");
        Call(rec, "StopRecording"); CaptureIoFault(null);

        var m = ReadDiagManifest(rig, "after_diag_failure");
        Assert.AreEqual("write_error", m.health_gaze_diagnostics.status); Assert.AreEqual("permanently_failed", m.health_gaze_diagnostics.writer_state);
        Assert.Greater(m.health_gaze_diagnostics.rows_lost, 0L); Assert.AreEqual(0L, m.health_gaze_diagnostics.rows_pending);
        Assert.AreEqual(m.health_gaze_diagnostics.rows_attempted, m.health_gaze_diagnostics.rows_written + m.health_gaze_diagnostics.rows_lost + m.health_gaze_diagnostics.rows_pending, "no row is unaccounted for");
        Assert.AreEqual("ok", m.health_gaze.status); Assert.IsTrue(m.gaze_ok);
        CollectionAssert.Contains(m.capture_streams_write_failed, "gaze_diagnostics"); CollectionAssert.DoesNotContain(m.capture_streams_write_failed, "gaze");
    }

    [Test]
    public void OldManifests_WithoutTheNewKeys_StillParse_WithNeutralDefaults()
    {
        var m = JsonUtility.FromJson<DiagManifestProbe>("{\"gaze_ok\": true, \"health_gaze\": {\"status\": \"ok\", \"rows_written\": 5}, \"capture_streams_failed\": []}");
        Assert.IsTrue(m.gaze_ok); Assert.AreEqual("ok", m.health_gaze.status);
        Assert.AreEqual("", m.health_gaze_diagnostics.status); Assert.AreEqual(0L, m.health_gaze_diagnostics.rows_lost); CollectionAssert.IsEmpty(m.capture_streams_write_failed);
    }
}
