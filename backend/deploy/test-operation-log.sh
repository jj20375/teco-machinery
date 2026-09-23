#!/bin/bash
# 一鍵測試「操作紀錄稽核」機制：呼叫幾個真的會寫 operation_log 的端點，
# 然後直接查資料庫，確認寫入的欄位（summary/before_json/after_json/actor/is_success）正確。
# 用法：
#   cd backend/deploy
#   ./test-operation-log.sh          # 啟動（若尚未啟動）、建測試帳號、跑驗證流程
#   ./test-operation-log.sh --down   # 只停止並清掉這次測試起的容器/volume
#
# 需要：docker、python3（算密碼雜湊）、jq（解析 JSON）。macOS 內建都有。
set -euo pipefail
cd "$(dirname "$0")"

# 見 test-permissions.sh 同一段註解：強制 C locale，避免中文標點被誤判成變數名稱字元。
export LC_ALL=C LANG=C

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; BOLD='\033[1m'; NC='\033[0m'
step() { echo -e "\n${BOLD}${YELLOW}== $1 ==${NC}"; }
ok()   { echo -e "${GREEN}✔ $1${NC}"; }
fail() { echo -e "${RED}✘ $1${NC}"; exit 1; }

if [[ "${1:-}" == "--down" ]]; then
  docker compose down -v
  rm -f .env
  echo "已停止並清空這次測試的容器與資料。"
  exit 0
fi

if [[ ! -f .env ]]; then
  step "建立測試用 .env"
  cat > .env <<EOF
DB_ROOT_PASSWORD=test_root_pw_$(openssl rand -hex 4)
DB_APP_PASSWORD=test_app_pw_$(openssl rand -hex 4)
INTERNAL_TOKEN=test_internal_token_$(openssl rand -hex 8)
JWT_SIGNING_KEY=test_jwt_signing_key_$(openssl rand -hex 16)
HANBELL_IP=192.168.10.198
HANBELL_PORT=4196
HANBELL_BAUD=9600
DDC1_IP=192.168.10.12
DDC1_PORT=502
DDC2_IP=192.168.10.14
DDC2_PORT=502
FRONTEND_ORIGIN=http://localhost:4321
EOF
  ok ".env 已建立（測試密碼，見 deploy/.env）"
fi
source .env

step "啟動 mariadb / api"
docker compose up -d --build mariadb api 2>&1 | tail -15

echo "等待 mariadb 就緒..."
for i in $(seq 1 30); do
  h=$(docker inspect --format='{{.State.Health.Status}}' "$(docker compose ps -q mariadb)" 2>/dev/null || echo "starting")
  [[ "$h" == "healthy" ]] && break
  sleep 2
done
[[ "$h" == "healthy" ]] || fail "mariadb 一直沒變 healthy，用 docker compose logs mariadb 查原因"
ok "mariadb 已就緒"

echo "等待 api 就緒..."
for i in $(seq 1 20); do
  code=$(docker compose exec -T api curl -s -o /dev/null -w "%{http_code}" http://localhost:8080/readyz 2>/dev/null || echo "000")
  [[ "$code" == "200" ]] && break
  sleep 2
done
[[ "$code" == "200" ]] || fail "api /readyz 一直沒過，用 docker compose logs api 查原因"
ok "api 已就緒"

step "套用 operation_log 稽核欄位遷移（重複執行安全，欄位已存在就跳過）"
docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac < mariadb/init/004_operation_log_audit_columns.sql
ok "遷移已套用"

# ---------------------------------------------------------------------------
# 建測試帳號：oplog_admin（merchant-admin，發動操作的人）、
#             oplog_target（viewer，被操作的對象——不用真的帳號，避免影響到你自己在用的資料）
# ---------------------------------------------------------------------------
step "建立測試帳號：oplog_admin（merchant-admin）、oplog_target（被操作對象）"

HASH_ADMIN=$(python3 -c "
import hashlib, os, base64
salt = os.urandom(16)
dk = hashlib.pbkdf2_hmac('sha256', b'OplogAdmin@123', salt, 210000, dklen=32)
print(f'210000.{base64.b64encode(salt).decode()}.{base64.b64encode(dk).decode()}')
")
HASH_TARGET=$(python3 -c "
import hashlib, os, base64
salt = os.urandom(16)
dk = hashlib.pbkdf2_hmac('sha256', b'OplogTarget@123', salt, 210000, dklen=32)
print(f'210000.{base64.b64encode(salt).decode()}.{base64.b64encode(dk).decode()}')
")

docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac <<SQL
INSERT IGNORE INTO app_user (username, display_name, password_hash)
  VALUES ('oplog_admin', '稽核測試管理員', '$HASH_ADMIN');
INSERT IGNORE INTO merchant_membership (merchant_id, user_id, role_id)
  SELECT 1, u.id, (SELECT id FROM app_role WHERE code='merchant-admin')
  FROM app_user u WHERE u.username = 'oplog_admin';

INSERT IGNORE INTO app_user (username, display_name, password_hash)
  VALUES ('oplog_target', '稽核測試對象', '$HASH_TARGET');
INSERT IGNORE INTO merchant_membership (merchant_id, user_id, role_id)
  SELECT 1, u.id, (SELECT id FROM app_role WHERE code='viewer')
  FROM app_user u WHERE u.username = 'oplog_target';
SQL
ok "帳號就緒：oplog_admin / OplogAdmin@123（merchant-admin）、oplog_target / OplogTarget@123（被操作對象）"

login() {
  docker compose exec -T api curl -fsS -X POST http://localhost:8080/api/v1/auth/login \
    -H "Content-Type: application/json" -d "{\"username\":\"$1\",\"password\":\"$2\"}"
}
call() {
  # call <method> <path> <token> [json body]；回應存到 /tmp/oplog_resp.json，回傳 HTTP status code
  local method=$1 path=$2 token=$3 body=${4:-}
  if [[ -n "$body" ]]; then
    docker compose exec -T api curl -s -o /tmp/oplog_resp.json -w "%{http_code}" \
      -X "$method" -H "Authorization: Bearer $token" -H "Content-Type: application/json" \
      "http://localhost:8080$path" -d "$body"
  else
    docker compose exec -T api curl -s -o /tmp/oplog_resp.json -w "%{http_code}" \
      -X "$method" -H "Authorization: Bearer $token" "http://localhost:8080$path"
  fi
}
resp() { docker compose exec -T api cat /tmp/oplog_resp.json; }
sql_count() {
  # sql_count <where 條件>：回傳 operation_log 符合條件的筆數
  docker compose exec -T mariadb mariadb -N -u root -p"$DB_ROOT_PASSWORD" teco_hvac \
    -e "SELECT COUNT(*) FROM operation_log WHERE $1" | tr -d '\r'
}
show_latest() {
  # show_latest <action>：印出該 action 最新一筆的內容，方便肉眼檢查欄位對不對
  docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac -e \
    "SELECT id, actor_display_name, action, target_id, summary, is_success, ip
     FROM operation_log WHERE action='$1' ORDER BY id DESC LIMIT 1\G"
}

step "登入 oplog_admin"
ADMIN_LOGIN=$(login oplog_admin "OplogAdmin@123")
ADMIN_TOKEN=$(echo "$ADMIN_LOGIN" | jq -r '.accessToken')
[[ "$ADMIN_TOKEN" != "null" && -n "$ADMIN_TOKEN" ]] || fail "oplog_admin 登入失敗：$ADMIN_LOGIN"
TARGET_MEMBERSHIP_ID=$(docker compose exec -T mariadb mariadb -N -u root -p"$DB_ROOT_PASSWORD" teco_hvac -e \
  "SELECT mm.id FROM merchant_membership mm JOIN app_user u ON u.id=mm.user_id WHERE u.username='oplog_target'" | tr -d '\r')
ok "登入成功，測試對象 membershipId=$TARGET_MEMBERSHIP_ID"

# ---------------------------------------------------------------------------
step "驗證 1／3：停用測試對象帳號 → 應寫入 merchant.user.update（含 before/after 差異）"
BEFORE_COUNT=$(sql_count "action='merchant.user.update'")
call PATCH "/api/v1/merchant/users/$TARGET_MEMBERSHIP_ID" "$ADMIN_TOKEN" '{"isActive":false}' > /dev/null
AFTER_COUNT=$(sql_count "action='merchant.user.update'")
[[ "$AFTER_COUNT" -gt "$BEFORE_COUNT" ]] && ok "operation_log 新增了一筆 merchant.user.update" \
  || fail "沒有寫入新的 merchant.user.update 紀錄"
show_latest "merchant.user.update"

# ---------------------------------------------------------------------------
step "驗證 2／3：重設測試對象密碼 → 應寫入 merchant.user.reset_password，且明碼不能外洩"
call POST "/api/v1/merchant/users/$TARGET_MEMBERSHIP_ID/reset-password" "$ADMIN_TOKEN" > /dev/null
NEW_PW=$(resp | jq -r '.temporaryPassword')
[[ -n "$NEW_PW" && "$NEW_PW" != "null" ]] || fail "重設密碼 API 沒有回傳 temporaryPassword：$(resp)"
echo "產生的臨時密碼：$NEW_PW（只是確認流程通了，不代表要記住它）"

LOG_COUNT=$(sql_count "action='merchant.user.reset_password'")
[[ "$LOG_COUNT" -gt 0 ]] && ok "operation_log 有 $LOG_COUNT 筆 merchant.user.reset_password" \
  || fail "沒有寫入 merchant.user.reset_password 紀錄"
show_latest "merchant.user.reset_password"

LEAKED=$(sql_count "action='merchant.user.reset_password' AND (before_json LIKE '%$NEW_PW%' OR after_json LIKE '%$NEW_PW%' OR summary LIKE '%$NEW_PW%')")
[[ "$LEAKED" == "0" ]] && ok "確認密碼明碼沒有外洩到 operation_log" \
  || fail "密碼明碼出現在 operation_log 裡，這是資安漏洞！"

# ---------------------------------------------------------------------------
step "驗證 3／3：改自己密碼但輸錯目前密碼 → 應寫入失敗紀錄（is_success=0）"
call POST /api/v1/auth/change-password "$ADMIN_TOKEN" '{"currentPassword":"wrong-password","newPassword":"NewPass@123"}' > /dev/null
FAIL_COUNT=$(sql_count "action='auth.change_password' AND is_success=0")
[[ "$FAIL_COUNT" -gt 0 ]] && ok "失敗的改密碼操作也正確記錄（is_success=0）" \
  || fail "失敗案例沒有被記錄"
show_latest "auth.change_password"

echo -e "\n${BOLD}${GREEN}全部 3 項驗證通過。${NC}"
echo "想直接看全部紀錄："
echo '  docker compose exec mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac \'
echo '    -e "SELECT id, actor_display_name, action, summary, is_success, created_at FROM operation_log ORDER BY id DESC LIMIT 20;"'
echo "服務仍在背景執行中；用 'docker compose logs -f api' 看 log，或 './test-operation-log.sh --down' 清乾淨。"
