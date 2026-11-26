#!/usr/bin/env bash
# Smoke test API FieldService przez curl (bez jq).
#
#   scripts/smoke.sh http://localhost:5090
#   scripts/smoke.sh https://mobilny.dev/maui/architektura
#   ADMIN_KEY=... scripts/smoke.sh <baseUrl>   # na końcu usuwa dane technika smoke (admin reset)
#   DISPATCHER_KEY=...                          # gdy na serwerze ustawiono Dispatcher__Key
#
# Przebieg: health → login (+401) → refresh → wizyty na dziś (+304) → start bez If-Match (428) → start z X-If-Match i
# Idempotency-Key ×2 (powtórka) → stara wersja (409) → część → podpis → zakończenie → katalog, miniatura →
# rezerwacja KOMPRESOR-R32 (409 Inventory.OutOfStock) → dyspozytor → [reset].
set -euo pipefail

BASE="${1:?użycie: scripts/smoke.sh <baseUrl>}"
BASE="${BASE%/}"
EMAIL="${SMOKE_EMAIL:-smoke.$(date +%s)$RANDOM@example.com}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
PASS=0

uuid() {
  if command -v uuidgen >/dev/null 2>&1; then uuidgen | tr 'A-Z' 'a-z'
  else cat /proc/sys/kernel/random/uuid; fi
}
# json_str <plik> <pole> → pierwsza wartość tekstowa pola (proste JSON-y API, bez zagnieżdżonych cudzysłowów)
json_str() { sed -n "s/.*\"$2\":\"\([^\"]*\)\".*/\1/p" "$1" | head -n1; }
fail() { echo "FAIL: $*" >&2; [ -f "$TMP/body" ] && { echo "--- odpowiedź:" >&2; head -c 2000 "$TMP/body" >&2; echo >&2; }; exit 1; }
ok() { PASS=$((PASS + 1)); printf '  ok  %s\n' "$*"; }

# req <oczekiwany kod> <metoda> <ścieżka> [argumenty curl...] → treść w $TMP/body
req() {
  local expected="$1" method="$2" path="$3"; shift 3
  local code
  code="$(curl -sS -o "$TMP/body" -w '%{http_code}' -X "$method" "$@" "$BASE$path")" || fail "$method $path: błąd połączenia"
  [ "$code" = "$expected" ] || fail "$method $path: HTTP $code (oczekiwano $expected)"
  ok "$method $path → $code"
}
contains() { grep -q -- "$1" "$TMP/body" || fail "brak '$1' w odpowiedzi"; }

echo "== smoke $BASE (technik: $EMAIL)"

req 200 GET /api/health
contains '"status":"ok"'
contains '"databaseConnected":true'
echo "     pathBase='$(json_str "$TMP/body" pathBase)'"

req 401 POST /api/auth/login -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"pin\":\"0000\"}"
req 200 POST /api/auth/login -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"pin\":\"1234\"}"
REFRESH="$(json_str "$TMP/body" refreshToken)"
[ -n "$REFRESH" ] || fail "brak refreshToken"
req 200 POST /api/auth/refresh -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$REFRESH\"}"
TOKEN="$(json_str "$TMP/body" accessToken)"
[ -n "$TOKEN" ] || fail "brak accessToken"
AUTH=(-H "Authorization: Bearer $TOKEN")

req 401 GET /api/visits/today

req 200 GET /api/visits/today "${AUTH[@]}" -D "$TMP/headers"
ETAG="$(sed -n 's/^[Ee][Tt]ag: *\(.*\)\r*$/\1/p' "$TMP/headers" | tr -d '\r' | head -n1)"
[ -n "$ETAG" ] || fail "brak ETag na liście wizyt"
req 304 GET /api/visits/today "${AUTH[@]}" -H "If-None-Match: $ETAG"
req 200 GET /api/visits/today "${AUTH[@]}"
# druga wizyta dnia jest zaplanowana (pierwsza rozpoczęta)
ID="$(grep -o '"id":"[^"]*"' "$TMP/body" | sed -n '2s/"id":"\(.*\)"/\1/p')"
[ -n "$ID" ] || fail "brak wizyt na dziś"
V="/api/visits/$ID"
JSON=(-H 'Content-Type: application/json')

req 428 POST "$V/start" "${AUTH[@]}"
KEY="$(uuid)"
req 200 POST "$V/start" "${AUTH[@]}" -H 'X-If-Match: "1"' -H "Idempotency-Key: $KEY"
contains '"status":"InProgress"'
# powtórka (zgubiona odpowiedź): ten sam klucz → zapisana odpowiedź, bez drugiego wykonania
req 200 POST "$V/start" "${AUTH[@]}" -H 'X-If-Match: "1"' -H "Idempotency-Key: $KEY"
req 409 POST "$V/activities" "${AUTH[@]}" "${JSON[@]}" -H 'X-If-Match: "1"' -d '{"code":"DIAG","description":"Smoke"}'
contains '"code":"Visit.VersionConflict"'
req 200 POST "$V/parts" "${AUTH[@]}" "${JSON[@]}" -H 'X-If-Match: "2"' -H "Idempotency-Key: $(uuid)" -d '{"partId":"FILTR-SIATKOWY-22","quantity":1}'
req 200 PUT "$V/signature" "${AUTH[@]}" "${JSON[@]}" -H 'X-If-Match: "3"' -H "Idempotency-Key: $(uuid)" -d '{"signerName":"Smoke Test"}'
req 200 POST "$V/complete" "${AUTH[@]}" "${JSON[@]}" -H 'X-If-Match: "4"' -H "Idempotency-Key: $(uuid)" -d '{"outcome":"Fixed"}'
contains '"status":"Completed"'

req 200 GET /api/parts "${AUTH[@]}"
THUMB="$(json_str "$TMP/body" thumbnailUrl)"
echo "     thumbnailUrl=$THUMB"
code="$(curl -sS -o "$TMP/thumb.png" -w '%{http_code}' "$THUMB")"
[ "$code" = "200" ] && [ "$(head -c 4 "$TMP/thumb.png" | od -An -tx1 | tr -d ' ')" = "89504e47" ] || fail "miniatura: HTTP $code"
ok "GET miniatura (bez tokenu) → 200 PNG"
req 200 GET /api/vanstock "${AUTH[@]}"
req 200 GET /api/workorders "${AUTH[@]}"
req 200 GET "/api/visits/history?page=1&pageSize=10" "${AUTH[@]}"
req 409 POST /api/parts/reservations "${AUTH[@]}" "${JSON[@]}" -d "{\"partId\":\"KOMPRESOR-R32\",\"warehouseId\":\"MAG-01\",\"visitId\":\"$ID\",\"quantity\":1}"
contains '"code":"Inventory.OutOfStock"'

DISPATCHER=()
[ -n "${DISPATCHER_KEY:-}" ] && DISPATCHER=(-H "X-Dispatcher-Key: $DISPATCHER_KEY")
req 200 GET "/api/dispatcher/visits?technicianEmail=$EMAIL" ${DISPATCHER[@]+"${DISPATCHER[@]}"}
req 200 GET /swagger/v1/swagger.json
contains '"servers"'

if [ -n "${ADMIN_KEY:-}" ]; then
  req 200 POST "/api/admin/reset?technicianEmail=$EMAIL" -H "X-Admin-Key: $ADMIN_KEY"
fi

echo "== SMOKE OK ($PASS kroków)"
