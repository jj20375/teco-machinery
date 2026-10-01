#!/bin/bash
# VM 內：每天備份資料庫到 ~/backup/（docs/正式機首次部署手冊.md 附錄 D.1，由 cron 呼叫）。
#
# 為什麼不用一行 `mariadb-dump | gzip > 檔案 && find ... -delete`：管線的結束代碼是 gzip 的，
# mariadb-dump 中途失敗時 gzip 仍會成功產生一個幾乎是空的檔案，接著照常清掉舊備份。
# 連續失敗 30 天，好的備份會被空檔案一個個換掉，而且沒人會發現。這支腳本改成：
#   1. 先寫到暫存檔，不碰正式檔名；
#   2. 驗證通過（gzip 完整、大小合理、結尾有 mariadb-dump 的「Dump completed」）才改名成正式備份；
#   3. 只有這次備份成功才清舊檔，而且至少保留最近 MIN_KEEP 份，不會把備份清空。
#
# 用法：
#   ~/teco/backend/deploy/host-setup/06-vm-db-backup.sh              # 備份一次
#   crontab：0 3 * * * ~/teco/backend/deploy/host-setup/06-vm-db-backup.sh >> ~/backup/cron.log 2>&1
set -uo pipefail

DEPLOY_DIR="$HOME/teco/backend/deploy"
BACKUP_DIR="$HOME/backup"
KEEP_DAYS=30
MIN_KEEP=7
MIN_BYTES=2048     # 完整的備份（含資料表結構與分割區定義）遠大於這個值，低於它幾乎可以確定是空的
LOG="$BACKUP_DIR/backup.log"

mkdir -p "$BACKUP_DIR"
log() { echo "$(date '+%F %T') $*" | tee -a "$LOG"; }

STAMP="$(date +%Y%m%d_%H%M%S)"
TMP="$BACKUP_DIR/.teco_hvac_$STAMP.sql.gz.tmp"
FINAL="$BACKUP_DIR/teco_hvac_$STAMP.sql.gz"
trap 'rm -f "$TMP"' EXIT

fail() { log "失敗：$*"; exit 1; }

cd "$DEPLOY_DIR" || fail "找不到 $DEPLOY_DIR"

# 密碼用容器內的 MARIADB_ROOT_PASSWORD 環境變數，不寫在指令列或 crontab 上。
if ! docker compose exec -T mariadb sh -c \
  'mariadb-dump -u root -p"$MARIADB_ROOT_PASSWORD" --single-transaction --routines --triggers teco_hvac' \
  </dev/null 2>>"$LOG" | gzip > "$TMP"; then
  fail "mariadb-dump 或 gzip 執行失敗"
fi
# 管線裡 mariadb-dump 失敗時 pipefail 會讓整條管線回傳失敗，上面的 if 已經擋掉；下面再驗證檔案本身。
gzip -t "$TMP" 2>>"$LOG" || fail "備份檔 gzip 驗證失敗（檔案不完整）"
SIZE="$(stat -c %s "$TMP")"
[ "$SIZE" -ge "$MIN_BYTES" ] || fail "備份檔只有 ${SIZE} bytes，小於 ${MIN_BYTES}，幾乎可以確定是空的"
zcat "$TMP" | tail -n 3 | grep -q "Dump completed" || fail "備份檔結尾沒有 mariadb-dump 的完成標記（中途被截斷）"

mv "$TMP" "$FINAL"
chmod 600 "$FINAL"

# 只有成功才清舊檔，而且至少保留最近 MIN_KEEP 份
COUNT="$(find "$BACKUP_DIR" -maxdepth 1 -name 'teco_hvac_*.sql.gz' | wc -l)"
PRUNED=0
if [ "$COUNT" -gt "$MIN_KEEP" ]; then
  while IFS= read -r old; do
    [ "$(find "$BACKUP_DIR" -maxdepth 1 -name 'teco_hvac_*.sql.gz' | wc -l)" -gt "$MIN_KEEP" ] || break
    rm -f "$old" && PRUNED=$((PRUNED + 1))
  done < <(find "$BACKUP_DIR" -maxdepth 1 -name 'teco_hvac_*.sql.gz' -mtime +"$KEEP_DAYS" | sort)
fi

log "成功：$(basename "$FINAL")（$((SIZE / 1024)) KB），目前共 $((COUNT - PRUNED)) 份，清除 $PRUNED 份超過 ${KEEP_DAYS} 天的舊檔"
