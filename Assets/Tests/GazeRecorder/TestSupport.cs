// TestSupport.cs
// Shared reflection + CSV helpers for the PlayMode tests. The project's runtime scripts live in the predefined Assembly-CSharp, which an
// assembly definition cannot reference, so tests reach the PRODUCTION types by reflection. No production logic is re-implemented here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;

static class Reflect
{
    public const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static Type T(string name)
    {
        var t = Type.GetType(name + ", Assembly-CSharp");
        Assert.IsNotNull(t, "type not found in Assembly-CSharp: " + name);
        return t;
    }

    public static object Call(object o, string name, params object[] args)
    {
        var m = o.GetType().GetMethod(name, All);
        Assert.IsNotNull(m, "method not found: " + o.GetType().Name + "." + name);
        return m.Invoke(o, args);
    }

    public static object CallStatic(Type t, string name, params object[] args)
    {
        var m = t.GetMethod(name, All);
        Assert.IsNotNull(m, "static method not found: " + t.Name + "." + name);
        return m.Invoke(null, args);
    }

    public static T Get<T>(object o, string name)
    {
        var t = o.GetType();
        var f = t.GetField(name, All);
        if (f != null) return (T)f.GetValue(o);
        var p = t.GetProperty(name, All);
        Assert.IsNotNull(p, "member not found: " + t.Name + "." + name);
        return (T)p.GetValue(o);
    }

    public static T GetStatic<T>(Type t, string name)
    {
        var p = t.GetProperty(name, All);
        if (p != null) return (T)p.GetValue(null);
        var f = t.GetField(name, All);
        Assert.IsNotNull(f, "static member not found: " + t.Name + "." + name);
        return (T)f.GetValue(null);
    }

    public static void Set(object o, string name, object v)
    {
        var f = o.GetType().GetField(name, All);
        Assert.IsNotNull(f, "field not found: " + o.GetType().Name + "." + name);
        f.SetValue(o, v);
    }
}

// Reaches GazeRecorder.DiagnosticsPathFor (production type in Assembly-CSharp) without a compile-time reference.
static class GazeRecorderHandle { public static string DiagPath(string gazePath) => (string)Reflect.T("GazeRecorder").GetMethod("DiagnosticsPathFor").Invoke(null, new object[] { gazePath }); }

sealed class Csv
{
    public List<string> Header = new List<string>();
    public List<List<string>> Rows = new List<List<string>>();
    public bool Exists;

    public int Col(string name)
    {
        int i = Header.IndexOf(name);
        Assert.GreaterOrEqual(i, 0, "column missing: " + name);
        return i;
    }
    public string Get(List<string> row, string col) => row[Col(col)];

    public IEnumerable<List<string>> Where(string col, string value)
    {
        int c = Col(col);
        foreach (var r in Rows) if (r[c] == value) yield return r;
    }

    /// <summary>True when this view was built from a v17 gaze.csv + gaze_diagnostics.csv pair (see Load).</summary>
    public bool MergedV17;
    public List<string> PrimaryHeader = new List<string>(), DiagnosticHeader = new List<string>();

    /// <summary>The file exactly as written (no merging): use for schema / width assertions.</summary>
    public static Csv LoadRaw(string path)
    {
        var c = new Csv();
        if (!File.Exists(path)) return c;
        c.Exists = true;
        var all = Parse(File.ReadAllText(path));
        if (all.Count == 0) return c;
        c.Header = all[0];
        for (int i = 1; i < all.Count; i++) c.Rows.Add(all[i]);
        return c;
    }

    /// <summary>
    /// Loads a CSV. A schema-v17 gaze.csv that has its gaze_diagnostics.csv beside it is returned as ONE view (the offline reader's job): the diagnostics columns are appended row by row (the files are
    /// one-to-one, checked here through frame_index and t_abs), and the columns that v17 no longer stores but that are fully derivable (ray_origin_*, ray_dir_*, ray_is_eye_gaze, roi_hit_name) are
    /// reconstructed with the documented rules. Every other file, and every historical v11-v16 gaze.csv, is returned unchanged.
    /// </summary>
    public static Csv Load(string path)
    {
        var c = LoadRaw(path);
        if (!c.Exists || !c.Header.Contains("face_closure_valid") || !c.Header.Contains("t_abs")) return c;
        string diagPath = (string)typeof(GazeRecorderHandle).GetMethod("DiagPath").Invoke(null, new object[] { path });
        var d = LoadRaw(diagPath);
        if (!d.Exists) return c;
        c.MergedV17 = true; c.PrimaryHeader = new List<string>(c.Header); c.DiagnosticHeader = new List<string>(d.Header);
        Assert.AreEqual(c.Rows.Count, d.Rows.Count, "gaze_diagnostics.csv has exactly one row per gaze row");
        int fi = c.Header.IndexOf("frame_index"), ti = c.Header.IndexOf("t_abs"), dfi = d.Header.IndexOf("frame_index"), dti = d.Header.IndexOf("t_abs");
        var extra = new List<int>(); for (int k = 0; k < d.Header.Count; k++) if (!c.Header.Contains(d.Header[k])) extra.Add(k);
        for (int r = 0; r < c.Rows.Count; r++)
        {
            Assert.AreEqual(c.Rows[r][fi], d.Rows[r][dfi], "diagnostics row " + r + " belongs to the same frame");
            Assert.AreEqual(c.Rows[r][ti], d.Rows[r][dti], "diagnostics row " + r + " has the same t_abs");
            foreach (int k in extra) c.Rows[r].Add(d.Rows[r][k]);
        }
        foreach (int k in extra) c.Header.Add(d.Header[k]);
        // derived v16 columns
        int cv = c.Header.IndexOf("combined_valid"), hx = c.Header.IndexOf("hmd_world_x"), hq = c.Header.IndexOf("hmd_world_rot_x"), co = c.Header.IndexOf("combined_origin_x"), cd = c.Header.IndexOf("combined_dir_x"), hn = c.Header.IndexOf("hit_collider_name"), fo = c.Header.IndexOf("fallback_ray_origin_x");
        string[] ro = { "ray_origin_x", "ray_origin_y", "ray_origin_z", "ray_dir_x", "ray_dir_y", "ray_dir_z" };
        foreach (var n in ro) c.Header.Add(n); c.Header.Add("ray_is_eye_gaze"); c.Header.Add("roi_hit_name");
        foreach (var row in c.Rows)
        {
            bool eye = row[cv] == "1";
            if (eye) { for (int k = 0; k < 3; k++) row.Add(row[co + k]); for (int k = 0; k < 3; k++) row.Add(row[cd + k]); }
            else if (fo >= 0 && row[fo] != "") { for (int k = 0; k < 6; k++) row.Add(row[fo + k]); }   // the cast ray of a head-fallback row is recorded in the diagnostics (fallback_ray_origin_*, fallback_ray_dir_*)
            else if (row[hx] == "" || row[hq] == "") { for (int k = 0; k < 6; k++) row.Add(""); }   // nothing recorded and no rendered view: blank, never invented
            else
            {
                float F(int i) => float.Parse(row[i], System.Globalization.CultureInfo.InvariantCulture);
                var pos = new Vector3(F(hx), F(hx + 1), F(hx + 2)); var fwd = new Quaternion(F(hq), F(hq + 1), F(hq + 2), F(hq + 3)) * Vector3.forward;
                var f4 = System.Globalization.CultureInfo.InvariantCulture;
                row.Add(pos.x.ToString("F4", f4)); row.Add(pos.y.ToString("F4", f4)); row.Add(pos.z.ToString("F4", f4)); row.Add(fwd.x.ToString("F4", f4)); row.Add(fwd.y.ToString("F4", f4)); row.Add(fwd.z.ToString("F4", f4));
            }
            row.Add(eye ? "1" : "0"); row.Add(eye ? row[hn] : "");
        }
        return c;
    }

    // RFC 4180 parser (quoted fields may contain commas, quotes and line breaks).
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"' && sb.Length == 0) { quoted = true; any = true; }
            else if (ch == ',') { row.Add(sb.ToString()); sb.Length = 0; any = true; }
            else if (ch == '\r') { }
            else if (ch == '\n') { row.Add(sb.ToString()); sb.Length = 0; rows.Add(row); row = new List<string>(); any = false; }
            else { sb.Append(ch); any = true; }
        }
        if (any || sb.Length > 0) { row.Add(sb.ToString()); rows.Add(row); }
        return rows;
    }
}

/// <summary>Installs the ExperimentRngManager test seams (ConfigurationOverride / EntropyOverride) through reflection; the production types are not referenceable from this assembly.</summary>
static class RngSeams
{
    static Type Mgr => Reflect.T("ExperimentRngManager");
    static Type Cfg => Reflect.T("RngSeedConfiguration");

    /// <summary>The configuration the manager will resolve (replaces the scene lookup).</summary>
    public static void SetConfig(ulong seed, string source = "TEST")
    {
        object cfg = Activator.CreateInstance(Cfg, seed, source);
        var del = System.Linq.Expressions.Expression.Lambda(typeof(Func<>).MakeGenericType(Cfg), System.Linq.Expressions.Expression.Constant(cfg, Cfg)).Compile();
        Mgr.GetField("ConfigurationOverride").SetValue(null, del);
    }

    /// <summary>The configuration lookup throws (an unreadable / invalid configuration).</summary>
    public static void SetConfigThrowing(Exception ex)
    {
        var del = System.Linq.Expressions.Expression.Lambda(typeof(Func<>).MakeGenericType(Cfg), System.Linq.Expressions.Expression.Throw(System.Linq.Expressions.Expression.Constant(ex), Cfg)).Compile();
        Mgr.GetField("ConfigurationOverride").SetValue(null, del);
    }

    /// <summary>Replaces the OS entropy source: fill receives the 8-byte buffer (throw to simulate an entropy failure).</summary>
    public static void SetEntropy(Action<byte[]> fill) { Mgr.GetField("EntropyOverride").SetValue(null, fill); }

}
