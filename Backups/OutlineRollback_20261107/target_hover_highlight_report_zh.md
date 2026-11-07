# Part 1 / Part 3 目标建筑悬停高亮增强(报告)

日期:2026-11-07。状态:代码、着色器、测试已写好,**离线编译通过(C#)**;**着色器未经 Unity 编译、PlayMode/EditMode 未运行、画面未目视、Quest Pro 未测**(Editor 被占用)。

## 1. 原有实现
`InteractableHoverFeedback`(与 `TargetSelectable` 挂在同一建筑根物体上)通过 `IPointerEnter/ExitHandler` 接收悬停,用 `MaterialPropertyBlock` 把 `_BaseColor` 提亮 +30%(场景里 12%/30% 两种序列化值)。射线管线:`TrackedDevicePhysicsRaycaster` → EventSystem → 命中子碰撞体后事件沿层级冒泡到根上的处理器(因此整栋建筑所有 Renderer 都会处理)。`ExperimentManager.SetCandidateInteractable` 同时启停 `TargetSelectable` 与 `InteractableHoverFeedback`(Part 3 候选建筑);Part 1 的测试目标由 `ConfigureTargetsForPart1` 激活。场景共 13 个实例(9 个默认禁用)。选中不会产生任何持久的“已选中”视觉效果(`TargetSelectable.Select` 只发事件、登记 `RegisterTargetSelection`),因此没有需要保留的选中外观;悬停逻辑完全不触碰选择状态。

Part 1 / Part 3 使用同一套悬停机制,仅“谁可交互”不同(Part 3 由 `SetCandidateInteractable` 按阶段控制)。建筑是共享预制体的实例,材质由 BATI 的 1110 个 URP/Lit 材质共享。

## 2. 渲染管线与材质核查
URP 14.0.11;全部 1110 个 BATI 材质为 URP/Lit,`_BaseColor` = 白,`_EMISSION` 关键字**全部关闭**。项目内没有现成描边着色器(只有 TextMeshPro 的)。结论:`_EmissionColor` 通过 MaterialPropertyBlock 不会生效(无法开启关键字),所以“自发光”仅在材质真的启用 `_EMISSION` 时才写入;当前建筑的颜色增强走 `_BaseColor`(乘法,保留纹理)。

## 3. 选定技术
- **描边**:反向外壳(inverted hull)。新着色器 `Assets/Resources/Shaders/TargetHoverOutline.shader`(`Hidden/Lyon/TargetHoverOutline`,URP 无光照、Cull Front、深度测试+写入、单通道实例化宏齐全)。每个建筑 MeshRenderer 在首次增强悬停时懒创建一个**默认禁用**的子物体 `__HoverOutline`(共享同一网格、无碰撞体、不投/收阴影),只在悬停时启用;全部建筑共用 **一个** 运行时 Material,颜色/宽度通过每个描边 Renderer 的 MaterialPropertyBlock 设置。无后处理、无额外相机、无 stencil、不改 URP 渲染器资产。
- **颜色增强**:`_BaseColor × lerp(白, 暖色, 强度) × (1+亮度)`;退出时恢复悬停前该 Renderer 的 block(原本没有 block 则清除,回到 SRP Batcher 路径)。
- 状态:`Enhanced`(Part 1 / Part 3 且参与者可操作)、`Legacy`(其它情况,含 Part 2:行为与之前完全一致,且不创建描边)、`None`(Part 1/3 中参与者被锁定:开始面板/D2 面板/结束面板/会话结束——此时点击会被忽略,不再显示可选中的暗示)。阶段判断使用 `ExperimentManager.RunningPartName` + `ParticipantControlEnabled`,并监听 `OnParticipantControlChanged`、`OnExperimentEvent`(part_start/part_end)、`OnSessionEnded`,悬停期间阶段变化会立刻清除或切换。

## 4. 默认参数(Inspector,组件 `InteractableHoverFeedback`)
| 字段 | 默认 | 说明 |
|---|---|---|
| Enhanced Hover | true | 总开关;false = 回到旧的亮度提升 |
| Enhance In Part1 / Part3 | true / true | 分阶段开关 |
| Require Participant Control | true | 锁定时不显示悬停 |
| Outline Color | (1.0, 0.72, 0.10) 琥珀色 | 在浅色石材与深色屋顶/窗纹理上都有对比 |
| Outline Width | 0.004 | 每米相机距离的世界外扩量(约 3 像素);**调粗/细就改这个**;0 = 关闭描边 |
| Outline Min / Max Width Meters | 0.03 / 0.6 | 近处/远处的外扩上下限 |
| Hover Tint / Tint Strength | (1.0, 0.86, 0.58) / 0.35 | 暖色倾向强度,0 = 无 |
| Enhanced Brightness | 0.25 | 增强模式下的亮度提升(+25%);**调亮度改这个** |
| Emission Intensity | 0.35 | 仅对启用了 `_EMISSION` 的材质生效(当前建筑无效) |
| Hover Brightness | 0.30(场景值) | 仅 Legacy 模式(Part 2/Editor)使用,未改 |
| Experiment Manager | 空 | 空 = 自动查找 |

## 5. 对 Part 2 的排除
Part 2 运行期间 `DesiredMode` 返回 `Legacy`:既不创建也不启用描边,颜色效果与原来逐位相同。未触碰 Part 2 Unexplored 传送限制、Continue 面板锁、Part 3 锁、选择规则、实验流程。

## 6. 修改文件
- 修改:`Assets/Scripts/InteractableHoverFeedback.cs`(字段全部新增、原字段名/类型不变,场景引用不会丢)。
- 新增:`Assets/Resources/Shaders/TargetHoverOutline.shader`、`Assets/Tests/GazeRecorder/TargetHoverHighlightTests.cs`、`Assets/Tests/EditMode/TargetHoverOutlineShaderTests.cs`、本文档。(`.meta` 由 Unity 导入时生成,请一并提交。)
- 备份:`Backups/TargetHoverOutline_20261107/`。
- 未改:`TargetSelectable`、`ExperimentManager`(本任务)、场景、预制体、材质、网格/碰撞体、TIN、NavMesh、ExplorationBoundary、GroundFallRecovery。

## 7. 测试(状态)
- C# 离线编译(Assembly-CSharp / GazeRecorder.Tests / EditModeTests):**通过**。
- PlayMode `TargetHoverHighlightTests`(A 激活:整栋所有 Renderer、无关建筑不变;B 退出还原;C 选择不受影响;D Part1/3 增强、Part2 旧效果、悬停中切换阶段/锁定/会话结束;E 双射线、子碰撞体冒泡、禁用/销毁/管理器销毁;F 共享材质不变、材质/对象不累积、原有 block 精确还原、自发光仅在关键字开启时写入):**NOT RUN**。
- EditMode `TargetHoverOutlineShaderTests`(着色器可按名称找到、无编译错误、属性齐全、立体实例化宏;BATI 材质全为 URP/Lit 且 `_EMISSION` 关闭):**NOT RUN**。
- 目视/画面验证、Quest Pro 性能:**NOT RUN**。

## 8. Quest Pro 性能考量
悬停期间每个子网格多一次绘制(只对被悬停的 1 栋建筑);无逐帧更新(组件没有 Update);一个共享 Material;描边 Renderer 带 MaterialPropertyBlock,只在悬停时参与(不影响 SRP Batcher 的其余物体);颜色提升带 block 的 Renderer 悬停时临时退出 SRP Batcher,退出后恢复。高面数建筑(数万三角形)悬停时会让该建筑多渲染一遍,请在设备上看帧时间。

## 9. 必须在 Unity / 头显中验证
1. 重新导入后查看 Console:`TargetHoverOutline.shader` 是否有编译错误(该着色器未经 Unity 编译,是最大风险);运行 EditMode `TargetHoverOutlineShaderTests` 与 PlayMode `TargetHoverHighlightTests`。
2. Game 视图/头显:Part 1、Part 3 射线指向目标建筑,描边是否清晰、纹理是否保留;L 形/复杂建筑外壳在硬边拐角处允许有极小缺口(硬法线网格的已知特性)。
3. 检查无 z-fighting/闪烁,左右眼一致(单通道实例化)。
4. 调整:看不清 → 增大 Outline Width(0.006)或 Enhanced Brightness;太刺眼 → 降低 Tint Strength / Enhanced Brightness。
5. 确认 Part 2 悬停仍是旧的细微变亮,无描边。
