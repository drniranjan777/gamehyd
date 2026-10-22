#!/usr/bin/env bash
# Phase 3 exit gate, end to end on one machine:
#   fresh PostgreSQL database -> platform API -> managed match server (tickets required) ->
#   4 bots: guest sign-up, matchmaking, signed seat, play -> signed result -> XP, coins, history in the database.
# Needs: TEST_ADMIN_DATABASE_URL (superuser URL), psql, built API (pnpm --filter @rwbr/api build) and
# .NET binaries (dotnet build game/dedicated-server/RWBR.MatchServer.slnx -c Release).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
: "${TEST_ADMIN_DATABASE_URL:?set TEST_ADMIN_DATABASE_URL, e.g. postgres://postgres@localhost:55432/postgres}"
PSQL="${PSQL:-psql}"
EXT=""; [[ "${OS:-}" == "Windows_NT" ]] && EXT=".exe"
BIN="$ROOT/game/dedicated-server/artifacts/bin"
DB=rwbr_smoke
API_PORT="${API_PORT:-3914}"
GAME_PORT="${GAME_PORT:-47790}"
LOGS="$ROOT/.local/smoke"
mkdir -p "$LOGS"

PIDS=()
cleanup() { for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null || true; done; }
trap cleanup EXIT

echo "== database"
URLS=$(cd "$ROOT/database" && node --import tsx src/testdb-cli.ts create "$DB")
APP_URL=$(node -e "console.log(JSON.parse(process.argv[1]).appUrl)" "$URLS")
ADMIN_DB_URL=$(node -e "const u=new URL(process.argv[1]);u.pathname='/$DB';console.log(u.toString())" "$TEST_ADMIN_DATABASE_URL")

# Fresh secrets for this run only.
SERVER_SECRET=$(node -e "console.log(require('crypto').randomBytes(32).toString('hex'))")
TICKET_SECRET=$(node -e "console.log(require('crypto').randomBytes(32).toString('hex'))")

echo "== api on :$API_PORT"
PORT=$API_PORT DATABASE_URL="$APP_URL" JWT_SECRET="$(node -e "console.log(require('crypto').randomBytes(32).toString('hex'))")" \
  SERVER_SHARED_SECRET="$SERVER_SECRET" TICKET_SECRET_HEX="$TICKET_SECRET" \
  MATCH_CAPACITY=4 MATCH_MIN_PLAYERS=4 MATCH_FILL_WAIT_SECONDS=5 DEFAULT_MAP_VERSION=greybox-block-40-v2 \
  node "$ROOT/apps/api/dist/main.js" > "$LOGS/api.log" 2>&1 &
PIDS+=($!)
for i in $(seq 1 40); do curl -sf "http://localhost:$API_PORT/api/v1/health/ready" >/dev/null && break; sleep 0.5; done

echo "== managed match server on udp/$GAME_PORT"
RWBR_SERVER_SHARED_SECRET="$SERVER_SECRET" RWBR_TICKET_SECRET_HEX="$TICKET_SECRET" \
  "$BIN/RWBR.MatchServer/release/RWBR.MatchServer$EXT" --api-url "http://localhost:$API_PORT/api/v1" \
  --address "127.0.0.1:$GAME_PORT" --port "$GAME_PORT" --fast --max-matches 1 > "$LOGS/server.log" 2>&1 &
SERVER_PID=$!
PIDS+=($SERVER_PID)

echo "== 4 bots through the platform"
"$BIN/RWBR.Bots/release/RWBR.Bots$EXT" --api "http://localhost:$API_PORT/api/v1" --count 4 --timeout 120 | tee "$LOGS/bots.log"
wait $SERVER_PID || true

echo "== checks"
q() { "$PSQL" "$ADMIN_DB_URL" -tAc "$1"; }
fail=0
check() { if [[ "$2" == "$3" ]]; then echo "  ok   $1 ($2)"; else echo "  FAIL $1: got '$2', want '$3'"; fail=1; fi; }
check "completed matches" "$(q "SELECT count(*) FROM matches WHERE status = 'COMPLETED'")" 1
check "players with a placement" "$(q "SELECT count(*) FROM match_participants WHERE placement IS NOT NULL")" 4
check "players with XP" "$(q "SELECT count(*) FROM player_profiles WHERE xp > 0 AND matches_played = 1")" 4
check "exactly one winner" "$(q "SELECT count(*) FROM player_profiles WHERE wins = 1")" 1
check "reward ledger rows" "$(q "SELECT count(*) FROM wallet_transactions WHERE reason = 'MATCH_REWARD'")" 4
check "ledger drift" "$(q "SELECT count(*) FROM wallet_ledger_drift")" 0
check "accepted results" "$(q "SELECT count(*) FROM match_verification_records WHERE outcome = 'ACCEPTED'")" 1
check "rejected results" "$(q "SELECT count(*) FROM match_verification_records WHERE outcome = 'REJECTED'")" 0

if [[ $fail -eq 0 ]]; then
  (cd "$ROOT/database" && node --import tsx src/testdb-cli.ts drop "$DB")
  echo "SMOKE PASSED"
else
  echo "SMOKE FAILED (database $DB kept for inspection; logs in $LOGS)"
  exit 1
fi
