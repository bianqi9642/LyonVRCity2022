// GazeRecorderTests.cs
// PlayMode tests for GazeRecorder fixes F-01 (pause/resume) and F-02 (culture-independent gaze.csv).
//
// The project's runtime scripts live in the predefined Assembly-CSharp, which an assembly definition cannot reference, so these
// tests reach the PRODUCTION GazeRecorder / ExperimentManager types through reflection. Nothing is re-implemented here: rows are
// written by the real SamplingLoop and read back from disk. All output goes to a unique temp directory (GazeRecorder.outputPathOverride),
// never to persistentDataPath. These tests simulate Unity's lifecycle callbacks; they are NOT a Meta Quest test.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class GazeRecorderTests
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static readonly Type RecorderType = Type.GetType("GazeRecorder, Assembly-CSharp");
    static readonly Type ExperimentType = Type.GetType("ExperimentManager, Assembly-CSharp");

    string _dir;
    readonly List<GameObject> _spawned = new List<GameObject>();

    [SetUp]
    public void SetUp()
    {
        Assert.IsNotNull(RecorderType, "GazeRecorder type not found in Assembly-CSharp");
        _dir = Path.Combine(Path.GetTempPath(), "GazeRecorderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var go in _spawned) if (go != null) UnityEngine.Object.Destroy(go);
        _spawned.Clear();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ------------------------------------------------------------------ reflection helpers

    static object Call(object o, string name, params object[] args)
    {
        var m = o.GetType().GetMethod(name, All);
        Assert.IsNotNull(m, "method not found: " + name);
        return m.Invoke(o, args);
    }
    static T Get<T>(object o, string name)
    {
        var t = o.GetType();
        var f = t.GetField(name, All);
        if (f != null) return (T)f.GetValue(o);
        var p = t.GetProperty(name, All);
        Assert.IsNotNull(p, "member not found: " + name);
        return (T)p.GetValue(o);
    }
    static void Set(object o, string name, object v)
    {
        var f = o.GetType().GetField(name, All);
        Assert.IsNotNull(f, "field not found: " + name);
        f.SetValue(o, v);
    }

    // ------------------------------------------------------------------ RFC 4180 parser

    static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"' && sb.Length == 0) { quoted = true; any = true; }
            else if (c == ',') { row.Add(sb.ToString()); sb.Length = 0; any = true; }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(sb.ToString()); sb.Length = 0; rows.Add(row); row = new List<string>(); any = false; }
            else { sb.Append(c); any = true; }
        }
        if (any || sb.Length > 0) { row.Add(sb.ToString()); rows.Add(row); }
        return rows;
    }

    List<List<string>> ReadRows(string path) =>
        File.Exists(path) ? ParseCsv(File.ReadAllText(path)) : new List<List<string>>();

    int DataRowCount(string path) => Math.Max(0, ReadRows(path).Count - 1);

    // ------------------------------------------------------------------ scene helpers

    // Recorder with a head transform at a fractional pose and a big collider 5.x m in front of it (fractional hit distance / point).
    Component MakeRecorder(string csvName, string objectName = "GazeTestWall")
    {
        var head = new GameObject("GazeTestHead");
        head.transform.SetPositionAndRotation(new Vector3(1.23456f, 2.34567f, -3.45678f), Quaternion.Euler(10f, 20f, 30f));
        _spawned.Add(head);

        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = objectName;
        wall.transform.SetPositionAndRotation(head.transform.position + head.transform.forward * 5.1234f, head.transform.rotation);
        wall.transform.localScale = new Vector3(200f, 200f, 0.1f);
        _spawned.Add(wall);

        var go = new GameObject("GazeTestRecorder");
        _spawned.Add(go);
        var rec = go.AddComponent(RecorderType);
        Set(rec, "headTransform", head.transform);
        Set(rec, "sampleRate", 100f);
        Set(rec, "enableDebugLogs", false);
        Set(rec, "subjectId", "TESTSUBJ");
        Set(rec, "trialId", "TESTTRIAL");
        Set(rec, "outputPathOverride", Path.Combine(_dir, csvName));
        return rec;
    }

    string PathOf(Component rec) => Get<string>(rec, "outputPathOverride");

    static IEnumerator WaitReal(float s) { yield return new WaitForSecondsRealtime(s); }

    // ====================================================================================== F-02

    [Test]
    public void FormatHelpers_AreCultureInvariant()
    {
        var saved = CultureInfo.CurrentCulture;
        var savedUi = CultureInfo.CurrentUICulture;
        try
        {
            var fr = new CultureInfo("fr-FR");
            Assert.AreEqual(",", fr.NumberFormat.NumberDecimalSeparator, "precondition: fr-FR uses a decimal comma on this runtime");

            float[] values = { 0f, 1.5f, -1234.56789f, 0.00004f, 123456.789f };
            foreach (var cultureName in new[] { "en-US", "fr-FR", "de-DE" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(cultureName);
                CultureInfo.CurrentUICulture = new CultureInfo(cultureName);
                foreach (float v in values)
                {
                    string f4 = Fmt("F4", typeof(float), v);
                    string f3 = Fmt("F3", typeof(float), v);
                    Assert.AreEqual(v.ToString("F4", CultureInfo.InvariantCulture), f4, cultureName + " F4 " + v);
                    Assert.AreEqual(v.ToString("F3", CultureInfo.InvariantCulture), f3, cultureName + " F3 " + v);
                    Assert.IsFalse(f4.Contains(",") || f3.Contains(","), cultureName + ": decimal comma leaked");
                }
                Assert.AreEqual("-12.3457", Fmt("F4", typeof(double), -12.34567d), cultureName + " double F4");
                Assert.AreEqual("-7", Fmt("Inv", typeof(long), -7L), cultureName + " negative integer");
                Assert.AreEqual("1700000000123", Fmt("Inv", typeof(long), 1700000000123L), cultureName + " unix ms");
                Assert.AreEqual("NaN", Fmt("F3", typeof(float), float.NaN), cultureName + " NaN");
            }

            // Evidence that the old, culture-dependent formatting really was unsafe in this runtime.
            CultureInfo.CurrentCulture = fr;
            Assert.AreEqual("1,5000", 1.5f.ToString("F4"), "old-style ToString(\"F4\") produces a decimal comma under fr-FR");
        }
        finally { CultureInfo.CurrentCulture = saved; CultureInfo.CurrentUICulture = savedUi; }
    }

    // Invokes one of GazeRecorder's private static formatting helpers (F3 / F4 / Inv) with an exact parameter type.
    static string Fmt(string name, Type argType, object arg)
    {
        var m = RecorderType.GetMethod(name, All, null, new[] { argType }, null);
        Assert.IsNotNull(m, "helper not found: " + name + "(" + argType.Name + ")");
        return (string)m.Invoke(null, new[] { arg });
    }

    static readonly string[] FractionalColumns =
    {
        "head_pos_x", "head_pos_y", "head_pos_z", "head_rot_x", "head_rot_y", "head_rot_z", "head_rot_w",
        "combined_origin_x", "combined_origin_y", "combined_origin_z", "combined_dir_x", "combined_dir_y", "combined_dir_z",
        "hit_point_x", "hit_point_y", "hit_point_z"
    };
    static readonly Regex Fixed4 = new Regex(@"^-?\d+\.\d{4}$");

    // Runs the real recorder for a few frames under the given culture and returns the parsed CSV (header + rows).
    IEnumerator RecordUnderCulture(string cultureName, string csvName, Action<List<List<string>>> done)
    {
        var saved = CultureInfo.CurrentCulture;
        var savedUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            CultureInfo.CurrentUICulture = new CultureInfo(cultureName);
            var rec = MakeRecorder(csvName);
            yield return null;                       // let Start() run and colliders register
            Call(rec, "StartRecording");
            yield return WaitReal(0.3f);
            Call(rec, "StopRecording");
            done(ReadRows(PathOf(rec)));
        }
        finally { CultureInfo.CurrentCulture = saved; CultureInfo.CurrentUICulture = savedUi; }
    }

    [UnityTest]
    public IEnumerator GazeRows_UnderEnUsAndFrFr_AreEquivalentAndMatchHeaderWidth()
    {
        List<List<string>> en = null, fr = null;
        yield return RecordUnderCulture("en-US", "en.csv", r => en = r);
        yield return RecordUnderCulture("fr-FR", "fr.csv", r => fr = r);

        foreach (var rows in new[] { en, fr })
        {
            Assert.GreaterOrEqual(rows.Count, 3, "expected a header plus several rows");
            int width = rows[0].Count;
            Assert.AreEqual(55, width, "header width changed (55 columns at HEAD; the schema must stay unchanged)");
            for (int i = 1; i < rows.Count; i++)
                Assert.AreEqual(width, rows[i].Count, "row " + i + " width differs from header");
        }

        var hdr = fr[0];
        CollectionAssert.AreEqual(en[0], hdr, "header must be identical under both cultures");
        foreach (var col in FractionalColumns)
        {
            int ci = hdr.IndexOf(col);
            Assert.GreaterOrEqual(ci, 0, col);
            for (int i = 1; i < fr.Count; i++)
            {
                Assert.IsTrue(Fixed4.IsMatch(fr[i][ci]), "fr-FR " + col + " = '" + fr[i][ci] + "'");
                Assert.AreEqual(en[1][ci], fr[i][ci], col + " must be identical for en-US and fr-FR (same geometry)");
            }
        }
        foreach (var col in new[] { "time_since_start_s", "t_abs" })
        {
            int ci = hdr.IndexOf(col);
            for (int i = 1; i < fr.Count; i++) Assert.IsTrue(Fixed4.IsMatch(fr[i][ci]), col + " = '" + fr[i][ci] + "'");
        }
        Assert.AreEqual("1.2346", fr[1][hdr.IndexOf("head_pos_x")]);
        Assert.AreEqual("1", fr[1][hdr.IndexOf("ray_did_hit")]);
        Assert.AreEqual("", fr[1][hdr.IndexOf("roi_hit_distance")], "legacy missing-value convention: blank when eye gaze invalid");
        Assert.AreEqual("0", fr[1][hdr.IndexOf("combined_valid")]);
        Assert.AreEqual("head_fallback", fr[1][hdr.IndexOf("gaze_ray_source")]);
        Assert.AreEqual("0.000", fr[1][hdr.IndexOf("left_confidence")]);
        foreach (var col in new[] { "left_confidence", "right_confidence", "combined_confidence" })
            Assert.IsFalse(fr[1][hdr.IndexOf(col)].Contains(","));
    }

    [UnityTest]
    public IEnumerator GazeRows_WithCsvSpecialCharactersInNames_KeepColumnCountAndValues()
    {
        var rec = MakeRecorder("special.csv", "Wall,\"A\"\nB");
        Set(rec, "subjectId", "P,\"1\"\nx");
        yield return null;
        Call(rec, "StartRecording");
        yield return WaitReal(0.2f);
        Call(rec, "StopRecording");

        var rows = ReadRows(PathOf(rec));
        Assert.GreaterOrEqual(rows.Count, 2);
        for (int i = 1; i < rows.Count; i++) Assert.AreEqual(rows[0].Count, rows[i].Count, "row " + i);
        Assert.AreEqual("P \"1\" x", rows[1][rows[0].IndexOf("subject_id")], "value preserved after CSV un-escaping");
        Assert.AreEqual("Wall \"A\" B", rows[1][rows[0].IndexOf("hit_object_name")]);
        Assert.AreEqual("Wall \"A\" B", rows[1][rows[0].IndexOf("hit_collider_name")]);
    }

    // ====================================================================================== F-01

    // Drives the real trial path: ExperimentManager.BeginTrial/EndTrial raise OnTrialStart/OnTrialEnd, which GazeRecorder subscribes to.
    sealed class Rig { public Component rec; public Component em; public string path; }

    IEnumerator MakeRig(Action<Rig> done)
    {
        var emGo = new GameObject("GazeTestExperimentManager");
        _spawned.Add(emGo);
        var em = emGo.AddComponent(ExperimentType);
        var rec = MakeRecorder("lifecycle.csv");
        Set(rec, "experimentManager", em);
        yield return null;                           // GazeRecorder.Start() subscribes to OnTrialStart / OnTrialEnd
        done(new Rig { rec = rec, em = em, path = PathOf(rec) });
    }

    static bool Sampling(Component rec) => Get<bool>(rec, "IsSampling");
    static void Pause(Component rec, bool p) => Call(rec, "OnApplicationPause", p);

    [UnityTest]
    public IEnumerator PauseResume_RestartsSamplingOnlyWhileTrialIsOpen_WithoutDuplicates()
    {
        Rig rig = null;
        yield return MakeRig(r => rig = r);
        Component rec = rig.rec;

        Assert.IsFalse(Sampling(rec), "no sampling before the trial starts");
        Pause(rec, true); Pause(rec, false);
        Assert.IsFalse(Sampling(rec), "pause/resume before a trial must not start recording");

        // Trial start -> rows written.
        Call(rig.em, "BeginTrial");
        yield return WaitReal(0.25f);
        Assert.IsTrue(Sampling(rec));
        int c0 = DataRowCount(rig.path);
        Assert.Greater(c0, 2, "rows are written during the trial");

        for (int cycle = 1; cycle <= 3; cycle++)
        {
            Pause(rec, true);
            Assert.IsFalse(Sampling(rec), "sampling stopped on pause");
            Assert.IsTrue(Get<bool>(rec, "IsSuspendedByPause"));
            int cPaused = DataRowCount(rig.path);
            yield return WaitReal(0.2f);
            Assert.AreEqual(cPaused, DataRowCount(rig.path), "no rows while suspended (cycle " + cycle + ")");

            Pause(rec, false);
            Call(rec, "OnApplicationFocus", true);   // extra callback in the other order: must not create a second loop
            Pause(rec, false);
            Assert.IsTrue(Sampling(rec), "sampling resumed (cycle " + cycle + ")");
            Assert.IsFalse(Get<bool>(rec, "IsSuspendedByPause"));
            yield return WaitReal(0.25f);
            Assert.Greater(DataRowCount(rig.path), cPaused, "new rows after resume (cycle " + cycle + ")");
        }

        // One loop only, one header, relative-time origin preserved across the pauses.
        var rows = ReadRows(rig.path);
        var h = rows[0];
        int headerLines = 0;
        foreach (var line in File.ReadAllLines(rig.path)) if (line.StartsWith("time_since_start_s,")) headerLines++;
        Assert.AreEqual(1, headerLines, "exactly one header line");

        int iFrame = h.IndexOf("frame_index"), iAbs = h.IndexOf("t_abs"), iRel = h.IndexOf("time_since_start_s");
        int prevFrame = -1; double minO = double.MaxValue, maxO = double.MinValue, prevAbs = -1;
        for (int i = 1; i < rows.Count; i++)
        {
            Assert.AreEqual(h.Count, rows[i].Count);
            int fr = int.Parse(rows[i][iFrame], CultureInfo.InvariantCulture);
            Assert.Greater(fr, prevFrame, "duplicate sampling loops would repeat a frame_index (row " + i + ")");
            prevFrame = fr;
            double abs = double.Parse(rows[i][iAbs], CultureInfo.InvariantCulture);
            Assert.GreaterOrEqual(abs, prevAbs); prevAbs = abs;
            double o = abs - double.Parse(rows[i][iRel], CultureInfo.InvariantCulture);
            minO = Math.Min(minO, o); maxO = Math.Max(maxO, o);
        }
        Assert.Less(maxO - minO, 0.001, "time_since_start_s origin (trial start) must not move on resume");

        // Trial end -> no restart afterwards.
        Call(rig.em, "EndTrial");
        Assert.IsFalse(Sampling(rec));
        int cEnd = DataRowCount(rig.path);
        Pause(rec, true); Pause(rec, false); Call(rec, "OnApplicationFocus", true);
        yield return WaitReal(0.2f);
        Assert.IsFalse(Sampling(rec), "resume after trial end must not restart sampling");
        Assert.AreEqual(cEnd, DataRowCount(rig.path));
    }

    [UnityTest]
    public IEnumerator TrialEndingWhilePaused_IsNotRestartedByResume()
    {
        Rig rig = null;
        yield return MakeRig(r => rig = r);
        Call(rig.em, "BeginTrial");
        yield return WaitReal(0.15f);
        Pause(rig.rec, true);
        Call(rig.em, "EndTrial");                    // trial ends while the app is paused
        int c = DataRowCount(rig.path);
        Pause(rig.rec, false);
        yield return WaitReal(0.2f);
        Assert.IsFalse(Sampling(rig.rec));
        Assert.IsFalse(Get<bool>(rig.rec, "IsSuspendedByPause"));
        Assert.AreEqual(c, DataRowCount(rig.path));
    }

    [UnityTest]
    public IEnumerator EndedSession_AndQuit_PermanentlyPreventRestart()
    {
        Rig rig = null;
        yield return MakeRig(r => rig = r);
        Component rec = rig.rec;

        // Session ended while a (hypothetically) open, suspended recording exists: resume must not restart it.
        Call(rig.em, "BeginTrial");
        yield return WaitReal(0.15f);
        Pause(rec, true);
        ExperimentType.GetField("<SessionEnded>k__BackingField", All).SetValue(rig.em, true);
        int c = DataRowCount(rig.path);
        Pause(rec, false);
        yield return WaitReal(0.2f);
        Assert.IsFalse(Sampling(rec), "resume after SessionEnded must not restart sampling");
        Assert.AreEqual(c, DataRowCount(rig.path));
        Call(rec, "StartRecording");
        Assert.IsFalse(Sampling(rec), "StartRecording is ignored after the session ended");
        Call(rec, "StopRecording");
        ExperimentType.GetField("<SessionEnded>k__BackingField", All).SetValue(rig.em, false);

        // Quit / destroy marks the recorder terminated for good.
        Call(rec, "StartRecording");
        Assert.IsTrue(Sampling(rec));
        Pause(rec, true);
        Call(rec, "OnApplicationQuit");
        Pause(rec, false);
        Call(rec, "OnApplicationFocus", true);
        Call(rec, "StartRecording");
        yield return WaitReal(0.1f);
        Assert.IsFalse(Sampling(rec), "a terminated recorder never restarts");
    }
}
