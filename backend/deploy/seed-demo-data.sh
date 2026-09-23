#!/bin/bash
# 產生「歷史模擬資料」給報表/趨勢圖/熱力圖用——因為現場 IoT 還沒連線，Collector 沒有真實資料
# 可寫，這兩張報表頁面（統計報表、異常告警報表）打開來一片空白，demo 給同事看時很難看懂功能。
#
# 範圍刻意只做「歷史資料表」，不動「即時記憶體狀態」：
#   - 有做：rollup_chiller_1h / rollup_fcu_1h（報表頁查的就是這兩張，見
#     hvac-service.ts 的 getChillerHistoryApi/getFcuHistoryApi interval='1h' 預設值）、
#     幾筆已結束的 alarm_event（異常告警報表用）、**今天**（Asia/Taipei 當地日期）的
#     fcu_reading 原始讀值——前台戰情室左側「每小時 FCU 啟用數」/「每小時溫度變化」
#     這兩張小圖表查的是 FcuEndpoints.BuildHourlyStatsAsync，直接讀 fcu_reading 原始表
#     按小時分組，不是查 rollup_fcu_1h，所以光灌 rollup 這兩張圖表還是空的，要另外補。
#   - 沒做：監控中心／前台戰情室那些「現在幾 %／運轉中」的即時卡片。那些讀的是
#     CurrentStateStore（API 記憶體），只有 Collector 真的連線送資料才會更新，重開 API
#     就會消失——不是這支腳本能填的資料，會繼續顯示「離線」，這是預期中的行為
#     （要讓這部分也有東西看，見 simulate-live-data.sh）。
#   - chiller_reading 原始表沒有對應的前台小圖表在用，維持不動。
#
# 用法：
#   cd backend/deploy
#   ./seed-demo-data.sh              # 預設灌最近 7 天的每小時資料
#   ./seed-demo-data.sh --days 14    # 灌最近 14 天（上限 30 天，避免產生器裡的數字表不夠用）
#
# 可重複執行：用 ON DUPLICATE KEY UPDATE，重跑只會覆蓋同一段時間的值，不會疊加出重複資料。
# 清除時用 ./clear-demo-data.sh，會讀這支腳本寫的 .demo-data-range 檔案，只刪這批模擬資料
# 涵蓋的時間範圍，不會動到範圍外的真實資料。
set -euo pipefail
cd "$(dirname "$0")"
export LC_ALL=C LANG=C

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; BOLD='\033[1m'; NC='\033[0m'
step() { echo -e "\n${BOLD}${YELLOW}== $1 ==${NC}"; }
ok()   { echo -e "${GREEN}✔ $1${NC}"; }
fail() { echo -e "${RED}✘ $1${NC}"; exit 1; }

DAYS=7
if [[ "${1:-}" == "--days" ]]; then
  DAYS="${2:?請指定天數，例如 --days 14}"
fi
if (( DAYS < 1 || DAYS > 30 )); then
  fail "--days 必須介於 1~30（產生器用的數字表最多支援 1000 小時，30 天=720 小時還留了緩衝）"
fi
HOURS=$((DAYS * 24))

[[ -f .env ]] || fail "找不到 .env，請先在這個目錄跑過 docker compose（或 test-permissions.sh）建立好環境"
source .env
: "${DB_ROOT_PASSWORD:?.env 裡沒有 DB_ROOT_PASSWORD}"

step "確認 mariadb 容器在跑"
docker compose ps mariadb --format '{{.State}}' | grep -q running || fail "mariadb 容器沒在跑，先 docker compose up -d"

# 用 UTC 計算範圍並記錄下來，clear-demo-data.sh 只會刪這個範圍內的列，不會動範圍外的真實資料。
FROM_BUCKET=$(docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" -N -e \
  "SELECT DATE_FORMAT(UTC_TIMESTAMP() - INTERVAL $HOURS HOUR, '%Y-%m-%d %H:00:00');" 2>/dev/null)
TO_BUCKET=$(docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" -N -e \
  "SELECT DATE_FORMAT(UTC_TIMESTAMP(), '%Y-%m-%d %H:00:00');" 2>/dev/null)

# 前台「每小時 FCU 啟用數/溫度變化」查的是 Asia/Taipei 當地「今天」00:00~現在這個範圍
# （TimeZoneInfo.Local，容器 TZ=Asia/Taipei，UTC+8 沒有日光節約時間，直接用固定 8 小時
# 換算即可，不需要動用 CONVERT_TZ／時區資料庫）。
TODAY_FROM_UTC=$(docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" -N -e \
  "SELECT DATE_FORMAT(DATE(UTC_TIMESTAMP() + INTERVAL 8 HOUR) - INTERVAL 8 HOUR, '%Y-%m-%d %H:%i:%s');" 2>/dev/null)
TODAY_HOUR_COUNT=$(docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" -N -e \
  "SELECT HOUR(UTC_TIMESTAMP() + INTERVAL 8 HOUR) + 1;" 2>/dev/null)

step "產生最近 $DAYS 天（$FROM_BUCKET ~ $TO_BUCKET，UTC）的每小時模擬資料"

docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac <<SQL
SET @from_bucket = '$FROM_BUCKET';
SET @hour_count = $HOURS;

-- 0~999 的數字表：用三個 0~9 的子查詢交叉相乘湊出來，不用 recursive CTE，
-- 避免 MariaDB 的 cte_max_recursion_depth（預設 1000）在天數調大時剛好卡到上限。
DROP TEMPORARY TABLE IF EXISTS tmp_digits;
CREATE TEMPORARY TABLE tmp_digits (n INT);
INSERT INTO tmp_digits (n) VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9);

DROP TEMPORARY TABLE IF EXISTS tmp_hours;
CREATE TEMPORARY TABLE tmp_hours AS
SELECT (u.n + t.n*10 + h.n*100) AS offset_h
FROM tmp_digits u, tmp_digits t, tmp_digits h
WHERE (u.n + t.n*10 + h.n*100) < @hour_count;

-- 冰水主機每小時彙總：負載率跟耗電量帶一點日夜週期（白天高、深夜低），其餘欄位小幅隨機浮動，
-- 數字只是為了讓報表/趨勢圖看起來有東西可看，不代表任何真實現場數據。
INSERT INTO rollup_chiller_1h
  (device_id, bucket, avg_load_pct, min_chilled_out, max_chilled_out, min_chilled_in, max_chilled_in,
   avg_chilled_delta, avg_power_kw, kwh_delta, running_hours, run_minutes)
SELECT
  c.id,
  @from_bucket + INTERVAL th.offset_h HOUR,
  ROUND(55 + 20*SIN((HOUR(@from_bucket + INTERVAL th.offset_h HOUR) - 8) / 24 * 2 * PI()) + (RAND()*10-5), 1),
  ROUND(6.8 + (RAND()*0.6-0.3), 1),
  ROUND(7.3 + (RAND()*0.6-0.3), 1),
  ROUND(12.0 + (RAND()*0.6-0.3), 1),
  ROUND(12.6 + (RAND()*0.6-0.3), 1),
  ROUND(5.0 + (RAND()*0.4-0.2), 1),
  ROUND(45 + 15*SIN((HOUR(@from_bucket + INTERVAL th.offset_h HOUR) - 8) / 24 * 2 * PI()) + (RAND()*6-3), 1),
  ROUND(35 + RAND()*15, 1),
  th.offset_h DIV 24 + 1,
  FLOOR(40 + RAND()*20)
-- 故意用逗號連接（不是 CROSS JOIN）：MariaDB 把 CROSS JOIN 當 INNER JOIN 的同義詞，
-- 語法上可以接 ON，緊接著後面的 ON DUPLICATE KEY UPDATE 會被誤判成 JOIN 的 ON 條件，
-- 導致「KEY UPDATE」處語法錯誤——逗號連接沒有這個歧義，兩者在這裡效果完全相同。
FROM tmp_hours th, device_chiller c
ON DUPLICATE KEY UPDATE
  avg_load_pct = VALUES(avg_load_pct), min_chilled_out = VALUES(min_chilled_out),
  max_chilled_out = VALUES(max_chilled_out), min_chilled_in = VALUES(min_chilled_in),
  max_chilled_in = VALUES(max_chilled_in), avg_chilled_delta = VALUES(avg_chilled_delta),
  avg_power_kw = VALUES(avg_power_kw), kwh_delta = VALUES(kwh_delta),
  running_hours = VALUES(running_hours), run_minutes = VALUES(run_minutes);

-- FCU 每小時彙總：室溫在 24~27℃ 帶小幅浮動，on_minutes 大部分時間接近滿載運轉。
INSERT INTO rollup_fcu_1h (device_id, bucket, avg_temp, min_temp, max_temp, on_minutes)
SELECT
  f.id,
  @from_bucket + INTERVAL th.offset_h HOUR,
  ROUND(25.5 + (RAND()*2-1), 1),
  ROUND(24.5 + (RAND()*1-0.5), 1),
  ROUND(26.5 + (RAND()*1-0.5), 1),
  FLOOR(45 + RAND()*15)
FROM tmp_hours th, device_fcu f
ON DUPLICATE KEY UPDATE
  avg_temp = VALUES(avg_temp), min_temp = VALUES(min_temp), max_temp = VALUES(max_temp),
  on_minutes = VALUES(on_minutes);

-- 前台「每小時 FCU 啟用數/溫度變化」這兩張小圖表查的是 fcu_reading 原始表（不是上面的
-- rollup_fcu_1h），只需要今天（Asia/Taipei 當地日期）每小時一筆代表值即可，不用灌到跟
-- 現場 5 秒一筆一樣密。裝置 id 除以 8 餘 0 的設成關閉，製造跟模擬即時資料類似的「大部分
-- 開啟、少數關閉」畫面，純粹是為了圖表好看，不代表真實現場狀態。
SET @today_from_utc = '$TODAY_FROM_UTC';
SET @today_hour_count = $TODAY_HOUR_COUNT;

DROP TEMPORARY TABLE IF EXISTS tmp_today_hours;
CREATE TEMPORARY TABLE tmp_today_hours AS
SELECT (u.n + t.n*10) AS offset_h
FROM tmp_digits u, tmp_digits t
WHERE (u.n + t.n*10) < @today_hour_count;

INSERT INTO fcu_reading (device_id, ts, switch_status, mode, fan_speed, temperature, read_status)
SELECT
  f.id,
  @today_from_utc + INTERVAL th.offset_h HOUR + INTERVAL 30 MINUTE,
  CASE WHEN MOD(f.id, 8) = 0 THEN 0 ELSE 1 END,
  1, 3,
  ROUND(25.3 + (RAND()*1.6-0.8) + 1.2*SIN((th.offset_h-8)/24*2*PI()), 1),
  1
FROM tmp_today_hours th, device_fcu f
ON DUPLICATE KEY UPDATE
  switch_status = VALUES(switch_status), temperature = VALUES(temperature), read_status = VALUES(read_status);

DROP TEMPORARY TABLE tmp_today_hours;

-- 幾筆「已結束」的模擬告警，給異常告警報表用；memo 一律帶 [DEMO] 前綴，
-- clear-demo-data.sh 靠這個字串精準只刪這批資料，不會動到真實告警。
-- 只造已結束的（ended_at 有值），不造仍在告警中的，避免監控中心/FCU管理頁那些
-- 即時輪詢 alarm 的地方（liistAlarmsApi('active')）忽然冒出跟即時卡片對不起來的假告警。
INSERT INTO alarm_event (device_type, device_id, rule_code, severity, started_at, ended_at, peak_value, memo)
(SELECT 0, c1.id, 'ChilledWaterOutletTemperature.0.8', 1,
        @from_bucket + INTERVAL 12 HOUR, @from_bucket + INTERVAL 13 HOUR, 8.6, '[DEMO] 模擬資料，非真實告警'
 FROM device_chiller c1 ORDER BY c1.id LIMIT 1)
UNION ALL
(SELECT 0, c2.id, 'IsBearingTemperatureTooHigh', 2,
        @from_bucket + INTERVAL 40 HOUR, @from_bucket + INTERVAL 40.5 HOUR, NULL, '[DEMO] 模擬資料，非真實告警'
 FROM device_chiller c2 ORDER BY c2.id DESC LIMIT 1)
UNION ALL
(SELECT 1, f1.id, 'Temperature.0.28', 1,
        @from_bucket + INTERVAL 20 HOUR, @from_bucket + INTERVAL 21 HOUR, 28.4, '[DEMO] 模擬資料，非真實告警'
 FROM device_fcu f1 WHERE f1.floor = 'B1' ORDER BY f1.id LIMIT 1)
UNION ALL
(SELECT 1, f2.id, 'Temperature.1.18', 1,
        @from_bucket + INTERVAL 60 HOUR, @from_bucket + INTERVAL 60.3 HOUR, 17.6, '[DEMO] 模擬資料，非真實告警'
 FROM device_fcu f2 WHERE f2.floor = 'B2' ORDER BY f2.id LIMIT 1);

DROP TEMPORARY TABLE tmp_hours;
DROP TEMPORARY TABLE tmp_digits;
SQL

ok "已灌入 rollup_chiller_1h / rollup_fcu_1h（$FROM_BUCKET ~ $TO_BUCKET）、今天的 fcu_reading（Asia/Taipei 00:00~現在）與 4 筆模擬告警"

# 記錄這次灌的範圍，clear-demo-data.sh 靠這個檔案知道要刪哪一段，不用猜。
TODAY_TO_UTC=$(docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" -N -e \
  "SELECT DATE_FORMAT('$TODAY_FROM_UTC' + INTERVAL $TODAY_HOUR_COUNT HOUR, '%Y-%m-%d %H:%i:%s');" 2>/dev/null)
cat > .demo-data-range <<EOF
FROM_BUCKET="$FROM_BUCKET"
TO_BUCKET="$TO_BUCKET"
TODAY_FROM_UTC="$TODAY_FROM_UTC"
TODAY_TO_UTC="$TODAY_TO_UTC"
EOF
ok "範圍已記錄到 backend/deploy/.demo-data-range，供 ./clear-demo-data.sh 使用"

step "統計"
docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac -e "
SELECT 'rollup_chiller_1h' AS 資料表, COUNT(*) AS 筆數 FROM rollup_chiller_1h WHERE bucket BETWEEN '$FROM_BUCKET' AND '$TO_BUCKET'
UNION ALL
SELECT 'rollup_fcu_1h', COUNT(*) FROM rollup_fcu_1h WHERE bucket BETWEEN '$FROM_BUCKET' AND '$TO_BUCKET'
UNION ALL
SELECT 'fcu_reading(今天)', COUNT(*) FROM fcu_reading WHERE ts >= '$TODAY_FROM_UTC' AND ts < '$TODAY_TO_UTC'
UNION ALL
SELECT 'alarm_event(demo)', COUNT(*) FROM alarm_event WHERE memo = '[DEMO] 模擬資料，非真實告警';
"

echo ""
ok "完成。去「統計報表」「異常告警報表」頁面、以及前台戰情室左側的兩張小圖表都應該看得到資料了。"
echo "   注意：監控中心／前台戰情室右側那些「現在幾 %／運轉中」的即時卡片還是會顯示離線，"
echo "   那是另一套即時機制，這支腳本管不到，要看那部分請跑 ./simulate-live-data.sh。"
