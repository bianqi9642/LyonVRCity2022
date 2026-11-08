// GazeConfidence.cs
// What gaze.csv's left_confidence / right_confidence / combined_confidence mean, and how their availability and behaviour are diagnosed. Pure C# (unit-testable).
//
// SOURCE OF THE VALUE (traced through the installed Meta XR SDK Core 201.0.0, not assumed)
//   native eye-tracking runtime (OpenXR XR_FB_eye_tracking_social gazeConfidence, via OVRPlugin) -> OVRPlugin.GetEyeGazesState -> EyeGazeState.Confidence (the managed wrapper only unrolls the
//   marshalled struct, OVRPlugin.cs GetEyeGazesState) -> OVREyeGaze.Confidence (OVREyeGaze.cs assigns it only for a VALID provider sample: for an invalid one it returns BEFORE the assignment, so
//   the property keeps its previous, stale value) -> GazeRecorder.ReadEye. No managed code in the SDK or in this project transforms or defaults the number, and this project writes no 0.5 constant:
//   0.5 appears only as the ACCEPTANCE THRESHOLD (GazeRecorder.minConfidenceForValid, OVREyeGaze.ConfidenceThreshold). A recorded value of exactly 0.5 on every row therefore came out of the native
//   provider on that runtime (Windows Editor / OpenXR Display), i.e. a provider value that did not vary - it is recorded as such and flagged by ConfidenceDiagnostics, never presented as a quality metric.
//
// RECORDED SEMANTICS (schema v11)
//   left_confidence / right_confidence : the RAW provider value of that eye for THIS sample, formatted F3, or BLANK when no usable provider value exists for the sample (no component, provider flagged
//                                        the eye invalid so the SDK value is stale, NaN / infinity). A genuine 0 is "0.000"; blank is never 0 and never a default.
//   combined_confidence                : the mean of the raw confidences of the valid eyes that contributed to the combined ray, defined ONLY when every contributing eye has a value (one valid eye: that
//                                        eye's value); blank for the head-fallback ray or when a contributing eye has none. A project-derived number, documented as such.
//   Validity (combined_valid, left_valid, right_valid) : provider valid AND (confidence >= minConfidenceForValid when a confidence is available). A provider-valid eye WITHOUT a usable confidence is valid
//                                        (the threshold cannot reject what was not measured) - missing confidence never invalidates a ray, and the threshold is never written into an observation column.
// All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.Globalization;

public static class GazeConfidence
{
    public static bool Usable(float c) => !(float.IsNaN(c) || float.IsInfinity(c));

    /// <summary>
    /// Validity of ONE eye from the provider flag and the raw confidence. With a measured confidence the project threshold accepts / rejects it; with none, a provider-valid eye stays valid (the threshold
    /// cannot reject what was not measured) and an eye the provider did not flag valid - or an unknown provider - is not valid.
    /// </summary>
    public static bool IsValid(float confidence, bool confidenceAvailable, float threshold, bool providerKnown, bool providerValid)
        => confidenceAvailable ? confidence >= threshold : (providerKnown && providerValid);

    /// <summary>The SDK keeps its previous Confidence when the provider flags an eye invalid: such a number is not an observation of this sample.</summary>
    public static bool AvailableAfterProvider(bool usable, bool providerKnown, bool providerValid) => usable && !(providerKnown && !providerValid);

    /// <summary>F3 of an available value, blank otherwise (never "0.000" for a missing measurement).</summary>
    public static string Format(float c, bool available) => available ? c.ToString("F3", CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// Combined confidence of the valid eyes: both valid -> the mean, only if BOTH have a confidence; one valid -> that eye's, if it has one; none valid -> unavailable.
    /// </summary>
    public static bool TryCombine(bool leftValid, bool leftHas, float left, bool rightValid, bool rightHas, float right, out float combined)
    {
        combined = 0f;
        if (leftValid && rightValid) { if (!leftHas || !rightHas) return false; combined = (left + right) * 0.5f; return true; }
        if (leftValid) { if (!leftHas) return false; combined = left; return true; }
        if (rightValid) { if (!rightHas) return false; combined = right; return true; }
        return false;
    }
}

/// <summary>Running behaviour of one eye's raw provider confidence over the recorded rows (bounded memory: a small set of distinct values, running min / max / sum).</summary>
[Serializable]
public sealed class ConfidenceStream
{
    public const int MinSamplesForVerdict = 50;
    public const int MaxDistinctTracked = 64;

    public long samples;                 // rows with an available confidence
    public double min = -1, max = -1, mean = -1;
    public int distinct_values;          // distinct F3-rounded values seen (capped at MaxDistinctTracked)
    public string behaviour = "no_samples";   // no_samples | insufficient_samples | constant | varying

    double _sum;
    readonly HashSet<int> _seen = new HashSet<int>();

    public void Observe(float c)
    {
        if (!GazeConfidence.Usable(c)) return;
        samples++; _sum += c;
        if (samples == 1 || c < min) min = c;
        if (samples == 1 || c > max) max = c;
        mean = _sum / samples;
        if (_seen.Count < MaxDistinctTracked) _seen.Add((int)Math.Round(c * 1000.0));
        distinct_values = _seen.Count;
        behaviour = samples < MinSamplesForVerdict ? "insufficient_samples" : (_seen.Count == 1 ? "constant" : "varying");
    }
}

/// <summary>Manifest block: what the recorded confidence is and whether it carries information on this runtime.</summary>
[Serializable]
public sealed class ConfidenceManifest
{
    public string provider_field = "OVRPlugin.EyeGazeState.Confidence (native XR_FB_eye_tracking_social gazeConfidence) via OVREyeGaze.Confidence";
    public string documented_range = "0..1 (provider documentation; the SDK passes the value through unchanged, nothing here clamps or rescales it)";
    public string columns_meaning = "left_confidence / right_confidence = raw provider value of that eye for the sample, blank when none exists; combined_confidence = mean of contributing valid eyes' raw values, blank unless every contributing eye has one";
    public string threshold_role = "minConfidenceForValid is an ACCEPTANCE THRESHOLD applied to the raw value; it is never written into an observation column";
    public float project_acceptance_threshold;
    public ConfidenceStream left = new ConfidenceStream(), right = new ConfidenceStream();
    public long rows_confidence_unavailable_left, rows_confidence_unavailable_right;
    // overall verdict: varying_provider_confidence | constant_on_this_runtime | insufficient_samples | no_samples. "constant" means the provider reported one single value for every
    // row so far (>= 50 samples): the number is preserved but carries no quality information on this runtime.
    public string semantics = "no_samples";

    public void Refresh()
    {
        string a = left.behaviour, b = right.behaviour;
        if (a == "no_samples" && b == "no_samples") semantics = "no_samples";
        else if (a == "varying" || b == "varying") semantics = "varying_provider_confidence";
        else if (a == "constant" && (b == "constant" || b == "no_samples")) semantics = "constant_on_this_runtime";
        else if (b == "constant" && a == "no_samples") semantics = "constant_on_this_runtime";
        else semantics = "insufficient_samples";
    }
}
