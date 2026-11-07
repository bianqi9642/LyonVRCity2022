# 屋顶圆柱体排查报告（只读，未删除/禁用/修改任何对象）

数据：`Diagnostics/NavMeshGround/rooftop_cylinders/`（CSV：`rooftop_cylinders_all_candidates.csv` 全部 582 个候选；`rooftop_cylinders_confirmed_or_likely.csv` 确认/疑似屋顶项；`summary.txt`）。
工具：`Tools > NavMesh Ground Audit > Rooftop cylinders (read-only window)` 打开窗口，对每个条目有 **Ping / Select / Select anchor root** 按钮（只改变 Editor 选择，不改场景）。批处理：`-executeMethod NavMeshAudit.RooftopCylinders.RunBatch -auditBake 1`。

## 1. 扫描范围与方法
* 扫描整个已保存的 `LyonVRVity35`：**4 550 个带网格的物体**（含未激活物体、嵌套 prefab 实例）。
* 圆柱判定**不看名字**：网格顶点投影到任一轴的垂直平面后 ≥80% 落在同一圆上，且 ≥60% 在两个端面上（对不可读网格才退化为名字）。排除城市自身的建筑 / 地形 / 桥面网格后，共 **582 个圆柱候选**：
  * **576 个**：`TeleportationArea/**/Teleport Anchor Variant/Visuals`（网格 `pCylinder3`，来自 XRI 示例 `Primitive_Cylinder.fbx`，尺寸 3.0 × 0.15 × 3.0 m 的扁圆盘，缩放 (3, 0.75, 3)）；
  * **6 个**：XR 手柄 `Poke Point/Cylinder`（0.017 × 0.011 × 0.072 m，在 XR 装备下，与屋顶无关）。
* 屋顶判定用建筑几何（2 028 个 BATI 网格的非垂直三角形，不依赖 Collider，因为部分建筑没有朝上的屋顶碰撞面）：在圆柱中心 XZ 向下找圆柱底面以下最高的建筑三角形 = 屋顶高度；屋顶须比该处地面高 ≥2.5 m；再取圆盘边缘 8 点 + 中心共 9 点检查是否都落在同一屋顶面（±0.5 m）。

## 2. 结果
| 分类 | 数量 |
|---|---:|
| Confirmed rooftop cylinder（中心在屋顶、|偏差|≤1 m、≥8/9 点在屋顶） | 0 |
| **Likely rooftop cylinder** | **1** |
| Near a rooftop, not confirmed | 0 |
| Not on a rooftop | 581 |

其余 575 个传送点圆盘全部贴地（相对地面 −0.15 … +0.24 m，中位数 −0.05 m），是正常的地面级传送标记，**保留**。

## 3. 唯一的屋顶圆柱（Likely）
| 字段 | 值 |
|---|---|
| 完整层级路径 | `TeleportationArea/OrganicTeleportPoint/Unexplored/Teleport Anchor Variant/Visuals`（该路径下共有很多同名物体；以坐标为准，CSV 中 ID 为 530 左右，排序可能微变） |
| 世界坐标 | **(2120.256, 189.653, 746.289)**；旋转 (0,0,0)；缩放 (3, 0.75, 3)；包围盒 3.000 × 0.150 × 3.000 m |
| 父级 / prefab | 父级 `Teleport Anchor Variant`；prefab：`Assets/Prefabs/Teleport Anchor Variant.prefab`（基于 XRI 示例 `Teleport Anchor.prefab`） |
| 所在建筑 | `69385AH45`（`Organic Area/5EME/69385AH/69385AH45/geometry0`），屋顶 Y = 189.672，该处地面 Y = 168.068（**屋顶高出地面 21.6 m**） |
| 圆盘底面相对屋顶 | −0.019 m（正好放在屋面上）；圆盘边缘 9 点中 5 点在屋面上（圆盘一部分悬出屋檐，所以是 Likely 而非 Confirmed） |
| NavMesh | 被采集为可走源；烘焙后圆盘上方生成 **11 个可走多边形（约 2.8 m²，Y≈189.8）**——这就是上次报告里的 11 个“屋顶多边形” |

### 它是纯视觉装饰，还是传送必需？——**不是纯视觉**
`Teleport Anchor Variant` 的结构（本场景实例，已逐项读取）：

| 对象 | 组件 | 作用 |
|---|---|---|
| `Teleport Anchor Variant`（根） | `TeleportationAnchor`、`TeleportPointVisual` | XRI 传送锚点；`Teleport Anchor Transform` → 子物体 `Anchor`；`Colliders` 列表为 **空** |
| `Anchor`（子） | 仅 Transform | 传送落点（`m_TeleportAnchorTransform`） |
| `Visuals`（子） | `MeshFilter`(pCylinder3) + `MeshRenderer`(Teleport_Ordinary_Cyan) + **`MeshCollider`(convex，非 trigger)** | 圆盘外观 **和** 交互碰撞体 |

因为 `TeleportationAnchor.Colliders` 为空，XRI 会自动使用子物体上的碰撞体——**`Visuals` 上的 MeshCollider 就是唯一的交互碰撞体**。（另有 4 个河岸传送点的 `RiverTeleportPoint` 也挂在 `Visuals` 上，同样依赖这个 Collider。）因此：
* **不要删除 `Visuals`**，更不要删除整个 `Teleport Anchor Variant`（除非你本来就不想在这栋楼的屋顶上有传送点）。
* 真正“纯视觉”的只有 `Visuals` 上的 **MeshRenderer** 组件。我在克隆里验证过：**只禁用该 MeshRenderer** → 圆盘不再显示，屋顶上的 11 个多边形降为 **0**，MeshCollider 仍在，传送功能不受影响；整个 `Visuals` 停用也得到 0，但会同时失去交互碰撞体。（验证在内存中完成，没有保存。）
* `TeleportPointVisual` 的 `visualRenderer` 为空，回退到“与 Collider 同对象的 MeshRenderer”，没有任何其他脚本序列化引用 `Visuals`（扫描全部 MonoBehaviour 的序列化引用，结果为空）。

### 建议
| 对象 | 建议 | 理由 |
|---|---|---|
| 这个屋顶圆盘（Visuals） | **Review**（由你决定） | 不能删 `Visuals`。若只想去掉屋顶上的可走补丁：只关闭 `Visuals/MeshRenderer`；若这个“Unexplored”传送点本不该在屋顶上（参与者可以传送到屋顶）：整体删除/移动 `Teleport Anchor Variant` 根；若是有意放置：保留 |
| 其余 575 个圆盘 | **Keep** | 贴地的正常传送标记 |
| 6 个 Poke Point 圆柱 | **Keep** | XR 装备部件 |
| “Safe to delete visually” | **0 项** | 所有 `Visuals` 都带交互 MeshCollider |

## 4. 说明
* 576 个圆盘都会被当作可走源（它们与地面共面，无害），只有屋顶这个在地面之上，所以只有它产生孤立的屋顶多边形。
* 所有结果为 Editor 几何分析，未做 Quest 实机验证；脚本仅在 Editor 中读取，不修改场景。
