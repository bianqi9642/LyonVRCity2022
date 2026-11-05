# NavMesh 屋顶 / TIN 孔洞 / 地面碰撞 审计与修复报告（2026-11-05）

**验证范围**：全部数据来自 Unity 2022.3.53f1 的 Editor / batch 模式（在项目的 APFS 克隆上运行，不干扰你已打开的 Editor）。**没有进行 Quest Pro / Omnideck 头显实测，不能据此宣称 VR 运行时安全。** 没有任何 git 提交。
原始报告（CSV + summary）：`Diagnostics/NavMeshGround/{before,after,seams,fix_log}/`（在 `Assets` 之外）。备份：`Backups/NavMeshGroundFix_20261105/`（场景、三个 NavMesh 资产、`NavMeshAreas.asset`、`TagManager.asset`，含 `SHA1SUMS.txt`）。英文技术说明：`docs/navmesh_ground_audit.md`。

---

## 0. Phase 1 只读审计：实际的生成与碰撞机制（已验证，非假设）

| 项目 | 实际情况 |
|---|---|
| NavMesh 生成 | 场景根节点 `NavMesh` 上**唯一一个 `NavMeshSurface`**（com.unity.ai.navigation 1.1.7）。Collect Objects = All Game Objects；Use Geometry = **Render Meshes**；Include Layers = **Everything**；Default Area = Walkable；Override Voxel/Tile 关闭；Min Region Area 2；数据 = `NavMesh-NavMesh 2.asset`（15.9 MB；同目录另两个 NavMesh 资产未被引用） |
| Agent 设置 | 来自 `ProjectSettings/NavMeshAreas.asset` 的 Agent Type 0：radius 0.3、height 2、max slope 60°、step height 0.75、voxel = radius/3 = 0.1、tile 256。（场景里旧式 `NavMeshSettings` 0.5/2/45/0.4 **不被该 Surface 使用**） |
| 源几何（修复前，共 1480 个） | Building 1454、TinGround 14、Bridge 7、Other 5（XR 手柄 Poke 点和 TunnelingVignette 等）；**全部 Walkable**；场景中只有一个 NavMeshModifier（`SouthRiver_TIN_GapPatch`，Ignore From Build） |
| 图层 | TIN 的 **MeshCollider 在 Ground(3)**，但 **TIN 的渲染网格在 Default(0)**；BATI 建筑渲染 + 碰撞也在 Default(0)（9 栋目标楼在 TargetPreview(6)）。**建筑与地面渲染网格同层 → 无法靠 Layer 区分地面和屋顶** |
| 建筑网格 | 每栋建筑一个 glTF 网格：`geometry0`（墙 + 屋顶合并）+ 子物体 `geometry0_1`（街面高度的平面 footprint 板）。屋顶与墙在同一网格内，只能按**物体层级**用 Modifier 处理 |
| TIN 地面 | Organic 6 块 + SouthRiver 补丁、Grid 6 块（8 个实例，其中 2 块是**完全重复**的实例）、Practice 6 块（White）。**场景保存时 `TIN_Textured`（Grid、Organic）和 `Practice Area` 全是 inactive** |
| 物理地面 | 同一批 TIN 网格直接作为 Ground 层 `MeshCollider`（单面）；桥面 PONT 同层 |
| 玩家控制 | 实际活动的 rig 是 `XR Interaction Setup/XR Origin (XR Rig)`：`CharacterController`（layer 2，h 1.361/r 0.1/skin 0.08/step 0.5）+ `AutoGroundAlign`（射线起点 base+0.1 m，向下 3 m，只看 Ground 层）+ XRI 连续移动 `OmnideckMove`（**useGravity=1**，模式 AttemptingMove）。`OVRCameraRig` 是其子物体（CLAUDE.md 中“活动 rig = OVRCameraRig”与实际不符） |
| 无恢复机制 | 没有任何 fall recovery；AutoGroundAlign 只在 base 不低于地表 0.1 m 以上时才能把 rig 拉回 |

---

## 1. 屋顶被烘焙进 NavMesh 的根本原因

* Surface 采集**所有层的所有 MeshRenderer**（Everything + Render Meshes），1454 个建筑源全是 Walkable，没有任何 Modifier；每个朝上且坡度 ≤ 60° 的屋顶三角形都被体素化为可走面。
* 证据（在同一 NavMesh 上，以 TIN 地面 XZ 足迹为参照，三个角点都高于地面 >0.5 m 才算“屋顶”）：**43 095 个多边形 / 309 953 m²（占 NavMesh 面积约 10.5%），1 712 个屋顶孤岛**。

## 2. TIN / NavMesh “孔洞”的根本原因

| 假设 | 结论 | 证据 |
|---|---|---|
| TIN 里有大的意外内部孔洞 | **基本没有** | 21 个 TIN 网格焊接（1 cm）后，内部边界环 194 个，合计 161 m²，其中 165 个只有 3–4 条边（发丝级裂缝）；仅 1 个 160 m² 的真实开口，位于 Practice TIN（2929.5, 160.6, 2465.3），高程等于河床 → 很可能是水面，**未填补，请确认** |
| TIN 方向/法线缺陷 | **是真正缺陷** | **573 个三角形（2 127 m²）绕序翻转**（法线朝下）；3 025 个退化三角形、5 108 个重复三角形（后者主要来自 2 个重复的 Grid 实例）；`Organic` 318、`Grid` 98、`Practice` 158 |
| 烘焙把孔洞放大 | 否 | Agent radius 0.3 / voxel 0.1 只在墙边侵蚀很窄一圈；未改任何烘焙参数 |
| NavMesh 缺口的构成（2 m 采样，5 424 个候选空洞样点） | 大多数是合理的 | 坡度 >60°：3 321；陡坡/建筑 2 m 内侵蚀：1 114 + 4；地形边缘 16；**翻转三角形 468**；未解释 501（抽查的一处是建筑足迹板区域，见 §10）。SouthRiver 补丁是你有意设置的 `ignoreFromBuild`（519 样点，从分母剔除） |

结论：不应填补“所有洞”。真正需要修复的是翻转三角形；陡坡、建筑墙边、河道补丁是合理的不可走区域。

## 3. 参与者下落的根本原因

1. **翻转（朝下）的 TIN 三角形对单面 MeshCollider 是“透明”的**：同一批网格直接做 Ground 碰撞体，CharacterController 站上去就穿过去。实测（按 rig 的真实 CharacterController 参数在 128 个朝下三角形上落脚）：**74/128 落地，54 个穿透（42%）**；AutoGroundAlign 射线 66/128 命中，62 个没有命中。对照组（正常三角形）150/150。
2. 采样中物理地面缺失 **492 个样点**，全部归因于朝下三角形。
3. 一旦 rig 掉到地表以下 >0.1 m，`AutoGroundAlign` 的射线（base+0.1 m 起点）永远打不到地面 → **无法恢复**，与“无法恢复”的现象一致。
4. 另外：场景保存时地形组是 inactive 的（无地形 = 无任何 Ground 碰撞体）。这不是代码缺陷，但**必须确认你实际运行/构建时地形处于激活状态**（见 §10）。

---

## 4. 修改清单（文件 / 对象 / 资产）

**新增**
* `Assets/Models/TIN/Repaired/*.asset`（18 个网格，8.3 MB）：派生的“地面碰撞 + 导航代理”网格。
* `Assets/Scenes/LyonVRVity35/NavMesh-NavMesh_GroundOnly.asset`：新 NavMeshData（11 MB）。旧的 `NavMesh-NavMesh 2.asset` **原样保留**。
* `Assets/Scripts/GroundFallRecovery.cs`：次级安全网（挂在 `XR Interaction Setup/XR Origin (XR Rig)`）。
* `Assets/Editor/NavMeshAudit/*`：诊断与修复工具（菜单 `Tools > NavMesh Ground Audit`）。
* `Assets/Tests/EditMode/GroundNavRegressionTests.cs`：7 个 EditMode 回归测试（放在已有的 `GazeRecorder.EditModeTests` 程序集）。
* 场景根节点 `NavMeshGroundProxy`（18 个子物体）。

**修改（`Assets/Scenes/LyonVRVity35.unity`）** —— 语义比对：0 个文档被删除，92 个新增，40 个改动：
* 20 个 Ground 层 TIN `MeshCollider.m_Mesh` → 修复网格；
* `NavMesh` 物体上的 NavMeshSurface：Include Layers `Everything → Default+Ground+TargetPreview (73)`，NavMesh Data → 新资产；
* 新增 16 个 NavMeshModifier（见 §5）+ `GroundFallRecovery`；
* Unity 重新序列化时给两个既有脚本补入了其新增字段的默认值（`autoCreateFaceExpressions`、`recordDiagnostics`、`part3AudioRecording` 等），无行为变化。

**未改**：glTF 导入资产、视觉 TIN/BATI 网格与材质、图层（未全局改动任何物体的 Layer）、`AutoGroundAlign`、`ExperimentManager`、记录器、teleport、探索边界、XR 加载器/OpenXR 设置、`ProjectSettings`（`Open XR Package Settings.asset` 的既有改动不属于本次）。

## 5. 修改前后的 NavMesh 配置

| 项 | 修改前 | 修改后 |
|---|---|---|
| Include Layers | Everything | Default + Ground + TargetPreview（mask 73） |
| Collect / Use Geometry / Default Area / Agent / Voxel / Tile / Min Region | All / Render Meshes / Walkable / Humanoid 0.3,2,60°,0.75 / 0.1 / 256 / 2 | **不变** |
| 源：建筑 | 1454 个，Walkable | 2028 个，**Not Walkable**（仍作为障碍参与烘焙；6 个根 + 1 个 inactive 的 `BATI_Discard`） |
| 源：TIN | 14 个可视渲染网格（含翻转三角形） | 18 个修复代理网格；可视 TIN 通过 `ignoreFromBuild` 不再作为源 |
| 源：Bridge / Other | 7 / 5（含 XR rig 零件） | 7 / 0（`XR Interaction Setup` 上加了 ignoreFromBuild） |
| NavMeshModifier | 1 个 | 17 个：`Grid Area/TIN_Textured`、`Organic Area/TIN_Textured`、6 个 `Practice Area/gml_*`、`XR Interaction Setup`（均 Ignore From Build + Apply To Children）；`Grid Area/3EME`、`3EMEPeripheral`、`6EMEPeripheral`、`Organic Area/5EME`、`5EMEPeripheral`、`Practice Area/2EME`、`BATI_Discard`（均 Override Area = Not Walkable + Apply To Children）；既有的 `SouthRiver_TIN_GapPatch` 原样保留 |
| NavMeshData | `NavMesh-NavMesh 2.asset` | `NavMesh-NavMesh_GroundOnly.asset` |

为何用 **Not Walkable 而不是 Ignore From Build** 处理建筑：Ignore 会让建筑从烘焙中消失，建筑足迹内的 TIN 地面就会与街道连通（NPC 会穿楼）；Not Walkable 让建筑继续作为障碍，只是其表面（屋顶）永远不可走。
为何没有新增“WalkableGround”图层：TIN 渲染网格与建筑同在 Default 层，改层需要大范围改动既有物体且仍分不开同一网格里的屋顶/墙，因此用层级 Modifier + 独立代理网格实现；仅用 Include Layers 做了加固。

## 6. 网格修复方法

对每个 TIN 瓦片网格（局部空间）：① 以 1 mm 焊接顶点；② 删除退化（面积 <1e-6 m²）和重复三角形；③ 用该实例真实的 localToWorld（含镜像行列式）计算世界法线，**法线朝下（ny < -1e-3）的三角形交换两个索引使其朝上**，近乎垂直的面（|ny|<1e-3）不动；④ 不移动任何顶点（高程和坡度完全保持，不填平、不外推）。结果：翻转 637 个（含 Practice 区），丢弃退化/重复 2 862 个；原 glTF 不动，生成独立资产；重复运行幂等（场景字节级一致、NavMeshData GUID 稳定）。
真正的内部开口（Practice 160 m²）与瓦片重叠（§10）**不自动填补**。

## 7. 地面 Collider 变化

* 20 个 Ground 层 TIN `MeshCollider` 使用修复网格（桥面、SouthRiver 补丁不变；补丁仍无导航但保留物理地面）。
* 修复后：2 m 采样的物理地面缺失 **492 → 0**；CharacterController 在 128 个原翻转三角形上 **128/128 落地**，AutoGroundAlign 射线 **128/128 命中**。
* 新增 `GroundFallRecovery`（次级安全网，**不替代修复**）。激活条件（全部满足）：组件启用且已有“安全位置”（地面射线命中且 rig 底部在地表上，下沉 ≤0.1 m）；连续无地面支撑 ≥0.6 s（非缩放时间）；并且 rig 比安全位置低 >1.0 m，或头顶高处射线发现 >0.6 m 高的 Ground 表面（在地下）。**不会触发**：单帧位移 >2.5 m（视为 `TeleportPlayerTo`/河岸传送，重置安全位置并冷却 2 s）、冷却期内、没有安全位置时。恢复时只写 rig 的 transform（禁用再启用 CharacterController），记 `Debug.LogWarning` 并触发 `OnFallRecovered` 事件；不碰实验流程、记录器、NavMesh、AutoGroundAlign。轨迹数据中该时刻会出现一次位置跳变——数据分析时请据 log/事件处理。

## 8. 诊断结果与覆盖统计（2 m 采样，垂直容差 0.5 m）

| 指标 | 修改前 | 修改后 |
|---|---|---|
| NavMesh 总多边形 / 面积 | 108 041 / 2 958 449 m² | 64 698 / 2 642 348 m² |
| 高于地面 >0.5 m 的“屋顶”多边形 | **43 095（309 953 m²）** | **0** |
| 屋顶孤岛（多数多边形高于地面的连通块） | 1 712 | **0**（总连通块 3 335 → 1 631） |
| 建筑作为 **Walkable** 源 | 1 454 | **0** |
| TIN 足迹样点 / 分母（剔除建筑下方 84 704 与有意的 SouthRiver 补丁 519） | 670 403 / 585 180 | 同 |
| NavMesh 覆盖（分母内） | 98.35% | **98.45%**（剔除建筑边/侵蚀带后 99.07% → 99.16%） |
| 候选空洞样点（区域数） | 5 424（1 398） | 4 860（1 323）；其中“翻转三角形”原因 468 → 3 |
| 物理 Ground 碰撞覆盖 | 99.92%（缺 492） | **100%（缺 0）** |
| 有 NavMesh 但无物理地面 | 1 | 0 |
| 有物理地面但无 NavMesh | 9 170（陡坡/墙边/建筑内等） | 9 094 |
| CharacterController 在原翻转三角形落地 | 74/128 | **128/128**（对照 150/150） |
| AutoGroundAlign 射线命中 | 66/128 | **128/128** |
| 连通性（同一批点对；`PathComplete`） | within_grid 35/40，within_organic 35/40，grid↔organic 27/40，起点/过渡锚点 21/28 | 35/40，35/40，28/40，21/28（无退化，grid↔organic +1） |
| NPC 路径点（987 个）有 NavMesh（0.5 m 内） | 944 | 944 |
| 建筑足迹下的 NavMesh 样点 | 78 721 | 76 716 |

说明：起点/过渡锚点的 7 个 `PathPartial` 全部与 Practice 区的 `StartPoint`（2588.9, 167.3, 2630.0）配对，该练习区与主城是分离的 NavMesh，修复前后一致；Grid↔Organic 之间本来就隔河（靠传送点），未强制连通。

## 9. 测试与结果

* **EditMode**（`-runTests -testPlatform EditMode -assemblyNames GazeRecorder.EditModeTests`）：修复后场景 **7/7 通过**（含既有的 `EveryWiredStartAnchor_StandsOnTheRealGroundCollider…`，起点高度仍在 2 cm 容差内）。在**原场景**上，4 个新增几何测试按预期失败（屋顶多边形 43 030、CC 穿透 28/80、碰撞体仍用原网格、建筑 2028 个可走源）——突变检查有效；合成场景下的 `GroundFallRecovery` 两个测试（恢复 / 忽略传送）在写测试时发现并修复了一个真实缺陷（“最后安全位置”曾记录下沉中的位置）。
* **重跑幂等性**：再次执行修复 → 场景字节级相同、NavMeshData GUID 相同、测试 7/7、诊断数字完全一致（证明“重新生成 NavMeshData 后排除机制依然有效”）。（`Diagnostics/NavMeshGround/fix_log/fix_log.txt` 是这次**第二次**运行的日志，所以其中“TIN colliders re-assigned: 0”；首次运行重新指派了 20 个碰撞体、翻转 637 个三角形。）
* **未做**：PlayMode 完整实验流程、鼠标点击 `OnMouseDown`/传送点冷却的交互回归、NPC 行走/卡顿观察、任何头显测试。teleport 与实验流程代码**未改动**，AutoGroundAlign / ExperimentManager 未改，但这是“未改代码”而不是“已回归测试”。

## 10. 剩余风险与必须的头显 / 人工验证

1. **（最高优先）确认地形激活状态**：保存的场景里 `Organic Area/TIN_Textured`、`Grid Area/TIN_Textured`、`Practice Area` 均 inactive（`TIN_Textured` 自提交 e7ebbfac / 2026-11-01 起；`Practice Area` 更早即如此）。未激活 = 没有任何 Ground 碰撞体，参与者必然下落。我没有改动激活状态（超出范围，且不清楚你是运行时手动激活还是有意为之）。请确认实验/构建时它们是激活的。
2. 建筑足迹内部（TIN 在屋顶之下）仍有 ~76 700 个采样格的 NavMesh（孤立的室内孤岛，仅 ~376 个多边形与街道网络相连），与修复前同类，未处理；若出生点/徘徊点落入其中会出现在楼内。`NavMesh 内部孤岛` 清单见 `D3_navmesh_islands.csv`。
3. **瓦片重叠**（`seams/D2b_tile_overlaps.csv`）：29 324 个样点（117 296 m²）被两块瓦片同时覆盖；Grid 的两个重复实例互相完全一致，但 Grid↔Organic 重叠区垂直差平均 0.31 m、最大 8.2 m（2 559 个样点 >0.25 m），两层表面都有碰撞体和 NavMesh，AutoGroundAlign 取最近命中。涉及 GridArea/OrganicArea 过渡，未改动，需要人工判断。
4. Practice TIN 的 160 m² 开口（2929.5, 160.6, 2465.3）：确认是水面而非缺陷。
5. NavMesh 中 3.3 k 样点因 >60° 陡坡不可走，是有意的行人限制（未放宽坡度）。
6. 头显必测：Quest Pro/Omnideck 上 rig 追踪高度与脚底贴地、重力手感、穿过原翻转三角形位置不再下落、`GroundFallRecovery` 在真实“假想下落”时的触发舒适度（以及它引起的位置跳变在 TrajectoryRecorder 数据中的表现）、传送点/起点传送后不被误触发、NPC 行走无卡顿（NavMesh 变化：屋顶消失、部分室内/建筑足迹板区域消失）。
7. 新 NavMesh 资产与 18 个代理/修复网格会进包（约 19 MB）；代理 MeshRenderer 无材质、不投影，不渲染。

## 附：请在 Unity Inspector 里手动核对

1. Hierarchy 根节点 `NavMesh` → **Nav Mesh Surface**：Agent Type = Humanoid；Collect Objects = **All Game Objects**；**Include Layers = Default, Ground, TargetPreview**；Use Geometry = **Render Meshes**；Default Area = Walkable；Override Voxel Size / Override Tile Size 关闭；Min Region Area = 2；**NavMesh Data = `NavMesh-NavMesh_GroundOnly`**。回滚：把 NavMesh Data 改回 `NavMesh-NavMesh 2`，Include Layers 改回 Everything。
2. `Window > AI > Navigation > Agents`：Humanoid radius 0.3 / height 2 / step height 0.75 / max slope 60（应与修改前相同）。
3. Hierarchy 根节点 `NavMeshGroundProxy`（18 个子物体）：每个 MeshFilter 的 Mesh 为 `Ground_gml_…`；Mesh Renderer 的 Materials Size = 0，Cast Shadows = Off。
4. `Grid Area/TIN_Textured`、`Organic Area/TIN_Textured`、`Practice Area/gml_*`（6 个）、`XR Interaction Setup`：**Nav Mesh Modifier → Ignore From Build ✓，Apply To Children ✓**。
5. `Grid Area/3EME`、`Grid Area/3EMEPeripheral`、`Grid Area/6EMEPeripheral`、`Organic Area/5EME`、`Organic Area/5EMEPeripheral`、`Practice Area/2EME`：**Nav Mesh Modifier → Override Area ✓，Area Type = Not Walkable，Apply To Children ✓**。
6. 20 个 Ground 层 TIN 碰撞体（如 `Grid Area/TIN_Textured/gml_…`）→ **Mesh Collider → Mesh = `Ground_gml_…`**（路径 `Assets/Models/TIN/Repaired`）。
7. `XR Interaction Setup/XR Origin (XR Rig)`：新增 **Ground Fall Recovery**（Ground Mask 留空即自动使用 Ground 层；`Enable Debug Logs` 可临时打开）；`Auto Ground Align` 与 `Character Controller` 设置应保持不变。
8. 修复前后对比：运行 `Tools > NavMesh Ground Audit > Run diagnostics (D1-D5, current scene)`（输出在 `Diagnostics/NavMeshGround/manual_*`）；复现修复：`Tools > NavMesh Ground Audit > Apply ground fix …`（幂等）。
9. Editor 需要**重新加载磁盘上的 `LyonVRVity35.unity`**（不要把内存中的旧版本保存覆盖）。
