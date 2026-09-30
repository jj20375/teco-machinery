# IoT 現場接通驗證手冊

到現場接上冰水主機（漢鐘 Gateway）與兩台 DDC 時，用這份手冊確認三件事：

1. **程式有沒有接通**：三條通道都連上，資料持續送進系統。
2. **數據內容對不對**：欄位值合理、FCU 台數對得上、時間沒有偏移。
3. **系統有沒有持續作動**：資料有寫進資料庫、背景排程有在跑。

主要工具是平台管理底下的 **「系統診斷」頁（`/platform/diagnostics`）**。這份手冊說明怎麼搭配指令逐層排查，以及每個症狀代表什麼。

> 所有指令預設在現場 VM 上的 `~/teco/backend/deploy/` 目錄執行（先 `ssh teco@<VM IP>`）。
> 部署方式見 [`正式機首次部署手冊.md`](正式機首次部署手冊.md)；平常從 Mac 查 log／狀態的方式見
> `backend/deploy/host-setup/03-docker-context-from-mac.md`。

---

## 0. 三條通道一覽

| 通道 | 預設位址（暫填） | 協定 | 接的設備 |
|---|---|---|---|
| 漢鐘 Gateway | `192.168.10.198:4196`，baud 9600 | Modbus RTU over TCP | 冰水主機 ×2（ModbusId 1、2） |
| DDC1 | `192.168.10.12:502` | Modbus TCP | B1F FCU 64 台 |
| DDC2 | `192.168.10.14:502` | Modbus TCP | B2F FCU 31 台 |

⚠️ 上表的 IP **目前都是暫填值**，到現場要換成實際位址（見第 1 節）。

---

## 1. 出發前／到現場第一件事

### 1.1 帳號與權限

- 用**平台帳號**登入，登入後的身分選「平台管理」，左側選單才會出現「系統診斷」。
- 需要 `platform.diagnostics` 的讀取權限。`platform-admin` 角色預設就有；其他平台角色要到「角色管理」自己勾。
- **已經在跑的資料庫要手動補一次權限**（全新安裝會自動套用）：

```bash
source .env && docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" < mariadb/init/012_platform_diagnostics_permission.sql
```

補完後要重新登入，新的權限才會生效。

### 1.2 填入現場真實 IP

IP 不用改程式，只要改 `backend/deploy/.env`，沒填的欄位會用 `compose.yaml` 裡的暫填值：

```
HANBELL_IP=實際位址
HANBELL_PORT=4196
HANBELL_BAUD=9600
DDC1_IP=實際位址
DDC1_PORT=502
DDC2_IP=實際位址
DDC2_PORT=502
```

改完後重建 collector，讓新設定生效：

```bash
docker compose up -d --build collector
```

### 1.3 確認沒有在跑模擬資料

`simulate-live-data.sh` 會把 collector 停掉，並塞入假的「已連線」資料。到現場前一定要先還原，不然看到的綠燈是假的：

```bash
./stop-live-simulation.sh
```

```bash
docker compose ps
```

`collector`、`api`、`mariadb` 三個都要是 `running`／`healthy`。

---

## 2. 分層排查：由下往上

越底層越先查。**下層不通，上層一定不通**，不要跳著看。

### 第 1 層：網路（主機到設備）

從主機測 TCP 連線：

```bash
nc -vz -w 3 <DDC1_IP> 502
```

再從 **collector 容器內**測一次。容器跟主機的網路路徑不一定相同，這一步最能反映程式實際的連線狀況。容器裡沒有 `nc`，改用 `curl`：

```bash
docker compose exec collector curl -v --connect-timeout 3 -m 5 telnet://<DDC1_IP>:502 < /dev/null
```

判讀方式：

- 輸出出現 `Connected to` → 通了。之後因為 `-m 5` 逾時結束是正常的。
- 出現 `Failed to connect … Timeout was reached`、`Connection refused` 或 `No route to host` → 網路不通。這是網段、線路、防火牆或 vSwitch 的問題，跟程式無關。

漢鐘 Gateway（`<HANBELL_IP>:4196`）和 DDC2 也各測一次。

### 第 2 層：供應商 DLL 能不能讀到資料（探針）

`tools/probe/` 是獨立探針，不經過 API、資料庫或 collector，只回答一件事：供應商的 DLL 能不能從設備讀到資料。

⚠️ 探針的 IP 寫死在 `tools/probe/Program.cs` 裡（`192.168.10.198/12/14`）。現場 IP 不一樣的話，要先改這個檔案。

```bash
docker build -f ../tools/probe/Dockerfile.probe -t teco-probe ../tools/probe
```

```bash
docker run --rm --network teco-iot-area_teco-field teco-probe --connect
```

探針會觀察 30 秒。看到 `ConnectionState = Connected`，且資料事件 `IsReadSuccess = true`，代表 DLL 這一層沒問題。

### 第 3 層：Collector 程序

```bash
docker compose logs -f --tail=200 collector
```

要看的訊息：

| log 訊息 | 意思 |
|---|---|
| `設備對照表載入完成：2 台冰水主機、95 台 FCU` | 資料庫對照表讀取成功 |
| `通道 Ddc1 連線狀態變化：Connected` | 這條通道連上了 |
| `通道 … ConnectFailed` 或 `Disconnected` 反覆出現 | 連不上。回頭查第 1 層 |
| `找不到 Channel=… StationId=… Position=… 對應的 device_fcu` | 現場有這台 FCU，但資料庫沒有對照，這台的資料不會寫入資料庫 |
| `找不到 ModbusId=… 對應的 device_chiller` | 冰水主機的站號跟資料庫對不上 |
| `超過 300 秒未收到任何事件，重建 Collector` | DLL 卡住，已自動重建。偶爾一次可以接受，頻繁出現要回報供應商 |

Collector 自己的健康狀態（`healthy` 表示三條通道最近都有成功讀取，`degraded` 表示至少一條逾時）：

```bash
docker compose exec collector curl -s http://localhost:8080/healthz
```

### 第 4 層：系統診斷頁（主要工具）

用平台帳號打開 **`/platform/diagnostics`**。頁面每 5 秒自動更新。從上到下各區塊說明如下：

| 區塊 | 看什麼 | 正常的樣子 |
|---|---|---|
| 頂部總燈號 | 全部檢查的綜合結果 | 「正常」 |
| 通道連線 | 三條通道的 IP:Port、連線狀態、讀取狀態、最後成功讀取時間 | 顯示「已連線」、「讀取成功」，最後成功讀取在幾秒內 |
| Collector 程序 | Collector 容器有沒有回應、最後一次推送時間 | 「正常」，最後推送在 5 秒左右 |
| 檢查結果 | 推送、時間、數據內容、資料庫寫入逐項判定 | 全部「正常」 |
| 冰水主機逐欄數據 | 兩台主機 16 個欄位的目前值、上一筆值、暫定範圍 | 沒有黃底的欄位 |
| FCU 台數比對 | 現場回報台數、資料庫登記台數、兩邊對得上的台數，以及缺漏清單 | DDC1 是 64／64／64，DDC2 是 31／31／31 |
| 排程與資料庫落地 | 排程上次執行時間與結果；最近 10 分鐘寫入的筆數；最新的聚合整點 | 見第 5 節 |
| 連線變化紀錄 | `channel_health` 最近 30 筆連線狀態變化 | 連上之後不應該一直出現新的斷線紀錄 |

「FCU 台數比對」最下面可以展開 **FCU 原始讀值**，逐台列出開關、模式、風速、溫度，方便跟現場面板對照。

燈號的意思：

- **正常**（綠）
- **需確認**（黃）：不一定是壞掉，要人工判斷
- **異常**（紅）：一定有問題
- **無法判斷**（灰）：資料不足，例如通道還沒讀到資料，就無法判斷資料庫寫入是否正常

### 第 5 層：資料庫（最終證據）

診斷頁的「資料庫落地」區塊就是用下面這些查詢算出來的。需要直接確認時可以自己跑。注意欄位名稱：時序表的時間欄位是 `ts`，聚合表是 `bucket`，全部都是 UTC。

最近讀取成功的資料（`read_status = 1` 才算成功。2026-09-29 起 Collector 讀取不成功時不再寫入，但更早的舊資料裡還有 `read_status = 3` 的空資料，所以仍要排除）：

```bash
source .env && docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac -e "SELECT 'chiller' t, MAX(ts), COUNT(*) FROM chiller_reading WHERE ts >= UTC_TIMESTAMP() - INTERVAL 10 MINUTE AND read_status = 1 UNION ALL SELECT 'fcu', MAX(ts), COUNT(*) FROM fcu_reading WHERE ts >= UTC_TIMESTAMP() - INTERVAL 10 MINUTE AND read_status = 1;"
```

聚合排程的最新整點：

```bash
source .env && docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac -e "SELECT 'chiller', MAX(bucket) FROM rollup_chiller_1h UNION ALL SELECT 'fcu', MAX(bucket) FROM rollup_fcu_1h;"
```

連線狀態變化：

```bash
source .env && docker compose exec -T mariadb mariadb -u root -p"$DB_ROOT_PASSWORD" teco_hvac -e "SELECT * FROM channel_health ORDER BY changed_at DESC LIMIT 20;"
```

---

## 3. 症狀對照表

| 症狀（診斷頁或 log） | 可能原因 | 處理方式 |
|---|---|---|
| 通道一直「連線中」↔「連線失敗」循環 | 網路不通、IP 或 Port 填錯 | 回到第 1 層。確認 `.env` 的 IP，並用 `docker compose up -d --build collector` 重建 |
| 「已連線」但讀取狀態是「讀取失敗」 | TCP 通了，但 Modbus 站號、暫存器位址或鮑率不對 | 漢鐘確認 baud rate；DDC 確認站號設定，並請供應商確認 |
| Collector 程序紅燈「連不到 Collector」 | collector 容器停止，或還在跑模擬 | `docker compose ps`；如果有跑模擬就執行 `./stop-live-simulation.sh` |
| 「Collector → API 推送」紅燈 | collector 卡住，或 `INTERNAL_TOKEN` 不一致 | 看 collector log 有沒有 401；確認 `.env` 的 `INTERNAL_TOKEN` 兩邊用的是同一份 |
| 「事件時間與收到時間差」剛好差 8 小時 | 時區換算錯誤（UTC 和台北時間混用）。兩個時間都來自 Collector 主機（說明書 4.2：事件時間「不是設備量測時間」），**跟現場設備的時鐘無關** | 檢查主機與容器的時區（`TZ=Asia/Taipei`），不需要去查 DDC 或 Gateway |
| 時間差幾十秒到幾分鐘 | Collector 處理延遲或主機負載過高（兩個時間都在同一台主機產生，正常應在 1 秒內） | 看 collector log 有沒有大量錯誤、主機 CPU 是否滿載 |
| 「事件時間持續前進」黃燈 | 連續兩筆的事件時間相同，代表收到重複的同一筆事件 | 偶爾一次可以忽略；持續出現就回報供應商 |
| 冰水主機「讀取成功但所有數值都是 0」 | ModbusId 或暫存器位址對錯，讀到的是空白區 | 確認兩台主機的 Modbus 位址是 1 和 2 |
| 冰水主機某欄位黃底「超出暫定合理範圍」 | 設備真的處於特殊狀態，或者範圍訂得不對、單位錯 | 跟現場面板讀數比對（見第 4 節）。面板讀數一致，就調整範圍設定 |
| 「累計值比上一筆小（倒退）」 | 設備重置、讀到錯的暫存器，或數值溢位 | 跟面板比對。重複發生要回報供應商 |
| 「現場有回報、資料庫沒有對照」 | 現場的 FCU 站號／位置不在 `device_fcu` | 這批 FCU **不會寫入資料庫**。要補 `device_fcu` 資料，再到「空間設備配置」放上圖面 |
| 「資料庫有登記、現場沒有回報」 | 資料庫多登記了、現場那台沒接，或 DDC 設定少掃了 | 跟現場確認實際台數。不存在的就在資料庫停用（`is_active = 0`） |
| 「疑似沒回應（狀態全 Unknown 且溫度 0）」 | 那台 FCU 斷電或通訊線斷了 | 到現場檢查那台 FCU。**整層都是這樣**，通常是 DDC 本身沒有讀到 |
| 「資料寫入 chiller_reading／fcu_reading」紅燈 | 通道有讀到資料，但資料庫沒有新資料 | 看 collector log 有沒有資料庫錯誤，或「找不到 ModbusId／device_fcu」 |
| 聚合排程黃燈（聚合整點落後原始資料超過 3 小時） | 聚合排程停了或一直失敗 | 看 `docker compose logs api` 有沒有「每小時聚合排程失敗」 |
| 排程顯示「API 啟動後尚未執行」 | API 剛重啟，排程紀錄只存在記憶體 | 正常現象。聚合排程啟動時會立刻跑一次，幾秒後重新整理就會看到 |

---

## 4. 數據內容逐欄核對

程式判斷「合理範圍」只是第一道防線。**到現場一定要拿設備面板或東元人員的讀數，逐欄對一次**，確認欄位沒有對錯、單位沒有錯、小數點位數正確。

### 冰水主機（每台各對一次）

| 欄位 | 暫定範圍 | 對照面板 | 備註 |
|---|---|---|---|
| 冰水出水溫度 | 0~30 °C | ☐ | 常見 6~8 °C |
| 冰水入水溫度 | 0~30 °C | ☐ | 常見 11~13 °C |
| 冰水溫差 ΔT | -5~15 °C | ☐ | 應約等於入水減出水 |
| 冷卻水出水溫度 | 5~50 °C | ☐ | |
| 冷卻水入水溫度 | 5~50 °C | ☐ | |
| 趨近溫度 | -5~20 °C | ☐ | |
| 負載率 | 0~100 % | ☐ | |
| 輸入電壓 | 0~500 V | ☐ | 運轉中應接近 380 V |
| 輸入電流 | 0~2000 A | ☐ | |
| 輸入功率 | 0~2000 kW | ☐ | |
| 實際轉速 | 0~10000 rpm | ☐ | |
| 高壓 | ≥ 0 | ☐ | **單位待供應商確認**（程式裡已經把原始值除以 100） |
| 低壓 | ≥ 0 | ☐ | **單位待供應商確認** |
| 累計運轉時數 | ≥ 0，不可倒退 | ☐ | |
| 累計啟動次數 | ≥ 0，不可倒退 | ☐ | |
| 累計耗電量 | ≥ 0，不可倒退 | ☐ | |
| 各項警報旗標 | — | ☐ | 在面板製造或確認一個已知警報，看監控中心有沒有出現 |

### FCU（每層抽查至少 5 台）

- 在「FCU 原始讀值」找到該台（用站號／位置或 ID），跟現場溫控面板比對**溫度、開關、模式、風速**。
- 跟現場技術人員或供應商對設備時，用「FCU 管理」頁的**供應商編號**（例如「DDC1 · FC_MC1_01」），那是他們使用的名稱；注意同一個編號在 DDC1、DDC2 各有一台。
- 在現場**切換一台 FCU 的開關或風速**，大約 5~10 秒內診斷頁的原始讀值應該跟著改變。這是確認「真的是即時資料」最直接的方法。
- 確認各台 FCU 的實際位置後，要到「空間設備配置」重新拖拉存檔。**目前 `zone_code` 全部是模擬對照，交機前一定要覆蓋掉**（見 CLAUDE.md 已知限制）。

### 校正合理範圍

範圍集中寫在 `backend/src/Teco.Hvac.Api/Services/Diagnostics/DataValidationRules.cs`。拿到真實數據後，如果發現範圍不合理，就改這個檔案，然後重建：

```bash
docker compose up -d --build api
```

---

## 5. 接通後 24 小時觀察

接通當天看到全綠還不夠。隔天再打開診斷頁，確認下面每一項：

- [ ] **連線變化紀錄**：過去一天沒有頻繁的斷線、重連。偶爾一兩次可以接受，每小時都有就要查網路穩定性。
- [ ] **資料庫落地**：`fcu_reading` 最近 10 分鐘涵蓋 95／95 台，`chiller_reading` 涵蓋 2／2 台。
- [ ] **每小時聚合**：上次執行在 15 分鐘內、結果「正常」；「資料庫最新聚合整點」是 1~2 小時前的整點，並且持續往前推進。
- [ ] **分割區維護**：上次執行在 24 小時內、結果「正常」。
- [ ] **報表頁**：場館後台的報表能查到當天的每小時數據。
- [ ] **告警**：沒有大量誤報。如果有，到「告警門檻設定」調整門檻。
- [ ] collector log 沒有反覆出現「重建 Collector」。

---

## 6. 監視位置總表

| 想知道什麼 | 去哪裡看 |
|---|---|
| 整體有沒有接通、數據對不對、排程有沒有在跑 | 平台管理 → **系統診斷**（`/platform/diagnostics`） |
| 使用者看到的設備狀態（運轉、停止、離線） | 場館後台 → 監控中心、冰水主機、FCU 頁 |
| Collector 的即時 log | `docker compose logs -f collector` |
| 排程與 API 的 log | `docker compose logs -f api`，搜尋「聚合排程」或「分割區」 |
| Collector 健康狀態（給 Docker healthcheck 用） | `docker compose exec collector curl -s http://localhost:8080/healthz` |
| 容器是否健康 | `docker compose ps` |
| 歷史連線紀錄、原始讀值 | 資料庫：`channel_health`、`chiller_reading`、`fcu_reading`（第 2 節第 5 層） |

---

## 7. 不用去現場就能先驗證診斷頁

想確認診斷頁本身能不能正確亮燈（例如修改規則之後），可以用模擬腳本：

```bash
./simulate-live-data.sh --with-anomalies
```

這個指令會先送一筆正常資料，再送一筆刻意做壞的資料。診斷頁應該顯示以下結果：

| 預期結果 | 燈號 |
|---|---|
| 事件時間差 8 小時 | 紅 |
| 冰水主機 1：冰水出水溫度 45 °C、累計運轉時數倒退 | 黃 |
| DDC1：1 台資料庫有登記但現場沒有回報 | 黃 |
| DDC2：1 台沒有對照（站號 99／位置 99）、1 台溫度 85 °C、1 台疑似沒回應 | 黃 |
| Collector 程序 | 紅（腳本會暫停 collector，屬預期） |
| 資料庫寫入 | 紅（模擬資料只推給 API，不會寫進資料庫，屬預期） |

驗證完**一定要還原**：

```bash
./stop-live-simulation.sh
```

不加 `--with-anomalies` 時，數據內容與台數比對應該全部是綠燈。Collector 程序和資料庫寫入仍然會是紅燈，原因同上表。
