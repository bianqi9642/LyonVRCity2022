#!/bin/bash
# usage: bench_run.sh "<runs>" <tag> [warm] [meas] [scriptsdir]
S=/private/tmp/claude-501/-Users-qiaoy-Desktop-project-environment-LyonVRCity2022/fc47a207-d182-4410-a2e2-356aaa6b229c/scratchpad
T=$S/bproj
$S/bench_sync.sh ${5:-} > /dev/null
cp $S/NpcBench.cs $T/Assets/Tests/GazeRecorder/NpcBench.cs
export NPCBENCH_RUNS="$1" NPCBENCH_TAG="$2" NPCBENCH_WARM="${3:-8}" NPCBENCH_MEAS="${4:-20}" NPCBENCH_OUT="$S/bench_$2.jsonl"
rm -f $S/results_bench_$2.xml
/Applications/Unity/Hub/Editor/2022.3.53f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -projectPath $T -runTests -testPlatform PlayMode -assemblyNames GazeRecorder.Tests -testFilter "NpcLifecycleTests.Bench" -testResults $S/results_bench_$2.xml -logFile $S/unity_bench_$2.log
echo "exit=$?"
grep -E "error CS" $S/unity_bench_$2.log | head -5
python3 -I - <<PY
import xml.etree.ElementTree as ET
try:
    r=ET.parse("$S/results_bench_$2.xml").getroot(); print({k:r.get(k) for k in ('total','passed','failed')})
    for tc in r.iter('test-case'):
        m=tc.find('.//message'); print(tc.get('result'),(m.text or '')[:400] if m is not None else '')
except Exception as e: print('no results',e)
PY
