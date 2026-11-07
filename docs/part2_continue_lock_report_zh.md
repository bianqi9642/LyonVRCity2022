# Part 2 Continue 面板:锁定参与者位移(报告)

日期:2026-11-07。状态:代码已改、已离线编译;**EditMode / PlayMode / Quest 均未运行**(Editor 正被用户占用)。

## 1. Continue 面板对象
`UIManager.part2ContinuePanelTemplate`(带 `PanelController` 的模板,运行时实例化为 UIManager 的子物体)。入口:`UIManager.ShowPart2ContinuePanel(Action)`,由 `ExperimentManager.RunPart2()` 调用。Continue 只能通过面板上的 `confirmButton`(UI 射线点击)触发,没有走 Teleport 输入。

## 2. Part 3 参考机制
`ExperimentManager.LockParticipantForCheckpoint / ReleaseParticipantCheckpointLock`(`_checkpointLockActive`,幂等),经唯一闸门 `SetParticipantControl` → `ParticipantControlEnabled` / `OnParticipantControlChanged` → `ParticipantControlLock`、`GuardedTeleportationProvider`、`RiverTeleportPoint`、`RiverPointAvailability`、`TargetSelectable`。Part 3 逻辑未改。

## 3. 激活点
`RunPart2`:`SetPhase("p2_continue_pending")` 之后、`RaiseEvt("panel_shown", label:"part2_continue")` 与 `ShowPart2ContinuePanel` 之前,同步调用 `LockParticipantForCheckpoint("P2","part2_continue_panel_shown")`(受新增字段 `lockParticipantWhilePart2ContinuePanel`,默认 true 控制)。

## 4. 释放点
Continue 回调内,在 `RaiseEvt("panel_confirmed")` → 原有 `TeleportPlayerTo(...)` → `teleported = true` → `SetPhase("p2_task2")` **之后**调用 `ReleaseParticipantCheckpointLock("P2","part2_continue_confirmed")`;`WaitUntil(teleported)` 之后再放一次幂等保险。回调开头新增 `if (teleported) return;` 以忽略重复 Continue 事件。

## 5. 被锁定的对象
所有非 Teleportation 的 `LocomotionProvider`(连续移动、Omnideck 连续移动、连续/瞬时转向等);`GuardedTeleportationProvider` 拒绝所有 XRI 传送(`participant_locked`,已排队/待完成的请求也被拒绝);河流传送点;建筑选择。

## 6–7. UI 射线 / 头部
UI 输入、EventSystem、控制器射线、XR Interaction Manager、追踪与头部物理移动均未触碰(闸门不涉及它们)。

## 8–9. 状态保存与叠加
`ParticipantControlLock` 保存并恢复每个 provider 原本的 enabled 状态(原本禁用的保持禁用)。若已有其他锁(Start/End 面板、会话结束),`LockParticipantForCheckpoint` 直接返回且不置 `_checkpointLockActive`,因此 Continue 不会解开不属于自己的锁;会话结束/End 面板状态下释放为空操作,不会重新开放。`BeginTrial` 清除标志。

## 10. 传送 / 区域切换
Continue 目的地、`TeleportPlayerTo`、ExplorationBoundary 校验均未改动(脚本传送不受 `ParticipantControlEnabled` 限制)。

## 计时与记录
锁定不改变任何时钟、trial、NPC、音频、录制:`p2_continue_pending` 本就是 trial 打开但非任务时间的阶段;第二段 `part2SecondDuration` 仍从 Continue 点击起算。新增 events.csv 行:`participant_control`(`locked;reason=part2_continue_panel_shown` / `unlocked;reason=part2_continue_confirmed`),沿用既有事件类型与列,无 schema 变更。

## 11. 测试
- 新增 `Assets/Tests/GazeRecorder/Part2ContinueLockFlowTests.cs`(PlayMode,真实 `RunPart2`):A 普通 Part 2 不锁;B/C 面板出现后锁定且持续锁定、河流点不可用、Continue 按钮可用;D Continue 恰好一次、传送位置不变、解锁、事件顺序;选项关闭=旧行为;重复 Continue 事件;面板期间会话终止后不重开;已有锁不叠加;默认值。Part 3 由既有 `Destination2*Tests` 覆盖(未改)。
- 离线编译 `Assembly-CSharp`、`GazeRecorder.Tests`:通过(仅既有警告)。
- **PlayMode / EditMode:NOT RUN**。**Quest Pro:NOT RUN**。

## 12. 修改文件
`Assets/Scripts/ExperimentManager.cs`(新增字段 + 3 处调用 + 重复事件保护);新增测试文件与本文档。备份:`Backups/Part2ContinueLock_20261107/ExperimentManager.cs.orig`。

## 13. 剩余风险 / Quest Pro 测试
1. 手柄上同时用于 UI 点击与传送激活的按键:Continue 后若仍按住,解锁瞬间不应触发传送(XRI 需要新的按下);请在设备上确认。
2. 面板首次放置可能等待至 settleTimeout,此期间已锁定(与 Part 3 一致)。
3. 中途 `StartPart2()` 重启会停止协程;锁标志由下一次 `BeginTrial` 清除(与 D2 相同)。
4. 请在 Quest Pro 上验证:面板出现后摇杆/转向/传送均无效,射线点击 Continue 正常,点击后落点不变且立即可移动,头部可自由转动。
