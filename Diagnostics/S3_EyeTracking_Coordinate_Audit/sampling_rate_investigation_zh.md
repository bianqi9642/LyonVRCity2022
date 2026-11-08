# S3 眼动采样频率（≈3.2 Hz）调查与提频可行性评估（只读）

对象：**仅** 会话 `20261106T145652Z_0980aa930c32`（S3，subject_id = 4）。
约束：只读调查；未修改任何脚本、场景、预制体、设置或参与者数据；原始 CSV 未被重写；未提交；没有做任何 Quest Pro 运行时测试。本文件是唯一写入物。
证据标签：**Confirmed**（数据或源码直接证明）· **Strongly supported**（多项独立证据支持）· **Uncertain**（证据不足）· **Not testable**（缺少数据/设备/运行时）。
代码行号为当前工作树；本次工作树中未提交的眼动有效性修改**没有改动采样循环的调度、读取频率或写入方式**，S3 由 HEAD 代码录制，二者采样逻辑相同。

---

## 1. 执行摘要

1. **S3 的 3.2 Hz 不是眼动硬件、提供者或 CSV 写入的速率，而是 Unity 帧率。** `frame_index`（= `Time.frameCount`）在全部 3095 个间隔里**恰好 +1**；记录器每帧最多写一行；Unity 帧率因此就是记录频率（3.225 fps，测得）。 **[Confirmed]**
2. **帧率低的主要关联因素是场上活动 NPC 的数量，呈现近线性的剂量—反应关系。** 利用同一会话 `npc_state.csv`（带 `frame_index` 与 `t_abs`）可以重建热身阶段的帧率：活动 NPC 约 140 个时帧时间 10 ms（≈100 fps），3150 个时 227 ms（4.4 fps）；试次阶段 NPC 由 3 600 增至 5 000，帧时间 258 → ≈310–340 ms。对热身与试次两段共 37 个窗口做线性拟合：**帧时间 ≈ 7.2 ms + 0.066 ms × NPC 数，R² = 0.97**（每个 NPC 约 66 µs/帧）。 **[Confirmed 相关与形状；机制是 NPC 仿真还是 NPC 数据采集，S3 内无法分离（Not testable）]**
3. **gaze 记录器自身不是显著瓶颈：** 它每帧只做 O(1) 工作（读 3 次 OVRPlugin、2 次射线、2 次小文件追加），而帧时间随 NPC 数线性增长且试次阶段（有 gaze 记录）与热身阶段（无 gaze 记录）落在同一条直线上。 **[Strongly supported；精确的毫秒占比 Not testable（S3 清单缺失，没有 `frame_timing` 与写入耗时统计）]**
4. **提供者时间戳不是传感器采样时间：** `provider_time_s` 的相邻差**全部**是 1/72 s 的整数倍（3095 个差值中 100 % 在 0.05 个刻度内，残差中位 0.0008 刻度；差值取 15…37 个刻度）。这是 72 Hz 显示/合成器时间栅格，不是眼动传感器的原生时间。因此“时间戳在前进”**不能**证明每行是一次独立的传感器测量。 **[Confirmed 栅格；解释为预测显示时间 Strongly supported]**
5. **S3 是在 Windows Editor + OpenXR 路径（经头显链路）录制的，不是 Quest 独立运行：** `audio/audio_metadata.json` 记录 `runtime_path = windows_editor_openxr`。因此这里的帧率瓶颈属于 PC Editor 的 CPU/渲染，不代表 Quest 独立构建的性能。 **[Confirmed 路径；Quest 独立性能 Not testable]**
6. **安装的 SDK（Meta XR Core 201.0.0）只提供“最新状态查询”：** `OVRPlugin.GetEyeGazesState(Step, frameIndex, ref state)` 返回两只眼的**一个**状态和一个 `Time`，没有样本缓冲、序列号、回调、事件或原生采样频率接口。应用错过的提供者更新不可恢复。 **[Confirmed（安装的 C# 包装层）；原生二进制内部 Not testable]**
7. **结论：主要应通过降低 Unity 帧时间（NPC 工作量）来提高有效记录频率；改写 gaze 采集/写盘逻辑不会让 S3 条件下的频率明显提高。** 在没有 NPC 负载下降的前提下，30 Hz、72 Hz 都**没有证据支持**。具体可行性见 §8。

---

## 2. S3 的实际采样频率与时间戳分析（调查 A）

数据：原始 `gaze.csv`（3096 行）与 `gaze_diagnostics.csv`（3096 行，按行一一对应）。由 `t_abs`（= `Time.realtimeSinceStartupAsDouble`，进程内秒数）首尾计算，而不是用行数除以会话时长：

| 项 | 值 |
|---|---|
| 行数 | 3096 |
| 首 / 末 `t_abs` | 757.7157 s / 1717.3606 s |
| 时长 | 959.645 s |
| 频率 = (行数−1) / (末−首) | **3.2252 Hz** |
| 相邻间隔 均值 / 中位 / SD | 0.3101 / 0.3023 / 0.0441 s |
| 最小 / 最大 | 0.2151 / 0.4937 s（最大/最小 = 2.3） |
| P1 / P5 / P25 / P75 / P95 / P99 | 0.2221 / 0.2455 / 0.2805 / 0.3403 / 0.3933 / 0.4241 s |
| 重复时间戳 / 倒退 | 0 / 0 |
| 间隔 > 1 s、> 0.5 s | 0 / 0 |
| 间隔 > 上围栏 0.430 s（Q3+1.5·IQR） | 22（0.71 %）；> 1.5×中位 0.45 s：7；> 2×中位：0 |
| 间隔 < 0.5×中位 / < 下围栏 0.191 s | 0 / 0 |
| `frame_index` 相邻差 | **全部 = 1（3095/3095）**；`frame_index` 的首末给出的帧率 = 3.2252 fps，与行频率完全相同 |
| 缺失的“时间戳”（有帧但没有行） | 0（每一帧都有一行；没有 `frame_index` 跳变） |
| `provider_time_s` 重复值 | 0（3096 个全不同）；`provider_time_changed`：3095 个 “1”，首行空白 |
| 注视方向与上一行**完全相同**的行 | 0；原始提供者位置完全相同的行 1 个；相邻行方向变化中位 6.3° |
| 名义频率 | `GazeRecorder.sampleRate = 20`（场景，`LyonVRVity35.unity:172823`），即 0.05 s；实际中位间隔为名义的 6.0 倍（达成率 16 %）。 |

**稳定性（每 60 s 一个窗口）：** 率在 2.63–3.88 Hz 之间变化，不是常数，但同一量级：

| 窗口起点（t 自首行） | 0 | 60 | 120 | 180 | 240 | 300 | 360 | 420 | 480 | 540 | 600 | 660 | 720 | 780 | 840 | 900 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 行/秒 | 3.88 | 3.85 | 3.52 | 2.95 | 3.17 | 3.37 | 3.08 | 2.93 | 3.20 | 3.27 | 3.42 | 2.92 | 3.38 | 3.03 | 3.00 | 2.63 |

间隔序列有很强的自相关（lag1 0.84，lag10 0.62，lag50 0.45）：频率慢变，不是随机抖动。它不与 HMD 位移（r = 0.013）、命中类别（TERRAIN 0.303 / BUILDING 0.296 / NPC 0.344 / OTHER 0.337 s，NPC 稍长但各类别均为 0.3 s 量级）或离散事件（teleport、眨眼、面板前后 1.5 s 内的中位间隔 0.290–0.311 s，对比其余 0.302 s）相关。 **[Confirmed]**

---

## 3. 当前眼动采集流程（调查 B）

| 问题 | 结论 | 位置 |
|---|---|---|
| 谁取得提供者数据 | **两条独立路径**：①两个 `OVREyeGaze` 组件在各自的 `Update()` 里调用 `OVRPlugin.GetEyeGazesState(Step.Render, -1, …)`，写到各自 transform（每帧各 1 次）；②`GazeRecorder.ReadRawGaze()` 在记录行内再读 1 次（每行 1 次）。每帧共 3 次查询 | `Library/PackageCache/com.meta.xr.sdk.core@201.0.0/Scripts/Movement/OVREyeGaze.cs:158-172`；`Assets/Scripts/GazeRecorder.cs:736-748` |
| 记录器的生命周期方法 | **协程** `SamplingLoop()`，`StartCoroutine`；`yield return new WaitForSecondsRealtime(...)`；没有 `InvokeRepeating`、后台线程、回调 | `GazeRecorder.cs:460, 547`（启动）、`1108`（循环）、`1125`、`1352`（等待） |
| 是否每个 Unity 帧都采样 | **显式时间间隔 + 帧对齐**：名义间隔 `1/sampleRate`（= 0.05 s）；`SampleScheduler` 用绝对截止时间栅格，最小等待 1 ms，所以在帧时间 > 间隔时每帧恰好写一行，不会突发补写 | `Assets/Scripts/Capture/SamplingScheduling.cs:22-61`（`MinWait` 保证“一帧最多一行”） |
| 节流/过滤/去抖 | 有：①采集窗口门（`dataCapture.GazeCaptureActive`），关闭时不写；②调度器；**没有**按内容的过滤、去抖或去重 | `GazeRecorder.cs:1118-1126`；`SamplingScheduling.cs:42-60` |
| `frame_index` 是什么 | **`Time.frameCount`**（不是自增序号、也不是提供者计数） | `GazeRecorder.cs:1288`（gaze.csv）、`:1309`（diagnostics） |
| 提供者是否每帧都读 | 是，每帧（`OVREyeGaze.Update` 无条件调用；记录行内又读一次）。只有 `OVRPlugin.initialized && eyeTrackingEnabled` 才读 | `OVREyeGaze.cs:158-166`；`GazeRecorder.cs:742` |
| 读到的是新样本还是最新状态 | **最新状态**。API 没有“新样本”标志；`OVREyeGaze` 在 `IsValid` 假或 `Confidence < 阈值` 时直接 `return`，保留上一帧的 transform 与 Confidence | `OVREyeGaze.cs:165-170` |
| 能否区分新样本与重复读取 | **只能间接区分**：记录器写 `provider_time_s` 与 `provider_time_changed`（与上一行比较）。因为时间戳在 1/72 s 栅格上，它能表示“这一帧的预测时间变了”，不等于“传感器有新测量” | `GazeRecorder.cs:790-803`（`PipelineColumns`） |
| 执行顺序 | `TrackedPoseDriver −30000` → `OVRManager −100` → `OVREyeGaze −80` → `OVRCameraRig −50` → 其余 `Update` → **协程恢复（`WaitForSecondsRealtime` 在 `Update` 之后、`LateUpdate` 之前）**。协程读到的提供者状态属于同一帧 | Unity 脚本执行序；S3 `anchors_updated_frame == frame_index` 3095/3095 |
| 每行的主线程工作 | 3 次 OVRPlugin 查询；`ViewFrame` 读（1 次向下射线）；ROI 射线 1 次；面部/眨眼读；**两次**打开-追加-冲刷-关闭的小文件写（`gaze.csv` ≈0.47 KB、`gaze_diagnostics.csv` ≈0.59 KB） | `GazeRecorder.cs:1145-1161, 1219, 1305-1320`；`CaptureCsvWriter.cs:149-190`；`CaptureIO.cs:61-72` |

---

## 4. 四种“频率”的区分（调查 C）与汇总表

| 层 | 当前频率 | 证据 | 是瓶颈？ | 能否提高？ |
|---|---|---|---|---|
| **A 原生传感器采样** | **未知**（Not testable） | 安装的 SDK / 头文件没有采样率、序列号或硬件时间戳（`XrEyeGazeFB` 在头文件里只有成员名 `isValid, gazePose, gazeConfidence`，`openxr_reflection_full.h:4112-4115`）。S3 里没有可用于推断传感器率的信号 | 不能判断（不是 3.2 Hz 的原因：硬件不会因应用帧率而变慢，这一点属于推断） | 应用无法改变 |
| **B 提供者更新/时间栅格** | 提供者时间在 **1/72 s 栅格**上（measured）；潜在“每显示刻度一个时间值”≤ 72 个/秒；应用实际只观察到 3.2 个/秒 | `provider_time_s` 差值 100 % 为 1/72 s 的整数倍；差值 15–37 刻度；与 Unity 帧间隔的相关系数 0.90，差值 SD 20 ms | 否（提供者不限制；但它不是传感器时间，**不可作为“独立测量数”**） | 取决于应用能否更频繁地查询（见 §6） |
| **C Unity 轮询** | **3.225 Hz**（measured） | `frame_index` 每行 +1；`frame_index` 首末帧率 = 行频率 | **是（主要瓶颈）** | 取决于 Unity 帧时间 |
| **D CSV 记录** | **3.225 行/s**（measured），= 轮询频率 | 每帧一行，无跳帧、无重复；写入速率 = 轮询速率；写入异常/耗时统计 **Not testable**（清单缺失） | 没有证据表明是瓶颈（行不丢；写盘耗时无法量化） | 批量写盘可减少 I/O，但 S3 数据不支持它是主要成本 |

要点：
* 我**没有**把“时间戳在前进”当作“每行都是一次新的传感器采样”；也没有把 CSV 行数除以时长当作传感器频率。
* “是否有样本在采集与写盘之间丢失”：在**帧粒度**上没有（`frame_index` 无跳变）；在**提供者更新粒度**上，应用每帧只取一次状态，帧间提供者产生的任何其他样本（若存在）不可恢复。

---

## 5. Unity FPS 是否是瓶颈（调查 D）

### 5.1 已测得的事实
1. **帧率 = 记录频率**（§2）。 **[Confirmed]**
2. **热身阶段帧率重建**：`npc_state.csv`（该文件本身被截断，只能读取到 t = 626 s，约 5.03 M 行，按流式读取，未落盘）每行带 `frame_index`、`t_abs`、行数 = 当时被采样的活动 NPC 数：

| 阶段 | t（s） | 活动 NPC | 帧时间（ms） | 帧率 |
|---|---:|---:|---:|---:|
| 热身 | 110 | 140 | 10.0 | 100 |
| 热身 | 185 | 760 | 46.2 | 21.6 |
| 热身 | 260 | 1 282 | 84.5 | 11.8 |
| 热身 | 360 | 1 834 | 130.6 | 7.7 |
| 热身 | 460 | 2 443 | 178.4 | 5.6 |
| 热身 | 560 | 2 952 | 213.2 | 4.7 |
| 热身 | 610 | 3 153 | 227.3 | 4.4 |
| **试次（gaze 记录开始）** | 757 | 3 624 | 258.4 | 3.9 |
| 试次 | 877 | 4 063 | 284.6 | 3.5 |
| 试次 | 1 057 | 4 597 | 298.7 | 3.3 |
| 试次 | 1 177 | 4 950 | 338.8 | 3.0 |
| 试次（NPC 数恒为 5 000） | 1 237–1 657（8 个窗口） | 5 000 | 293–379（均值 ≈ 324） | 2.6–3.4 |

   （试次阶段的活动 NPC 数由 `npc_lifetimes.csv` 的 `spawn_t_abs` 与 `npc_events.csv` 的 `npc_despawn` 计数；试次期间仍有约 1 700 个 NPC 被生成，约在 1 200 s 达到 5 000。热身阶段 NPC 数由 `npc_state` 每次快照的行数得到，更精确。）
3. **线性拟合**（37 个窗口，热身+试次）：`帧时间 ≈ 7.2 ms + 0.0661 ms × N`，R² = 0.971；N=5 000 时预测 338 ms，实测约 310–340 ms。外推：该机器、该代码下 30 fps 对应 N ≈ 400，72 fps 对应 N ≈ 100（**外推，不是测量**；N=140 时实测 10 ms 与之相符）。 **[Confirmed 相关；其余 Strongly supported]**
4. **恒定 N = 5 000 时仍有 ±30 ms 的帧时间波动，且 NPC 事件速率与窗口平均间隔无相关（r = 0.086）**：这部分波动**不能**由 NPC 数量解释，原因 **Uncertain**。

### 5.2 代码里能确认的“与 N 成正比的每帧工作”
* `DataCaptureManager.SampleNpcs()`（`Assets/Scripts/Capture/DataCaptureManager.cs:1308-1440`，由 `NpcLoop()` 协程 `1285-1303` 驱动）在场景设置 `npcStateSamplingIntervalSeconds = 0.1`、`npcStateDataBudgetMBPerMinute = 0`（不限量）（`LyonVRVity35.unity:182988-182990`）下，每个 `0.1 s` 的 `WaitForSecondsRealtime` 之后运行一次——在帧时间 > 0.1 s 时即**每帧一次**。每次对**每个**活动 NPC：读取 Transform、`NavMeshAgent` 的 `velocity / desiredVelocity / destination / hasPath / pathPending / speed`；`NpcLineOfSight.Query`（对参与者的视线检测，`:1360-1361`）；拼接 ≈ 45 个字符串字段；然后一次性追加整个种群（`:1434`，“一次打开/写/关闭”）。
* 数据量：试次的 `npc_state.csv` 约 318 B/行（从可读部分量得），一次快照 5 000 行 ≈ 1.6 MB；`npc_state.csv` 声明大小 6.88 GB ≈ 2.16 × 10⁷ 行。减去热身的估计行数后，试次的快照数约 3 000 次，与 3 096 个 gaze 行同量级（**估计，Strongly supported**）——即试次期间每一帧都做一次 5 000 NPC 的完整快照与写盘（≈ 5 MB/s）。
* 因此 NPC 工作量里**至少包含两类与 N 成正比的成本**：NPC 仿真（`NavMeshAgent`、动画等）和数据采集（快照+射线+字符串+写盘）。**S3 的数据不能把二者分开**（没有无采集的对照、没有 Profiler 或 `frame_timing`）。不得声称 NPC“仿真”是原因，也不得声称“采集”是原因——只能说“NPC 总负载”。 **[Confirmed 关联；分解 Not testable]**

### 5.3 对列表中其他项的审查
| 检查项 | 结果 |
|---|---|
| `Application.targetFrameRate` | 项目脚本没有设置（仅在清单里读取并记录 `target_frame_rate`，`DataCaptureManager.cs:2118, 2518`）；默认由平台决定 |
| `QualitySettings.vSyncCount` | 脚本不设置；`ProjectSettings/QualitySettings.asset`：Very Low / Low = 0，Medium 及以上 = 1；`m_CurrentQuality = 1`（Low，vSync 0，指定了自定义 URP 资产，其名称我未逐一核对；`Assets/Settings/Project Configuration/` 下两个 URP 配置的渲染缩放均为 1、MSAA 均为 4）。Windows Editor 运行时实际使用的质量等级 S3 未记录（Not testable） |
| 头显刷新率 | 项目脚本**没有**调用 `OVRPlugin.systemDisplayFrequency` 或 `OVRDisplay.displayFrequency`（对 `Assets/Scripts` 下的 C# 搜索无命中；未搜索 Editor 工具与场景序列化的 OVRManager 字段）。S3 的提供者时间栅格显示显示刻度为 72 Hz |
| CPU/GPU 性能级别、渲染分辨率 | 脚本没有设置；渲染缩放 1。S3 没有 CPU/GPU 时间记录（Not testable） |
| NPC / NavMeshAgent / Animator | 5 000 个 NPC（`npc_lifetime_open_at_end` = 5 000；NavMeshAgent 半径 0.3、高度 1.8）；见 §5.1 的剂量—反应 |
| 物理与碰撞 | 视线检测的射线按 NPC 数线性增长（§5.2）；gaze 射线每行 2 次 |
| gaze 射线与坐标转换 | 每行：1 次地面射线 + 1 次 ROI 射线 + 若干 `Transform` 读，O(1) |
| 场景搜索 | `ViewFrameProbe.Resolve` 的 `FindObjectOfType` 被限制为每秒 1 次且仅在引用缺失时；采样路径不做重复搜索 |
| GC | 面向 NPC 的快照每帧生成数十万个字符串（`_rf.Add(F(...))`），是 GC 压力来源之一；gaze 路径仅 3 个小字符串行。GC 次数、暂停时间 S3 没有记录 |
| 同步写盘/Flush | 两个 gaze 文件每行各一次“打开→追加→`Flush(false)`→关闭”（`CaptureIO.cs:61-72`）；NPC 快照每帧 ≈1.6 MB 的同一模式。耗时由 `CaptureCsvWriter.LastCommitSeconds` 统计，但**S3 清单缺失，数值不可得** |
| 日志 | `enableDebugLogs` 控制；S3 里无证据显示每帧日志 |
| 主线程阻塞 | 全部写盘与 NPC 快照在主线程；无后台线程写入（`CaptureIO`、`CaptureCsvWriter` 均同步） |

### 5.4 gaze 记录器自身开销
* 代码层面是 O(1)。S3 试次阶段（有 gaze 记录）的帧时间落在热身（无 gaze 记录）拟合直线的位置上（试次 N=3 624 实测 258 ms，直线预测 247 ms；N=5 000 实测均值 ≈ 324 ms，预测 338 ms），所以 gaze 记录器的额外开销不超过拟合误差（约 ±30 ms 量级；不超过帧时间的约 10 %）。精确占比：**Not testable**。
* 因此“把 gaze 写盘批量化/移到别的生命周期方法”不能把 3.2 Hz 变成几十 Hz。

### 5.5 与事件的相关性
间隔与 NPC 事件速率窗口相关 r = 0.086、与 HMD 位移 r = 0.013、与 teleport/眨眼/面板事件无差异（§2）。**不能**从这些推出“某事件造成帧变慢”。唯一有效的关联是 NPC 数量（§5.1）。

### 5.6 限制
S3 的 `session_manifest.json`（含 `frame_timing`、`target_frame_rate`、`device_model`、`graphics_device`、写盘耗时统计）不在被截断的压缩包里，所以：真实 CPU/GPU 时间、掉帧统计、GC、写盘耗时都不可得。**回溯归因只能到“NPC 总负载”这一层。**

---

## 6. Meta SDK 的采集能力与限制（调查 E）

安装版本：`com.meta.xr.sdk.core@201.0.0`（C# 包装层可读；原生 `OVRPlugin` 二进制不可检查）。

| 问题 | 结论 | 依据 |
|---|---|---|
| `OVREyeGaze` 是否设计为从 Unity 帧循环轮询 | **是**：`Update()` 内每帧调用一次 `GetEyeGazesState`，不用回调 | `OVREyeGaze.cs:158-166` |
| 取最新眼位姿还是缓冲序列 | **最新状态**：`GetEyeGazesState(Step, frameIndex, ref EyeGazesState)` 只填一个 `EyeGazeState[2]` 和一个 `Time`；`EyeGazesState` 无缓冲或数组历史 | `OVRPlugin.cs:3170-3183, 10728-10766` |
| 原生时间戳/样本序列号 | **有 `Time`（double），无序列号**。`Time` 在 S3 里落在 1/72 s 栅格上 → 更像该帧的（预测）显示时间，而非传感器采样时刻。传感器时间戳**不可得** | S3 `provider_time_s`；`OVRPlugin.cs:3182` |
| 事件/回调/更高频机制 | 安装的 C# 包装层**没有**：只有 `StartEyeTracking / StopEyeTracking / GetEyeGazesState / eyeTrackingEnabled / eyeTrackingSupported`（`OVRPlugin.cs:10705-10783`）。OpenXR 路径在头文件里仅有结构体成员名，没有语义说明 | 同上；`openxr_reflection_full.h` |
| 是否可脱离渲染/主线程独立采集 | **安装的包装层没有此类 API**。能否从工作线程调用 `GetEyeGazesState`：包装层未声明线程安全；此外它查询的是“最新（预测）状态”，不是传感器数据流 → **Not testable / Uncertain**，不能当作可行方案 |  |
| 提供者侧速率限制 | **未记录**：SDK 文档（本包内）没有任何眼动速率的说明；`Step.Physics` 在 OpenXR 下被弃用，只能用 `Step.Render`（`OVRPlugin.cs:625-629, 10733-10737`） |  |
| 能否取回帧间的历史样本 | **不能**（无此 API） | 同上 |
| `frameIndex` 参数 | 项目与 `OVREyeGaze` 都传 `−1`（= 渲染步最新）；包装层没有其他语义的说明 | `OVREyeGaze.cs:160`；`GazeRecorder.cs:742` |
| 刷新率接口 | 有：`OVRPlugin.systemDisplayFrequenciesAvailable`（可用刷新率列表）、`systemDisplayFrequency`（读/设）；`OVRDisplay.displayFrequency`（`OVRPlugin.cs:7561-7640`；`OVRDisplay.cs:335-338`）。具体设备支持哪些值 **Not testable**（需要在设备上读取）。 |  |

后果：**应用不轮询的时间段里，提供者可能产生的任何额外样本不可恢复。** 提频只能靠“更频繁地查询最新状态”，而更频繁地查询只有在提供者的状态真的更新得更快时才得到新数据——这一点在本 SDK 里无法验证。

---

## 7. 根因评估（按置信度排序）

| 排名 | 结论 | 置信度 |
|---|---|---|
| 1 | **Unity 主线程帧时间（3.2 fps）使“每帧一行”的记录器只能写 3.2 行/秒** | **Confirmed**（`frame_index` 恒 +1；首末帧率=行率） |
| 2 | **帧时间随活动 NPC 数线性增长（≈ 7 ms + 66 µs × N，R² = 0.97）；S3 试次时 N = 3 600–5 000** | **Confirmed 关联**；因果方向与形状 **Strongly supported** |
| 3 | NPC 总负载包含 NPC 仿真与逐帧的 NPC 数据采集（每帧完整快照 + 视线射线 + 字符串 + 约 1.6 MB 同步写盘）；两者占比未知 | 存在性 **Confirmed（代码）**；占比 **Not testable** |
| 4 | gaze 记录器自身不是主要成本 | **Strongly supported**（O(1) 工作；与 N 无关；落在拟合线上），精确值 Not testable |
| 5 | Windows Editor + OpenXR（链路）路径、PC 配置的限制（`windows_editor_openxr`） | 路径 **Confirmed**；对帧时间的贡献 **Not testable** |
| 6 | 眼动硬件/提供者限制 | **没有证据**；提供者时间栅格为 72 Hz（≥ 观察到的 3.2 Hz 的 20 倍） |
| 7 | CSV 写盘速度限制了 gaze 记录 | **没有证据**（行不丢、O(1) 写；写盘耗时未测） |
| 8 | 恒定 N 下 ±30 ms 的残余波动（如 GC、后台任务、NavMesh 重算） | **Uncertain** |

---

## 8. 提频可行性（调查 F、G）

### 8.1 五个选项
| 选项 | 评估 |
|---|---|
| **1 提高 Unity FPS** | **唯一能在本数据下直接提高有效记录率的方向。** 将帧时间从 ≈ 310 ms 降到目标需要降低 NPC 工作量（数量、每个 NPC 的开销、逐帧采集开销）。按拟合的外推（仅在该 PC/代码下）：15 fps ⇒ N ≲ 900；30 fps ⇒ N ≲ 400；72 fps ⇒ N ≲ 100。这些是外推，**实际能到多少需要 Profiler 测量**。在 Quest 独立构建上同样 NPC 数的开销更高（CPU 弱）。**能否在 5 000 NPC 下达到 30/72 fps：无证据，倾向不能。** |
| **2 更改采集机制** | 在 `Update()`、`LateUpdate()` 或协程里读取，**不改变新数据的产生率**：提供者状态每帧至多更新一次。把读取移到 `LateUpdate` 只会改变与 `Camera.main`/XR Origin 移动的先后顺序（S3 中 168 行原点错位与此有关，见前一份调查），对频率没有好处；不应仅为“看起来合理”而移动。事件/回调/独立线程：**安装的 SDK 不提供**（§6）。 |
| **3 减少 CSV 写盘开销** | gaze 路径：每行 2 次小文件“打开-追加-冲刷-关闭”，成本 O(1)，相对 300 ms 帧可忽略；批量缓冲会降低断电/暂停时的数据安全，且 S3 数据不支持它是瓶颈。NPC 路径：每帧约 1.6 MB 同步写盘与 O(N) 快照是 **真实的、与 N 成正比的成本**，其调节旋钮已存在（`npcStateDataBudgetMBPerMinute` / `npcStateSamplingIntervalSeconds`），属于“NPC 数据采集”而非 gaze。**这是否值得做：先测量**（见 §9）。 |
| **4 头显刷新率** | 刷新率设置的是显示/合成器与提供者时间栅格（S3 为 72 Hz），**不会让一个 3 fps 的应用变快**；也不会自动提高眼动传感器速率。提高刷新率只有在应用已能跑到该帧率时才有意义；在 Quest 独立运行时更高刷新率还会缩短每帧预算。可用值需在设备上用 `systemDisplayFrequenciesAvailable` 读取（Not testable）。 |
| **5 与渲染解耦** | 包装层没有缓冲/回调/线程安全说明；即使从其他线程调用，也只是读取同一个“最新状态”；需要处理线程安全、时间戳同步、缓冲管理与数据丢失风险，**证据不足以推荐**。 |

### 8.2 目标频率
区分“目标采集频率”和“真正新的测量数”：应用可以每帧写一行，但如果提供者每帧只给出“该帧时刻的最新状态”，则新测量数 ≤ 帧数（甚至 ≤ 传感器的真实更新数，后者未知）。

| 目标 | 分类 | 说明 |
|---|---|---|
| 3.2 Hz（现状） | 当前实现下就是现状 | S3 条件（5 000 NPC，PC Editor） |
| 15 Hz | **需要先降低帧时间**（NPC 负载优化/减少活动 NPC） | 外推 N ≲ 900；gaze 代码无需改动 |
| 30 Hz | **需要大幅降低帧时间** | 外推 N ≲ 400；5 000 NPC 无依据；是否能由“优化每个 NPC 的开销”达成：Not testable |
| 60 Hz | 不被现有证据支持 | 需要 ≤ 16.7 ms 帧时间；外推 N ≲ 140；还要 Quest 独立构建的性能 |
| 72 Hz | 仅当应用帧率≥72 且提供者每帧更新时才有意义；**证据不足** | 72 Hz 是 S3 的显示/提供者时间栅格；达到它意味着 N ≲ 100（外推）。独立测量数无法证实 |
| 90 Hz | 不被现有证据支持 | S3 的时间栅格是 72 Hz，不是 90 Hz；若头显设为 90 Hz 需另测 |

---

## 9. 推荐的最小改动（调查 H）——**本次不实施**

**总体判断：主要靠 Unity 性能（NPC 负载）优化；gaze 采集管线和 CSV 写盘不是首要。** 在做任何代码修改前，先用一次**测量**确定瓶颈分解，这是最小、最安全的第一步。

### 9.1 第一步（零代码）：测量
1. 用现有清单字段重新采集一个 S3 类会话，并**确保压缩包完整**，读取 `session_manifest.json` 的 `frame_timing`、`target_frame_rate`、`device_model`、`graphics_device`，以及 `CaptureCsvWriter` 的 `SumCommitSeconds / MaxCommitSeconds / Commits`（对 gaze、gaze_diagnostics、npc_state）。
2. 在 Unity Profiler 中分别以 NPC 数 = 500 / 1 000 / 2 500 / 5 000，且 `npcCaptureEnabled` 开/关（现有 Part 配置项）各取 30 s，读 CPU 帧时间、`SampleNpcs`、NavMeshAgent 更新、Animator、GC.Alloc、`CaptureIO.AppendText`。这直接分解 §5.2 无法分开的“仿真 vs 采集”。
3. 在设备上做提供者微实验：在同一帧内连续调用 `GetEyeGazesState` 10 次，记录 `Time` 与眼姿的变化；改变显示刷新率（从 `systemDisplayFrequenciesAvailable` 读取）；比较 Link 与 Quest 独立构建。这决定“每帧多读是否得到更多独立测量”。

### 9.2 第二步（若测量证明 NPC 数据采集是显著成本）：调整 NPC 采集而不是 gaze
| 项 | 内容 |
|---|---|
| 脚本/方法 | `Assets/Scripts/Capture/DataCaptureManager.cs`：`SampleNpcs()`（1308-1440）、`NpcLoop()` 中的 `WaitForSecondsRealtime(_rate.EffectiveInterval)`（1285-1302）；配置项 `npcStateSamplingIntervalSeconds`、`npcStateDataBudgetMBPerMinute`（场景 `LyonVRVity35.unity:182988-182990`） |
| 预期收益 | 降低每秒 O(N) 快照次数/数据量，从而降低帧时间 → 提高 gaze 行频率（间接）。**收益大小取决于 9.1 的测量** |
| 风险 | 改变 npc_state 的时间分辨率（影响 NPC 分析）；视线检测列（`los_clear_to_participant`）与 gaze 的对齐粒度变粗 |
| 测试 | 现有 NPC 采集测试 + 对比采样前后的行数/时间间隔；回归 gaze/npc_state 对齐 |
| 是否需要头显验证 | 帧率改善需要在目标设备上验证 |

### 9.3 第三步（仅当第一步证明 NPC 负载已不是瓶颈，且 gaze 路径开销显著）：gaze 路径微优化
| 项 | 内容 |
|---|---|
| 候选 | ①让 `ReadRawGaze` 与 `OVREyeGaze.Update` 共用一次读取（现在每帧 3 次 `GetEyeGazesState`，其中记录器 1 次可以与组件重复）；②合并 `gaze.csv` 与 `gaze_diagnostics.csv` 的两次追加为一次打开（保持逐行 open/append/close 以不损害崩溃安全） |
| 脚本/方法 | `GazeRecorder.cs`：`ReadRawGaze()`（736）、`WriteDiagRow()`（1360）；`CaptureCsvWriter.WriteLine()` |
| 预期收益 | 每帧省几十微秒到亚毫秒；**在 300 ms 的帧里不可见** |
| 风险 | 改变崩溃安全或两个文件的一一对应性；改动 schema 无必要 |
| 测试 | 单元测试 + 比较输出字节完全相同 |
| 头显验证 | 需要 |
| 判断 | **在 S3 条件下不值得**；仅作为 NPC 优化之后的备选 |

### 9.4 不推荐的改动
* 把 gaze 读取移到 `LateUpdate` / 另一线程以“提频”：无证据支持（§8.1 选项 2、5）。
* 在当前 3 fps 下提高 `sampleRate`：已经是每帧一行（上限），无效。
* 把同一帧里的重复读取当作新样本记录：会虚增行数，违反“不要把重复样本当作新测量”。如需更多行，应以提供者时间戳是否变化为准，并保持 `provider_time_changed` 的语义。
* 修改 CSV 架构：本调查所需的所有证据都已存在于现有列中（`frame_index`、`t_abs`、`provider_time_s`、`provider_time_changed`）。

---

## 10. 需要在 Quest Pro 上验证的内容
1. 完整的会话清单：`frame_timing`（帧时间分布、> 100 ms 帧数）、写盘耗时、设备型号、`target_frame_rate`。
2. **独立构建 vs Link/Editor**：同一场景同一 NPC 数的帧率对比；NPC 数 500 / 1 000 / 2 500 / 5 000。
3. 刷新率扫描：72 / 90 / 120（以 `systemDisplayFrequenciesAvailable` 为准）下 `provider_time_s` 的栅格间隔与应用帧率。
4. 提供者查询微实验（同帧多次读取、`Time` 变化）；与已知刺激（如快速定点注视任务）核对“新测量数”。
5. 优化后重新跑完整试次，核对 `frame_index` 连续、`provider_time_changed` 比例、方向重复率、行数/秒与帧率的一致性。
6. 提频后的眼动数据有效性：`left/right_provider_valid`、置信度、原点错位行比例（帧率提高后应下降）。

---

## 11. 局限与未解决的问题
* S3 的 `session_manifest.json`（及 `trajectory.csv`）不在被截断的压缩包里：没有 `frame_timing`、写盘耗时、设备/GPU 型号、`target_frame_rate`；原始 zip 未被改动。
* `npc_state.csv` 只能读取到 t = 626 s（约 5.03 M 行，约 23 % 的估计总行数）；试次阶段的帧时间来自 gaze 行本身，NPC 数来自 `npc_lifetimes.csv`/`npc_events.csv`，**不是**同一来源的逐帧计数。
* NPC 仿真与 NPC 数据采集各占多少无法分开；恒定 N 时的 ±30 ms 波动原因未知。
* 72 Hz 栅格被解释为预测显示时间：**Strongly supported，未用原生文档证实**；传感器真实采样率与时间戳 **Not testable**。
* `audio_metadata.json` 的时间不确定度说明按“90 Hz 帧”写成；S3 实测栅格是 72 Hz（一个小的文档不一致，对本结论无影响）。
* 线性拟合是描述性的，外推到 N < 140 或 N > 5 000 不可靠；Quest 独立构建的性能未测。
* 所有结果为离线数据与源码分析；**没有任何 Quest Pro 运行时测试**。

---

## 12. 最终回答

1. **为什么 S3 只记录约 3.2 个 gaze 样本/秒？** 因为 Unity 主线程只有约 3.2 fps（帧时间 ≈ 310 ms），记录器每帧最多写一行；帧时间与活动 NPC 数近线性相关（≈ 7 ms + 66 µs/NPC；S3 试次时 3 600–5 000 个）。**[Confirmed：帧率=行率；Strongly supported：NPC 总负载是主因]**
2. **是物理眼动仪、Meta 提供者、Unity FPS、记录器实现，还是组合？** 主要是 **Unity FPS**（NPC 总负载）；物理眼动仪与提供者**没有证据**构成限制（提供者时间栅格 72 Hz，传感器率未知）；记录器的“每帧一行”设计使记录率等于帧率，但记录器本身开销不大；组合关系：帧率决定记录率。
3. **30 Hz 是否现实？** 在 5 000 NPC 与当前代码下**不现实**；需要把帧时间降到 ≈ 33 ms（外推 N ≲ 400）。**没有证据**表明仅靠 gaze 代码改动能达到。
4. **72 Hz 是否现实？** **证据不足**：S3 的显示/提供者栅格是 72 Hz，但达到 72 fps 在外推下要求 N ≲ 100；且 72 Hz 的行也未必是 72 个独立的传感器测量（传感器率未知）。
5. **应首先尝试的最小、技术上可靠的改动？** 先**测量**（完整清单 + Profiler，分解 NPC 仿真与 NPC 采集，§9.1），再决定是否调整 NPC 采集的频率/预算（§9.2）。不修改 gaze 采集管线。
6. **应用能否以更高频率记录“真正新的”注视样本，还是只是更频繁地记录同一提供者状态？** 在已安装的 SDK 下，应用只能读“最新状态”，每帧最多得到一个新的提供者时间值；**提高记录频率最多等于提高帧率，且不能证明每行是新的传感器测量**。更高的行频率若超过提供者的真实更新率，将只是重复读同一状态——这一点需要 §10-4 的微实验确认。
7. **接受提频之前必须在 Quest Pro 上测试什么？** 见 §10：完整清单与 `frame_timing`、独立构建 vs Link 帧率、刷新率扫描、同帧多次读取的提供者 `Time` 变化、`provider_time_changed` 比例与方向重复率、`frame_index` 连续性、优化后有效性与原点错位行比例。

**证据是否足以识别根因？** 对“记录率 = Unity 帧率，且帧时间随 NPC 数线性增长”是充分的；对“NPC 仿真 vs NPC 采集的具体分担”、写盘耗时和传感器真实频率则**不充分**，所需的额外运行时测量见 §9.1 与 §10。
