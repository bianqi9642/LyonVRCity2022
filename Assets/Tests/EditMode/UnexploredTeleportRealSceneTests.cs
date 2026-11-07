// UnexploredTeleportRealSceneTests.cs  (EditMode, Editor-only assembly)
// READ-ONLY audit of the experiment scene's unexplored teleportation points: it inspects the scene that is ALREADY LOADED in the Editor (nothing is opened, activated, changed or saved) and checks the structure
// UnexploredTeleportAvailability relies on:
//     TeleportationArea/OrganicTeleportPoint/Unexplored  (125 points)      TeleportationArea/GridTeleportPoint/Unexplored  (190 points)
// every point = a TeleportationAnchor with a destination transform, at least one Renderer (the visual marker) and its interaction collider on a NON-Ground layer, so that restricting it can neither touch ground
// contact nor an explored point. It is Ignored when the experiment scene is not the loaded scene. If you add or remove unexplored points on purpose, update the two counts.
// The scripts live in Assembly-CSharp / XRI, which an assembly definition cannot reference: types are reached by reflection.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

[Category("RealScene")]
public class UnexploredTeleportRealSceneTests
{
    const int ExpectedOrganic = 125, ExpectedGrid = 190;
    const int GroundLayer = 3;
    static readonly Type AnchorT = Type.GetType("UnityEngine.XR.Interaction.Toolkit.TeleportationAnchor, Unity.XR.Interaction.Toolkit");

    static string RootOf(Transform t, out bool inUnexplored)
    {
        inUnexplored = false;
        for (Transform p = t; p != null; p = p.parent)
        {
            if (p.name == "Unexplored") inUnexplored = true;
            else if (inUnexplored && (p.name == "GridTeleportPoint" || p.name == "OrganicTeleportPoint")) return p.name;
        }
        inUnexplored = false;
        return null;
    }

    [Test]
    public void UnexploredGroups_HaveTheExpectedPoints_EachMadeOfAnchorMarkerAndANonGroundInteractionCollider()
    {
        Assert.IsNotNull(AnchorT, "XRI TeleportationAnchor type not found");
        var all = UnityEngine.Object.FindObjectsOfType(AnchorT, true).Cast<Component>().ToList();
        var unexplored = new Dictionary<string, List<Component>> { { "GridTeleportPoint", new List<Component>() }, { "OrganicTeleportPoint", new List<Component>() } };
        foreach (Component a in all) { string root = RootOf(a.transform, out bool u); if (u) unexplored[root].Add(a); }
        if (unexplored.Values.Sum(l => l.Count) == 0) Assert.Ignore("The experiment scene (TeleportationArea/.../Unexplored) is not the loaded scene.");

        Assert.AreEqual(ExpectedOrganic, unexplored["OrganicTeleportPoint"].Count, "Organic unexplored points");
        Assert.AreEqual(ExpectedGrid, unexplored["GridTeleportPoint"].Count, "Grid unexplored points");

        int noRenderer = 0, noMarkerCollider = 0, groundCollider = 0, noDestination = 0;
        var destProp = AnchorT.GetProperty("teleportAnchorTransform");
        foreach (Component a in unexplored.Values.SelectMany(l => l))
        {
            if (a.GetComponentsInChildren<Renderer>(true).Length == 0) noRenderer++;
            var cols = a.GetComponentsInChildren<Collider>(true);
            if (cols.Length == 0) noMarkerCollider++;
            if (cols.Any(c => c.gameObject.layer == GroundLayer)) groundCollider++;
            if (destProp.GetValue(a) == null) noDestination++;
        }
        Assert.AreEqual(0, noRenderer, "every unexplored point has a visual marker renderer");
        Assert.AreEqual(0, noMarkerCollider, "every unexplored point has its interaction collider");
        Assert.AreEqual(0, groundCollider, "no unexplored point owns a Ground-layer collider: restricting them can never touch ground contact");
        Assert.AreEqual(0, noDestination, "every unexplored point keeps its destination transform");
    }

    [Test]
    public void ExploredAndOtherAnchors_AreOutsideTheUnexploredGroups()
    {
        var all = UnityEngine.Object.FindObjectsOfType(AnchorT, true).Cast<Component>().ToList();
        if (all.Count == 0) Assert.Ignore("The experiment scene is not the loaded scene.");
        int explored = all.Count(a => { Transform t = a.transform; for (; t != null; t = t.parent) if (t.name == "Explored") return true; return false; });
        int practice = all.Count(a => { Transform t = a.transform; for (; t != null; t = t.parent) if (t.name == "PracticeTeleportPoint") return true; return false; });
        Assert.Greater(explored, 0, "explored points exist and are not part of the restriction");
        Assert.AreEqual(0, all.Count(a => { Transform t = a.transform; bool ex = false, un = false; for (; t != null; t = t.parent) { if (t.name == "Explored") ex = true; if (t.name == "Unexplored") un = true; } return ex && un; }), "no point is both");
        TestContext.WriteLine($"anchors: {all.Count} total, {explored} explored, {practice} practice");
    }
}
