// NpcAppliedProfile.cs
// Data-capture value types that describe what an AgentProfile ACTUALLY produced on one NPC: the realised appearance (which option, mesh and materials each
// body part ended up with after the weighted-random draws) plus the profile parameters that were in force. They are filled by AppearanceController /
// AgentFactory at apply time (observation only - no random draw, branch or value of the simulation depends on them) and frozen into an immutable
// NpcLifetimeProfile when a lifetime begins, so a pooled GameObject that is re-skinned later can never rewrite the record of an earlier lifetime.
//
// Identity of the profile CONTENT: AgentProfile is a ScriptableObject that can be edited between sessions, so a name alone does not say what was applied.
// AgentProfileSnapshot.Describe() produces a canonical, culture-independent description (names, weights, gender restrictions, speed options, scalars) and a
// non-cryptographic 64-bit FNV-1a content hash of it. DataCaptureManager stores the full description once per hash in npc_profiles.csv.
//
// All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>What one body part ended up with. Strings are formatted lazily (a prewarmed pool re-applies profiles thousands of times).</summary>
public sealed class AppliedPartRecord
{
    /// <summary>False when the AppearanceController has no renderer for this part (nothing is shown, nothing recorded).</summary>
    public bool hasRenderer;
    /// <summary>Index into the profile's option array of the option that was chosen. -1 = options exist but none is valid for the gender (renderer cleared);
    /// -2 = the profile offers no options for this part (the prefab's own mesh/material stays).</summary>
    public int optionIndex = -2;
    /// <summary>Mesh shown after application (renderer.sharedMesh). Null = none.</summary>
    public Mesh mesh;
    /// <summary>Material ASSETS applied per renderer slot (the chosen source materials, not the runtime "(Instance)" copies).</summary>
    public Material[] materials = Array.Empty<Material>();
    /// <summary>Per slot: index of the material inside the chosen option's material list that the random draw picked; -1 = slot kept its existing material.</summary>
    public int[] materialIndices = Array.Empty<int>();

    public string MeshName => mesh != null ? mesh.name : "";

    /// <summary>"name|name|..." one entry per renderer slot, in slot order. Blank entries mean an empty slot.</summary>
    public string MaterialNames
    {
        get
        {
            if (materials == null || materials.Length == 0) return "";
            var sb = new StringBuilder(48);
            for (int i = 0; i < materials.Length; i++)
            {
                if (i > 0) sb.Append('|');
                Material m = materials[i];
                if (m == null) continue;
                string n = m.name;
                if (n.EndsWith(" (Instance)", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 11);
                sb.Append(n);
            }
            return sb.ToString();
        }
    }

    public string OptionText => hasRenderer && optionIndex != -2 ? optionIndex.ToString(CultureInfo.InvariantCulture) : "";
}

/// <summary>The realised appearance of one ApplyProfile call. Immutable once published: AppearanceController creates a NEW instance per call.</summary>
public sealed class AppliedAppearance
{
    public double appliedTAbs;
    public AgentGender gender;
    public AppliedPartRecord body = new AppliedPartRecord(), hair = new AppliedPartRecord(), top = new AppliedPartRecord(),
                             bottom = new AppliedPartRecord(), shoes = new AppliedPartRecord(), accessories = new AppliedPartRecord();

    /// <summary>16-hex FNV-1a hash of the realised look (gender + every part's option, mesh and material names). Equal looks -> equal hash.</summary>
    public string LookHash()
    {
        var sb = new StringBuilder(256);
        sb.Append(gender).Append(';');
        AppendPart(sb, "body", body); AppendPart(sb, "hair", hair); AppendPart(sb, "top", top);
        AppendPart(sb, "bottom", bottom); AppendPart(sb, "shoes", shoes); AppendPart(sb, "accessories", accessories);
        return AgentProfileSnapshot.Fnv1a64Hex(sb.ToString());
    }

    static void AppendPart(StringBuilder sb, string name, AppliedPartRecord p)
        => sb.Append(name).Append('=').Append(p.hasRenderer ? p.OptionText : "-").Append('/').Append(p.MeshName).Append('/').Append(p.MaterialNames).Append(';');
}

/// <summary>Everything that is recorded once per NPC lifetime about the profile that was applied to it. Immutable.</summary>
public sealed class NpcLifetimeProfile
{
    public string profileName = "";
    public string profileContentHash = "";
    public float femaleProbability, uncertainProbability;
    public bool forceUncertain;
    /// <summary>Compact description of the UncertainBehavior parameters in force ("" when the prefab has no UncertainBehavior).</summary>
    public string uncertainConfig = "";
    public AppliedAppearance appearance;   // may be null when no AppearanceController exists / no profile was applied

    public static readonly NpcLifetimeProfile None = new NpcLifetimeProfile();
}

public static class AgentProfileSnapshot
{
    // Hash -> full JSON description (one entry per distinct profile content seen in this process). Read by DataCaptureManager for npc_profiles.csv.
    static readonly Dictionary<string, string> s_json = new Dictionary<string, string>();

    public static string JsonFor(string hash) => hash != null && s_json.TryGetValue(hash, out string j) ? j : "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { s_json.Clear(); }

    /// <summary>Builds the immutable per-lifetime record from what AgentFactory/AppearanceController applied. Called when a lifetime begins.</summary>
    public static NpcLifetimeProfile Capture(AgentProfile profile, AppliedAppearance appearance, UncertainBehavior uncertain)
    {
        var r = new NpcLifetimeProfile { appearance = appearance, uncertainConfig = DescribeUncertain(uncertain) };
        if (profile != null)
        {
            r.profileName = profile.name;
            r.femaleProbability = profile.femaleProbability;
            r.forceUncertain = profile.forceUncertain;
            r.uncertainProbability = profile.uncertainProbability;
            r.profileContentHash = Describe(profile, out string json);
            if (!s_json.ContainsKey(r.profileContentHash)) s_json[r.profileContentHash] = json;
        }
        return r;
    }

    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    public static string DescribeUncertain(UncertainBehavior u)
    {
        if (u == null) return "";
        return "pause_p_s=" + F(u.pauseProbPerSec) + ";hesitate_p_s=" + F(u.hesitateProbPerSec) + ";slow_p_s=" + F(u.slowProbPerSec) +
               ";detour_p_s=" + F(u.detourProbPerSec) + ";pause_s=" + F(u.pauseMin) + "-" + F(u.pauseMax) +
               ";hesitate_s=" + F(u.hesitateMin) + "-" + F(u.hesitateMax) + ";slow_s=" + F(u.slowMin) + "-" + F(u.slowMax) +
               ";slow_factor=" + F(u.slowFactor) + ";detour_radius_m=" + F(u.detourRadius) + ";detour_timeout_s=" + F(u.detourDuration) +
               ";look_around=" + (u.lookAroundEnabled ? "1" : "0");
    }

    /// <summary>Canonical content hash + JSON description of an AgentProfile (names only in the hash; asset GUIDs added to the JSON in the Editor).</summary>
    public static string Describe(AgentProfile p, out string json)
    {
        var sb = new StringBuilder(1024);   // canonical text that is hashed
        var js = new StringBuilder(2048);   // JSON for the registry

        js.Append("{\"name\":").Append(Q(p.name));
        Scalars(sb, js, p);
        js.Append(",\"speedOptions\":[");
        if (p.speedOptions != null)
            for (int i = 0; i < p.speedOptions.Length; i++)
            {
                var o = p.speedOptions[i]; if (o == null) continue;
                sb.Append("speedOpt=").Append(F(o.multiplier)).Append('/').Append(F(o.weight)).Append(';');
                if (js[js.Length - 1] != '[') js.Append(',');
                js.Append("{\"multiplier\":").Append(F(o.multiplier)).Append(",\"weight\":").Append(F(o.weight)).Append('}');
            }
        js.Append("],\"parts\":{");
        Part(sb, js, "body", p.bodyOptions, true);
        Part(sb, js, "hair", p.hairOptions, false);
        Part(sb, js, "top", p.topOptions, false);
        Part(sb, js, "bottom", p.bottomOptions, false);
        Part(sb, js, "shoes", p.shoesOptions, false);
        Part(sb, js, "accessories", p.accessoriesOptions, false);
        js.Append("}}");
        json = js.ToString();
        return Fnv1a64Hex(sb.ToString());
    }

    static void Scalars(StringBuilder sb, StringBuilder js, AgentProfile p)
    {
        sb.Append("speed=").Append(F(p.speed)).Append(";acc=").Append(F(p.acceleration)).Append(";ang=").Append(F(p.angularSpeed))
          .Append(";avoid=").Append(p.avoidancePriority.ToString(CultureInfo.InvariantCulture)).Append(";forceU=").Append(p.forceUncertain ? '1' : '0')
          .Append(";pU=").Append(F(p.uncertainProbability)).Append(";pF=").Append(F(p.femaleProbability))
          .Append(";r=").Append(F(p.agentRadius)).Append(";h=").Append(F(p.agentHeight)).Append(';');
        js.Append(",\"speed\":").Append(F(p.speed)).Append(",\"acceleration\":").Append(F(p.acceleration)).Append(",\"angularSpeed\":").Append(F(p.angularSpeed))
          .Append(",\"avoidancePriority\":").Append(p.avoidancePriority.ToString(CultureInfo.InvariantCulture))
          .Append(",\"forceUncertain\":").Append(p.forceUncertain ? "true" : "false").Append(",\"uncertainProbability\":").Append(F(p.uncertainProbability))
          .Append(",\"femaleProbability\":").Append(F(p.femaleProbability)).Append(",\"agentRadius\":").Append(F(p.agentRadius))
          .Append(",\"agentHeight\":").Append(F(p.agentHeight));
    }

    static void Part(StringBuilder sb, StringBuilder js, string name, AgentProfile.PartOption[] opts, bool first)
    {
        if (!first) js.Append(',');
        js.Append('"').Append(name).Append("\":[");
        sb.Append(name).Append('[');
        bool firstOpt = true;
        if (opts != null)
            for (int i = 0; i < opts.Length; i++)
            {
                var o = opts[i];
                if (!firstOpt) js.Append(',');
                firstOpt = false;
                if (o == null) { js.Append("null"); sb.Append("null;"); continue; }
                string mesh = o.mesh != null ? o.mesh.name : "";
                sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(':').Append(mesh).Append(':').Append(F(o.weight)).Append(':').Append((int)o.genderRestriction).Append(':');
                js.Append("{\"index\":").Append(i.ToString(CultureInfo.InvariantCulture)).Append(",\"mesh\":").Append(Q(mesh))
                  .Append(",\"weight\":").Append(F(o.weight)).Append(",\"genderRestriction\":").Append(Q(o.genderRestriction.ToString()));
                AssetIdentity(js, "mesh", o.mesh);
                js.Append(",\"materials\":[");
                if (o.materials != null)
                    for (int m = 0; m < o.materials.Length; m++)
                    {
                        string mn = o.materials[m] != null ? o.materials[m].name : "";
                        sb.Append(mn).Append(',');
                        if (m > 0) js.Append(',');
                        js.Append("{\"name\":").Append(Q(mn));
                        AssetIdentity(js, "", o.materials[m]);
                        js.Append('}');
                    }
                js.Append("]}");
                sb.Append(';');
            }
        sb.Append("];");
        js.Append(']');
    }

    // Editor-only: GUID + local file id make two assets with the same display name distinguishable. Not available in a player build, where the
    // option index + names (+ the content hash) identify the asset inside the committed profile.
    static void AssetIdentity(StringBuilder js, string prefix, UnityEngine.Object o)
    {
#if UNITY_EDITOR
        if (o == null) return;
        if (UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string guid, out long local))
            js.Append(",\"").Append(prefix.Length == 0 ? "guid" : prefix + "Guid").Append("\":").Append(Q(guid))
              .Append(",\"").Append(prefix.Length == 0 ? "localId" : prefix + "LocalId").Append("\":").Append(local.ToString(CultureInfo.InvariantCulture));
#endif
    }

    static string Q(string s)
    {
        if (s == null) return "\"\"";
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Non-cryptographic 64-bit FNV-1a hash of the UTF-16 code units, as 16 lower-case hex digits. Stable across platforms and runs.</summary>
    public static string Fnv1a64Hex(string s)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < s.Length; i++)
        {
            h ^= (ulong)(s[i] & 0xFF); h *= 1099511628211UL;
            h ^= (ulong)(s[i] >> 8); h *= 1099511628211UL;
        }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }
}
