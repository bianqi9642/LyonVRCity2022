#!/bin/bash
# usage: run_suite.sh <name> [scriptsdir]   full PlayMode suite of GazeRecorder.Tests against the given scripts (default repo)
S=/private/tmp/claude-501/-Users-qiaoy-Desktop-project-environment-LyonVRCity2022/fc47a207-d182-4410-a2e2-356aaa6b229c/scratchpad
T=$S/bproj
$S/bench_sync.sh ${2:-} > /dev/null
rm -f $T/Assets/Tests/GazeRecorder/NpcBench.cs* $T/Assets/Tests/GazeRecorder/NpcFmtProbe.cs*
cp /Users/qiaoy/Desktop/project/environment/LyonVRCity2022/Assets/Tests/GazeRecorder/NpcLifecycleTests.cs $T/Assets/Tests/GazeRecorder/NpcLifecycleTests.cs   # the ORIGINAL harness (the benchmark copy is patched)
[ -f $S/NpcLosBatchTests.cs ] && cp $S/NpcLosBatchTests.cs $T/Assets/Tests/GazeRecorder/
F=""; [ -n "$3" ] && F="-testFilter $3"
rm -f $S/results_$1.xml
/Applications/Unity/Hub/Editor/2022.3.53f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -projectPath $T -runTests -testPlatform PlayMode -assemblyNames GazeRecorder.Tests $F -testResults $S/results_$1.xml -logFile $S/unity_$1.log
echo "exit=$?"; grep -E "error CS" $S/unity_$1.log | sort -u | head -5
python3 -I - <<PY
import xml.etree.ElementTree as ET
try:
    r=ET.parse("$S/results_$1.xml").getroot(); print({k:r.get(k) for k in ('total','passed','failed','skipped','inconclusive')})
    for tc in r.iter('test-case'):
        if tc.get('result') not in ('Passed','Skipped','Ignored'):
            m=tc.find('.//message'); print('FAIL',tc.get('fullname'),(m.text or '')[:300].replace('\n',' ') if m is not None else '')
except Exception as e: print('no results',e)
PY
