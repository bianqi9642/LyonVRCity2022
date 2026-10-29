using UnityEngine;

/// <summary>
/// Apply AgentProfile visuals to the assigned SkinnedMeshRenderer components.
/// Assign the appropriate SkinnedMeshRenderer fields in the prefab Inspector:
/// bodyRenderer, hairRenderer, topRenderer, bottomRenderer, shoesRenderer, accessoriesRenderer.
///
/// Gender-consistent appearance:
///   - ApplyProfile() rolls the NPC's gender ONCE (AgentProfile.ChooseGender), stores it on
///     CurrentGender, then passes that single value into every ApplyPart call. Gender is
///     therefore determined once per NPC (per spawn / pool reuse), never per clothing item.
///   - Hair / Top / Bottom are selected with the gender-filtered overload
///     (AgentProfile.ChooseWeightedOption(options, gender)), so a male NPC can only ever
///     receive Unisex or MaleOnly options and a female NPC only Unisex or FemaleOnly options.
///   - Shoes / Accessories / Body options are Unisex and behave exactly as before.
///   - On pool reuse ApplyProfile runs again in full: a fresh gender is rolled and ALL of
///     hair/top/bottom are re-applied for it in the same synchronous call, so there is no
///     window in which the gender and the clothing disagree. If a required gendered pool is
///     empty for the rolled gender, the renderer is cleared (never left showing the previous
///     NPC's opposite-gender mesh) and a warning is logged when enableDebugLogs is on.
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

    [Header("Debug")]
    [Tooltip("When true, logs a warning if a gendered appearance pool (hair/top/bottom) has no " +
             "option for the rolled gender. Enable while setting up the Agent Profile.")]
    public bool enableDebugLogs = false;

    /// <summary>
    /// Gender assigned to this NPC by the most recent ApplyProfile call. Constant for the
    /// lifetime of one spawn/pool-activation; re-rolled on the next ApplyProfile (pool reuse).
    /// Meaningful only after ApplyProfile has run at least once (always the case before an NPC
    /// is spawned into view, since AgentFactory calls ApplyProfile during spawn setup).
    /// </summary>
    public AgentGender CurrentGender { get; private set; }

    /// <summary>
    /// Data capture (observation only): what the MOST RECENT ApplyProfile call actually produced - the realised option index, mesh and material
    /// assets per part. A new immutable instance is created per call, so a holder of an older instance (an earlier NPC lifetime) is never affected
    /// by a later re-skin of this pooled object. Null until ApplyProfile has run.
    /// </summary>
    public AppliedAppearance LastApplied { get; private set; }

    /// <summary>
    /// Apply the profile visuals to all assigned renderers. Gender is rolled from the profile.
    /// Null-safe: missing renderers or missing profile data are skipped.
    /// </summary>
    public void ApplyProfile(AgentProfile profile)
    {
        if (profile == null) return;
        ApplyProfile(profile, profile.ChooseGender());
    }

    /// <summary>
    /// Apply the profile visuals with an explicitly forced gender (e.g. for a future
    /// experiment gender condition). Existing callers use the single-argument overload.
    /// </summary>
    public void ApplyProfile(AgentProfile profile, AgentGender gender)
    {
        if (profile == null) return;

        // Gender is decided ONCE here and reused for every gender-restricted part below.
        CurrentGender = gender;

        var applied = new AppliedAppearance { appliedTAbs = CaptureClock.TAbs, gender = gender };
        applied.body        = ApplyPart(profile, profile.bodyOptions,        bodyRenderer,        CurrentGender);
        applied.hair        = ApplyPart(profile, profile.hairOptions,        hairRenderer,        CurrentGender);
        applied.top         = ApplyPart(profile, profile.topOptions,         topRenderer,         CurrentGender);
        applied.bottom      = ApplyPart(profile, profile.bottomOptions,      bottomRenderer,      CurrentGender);
        applied.shoes       = ApplyPart(profile, profile.shoesOptions,       shoesRenderer,       CurrentGender);
        applied.accessories = ApplyPart(profile, profile.accessoriesOptions, accessoriesRenderer, CurrentGender);
        LastApplied = applied;
    }

    /// <summary>
    /// Apply one part's chosen option to the provided renderer.
    /// Uses the profile's gender-filtered chooser to pick a PartOption by weight.
    /// Unisex options are always eligible, so shoes/accessories/body are unaffected.
    /// </summary>
    private AppliedPartRecord ApplyPart(AgentProfile profile, AgentProfile.PartOption[] options,
                           SkinnedMeshRenderer renderer, AgentGender gender)
    {
        var record = new AppliedPartRecord();   // observation only: nothing below depends on it
        if (renderer == null) return record;
        record.hasRenderer = true;
        record.mesh = renderer.sharedMesh;
        record.materials = renderer.sharedMaterials;
        if (options == null || options.Length == 0) return record;   // optionIndex stays -2: the profile offers nothing for this part

        var chosen = profile.ChooseWeightedOption(options, gender);
        record.optionIndex = chosen == null ? -1 : System.Array.IndexOf(options, chosen);

        if (chosen == null)
        {
            // The array has options but NONE are valid for this gender. This is a profile
            // configuration error for a gendered part. Do NOT leave the current mesh in place:
            // on pool reuse it could be the opposite gender's mesh from the previous NPC.
            if (renderer.sharedMesh != null)
            {
                renderer.sharedMesh = null;
                if (enableDebugLogs)
                    Debug.LogWarning($"[AppearanceController] '{renderer.name}' on '{gameObject.name}': " +
                                     $"no PartOption is available for gender {gender}. Renderer cleared to " +
                                     $"avoid a cross-gender mesh. Add at least one Unisex/{gender} option.");
            }
            record.mesh = renderer.sharedMesh;
            return record;
        }

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
            record.materialIndices = new int[slotCount];
            for (int s = 0; s < slotCount; s++)
            {
                int matIndex = Random.Range(0, chosen.materials.Length);
                record.materialIndices[s] = matIndex;
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
            record.materials = newMats;   // the chosen source assets, not the runtime "(Instance)" copies
        }

        record.mesh = renderer.sharedMesh;
        return record;
    }
}