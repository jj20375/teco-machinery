#!/bin/bash
# 清空 seed-demo-data.sh 灌的模擬資料。
#
# 只刪 seed-demo-data.sh 留下的 .demo-data-range 檔案記錄的那段時間範圍（rollup 資料表、
# 今天的 fcu_reading）跟 memo 帶 [DEMO] 標記的告警，不會用 TRUNCATE 整張表清空——現場
# Collector 之後開始寫入真實資料時，這些表會混著真實跟模擬資料，用時間範圍 + 標記字串
# 精準刪除才安全。
#
# 用法：
#   cd backend/deploy
#   ./clear-demo-data.sh
set -euo pipefail
cd "$(dirname "$0")"
export LC_ALL=C LANG=C

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; BOLD='\033[1m'; NC='\033[0m'
step() { echo -e "\n${BOLD}${YELLOW}== $1 ==${NC}"; }
ok()   { echo -e "${GREEN}✔ $1${NC}"; }
fail() { echo -e "${RED}✘ $1${NC}"; exit 1; }

[[ -f .demo-data-range ]] || fail "找不到 backend/deploy/.demo-data-range，代表沒有跑過 ./seed-demo-data.sh，或已經清過了"
source .demo-data-range
: "${FROM_BUCKET:?.demo-data-range 裡沒有 FROM_BUCKET}"
: "${TO_BUCKET:?.demo-data-range 裡沒有 TO_BUCKET}"
# TODAY_FROM_UTC/TODAY_TO_UTC 是後來才加的欄位，舊的 .demo-data-range 檔案可能沒有，
# 用空字串當預設值，底下的 fcu_reading 清除步驟會自己判斷要不要跳過。
TODAY_FROM_UTC="${TODAY_FROM_UTC:-}"
TODAY_TO_UTC="${TODAY_TO_UTC:-}"

[[ -f .env ]] || fail "找不到 .env"
source .env
: "${DB_ROOT_PASSWORD:?.env 裡沒有 DB_ROOT_PASSWORD}"

step "確認 mariadb 容器在跑"
docker compose ps mariadb --format '{{.State}}' | grep -q running || fail "mariadb 容器沒在跑，先 docker compose up -d"

step "清除範圍 $FROM_BUCKET ~ $TO_BUCKET（UTC）的模擬資料"
docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac <<SQL
DELETE FROM rollup_chiller_1h WHERE bucket BETWEEN '$FROM_BUCKET' AND '$TO_BUCKET';
DELETE FROM rollup_fcu_1h WHERE bucket BETWEEN '$FROM_BUCKET' AND '$TO_BUCKET';
DELETE FROM alarm_event WHERE memo = '[DEMO] 模擬資料，非真實告警';
SQL
if [[ -n "$TODAY_FROM_UTC" && -n "$TODAY_TO_UTC" ]]; then
  docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac -e "
    DELETE FROM fcu_reading WHERE ts >= '$TODAY_FROM_UTC' AND ts < '$TODAY_TO_UTC';
  "
fi

step "確認清除結果"
docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac -N -e "
SELECT COUNT(*) FROM rollup_chiller_1h WHERE bucket BETWEEN '$FROM_BUCKET' AND '$TO_BUCKET';
SELECT COUNT(*) FROM rollup_fcu_1h WHERE bucket BETWEEN '$FROM_BUCKET' AND '$TO_BUCKET';
SELECT COUNT(*) FROM alarm_event WHERE memo = '[DEMO] 模擬資料，非真實告警';
" | { read -r a; read -r b; read -r c;
  [[ "$a" == "0" && "$b" == "0" && "$c" == "0" ]] || fail "清除後還有殘留：rollup_chiller_1h=$a rollup_fcu_1h=$b alarm_event=$c"
  ok "rollup/alarm 全部歸零"
}
if [[ -n "$TODAY_FROM_UTC" && -n "$TODAY_TO_UTC" ]]; then
  FCU_READING_LEFT=$(docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" -N teco_hvac -e "
    SELECT COUNT(*) FROM fcu_reading WHERE ts >= '$TODAY_FROM_UTC' AND ts < '$TODAY_TO_UTC';
  ")
  [[ "$FCU_READING_LEFT" == "0" ]] || fail "fcu_reading 清除後還有殘留：$FCU_READING_LEFT 筆"
fi
ok "全部歸零，清除完成"

rm -f .demo-data-range
ok "已刪除 .demo-data-range，下次要看 demo 資料再重新跑 ./seed-demo-data.sh"
