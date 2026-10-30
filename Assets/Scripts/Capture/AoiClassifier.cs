// AoiClassifier.cs
// Deterministic, lightweight classification of a gaze-ray hit into a raw semantic class plus stable
// identifiers, for GazeRecorder (Architecture C, Revision 2). NOT a theoretical AOI model - it only
// tags what the ray physically hit so offline analysis can build any AOI definition it wants.
//
// First-match priority:
//   1. PedestrianAgent in parents                                   -> NPC
//   2. TargetSelectable in parents, owner is a Part3 candidate /
//      Part1 target                                                 -> CANDIDATE_BUILDING
//      (any other TargetSelectable)                                 -> BUILDING
//   4. MeshCollider + Default layer + name in the registry /
//      matches the BATI name pattern                                -> BUILDING
//   5. layer == Ground -> TERRAIN   6. layer == Water -> WATER
//   7. layer == UI or a Canvas in parents -> UI
//   8. anything else that was hit -> OTHER      9. nothing hit -> NONE
//
// TARGET_BUILDING (schema v5) means "the building that is the ACTIVE search target at this very sample": DataCaptureManager.CurrentTargetBuilding
// (Part 3 phase d1_search -> D1, d2_search -> D2, and its TargetSelectable currently enabled). It is derived from live state on EVERY call by reference
// comparison; it is never cached and never inferred from a GameObject name. D1 and D2 stay assigned all session, so the other destination, earlier targets and
// panel-reading phases are NOT current: they keep the candidate / building class and hit_is_current_target = 0 (hit_assigned_destination still names D1/D2).
//
// building_id is always GameObject.name (frozen decision). targetId is never used as identity.
// A one-entry collider cache skips hierarchy re-resolution during a fixation; it holds only target-INDEPENDENT facts (base class, owner object).
// All comments in English per project convention.

using System.Text.RegularExpressions;
using UnityEngine;

public static class AoiClassifier
{
    public struct HitInfo
    {
        public string semanticClass;
        public string buildingId;
        public string npcId;
        public string npcSpawnInstanceId;
        public string isInteractable;   // "1" / "0" / ""
        public string isCurrentTarget;  // "1" = the hit is (inside) the currently ACTIVE target building; "0" = anything else that was hit; "" = no hit / not determinable (no DataCaptureManager)
        public string assignedDestination; // "D1" / "D2" when the hit building is an assigned destination of this session (current or not), else ""
        public string colliderName;
        public string objectName;
        public int    instanceId;
        public int    layer;            // -1 when none
        public string tag;
    }

    public static readonly HitInfo None = new HitInfo
    {
        semanticClass = "NONE", buildingId = "", npcId = "", npcSpawnInstanceId = "",
        isInteractable = "", isCurrentTarget = "", assignedDestination = "", colliderName = "", objectName = "", instanceId = 0, layer = -1, tag = ""
    };

    // one-entry cache (per fixation)
    static Collider s_col;
    static string s_class, s_buildingId, s_colliderName, s_objectName, s_tag;
    static int s_instanceId, s_layer;
    static TargetSelectable s_ts;   // may be null
    static GameObject s_owner;      // resolved building owner (TargetSelectable path), may be null
    static PedestrianAgent  s_pa;   // may be null

    /// <summary>Number of gaze hits on an NPC that had no open lifetime (blank npc_spawn_instance_id). Diagnostic; see DataCaptureManager.NoteUnresolvedNpcHit.</summary>
    public static int UnresolvedNpcHits { get; private set; }

    /// <summary>Resets the diagnostic counter (tests).</summary>
    public static void ResetDiagnostics() { UnresolvedNpcHits = 0; s_col = null; }

    static readonly Regex s_batiRe = new Regex(@"^\d{5}[A-Z]{2}\d+$", RegexOptions.Compiled);

    static int s_uiLayer = -2, s_groundLayer = -2, s_waterLayer = -2;

    static void EnsureLayers()
    {
        if (s_uiLayer != -2) return;
        s_uiLayer     = LayerMask.NameToLayer("UI");
        s_groundLayer = LayerMask.NameToLayer("Ground");
        s_waterLayer  = LayerMask.NameToLayer("Water");
    }

    public static HitInfo Classify(bool didHit, Collider hit, ExperimentManager em, DataCaptureManager dcm)
    {
        if (!didHit || hit == null) return None;

        if (!ReferenceEquals(hit, s_col))
        {
            ResolveStatic(hit, em, dcm);
            s_col = hit;
        }

        HitInfo info;
        // Target-dependent part, recomputed every call from live state (a cached collider must never keep obsolete target status).
        string cls = s_class;
        bool buildingLike = cls == "BUILDING" || cls == "CANDIDATE_BUILDING";
        GameObject current = dcm != null ? dcm.CurrentTargetBuilding : null;
        bool isCurrent = current != null && buildingLike && ((s_owner != null && s_owner == current) || hit.transform.IsChildOf(current.transform));
        if (isCurrent) cls = "TARGET_BUILDING";
        info.isCurrentTarget = dcm == null ? "" : (isCurrent ? "1" : "0");
        GameObject roleOwner = s_owner;
        if (dcm != null && roleOwner == null && buildingLike)
        {
            if (dcm.AssignedBuildingD1 != null && hit.transform.IsChildOf(dcm.AssignedBuildingD1.transform)) roleOwner = dcm.AssignedBuildingD1;
            else if (dcm.AssignedBuildingD2 != null && hit.transform.IsChildOf(dcm.AssignedBuildingD2.transform)) roleOwner = dcm.AssignedBuildingD2;
        }
        info.assignedDestination = dcm != null && buildingLike ? dcm.AssignedDestinationRole(roleOwner) : "";

        info.semanticClass      = cls;
        info.buildingId         = s_buildingId;
        info.colliderName       = s_colliderName;
        info.objectName         = s_objectName;
        info.tag                = s_tag;
        info.instanceId         = s_instanceId;
        info.layer              = s_layer;
        // NPC identity is research data of the NPC recorder: it is filled only while the NPC master switch (npcCaptureEnabled) is on. With it off
        // the class stays "NPC" (a geometric fact about the collider) but both IDs are blank - "unavailable by configuration", not "not looked at".
        info.npcId = "";
        info.npcSpawnInstanceId = "";
        if (s_pa != null && dcm != null && dcm.NpcRecordingEnabled)
        {
            info.npcId = SafeStr(s_pa.AgentID);
            // Lifetime ID of the NPC that was hit, resolved from the agent itself (the same value npc_state / npc_events / events use).
            // It is looked up on EVERY call, never cached: after despawn the ended lifetime resolves to "" and after reuse to the new one.
            // A hit on an NPC with no open lifetime is left blank (unresolved) and counted - it is never given an invented ID.
            info.npcSpawnInstanceId = SafeStr(dcm.GetSpawnInstanceId(s_pa));
            if (info.npcSpawnInstanceId.Length == 0)
            {
                UnresolvedNpcHits++;
                dcm.NoteUnresolvedNpcHit(s_pa);
            }
        }
        info.isInteractable     = (s_ts != null) ? (s_ts.enabled ? "1" : "0") : "";
        return info;
    }

    static void ResolveStatic(Collider hit, ExperimentManager em, DataCaptureManager dcm)
    {
        EnsureLayers();

        GameObject go = hit.gameObject;
        s_colliderName = SafeStr(hit.name);
        s_instanceId   = go.GetInstanceID();
        s_layer        = go.layer;
        s_tag          = SafeStr(go.tag);
        s_ts           = null;
        s_pa           = null;
        s_owner        = null;
        s_buildingId   = "";
        s_objectName   = SafeStr(go.name);

        // 1. NPC
        PedestrianAgent pa = hit.GetComponentInParent<PedestrianAgent>();
        if (pa != null)
        {
            s_pa = pa;
            s_class = "NPC";
            s_objectName = SafeStr(pa.gameObject.name);
            return;
        }

        // 2 / 3. TargetSelectable
        TargetSelectable ts = hit.GetComponentInParent<TargetSelectable>();
        if (ts != null)
        {
            s_ts = ts;
            GameObject owner = ResolveCandidateOwner(ts.transform, em) ?? ts.gameObject;
            s_owner = owner;
            s_buildingId = SafeStr(owner.name);
            s_objectName = SafeStr(owner.name);

            if (em != null && IsExperimentCandidate(owner, em))
            {
                s_class = "CANDIDATE_BUILDING";
                return;
            }
            s_class = "BUILDING";
            return;
        }

        // 4. building by registry name / BATI pattern
        string batiName = ResolveBatiName(hit.transform);
        bool looksBuilding =
            (hit is MeshCollider) && (s_layer == 0) &&
            (!string.IsNullOrEmpty(batiName) || (dcm != null && dcm.IsRegisteredBuildingName(SafeStr(go.name))));
        if (looksBuilding)
        {
            s_class = "BUILDING";
            s_buildingId = !string.IsNullOrEmpty(batiName) ? batiName : SafeStr(go.name);
            s_objectName = s_buildingId;
            return;
        }

        // 5 / 6 / 7
        if (s_groundLayer >= 0 && s_layer == s_groundLayer) { s_class = "TERRAIN"; return; }
        if (s_waterLayer  >= 0 && s_layer == s_waterLayer)  { s_class = "WATER";   return; }
        if ((s_uiLayer >= 0 && s_layer == s_uiLayer) || hit.GetComponentInParent<Canvas>() != null) { s_class = "UI"; return; }

        // 8
        s_class = "OTHER";
    }

    static GameObject ResolveCandidateOwner(Transform t, ExperimentManager em)
    {
        if (em == null) return null;
        for (int i = 0; i < 8 && t != null; i++)
        {
            if (IsExperimentCandidate(t.gameObject, em)) return t.gameObject;
            t = t.parent;
        }
        return null;
    }

    static bool IsExperimentCandidate(GameObject go, ExperimentManager em)
    {
        if (em == null || go == null) return false;

        if (em.part1TargetBuildings != null)
            for (int i = 0; i < em.part1TargetBuildings.Length; i++)
                if (em.part1TargetBuildings[i] == go) return true;

        return CandidateInZone(go, em.part3ZoneGE) || CandidateInZone(go, em.part3ZoneGU)
            || CandidateInZone(go, em.part3ZoneOE) || CandidateInZone(go, em.part3ZoneOU);
    }

    static bool CandidateInZone(GameObject go, ExperimentManager.Part3ZoneTargets z)
        => z != null && (z.buildingA == go || z.buildingB == go);

    static string ResolveBatiName(Transform t)
    {
        for (int i = 0; i < 6 && t != null; i++)
        {
            if (!string.IsNullOrEmpty(t.name) && s_batiRe.IsMatch(t.name)) return t.name;
            t = t.parent;
        }
        return null;
    }

    static string SafeStr(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace(",", " ").Replace("\n", " ").Replace("\r", " ");
    }
}