// AccessoryWeightAdjuster.cs
// Part 2 accessory (wristband) distribution matching.
//
// WHAT IT DOES
//   In Part 3 each NPC is an attractor NPC with probability a (the effective Part 3 attractor probability) and a wandering NPC otherwise; a wandering NPC never
//   wears the accessory and an attractor NPC draws it from its Agent Profile's accessoriesOptions. So for one profile the overall Part 3 probability of accessory
//   option i is a * w_i / W. This class builds the weights a Part 2 NPC should use so that its probability of option i is the same number:
//       wristband option i (an option with a mesh):   w'_i    = a * w_i
//       the "None" option (no mesh, no materials):    w'_none = W - sum(w'_i)          (W = sum of all original weights, so the total is unchanged)
//   a = 1 gives the original weights, a = 0 puts all the weight on None.
//
// DESIGN RULES
//   * NON-DESTRUCTIVE: the shared AgentProfile asset is never touched. GetOptions returns either the ORIGINAL array (a >= 1, or the profile does not follow the
//     expected structure) or a COPY whose PartOption objects share the original mesh / materials references and carry the adjusted weight. The copy keeps the
//     element order, so an option index recorded from it is the index in the profile.
//   * NO COMPOUNDING: every result is computed from the original weights; the one-entry cache is validated against the current original weights, so editing the
//     profile or changing a never reuses a stale or already-scaled array.
//   * NO RANDOM DRAW: only weights are computed. The profile's existing weighted selector (AgentProfile.ChooseWeightedOption) does the single draw.
//   * SAFE STRUCTURE CHECK: the profile must have exactly one None option (mesh null and no materials), at least one wristband option, only Unisex non-null options
//     and finite non-negative weights with a positive total. Otherwise the original weights are used and a warning is logged once per profile (never silently
//     applying index-based logic to a differently structured profile).
// Pure computation apart from the warning; no scene access. Used by AppearanceController.ApplyProfile for Part 2 spawns only.

using System.Collections.Generic;
using UnityEngine;

public static class AccessoryWeightAdjuster
{
    // Clamp to 0..1 (NaN counts as 1 = no adjustment).
    public static float SanitizeScale(float a) => float.IsNaN(a) ? 1f : Mathf.Clamp01(a);

    /// <summary>
    /// The adjusted weights for original weights w (negative values count as 0), the index of the None element and a: wristband elements a * w_i, None gets the
    /// remainder so the total is unchanged. Pure math; a is sanitised to 0..1.
    /// </summary>
    public static float[] AdjustedWeights(float[] original, int noneIndex, float a)
    {
        a = SanitizeScale(a);
        var result = new float[original.Length];
        double total = 0.0, scaled = 0.0;
        for (int i = 0; i < original.Length; i++) total += Mathf.Max(0f, original[i]);
        for (int i = 0; i < original.Length; i++)
        {
            if (i == noneIndex) continue;
            result[i] = (float)(a * Mathf.Max(0f, original[i]));
            scaled += result[i];
        }
        result[noneIndex] = a >= 1f ? Mathf.Max(0f, original[noneIndex]) : (float)System.Math.Max(0.0, total - scaled);
        return result;
    }

    /// <summary>
    /// True when options follow the structure the rule needs: all options non-null and Unisex, finite non-negative weights with a positive total, at least one
    /// wristband option (mesh set) and exactly one None option (no mesh, no materials). noneIndex is that option's index.
    /// </summary>
    public static bool TryFindNone(AgentProfile.PartOption[] options, out int noneIndex, out string reason)
    {
        noneIndex = -1; reason = null;
        if (options == null || options.Length < 2) { reason = "fewer than two accessory options"; return false; }
        int wristbands = 0; double total = 0.0;
        for (int i = 0; i < options.Length; i++)
        {
            var o = options[i];
            if (o == null) { reason = "option " + i + " is null"; return false; }
            if (o.genderRestriction != GenderRestriction.Unisex) { reason = "option " + i + " is not Unisex"; return false; }
            if (float.IsNaN(o.weight) || float.IsInfinity(o.weight) || o.weight < 0f) { reason = "option " + i + " has an invalid weight"; return false; }
            total += o.weight;
            bool none = o.mesh == null && (o.materials == null || o.materials.Length == 0);
            if (none)
            {
                if (noneIndex >= 0) { reason = "more than one None option (" + noneIndex + " and " + i + ")"; return false; }
                noneIndex = i;
            }
            else if (o.mesh != null) wristbands++;
            else { reason = "option " + i + " has materials but no mesh"; return false; }
        }
        if (noneIndex < 0) { reason = "no None option"; return false; }
        if (wristbands == 0) { reason = "no wristband option"; return false; }
        if (!(total > 0.0)) { reason = "total weight is not positive"; return false; }
        return true;
    }

    // One-entry cache, validated against the current ORIGINAL weights on every call.
    private static AgentProfile.PartOption[] cachedSource;
    private static float cachedScale = -1f;
    private static float[] cachedWeights;
    private static AgentProfile.PartOption[] cachedResult;
    private static readonly HashSet<int> warnedProfiles = new HashSet<int>();

    /// <summary>
    /// The options a Part 2 NPC should draw its accessory from: the profile's own array when a >= 1 or the structure check fails, otherwise an adjusted COPY (see the
    /// class comment). The profile asset is never modified.
    /// </summary>
    public static AgentProfile.PartOption[] GetOptions(AgentProfile profile, float a)
    {
        if (profile == null) return null;
        AgentProfile.PartOption[] src = profile.accessoriesOptions;
        a = SanitizeScale(a);
        if (a >= 1f || src == null || src.Length == 0) return src;

        int noneIndex; string reason;
        if (!TryFindNone(src, out noneIndex, out reason))
        {
            if (warnedProfiles.Add(profile.GetInstanceID()))
                Debug.LogWarning("[AccessoryWeightAdjuster] Agent Profile '" + profile.name + "' accessoriesOptions do not follow the expected structure (" + reason +
                                 "): Part 2 uses the profile's original accessory weights unchanged.");
            return src;
        }

        if (cachedResult != null && ReferenceEquals(cachedSource, src) && cachedScale == a && cachedWeights.Length == src.Length)
        {
            bool same = true;
            for (int i = 0; i < src.Length && same; i++) same = cachedWeights[i] == src[i].weight && (cachedResult[i].mesh == src[i].mesh) && ReferenceEquals(cachedResult[i].materials, src[i].materials);
            if (same) return cachedResult;
        }

        var original = new float[src.Length];
        for (int i = 0; i < src.Length; i++) original[i] = src[i].weight;
        float[] adjusted = AdjustedWeights(original, noneIndex, a);
        var copy = new AgentProfile.PartOption[src.Length];
        for (int i = 0; i < src.Length; i++)
            copy[i] = new AgentProfile.PartOption { mesh = src[i].mesh, materials = src[i].materials, weight = adjusted[i], genderRestriction = src[i].genderRestriction };
        cachedSource = src; cachedScale = a; cachedWeights = original; cachedResult = copy;
        return copy;
    }
}
