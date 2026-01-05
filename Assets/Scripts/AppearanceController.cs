using UnityEngine;

/// <summary>
/// Apply AgentProfile visuals to the assigned SkinnedMeshRenderer components.
/// Assign the appropriate SkinnedMeshRenderer fields in the prefab Inspector:
/// bodyRenderer, hairRenderer, topRenderer, bottomRenderer, shoesRenderer, accessoriesRenderer.
/// </summary>
[DisallowMultipleComponent]
public class AppearanceController : MonoBehaviour
{
    [Header("Skinned Mesh Renderers - assign in prefab Inspector")]
    public SkinnedMeshRenderer bodyRenderer;
    public SkinnedMeshRenderer hairRenderer;
    public SkinnedMeshRenderer topRenderer;
    public SkinnedMeshRenderer bottomRenderer;
    public SkinnedMeshRenderer shoesRenderer;
    public SkinnedMeshRenderer accessoriesRenderer;

    /// <summary>
    /// Apply the profile visuals to all assigned renderers.
    /// Null-safe: missing renderers or missing profile data are skipped.
    /// </summary>
    public void ApplyProfile(AgentProfile profile)
    {
        if (profile == null) return;

        ApplyPart(profile, profile.bodyOptions, bodyRenderer);
        ApplyPart(profile, profile.hairOptions, hairRenderer);
        ApplyPart(profile, profile.topOptions, topRenderer);
        ApplyPart(profile, profile.bottomOptions, bottomRenderer);
        ApplyPart(profile, profile.shoesOptions, shoesRenderer);
        ApplyPart(profile, profile.accessoriesOptions, accessoriesRenderer);
    }

    /// <summary>
    /// Apply one part's chosen option to the provided renderer.
    /// Uses the profile's chooser to pick a PartOption by weight.
    /// </summary>
    private void ApplyPart(AgentProfile profile, AgentProfile.PartOption[] options, SkinnedMeshRenderer renderer)
    {
        if (renderer == null) return;
        if (options == null || options.Length == 0) return;

        var chosen = profile.ChooseWeightedOption(options);
        if (chosen == null) return;

        // Apply mesh if provided
        if (chosen.mesh != null)
        {
            // Assign sharedMesh to avoid runtime mesh duplication unless intended.
            renderer.sharedMesh = chosen.mesh;
        }

        // Apply materials if provided
        if (chosen.materials != null && chosen.materials.Length > 0)
        {
            // Determine number of material slots (submeshes)
            int slotCount = 1;
            if (renderer.sharedMaterials != null && renderer.sharedMaterials.Length > 0)
                slotCount = renderer.sharedMaterials.Length;

            Material[] newMats = new Material[slotCount];
            for (int s = 0; s < slotCount; s++)
            {
                int matIndex = Random.Range(0, chosen.materials.Length);
                Material baseMat = chosen.materials[matIndex];

                // If the selected base material is null, fallback to existing material in that slot if possible
                if (baseMat == null)
                {
                    newMats[s] = (renderer.sharedMaterials != null && renderer.sharedMaterials.Length > s)
                        ? renderer.sharedMaterials[s]
                        : null;
                }
                else
                {
                    // Assign the material instance to renderer.materials array.
                    // `renderer.materials` will create instances as needed.
                    newMats[s] = baseMat;
                }
            }

            // Set materials (this will create instances so each renderer can be modified independently at runtime)
            renderer.materials = newMats;
        }
    }
}
