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
             "option for the rolled gender, and one line per ApplyProfile with the accessory (wristband) that was applied " +
             "(option, mesh, material). Enable while setting up the Agent Profile; never logged per frame.")]
    public bool enableDebugLogs = false;

    // The prefab's own accessory material slots, cached before the first accessory reset. Only used as a fallback when a bracelet option
    // supplies a mesh but an empty material list (a profile configuration error), so the renderer never shows null (pink) materials.
    private Material[] authoredAccessoryMaterials;
    // The prefab's own accessory mesh, cached with the materials. Only used to restore the authored look after a suppressed (no-accessory) lifetime
    // when the next profile offers no accessory options at all.
    private Mesh authoredAccessoryMesh;
    // True while the accessory renderer is empty because of ApplyProfile(..., suppressAccessory: true) rather than because of a profile draw.
    private bool accessoryClearedBySuppression;

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
        ApplyProfile(profile, profile.ChooseGender(), false);
    }

    /// <summary>
    /// Part 3: as <see cref="ApplyProfile(AgentProfile)"/>, but with suppressAccessory = true the accessory (wristband) is NOT drawn from the profile: the renderer is
    /// cleared and the NPC wears none (used for Part 3 wandering NPCs). suppressAccessory = false is identical to the single-argument overload.
    /// </summary>
    public void ApplyProfile(AgentProfile profile, bool suppressAccessory)
    {
        if (profile == null) return;
        ApplyProfile(profile, profile.ChooseGender(), suppressAccessory);
    }

    /// <summary>
    /// Apply the profile visuals with an explicitly forced gender (e.g. for a future
    /// experiment gender condition). Existing callers use the single-argument overload.
    /// </summary>
    public void ApplyProfile(AgentProfile profile, AgentGender gender)
    {
        ApplyProfile(profile, gender, false);
    }

    /// <summary>Full form: forced gender and the Part 3 accessory switch (see <see cref="ApplyProfile(AgentProfile, bool)"/>).</summary>
    public void ApplyProfile(AgentProfile profile, AgentGender gender, bool suppressAccessory)
    {
        ApplyProfileCore(profile, gender, suppressAccessory, 1f, ExperimentRngManager.UnassignedNpcRng.Appearance);
    }

    /// <summary>
    /// Part 2 accessory distribution matching: as <see cref="ApplyProfile(AgentProfile, bool)"/>, with the accessory drawn from the profile's weights adjusted by
    /// <see cref="AccessoryWeightAdjuster"/> for the effective Part 3 attractor probability accessoryWeightScale (wristband weights x scale, remainder to None). The
    /// profile asset is not modified. accessoryWeightScale = 1 is identical to <see cref="ApplyProfile(AgentProfile, bool)"/>; it has no effect when suppressAccessory is true.
    /// </summary>
    public void ApplyProfile(AgentProfile profile, bool suppressAccessory, float accessoryWeightScale)
    {
        if (profile == null) return;
        ApplyProfile(profile, suppressAccessory, accessoryWeightScale, null);
    }

    /// <summary>
    /// As above, drawing EVERY appearance random value (gender, option choices, material choices, accessory) from appearanceRng, the NPC's own Appearance stream of the
    /// experiment RNG. null = the shared "unassigned" Appearance stream (deterministic, never UnityEngine.Random).
    /// </summary>
    public void ApplyProfile(AgentProfile profile, bool suppressAccessory, float accessoryWeightScale, Pcg32 appearanceRng)
    {
        if (profile == null) return;
        Pcg32 rng = appearanceRng ?? ExperimentRngManager.UnassignedNpcRng.Appearance;
        ApplyProfileCore(profile, profile.ChooseGender(rng), suppressAccessory, accessoryWeightScale, rng);
    }

    /// <summary>Observation only: the accessory weight scale used by the most recent ApplyProfile call (1 = the profile's own weights).</summary>
    public float LastAccessoryWeightScale { get; private set; } = 1f;

    private void ApplyProfileCore(AgentProfile profile, AgentGender gender, bool suppressAccessory, float accessoryWeightScale, Pcg32 rng)
    {
        if (profile == null) return;

        // Gender is decided ONCE here and reused for every gender-restricted part below.
        CurrentGender = gender;
        float scale = AccessoryWeightAdjuster.SanitizeScale(accessoryWeightScale);
        LastAccessoryWeightScale = suppressAccessory ? 1f : scale;

        var applied = new AppliedAppearance { appliedTAbs = CaptureClock.TAbs, gender = gender };
        applied.body        = ApplyPart(profile, profile.bodyOptions,        bodyRenderer,        CurrentGender, rng);
        applied.hair        = ApplyPart(profile, profile.hairOptions,        hairRenderer,        CurrentGender, rng);
        applied.top         = ApplyPart(profile, profile.topOptions,         topRenderer,         CurrentGender, rng);
        applied.bottom      = ApplyPart(profile, profile.bottomOptions,      bottomRenderer,      CurrentGender, rng);
        applied.shoes       = ApplyPart(profile, profile.shoesOptions,       shoesRenderer,       CurrentGender, rng);
        applied.accessories = suppressAccessory
            ? SuppressAccessory()
            : ApplyPart(profile, scale < 1f ? AccessoryWeightAdjuster.GetOptions(profile, scale) : profile.accessoriesOptions, accessoriesRenderer, CurrentGender, rng, isAccessory: true);
        LastApplied = applied;

        if (enableDebugLogs && accessoriesRenderer != null)
        {
            AppliedPartRecord a = applied.accessories;
            Debug.Log($"[AppearanceController] '{gameObject.name}' (id {gameObject.GetInstanceID()}) accessories: option {a.optionIndex}, " +
                      (accessoriesRenderer.sharedMesh != null
                          ? $"bracelet mesh '{a.MeshName}', material '{a.MaterialNames}' (index {(a.materialIndices != null && a.materialIndices.Length > 0 ? a.materialIndices[0] : -1)})"
                          : "no bracelet (renderer cleared)"), this);
        }
    }

    /// <summary>
    /// Part 3 rule for wandering NPCs: no accessory, whatever the profile offers. No profile draw happens (so no UnityEngine.Random value is consumed for the
    /// accessory); the renderer is emptied exactly as ApplyPart empties it before a draw, so a pooled object cannot keep the previous lifetime's bracelet.
    /// Recorded as option -3 (suppressed) with no mesh.
    /// </summary>
    private AppliedPartRecord SuppressAccessory()
    {
        var record = new AppliedPartRecord();
        SkinnedMeshRenderer renderer = accessoriesRenderer;
        if (renderer == null) return record;
        record.hasRenderer = true;
        if (authoredAccessoryMaterials == null) { authoredAccessoryMaterials = renderer.sharedMaterials; authoredAccessoryMesh = renderer.sharedMesh; }
        ClearAccessoryRenderer(renderer);
        accessoryClearedBySuppression = true;
        record.optionIndex = AppliedPartRecord.SuppressedOption;
        record.mesh = null;
        record.materials = renderer.sharedMaterials;
        return record;
    }

    /// <summary>
    /// Apply one part's chosen option to the provided renderer.
    /// Uses the profile's gender-filtered chooser to pick a PartOption by weight.
    /// Unisex options are always eligible, so shoes/accessories/body are unaffected.
    ///
    /// isAccessory (the wristband renderer only): the renderer is explicitly CLEARED (mesh null, material slots null, same slot count) before the
    /// option is applied, so a pooled NPC can never keep the previous spawn's bracelet; an option with no mesh and no materials ("no bracelet")
    /// therefore leaves it empty. One material is drawn uniformly from the option's Materials list per application (the list is the set of
    /// colour ALTERNATIVES) and assigned to every slot as a shared asset, so the bracelet is a single colour and no per-NPC material instance is made.
    /// </summary>
    private AppliedPartRecord ApplyPart(AgentProfile profile, AgentProfile.PartOption[] options,
                           SkinnedMeshRenderer renderer, AgentGender gender, Pcg32 rng, bool isAccessory = false)
    {
        var record = new AppliedPartRecord();   // observation only: nothing below depends on it
        if (renderer == null) return record;
        record.hasRenderer = true;
        record.mesh = renderer.sharedMesh;
        record.materials = renderer.sharedMaterials;
        if (options == null || options.Length == 0)
        {
            // optionIndex stays -2: the profile offers nothing for this part (the prefab's own look stays). After a suppressed lifetime the renderer is empty,
            // so the authored accessory is put back: a profile with no accessory options behaves as it always did.
            if (isAccessory && accessoryClearedBySuppression && authoredAccessoryMaterials != null)
            {
                renderer.sharedMesh = authoredAccessoryMesh;
                renderer.sharedMaterials = authoredAccessoryMaterials;
                accessoryClearedBySuppression = false;
                record.mesh = renderer.sharedMesh;
                record.materials = renderer.sharedMaterials;
            }
            return record;
        }

        if (isAccessory)
        {
            if (authoredAccessoryMaterials == null) { authoredAccessoryMaterials = renderer.sharedMaterials; authoredAccessoryMesh = renderer.sharedMesh; }
            accessoryClearedBySuppression = false;
            ClearAccessoryRenderer(renderer);
            record.mesh = null;
            record.materials = renderer.sharedMaterials;
        }

        var chosen = profile.ChooseWeightedOption(options, gender, rng);
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
            int accessoryDraw = isAccessory ? rng.Range(0, chosen.materials.Length) : -1;   // ONE draw for the whole bracelet
            for (int s = 0; s < slotCount; s++)
            {
                int matIndex = isAccessory ? accessoryDraw : rng.Range(0, chosen.materials.Length);
                record.materialIndices[s] = matIndex;
                Material baseMat = chosen.materials[matIndex];

                // If the selected base material is null, fallback to existing material in that slot if possible
                if (baseMat == null)
                {
                    Material[] fallback = isAccessory ? authoredAccessoryMaterials : renderer.sharedMaterials;
                    newMats[s] = (fallback != null && fallback.Length > s) ? fallback[s] : null;
                }
                else
                {
                    // Assign the material instance to renderer.materials array.
                    // `renderer.materials` will create instances as needed.
                    newMats[s] = baseMat;
                }
            }

            // Set materials (this will create instances so each renderer can be modified independently at runtime).
            // The accessory takes the shared assets directly: no per-NPC material instance.
            if (isAccessory) renderer.sharedMaterials = newMats;
            else renderer.materials = newMats;
            record.materials = newMats;   // the chosen source assets, not the runtime "(Instance)" copies
        }
        else if (isAccessory && chosen.mesh != null)
        {
            // A bracelet mesh without any material is a profile configuration error: fall back to the prefab's own slots instead of null (pink).
            if (authoredAccessoryMaterials != null) renderer.sharedMaterials = authoredAccessoryMaterials;
            record.materials = renderer.sharedMaterials;
            if (enableDebugLogs)
                Debug.LogWarning($"[AppearanceController] accessories option {record.optionIndex} on '{gameObject.name}' has a mesh but no materials; using the prefab's own material.");
        }

        record.mesh = renderer.sharedMesh;
        return record;
    }

    /// <summary>Empty the wristband renderer: no mesh, every material slot null (slot count kept, at least one).</summary>
    private static void ClearAccessoryRenderer(SkinnedMeshRenderer renderer)
    {
        Material[] current = renderer.sharedMaterials;
        int slots = Mathf.Max(1, current != null ? current.Length : 0);
        renderer.sharedMesh = null;
        renderer.sharedMaterials = new Material[slots];
    }
}