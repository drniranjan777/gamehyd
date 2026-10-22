#!/usr/bin/env bash
# Runs the API load test against a fresh database and a production build of the API on this machine.
#   USERS=200 SECONDS_=60 TEST_ADMIN_DATABASE_URL=postgres://postgres@localhost:55432/postgres infrastructure/scripts/load-test.sh
# Needs: TEST_ADMIN_DATABASE_URL and a built API (pnpm --filter @rwbr/api build).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
: "${TEST_ADMIN_DATABASE_URL:?set TEST_ADMIN_DATABASE_URL}"
DB=rwbr_load
API_PORT="${API_PORT:-3915}"
LOGS="$ROOT/.local/load"
mkdir -p "$LOGS"

PIDS=()
cleanup() {
  for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null || true; done
  (cd "$ROOT/database" && node --import tsx src/testdb-cli.ts drop "$DB" >/dev/null 2>&1) || true
}
trap cleanup EXIT

URLS=$(cd "$ROOT/database" && node --import tsx src/testdb-cli.ts create "$DB")
hex() { node -e "console.log(require('crypto').randomBytes($1).toString('hex'))"; }
export DATABASE_URL=$(node -e "console.log(JSON.parse(process.argv[1]).appUrl)" "$URLS")
export JWT_SECRET=$(hex 32) SERVER_SHARED_SECRET=$(hex 32) TICKET_SECRET_HEX=$(hex 32) ADMIN_TOTP_KEY_HEX=$(hex 32)
# Rate limits are per IP; every virtual player shares this machine's address, so they are off for the test.
PORT=$API_PORT RATE_LIMIT_ENABLED=false LOG_REQUESTS=false RECONCILE_INTERVAL_SECONDS=0 \
  node "$ROOT/apps/api/dist/main.js" > "$LOGS/api.log" 2>&1 &
PIDS+=($!)
for i in $(seq 1 40); do curl -sf "http://localhost:$API_PORT/api/v1/health/ready" >/dev/null && break; sleep 0.5; done

node "$ROOT/infrastructure/scripts/load-test.mjs" --api "http://localhost:$API_PORT/api/v1" \
  --users "${USERS:-200}" --seconds "${SECONDS_:-60}" | tee "$LOGS/result.txt"
