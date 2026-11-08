# GazeOrigin 坐标与 ±0.0325 m 偏移专项调查（只读）

范围：只读调查，未修改任何代码、场景、预制体、配置或参与者数据；本文件是唯一写入物。
数据：S3 = `20261106T145652Z_0980aa930c32`（subject_id = 4）。第 2 节为回答“不同参与者/会话是否不同”，**只读取** 其他已有会话 `gaze_diagnostics.csv` 中的 3 个列做对比（见 §2.3），不做其他分析。
代码行号为当前工作树；本次会话未提交的眼动有效性修改（`EyeTrackingLifecycle.cs`、`GazeRecorder.cs` 的有效性逻辑等）**没有改动任何原点/方向的生成代码**。S3 由 HEAD 代码录制，原点逻辑二者相同。
证据标签：**Confirmed** / **Strongly supported** / **Uncertain** / **Not testable**。**全部为 Editor 离线分析，没有头显实测。**

---

## 0. 结论摘要

1. `GazeOrigin` 不是测得的眼睛位置。录制的单眼原点 = `Camera.main`（XRI “Main Camera”）的世界姿态 ⊕ 一个**在头部坐标系中恒为 (∓0.0325, 0, 0) m 的向量**；合成原点（`combined_origin`）= 两眼原点的均值 = **HMD 位置本身**（侧向偏移互相抵消）。 **[Confirmed]**
2. 该偏移**只有侧向分量**，竖直与前后为 0（S3 实测 Y、Z 中位 0.07 / 0.12 mm，SD 来自帧内时序抖动，不是系统偏移）。 **[Confirmed]**
3. 偏移**不来自项目或 SDK 的 C# 代码**（全工程搜索 `0.0325`、`0.065f` 均无命中），而来自 `OVRPlugin.GetEyeGazesState` 返回的位姿（原生二进制，无法检查）。S3 中提供者的双眼间距恒为 **65.000 mm（SD 0）**，而同一会话运行时报告的 IPD 为 **66.70 mm**，渲染眼锚间距也是 66.7 mm——所以它**不随头显 IPD 设置变化**。 **[Confirmed 数据；“为什么是 65 mm”Not testable]**
4. 在 3 个 subject_id（1、2、4）共 7 个会话中，提供者偏移全部是 ±0.03250 m（SD ≤ 0.04 mm），而运行时 IPD 在 66.70–68.21 mm 之间变化 → 在这些数据里它**是全局常量**，不是每位参与者的测量值。 **[Confirmed（这些会话）；对未录制的参与者 Uncertain]**
5. 就“光线与 NPC/ROI 相交”而言：**±0.0325 m 本身对合成射线几乎不造成误差**（对称抵消），只对“单眼射线”造成恒定的 32.5 mm 平行位移（与距离无关）。更大的、已测得的问题是 **168 + 57 行（7.3 %）的原点与同帧 HMD 不一致，达 3–20 cm**（§5）。 **[Confirmed 几何与 S3 测量；真实命中误差 Not testable（没有已知注视目标）]**
6. S3 中左右眼注视方向的夹角恒为 3.05°（几乎与命中距离无关，相关系数 −0.011），即每只眼的射线都“指向前方约 1.22 m 处”。因此**单眼射线的汇聚点不是测量值**，只应使用合成方向。 **[Confirmed 现象；成因 Uncertain]**

---

## 1. 精确的原点与偏移（第 1 部分）

### 1.1 数据流与精确代码位置

| 步骤 | 内容 | 位置 |
|---|---|---|
| 提供者读取 | `OVRPlugin.GetEyeGazesState(Step.Render, -1, ref state)`；取 `EyeGazes[(int)Eye]`；位姿在 OVRPlugin 跟踪空间 | `Library/PackageCache/com.meta.xr.sdk.core@201.0.0/Scripts/Movement/OVREyeGaze.cs:160-172` |
| 有效/置信度门 | `IsValid` 为假则 `return`（保留旧 transform 与旧 Confidence）；`Confidence < ConfidenceThreshold(0.5)` 同样 `return` | 同上 `:165-170` |
| 空间转换 | `TrackingMode == WorldSpace` → `pose.ToWorldSpacePose(Camera.main)` | 同上 `:173-181`；`OVRCommon.cs:119-135`（`ToWorldSpacePose`），`:145-160`（`ToHeadSpacePose`） |
| 写入 | `transform.position = pose.position`；`transform.rotation = CalculateEyeRotation(pose.orientation)`（视图变换为单位旋转、初始偏移为单位，见 1.3） | 同上 `:183-191`、`:194-220` |
| 项目读取 | `sample.origin = eyeGaze.transform.position; sample.direction = eyeGaze.transform.forward` | `Assets/Scripts/GazeRecorder.cs:687-688` |
| 可信度门/回退 | `EyeGazeOrigin.Resolve(providerWorld, anchorWorld, 0.12 m, …)` | `GazeRecorder.cs:705`；`Assets/Scripts/Capture/EyeGazeOrigin.cs:42, 70-88` |
| 合成射线 | 两眼有效：`origin = (L+R)/2`，`dir = normalize(L.dir + R.dir)` | `GazeRecorder.cs:1175-1177` |
| ROI 射线 | `Physics.Raycast(combinedOrigin, combinedDirection, …)` | `GazeRecorder.cs:1219` |
| 回退眼位置 | `leftWorld = view.pos + view.rot · leftLocal`，`leftLocal = inverse(centreEye.rot)·(leftEyeAnchor.pos − centreEye.pos)` | `Assets/Scripts/Capture/ViewFrame.cs:149-150` |
| 眼锚来源 | `OVRCameraRig.UpdateAnchors`：`centerEyeAnchor ← Node.EyeCenter`，`leftEyeAnchor ← Node.EyeLeft`，`rightEyeAnchor ← Node.EyeRight`（运行时的渲染眼节点） | `OVRCameraRig.cs:316-366` |

### 1.2 HMD 参考变换 —— 精确是哪一个

| 候选 | 是否为“HMD 参考” | 依据 |
|---|---|---|
| **`Camera.main` = XRI “Main Camera”**（`XR Interaction Setup` 预制体：XR Origin → Camera Offset(局部 Y = 1.36144) → Main Camera，由 TrackedPoseDriver 驱动） | **是**：OVREyeGaze 的 `ToWorldSpacePose(Camera.main)` 用它；`hmd_world_*` 也是它；ViewFrame 的 `View` | `OVRCommon.cs:119`；`ViewFrame.cs` 头注；预制体 `Assets/Samples/XR Interaction Toolkit/2.6.5/Starter Assets/Prefabs/XR Interaction Setup.prefab`（场景实例 `LyonVRVity35.unity`）；XRI Head 输入动作绑定 `<XRHMD>/centerEyePosition` / `centerEyeRotation`（`XRI Default Input Actions.inputactions:85,107`）。预制体中的驱动器确实引用该动作：Strongly supported（未逐字段解析预制体） |
| XR Origin 底座 | 否（仅提供 XR Origin 平移；Camera Offset 的 Y = 1.36144 通过 Main Camera 的父级进入世界位置） | `ViewFrame.cs` 头注 |
| Camera Offset | 否（Main Camera 的父级，固定常量，不是眼位置） | 同上 |
| OVRCameraRig `CenterEyeAnchor` | **否**（仅用于回退时求眼睛的头部局部位置，再“重新应用”到 Main Camera）；与 Main Camera 同一追踪头部、少 Camera Offset（S3：`hmd_world_y − head_pos_y = 1.3614 m`，水平差 ≤ 1.7 cm） | `ViewFrame.cs:149-150`；S3 `gaze_diagnostics.csv` `head_pos_*` |
| Left/RightEyeAnchor | 否（回退来源；不进入提供者原点） | `ViewFrame.cs` |
| `EyeGazeProviders/EyeGaze_Left/Right`（OVREyeGaze 所在对象） | 只是输出容器：根对象、无父级、单位变换（位置 0、旋转 0、缩放 1），`TrackingMode = 1(WorldSpace)`，`ReferenceFrame` 为空，`Eye` = 0/1，组件在场景中禁用、运行时由生命周期启用 | `Assets/Scenes/LyonVRVity35.unity:226102-226200, 143779-143798, 143886-143902` |

**HMD 参考在物理上位于哪里：** 由 API 语义（`Node.EyeCenter` = 运行时的“中心眼”）和 S3 数据共同给出：左/右渲染眼锚相对中心眼的位置 = (∓33.35, 0.00±0.03, 0.00±0.07) mm，即**中心眼恰为两个渲染眼节点的中点（Y、Z 向 <0.3 mm）**；头节点（OVRCommon.ToHeadSpacePose 使用的 `XRNode.Head`/`OVRPlugin.Node.Head`）与中心眼重合在 ≤ 1.5 mm 内（`left/right_gaze_vs_view_anchor_head_m` 均值 0.85 mm，最大 1.46 mm）。 **[Confirmed：参考点是运行时两个渲染眼节点的中点。]**
**它是否等于参与者瞳孔的中点或头显几何中心：Not testable**——渲染眼节点由运行时的头显模型 + IPD 设置给出，并非眼球位置测量；参与者佩戴状态（眼距离、偏移）无任何记录。

### 1.3 数学表达

记 T 为 OVRPlugin 跟踪空间；`p_H, R_H` 为头节点位姿（`XRNode.Head`，`OVRCommon.cs:145-160`）；`p_E, R_E` 为提供者眼位姿（`GetEyeGazesState`）；`p_C, R_C` 为 `Camera.main` 的世界位姿。

* 头部坐标系中的眼位置： `e = R_H⁻¹ · (p_E − p_H)`（`ToHeadSpacePose`）
* 世界原点： `O = p_C + R_C · e`（`ToWorldSpacePose`：`cameraTransform × headSpaceTransform`，取第 4 列）
* 世界方向： 提供者的朝向在 `ToWorldSpacePose` 中为 `R_C · (R_H⁻¹ R_E)`，`CalculateEyeRotation` 再乘以视图变换（`EyeGazeProviders` 为单位）与初始偏移（单位）；`d = (R_C R_H⁻¹ R_E) · ẑ`
* 合成： `O_c = (O_L + O_R)/2 = p_C + R_C · (e_L + e_R)/2`； 因 `e_L + e_R = (−0.0325 + 0.0325, 0, 0) = 0`，**`O_c = p_C`**；`d_c = normalize(d_L + d_R)`。
* 回退（仅替换原点，不改方向）： `O_fb = p_C + R_C · a`，`a = R_Ĉ⁻¹ (p_anchor − p_Ĉ)`（`CenterEyeAnchor` 坐标系中的眼锚局部位置），S3 中 `a = (∓0.03335, 0, 0)`。

**局部还是世界：** `e` 是头部局部量（常量）；记录在 CSV 里的 `*_origin_*` 是**世界坐标**。 **[Confirmed]**
**数值：** `e_L = (−0.0325, 0.0000, 0.0000)`，`e_R = (+0.0325, 0.0000, 0.0000)` m（`left/right_gaze_head_pos_*`，S3：X −0.03250±0.00004 / +0.03250±0.00004；Y、Z 平均 0.00000，SD ≤ 0.02 mm；最大绝对值 0.8 mm）。 **[Confirmed]**
**含义：** 每只眼各一个位置（左眼负、右眼正；符号由眼别决定，与其他条件无关；Unity 头部坐标 +X = 右）。不是单个眼锚、也不是“假设 IPD/2”的项目代码——项目代码里没有该数值，Meta SDK 的 C# 里也没有；来自提供者返回的位姿。它等价于 65.000 mm 的“名义双眼间距”。 **[Confirmed 来源不在 C#；“名义/估计”性质 Strongly supported；原生内部原因 Not testable]**

---

## 2. 偏移是否对所有参与者相同（第 2 部分）

### 2.1 代码/配置层面

| 候选来源 | 结论 | 依据 |
|---|---|---|
| 项目 C# 硬编码 | **无** | 对 `Assets` 全部非测试、非示例脚本搜索 `0.0325`、`0.065f`，无命中 |
| Meta SDK C#（`com.meta.xr.sdk.core@201.0.0/Scripts`） | **无** | 同上搜索无命中 |
| 预制体/场景序列化值 | **无** | `EyeGaze_Left/Right`：位置 0、`ApplyPosition=1`、`ReferenceFrame` 空；没有偏移字段 |
| 配置文件 | **无** | 项目中无读取该偏移的配置；`ViewFrameManifest.participant_height_calibration = "none"`（`ViewFrame.cs`，明确写着没有实现任何参与者校准） |
| 提供者 | **是** | 偏移在 `OVRPlugin.GetEyeGazesState` 返回的位姿里（`OVREyeGaze.cs:160-172`）。原生二进制不可检查：它是运行时按头显模型输出的常量，还是随某个设置变化，**代码层面无法证明（Not testable）** |
| 每位参与者校准 | **无** | 项目没有眼动校准/IPD 校准流程 |

### 2.2 眼锚的初始化
眼锚由 `OVRCameraRig` 在每帧 `UpdateAnchors` 中取自运行时的渲染眼节点位置（`OVRCameraRig.cs:350-356`）；`monoscopic` 或无头显时被设为与中心眼相同（`:339-342`）。项目不设置该值。 **眼锚偏移因此依赖运行时（头显的 IPD 设置）**，与提供者的常量不同。

### 2.3 数据层面（只读取 3 列：`left/right_gaze_head_pos_x`、`runtime_user_ipd_m`、`view_anchor_separation_m`）

| 会话 | subject_id | 有效行 | 提供者偏移 左 / 右 (m, 均值 / SD) | 运行时 IPD (m) | 眼锚间距 (m) |
|---|---|---:|---|---|---|
| 20261105T111311Z | 1 | 816 | −0.03250 / 0.00000 · +0.03250 / 0.00000 | 0.06699 | 0.06699 |
| 20261105T111835Z | 1 | 1319 | −0.03250 · +0.03250（SD 0.00001） | 0.06699 | 0.06699 |
| 20261105T150358Z | 1 | 9836 | 同上 | 0.06688 | 0.06688 |
| 20261105T165706Z | 2 | 3542 | 同上 | 0.06821 | 0.06819 |
| 20261105T170113Z | 2 | 9535 | 同上 | 0.06821 | 0.06821 |
| 20261106T142958Z | 4 | 5209 | 同上 | 0.06670 | 0.06673 |
| **S3 20261106T145652Z** | **4** | **3096** | −0.03250 · +0.03250（SD 0.00004） | **0.06670** | **0.06670** |

要点：
* 运行时 IPD 与眼锚间距**随会话变化**（66.70 → 68.21 mm，且与参与者 ID 的分组一致：同一 subject 同一值，不同 subject 不同值），而提供者的偏移**不变**。
* 因此，在这些数据里，**偏移与参与者、会话、头显 IPD 设置都无关**。注意：我没有对 subject_id 与真人/头显序列号做核对，也不能断言“不同 subject_id = 不同的人”或“同一台头显”；结论只对这 7 个已记录会话成立。**对未录制的参与者、其他头显或其他运行时版本：Uncertain**。

---

## 3. 三个空间维度（第 3 部分）

S3，把 `*_origin − hmd_world` 旋入头部坐标系（左乘 `inverse(hmd_world_rot)`；二者同为世界坐标，可比较）。毫米：

| 子集 | 行数 | 眼 | 侧向 X (中位) | 竖直 Y (中位 / SD / 最大) | 前后 Z (中位 / SD / 最大) |
|---|---:|---|---:|---|---|
| 一致的提供者行（与同帧 Main Camera 组合的原点差 <3 cm） | 2871 | 左 | **−32.47** | 0.07 / 7.0 / 29.2 | 0.12 / 2.4 / 11.9 |
| | | 右 | **+32.52** | 0.08 / 7.0 / 29.4 | 0.10 / 2.4 / 12.4 |
| | | 合成 | **0.00** | 0.00 / 7.0 / 29.3 | 0.00 / 2.4 / 11.8 |
| 提供者行但帧内错位（>3 cm） | 168 | 左 / 右 / 合成 | −31.5 / +33.6 / +1.0 | **67.6** / 46.3 / 112.3 | **20.1** / 16.1 / 51.2 |
| 回退行（eye-anchor） | 57 | 左 / 右 / 合成 | −33.35 / +33.33 / 0.00 | −0.02 / 0.11 / 0.35 | 0.00 / 0.30 / 1.1 |

* **当前实现只施加侧向偏移；没有任何竖直或前后偏移**（提供者与回退的 Y、Z 都 ≈ 0；一致行里 Y、Z 的 SD 7 mm / 2.4 mm 来自帧内的时序，不是系统偏移）。 **[Confirmed]**
* 与提供者约定对比：提供者位姿相对头节点的竖直/前后为 0（Y、Z < 0.8 mm），与渲染眼节点（相对中心眼 Y、Z < 0.3 mm）一致。 因为两者都**是对称的“眼距”模型**：没有给出眼球旋转中心或瞳孔相对于显示器眼节点的前后/竖直位置。 **[Confirmed 现象]**
* 提供者的 `gazePose.position` 在 Meta 规范中表示什么（眼球中心、瞳孔、还是 gaze 原点）：安装的 SDK 与 Unity OpenXR 包里只有结构体成员名（`XrEyeGazeFB: isValid, gazePose, gazeConfidence`，`openxr_reflection_full.h:4112-4115`），**没有语义文字**，故 **Uncertain / Not testable**。
* 仅 HMD 参考“位于哪里”已在 §1.2 给出：渲染眼节点的中点；没有证据它在物理上位于参与者两瞳孔的中点。

---

## 4. 空间误差评估（第 4 部分）

区分：**理论几何误差**（下表）≠ **实测实验误差**。后者需要已知注视目标（校准任务）才能得到，S3 没有，所以**实测的命中误差：Not testable**。下面“S3 实测”只指记录坐标彼此的一致性，不是命中精度。

### 4.1 理论几何：原点误差 δ 对“方向正确”的射线的影响
射线方向不变、原点平移 δ，则射线整体平行平移，目标平面上的命中点位移为 δ 垂直于射线的分量 **与距离 D 无关**；等价角度 ≈ atan(δ/D)。

| 假设的原点误差 | 命中位移（所有 D） | D=0.5 m | 1 m | 2 m | 3 m | 5 m |
|---|---|---|---|---|---|---|
| 单眼射线却从中心起点发出（或反之）δ = 32.5 mm | **32.5 mm** | 3.72° | 1.86° | 0.93° | 0.62° | 0.37° |
| 提供者 65.0 mm vs 运行时 IPD（S3 66.7 mm）→ 每眼 0.85 mm | 0.85 mm | 0.10° | 0.05° | 0.02° | 0.02° | 0.01° |
| 若真实 IPD 为 54–74 mm（**外部假设**，非本项目数据）→ 每眼 −5.5 … +4.5 mm | 约 5 mm | 0.6° | 0.3° | 0.16° | 0.1° | 0.06° |
| 未建模的竖直/前后偏移 10 mm / 20 mm（**说明性**，数值无来源） | 10 / 20 mm | 1.1°/2.3° | 0.6°/1.1° | 0.3°/0.6° | 0.2°/0.4° | 0.1°/0.2° |
| S3 实测：错位行 168 行，合成原点相对 HMD 的距离中位约 70 mm、最大 118 mm（§3；分量是 Y 67.6 mm + Z 20.1 mm 的向量） | 与射线垂直的分量，约 2–12 cm | — | — | — | — | — |

**对合成射线（`combined_origin` = HMD 位置，`combined_dir` = 两眼平均方向）：** ±0.0325 m 左右对称抵消，原点即 HMD 位置；若参与者的真实“中心眼”与渲染中心眼重合，则 ±0.0325 带来的误差为 0，对称注视时平均方向恰好指向目标。**所以 ±0.0325 本身不是合成射线的主要误差来源**。真实误差来自：(a) 渲染中心眼与参与者真实眼的未知偏移（Not testable）；(b) 帧内错位（S3 实测，§5）；(c) 注视方向本身的精度（本调查未评估）。

### 4.2 对 ROI 的含义（用 S3 里射线的实际命中距离作参照）
S3 的 NPC 命中 208 个（命中距离：≤0.5 m 占 7.7 %，≤1 m 11.5 %，≤2 m 22.1 %，≤3 m 36.5 %，≤5 m 51.4 %，中位 4.7 m）；建筑命中中位 20.5 m（5–95 %：6.7–45.3 m）。NPC 的 NavMeshAgent 半径 0.2 m（`Assets/Prefabs/Npc.prefab`），即身体宽约 0.4 m（物理碰撞体为多个小碰撞体，这里只是量级参照）。

| 目标 | 3.25 cm（单眼射线原点误差）占比 | 2 cm 级未建模偏移 | S3 错位行（2–12 cm） | 结论 |
|---|---|---|---|---|
| 大型环境 ROI（建筑立面/地形，数米–数十米） | ≪1 % | ≪1 % | 可忽略相对于 ≥ 6 m 的目标 | 不影响 |
| 单个 NPC（宽约 0.4 m，头约 0.2 m） | 身体宽度的约 8 %；头部 ROI 的约 16 % | 5–10 % | 错位行 2–12 cm，相当于 NPC 半宽（0.2 m）的 10–60 % | 用合成射线通常可识别；NPC 边缘与头部 ROI 的判定会受影响 |
| 相邻 NPC（中心间距约 0.4 m 以上，NavMesh 避让；**未测量实际间距**） | 若两 NPC 间隙 ≈ 0（几乎接触）则 3 cm 就能越界 | 同上 | 错位行可能误判到邻居 | 只有在注视点靠近两者分界线时会误分配；不是整体偏差 |
| 小物体/精细 ROI（≤5 cm） | 65 % 以上 | 40–80 % | 超过目标尺寸 | **不适用**（超出原点代理的精度，且 3.2 fps） |

### 4.3 单眼射线额外的问题（S3 实测）
S3 里左右注视方向的夹角恒为 **3.05°**（5–95 %：2.84°–3.15°），与命中距离几乎无关：相关系数（角度, ln 距离）= **−0.011**；命中距离 0–2 / 2–5 / 5–15 / 15–60 m 四档的中位夹角分别 3.060° / 3.048° / 3.049° / 3.053°。即每只眼的射线都内收约 1.525°，**共同指向正前方约 1.22 m**（`0.0325 / tan(1.525°)`），而不是被注视物体的真实距离。 **[Confirmed 现象；原因（提供者模型/固定汇聚/其他）Uncertain]**
后果：单眼射线（原点 ±0.0325）在目标距离 D 处的侧向位置为 `x_L = −0.0325 + D·tan(1.525°)`：D=0.5 / 1 / 2 / 3 / 5 m 时分别为 −1.9 / −0.6 / +2.1 / +4.7 / +10.1 cm，左右眼射线彼此错开 3.8 / 1.2 / 4.2 / 9.5 / 20 cm。**所以不能把 `left_*`、`right_*` 射线当作可独立使用的深度/汇聚证据；用平均方向（合成射线）即可抵消这一对称偏差。**

---

## 5. 提供者原点 vs 回退原点（第 5 部分，S3）

先说明：**“provider” 的含义是该原点在 12 cm 内与渲染眼锚吻合，而不是测量得到的眼睛位置。**
对 `gaze_origin_source = provider` 的 3039 行（C1 的 3038 行 + 缺溯源的 1 行）与 57 行 `eye_anchor_view`，逐字段：

| 字段 | provider 行（3039） | 回退行（57） |
|---|---|---|
| `left/right_dir_*` | **提供者的朝向**：`OVREyeGaze.transform.forward`，由提供者四元数经 `R_C R_H⁻¹ R_E` 得到。S3 中与“提供者头部空间朝向 × 同帧 Main Camera”重算的偏差：中位 0.000°，95 % 分位 0.000°，最大 0.028° **[Confirmed]** | 同左（回退不改方向）。这 57 行的重算偏差为 0.000°（最大 0.000°） |
| `left/right_origin_*` | **提供者位置经应用层变换**：`Camera.main` 世界位姿 ⊕ 头部局部恒量 (∓0.0325, 0, 0)。数值来自提供者，但**除头部位姿与恒定的 65 mm 间距外不含任何眼睛特有信息**（头部空间 Y/Z ≈ 0，SD < 0.02 mm）。写入时刻是 `OVREyeGaze.Update`（执行序 −80），与 `Camera.main` 当时的位姿绑定 | **应用生成的 HMD 相对原点**：`Camera.main`（记录时刻）⊕ 眼锚局部位置 (∓0.03335, 0, 0)；眼锚来自运行时的渲染眼节点（IPD 66.7 mm）。`left_origin == left_eye_world`（差 0.0）。不是提供者数据 |
| `combined_origin_*` | 两眼均值 → 应用生成，等于 HMD 位置（相干行 0.00 mm；错位行 ~70 mm） | 均值 = HMD 位置（≤ 0.9 mm） |
| `combined_dir_*` | 应用计算：`normalize(L.dir+R.dir)` | 同左 |
| `hmd_world_*` | 应用读取 `Camera.main`（记录时刻） | 同左 |
| `left/right_eye_world_*`（diag） | 应用组合：眼锚局部量重新应用到 `Camera.main` | 同上，且被用作回退原点 |
| `left/right_raw_tracking_*`（diag） | 提供者原始位姿（跟踪空间；位置随头部移动，**只含头部位姿 + 恒定偏移**） | 同左 |

* 因此**“提供者有效的方向”不等于“提供者给出的真实眼位置”**：方向是提供者数据；原点是提供者位置的头部相对常量再经应用层变换；回退行的原点是应用对运行时渲染眼节点的重组。 **[Confirmed]**
* 表中“原点 vs 同帧重算”的偏差：一致的提供者行中位 3.2 mm，95 % 分位 31.7 mm，最大 118 mm；回退行中位 **152.9 mm**（最大 206 mm，均 > 0.12 m 的门限，也因此触发回退）。成因最可能是：`OVREyeGaze.Update`（−80）把原点写入后，同一帧稍后 XR Origin 底座又被移动（行走速度下 S3 约 3.2 fps，每帧 0.39–0.46 m 的位移，回退行的行间位移中位 0.46 m 对其他 0.39 m），而记录器在帧后段读取 `Camera.main`。**Strongly supported（与位移相关，r ≈ 0.4；移动脚本未逐一确认）。**
* 影响：错位行的方向是正确的，但原点相对同帧 HMD 平移了几 cm 到 ~20 cm。这是比 ±0.0325 更大的实测一致性问题，已在 `gaze_diagnostics.csv` 中可识别：`left/right_worldspace_vs_composed_origin_m > 0.03`。

---

## 6. 最终问题

1. **当前 GazeOrigin 偏移是否只有侧向，竖直与前后为零？**
   是。两眼各 ∓0.0325 m 侧向；Y、Z 的系统偏移 ≈ 0（<0.2 mm 中位）。合成原点（均值）就是 HMD 位置。回退行为 ∓0.03335 m 侧向，Y、Z 也为 0。 **[Confirmed]**
2. **±0.0325 m 对所有参与者是否恒定？**
   在已记录的 7 个会话（subject_id 1、2、4）中，**恒定（SD ≤ 0.04 mm）**，而运行时 IPD 在 66.70–68.21 mm 之间变化；项目与 SDK C# 中没有该常量，也没有任何校准。 **[Confirmed（这些会话）；其他参与者/头显/版本 Uncertain；原生为何常量 Not testable]**
3. **它代表物理上合理的眼睛位置，还是应用定义的代理？**
   是**代理（名义值）**，不是实测眼位置：提供者位置相对头节点是固定的 (∓0.0325, 0, 0)，不随 IPD、眼距、注视变化。是否与某一参与者的真实瞳孔位置接近：Not testable；其值 65 mm 与成年人平均 IPD 量级一致只是巧合性的可信度，不构成证据。 **[Confirmed 代理性质]**
4. **当前实现是否考虑个体 IPD 或眼位置？**
   **否。** 提供者原点不随运行时 IPD 变化（S3：65.0 vs 66.7 mm）；回退原点用的是运行时报告的头显 IPD（它反映头显的 IPD 设置，不是眼睛测量）。项目没有个体校准。 **[Confirmed]**
5. **记录的 GazeOrigin 是否适合对 NPC 与环境 ROI 做射线检测？**
   * 环境/建筑级 ROI：适合（误差 ≪ 目标尺寸）。
   * 单个 NPC：用**合成射线**（`combined_*`）大体适合；NPC 边缘、头部 ROI 与相邻 NPC 的分界有误差风险。 **必须排除/标记 168 行错位行与 57 行回退行**（占 7.3 %）。
   * 小物体/精细 ROI：不适合。
   * 单眼射线（`left_*`/`right_*`）：不建议作为独立射线使用（原点是代理，且汇聚恒定 ≈ 1.22 m，§4.3）。
   **[Strongly supported（几何 + S3 一致性）；实际命中精度 Not testable]**
6. **在实验距离下的可能空间误差量级？**
   * 理论上由 ±0.0325 引起（仅单眼射线）：恒定 3.25 cm 的平行位移，等价角度 3.7°（0.5 m）/ 1.9°（1 m）/ 0.9°（2 m）/ 0.6°（3 m）/ 0.4°（5 m）；对合成射线几乎为 0。
   * S3 已测得的原点不一致：5.4 % 的行 3–12 cm，1.8 % 的行用回退，其余一致行的 Y 向 SD 7 mm、Z 向 SD 2.4 mm。
   * S3 的命中距离分布：NPC 命中中位 4.7 m（51 % ≤ 5 m，22 % ≤ 2 m），建筑中位 20 m。在这些距离上，对 NPC 级目标的误差量级是**厘米级**，对建筑级目标可忽略。**实测命中误差：Not testable**（没有已知注视目标）。
7. **最小的、在技术上有依据的改进？**
   * **不需要改动录制代码来修正 ±0.0325。** 它对合成射线无害，且任何“更真实”的数值都需要测量（IPD/眼位）才能证明，现有数据无法证明。
   * 最小且有依据的是**分析侧处理（零代码）**：使用 `combined_origin`/`combined_dir`；用 `gaze_diagnostics.csv` 的 `left/right_worldspace_vs_composed_origin_m > 0.03` 与 `gaze_origin_source != provider` 标记 225 行（168 + 57）；需要精确原点时，用已记录的 `left/right_gaze_head_pos_*` 与 `hmd_world_*` 重新合成（`p_C + R_C·e`，列已存在，S3 中这与方向完全一致）。
   * 若将来要修改录制：只在采样时用**同一帧**的 Main Camera 重新合成原点（`EyePipelineMath.ComposeOrigin` 已存在于诊断计算中），属于原点语义的改变，需要在 schema 中公布（本任务不做）。
   * 要得到真实的眼位置，需要新的测量（例如定点注视校准 + 头显佩戴/IPD 记录）——这是研究设计问题，不是缺陷修复。 **[Strongly supported]**

---

## 7. 局限与不确定性

* 原生 `OVRPlugin` 二进制不可检查：65.0 mm 的名义间距为何固定、`gazePose.position` 的确切含义（Meta 规范语义），安装的 SDK/OpenXR 头文件里**没有文字说明**，故 **Uncertain / Not testable**。
* S3 的会话清单缺失（压缩包截断），所以没有设备型号、`xr_origin`、`tracking_space` 等运行时记录；设备型号只能由“眼动数据存在”间接支持。
* subject_id 与实际参与者/头显序列号的对应未核对；§2.3 的结论仅限已记录会话。
* §4 的 IPD 范围、竖直/前后偏移为**说明性假设**；3.2 fps 与没有已知注视目标，使“实测命中误差”无法评估。
* 所有结果均为 Editor 离线分析；**没有 Quest/Quest Pro 实测**。
* 错位行成因（移动脚本在 `OVREyeGaze.Update` 之后移动 XR Origin）为 Strongly supported，未逐脚本验证。

## 8. 证据索引

* SDK：`Library/PackageCache/com.meta.xr.sdk.core@201.0.0/Scripts/Movement/OVREyeGaze.cs`、`Scripts/OVRCommon.cs:119-160`、`Scripts/OVRCameraRig.cs:316-366`、`Scripts/OVRPlugin.cs`（`EyeGazeState` 3170、`GetEyeGazesState` 10728）。
* 项目：`Assets/Scripts/GazeRecorder.cs`（687-705, 1161-1177, 1219）、`Assets/Scripts/Capture/EyeGazeOrigin.cs`、`Assets/Scripts/Capture/ViewFrame.cs`、`Assets/Scenes/LyonVRVity35.unity:226102-226200`（EyeGazeProviders）、`Assets/Prefabs/Npc.prefab`。
* 数据：S3 `gaze.csv`（61 列）、`gaze_diagnostics.csv`（85 列）；对比会话只读取三列（§2.3）。
* 计算在会话临时目录中进行，未写入项目；本文件是唯一的写入物。
