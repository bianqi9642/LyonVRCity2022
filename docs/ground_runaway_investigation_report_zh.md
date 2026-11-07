# Quest 测试中参与者持续“坠落”的调查报告（2026-11-07）

证据来源：两次会话的 CSV 数据、项目代码、以及在项目克隆上的离线复现。**我没有做头显实测，也没有任何提交。** 英文摘要：`docs/ground_runaway_investigation.md`。
一句话结论：**这两次不是“穿过地面”，而是 `ExplorationBoundary` 的一个坐标写错 bug 把 rig 一帧抛到 ~650 m 之外，之后每帧再抛 ~1000 m，rig 自然在无地面的空中自由落体。** 修复后的 TIN 与 100% 的离线碰撞覆盖没有错，但与这次故障无关，GroundFallRecovery 在这种情形下又被设计性地“看不见”。

---

## 1. 两次 ZIP 的文件结构与数据完整性

| 存档 | 文件 | 大小 (B) | 说明 |
|---|---|---:|---|
| 14:05 `20261106T140500Z_2a699c8c7e66.zip` | zip 本身 | 1 790 120 566 | **已损坏（截断）**：缺少 central directory（`unzip` 报 End-of-central-directory not found），`npc_state.csv` 声明 5 215 160 486 B 但只有约 1.74 GB 到达 |
| | building_registry.csv | 180 213 | 完整 |
| | events.csv | 14 661 | 完整（122 行，t_abs 4.02→1329.47，`session_end app_quit`） |
| | gaze.csv / gaze_diagnostics.csv | 831 688 / 1 000 370 | 完整（1699 行，含 `hmd_world_*`、`ground_y`、`hmd_height_above_ground`） |
| | npc_events.csv / npc_lifetimes.csv / npc_profiles.csv | 44 988 507 / 3 617 253 / 10 151 | 完整 |
| | npc_state.csv | 截断 | 不可用，与本次问题无关 |
| | **trajectory.csv、session_manifest.json(.bak)、session_config.used.json** | — | **不在可读部分里（zip 目录序在 npc_state 之后，已随截断丢失）** |
| 14:29 `20261106T142958Z_6b2d7675e9b8`（已是解压目录，无 zip） | 12 个文件：building_registry、events (19 994)、gaze (2 504 612)、gaze_diagnostics (3 064 041)、npc_events (4 852 409)、npc_lifetimes (693 562)、npc_profiles、npc_state (615 422 100)、session_config.used.json、session_manifest.json(+.bak)、trajectory.csv (1 800 253，5377 行) | | manifest 的所有 writer `health_*` 均 `ok`、无写入错误，`end_reason: app_quit` |

两次均**没有**：Unity Player/Editor 日志、Android logcat、XR 运行时消息、GroundFallRecovery / AutoGroundAlign 日志、CharacterController 诊断、碰撞事件。只有项目自己的 CSV 记录器。
重要：14:29 的 manifest 写明 `platform: WindowsEditor`、`is_editor: True`、`runtime_path: windows_editor_openxr`、设备 `UOL-1124482`——**这是 PC 上的 Unity Editor（OpenXR 链接），不是 Quest 单机 Android 构建**。14:05 无 manifest，平台未知（数据格式相同）。
会话起止（UTC）：14:05 → t_abs 4.02–1329.47 s，14:29（manifest）2026-11-06T14:29:58Z–14:36:15Z。Unity 2022.3.53f1，场景 LyonVRVity35，Part2，参与者 4。
建议：重新拷贝 14:05 的 zip（现在这份是传输中断的文件）。

## 2. 14:05 测试的坠落时间线（`gaze.csv` + `events.csv`；trajectory 缺失）

| t_abs (s) | 事件 / 状态 |
|---|---|
| 4.04 | teleport 到 StartPointG；5 000 NPC 池准备到 87.9 s，NPC 预热 600 s 至 688.1 s（采样率仅 ~3.3 Hz，见 §4） |
| 811.19 / 811.48 | `panel_confirmed` / `p2_task1` 开始；之后用 XRI teleport 行走（~20 m 一跳），`teleport_rejected head_outside_exploration_area` 出现多次 |
| 1111.63–1121.29 | `p2_continue_pending` 面板，确认后 `teleport` 到 StartPointO (2228.18, 169.44, 810.96)，进入 `p2_task2` |
| 1121.3 → 1284.9 | 连续行走（无 teleport 事件）向西，HMD 高度 1.22–1.54 m，`ground_y` 全程有效，**无地面丢失** |
| **1284.92（最后一个正常样本）** | HMD (2114.43, 168.864, 809.08)，ground_y 167.336，离地 1.528 m。此点距离 Exploration Area 南缘约 1 m |
| **1285.32（首个异常样本）** | HMD z=1450.77（**+641.7 m / 帧**），ground_y=NaN。1285.71：z 2467 (+1016)，x 2114→2049；之后 x 锁定 2046.0，z 每帧 +1016…+1020（缓慢增大） |
| 1286.0 起 | y 168.86→167.9→166.1→163.0…，加速度≈g（自由落体）。1298.9–1329.0 出现 `app_focus` 0/1，1329.47 `app_quit`，此时 HMD ≈ (2046, -7997, 506 212) |

分类：既不是 A（穿地）也不是 B；是 **C（XR Origin 被脚本意外移动）**，随后 **E 变体**（没有恢复）。D 不成立：附近没有 `teleport` 事件（也没有 `teleport_epoch` 变化）。

## 3. 14:29 测试的坠落时间线（`trajectory.csv` 20 Hz + `gaze.csv`）

| t_abs (s) | 事件 / 状态 |
|---|---|
| 4.51 | teleport 到 StartPointO；67.89 `p2_task1` 开始，随后 XRI teleport 行走 |
| 347.15 / 349.54 | 两次 `teleport_rejected head_outside_exploration_area`，目标 (1971.8, 203.8, 843.0)：此时参与者已贴近西缘 |
| 367.95 | `phase_start p2_continue_pending`（300 s 到期），`panel_shown part2_continue` |
| **368.486（最后正常样本）** | rig (1966.97, 205.560, 865.79)，view 高度 1.508 m，`hmd_local` 正常 (-0.04, 0.03, 0.30) |
| **368.558（首个异常）** | rig z=1525.94（**+660.15 m / 帧**），x +0.18，y 不变；`teleport_epoch` 仍为 19 |
| 368.599 | rig (2045.80, 205.56, 2505.47)：z +979.53，x +78.65 |
| 368.64 → 380.97 | 每帧 z **+979.53 m**（帧间隔 0.04 与 0.07 s 时增量相同，所以与时间无关，是每帧固定位移）；y 先保持 205.56 约 1.6 s，之后自由落体 |
| 381.22 | `app_quit`：rig (2045.76, **-31.45**, 249 690.8)，ground_y 不可得 238 个样本（368.56–381.25） |

## 4. 两次测试的共性与差异

| 指标 | 14:05 | 14:29 |
|---|---|---|
| 首次异常时间 | t_abs 1285.32（±0.3 s） | t_abs 368.558 |
| 初始位置 | HMD (2114.43, 168.86, 809.08)，距 Exploration Area 边缘 ~1 m | rig (1966.97, 205.56, 865.79)，西缘 |
| 实验阶段 | Part2 / `p2_task2` | Part2 / `p2_continue_pending` |
| 地面 Collider 是否存在 | 是：onset 前 1 554 个样本 ground_y 有效，离地 1.22–1.54 m | 是：onset 前 4 971 个样本，离地 1.29–1.67 m |
| CharacterController 接地 | **未记录**（无此字段） | **未记录** |
| 最后安全位置 | 无记录（GroundFallRecovery 不写日志） | 无记录 |
| Recovery 是否评估 / 触发 | 无证据；按代码不可能触发（§8） | 同左 |
| 终局 | 7997 m 深、z 506 km，`app_quit` | y=-31、z 249 km，`app_quit` |
| 帧率 | ~3.3 Hz 行（5 000 NPC 池 + 600 s 预热，npc_events 218 854 行） | ~17.7 Hz（1 000 NPC） |
| 平台 | 未知 | WindowsEditor / OpenXR |

共性：同一组数学特征（首帧 ≈ z−高度，之后每帧固定 ≈ 1185−y 的 +Z 位移；x 收敛到 ~2046）；都在 Part2、都贴着 Exploration Area 边缘、都无 teleport 事件；都**可复现**（2/2）。差异仅在帧率/NPC 负载（14:05 约 3 fps，本身对 VR 不可用，但不是坠落原因）和阶段。位置不同（西缘 vs 南缘），所以不是某一块 Collider 的问题。

## 5. Ground Collider 运行时状态

* 运行时直接证据：两次会话在 onset 前所有样本的 `ground_y` 有效（`AutoGroundAlign` 的 Ground 层射线命中），HMD 离地高度 1.22–1.67 m，没有下沉趋势。→ **参与者脚下的物理地面在故障前一直存在。**
* 运行时用的是否为修复网格：日志里没有网格引用，**无法由数据证明**。提交（HEAD）场景中 20 个 Ground 层 TIN 碰撞体确实引用 `Assets/Models/TIN/Repaired/*`（离线检查：20/21 为修复网格，1 个是 SouthRiver 补丁原件）。
* 谁激活地形：全项目无任何脚本激活/停用 `TIN_Textured`/`Practice Area`（grep 确认）。HEAD 场景里 `TIN_Textured`（两组）、`Practice Area` 为 inactive，而会话里有地面 → 运行时必然有人在 Editor 里手动激活（日志里没有记录）。**现在磁盘上的未提交场景（2026-11-07 12:57 保存）把 `Grid Area`、`Organic Area`、`Practice Area`、`NavMesh`、`NavMeshGroundProxy` 全部设成了 inactive**，所以那个文件不能代表当时的运行时状态。
* 不要盲目全部激活；这里没有改任何激活状态。

## 6. CharacterController 接地状态

场景里的 rig（`XR Interaction Setup/XR Origin (XR Rig)`）：CharacterController enabled、layer 2、h=1.36144、r=0.1、center (0,0.76,0)、skin 0.08、step 0.5、slope 45、minMove 0.001；`CharacterControllerDriver`、`OmnideckContinousMoveProvider`（useGravity=1、AttemptingMove）、`GuardedTeleportationProvider`、GrabMove/Climb provider、`AutoGroundAlign`、`GroundFallRecovery` 都在同一 rig 下。
`isGrounded`、`CollisionFlags` 在任何日志中都没有记录，**接地状态无法从两次数据确认**（只能从 HMD 离地高度与 ground_y 间接推断：onset 前正常）。故障后 14:29 的 y 在最初 ~1.6 s 保持常数、14:05 的 y 立即自由落体——说明 gravity 只在控制器移动时作用（AttemptingMove 模式），与 rig 被 `transform.position` 直接写入一致。

## 7. XR Origin 移动与重力逻辑

直接写 rig 位置的代码（全项目搜索）：`ExperimentManager.TeleportPlayerTo`、`RiverTeleportPoint.TeleportPlayerTo`、`ExplorationBoundary`（`LateUpdate`→`EnforceContainmentByShift` 或 `usePositionSafetyCheck`）、`GroundFallRecovery.Recover`；另有 XRI 的 Teleportation/ContinuousMove（CharacterController.Move）。
时序：`ExplorationBoundary.LateUpdate` 每帧执行（`enforceContainment=1`、`containHeadPosition=1`、`ClampToClosestEdge`），只在 Part2 且 `ProtectionActive` 时有效。
**bug 位置**：`Assets/Scripts/ExplorationBoundary.cs`（修复前）第 375 行
`shift = new Vector3(target.x - q.x, 0f, target.y - q.y);` —— `target` 是 `Vector2(x,z)`，所以 `target.y` 是 Z，而 `q.y` 是头部**高度**。应为 `q.z`。该行自提交 fb0bf256（2026-10-26 “Teleport interaction 3”）起存在，2026-10-27 起场景启用 `enforceContainment`。
复现：在克隆里取 14:29 的头位置 (1966.93, 205.60, 866.09)，调用生产代码循环：
frame0 shift=(0.30, 0, **660.42**)，frame1 (78.83, 0, **980.89**)，frame2…(0, 0, 980.89)；日志是 (0.18, 660.15)、(78.65, 979.53)、(-0.03, 979.52)。常数 = area 最远边 z≈1185.1 减头部高度 205.56 = 979.5；14:05 因 y 下落而缓慢变大（1185−y）。差异 ≤1 m 来自我重放时取的头位置略有不同。
为什么每帧都重复：移动 rig 后头仍离区域更远（正确距离应为 0.21 m，错误位移后变成 349 m、1321 m…），`IsInsideArea(head)` 一直为假，循环永不收敛。
合成测试：旧脚本下头在 y=200、区域外 2 m 时 shift=(-2.1, 0, **-200**)；y=0 时恰好“正确”——所以这个 bug 在水平地面高度接近 0 的单元测试里看不到。

## 8. GroundFallRecovery 是否正确触发

结论：**没有触发，而且在这种故障下按设计就不可能触发。**
旧代码（HEAD）`GroundFallRecovery.cs` 第 97–99 行：单帧位移 > `teleportJumpMeters`(2.5 m) 视为 teleport → `hasSafe=false`、冷却重置、不做任何恢复。故障中每帧位移都是 ~1000 m，于是每帧重置安全位置、永远进不了 `noSupportSeconds` 计时（第 112 行 `if (!hasSafe || ...) return`）。
逐项答复：A 脚本是否活动——日志里无证据（旧版不写日志）；B 条件是否满足——不满足（原因如上）；C 安全位置无效——没有安全位置；D 被其他脚本覆盖——ExplorationBoundary 每帧继续写；E 恢复位置无地面——n/a；F 其他：恢复逻辑仅覆盖“慢速掉落”。
上一轮已修复过的“安全位置记录下沉位置”缺陷在现版本仍保持（`safeMaxSink`，测试覆盖）。

## 9. 确认的根本原因及证据

| 排名 | 原因 | 状态 | 证据 / 反证 |
|---|---|---|---|
| 1 | `ExplorationBoundary.TryRecoverShift` 用 `q.y`（高度）代替 `q.z`，头部越界 >0.1 m 时 rig 被抛出 ~650 m，随后每帧 ~1000 m | **已确认** | 两次会话的位置序列（14:05 gaze 1285.32；14:29 trajectory 368.558，teleport_epoch 不变）；生产代码重放出同样的 660.42/980.89；旧脚本单元测试失败、新脚本通过。反证：无 |
| 2 | GroundFallRecovery 把每帧大位移当作 teleport，永远无法恢复 | **已确认（代码）** | 第 97–99、112 行；新增测试 `Recovery_UndoesAPerFrameRunawayIntoEmptySpace` 在旧版失败 |
| 3 | 参与者贴着 Exploration Area 边缘行走/头部越界（触发条件） | 很可能 | 14:05 距边 ~1 m；14:29 位于西缘（此前 teleport_rejected 都在这里）；Omnideck/房间尺度使头比根节点先出界 |
| 4 | 14:05 的 3.3 fps（5 000 NPC + 600 s 预热） | 促成因素，非原因 | 每帧位移与帧率无关；但 3 fps 在 VR 里不可用，应与 1 000 NPC 配置区分 |
| 5 | TIN 翻转三角形/碰撞覆盖 | **可排除为这两次的原因** | onset 前全部样本均有地面；故障点离地面 600 m 以外 |
| 6 | 运行时地形激活缺失 | 未发现证据 | ground_y 持续有效；但激活是手动的，见 §5 |
| — | Quest 单机特有行为 | 无法判断 | 数据来自 Windows Editor |

**回答核心问题：为什么修复了 TIN、离线碰撞覆盖 100% 之后仍然“坠落”？** 因为这次看到的坠落根本不是碰撞失败。之前的离线审计只测了静态几何（2 m 采样、静态 Collider、真实 CharacterController 向下 Move），它既没有运行 Part2 的 `ExplorationBoundary`，也没有模拟头部越界时的脚本写位置；故障来自一个把 rig 抛到地图外的脚本 bug，之后脚下没有任何地面——任何 Collider 都救不了。（NavMesh 与此无关。）

## 10. 修改的脚本、场景对象和资产

备份：`Backups/GroundRunaway_20261107/`（ExplorationBoundary.cs、GroundFallRecovery.cs、GroundNavRegressionTests.cs，含 SHA1）。**未改场景文件、TIN 资产、NavMesh、Collider、CharacterController 参数、图层、激活状态。**
* `Assets/Scripts/ExplorationBoundary.cs`：`q.y`→`q.z`；新增 `maxContainmentShift`（默认 30 m）：超限修正被拒绝并警告一次。（新增序列化字段，下次保存场景时写入默认值。）
* `Assets/Scripts/GroundFallRecovery.cs`：新增路径 B（无支撑的跳变目的地 0.5 s 后回退到跳变前最后有支撑的位置；落在地面上的真实 teleport 不受影响）；新增 `LastRecoveryReason`；可选诊断 CSV `ground_recovery_<UTC>.csv`（`Application.persistentDataPath`，事件驱动 + 无支撑时 1 Hz；记录 rig/头位置、cc enabled/isGrounded、碰到的 Collider 路径/层/网格名、最后安全位置、恢复原因；开关 `writeDiagnosticsFile`，不改任何记录器 schema）。
* `Assets/Editor/NavMeshAudit/GroundNavFix.cs`：（2026-11-07 更正）曾加入 `enabled = true`，已撤销；9 个被禁用的 Modifier 是有意配置，见 `docs/navmesh_modifier_config.md`。
* 测试：`Assets/Tests/EditMode/GroundRunawayRegressionTests.cs`（新，9 个合成用例）、`GroundNavRegressionTests.cs` 增加 1 个真实场景重放；内存中激活更多组。
* 文档：`docs/ground_runaway_investigation.md`、本报告。

## 11. 修复前后测试结果（EditMode，克隆项目，未头显）

* **旧脚本 + 新测试**（突变检查）：boundary 用例全部按预期失败（`(−2.10, 0, −200.00)`、`−155.7`、真实场景 `660.42 m`），runaway 恢复用例失败（“the runaway must be undone: 0”）。
* **新脚本**：17 个测试中 15 通过；新增的 9 个合成 + 1 个真实场景重放全部通过：区域外头位置的修正为小幅水平量（≈到边距离+0.1 m）、与高度无关、一次修正后进入区域；超过 30 m 拒绝；runaway 被撤销（`void_displacement`）；落在其他地面的 1 km teleport 不被撤销；下沉位置不会被记为安全位置。
* 仍失败的 2 个（都由当前未提交场景造成，非本次改动）：
  1. `NavMeshSurface_CollectsNoWalkableBuilding_AndOnlyRepairedProxiesAsTin`：当前场景里 9 个 NavMeshModifier（8 个 TIN 忽略 + 补丁）被保存为 `m_Enabled: 0`（HEAD 只有 1 个）。已验证：禁用时重新烘焙会把 20 个可视 TIN（含翻转三角形）重新当作 NavMesh 源；启用后为 0。已烘焙的 NavMeshData 资产不受影响。
  2. 旧的 `EveryWiredStartAnchor_…`：它只激活 TIN 组，而当前场景 `Grid/Organic Area` 为 inactive。
* **没做**：PlayMode 完整流程、Grid/Organic 过渡走查、真实 teleport 交互回归、头显测试。清单第 1–6、8 项（行走）在本问题上不是失败点，仅有合成几何覆盖，没有逐区行走回放；第 9–13 项由合成测试覆盖逻辑（teleport 不误触发、不保存地下位置、不触及记录器——代码没有碰记录器）。

## 12. 尚未解决的问题

1. 14:05 的 trajectory/manifest 缺失（zip 截断）；请重新拷贝。
2. 数据来自 Windows Editor 而非 Quest 单机；Android 构建行为未验证。
3. `isGrounded`/`CollisionFlags`/CharacterController 状态从未被记录；新诊断 CSV 会开始记录。
4. 未提交场景的状态：9 个 NavMeshModifier 被禁用、区域整体 inactive；请确认是否有意。（更正：这 9 个 Modifier 的禁用状态是有意配置，必须保留，不要启用；见 `docs/navmesh_modifier_config.md`。）
5. 参与者如何在会话中让头越界（Omnideck 房间尺度/走近边墙）——现在越界只会产生小修正；仍建议在边缘实地走一遍。
6. 以前报告里的遗留项依旧存在：Grid/Organic 瓦片重叠（最大 8 m 差）、Practice 内 160 m² 开口、建筑内室内 NavMesh 孤岛。
7. 14:05 的 3 fps（5 000 NPC + 600 s 预热）不适合真实实验。

## 13. 下一次 Quest Pro 实机测试方案

1. 用**真正的 Quest 构建**（或明确记录用 Link/Editor），确认场景里 `Grid Area`、`Organic Area`、需要的 TIN 组、`NavMesh` 均为激活，8 个 TIN 忽略 Modifier 为启用；构建前跑一次 EditMode 测试。
2. 在 Part2 西缘、南缘、各拐角各贴着边界走 2 分钟，并用头部探出边界；预期：rig 只发生小于 ~2 m 的修正，`ExplorationBoundary` 日志（`enableDebugLogs` 已开）显示 `rig shifted by X m` 且 X 很小；不应出现 30 m 拒绝警告。
3. 在每个阶段切换（p2_start→task1→continue→task2）各做一次靠边停留。
4. 收集：session 文件夹**及** `ground_recovery_*.csv`（Windows：`%USERPROFILE%\AppData\LocalLow\<公司>\<产品>\`）以及 Unity Player/Editor 日志；Android 用 `adb logcat -s Unity`。
5. 成功判据：无 `jump_detected`/`recovery` 行；若出现，`reason` 与位置要能解释；trajectory 中无 >2.5 m 单帧位移（除已登记的 teleport）。
6. 用 1 000 NPC 配置，确认帧率 >60 fps（Quest）；再单独测 5 000 NPC。
7. 在此之前**不能**声称 VR 运行时安全。
