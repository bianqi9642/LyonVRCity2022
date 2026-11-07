# Part 2 隐藏并禁用 Unexplored 传送点 —— 实现报告（2026-11-07）

## 1. 受影响对象（已在已保存的 `LyonVRVity35.unity` 中核对）
- `TeleportationArea/OrganicTeleportPoint/Unexplored/<编号>` —— **125 个**
- `TeleportationArea/GridTeleportPoint/Unexplored/<编号>` —— **190 个**
- 合计 **315 个**，全部是 `Teleport Anchor Variant` 预制体实例（无其它覆盖，仅根物体重命名为数字）。每个点的结构：
  根物体（`TeleportationAnchor`，teleportAnchorTransform 指向子物体 `Anchor`）、子物体 `Visuals`（`MeshFilter` + `MeshRenderer` + `MeshCollider`，Default 层，是交互射线命中的碰撞体）、子物体 `Anchor`（仅 Transform，传送目的点）。
- 不受影响：`Explored`（Organic 101、Grid 146）、`PracticeTeleportPoint`（10）、`LeftBank` / `RightBank` 河岸点、整个 Organic Area / Grid Area 层级本身。
- 说明：共享父物体 `TeleportationArea` 在已保存场景中本来就是 **inactive**（HEAD 同样如此；RiverPointAvailability 的注释也写明）。本方案不会激活它。

## 2. 具体隐藏 / 禁用的组件（仅组件级，GameObject 保持 active，不销毁、不移动）
对每个 Unexplored 点：
1. 其下所有 `XRBaseInteractable`（即 `TeleportationAnchor`；若有 `TeleportationArea` 也会覆盖）→ `enabled = false`（从 XRInteractionManager 注销，无法 hover / select / 发起传送请求）；
2. 其下所有 `Renderer`（可见标记）→ `enabled = false`；
3. 其下所有 **不在 Ground 层（第 3 层）** 的 `Collider`（即 Visuals 的交互 MeshCollider）→ `enabled = false`。Ground 层碰撞体永不触碰。
不触碰：目的 Transform `Anchor`、XR Interaction Setup、全局 TeleportationProvider 配置、河流传送点、ExplorationBoundary、NavMesh、TIN / 地面碰撞体。

## 3. 原始状态如何恢复
`TeleportStateLedger` 在第一次关闭某组件时记录其**原始 enabled 值**，限制结束时原样写回：
- 原来就关闭的组件，恢复后仍是关闭（不会"一律 enabled = true"）；
- 重复进入 Part 2 不会用"被限制后的值"覆盖原始值；
- 组件被禁用 / 销毁（OnDisable）时也会先恢复，不会遗留关闭状态。

## 4. 与 ExperimentManager 的集成（无轮询、无逐帧开销）
新增 `UnexploredTeleportAvailability`（场景加载后自动创建，与 `ParticipantControlLock` / `RiverPointAvailability` 相同模式，**无需修改场景**）：
- 订阅 `ExperimentManager.OnExperimentEvent`：`part_start`（`part == "Part2"` → 限制；其它 Part → 恢复）、`part_end` 与 `OnSessionEnded` → 恢复。
- `part_start` 在 `RunPart2()` 一开始同步触发，早于参与者获得控制权，因此不存在可选中的空窗期。
- 启用时读取 `ExperimentManager.RunningPartName`：直接从 Part 2 启动、或场景重载时 Part 2 正在运行，也会立即生效。
- Part 1：保持原状；Part 3：恢复原状并沿用现有 Part 3 规则；河流传送点规则完全不变。

## 5. 是否扩展了现有 Teleport Guard —— **是（小幅、增量）**
`GuardedTeleportationProvider`：新增两个带默认值的序列化字段（`unexploredGroupName = "Unexplored"`、`unexploredBlockedPart = "Part2"`），锚点缓存新增 `anchorCacheUnexplored`；`Evaluate` 在 `ParticipantControlEnabled` 检查之后、策略判断之前，若运行中的 Part 为 Part 2 且目的点属于 Unexplored 锚点 → 拒绝，原因 `unexplored_anchor_Part2`（沿用既有 `teleport_rejected` 事件，**数据记录架构未改**）。这是兜底：在 Part 2 开始前已被选中/悬停的目标无法完成传送。未新增第二套传送管理系统。

## 6. 测试
| 类别 | 内容 | 状态 |
|---|---|---|
| 纯逻辑（mono）| `TeleportStateLedgerTests`（2 项：原值只记一次、原本关闭的保持关闭）| **PASSED** |
| 编译 | Runtime / PlayMode 测试程序集 / EditMode 测试程序集 | **PASSED**（无错误）|
| PlayMode（`UnexploredTeleportComponentTests`，8 项）| Part1 不变；Part2 两个区域 Unexplored 全部隐藏且禁用、Explored 与区域外点不受影响、Ground 层碰撞体不变；Part3 恢复且保留原本关闭状态；Part1→2→3→2→2→1→2→3 重复切换与 part_end；直接以 Part 2 初始化；组件禁用/重新启用无重复订阅；不会激活 inactive 的共享父级；Guard 在 Part 2 拒绝 Unexplored、不拒绝 Explored、Part 1/3 不变 | **NOT RUN**（你的 Editor 已打开本项目，未启动第二个 Unity）|
| EditMode（`UnexploredTeleportRealSceneTests`，2 项）| 只读审计当前已加载场景：Organic 125 / Grid 190，每点含锚点、渲染器、非 Ground 交互碰撞体、目的点 | **NOT RUN** |
运行方式：Window > General > Test Runner，PlayMode 运行 `UnexploredTeleportComponentTests`；EditMode 在场景已打开时运行 `UnexploredTeleportRealSceneTests`。

## 7. 确认事项
- 未修改：场景文件、预制体、NavMesh、TIN / MeshFilter / MeshCollider 引用、NavMeshGroundProxy、GroundFallRecovery、ExplorationBoundary、数据记录 schema、实验流程、全局 XR 移动设置。Ground 碰撞体未被禁用或改动（代码按层排除）。
- 未删除任何 GameObject / 预制体 / 资源；未提交、未推送。

## 8. 修改的文件
- 修改：`Assets/Scripts/GuardedTeleportationProvider.cs`（备份：`Backups/UnexploredTeleport_20261107/GuardedTeleportationProvider.cs.orig`）
- 新增：`Assets/Scripts/TeleportStateLedger.cs`、`Assets/Scripts/UnexploredTeleportAvailability.cs`（含 .meta）、`Assets/Tests/GazeRecorder/UnexploredTeleportTests.cs`、`Assets/Tests/EditMode/UnexploredTeleportRealSceneTests.cs`（含 .meta）
- 场景中无需新增或修改任何 GameObject（运行时自动创建 `UnexploredTeleportAvailability`）。

## 9. 剩余风险与 Quest Pro 测试
- 以上行为尚未在 Unity Play Mode / 头显中验证；需在 Quest Pro 上确认：Part 2 开始瞬间两个区域的青色标记全部消失、射线无法 hover/选中、Explored 点正常传送；Part 3 恢复；Part 1 不变。
- 限制依赖层级名称（`Unexplored` 位于 `GridTeleportPoint` / `OrganicTeleportPoint` 之下）；若改名需同步修改组件 / Guard 的名称字段。
- 若有其它脚本在 Part 2 期间主动重新启用这些组件，不会被轮询纠正（Guard 仍会拒绝传送请求）。
- `TeleportationArea` 根物体本身保存为 inactive：如需让 Practice / Grid / Organic 点在运行时可用，需由你在 Editor 中确认其激活方式（本次未改动）。
