// ExplorationBoundaryWall.cs
//
// Marker + provenance record on every object created by the Exploration Boundary Generator
// (Assets/Editor/ExplorationBoundaryEditor.cs). It has NO runtime behaviour. It exists so that
//   * the Generator can recognise exactly the objects it owns and replace only those on regeneration, and
//   * each wall section carries, in the Scene, how its height was obtained (measured / partial / inferred / global
//     envelope fallback) and the numbers behind it.
//
// All comments in English per project convention.

using UnityEngine;

[DisallowMultipleComponent]
public class ExplorationBoundaryWall : MonoBehaviour
{
    public enum Kind { Group, Section, Post }
    public enum HeightSource { None, Measured, Partial, Inferred, GlobalEnvelopeFallback }

    [Header("Generator record (informational)")]
    public Kind kind = Kind.Section;
    public int runIndex;
    public int sectionIndex;
    public HeightSource heightSource = HeightSource.None;
    [Tooltip("Valid ground samples / total samples that were taken for this section.")]
    public int validSamples, totalSamples;
    [Tooltip("Lowest / highest ground used for this section (world Y, metres).")]
    public float groundMin, groundMax;
    [Tooltip("World Y of the collider bottom / top.")]
    public float colliderBottom, colliderTop;
    [TextArea] public string note;
}
