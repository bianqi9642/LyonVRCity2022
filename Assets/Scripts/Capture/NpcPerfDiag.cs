// NpcPerfDiag.cs
// DEVELOPMENT-ONLY timing counters for the NPC snapshot path (DataCaptureManager.SampleNpcs, NpcLineOfSight.Query, CaptureCsvWriter.CommitCore).
// The whole file and every call site are inside #if NPC_PERF_DIAG, a scripting define that is NOT set in any project or build setting: in the shipped project this file compiles to nothing
// and the instrumented call sites compile away. It is enabled only by a benchmark harness (a csc.rsp "-define:NPC_PERF_DIAG" in a scratch project). It records nothing to disk and never changes behaviour.
// All comments in English per project convention.

#if NPC_PERF_DIAG
using System.Diagnostics;

public static class NpcPerfDiag
{
    public static long TotalTicks, ReadTicks, EyeTicks, LosTicks, FormatTicks, EventTicks, WriteTicks, BatchBuildTicks, AppendTicks;
    public static long Snapshots, Rows, Raycasts, Bytes;
    public static long TargetReissues, TargetReissuesSameDest;   // MoveToTarget's periodic SetDestination to a Transform target: how many, and how many re-submitted the destination the agent already had a complete path to

    public static long Now => Stopwatch.GetTimestamp();
    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public static void Reset()
    {
        TotalTicks = ReadTicks = EyeTicks = LosTicks = FormatTicks = EventTicks = WriteTicks = BatchBuildTicks = AppendTicks = 0;
        Snapshots = Rows = Raycasts = Bytes = 0; TargetReissues = TargetReissuesSameDest = 0;
    }
}
#endif
