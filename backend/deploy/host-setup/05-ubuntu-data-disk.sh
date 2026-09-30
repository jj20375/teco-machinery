#!/bin/bash
# VM 內：把 Hyper-V 加進來的資料碟格式化並掛載到 /srv/teco-data（docs/正式機首次部署手冊.md 附錄 C.3 的腳本版）。
#
# 用法（在 VM 上）：
#   ./05-ubuntu-data-disk.sh --check     # 只檢查、列出會動哪顆碟，不改任何東西（不需要 sudo）
#   sudo ./05-ubuntu-data-disk.sh        # 實際執行；格式化前會要你輸入 YES 確認
#
# 防呆：只會選「整顆沒有分割區、沒有掛載、大小 >= 50 GiB」的磁碟，而且必須剛好一顆。
# 找不到、找到多顆、或 /srv/teco-data 已經掛著東西，一律中止，不會猜。系統碟一定有分割區與掛載點，不會被選中。
set -euo pipefail

MOUNT_POINT=/srv/teco-data
LABEL=teco-data
MIN_BYTES=$((50 * 1024 * 1024 * 1024))
CHECK_ONLY=0
[ "${1:-}" = "--check" ] && CHECK_ONLY=1

if findmnt -rn "$MOUNT_POINT" >/dev/null 2>&1; then
  echo "✔ $MOUNT_POINT 已經掛載，不需要再做："
  findmnt "$MOUNT_POINT"
  exit 0
fi

# 候選磁碟：type=disk、大小夠、自己沒有子節點（沒有分割區）、沒有掛載點、沒有檔案系統
candidates=()
while read -r name size; do
  [ "$size" -ge "$MIN_BYTES" ] || continue
  [ "$(lsblk -rno NAME "$name" | wc -l)" -eq 1 ] || continue
  [ -z "$(lsblk -rno MOUNTPOINTS "$name" | tr -d '[:space:]')" ] || continue
  [ -z "$(lsblk -rno FSTYPE "$name" | tr -d '[:space:]')" ] || continue
  candidates+=("$name")
done < <(lsblk -dbrnpo NAME,SIZE,TYPE | awk '$3=="disk"{print $1, $2}')

echo "== 目前磁碟"
lsblk -o NAME,SIZE,TYPE,FSTYPE,MOUNTPOINTS
echo

if [ "${#candidates[@]}" -eq 0 ]; then
  echo "✘ 找不到可用的資料碟（需要一顆沒有分割區、沒有掛載、>= 50 GiB 的新磁碟）。"
  echo "  請先在 Windows 主機執行手冊附錄 C.2（New-VHD 與 Add-VMHardDiskDrive）。"
  exit 1
fi
if [ "${#candidates[@]}" -gt 1 ]; then
  echo "✘ 找到多顆符合條件的磁碟，無法確定哪一顆是資料碟，中止：${candidates[*]}"
  exit 1
fi

DISK="${candidates[0]}"
echo "✔ 資料碟：$DISK（$(lsblk -dno SIZE "$DISK")，沒有分割區、沒有掛載）"
if [ "$CHECK_ONLY" -eq 1 ]; then
  echo "（--check 模式：沒有改動任何東西。要實際執行請用 sudo ./05-ubuntu-data-disk.sh）"
  exit 0
fi

if [ "$(id -u)" -ne 0 ]; then
  echo "✘ 實際執行需要 root，請用 sudo。" >&2
  exit 1
fi

echo
echo "⚠️  即將把 $DISK 整顆格式化成 ext4（標籤 $LABEL），上面的資料會全部清除。"
read -r -p "確認上面列的是新加的資料碟，輸入 YES 繼續：" answer </dev/tty
[ "$answer" = "YES" ] || { echo "已取消，沒有改動任何東西。"; exit 1; }

parted "$DISK" --script mklabel gpt mkpart "$LABEL" ext4 0% 100%
udevadm settle
PART="$(lsblk -lnpo NAME "$DISK" | sed -n '2p')"
[ -b "$PART" ] || { echo "✘ 找不到新建立的分割區，中止。" >&2; exit 1; }
mkfs.ext4 -q -L "$LABEL" "$PART"

# 空的掛載點設成不可寫入：資料碟萬一沒掛上，Docker 沒辦法在系統碟上偷偷建一個空資料庫，
# 資料庫容器會直接啟動失敗，而不是看起來正常、其實資料全空。
mkdir -p "$MOUNT_POINT"
chattr +i "$MOUNT_POINT"

# 用標籤而不是 sdb 掛載（磁碟代號可能變）；nofail 讓資料碟出問題時 VM 仍能開機，才連得進去修。
grep -q "LABEL=$LABEL " /etc/fstab || echo "LABEL=$LABEL $MOUNT_POINT ext4 defaults,nofail 0 2" >> /etc/fstab
systemctl daemon-reload
mount "$MOUNT_POINT"

mkdir -p "$MOUNT_POINT/mariadb"
echo
echo "✔ 完成："
findmnt "$MOUNT_POINT"
df -h "$MOUNT_POINT" | tail -1
echo "下一步：回到 docs/正式機首次部署手冊.md 第 6 步（.env 的 DB_DATA_DIR 應為 $MOUNT_POINT/mariadb）。"
