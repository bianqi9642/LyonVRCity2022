#!/bin/bash
# usage: bench_sync.sh <scripts_source_dir>  (default: repo Assets/Scripts)
P=/Users/qiaoy/Desktop/project/environment/LyonVRCity2022
S=/private/tmp/claude-501/-Users-qiaoy-Desktop-project-environment-LyonVRCity2022/fc47a207-d182-4410-a2e2-356aaa6b229c/scratchpad
T=$S/bproj
SRC=${1:-$P/Assets/Scripts}
rsync -a --delete "$SRC/" $T/Assets/Scripts/
cp $P/Assets/Scripts.meta $T/Assets/Scripts.meta
rsync -a --exclude 'NpcBench.cs*' --exclude 'NpcLifecycleTests.cs' $P/Assets/Tests/ $T/Assets/Tests/
cp $P/Assets/Tests.meta $T/Assets/Tests.meta 2>/dev/null
rsync -a $P/ProjectSettings/ $T/ProjectSettings/
rsync -a --delete $P/docs/ $T/docs/
cp $P/Packages/manifest.json $P/Packages/packages-lock.json $T/Packages/
# real NPC prefab + EasyNpcs scripts
rsync -a $P/Assets/EasyNpcs/ $T/Assets/EasyNpcs/; cp $P/Assets/EasyNpcs.meta $T/Assets/EasyNpcs.meta 2>/dev/null
mkdir -p $T/Assets/Prefabs; cp $P/Assets/Prefabs/Npc.prefab $P/Assets/Prefabs/Npc.prefab.meta $T/Assets/Prefabs/
cp $P/Assets/Prefabs.meta $T/Assets/Prefabs.meta 2>/dev/null
while read f; do [ -f "$P/$f" ] && mkdir -p "$T/$(dirname $f)" && cp "$P/$f" "$P/$f.meta" "$T/$(dirname $f)/" 2>/dev/null; done < $S/npc_deps.txt
echo "-define:NPC_PERF_DIAG" > $T/Assets/csc.rsp
# patched copy of the lifecycle test harness (templateOverride, S3-like multi-attractor itineraries)
python3 -I $S/patch_harness.py $P/Assets/Tests/GazeRecorder/NpcLifecycleTests.cs $T/Assets/Tests/GazeRecorder/NpcLifecycleTests.cs
echo synced
