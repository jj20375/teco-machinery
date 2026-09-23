# 3. 從 Mac／維運端遠端操作（不用每次 SSH 進去手動下指令）

## 3.1 先把你的 SSH 公鑰放到 VM

```bash
ssh-copy-id teco@192.168.10.200
# 或手動把 ~/.ssh/id_ed25519.pub 內容加進 VM 的 ~/.ssh/authorized_keys
```

確認可以無密碼登入：

```bash
ssh teco@192.168.10.200
```

## 3.2 建立 Docker context（把遠端 daemon 當本機用）

```bash
docker context create teco --docker "host=ssh://teco@192.168.10.200"
docker context ls
docker context use teco
```

## 3.3 日常部署／維運，不用手動 SSH

```bash
# 部署（第一次或有程式碼變更時）
docker --context teco compose -f backend/deploy/compose.yaml up -d --build

# 查狀態
docker --context teco compose -f backend/deploy/compose.yaml ps

# 看某個服務的 log
docker --context teco compose -f backend/deploy/compose.yaml logs -f collector

# 進容器排查問題
docker --context teco compose -f backend/deploy/compose.yaml exec api /bin/sh
```

`docker context` 底層就是透過 SSH 把 Docker CLI 指令轉送到遠端 daemon，
不需要在本機開任何額外的埠，也不需要每次都手動 `ssh` 進去再打指令。

## 3.4 需要真的 SSH 進去時

```bash
ssh teco@192.168.10.200
```

用於：看 VM 本身的系統日誌（`journalctl`）、磁碟空間（`df -h`）、
Hyper-V 開機自啟是否生效（重開機後 `docker compose ps` 應該全部 Up）。

## 3.5 備份

```bash
# 排程（cron 或 Windows 工作排程器呼叫這段）：資料庫備份
docker --context teco exec teco-mariadb-1 \
  mariadb-dump -u root -p"$DB_ROOT_PASSWORD" --single-transaction teco_hvac \
  > "teco_hvac_$(date +%Y%m%d).sql"
```

再搭配 Hyper-V VM checkpoint 做整機層級的備份（`Checkpoint-VM -Name teco-hvac-linux`）。
