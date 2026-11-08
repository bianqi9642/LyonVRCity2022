// NpcLosBatch.cs
// Runs the NPC -> participant line-of-sight queries of one npc_state snapshot as ONE batch of physics raycasts on the Unity job system (worker threads) instead of one synchronous query per NPC on the main thread.
//
// WHY: with thousands of NPCs the per-NPC queries are the largest remaining cost of the snapshot (measured: ~220 of ~255 ms per 5 000-NPC snapshot, more than three quarters, on the main thread).
//
// WHAT IS UNCHANGED: the answer. Every query is exactly the one NpcLineOfSight.Query makes (same origin, direction, length, layer mask, trigger and backface rules, same near/far split, same own/participant
// filtering), executed by the same physics scene; only WHERE it runs changes. The status of every NPC is therefore identical to the synchronous call, which the tests verify on randomised scenes.
// Anything that is not unambiguous after the batch (a full near-zone buffer, a closest far hit that belongs to the NPC / participant) is resolved by the synchronous reference QueryExhaustive, as before.
//
// SAFETY: the batch is scheduled and completed inside one call (Run), i.e. before anything can move a collider; it is read-only on the physics scene. Any failure disables the batch and the caller falls back to the
// synchronous per-NPC path for the rest of the session. All comments in English per project convention.

using System;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

public sealed class NpcLosBatch : IDisposable
{
    struct Item
    {
        public LosStatus pre;            // decided without a physics query (missing participant / origin, invalid geometry); otherwise Clear (a placeholder, see needsQuery)
        public bool needsQuery;
        public Vector3? eye;
        public Transform npcRoot;
        public float dist, nearLen;
        public int farIndex;             // index into the far-zone arrays, -1 when the segment ends inside the near zone
    }

    Item[] _items = new Item[256];
    int _count, _farCount;
    NativeArray<RaycastCommand> _nearCmd, _farCmd;
    NativeArray<RaycastHit> _nearHits, _farHits;
    int _capacity;
    Transform _target, _participantRoot;
    int _mask;
    bool _disposed;

    /// <summary>Maximum hits collected per near-zone query (the same limit NpcLineOfSight.SmallBuffer applies to the synchronous query).</summary>
    public const int NearMaxHits = NpcLineOfSight.SmallBuffer;

    public int Count => _count;

    /// <summary>Starts a batch for one snapshot. All queries of a batch share the participant target / root and the layer mask.</summary>
    public void Begin(Transform participantTarget, Transform participantRoot, int layerMask)
    {
        _count = 0; _farCount = 0;
        _target = participantTarget; _participantRoot = participantRoot; _mask = layerMask;
    }

    void EnsureCapacity(int n)
    {
        if (_items.Length < n) Array.Resize(ref _items, Math.Max(n, _items.Length * 2));
        if (_capacity >= n && _nearCmd.IsCreated) return;
        int cap = Math.Max(n, Math.Max(256, _capacity * 2));
        DisposeNative();
        _nearCmd = new NativeArray<RaycastCommand>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _farCmd = new NativeArray<RaycastCommand>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _nearHits = new NativeArray<RaycastHit>(cap * NearMaxHits, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        _farHits = new NativeArray<RaycastHit>(cap, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        _capacity = cap;
    }

    /// <summary>
    /// Registers one NPC query and returns its index. The pre-checks are those of NpcLineOfSight.Query. The command arrays are (re)allocated lazily; the commands themselves are written in Run().
    /// </summary>
    public int Add(Vector3? npcEye, Transform npcRoot)
    {
        int idx = _count++;
        if (_items.Length < _count) Array.Resize(ref _items, _items.Length * 2);
        var it = new Item { farIndex = -1 };
        if (_target == null) { it.pre = LosStatus.MissingParticipantTarget; _items[idx] = it; return idx; }
        if (npcEye == null || npcRoot == null) { it.pre = LosStatus.MissingNpcOrigin; _items[idx] = it; return idx; }
        Vector3 origin = npcEye.Value, target = _target.position;
        Vector3 delta = target - origin;
        float dist = delta.magnitude;
        if (float.IsNaN(dist) || float.IsInfinity(dist) || dist < 1e-3f) { it.pre = LosStatus.InvalidGeometry; _items[idx] = it; return idx; }
        it.needsQuery = true; it.eye = npcEye; it.npcRoot = npcRoot; it.dist = dist; it.nearLen = dist < NpcLineOfSight.NearZoneMeters ? dist : NpcLineOfSight.NearZoneMeters;
        _items[idx] = it;
        return idx;
    }

    /// <summary>Writes the commands, runs them on the job system and waits for completion. Throws if the physics batch cannot run (the caller then uses the synchronous path).</summary>
    public void Run()
    {
        if (_count == 0) return;
        EnsureCapacity(_count);
        // The synchronous Physics.Raycast calls synchronise colliders whose transforms changed (Physics.autoSyncTransforms, on in this project) before they query. A batch must see the same colliders, so it does the
        // same synchronisation once, up front. (With autoSyncTransforms off the synchronous path would not synchronise either, and neither does the batch.)
        if (Physics.autoSyncTransforms) Physics.SyncTransforms();
        var qp = new QueryParameters(_mask, false, QueryTriggerInteraction.Ignore, Physics.queriesHitBackfaces);
        Vector3 target = _target != null ? _target.position : Vector3.zero;
        int nearN = 0, farN = 0;
        // near commands are indexed by the item index (stride NearMaxHits in the results); far commands are packed
        for (int i = 0; i < _count; i++)
        {
            Item it = _items[i];
            if (!it.needsQuery)
            {
                // keep the arrays dense: a zero-length harmless command (never read)
                _nearCmd[i] = new RaycastCommand(Vector3.zero, Vector3.forward, qp, 0f);
                continue;
            }
            Vector3 origin = it.eye.Value;
            Vector3 dir = (target - origin) / it.dist;
            _nearCmd[i] = new RaycastCommand(origin, dir, qp, it.nearLen);
            nearN++;
            if (it.dist > it.nearLen)
            {
                _farCmd[farN] = new RaycastCommand(origin + dir * it.nearLen, dir, qp, it.dist - it.nearLen);
                it.farIndex = farN++;
                _items[i] = it;
            }
        }
        _farCount = farN;
        // RaycastCommand terminates each command's hit list with an entry whose collider is null (unless all NearMaxHits slots were filled), so results of an earlier batch are never read as new hits.
        JobHandle h1 = RaycastCommand.ScheduleBatch(_nearCmd.GetSubArray(0, _count), _nearHits.GetSubArray(0, _count * NearMaxHits), 16, NearMaxHits, default);
        JobHandle h2 = farN > 0 ? RaycastCommand.ScheduleBatch(_farCmd.GetSubArray(0, farN), _farHits.GetSubArray(0, farN), 16, 1, default) : default;
        JobHandle.CombineDependencies(h1, h2).Complete();
    }

    /// <summary>The status of query <paramref name="idx"/>, identical to NpcLineOfSight.Query for the same input. Valid after Run().</summary>
    public LosStatus Result(int idx)
    {
        Item it = _items[idx];
        if (!it.needsQuery) return it.pre;
        try
        {
            int n = 0;
            int baseIdx = idx * NearMaxHits;
            while (n < NearMaxHits && _nearHits[baseIdx + n].colliderInstanceID != 0) n++;
            for (int k = 0; k < n; k++)
            {
                Collider c = _nearHits[baseIdx + k].collider;
                Transform ct = c != null ? c.transform : null;
                if (ct == null) continue;
                if (!NpcLineOfSight.IsOwnOrParticipant(ct, it.npcRoot, _participantRoot, _target)) return LosStatus.Blocked;   // conclusive even if the buffer is full (same rule as NpcLineOfSight.Query)
            }
            if (n >= NearMaxHits) return NpcLineOfSight.QueryExhaustive(it.eye, it.npcRoot, _target, _participantRoot, _mask);   // full buffer, all hits the NPC's / participant's own: let the reference decide
            if (it.dist <= it.nearLen) return LosStatus.Clear;
            if (it.farIndex < 0) return NpcLineOfSight.QueryExhaustive(it.eye, it.npcRoot, _target, _participantRoot, _mask);   // cannot happen: a far zone exists when dist > nearLen
            RaycastHit hit = _farHits[it.farIndex];
            if (hit.colliderInstanceID == 0) return LosStatus.Clear;
            Collider fc = hit.collider;
            Transform farT = fc != null ? fc.transform : null;
            if (farT == null) return NpcLineOfSight.QueryExhaustive(it.eye, it.npcRoot, _target, _participantRoot, _mask);
            if (!NpcLineOfSight.IsOwnOrParticipant(farT, it.npcRoot, _participantRoot, _target)) return LosStatus.Blocked;
            return NpcLineOfSight.QueryExhaustive(it.eye, it.npcRoot, _target, _participantRoot, _mask);
        }
        catch (Exception) { return LosStatus.QueryError; }
    }

    void DisposeNative()
    {
        if (_nearCmd.IsCreated) _nearCmd.Dispose();
        if (_farCmd.IsCreated) _farCmd.Dispose();
        if (_nearHits.IsCreated) _nearHits.Dispose();
        if (_farHits.IsCreated) _farHits.Dispose();
        _capacity = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeNative();
    }
}
