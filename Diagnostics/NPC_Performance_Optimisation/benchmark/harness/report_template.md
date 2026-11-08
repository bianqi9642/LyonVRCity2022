# NPC 性能优化：剖析、受控实现与验证报告

日期：2026-11-08　仓库：`LyonVRCity2022`（分支 `main`，开始时 HEAD = `44c8edac` “Eye-tracking recorder updated”，**开始时工作树干净**，没有任何已有的未提交修改）
Unity 2022.3.53f1 · URP 14.0.11 · AI Navigation 1.1.7 · Meta XR SDK Core 201.0.0
**未提交任何内容。** 未修改任何场景、预制体、NavMesh、材质、参与者数据、`ProjectSettings` 或 NPC 行为/外观/分布的任何参数。

> **读这份报告前请先知道三件事**
> 1. 所有测量都来自 **macOS（Apple Silicon）上的 Unity Editor，`-nographics` 批处理模式，真实的 `Npc.prefab` + 真实的 `SpawnerManager / PedestrianAgent / MoveToTarget / DataCaptureManager`，运行时构建的 NavMesh 平面和合成“建筑”碰撞体**。没有渲染、没有蒙皮、没有 GPU 时间、没有真实城市模型、没有头显。因此：绝对毫秒数**不能**搬到 Quest Pro 或 PC 头显构建上；有意义的是**同一台机器上前后对比的比例**和**各部分的相对占比**。
> 2. 本次**没有任何 Quest Pro / 头显 / PC 构建运行时验证**（见 §12 的待验证清单）。不声称任何头显帧率。
> 3. 仿真侧（NPC 移动、动画、NavMesh）**没有改动**：行为改变型优化一律没有实施（§13）。实施的全部是**数据采集路径**上与原实现**逐字节等价**的优化。

---

## 0. 摘要

{{KEY}}

主要结论：

1. **S3 上的低帧率有两个叠加的来源，而且在大 NPC 数时采集比仿真更重。** 在真实 `Npc.prefab` 的基准里，基线 5 000 个 NPC 仅仿真就是约 220 ms/帧（4.5 FPS）；打开默认采集后是约 609 ms/帧（1.6 FPS），其中**一次 NPC 快照 ≈ 397 ms**。1 000 个 NPC 时仿真 ≈ 22 ms，采集使平均帧时间增加约 37 %；2 000 个时增加 104 %；5 000 个时增加 177 %。
2. **采集代价是超线性的**：每个 NPC 的视线（LOS）射线成本随人群密度增长（1 000 → 5 000 个 NPC，单个 NPC 的 LOS 成本约 17 µs → 58 µs），因为 NPC 自己带 12 个碰撞体（5 000 个 NPC = 6 万个碰撞体），射线会穿过大量他人的肢体碰撞体。
3. **仿真侧的主要成本是 Animator（≈ 70 %）**，其次是 `MoveToTarget.Update`（≈ 22 %）；NavMesh 人群/路径本身只有约 6 %，物理、周期性 `SetDestination` 重发、NPC 日志在稳态下**可忽略**（消融实验，§4、§5）。Animator 的唯一实质性削减办法（剔除/降频）会改变参与者可见的动作线索，**因此没有实施**。
4. **已实施（并验证了与原实现逐字节等价）的 5 项采集优化** 使一次 NPC 快照的成本在 1 000 / 2 000 / 5 000 个 NPC 时分别下降约 78 % / 81 % / 84 %（36.8 → 8.1、92.3 → 17.3、397.3 → 62.1 ms），默认采集的额外开销（B − A）下降 84 % / 91 % / 84 %，每帧托管分配下降 46 / 38 / 31 倍（见 §8）。5 000 个 NPC 的平均帧时间 609 → 263 ms（−57 %）。此后剩余的主要成本不再是采集，而是仿真本身（Animator）。
5. **一个重要的副作用要提前告诉你**：默认采集间隔是 0.1 s（10 Hz），但 S3 级别的帧率只有 1.6–3 FPS，所以**实际上每帧一次快照**（≈ 3 Hz）。帧率提高后，快照频率会**向 10 Hz 回升**，单位时间写入量随之增大（5 000 个 NPC 时 10 Hz ≈ 16 MB/s ≈ 950 MB/min，而基准里 1.6 FPS 时约 2.6 MB/s）。数据分辨率/文件体积是**研究设计层面的决定**（§1.5），本次**没有改变默认值**。
6. 帧时间目标（15/30/60/72/90 FPS）的可行性见 §8.3：在这台机器的 Editor（无渲染）里，优化后 1 000 个 NPC 可以越过 30 FPS（44.5 FPS），2 000 个只达到 15 FPS 档（18.2 FPS），5 000 个只有 3.8 FPS；**不外推到 72/90 FPS，也不外推到头显。**

---

## 1. 原始架构与瓶颈（第一阶段：确认的实现，不沿用旧审计的解释）

### 1.1 仓库与版本
* 分支 `main`，HEAD `44c8edac`，工作树开始时干净（`git status` 无输出）。Unity 2022.3.53f1；URP 14.0.11；`com.unity.ai.navigation` 1.1.7；Meta XR Core 201.0.0。
* 场景 `LyonVRVity35.unity` 里的 NPC 设置：`maxAgents = 3000`、`poolPrewarm = 3000`、`spawnInterval = 0.1`、`prewarmFrameBudgetMs = 8`、`navMeshPathfindingIterationsPerFrame = 100`（SpawnerManager 在 `Awake` 里一次性写入全局 `NavMesh.pathfindingIterationsPerFrame`，项目中唯一设置处）。S3 实际使用了 5 000 个（`npc_lifetimes.csv` 的 5 000 个 `prewarm`），来源不在场景文件中（运行配置）。

### 1.2 NPC 仿真架构
`SpawnerManager`（池化预热、每个 `spawnInterval` 至多生成 **1** 个 NPC → NPC 数的爬升速度受**帧时间**限制）→ `AgentFactory`（外观/速度）→ `Assets/Prefabs/Npc.prefab`：
`NavMeshAgent`（半径 0.2、高度 2、避让类型 4）+ `PedestrianAgent`（行程/停留，`Update` 在大多数状态下提前返回）+ `MoveToTarget`（每帧：速度、动画参数、卡滞检测、对 Transform 目标周期性 `SetDestination`）+ `UncertainBehavior`（按配置启用）+ `AppearanceController` + **Animator（`m_CullingMode = 0`，AlwaysAnimate）**。预制体内含 117 个 Transform、**15 个 SkinnedMeshRenderer**、**12 个运动学 Rigidbody + 10 个 CharacterJoint + 12 个碰撞体**、3 个 AudioSource，以及一批 EasyNpcs 脚本（几乎全部 `enabled: 0`）。`MoveToTarget.enableDebugLogs = 1`、`UncertainBehavior.enableLogging = 1`（预制体设置，代码不覆盖）。

### 1.3 采集架构与“采样门”的真实位置（5.1 的核查）
`DataCaptureManager.NpcLoop()` 是一个协程：`ApplyNpcSamplingSettings()` → 若 `!NpcStateSamplingActive` 则 `yield WaitForSecondsRealtime(BaseInterval)` 并继续 → 否则 `SampleNpcs()` → `yield WaitForSecondsRealtime(EffectiveInterval)`。**所以“门”已经在最早的位置：没到间隔时没有任何 NPC 枚举、射线、记录构造、序列化或写盘**（`SampleNpcs` 只从这里被调用）。该间隔是**每次快照之后**才开始计时的实时间隔，所以：
* 帧时间 < 间隔时：约 `1 / (间隔 + 快照耗时)` 次/秒；
* **帧时间 ≥ 间隔时：恰好每帧一次快照**（`WaitForSecondsRealtime` 在下一帧才恢复），长时间卡顿之后**不会补发**多次（每次恢复只有一次快照）。这正是 S3 的情形（≈ 0.3 s/帧 > 0.1 s）。

因此 5.1 要求的“把门移到更早位置”**不需要也没有可移动的东西**；有效的杠杆是**每次快照里的成本**（§3）和**间隔/预算设置**（§1.5）。独立的 NPC 生命周期事件（`npc_events.csv`）走另一条路径，不受这个门的影响，本次没有改动。

每次快照对每个活动 NPC 做的事（读自源码，`DataCaptureManager.SampleNpcs`）：读取 Transform 与 `NavMeshAgent` 的 6 个原生属性；解析头骨作为眼点；对参与者做 **LOS 射线**（`NpcLineOfSight.Query`）；用约 45 个字段拼一行；目的地变化的边沿检测；最后一次性追加整个种群到 `npc_state.csv`。

### 1.4 “约 1.6 MB”的确切含义（已用实测核对）
* **它是“一次完整 NPC 快照”的字节数**，不是“每帧”也不是“每秒”。每行约 303–318 B（S3 读出的前 6 万行是 317.8 B/行；基准里 303.4 B/行），一次快照 = 活动 NPC 数 × 行字节数；5 000 个 NPC 时实测 **1 575 861 B/快照**。
* 它**等于每帧的写入量，当且仅当每帧恰好有一次快照**——这在帧时间 ≥ 采集间隔（0.1 s）时成立（S3、以及基准里 5 000 个 NPC 的 B 配置：1.03 快照/帧）。帧率高于 10 FPS 时每帧的写入量更小，但单位时间写入量 = 快照频率 × 快照字节。
* 旧报告里“每帧约 1.6 MB”是对 S3 条件（每帧一次快照、5 000 个 NPC）的**估算**，方向正确，但应说成“每次快照 ≈ 1.6 MB；S3 帧率下约每帧一次”。

### 1.5 `npcStateSamplingIntervalSeconds` 与 `npcStateDataBudgetMBPerMinute` 的实际行为
* `npcStateSamplingIntervalSeconds`（场景 0.1）= 两次**完整种群快照**之间的**最小实时间隔**（下限 0.01）。它不是渲染帧率；实际频率被帧率量化。
* `npcStateDataBudgetMBPerMinute`（场景 0 = 不限）> 0 时，`NpcStateRateController` 用**实测字节**（行字节的指数滑动平均 × 下一次的 NPC 数）估算 `T_req = 预测快照字节 × 60 / (预算 × 1e6)`，把有效间隔拉到 `T_req × 1.05`（上限 `npcStateMaxIntervalSeconds` = 2 s，下限为基础间隔）；需求上升立刻响应，下降平滑（×0.7）。**它从不丢弃快照中的 NPC，也不改变事件流。**
* 两者的关系：预算只会**加大**间隔，所以它们共同决定快照频率；预算对每次快照的内部成本毫无帮助。
* **权衡（数据分辨率 ↔ 文件体积 ↔ 性能）**：基准里 D 配置（0.5 s）把快照频率降到约 1/5，因此每帧平均成本下降（5 000 个 NPC：609 → 349 ms 平均帧时间），但 NPC 轨迹的时间分辨率从 ≤10 Hz 变成 ≤2 Hz。这是**改变数据分辨率**的做法，**本次不改默认值，也不建议在没有研究者决定的情况下改**。

---

## 2. 基线基准（第二阶段）

### 2.1 方法
* 用现有测试框架的反射式夹具（`NpcLifecycleTests` 的 `BuildRig`）搭建**真实**的 `SpawnerManager + PedestrianAgent + MoveToTarget + DataCaptureManager`，NPC 模板是真实的 `Assets/Prefabs/Npc.prefab`；运行时烘焙 240×240 m NavMesh；放置 2 500 个合成“建筑”方块碰撞体，参与者目标位于 (−100, 1.7, −100)，使 LOS 射线是长射线并且会遇到障碍物。
* NPC 行程仿照 S3：每个 NPC 8 个吸引点（环形分布，半径 80 m）、停留 20–40 s、速度 0.6 m/s、出生半径 60 m；`Random.InitState(20261106)`。
* 对每个 NPC 数先爬升到目标数量，再预热 8 s，再测量 ≥ 20 s（≥ 30 帧）。记录每帧 `Time.unscaledDeltaTime`；统计平均/中位/P95/P99/最大帧时间、每帧托管分配（`GC.GetTotalMemory` 的正增量近似）、每帧固定步数、`pathPending` 比例，以及开发期计时器给出的快照各段耗时（`#if NPC_PERF_DIAG`，见 §7）。
* 配置：**A** 仿真开、采集关；**B** 仿真开、采集开、现有设置（0.1 s）；**C**＝与 B 相同（项目现有设置**就是**默认值，故 B/C 合并）；**D** 仿真开、采集开、间隔 0.5 s。

### 2.2 不确定性与限制
* 运行之间有 ±3–10 % 的波动（同一份 A 配置在“基线构建”和“优化构建”的两次运行里，1 000 / 2 000 / 5 000 个 NPC 的平均帧时间分别是 22.0/21.2、52.2/49.3、220.2/198.8 ms）。**因此“采集额外开销”一律用同一次构建内的 B − A 计算**，对比用“B 的平均帧时间”和“快照耗时”。
* 非确定性来源：线程调度、Animator/NavMesh 作业的并行度、NPC 随机停留、帧计时。种子只固定了 NPC 的随机选择。
* 5 000 个 NPC 的爬升很慢（基线 A 配置约 780 s，B 配置约 1 670 s；每个 `spawnInterval` 最多生成 1 个）。基线有 5 000 个 NPC 的 D 配置（0.5 s）；步骤1 的同一配置因爬升超时（NPC 在爬升期间陆续走完行程消失）没有取得，“最终”版没有再跑。缺失即“未测”，不估算。
* **没有渲染/蒙皮/GPU**：`-nographics` 下不做渲染，所以 CPU 受限与 GPU 受限不能在此区分；这里的“仿真成本”只是 CPU 侧（Animator 求值、脚本、NavMesh、物理）。
* 合成方块不代表真实城市碰撞体；真实城市里的 LOS 射线成本无法在此重现。

### 2.3 结果（“基线” = 当前 HEAD + 仅在 `#if NPC_PERF_DIAG` 之内的计时器；“步骤1” = LOS 近远分段 + 行直写 + 快速 F4 + 流式写盘 + 缓存，仍为单线程；“步骤2” = 步骤1 + 并行 LOS 批处理，但近区缓冲满时一律回落穷举查询；“最终” = 步骤2 + 近区缓冲满且已含外来命中时直接判定 blocked。A 配置（采集关）与采集代码无关，“最终”行的 A 取自同一套运行。）

{{T_ALL}}

---

## 3. NPC 采集的确切成本（第三阶段）

### 3.1 一次快照里的各段耗时（开发期计时器，毫秒/快照）

{{T_SNAP}}

* 基线：1 000 个 NPC 时一次快照 36.8 ms，其中 **LOS 17.0 ms（46 %）、行格式化 15.3 ms（42 %）**，读状态 1.5 ms、眼点 1.7 ms、写盘 0.6 ms（占 1.6 %）；2 000 个：LOS 48.3、格式化 33.6，写盘 1.4；5 000 个：LOS **292.6**、格式化 82.8、读 8.3、眼点 7.6、写盘 4.1。
* **写盘不是瓶颈**：每次快照的“打开→追加→冲刷→关闭”只有 0.5–5 ms；1.58 MB 在这台机器上写得很快。真正的成本是 **CPU：LOS 射线 + 浮点格式化 + 大量小字符串分配**。
* **LOS 射线数**：基线每个 NPC 平均 1.9–1.94 次（近一半的 NPC 触发了“缓冲区满→用 256 缓冲区再查一次”的二次射线，因为沿长射线收集了全部命中），基线 5 000 个 NPC 一次快照约 9 680 次射线。
* **为什么 5 000 个 NPC 时“步骤2”只省了不多、而“最终”省了很多**：在 240×240 m 内 5 000 个 NPC 很密，NPC 头部射线前 3 m 里常有 ≥ 32 个碰撞体（别的 NPC 的肢体），近区缓冲被填满；“步骤2”遇到缓冲满一律回落到穷举查询（5 000 个 NPC 时约 1 500–3 000 个 NPC 回落，LOS 仍 181 ms，射线数 8 081）。“最终”改为：只要被返回的命中里有一个外来碰撞体就足以判定 blocked（参考实现同样会判 blocked），只有“32 个全是该 NPC/参与者自己的”才回落——于是回落几乎消失，射线数恰好 = NPC 数，LOS 降到 28 ms。
* **托管分配**：基线 1 000 个 NPC 每帧 ≈ 500 KB、2 000 个 ≈ 2.2 MB、5 000 个 ≈ 11 MB（来自约 25 个 `float.ToString("F4")` × 每行、每行再 `Join` 成新字符串，外加把整张快照变成一个 ≈ 3 MB 的字符串再转成 ≈ 1.6 MB 的 `byte[]`，这两个都是大对象堆分配）。
* 浮点格式化的单价：本运行时的 `float.ToString("F4")` 约 **0.47 µs/次**（它走原生 `snprintf`），`TryFormat` 同价（不省时间，只省字符串）；这就是格式化段占比高的原因。

### 3.2 采集对帧时间的总体影响（B − A，同一构建内）

{{T_CAPSHARE}}

### 3.3 采集成本是否“必要”
* LOS（`los_clear_to_participant`、`los_status`）与每个字段都是已有的研究变量，**没有删除任何列、没有降低分辨率、没有改变 LOS 的科学定义**（§9）。因此上述优化都只是“同样的数据，更便宜地算出来”。
* 没有发现重复射线或同一采样时刻对同一 NPC 的重复查询；基线的“二次射线”是实现上的重复收集（已用 §7.1 的等价近远分段消除）。

---

## 4. NPC 仿真的确切成本（第四阶段）

方法：消融（`-nographics` 下 Unity Profiler 的 `ProfilerRecorder` 取不到标记值，改用“在稳态下关闭某一部分，测其对平均帧时间的边际影响”）。2 000 个 NPC、采集关，预热后关闭 → 2 s 稳定 → 测 ≥ 12 s。**消融是边际成本的估计，相互之间不严格可加；禁用 `NavMeshAgent` 同时使 `MoveToTarget` 空转。**

{{T_ABL2000}}

{{REISSUE}}

补充（早期夹具，1 000 个 NPC，单吸引点短停留；只作交叉验证）：

{{T_ABL1000}}

解读：
* **Animator 求值占仿真的约 70 %**（2 000 个 NPC：51.6 → 15.2 ms）。预制体 `m_CullingMode = AlwaysAnimate`，所以屏幕外、墙后、很远的 NPC 也在每帧完整求值 117 根骨骼。
* **`MoveToTarget.Update` 约 11 ms（22 %）/ 2 000 个 NPC（≈ 5.5 µs/NPC/帧）**：每帧约 10–15 次到原生层的调用（`NavMeshAgent.enabled / isOnNavMesh / velocity ×2 / hasPath / pathPending / remainingDistance`，`transform.position` ×3，Animator 字符串参数 ×2，`Quaternion.Slerp`…）。
* **NavMesh 人群 + 寻路约 3 ms（6 %）**：禁用 `NavMeshAgent` 比禁用 `MoveToTarget` 只多省 3.3 ms。
* 物理（运动学刚体，1 000 个 NPC 时关闭自动模拟无差别）、**NPC 日志**（`nolog`：52.6 vs 51.6 ms）、**周期性 `SetDestination` 重发**（`reiss`：52.5 vs 51.6 ms）在稳态下都**没有可测量的成本**。
* 每帧固定步数：2 000 个 NPC 约 2.6 步/帧，5 000 个 NPC 约 11 步/帧（帧时间 ≫ `fixedDeltaTime = 0.02`，上限由 `Time.maximumDeltaTime = 0.333` 限制）；5 000 个 NPC 时物理的边际成本**未测**（取消了该消融以优先完成 LOS 并行化），见 §11。

---

## 5. NavMesh / 路径请求（第六阶段 6.1–6.5）

* **`SetDestination` 调用点**：`MoveToTarget` 有 10 处。稳态下最频繁的是对 **Transform 目标**的周期性重发：`if (Time.time - lastTargetUpdateTime >= targetUpdateInterval) agent.SetDestination(targetTransform.position)`，预制体 `targetUpdateInterval = 10 s`，所以约 `N / 10` 次/秒。{{REISSUE}} 即：**重发的几乎全是代理已经有完整路径的同一目的地（冗余请求）**。
* **但它没有可测量的成本**：把 `targetUpdateInterval` 设为无穷（消融 `reiss`）后 2 000 个 NPC 的平均帧时间 52.5 vs 51.6 ms（在 ±3 % 的运行波动之内）。`pathPending` 在 2 000 个 NPC 稳态下的比例约 0.02 %（没有路径积压）。因此**没有实施“跳过冗余 SetDestination”**：它有改变 NPC 重新规划行为的风险（路径会从当前位置重新计算），而测量不到收益。
* **路径请求排队/错峰**：无积压（`pathPending` ≈ 0），故不需要；也没有可用于证明“长时间静止/饿死”的风险边界的基线，不引入。
* **吸引点/漫游计算**：`PedestrianAgent.Update` 在 Part 3 行程里对有 `MoveToTarget` 的 NPC 立即返回（到达由事件驱动）；未发现每帧重算目标/重采样漫游点的热路径。
* **对象生命周期**：试次内 NPC 基本不销毁/重建（S3：5 247 次生成，仅 247 次 despawn），池化已存在（`usePooling`）；无新增池化必要。
* **全局 `NavMesh.pathfindingIterationsPerFrame`**：场景值 100（Unity 默认）。更大值缩短 `pathPending` 但增加每帧主线程成本，更小值相反；在 `pathPending ≈ 0` 的情况下**没有依据**调整，因此**不改**。

---

## 6. 渲染与 Animator（第七阶段）

* 已测：Animator 求值是仿真的最大一项（§4）。预制体是 AlwaysAnimate；15 个 SkinnedMeshRenderer/NPC。
* **未测**：蒙皮/渲染/GPU/Draw call/阴影/LOD（`-nographics` 无渲染）。**没有证据**支持任何渲染侧优化，也没有实施。
* 可选的 Animator 优化：`cullingMode = CullUpdateTransforms/CullCompletely`（视野外不更新），或对远处 NPC 降频/关闭。**这些都会改变参与者可见或被记录的内容**：① 视野外 NPC 的骨骼 Transform 停止更新，而 `npc_state` 的 LOS 眼点取的是**头骨的 Transform 位置**（会变旧）；② `CullCompletely` 会暂停状态机，NPC 重新入画时有动作相位/过渡的不连续；③ 无法在 `-nographics` 下测“可见性”。属于**行为改变型优化**，需要独立的验证计划（§13），本次**不实施**。

---

## 7. 已实施的改动与理由（全部为“同样的数据，更便宜”）

分类：**D = 数据等价（逐字节相同的 CSV 内容）**；**R = 需要运行时验证的并行/线程相关改动**。

| # | 改动 | 文件 | 为什么 | 风险 | 等价性验证 |
|---|---|---|---|---|---|
| 1 | LOS 近/远分段查询 `NpcLineOfSight.Query`：先在 3 m 近区做与参考相同的“收集全部命中”查询（含 NPC 自己的碰撞体），再对剩余线段做**一次最近命中**射线；任何不明确的情况（近区缓冲满、最近命中是 NPC 自己/参与者的碰撞体）**回落到原实现 `QueryExhaustive`**（原代码逐字保留）。 | `Capture/NpcLineOfSight.cs` | 原实现对整条 100 m 级射线收集全部命中（≤32，满了再查 ≤256），而一个“外来碰撞体”就足以判定 blocked。 | D | `NpcPerformanceTests.Los_NearFarSplit…`：4 000 个随机查询（含细墙、近区 NPC、参与者有/无碰撞体、有/无 `participantRoot`）与参考结果全相同 |
| 2 | 头骨缓存：`TryGetNpcEyePointCached`（头骨 Transform 每个 NPC 解析一次，销毁则重取）；`rec.agentIdS` 缓存 `S(agentId)`。 | `Capture/NpcLineOfSight.cs`、`DataCaptureManager.cs` | 去掉每次快照每个 NPC 的 `Animator.GetBoneTransform` 与三次 `string.Replace`。 | D | 同上 LOS 测试 + 既有 `Los_*` 测试 |
| 3 | `CsvRowBuilder`：行直接追加到一个复用的 `StringBuilder`，不再有每字段字符串、`List<string>`、`Join` 产生的每行新字符串；快照共有的字段只转义一次；**快速 F4**：仅在 `1e-4 ≤ |v| < 1e7` 与 0 时，按本运行时“7 位有效数字再做 F4（第二次舍入，平局进位）”的规则用整数运算输出；其余、以及**启动自检不一致时**，一律回落到 `float.TryFormat(…,"F4")`。 | `Capture/CsvRowBuilder.cs`（新）、`DataCaptureManager.cs` | 本运行时每次 `ToString("F4")` ≈ 0.47 µs（原生 `snprintf`），每行 ≈ 25 次；每行 ≈ 45 个小字符串。 | D（自检保护） | `CsvRowBuilder_ProducesExactlyTheFormerListJoinOutput…`（4 000 行随机字段，含 `-0`、极小、平局、NaN/∞、需引号的文本）；`CsvRowBuilder_F4_EqualsTheRuntimeFormatter_On600000Values…`（60 万个值含 8 位有效数字的精确平局：与真实格式化器**零差异**）；探针另测 544 万个值零差异 |
| 4 | 流式写盘：`CaptureCsvWriter.WriteRows(StringBuilder,int)` + `CaptureIO.AppendBuilder`：按 16 K 字符块用有状态 UTF-8 编码器直接写文件流，不再把 1.5 MB 的快照变成一个 ≈ 3 MB 字符串再变 ≈ 1.6 MB `byte[]`（两次大对象堆分配）。步骤名（open/append/flush）、故障注入点、失败策略、计数器、`BytesWritten`、逐行 open/append/close 的崩溃安全模式**都与 `WriteLines` 相同**。 | `Capture/CaptureCsvWriter.cs`、`Capture/CaptureIO.cs` | 去掉每快照约 5 MB 的大对象堆分配与 GC。 | D | `WriteRows_WritesTheSameBytesAndCounters…`（含跨 16 K 块边界的代理对 😀）、`WriteRows_FailurePolicy…`（注入 append 故障：状态/计数相同、不重试）、`WriteRows_AfterClose…` |
| 5 | **并行 LOS 批处理** `NpcLosBatch`（含上面说的“近区缓冲满且已含外来命中即判 blocked”规则，该规则同样用于同步 `Query`）：把一次快照所有 NPC 的 LOS 射线打包成 `RaycastCommand` 批（近区 32 命中、远区 1 命中），在 Job 系统工作线程上并行执行，主线程只组装命令和解释结果；**解释逻辑与同步 `Query` 相同，不明确时回落 `QueryExhaustive`**；批前与同步路径一样先 `Physics.SyncTransforms()`（`autoSyncTransforms`）；任何异常则本会话永久回落到同步逐个查询；`DataCaptureManager.npcStateBatchedLineOfSight`（默认 true，新增序列化字段，旧场景自动取默认值）可一键关闭。 | `Capture/NpcLosBatch.cs`（新）、`DataCaptureManager.cs` | 5 000 个 NPC 时 LOS 占一次快照的大头（220 / 256 ms），且是只读的、可并行的物理查询。 | **R**（线程相关） | `NpcLosBatchTests`：60 轮快照 × 40–600 个查询，碰撞体在轮间移动**且测试不显式同步**，批结果与同步 `Query` 及参考 `QueryExhaustive` 全相同；缺参与者/缺眼点/零长度/近区缓冲溢出同样一致 |
| 6 | 开发期计时器 `NpcPerfDiag`（`#if NPC_PERF_DIAG`，该符号**不在任何设置里**，发布构建中整个文件与所有调用点都编译为空）。 | `Capture/NpcPerfDiag.cs`（新）、`DataCaptureManager.cs`、`CaptureCsvWriter.cs`、`NpcLineOfSight.cs`、`MoveToTarget.cs`（仅 3 行 `#if`） | 把基准分解到各段，不留运行期开销。 | 无 | 用 csc.rsp 只在临时工程里启用 |

**一次改一项**：基准里“基线 → 步骤1（改动 1–4，单线程）→ 最终（再加 5）”分别测量，并且每个快照分段都有独立计时（LOS / 眼点 / 格式化 / 写盘），所以各项的边际效果可以直接读出（§3.1 表）。改动 1–4 之间没有再单独跑全套整数矩阵（每个 5 000 个 NPC 的运行要 10–30 分钟），而是靠分段计时分离。

**没有实现的**：见 §13。

---

## 8. 前后对比（第十节要求）

### 8.1 帧时间与分配（仅 Editor、无渲染；比例有意义，毫秒不可外推）

{{T_CMP}}

### 8.2 采集的额外开销（B − A）

（见 §3.2 表。）

### 8.3 对目标帧时间的可行性（只针对这台机器的 Editor、`-nographics`）

| 目标 | 帧时间上限 | 1 000 个 NPC（最终，B） | 2 000 个 NPC | 5 000 个 NPC |
|---|---:|---|---|---|
| 15 FPS | 66.7 ms | 达到（P99 30.2 ms） | 平均达到（54.9 ms；P99 69.7 ms 略超） | 未达到 |
| 30 FPS | 33.3 ms | 达到（平均 22.5 ms，P99 30.2 ms） | 未达到（18.2 FPS） | 未达到（3.8 FPS） |
| 60 / 72 / 90 FPS | 16.7 / 13.9 / 11.1 ms | 未达到 | 未达到 | 未达到 |

> 30 FPS 只是诊断用的中间目标，**不是**最终 VR 目标。**不据此声称 72/90 FPS 可行**；真实目标取决于头显运行时与显示刷新率，Quest 独立构建的 CPU 更弱、有真实渲染成本，且 Animator（占仿真 ≈ 70 %）在这里没有被削减。

### 8.4 对凝视记录的含义（第 11 节）
* 凝视记录器**没有被修改**，仍是“每个 Unity 帧至多一行、来自提供者的最新状态”。帧率提高 → 凝视行频率按同样比例提高（例如 2 000 个 NPC：基线 B 约 9.4 FPS → 最终约 18 FPS）。这是 Unity 记录频率，**不是**传感器采样频率；没有重复或伪造样本、没有改时间戳。
* 在本次测量中**未运行**凝视记录器（Editor 无头显），所以“新的提供者时间戳比例/重复比例/唯一更新数”**未测**，需要头显（§12）。

---

## 9. 行为与数据完整性验证

* **NPC 行为/外观/分布/速度/行程/停留/避让**：仿真侧代码**零改动**（`MoveToTarget` 只加了 3 行被 `#if NPC_PERF_DIAG` 包住的计数器）；预制体/场景/NavMesh/ProjectSettings 未改。
* **`npc_state.csv`**：列、顺序、值格式、引号规则、换行、`BytesWritten`（预算控制器使用）与原实现一致，由 §7 的逐字节/随机等价测试保证；既有的 NPC 架构/生命周期/schema/Part2 采集测试全部通过（§10）。
* **LOS 的科学定义**（障碍 = 默认层 + Ground 层中、不属于该 NPC 且不属于参与者的非触发碰撞体；其他 NPC 会遮挡；触发器忽略；缓冲溢出 = 不确定而非猜测）**未变**：新实现对每个输入返回与参考实现相同的状态。
* **同一采样时刻**：LOS 批在 `SampleNpcs` 开头对同一帧、同一变换状态一次完成，与每个 NPC 的状态读取处于同一帧。
* **缺失 NPC/重复 ID/缺失生命周期事件/时间戳**：采集触发条件、`rec.sampleSeq`、边沿检测、事件写入顺序都没动。
* **未做**：没有在同一份“冻结的”仿真状态上并排运行旧/新两条完整的 `SampleNpcs` 并比较整张 `npc_state.csv`（这需要确定性的 NPC 位置，当前夹具做不到）。等价性是在**组件级**（行构建、格式化、写盘、LOS）用随机输入证明的，并由集成测试补充。

---

## 10. 测试

{{FLAKE}}

| 运行 | 范围 | 结果 |
|---|---|---|
| 新增等价测试（`NpcPerformanceTests` 8 个 + `NpcLosBatchTests` 3 个，含密集人群回归测试） | 行构建器、F4、写盘、失败策略、LOS 近远分段、LOS 批 | **11 / 11 通过**（第一次运行 7/8，唯一失败是测试自己没有声明预期的日志错误，已修复并复跑通过） |
| 最终全套 PlayMode（`GazeRecorder.Tests`，最终代码，记录在案的一次） | 400 个测试 | **387 通过 / 5 失败 / 8 跳过**：3 个确定性的预先存在失败 + 2 个时序不稳定测试（见上与下）。同一最终逻辑的此前两次全套运行（399 个测试，少一个回归测试）为 388 通过 / 3 失败 与 387 通过 / 4 失败 |

{{SUITE}}

**五个失败都不是本次改动引起的：其中 2 个是上面说的时序不稳定测试（`Gaze_RowsCarryPhaseAndCurrentTarget_…`、`Phase_Part2_ProductionFlow_…`，基线也会失败）；另外 3 个是确定性的预先存在失败，已在基线脚本上复现：**
1. `NpcLifecycleTests.Part2Flow_ParticipantControl_Task1Free_ContinuePanelLocked_ReleasedByTheContinue_…` —— 用 HEAD 脚本（仅计时器）运行同样失败。
2. `TerrainLayerTests.AutoGroundAlign_IgnoresATerrainLeftOnTheDefaultLayer_…` 和 3. `…SettlesOnAGroundLayerTerrain_…` —— 地面对齐精度（100.0029 vs 容差 ±0.002）；基线脚本同样失败。

过程中还发现并处理了两个问题：① 我新增的 `TryGetNpcEyePoint` 重载使既有测试用 `GetMethod("TryGetNpcEyePoint")` 反射时出现 `AmbiguousMatchException`，已改名为 `TryGetNpcEyePointCached`；② 基准用的夹具补丁版（多吸引点）会让 `DwellArrivalAndEntranceTeleport…` 因吸引点名称不同而失败——这是基准夹具的产物，最终套件用原始夹具运行，已通过。

**没有运行**：Quest/头显/PC 构建上的任何测试；PathPending/目的地更新相关的新测试（因为该部分没有改动）。

---

## 11. 剩余瓶颈

1. **Animator 求值（仿真的 ≈ 70 %）**：AlwaysAnimate + 117 骨骼 × N。这是 5 000 个 NPC 的 ≈ 140 ms/帧，**没有行为等价的办法**；候选办法在 §13。
2. **`MoveToTarget.Update`（≈ 22 %，≈ 5.5 µs/NPC/帧）**：约 10–15 次原生调用/NPC/帧；可以缓存重复读取的 `agent.velocity`/`transform.position`、用整数哈希代替 Animator 字符串参数，但预计只省 10–20 %（约 1–2 ms / 1 000 个 NPC），且改动触及移动代码，未做。
3. **采集在 5 000 个 NPC 时仍有 +30 % 的帧时间**（最终 B 262.5 ms vs A 201.2 ms）：快照 62 ms 中 LOS 28 ms、行格式化 20 ms、读状态 6 ms、眼点 5 ms；5 000 个 NPC 的帧时间 ≈ 77 % 仍是仿真（Animator）。
4. **采集频率设计**：默认 10 Hz 的完整种群快照在 5 000 个 NPC 时即使最优化也要占用大量主线程时间；这是研究设计（§1.5）。
5. **5 000 个 NPC 的物理固定步**：约 11 步/帧；其边际成本未测。
6. **未测**：渲染/GPU、真实城市碰撞体的 LOS、头显运行时。

---

## 12. 仍然需要的 Quest Pro 运行时验证（任何自动化/Editor 测试都不能代替）

在把这些改动用于正式数据收集前，应在目标头显上：
1. **稳定帧率**：代表性 NPC 数（500 / 1 000 / 2 000 / 3 000 / 5 000）下的帧率、**帧时间分布（P95/P99/最大）**、掉帧；读取会话清单里的 `frame_timing`。
2. **并行 LOS 批**：打开/关闭 `npcStateBatchedLineOfSight` 各跑一次；Quest 的工作线程少且与 Animation/渲染作业共用，需确认没有因抢占而变慢；检查无 `los_batch` 警告、LOS 状态分布（clear / blocked / 空白）与关闭时一致。
3. **凝视记录**：行频率、`provider_time_s` 是否推进、`provider_time_changed` 比例、重复方向行、唯一提供者更新数（只在有证据时才能说“传感器样本更多”）。
4. **NPC 移动连续性与社会线索可见性**：目视与轨迹（`npc_state.csv`）对照——速度、方向、停留、避让、动画是否与基线一致。
5. **CSV 完整性**：`npc_state.csv` 的列数/行宽/`F4` 格式/时间戳单调；`session_manifest.json` 中的 `npc_state` 流健康、写错误为 0。
6. **应用暂停/恢复与崩溃安全**：暂停、取下头显、终止后文件仍完整（写盘仍是逐次打开-追加-关闭）。
7. **热与持续负载**：≥ 30 分钟持续运行的温度与降频。
8. **启动自检**：确认 `CsvRowBuilder.FastF4Active`（若在 IL2CPP/Android 运行时格式化行为不同，会自动回落到标准格式化，输出不变，只是更慢）。

---

## 13. 没有实施的优化及原因

| 候选 | 类别 | 为什么没做 |
|---|---|---|
| Animator `cullingMode` 视野外剔除、远处 NPC 动画降频/关闭 | **行为/数据改变型**（Priority 3） | 影响可见动作线索与 LOS 眼点（头骨位置会过时）；`-nographics` 无法测可见性；需要独立验证计划 |
| 距离分级的仿真 LOD（导航/Animator/采集分级） | 行为改变型 | 同上，且会改变轨迹 |
| 降低 NPC 快照频率/加预算/只采样子集 | **数据分辨率改变型** | 研究设计决定；本次不改默认 |
| 跳过冗余 `SetDestination`、路径请求排队 | 中等风险 | 测不到收益（§5），且可能改变重新规划行为 |
| 修改 `NavMesh.pathfindingIterationsPerFrame` | — | `pathPending ≈ 0`，无依据 |
| 关闭 NPC 日志（预制体 `enableDebugLogs`/`enableLogging`） | 修改预制体 | 稳态下无可测成本（§4）；不动预制体 |
| `MoveToTarget` 缓存重复原生读取、哈希化 Animator 参数 | 低收益、触及移动代码 | 预计 10–20 % of 22 % |
| 异步（后台线程）写盘 | 需要有界队列与溢出策略 | 写盘只占快照的 1–2 %；不值得引入丢数据风险 |
| 对象池、渲染/阴影/LOD/批处理 | — | 无证据（池已存在；渲染未测） |

---

## 14. 变更清单、备份与 Git

**修改的已有文件**（修改前备份在 `Diagnostics/NPC_Performance_Optimisation/code_backup/`，已核对与 HEAD 逐字节相同）：

| 文件 | 改动与原因 |
|---|---|
| `Assets/Scripts/Capture/DataCaptureManager.cs` | `SampleNpcs` 行直写（`CsvRowBuilder`）、写盘改用 `WriteRows`、共有字段只转义一次、头骨/ID 缓存、并行 LOS 预处理 `RunLosBatch`、新序列化开关 `npcStateBatchedLineOfSight`、释放原生数组、开发期计时器（`#if`） |
| `Assets/Scripts/Capture/NpcLineOfSight.cs` | 新 `Query`（近远分段）；原实现原样保留为 `QueryExhaustive`；`TryGetNpcEyePointCached`；公开 `IsOwnOrParticipant` |
| `Assets/Scripts/Capture/CaptureCsvWriter.cs` | 新 `WriteRows`（与 `WriteLines` 同语义）；`Commit/CommitCore` 增加构建器分支；计时器（`#if`） |
| `Assets/Scripts/Capture/CaptureIO.cs` | 新 `AppendBuilder`（流式、同样的步骤/故障注入点） |
| `Assets/Scripts/MoveToTarget.cs` | 仅 3 行 `#if NPC_PERF_DIAG` 计数器（发布构建中编译为空） |

**新增文件**（各带 `.meta`）：`Assets/Scripts/Capture/CsvRowBuilder.cs`、`NpcLosBatch.cs`、`NpcPerfDiag.cs`；`Assets/Tests/GazeRecorder/NpcPerformanceTests.cs`、`NpcLosBatchTests.cs`；本报告所在目录 `Diagnostics/NPC_Performance_Optimisation/`。

**未改动**：任何场景、预制体、材质、NavMesh、`ProjectSettings`、参与者数据、NPC 仿真脚本逻辑。**没有提交。**

基准夹具、同步/运行脚本和**全部原始结果**（JSON 行、测试输出）保存在 `Diagnostics/NPC_Performance_Optimisation/benchmark/`（Unity 工程之外，不会被编译；见该目录的 `README.md`）。Unity 工程（`Assets/`）内不含任何基准代码，也未启用 `NPC_PERF_DIAG`。

---

## 15. 局限（诚实清单）
* 只有 Editor + `-nographics` 的 CPU 侧数据，没有渲染/GPU，没有头显。
* 合成建筑碰撞体、合成 NavMesh 平面，不是真实城市。
* 运行间波动 ±3–10 %；每个配置只有一次长运行（没有重复取均值）。
* 5 000 个 NPC 的 D 配置等个别运行可能因爬升超时而缺失，缺失即“未测”。
* 并行 LOS 的收益取决于 CPU 核数；Quest 上必须实测，若无收益或有负收益，用开关关闭。
* 步骤1 与基线之间每项改动的边际效果由分段计时给出，而不是分别做全套矩阵。
