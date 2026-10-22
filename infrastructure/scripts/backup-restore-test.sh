#!/usr/bin/env bash
# Backup and restore drill (Phase 6): builds a database with real activity (players, a dev purchase, a refund,
# audit rows), takes a pg_dump, restores it into a new database, and proves the restore is complete and consistent:
# same row counts in every table, migrations recorded, append-only triggers still active, no wallet ledger drift.
#   TEST_ADMIN_DATABASE_URL=postgres://postgres@localhost:55432/postgres infrastructure/scripts/backup-restore-test.sh
# Needs: pg_dump, pg_restore and psql (PostgreSQL 18 client) on PATH, and a built API.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
: "${TEST_ADMIN_DATABASE_URL:?set TEST_ADMIN_DATABASE_URL}"
SRC=rwbr_backup_src
DST=rwbr_backup_restored
API_PORT="${API_PORT:-3916}"
LOGS="$ROOT/.local/backup"
mkdir -p "$LOGS"
dburl() { node -e "const u=new URL(process.argv[1]);u.pathname='/'+process.argv[2];console.log(u.toString())" "$TEST_ADMIN_DATABASE_URL" "$1"; }

PIDS=()
cleanup() {
  for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null || true; done
  (cd "$ROOT/database" && node --import tsx src/testdb-cli.ts drop "$SRC" >/dev/null 2>&1) || true
  psql "$TEST_ADMIN_DATABASE_URL" -qc "DROP DATABASE IF EXISTS $DST" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "== source database with activity"
URLS=$(cd "$ROOT/database" && node --import tsx src/testdb-cli.ts create "$SRC")
hex() { node -e "console.log(require('crypto').randomBytes($1).toString('hex'))"; }
DEV_SECRET=$(hex 24)
export DATABASE_URL=$(node -e "console.log(JSON.parse(process.argv[1]).appUrl)" "$URLS")
export JWT_SECRET=$(hex 32) SERVER_SHARED_SECRET=$(hex 32) TICKET_SECRET_HEX=$(hex 32) ADMIN_TOTP_KEY_HEX=$(hex 32) \
  DEV_PURCHASES_ENABLED=true DEV_PURCHASE_SECRET="$DEV_SECRET"
PORT=$API_PORT LOG_REQUESTS=false node "$ROOT/apps/api/dist/main.js" > "$LOGS/api.log" 2>&1 &
PIDS+=($!)
for i in $(seq 1 40); do curl -sf "http://localhost:$API_PORT/api/v1/health/ready" >/dev/null && break; sleep 0.5; done

API="http://localhost:$API_PORT/api/v1"
field() { node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s)[process.argv[1]]))" "$1"; }
post() { curl -s -X POST "$API$1" -H 'content-type: application/json' "${@:3}" -d "$2"; }
for i in 1 2 3 4 5; do post /auth/guest "{\"displayName\":\"Backup_$i\"}" > /dev/null; done
TOKEN=$(post /auth/guest '{"displayName":"Backup_Buyer"}' | field accessToken)
ORDER=$(post /store/orders '{"productKey":"gems_100","platform":"DEV","idempotencyKey":"backup-order-1"}' -H "authorization: Bearer $TOKEN" | field orderId)
RECEIPT=$(cd "$ROOT/apps/api" && node -e "console.log(require('./dist/store/verifiers.js').devReceipt(process.argv[1], process.argv[2], 'gems_100', 'backup-tx-1'))" "$DEV_SECRET" "$ORDER")
post "/store/orders/$ORDER/verify" "{\"proof\":\"$RECEIPT\"}" -H "authorization: Bearer $TOKEN" > /dev/null
BODY='{"eventId":"backup-evt-1","type":"REFUND","transactionId":"backup-tx-1"}'
SIG=$(node -e "console.log(require('crypto').createHmac('sha256',process.argv[1]).update(process.argv[2]).digest('hex'))" "$DEV_SECRET" "$BODY")
curl -s -X POST "$API/webhooks/dev" -H 'content-type: application/json' -H "x-rwbr-dev-signature: $SIG" -d "$BODY" > /dev/null
kill "${PIDS[0]}" 2>/dev/null || true

echo "== pg_dump (custom format) and restore"
START=$(date +%s)
pg_dump --format=custom --no-owner --file "$LOGS/backup.dump" "$(dburl $SRC)"
psql "$TEST_ADMIN_DATABASE_URL" -qc "DROP DATABASE IF EXISTS $DST" -c "CREATE DATABASE $DST"
pg_restore --no-owner --exit-on-error --dbname "$(dburl $DST)" "$LOGS/backup.dump"
echo "  dump $(du -h "$LOGS/backup.dump" | cut -f1), dump + restore in $(( $(date +%s) - START )) s"

echo "== checks"
COUNTS="SELECT string_agg(t || '=' || n, ',' ORDER BY t) FROM (
  SELECT relname AS t, (xpath('/row/n/text()', query_to_xml('SELECT count(*) AS n FROM public.' || quote_ident(relname), false, true, '')))[1]::text AS n
    FROM pg_stat_user_tables WHERE schemaname = 'public') c"
fail=0
check() { if [[ "$2" == "$3" ]]; then echo "  ok   $1"; else echo "  FAIL $1: got '$2', want '$3'"; fail=1; fi; }
check "row counts in every table match" "$(psql "$(dburl $DST)" -tAc "$COUNTS")" "$(psql "$(dburl $SRC)" -tAc "$COUNTS")"
check "players restored" "$(psql "$(dburl $DST)" -tAc "SELECT count(*) FROM users")" 6
check "refunded order restored" "$(psql "$(dburl $DST)" -tAc "SELECT status FROM orders")" REFUNDED
check "migrations recorded" "$(psql "$(dburl $DST)" -tAc "SELECT count(*) FROM schema_migrations")" "$(ls "$ROOT/database/migrations"/*.sql | wc -l | tr -d ' ')"
check "ledger drift" "$(psql "$(dburl $DST)" -tAc "SELECT count(*) FROM wallet_ledger_drift")" 0
DELETE_OUT=$(psql "$(dburl $DST)" -tAc "DELETE FROM admin_audit_logs" 2>&1 || true)
check "audit log still append-only" "$([[ "$DELETE_OUT" == *"is append-only"* ]] && echo refused || echo allowed)" refused

if [[ $fail -eq 0 ]]; then echo "BACKUP-RESTORE PASSED"; else echo "BACKUP-RESTORE FAILED (logs in $LOGS)"; exit 1; fi
