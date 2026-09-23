#!/bin/bash
# 一鍵測試「平台管理員 + 場館 CRUD/子項簡化開關」權限機制。
# 用法：
#   cd backend/deploy
#   ./test-permissions.sh          # 啟動、建測試帳號、跑完整驗證流程
#   ./test-permissions.sh --reset  # 先清空舊資料庫（含舊測試帳號）再跑
#   ./test-permissions.sh --down   # 只停止並清掉這次測試起的容器/volume
#
# 需要：docker、python3（算密碼雜湊）、jq（解析 JSON）。macOS 內建都有。
set -euo pipefail
cd "$(dirname "$0")"

# 強制用 C locale 執行：某些語系下 bash 判斷變數名稱結尾字元時，會誤把 UTF-8 中文
# 標點的位元組當成合法的變數名稱字元（例如 "$code，" 被誤判成變數名稱含逗號的位元組），
# 導致變數變成「不存在」而噴 unbound variable。C locale 下這個字元分類只認 ASCII，不會誤判。
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

# ---------------------------------------------------------------------------
# 0. 準備 .env（測試用密碼，僅供本機驗證，不要拿去正式環境）
# ---------------------------------------------------------------------------
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

if [[ "${1:-}" == "--reset" ]]; then
  step "清空舊資料庫（--reset）"
  docker compose down -v 2>&1 | tail -5
fi

# ---------------------------------------------------------------------------
# 1. 啟動整套服務
# ---------------------------------------------------------------------------
step "啟動 mariadb / api / collector"
docker compose up -d --build mariadb api collector 2>&1 | tail -15

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

# ---------------------------------------------------------------------------
# 2. 建測試帳號（若已存在就跳過）— platform_admin / merchant_editor
# ---------------------------------------------------------------------------
step "建立測試帳號（platform_admin / merchant_editor）"

HASH_PLATFORM_ADMIN=$(python3 -c "
import hashlib, os, base64
salt = os.urandom(16)
dk = hashlib.pbkdf2_hmac('sha256', b'PlatformAdmin@123', salt, 210000, dklen=32)
print(f'210000.{base64.b64encode(salt).decode()}.{base64.b64encode(dk).decode()}')
")
HASH_EDITOR=$(python3 -c "
import hashlib, os, base64
salt = os.urandom(16)
dk = hashlib.pbkdf2_hmac('sha256', b'Editor@12345', salt, 210000, dklen=32)
print(f'210000.{base64.b64encode(salt).decode()}.{base64.b64encode(dk).decode()}')
")

docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac <<SQL
INSERT IGNORE INTO app_user (username, display_name, password_hash, system_role_id)
  VALUES ('platform_admin', '測試平台管理員', '$HASH_PLATFORM_ADMIN',
    (SELECT id FROM app_role WHERE code='platform-admin'));

INSERT IGNORE INTO app_user (username, display_name, password_hash)
  VALUES ('merchant_editor', '測試編輯者', '$HASH_EDITOR');

INSERT IGNORE INTO merchant_membership (merchant_id, user_id, role_id)
  SELECT 1, u.id, (SELECT id FROM app_role WHERE code='editor')
  FROM app_user u WHERE u.username = 'merchant_editor';
SQL
ok "帳號就緒：platform_admin / PlatformAdmin@123（平台管理員）、merchant_editor / Editor@12345（場館 editor）"

login() {
  docker compose exec -T api curl -fsS -X POST http://localhost:8080/api/v1/auth/login \
    -H "Content-Type: application/json" -d "{\"username\":\"$1\",\"password\":\"$2\"}"
}
call() {
  # call <method> <path> <token> [json body]
  local method=$1 path=$2 token=$3 body=${4:-}
  if [[ -n "$body" ]]; then
    docker compose exec -T api curl -s -o /tmp/resp.json -w "%{http_code}" \
      -X "$method" -H "Authorization: Bearer $token" -H "Content-Type: application/json" \
      "http://localhost:8080$path" -d "$body"
  else
    docker compose exec -T api curl -s -o /tmp/resp.json -w "%{http_code}" \
      -X "$method" -H "Authorization: Bearer $token" "http://localhost:8080$path"
  fi
}

# ---------------------------------------------------------------------------
# 3. 驗證：簡化模式下 editor（DB 只有 read）應該拿到完整 CRUD
# ---------------------------------------------------------------------------
step "驗證 1／4：場館目前是簡化模式，editor 角色 DB 只設 read，JWT 應展開成完整 CRUD"

ADMIN_LOGIN=$(login platform_admin "PlatformAdmin@123")
ADMIN_TOKEN=$(echo "$ADMIN_LOGIN" | jq -r '.accessToken')
[[ "$ADMIN_TOKEN" != "null" && -n "$ADMIN_TOKEN" ]] || fail "平台管理員登入失敗：$ADMIN_LOGIN"
ok "平台管理員登入成功，scope=$(echo "$ADMIN_LOGIN" | jq -r '.user.scopeKind')"

# 確保場館是簡化模式（種子資料預設值），不管前一次測試留下什麼狀態
call PATCH /api/v1/platform/merchants/1 "$ADMIN_TOKEN" '{"isRoleCrudConfigurationEnabled":false,"isRoleOptionConfigurationEnabled":false}' > /dev/null

EDITOR_LOGIN=$(login merchant_editor "Editor@12345")
EDITOR_TOKEN=$(echo "$EDITOR_LOGIN" | jq -r '.accessToken')
[[ "$EDITOR_TOKEN" != "null" && -n "$EDITOR_TOKEN" ]] || fail "editor 登入失敗：$EDITOR_LOGIN"

CHILLER_ACTIONS=$(echo "$EDITOR_LOGIN" | jq -r '.user.grants[] | select(.code=="hvac.chillers") | .actions | join(",")')
echo "editor 的 hvac.chillers 有效動作：$CHILLER_ACTIONS"
if [[ "$CHILLER_ACTIONS" == "create,read,update,delete" ]]; then
  ok "簡化模式生效：DB 只給 read，JWT 卻是完整 CRUD"
else
  fail "簡化模式沒生效，預期 create,read,update,delete，實際：$CHILLER_ACTIONS"
fi

# ---------------------------------------------------------------------------
# 4. 驗證：平台管理員切成細項模式後，同一個 editor 重新登入應該只剩 read
# ---------------------------------------------------------------------------
step "驗證 2／4：平台管理員切成細項模式，editor 重新登入應只剩 read"

call PATCH /api/v1/platform/merchants/1 "$ADMIN_TOKEN" '{"isRoleCrudConfigurationEnabled":true,"isRoleOptionConfigurationEnabled":true}' > /dev/null
EDITOR_LOGIN2=$(login merchant_editor "Editor@12345")
CHILLER_ACTIONS2=$(echo "$EDITOR_LOGIN2" | jq -r '.user.grants[] | select(.code=="hvac.chillers") | .actions | join(",")')
echo "切成細項模式後，editor 的 hvac.chillers 有效動作：$CHILLER_ACTIONS2"
if [[ "$CHILLER_ACTIONS2" == "read" ]]; then
  ok "細項模式生效：只剩資料庫實際勾選的 read"
else
  fail "細項模式沒生效，預期只有 read，實際：$CHILLER_ACTIONS2"
fi
EDITOR_TOKEN2=$(echo "$EDITOR_LOGIN2" | jq -r '.accessToken')

# ---------------------------------------------------------------------------
# 5. 驗證：scope 隔離、子功能檢查、舊 token 失效
# ---------------------------------------------------------------------------
step "驗證 3／4：權限邊界（scope 隔離、子功能檢查、未帶 token）"

code=$(call GET /api/v1/platform/merchants "$EDITOR_TOKEN2")
[[ "$code" == "403" ]] && ok "場館 token 打 platform.* 端點 → 403（scope 隔離正確）" \
  || fail "預期 403，實際 $code"

code=$(call POST /api/v1/alarms/1/ack "$EDITOR_TOKEN2" '{}')
[[ "$code" == "403" ]] && ok "細項模式下 editor（只有 read）嘗試確認告警 → 403（缺 update）" \
  || fail "預期 403，實際 $code"

code=$(call GET /api/v1/chillers "$EDITOR_TOKEN2")
[[ "$code" == "200" ]] && ok "editor 讀取冰水主機清單 → 200（有 read）" \
  || fail "預期 200，實際 $code"

code=$(docker compose exec -T api curl -s -o /dev/null -w "%{http_code}" http://localhost:8080/api/v1/chillers)
[[ "$code" == "401" ]] && ok "完全沒帶 token → 401" \
  || fail "預期 401，實際 $code"

step "驗證 4／4：AuthVersion 機制——切換場館開關後，切換前的舊 token 應立即失效"

OLD_TOKEN=$EDITOR_TOKEN2
code=$(call GET /api/v1/chillers "$OLD_TOKEN")
[[ "$code" == "200" ]] && echo "（切換前）用剛簽發的 token 讀取 → ${code}，正常"

call PATCH /api/v1/platform/merchants/1 "$ADMIN_TOKEN" '{"isRoleCrudConfigurationEnabled":false,"isRoleOptionConfigurationEnabled":false}' > /dev/null

code=$(call GET /api/v1/chillers "$OLD_TOKEN")
[[ "$code" == "401" ]] && ok "切換開關後，舊 token 立即變 401（AuthVersion 機制生效）" \
  || fail "預期 401，實際 ${code}——舊 token 應該要失效"

echo -e "\n${BOLD}${GREEN}全部 4 項驗證通過。場館已重設回簡化模式（種子資料的預設值）。${NC}"
echo "服務仍在背景執行中；用 'docker compose logs -f api' 看 log，或 './test-permissions.sh --down' 清乾淨。"
