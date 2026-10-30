// AccessoryAppearanceTests.cs
// Editor tests (Window > General > Test Runner, EditMode or PlayMode) for the NPC wristband appearance: AppearanceController.ApplyProfile with a
// profile that has two bracelet options (four colour alternatives each) and a "no bracelet" option. The production AppearanceController and
// AgentProfile live in Assembly-CSharp, which an assembly definition cannot reference, so - like the other tests here - they are reached by
// reflection (TestSupport.Reflect). A bare SkinnedMeshRenderer is used: no scene, NavMesh or spawner. Not a Quest test.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using static Reflect;

public class AccessoryAppearanceTests
{
    static Type OptType => Type.GetType("AgentProfile+PartOption, Assembly-CSharp");

    GameObject go;
    Component app;
    SkinnedMeshRenderer accessories;
    UnityEngine.Object profile;
    Mesh meshL, meshR, placeholderMesh;
    Material[] colours;          // the four wristband alternatives
    Material placeholderMaterial;
    object male;
    System.Reflection.MethodInfo applyProfile;
    readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

    T Own<T>(T o) where T : UnityEngine.Object { owned.Add(o); return o; }

    [SetUp]
    public void SetUp()
    {
        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) Assert.Ignore("no shader available to build test materials");
        placeholderMesh = Own(new Mesh { name = "PlaceholderBody" });
        meshL = Own(new Mesh { name = "BraceletL" });
        meshR = Own(new Mesh { name = "BraceletR" });
        placeholderMaterial = Own(new Material(sh) { name = "Placeholder" });
        colours = new[]
        {
            Own(new Material(sh) { name = "Wristband_FluoYellow" }), Own(new Material(sh) { name = "Wristband_Cyan" }),
            Own(new Material(sh) { name = "Wristband_Orange" }),     Own(new Material(sh) { name = "Wristband_Magenta" })
        };

        go = Own(new GameObject("TestNpc"));
        app = go.AddComponent(T("AppearanceController"));
        GameObject child = new GameObject("Accessories"); child.transform.SetParent(go.transform);
        accessories = child.AddComponent<SkinnedMeshRenderer>();
        accessories.sharedMesh = placeholderMesh;                       // like the prefab: a placeholder mesh and one material slot
        accessories.sharedMaterials = new[] { placeholderMaterial };
        Set(app, "accessoriesRenderer", accessories);

        profile = Own(ScriptableObject.CreateInstance(T("AgentProfile")));
        SetOptions(Bracelet(meshL, 0.25f), Bracelet(meshR, 0.25f), None(0.5f));

        Type gender = T("AgentGender");
        male = Enum.Parse(gender, "Male");
        applyProfile = app.GetType().GetMethod("ApplyProfile", new[] { profile.GetType(), gender });
        Assert.IsNotNull(applyProfile, "AppearanceController.ApplyProfile(AgentProfile, AgentGender) not found");
    }

    [TearDown]
    public void TearDown() { foreach (UnityEngine.Object o in owned) if (o != null) UnityEngine.Object.DestroyImmediate(o); owned.Clear(); }

    object Bracelet(Mesh m, float w) { object o = Activator.CreateInstance(OptType); Set(o, "mesh", m); Set(o, "materials", colours.Clone()); Set(o, "weight", w); return o; }
    object None(float w) { object o = Activator.CreateInstance(OptType); Set(o, "mesh", null); Set(o, "materials", new Material[0]); Set(o, "weight", w); return o; }

    void SetOptions(params object[] opts)
    {
        Array arr = Array.CreateInstance(OptType, opts.Length);
        for (int i = 0; i < opts.Length; i++) arr.SetValue(opts[i], i);
        Set(profile, "accessoriesOptions", arr);
    }

    void Apply() { applyProfile.Invoke(app, new object[] { profile, male }); }
    void ApplyOnly(params object[] opts) { SetOptions(opts); Apply(); }
    object LastAccessories() { return Get<object>(Get<object>(app, "LastApplied"), "accessories"); }

    [Test]
    public void ConfiguredProfile_ShowsNoBraceletHalfTheTime_AndAllFourColoursAppear()
    {
        int none = 0, bracelet = 0; var seen = new Dictionary<string, int>();
        const int n = 2000;
        for (int i = 0; i < n; i++)
        {
            Apply();
            if (accessories.sharedMesh == null) none++;
            else { bracelet++; string m = accessories.sharedMaterials[0].name; seen[m] = seen.TryGetValue(m, out int c) ? c + 1 : 1; }
        }
        Assert.AreEqual(n, none + bracelet);
        Assert.That(none / (double)n, Is.InRange(0.44, 0.56), "about 50% without bracelet");
        foreach (Material c in colours)
        {
            Assert.IsTrue(seen.ContainsKey(c.name), c.name + " never appeared: every alternative must be eligible");
            Assert.That(seen[c.name] / (double)bracelet, Is.InRange(0.19, 0.31), c.name + " should be about 25% of bracelets");
        }
    }

    [Test]
    public void NoBracelet_ClearsMeshAndEverySlot_EvenAfterABracelet()
    {
        ApplyOnly(Bracelet(meshL, 1f));
        Assert.AreSame(meshL, accessories.sharedMesh);
        ApplyOnly(None(1f));
        Assert.IsNull(accessories.sharedMesh, "old bracelet mesh must not survive");
        foreach (Material m in accessories.sharedMaterials) Assert.IsNull(m, "old bracelet material must not survive");
        Assert.AreEqual(0, Get<int>(LastAccessories(), "optionIndex"), "the no-bracelet option is option 0 here");
        Assert.AreEqual("", Get<string>(LastAccessories(), "MeshName"));
    }

    [Test]
    public void FirstTimeNpc_NoBracelet_DoesNotKeepThePrefabPlaceholderMesh()
    {
        ApplyOnly(None(1f));
        Assert.IsNull(accessories.sharedMesh);
        Assert.IsNull(accessories.sharedMaterials[0]);
    }

    [Test]
    public void PoolReuse_Transitions_NeverLeakAMeshOrMaterial()
    {
        for (int cycle = 0; cycle < 6; cycle++)
        {
            ApplyOnly(Bracelet(meshL, 1f));
            Assert.AreSame(meshL, accessories.sharedMesh);
            Assert.Contains(accessories.sharedMaterials[0], colours);

            ApplyOnly(None(1f));
            Assert.IsNull(accessories.sharedMesh); Assert.IsNull(accessories.sharedMaterials[0]);

            ApplyOnly(Bracelet(meshR, 1f));
            Assert.AreSame(meshR, accessories.sharedMesh, "no bracelet -> bracelet: the new mesh is assigned");
            Assert.Contains(accessories.sharedMaterials[0], colours);
        }
    }

    [Test]
    public void BraceletColour_ChangesBetweenApplications_AndIsASharedAssetNotAnInstance()
    {
        var seen = new HashSet<Material>();
        for (int i = 0; i < 100; i++)
        {
            ApplyOnly(Bracelet(meshL, 1f));
            Material m = accessories.sharedMaterials[0];
            Assert.Contains(m, colours); Assert.IsFalse(m.name.EndsWith("(Instance)"));
            Assert.AreEqual(1, accessories.sharedMaterials.Length);
            seen.Add(m);
            Assert.AreSame(m, accessories.sharedMaterials[0], "stable until the next ApplyProfile");
        }
        Assert.Greater(seen.Count, 1, "the colour must not be fixed to the first alternative");
        Assert.AreEqual(4, seen.Count);
    }

    [Test]
    public void ProfileWithoutAccessoryOptions_LeavesTheRendererUntouched()
    {
        SetOptions();
        Apply();
        Assert.AreSame(placeholderMesh, accessories.sharedMesh);
        Assert.AreSame(placeholderMaterial, accessories.sharedMaterials[0]);
        Assert.AreEqual(-2, Get<int>(LastAccessories(), "optionIndex"));
    }
}
