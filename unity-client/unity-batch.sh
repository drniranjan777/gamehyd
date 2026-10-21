#!/usr/bin/env bash
# Headless Unity tasks for this project.
#   ./unity-batch.sh setup            regenerate the greybox scene + player settings
#   ./unity-batch.sh test EditMode    run EditMode tests (or PlayMode)
# Override the editor with UNITY_EXE. Only one Unity process may have the project open.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
UNITY_EXE="${UNITY_EXE:-G:/Unity/Editors/6000.6.7f1/Editor/Unity.exe}"
LOGS="$HERE/Logs"
mkdir -p "$LOGS"

case "${1:-}" in
  setup)
    "$UNITY_EXE" -batchmode -nographics -quit -projectPath "$HERE" \
      -executeMethod RWBR.EditorTools.ProjectSetup.CreateGreyboxScene -logFile "$LOGS/setup.log"
    ;;
  test)
    platform="${2:-EditMode}"
    "$UNITY_EXE" -batchmode -nographics -projectPath "$HERE" -runTests -testPlatform "$platform" \
      -testResults "$LOGS/results-$platform.xml" -logFile "$LOGS/test-$platform.log" || true
    grep -o '<test-run [^>]*' "$LOGS/results-$platform.xml" | grep -oE '(result|total|passed|failed)="[^"]*"' | tr '\n' ' '
    echo
    grep -q 'result="Passed"' <(grep -o '<test-run [^>]*' "$LOGS/results-$platform.xml")
    ;;
  *)
    echo "usage: $0 setup | test [EditMode|PlayMode]" >&2
    exit 2
    ;;
esac
