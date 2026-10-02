# Teco.Hvac Backend

東元智慧環境監控的後端：接住 `Teco_Golf_DataCollector`（供應商 DLL）的即時資料、
落地到 MariaDB、透過 ASP.NET Core 10 API + SignalR 供前後台使用。

規劃全文見 [`../docs/BACKEND_INTEGRATION_PLAN.md`](../docs/BACKEND_INTEGRATION_PLAN.md)；
本檔案是「這個 repo 目前實際長什麼樣子、怎麼跑」的現況說明，**只保留現在的狀態**。
每一次功能新增、真的抓到的 bug、詳細的驗證過程，全部按日期記錄在
[`backend/CHANGELOG.md`](CHANGELOG.md)——追查「這個設計當初為什麼這樣做」時去那份文件找，
這裡只需要知道「現在是這樣」。

## 現況（持續更新，最後一次大幅修訂 2026-09-24）

P0–P4 的技術驗證與端對端資料流早已完整跑過（見下方「已驗證」）；P5 告警引擎（含可設定門檻、
熱重載）、報表、資料保留排程、權限與稽核系統、refresh token、場館與平台角色 CRUD、平台管理
前端等後續功能也都已經完成並用真實 docker compose 環境驗證過——**全部後台頁面、前台戰情室、
平台管理頁面都已接真實 API**，沒有任何頁面還在打 mock service。完整開發時間軸見
[`CHANGELOG.md`](CHANGELOG.md)，不要只看這段摘要就以為進度停在早期階段。目前唯一還沒完成的是
**P6：部署腳本已寫好但尚未在真正的 Hyper-V VM 上跑過**（我這邊沒有那台 Windows 主機的存取
權限，這步驟需要你在現場執行 `deploy/host-setup/`），以及少數需要供應商/東元回覆才能繼續的
項目（見檔案最下方「需要向東元/供應商確認」）。

### 專案結構

```
backend/
├─ global.json                        SDK 10.0.100，rollForward: latestFeature
├─ Directory.Build.props              net10.0、Nullable、InvariantGlobalization=false
├─ Teco.Hvac.slnx
├─ lib/                                供應商交付的 8 個 DLL
├─ src/
│  ├─ Teco.Hvac.Contracts/            DTO：ChillerSnapshot / FcuSnapshot / DataQuality / IngestPayload...
│  ├─ Teco.Hvac.Domain/                Entities + AlarmRule/AlarmEvaluator
│  ├─ Teco.Hvac.Infrastructure/        Dapper + MySqlConnector repositories（見下方 P0-4 決策）
│  ├─ Teco.Hvac.Collector/             包 DLL 的 Worker：事件→即時推播→節流落地→告警評估→看門狗
│  └─ Teco.Hvac.Api/                   REST + SignalR + JWT
├─ tools/probe/                        P0 技術驗證用的獨立探針（見下方）
└─ deploy/
   ├─ compose.yaml, *.Dockerfile, Caddyfile
   ├─ mariadb/init/                    001_schema.sql（分割表）、002_seed.sql（設備主檔+規則種子）
   └─ host-setup/                      Hyper-V VM + Docker Engine CE 安裝步驟（尚待現場執行）
```

## 已驗證（真的跑過，不是紙上推演）

**P0 技術驗證**（`tools/probe/`）：
- net10.0 專案引用全部 8 個供應商 DLL（net8.0 × 6、netstandard2.0 × 2）→ 編譯成功
- `System.IO.Ports` 排除平面 DLL、改走 NuGet PackageReference → 在 **linux-arm64**（原生）與
  **linux-x64**（QEMU 模擬）容器內都能建構 `CS_TecoGolf_DataCollector`、註冊事件、Start/Stop，
  未出現 `PlatformNotSupportedException`／`DllNotFoundException`
- Pomelo.EntityFrameworkCore.MySql 9.0.0 把 EF Core Relational 鎖在 `[9.0.0, 9.0.999]`，
  **不支援 EF Core 10** → 決策：全面改用 `MySqlConnector` + `Dapper`，不用 EF Core

**P1–P4 端對端**（`docker compose up -d --build`，於本機 Docker 跑過完整流程）：
- MariaDB 11.8 初始化：`000_create_app_user.sh`（建 `teco_app` 帳號）→ `001_schema.sql`
  （15 張表，含分割表）→ `002_seed.sql`（2 台冰水主機、95 台 FCU＝64+31、3 角色、4 權限、2 條告警規則）
  → **實測中抓到並修正**：MariaDB 的 `PARTITION ... MAXVALUE` 語法必須寫成
  `VALUES LESS THAN (MAXVALUE)`，單獨 `MAXVALUE` 會報 `ERROR 1479`
- Collector 啟動、載入設備對照表（95 台 FCU 全部對到 `device_fcu`，無「找不到對照」警告）、
  對現場 IP（`192.168.10.x`，本機當然連不到）跑出正確的 `Connecting → Disconnected` 生命週期，
  沒有因為連不到就崩潰
- Collector → `POST /internal/ingest/data` → Api 202 → `CurrentStateStore` 更新 →
  `GET /api/v1/realtime/snapshot` 正確回傳含 95 台 FCU 的完整快照與逐通道 `dataQuality`
- 節流落地生效：`fcu_reading` 有 95 筆（每台至少一筆）、`chiller_reading` 持續累積、
  `channel_health` 正確記錄 `ConnectFailed→Connecting` 的狀態轉換
- 告警抑制生效：`ReadStatus=Disconnected` 時 `alarm_event` 維持 0 筆（沒有因為斷線就誤報）
- JWT 登入（PBKDF2 驗證）→ `/api/v1/alarms` 無 token 回 401、帶 token 回 200
- `/api/v1/chillers`、`/api/v1/fcus?floor=B1`（正確回傳 64 筆）都能正確合併 DB 主檔與即時快照
- Caddy 反向代理：靜態前端（Astro `dist/`）與 `/api/*`、`/hubs/*` 轉發都正常

**平台「系統診斷」**（2026-09-29，`/platform/diagnostics`，細節見 `CHANGELOG.md` 同名章節）：
- `GET /api/v1/platform/diagnostics`：通道狀態、Collector `/healthz`、數據內容檢查、FCU 台數比對、
  資料庫落地統計、排程執行狀態、`channel_health` 歷史；`GET /api/v1/platform/diagnostics/raw`：原始快照。
  需要 platform scope＋`platform.diagnostics:read`（`012_platform_diagnostics_permission.sql`）
- 已用瀏覽器實測三種情境：暫填 IP 連不到（誠實顯示未連線）、`simulate-live-data.sh`
  （數據內容全綠）、`simulate-live-data.sh --with-anomalies`（7 種異常全部正確亮燈）
- 現場接通流程見 [`docs/IOT_現場接通驗證手冊.md`](../docs/IOT_現場接通驗證手冊.md)

**資料對照缺口修正**（2026-09-29，細節見 `CHANGELOG.md` 同名章節與 `docs/IOT_資料對照與缺口清單.md`）：
- 供應商 DLL 已用反射核對，欄位與說明書一致，所有欄位都有各自存進資料庫
- 冰水主機超過後台門檻時狀態判為「異常」；冰水主機報表有負載率欄；FCU 報表的模式／風速有值
- 每小時彙總補齊冰水主機電氣、壓力、冷卻水、警報與 FCU 模式／風速（migration `013`，既有資料庫要手動套用）
- Collector 讀取不成功時不再寫入時序表（斷線紀錄只在 `channel_health`）
- 重讀供應商說明書全文逐欄對照：說明書的欄位全部都有接進來。`GET /api/v1/fcus` 新增 `vendorCode`（供應商 FCU 編號，
  例如 `FC_MC1_01`，DDC 之間會重複）與 `address`（暫存器文件位址），FCU 管理頁與空間設備配置顯示成「DDC1 · FC_MC1_01」

**冰水主機保養提醒**（2026-10-01，「機車換機油」模式，判定規則見 `docs/BACKEND_INTEGRATION_PLAN.md` §2.3，細節見 `CHANGELOG.md` 同名章節）：
- migration `014_chiller_maintenance.sql`：`device_chiller.maintenance_baseline_hours/_at`＋`chiller_maintenance_log`（既有資料庫要手動套用）
- `GET /api/v1/chillers/{id}/maintenance`（`hvac.chillers:read`）、`POST /api/v1/chillers/{id}/maintenance/reset`（`hvac.thresholds:update`）
- 已實測：起算、未達不通知、達標只開一筆、拖到兩倍間隔仍只有一筆、無權限 403、重置後熄燈並重新起算、再達標會再通知；
  後台冰水主機頁用瀏覽器實際按過「保養完成」
- Collector 端是用臨時程式直接呼叫 `AlarmEngine` 驗證的（本機沒有現場設備，Collector 不會有讀取成功的資料），
  **尚未在現場真實資料上跑過**

**尚未驗證**（需要現場環境或使用者操作）：
- P0-3：容器實際連到 `192.168.10.198/12/14` 三個現場 IP（我這裡沒有那個網路）
- P6：在真正的 Hyper-V VM 上執行 `deploy/host-setup/`
- SignalR 前端訂閱（後端廣播邏輯已驗證會被觸發，但沒有瀏覽器端訂閱測試）
- 平台管理前端（場館管理／系統帳號／角色管理）目前只用 curl 驗證過 API 行為，沒有實際
  用瀏覽器點過畫面（詳見 `CHANGELOG.md`「平台管理前端＋雙軌身分切換」一節）

## 權限機制

使用者要求「平台管理員」這層、以及「決定某商家是否啟用 CRUD／子項功能」的開關。
已實作完整的權限模型：

- **雙軌帳號**：`AppUser` 一張表同時涵蓋平台帳號（`SystemRoleId` 非空或 `IsPlatformAdmin=1`）
  與場館帳號（透過 `MerchantMembership` 掛到一或多個 `Merchant`）。JWT 只會是 `platform` 或
  `merchant` scope 其中一種，不會混；一個人若同時具備兩種身分，登入後可以在兩者之間切換
  （`GET /auth/scopes`／`POST /auth/scopes/select`，前端已接，見 `CHANGELOG.md`
  「平台管理前端＋雙軌身分切換」一節）。
- **場館 = TECO 的多租戶單位**：目前假設是「同集團其他場館」（單庫 + `merchant_id` 範圍隔離），
  **不是**「一商家一資料庫」的實體隔離＋provisioning 架構。這是與使用者確認過的決策
  （若未來真的需要跨公司資料實體隔離，才需要再加那層）。
- **Role/Permission/RolePermission 三層 CRUD＋子功能**：權限代碼（如 `hvac.chillers`）+
  `PerCreate/PerRead/PerUpdate/PerDelete` + `OptionsJson`（子功能，如 `hvac.alarms` 的 `ack`）。
- **CRUD／子項簡化開關**：`Merchant.IsRoleCrudConfigurationEnabled` /
  `IsRoleOptionConfigurationEnabled`，只有平台管理員能改（`PATCH /api/v1/platform/merchants/{id}`，
  平台管理頁面已有對應 UI）。資料庫永遠存細項真相；開關只影響「發 JWT 那一刻怎麼展開」
  （`Teco.Hvac.Domain.Permissions.MerchantRolePermissionConfigurationRules.Apply`）：
  - 關閉（`false`，種子資料的預設值，對應 TECO 現在這個客戶要的「一開功能就有完整 CRUD」）：
    只要角色被授予某資源（哪怕只勾 `read`），JWT 就自動展開成完整 CRUD ＋ 該資源全部子功能。
  - 開啟（`true`，給未來想精細控制的場館）：JWT 只給資料庫裡實際勾選的內容。
- **`IsFullAccess` 角色（`platform-admin`／`merchant-admin`）即時動態計算**：不依賴
  Seeder／migration 手動維護，一律等於目前權限目錄裡同 scope 的全部權限＋全部子功能，新增
  權限資源後這兩個角色自動涵蓋，不用再補資料（見 `PermissionGrantService.ForRolesAsync`）。
- **上限防線**：場館自訂角色算出的 grant，最終都會跟 `merchant-admin`（全域範本角色）的
  實際授予做交集（`MerchantRolePermissionCeilingRules.RestrictGrants`），即使資料或程式碼
  有 bug 也不會讓人越權。
- **場館擁有者與管理員層級保護**（2026-10-02）：`merchant_membership.is_owner` 標出場館的第一位管理員（擁有者），
  由 `MerchantMembershipGuard` 在刪除、停用／啟用、改角色、重設密碼、改名時檢查：擁有者不能被刪除、停用、改角色，
  別人也不能幫他重設密碼或改名；其他場館管理員只有擁有者能刪除、停用、改角色、重設密碼、改名；本人改自己的名字與密碼不受限。
  違規回 403＋中文原因。平台管理員走 `/api/v1/platform`，仍可重設擁有者的密碼。擁有者換人目前沒有畫面，要直接改資料庫。
- **JWT 失效機制**：`AppUser.AuthVersion` 在密碼重設、角色/權限變更、場館開關變更時遞增；
  `Program.cs` 的 `OnTokenValidated` 每次請求都比對 DB，版本不符立即拒絕。

**端點**：`/api/v1/auth/{login,scopes,scopes/select,refresh,refresh-token,me,change-password}`、
`/api/v1/platform/{merchants,system-users,roles,permissions}`（平台管理員專用，前端見
`src/apps/monitoring/pages/platform/`）、
`/api/v1/merchant/{users,roles,permissions,operation-logs}`（場館自己的日常管理，受
merchant-admin 上限約束）。

**已知缺口**（誠實列出）：
- 沒有做 `scope_kind` 多重身分「模擬進入」（impersonation）功能——TECO 目前不需要平台
  管理員假扮場館帳號操作。
- `device_chiller`/`device_fcu` 還沒加 `merchant_id`——現在的 Collector 硬編碼連單一場館的三個 IP，
  多場館的設備資料隔離要等實際有第二個場館時再做（那需要 Collector 也跟著支援多實例或多站連線）。
- 平台角色（`platform-admin`/`platform-operator`）沒有改名/刪除；系統帳號沒有停用/刪除；
  場館沒有停用/刪除（`Merchant.Status` 欄位存在但沒有任何地方會把它改成 `suspended`）。
- `SetRolePermissions` 對系統範本角色（`is_system=1`）的檢查是 `AppUser.IsPlatformAdmin`
  這個獨立的超級旗標，不是「有 `system_role_id` 指到 platform-admin 角色」就可以——目前只有
  `platform_admin`（測試/開發用帳號）這個帳號的 `is_platform_admin=1`（2026-09-24 手動於
  資料庫開啟），其餘帳號都沒有，一般平台角色若要用 API 編輯 `platform-admin`/
  `platform-operator` 這兩個系統範本角色自己的權限，一樣會被擋下。需要幫其他帳號開通時
  直接改資料庫的 `is_platform_admin` 欄位（沒有對應的 API/UI，因為這是刻意的高權限開關，
  不開放自助操作）。詳見 `CHANGELOG.md`「平台管理前端＋雙軌身分切換」一節。

## 本機開發

```bash
cd backend
dotnet build Teco.Hvac.slnx   # 建置全部 5 個專案
```

跑完整堆疊（含 MariaDB）：

```bash
cd backend/deploy
cp .env.example .env   # 填入密碼／token（本機測試可以隨便填，正式環境不可）
docker compose up -d --build
docker compose logs -f collector   # 看 Collector 的連線／資料事件
docker compose down          # 停止；加 -v 會連 MariaDB 資料一起刪
```

## P0-4 決策：為什麼不用 EF Core

`Pomelo.EntityFrameworkCore.MySql` 目前最新穩定版 9.0.0 的 nuspec 把
`Microsoft.EntityFrameworkCore.Relational` 鎖在 `[9.0.0, 9.0.999]`，跟 ASP.NET Core 10 /
EF Core 10 不相容。與其混用 EFCore9 + net10 app（會有一堆隱性版本地雷），全面改用
`MySqlConnector`（官方 ADO.NET driver）+ `Dapper`。時序資料表本來就有分割區、`ON DUPLICATE KEY`
這類 EF Core 不太會生成的 SQL，手寫反而更直接。日後 Pomelo 補上 EF Core 10 支援，
要不要換回來是可以重新評估的選項，不是不可逆的決定。

## 尚未實作 / 已知缺口（誠實列出，不要假裝做完了）

- **FCU 對照表管理**：不需要另外開發——「空間設備配置」頁面（`/admin/floor-plan`）儲存時
  就會透過 `FloorPlanRepository.SyncFcuZoneCodesAsync` 同步寫入 `device_fcu.zone_code`，
  這就是 `docs/BACKEND_INTEGRATION_PLAN.md` §2.6 講的「FCU ↔ 分區對照表」，只是用視覺化
  拖拉介面取代表格 CRUD。剩下的只是「把 64/31 台真實 FCU 對應到 72/41 個分區」這個資料填寫
  工作，要等供應商/現場人員提供分區對照資料才能做，不是功能缺口——**目前 93 台 FCU 的
  `zone_code` 全部是驗收用的模擬對照表，交機前必須換成真實對照**（見 `CHANGELOG.md`
  「FCU 對照表管理」一節）。
- **既有自訂角色不會自動取得新增的權限**：`hvac.alarms`/`hvac.thresholds`/`hvac.floor_plan`
  加進權限目錄後，只有系統範本角色（`merchant-admin`/`editor`/`viewer`）的授權被 migration
  更新，場館自己複製出來的自訂角色（例如測試用的 `member-1`）不會自動補上，需要場館管理員自己
  到角色管理頁勾選。
- **平台管理前端未經瀏覽器點擊互動驗證**：只用 curl 驗過 API 行為與 `npm run typecheck`/
  `npm run build`，見上方「尚未驗證」與 `CHANGELOG.md`「平台管理前端＋雙軌身分切換」一節。
- **「管理成員」面板新增成員只支援建全新帳號**，不支援選擇「已存在的其他帳號」（後端 API
  有支援帶 `userId`，但沒有「搜尋既有使用者」端點，UI 體驗會很差，先不做）。
- **三個報表頁的「匯出 Excel」尚未實作**：按鈕只跳出「已匯出」提示，沒有產生任何檔案
  （`docs/IOT_資料對照與缺口清單.md` 2.10）。
- **平台「系統診斷」頁的合理範圍是暫定值**：`Services/Diagnostics/DataValidationRules.cs` 裡的
  冰水主機／FCU 數值範圍是依一般運轉常識訂的，不是供應商規格，現場接通後要拿真實讀數校正
  （見 `docs/IOT_現場接通驗證手冊.md` 第 4 節）。排程執行紀錄只存在 API 記憶體，重啟即清空。
- **P6 host-setup 未在真機跑過**（首次部署流程見 `docs/正式機首次部署手冊.md`）：腳本與文件已寫好，但需要你在現場那台 Windows 主機上實際執行
  （Hyper-V 需要系統管理員權限與 Pro/Enterprise/Server 版本，我這邊無法代為操作）。

## 需要向東元 / 供應商確認（會影響上面缺口能否補完）

2026-10-02 起以現有資料為準，不再主動確認；FCU 設定溫度與水流量的畫面欄位已移除。
見 `../docs/BACKEND_INTEGRATION_PLAN.md` §10（FCU 設定溫度、控制 API、壓力單位、
分區對照表、額定容量 RT、待保養判定依據、現場 Windows 版本）。
