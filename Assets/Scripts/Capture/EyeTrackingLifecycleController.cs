// EyeTrackingLifecycleController.cs
// MonoBehaviour host for EyeTrackingLifecycle (see that file for the state machine, the ownership table and the two execution paths). It builds the REAL
// EyeTrackingPlatform from the installed Meta XR SDK 201 public API (OVRPlugin / OVRManager / OVRPermissionsRequester + UnityEngine.Android.Permission) - no private API,
// no reflection - and ticks the lifecycle once per frame. GazeRecorder creates it automatically (fail-open) when the scene has none, so recording never depends on
// Awake ordering or on a scene component.
//
// Rules this component follows
//   * It never reads gaze POSE/CONFIDENCE (GazeRecorder is the only reader of gaze data). Its only provider read is the validity flag per eye (OVRPlugin.GetEyeGazesState),
//     used to tell a live provider from a stale OVREyeGaze.Confidence (OVREyeGaze leaves Confidence untouched on invalid samples).
//   * It never calls OVRPlugin.StartEyeTracking itself; it enables/re-enables the two OVREyeGaze components, whose OnEnable is the SDK's start path.
//   * It never moves the rig or touches NavMesh / locomotion state.
// All comments in English per project convention.

using System;
using UnityEngine;

[DisallowMultipleComponent]
public class EyeTrackingLifecycleController : MonoBehaviour
{
    [Header("Eye sources (auto-filled from GazeRecorder when empty)")]
    [Tooltip("OVREyeGaze with Eye = Left. If empty it is taken from the scene's GazeRecorder.")]
    public OVREyeGaze leftEyeGaze;
    [Tooltip("OVREyeGaze with Eye = Right. If empty it is taken from the scene's GazeRecorder.")]
    public OVREyeGaze rightEyeGaze;

    [Header("Lifecycle")]
    [Tooltip("Master switch. When false the lifecycle reports 'not_enabled' and never touches the OVREyeGaze components (legacy behaviour: whatever OVREyeGaze did on its own).")]
    public bool enableLifecycle = true;
    [Tooltip("Retry / backoff / tracking-unavailable thresholds. Defaults are bounded: capability 6 checks, 8 start attempts per episode, 5 recovery episodes.")]
    public EyeTrackingLifecycleOptions options = new EyeTrackingLifecycleOptions();

    [Header("Debug")]
    [Tooltip("Gates transition logs (state changes only - never per-frame data).")]
    public bool enableDebugLogs = true;

    /// <summary>The state machine; null until Awake. Real platform unless a test replaced it before the first Update.</summary>
    public EyeTrackingLifecycle Lifecycle { get; private set; }

    /// <summary>Non-empty when an OVREyeGaze lives directly on an OVRCameraRig eye anchor (the rig rewrites that transform after OVREyeGaze writes the gaze pose).</summary>
    [NonSerialized] public string HostConflict = "";   // a field (not a property) so tests can set it; written only by this component

    GazeRecorder _recorder;
    bool _resolved;
    OVRPlugin.EyeGazesState _gazeStateBuf;
    double _providerTime = double.NegativeInfinity;

    void Awake()
    {
        Lifecycle = new EyeTrackingLifecycle(BuildPlatform(), options ?? new EyeTrackingLifecycleOptions());
        Lifecycle.Transitioned += OnTransitioned;
    }

    void Start() { ResolveSources(); }

    void ResolveSources()
    {
        if (_resolved) return;
        if (_recorder == null) _recorder = FindObjectOfType<GazeRecorder>();
        if (_recorder != null)
        {
            if (leftEyeGaze == null) leftEyeGaze = _recorder.leftEyeGaze;
            if (rightEyeGaze == null) rightEyeGaze = _recorder.rightEyeGaze;
        }
        HostConflict = DetectHostConflict(leftEyeGaze, rightEyeGaze);
        if (HostConflict.Length > 0)
            Debug.LogWarning("[EyeTrackingLifecycle] An OVREyeGaze is attached directly to an OVRCameraRig eye anchor (" + HostConflict + "). OVRCameraRig rewrites that transform every frame, " +
                "so the recorded gaze direction/origin is NOT the eye-gaze pose. Move the OVREyeGaze components to independent objects (see docs/eye_tracking_verification.md).");
        _resolved = true;
    }

    void Update()
    {
        if (Lifecycle == null) return;
        if (!_resolved) ResolveSources();
        if (!enableLifecycle) { Lifecycle.DisableByConfiguration(CaptureClock.TAbs); return; }
        Lifecycle.Tick(CaptureClock.TAbs);
    }

    void OnApplicationPause(bool paused)
    {
        if (Lifecycle == null) return;
        if (paused) Lifecycle.OnPause(CaptureClock.TAbs); else Lifecycle.OnResume(CaptureClock.TAbs);
    }

    void OnDestroy() { Shutdown(); }
    void OnApplicationQuit() { Shutdown(); }

    void Shutdown()
    {
        if (Lifecycle == null) return;
        Lifecycle.Transitioned -= OnTransitioned;
        Lifecycle.Shutdown(CaptureClock.TAbs);
    }

    void OnTransitioned(EyeTrackingTransition t)
    {
        if (!enableDebugLogs) return;
        if (t.event_name == "application_paused" || t.event_name == "application_resumed") return;
        Debug.Log($"[EyeTrackingLifecycle] {t.event_name}: {t.from} -> {t.to} ({t.reason}) permission={t.permission_state} capability={t.capability_state} tracking_enabled={t.tracking_enabled} runtime={Lifecycle.Platform.RuntimePath}");
    }

    /// <summary>Operator hook (for example from a debug UI): retry a failed/denied/unsupported acquisition. allowPrompt=true may show the permission dialog once more.</summary>
    public void RetryNow(bool allowPrompt) { if (Lifecycle != null) Lifecycle.RequestRetry(CaptureClock.TAbs, allowPrompt); }

    // ------------------------------------------------------------------ real platform

    public static string DetectRuntimePath()
    {
#if UNITY_EDITOR
        return Application.platform == RuntimePlatform.WindowsEditor ? "windows_editor_openxr" :
               Application.platform == RuntimePlatform.OSXEditor ? "mac_editor" : "editor_" + Application.platform.ToString().ToLowerInvariant();
#elif UNITY_ANDROID
        return "android_standalone";
#else
        return "player_" + Application.platform.ToString().ToLowerInvariant();
#endif
    }

    // Only an OVREyeGaze that IS the eye anchor transform is overwritten by the rig (OVREyeGaze runs at -80, OVRCameraRig at -50, both in Update).
    public static string DetectHostConflict(OVREyeGaze left, OVREyeGaze right)
    {
        string r = "";
        r += ConflictOf(left, "left");
        r += ConflictOf(right, "right");
        return r;
    }

    static string ConflictOf(OVREyeGaze g, string label)
    {
        if (g == null) return "";
        OVRCameraRig rig = g.GetComponentInParent<OVRCameraRig>();
        if (rig == null) return "";
        Transform t = g.transform;
        bool onAnchor = (rig.leftEyeAnchor != null && t == rig.leftEyeAnchor) || (rig.rightEyeAnchor != null && t == rig.rightEyeAnchor) || (rig.centerEyeAnchor != null && t == rig.centerEyeAnchor);
        return onAnchor ? (label + "_on_rig_eye_anchor;") : "";
    }

    OVREyeGaze ComponentAt(int i) => i == 0 ? leftEyeGaze : rightEyeGaze;

    bool UsesWorldSpace()
    {
        return (leftEyeGaze != null && leftEyeGaze.TrackingMode == OVREyeGaze.EyeTrackingMode.WorldSpace) ||
               (rightEyeGaze != null && rightEyeGaze.TrackingMode == OVREyeGaze.EyeTrackingMode.WorldSpace);
    }

    EyeTrackingPlatform BuildPlatform()
    {
        var p = new EyeTrackingPlatform { RuntimePath = DetectRuntimePath() };
#if UNITY_ANDROID && !UNITY_EDITOR
        p.PermissionApplicable = true;
#endif
        p.XrReady = () => OVRManager.OVRManagerinitialized || OVRPlugin.initialized;
        p.Supported = () => OVRPlugin.eyeTrackingSupported;
        p.TrackingEnabled = () => OVRPlugin.eyeTrackingEnabled;
        p.PermissionGranted = () => OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.EyeTracking);
        p.WorldFrameReady = () => !UsesWorldSpace() || Camera.main != null;
        p.RequestPermission = RequestPermissionOnce;
        p.ComponentPresent = i => ComponentAt(i) != null;
        p.ComponentEnabled = i => { var c = ComponentAt(i); return c != null && c.enabled; };
        p.SetComponentEnabled = (i, on) => { var c = ComponentAt(i); if (c != null) c.enabled = on; };
        p.ReadProvider = ReadProviderMask;
        p.ProviderTime = () => _providerTime;
        return p;
    }

    // Validity flags only (no pose, no confidence): OVRPlugin.GetEyeGazesState is the same public call OVREyeGaze.Update makes.
    int ReadProviderMask()
    {
        if (!OVRPlugin.GetEyeGazesState(OVRPlugin.Step.Render, -1, ref _gazeStateBuf)) return 0;
        _providerTime = _gazeStateBuf.Time;
        int mask = EyeProviderBits.QueryOk;
        var gazes = _gazeStateBuf.EyeGazes;
        if (gazes != null && gazes.Length >= 2)
        {
            if (gazes[(int)OVRPlugin.Eye.Left].IsValid) mask |= EyeProviderBits.LeftValid;
            if (gazes[(int)OVRPlugin.Eye.Right].IsValid) mask |= EyeProviderBits.RightValid;
        }
        return mask;
    }

    // The single permission dialog. Uses the public Unity API directly (OVRPermissionsRequester only logs a denial and exposes no denied event).
    static void RequestPermissionOnce(Action<int> done)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        var cb = new UnityEngine.Android.PermissionCallbacks();
        cb.PermissionGranted += _ => done(EyePermissionOutcome.Granted);
        cb.PermissionDenied += _ => done(EyePermissionOutcome.Denied);
        cb.PermissionDeniedAndDontAskAgain += _ => done(EyePermissionOutcome.DeniedPermanently);
        UnityEngine.Android.Permission.RequestUserPermission(OVRPermissionsRequester.GetPermissionId(OVRPermissionsRequester.Permission.EyeTracking), cb);
#else
        done(EyePermissionOutcome.Error);   // never reached: PermissionApplicable is false off-device
#endif
    }
}
