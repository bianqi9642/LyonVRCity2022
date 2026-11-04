// Part3AudioRecorder.cs
// Continuous think-aloud microphone recording for Part 3, synchronised to the session's CaptureClock and finalised inside the existing capture lifecycle.
//
// SCOPE AND PLACEMENT
//   * Part 3 only. ExperimentManager.RunPart3 is the ONLY caller of RunPreflight() / StartRecording() / StopAtBoundary(); nothing else starts it. DataCaptureManager creates the component (no scene edit),
//     routes its lifecycle events into events.csv, includes it in FinalizeCapture() and copies its status into session_manifest.json ("audio_capture").
//   * Interval: audio starts at the participant's Start click (the microphone is confirmed running BEFORE BeginTrial() lets the participant act) and stops at EndTrial() - the same boundary that ends
//     OnTrialEnd for gaze / trajectory / NPC capture. Half-open: [first audio frame, part3 trial end). No audio exists before the Start click: preflight starts the microphone only to prove that it
//     delivers samples and discards it without reading a single sample.
//   * The recorded signal is the raw microphone input: no voice-activity detection, no segmentation, no AGC, no denoising, no resampling, no channel conversion (Microphone.Start is asked for the device's
//     own rate and the clip's own channel count is kept). Silence is preserved.
//
// DATA PATH (main thread -> writer thread)
//   Microphone ring (looping AudioClip) -> MicCursor (wrap / overrun detection from the shared clock) -> AudioClip.GetData in <= 4096-frame pieces -> PcmConvert (PCM16) -> pooled byte[] ->
//   AudioWriteQueue (one writer thread, periodic flush with header refresh) -> audio/part3_audio.wav.partial -> renamed to part3_audio.wav only after validation.
//   Nothing keeps the recording in RAM; steady state allocates nothing per frame.
//
// TIME
//   t_abs = CaptureClock.TAbs everywhere (no Time.time, no DateTime, no second stopwatch). The microphone has its own sample clock, so the sample_index <-> t_abs relation is an affine fit per continuous
//   segment over periodic anchors (AudioClockMap). First-sample time = reading time - ring position / rate. Residual jitter and drift are reported, not assumed away.
//
// FAILURE POLICY (visible, never silent): permission denied / no device / start failure / latency above the limit keep the experiment from starting (ExperimentManager shows a retry panel); a failure
// DURING Part 3 never fabricates samples: the gap is recorded (events, audio_index.csv, manifest) and the recording is marked completed_with_gaps / incomplete.
// All comments in English per project convention.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public enum AudioCaptureState
{
    Disabled, Preflight, Ready, StartRequested, Recording, Stopping, Finalizing, Closed,
    PermissionDenied, DeviceUnavailable, StartFailed, RecordingInterrupted, BufferOverrun, WriteFailed, FinalizationFailed, Incomplete
}

/// <summary>The microphone as plain delegates, so the whole recorder can be driven by a simulated device in tests. Defaults are UnityEngine.Microphone.</summary>
public sealed class MicrophoneApi
{
    public Func<string[]> Devices;
    public Func<string, int[]> GetCaps;                    // {min,max} sample rates (0,0 = any)
    public Func<string, bool, int, int, bool> Start;       // device, loop, lengthSec, frequency
    public Func<string, bool> IsRecording;
    public Func<string, int> GetPosition;
    public Func<int> ClipFrames, ClipChannels, ClipFrequency;
    public Func<float[], int, bool> GetData;               // fills the whole array from frame offset
    public Action<string> End;

    public static MicrophoneApi CreateUnity()
    {
        AudioClip clip = null;
        return new MicrophoneApi
        {
            Devices = () => Microphone.devices,
            GetCaps = d => { Microphone.GetDeviceCaps(d, out int lo, out int hi); return new[] { lo, hi }; },
            Start = (d, loop, len, freq) => { clip = Microphone.Start(d, loop, len, freq); return clip != null; },
            IsRecording = d => Microphone.IsRecording(d),
            GetPosition = d => Microphone.GetPosition(d),
            ClipFrames = () => clip != null ? clip.samples : 0,
            ClipChannels = () => clip != null ? clip.channels : 0,
            ClipFrequency = () => clip != null ? clip.frequency : 0,
            GetData = (buf, offset) => clip != null && clip.GetData(buf, offset),
            End = d => { Microphone.End(d); clip = null; }
        };
    }
}

/// <summary>Microphone permission as delegates (Android runtime permission; always granted elsewhere - the OS-level state is then visible only as "no device").</summary>
public sealed class MicPermissionApi
{
    public Func<bool> HasPermission = () => true;
    public Action<Action<bool>> Request = done => done(true);
    public bool Applicable;

    public static MicPermissionApi CreateUnity()
    {
        var p = new MicPermissionApi();
#if UNITY_ANDROID && !UNITY_EDITOR
        p.Applicable = true;
        p.HasPermission = () => UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone);
        p.Request = done =>
        {
            var cb = new UnityEngine.Android.PermissionCallbacks();
            cb.PermissionGranted += _ => done(true);
            cb.PermissionDenied += _ => done(false);
            cb.PermissionDeniedAndDontAskAgain += _ => done(false);
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone, cb);
        };
#endif
        return p;
    }
}

[Serializable]
public class AudioSegmentFit { public int segment; public long first_sample, last_sample_exclusive; public double t_first_est, rate_hz_fit, drift_ppm, residual_rms_ms; public int anchors; }

/// <summary>Manifest block "audio_capture" (also the content of audio/audio_metadata.json). Every number is a measurement of this run or blank/-1; nothing is an assumption.</summary>
[Serializable]
public class AudioCaptureManifest
{
    public bool enabled;
    public string part = "Part3";
    public string status = "not_enabled";      // not_enabled | not_started | recording | completed | completed_with_gaps | incomplete | failed
    public string state = "Disabled";
    public string failure_reason = "";
    public string recording_id = "";
    public string format = "WAV PCM16 (interleaved, little-endian)";
    public int sample_rate_hz, channels, bit_depth = 16;
    public string file = "", index_file = "", anchors_file = "", metadata_file = "";
    public string clock_source = "CaptureClock.TAbs (Time.realtimeSinceStartupAsDouble)";
    public string timestamp_convention = "t_abs(sample_index) = start of that frame; intervals half-open [t0,t1); sample_index = WAV frame index, lost frames are not counted";
    public string device_descriptor = "", runtime_path = "";
    public double start_button_clicked_t_abs = -1, audio_start_requested_t_abs = -1, first_sample_t_abs_est = -1, experiment_effective_start_t_abs = -1, audio_start_latency_ms = -1;
    public int start_attempts;
    public double end_t_abs = -1, last_sample_t_abs_est = -1;
    public long total_frames, file_bytes, dropped_frames, lost_frames_overrun, backlog_dropped_frames;
    public double duration_sec;
    public int buffer_overruns, interruption_count;
    public double interruption_total_s;
    public string[] gaps = new string[0];
    public int clock_anchor_count;
    public double residual_rms_ms = -1, timing_uncertainty_ms = -1;
    public string timing_uncertainty_note = "";
    public AudioSegmentFit[] segments = new AudioSegmentFit[0];
    public bool finalization_success;
    public string file_validation = "", index_validation = "";
    public bool index_consistent;
    public double rms_dbfs = -999, peak_abs = -1, clipping_percent = -1, near_silence_percent = -1;
    public string signal_note = "diagnostics only; the source audio is never modified";
    public string[] state_trail = new string[0];
    public string privacy_note = "no sample exists before the Start click; preflight discards a microphone probe without reading it";
}

[DisallowMultipleComponent]
public class Part3AudioRecorder : MonoBehaviour
{
    public static Part3AudioRecorder Instance { get; private set; }

    [Header("Device")]
    [Tooltip("Exact (case-insensitive) microphone device name to use. Empty = Unity's default input. A non-empty name that is not present FAILS the preflight (no silent fallback).")]
    public string preferredDevice = "";
    [Tooltip("If non-empty, the chosen device name must contain this text (for example 'Oculus' or 'Quest' on Quest Link, where the Windows default microphone may not be the headset).")]
    public string requiredDeviceNameContains = "";
    [Tooltip("Requested sample rate. 0 = 48000 if the device supports it, else its highest supported rate. The device's own rate is used as is (no resampling).")]
    public int requestedSampleRate = 0;

    [Header("Timing limits")]
    [Tooltip("Seconds the microphone may take to deliver its first sample after Microphone.Start.")]
    public float startTimeoutSeconds = 3f;
    [Tooltip("Maximum allowed gap between the participant's Start click and the first captured audio sample. Above it the start fails and is retried.")]
    public float maxStartLatencySeconds = 2f;
    [Tooltip("Seconds without microphone position progress after which the input is considered interrupted.")]
    public float stallSeconds = 1f;
    public float anchorIntervalSeconds = 1f;
    public float chunkSeconds = 1f;
    public float flushIntervalSeconds = 1f;
    [Tooltip("Length of the microphone ring buffer in seconds. The recorder drains every frame; this is only the safety margin against long hitches.")]
    public int ringSeconds = 10;
    public int maxRestartAttempts = 3;
    public float restartBackoffSeconds = 0.5f;

    [Header("Output")]
    public string outputSubfolder = "audio";
    public string fileName = "part3_audio.wav";
    public bool useBackgroundWriter = true;
    public long minFreeStorageMegabytes = 200;
    public bool enableDebugLogs = false;

    public const int ChunkFrames = 4096;

    // ---- wiring (DataCaptureManager sets these; tests may replace the seams) ----
    public Func<string> sessionFolderProvider = () => "";
    public Func<string> sessionIdProvider = () => "";
    public Action<string, double, double?, string> eventSink;   // event type, t_abs, value_num, value_str
    public MicrophoneApi mic;
    public MicPermissionApi permission;
    public string runtimePath = "";
    [NonSerialized] public Func<long, long> testFreeBytes;       // test seam (null in production)
    [NonSerialized] public bool testFailWriterRename;
    [NonSerialized] public long testFailWriterAfterBytes = -1;

    // ---- public state ----
    public AudioCaptureState State { get; private set; } = AudioCaptureState.Disabled;
    public string FailureReason { get; private set; } = "";
    public bool IsReady => State == AudioCaptureState.Ready;
    public bool IsRecording => State == AudioCaptureState.Recording || State == AudioCaptureState.BufferOverrun;
    public bool Started { get; private set; }
    public bool FinalizationOk { get; private set; } = true;   // false only after a failed finalization
    public bool Finalized { get; private set; }
    public readonly AudioCaptureManifest Manifest = new AudioCaptureManifest();
    public double StartClickT => _clickT;
    // live counters (the Manifest object is refreshed by FillManifest / FinalizeFiles)
    public int InterruptionCount => _interruptions;
    public int OverrunCount => _overruns;
    public long LostFrameCount => _lostFrames;
    public long FramesWritten => _framesWritten;

    // ---- internals ----
    string _device = ""; int _rate, _channels, _ringFrames;
    MicCursor _cursor; AudioClockMap _map; WavFileWriter _wav; AudioWriteQueue _queue;
    CaptureCsvWriter _indexCsv, _anchorCsv;
    float[] _readBuf; double _clickT, _tRequested, _firstSampleT = double.NaN, _tEnd = double.NaN;
    long _framesWritten;               // frames accepted into the file (sample_index of the next frame)
    int _lastPos; double _lastAdvanceT, _lastAnchorT, _lastFlushT;
    long _chunkStartFrame; double _chunkOpenT; int _chunkIndex; long _chunkDropped; int _chunkOverruns; int _chunkLastPos; int _chunkAnchorId = -1; int _chunkSegment;
    int _overruns; long _lostFrames, _droppedFrames;
    int _interruptions; double _interruptionTotal; readonly List<string> _gaps = new List<string>();
    double _gapStartT = double.NaN; bool _paused; int _restartAttempts; double _nextRestartT; bool _awaitFirst; double _restartRequestedT;
    double _sumSq; long _sampleCount, _clipCount, _quietCount; float _peak;
    bool _writerFailedReported; readonly List<string> _trail = new List<string>();
    string _recordingId = "", _audioDir = "", _wavPath = "", _metaPath = "";
    bool _stopRequested, _startedOnce;

    void Awake() { Instance = this; EnsureApis(); }
    void OnDestroy() { if (Instance == this) Instance = null; if (!Finalized && (_wav != null || State == AudioCaptureState.Recording)) FinalizeForSession("on_destroy"); }
    void OnApplicationQuit() { if (!Finalized && (_wav != null || IsRecording)) FinalizeForSession("app_quit"); }

    void EnsureApis()
    {
        if (mic == null) mic = MicrophoneApi.CreateUnity();
        if (permission == null) permission = MicPermissionApi.CreateUnity();
    }

    public void Configure(bool enabled)
    {
        Manifest.enabled = enabled;
        if (!enabled) { SetState(AudioCaptureState.Disabled); Manifest.status = "not_enabled"; }
        else if (State == AudioCaptureState.Disabled) { SetState(AudioCaptureState.Disabled); Manifest.status = "not_started"; }
    }

    void SetState(AudioCaptureState s)
    {
        State = s; Manifest.state = s.ToString();
        _trail.Add(CaptureClock.TAbs.ToString("F3", CultureInfo.InvariantCulture) + ":" + s);
        if (_trail.Count > 64) _trail.RemoveAt(0);
        if (enableDebugLogs) Debug.Log("[Part3AudioRecorder] state -> " + s);
    }

    void Emit(string type, double t, double? num, string str) { try { eventSink?.Invoke(type, t, num, str ?? ""); } catch (Exception e) { Debug.LogWarning("[Part3AudioRecorder] event sink failed: " + e.Message); } }

    // ============================================================================================ preflight (before the participant can start)

    /// <summary>Permission, device, capture format, writable output and a discarded microphone probe. Ends in Ready or in a failure state with FailureReason. Starts no recording.</summary>
    public IEnumerator RunPreflight()
    {
        EnsureApis();
        if (!Manifest.enabled) { SetState(AudioCaptureState.Disabled); yield break; }
        if (IsRecording || State == AudioCaptureState.Stopping || State == AudioCaptureState.Finalizing || Finalized) yield break;
        SetState(AudioCaptureState.Preflight); FailureReason = ""; Manifest.runtime_path = runtimePath;

        string folder = sessionFolderProvider?.Invoke() ?? "";
        if (string.IsNullOrEmpty(folder)) { Fail(AudioCaptureState.StartFailed, "no_session_folder"); yield break; }

        // 1. permission
        double t = CaptureClock.TAbs; bool has = permission.HasPermission();
        Emit("audio_permission_checked", t, has ? 1.0 : 0.0, "applicable=" + permission.Applicable + ";granted=" + has);
        if (!has)
        {
            bool answered = false, granted = false; double t0 = CaptureClock.TAbs;
            permission.Request(g => { granted = g; answered = true; });
            while (!answered && CaptureClock.TAbs - t0 < 90.0) yield return null;
            if (!granted) { Fail(AudioCaptureState.PermissionDenied, answered ? "microphone_permission_denied" : "microphone_permission_request_timed_out"); yield break; }
            Emit("audio_permission_granted", CaptureClock.TAbs, 1.0, "requested_at_runtime");
        }
        else Emit("audio_permission_granted", CaptureClock.TAbs, 1.0, "already_granted");

        // 2. device (no silent fallback)
        string[] devices = null; try { devices = mic.Devices(); } catch (Exception) { }
        if (devices == null || devices.Length == 0) { Fail(AudioCaptureState.DeviceUnavailable, "no_microphone_device"); yield break; }
        string chosen = null;
        if (!string.IsNullOrEmpty(preferredDevice))
        {
            foreach (var d in devices) if (string.Equals(d, preferredDevice, StringComparison.OrdinalIgnoreCase)) { chosen = d; break; }
            if (chosen == null) { Fail(AudioCaptureState.DeviceUnavailable, "preferred_device_not_found"); yield break; }
        }
        else chosen = devices[0];
        if (!string.IsNullOrEmpty(requiredDeviceNameContains) && chosen.IndexOf(requiredDeviceNameContains, StringComparison.OrdinalIgnoreCase) < 0) { Fail(AudioCaptureState.DeviceUnavailable, "device_name_does_not_match_required_text"); yield break; }
        _device = chosen;

        // 3. capture format: the device's own rate, never resampled
        int[] caps = null; try { caps = mic.GetCaps(_device); } catch (Exception) { }
        int lo = caps != null && caps.Length > 1 ? caps[0] : 0, hi = caps != null && caps.Length > 1 ? caps[1] : 0;
        int rate = requestedSampleRate;
        if (rate <= 0) rate = (lo == 0 && hi == 0) ? 48000 : (lo <= 48000 && 48000 <= hi ? 48000 : hi);
        if (!(lo == 0 && hi == 0) && (rate < lo || rate > hi)) { Fail(AudioCaptureState.DeviceUnavailable, "requested_rate_not_supported"); yield break; }
        _rate = rate;

        // 4. output folder writable + free space
        try
        {
            _audioDir = Path.Combine(folder, outputSubfolder); Directory.CreateDirectory(_audioDir);
            string probe = Path.Combine(_audioDir, ".write_probe"); File.WriteAllText(probe, "x"); File.Delete(probe);
            long free = testFreeBytes != null ? testFreeBytes(0) : FreeBytes(_audioDir);
            if (free >= 0 && free < minFreeStorageMegabytes * 1024L * 1024L) { Fail(AudioCaptureState.StartFailed, "insufficient_free_storage"); yield break; }
        }
        catch (Exception e) { Fail(AudioCaptureState.StartFailed, "output_not_writable:" + e.GetType().Name); yield break; }

        // 5. does Unity actually deliver samples? A short probe that is discarded unread (no pre-experiment audio is ever kept).
        bool started = false; try { started = mic.Start(_device, true, 1, _rate); } catch (Exception) { }
        if (!started) { Fail(AudioCaptureState.DeviceUnavailable, "microphone_start_failed_in_probe"); yield break; }
        double pt0 = CaptureClock.TAbs; bool advancing = false;
        while (CaptureClock.TAbs - pt0 < Math.Max(1.0f, startTimeoutSeconds)) { if (mic.GetPosition(_device) > 0) { advancing = true; break; } yield return null; }
        int pch = 0; try { pch = mic.ClipChannels(); } catch (Exception) { }
        try { mic.End(_device); } catch (Exception) { }
        if (!advancing) { Fail(AudioCaptureState.DeviceUnavailable, "microphone_delivered_no_samples_in_probe"); yield break; }
        _channels = pch > 0 ? pch : 1;

        Manifest.sample_rate_hz = _rate; Manifest.channels = _channels; Manifest.device_descriptor = Describe(_device);
        SetState(AudioCaptureState.Ready);
    }

    static string Describe(string device) => string.IsNullOrEmpty(device) ? "default_input" : device;   // the device NAME as Unity lists it; no user or machine identity is added

    static long FreeBytes(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace; } catch (Exception) { return -1; }   // unknown (for example Android): not a failure
    }

    void Fail(AudioCaptureState s, string reason)
    {
        FailureReason = reason; Manifest.failure_reason = reason; SetState(s);
        if (Manifest.status == "not_started" || Manifest.status == "not_enabled" || !_startedOnce) Manifest.status = "failed";
        Emit("audio_recording_failed", CaptureClock.TAbs, null, s + ":" + reason);
        Debug.LogError("[Part3AudioRecorder] " + s + ": " + reason);
    }

    // ============================================================================================ start (after the Start click)

    /// <summary>Starts the microphone and the file, then waits for the first sample. clickT is the participant's Start click (CaptureClock.TAbs). Ends in Recording or StartFailed.</summary>
    public IEnumerator StartRecording(double clickT)
    {
        EnsureApis();
        if (IsRecording || State == AudioCaptureState.StartRequested || Finalized) { if (enableDebugLogs) Debug.LogWarning("[Part3AudioRecorder] StartRecording ignored (already started or finalized)."); yield break; }
        if (State != AudioCaptureState.Ready && State != AudioCaptureState.StartFailed) { Fail(AudioCaptureState.StartFailed, "not_ready_state_" + State); yield break; }
        if (_rate <= 0 || _channels <= 0 || string.IsNullOrEmpty(_device) && _rate <= 0) { Fail(AudioCaptureState.StartFailed, "preflight_not_completed"); yield break; }
        _startedOnce = true; Manifest.start_attempts++;
        _clickT = clickT; _tRequested = CaptureClock.TAbs; FailureReason = ""; Manifest.failure_reason = "";
        Manifest.start_button_clicked_t_abs = clickT; Manifest.audio_start_requested_t_abs = _tRequested;
        SetState(AudioCaptureState.StartRequested);
        Emit("audio_recording_requested", _tRequested, null, "click_t_abs=" + clickT.ToString("F4", CultureInfo.InvariantCulture));

        string folder = sessionFolderProvider?.Invoke() ?? "";
        _recordingId = (sessionIdProvider?.Invoke() ?? "") + "_p3audio";
        _audioDir = Path.Combine(folder, outputSubfolder); _wavPath = Path.Combine(_audioDir, fileName); _metaPath = Path.Combine(_audioDir, "audio_metadata.json");

        // file first: an unwritable disk must fail the start, not the middle of Part 3
        _wav = new WavFileWriter { TestFailRename = testFailWriterRename, TestFailAfterBytes = testFailWriterAfterBytes };
        if (!_wav.Open(_wavPath, _rate, _channels)) { string why = _wav.Error; DiscardAttempt(); Fail(AudioCaptureState.StartFailed, "wav_open_failed:" + why); yield break; }

        bool ok = false; try { ok = mic.Start(_device, true, ringSeconds, _rate); } catch (Exception) { }
        if (!ok) { DiscardAttempt(); Fail(AudioCaptureState.StartFailed, "microphone_start_failed"); yield break; }
        int clipRate = 0, clipCh = 0, clipFrames = 0;
        try { clipRate = mic.ClipFrequency(); clipCh = mic.ClipChannels(); clipFrames = mic.ClipFrames(); } catch (Exception) { }
        if (clipRate > 0 && clipRate != _rate) { Manifest.timing_uncertainty_note = "device rate " + clipRate + " differs from requested " + _rate; _rate = clipRate; }
        if (clipCh > 0 && clipCh != _channels) { _channels = clipCh; }
        if (clipFrames <= 0) { try { mic.End(_device); } catch (Exception) { } DiscardAttempt(); Fail(AudioCaptureState.StartFailed, "microphone_clip_has_no_frames"); yield break; }
        // the WAV header was written with the preflight format: reopen if the real clip format differs
        if (_wav.SampleRate != _rate || _wav.Channels != _channels) { _wav.Dispose(); TryDelete(_wav.PartialPath); _wav = new WavFileWriter { TestFailRename = testFailWriterRename, TestFailAfterBytes = testFailWriterAfterBytes }; if (!_wav.Open(_wavPath, _rate, _channels)) { string why = _wav.Error; try { mic.End(_device); } catch (Exception) { } DiscardAttempt(); Fail(AudioCaptureState.StartFailed, "wav_reopen_failed:" + why); yield break; } }
        _ringFrames = clipFrames; _cursor = new MicCursor(_ringFrames, _rate); _map = new AudioClockMap(_rate);
        _readBuf = new float[ChunkFrames * _channels];
        _queue = new AudioWriteQueue(_wav, useBackgroundWriter, ChunkFrames * _channels * 2, 512, flushIntervalSeconds);

        // first sample: wait until the microphone cursor moves, then back-date by the position
        int pos = 0; double tRead = _tRequested; bool got = false;
        while (CaptureClock.TAbs - _tRequested < startTimeoutSeconds)
        {
            yield return null;
            tRead = CaptureClock.TAbs; try { pos = mic.GetPosition(_device); } catch (Exception) { pos = 0; }
            if (pos > 0) { got = true; break; }
        }
        if (!got) { try { mic.End(_device); } catch (Exception) { } DiscardAttempt(); Fail(AudioCaptureState.StartFailed, "no_microphone_samples_within_start_timeout"); yield break; }
        double first = AudioClockMap.FirstSampleTime(tRead, pos, _rate);
        if (first < _tRequested) first = _tRequested;   // a sample cannot predate the request: clamp (the estimate is then an upper bound on the latency)
        double latency = first - clickT;
        if (latency > maxStartLatencySeconds) { try { mic.End(_device); } catch (Exception) { } DiscardAttempt(); Fail(AudioCaptureState.StartFailed, "start_latency_exceeded:" + (latency * 1000.0).ToString("F0", CultureInfo.InvariantCulture) + "ms"); yield break; }

        _firstSampleT = first; Manifest.first_sample_t_abs_est = first; Manifest.audio_start_latency_ms = latency * 1000.0;
        _framesWritten = 0; _lastPos = 0; _lastAdvanceT = tRead; _lastAnchorT = tRead; _lastFlushT = tRead;
        _map.BeginSegment(0); _map.AddAnchor(0, first, 0, AudioSettings.dspTime); WriteAnchorRow(_map.Current.anchors[0]);
        _cursor.Begin(0, first);
        OpenIndexFiles(folder);
        OpenChunk(0, first, 0);
        // write the first drain now so the writer is proven alive before the experiment may proceed
        SetState(AudioCaptureState.Recording);
        DrainTo(pos, tRead, 0);
        if (_wav.Failed) { string why = _wav.Error; FailureReason = "wav_write_failed:" + why; }
        Started = true; Manifest.status = "recording"; Manifest.recording_id = _recordingId;
        Manifest.sample_rate_hz = _rate; Manifest.channels = _channels; Manifest.file = outputSubfolder + "/" + fileName;
        Manifest.index_file = outputSubfolder + "/audio_index.csv"; Manifest.anchors_file = outputSubfolder + "/audio_anchors.csv"; Manifest.metadata_file = outputSubfolder + "/audio_metadata.json";
        WriteMetadata("recording");
        Emit("audio_recording_started", CaptureClock.TAbs, latency * 1000.0, "first_sample_t_abs_est=" + first.ToString("F4", CultureInfo.InvariantCulture) + ";rate=" + _rate + ";channels=" + _channels + ";device=" + Describe(_device));
    }

    /// <summary>Ends an attempt that never became a recording: its (empty or sub-second) partial file is removed, so a retry can use the same name and no stray file is left.</summary>
    void DiscardAttempt()
    {
        try { _queue?.Complete(1000); } catch (Exception) { }
        try { _wav?.Dispose(); if (_wav != null) TryDelete(_wav.PartialPath); } catch (Exception) { }
        _queue = null; _wav = null; _cursor = null; _map = null;
        try { _indexCsv?.Close(); _anchorCsv?.Close(); } catch (Exception) { }
        _indexCsv = _anchorCsv = null;
        string d = _audioDir; try { if (!string.IsNullOrEmpty(d)) { foreach (var f in new[] { "audio_index.csv", "audio_anchors.csv", "audio_metadata.json" }) { string p = Path.Combine(d, f); if (File.Exists(p) && !Started) File.Delete(p); } } } catch (Exception) { }
    }
    static void TryDelete(string p) { try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) File.Delete(p); } catch (Exception) { } }

    /// <summary>ExperimentManager calls this right before BeginTrial(): the instant the participant may act.</summary>
    public void NoteExperimentStart(double tAbs) { Manifest.experiment_effective_start_t_abs = tAbs; }

    void OpenIndexFiles(string folder)
    {
        string sid = sessionIdProvider?.Invoke() ?? "";
        _indexCsv = new CaptureCsvWriter(Path.Combine(_audioDir, "audio_index.csv"),
            "session_id,part_id,audio_recording_id,chunk_index,sample_index_start,sample_index_end_exclusive,t_abs_start_est,t_abs_end_est,sample_rate_hz,channels,frames_written,duration_sec,microphone_position,clock_anchor_id,dropped_frames,buffer_overrun_count,write_status,segment", "audio_index");
        _anchorCsv = new CaptureCsvWriter(Path.Combine(_audioDir, "audio_anchors.csv"),
            "session_id,audio_recording_id,clock_anchor_id,segment,sample_index,t_abs,microphone_buffer_position,sample_rate_hz,dsp_time", "audio_anchors");
    }

    // ============================================================================================ per-frame drain

    void Update() { Tick(CaptureClock.TAbs); }

    /// <summary>One drain step at time 'now' (CaptureClock). Public so tests can drive it with a simulated clock.</summary>
    public void Tick(double now)
    {
        if (Finalized) return;
        if (State == AudioCaptureState.RecordingInterrupted) { HandleInterrupted(now); return; }
        if (!IsRecording) return;
        bool rec = true; try { rec = mic.IsRecording(_device); } catch (Exception) { rec = false; }
        if (!rec) { BeginInterruption(now, "microphone_stopped"); return; }
        int pos; try { pos = mic.GetPosition(_device); } catch (Exception) { BeginInterruption(now, "position_read_failed"); return; }
        if (pos != _lastPos) _lastAdvanceT = now;
        else if (now - _lastAdvanceT > stallSeconds) { BeginInterruption(now, "input_stalled", _lastAdvanceT); return; }
        DrainTo(pos, now, 0);
        if (_wav != null && _wav.Failed && !_writerFailedReported) { _writerFailedReported = true; OnWriterFailed(now); }
        if (!_writerFailedReported && _queue != null && !_queue.Threaded && now - _lastFlushT >= flushIntervalSeconds) { _wav.Flush(); _lastFlushT = now; }
        if (now - _lastFlushT >= 2.0 * Math.Max(0.5f, flushIntervalSeconds)) { _lastFlushT = now; WriteMetadata("recording"); }
    }

    void OnWriterFailed(double now)
    {
        SetState(AudioCaptureState.WriteFailed); FailureReason = "wav_write_failed:" + _wav.Error; Manifest.failure_reason = FailureReason;
        Emit("audio_recording_failed", now, null, "WriteFailed:" + _wav.Error);
        Debug.LogError("[Part3AudioRecorder] write failed: " + _wav.Error);
    }

    // Reads every frame the microphone produced since the last call (up to 'trimTail' frames fewer, to enforce an end boundary) and hands it to the writer.
    void DrainTo(int pos, double now, int trimTail)
    {
        if (_cursor == null || _wav == null) return;
        var d = _cursor.Advance(pos, now);
        if (d.lostFrames > 0) OnOverrun(d, now, pos);
        int toRead = Math.Max(0, d.frames - Math.Max(0, trimTail));
        int done = 0;
        while (toRead > 0)
        {
            int cnt = Math.Min(toRead, ChunkFrames);
            byte[] bytes = _queue.Rent();
            int framesInBuf = 0;
            while (framesInBuf < cnt)
            {
                int start = (d.ringStart + done + framesInBuf) % _ringFrames;
                int part = Math.Min(cnt - framesInBuf, _ringFrames - start);
                // AudioClip.GetData fills the whole array: read at an offset that keeps the whole array inside the ring (no reliance on wrap-around behaviour) and skip to the wanted frame
                int rs = Math.Min(start, Math.Max(0, _ringFrames - ChunkFrames)); int skip = (start - rs) * _channels;
                bool ok = false; try { ok = mic.GetData(_readBuf, rs); } catch (Exception) { }
                if (!ok) { break; }
                Account(_readBuf, skip, part * _channels);
                _clipCount += PcmConvert.FloatToPcm16(_readBuf, skip, bytes, framesInBuf * _channels * 2, part * _channels);
                framesInBuf += part;
            }
            if (framesInBuf == 0) { _queue.Enqueue(bytes, 0); _droppedFrames += toRead; _chunkDropped += toRead; _map.BeginSegment(_framesWritten); break; }   // unreadable ring: count the loss, never invent samples
            bool accepted = !_wav.Failed && _queue.Enqueue(bytes, framesInBuf * _channels * 2);
            if (accepted) { _framesWritten += framesInBuf; _map.ExtendSegment(_framesWritten); }
            else if (!_wav.Failed) { _droppedFrames += framesInBuf; _chunkDropped += framesInBuf; _map.BeginSegment(_framesWritten); }
            else { _droppedFrames += framesInBuf; _chunkDropped += framesInBuf; }
            done += framesInBuf; toRead -= framesInBuf;
            if (framesInBuf < cnt) break;
        }
        _lastPos = pos;
        if (d.frames > 0 && (now - _lastAnchorT >= anchorIntervalSeconds))
        {
            var a = _map.AddAnchor(_framesWritten, now, pos, AudioSettings.dspTime); WriteAnchorRow(a); _lastAnchorT = now; _chunkAnchorId = a.id; Manifest.clock_anchor_count = _map.AnchorCount;
        }
        _chunkLastPos = pos;
        if (now - _chunkOpenT >= chunkSeconds && _framesWritten > _chunkStartFrame) CloseChunk(now, "ok");
    }

    void Account(float[] buf, int offset, int samples)
    {
        double ss = 0; long quiet = 0; float peak = _peak;
        for (int i = 0; i < samples; i++) { float x = buf[offset + i]; if (float.IsNaN(x)) x = 0f; float a = x < 0 ? -x : x; ss += (double)x * x; if (a > peak) peak = a; if (a < 0.001f) quiet++; }
        _sumSq += ss; _sampleCount += samples; _quietCount += quiet; _peak = peak;
    }

    void OnOverrun(MicCursor.Drain d, double now, int pos)
    {
        _overruns++; _chunkOverruns++; _lostFrames += d.lostFrames;
        double readableSec = d.frames / (double)_rate, lostSec = d.lostFrames / (double)_rate;
        double gapEnd = now - readableSec, gapStart = gapEnd - lostSec;
        if (_framesWritten > _chunkStartFrame) CloseChunk(gapStart, "ok");
        WriteIndexRow(_framesWritten, _framesWritten, gapStart, gapEnd, pos, _chunkAnchorId, d.lostFrames, "gap_overrun", _map.Current != null ? _map.Current.id : 0, 0); _chunkIndex++;
        var seg = _map.BeginSegment(_framesWritten);
        var a = _map.AddAnchor(_framesWritten, gapEnd, pos, AudioSettings.dspTime); WriteAnchorRow(a); _chunkAnchorId = a.id;
        _gaps.Add(gapStart.ToString("F3", CultureInfo.InvariantCulture) + "-" + gapEnd.ToString("F3", CultureInfo.InvariantCulture) + ":buffer_overrun:" + d.lostFrames + "_frames");
        OpenChunk(_framesWritten, gapEnd, pos);
        if (State == AudioCaptureState.Recording) SetState(AudioCaptureState.BufferOverrun);
        Emit("audio_buffer_overrun", now, d.lostFrames, "lost_s=" + lostSec.ToString("F3", CultureInfo.InvariantCulture) + ";gap=" + gapStart.ToString("F3", CultureInfo.InvariantCulture) + "-" + gapEnd.ToString("F3", CultureInfo.InvariantCulture));
    }

    // ---- chunk / anchor / index rows ----
    void OpenChunk(long startFrame, double tOpen, int pos) { _chunkStartFrame = startFrame; _chunkOpenT = tOpen; _chunkDropped = 0; _chunkOverruns = 0; _chunkLastPos = pos; _chunkSegment = _map.Current != null ? _map.Current.id : 0; }

    void CloseChunk(double now, string status)
    {
        long end = _framesWritten; if (end <= _chunkStartFrame && _chunkDropped == 0) { _chunkOpenT = now; return; }
        WriteIndexRow(_chunkStartFrame, end, _map.TimeOf(_chunkStartFrame), _map.TimeOf(end), _chunkLastPos, _chunkAnchorId, _chunkDropped, status, _chunkSegment, _chunkOverruns);
        _chunkIndex++; OpenChunk(end, now, _chunkLastPos);
    }

    void WriteIndexRow(long s0, long s1, double t0, double t1, int pos, int anchorId, long dropped, string status, int segment, int overruns)
    {
        if (_indexCsv == null) return;
        long frames = s1 - s0; string sid = sessionIdProvider?.Invoke() ?? "";
        var f = new List<string> { sid, "Part3", _recordingId, _chunkIndex.ToString(CultureInfo.InvariantCulture), s0.ToString(CultureInfo.InvariantCulture), s1.ToString(CultureInfo.InvariantCulture),
            t0.ToString("F6", CultureInfo.InvariantCulture), t1.ToString("F6", CultureInfo.InvariantCulture), _rate.ToString(CultureInfo.InvariantCulture), _channels.ToString(CultureInfo.InvariantCulture),
            frames.ToString(CultureInfo.InvariantCulture), (frames / (double)_rate).ToString("F6", CultureInfo.InvariantCulture), pos.ToString(CultureInfo.InvariantCulture),
            anchorId >= 0 ? anchorId.ToString(CultureInfo.InvariantCulture) : "", dropped.ToString(CultureInfo.InvariantCulture), (_overruns).ToString(CultureInfo.InvariantCulture), status, segment.ToString(CultureInfo.InvariantCulture) };
        _indexCsv.WriteLine(_indexCsv.Join(f));
    }

    void WriteAnchorRow(AudioClockMap.Anchor a)
    {
        if (_anchorCsv == null) return;
        string sid = sessionIdProvider?.Invoke() ?? "";
        var f = new List<string> { sid, _recordingId, a.id.ToString(CultureInfo.InvariantCulture), a.segment.ToString(CultureInfo.InvariantCulture), a.sampleIndex.ToString(CultureInfo.InvariantCulture),
            a.tAbs.ToString("F6", CultureInfo.InvariantCulture), a.micPosition.ToString(CultureInfo.InvariantCulture), _rate.ToString(CultureInfo.InvariantCulture), a.dspTime.ToString("F6", CultureInfo.InvariantCulture) };
        _anchorCsv.WriteLine(_anchorCsv.Join(f));
    }

    // ============================================================================================ interruption and recovery

    void BeginInterruption(double now, string reason, double gapStartOverride = double.NaN)
    {
        if (State == AudioCaptureState.RecordingInterrupted) return;
        // best effort: take what the ring still holds before declaring the gap
        try { if (mic.IsRecording(_device)) DrainTo(mic.GetPosition(_device), now, 0); } catch (Exception) { }
        if (_framesWritten > _chunkStartFrame) CloseChunk(now, "ok");
        _gapStartT = double.IsNaN(gapStartOverride) ? now : gapStartOverride;
        _interruptions++; _restartAttempts = 0; _nextRestartT = now; _awaitFirst = false;
        SetState(AudioCaptureState.RecordingInterrupted);
        Emit("audio_recording_interrupted", now, null, reason);
        Debug.LogWarning("[Part3AudioRecorder] interrupted: " + reason);
        _interruptReason = reason;
    }
    string _interruptReason = "";

    void HandleInterrupted(double now)
    {
        if (_paused) return;
        if (_awaitFirst)
        {
            int pos = 0; try { pos = mic.GetPosition(_device); } catch (Exception) { }
            if (pos > 0)
            {
                double first = AudioClockMap.FirstSampleTime(now, pos, _rate); if (first < _restartRequestedT) first = _restartRequestedT;
                // new continuous segment: the file continues (no samples invented for the gap), the time axis jumps
                _cursor = new MicCursor(_ringFrames, _rate); _cursor.Begin(0, first);
                _map.BeginSegment(_framesWritten); var a0 = _map.AddAnchor(_framesWritten, first, 0, AudioSettings.dspTime); WriteAnchorRow(a0); _chunkAnchorId = a0.id;
                OpenChunk(_framesWritten, first, 0); _lastPos = 0; _lastAdvanceT = now; _lastAnchorT = now;
                double gap = first - _gapStartT; _interruptionTotal += gap;
                _gaps.Add(_gapStartT.ToString("F3", CultureInfo.InvariantCulture) + "-" + first.ToString("F3", CultureInfo.InvariantCulture) + ":" + _interruptReason);
                WriteIndexRow(_framesWritten, _framesWritten, _gapStartT, first, 0, a0.id, 0, "gap_interruption", _map.Current.id, 0); _chunkIndex++;
                _awaitFirst = false; SetState(AudioCaptureState.Recording);
                Emit("audio_recording_resumed", now, gap, "gap_s=" + gap.ToString("F3", CultureInfo.InvariantCulture) + ";reason=" + _interruptReason);
                DrainTo(pos, now, 0);
            }
            else if (now - _restartRequestedT > startTimeoutSeconds) { _awaitFirst = false; _restartAttempts++; _nextRestartT = now + restartBackoffSeconds; try { mic.End(_device); } catch (Exception) { } }
            return;
        }
        if (_restartAttempts >= maxRestartAttempts || now < _nextRestartT) return;
        _restartAttempts++;
        bool ok = false; try { mic.End(_device); ok = mic.Start(_device, true, ringSeconds, _rate); } catch (Exception) { }
        if (!ok) { _nextRestartT = now + restartBackoffSeconds; return; }
        int fr = 0; try { fr = mic.ClipFrames(); } catch (Exception) { }
        if (fr > 0 && fr != _ringFrames) { _ringFrames = fr; }
        _awaitFirst = true; _restartRequestedT = now;
    }

    void OnApplicationPause(bool pause)
    {
        if (Finalized) return;
        double now = CaptureClock.TAbs;
        if (pause) { if (IsRecording) { BeginInterruption(now, "app_pause"); } _paused = true; }
        else { _paused = false; _nextRestartT = now; }
    }

    // ============================================================================================ stop / finalize

    /// <summary>Stops at the Part 3 trial end boundary. Idempotent (a second call returns the first result). Drains every frame up to tEnd, finalises the files and reports through the manifest and events.</summary>
    public bool StopAtBoundary(double tEnd, string reason)
    {
        if (Finalized) return FinalizationOk;
        if (!Started && !IsRecording) { return true; }   // never recorded (disabled / failed start): nothing to stop
        if (_stopRequested) return FinalizationOk;
        _stopRequested = true; _tEnd = tEnd;
        Emit("audio_recording_stop_requested", tEnd, null, reason);
        bool wasInterrupted = State == AudioCaptureState.RecordingInterrupted;
        SetState(AudioCaptureState.Stopping);
        if (!wasInterrupted)
        {
            double now = CaptureClock.TAbs;
            try { int pos = mic.GetPosition(_device); int trim = (int)Math.Round(Math.Max(0.0, now - tEnd) * _rate); DrainTo(pos, now, trim); } catch (Exception) { }
        }
        try { mic.End(_device); } catch (Exception) { }
        Emit("audio_recording_stopped", CaptureClock.TAbs, null, reason);
        return FinalizeFiles(reason, wasInterrupted || (_awaitFirst));
    }

    /// <summary>Called by the session finalisation (and on destroy / quit): finishes a recording that is still open - then it is marked incomplete (Part 3 did not reach its end) - and closes the files.</summary>
    public bool FinalizeForSession(string reason)
    {
        if (Finalized) return FinalizationOk;
        if (!Started && _wav == null) { Finalized = true; if (Manifest.status == "not_started") Manifest.status = Manifest.enabled ? "not_started" : "not_enabled"; return true; }
        if (!_stopRequested)
        {
            double now = CaptureClock.TAbs;
            _stopRequested = true; _tEnd = now; _endedBySession = true;
            Emit("audio_recording_stop_requested", now, null, "session_" + reason);
            bool wasInt = State == AudioCaptureState.RecordingInterrupted;
            SetState(AudioCaptureState.Stopping);
            if (!wasInt) { try { DrainTo(mic.GetPosition(_device), now, 0); } catch (Exception) { } }
            try { mic.End(_device); } catch (Exception) { }
            Emit("audio_recording_stopped", CaptureClock.TAbs, null, "session_" + reason);
            return FinalizeFiles("session_" + reason, true);
        }
        return FinalizationOk;
    }
    bool _endedBySession;

    bool FinalizeFiles(string reason, bool incompleteHint)
    {
        SetState(AudioCaptureState.Finalizing);
        double tEnd = _tEnd;
        if (_framesWritten > _chunkStartFrame) CloseChunk(tEnd, "ok");
        bool queueOk = true; try { queueOk = _queue == null || _queue.Complete(5000); } catch (Exception) { queueOk = false; }
        WavValidation v = null;
        try { v = _wav != null ? _wav.Finish() : new WavValidation { problem = "no_writer" }; } catch (Exception e) { v = new WavValidation { problem = "exception:" + e.GetType().Name }; }
        bool wavOk = v != null && v.valid && queueOk;
        long expectedFrames = _framesWritten;
        string fileValidation = wavOk ? (v.frames == expectedFrames ? "ok" : "frame_count_mismatch:wav=" + v.frames + ";index=" + expectedFrames) : ((v != null ? v.problem : "no_validation") + (queueOk ? "" : ";writer_thread_did_not_finish"));
        if (wavOk && v.frames != expectedFrames) wavOk = false;
        try { _indexCsv?.Close(); _anchorCsv?.Close(); } catch (Exception) { }
        var idx = AudioIndexCheck.Check(Path.Combine(_audioDir ?? "", "audio_index.csv"), wavOk ? v.frames : expectedFrames);
        Manifest.index_consistent = idx.consistent; Manifest.index_validation = idx.problem;
        FillSummary(v, wavOk, fileValidation, reason, incompleteHint);
        FinalizationOk = wavOk && idx.consistent;
        Finalized = true;
        SetState(FinalizationOk ? (Manifest.status == "incomplete" ? AudioCaptureState.Incomplete : AudioCaptureState.Closed) : AudioCaptureState.FinalizationFailed);
        Manifest.state_trail = _trail.ToArray();
        WriteMetadata(Manifest.status);
        if (FinalizationOk) Emit("audio_file_finalized", CaptureClock.TAbs, Manifest.total_frames, "status=" + Manifest.status + ";file=" + Manifest.file + ";frames=" + Manifest.total_frames);
        else { FailureReason = Manifest.failure_reason = "finalization_failed:" + fileValidation + (idx.consistent ? "" : ";index:" + idx.problem); Emit("audio_recording_failed", CaptureClock.TAbs, null, "FinalizationFailed:" + FailureReason); Debug.LogError("[Part3AudioRecorder] " + FailureReason); }
        return FinalizationOk;
    }

    void FillSummary(WavValidation v, bool wavOk, string fileValidation, string reason, bool incompleteHint)
    {
        var m = Manifest;
        m.total_frames = _framesWritten; m.duration_sec = _rate > 0 ? _framesWritten / (double)_rate : 0; m.file_bytes = v != null ? v.fileBytes : 0;
        m.dropped_frames = _droppedFrames; m.lost_frames_overrun = _lostFrames; m.backlog_dropped_frames = _queue != null ? _queue.DroppedByBacklog : 0; m.buffer_overruns = _overruns;
        m.interruption_count = _interruptions; m.interruption_total_s = _interruptionTotal; m.gaps = _gaps.ToArray();
        m.end_t_abs = _tEnd; m.first_sample_t_abs_est = _firstSampleT;
        m.last_sample_t_abs_est = _map != null && _framesWritten > 0 ? _map.TimeOf(_framesWritten - 1) : -1;
        m.file_validation = fileValidation; m.finalization_success = wavOk;
        if (_map != null)
        {
            var fits = new List<AudioSegmentFit>(); double worst = 0; double sumSqRes = 0; int n = 0;
            foreach (var s in _map.Segments) { if (!s.fitted) continue; fits.Add(new AudioSegmentFit { segment = s.id, first_sample = s.firstSample, last_sample_exclusive = s.lastSample, t_first_est = s.t0 + (s.firstSample - s.s0) * s.secondsPerSample, rate_hz_fit = s.RateHz, drift_ppm = s.DriftPpm, residual_rms_ms = s.residualRmsSec * 1000.0, anchors = s.anchors.Count }); worst = Math.Max(worst, s.residualRmsSec); sumSqRes += s.residualRmsSec * s.residualRmsSec; n++; }
            m.segments = fits.ToArray(); m.clock_anchor_count = _map.AnchorCount; m.residual_rms_ms = worst * 1000.0;
            double blockMs = 1000.0 / Math.Max(1.0, 90.0);   // one Unity frame at 90 Hz: the drain granularity of the position reading
            m.timing_uncertainty_ms = Math.Max(blockMs, 2.0 * worst * 1000.0);
            m.timing_uncertainty_note = "estimated ± (max of one 90 Hz frame and twice the anchor residual RMS) for the RELATIVE mapping; the microphone's input latency before the position cursor (typically tens of ms) is NOT measurable without a loopback test and is not included";
        }
        if (_sampleCount > 0)
        {
            double rms = Math.Sqrt(_sumSq / _sampleCount); m.rms_dbfs = rms > 0 ? 20.0 * Math.Log10(rms) : -999; m.peak_abs = _peak;
            m.clipping_percent = 100.0 * _clipCount / _sampleCount; m.near_silence_percent = 100.0 * _quietCount / _sampleCount;
        }
        bool unrecoveredInterruption = _awaitFirst || (!double.IsNaN(_gapStartT) && _gaps.Count < _interruptions);
        if (!wavOk) m.status = "incomplete";
        else if (_endedBySession) m.status = "incomplete";
        else if (unrecoveredInterruption || _writerFailedReported) m.status = "incomplete";
        else if (_overruns > 0 || _interruptions > 0 || _droppedFrames > 0) m.status = "completed_with_gaps";
        else m.status = "completed";
        if (m.status == "incomplete" && string.IsNullOrEmpty(m.failure_reason)) m.failure_reason = _endedBySession ? "session_ended_before_part3_end:" + reason : (unrecoveredInterruption ? "ended_while_interrupted:" + _interruptReason : "");
    }

    // ============================================================================================ metadata file (atomic) and manifest copy

    void WriteMetadata(string status)
    {
        try
        {
            if (string.IsNullOrEmpty(_metaPath)) return;
            var m = Manifest;
            if (!Finalized)
            {
                m.status = status; m.total_frames = _framesWritten; m.dropped_frames = _droppedFrames; m.buffer_overruns = _overruns; m.interruption_count = _interruptions;
                m.duration_sec = _rate > 0 ? _framesWritten / (double)_rate : 0; m.state_trail = _trail.ToArray();
            }
            string json = JsonUtility.ToJson(m, true);
            string tmp = _metaPath + ".tmp";   // temp + replace: the file is either the previous complete JSON or the new complete JSON
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(_metaPath)) File.Delete(_metaPath);
            File.Move(tmp, _metaPath);
        }
        catch (Exception) { /* the metadata file is a convenience copy; the manifest block is authoritative */ }
    }

    /// <summary>Copies the live status into the session manifest block (cheap, no I/O).</summary>
    public void FillManifest(AudioCaptureManifest dst)
    {
        var m = Manifest;
        if (!Finalized && Started)
        {
            m.total_frames = _framesWritten; m.duration_sec = _rate > 0 ? _framesWritten / (double)_rate : 0; m.dropped_frames = _droppedFrames; m.lost_frames_overrun = _lostFrames;
            m.buffer_overruns = _overruns; m.interruption_count = _interruptions; m.interruption_total_s = _interruptionTotal; m.gaps = _gaps.ToArray(); m.state_trail = _trail.ToArray();
            if (_map != null) m.clock_anchor_count = _map.AnchorCount;
        }
        m.state = State.ToString();
        JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(m), dst);
    }
}

/// <summary>Consistency checks of audio_index.csv against the finalised WAV (frame totals, monotonic indices, no overlap, ordered times).</summary>
public static class AudioIndexCheck
{
    public sealed class Result { public bool consistent; public string problem = ""; public long totalFrames; public int rows; }

    public static Result Check(string indexPath, long wavFrames)
    {
        var r = new Result();
        try
        {
            if (!File.Exists(indexPath)) { r.problem = "index_missing"; return r; }
            var lines = File.ReadAllLines(indexPath); if (lines.Length < 2) { r.problem = "index_empty"; return r; }
            var h = lines[0].Split(','); int iS = Array.IndexOf(h, "sample_index_start"), iE = Array.IndexOf(h, "sample_index_end_exclusive"), iT0 = Array.IndexOf(h, "t_abs_start_est"), iT1 = Array.IndexOf(h, "t_abs_end_est"), iSt = Array.IndexOf(h, "write_status");
            if (iS < 0 || iE < 0 || iT0 < 0 || iT1 < 0 || iSt < 0) { r.problem = "index_header_unexpected"; return r; }
            long next = 0; double lastT = double.NegativeInfinity; long frames = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue; var f = lines[i].Split(','); r.rows++;
                long s = long.Parse(f[iS], CultureInfo.InvariantCulture), e = long.Parse(f[iE], CultureInfo.InvariantCulture);
                double t0 = double.Parse(f[iT0], CultureInfo.InvariantCulture), t1 = double.Parse(f[iT1], CultureInfo.InvariantCulture);
                if (s != next) { r.problem = "sample_index_gap_or_overlap_at_row_" + i + ":" + s + "_expected_" + next; return r; }
                if (e < s) { r.problem = "negative_chunk_at_row_" + i; return r; }
                if (t1 < t0 - 1e-6) { r.problem = "time_goes_backwards_in_row_" + i; return r; }
                if (t0 < lastT - 1e-3) { r.problem = "time_not_monotonic_at_row_" + i; return r; }
                lastT = t1; next = e; frames += e - s;
            }
            r.totalFrames = frames;
            if (frames != wavFrames) { r.problem = "index_frames_" + frames + "_differ_from_wav_" + wavFrames; return r; }
            r.consistent = true;
        }
        catch (Exception e) { r.problem = "exception:" + e.GetType().Name; }
        return r;
    }
}
