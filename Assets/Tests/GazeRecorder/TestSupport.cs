// TestSupport.cs
// Shared reflection + CSV helpers for the PlayMode tests. The project's runtime scripts live in the predefined Assembly-CSharp, which an
// assembly definition cannot reference, so tests reach the PRODUCTION types by reflection. No production logic is re-implemented here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;

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

    public static Csv Load(string path)
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
