// EyeTrackingLifecycle.cs
// Project-owned eye-tracking ACQUISITION lifecycle (state machine + permission manager). Pure C#: every runtime dependency (XR readiness, capability,
// permission, the OVREyeGaze components, the provider validity query) is reached through EyeTrackingPlatform, so the production state machine is unit-testable with
// scripted fakes and the same class drives the real device.
//
// Why this exists
//   OVREyeGaze starts the global eye-tracking session in OnEnable(), disables ITSELF if that fails (no permission yet, XR not ready, extension unavailable) and never
//   retries on its own. Previously the only permission request lived in GazeRecorder.Awake (order-dependent, silently skipped when eyeTrackingSupported was still false), so
//   a start that failed once stayed failed for the whole session while GazeRecorder kept writing head-fallback rows that looked like a working-but-idle sensor.
//
// Ownership (one owner per concern)
//   * EyeTrackingLifecycle (this class)      : WHEN to start, the single permission request, bounded retries, pause/resume, shutdown, and the observable state.
//   * OVREyeGaze (left / right, Meta SDK)    : the actual OVRPlugin.StartEyeTracking() call (in its OnEnable) and the per-frame pose. This class never calls
//                                              StartEyeTracking itself - it only enables/re-enables the components - so there is exactly one start path in the SDK.
//   * GazeRecorder                           : the only reader of gaze DATA (confidence/pose) and the only gaze.csv writer. It consumes the observable state.
//   * OVRManager / OVRPermissionsRequester   : NOT used to request the eye-tracking permission (OVRManager.requestEyeTrackingPermissionOnStartup stays off) so the dialog
//                                              can never be requested twice by two owners.
//
// Execution paths that must not be confused
//   A) Windows Editor / PC OpenXR / Quest Link : the Android runtime permission does not exist (PermissionApplicable=false, permission_state = not_applicable).
//      Whether the extension is exposed is decided by the PC OpenXR runtime; "unsupported" there is reported with the runtime path in the reason and says nothing
//      about a standalone Quest Pro build.
//   B) Android Standalone (Quest Pro)         : com.oculus.permission.EYE_TRACKING is a runtime permission and must be granted before OVREyeGaze can start.
//
// All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.Globalization;

public enum EyeTrackingState
{
    Idle, NotApplicable, WaitingForXR, CheckingCapability, RequestingPermission, WaitingForPermission,
    Starting, Running, StartFailed, PermissionDenied, Unsupported, TrackingUnavailable, Stopped
}

public enum EyePermissionState { Unknown, NotApplicable, NotRequested, Requested, Granted, Denied, Error }

public enum EyeCapabilityState { Unknown, Supported, Unsupported }

public static class EyeTrackingNames
{
    public static string Of(EyeTrackingState s)
    {
        switch (s)
        {
            case EyeTrackingState.Idle: return "idle";
            case EyeTrackingState.NotApplicable: return "not_applicable";
            case EyeTrackingState.WaitingForXR: return "waiting_for_xr";
            case EyeTrackingState.CheckingCapability: return "checking_capability";
            case EyeTrackingState.RequestingPermission: return "requesting_permission";
            case EyeTrackingState.WaitingForPermission: return "waiting_for_permission";
            case EyeTrackingState.Starting: return "starting";
            case EyeTrackingState.Running: return "running";
            case EyeTrackingState.StartFailed: return "start_failed";
            case EyeTrackingState.PermissionDenied: return "permission_denied";
            case EyeTrackingState.Unsupported: return "unsupported";
            case EyeTrackingState.TrackingUnavailable: return "tracking_unavailable";
            default: return "stopped";
        }
    }

    public static string Of(EyePermissionState s)
    {
        switch (s)
        {
            case EyePermissionState.NotApplicable: return "not_applicable";
            case EyePermissionState.NotRequested: return "not_requested";
            case EyePermissionState.Requested: return "requested";
            case EyePermissionState.Granted: return "granted";
            case EyePermissionState.Denied: return "denied";
            case EyePermissionState.Error: return "error";
            default: return "unknown";
        }
    }

    public static string Of(EyeCapabilityState s)
    {
        switch (s)
        {
            case EyeCapabilityState.Supported: return "supported";
            case EyeCapabilityState.Unsupported: return "unsupported";
            default: return "unknown";
        }
    }
}

/// <summary>Permission request outcome codes used by the platform callback (ints so scripted fakes need no production type).</summary>
public static class EyePermissionOutcome
{
    public const int Granted = 0, Denied = 1, DeniedPermanently = 2, Error = 3;
}

/// <summary>Provider-validity bit mask returned by EyeTrackingPlatform.ReadProvider.</summary>
public static class EyeProviderBits
{
    public const int QueryOk = 1, LeftValid = 2, RightValid = 4;
}

/// <summary>
/// Everything the lifecycle needs from the outside world. The defaults are the real OVRPlugin / Android implementations (see EyeTrackingPlatformFactory);
/// tests replace individual delegates. A null delegate is treated as "unavailable / false" so a partial fake can never throw.
/// </summary>
public sealed class EyeTrackingPlatform
{
    /// <summary>"windows_editor_openxr", "mac_editor", "android_standalone", "player_*" ... - recorded in every diagnostic so the two execution paths stay distinguishable.</summary>
    public string RuntimePath = "unknown";
    /// <summary>True only where an Android runtime permission exists (Android Standalone build).</summary>
    public bool PermissionApplicable;

    public Func<bool> XrReady;
    public Func<bool> Supported;
    public Func<bool> TrackingEnabled;
    public Func<bool> PermissionGranted;
    /// <summary>The coordinate frame OVREyeGaze needs (Camera.main in WorldSpace mode) exists.</summary>
    public Func<bool> WorldFrameReady;
    /// <summary>Starts the (single) permission dialog; the argument is called once with an EyePermissionOutcome code.</summary>
    public Action<Action<int>> RequestPermission;

    public Func<int, bool> ComponentPresent;               // index 0 = left, 1 = right
    public Func<int, bool> ComponentEnabled;
    public Action<int, bool> SetComponentEnabled;

    /// <summary>Returns EyeProviderBits for the latest provider query (0 = query failed / no runtime).</summary>
    public Func<int> ReadProvider;
    /// <summary>Provider timestamp of the sample returned by the latest ReadProvider (advances only for a NEW provider sample).</summary>
    public Func<double> ProviderTime;

    public bool Call(Func<bool> f) { if (f == null) return false; try { return f(); } catch { return false; } }
}

/// <summary>One recorded lifecycle event (state change or notable event). Serializable straight into the manifest.</summary>
[Serializable]
public class EyeTrackingTransition
{
    public double t_abs;
    public string event_name = "";
    public string from = "", to = "", reason = "";
    public string permission_state = "", capability_state = "", tracking_enabled = "";
}

[Serializable]
public sealed class EyeTrackingLifecycleOptions
{
    public double xrPollInterval = 1.0;
    public double[] capabilityRetryDelays = { 1, 2, 4, 8, 15 };
    public int capabilityMaxChecks = 6;
    public double[] startRetryDelays = { 1, 2, 4, 8, 15, 30 };
    public int maxStartAttempts = 8;
    public int maxRecoveryEpisodes = 5;
    public double permissionPollInterval = 0.5;
    public double trackingUnavailableAfterSeconds = 3.0;
    public int maxTransitions = 96;
}

public sealed class EyeTrackingLifecycle
{
    public readonly EyeTrackingPlatform Platform;
    public readonly EyeTrackingLifecycleOptions Options;

    public EyeTrackingState State { get; private set; } = EyeTrackingState.Idle;
    public EyePermissionState Permission { get; private set; } = EyePermissionState.Unknown;
    public EyeCapabilityState Capability { get; private set; } = EyeCapabilityState.Unknown;
    public string LastReason { get; private set; } = "";
    public string LastResult { get; private set; } = "none";   // none | success | failure

    public int StartupAttempts { get; private set; }
    public int StartupSuccesses { get; private set; }
    public int StartupFailures { get; private set; }
    public int CapabilityChecks { get; private set; }
    public int PermissionRequests { get; private set; }
    public int RecoveryEpisodes { get; private set; }
    public int ResumeRetries { get; private set; }
    public int IgnoredLateCallbacks { get; private set; }

    /// <summary>Provider-level counters: a NEW provider sample (timestamp advanced) with at least one valid eye. Independent of how many gaze.csv rows were written.</summary>
    public long ProviderSamples { get; private set; }
    public long ProviderLeftValid { get; private set; }
    public long ProviderRightValid { get; private set; }
    public long ProviderAnyValid { get; private set; }
    public double FirstValidT { get; private set; } = double.NaN;
    public double LastValidT { get; private set; } = double.NaN;
    public double RunningSinceT { get; private set; } = double.NaN;

    /// <summary>Latest provider verdict per eye (false when the query failed or the lifecycle is not running). ProviderProbeAvailable = the last query succeeded.</summary>
    public bool LeftProviderValid { get; private set; }
    public bool RightProviderValid { get; private set; }
    public bool ProviderProbeAvailable { get; private set; }

    public bool IsPaused { get; private set; }

    /// <summary>Raised for every recorded transition / event (including "first_valid_provider_sample"). Listeners must not throw.</summary>
    public event Action<EyeTrackingTransition> Transitioned;

    readonly List<EyeTrackingTransition> _transitions = new List<EyeTrackingTransition>();
    public IReadOnlyList<EyeTrackingTransition> Transitions => _transitions;
    public int DroppedTransitions { get; private set; }

    double _nextActionT;
    int _episodeAttempts;
    bool _exhausted;
    int _requestEpoch;
    bool _outcomeConsumed;
    int _pendingOutcome = -1;
    double _lastProviderTs = double.NegativeInfinity;
    bool _denyPermanent;
    bool _forcePrompt;

    public EyeTrackingLifecycle(EyeTrackingPlatform platform, EyeTrackingLifecycleOptions options = null)
    {
        Platform = platform ?? new EyeTrackingPlatform();
        Options = options ?? new EyeTrackingLifecycleOptions();
    }

    // ------------------------------------------------------------------ derived, explicit acquisition status (never a default-false)

    /// <summary>unknown | not_applicable | not_enabled | unsupported | permission_required | permission_denied | initializing | initialization_failed | running | tracking_unavailable | stopped</summary>
    public string AcquisitionStatus
    {
        get
        {
            switch (State)
            {
                case EyeTrackingState.Idle: return "unknown";
                case EyeTrackingState.NotApplicable: return LastReason == "disabled_by_configuration" ? "not_enabled" : "not_applicable";
                case EyeTrackingState.WaitingForXR:
                case EyeTrackingState.CheckingCapability:
                case EyeTrackingState.RequestingPermission:
                case EyeTrackingState.Starting: return "initializing";
                case EyeTrackingState.WaitingForPermission: return "permission_required";
                case EyeTrackingState.PermissionDenied: return "permission_denied";
                case EyeTrackingState.Unsupported: return "unsupported";
                case EyeTrackingState.StartFailed: return "initialization_failed";
                case EyeTrackingState.Running: return "running";
                case EyeTrackingState.TrackingUnavailable: return "tracking_unavailable";
                default: return "stopped";
            }
        }
    }

    public bool IsAcquiring => State == EyeTrackingState.Running;

    public string ComponentState(int i)
    {
        if (!Platform.Call(() => Platform.ComponentPresent != null && Platform.ComponentPresent(i))) return "absent";
        return Platform.Call(() => Platform.ComponentEnabled != null && Platform.ComponentEnabled(i)) ? "enabled" : "disabled";
    }

    public double SecondsWithoutValid(double now)
    {
        // The reference is the later of the last valid provider sample and the start of the current observation window (running since / resumed at), so a pause is never counted.
        double a = LastValidT, b = RunningSinceT;
        double refT = double.IsNaN(a) ? b : (double.IsNaN(b) ? a : Math.Max(a, b));
        return double.IsNaN(refT) ? -1 : Math.Max(0, now - refT);
    }

    // ------------------------------------------------------------------ driving

    public void Tick(double now)
    {
        if (State == EyeTrackingState.Stopped || IsPaused) return;
        try { TickInner(now); }
        catch (Exception e) { LastReason = "lifecycle_exception: " + e.Message; Move(now, "exception", EyeTrackingState.StartFailed, LastReason); ScheduleRetryOrExhaust(now); }
    }

    void TickInner(double now)
    {
        switch (State)
        {
            case EyeTrackingState.Idle: Begin(now); break;
            case EyeTrackingState.WaitingForXR:
                if (now < _nextActionT) break;
                if (Platform.Call(Platform.XrReady)) Move(now, "xr_ready", EyeTrackingState.CheckingCapability, "xr_initialised");
                else { _nextActionT = now + Options.xrPollInterval; LastReason = "xr_not_initialized"; }
                break;
            case EyeTrackingState.CheckingCapability:
                if (now >= _nextActionT) EvaluateCapability(now);
                break;
            case EyeTrackingState.WaitingForPermission: PollPermission(now); break;
            case EyeTrackingState.Starting:
                if (now >= _nextActionT) DoStart(now);
                break;
            case EyeTrackingState.StartFailed:
                if (!_exhausted && now >= _nextActionT) Move(now, "retry", EyeTrackingState.Starting, "retry_after_backoff");
                break;
            case EyeTrackingState.Running:
            case EyeTrackingState.TrackingUnavailable: Monitor(now); break;
        }
    }

    void Begin(double now)
    {
        bool any = Platform.Call(() => Platform.ComponentPresent != null && (Platform.ComponentPresent(0) || Platform.ComponentPresent(1)));
        if (!any) { Move(now, "begin", EyeTrackingState.NotApplicable, "no_eye_gaze_components"); return; }
        Permission = Platform.PermissionApplicable ? EyePermissionState.NotRequested : EyePermissionState.NotApplicable;
        _episodeAttempts = 0; _exhausted = false;
        Move(now, "begin", EyeTrackingState.WaitingForXR, "lifecycle_started");
    }

    /// <summary>Marks the lifecycle as not applicable by configuration (eye tracking switched off for this session).</summary>
    public void DisableByConfiguration(double now) { if (State == EyeTrackingState.Idle) Move(now, "begin", EyeTrackingState.NotApplicable, "disabled_by_configuration"); }

    void EvaluateCapability(double now)
    {
        CapabilityChecks++;
        if (Platform.Call(Platform.Supported))
        {
            Capability = EyeCapabilityState.Supported;
            ProceedToPermission(now);
            return;
        }
        if (CapabilityChecks >= Options.capabilityMaxChecks)
        {
            Capability = EyeCapabilityState.Unsupported;
            Move(now, "capability", EyeTrackingState.Unsupported, "capability_unsupported_on_" + Platform.RuntimePath);
            return;
        }
        LastReason = "capability_not_yet_reported";
        _nextActionT = now + Delay(Options.capabilityRetryDelays, CapabilityChecks - 1);
    }

    void ProceedToPermission(double now)
    {
        if (!Platform.PermissionApplicable)
        {
            Permission = EyePermissionState.NotApplicable;
            Move(now, "permission", EyeTrackingState.Starting, "permission_not_applicable_on_" + Platform.RuntimePath);
            return;
        }
        if (Platform.Call(Platform.PermissionGranted))
        {
            Permission = EyePermissionState.Granted;
            Move(now, "permission", EyeTrackingState.Starting, "permission_already_granted");
            return;
        }
        if (_denyPermanent) { Permission = EyePermissionState.Denied; Move(now, "permission", EyeTrackingState.PermissionDenied, "permission_denied_dont_ask_again"); return; }
        if (Permission == EyePermissionState.Denied && !_forcePrompt) { Move(now, "permission", EyeTrackingState.PermissionDenied, "permission_previously_denied"); return; }

        // A single request per attempt episode. The dialog is never re-requested while one is outstanding or after a denial.
        _forcePrompt = false;
        PermissionRequests++;
        Permission = EyePermissionState.Requested;
        int epoch = ++_requestEpoch; _outcomeConsumed = false; _pendingOutcome = -1;
        Move(now, "permission_request", EyeTrackingState.RequestingPermission, "permission_missing");
        try
        {
            if (Platform.RequestPermission == null) throw new InvalidOperationException("no permission requester");
            Platform.RequestPermission(code => OnPermissionOutcome(epoch, code));
        }
        catch (Exception e)
        {
            Permission = EyePermissionState.Error;
            Move(now, "permission_result", EyeTrackingState.StartFailed, "permission_request_exception: " + e.Message);
            ScheduleRetryOrExhaust(now);
            return;
        }
        Move(now, "permission_request", EyeTrackingState.WaitingForPermission, "permission_dialog_requested");
        _nextActionT = now + Options.permissionPollInterval;
    }

    // Invoked by the platform (possibly late, possibly twice). Stale epochs (a later request, shutdown) and repeats are ignored.
    void OnPermissionOutcome(int epoch, int code)
    {
        if (epoch != _requestEpoch || State == EyeTrackingState.Stopped || _outcomeConsumed) { IgnoredLateCallbacks++; return; }
        _outcomeConsumed = true;
        _pendingOutcome = code;
    }

    void PollPermission(double now)
    {
        if (_pendingOutcome >= 0)
        {
            int code = _pendingOutcome; _pendingOutcome = -1;
            switch (code)
            {
                case EyePermissionOutcome.Granted:
                    Permission = EyePermissionState.Granted;
                    Move(now, "permission_result", EyeTrackingState.Starting, "permission_granted"); return;
                case EyePermissionOutcome.DeniedPermanently:
                    _denyPermanent = true; Permission = EyePermissionState.Denied;
                    Move(now, "permission_result", EyeTrackingState.PermissionDenied, "permission_denied_dont_ask_again"); return;
                case EyePermissionOutcome.Denied:
                    Permission = EyePermissionState.Denied;
                    Move(now, "permission_result", EyeTrackingState.PermissionDenied, "permission_denied_by_user"); return;
                default:
                    Permission = EyePermissionState.Error;
                    Move(now, "permission_result", EyeTrackingState.StartFailed, "permission_request_error");
                    ScheduleRetryOrExhaust(now); return;
            }
        }
        if (now < _nextActionT) return;
        _nextActionT = now + Options.permissionPollInterval;
        // The callback can be lost when the app is paused behind the system dialog; the granted state itself is authoritative.
        if (Platform.Call(Platform.PermissionGranted))
        {
            Permission = EyePermissionState.Granted; _requestEpoch++;   // any later callback of that request is stale
            Move(now, "permission_result", EyeTrackingState.Starting, "permission_granted_detected_by_poll");
        }
    }

    void DoStart(double now)
    {
        if (Platform.WorldFrameReady != null && !Platform.Call(Platform.WorldFrameReady))
        {
            LastReason = "world_frame_unavailable"; _nextActionT = now + Options.xrPollInterval; return;
        }

        StartupAttempts++; _episodeAttempts++;
        // OVREyeGaze.OnEnable() runs OVRPlugin.StartEyeTracking() and disables the component again if that fails. Only components that are not
        // already enabled are touched, so a healthy eye is never toggled (no duplicate start) while a failed one is retried.
        for (int i = 0; i < 2; i++)
        {
            if (!Platform.Call(() => Platform.ComponentPresent != null && Platform.ComponentPresent(i))) continue;
            if (!Platform.Call(() => Platform.ComponentEnabled != null && Platform.ComponentEnabled(i)))
            {
                try { Platform.SetComponentEnabled?.Invoke(i, true); } catch (Exception e) { LastReason = "enable_exception: " + e.Message; }
            }
        }

        string failed = "";
        for (int i = 0; i < 2; i++)
        {
            bool present = Platform.Call(() => Platform.ComponentPresent != null && Platform.ComponentPresent(i));
            if (present && !Platform.Call(() => Platform.ComponentEnabled != null && Platform.ComponentEnabled(i))) failed += (failed.Length > 0 ? "+" : "") + (i == 0 ? "left" : "right");
        }
        bool trackingOn = Platform.Call(Platform.TrackingEnabled);

        if (failed.Length == 0 && trackingOn)
        {
            StartupSuccesses++; LastResult = "success";
            RunningSinceT = now; LastProviderReset();
            Move(now, "start_success", EyeTrackingState.Running, StartupFailures > 0 ? "started_after_" + StartupFailures + "_failures" : "started");
            return;
        }

        StartupFailures++; LastResult = "failure";
        string reason = failed.Length > 0 ? "component_self_disabled:" + failed : "tracking_not_enabled_after_start";
        Move(now, "start_failure", EyeTrackingState.StartFailed, reason);
        ScheduleRetryOrExhaust(now);
    }

    void ScheduleRetryOrExhaust(double now)
    {
        if (_episodeAttempts >= Options.maxStartAttempts)
        {
            _exhausted = true;
            RecordEvent(now, "retries_exhausted", "bounded_retries_exhausted_after_" + _episodeAttempts + "_attempts");
            return;
        }
        _exhausted = false;
        _nextActionT = now + Delay(Options.startRetryDelays, Math.Max(0, _episodeAttempts - 1));
    }

    void Monitor(double now)
    {
        // A component that disabled itself while running (SDK-side failure, device removed...) is a recoverable failure, bounded by maxRecoveryEpisodes.
        string lost = "";
        for (int i = 0; i < 2; i++)
        {
            bool present = Platform.Call(() => Platform.ComponentPresent != null && Platform.ComponentPresent(i));
            if (present && !Platform.Call(() => Platform.ComponentEnabled != null && Platform.ComponentEnabled(i))) lost += (lost.Length > 0 ? "+" : "") + (i == 0 ? "left" : "right");
        }
        if (lost.Length > 0 || !Platform.Call(Platform.TrackingEnabled))
        {
            LeftProviderValid = RightProviderValid = false;
            RecoveryEpisodes++;
            string reason = lost.Length > 0 ? "component_disabled_while_running:" + lost : "tracking_disabled_while_running";
            Move(now, "tracking_lost", EyeTrackingState.StartFailed, reason);
            if (RecoveryEpisodes > Options.maxRecoveryEpisodes) { _exhausted = true; RecordEvent(now, "retries_exhausted", "recovery_episodes_exhausted"); return; }
            _episodeAttempts = 0; _exhausted = false; _nextActionT = now + Delay(Options.startRetryDelays, 0);
            return;
        }

        int mask = 0;
        try { mask = Platform.ReadProvider != null ? Platform.ReadProvider() : 0; } catch { mask = 0; }
        ProviderProbeAvailable = (mask & EyeProviderBits.QueryOk) != 0;
        LeftProviderValid = ProviderProbeAvailable && (mask & EyeProviderBits.LeftValid) != 0;
        RightProviderValid = ProviderProbeAvailable && (mask & EyeProviderBits.RightValid) != 0;
        bool anyNow = LeftProviderValid || RightProviderValid;

        double ts = _lastProviderTs;
        try { if (Platform.ProviderTime != null) ts = Platform.ProviderTime(); } catch { }
        if (ProviderProbeAvailable && ts > _lastProviderTs)
        {
            _lastProviderTs = ts;
            ProviderSamples++;
            if (LeftProviderValid) ProviderLeftValid++;
            if (RightProviderValid) ProviderRightValid++;
            if (anyNow)
            {
                ProviderAnyValid++;
                LastValidT = now;
                if (double.IsNaN(FirstValidT)) { FirstValidT = now; RecordEvent(now, "first_valid_provider_sample", "first_valid_eye_sample_from_provider"); }
            }
        }

        if (anyNow && State == EyeTrackingState.TrackingUnavailable) Move(now, "tracking_resumed", EyeTrackingState.Running, "valid_provider_samples_again");
        else if (!anyNow && State == EyeTrackingState.Running && SecondsWithoutValid(now) > Options.trackingUnavailableAfterSeconds)
            Move(now, "tracking_unavailable", EyeTrackingState.TrackingUnavailable, "no_valid_provider_samples_for_" + SecondsWithoutValid(now).ToString("F1", CultureInfo.InvariantCulture) + "s");
    }

    void LastProviderReset() { _lastProviderTs = double.NegativeInfinity; LeftProviderValid = RightProviderValid = false; }

    // ------------------------------------------------------------------ external events

    public void OnPause(double now)
    {
        if (State == EyeTrackingState.Stopped || IsPaused) return;
        IsPaused = true;
        RecordEvent(now, "application_paused", "application_paused");
    }

    public void OnResume(double now)
    {
        if (State == EyeTrackingState.Stopped || !IsPaused) return;
        IsPaused = false;
        RecordEvent(now, "application_resumed", "application_resumed");
        switch (State)
        {
            case EyeTrackingState.Running:
            case EyeTrackingState.TrackingUnavailable:
                RunningSinceT = now;   // the pause must not be read as "no valid samples"
                LastProviderReset();
                break;
            case EyeTrackingState.WaitingForPermission:
                break;   // the system dialog is exactly what paused the app: keep waiting, never request again
            case EyeTrackingState.PermissionDenied:
                if (Platform.PermissionApplicable && Platform.Call(Platform.PermissionGranted))
                {
                    Permission = EyePermissionState.Granted; _denyPermanent = false;
                    Move(now, "resume_recheck", EyeTrackingState.Starting, "permission_granted_in_system_settings");
                }
                break;
            case EyeTrackingState.StartFailed:
            case EyeTrackingState.Unsupported:
                ResumeRetries++;
                Restart(now, "resume_recheck", false);
                break;
        }
    }

    /// <summary>User/operator initiated retry (for example after fixing the permission in the system settings). allowPrompt=true may show the dialog once again.</summary>
    public void RequestRetry(double now, bool allowPrompt)
    {
        if (State == EyeTrackingState.Stopped) return;
        if (State == EyeTrackingState.Running || State == EyeTrackingState.TrackingUnavailable || State == EyeTrackingState.NotApplicable) return;
        if (State == EyeTrackingState.WaitingForPermission || State == EyeTrackingState.RequestingPermission) return;   // already in flight: no duplicate dialog
        Restart(now, "manual_retry", allowPrompt);
    }

    void Restart(double now, string eventName, bool allowPrompt)
    {
        _episodeAttempts = 0; _exhausted = false; CapabilityChecks = 0;
        if (allowPrompt) { _forcePrompt = true; _denyPermanent = false; }
        Move(now, eventName, EyeTrackingState.WaitingForXR, eventName);
        _nextActionT = now;
    }

    /// <summary>Terminal: no further enabling, requesting or retrying; a permission callback arriving afterwards is ignored.</summary>
    public void Shutdown(double now)
    {
        if (State == EyeTrackingState.Stopped) return;
        _requestEpoch++;
        Move(now, "shutdown", EyeTrackingState.Stopped, "shutdown_from_" + EyeTrackingNames.Of(State));
        LeftProviderValid = RightProviderValid = false;
    }

    // ------------------------------------------------------------------ bookkeeping

    static double Delay(double[] delays, int index)
    {
        if (delays == null || delays.Length == 0) return 1.0;
        return delays[Math.Min(Math.Max(index, 0), delays.Length - 1)];
    }

    void Move(double now, string eventName, EyeTrackingState to, string reason)
    {
        EyeTrackingState from = State;
        State = to; LastReason = reason;
        Record(now, eventName, from, to, reason);
    }

    void RecordEvent(double now, string eventName, string reason) { LastReason = reason; Record(now, eventName, State, State, reason); }

    void Record(double now, string eventName, EyeTrackingState from, EyeTrackingState to, string reason)
    {
        string tracking = "unknown";
        try { if (Platform.TrackingEnabled != null) tracking = Platform.TrackingEnabled() ? "true" : "false"; } catch { }
        var tr = new EyeTrackingTransition
        {
            t_abs = now, event_name = eventName, from = EyeTrackingNames.Of(from), to = EyeTrackingNames.Of(to), reason = reason ?? "",
            permission_state = EyeTrackingNames.Of(Permission), capability_state = EyeTrackingNames.Of(Capability), tracking_enabled = tracking
        };
        if (_transitions.Count >= Options.maxTransitions) { _transitions.RemoveAt(1); DroppedTransitions++; }   // keep index 0 (the start of the session)
        _transitions.Add(tr);
        try { Transitioned?.Invoke(tr); } catch { }
    }
}

/// <summary>
/// The eye-tracking ACQUISITION block of session_manifest.json (schema v9). Everything here is an observation taken at a lifecycle checkpoint - never a default-false:
/// unknown / not_applicable / unsupported are explicit strings. gaze.csv WRITER health stays in health_gaze; this block is about whether eyes were actually tracked.
/// </summary>
[Serializable]
public class EyeTrackingManifest
{
    // ================= CANONICAL eye-tracking state (schema v11): the ONE source of truth for the independent concepts below. Everything else in the manifest that mentions eye tracking status
    // (eye_tracking_supported/_enabled/_permission_granted and their *_state strings) is an ALIAS computed from these at every serialisation - never an independently updated value. =================
    //   capability      : does the hardware / runtime support the feature        supported | unsupported | unknown
    //   permission      : is the platform permission satisfied                  granted | denied | denied_permanently | not_applicable (the platform has no such permission) | unknown | ...
    //   configured      : did the application enable it (lifecycle active, both OVREyeGaze components present and not disabled by configuration)
    //   runtime_available: is the provider accessible in this execution (OVRPlugin reports eye tracking enabled)
    //   tracking_active : is valid tracking being supplied right now (lifecycle Running; TrackingUnavailable = lost, not "revoked")
    // A temporary loss of tracking changes tracking_active only; it never changes capability, permission or configured.
    public string capability = "unknown", permission = "unknown";
    public bool configured, runtime_available, tracking_active;
    public double last_state_change_t_abs = -1;
    public string provider = "OVRPlugin.GetEyeGazesState via OVREyeGaze (Meta XR SDK)";
    public string sdk_version = "unknown";

    public bool lifecycle_present;
    public string runtime_path = "unknown";
    public bool permission_applicable;
    public string lifecycle_state = "unknown", acquisition_status = "unknown", acquisition_reason = "no_lifecycle_controller";
    public string permission_state = "unknown", capability_state = "unknown", tracking_enabled_state = "unknown";
    public string left_component_state = "unknown", right_component_state = "unknown";
    public string gaze_host_conflict = "";
    public double queried_t_abs = -1;

    public int startup_attempts, startup_successes, startup_failures, capability_checks, permission_requests, recovery_episodes, resume_retries, ignored_late_callbacks;
    public string last_startup_result = "none";

    // provider-level: counted once per NEW provider sample (timestamp advanced), independent of the CSV row rate
    public long provider_samples, provider_left_valid_samples, provider_right_valid_samples, provider_any_valid_samples;
    public double first_valid_provider_t_abs = -1, last_valid_provider_t_abs = -1, seconds_since_last_valid_provider = -1;

    // recorder-level: counted from gaze.csv rows actually written
    public long rows_valid_combined, rows_fallback, rows_fallback_not_acquiring, rows_stale_confidence_rejected, rows_host_conflict_blocked, rows_left_valid, rows_right_valid;
    public double first_valid_row_t_abs = -1, last_valid_row_t_abs = -1, longest_no_valid_eye_s;

    // gaze origin validation (schema v10): provider eye origin vs the rig's own eye anchor, counted over valid eye samples (see EyeGazeOrigin). origin_mean_offset_* is
    // provider minus anchor in WORLD axes: a constant non-zero vertical component is the signature of a tracking-origin mismatch between the eye pose and the head pose.
    public bool origin_validation_enabled;
    public long origin_samples_checked, origin_samples_provider, origin_samples_eye_anchor_fallback, origin_samples_unchecked;
    public double origin_deviation_max_m = -1, origin_deviation_mean_m = -1, origin_mean_offset_x_m, origin_mean_offset_y_m, origin_mean_offset_z_m, origin_max_allowed_deviation_m = -1;
    // schema v12: the provider-minus-anchor offset in three frames (mean + sample std, metres; -1 std when fewer than 2 samples). Constant in world / rig axes but varying in head axes = a tracking-origin
    // offset; constant in head axes = a head-model offset. origin_frames_samples is 0 when the frames were not recorded (no anchor / no head transform).
    public long origin_frames_samples;
    public float[] origin_offset_world_mean_m = new float[0], origin_offset_world_std_m = new float[0];
    public float[] origin_offset_rig_mean_m = new float[0], origin_offset_rig_std_m = new float[0];
    public float[] origin_offset_head_mean_m = new float[0], origin_offset_head_std_m = new float[0];
    public TrackingSpaceManifest tracking_space = new TrackingSpaceManifest();
    public EyePipelineManifest eye_pipeline = new EyePipelineManifest();   // schema v14: the per-eye provider pipeline (raw provider vs head space vs view anchors vs IPD, frame coherence)
    public ViewFrameManifest view_frame = new ViewFrameManifest();   // schema v13: the rendered view, the Camera Floor Offset and ground-relative heights
    public float[] origin_vs_rig_anchor_mean_m = new float[0], origin_vs_rig_anchor_std_m = new float[0];   // provider minus the RAW OVR anchor (rig frame): the Camera Floor Offset shows up here

    // raw provider confidence behaviour (see GazeConfidence) and the blink-signal capability (see BlinkCapability)
    public ConfidenceManifest confidence = new ConfidenceManifest();
    public BlinkManifest blink = new BlinkManifest();

    public int transitions_dropped;
    public System.Collections.Generic.List<EyeTrackingTransition> transitions = new System.Collections.Generic.List<EyeTrackingTransition>();

    public void FillLifecycle(EyeTrackingLifecycle lc, double now, string hostConflict)
    {
        queried_t_abs = now;
        gaze_host_conflict = hostConflict ?? "";
        lifecycle_present = lc != null;
        if (lc == null) { acquisition_status = "unknown"; acquisition_reason = "no_lifecycle_controller"; return; }
        runtime_path = lc.Platform.RuntimePath; permission_applicable = lc.Platform.PermissionApplicable;
        lifecycle_state = EyeTrackingNames.Of(lc.State); acquisition_status = lc.AcquisitionStatus; acquisition_reason = lc.LastReason;
        permission_state = EyeTrackingNames.Of(lc.Permission); capability_state = EyeTrackingNames.Of(lc.Capability);
        string te = "unknown";
        try { if (lc.Platform.TrackingEnabled != null) te = lc.Platform.TrackingEnabled() ? "true" : "false"; } catch { }
        tracking_enabled_state = te;
        left_component_state = lc.ComponentState(0); right_component_state = lc.ComponentState(1);
        startup_attempts = lc.StartupAttempts; startup_successes = lc.StartupSuccesses; startup_failures = lc.StartupFailures;
        capability_checks = lc.CapabilityChecks; permission_requests = lc.PermissionRequests; recovery_episodes = lc.RecoveryEpisodes;
        resume_retries = lc.ResumeRetries; ignored_late_callbacks = lc.IgnoredLateCallbacks; last_startup_result = lc.LastResult;
        provider_samples = lc.ProviderSamples; provider_left_valid_samples = lc.ProviderLeftValid; provider_right_valid_samples = lc.ProviderRightValid; provider_any_valid_samples = lc.ProviderAnyValid;
        first_valid_provider_t_abs = double.IsNaN(lc.FirstValidT) ? -1 : lc.FirstValidT;
        last_valid_provider_t_abs = double.IsNaN(lc.LastValidT) ? -1 : lc.LastValidT;
        seconds_since_last_valid_provider = lc.State == EyeTrackingState.Running || lc.State == EyeTrackingState.TrackingUnavailable ? lc.SecondsWithoutValid(now) : -1;
        transitions_dropped = lc.DroppedTransitions;
        transitions.Clear();
        for (int i = 0; i < lc.Transitions.Count; i++) transitions.Add(lc.Transitions[i]);
        DeriveCanonical(lc);
    }

    void DeriveCanonical(EyeTrackingLifecycle lc)
    {
        capability = capability_state;
        permission = permission_state;
        configured = lc.State != EyeTrackingState.NotApplicable && lc.State != EyeTrackingState.Idle && left_component_state != "absent" && right_component_state != "absent";
        runtime_available = tracking_enabled_state == "true";
        tracking_active = lc.State == EyeTrackingState.Running;
        last_state_change_t_abs = lc.Transitions.Count > 0 ? lc.Transitions[lc.Transitions.Count - 1].t_abs : -1;
        try { sdk_version = "OVRPlugin " + OVRPlugin.version + " (Meta XR SDK Core 201.0.0)"; } catch (System.Exception) { sdk_version = "unknown"; }
    }
}

[Serializable]
public class FrameTimingSummary
{
    public bool available;
    public string measured_during = "open capture windows only (trial open, End panel excluded)";
    public long frames;
    public double mean_hz = -1, median_frame_ms = -1, p95_frame_ms = -1, max_frame_ms = -1;
    public long frames_over_100ms;
    public int target_frame_rate, vsync_count;
    public float capture_delta_time, time_scale;
}


/// <summary>Manifest block "eye_tracking.blink": whether a blink / eye-closure signal exists on this runtime and project configuration, and what was detected from it. The ABSENCE of blink events is
/// evidence of "no blinks" only when status == available.</summary>
/// <summary>
/// G1 evidence about the tracking origins behind the gaze origin: what OVRPlugin and Unity XR each report, and the vertical difference between the two readings of the SAME head node.
/// It is evidence, not a verdict: tracking_origins_relation says whether the two origin kinds agree; head_height_delta_* compares with the provider-minus-anchor offset.
/// </summary>
[Serializable]
public class TrackingSpaceManifest
{
    public bool captured;
    public long probes, probes_with_both_heads;
    public bool ovrplugin_initialized, openxr_active;
    public string ovrplugin_tracking_origin = "", unity_xr_tracking_origin_mode = "", loaded_xr_device = "", native_xr_api = "";
    public string tracking_origins_relation = "unknown";   // same_kind | differ | unknown
    public bool head_delta_available;   // false = the values below are placeholders (0), not measurements (never NaN in the JSON)
    public double head_y_ovrplugin_last_m, head_y_unity_xr_last_m;
    public double head_height_delta_mean_m, head_height_delta_std_m;   // OVRPlugin head y minus Unity XR head y
    public string note = "evidence only: a head_height_delta equal to origin_offset_world_mean_m[1] indicates two different tracking origins (not a gaze-specific fault); not a hardware verification";
}

[Serializable]
public class BlinkManifest
{
    public string status = "unavailable";      // unsupported | unavailable | not_configured | available (BlinkCapability)
    public string reason = "not_evaluated_yet";
    public bool eye_gaze_api_exposes_openness = false;   // read from the installed SDK: EyeGazeState = { Pose, Confidence, IsValid }
    public string signal = "FaceExpression.EyesClosedL / EyesClosedR blendshape weights (0..1) from Meta face tracking (OVRFaceExpressions); separate from eye tracking";
    public string requires = "OculusProjectConfig.faceTrackingSupport + com.oculus.permission.FACE_TRACKING + an OVRFaceExpressions component; records facial data";
    public bool ovrplugin_initialized, runtime_face_tracking_supported, runtime_face_tracking_enabled, component_present, permission_granted, signal_valid;
    public bool thresholds_calibrated = false;
    public float close_threshold, open_threshold;
    public long blinks_detected, blinks_truncated, unilateral_closure_samples, invalid_samples, out_of_range_samples;
    public double mean_duration_s = -1, max_duration_s = -1;
    public bool absence_of_blink_events_is_evidence_of_no_blinks;
}
