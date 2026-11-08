// NpcLosBatchTests.cs
// The batched line-of-sight pre-pass (NpcLosBatch, job-system raycasts) must return, for every NPC, exactly the status of the synchronous NpcLineOfSight.Query (and therefore of the reference QueryExhaustive).

using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public class NpcLosBatchTests
{
    static readonly Type BatchType = T("NpcLosBatch");
    static readonly Type LosType = T("NpcLineOfSight");
    readonly List<UnityEngine.Object> _objs = new List<UnityEngine.Object>();
    object _batch;

    [TearDown]
    public void TearDown()
    {
        if (_batch != null) { Call(_batch, "Dispose"); _batch = null; }
        foreach (var o in _objs) if (o != null) UnityEngine.Object.DestroyImmediate(o);
        _objs.Clear();
    }

    Transform MakeNpc(Vector3 pos, string name, int limbs = 6)
    {
        var root = new GameObject(name); _objs.Add(root); root.transform.position = pos;
        for (int i = 0; i < limbs; i++)
        {
            var c = new GameObject("limb" + i); c.transform.SetParent(root.transform, false);
            c.transform.localPosition = new Vector3((i % 3 - 1) * 0.25f, 0.4f + (i % 8) * 0.28f, (i % 2) * 0.12f);
            var cc = c.AddComponent<CapsuleCollider>(); cc.radius = 0.1f; cc.height = 0.5f;
        }
        return root.transform;
    }

    static int Code(object status) => (int)status;

    GameObject MakeCity(System.Random rnd, int boxes)
    {
        var city = new GameObject("city"); _objs.Add(city);
        for (int i = 0; i < boxes; i++)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube); b.transform.SetParent(city.transform, false);
            b.transform.position = new Vector3((float)(rnd.NextDouble() * 120 - 60), (float)(rnd.NextDouble() * 6), (float)(rnd.NextDouble() * 120 - 60));
            b.transform.localScale = new Vector3(0.1f + (float)rnd.NextDouble() * 6f, 0.5f + (float)rnd.NextDouble() * 8f, 0.1f + (float)rnd.NextDouble() * 6f);
        }
        return city;
    }

    [UnityTest]
    public IEnumerator Batch_ReturnsTheSynchronousStatus_ForEveryNpc_AcrossSeveralSnapshotsWithChangingGeometry()
    {
        var rnd = new System.Random(2026);
        MakeCity(rnd, 90);
        var npcs = new List<Transform>();
        for (int i = 0; i < 40; i++) npcs.Add(MakeNpc(new Vector3((float)(rnd.NextDouble() * 24 - 12), 0f, (float)(rnd.NextDouble() * 24 - 12)), "npc" + i));
        var participant = new GameObject("participant"); _objs.Add(participant);
        var head = new GameObject("head"); head.transform.SetParent(participant.transform, false); head.transform.localPosition = new Vector3(0f, 1.7f, 0f);
        var pcol = participant.AddComponent<CapsuleCollider>(); pcol.center = new Vector3(0f, 0.9f, 0f); pcol.height = 1.8f; pcol.radius = 0.25f;
        _batch = Activator.CreateInstance(BatchType);
        yield return null;
        Assert.IsTrue(Physics.autoSyncTransforms, "this test documents the project setting (DynamicsManager autoSyncTransforms = 1)");

        int blocked = 0, clear = 0, total = 0;
        for (int snapshot = 0; snapshot < 60; snapshot++)
        {
            participant.transform.position = new Vector3((float)(rnd.NextDouble() * 100 - 50), 0f, (float)(rnd.NextDouble() * 100 - 50));
            foreach (var n in npcs) n.position = new Vector3((float)(rnd.NextDouble() * 24 - 12), 0f, (float)(rnd.NextDouble() * 24 - 12));   // the colliders move between snapshots; NO explicit Physics.SyncTransforms(): the batch must synchronise like the synchronous queries do (autoSyncTransforms)
            Transform pRoot = (snapshot % 3 == 0) ? null : participant.transform;
            var eyes = new List<Vector3?>(); var idx = new List<int>();
            Call(_batch, "Begin", head.transform, pRoot, 1 << 0);
            int count = (snapshot % 5 == 0) ? 600 : npcs.Count;   // a larger snapshot now and then: the native arrays must grow without reading stale slots
            for (int q = 0; q < count; q++)
            {
                Transform npc = npcs[q % npcs.Count];
                Vector3? eye = npc.position + new Vector3((float)(rnd.NextDouble() - 0.5) * 0.3f, 1.4f + (float)rnd.NextDouble() * 0.3f, (float)(rnd.NextDouble() - 0.5) * 0.3f);
                if (q % 97 == 13) eye = null;   // missing origin
                eyes.Add(eye);
                idx.Add((int)Call(_batch, "Add", eye, npc));
            }
            Call(_batch, "Run");
            for (int q = 0; q < count; q++)
            {
                Transform npc = npcs[q % npcs.Count];
                object expect = CallStatic(LosType, "Query", eyes[q], npc, head.transform, pRoot, 1 << 0);
                object exhaustive = CallStatic(LosType, "QueryExhaustive", eyes[q], npc, head.transform, pRoot, 1 << 0);
                object got = Call(_batch, "Result", idx[q]);
                Assert.AreEqual(Code(expect), Code(got), "snapshot " + snapshot + " query " + q + ": batch " + got + " vs synchronous " + expect);
                Assert.AreEqual(Code(exhaustive), Code(got), "snapshot " + snapshot + " query " + q + ": batch " + got + " vs reference " + exhaustive);
                total++; string name = got.ToString(); if (name == "Blocked") blocked++; else if (name == "Clear") clear++;
            }
        }
        Assert.Greater(blocked, total / 20); Assert.Greater(clear, total / 20);
        TestContext.Out.WriteLine("LOS batch equivalence: total=" + total + " blocked=" + blocked + " clear=" + clear);
    }

    [UnityTest]
    public IEnumerator Batch_MissingTarget_InvalidGeometry_AndNearBufferOverflow_MatchTheSynchronousPath()
    {
        var npc = MakeNpc(Vector3.zero, "npcA");
        // 40 of the NPC's OWN colliders along the first metres of the ray: the near-zone buffer (32) fills up -> the reference must decide, and it says clear (own colliders are not obstacles)
        var crowd = MakeNpc(new Vector3(0f, 0f, 1f), "ownCrowd", 0);
        foreach (var t in new List<Transform>()) { }
        var big = new GameObject("own40"); _objs.Add(big); big.transform.SetParent(npc, false);
        for (int i = 0; i < 40; i++) { var c = new GameObject("o" + i); c.transform.SetParent(big.transform, false); c.transform.localPosition = new Vector3(0f, 1.5f, 0.3f + i * 0.05f); var bc = c.AddComponent<BoxCollider>(); bc.size = new Vector3(0.4f, 0.4f, 0.02f); }
        var target = new GameObject("target"); _objs.Add(target); target.transform.position = new Vector3(0f, 1.5f, 12f);
        yield return null; Physics.SyncTransforms();
        _batch = Activator.CreateInstance(BatchType);

        Vector3? eye = new Vector3(0f, 1.5f, 0f);
        // missing participant
        Call(_batch, "Begin", null, null, 1);
        int i1 = (int)Call(_batch, "Add", eye, npc);
        Call(_batch, "Run");
        Assert.AreEqual(Code(CallStatic(LosType, "Query", eye, npc, null, null, 1)), Code(Call(_batch, "Result", i1)), "missing participant target");

        Call(_batch, "Begin", target.transform, null, 1 << 0);
        int a = (int)Call(_batch, "Add", eye, npc);                                       // near buffer overflow
        int b = (int)Call(_batch, "Add", (Vector3?)target.transform.position, npc);      // zero-length segment (eye == target)
        int c2 = (int)Call(_batch, "Add", null, npc);                                     // missing origin
        Call(_batch, "Run");
        Assert.AreEqual(Code(CallStatic(LosType, "Query", eye, npc, target.transform, null, 1 << 0)), Code(Call(_batch, "Result", a)), "near buffer overflow");
        Assert.AreEqual(Code(CallStatic(LosType, "QueryExhaustive", eye, npc, target.transform, null, 1 << 0)), Code(Call(_batch, "Result", a)), "near buffer overflow vs reference");
        Assert.AreEqual(Code(CallStatic(LosType, "Query", (Vector3?)target.transform.position, npc, target.transform, null, 1 << 0)), Code(Call(_batch, "Result", b)), "zero-length segment");
        Assert.AreEqual(Code(CallStatic(LosType, "Query", null, npc, target.transform, null, 1 << 0)), Code(Call(_batch, "Result", c2)), "missing origin");
    }

    [UnityTest]
    public IEnumerator DenseCrowd_FullNearBufferWithAForeignHit_IsBlocked_ExactlyLikeTheReference_AndAllOwnHitsStillFallBack()
    {
        // a dense crowd: 45 colliders of OTHER NPCs within the first 2 m of the ray fill the 32-hit near-zone buffer; at least one returned hit is foreign, so the answer is "blocked" without any further query
        var npc = MakeNpc(Vector3.zero, "npcA");
        var others = new GameObject("others"); _objs.Add(others);
        for (int i = 0; i < 45; i++) { var c = new GameObject("f" + i); c.transform.SetParent(others.transform, false); c.transform.position = new Vector3(0f, 1.5f, 0.4f + i * 0.035f); var bc = c.AddComponent<BoxCollider>(); bc.size = new Vector3(0.4f, 0.4f, 0.01f); }
        var target = new GameObject("target"); _objs.Add(target); target.transform.position = new Vector3(0f, 1.5f, 15f);
        yield return null;
        _batch = Activator.CreateInstance(BatchType);
        Vector3? eye = new Vector3(0f, 1.5f, 0f);
        Call(_batch, "Begin", target.transform, null, 1 << 0);
        int a = (int)Call(_batch, "Add", eye, npc);
        Call(_batch, "Run");
        object reference = CallStatic(LosType, "QueryExhaustive", eye, npc, target.transform, null, 1 << 0);
        object sync = CallStatic(LosType, "Query", eye, npc, target.transform, null, 1 << 0);
        Assert.AreEqual("Blocked", reference.ToString(), "scene sanity: the reference says blocked");
        Assert.AreEqual(Code(reference), Code(sync), "synchronous near/far query");
        Assert.AreEqual(Code(reference), Code(Call(_batch, "Result", a)), "batched query");
    }
}
