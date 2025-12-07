using UnityEngine;

/// <summary>
/// Apply appearance choices from AgentProfile to the instance. Keep it small and overridable.
/// </summary>
public class AppearanceController : MonoBehaviour
{
    // hook points for body parts or renderers
    public Renderer clothingRenderer;
    public GameObject[] variantObjects;

    /// <summary>
    /// Apply profile visuals. Non-destructive: only toggles things if profile supplies them.
    /// </summary>
    public void ApplyProfile(AgentProfile profile)
    {
        // choose variant if provided
        if (variantObjects != null && variantObjects.Length > 0 && profile.bodyVariants != null && profile.bodyVariants.Length > 0)
        {
            // simple behavior: enable objects count match; or you could instantiate variant prefab etc.
            for (int i=0;i<variantObjects.Length;i++)
            {
                variantObjects[i].SetActive(i < profile.bodyVariants.Length);
            }
        }

        // apply material/colors
        if (clothingRenderer != null)
        {
            if (profile.clothingMaterials != null && profile.clothingMaterials.Length > 0)
            {
                clothingRenderer.material = profile.clothingMaterials[Random.Range(0, profile.clothingMaterials.Length)];
            }
            else if (profile.clothingColors != null && profile.clothingColors.Length > 0)
            {
                clothingRenderer.material.color = profile.clothingColors[Random.Range(0, profile.clothingColors.Length)];
            }
        }
    }
}
