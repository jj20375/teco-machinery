#!/bin/bash
# 還原 simulate-live-data.sh 的效果：把 3 個通道的連線狀態改回「未連線」，
# 監控中心／前台戰情室的即時卡片會立刻變回離線（不用重啟 api 容器），
# 再清掉那批標記 [DEMO-LIVE] 的即時告警，最後重新啟動被暫停的 collector 容器
# （見 simulate-live-data.sh 開頭的說明：collector 會不斷回報連不到現場設備，
# 為了不讓它跟模擬資料互相打架，跑 demo 期間先把它停掉了）。
#
# 用法：
#   cd backend/deploy
#   ./stop-live-simulation.sh
set -euo pipefail
cd "$(dirname "$0")"
export LC_ALL=C LANG=C

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; BOLD='\033[1m'; NC='\033[0m'
step() { echo -e "\n${BOLD}${YELLOW}== $1 ==${NC}"; }
ok()   { echo -e "${GREEN}✔ $1${NC}"; }
fail() { echo -e "${RED}✘ $1${NC}"; exit 1; }

[[ -f .env ]] || fail "找不到 .env"
source .env
: "${DB_ROOT_PASSWORD:?.env 裡沒有 DB_ROOT_PASSWORD}"
: "${INTERNAL_TOKEN:?.env 裡沒有 INTERNAL_TOKEN}"

docker compose ps api --format '{{.State}}' | grep -q running || fail "api 容器沒在跑"

step "把 Gateway/DDC1/DDC2 都標記為未連線"
for channel in 0 1 2; do
  docker compose exec -T api curl -fsS -X POST http://localhost:8080/internal/ingest/connection \
    -H "X-Internal-Token: $INTERNAL_TOKEN" -H "Content-Type: application/json" \
    -d "{\"channel\":$channel,\"ip\":\"192.168.10.$((10+channel))\",\"port\":\"502\",\"connectionState\":4,\"isConnected\":false,\"triggerTimeUtc\":\"$(date -u +%Y-%m-%dT%H:%M:%S.000Z)\"}" \
    > /dev/null
done
ok "3 個通道都已標記為未連線，即時卡片應該立刻變回離線"

step "清除模擬即時告警"
docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac -e "
  DELETE FROM alarm_event WHERE memo = '[DEMO-LIVE] 模擬即時資料，非真實告警';
"
ok "已清除"

step "恢復 collector 容器"
docker compose start collector > /dev/null
ok "collector 已恢復運作"

echo ""
ok "完成。監控中心／前台戰情室現在應該全部顯示離線，跟 IoT 還沒連線時一樣。"
