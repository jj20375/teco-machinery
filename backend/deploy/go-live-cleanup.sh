#!/bin/bash
# 交機前清理：把測試期間累積的「資料」清掉，保留所有「設定」。在要交機的那台 VM 上執行。
#
# 清掉：設備讀值（chiller_reading、fcu_reading）、每小時聚合、告警紀錄、連線紀錄（channel_health）、
#       保養履歷與基準點、登入 token（所有人要重新登入）、操作紀錄（可用 --keep-operation-log 保留）。
# 保留：帳號、角色、權限、場館、設備主檔、FCU 樓層分區配置、設備自訂代碼、告警門檻（只印出來給你檢查，不動）。
#
# 用法（在 VM 的 ~/teco/backend/deploy 執行）：
#   ./go-live-cleanup.sh                              只列出現況與會清掉什麼（預設，不改任何東西）
#   ./go-live-cleanup.sh --apply                      備份 → 停 Collector → 清除 → 重啟 API
#   ./go-live-cleanup.sh --apply --keep-operation-log 同上，但保留操作紀錄
#   ./go-live-cleanup.sh --apply --delete-user qa_admin --delete-user oplog_admin   順便刪掉這些測試帳號
#   --start-collector   清完後把 Collector 也開回來（預設不開：到現場要先改 .env 的設備 IP）
#   --yes               略過互動確認（只給自動化測試用，交機時請不要用）
#
# 清完之後 Collector 是停著的。改好 .env 的設備 IP 後：docker compose up -d collector（不能用 restart，不會重讀 .env）。
#
# 安全設計：預設只看不動；動手前一定先做一份備份（~/backup/pre-go-live_*.sql.gz，驗證過才繼續，且不會被每日備份的
# 清舊檔機制刪掉）；要輸入「清除 <專案名稱>」才會執行；不會 DROP 任何資料表，分割區定義也保留。
#
# 環境變數（測試這支腳本本身用，正式機不用設）：PROJECT（預設 teco-iot-area）、ENV_FILE（預設 ./.env）、BACKUP_DIR。
set -euo pipefail
cd "$(dirname "$0")"
export LC_ALL=C.UTF-8 LANG=C.UTF-8 2>/dev/null || export LC_ALL=C LANG=C

PROJECT="${PROJECT:-teco-iot-area}"
ENV_FILE="${ENV_FILE:-.env}"
BACKUP_DIR="${BACKUP_DIR:-$HOME/backup}"
DB_C="$PROJECT-mariadb-1"; COLLECTOR_C="$PROJECT-collector-1"; API_C="$PROJECT-api-1"

APPLY=0; KEEP_OPLOG=0; START_COLLECTOR=0; ASSUME_YES=0; DELETE_USERS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --apply) APPLY=1 ;;
    --keep-operation-log) KEEP_OPLOG=1 ;;
    --start-collector) START_COLLECTOR=1 ;;
    --yes) ASSUME_YES=1 ;;
    --delete-user) shift; [[ $# -gt 0 ]] || { echo "--delete-user 後面要接帳號" >&2; exit 2; }; DELETE_USERS+=("$1") ;;
    -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
    *) echo "不認得的參數：$1（--help 看用法）" >&2; exit 2 ;;
  esac
  shift
done

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; BOLD='\033[1m'; NC='\033[0m'
step() { echo -e "\n${BOLD}${YELLOW}== $1 ==${NC}"; }
ok()   { echo -e "${GREEN}✔ $1${NC}"; }
warn() { echo -e "${YELLOW}⚠ $1${NC}"; }
fail() { echo -e "${RED}✘ $1${NC}" >&2; exit 1; }

[[ -f "$ENV_FILE" ]] || fail "找不到 $ENV_FILE"
ROOT_PW="$(grep -E '^DB_ROOT_PASSWORD=' "$ENV_FILE" | head -1 | cut -d= -f2- | sed -E 's/^"(.*)"$/\1/')"
[[ -n "$ROOT_PW" ]] || fail "$ENV_FILE 裡沒有 DB_ROOT_PASSWORD"
docker inspect -f '{{.State.Running}}' "$DB_C" 2>/dev/null | grep -q true || fail "資料庫容器 $DB_C 沒在跑（PROJECT=$PROJECT 對嗎？）"

# 密碼走環境變數，不出現在指令列。
# sql 只給「從 stdin 餵 SQL」的地方用（heredoc）；其餘一律 sqle，stdin 接 /dev/null：
# docker exec -i 會吃掉標準輸入，不隔開的話後面互動確認的 read 會讀到空的而失敗。
sql()  { docker exec -i -e MYSQL_PWD="$ROOT_PW" "$DB_C" mariadb -uroot teco_hvac "$@"; }
sqle() { docker exec -e MYSQL_PWD="$ROOT_PW" "$DB_C" mariadb -uroot teco_hvac "$@" </dev/null; }
sqlt() { sqle -t "$@"; }
one()  { sqle -N -B -e "$1" | tail -1; }

step "目前在清哪一套：$PROJECT"
echo "資料庫容器：$DB_C"
if [[ "$PROJECT" == "teco-iot-area" ]] && docker ps --format '{{.Names}}' | grep -q '^teco-iot-test-'; then
  warn "測試環境（teco-iot-test）還在跑。它是獨立的另一套，不會被這支腳本清到；交機前別忘了 vm/test-env.sh down。"
fi

step "資料現況（會被清掉的）"
sqlt -e "
SELECT 'chiller_reading' AS 資料表, COUNT(*) AS 筆數, MIN(ts) AS 最早, MAX(ts) AS 最晚 FROM chiller_reading
UNION ALL SELECT 'fcu_reading', COUNT(*), MIN(ts), MAX(ts) FROM fcu_reading
UNION ALL SELECT 'rollup_chiller_1h', COUNT(*), MIN(bucket), MAX(bucket) FROM rollup_chiller_1h
UNION ALL SELECT 'rollup_fcu_1h', COUNT(*), MIN(bucket), MAX(bucket) FROM rollup_fcu_1h
UNION ALL SELECT 'alarm_event', COUNT(*), MIN(started_at), MAX(started_at) FROM alarm_event
UNION ALL SELECT 'channel_health', COUNT(*), MIN(changed_at), MAX(changed_at) FROM channel_health
UNION ALL SELECT 'chiller_maintenance_log', COUNT(*), MIN(performed_at), MAX(performed_at) FROM chiller_maintenance_log
UNION ALL SELECT 'operation_log$([[ $KEEP_OPLOG -eq 1 ]] && echo '（保留）' || echo '')', COUNT(*), MIN(created_at), MAX(created_at) FROM operation_log
UNION ALL SELECT 'refresh_token', COUNT(*), MIN(created_at), MAX(created_at) FROM refresh_token;" 2>&1 | grep -v Warning
echo "（時間都是 UTC）"

READINGS="$(one "SELECT (SELECT COUNT(*) FROM chiller_reading) + (SELECT COUNT(*) FROM fcu_reading)")"
RECENT="$(one "SELECT (SELECT COUNT(*) FROM chiller_reading WHERE ts > UTC_TIMESTAMP() - INTERVAL 15 MINUTE) + (SELECT COUNT(*) FROM fcu_reading WHERE ts > UTC_TIMESTAMP() - INTERVAL 15 MINUTE)")"
if [[ "$RECENT" != "0" ]]; then
  warn "最近 15 分鐘內還有 $RECENT 筆新讀值：Collector 正在寫入。如果這台已經接上現場真實設備，這些是真實資料，清掉就回不來。"
elif [[ "$READINGS" != "0" ]]; then
  warn "有 $READINGS 筆讀值但最近沒有新增：請確認它們是測試／模擬資料，不是現場真實資料。"
fi

step "會保留的設定（請檢查有沒有需要現場調整的）"
echo "-- 帳號（測試帳號要不要刪？用 --delete-user 帳號）"
sqlt -e "SELECT u.username AS 帳號, u.display_name AS 名稱, COALESCE(GROUP_CONCAT(r.code SEPARATOR ','), IF(u.is_platform_admin = 1, '平台', '')) AS 角色,
         IF(MAX(m.is_owner) = 1, '擁有者', '') AS 備註, IFNULL(DATE(u.last_login_at), '') AS 最後登入
         FROM app_user u LEFT JOIN merchant_membership m ON m.user_id = u.id LEFT JOIN app_role r ON r.id = COALESCE(m.role_id, u.system_role_id)
         GROUP BY u.id ORDER BY u.id;" 2>&1 | grep -v Warning
echo "-- 告警門檻（照客戶需求確認；範圍／上限／下限不合理的現場要改）"
sqlt -e "SELECT IF(device_type = 0, CONCAT('冰水主機 ', scope), 'FCU') AS 設備, metric AS 指標,
         MAX(IF(operator = 1, threshold, NULL)) AS 下限, MAX(IF(operator = 0, threshold, NULL)) AS 上限
         FROM alarm_rule WHERE is_enabled = 1 GROUP BY device_type, scope, metric ORDER BY device_type, scope, metric;" 2>&1 | grep -v Warning
echo "-- 設備對照"
sqlt -e "SELECT (SELECT COUNT(*) FROM device_chiller) AS 冰水主機, (SELECT COUNT(*) FROM device_fcu) AS FCU,
         (SELECT COUNT(*) FROM device_floor_placement) AS 已配置座標的設備;" 2>&1 | grep -v Warning
warn "FCU 的樓層分區對照如果還是驗收用的模擬對照，要在「空間設備配置」頁依現場實際位置重新拖拉並存檔（這支腳本不會動它）。"

for u in "${DELETE_USERS[@]+"${DELETE_USERS[@]}"}"; do
  [[ "$u" =~ ^[A-Za-z0-9._-]+$ ]] || fail "帳號「$u」含有不允許的字元"
  info="$(one "SELECT CONCAT(IF(u.is_platform_admin = 1 OR u.system_role_id IS NOT NULL, 'P', '-'), IF(EXISTS(SELECT 1 FROM merchant_membership m WHERE m.user_id = u.id AND m.is_owner = 1), 'O', '-')) FROM app_user u WHERE u.username = '$u'")"
  [[ -n "$info" ]] || fail "找不到帳號「$u」"
  [[ "$info" == "--" ]] || fail "帳號「$u」是平台帳號或場館擁有者，這支腳本不刪（要刪請手動處理）"
done

if [[ $APPLY -eq 0 ]]; then
  step "預覽結束：沒有改動任何東西"
  echo "要實際清除請加 --apply；清除前會先備份。"
  [[ ${#DELETE_USERS[@]} -gt 0 ]] && echo "將刪除帳號：${DELETE_USERS[*]}"
  exit 0
fi

step "確認"
echo "即將清掉上面「資料現況」的全部資料$([[ $KEEP_OPLOG -eq 1 ]] && echo '（操作紀錄保留）')、重設保養基準點$([[ ${#DELETE_USERS[@]} -gt 0 ]] && echo "、刪除帳號 ${DELETE_USERS[*]}")。"
echo "清完所有人要重新登入，Collector 會維持停止，等你改好設備 IP 再開。"
if [[ $ASSUME_YES -ne 1 ]]; then
  read -r -p "確定要清除請輸入「清除 $PROJECT」：" answer
  [[ "$answer" == "清除 $PROJECT" ]] || fail "輸入不符，已取消，沒有改動任何東西。"
fi

step "備份（驗證通過才會繼續）"
mkdir -p "$BACKUP_DIR"
STAMP="$(date +%Y%m%d_%H%M%S)"
TMP="$BACKUP_DIR/.pre-go-live_$STAMP.sql.gz.tmp"; FINAL="$BACKUP_DIR/pre-go-live_${PROJECT}_$STAMP.sql.gz"
trap 'rm -f "$TMP"' EXIT
if ! docker exec -e MYSQL_PWD="$ROOT_PW" "$DB_C" mariadb-dump -uroot --single-transaction --routines --triggers teco_hvac 2>/dev/null </dev/null | gzip > "$TMP"; then
  fail "備份失敗，沒有改動任何東西"
fi
gzip -t "$TMP" || fail "備份檔驗證失敗，沒有改動任何東西"
[[ "$(stat -c %s "$TMP" 2>/dev/null || stat -f %z "$TMP")" -ge 2048 ]] || fail "備份檔太小，幾乎可以確定是空的，沒有改動任何東西"
zcat "$TMP" | tail -n 3 | grep -q "Dump completed" || fail "備份檔沒有完成標記（中途被截斷），沒有改動任何東西"
mv "$TMP" "$FINAL"; chmod 600 "$FINAL"
ok "備份完成：$FINAL"

step "停止 Collector（清的時候不能有人在寫入）"
docker stop "$COLLECTOR_C" >/dev/null 2>&1 && ok "Collector 已停止" || warn "Collector 容器沒在跑，略過"

step "清除"
# TRUNCATE 會保留資料表結構與分割區定義，只清資料；全部放進同一個連線依序執行。
sql <<SQL
TRUNCATE TABLE chiller_reading;
TRUNCATE TABLE fcu_reading;
TRUNCATE TABLE rollup_chiller_1h;
TRUNCATE TABLE rollup_fcu_1h;
TRUNCATE TABLE alarm_event;
TRUNCATE TABLE channel_health;
TRUNCATE TABLE chiller_maintenance_log;
TRUNCATE TABLE refresh_token;
$([[ $KEEP_OPLOG -eq 1 ]] || echo 'TRUNCATE TABLE operation_log;')
UPDATE device_chiller SET maintenance_baseline_hours = NULL, maintenance_baseline_at = NULL;
SQL
ok "資料已清除，保養基準點已重設"
for u in "${DELETE_USERS[@]+"${DELETE_USERS[@]}"}"; do
  sqle -e "DELETE FROM app_user WHERE username = '$u'"
  ok "已刪除帳號 $u"
done
# 讓所有人的舊 access token 也失效（refresh token 已清；access token 靠 AuthVersion 比對）
sqle -e "UPDATE app_user SET auth_version = auth_version + 1"

step "重啟 API（清掉它記憶體裡的舊狀態）"
docker restart "$API_C" >/dev/null && ok "API 已重啟"
if [[ $START_COLLECTOR -eq 1 ]]; then
  docker start "$COLLECTOR_C" >/dev/null && ok "Collector 已啟動"
fi

step "完成後的資料"
sqlt -e "SELECT (SELECT COUNT(*) FROM chiller_reading) + (SELECT COUNT(*) FROM fcu_reading) AS 讀值,
         (SELECT COUNT(*) FROM alarm_event) AS 告警, (SELECT COUNT(*) FROM channel_health) AS 連線紀錄,
         (SELECT COUNT(*) FROM operation_log) AS 操作紀錄, (SELECT COUNT(*) FROM app_user) AS 帳號,
         (SELECT COUNT(*) FROM device_floor_placement) AS 已配置設備;" 2>&1 | grep -v Warning
echo
ok "清理完成。備份在 $FINAL（資料庫還原方式見 docs/正式機首次部署手冊.md 附錄 D.4）"
if [[ $START_COLLECTOR -ne 1 ]]; then
  echo -e "${BOLD}下一步：${NC}編輯 .env 填入現場設備 IP，然後 ${BOLD}docker compose up -d collector${NC}（Collector 目前是停止的）。"
fi
