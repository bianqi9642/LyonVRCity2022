# Part 3 think-aloud audio capture (schema v15)

Status: implemented and covered by Editor tests with a **simulated microphone**. **Not verified on a Quest Pro, Quest Link or Android build.** See section 9.

## 1. What is recorded, and when
* The participant's **microphone input**, continuously, from the **Part 3 Start click** until the **Part 3 trial end** (`ExperimentManager.EndTrial()`, the same boundary as `OnTrialEnd` for gaze / trajectory / NPC capture). Interval `[first audio frame, trial_end)`.
* Silence is kept. No VAD, segmentation, AGC, denoising, compression, resampling or channel conversion. The device's own sample rate (48 kHz when supported) and channel count are used.
* **Part 3 only.** `ExperimentManager.RunPart3` is the only caller; Parts 1 and 2 never reference the recorder (a test scans for it). Switch off with `ExperimentManager.part3AudioRecording = false`; `part3AudioRequired = false` lets Part 3 continue without audio if the microphone fails (the failure is then in the manifest).
* No audio exists from before the Start click: preflight starts the microphone for a probe and discards it without reading a sample.

## 2. Sequence (exact code locations)
| Step | Where | What |
|---|---|---|
| 0 | `ExperimentManager.RunPart3`, after the NPC warm-up, before the Start panel | `ResolvePart3Audio()` -> `DataCaptureManager.EnsureAudioRecorder()` (component added at runtime, **no scene edit**) |
| 1 | `Part3AudioPreflightGate` -> `Part3AudioRecorder.RunPreflight()` | permission (`UnityEngine.Android.Permission` on Android), microphone present, chosen device, supported rate, `audio/` writable + free space, discarded probe. Failure: a panel states the reason (`UIManager.ShowPart3StartPanel(onStart, overrideMessage)`), its button retries. Participant locked meanwhile. |
| 2 | Start panel callback | `startClickT = CaptureClock.TAbs` (first statement of the click callback) |
| 3 | `Part3AudioStartGate` -> `StartRecording(startClickT)` | file opened, `Microphone.Start`, wait for the first sample (timeout 3 s), first-sample time estimated, latency vs the click checked (limit 2 s). Failure: retry panel; no stray file. |
| 4 | `RunPart3`, before `BeginTrial()` | `NoteExperimentStart(t)`; `BeginTrial()` runs only once audio is confirmed recording |
| 5 | `ExperimentManager.EndTrial()` | `StopAtBoundary(CaptureClock.TAbs)`: drain to the boundary, stop the microphone, finalise WAV + index, validate, rename |
| 6 | `DataCaptureManager.FinalizeCapture` | if still open (Part 3 never ended): `FinalizeForSession` -> recording flagged `incomplete`; **then** gaze close, `session_end`, stream closing. A failed audio finalisation keeps the session in `Closing` (`capture_streams_failed` contains `audio`). |

## 3. State machine
`Disabled -> Preflight -> Ready -> StartRequested -> Recording -> Stopping -> Finalizing -> Closed`
Failure / interruption states: `PermissionDenied`, `DeviceUnavailable`, `StartFailed`, `RecordingInterrupted` (restarts up to 3 times with back-off), `BufferOverrun` (recording continues, data loss counted), `WriteFailed`, `FinalizationFailed`, `Incomplete`. Every transition is in `audio_capture.state_trail`; none maps a failure to a success state.

## 4. Buffering
`Microphone.Start(device, loop=true, 10 s)` ring -> drained every frame by `MicCursor` (position wrap + elapsed-time lap detection) -> `AudioClip.GetData` in <= 4096-frame reads (offset kept inside the ring; no reliance on wrap-around behaviour) -> `PcmConvert` (PCM16, clipping counted) -> pooled buffers -> `AudioWriteQueue` (one writer thread, flush + header refresh every 1 s). No full-length buffer in RAM; no per-frame allocation in steady state.
Overrun: when the main thread stalls longer than the ring, the overwritten frames are **counted** (`lost_frames_overrun`, event `audio_buffer_overrun`, index row `gap_overrun`), a new clock segment starts, nothing is invented.

## 5. Files (`<session>/audio/`)
| File | Content |
|---|---|
| `part3_audio.wav` | PCM16 little-endian WAV, 44-byte header. Written as `part3_audio.wav.partial` with a header refreshed at every flush; renamed only after the closing header is written and the file validates. Never overwrites an existing file. |
| `audio_index.csv` | **Schema v17: written ONCE, at finalisation, from one frozen clock map** (see section 'Frozen time mapping (schema v17)'). One row per ~1 s chunk plus one **zero-frame row per gap**: `session_id, part_id, audio_recording_id, chunk_index, sample_index_start, sample_index_end_exclusive, t_abs_start_est, t_abs_end_est, sample_rate_hz, channels, frames_written, duration_sec, microphone_position, clock_anchor_id, dropped_frames, buffer_overrun_count, write_status (ok / gap_overrun / gap_interruption), segment`. `t_abs_*_est` are produced by the final per-segment fit (the same one reported in the metadata), so adjacent rows chain exactly. While recording, only the immutable facts are journalled (`audio_index.csv.partial`, no timestamps); it is deleted when the final index validated. |
| `audio_anchors.csv` | The clock anchors: `session_id, audio_recording_id, clock_anchor_id, segment, sample_index, t_abs, microphone_buffer_position, sample_rate_hz, dsp_time` (about 1 Hz). Since v17 **every** anchor is a row, including anchor 0 (sample 0), which v15/v16 held only in memory because the file was opened after it was taken; `t_abs` has 9 decimals so the fit can be reproduced from this file alone. |
| `audio_metadata.json` | The same block as the manifest `audio_capture`, refreshed while recording (`status: recording`) and final at close. |

Events (`events.csv`, one row per transition, never per buffer, all before `session_end`): `audio_permission_checked`, `audio_permission_granted`, `audio_recording_requested`, `audio_recording_started` (value_num = start latency ms), `audio_recording_interrupted`, `audio_recording_resumed` (value_num = gap s), `audio_buffer_overrun` (value_num = lost frames), `audio_recording_stop_requested`, `audio_recording_stopped`, `audio_file_finalized`, `audio_recording_failed`.

Manifest `audio_capture`: status (`not_enabled | not_started | recording | completed | completed_with_gaps | incomplete | failed`), state, failure_reason, format, sample_rate_hz, channels, bit_depth, file names, clock_source, device_descriptor (the device name Unity lists; no user identity), runtime_path, `start_button_clicked_t_abs`, `audio_start_requested_t_abs`, `first_sample_t_abs_est`, `experiment_effective_start_t_abs`, `audio_start_latency_ms`, `start_attempts`, `end_t_abs`, `last_sample_t_abs_est`, `total_frames`, `duration_sec`, `file_bytes`, `dropped_frames`, `lost_frames_overrun`, `buffer_overruns`, `interruption_count`, `interruption_total_s`, `gaps[]` (`t0-t1:reason`), `clock_anchor_count`, `segments[]` (per-segment fitted rate, drift ppm, residual RMS), `timing_uncertainty_ms` (+ note), `finalization_success`, `file_validation`, `index_consistent`, signal diagnostics (`rms_dbfs, peak_abs, clipping_percent, near_silence_percent`; diagnostics only), `state_trail`.

## 6. Time model
* One clock: `CaptureClock.TAbs` = `Time.realtimeSinceStartupAsDouble` (monotonic, timeScale-independent, continues while the app is paused/focus lost as long as the process runs). Not `DateTime`, not `Time.time`, no second stopwatch, never file times.
* `sample_index` = WAV frame index (0-based, never reset, lost frames not counted). `t_abs(sample_index)` = the **start** of that frame. Intervals half-open.
* First sample: `first_sample_t = t_reading - ring_position / sample_rate` (the microphone cursor, read at `t_reading`, says how many frames are older), clamped to not predate the request. `audio_start_latency_ms = first_sample_t - start_click_t`.
* Mapping: per continuous **segment** an affine least-squares fit of the anchors, `t = t0 + (s - s0) * seconds_per_sample`. With fewer than 8 anchors only the offset is fitted (nominal rate), because drift cannot be separated from jitter. A gap (interruption or overrun) starts a new segment: the sample axis stays contiguous, only the time axis jumps.
* Query: "audio between t_abs 32.5 and 35.0": `docs/part3_audio_alignment_example.py <session> 32.5 35.0` returns the sample range, the extracted audio and the gaze / trajectory / events / NPC rows of the same interval.
* **Uncertainty.** `timing_uncertainty_ms` = max(one 90 Hz frame, 2 x anchor residual RMS) for the **relative** mapping. It does **not** include the microphone's input latency before its position cursor (the recorder reads "now" when it sees the position advance): tens of ms are typical and are only measurable with a loopback test on the device (section 8). Do not claim better than that.

## 7. Interruptions and shutdown
* `OnApplicationPause(true)`, `IsRecording == false`, a position that stops advancing for 1 s, or a failed position read = **interruption**: best-effort drain, event `audio_recording_interrupted`, gap start. Recovery re-opens the microphone, starts a new segment, writes a zero-frame `gap_interruption` row and `audio_recording_resumed`. If recovery fails the recorder stays `RecordingInterrupted` and the recording ends `incomplete` (`ended_while_interrupted`).
* Session finalisation before Part 3 ends -> file finalised but `incomplete` (`session_ended_before_part3_end`).
* Process death: the `.partial` file keeps the data flushed so far; `WavFormat.TryRecoverPartial` rebuilds a valid copy from the bytes present (never modifies the partial). `audio_metadata.json` says `status: recording` until a clean close. A file with the final name exists only after validation.

## 8. Validation procedure (hardware)
1. Start Part 3; say a marker phrase immediately after pressing Start; speak at known events (teleport, target selection); say a phrase just before the end; end Part 3.
2. Check `audio_capture`: `status completed`, `finalization_success`, `index_consistent`, `audio_start_latency_ms` (expect tens to a few hundred ms), `buffer_overruns 0`, `interruption_count 0`.
3. Run the example script around each marker and compare with `gaze.csv` / `events.csv`.
4. **Loopback timing test** (needed once per device/runtime): play a short click from the headset speaker at a logged `t_abs` while recording, locate the click in the WAV, and compare the WAV time (`time_of(frame)`) with the logged time: the difference is the input latency to add (and its spread is the real uncertainty).
5. Remove / re-seat the headset during recording and press the system button: check the interruption rows; verify nothing is filled in.
6. Windows Link: confirm which microphone Unity selected (`device_descriptor`); set `preferredDevice` / `requiredDeviceNameContains` (for example `Oculus` or `Quest`) if the Windows default is not the headset.

## 9. Platform status (honest)
| | Windows Editor / Quest Link | Android Standalone (Quest Pro) |
|---|---|---|
| Compiles | yes (Editor tests run) | **not built here** |
| Microphone capture verified | **no** (simulated device only) | **no** |
| Permission | no runtime permission needed on desktop; an OS-level block shows as "no microphone device" | `UnityEngine.Android.Permission.RequestUserPermission(Microphone)` is requested at preflight and the Start is blocked until granted. **`RECORD_AUDIO` in the built manifest is expected (Unity adds it when the `Microphone` class is used; the project has no custom manifest and Meta's `OVRManifestPreprocessor` leaves `RECORD_AUDIO` untouched) but was not inspected: check the APK (`aapt dump permissions`).** |
| Device choice | Windows default input unless `preferredDevice` is set; **not necessarily the headset** | the Quest's input device list as Unity reports it |
| Output location | session folder under `Application.persistentDataPath` (existing convention) | same |
| Pause / resume | handled as interruption (simulated only) | handled as interruption; Unity pauses mic input on suspend (behaviour on the device untested) |

## 10. Known limits
Input latency before the position cursor is unknown without the loopback test; microphone position granularity is a few ms; a very long main-thread stall beyond the 10 s ring loses data (counted); a recording that Unity itself stops while the app keeps running is detected by the 1 s stall rule, not instantly; the 4 GiB WAV size limit (about 12 h at 48 kHz mono) is far above a session.


## Frozen time mapping (schema v17)

Defect fixed: in v15/v16 each index row was stamped with the live clock map at the moment the chunk was closed, and the map was refitted whenever an anchor arrived. Two adjacent chunks share a sample boundary but were stamped with two different mapping versions, so
`t_abs_end_est(n)` could exceed `t_abs_start_est(n+1)` by a few ms although the sample indices were contiguous and no audio was duplicated. The 1 ms index check then reported `time_not_monotonic_at_row_<n>`, the audio stream was marked `FinalizationFailed`
and the session stayed in `Closing` (audited session 20261104T141934Z_424f001ded51: 7 rows overlapping by 1.1-4.1 ms, valid WAV). The first anchor was also never written (the anchor file was opened after it was taken: 62 rows vs 63 anchors).

Now:
1. **During recording** only immutable facts are kept per chunk (index, segment, sample range, frames, status, anchor id) and journalled to `audio_index.csv.partial`. Anchors are written to `audio_anchors.csv` as they are taken, starting with anchor 0.
2. **At finalisation** (after the stop at the existing boundary, unchanged): the writer queue is drained, the WAV is validated, anchor collection stops, every anchor is checked to be a row of the file (`clock_anchor_count` = persisted rows), `AudioClockMap.Freeze()` fits every segment once from all its anchors,
   and `audio_index.csv` is written once. A chunk's start and exclusive end use `TimeOfIn(segment, sample)`: the same function at the same sample, so `t_abs_start_est(n+1) == t_abs_end_est(n)` exactly (the files differ at most by the 6-decimal formatting). A gap row spans from the end of the previous chunk to the start of the next one, so rows always chain; the jump is the only place where the time axis is discontinuous.
3. `AudioIndexCheck.Check(index, wavFrames, anchors)` validates consecutive chunk indices, contiguous sample indices, explicit gap rows (zero frames) at every segment change, positive sample ranges, total frames == WAV frames, non-decreasing time, **no overlap and no hole between adjacent rows** (tolerance 2 microseconds = float formatting), and that every chunk time is **reproducible from the persisted anchors** (re-fit; tolerance 5 microseconds).
   Discrepancies are classified: `float_representation` (accepted), `actual_discontinuity` (a gap row, accepted), `timestamp_mapping_defect` (fails). The clock-fit uncertainty (+-`timing_uncertainty_ms`) is a property of the mapping, not a licence for overlapping rows.
4. Manifest / metadata additions: `time_mapping` (`final_frozen_affine_per_segment_v1`), `time_mapping_frozen`, `index_rows`, `index_max_boundary_mismatch_s`, `index_timestamp_classification`; `clock_anchor_count` is the number of persisted anchor rows; a missing / unwritten anchor makes the finalisation fail visibly (`anchors_persisted_<n>_of_<m>`).
5. The stop boundary is untouched: audio still stops at `EndTrial()` (`phase_end`), not at `trial_end`, the End panel, the participant's confirmation or `part_end`.
