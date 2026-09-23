# 東元智慧環境監控 — 後端整合規劃（ASP.NET Core 10 + MariaDB + Docker on Windows）

> 依據：`docs/Teco_Golf_DataCollector/Teco_Golf_DataCollector_使用說明書.docx`（v1.0, .NET 8）
> 與 `docs/Teco_Golf_DataCollector/Release/*.dll`（實際交付組合）
> 撰寫日期：2026-09-14

---

## 0. 一句話結論

Data Collector 是一個 **push 模式、每 5 秒一次全場快照、唯讀** 的 .NET 函式庫，
所以後端要做的不是「去問設備」，而是「接住事件 → 節流落地 → 即時廣播 → 事後聚合」。
整體跑在 **Linux 容器**（Windows 只當宿主，用 Hyper-V VM 或 WSL2 裝 Docker Engine CE，不需要 Docker Desktop）。

---

## 1. 從說明書與 DLL 讀出來的硬事實

### 1.1 交付與框架

| 項目 | 事實 |
|---|---|
| 交付形式 | 純 DLL，**無原始碼**，8 個檔 |
| 目標框架 | `Teco_Golf_DataCollector` / `CS_Communication_*` / `CS_Protocol_Modbus` = **net8.0**；`CS_GeneralNS` / `CS_Network_CommonUse` = **netstandard2.0** |
| 外部相依 | 只有 `System.IO.Ports` 10.0.5（Windows 平面 DLL）。掃描組件後，**沒有** WinForms / Registry / System.Management 等 Windows-only 依賴 |
| `System.IO.Ports` 實際用途 | 只出現在 `CS_Communication_Modbus_Master_UART`（`m_serialPort`）。本案走的是 `_TCP` 與 `_Gateway`（`_NetBase`），**執行期不會碰到 SerialPort** |

**推論：這組 DLL 有很高機率可以在 Linux 容器裡跑。** 這是整份規劃能成立的關鍵前提，列為 P0 驗證項。

### 1.2 通訊拓撲

| 通道 | IP:Port | 協議 | 設備 |
|---|---|---|---|
| `HanbellModbusGateway` | 192.168.10.198:4196 | Gateway 轉 RTU，Baud 9600 | 漢鐘冰水主機 ×2（Modbus ID 1、2） |
| `DDC1` | 192.168.10.12:502 | Modbus TCP | B1F **64 台** FCU |
| `DDC2` | 192.168.10.14:502 | Modbus TCP | B2F **31 台** FCU |

現場電腦 IP：主 `192.168.50.199` / 進階 `192.168.10.199`（雙網段）。
→ **容器必須能路由到 192.168.10.0/24**，這是 Docker 網路設計的唯一硬需求。

### 1.3 執行時序（內部常數，API 不開放調整）

| 工作 | 週期 |
|---|---|
| 漢鐘輪詢 | 1 秒 |
| DDC1 / DDC2 輪詢 | 各 4 秒 |
| `EventDataReceived` | **5 秒** |
| 斷線重連 | 5 秒 |

### 1.4 資料有效性契約（不能忽略）

- `IsConnected` = TCP 通了；`IsReadSuccess` = 該組**最近一次完整輪詢**成功。
- 兩者無關：Gateway `Connected` 但漢鐘 `Failed` 是正常可能。
- 讀取失敗時**保留上一次的值**，`0` / `false` / `Unknow` 可能是初始值也可能是舊值。
- **沒有逐欄位時間戳、沒有 `LastSuccessTime`**，要由應用端自己記「上次成功的時間」。
- **沒有逐台 FCU 的成功旗標** — 整個 DDC 共用一個 `IsReadSuccess`。
- 即使斷線，`EventDataReceived` 仍每 5 秒送 → 事件有到 ≠ 讀取成功。

---

## 2. 對現有 spec / 設計稿的影響（重要，需決策）

這一節是規劃裡最該先看的部分，因為它會改動前後台的畫面與 `TECO_HVAC_SPECIFICATION.md`。

### 2.1 FCU 只有四個欄位

`FCUValue` = `SwitchStatus` / `Mode` / `FanSpeed` / `Temperature`(室內溫度, °C)。

**沒有「設定溫度 / setpoint」。**
→ spec §參.二 的「異常 = ΔT(室溫 − 設定溫度) 超過門檻」**目前做不出來**。
可行的只有兩條路：

1. 改用**絕對室溫上下限**（可依樓層／分區各自設定門檻）— 這與 Figma 前台 FCU 清單的呈現一致，也是我建議的預設。
2. 請供應商加開設定溫度暫存器（`Address = 40051 + (Position−1) × 16`，同一個 16-register 區塊裡很可能就有 setpoint）。

這正好可以結案 `teco-spec-review-findings` 的第 1 項（三方矛盾），答案偏向「絕對溫度」。

### 2.2 FCU 無法做「單台離線」

沒有逐台健康旗標 → 離線只有 **DDC（樓層）粒度**。
DDC1 `IsReadSuccess=false` 時，B1F 的 64 台只能整層標記為「資料不可信」。

補救啟發式（需現場驗證後才採用）：某台 `SwitchStatus/Mode/FanSpeed` 全為 `Unknow` 且 `Temperature=0`，視為該台未回應。

### 2.3 「待保養」狀態無資料來源

FCU 沒有累積運轉時數。只有冰水主機有 `AccumulatedRunningHours` / `AccumulatedStartCount`。
→ FCU 的「待保養」只能靠後台手動排程（保養週期表），不能自動推算。

### 2.4 全系統唯讀，沒有控制能力

Collector 公開 API 只有 `Start` / `Stop` / 兩個事件。`FCUUnit.Update()` 明寫「僅改資料物件，不寫入硬體」。
→ **後台不能開關 FCU、不能改模式、不能設溫度。** 若設計稿有控制按鈕，需拿掉或改成「僅顯示」。這要跟東元確認是否為驗收範圍。

### 2.5 負載率不用自己算

`TecoGolfHanbellStatus.LoadPercentage` 直接給 %，`ChilledWaterTemperatureDifference` 也直接給 ΔT。
→ `teco-spec-review-findings` 第 4 項（負載率公式三選一）**可直接結案：用設備回報值**。
（但若要算 kW/RT 效率，仍缺「額定容量 RT」，需向東元索取。）

### 2.6 FCU 台數 vs 前台分區數對不上

| | Collector | 前台圖面分區 |
|---|---|---|
| B1F | 64 台 | 72 區 |
| B2F | 31 台 | 41 區 |

需要一份 **FCU ↔ 分區對照表**。建議做成後台可維護的資料表，不要寫死在前端。

### 2.7 FCU ID 跨 DDC 會重複

`FC_MC1_01` 在 DDC1 和 DDC2 都可能出現。
**唯一鍵必須是 `(Channel, StationID, Position)`**，不能用 `ID` 當主鍵。

### 2.8 單點風險

兩台漢鐘共用同一條 Gateway TCP（192.168.10.198:4196）。Gateway 掛掉 = 兩台主機同時失聯。告警規則要能區分「設備異常」與「通道異常」，不要在 Gateway 斷線時噴出 14 個假警報。

---

## 3. 系統架構

```
┌──────────────────────── Windows Server（宿主，不裝 Docker Desktop）────────────────────────┐
│                                                                                          │
│   Hyper-V Linux VM（Ubuntu 24.04 LTS）或 WSL2   ── Docker Engine CE (Apache-2.0)          │
│                                                                                          │
│   ┌────────────────┐   POST /internal/ingest   ┌────────────────┐                        │
│   │ teco-collector │ ────────────────────────► │    teco-api    │ ◄── SignalR ── 前台看板 │
│   │  (.NET 10      │                           │ (ASP.NET Core  │ ◄── REST ───── 後台管理 │
│   │   Worker)      │ ──┐                       │      10)       │                        │
│   │  ↓ 8 個 DLL    │   │                       └───────┬────────┘                        │
│   └───────┬────────┘   │                               │                                 │
│           │            └───────────┐                   │                                 │
│           │  批次寫入               │ 讀歷史/聚合        │                                 │
│           ▼                        ▼                   ▼                                 │
│   ┌──────────────────────────────────────────────────────────┐                           │
│   │                  mariadb 11.8 LTS (volume)               │                           │
│   └──────────────────────────────────────────────────────────┘                           │
│                                                                                          │
│   ┌────────────────┐                                                                     │
│   │  caddy / nginx │ ── 反向代理 + 服務 Astro 靜態 dist                                    │
│   └────────────────┘                                                                     │
└──────────────────────────────────────────────────────────────────────────────────────────┘
            │ 容器 → 宿主路由 → 192.168.10.0/24
            ▼
   Gateway 192.168.10.198:4196 ／ DDC1 .12:502 ／ DDC2 .14:502
```

### 為什麼 Collector 拆成獨立服務

1. **隔離風險**：DLL 無原始碼，萬一有記憶體洩漏或未捕捉例外，不要拖垮 API。
2. **保留退路**：若 P0 驗證發現 DLL 真的必須跑 Windows，只要把這一個容器換成 Windows 服務／Windows 容器，其餘不動。
3. **單一寫入者**：時序資料只有它寫，避免併發衝突。

若後續確認一切穩定、也想省一個容器，可以把 `CollectorHostedService` 直接掛進 API — 介面（`IDeviceSnapshotSink`）刻意留了這個縫。

### Collector → API 的即時通道

用 **內網 HTTP POST**（compose network 內，帶 `X-Internal-Token`），API 維護記憶體內的 current state 並經 SignalR 廣播。
不引入 Redis / MQTT：單站、單一 collector、5 秒一次，多一個元件的維運成本不划算。
（備案：API 每 5 秒讀 DB 最新一筆。更鬆耦合，但延遲多一拍。）

---

## 4. 專案結構與 .NET 10 設定

```
Teco.Hvac/
├─ global.json                  ← 釘 SDK 版本
├─ Directory.Build.props        ← 統一 net10.0
├─ Teco.Hvac.slnx
├─ lib/                         ← 8 個交付 DLL（不含 System.IO.Ports，見下）
├─ src/
│  ├─ Teco.Hvac.Collector/      Worker：包 DLL、正規化、節流、寫 DB、POST API
│  ├─ Teco.Hvac.Api/            ASP.NET Core 10：REST + SignalR + 認證
│  ├─ Teco.Hvac.Domain/         實體、告警規則、值物件
│  ├─ Teco.Hvac.Infrastructure/ EF Core 10 + Pomelo(MariaDB)、Repository
│  └─ Teco.Hvac.Contracts/      DTO（Collector 與 Api 共用）
├─ tests/
└─ deploy/
   ├─ compose.yaml
   ├─ collector.Dockerfile
   ├─ api.Dockerfile
   └─ mariadb/init/
```

### 4.1 `global.json` — 你問的「讓它知道要用 10」

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

`rollForward: latestFeature` 表示：允許 10.0.1xx 的後續修補，但**不會**偷偷跳到 11。
放在方案根目錄，`dotnet build` 從任何子目錄執行都會套用。

### 4.2 `Directory.Build.props`

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <!-- 繁中格式化與時區必須，勿設為 true -->
    <InvariantGlobalization>false</InvariantGlobalization>
    <!-- 執行期只允許在 10.x 內前滾 -->
    <RollForward>latestMinor</RollForward>
  </PropertyGroup>
</Project>
```

> `InvariantGlobalization=false` 代表映像檔必須有 ICU。用 Debian-based 的
> `mcr.microsoft.com/dotnet/aspnet:10.0` 沒問題；若改用 Alpine，要自己 `apk add icu-libs`。

### 4.3 引用 net8.0 DLL 的關鍵眉角

net10.0 專案引用 net8.0 / netstandard2.0 組件是**官方支援**的（同一 .NET 家族向前相容），不需要 shim。但有兩個陷阱：

```xml
<ItemGroup>
  <!-- 排除 System.IO.Ports，改走 NuGet -->
  <Reference Include="..\..\lib\*.dll" Exclude="..\..\lib\System.IO.Ports.dll">
    <Private>true</Private>
  </Reference>

  <!-- 讓 NuGet 依 RID 挑正確的 linux-x64 / win-x64 實作 -->
  <PackageReference Include="System.IO.Ports" Version="10.0.*" />
</ItemGroup>
```

**為什麼**：說明書交付的是 `runtimes/win/lib/net8.0/System.IO.Ports.dll`（Windows 專用平面檔）。
在 Linux 容器裡用它，一旦有任何程式碼路徑碰到就會炸。改用 PackageReference 之後，
NuGet 會依 `RuntimeIdentifier` 自動挑 `linux-x64` 的實作，Windows 部署時也照樣正確。
這一步同時把「DLL 能不能跑 Linux」的風險從「未知」降到「只剩 Gateway/TCP 這條路徑」。

若同時保留平面 DLL 與 PackageReference，會出現組件重複與版本衝突（MSB3277），所以 `Exclude` 不能省。

### 4.4 部署輸出

說明書 §7.2 特別強調：要用**自己應用程式**產生的 `.deps.json` 與 `.runtimeconfig.json`，
不要拿 Collector 或供應端測試程式的 JSON 覆蓋。多階段 Dockerfile 用 `dotnet publish` 天然符合。

---

## 5. Collector 服務設計

### 5.1 生命週期

```csharp
public sealed class CollectorHostedService : BackgroundService
{
    // 1. 註冊事件（必須在 Start 之前 → 才收得到首次 ConnectFailed）
    // 2. 用「整個 Property 指派」設定三條路徑（getter 回傳副本，改副本無效）
    // 3. Start()（非同步啟動，立即返回）
    // 4. 事件處理器只做：轉 DTO → 丟進 Channel<Snapshot> → 立刻返回
    // 5. 停止：Stop() → 解除事件 → Dispose()
}
```

**事件處理器內絕不做 I/O。** 說明書 §7.1 明示事件跑在背景工作執行緒上，
處理太久會延遲後續輪詢。所以一律 `Channel.Writer.TryWrite(snapshot)` 後立即返回，
由獨立的 consumer task 負責寫 DB 與 POST。

### 5.2 看門狗

因為 DLL 無原始碼、也沒有「整體健康」API，自己加一層監督：

- 記錄每個通道最後一次 `IsReadSuccess=true` 的 UTC 時間。
- 超過 60 秒（= 12 個週期）沒有任何成功 → 寫 `channel_health` + 發告警。
- 超過 5 分鐘完全沒收到 `EventDataReceived` → `Stop()` + `Dispose()` + 重建 Collector。
- 這些指標同時透過 `/healthz` 曝露，讓 Docker healthcheck 能重啟容器。

### 5.3 時間處理

`UpdateTime` / `TriggerTime` 都是 `DateTime.Now`（本機時間、`Kind=Local`、**不含時區**）。
→ ingest 當下就轉成 UTC 存（`DATETIME(3)`），顯示層再轉 `Asia/Taipei`。
→ 容器要設 `TZ=Asia/Taipei`，否則 `DateTime.Now` 在 UTC 容器裡會差 8 小時。
→ 同時記錄 `received_at`（後端收到時間），因為 `UpdateTime` 不是設備量測時間。

### 5.4 寫入節流（關鍵，決定資料庫存活）

若每 5 秒原樣寫入：95 × 17,280 = **164 萬筆/天**，一年 6 億筆 → MariaDB 單機吃不消。

| 資料 | 寫入策略 | 筆數/天 | 筆數/年 |
|---|---|---|---|
| FCU | 每 **60 秒**寫一筆；另外 `SwitchStatus`/`Mode`/`FanSpeed` 一有變化或溫度變動 > 0.5°C 就立即補寫 | ≈ 137,000 | ≈ 5,000 萬 |
| 冰水主機 | 每 **10 秒**一筆（欄位多但只有 2 台） | 17,280 | ≈ 630 萬 |
| 告警 | 狀態機，只記「進入 / 離開」兩筆 | 極少 | 極少 |
| 通道健康 | 只記狀態變化 | 極少 | 極少 |

寫法：單次 multi-row `INSERT`（95 筆一起），不要 95 次來回。EF Core 用 `ExecuteSqlRaw`／`MySqlBulkCopy` 皆可。

---

## 6. MariaDB 設計

### 6.1 版本與設定

- **MariaDB 11.8 LTS**（或 11.4 LTS），InnoDB，`utf8mb4` + `utf8mb4_uca1400_ai_ci`。
- 關鍵參數：`innodb_buffer_pool_size` 給宿主記憶體 50–60%、`innodb_flush_log_at_trx_commit=2`（時序資料容忍 1 秒損失，換取吞吐）。
- 驅動：`Pomelo.EntityFrameworkCore.MySql`，**版本需對齊 EF Core 10**。
  → 若 Pomelo 尚未釋出對應 EF Core 10 的版本，退路是 `MySqlConnector` + Dapper（時序查詢本來就適合手寫 SQL），只在後台 CRUD 用 EF。這點要在 P0 一併確認。

> 呼應 `teco-spec-review-findings` 第 2 項：**單庫即可**。
> 這是單站自用系統，不要照搬美達特的 Platform/Customer 雙庫與經銷處/租戶語彙。

### 6.2 資料表

**設備主檔**

```sql
device_chiller(id, code, modbus_id, display_name, rated_capacity_rt, is_active)
device_fcu(id, channel, station_id, position, collector_id, address,
           floor, zone_code, display_name, is_active,
           UNIQUE KEY uk_addr (channel, station_id, position))
```
`zone_code` 對應前台的 `B1-Z07` / `B2-E03`，由後台維護（見 §2.6）。

**時序（按月 RANGE 分割，用 `DROP PARTITION` 做零成本清除）**

```sql
chiller_reading(device_id, ts, received_at,
  cooling_water_in, cooling_water_out,
  chilled_water_in, chilled_water_out, chilled_water_delta,
  input_current, input_voltage, high_pressure, low_pressure,
  actual_rpm, running_hours, start_count,
  input_power_kw, approach_temp, accumulated_kwh,
  load_percentage, water_control,
  alarm_bits INT UNSIGNED,   -- 14 個 bool 壓成 bitmask
  read_status TINYINT,
  PRIMARY KEY (device_id, ts))
PARTITION BY RANGE (TO_DAYS(ts)) (...)

fcu_reading(device_id, ts, switch_status, mode, fan_speed, temperature,
            read_status, PRIMARY KEY (device_id, ts))
PARTITION BY RANGE (TO_DAYS(ts)) (...)
```

**告警與健康**

```sql
alarm_rule(id, device_type, scope, metric, operator, threshold,
           severity, debounce_seconds, is_enabled)
alarm_event(id, device_type, device_id, rule_code, severity,
            started_at, ended_at, peak_value, ack_by, ack_at, memo)
channel_health(id, channel, connection_state, read_status,
               changed_at, duration_seconds)
```

**聚合（排程每小時算前一小時）**

```sql
rollup_chiller_1h(device_id, bucket, avg_*, min_*, max_*, kwh_delta, run_minutes)
rollup_fcu_1h(device_id, bucket, avg_temp, min_temp, max_temp, on_minutes)
```

**後台（補 spec §陸 ERD 缺的權限模型）**

```sql
app_user / app_role / app_permission / app_role_permission / app_user_role
operation_log(id, user_id, action, target_type, target_id,
              before_json, after_json, ip, created_at)
```
`before_json` / `after_json` 正是 `teco-spec-review-findings` 第 9 項要求的「異動前/後值」。

### 6.3 保留策略

| 表 | raw 保留 | 之後 |
|---|---|---|
| `fcu_reading` | 90 天 | 只留 `rollup_fcu_1h`（永久） |
| `chiller_reading` | 180 天 | 只留 `rollup_chiller_1h`（永久） |
| `alarm_event` / `operation_log` | 永久 | — |

用 MariaDB Event Scheduler 或 collector 的每日排程執行 `ALTER TABLE ... DROP PARTITION`。

---

## 7. API 設計（ASP.NET Core 10）

```
GET  /api/v1/realtime/snapshot           目前四組狀態 + 每組 dataQuality
WS   /hubs/telemetry                     SignalR 推播（5 秒一次）
GET  /api/v1/chillers                    主機清單 + 現況
GET  /api/v1/chillers/{code}/history     ?from&to&interval=raw|1m|1h
GET  /api/v1/fcus                        ?floor=B1&status=
GET  /api/v1/fcus/{id}/history
GET  /api/v1/floors/{floor}/heatmap      分區 → 溫度（供前台熱力圖）
GET  /api/v1/alarms                      ?status=active|history
POST /api/v1/alarms/{id}/ack
GET  /api/v1/reports/energy              ?from&to&groupBy=day|month
GET  /api/v1/reports/runtime
CRUD /api/v1/admin/{users,roles,thresholds,fcu-mapping}
GET  /healthz  /readyz  /metrics
POST /internal/ingest                    僅限 compose 內網 + 共享 token
```

**每個回應都要帶資料品質欄位**，否則前端無從分辨「0°C」是真值還是初始值：

```json
{
  "updateTime": "2026-09-14T10:30:05.123+08:00",
  "receivedAt": "2026-09-14T10:30:05.210+08:00",
  "dataQuality": {
    "channel": "DDC1",
    "isConnected": true,
    "readStatus": "Success",
    "lastSuccessAt": "2026-09-14T10:30:05.123+08:00",
    "staleSeconds": 0
  },
  "value": { }
}
```

認證沿用美達特慣例：JWT access + refresh token、角色/權限授權原則（policy-based）。

---

## 8. Windows 上跑 Docker，不裝 Docker Desktop

### 8.1 授權先釐清

| 產品 | 授權 | 適用 |
|---|---|---|
| **Docker Desktop** | 大型企業需付費訂閱 | ❌ 避開 |
| **Docker Engine (CE / moby)** | Apache-2.0，**商用免費** | ✅ |
| **Podman** | Apache-2.0，免費 | ✅ 備選 |
| Mirantis Container Runtime | 付費 | ❌ |

要避開的是 **Desktop 那個 GUI 產品**，不是 Docker 本身。Engine 在 Linux 上一直是免費的。

### 8.2 三種宿主方案

**方案 A — Hyper-V Linux VM（推薦給正式機）**

```
Windows Server + Hyper-V
  └─ Ubuntu Server 24.04 LTS VM
       └─ apt install docker-ce docker-compose-plugin
```

- 網路用 **External Virtual Switch** 綁在 192.168.10.0/24 那張網卡，VM 直接拿該網段的靜態 IP（例如 192.168.10.200）。容器出網走 bridge NAT → VM → 設備，路徑單純可預測。
- 與最終正式環境（Linux）完全一致，沒有 WSL 的邊角問題。
- 開機自啟、快照備份、資源限制都是 Hyper-V 原生能力。
- 代價：多一層 VM 的記憶體開銷（配 8–16 GB）。

**方案 B — WSL2 + Docker Engine CE（開發機／輕量部署）**

```powershell
wsl --install -d Ubuntu-24.04
```
```bash
# WSL 內，這是 Docker Engine，不是 Docker Desktop
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER
```
```ini
# /etc/wsl.conf — 讓 systemd 管 docker，開機自動起
[boot]
systemd=true
```
```powershell
# Windows 11 22H2+ / Server 2025：鏡像網路，容器可直接看到宿主的兩個網段
# %USERPROFILE%\.wslconfig
[wsl2]
networkingMode=mirrored
```

- 免費、輕量、啟動快。
- **風險**：預設 NAT 模式下，容器 → 192.168.10.0/24 的路由需實測。`mirrored` 模式可解，但要確認 Windows 版本支援。
- 需要一個 Windows 排程工作在開機時 `wsl -d Ubuntu-24.04 -u root /usr/bin/true`（觸發 WSL 啟動，systemd 接手 docker）。

**方案 C — Podman + Quadlet**

無 daemon、rootless、`podman kube play` 可讀 compose 轉出的 YAML。若公司政策連 Docker 這個名字都要避開，這是乾淨的替代。

**不建議：Windows 容器。** MariaDB 官方沒有 Windows 映像，資料庫無論如何得跑 Linux 容器；混合兩種容器類型徒增複雜度。只有在 P0 驗證發現 DLL 真的無法在 Linux 執行時，才把 **collector 單獨**改成 Windows 服務（直接跑在宿主，不容器化），其餘維持不變。

### 8.3 compose 骨架

```yaml
services:
  mariadb:
    image: mariadb:11.8
    restart: unless-stopped
    environment:
      MARIADB_ROOT_PASSWORD_FILE: /run/secrets/db_root
      MARIADB_DATABASE: teco_hvac
      TZ: Asia/Taipei
    volumes:
      - mariadb-data:/var/lib/mysql
      - ./mariadb/init:/docker-entrypoint-initdb.d:ro
      - ./mariadb/conf.d:/etc/mysql/conf.d:ro
    healthcheck:
      test: ["CMD", "healthcheck.sh", "--connect", "--innodb_initialized"]
      interval: 10s
      timeout: 5s
      retries: 12

  collector:
    build: { context: .., dockerfile: deploy/collector.Dockerfile }
    restart: unless-stopped
    environment:
      TZ: Asia/Taipei
      DOTNET_ENVIRONMENT: Production
      Collector__Hanbell__Ip: 192.168.10.198
      Collector__Hanbell__Port: "4196"
      Collector__Hanbell__BaudRate: "9600"
      Collector__Ddc1__Ip: 192.168.10.12
      Collector__Ddc2__Ip: 192.168.10.14
    depends_on:
      mariadb: { condition: service_healthy }
    healthcheck:
      test: ["CMD", "curl", "-fsS", "http://localhost:8080/healthz"]
      interval: 30s

  api:
    build: { context: .., dockerfile: deploy/api.Dockerfile }
    restart: unless-stopped
    environment:
      TZ: Asia/Taipei
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_HTTP_PORTS: "8080"
    depends_on:
      mariadb: { condition: service_healthy }

  web:
    image: caddy:2
    restart: unless-stopped
    ports: ["80:80", "443:443"]
    volumes:
      - ../frontend/dist:/srv:ro
      - ./Caddyfile:/etc/caddy/Caddyfile:ro

volumes:
  mariadb-data:
```

### 8.4 Dockerfile（collector 為例）

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY lib/ ./lib/
COPY src/ ./src/
RUN dotnet publish src/Teco.Hvac.Collector/Teco.Hvac.Collector.csproj \
    -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV TZ=Asia/Taipei
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "Teco.Hvac.Collector.dll"]
```

`lib/` 必須在 build context 內（所以 `context: ..`）。`aspnet:10.0` 已內建非 root 的 `app` 使用者，預設埠 8080。

### 8.5 備份

Windows 端排程每日：
```powershell
docker exec teco-mariadb mariadb-dump -u root -p$env:DBPW --single-transaction --routines teco_hvac | 
  Out-File -Encoding utf8 "D:\backup\teco_$(Get-Date -f yyyyMMdd).sql"
```
加上 volume 快照（Hyper-V 方案可直接用 VM checkpoint）。

---

## 9. 執行階段

### P0 — 技術驗證（1–2 天，**整份規劃的前提，先做**）

1. 建一個 net10.0 Console，引用 8 個 DLL（`System.IO.Ports` 改走 NuGet），確認**編譯與載入**都過。
2. 放進 `aspnet:10.0` Linux 容器，跑起來，確認沒有 `PlatformNotSupportedException` 或 `DllNotFoundException`。
3. 在現場（或用 Modbus TCP 模擬器）確認**容器能連到 192.168.10.0/24**，收到 `Connected` + `IsReadSuccess=true`。
4. 確認 Pomelo 對 EF Core 10 的版本狀態。

> 這四項任一不過，方案就要調整（多半是把 collector 拉出容器跑 Windows 服務），所以先驗證。

### P1 — Collector + 資料落地（3–5 天）
DTO 正規化、節流寫入、分割表、看門狗、`channel_health`。

### P2 — API + 即時（4–6 天）
REST + SignalR、資料品質欄位、認證授權、前台接真資料（取代 mock service）。

### P3 — 告警 + 報表（4–6 天）
規則引擎（含 debounce 與 Gateway 斷線抑制）、聚合排程、報表三頁。

### P4 — 權限、稽核、部署（3–4 天）
RBAC、`operation_log` 前後值、compose 正式化、備份、健康檢查、交付文件。

---

## 10. 需要向供應商 / 東元確認的事項

| # | 問題 | 影響 |
|---|---|---|
| 1 | FCU **設定溫度 (setpoint)** 能否從暫存器提供？ | 決定異常判定用 ΔT 還是絕對溫度（§2.1） |
| 2 | 是否有**寫入／控制** API（開關、模式、風速、設溫）？ | 後台是否保留控制功能（§2.4） |
| 3 | `HighPressure` / `LowPressure` 的**物理單位**？（說明書明寫未定義） | 前台顯示與告警門檻 |
| 4 | **FCU ↔ 樓層分區對照表**（64 台 vs 72 區、31 台 vs 41 區） | 熱力圖能否上線（§2.6） |
| 5 | 冰水主機**額定容量 RT** | 能否算 kW/RT 效率 |
| 6 | Collector 是否允許**多實例同時連線**同一 Gateway？ | 是否能做熱備援 |
| 7 | Gateway 單點（兩台主機共用一條 TCP）是否可接受？ | 告警設計與可用性承諾 |
| 8 | FCU「待保養」的判定依據（無運轉時數資料） | 是否改為後台手動保養排程（§2.3） |

---

## 11. 與既有文件的關係

- 本文推翻／補充 `docs/TECO_HVAC_SPECIFICATION.md` 的：§參.二（FCU 異常判定）、負載率公式、雙資料庫設計、缺少的通訊協定章節。
- 前端 `src/` 目前的 mock service 介面，建議在 P2 直接對齊本文 §7 的回應格式（特別是 `dataQuality`），避免二次改寫。
