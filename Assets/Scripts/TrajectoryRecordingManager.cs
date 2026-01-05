using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simple manager to control multiple TrajectoryRecorder components.
/// Attach this to a single GameObject (e.g., ExperimentController).
/// Use Register/Unregister if you spawn objects at runtime.
/// It can StartAll / StopAll; you can also pass subject/trial metadata to all recorders.
/// </summary>
public class TrajectoryRecordingManager : MonoBehaviour
{
    [Tooltip("If true, will find all TrajectoryRecorder components at Start() and register them.")]
    public bool autoRegisterAllInScene = true;

    private List<TrajectoryRecorder> recorders = new List<TrajectoryRecorder>();

    private void Start()
    {
        if (autoRegisterAllInScene)
        {
            var found = FindObjectsOfType<TrajectoryRecorder>();
            foreach (var r in found) RegisterRecorder(r);
        }
    }

    /// <summary>
    /// Register a recorder so manager can control it.
    /// </summary>
    public void RegisterRecorder(TrajectoryRecorder r)
    {
        if (r == null) return;
        if (!recorders.Contains(r)) recorders.Add(r);
    }

    /// <summary>
    /// Unregister (e.g., before destroying)
    /// </summary>
    public void UnregisterRecorder(TrajectoryRecorder r)
    {
        if (r == null) return;
        recorders.Remove(r);
    }

    /// <summary>
    /// Start recording on all registered recorders.
    /// Optionally override subject/trial metadata for all.
    /// </summary>
    public void StartAll(string subjectId = null, string trialId = null)
    {
        foreach (var r in recorders)
        {
            if (!string.IsNullOrEmpty(subjectId)) r.subjectId = subjectId;
            if (!string.IsNullOrEmpty(trialId)) r.trialId = trialId;
            r.StartRecording();
        }
    }

    /// <summary>
    /// Stop recording on all registered recorders.
    /// </summary>
    public void StopAll()
    {
        foreach (var r in recorders)
        {
            r.StopRecording();
        }
    }
}
