#!/usr/bin/env bash
# Headless Unity tasks for this project.
#   ./unity-batch.sh setup            regenerate the greybox scene + player settings
#   ./unity-batch.sh test EditMode    run EditMode tests (or PlayMode)
#   ./unity-batch.sh characters       build the character controller/prefab from Assets/ThirdParty/Mixamo
#   ./unity-batch.sh android          development APK -> Builds/Android/rwbr-practice.apk
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
    # A run that never starts (e.g. compile errors) must not report the previous run's results.
    rm -f "$LOGS/results-$platform.xml"
    "$UNITY_EXE" -batchmode -nographics -projectPath "$HERE" -runTests -testPlatform "$platform" \
      -testResults "$LOGS/results-$platform.xml" -logFile "$LOGS/test-$platform.log" || true
    grep -o '<test-run [^>]*' "$LOGS/results-$platform.xml" | grep -oE '(result|total|passed|failed)="[^"]*"' | tr '\n' ' '
    echo
    grep -q 'result="Passed"' <(grep -o '<test-run [^>]*' "$LOGS/results-$platform.xml")
    ;;
  characters)
    # Builds the animator controller (and the character prefab once the Mixamo files are in Assets/ThirdParty/Mixamo).
    "$UNITY_EXE" -batchmode -nographics -quit -projectPath "$HERE"       -executeMethod RWBR.EditorTools.CharacterImport.BuildFromCommandLine -logFile "$LOGS/characters.log" || status=$?
    grep -A30 "RWBR characters:" "$LOGS/characters.log" | grep -E "RWBR characters|warning:|error:" || true
    exit "${status:-0}"
    ;;
  android)
    "$UNITY_EXE" -batchmode -nographics -quit -projectPath "$HERE" -buildTarget Android       -executeMethod RWBR.EditorTools.ProjectSetup.BuildAndroid -logFile "$LOGS/android.log"
    grep -E "RWBR: Android build" "$LOGS/android.log" || { grep -E "error|Error" "$LOGS/android.log" | tail -20; exit 1; }
    ;;
  *)
    echo "usage: $0 setup | test [EditMode|PlayMode] | characters | android" >&2
    exit 2
    ;;
esac
