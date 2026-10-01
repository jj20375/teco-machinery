# 3. 從 Mac／維運端遠端操作

> 第一次部署的完整流程（含資料庫搬移）見 [`docs/正式機首次部署手冊.md`](../../../docs/正式機首次部署手冊.md)。
> 這份文件只講「部署完之後，平常怎麼從 Mac 遠端操作」。

## 3.1 先把你的 SSH 公鑰放到 VM

**一定要在執行 `02-ubuntu-docker-setup.sh` 之前做**。那支腳本會關掉 SSH 密碼登入；
如果公鑰還沒放好，之後就只能從 Hyper-V 主控台視窗操作（腳本會偵測到沒有公鑰而跳過關閉密碼登入，
但仍建議照順序做）。

連線方式（Tailscale、`~/.ssh/config` 別名 `teco-vm-tailscale`、專用金鑰）的完整做法見
[`docs/正式機首次部署手冊.md`](../../../docs/正式機首次部署手冊.md) 附錄 A。以下的指令都用那份文件設定的別名，
不要寫死 VM 的區網 IP（Default Switch 的 NAT 位址重開機可能改變）。

```bash
ssh-copy-id -i ~/.ssh/id_ed25519_teco.pub teco-vm-tailscale
```

確認可以無密碼登入：

```bash
ssh teco-vm-tailscale
```

## 3.2 部署／更新程式：一律 SSH 進 VM 執行

⚠️ **不要用 `docker --context teco compose ... up` 從 Mac 遠端部署。**
`compose.yaml` 有好幾個 bind mount（`./mariadb/init`、`./mariadb/conf.d`、`./Caddyfile`、
`../../dist`），透過 docker context 執行時，這些路徑會被當成**VM 上的路徑**去找，而不是你 Mac
上的檔案。VM 上沒有那個路徑時 Docker 會自動建一個空資料夾掛進去，結果是：

- MariaDB 初始化腳本沒被執行 → 資料庫是空的，API 起來後每支查詢都失敗
- Caddyfile 變成空資料夾 → 網站容器起不來
- `dist` 是空的 → 就算網站起來也只有 404

所以程式碼要先同步到 VM，再在 VM 上執行 compose：

```bash
# Mac：建置前端，然後把專案同步到 VM（.env 與含密碼的文件不同步，VM 上的 .env 各自維護）
npm run build
rsync -av --delete \
  --exclude node_modules --exclude 'bin/' --exclude 'obj/' --exclude .git \
  --exclude 'backend/deploy/.env' --exclude 'docs/工作站主機資料.md' \
  ./ teco-vm-tailscale:~/teco/
```

```bash
# VM：重建並啟動有變更的服務
ssh teco-vm-tailscale
cd ~/teco/backend/deploy
docker compose up -d --build
```

`--delete` 會刪掉 VM 上「Mac 已經沒有」的檔案，讓兩邊保持一致；被 `--exclude` 的
`.env` 不受影響。

## 3.3 日常查看：可以用 docker context（不用 compose 檔）

查狀態、看 log、進容器這類操作不需要 bind mount，用 docker context 從 Mac 直接下指令沒問題：

```bash
docker context create teco --docker "host=ssh://teco-vm-tailscale"
```

```bash
# 查狀態（容器名稱固定是 teco-iot-area-<服務>-1，因為 compose.yaml 指定了 name: teco-iot-area）
docker --context teco ps

# 看某個服務的 log
docker --context teco logs -f --tail=200 teco-iot-area-collector-1

# 進容器排查問題
docker --context teco exec -it teco-iot-area-api-1 /bin/sh
```

`docker context` 底層是透過 SSH 把 Docker CLI 指令轉送到遠端 daemon，不需要開額外的埠。
不建議 `docker context use teco` 切成預設——之後在 Mac 本機下的 docker 指令會全部打到現場 VM，
很容易誤操作。

## 3.4 需要真的 SSH 進去時

```bash
ssh teco-vm-tailscale
```

用於：部署（見 3.2）、看 VM 本身的系統日誌（`journalctl`）、磁碟空間（`df -h`）、
Hyper-V 開機自啟是否生效（重開機後 `docker compose ps` 應該全部 Up）。

## 3.5 備份

> ⚠️ **請改用 `06-vm-db-backup.sh`**（見 `docs/正式機首次部署手冊.md` 附錄 D.1）。下面的 cron 一行寫法在備份失敗時
> 會產生近乎空的檔案並照常清掉舊備份，只保留當作歷史說明。

在 VM 上用 cron 每天備份。密碼直接用容器內的 `MARIADB_ROOT_PASSWORD` 環境變數，
不用寫在 crontab 或指令列上：

```bash
mkdir -p ~/backup
crontab -e
```

加入這一行（每天 03:00 備份、保留 30 天）：

```
0 3 * * * cd ~/teco/backend/deploy && docker compose exec -T mariadb sh -c 'mariadb-dump -u root -p"$MARIADB_ROOT_PASSWORD" --single-transaction --routines --triggers teco_hvac' | gzip > ~/backup/teco_hvac_$(date +\%Y\%m\%d).sql.gz && find ~/backup -name 'teco_hvac_*.sql.gz' -mtime +30 -delete
```

備份檔只放在 VM 內不夠（VM 壞了就一起沒了），要定期複製到 Windows 主機或其他機器：

```bash
# Mac 或 Windows（OpenSSH）
scp 'teco-vm-tailscale:~/backup/*.sql.gz' ./teco-backup/
```

備份的完整三層做法（VM 內 → 每天拉到 Windows → 複製到這台主機以外）與 log 空間上限，見
[`docs/正式機首次部署手冊.md`](../../../docs/正式機首次部署手冊.md) 附錄 D。Windows 端的拉取腳本是
`04-windows-pull-backup.ps1`。

Hyper-V 的檢查點（`Checkpoint-VM`）**不能取代備份**：它跟 VM 存在同一顆硬碟上，而且對執行中的資料庫
不保證資料一致。只適合在「改系統設定前」暫時留一個還原點。

還原方式：

```bash
gunzip -c teco_hvac_20261001.sql.gz | docker compose exec -T mariadb sh -c 'mariadb -u root -p"$MARIADB_ROOT_PASSWORD" teco_hvac'
```
