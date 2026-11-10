// Part3TargetSelection.cs
// The participant-specific choice of the Part 3 target building inside the region that the Part 3 GROUP INDEX assigned (pure logic, no scene objects).
//
//   * The group index fixes the start point and the target regions (ExperimentManager.Part3GroupTable). It is never random and never uses a seed.
//   * Within one assigned region the target building is drawn uniformly from the region's ELIGIBLE buildings with a dedicated stream whose seed is derived from
//         Effective Master Seed + "Part3TargetBuilding" + participant ID + Part 3 group index + target region ID      (SeedDerivation.TargetBuildingSeed)
//     That stream shares nothing with the NPC streams: the draw cannot shift them and they cannot shift it.
//   * Candidates are identified by their stable unique id (TargetSelectable.ResolvedTargetId) and SORTED with an ordinal comparison before the draw, so neither the Unity
//     hierarchy, the scene enumeration order nor the order of the Inspector slots can change the outcome. Duplicate or empty ids are an error.
//   * No fallback exists: a missing participant ID, an invalid group index, an empty candidate list or an unusable id make the selection fail with a clear message and the
//     Part does not start. A building of another region is never selected. There is no quota / no-replacement rule: independent participants may draw the same building.
//
// All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.Globalization;

public static class Part3TargetSelection
{
    public sealed class Result
    {
        public string RegionId;
        /// <summary>Eligible candidate ids, sorted ordinally (the order the draw indexes into).</summary>
        public string[] CandidateIds;
        /// <summary>16 lowercase hex digits: HashUtf8 of the sorted ids joined by '|' (a compact, order-independent fingerprint of the eligible set).</summary>
        public string CandidateFingerprint;
        public int SelectedIndex;
        public string SelectedId;
        /// <summary>The exact 64-bit derived stream seed (SeedDerivation.TargetBuildingSeed); serialised as 16 hex digits wherever it is recorded.</summary>
        public ulong DerivedSeed;
        public string DerivedSeedHex => DerivedSeed.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>Sorts candidate ids ordinally. Null / empty / duplicate ids are errors (a candidate that cannot be identified stably cannot be selected reproducibly).</summary>
    public static bool TrySortCandidates(IList<string> ids, out string[] sorted, out string error)
    {
        sorted = null; error = null;
        if (ids == null || ids.Count == 0) { error = "no eligible target building"; return false; }
        var list = new List<string>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(ids[i])) { error = "candidate " + i + " has no usable id"; return false; }
            list.Add(ids[i]);
        }
        list.Sort(string.CompareOrdinal);
        for (int i = 1; i < list.Count; i++)
            if (string.Equals(list[i], list[i - 1], StringComparison.Ordinal)) { error = "duplicate candidate id '" + list[i] + "'"; return false; }
        sorted = list.ToArray();
        return true;
    }

    public static string Fingerprint(string[] sortedIds)
    {
        return SeedDerivation.HashUtf8(string.Join("|", sortedIds)).ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Draws the participant's target building for one region. Requires the experiment RNG to be Ready (it initialises on first use and throws when it is Failed). Returns false with
    /// an error text instead of any fallback.
    /// </summary>
    public static bool TrySelect(string participantId, int part3GroupIndex, string regionId, IList<string> candidateIds, out Result result, out string error)
    {
        result = null; error = null;
        if (string.IsNullOrWhiteSpace(participantId)) { error = "the participant ID is missing: the participant-specific target-building selection cannot run (no shared-stream fallback)"; return false; }
        if (part3GroupIndex < 0) { error = "invalid Part 3 group index " + part3GroupIndex; return false; }
        if (string.IsNullOrWhiteSpace(regionId)) { error = "the target region of group " + part3GroupIndex + " is not defined"; return false; }
        string[] sorted;
        if (!TrySortCandidates(candidateIds, out sorted, out string sortError)) { error = "region " + regionId + ": " + sortError; return false; }

        ulong seed;
        Pcg32 stream = ExperimentRngManager.CreateTargetBuildingStream(participantId, part3GroupIndex, regionId, out seed);
        int index = stream.Range(0, sorted.Length);
        result = new Result
        {
            RegionId = regionId, CandidateIds = sorted, CandidateFingerprint = Fingerprint(sorted),
            SelectedIndex = index, SelectedId = sorted[index], DerivedSeed = seed,
        };
        return true;
    }
}
