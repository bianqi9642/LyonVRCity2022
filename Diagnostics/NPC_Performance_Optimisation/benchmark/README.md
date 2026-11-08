# 基准工具与原始结果（不属于 Unity 工程，不会被 Unity 编译）

* `harness/NpcBench.cs` —— 基准测试（`[UnityTest] NpcLifecycleTests.Bench`），依赖仓库里的 `NpcLifecycleTests` 夹具；`patch_harness.py` 在**临时工程**里给该夹具打补丁（`templateOverride`、多吸引点行程）。
* `harness/bench_sync.sh / bench_run.sh / run_suite.sh` —— 把仓库脚本同步到临时 Unity 工程（含 `Assets/csc.rsp` = `-define:NPC_PERF_DIAG`），批处理运行；**路径写死为本次会话的临时目录，需要按需修改。**
* `results/*.jsonl` —— 每行一次运行的原始结果（`bench_matrix_base` = 基线、`bench_matrix_opt` = 步骤1、`bench_matrix_opt2` = 步骤2、`bench_matrix_opt3` = 最终；`bench_abl2000/1000` = 消融）。
* `results/run_suite_*.out` —— 全套 PlayMode 测试输出；`flake_results.txt` —— 两个时序敏感测试的重复运行统计。
* 仓库中的源码**没有**启用 `NPC_PERF_DIAG`，也不含任何基准代码。
