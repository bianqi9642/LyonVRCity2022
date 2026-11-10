// ExperimentRng.cs
// The deterministic random-number core of the experiment (pure C#, no UnityEngine): the PRNG, the stable seed derivation, the stream identifiers and the
// per-NPC stream set. ExperimentRngManager (separate file) owns the Master Seed, the Part-level streams and the session record; every project-owned random
// decision of the experiment draws from a stream defined here instead of UnityEngine.Random / System.Random.
//
// ALGORITHMS (versioned; both versions are written to the session manifest)
//   PRNG            : PCG32 (O'Neill 2014, XSH-RR 64/32, 64-bit state, odd 64-bit increment), reference seeding: state = 0; inc = (initseq << 1) | 1; step; state += initstate; step.
//                     Test vector (initstate 42, initseq 54): a15c02b7 7b47f409 ba1d3330 83d2f293 bfa4784b cbed606e (pcg-random.org reference output).
//   Seed derivation : SplitMix64 finalizer applied once per input (see SeedDerivation): Master Seed -> Part -> Stream -> optional index. Pure integer arithmetic, no
//                     string hashing, no clock, no platform dependence, so the same inputs give the same stream on every runtime.
//   Increase RngConstants.AlgorithmVersion / SeedDerivationVersion whenever either changes (it changes every stream).
//   MASTER SEED: an unsigned 64-bit integer (ulong, 0 = automatic sentinel) and the ONLY root of every stream. The derivation XORs the full 64-bit seed into the first Mix. External form:
//   exactly 16 hexadecimal digits (MasterSeedFormat); there is no other accepted notation and no per-stream root override.
//
// CONVENTIONS
//   * One stream = one independent random process (RngStream). Streams never share state: drawing from one cannot move another.
//   * Two levels: Part-level streams (index -1, keyed by Master Seed + Part + stream) and per-NPC streams (keyed additionally by the stable spawn index of the NPC).
//   * Value semantics follow UnityEngine.Random: integer Range(min, max) has an EXCLUSIVE max, float Range(min, max) is INCLUSIVE of max, Value/NextFloat is in [0, 1).
//   * Not thread-safe: all experiment randomness is drawn on the Unity main thread.
// All comments in English per project convention.

using System;

/// <summary>Identifiers of the independent random processes. The numeric values are part of the seed derivation: never renumber, only append.</summary>
public enum RngStream
{
    PartSetup = 1,           // Part start positions, teleport points, destination building choice
    SpawnPlacement = 2,      // NPC entrance choice, spawn-position variation, placement around the entrance
    MovementAssignment = 3,  // Part 3 Attractor / Wandering assignment (first value of the NPC's own stream)
    MovementAttributes = 4,  // NPC speed multiplier, uncertain-behaviour roll, attractor dwell time
    Appearance = 5,          // gender, body parts, materials, accessory
    Destination = 6,         // attractors, their order and the exit of a Part 3 attractor route
    Wandering = 7,           // wandering-point count, selection and exit of a wandering route
    AttractorDwell = 8,      // standing point inside an Attractor plane
    Uncertainty = 9,         // hesitation / pause / slow / detour / look-around decisions
    NavigationNudge = 10,    // small navigation corrections (target nudge)
    TargetBuilding = 11,     // participant-specific Part 3 target-building choice inside the group-assigned region (seed from Master Seed + participant + group + region, see SeedDerivation.TargetBuildingSeed)
}

/// <summary>The Part a stream belongs to. None = outside a running Part (pool preparation, test rigs). Values are part of the seed derivation.</summary>
public enum RngPart { None = 0, Part1 = 1, Part2 = 2, Part3 = 3 }

public static class RngConstants
{
    public const string AlgorithmName = "PCG32-XSH-RR";
    public const int AlgorithmVersion = 1;
    public const string SeedDerivationName = "SplitMix64-chain";
    public const int SeedDerivationVersion = 1;
    /// <summary>
    /// Schema/version of the RNG block written to the manifest and of the npc_lifetimes rng_* columns. 3 = the only supported contract: the unsigned 64-bit Master Seed written as
    /// 16-digit lowercase hex strings (RngConstants.SeedRepresentation), no legacy seed fields. Versions 1 and 2 (earlier 32-bit / transitional layouts) are not supported by the readers.
    /// </summary>
    public const int RngConfigVersion = 3;
    /// <summary>Manifest label of the seed representation (rng.master_seed_representation).</summary>
    public const string SeedRepresentation = "uint64_hex16";
    /// <summary>Part-level streams use this index in the derivation (NPC streams use their spawn index, which starts at 0).</summary>
    public const long PartLevelIndex = -1;
    /// <summary>Number of Part slots (None + Part1..3) and of stream slots (index 0 unused) for the draw counters.</summary>
    public const int PartSlots = 4, StreamSlots = 12;
    /// <summary>Version of every stream's definition (what the stream is used for and how it is keyed). Recorded per stream in the manifest; bump an individual entry only if that stream's meaning changes.</summary>
    public const int StreamVersion = 1;
    /// <summary>Domain label of the participant-specific target-building derivation (hashed, never GetHashCode).</summary>
    public const string TargetBuildingDomain = "Part3TargetBuilding";
    public static readonly RngStream[] AllStreams = (RngStream[])Enum.GetValues(typeof(RngStream));
}

/// <summary>
/// The Master Seed as an unsigned 64-bit integer: parsing and formatting of its canonical external form (exactly 16 hexadecimal digits, culture independent), the compatibility mapping of
/// the former signed 32-bit seed, and the byte-order rule of the automatic seed. Pure C# (no UnityEngine). Never goes through float / double.
/// </summary>
public static class MasterSeedFormat
{
    public const int HexLength = 16;
    /// <summary>Strict parse: exactly 16 characters, each 0-9 / a-f / A-F. No sign, no "0x", no whitespace, no trimming. The result may be 0 (= automatic).</summary>
    public static bool TryParseHex(string text, out ulong value, out string error)
    {
        value = 0; error = "";
        if (text == null) { error = "the seed is null"; return false; }
        if (text.Length != HexLength) { error = "expected exactly " + HexLength + " hexadecimal characters, got " + text.Length; return false; }
        ulong v = 0;
        for (int i = 0; i < HexLength; i++)
        {
            char c = text[i];
            int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (d < 0) { error = "character " + (i + 1) + " is not a hexadecimal digit (0-9, a-f, A-F)"; return false; }
            v = (v << 4) | (uint)d;
        }
        value = v;
        return true;
    }

    /// <summary>Canonical form: 16 lowercase hexadecimal digits, invariant culture.</summary>
    public static string ToHex(ulong value) => value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Eight bytes of entropy -> ulong, LITTLE-ENDIAN by definition (bytes[0] is the least significant), independent of the machine byte order.</summary>
    public static ulong FromBytesLittleEndian(byte[] bytes)
    {
        if (bytes == null || bytes.Length != 8) throw new ArgumentException("exactly 8 bytes are required", nameof(bytes));
        ulong v = 0;
        for (int i = 7; i >= 0; i--) v = (v << 8) | bytes[i];
        return v;
    }
}

/// <summary>Stable derivation of a 64-bit stream seed from (Master Seed, Part, stream, index). Pure integer arithmetic.</summary>
public static class SeedDerivation
{
    /// <summary>SplitMix64 finalizer (Steele, Lea, Flood 2014) of x + the golden-ratio increment. SplitMix64(0) = e220a8397b1dcdaf.</summary>
    public static ulong Mix(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }

    /// <summary>Base value of a stream: chained mixing so every input changes every output bit. index = RngConstants.PartLevelIndex (-1) for a Part-level stream.</summary>
    public static ulong StreamSeed(ulong rootSeed, RngPart part, RngStream stream, long index)
    {
        unchecked
        {
            ulong h = Mix(rootSeed ^ 0x4C594F4E52455831UL);   // domain separator "LYONREX1"; the whole 64-bit seed
            h = Mix(h + (ulong)(long)(int)part);
            h = Mix(h + (ulong)(long)(int)stream * 0x100000001B3UL);
            h = Mix(h + (ulong)(index + 1));
            return h;
        }
    }

    /// <summary>FNV-1a 64-bit hash of the UTF-8 bytes of a label (an identifier such as a participant ID or a region name), finished with Mix. Stable on every runtime; never string.GetHashCode().</summary>
    public static ulong HashUtf8(string text)
    {
        unchecked
        {
            ulong h = 0xCBF29CE484222325UL;
            if (text != null)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
                for (int i = 0; i < bytes.Length; i++) { h ^= bytes[i]; h *= 0x100000001B3UL; }
                h ^= (ulong)bytes.Length;   // the length separates "" from missing characters, and prefixes from the whole
                h *= 0x100000001B3UL;
            }
            return Mix(h);
        }
    }

    /// <summary>
    /// Stream seed of the participant-specific target-building draw: Master Seed + domain "Part3TargetBuilding" + participant ID + Part 3 group index + target region ID.
    /// The region is part of the key, so the two destinations of a group (different regions) and different groups have separately derived streams. NPC streams never use it.
    /// </summary>
    public static ulong TargetBuildingSeed(ulong masterSeed, string participantId, int part3GroupIndex, string regionId)
    {
        unchecked
        {
            ulong h = Mix(masterSeed ^ 0x4C594F4E52455832UL);   // domain separator "LYONREX2" (the NPC streams use "LYONREX1")
            h = Mix(h + HashUtf8(RngConstants.TargetBuildingDomain));
            h = Mix(h + HashUtf8(participantId));
            h = Mix(h + (ulong)(long)(part3GroupIndex + 1));
            h = Mix(h + HashUtf8(regionId));
            return h;
        }
    }

    /// <summary>The PCG32 initial state of a stream (the 'initstate' of the reference seeding).</summary>
    public static ulong InitState(ulong streamSeed) { unchecked { return Mix(streamSeed + 1UL); } }
    /// <summary>The PCG32 sequence selector of a stream (the 'initseq' of the reference seeding).</summary>
    public static ulong InitSeq(ulong streamSeed) { unchecked { return Mix(streamSeed + 2UL); } }
}

/// <summary>
/// PCG32 generator with the value helpers the experiment needs. One instance = one stream. Tracks how many 32-bit values it produced (Draws) and reports them to
/// the session-wide per-(Part, stream) counters (ExperimentRngCounters) for the manifest summary.
/// </summary>
public sealed class Pcg32
{
    ulong state, inc;
    readonly int partSlot, streamSlot;

    /// <summary>Number of 32-bit values drawn from this stream so far (one value = one PCG32 step).</summary>
    public long Draws { get; private set; }
    public RngPart Part { get; }
    public RngStream Stream { get; }
    /// <summary>The 64-bit stream seed (see SeedDerivation.StreamSeed); recorded for reconstruction and tests.</summary>
    public ulong StreamSeed { get; }

    /// <summary>Reference seeding with explicit (initstate, initseq): used by the test vectors.</summary>
    public Pcg32(ulong initState, ulong initSeq)
    {
        Seed(initState, initSeq);
        partSlot = -1; streamSlot = -1;
    }

    /// <summary>A stream of the experiment: derived from the root seed, Part, stream and index (-1 = Part level).</summary>
    public Pcg32(ulong rootSeed, RngPart part, RngStream stream, long index)
    {
        Part = part; Stream = stream;
        StreamSeed = SeedDerivation.StreamSeed(rootSeed, part, stream, index);
        Seed(SeedDerivation.InitState(StreamSeed), SeedDerivation.InitSeq(StreamSeed));
        partSlot = (int)part; streamSlot = (int)stream;
    }

    /// <summary>A stream from an already derived 64-bit stream seed (the participant-specific target-building stream). The Part / stream labels only route the draw counters.</summary>
    public Pcg32(ulong streamSeed, RngPart part, RngStream stream)
    {
        Part = part; Stream = stream; StreamSeed = streamSeed;
        Seed(SeedDerivation.InitState(streamSeed), SeedDerivation.InitSeq(streamSeed));
        partSlot = (int)part; streamSlot = (int)stream;
    }

    void Seed(ulong initState, ulong initSeq)
    {
        unchecked
        {
            state = 0UL;
            inc = (initSeq << 1) | 1UL;
            Step();
            state += initState;
            Step();
        }
    }

    ulong Step()
    {
        unchecked
        {
            ulong old = state;
            state = old * 6364136223846793005UL + inc;
            return old;
        }
    }

    /// <summary>The next uniformly distributed 32-bit value.</summary>
    public uint NextUInt()
    {
        unchecked
        {
            ulong old = Step();
            uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            Draws++;
            if (partSlot >= 0) ExperimentRngCounters.Count(partSlot, streamSlot);
            return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
        }
    }

    /// <summary>Uniform float in [0, 1) with 24 bits of resolution (one draw). The counterpart of UnityEngine.Random.value (which also includes 1).</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

    /// <summary>True with probability p (p &lt;= 0 never, p &gt;= 1 always). One draw.</summary>
    public bool Chance(float p) => NextFloat() < p;

    /// <summary>Uniform integer in [minInclusive, maxExclusive) without modulo bias (Lemire's method). Returns minInclusive when the range is empty, like UnityEngine.Random.Range(int, int) with max &lt;= min.</summary>
    public int Range(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        uint span = unchecked((uint)(maxExclusive - minInclusive));
        ulong m = (ulong)NextUInt() * span;
        uint low = (uint)m;
        if (low < span)
        {
            uint threshold = unchecked((uint)(-(int)span)) % span;
            while (low < threshold)
            {
                m = (ulong)NextUInt() * span;
                low = (uint)m;
            }
        }
        return unchecked(minInclusive + (int)(m >> 32));
    }

    /// <summary>Uniform float in [min, max] INCLUSIVE of both ends (the semantics of UnityEngine.Random.Range(float, float)); one draw. Swapped bounds are ordered.</summary>
    public float Range(float min, float max)
    {
        if (max < min) { float t = min; min = max; max = t; }
        float u = (NextUInt() >> 8) * (1f / 16777215f);   // [0, 1] inclusive
        return min + (max - min) * u;
    }

    /// <summary>Uniform point in the unit disc (like UnityEngine.Random.insideUnitCircle): two draws.</summary>
    public void InsideUnitCircle(out float x, out float y)
    {
        float r = (float)Math.Sqrt(NextFloat());
        float a = NextFloat() * 6.28318530717959f;
        x = r * (float)Math.Cos(a); y = r * (float)Math.Sin(a);
    }

    /// <summary>Uniform point in the unit ball (like UnityEngine.Random.insideUnitSphere): rejection sampling from the cube, so a variable number of draws.</summary>
    public void InsideUnitSphere(out float x, out float y, out float z)
    {
        do
        {
            x = NextFloat() * 2f - 1f; y = NextFloat() * 2f - 1f; z = NextFloat() * 2f - 1f;
        } while (x * x + y * y + z * z > 1f);
    }

    /// <summary>
    /// Index of an option chosen with probability proportional to its weight, from ONE draw. Negative, NaN and infinite weights count as 0; an option with weight 0 is NEVER
    /// returned. Returns -1 when no weight is positive. weightOf(i) must be pure.
    /// </summary>
    public int ChooseWeighted(int count, Func<int, float> weightOf)
    {
        double total = 0.0; int lastPositive = -1;
        for (int i = 0; i < count; i++) { float w = weightOf(i); if (w > 0f && !float.IsInfinity(w)) { total += w; lastPositive = i; } }
        if (lastPositive < 0) return -1;
        double r = NextFloat() * total;   // [0, total)
        double acc = 0.0;
        for (int i = 0; i < count; i++)
        {
            float w = weightOf(i);
            if (!(w > 0f) || float.IsInfinity(w)) continue;
            acc += w;
            if (r < acc) return i;
        }
        return lastPositive;   // rounding only: still a positive-weight option
    }
}

/// <summary>Session-wide draw counters per (Part, stream), filled by every Pcg32 of the experiment (manifest summary). Main thread only.</summary>
public static class ExperimentRngCounters
{
    static readonly long[,] counts = new long[RngConstants.PartSlots, RngConstants.StreamSlots];
    public static void Count(int part, int stream) { counts[part, stream]++; }
    public static long Get(RngPart part, RngStream stream) => counts[(int)part, (int)stream];
    public static long Total(RngStream stream) { long t = 0; for (int p = 0; p < RngConstants.PartSlots; p++) t += counts[p, (int)stream]; return t; }
    public static void Reset() { Array.Clear(counts, 0, counts.Length); }
}

/// <summary>
/// The independent streams of ONE NPC spawn, created lazily from (root seed, Part, stream, spawn index). A random decision of this NPC never moves another NPC's
/// streams, and the same (root seed, Part, spawn index) always yields the same streams. Not tied to any scene object.
/// </summary>
public sealed class NpcRngSet
{
    readonly ulong rootSeed;
    readonly Pcg32[] streams = new Pcg32[RngConstants.StreamSlots];

    public RngPart Part { get; }
    /// <summary>The stable spawn index (>= 0) within the Part; -1 for the shared "unassigned" set used by components that run without a spawner.</summary>
    public long SpawnIndex { get; }

    /// <param name="rootSeed">The effective 64-bit Master Seed: the single root of every stream of this NPC.</param>
    public NpcRngSet(ulong rootSeed, RngPart part, long spawnIndex)
    {
        this.rootSeed = rootSeed; Part = part; SpawnIndex = spawnIndex;
    }

    /// <summary>The stream with the given identifier (created on first use).</summary>
    public Pcg32 Get(RngStream id)
    {
        int slot = (int)id;
        if (slot < 1 || slot >= RngConstants.StreamSlots) throw new ArgumentOutOfRangeException(nameof(id), "unknown RNG stream " + slot);
        if (id == RngStream.TargetBuilding) throw new ArgumentException("TargetBuilding is a participant-level stream (ExperimentRngManager.CreateTargetBuildingStream), not an NPC stream", nameof(id));
        Pcg32 s = streams[slot];
        if (s == null)
        {
            s = streams[slot] = new Pcg32(rootSeed, Part, id, SpawnIndex);
        }
        return s;
    }

    public Pcg32 SpawnPlacement => Get(RngStream.SpawnPlacement);
    public Pcg32 MovementAssignment => Get(RngStream.MovementAssignment);
    public Pcg32 MovementAttributes => Get(RngStream.MovementAttributes);
    public Pcg32 Appearance => Get(RngStream.Appearance);
    public Pcg32 Destination => Get(RngStream.Destination);
    public Pcg32 Wandering => Get(RngStream.Wandering);
    public Pcg32 AttractorDwell => Get(RngStream.AttractorDwell);
    public Pcg32 Uncertainty => Get(RngStream.Uncertainty);
    public Pcg32 NavigationNudge => Get(RngStream.NavigationNudge);
}
