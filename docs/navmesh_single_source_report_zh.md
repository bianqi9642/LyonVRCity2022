# NavMesh 地面源简化报告：只用修复后的 TIN（2026-11-07）

验证均为 Unity Editor（克隆项目）离线结果，**没有 Meta Quest Pro 实机验证，没有提交**。数据：`Diagnostics/NavMeshGround/single_source/`、`rooftop_cylinders/`。备份：`Backups/SingleSource_20261107/`（场景、旧 NavMeshData、测试文件）。

## A. 地面 Mesh 诊断
1. **`gml_` 物体是否都换成了修复网格？** 不是。上一轮只改了 **MeshCollider.sharedMesh**。逐个审计（`tin_mesh_references.csv`）：20 个 TIN 瓦片全部是 **B 类：仅 MeshCollider 引用修复网格**，而其子物体 `geometry0` 的 **MeshFilter 仍引用原始 glTF 网格**；SouthRiver 补丁（1 个）是 C 类（两者都是原件，本来就不修）。没有 A 类、D 类。
2. 仍引用原始网格的：20 个瓦片的 **MeshFilter**（Grid 8 个实例含 2 个完全重复的实例、Organic 6、Practice 6）。**MeshCollider 的 20 个引用都是修复网格**（Ground_gml_*，18 个唯一资产）。
3./4. MeshFilter 与 MeshCollider 是两个独立引用——正是这个差异造成“两套地面”。
5. **为什么两套都被采集？** NavMeshSurface 使用 Render Meshes：(a) 瓦片自己的 `geometry0` 渲染器（原始 TIN，含翻转三角形）本来就是源；(b) `NavMeshGroundProxy` 的 18 个无材质渲染器（修复网格）在代理根节点**激活**时也是源。9 个 TIN 忽略 Modifier 保持禁用，所以两者都不会被排除。注意：**保存的场景里 `NavMeshGroundProxy` 根是 inactive**，只有在它被激活时（我之前的诊断工具为模拟“全部激活”而在内存中激活了它）才会出现两套；按保存状态烘焙时只采集原始 TIN（翻转三角形造成的洞又回来：候选空洞 4 862 → 5 466，Practice 覆盖 98.91 → 98.67%）。
6. **网格几何**：原始网格与修复网格的“非退化唯一三角形”完全一致（onlyInVisual = onlyInRepaired = 0），差别只是**绕序**（原始共 573 个朝下三角形，修复 0 个）；代理使用的正是碰撞体那个资产（同一 asset）。

## B. 简化后的 NavMesh 配置
做法（最少改动，不新增层/修饰器/脚本）：**让瓦片自己的 MeshFilter 也引用修复网格资产**，于是 `gml_` 物体本身就是唯一的修复地面源，代理不再参与烘焙。
* 修复资产改为**保留 UV 与法线**（原来只有位置）：`GroundNavFix.BuildRepaired` 现在按“位置+法线+UV”焊接，仅重排朝下三角形并为它们生成独立顶点（法线转朝上）。18 个资产**原地重写，GUID 不变**，所以 20 个 MeshCollider 引用原样保留。验证（`probe_vis.txt`）：18/18 个瓦片的顶点通道（Position/Normal/[UV0]）与原件相同，子网格数 1，材质不变，位置与 UV 与原件逐三角形一致；差异只有被丢弃的退化/重复三角形和少量重绕三角形（法线转上）；运行时内存 41 → 39 MB。
* 场景：20 个瓦片 `geometry0` 的 `MeshFilter.m_Mesh` 改为同一修复资产（20 条 prefab 覆盖），NavMeshSurface 的 NavMesh Data 指向新资产 `Assets/Scenes/LyonVRVity35/NavMesh-NavMesh_RepairedTIN.asset`。语义比对：0 文档删除、0 新增、23 个改动（20 个 prefab 覆盖 + NavMeshSurface 引用 + ExplorationBoundary / GroundFallRecovery 上一轮新增字段的默认值）。
* **保留的源**：18 个修复资产对应的 20 个 `gml_` 瓦片 `geometry0`（Grid 8 含 2 个重复实例 / Organic 6 / Practice 6）、7 个桥面、建筑（Not Walkable）。
* **排除的冗余源**：`NavMeshGroundProxy`（保存状态为 inactive，烘焙时也保持关闭）；原始 glTF 网格不再是任何源。
* **`NavMeshGroundProxy` 还需要吗？** 不需要。我**没有删除**它和它的 18 个子物体（也没有删资产）；你确认后可手动删除。注意：如果以后把它设为 active 再烘焙，会重新出现重复源。
* **9 个 TIN Modifier 全部仍为 Disabled**：烘焙工具在前后比较所有 Modifier 的 enabled 状态（9 个禁用、全部不变），新代码不写 `enabled`。建筑 Not Walkable Modifier、XR 忽略 Modifier 不变。
* **NavMeshSurface 最终配置**：Collect Objects = All Game Objects；Include Layers = Default + Ground + TargetPreview（73）；Use Geometry = Render Meshes；Agent/Voxel/Tile/Min Region 不变；NavMesh Data = `NavMesh-NavMesh_RepairedTIN`（旧的 `NavMesh-NavMesh_GroundOnly.asset` 原样保留，回退只需把该字段改回去）。

### 修复后每区域结果（2 m 采样；旧 = 上一版已保存的 GroundOnly 资产）
| 区域 | 修复地面源数 | 源三角形 | NavMesh 多边形（旧 → 新） | 面积 m²（旧 → 新） | 采样覆盖（旧 → 新） |
|---|---:|---:|---:|---|---|
| PONT（桥面） | 7（桥，非 TIN） | 68 | 122 → 122 | 13 213 → 13 213 | 96.87% → 96.84% |
| Practice | 6 | 35 010 | 15 403 → 15 433 | 777 344 → 777 342 | 98.91% → 98.91% |
| Organic | 6 | 153 773 | 30 230 → 30 402 | 975 280 → 975 326 | 97.75% → 97.75% |
| Grid | 8（含 2 个重复实例） | 46 879 | 18 825 → 18 825 | 876 459 → 876 459 | 98.81% → 98.81% |
* 每个地面表面只采集一次（除下面两个重复实例）；多边形数/面积/覆盖与旧版一致。
* 重新烘焙（同样的源）多边形数在 Practice 会有 ±8% 的碎片化差异（面积不变），属烘焙网格切分差异。
* **连通性（同一批点对 PathComplete）**：同区 Grid 35/40 → 35/40，同区 Organic 35/40 → 35/40，Grid↔Organic 28/40 → 29/40，起点/过渡锚点 21/28 → 21/28；NPC 路径点 944/987 → 944/987。
* **屋顶**：建筑可走源 = 0；建筑屋顶多边形 = 0。新资产里有 **11 个屋顶样多边形**，它们**不是建筑**，是屋顶上那个传送点圆盘（见 `docs/rooftop_cylinders_report_zh.md`）。旧资产（11 月 5 日烘焙时该传送点未被采集）没有它们；两者的差别只是烘焙时场景里该对象是否参与。
* 对照：按保存状态烘焙但不做本次修改（只有原始 TIN）：候选空洞 5 466、覆盖 Grid/Organic/Practice = 98.77/97.69/98.67%——即翻转三角形造成的缺口；本次修改后恢复到 4 865 / 98.81/97.75/98.91%。

## D. 修改摘要
* **改动文件**：`Assets/Editor/NavMeshAudit/GroundNavFix.cs`（修复网格保留 UV/法线；新增 `UseRepairedTin`，菜单 `Tools > NavMesh Ground Audit > Use repaired TIN as the single NavMesh source…`）；新增只读工具 `GroundNavAuditMeshRefs.cs`、`GroundNavAuditRooftopCylinders.cs`、`GroundNavAuditSourceReport.cs`（改进）；测试 `GroundNavRegressionTests.cs`（改为单源架构断言）。
* **场景对象**：20 个 `gml_*/geometry0` 的 MeshFilter.m_Mesh；NavMeshSurface 的 NavMesh Data。**没有**改任何 Modifier、层、激活状态、传送逻辑、ExplorationBoundary/GroundFallRecovery 代码。
* **资产**：18 个 `Assets/Models/TIN/Repaired/*.asset` 被重写（GUID 不变；体积 8.3 → 39 MB，因为现在保留 UV/法线，未入 LFS——注意仓库增长约 31 MB）；新增 `NavMesh-NavMesh_RepairedTIN.asset`（11 MB）；**未删除任何资产/对象**。
* **回归测试（克隆项目，Editor）**：EditMode **23 个中 22 个通过**；失败的 1 个是旧的 `EveryWiredStartAnchor_…`（与以前相同，按你的要求未调查）。突变检查：在改动前的场景上，新增的“MeshFilter 与 MeshCollider 引用同一修复资产”“采集到修复瓦片”测试按预期失败。PlayMode（`GazeRecorder.Tests`，克隆项目）：363 个中 359 通过、4 失败——`Scene_EveryTerrainCollider…` 与一个 `AutoGroundAlign_…`（2 mm 容差）在改动前就失败（已用修复前脚本复现）；另一个 `AutoGroundAlign_…` 是容差边缘的偶发；`Uncertain_Detour_…` 在整套运行时失败，但单独在最终状态和改动前基线上各跑一次都通过（负载下的偶发）。这些都不涉及本次改动的代码。
* **剩余风险**：(1) 没有 Quest 实机验证；(2) 修复资产变大；翻转三角形现在在渲染中可见（原先背面剔除时是“隐形洞”），法线已转上，但肉眼外观需在 Editor 里确认；(3) 两个完全重复的 Grid 实例（`Grid Area/TIN_Textured/gml_29317c3e…` 与 `gml_c9b1830c…` 各 2 份）仍会各被采集两次（共 +5 111 三角形），我没有删除，建议你手动删掉重复的那份；(4) 屋顶圆盘的 11 个多边形取决于你对该传送点的决定；(5) `NavMeshGroundProxy` 仍在场景中（inactive），不要在烘焙时激活。
* **如何重复烘焙**：确认 `NavMesh` 对象和区域组为激活、`NavMeshGroundProxy` 保持 inactive → NavMeshSurface Inspector 点 Bake，或 `Use repaired TIN as the single NavMesh source…`（幂等，不改 Modifier 的 enabled）。
