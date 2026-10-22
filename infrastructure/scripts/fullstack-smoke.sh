#!/usr/bin/env bash
# Phase 3 and Phase 5 exit checks, end to end on one machine:
#   fresh PostgreSQL database -> platform API -> managed match server (tickets required) ->
#   4 bots: guest sign-up, matchmaking, signed seat, play -> signed result -> XP, coins, history in the database;
#   then a dev-store purchase verified twice (granted once), the first admin created with the CLI, a TOTP sign-in
#   and an audited refund.
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
JWT_SECRET=$(node -e "console.log(require('crypto').randomBytes(32).toString('hex'))")
ADMIN_KEY=$(node -e "console.log(require('crypto').randomBytes(32).toString('hex'))")
DEV_SECRET=$(node -e "console.log(require('crypto').randomBytes(24).toString('hex'))")
export DATABASE_URL="$APP_URL" JWT_SECRET SERVER_SHARED_SECRET="$SERVER_SECRET" TICKET_SECRET_HEX="$TICKET_SECRET" \
  ADMIN_TOTP_KEY_HEX="$ADMIN_KEY" DEV_PURCHASES_ENABLED=true DEV_PURCHASE_SECRET="$DEV_SECRET"

echo "== api on :$API_PORT"
PORT=$API_PORT MATCH_CAPACITY=4 MATCH_MIN_PLAYERS=4 MATCH_FILL_WAIT_SECONDS=5 DEFAULT_MAP_VERSION=greybox-block-40-v2 \
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

echo "== store purchase and admin refund (Phase 5)"
API="http://localhost:$API_PORT/api/v1"
field() { node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s)[process.argv[1]]))" "$1"; }
post() { curl -s -X POST "$API$1" -H 'content-type: application/json' "${@:3}" -d "$2"; }
TOKEN=$(post /auth/guest '{"displayName":"Smoke_Buyer"}' | field accessToken)
ORDER=$(post /store/orders '{"productKey":"outfit_lakeside_runner","platform":"DEV","idempotencyKey":"smoke-order-1"}' -H "authorization: Bearer $TOKEN" | field orderId)
RECEIPT=$(cd "$ROOT/apps/api" && node -e "console.log(require('./dist/store/verifiers.js').devReceipt(process.argv[1], process.argv[2], 'outfit_lakeside_runner', 'smoke-tx-1'))" "$DEV_SECRET" "$ORDER")
for attempt in 1 2; do
  check "verify attempt $attempt" "$(post "/store/orders/$ORDER/verify" "{\"proof\":\"$RECEIPT\"}" -H "authorization: Bearer $TOKEN" | field status)" FULFILLED
done
ADMIN_OUT=$(cd "$ROOT/apps/api" && ADMIN_PASSWORD="smoke-admin-password-1" node dist/admin-cli.js --email ops@smoke.test --name "Smoke Ops" --role SUPER_ADMIN)
TOTP_SECRET=$(echo "$ADMIN_OUT" | sed -n 's/^TOTP secret (shown once): //p')
CODE=$(cd "$ROOT/apps/api" && node -e "const c=require('./dist/admin/admin-crypto.js');console.log(c.totpAt(c.base32Decode(process.argv[1]),c.currentStep()))" "$TOTP_SECRET")
ADMIN_TOKEN=$(post /admin/auth/login "{\"email\":\"ops@smoke.test\",\"password\":\"smoke-admin-password-1\",\"code\":\"$CODE\"}" | field token)
check "admin refund" "$(post "/admin/orders/$ORDER/refund" '{"reason":"smoke test refund"}' -H "authorization: Admin $ADMIN_TOKEN" | field status)" REFUNDED
check "orders refunded" "$(q "SELECT count(*) FROM orders WHERE status = 'REFUNDED'")" 1
check "payments recorded once" "$(q "SELECT count(*) FROM payment_transactions WHERE provider_transaction_id = 'smoke-tx-1'")" 1
check "refunded item revoked" "$(q "SELECT count(*) FROM player_inventory WHERE source = 'PURCHASE'")" 0
check "refund audited" "$(q "SELECT count(*) FROM admin_audit_logs WHERE action = 'order.refund' AND actor LIKE 'admin:%'")" 1
check "admin sign-in audited" "$(q "SELECT count(*) FROM admin_audit_logs WHERE action = 'admin.login'")" 1
check "ledger drift after store" "$(q "SELECT count(*) FROM wallet_ledger_drift")" 0

if [[ $fail -eq 0 ]]; then
  (cd "$ROOT/database" && node --import tsx src/testdb-cli.ts drop "$DB")
  echo "SMOKE PASSED"
else
  echo "SMOKE FAILED (database $DB kept for inspection; logs in $LOGS)"
  exit 1
fi
