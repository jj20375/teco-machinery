#!/bin/bash
# 讓「監控中心」「前台戰情室」那些即時卡片也有東西可看（demo 用）。
#
# 這些即時卡片讀的是 API 記憶體裡的 CurrentStateStore，只有 Collector 真的連線送資料
# 才會更新（見 backend/README.md「業主驗收時抓到...」一節的說明）。因為現場 IoT 還沒接，
# 這支腳本改用 Collector 真正會用的同一條路徑——POST /internal/ingest/connection 和
# /internal/ingest/data（見 IngestEndpoints.cs）——餵一份假快照進去，从 API 的角度看
# 這就是「Collector 傳來一筆資料」，走的是正式路徑，不是繞過去改資料庫。
#
# **這支腳本會先把 collector 容器停掉**，跑完 demo 後用 stop-live-simulation.sh 恢復。
# 原因（真的花了很多時間才抓到的真相）：collector 容器雖然連不到現場設備，但它自己
# 一直沒有停過，會不斷嘗試連線、失敗、重試，每次狀態變化都會誠實地呼叫
# POST /internal/ingest/connection 回報「未連線」——這跟本腳本假造的「已連線」狀態
# 打的是同一支端點，兩邊會互相搶著把 CurrentStateStore 的連線狀態改成不同的值，
# 導致畫面時好時壞、過幾秒又跳回離線，看起來像是隨機發生的怪 bug，其實是兩個寫入者
# 在打架。停掉 collector 後，本腳本寫進去的假資料就不會再被真實（但連不到現場、
# 一直回報失敗）的 Collector 蓋掉。
#
# 機台狀態刻意做成有正常、有異常、有停止，方便展示不同 UI 樣式：
#   冰水主機 1：正常運轉。冰水主機 2：異常（軸承溫度過高）。
#   FCU：大部分正常運轉，每個 DDC（B1/B2）各挑前 2 台設成異常（配一筆真正的
#        alarm_event，因為 FCU 的異常判定是查目前有效告警清單，不是快照裡的欄位）、
#        接著 3 台設成停止（開關狀態 Off）。
#
# 用法：
#   cd backend/deploy
#   ./simulate-live-data.sh
#   ./simulate-live-data.sh --with-anomalies   # 額外塞入異常數據，驗證平台「系統診斷」頁每條檢查都會亮燈
#
# --with-anomalies 會先送一筆正常快照當「上一筆」，再送一筆刻意做壞的快照：
#   冰水主機 1 冰水出水溫度 45°C（超出範圍）＋累計運轉時數倒退、UpdateTime 往前偏 8 小時（模擬時區 bug）、
#   DDC1 少回報 1 台（資料庫有登記、現場沒回報）、DDC2 多 1 台沒對照的 FCU（站號 99／位置 99）、
#   DDC2 1 台溫度 85°C、1 台狀態全 Unknown 且溫度 0（疑似沒回應）。
# 還原：
#   ./stop-live-simulation.sh
set -euo pipefail
cd "$(dirname "$0")"

WITH_ANOMALIES=false
[[ "${1:-}" == "--with-anomalies" ]] && WITH_ANOMALIES=true
export LC_ALL=C LANG=C

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; BOLD='\033[1m'; NC='\033[0m'
step() { echo -e "\n${BOLD}${YELLOW}== $1 ==${NC}"; }
ok()   { echo -e "${GREEN}✔ $1${NC}"; }
fail() { echo -e "${RED}✘ $1${NC}"; exit 1; }

command -v python3 >/dev/null || fail "需要 python3 來組 JSON 酬載"
[[ -f .env ]] || fail "找不到 .env"
source .env
: "${DB_ROOT_PASSWORD:?.env 裡沒有 DB_ROOT_PASSWORD}"
: "${INTERNAL_TOKEN:?.env 裡沒有 INTERNAL_TOKEN}"

step "確認 mariadb / api 容器在跑"
docker compose ps mariadb --format '{{.State}}' | grep -q running || fail "mariadb 容器沒在跑，先 docker compose up -d"
docker compose ps api --format '{{.State}}' | grep -q running || fail "api 容器沒在跑，先 docker compose up -d"

step "暫停 collector 容器（它會不斷回報連不到現場設備，跟本腳本的假資料互相打架）"
docker compose stop collector > /dev/null
ok "collector 已暫停；跑完 demo 記得用 ./stop-live-simulation.sh 恢復"

CHILLERS_FILE=$(mktemp)
FCUS_FILE=$(mktemp)
PAYLOAD_FILE=$(mktemp)
BASELINE_FILE=$(mktemp)
ABNORMAL_IDS_FILE=$(mktemp)
trap 'rm -f "$CHILLERS_FILE" "$FCUS_FILE" "$PAYLOAD_FILE" "$BASELINE_FILE" "$ABNORMAL_IDS_FILE"' EXIT

step "讀取目前的設備清單"
docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" -N teco_hvac -e \
  "SELECT id, code, modbus_id FROM device_chiller ORDER BY modbus_id;" > "$CHILLERS_FILE"
docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" -N teco_hvac -e \
  "SELECT id, channel, station_id, position, address, floor FROM device_fcu ORDER BY channel, position;" > "$FCUS_FILE"

[[ -s "$CHILLERS_FILE" ]] || fail "device_chiller 是空的，還沒跑過種子資料？"
[[ -s "$FCUS_FILE" ]] || fail "device_fcu 是空的，還沒跑過種子資料？"

step "組出模擬快照（Python，讀 $CHILLERS_FILE / $FCUS_FILE）"
python3 - "$CHILLERS_FILE" "$FCUS_FILE" "$PAYLOAD_FILE" "$ABNORMAL_IDS_FILE" "$BASELINE_FILE" "$WITH_ANOMALIES" <<'PYEOF'
import sys, json, random, copy
from datetime import datetime, timezone, timedelta

chillers_path, fcus_path, payload_path, abnormal_ids_path, baseline_path, with_anomalies = sys.argv[1:7]
now_dt = datetime.now(timezone.utc)
now = now_dt.isoformat()
random.seed(42)  # demo 用，固定種子讓每次跑結果一致，方便對答案

def normal_chiller(modbus_id):
    return {
        "modbusId": modbus_id, "updateTimeUtc": now, "readStatus": 1, "isConnected": True,
        "coolingWaterOutletTemperature": round(32 + random.uniform(-0.5, 0.5), 1),
        "coolingWaterInletTemperature": round(28 + random.uniform(-0.5, 0.5), 1),
        "chilledWaterOutletTemperature": round(6.8 + random.uniform(-0.3, 0.3), 1),
        "chilledWaterTemperatureDifference": 5.0,
        "chilledWaterInletTemperature": round(11.9 + random.uniform(-0.3, 0.3), 1),
        "inputCurrent": round(120 + random.uniform(-5, 5), 1),
        "inputVoltage": 380.0,
        "highPressure": 15.2, "lowPressure": 5.1,
        "actualRpm": 2900, "accumulatedRunningHours": 12480, "accumulatedStartCount": 452,
        "inputPowerKilowatt": round(55 + random.uniform(-3, 3), 1),
        "approachTemperature": 1.2, "accumulatedEnergyKilowattHour": 583200.5,
        "loadPercentage": 65, "waterControl": 1,
        "isAlarm": False, "isChilledWaterFlowAbnormal": False, "isCoolingWaterFlowAbnormal": False,
        "isInverterAbnormal": False, "isCompressorOverload": False, "isBearingTemperatureTooHigh": False,
        "isDischargeTemperatureTooHigh": False, "isMotorTemperatureTooHigh": False,
        "isPowerVoltageTooHigh": False, "isPowerVoltageTooLow": False, "isCurrentTooHigh": False,
        "isHighPressureTooHigh": False, "isLowPressureTooLow": False,
        "isOutletAntiFreezeAbnormal": False, "isInletAntiFreezeAbnormal": False,
    }

def abnormal_chiller(modbus_id):
    c = normal_chiller(modbus_id)
    c.update({"isAlarm": True, "isBearingTemperatureTooHigh": True, "loadPercentage": 58})
    return c

with open(chillers_path, encoding="utf-8") as f:
    chiller_rows = [line.rstrip("\n").split("\t") for line in f if line.strip()]
hanbell = {}
for idx, (cid, code, modbus_id) in enumerate(chiller_rows):
    mid = int(modbus_id)
    hanbell[mid] = abnormal_chiller(mid) if idx == 1 else normal_chiller(mid)

with open(fcus_path, encoding="utf-8") as f:
    fcu_rows = [line.rstrip("\n").split("\t") for line in f if line.strip()]

by_channel = {1: [], 2: []}
for row in fcu_rows:
    fid, channel, station_id, position, address, floor = row
    by_channel[int(channel)].append({
        "id": int(fid), "channel": int(channel), "stationId": int(station_id),
        "position": int(position), "address": int(address), "floor": floor,
    })

abnormal_ids = []
ddc_lists = {1: [], 2: []}
for channel, rows in by_channel.items():
    abnormal_idx = set(range(0, min(2, len(rows))))       # 每個 DDC 前 2 台設異常
    stopped_idx = set(range(2, min(5, len(rows))))        # 接著 3 台設停止
    for i, r in enumerate(rows):
        is_abnormal = i in abnormal_idx
        is_stopped = i in stopped_idx
        if is_abnormal:
            abnormal_ids.append(r["id"])
        snapshot = {
            "channel": r["channel"], "stationId": r["stationId"], "position": r["position"],
            "id": f"FC_MC{r['channel']}_{r['stationId']}_{r['position']:02d}", "address": r["address"],
            "switchStatus": 0 if is_stopped else 1,
            "mode": 1, "fanSpeed": 3,
            "temperature": round(28.5 + random.uniform(-0.3, 0.3), 1) if is_abnormal
                           else round(25.3 + random.uniform(-0.8, 0.8), 1),
        }
        ddc_lists[channel].append(snapshot)

payload = {
    "updateTimeUtc": now, "receivedAtUtc": now,
    "hanbell1": hanbell[1], "hanbell2": hanbell[2],
    "ddc1": {"channel": 1, "updateTimeUtc": now, "readStatus": 1, "isConnected": True, "fcuList": ddc_lists[1]},
    "ddc2": {"channel": 2, "updateTimeUtc": now, "readStatus": 1, "isConnected": True, "fcuList": ddc_lists[2]},
}

# 正常快照先存一份當「上一筆」——--with-anomalies 模式要靠它才驗得出累計值倒退。
with open(baseline_path, "w", encoding="utf-8") as f:
    json.dump(payload, f, ensure_ascii=False)

if with_anomalies == "true":
    bad = copy.deepcopy(payload)
    shifted = (now_dt - timedelta(hours=8)).isoformat()
    bad["updateTimeUtc"] = shifted
    bad["hanbell1"]["chilledWaterOutletTemperature"] = 45.0
    bad["hanbell1"]["accumulatedRunningHours"] -= 500
    if bad["ddc1"]["fcuList"]:
        bad["ddc1"]["fcuList"].pop()
    bad["ddc2"]["fcuList"].append({
        "channel": 2, "stationId": 99, "position": 99, "id": "FC_UNMAPPED_99", "address": 9999,
        "switchStatus": 1, "mode": 1, "fanSpeed": 3, "temperature": 24.0,
    })
    if len(bad["ddc2"]["fcuList"]) > 7:
        bad["ddc2"]["fcuList"][5]["temperature"] = 85.0
        bad["ddc2"]["fcuList"][6].update({"switchStatus": -1, "mode": -1, "fanSpeed": -1, "temperature": 0})
    payload = bad
    print("已加入異常數據：溫度超範圍、累計值倒退、UpdateTime 偏移 8 小時、FCU 缺漏／未對照／疑似沒回應")

with open(payload_path, "w", encoding="utf-8") as f:
    json.dump(payload, f, ensure_ascii=False)
with open(abnormal_ids_path, "w", encoding="utf-8") as f:
    f.write("\n".join(str(i) for i in abnormal_ids))

total = sum(len(l) for l in ddc_lists.values())
stopped = sum(1 for l in ddc_lists.values() for s in l if s["switchStatus"] == 0)
print(f"冰水主機：1 台正常、1 台異常。FCU 共 {total} 台：{len(abnormal_ids)} 台異常、"
      f"{stopped} 台停止、其餘 {total - len(abnormal_ids) - stopped} 台正常運轉")
PYEOF

ok "已產生模擬快照：$PAYLOAD_FILE"

post_connections() {
  for channel in 0 1 2; do
    docker compose exec -T api curl -fsS -X POST http://localhost:8080/internal/ingest/connection \
      -H "X-Internal-Token: $INTERNAL_TOKEN" -H "Content-Type: application/json" \
      -d "{\"channel\":$channel,\"ip\":\"192.168.10.$((10+channel))\",\"port\":\"502\",\"connectionState\":2,\"isConnected\":true,\"triggerTimeUtc\":\"$(date -u +%Y-%m-%dT%H:%M:%S.000Z)\"}" \
      > /dev/null
  done
}

# 用前台的公開唯讀端點（不用登入）驗證是不是「全部三個通道」都真的顯示已連線——
# 不是猜一個固定延遲就假設一定成功，是真的檢查過才敢說完成。
all_channels_connected() {
  docker compose exec -T api curl -s http://localhost:8080/api/v1/public/chillers 2>/dev/null | python3 -c "
import json, sys
try:
    data = json.load(sys.stdin)
    sys.exit(0 if all(c['dataQuality']['isConnected'] for c in data) else 1)
except Exception:
    sys.exit(1)
" && docker compose exec -T api curl -s http://localhost:8080/api/v1/public/fcus 2>/dev/null | python3 -c "
import json, sys
try:
    data = json.load(sys.stdin)
    channels = {f['dataQuality']['channel'] for f in data}
    sys.exit(0 if channels and all(f['dataQuality']['isConnected'] for f in data) else 1)
except Exception:
    sys.exit(1)
"
}

step "送出連線狀態（Gateway/DDC1/DDC2 全部標記為已連線）"
post_connections
ok "3 個通道都已標記為連線中"

if [[ "$WITH_ANOMALIES" == "true" ]]; then
  step "先送一筆正常快照當作「上一筆」"
  docker compose exec -T api curl -fsS -X POST http://localhost:8080/internal/ingest/data \
    -H "X-Internal-Token: $INTERNAL_TOKEN" -H "Content-Type: application/json" \
    --data-binary @- < "$BASELINE_FILE" > /dev/null
  ok "正常快照已送出"
fi

step "送出設備快照"
docker compose exec -T api curl -fsS -X POST http://localhost:8080/internal/ingest/data \
  -H "X-Internal-Token: $INTERNAL_TOKEN" -H "Content-Type: application/json" \
  --data-binary @- < "$PAYLOAD_FILE" > /dev/null
ok "快照已送達，監控中心／前台戰情室應該馬上會更新（SignalR 廣播）"

step "驗證連線狀態（確認 collector 真的沒有再跟本腳本搶著寫）"
CONVERGED=false
for attempt in 1 2 3; do
  if all_channels_connected; then
    CONVERGED=true
    ok "已確認：Gateway／DDC1／DDC2 全部顯示已連線"
    break
  fi
  sleep 1
  post_connections
done
if [[ "$CONVERGED" != "true" ]]; then
  echo -e "${RED}✘ 驗證後仍有通道顯示未連線——先確認 collector 是不是真的停了：${NC}"
  echo "   docker compose ps collector（應該顯示 exited/stopped，不是 running）"
fi

step "幫異常 FCU／冰水主機建立對應的即時告警"
ABNORMAL_IDS=$(cat "$ABNORMAL_IDS_FILE")
if [[ -n "$ABNORMAL_IDS" ]]; then
  VALUES_SQL=""
  while IFS= read -r id; do
    [[ -n "$VALUES_SQL" ]] && VALUES_SQL+=","
    VALUES_SQL+="(1, $id, 'Temperature.0.28', 1, UTC_TIMESTAMP(), NULL, 28.5, '[DEMO-LIVE] 模擬即時資料，非真實告警')"
  done <<< "$ABNORMAL_IDS"
  VALUES_SQL+=",(0, (SELECT id FROM device_chiller ORDER BY modbus_id DESC LIMIT 1), 'IsBearingTemperatureTooHigh', 2, UTC_TIMESTAMP(), NULL, NULL, '[DEMO-LIVE] 模擬即時資料，非真實告警')"

  # 先刪掉舊的模擬即時告警再插入新的——沒做這一步的話，沒先跑 stop-live-simulation.sh
  # 就重複執行這支腳本，會一直疊加出同樣內容的重複告警列。
  docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac -e "
    DELETE FROM alarm_event WHERE memo = '[DEMO-LIVE] 模擬即時資料，非真實告警';
    INSERT INTO alarm_event (device_type, device_id, rule_code, severity, started_at, ended_at, peak_value, memo)
    VALUES $VALUES_SQL;
  "
  ok "已建立 $(echo "$ABNORMAL_IDS" | wc -l | tr -d ' ') 筆 FCU 即時告警 + 1 筆冰水主機即時告警"
fi

echo ""
ok "完成。重新整理監控中心／前台戰情室應該就能看到運轉中/停止/異常混合的畫面。"
if [[ "$WITH_ANOMALIES" == "true" ]]; then
  echo "   異常模式：用平台帳號打開 /platform/diagnostics，確認各項檢查有亮黃燈／紅燈。"
  echo "   注意：collector 被暫停，所以「Collector 程序」那張卡片顯示紅燈是預期的。"
fi
echo "   還原：./stop-live-simulation.sh"
