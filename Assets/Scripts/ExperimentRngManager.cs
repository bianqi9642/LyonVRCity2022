// ExperimentRngManager.cs
// The single owner of the experiment's random numbers: the Master Seed, the Part-level streams, the factory of per-NPC stream sets, the participant-specific target-building
// stream and the session record (manifest block, draw-count summary). The algorithms live in ExperimentRng.cs.
//
// MASTER SEED POLICY (exact; docs/rng_system.md)
//   Sources, in order:   1. session_config.json top-level `experimentRngMasterSeed`   2. serialized ExperimentManager.experimentRngMasterSeed (Inspector / scene)
//   The Master Seed is an UNSIGNED 64-bit integer (ulong). Canonical external form: exactly 16 hexadecimal digits ("0123456789abcdef", upper or lower case accepted, always written
//   lower case). "0000000000000000" = automatic. This is the ONLY accepted notation: a JSON number (of any kind) is rejected.
//   * JSON key present and valid (16-digit hex string)             -> that value wins, whatever the Inspector says.
//   * JSON key absent                                              -> the Inspector value (serialized 16-digit hex string; a missing key is not an error).
//   * JSON key present but invalid (short / long / non-hex string, any JSON number, bool, null, mistyped key) -> the whole session configuration is REJECTED
//     (SessionConfigCore). An invalid Inspector string is rejected the same way. Nothing falls back to another source, and an invalid value NEVER becomes an automatic seed.
//   * Resolved value NON-ZERO -> fixed seed (policy "shared_fixed_seed"). Resolved value 0 (explicit JSON "0000000000000000", or JSON absent and Inspector zero) -> ONE
//     automatic 64-bit seed is generated from the operating-system CSPRNG (policy "session_specific_auto_seed"): 8 random bytes, little-endian, never 0. No clock, no instance id,
//     no Environment.TickCount. If the entropy source fails the manager enters the Failed state: every RNG request throws and ExperimentManager refuses to start a Part.
//
// LIFECYCLE (no reliance on Awake/Start order)
//   The first RNG request calls EnsureInitialized(), which (a) resolves the session configuration through ExperimentManager.TryGetRngMasterSeedConfiguration - that loads the
//   session configuration if it has not been loaded yet and requires it to be VALID, (b) resolves the seed as above, (c) generates the automatic seed if requested, (d) freezes
//   the effective seed. Only then does any stream exist, so pool preparation and every NPC / Part draw run after a validated seed. If the configuration is missing or invalid the
//   manager is Failed (useful diagnostic, FailureReason); it never freezes a guessed seed. A scene with no ExperimentManager at all is a standalone (non-experiment) scene: it uses
//   an automatic seed, recorded as source "no_experiment_manager". The seed is never regenerated at Part start and cannot change after initialisation.
//
// STREAMS
//   Part level : GetPartStream(part, stream)           keyed by (Master Seed, Part, stream)
//   NPC level  : CreateNpcRng(part, spawnIndex)        keyed by (Master Seed, Part, stream, spawnIndex); NOT by participant or group (common NPC randomness across participants)
//   Participant: CreateTargetSelectionStream(...)      one stream PER DESTINATION (Target1Building / Target2Building), keyed by (Master Seed, Part 3, stream id, participant ID, group index, zone ID); independent of every NPC stream and of each other
//   Every stream, including the Part 3 MovementAssignment stream, derives from the Master Seed alone; there is no per-stream seed override.
//
// This class holds static state because the experiment has exactly one RNG configuration per application run; it is reset on domain/scene (re)load
// (RuntimeInitializeOnLoadMethod) and explicitly by tests (ResetForTests). Main thread only. All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>The resolved Master Seed configuration: the configured 64-bit value (0 = automatic) and where it came from.</summary>
public sealed class RngSeedConfiguration
{
    public readonly ulong Seed;
    /// <summary>EXTERNAL_JSON, SCENE_INHERITED, INSPECTOR, no_experiment_manager or a test label.</summary>
    public readonly string Source;
    public RngSeedConfiguration(ulong seed, string source) { Seed = seed; Source = source; }
}

public static class ExperimentRngManager
{
    public const string PolicyShared = "shared_fixed_seed";
    public const string PolicySession = "session_specific_auto_seed";
    public const string SeedSourceFixed = "fixed";
    public const string SeedSourceAuto = "auto_generated";
    public const string EntropyOs = "os_csprng (System.Security.Cryptography.RandomNumberGenerator)";
    public const string EntropyNotUsed = "not_used_fixed_seed";
    public const string ValidationValid = "valid";

    public enum RngState { Uninitialized, Ready, Failed }

    static RngState state;
    static ulong configuredSeed, effectiveSeed;
    static string seedSource = "", policy = "", configSource = "", initNote = "", entropySource = EntropyNotUsed, failureReason = "";
    static readonly Dictionary<long, Pcg32> partStreams = new Dictionary<long, Pcg32>();
    static NpcRngSet unassigned;

    /// <summary>Test seam: replaces the scene lookup of the configured seed. Return the resolved configuration; throw to simulate an unreadable / invalid configuration. Cleared by ResetForTests.</summary>
    public static Func<RngSeedConfiguration> ConfigurationOverride;
    /// <summary>Test seam: replaces the OS entropy source (fills the 8 bytes of the automatic seed). Throw to simulate an entropy failure. Cleared by ResetForTests.</summary>
    public static Action<byte[]> EntropyOverride;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { ResetForTests(); }

    /// <summary>Forgets the whole RNG state (Master Seed, Part streams, counters). Production code never calls this; it runs at application start and in tests.</summary>
    public static void ResetForTests()
    {
        state = RngState.Uninitialized; configuredSeed = 0; effectiveSeed = 0; seedSource = ""; policy = ""; configSource = ""; initNote = ""; entropySource = EntropyNotUsed; failureReason = "";
        partStreams.Clear(); unassigned = null; ConfigurationOverride = null; EntropyOverride = null;
        ExperimentRngCounters.Reset();
    }

    // ------------------------------------------------------------------------------------------------------------------------------ initialisation

    public static bool IsInitialized => state == RngState.Ready;
    public static RngState State => state;
    /// <summary>Why the manager is Failed (empty otherwise).</summary>
    public static string FailureReason => failureReason;
    /// <summary>"valid" when Ready, otherwise "not_initialized" / "failed: reason" (manifest rng_configuration_validation).</summary>
    public static string ConfigurationValidation => state == RngState.Ready ? ValidationValid : state == RngState.Failed ? "failed: " + failureReason : "not_initialized";

    /// <summary>The configured input (0 = automatic). Initialises on first use.</summary>
    public static ulong ConfiguredMasterSeed { get { EnsureInitialized(); return configuredSeed; } }
    /// <summary>The seed actually used (never 0 once Ready). Initialises on first use; throws when the manager is Failed.</summary>
    public static ulong EffectiveMasterSeed { get { EnsureInitialized(); return effectiveSeed; } }
    /// <summary>"fixed" or "auto_generated".</summary>
    public static string SeedSource { get { EnsureInitialized(); return seedSource; } }
    /// <summary>"shared_fixed_seed" (same seed for all participants who are given it) or "session_specific_auto_seed".</summary>
    public static string SeedPolicy { get { EnsureInitialized(); return policy; } }
    /// <summary>Where the configured value came from: EXTERNAL_JSON, INSPECTOR, SCENE_INHERITED, or no_experiment_manager.</summary>
    public static string ConfigSource { get { EnsureInitialized(); return configSource; } }
    /// <summary>The entropy source of an automatic seed, or "not_used_fixed_seed".</summary>
    public static string EntropySource { get { EnsureInitialized(); return entropySource; } }
    /// <summary>Empty unless something noteworthy happened during initialisation.</summary>
    public static string InitializationNote { get { EnsureInitialized(); return initNote; } }

    /// <summary>
    /// Idempotent. Resolves and validates the configured seed, generates the automatic seed if (and only if) the resolved value is 0, and freezes the effective seed. Throws
    /// InvalidOperationException (and stays Failed) when the configuration is missing / invalid or the entropy source fails: no stream is ever created from a guessed seed.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (state == RngState.Ready) return;
        if (state == RngState.Failed) throw new InvalidOperationException("[ExperimentRngManager] RNG unavailable: " + failureReason);

        ulong configured; string source;
        try
        {
            RngSeedConfiguration c = ConfigurationOverride != null ? ConfigurationOverride() : ResolveFromScene();
            if (c == null) throw new InvalidOperationException("no Master Seed configuration was returned");
            configured = c.Seed; source = c.Source ?? "";
        }
        catch (Exception ex)
        {
            throw Fail("the Master Seed configuration could not be resolved: " + ex.Message);
        }

        ulong effective; string entropy = EntropyNotUsed;
        if (configured != 0) effective = configured;
        else
        {
            try { effective = GenerateAutomaticSeed(); entropy = EntropyOs; }
            catch (Exception ex) { throw Fail("the operating-system entropy source failed (" + ex.GetType().Name + ": " + ex.Message + "); no automatic Master Seed can be generated"); }
        }

        configuredSeed = configured; configSource = source; entropySource = entropy;
        effectiveSeed = effective;
        if (configured != 0) { seedSource = SeedSourceFixed; policy = PolicyShared; }
        else { seedSource = SeedSourceAuto; policy = PolicySession; }
        state = RngState.Ready;
        Debug.Log("[ExperimentRngManager] Master Seed " + MasterSeedFormat.ToHex(effectiveSeed) + " (" + seedSource + ", policy " + policy + ", configured " +
                  MasterSeedFormat.ToHex(configured) + " from " + source + "); " + RngConstants.AlgorithmName + " v" + RngConstants.AlgorithmVersion + ", derivation v" + RngConstants.SeedDerivationVersion + ".");
    }

    static InvalidOperationException Fail(string reason)
    {
        state = RngState.Failed; failureReason = reason;
        Debug.LogError("[ExperimentRngManager] " + reason + " The experiment cannot start; every RNG request will fail until the configuration is fixed and the application restarted.");
        return new InvalidOperationException("[ExperimentRngManager] RNG unavailable: " + reason);
    }

    // The session configuration is loaded (idempotent, memoised) and must be VALID before a seed can be frozen. No ExperimentManager at all = a standalone, non-experiment scene.
    static RngSeedConfiguration ResolveFromScene()
    {
        ExperimentManager em = UnityEngine.Object.FindObjectOfType<ExperimentManager>(true);
        if (em == null)
        {
            Debug.LogWarning("[ExperimentRngManager] No ExperimentManager in the scene (standalone, non-experiment use): an automatically generated Master Seed is used and recorded.");
            return new RngSeedConfiguration(0UL, "no_experiment_manager");
        }
        ulong seed; string src, error;
        if (!em.TryGetRngMasterSeedConfiguration(out seed, out src, out error)) throw new InvalidOperationException(error);
        return new RngSeedConfiguration(seed, src);
    }

    // One automatic 64-bit seed from the OS entropy source (CSPRNG; System.Security.Cryptography.RandomNumberGenerator). ALL 64 bits come from 8 entropy bytes assembled
    // LITTLE-ENDIAN by MasterSeedFormat.FromBytesLittleEndian (never BitConverter, which follows the machine byte order). Never 0 (the automatic sentinel): in the 2^-64 case that the
    // generator returns eight zero bytes the draw is repeated instead of being patched, so no bit is ever forced. Never clock-derived. Android IL2CPP behaviour has NOT been verified on a device.
    static ulong GenerateAutomaticSeed()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var bytes = new byte[8];
            if (EntropyOverride != null) EntropyOverride(bytes);
            else using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            ulong seed = MasterSeedFormat.FromBytesLittleEndian(bytes);
            if (seed != 0UL) return seed;
        }
        throw new InvalidOperationException("the entropy source returned only zero bytes");
    }

    // ------------------------------------------------------------------------------------------------------------------------------ streams

    static void Validate(RngPart part, RngStream stream)
    {
        if ((int)part < 0 || (int)part >= RngConstants.PartSlots) throw new ArgumentOutOfRangeException(nameof(part), "invalid RNG Part " + (int)part);
        if ((int)stream < 1 || (int)stream >= RngConstants.StreamSlots) throw new ArgumentOutOfRangeException(nameof(stream), "invalid RNG stream " + (int)stream);
        if (stream == RngStream.Target1Building || stream == RngStream.Target2Building) throw new ArgumentException(stream + " is a participant-level selection stream: use CreateTargetSelectionStream", nameof(stream));
    }

    /// <summary>The Part-level stream (shared by every consumer of that process in that Part). Created on first use; the same stream object is returned afterwards.</summary>
    public static Pcg32 GetPartStream(RngPart part, RngStream stream)
    {
        Validate(part, stream);
        EnsureInitialized();
        long key = ((long)(int)part << 8) | (long)(int)stream;
        Pcg32 s;
        if (!partStreams.TryGetValue(key, out s)) partStreams[key] = s = new Pcg32(effectiveSeed, part, stream, RngConstants.PartLevelIndex);
        return s;
    }

    /// <summary>
    /// The independent per-NPC streams of spawn number spawnIndex (>= 0) in the given Part. They depend on Master Seed, Part and spawn index ONLY - never on participant or group - so
    /// participants who share a Master Seed share the NPC random inputs.
    /// The MovementAssignment stream derives from the Master Seed like every other stream; there is no override.
    /// </summary>
    public static NpcRngSet CreateNpcRng(RngPart part, long spawnIndex)
    {
        Validate(part, RngStream.PartSetup);
        if (spawnIndex < 0) throw new ArgumentOutOfRangeException(nameof(spawnIndex), "an NPC spawn index must be >= 0");
        EnsureInitialized();
        return new NpcRngSet(effectiveSeed, part, spawnIndex);
    }

    /// <summary>
    /// The participant-specific stream that picks the target building of ONE destination inside the zone the group index assigned to it. stream must be Target1Building (Destination 1) or
    /// Target2Building (Destination 2): each has its own derived seed = SeedDerivation.TargetSelectionSeed(Effective Master Seed, Part 3, stream, participant ID, group index, zone ID).
    /// A fresh Pcg32 object per call: asking twice with the same arguments yields the same sequence, the two destinations never share a generator, and nothing here touches an NPC stream.
    /// </summary>
    public static Pcg32 CreateTargetSelectionStream(RngStream stream, string participantId, int groupIndex, string zoneId, out ulong derivedSeed)
    {
        if (stream != RngStream.Target1Building && stream != RngStream.Target2Building) throw new ArgumentException("the target-selection stream must be Target1Building or Target2Building, got " + stream, nameof(stream));
        if (string.IsNullOrWhiteSpace(participantId)) throw new ArgumentException("a participant ID is required for the participant-specific target selection", nameof(participantId));
        if (string.IsNullOrWhiteSpace(zoneId)) throw new ArgumentException("a destination zone ID is required", nameof(zoneId));
        if (groupIndex < 0) throw new ArgumentOutOfRangeException(nameof(groupIndex), "invalid group index " + groupIndex);
        EnsureInitialized();
        derivedSeed = SeedDerivation.TargetSelectionSeed(effectiveSeed, RngPart.Part3, stream, participantId, groupIndex, zoneId);
        return new Pcg32(derivedSeed, RngPart.Part3, stream);
    }

    /// <summary>
    /// The shared set (spawn index -1, Part None) for components that run without a spawner (an NPC prefab placed by hand, a test rig). Still deterministic and
    /// counted, never UnityEngine.Random.
    /// </summary>
    public static NpcRngSet UnassignedNpcRng
    {
        get
        {
            EnsureInitialized();
            return unassigned ?? (unassigned = new NpcRngSet(effectiveSeed, RngPart.None, -1));
        }
    }

    /// <summary>Maps ExperimentManager.RunningPartName ("Part1".."Part3", "" or anything else) to an RngPart. Unknown / empty = None.</summary>
    public static RngPart PartFromName(string runningPartName)
    {
        switch (runningPartName)
        {
            case "Part1": return RngPart.Part1;
            case "Part2": return RngPart.Part2;
            case "Part3": return RngPart.Part3;
            default: return RngPart.None;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------ record

    /// <summary>Names of every stream of the inventory, in enum order (manifest).</summary>
    public static string[] StreamInventory()
    {
        var l = new List<string>();
        foreach (RngStream s in RngConstants.AllStreams) l.Add(s.ToString());
        return l.ToArray();
    }

    /// <summary>"Name=version" for every stream (manifest rng.stream_versions). All streams are version 1.</summary>
    public static string[] StreamVersions()
    {
        var l = new List<string>();
        foreach (RngStream s in RngConstants.AllStreams) l.Add(s + "=" + RngConstants.StreamVersion.ToString(CultureInfo.InvariantCulture));
        return l.ToArray();
    }

    /// <summary>
    /// "Part:Stream=seedHex" (16 lowercase hex digits = the exact 64-bit seed) for the Part-level stream of every (Part, stream) pair that has been created so far (stable order). The seed of
    /// a per-NPC stream is reconstructed with SeedDerivation.StreamSeed(MasterSeed, part, stream, spawnIndex) from the npc_lifetimes rng_part / rng_spawn_index columns.
    /// </summary>
    public static string[] PartStreamSeeds()
    {
        var l = new List<KeyValuePair<long, string>>();
        foreach (var kv in partStreams)
            l.Add(new KeyValuePair<long, string>(kv.Key, kv.Value.Part + ":" + kv.Value.Stream + "=" + kv.Value.StreamSeed.ToString("x16", CultureInfo.InvariantCulture)));
        l.Sort((a, b) => a.Key.CompareTo(b.Key));
        var r = new string[l.Count];
        for (int i = 0; i < r.Length; i++) r[i] = l[i].Value;
        return r;
    }

    /// <summary>"Part:Stream=count" for every (Part, stream) with at least one draw (32-bit values), stable order.</summary>
    public static string[] DrawCountSummary()
    {
        var l = new List<string>();
        for (int p = 0; p < RngConstants.PartSlots; p++)
            for (int s = 1; s < RngConstants.StreamSlots; s++)
            {
                long c = ExperimentRngCounters.Get((RngPart)p, (RngStream)s);
                if (c > 0) l.Add(((RngPart)p) + ":" + ((RngStream)s) + "=" + c.ToString(CultureInfo.InvariantCulture));
            }
        return l.ToArray();
    }
}
