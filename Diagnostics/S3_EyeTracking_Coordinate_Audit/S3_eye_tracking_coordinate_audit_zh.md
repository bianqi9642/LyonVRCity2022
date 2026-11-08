# S3 眼动有效性与 HMD / 注视坐标审计（含最小录制代码修复）

对象：**仅** 会话 `20261106T145652Z_0980aa930c32`（S3）。没有使用 S1、S2 的任何数据做结论；没有重新调查 XR Origin 失控问题；原始录制未被修改。
证据标签：**Confirmed**（数据或源码直接证明）· **Strongly supported**（多项独立观察支持）· **Uncertain**（证据不足）· **Not testable**（缺少数据/设备/运行时证据）。
所有输出位于 `Diagnostics/S3_EyeTracking_Coordinate_Audit/`；**所有结论均为 Unity Editor 离线分析，没有 Quest 头显验证。**

---

## 1. 执行摘要

1. **`start_failed` 是生命周期状态机的“过期终态”，不是数据不可用。** `EyeTrackingLifecycle` 在启动阶段（约会话前 2 分钟内）用完了有界的 8 次重试后进入终态 `start_failed`，**此后不再检查**；而 S3 的每一行都证明 `OVRPlugin.eyeTrackingEnabled` 为 true、两个 `OVREyeGaze` 组件处于启用状态、提供者在持续输出（见 §3）。**[Confirmed：状态 ≠ 实际提供者状态；失败原因字符串 Not testable（S3 清单缺失）]**
2. **“提供者有效标志为空”的原因**：`left/right_provider_valid` 只在生命周期探针可用（状态为 Running/TrackingUnavailable）时才写入；`start_failed` 时一律留空，并且 **有效性判定退化为“置信度≥0.5”**——而 `OVREyeGaze.Confidence` 在提供者判无效时保持旧值，所以旧代码在“提供者未知”时不能证明任何一行有效。**[Confirmed（源码）]**
3. **但 S3 的现有数据中恰好含有逐行的提供者有效性证据**：原始提供者读取列（`left/right_raw_tracking_*`）仅在该眼 `IsValid` 为真时才填写；3096/3096 行两眼均有值，`provider_time_s` 在 3095/3095 个间隔上前进，录制的注视方向与“当前提供者样本 × 渲染相机”的方向偏差中位 0°、最大 0.028°。因此 **3038 行（98.1 %）可归为 “提供者有效，由原始读取证实”**，57 行原点被 eye-anchor 回退替换（方向仍是提供者的），1 行缺少溯源字段。没有“提供者无效/不可用”的行。**[Confirmed（列含义由源码确认）；类别标注 Strongly supported]**
4. **置信度 0.998 不是应用写入的常量**：整个项目不存在 0.998 常量；`OVREyeGaze.Confidence` 的初值为 0，仅在 `Update()` 中用提供者 `eyeGaze.Confidence` 赋值（SDK `OVREyeGaze.cs:158-168`）。它是 **提供者给出的、在该运行时上不变的数值**，没有质量区分能力。**[Confirmed 来源；是否反映测量质量 Not testable]**
5. **HMD 坐标是正确的，且 0.38 m 基线有合理解释**：`hmd_world_y = XR Origin 底座 + 1.36144 + 头部相对重新定中点的高度（OVRPlugin 眼位 Y）`，三项关系在 S3 中被独立验证（残差中位 −1 mm，SD 1.4 cm）。S3 头部全程比定中点低 −0.378 m，两次升高到 +0.09～+0.11 m（≈定中点高度）——大小 ≈0.47 m，方向与坐→站一致。**[Confirmed 坐标关系；“坐/站”归因 Strongly supported，需与你的实验记录对照]**
6. **`GazeOrigin` 不是测得的眼睛位置，而是 HMD 相对的名义代理**：提供者的头部空间眼位置恒为 (±0.0325, 0, 0) m（SD 0.04 mm），记录的原点 = 渲染相机姿态 × 该常量，因此在头部坐标系中的偏移为 左 −0.0325 / 右 +0.0325 m（实测 −0.03247 / +0.03252），垂直与前后 ≈0。**[Confirmed]**
7. **`GazeDir` 在世界坐标中，变换正确**：把记录方向旋回头部坐标系，仰角 −16.73°，与提供者自己的头部空间方向仰角 −16.68° 一致（差中位 −0.06°，最大 0.8°）。“注视比头部前向低 17–19°”因此 **不是变换/轴/符号错误，而是提供者报告的眼在头内方向**；其物理合理性（头显佩戴/校准/真实行为）**Uncertain**（需要在头显上做定点注视校准）。
8. **采样率 3.2 Hz = Unity 帧率 3.2 fps**（`frame_index` 每行恰好 +1），不是录制间隔或提供者限制。原因（NPC 规模？）**Uncertain**：S3 的事件记录了 3521 个活跃 NPC 与 5247 个生命周期，但帧计时块缺失。
9. **已做的最小代码修复（未提交，未在头显验证）**：①生命周期在重试耗尽后被动观察，晚启用的提供者记为 `late_start_success → running`；②逐行提供者有效性改用同一帧的原始提供者读取（不依赖生命周期状态），无法确定时保持空白并拒绝作为眼样本；③对应的 9 个新测试通过。详见 §7 与 `code_change_report.md`。

---

## 2. S3 源文件完整性

| 路径 | 大小 | 行数 / 列数 | 状态 |
|---|---:|---|---|
| `~/Downloads/20261106T145652Z_0980aa930c32.zip`（原件，未改动） | 1 769 108 577 B | – | **截断**：`bsdtar` 报告 `npc_state.csv` 需 6 877 113 570 B，仅有 1 605 472 256 B |
| 解压副本（临时目录，只读使用）`…/scratchpad/posture/s3/20261106T145652Z_0980aa930c32/` | | | |
| `gaze.csv` | 1 463 725 | 3 096 / 61 | 完整（v17） |
| `gaze_diagnostics.csv` | 1 819 501 | 3 096 / 85 | 完整，逐行键一致 |
| `events.csv` | 21 790 | 169 / 20 | 完整（含 `session_end end_confirmed`） |
| `npc_events.csv`、`npc_lifetimes.csv`、`npc_profiles.csv`、`building_registry.csv` | 64 110 482 / 3 608 648 / 10 152 / 180 213 | – | 存在 |
| `audio/*`（WAV 92 178 238 B、index、anchors、metadata） | | | 存在 |
| `npc_state.csv` | 解压后截断（已从临时目录删除） | | 截断，与本审计无关 |
| **`trajectory.csv`、`session_manifest.json`(+.bak)、`session_config.used.json`** | – | – | **不在存档中**（已核对：存档在 `npc_state.csv` 处截断，这三个文件位于其后）。**不假设其存在。** |

* 时间戳：`t_abs`（`CaptureClock.TAbs = Time.realtimeSinceStartupAsDouble`，**进程相对的秒数**，不是 UTC，也不是会话起点相对时间）；`unix_time_ms − 1000·t_abs` 在会话内波动 15.9 ms（读取时刻差）。
* gaze 区间 757.7157–1717.3606 s（959.6 s）；`t_abs` 严格单调，无重复，无 >1 s 间隔。会话：`session_start` 18.35 s，`trial_start` 757.65 s，`trial_end` 1718.67 s，正常结束。
* 因缺少清单：**无法读取生命周期转移日志、`frame_timing`、`ovr_manager_tracking_origin_type`、`eye_tracking.tracking_space` 等运行时记录。** 依赖这些的结论已标 Uncertain / Not testable。

---

## 3. 眼动启动与提供者有效性诊断

### 3.1 数据流（含精确位置；代码版本 = 录制时的 HEAD，S3 之后无相关文件改动）

| 阶段 | 函数 / 变量 / API | 位置（原始代码） |
|---|---|---|
| 1 权限/能力 | `EyeTrackingLifecycle.EvaluateCapability / ProceedToPermission`；平台委托 `OVRPlugin.eyeTrackingSupported`、PC/Link 路径 `PermissionApplicable=false` | `EyeTrackingLifecycle.cs` ≈ 316–360；`EyeTrackingLifecycleController.cs` `BuildPlatform` |
| 2 提供者初始化 | `OVREyeGaze.OnEnable()` → `StartEyeTracking()` → `OVRPlugin.StartEyeTracking()`，失败则 `enabled=false` | SDK `Scripts/Movement/OVREyeGaze.cs:108-141` |
| 3 启动请求 | `EyeTrackingLifecycle.DoStart`：对未启用的组件 `SetComponentEnabled(i,true)`（=触发 SDK 启动），**不直接调用 `StartEyeTracking`** | `EyeTrackingLifecycle.cs` ≈ 395–425 |
| 4 启动结果 | `DoStart`：两个组件都已启用 **且** `OVRPlugin.eyeTrackingEnabled` 为真 → `Running`；否则 `start_failure`（`component_self_disabled:*` 或 `tracking_not_enabled_after_start`）→ `ScheduleRetryOrExhaust` | 同上；`eyeTrackingEnabled`：SDK `OVRPlugin.cs:10705` |
| 5 逐帧更新 | `OVREyeGaze.Update()`（执行顺序 −80）：`GetEyeGazesState`；**`IsValid` 为假则直接返回（不改 Confidence、不改 transform）**；`Confidence ≥ 阈值` 才更新位姿 | SDK `OVREyeGaze.cs:158-190` |
| 6 原点/方向 | `OVREyeGaze` WorldSpace：`world = Camera.main.localToWorld × (头节点⁻¹ × 眼位姿)`；`GazeRecorder.ReadEye` 读取 `eyeGaze.transform.position/forward` 与 `Confidence` | `GazeRecorder.cs:658-715` |
| 7 提供者有效性 | **旧**：`probeKnown = lc.ProviderProbeAvailable && State∈{Running,TrackingUnavailable}`；`Monitor()` 仅在这些状态下调用 `ReadProvider()` | `GazeRecorder.cs:1125`；`EyeTrackingLifecycle.cs` `Monitor` ≈ 470–480 |
| 8 回退 | `EyeGazeOrigin.Resolve`：提供者原点偏离 rig 眼锚 >0.12 m → 用 rig 眼锚（再应用到渲染视角）；**只替换原点，方向不变**；无有效眼 → 头部前向射线 | `GazeRecorder.cs:691-705` |
| 9 序列化 | `left_provider_valid`：`(probeKnown && !injected) ? (lc.LeftProviderValid?"1":"0") : ""`；原始提供者列：`PipelineColumns` 仅当 `raw.left.valid` 时写入 | `GazeRecorder.cs:1295-1296`、`794-803` |

### 3.2 为什么 S3 为 `start_failed`

* **状态本身 Confirmed 记录在每一行**（3096/3096）。
* `DoStart` 的成功条件是：组件已启用 **且** `OVRPlugin.eyeTrackingEnabled`。S3 的每一行都满足这两点：①`ReadEye` 只在 `eyeGaze.isActiveAndEnabled` 时产出样本（S3 全是提供者原点/方向）；②`ReadRawGaze` 在 `!OVRPlugin.eyeTrackingEnabled` 时 **直接返回 false**（`GazeRecorder.cs:734`），而 S3 每行都有原始提供者列 → 每行 `eyeTrackingEnabled` 为真。**[Confirmed]**
* 状态机重试有界：`maxStartAttempts=8`，延迟 1,2,4,8,15,30,30 s（默认值；场景无覆盖，控制器由 `GazeRecorder` 自动创建）→ 约在会话开始后 2 分钟内耗尽（`ScheduleRetryOrExhaust` 置 `_exhausted`），而首个 gaze 行在 757.7 s。**在 `_exhausted` 之后，`TickInner` 的 `StartFailed` 分支不再做任何事**（原 `EyeTrackingLifecycle.cs:283`），即使提供者之后被启用。 **[Confirmed 机制；耗尽时刻由默认选项推算 Strongly supported]**
* 因此属于上述候选解释中的“**状态因实现/生命周期问题被错误地终态化**”。不是：设备不支持、权限被拒（PC/Link 路径无权限）、运行时被禁用、提供者无有效样本。
* 提供者为何在 ~2 分钟内未启用（首次失败原因字符串）：**Not testable**（清单转移日志缺失）。S3 的事件显示：会话前 650 s 为 NPC 预热（600 s），`app_focus=0` 在 652.76 s，Start 面板 719 s——“头显/窗口焦点之前提供者尚未启用”是一个与时间线相符的假设，**Uncertain**，需要头显实测。

### 3.3 空白的提供者有效标志

* 应记录的标志：`left_provider_valid`、`right_provider_valid`（gaze_diagnostics.csv；语义：该眼提供者 `EyeGazeState.IsValid`；blank = 未知，不是 0）；`left/right_valid`（项目接受 = 置信度≥0.5 且提供者有效）；`combined_valid`（左或右接受）。
* **赋值位置**：生命周期探针 `ReadProviderMask()`（`EyeTrackingLifecycleController.cs` `OVRPlugin.GetEyeGazesState`）→ `LeftProviderValid`；这些值只在 `Monitor()`（Running/TrackingUnavailable）中更新。
* **空白的含义**：既不是 CSV 写入问题，也不是 API 不支持，也不表示无效；而是“探针不可用，所以未知”。**检查顺序**：有效性在 `ReadEye` 读取注视值之后按当时的探针状态判定；探针不可用时不检查提供者。**[Confirmed]**
* 后果（旧代码）：`GazeConfidence.IsValid` 在“置信度可用”时只比较阈值；`AvailableAfterProvider` 在 `providerKnown=false` 时放行；于是 **提供者未知时“有效”仅取决于一个可能过期的置信度**。S3 实际并未过期（见 §3.4、§8），但这个逻辑本身不能证明有效。

### 3.4 恒定置信度

* `OVREyeGaze.Confidence`：`public float Confidence { get; private set; }`（默认 0，`OVREyeGaze.cs:48`），**唯一赋值点**是 `Update()` 中 `Confidence = eyeGaze.Confidence`（行 168，来自 `OVRPlugin.GetEyeGazesState`，原生 `XR_FB_eye_tracking_social gazeConfidence`）。项目内无任何 0.998 常量（全文检索无）；`GazeConfidence.cs` 文档说明不变换该值。→ **0.998 是提供者值，不是默认/回退/序列化默认。 [Confirmed]**
* S3：3096 行 `combined_confidence` 只有 1 个取值（0.998）；同时提供者时间戳 3095/3095 次前进 → 提供者在每个样本上都报告同一个数。它在此运行时 **没有质量区分度**（项目已有 `ConfidenceDiagnostics`“constant”判定；S3 清单缺失无法读取）。**不能把它当作有效性证据，也不能当作质量协变量。[Confirmed 来源；信息量 Not testable]**
* 修复后对此的行为：提供者判无效的眼，置信度列留空（原设计）；提供者未知的样本，置信度不再写为测量值。

### 3.5 eye-anchor 回退

* 触发条件：`EyeGazeOrigin.Resolve`——提供者原点距 rig 眼锚（渲染视角下的 view anchor）> `maxProviderOriginDeviationMeters`（0.12 m）或非有限 → 用眼锚（`origin_fallback_reason = provider_exceeds_deviation_gate`）。**S3：57 行，全部为该原因。**
* **只替换原点，不改方向**（S3 这些行的方向与提供者方向偏差同样 ≤0.03°）；**世界坐标**（眼锚经渲染相机重新应用）。
* 可区分：`gaze_origin_source = eye_anchor_view`（gaze.csv）+ `origin_fallback_reason`（diagnostics）。
* 能否在 `start_failed` 时发生：能（回退只依赖原点距离与眼锚，与生命周期状态无关）；S3 即如此。
* 会不会让无效数据显得有效：**不会改变有效性**（有效性不由原点决定），但回退行的 `combined_valid=1` 仍然成立；使用者必须读 `gaze_origin_source`。回退位置 **从不是提供者测量值**，已在表格中单独分类（C4）。

---

## 4. 常量置信度的解释（汇总）

见 §3.4。Confirmed：来源=提供者；Uncertain：是否随场景变化的质量信息；建议：仅用有效性标志与溯源列。

---

## 5. 回退分析（汇总）

见 §3.5；57 行（1.8 %）；位置见 `s3_eye_sample_provenance.csv`（`category = C4_*`）。

---

## 6. 根因（录制代码）

1. **`EyeTrackingLifecycle.TickInner`，`StartFailed` 分支**：`_exhausted` 之后无任何观察路径（原 283 行附近）——状态被永久固定，即使 `OVRPlugin.eyeTrackingEnabled` 之后为真。
2. **`GazeRecorder` 采样循环，`probeKnown`（原 1125 行）与列写入（原 1295–1296 行）**：逐行提供者有效性只来自生命周期探针；而同一行内 `PipelineColumns` 已经读取了提供者 `IsValid`（原始列），却没有用于有效性与 `*_provider_valid` 列。
3. **`GazeRecorder.ReadEye` 与 `GazeConfidence`**：提供者未知时，有效性回落到可能过期的置信度（见 §3.3）。

---

## 7. 已做的最小代码修改（详见 `code_change_report.md`）

| 文件 | 修改 |
|---|---|
| `Capture/EyeTrackingLifecycle.cs` | 新选项 `exhaustedRecheckInterval`（默认 2 s，0=关闭）；`StartFailed` 且已耗尽时每 2 s 被动检查“组件均启用且 `TrackingEnabled`”→ `Running`，转移名 `late_start_success`（原失败转移仍在日志中，失败计数不变，不发起新的 SDK 启动请求）；`EyeTrackingManifest.rows_provider_unverified` 新字段 |
| `GazeRecorder.cs` | 每行一次原始提供者读取（`_rowRaw`/`_rowRawOk`，复用到 `PipelineColumns`，仍是每行一次 OVRPlugin 读取）；`probeKnown = 同帧原始读取 ∥ 生命周期探针`；两眼提供者有效性优先取同帧原始读取；真实 OVREyeGaze 样本在提供者无法确认时 **不作为眼样本**（`providerUnverified`，置信度不写出，`origin_fallback_reason = provider_validity_unverified`）；`RowsProviderUnverified` 计数 |
| `Capture/GazeConfidence.cs` | 新纯函数 `ProviderEstablished` |
| `Capture/DataCaptureManager.cs` | 1 行：把新计数写入清单 `eye_tracking.rows_provider_unverified` |
| `Assets/Tests/GazeRecorder/EyeValidityRecordingTests.cs`(+.meta) | 新测试 |

* **CSV 列不变**（61 / 85 列保持；`left/right_provider_valid` 现在在更多行有值，blank 仍表示未知）；清单增加 1 个可加字段（向后兼容）。`eye_tracking_state` 仍是生命周期的真实状态；晚启动后显示 `running`，失败历史保留在 `transitions`。
* 未改：置信度的任何值（不伪造）、回退规则、有效性阈值、场景/预制体、实验逻辑。
* 每个被改文件在 `code_backup/` 有修改前备份（已与 HEAD 逐字节核对一致）。

---

## 8. 现有 S3 数据的有效性分类（`s3_eye_sample_provenance.csv`，每行带 `gaze_csv_data_row`、`t_abs`、`frame_index`）

| 类别 | 行数 | 依据 |
|---|---:|---|
| **C1 提供者派生且有效（由原始读取证实）** | **3 038** | 两眼原始列非空（⇒ 提供者 `IsValid`）；提供者时间戳前进；录制方向 = 当前提供者样本（偏差 <0.1°）；原点为提供者原点 |
| C2 提供者派生但有效性未证实 | 0 | |
| C3 无效/不可用 | 0 | 无任何行缺原始提供者证据 |
| **C4 回退派生（原点为 eye-anchor；方向为提供者）** | **57** | `gaze_origin_source=eye_anchor_view` |
| C5 溯源字段缺失 | 1 | 第 1 行（`provider_time_changed`、`anchors_updated_frame` 空） |

* 另有 **168 行** 提供者原点与“渲染相机×头部空间眼”重组结果相差 >3 cm（最大 0.206 m）：方向仍一致，**原点属混合帧**（`origin_mixed_frame_gt3cm`），与每行位移相关（水平 r=0.40，垂直 r=0.32；偏差行的行间位移中位 0.46 m 对 0.39 m）。原因（OVREyeGaze −80 先写原点，之后的移动/地面对齐脚本又移动了相机）为 **Strongly supported，未证明**。
* **可信 / 不可信（现有数据）**：可信——`t_abs`、`frame_index`、提供者时间、世界坐标注视方向（C1+C4）、HMD 位姿、头部相对定中点高度；**需标记**——`eye_tracking_state`（过期状态）、`gaze_origin`（C4 与 168 行混合帧）、`ground_y`/地面相对高度；**不可用**——`left/right_provider_valid`（空白）、`*_confidence`（常量，无信息）、任何把原点当“测得眼位置”的用法。
* 原始 S3 文件未被修改，也未回填任何提供者有效性值；上表只是读取已记录列后的外部分类。

---

## 9. HMD 坐标系重建

| 量 | 含义 | 坐标系 | 证据 |
|---|---|---|---|
| `hmd_world_*`、`hmd_world_rot_*` | `Camera.main`（XRI “Main Camera”，`XR Origin/Camera Offset` 之下，TrackedPoseDriver） | Unity 世界 | `ViewFrame.cs` 头注；S3：`hmd_world_y − head_pos_y = 1.36140 m`（SD 0.7 mm） |
| `head_pos_*`（diag） | OVRCameraRig CenterEyeAnchor（无 Camera Offset） | Unity 世界 | 同上；水平偏差 ≤1.7 cm；旋转与 `hmd_world_rot` 相差中位 0.14°（最大 8° 为帧内先后） |
| `left/right_raw_tracking_pos_y` | OVRPlugin 眼位 Y，**eye-level tracking 空间**：头部相对重新定中点的高度 | OVRPlugin tracking 空间 | 代码 `PipelineColumns` 注释/ `EyePipeline.cs` |
| `ground_y` | 渲染相机正下方的 Ground 层表面 | 世界 Y | ViewFrame |
| Camera Offset | `XROrigin.CameraYOffset = 1.36144`（Device 模式下 XRI 的默认站立眼高常量） | XR Origin 局部 | `ViewFrame.cs`；**S3 清单缺失，场景值未复核，Uncertain** |
| 重新定中点 | 原点：OVRPlugin eye-level（头显重新定中时的头位置）| – | **事件未被记录（Not testable）** |

**关系的独立验证（S3）：** `hmd_world_y = 底座 + 1.36144 + raw_y`；以 `底座估计 = head_pos_y − raw_y`，`底座估计 − ground_y` 中位 **−1.1 mm**，P5–P95 −2.1…+2.0 cm（SD 1.35 cm）。→ HMD 世界位置由 XR Origin 底座、固定的相机偏移与 OVRPlugin 头部局部高度线性组成，**没有二次变换/重复偏移**。**[Confirmed]**

---

## 10. GazeOrigin 坐标系重建

* 记录的原点 = `OVREyeGaze`(WorldSpace)：`Camera.main.localToWorld × (头节点⁻¹ × 眼位姿)`；其中 **提供者的头部空间眼位置是名义常量** `left (−0.0325,0,0)`、`right (+0.0325,0,0)`（S3：x SD 4×10⁻⁵ m，y,z ≈ 0）。→ 原点 = HMD 位姿 + 固定的 ±0.0325 m 侧向偏移，**不随眼睛运动，没有测得的瞳孔/眼球位置**。**[Confirmed]**
* 头部坐标系中（用 `inverse(hmd_world_rot)` 旋转；此时两者同为世界坐标，可比较）：左眼偏移 侧向 −0.03247 m、右眼 +0.03252 m（相干行）；垂直 SD 0.7 cm、前后 SD 0.24 cm（最大 ±2.9 cm / ±1.2 cm）；组合原点偏移中位 0。符号/轴正确（Unity 头部坐标 +x = 右，左眼为负）。
* 世界轴偏移：x、z 中位 0，SD 0.07 / 0.03 mm（组合原点相对 HMD 几乎精确重合）；y SD 1.8 cm，来自混合帧行。
* 不是“HMD 位置变了而原点停在无关位置”（两者同时随站起 +0.47 m），也没有双重变换。

## 11. GazeDir 坐标系重建

* 世界坐标（`OVREyeGaze` 把头部空间旋转与渲染相机旋转相乘）；单位长度 `||d|−1|` ≤7×10⁻⁵；无零向量/NaN；不是 HMD 前向的拷贝（与 HMD 前向的夹角中位 21.4°，范围 1–64°）；相邻行变化中位 6.3°（对应 0.3 s 间隔）。
* **用两条独立路径复现**：①录制方向旋入头部坐标系（仰角中位 −16.73°，方位 +8.10°）；②提供者自己的头部空间方向（仰角 −16.68°，方位 +9.72° — 仅左眼，组合为双眼平均）。仰角差中位 −0.06°，最大 0.8° → 变换正确。
* “注视比头部前向低 17–19°”：这是 **提供者的眼在头内方向**（不是回退默认——回退射线是头部前向，会得到 0°；不是变换错误）。HMD 俯仰为向上 19.5°，注视世界仰角≈+1.8°。它是否合理 **Uncertain**。

## 12. 候选坐↔站转换期间的 HMD / 注视行为（`s3_posture_transition_diagnostics.csv`）

时间索引的垂直变化（`raw_y`，相对定中点；**不标注为已确认的坐/站**）：

| 时刻 (t_abs) | 变化 |
|---|---|
| 1141.75 → 1143.30 | −0.325 → −0.107 m（≈1.5 s 内 +0.22 m；到 1143.65 s 为 +0.025 m，之后 ≈ +0.09 m） |
| 1143.3 – 1157.5 | 平台 +0.094 m（14.2 s） |
| 1155.64 → 1157.51 | +0.090 → −0.206 m；1158.3 s 达 −0.325 m（≈−0.42 m / ~2 s） |
| 1713.2 → 1715.0 | −0.319 → +0.057 m；1715.4 s 起 +0.10～+0.13 m 至记录结束（1717.36 s） |

| 段 | 眼位置 Y（m，相对定中点） | HMD 离地（m） | 眼原点–HMD（头部系，侧/垂/前，m） | 注视仰角（头部系） | 注视–头前向角 | 提供者证据 | 回退 | 混合帧 |
|---|---|---|---|---|---|---|---|---|
| 转换 1 前 30 s | −0.381 | 0.986 | 0 / 0.0003 / 0.0001 | −16.2° | 19.4° | 100 % | 0 % | 3.0 % |
| 转换 1 期间 | +0.094 | 1.459 | 0 / 0.0003 / 0.0001 | −16.0° | 19.8° | 100 % | 0 % | 0 % |
| 转换 1 后 30 s | −0.365 | 0.984 | 0 / 0.0095 / 0.0033 | −15.1° | 21.2° | 100 % | 0 % | 12.5 % |
| 转换 2 前 30 s | −0.378 | 0.984 | 0 / 0 / 0 | −12.4° | 17.9° | 100 % | 0 % | 13.8 % |
| 转换 2 期间（9 行） | +0.108 | 1.474 | 0 / 0 / 0 | −9.4° | 15.6° | 100 % | 0 % | 0 % |

结论：HMD 垂直位置 **在期望方向上变化**（升高 ~0.45 m，再降回），平台期内稳定（SD 6 cm），无意外的垂直跳变或漂移；原始（OVRPlugin 局部）与世界（应用层）同时变化，**不是只在应用变换后才出现**；眼原点相对 HMD 的偏移、注视方向的头部系仰角、提供者有效证据在转换前/中/后均不变；回退与混合帧行在移动较多的时段更多，但并未产生由录制引入的人为不连续。**没有发现未记录的重新定中、HMD 与原点各自被变换两次、方向被误解释的证据。** 转换 2 仅 9 行（3.2 Hz），统计功效有限。

**0.38 m 基线的解释：** 它是 OVRPlugin eye-level 局部高度 −0.378 m（头部低于该会话重新定中点），再加固定相机偏移 1.36144 得到渲染眼高 0.98 m。S3 的两次升高都回到 ≈ +0.1 m（≈定中点高度），升降幅度 ≈0.45 m，与“定中点是在更高姿势下设定的”**一致（Strongly supported）**；不是“固定场景基线错误”（它是 XRI 的标准偏移）、不是应用层变换（世界与局部残差 ≈0）、也没有证据显示被记录代码改变。重新定中事件未记录 → 无法证明（Not testable）。

## 13. 采样与同步（`s3_gaze_sampling_diagnostics.csv`）

* 3 096 行 / 959.6 s = **3.225 Hz**；间隔中位 0.302 s（P5–P95 0.245–0.393），最大 0.494 s；`frame_index` 每行 **+1（min=median=max=1）**，隐含 Unity 帧率 3.225 fps；无重复/倒退/缺失时间戳。
* 瓶颈：**Unity 帧率**（Confirmed 为帧限制：若是 3.3 Hz 的录制间隔，帧号步长会 >1；`SampleScheduler` 名义 0.05 s 在 0.3 s 的帧内每帧最多写一行）；不是提供者限制（提供者时间戳每行前进，间隔 0.3055 s，与行间隔相关 0.90）；不是定时器限制。帧率低的**原因**：S3 记录了 3521 个活跃 NPC（Start 时）与 5247 个生命周期，与“NPC 负载”相符，但 **未证明**（Uncertain）。无时间戳插值或重用。
* **同一帧**：`anchors_updated_frame == frame_index` 3095/3095；每行的相机、OVR 锚点、提供者样本都来自同一个 Unity 帧，但**帧内顺序不同**（OVREyeGaze −80，OVRCameraRig −50，项目脚本/移动在后），因此当相机在帧内移动时，原点相对 HMD 会出现几厘米～20 cm 的平移偏差（168 行），方向不受影响。这是“异步采样”的一种形式，而非时间戳问题。

## 14. 现有 S3 数据可信/不可信小结

| 变量 | 判定 |
|---|---|
| 世界坐标注视方向（`*_dir_*`） | **保留**（有提供者逐行证据；变换已验证）；用于空间注视分析 |
| `gaze_origin`（作为位置） | **标记**：HMD 相对代理，不是眼位置；回退 57 行、混合帧 168 行建议排除/标记 |
| `hmd_world_*` 位置/旋转、头部相对定中点高度 | **保留** |
| `ground_y`、地面相对高度 | 保留并标记（此审计不含失控问题） |
| `combined_valid`、`gaze_ray_source` | 保留（现已有证据支持） |
| `eye_tracking_state` | **过期（全程 start_failed）**，不要用 |
| `left/right_provider_valid` | 空白，勿用；用原始列是否存在（见 provenance 表） |
| `*_confidence` | 常量，**勿用** |
| 时间分辨率 3.2 Hz | 不适合扫视/注视动力学（本任务不做）；适合空间/寻路级分析 |

## 15. 剩余局限与必须的 Quest 验证

* **没有头显验证。** 单元测试使用脚本化假对象；真实 OVRPlugin 在 Link/Quest 上的“晚启用”时序、`ReadRawGaze`/`eyeTrackingEnabled` 的行为、以及修复后 `eye_tracking_state` 的实际转移，必须在头显上确认。
* S3 清单/轨迹缺失：转移日志、首次失败原因、帧计时、Camera Offset 场景值、tracking origin 类型均 Not testable。
* 眼在头内 −17° 的物理合理性：需要定点注视校准任务。
* “提供者未知 ⇒ 不接受”规则：在 Quest 独立构建上，若 `OVRPlugin.GetEyeGazesState` 在某些帧失败，会产生 `provider_validity_unverified` 行（预期，但需确认频率）。
* 若头显运行时确实无法启用眼动（设备不支持/设置关闭），这是运行时配置问题，代码不会也不应掩盖：状态会保持 `start_failed` / 原始读取不可用，有效性保持未知并被拒绝。

---

## 16. 必答问题

1. **S3 为何 `start_failed`？** 生命周期在启动期用完 8 次有界重试后固定为终态，之后不再观察；提供者其实在之后被启用并持续输出。[Confirmed 机制；首次失败原因 Not testable]
2. **S3 有真正由提供者验证的数据吗？** 有：3038 行有逐行提供者证据（原始 `IsValid` 列、新时间戳、方向=当前提供者样本）。[Confirmed 列含义；类别 Strongly supported]
3. **0.998 是提供者信息还是应用生成？** 提供者值（项目无常量，唯一赋值来自 `eyeGaze.Confidence`），但常量、无质量信息。[Confirmed 来源]
4. **哪些行是提供者/回退/未验证？** 提供者且有效 3038；回退原点 57（方向为提供者）；缺溯源 1；无“未验证/无效”行。[见 provenance 表]
5. **HMD 位置坐标系正确吗？** 正确：世界 = 底座 + 1.36144 + OVRPlugin 局部（残差 −1 mm）。[Confirmed]
6. **HMD 垂直运动与已知坐/站阶段一致吗？** 方向、幅度、稳定性都一致（两次 +0.45 m 升降）；具体时刻请对照你的流程（见 §12 时间索引）。[Strongly supported]
7. **0.38 m 基线有合法解释吗？** 有：eye-level 原点 + XRI 固定相机偏移；定中点高于该会话的头位。不是基线错误。[Strongly supported；重新定中事件 Not testable]
8. **`GazeOrigin` 相对 HMD 位置正确吗？** 正确：左/右 ∓0.0325 m 侧向，垂直/前后≈0（相干行 SD 7 / 2.4 mm）；168 行混合帧偏差。[Confirmed]
9. **`GazeOrigin` 是测得的眼位置吗？** 不是；是 HMD 相对的名义代理（提供者头部空间眼位置恒定）。[Confirmed]
10. **`GazeDir` 变换到正确坐标系了吗？** 是（世界坐标；两条独立路径一致，差 ≤0.8°）。[Confirmed]
11. **−17～−19° 是合理的还是实现问题？** 不是变换/回退/默认值问题；是提供者报告的眼在头内方向。物理合理性 Uncertain。
12. **S3 注视方向能用于空间注视分析吗？** 可以（谨慎：3.2 Hz，不做动力学；标记 C4/混合帧不影响方向）。
13. **保留/标记/排除：** 见 §14。
14. **下一位参与者之前：** (a) 在头显上验证修复（晚启用、`provider_valid` 列、`rows_provider_unverified`）；(b) 记录重新定中与参与者姿势/身高；(c) 解决 ~3 fps（NPC 规模/编辑器负载）；(d) 保证归档完整；(e) 在文档中明确 `GazeOrigin` 为代理、置信度为常量。

## 17. 产出文件

`S3_eye_tracking_coordinate_audit_zh.md`；`code_change_report.md`；`s3_eye_validity_summary.csv`、`s3_eye_sample_provenance.csv`（3 096 行，含原始行号与 `t_abs`）、`s3_hmd_gaze_coordinate_summary.csv`、`s3_posture_transition_diagnostics.csv`、`s3_gaze_sampling_diagnostics.csv`；`s3_results.json`；`plots/01–08`；`scripts/s3_audit.py`、`s3_plots.py`；`code_backup/`（修改前的 4 个文件）。
