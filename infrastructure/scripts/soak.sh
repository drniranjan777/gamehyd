#!/usr/bin/env bash
# Full-length match soak: one match server, N bots with simulated network impairment, default (production)
# zone timings. Writes the server's JSON report to $OUT and prints the bots' summary.
#   infrastructure/scripts/soak.sh [players=30] [loss=0.03] [latency_ms=60] [jitter_ms=30]
# Build first: dotnet build game/dedicated-server/RWBR.MatchServer.slnx -c Release
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BIN="$ROOT/game/dedicated-server/artifacts/bin"
EXT=""; [[ "${OS:-}" == "Windows_NT" ]] && EXT=".exe"
SERVER="$BIN/RWBR.MatchServer/release/RWBR.MatchServer$EXT"
BOTS="$BIN/RWBR.Bots/release/RWBR.Bots$EXT"

PLAYERS="${1:-30}"; LOSS="${2:-0.03}"; LATENCY="${3:-60}"; JITTER="${4:-30}"
PORT="${PORT:-7794}"
OUT="${OUT:-$ROOT/.local/soak-$PLAYERS.json}"
mkdir -p "$(dirname "$OUT")"

"$SERVER" --port "$PORT" --players "$PLAYERS" > "$OUT" 2> "${OUT%.json}.log" &
SERVER_PID=$!
trap 'kill $SERVER_PID 2>/dev/null || true' EXIT
sleep 1

"$BOTS" --port "$PORT" --count "$PLAYERS" --timeout 900 --loss "$LOSS" --latency "$LATENCY" --jitter "$JITTER" | head -4
wait $SERVER_PID
echo "server report: $OUT"
