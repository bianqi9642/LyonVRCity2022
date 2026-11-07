// TargetHoverOutlineShaderTests.cs
// EditMode checks of the hover-outline shader asset and of the data the highlight design rests on (Editor only; loads project assets, changes none).
//   * The shader compiles for the active pipeline (no ShaderUtil error messages), is supported, and exposes the four properties InteractableHoverFeedback sets.
//   * Every BATI building material still uses URP/Lit with _EMISSION OFF: the premise for "colour lift through _BaseColor, emission only where the keyword is on".
// NOT covered: how the outline looks (Game view / headset), SPI rendering on a Quest. See the report.

using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class TargetHoverOutlineShaderTests
{
    const string ShaderName = "Hidden/Lyon/TargetHoverOutline";

    [Test]
    public void OutlineShader_IsFoundByName_Compiles_AndIsSupported()
    {
        Shader s = Shader.Find(ShaderName);
        Assert.IsNotNull(s, "the shader must be findable by name at runtime (it lives in a Resources folder)");
        StringAssert.Contains("/Resources/", AssetDatabase.GetAssetPath(s).Replace('\\', '/') + "/", "inside a Resources folder -> included in player builds");
        var errors = ShaderUtil.GetShaderMessages(s).Where(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).Select(m => m.message + " @" + m.file + ":" + m.line).ToList();
        Assert.IsFalse(ShaderUtil.ShaderHasError(s), "shader errors: " + string.Join(" | ", errors));
        Assert.IsEmpty(errors, string.Join(" | ", errors));
        Assert.IsTrue(s.isSupported);
    }

    [Test]
    public void OutlineShader_ExposesThePropertiesTheHighlightSets()
    {
        var m = new Material(Shader.Find(ShaderName));
        try
        {
            foreach (string p in new[] { "_OutlineColor", "_OutlineWidth", "_OutlineMinWidth", "_OutlineMaxWidth" }) Assert.IsTrue(m.HasProperty(p), p);
            Assert.AreEqual(1, m.passCount);
            Assert.IsTrue(m.GetTag("RenderPipeline", false, "") == "UniversalPipeline", "URP shader");
            Assert.AreEqual((int)UnityEngine.Rendering.RenderQueue.Geometry + 1, m.renderQueue, "opaque queue: depth-tested like the building itself");
        }
        finally { Object.DestroyImmediate(m); }
    }

    [Test]
    public void OutlineShader_Source_IsStereoInstancingSafe_AndFrontFaceCulled()
    {
        string src = File.ReadAllText(Path.Combine(Application.dataPath, "Resources/Shaders/TargetHoverOutline.shader"));
        foreach (string need in new[] { "Cull Front", "UNITY_VERTEX_OUTPUT_STEREO", "UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO", "UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX", "UNITY_SETUP_INSTANCE_ID", "GetCameraPositionWS", "multi_compile_instancing" })
            StringAssert.Contains(need, src);
    }

    [Test]
    public void BatiMaterials_AreAllUrpLit_WithEmissionKeywordOff()
    {
        string root = Path.Combine(Application.dataPath, "Materials/BATI");
        if (!Directory.Exists(root)) Assert.Ignore("No BATI material folder in this checkout.");
        const string urpLitGuid = "933532a4fcc9baf4fa0491de14d08ed7";
        int checkedCount = 0;
        foreach (string f in Directory.EnumerateFiles(root, "*.mat", SearchOption.AllDirectories))
        {
            string t = File.ReadAllText(f);
            Assert.IsTrue(t.Contains(urpLitGuid), f + ": expected URP/Lit");
            Assert.IsFalse(t.Contains("- _EMISSION"), f + ": _EMISSION keyword enabled (re-check the emission part of the highlight)");
            checkedCount++;
        }
        Assert.Greater(checkedCount, 0);
    }
}
