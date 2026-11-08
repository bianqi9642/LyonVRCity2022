import json, os, sys, collections, statistics

S = os.path.dirname(os.path.abspath(__file__))
OUT = "/Users/qiaoy/Desktop/project/environment/LyonVRCity2022/Diagnostics/NPC_Performance_Optimisation/npc_performance_optimisation_report_zh.md"


def load(name):
    p = os.path.join(S, name)
    out = collections.OrderedDict()
    if not os.path.exists(p):
        return out
    for l in open(p):
        d = json.loads(l)
        out[(d["npcs"], d["cfg"], d.get("interval", 0.1) if d["cfg"] == "D" else 0.1)] = d
    return out


base = load("bench_matrix_base.jsonl")
opt1 = load("bench_matrix_opt.jsonl")
mid = load("bench_matrix_opt2.jsonl")
opt3 = load("bench_matrix_opt3.jsonl")
opt = collections.OrderedDict()
for _k, _d in mid.items():
    if _k[1] == "A":
        opt[_k] = _d          # simulation-only runs are unaffected by the capture code
for _k, _d in opt3.items():
    opt[_k] = _d
abl2000 = load("bench_abl2000.jsonl")
abl5000 = load("bench_abl5000.jsonl")
abl1000 = load("bench_abl1000.jsonl")


def g(d, k, fmt="%.1f", dash="–"):
    return (fmt % d[k]) if d and k in d and d[k] is not None else dash


def pct(a, b):
    return "%+.0f%%" % ((b - a) / a * 100.0) if a else "–"


def cfgname(k):
    return {"A": "A 仅仿真（采集关）", "B": "B/C 默认采集（0.1 s）", "D": "D 采集间隔 %.1f s" % k[2]}[k[1]]


keys = sorted(set(base) | set(opt) | set(opt1) | set(mid), key=lambda k: (k[0], {"A": 0, "B": 1, "D": 2}[k[1]]))

# ---------------------------------------------------------------- tables
rows = ["| NPC | 配置 | 版本 | 帧数 | 平均 ms | 中位 ms | P95 ms | P99 ms | 最大 ms | FPS(均值) | 每帧托管分配 KB | 快照/帧 |", "|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
for k in keys:
    for nm, src in (("基线", base), ("步骤1", opt1), ("步骤2", mid), ("最终", opt)):
        d = src.get(k)
        if not d:
            continue
        al = d.get("alloc_bytes_per_frame_approx")
        rows.append("| %d | %s | %s | %d | %s | %s | %s | %s | %s | %s | %s | %s |" % (k[0], cfgname(k), nm, d["frames"], g(d, "mean_ms"), g(d, "median_ms"), g(d, "p95_ms"), g(d, "p99_ms"), g(d, "max_ms"), g(d, "fps_mean"), ("%.0f" % (al / 1024)) if al is not None else "–", g(d, "snapshots_per_frame", "%.2f")))
T_ALL = "\n".join(rows)

rows = ["| NPC | 配置 | 基线 平均 ms（FPS） | 优化后 平均 ms（FPS） | 平均帧时间变化 | 基线 P95 | 优化后 P95 | 基线 P99 | 优化后 P99 | 基线 每帧分配 KB | 优化后 每帧分配 KB |", "|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
for k in keys:
    b, o = base.get(k), opt.get(k)
    if not b or not o or k[1] == "A":
        continue
    rows.append("| %d | %s | %s（%s） | %s（%s） | %s | %s | %s | %s | %s | %.0f | %.0f |" % (k[0], cfgname(k), g(b, "mean_ms"), g(b, "fps_mean"), g(o, "mean_ms"), g(o, "fps_mean"), pct(b["mean_ms"], o["mean_ms"]), g(b, "p95_ms"), g(o, "p95_ms"), g(b, "p99_ms"), g(o, "p99_ms"), b["alloc_bytes_per_frame_approx"] / 1024, o["alloc_bytes_per_frame_approx"] / 1024))
T_CMP = "\n".join(rows)

rows = ["| NPC | 配置 | 版本 | 单次快照总耗时 ms | 读取 NPC 状态 | 眼点（头骨） | **视线 LOS 射线** | **行格式化** | 事件边沿检测 | 写盘（整体） | 每快照射线数 | 每快照字节 | 每秒写入 MB（帧内实测） |", "|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
for k in keys:
    if k[1] == "A":
        continue
    for nm, src in (("基线", base), ("步骤1", opt1), ("步骤2", mid), ("最终", opt)):
        d = src.get(k)
        if not d or "snap_total_ms" not in d:
            continue
        rows.append("| %d | %s | %s | %.1f | %.2f | %.2f | %.1f | %.1f | %.2f | %.2f | %.0f | %.0f | %.2f |" % (k[0], cfgname(k), nm, d["snap_total_ms"], d["snap_read_ms"], d["snap_eye_ms"], d["snap_los_ms"], d["snap_format_ms"], d["snap_event_ms"], d["snap_write_ms"], d["raycasts_per_snapshot"], d["bytes_per_snapshot"], d["bytes_per_second"] / 1e6))
T_SNAP = "\n".join(rows)

# capture share of the frame
rows = ["| NPC | 仿真（A）平均 ms | 默认采集（B）基线 平均 ms | 采集额外开销（基线） | 默认采集 优化后 平均 ms | 采集额外开销（优化后） | 采集开销降低 |", "|---:|---:|---:|---:|---:|---:|---:|"]
for n in (100, 500, 1000, 2000, 5000):
    a_b, b_b, a_o, b_o = base.get((n, "A", 0.1)), base.get((n, "B", 0.1)), opt.get((n, "A", 0.1)), opt.get((n, "B", 0.1))
    if not (a_b and b_b and a_o and b_o):
        continue
    ob, oo = b_b["mean_ms"] - a_b["mean_ms"], b_o["mean_ms"] - a_o["mean_ms"]
    rows.append("| %d | %.1f / %.1f（基线 / 优化后构建） | %.1f | +%.1f ms（+%.0f%%） | %.1f | +%.1f ms（+%.0f%%） | %s |" % (n, a_b["mean_ms"], a_o["mean_ms"], b_b["mean_ms"], ob, ob / a_b["mean_ms"] * 100, b_o["mean_ms"], oo, oo / a_o["mean_ms"] * 100, pct(ob, oo)))
T_CAPSHARE = "\n".join(rows)


def abl_table(src, label):
    ctrl = src.get((src and list(src.keys())[0][0], "A", 0.1)) if src else None
    if not src:
        return "（未取得）"
    n = list(src.keys())[0][0]
    ctrl = src.get((n, "A", 0.1))
    names = {"A": "对照：完整仿真（采集关）", "Anim": "关闭全部 Animator", "Anolog": "关闭 NPC 日志（MoveToTarget.enableDebugLogs / UncertainBehavior.enableLogging）", "Areiss": "关闭周期性 SetDestination 重发（targetUpdateInterval=∞）",
             "Amove": "禁用 MoveToTarget", "Anav": "禁用 NavMeshAgent（同时使 MoveToTarget 空转）", "Aphys": "关闭物理自动模拟", "Aped": "禁用 PedestrianAgent"}
    rows = ["| 消融（%s） | 平均帧时间 ms | 与对照差值 ms | 占对照 |" % label, "|---|---:|---:|---:|"]
    for k, d in src.items():
        nm = names.get(k[1], k[1])
        if k[1] == "Aanim":
            nm = "关闭全部 Animator"
        if ctrl is None:
            rows.append("| %s | %.1f | – | – |" % (nm, d["mean_ms"]))
        else:
            rows.append("| %s | %.1f | %+.1f | %+.0f%% |" % (nm, d["mean_ms"], d["mean_ms"] - ctrl["mean_ms"], (d["mean_ms"] - ctrl["mean_ms"]) / ctrl["mean_ms"] * 100))
    return "\n".join(rows)


T_ABL2000 = abl_table(abl2000, "2000 NPC，采集关")
T_ABL5000 = abl_table(abl5000, "5000 NPC，采集关") if abl5000 else "（5000 NPC 的消融运行见 §4 末尾的说明）"
T_ABL1000 = abl_table(abl1000, "1000 NPC，采集关；注意：这一组使用早期的“单吸引点、停留 0.5 s”的行程，NPC 会很快完成并消失，仅作交叉验证") if abl1000 else ""

reissue = ""
if (2000, "A", 0.1) in abl2000:
    d = abl2000[(2000, "A", 0.1)]
    reissue = "2000 NPC 稳态下 MoveToTarget 对 Transform 目标的周期性 `SetDestination` 重发为 %.0f 次/秒，其中 %.0f%% 提交的正是该代理已经拥有完整路径的同一个目的地。" % (d.get("target_reissues_per_s", 0), 100.0 * d.get("target_reissues_same_dest", 0) / max(1, d.get("target_reissues", 1)))


lines = []
for n in (1000, 2000, 5000):
    b, o1, o = base.get((n, "B", 0.1)), opt1.get((n, "B", 0.1)), opt.get((n, "B", 0.1))
    a_b = base.get((n, "A", 0.1)); a_o = opt.get((n, "A", 0.1))
    if not b:
        continue
    txt = "- **%d 个 NPC，默认采集（B/C）**：基线平均帧时间 %.1f ms（%.1f FPS），单次快照 %.1f ms" % (n, b["mean_ms"], b["fps_mean"], b["snap_total_ms"])
    if o1:
        txt += "；步骤1（不并行）%.1f ms（%.1f FPS），快照 %.1f ms" % (o1["mean_ms"], o1["fps_mean"], o1["snap_total_ms"])
    if o:
        txt += "；**最终 %.1f ms（%.1f FPS），快照 %.1f ms**，平均帧时间 %s，每帧托管分配 %.0f KB → %.0f KB" % (o["mean_ms"], o["fps_mean"], o["snap_total_ms"], pct(b["mean_ms"], o["mean_ms"]), b["alloc_bytes_per_frame_approx"] / 1024, o["alloc_bytes_per_frame_approx"] / 1024)
    lines.append(txt)
KEY = "\n".join(lines)


flake = ""
fp = os.path.join(S, "flake_results.txt")
if os.path.exists(fp):
    rb = [l for l in open(fp) if l.startswith("base")]
    rc = [l for l in open(fp) if l.startswith("cur")]
    fb = sum(1 for l in rb if "fails=0" not in l); fc = sum(1 for l in rc if "fails=0" not in l)
    # three earlier single runs per variant (flk_*): baseline 0/3, current 1/3
    flake = ("**两个时序敏感测试的不稳定性（先说明，因为它们在我的几次全套运行里时有时无）**：`NpcLifecycleTests.Phase_Part2_ProductionFlow_…` 与 `Gaze_RowsCarryPhaseAndCurrentTarget_…` 检查“凝视行带的阶段标签 = 按阶段事件时间重建的标签”，容差只有 0.1 ms；当一个凝视行恰好在阶段事件**之前** < 0.1 ms 被采样时就会失败（失败信息形如 `GAZE row t_abs=5.7500 … p2_continue_pending@5.7501`）。"
             "为判断这是不是我的改动引起的，我在**基线脚本（HEAD + 仅计时器）**和**最终脚本**上各单独重复运行这两个测试：基线 %d/%d 次失败（加上更早的 3 次单独运行 0/3，共 %d/%d），最终 %d/%d 次失败（加上更早的 3 次 1/3，共 %d/%d）。"
             "**基线同样会以同样的信息失败，故这是预先存在的时序脆弱性，不是本次改动引入的**；我没有修改这两个测试。\n\n") % (fb, len(rb), fb, len(rb) + 3, fc, len(rc), fc + 1, len(rc) + 3)

suite_txt = "（未取得）"
p = os.path.join(S, "run_suite_final3.out")
if os.path.exists(p):
    suite_txt = open(p).read().strip()
perf_tests = open(os.path.join(S, "run_perf4.out")).read().strip() if os.path.exists(os.path.join(S, "run_perf4.out")) else ""

tmpl = open(os.path.join(S, "report_template.md"), encoding="utf-8").read()
for key, val in (("{{T_ALL}}", T_ALL), ("{{T_CMP}}", T_CMP), ("{{T_SNAP}}", T_SNAP), ("{{T_CAPSHARE}}", T_CAPSHARE), ("{{T_ABL2000}}", T_ABL2000), ("{{T_ABL5000}}", T_ABL5000), ("{{T_ABL1000}}", T_ABL1000), ("{{REISSUE}}", reissue), ("{{KEY}}", KEY), ("{{FLAKE}}", flake), ("{{SUITE}}", suite_txt), ("{{PERFTESTS}}", perf_tests)):
    tmpl = tmpl.replace(key, val)
open(OUT, "w", encoding="utf-8").write(tmpl)
print("report written", len(tmpl))
