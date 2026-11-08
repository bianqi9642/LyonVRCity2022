# 代码修改与测试报告（S3 眼动有效性修复）

范围：仅录制侧的最小修复。**未提交。未在头显上验证。** 未改场景、预制体、材质、NavMesh、参与者设置、置信度数值、回退规则、有效性阈值或实验逻辑。

## 1. 修改的文件（修改前备份：`code_backup/`，已与 HEAD 逐字节核对一致）

| 文件 | 函数/位置 | 修改 | 原因 |
|---|---|---|---|
| `Assets/Scripts/Capture/EyeTrackingLifecycle.cs` | `EyeTrackingLifecycleOptions`（新字段 `exhaustedRecheckInterval`，默认 2.0 s，0 = 关闭）；`TickInner` 的 `StartFailed` 分支；新方法 `TryLateStartObservation`；`EyeTrackingManifest.rows_provider_unverified` | 重试耗尽后不再固定为终态：每 2 s 被动检查“所有组件已启用 **且** `TrackingEnabled`”，成立则记录转移 `late_start_success` → `Running`。不发起新的 SDK 启动请求；`StartupFailures`、原失败转移日志保留；组件仍被禁用则不算成功 | 根因 1：耗尽重试后无任何观察路径，`start_failed` 成为过期终态 |
| `Assets/Scripts/GazeRecorder.cs` | `SamplingLoop`（`_rowRaw/_rowRawOk`、`rawKnown/lifecycleKnown/probeKnown`、两眼 `*ProviderValid` 来源、`left/right_provider_valid` 列）；`ReadEye`（`providerUnverified`、`RowsProviderUnverified`、置信度可用性）；`PipelineColumns`（复用同帧原始读取；`origin_fallback_reason` 新值 `provider_validity_unverified`） | 每行只做一次原始提供者读取，同时用于有效性与原始列；提供者有效性优先取同帧原始读取，其次生命周期探针，两者都没有则为“未知”（列留空，不写 0/1）；`requireProviderValidity` 为真且提供者未知时，真实 OVREyeGaze 样本**不作为眼样本**，置信度不写出 | 根因 2、3：逐行有效性只依赖生命周期探针；探针不可用时用可能过期的置信度判有效 |
| `Assets/Scripts/Capture/GazeConfidence.cs` | 新纯函数 `ProviderEstablished(requireProviderValidity, providerKnown)` | 把“提供者是否已确认”规则单独化以便测试 | 可测试性 |
| `Assets/Scripts/Capture/DataCaptureManager.cs` | 清单写入处 1 行 | `et.rows_provider_unverified = g.RowsProviderUnverified;` | 让清单报告被拒绝的未确认行数 |
| `Assets/Tests/GazeRecorder/EyeValidityRecordingTests.cs`(+`.meta`) | 新文件 | 9 个测试（见 §4） | 覆盖启动成功/失败/晚启动、提供者不可用、有效性变化、同帧提供者来源、规则 |

`git diff --stat`：4 个文件，+54 / −11。

## 2. 是否改变 CSV 架构？

* `gaze.csv`（61 列）、`gaze_diagnostics.csv`（85 列）：**列名、顺序、数量不变。**
* 取值语义的变化（均为“让以前的空白变成有证据的值”，不是新列）：
  * `left/right_provider_valid`：探针不可用时，只要同帧原始读取成功，现在会写 0/1；仍无法确认时保持空白（空白 = 未知，不是 0）。
  * `origin_fallback_reason`：新增取值 `provider_validity_unverified`（旧取值 `no_valid_eye` 等不变）。
  * `eye_tracking_state`：仍是生命周期真实状态；晚启动后会显示 `running`，转移日志新增事件名 `late_start_success`。
* 清单（`session_manifest.json`）：新增可选字段 `eye_tracking.rows_provider_unverified`；新增转移名 `late_start_success`；新增选项 `exhaustedRecheckInterval`。
* 向后兼容：旧读取器忽略新增字段即可；旧录制不受影响（S3 原件未改动）。分析脚本若按 `left_provider_valid` 空白判断“探针不可用”，在新录制中空白会少得多（含义不变）。
* 与 CLAUDE.md 规则对照：未改既有列顺序/名称；仍是 `OnTrialStart/OnTrialEnd` 驱动；时钟与文件名约定不变；打开/写入/关闭每行与暂停/退出钩子不变；仍通过 `GazeRecorder` 唯一入口，两个 `OVREyeGaze` 都保留；有效性仍由提供者标志 + `Confidence` 阈值得出，未发明新标志。

## 3. 行为前后对比

| 情况 | 修改前 | 修改后 |
|---|---|---|
| 启动期重试耗尽，之后提供者被启用 | 永远 `start_failed`；`provider_valid` 空白；有效性只靠（可能过期的）置信度 | 约 ≤2 s 内转 `running`（`late_start_success`），失败历史保留；有效性来自同帧提供者读取 |
| 提供者确实不可用 | `start_failed`，样本有效性靠置信度 | 状态保持 `start_failed`；原始读取失败 → 提供者未知 → 样本**不接受**，`origin_fallback_reason=provider_validity_unverified`，置信度不写出 |
| 提供者可读但生命周期探针不可用 | `provider_valid` 空白 | 写同帧 0/1 |
| 提供者对某只眼判无效 | 不变 | 不变（该眼无效，置信度留空） |
| 回退（eye-anchor） | 不变 | 不变 |
| 置信度数值 | 提供者值原样 | 不变（不伪造、不重写） |

## 4. 测试

**已执行（Unity 2022.3.53f1 PlayMode，batchmode，在 `tproj` 临时副本中，不在仓库内；`-nographics`，Editor 离线，无头显）：**

* `EyeValidityRecordingTests`：**9/9 通过** —— `InitSuccess_IsRunning_NoLateEvent`；`ProviderNotEnabled_RetriesAreBounded_ThenStaysStartFailed_AndTheFailureIsVisible`；`ProviderEnabledLate_AfterRetriesExhausted_IsReportedRunning_AndFailuresRemainRecorded`；`ComponentStillDisabled_IsNotALateStart_EvenIfTheProviderIsEnabled`；`LateCheckCanBeSwitchedOff`；`ProviderValidityChangesDuringRunning_IsFollowedPerEye_AndLossIsReported`；`ProviderUnavailable_QueryFailed_IsNotAValidFlag`；`GazeConfidence_ProviderEstablished_Rule`；`EyeValidity_ProviderFlagsComeFromTheSameRowRawRead_WhenTheLifecycleProbeIsUnavailable_AndFollowAChangeDuringTheSession`（通过 `testRawGaze` 接缝驱动真实 `GazeRecorder` 采样循环）。
* 变异检验：在 `GazeRecorder` 中把 `rawKnown` 强制为 false，recorder 测试失败；还原后通过（已核对文件与预期副本一致）。
* 覆盖对照：初始化成功 / 失败 / 晚启动；提供者不可用；有效/无效样本；原点/方向不可用（由既有 `EyePipeline`/`EyeGazeOrigin` 测试覆盖，本次未新增）；回退（既有测试）；置信度不可用（`ProviderEstablished` 与既有 `GazeConfidence` 测试）；有效性变化。
* 全套 PlayMode（修改后，389 个）：**379 通过 / 1 失败 / 9 跳过**。失败：`NpcLifecycleTests.Part2Flow_ParticipantControl_Task1Free_ContinuePanelLocked_ReleasedByTheContinue_ExactlyOnce_TeleportUnchanged`（`Part2ContinueLockFlowTests.cs:65`，断言“Continue 后参与者控制已释放”为 False）。
  * **与本次修改无关**：把我修改的 4 个源文件换回 HEAD 备份（并移除新测试）后单独运行，**同样失败**。该测试在 HEAD 之前的一次全套运行（363 个，0 失败）中通过；之后用户提交了 `1c5662e1 Fixation after pilot 1`。因此它是该提交（或其与旧测试的不一致）带来的**既有失败**；本任务**未修改也未调查**其原因，需要另行处理。

**已创建但未执行：** 无。
**仅 Editor 验证：** 以上全部。
**只能在 Quest / Quest Pro 上验证（仍需要）：**
1. 真实 OVRPlugin 下“晚启用”时序：`late_start_success` 是否出现、`eye_tracking_state` 是否转为 `running`；
2. 真实 `OVRPlugin.GetEyeGazesState`：`left/right_provider_valid` 是否按眼逐行变化，`rows_provider_unverified` 的频率；
3. 眼动权限提示（Android）、`oculus.software.eye_tracking`/`com.oculus.permission.EYE_TRACKING` 是否进入构建清单；
4. 该硬件上 `gazeConfidence` 是否仍恒定（0.998 是否为运行时特性）。

## 5. 未做的事（有意）

* 不回填或重写 S3 原始文件；`s3_eye_sample_provenance.csv` 只是读取已记录列后的外部分类。
* 不把未知有效性转成 true；不把回退当作测量；不改置信度阈值；不改场景/预制体。
* 未处理上述既有失败测试，未提交。
