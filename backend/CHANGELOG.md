# Teco.Hvac Backend — 變更歷程

這份文件是 [`backend/README.md`](README.md) 的完整開發歷程，依日期排列（舊到新）。
`README.md` 只保留「現在系統長什麼樣子」的現況說明；每一次功能新增、真的抓到的 bug、
怎麼驗證的細節，全部記錄在這裡，方便日後回溯「為什麼會這樣設計」「這個 bug 當初怎麼修的」。

修改程式碼前請先看 `README.md` 掌握現況，需要追查某個決策的來龍去脈時再回來查這份檔案。

## 「編輯成員」面板造成管理員自我降級的事故（2026-09-15 修正）

**發生了什麼**：使用者在「使用者管理」頁對 `merchant_admin`（真實在用的管理員帳號，不是測試帳號）
自己按了「設定」→ 存檔。`SetUserFeatures` 一律把目標成員換成專屬角色（`member-{membershipId}`，
只有 `MemberFeatureCodes` 那六個固定資源），`merchant_admin` 因此從系統範本角色 `merchant-admin`
被換成只有六項 `read` 的專屬角色——而 `merchant.roles`（角色管理）根本不在那六個開關裡，於是
`merchant_admin` 永久失去了角色管理能力。畫面上的症狀：使用者管理頁頂端出現紅字「沒有場館帳號
管理權限」（其實是 `merchant.roles` 這支查詢 403，不是 `merchant.users`），角色欄位全部顯示
「未指派」（角色清單載入失敗，前端的角色名稱對照表是空的）。

**修正**：
1. **後端擋在源頭**：`SetUserFeatures` 在轉換前檢查目標成員目前的角色，若是系統範本
   `merchant-admin` 直接回 400，訊息說明要去角色管理調整（見 `MerchantEndpoints.cs`）。
2. **前端不讓使用者點到**：`merchant-admin` 角色的成員，「設定」連結整個換成不可點的灰字
   （不再只鎖住面板裡的兩個開關——那樣鎖不住 `merchant.roles` 這種根本不在開關清單上的權限）。
3. **拆開兩個查詢的錯誤訊息**：原本使用者清單／角色清單兩個查詢的錯誤被合併成同一個
   `loadError`，導致「清單明明有資料卻整頁顯示看不到清單」的矛盾畫面。現在分開：使用者清單
   失敗才擋整頁，角色清單失敗只用較不驚悚的提示，說明角色名稱與新增使用者的角色選單暫時不可用。
4. **已修復 `merchant_admin` 這個真實帳號**：把 `merchant_membership.id=2` 的 `role_id` 改回系統
   `merchant-admin` 角色、刪除多出來的專屬角色、遞增該帳號的 `AuthVersion`（強制下次請求要重新
   登入，讓權限重新從正確的角色算起）。**沒有動密碼**。

**已用真實 API 驗證**：直接呼叫 `PUT /merchant/users/2/features` 想改 `merchant_admin`，正確收到
400 與說明訊息；修正後用另一個 `merchant-admin` 角色帳號（`qa_admin`）登入使用者管理頁，紅字消失、
所有 `merchant-admin` 角色的列都正確顯示角色名稱、且「設定」正確變成不可點的灰字。

## 操作紀錄稽核（2026-09-15 新增）

`operation_log` 表原本只是個 schema stub（有欄位、沒有任何寫入路徑）。這次補上完整的寫入機制：

- **Schema**：`merchant_id`（場館範圍隔離，platform-scope 操作為 NULL）、
  `actor_username`/`actor_display_name`（寫入當下快照操作者身分，不靠 JOIN `app_user` 反查）、
  `summary`（寫入當下就組好的人話句子）、`is_success`/`error_message`（失敗的操作也記）。
  全新安裝從 `001_schema.sql` 直接建表；已在跑的資料庫要另外執行
  `deploy/mariadb/init/004_operation_log_audit_columns.sql`。
- **寫入入口統一**：`Teco.Hvac.Api.Services.OperationLogger`，所有 endpoint 都透過它寫，
  不各自兜寫法。已接上的端點：
  - 場館：新增/停用/角色指派使用者、重設密碼、編輯成員頁面權限、建立角色、調整角色權限
  - 平台：建立/更新商家、建立場館成員、建立系統帳號、建立角色、調整角色權限
  - 帳號：使用者自行修改密碼（含輸入錯誤密碼的失敗案例）
  - 告警：確認告警（`POST /alarms/{id}/ack`）
  - **不記** `Collector → Api` 的內部資料寫入（`/internal/ingest/*`）——那是機器對機器的資料流，
    不是「操作」，一天幾十萬筆會讓稽核表失去意義。
  - 密碼本身永遠不寫進 `before_json`/`after_json`。
- **保留策略**：永久保留、不分區（一天頂多幾十筆，跟 `fcu_reading` 那種每 5 秒一筆的量級完全不同）。
- **讀取端／前端整合**（2026-09-15 補上，原本只做了寫入端）：
  `GET /api/v1/merchant/operation-logs?from=&to=`（`MerchantEndpoints.ListOperationLogs`），
  需要 `merchant.operation_log` 的 `read` 權限；查詢區間最大 366 天（對齊畫面「最大範圍：一年」）。
  `action` 代碼 → 畫面「操作類別」分類標籤的對照表在 `ActionCategoryLabels`，找不到對照就直接
  顯示代碼本身，不會擋畫面。前端 [`AdminLogsPage.vue`](../src/apps/monitoring/pages/admin/logs/AdminLogsPage.vue)
  已改用 `useQuery` 打這支真實端點，`admin-mock-service.ts` 的假資料與寫死的操作類別列舉
  （含已經做不到的「開啟/關閉冰水主機」「修改FCU溫差」）已不再使用。
- **`created_at` 的時區坑（已修正）**：原本 `OperationLogRepository.CreateAsync` 沒有明確帶
  `created_at`，讓 MariaDB 的 `DEFAULT CURRENT_TIMESTAMP` 生效——但容器設了 `TZ=Asia/Taipei`，
  存進去的其實是台北當地時間，跟其他時序表（`chiller_reading`/`fcu_reading`，明確存 UTC）不一致，
  日期區間查詢會對不上。已修正為明確寫入 `DateTimeOffset.UtcNow`。**2026-09-15 之前寫入的 9 筆
  測試資料仍是台北時間**（沒有回溯修正，反正是測試資料），之後新寫入的都是正確的 UTC。
- **已用真實 API 驗證**（docker compose + 瀏覽器，2026-09-15）：寫入端——`merchant.user.update`
  （啟用/停用）、`merchant.user.features.update`（編輯成員頁面權限）、`merchant.user.reset_password`
  （含密碼明碼不外洩的檢查）、`auth.change_password` 失敗案例（`is_success=0`）都正確寫入，
  `before_json`/`after_json` 差異、操作者快照、IP 都正確記錄，見 `deploy/test-operation-log.sh`。
  讀取端——瀏覽器實際切換日期區間，確認查無資料／查有資料兩種情境的畫面都正確反映真實資料，
  不再是不管選什麼都回傳同一組假資料。`alarm.ack` 未測（目前系統沒有任何未確認告警可測），
  但呼叫模式跟已驗證的端點完全相同。
- **已知缺口**：平台層級操作紀錄目前沒有對應的列表頁（`ListForMerchantAsync` 只服務場館範圍；
  platform-scope 的紀錄——例如建立商家——目前只寫得進去，還沒有地方查）。

## 監控中心總覽頁接真實 API（2026-09-16）

後台「監控中心」總覽頁（`/admin`）原本整頁打 mock，這次換成真實資料——是目前 7 個還在吃 mock
的後台頁面裡第一個被接上的，其餘頁面仍待後續處理。

- **不需要新後端端點**：KPI（FCU/冰水主機運轉中/停止/異常/離線數量與運轉率）、冰水主機卡片、
  樓層運轉率全部由前端從既有的 `GET /chillers`、`GET /fcus`、`GET /alarms?status=active` 三支
  端點即時算出來，沒有新增後端聚合 API——這三支端點本來就已經把 `dataQuality` 跟即時值算好了。
- **`GET /api/v1/alarms` 這次補了 enrichment**（`AlarmEndpoints.cs`）：原本只回傳
  `deviceType`/`deviceId`/`ruleCode` 這些原始鍵值，畫面沒辦法直接顯示。現在會 JOIN
  `ChillerRepository`/`FcuRepository` 的清單，加上 `deviceName`/`deviceCode`/`location`，並把
  `ruleCode`（漢鐘是旗標名稱如 `IsHighPressureTooHigh`，FCU 是 `AlarmEngine` 組出來的
  `Temperature.0.28` 格式）翻成人話 `ruleLabel`（如「高壓過高」「室內溫度過高」）——這支端點現在
  同時服務監控中心即時告警表和未來的告警報表頁，加值運算統一放後端算，前端不用自己兜資料。
- **狀態判定邏輯**（純前端，見 `hvac-service.ts`/`AdminOverviewPage.vue`）：
  - 離線：`dataQuality.isConnected === false` 或 `readStatus !== Success`。
  - 冰水主機異常：直接讀 `value.isAlarm`（後端已經把 14 個硬體旗標 OR 好）。
  - FCU 異常：FcuSnapshot 沒有現成的 alarm 旗標，要對照目前有效告警清單裡 `deviceType=Fcu`
    的 `deviceId` 集合來判斷。
  - 運轉中：冰水主機看 `loadPercentage > 0`；FCU 看 `switchStatus === On`。
- **刻意拿掉/簡化的部分（誠實列出，不是漏做）**：
  - **「水流量」欄位整個拿掉**：Figma 設計稿有這個數字，但 `ChillerSnapshot` 完全沒有對應欄位——
    供應商 SDK 沒有這個量測值，設計稿上的數字是虛構的，比照 `operation_log` 那次「開關冰水主機」
    假操作類別的處理方式，寧可不顯示也不要編假數字。
  - **`isReturnExceeded`/`isTempDiffExceeded`/`isHoursExceeded` 這三個門檻警示旗標沒有做**：
    需要場館可設定的告警門檻（回水溫度上限、溫差上限、運轉保護時數），但告警門檻設定的 CRUD
    介面還沒做，見本檔案下方「告警門檻設定接真實 API」一節，該功能後續已經完成。
- **本機驗證**：本機 docker compose 環境連不到現場 `192.168.10.x` 網段，所有裝置的
  `dataQuality` 永遠是 `Disconnected`——用瀏覽器實測畫面在「全部離線、數值全部是 0」的情境下
  正確顯示離線徽章與空狀態，沒有假裝有資料；樓層卡片正確算出 B1=64 台／B2=31 台（對照規格書的
  DDC1/DDC2 配置），確認樓層分組邏輯正確。**沒有機會驗證「有真實運轉資料」的情境**，這需要現場
  網路環境才能測。

## 平面圖配置改由後端保存（2026-09-16）

「空間設備配置」頁（`/admin/floor-plan`）的擺放位置原本只存在**使用者瀏覽器的 localStorage**，
換一台電腦或清快取就整個消失，前台看到的也只是自己這台瀏覽器的配置。這次改成後端保存。

- **新資料表 `device_floor_placement`**：主鍵 `(device_type, device_id)` 天然保證一台設備只有
  一個位置；`device_type` 沿用 `alarm_event` 的 0=Chiller / 1=Fcu。刻意開新表而不是在
  `device_chiller`/`device_fcu` 加欄位——`device_chiller` 本來就沒有樓層概念，而且「擺在圖上哪裡」
  是編輯器狀態，跟設備主檔性質不同。已在跑的資料庫要執行
  `deploy/mariadb/init/006_device_floor_placement.sql`。
- **端點**：`GET/PUT /api/v1/floor-plan/{floor}/placements`（`FloorPlanEndpoints.cs`）。
  PUT 是**整層覆寫**（前端本來就是「改一改、按一次儲存」），並用 `expectedVersion`（該層最新的
  `updated_at`）做樂觀鎖：期間有別人存過檔就回 409，對應先前 localStorage 版本裡「另一個頁面
  已更新配置」那道保護。存檔會寫 `operation_log`（`hvac.floor_plan.update`）。
- **新權限碼 `hvac.floor_plan`**：merchant-admin 完整 CRUD、editor 可讀可改、viewer 唯讀。
  同時加進「編輯成員」的核取方塊（從 6 項變 7 項，與側邊欄 7 個項目 1:1 對應）——少了這一項，
  用個人權限（`member-{id}` 專屬角色）的使用者永遠拿不到這個權限。前後端的清單要一起改：
  `MerchantEndpoints.MemberFeatureCodes` 與 `user-service.ts` 的 `MEMBER_FEATURE_OPTIONS`。
- **順帶補實 `device_fcu.zone_code`**：存檔時把 FCU 的 `area_id` 同步回這個欄位（原本永遠是 NULL），
  之後報表與熱區圖才能用分區統計。設備被移出圖面時一併清成 NULL。
- **設備清單也一起接了真實 API**：`floor-plan.ts` 的 `getFloorEquipmentApi` 從 mock 換成
  `/chillers`＋`/fcus`＋`/alarms`，狀態判定邏輯（`deriveChillerStatus`/`deriveFcuStatus`）抽到
  `hvac-service.ts` 與監控中心總覽頁共用，避免兩邊各寫一套而慢慢分歧。
- **舊的瀏覽器配置無法搬移（誠實說明）**：原本規劃要做「匯入這台瀏覽器的舊配置」按鈕，實作時
  發現做不到——舊配置的 deviceId 是 mock 時代的 `b1-fcu-01` 這種樓層流水號，跟真實
  `device_fcu.id`（依 channel/station_id/position 編號）沒有可靠對應，硬搬會把真實設備放到當初
  替假設備擺的位置。所以沒有做這個按鈕，舊資料就讓它留在瀏覽器裡失效。
- **已用真實流程驗證**（docker compose + 瀏覽器，2026-09-16）：API 層驗過 GET/PUT、409 樂觀鎖、
  重複設備擋 400、zone_code 同步、operation_log 寫入；UI 層在畫面上實際配置一台設備並存檔，
  接著**清空整個 localStorage（連登入狀態一起清掉）再重新登入**，配置仍在——確認資料真的在
  伺服器而不是瀏覽器。測試資料事後已清除。

## 前台戰情室接真實 API（2026-09-16）

前台大屏（`/`，`DashboardApp.vue`）是目前唯一「後端完全做好、驗證過，前端卻完全沒接」的頁面，
這次接上，過程中發現並修正一個架構性問題。

- **關鍵發現：前台是不用登入的公開頁面**，`DashboardLayout.astro` 完全沒有登入檢查（設計上就是
  大廳/戰情室螢幕，給沒有帳號的人看）。但一開始直接重用後台的 `/api/v1/{chillers,fcus,alarms,
  floor-plan}` 端點——這些全部要求 JWT，於是變成「沒人登入，螢幕就一直被 401 導去登入頁」，
  整個前台直接壞掉。**已用瀏覽器實測抓到這個問題**（清空 localStorage 後訪問 `/` 幾秒內被彈到
  `/login`），跟使用者確認方向後修正為新增一組公開唯讀端點。
- **新端點 `/api/v1/public/{chillers,fcus,alarms,floor-plan/{floor}/placements}`**
  （`PublicEndpoints.cs`）：完全不掛 `RequireAuthorization()`，資料形狀跟認證版一模一樣——直接
  重用 `ChillerEndpoints.BuildListAsync`/`FcuEndpoints.BuildListAsync`/`AlarmEndpoints.BuildListAsync`/
  `FloorPlanEndpoints.BuildPlacementsResponseAsync`（把四支端點原本內嵌在 handler 裡的清單組裝
  邏輯抽成 internal static 方法），不重寫第二份、避免兩邊分歧。這是內部場館看板用途，
  沒有做 rate limiting；若之後真的要曝露在公開網路上需要另外評估。
- **前端對應新增 `publicJsonApi`**（`auth-service.ts`）：跟 `authorizedJsonApi` 分開實作，
  刻意不共用——`authorizedFetchApi` 遇到 401 會清工作階段並導去 `/login`，那是後台管理頁的行為，
  套到公開大螢幕上就是問題本身。`hvac-service.ts`/`floor-plan-service.ts` 各自多了
  `listPublicChillersApi`/`listPublicFcusApi`/`listPublicAlarmsApi`/`getPublicFloorPlacementsApi`。
- **`FloorPlanViewer.vue` 同時被後台監控中心（需要登入）跟前台戰情室（不需要登入）共用**：
  重用既有的 `embedded` prop（本來就只有前台會傳 true）當作切換依據，`embedded=true` 時改打
  公開端點，不用新增額外的 prop。
- **一併清掉兩批「設計稿假設了硬體給不了的資料」的假數字**（跟操作紀錄、監控中心總覽頁踩過的
  是同一類問題）：
  - `ChillerData.flowRate`（水流量）與四個 `isXxxExceeded` 門檻旗標裡的 `isFlowExceeded`：
    供應商 SDK 沒有水流量這個量測值，`isSupplyTempExceeded`/`isReturnTempExceeded`/
    `isTempDiffExceeded` 三個也因為場館還沒有告警門檻設定功能，一律回 false，不假裝有超標。
  - `FcuItem.setTemp`（FCU 設定溫度）：供應商 SDK 沒有這個欄位，畫面改用 `--` 顯示（比照
    室溫欄位既有的 NaN-safe 寫法），不編假數字。
  - `FloorHeatmapData.avgTemp`/`sensors`：翻過所有戰情室元件的樣板，這兩個欄位從未被實際渲染
    （熱區圖其實是用 `FloorPlanViewer` 畫的），直接從型別定義刪除。
- **尚未接**：每小時 FCU 啟用數長條圖與每小時室溫趨勢折線圖需要新的後端聚合查詢
  （依樓層 + 小時分組），`dashboard-mock-service.ts` 精簡到只剩這兩支還在用 mock，其餘全部刪除。
- **已用真實流程驗證**（docker compose + 瀏覽器，2026-09-16）：`curl` 確認 4 支公開端點未帶
  token 皆回 200、原本的認證端點仍然回 401（沒有意外放寬權限）；瀏覽器清空
  `localStorage`／`sessionStorage` 後直接訪問 `/`，畫面正常顯示真實資料（95 台 FCU、2 台冰水
  主機皆離線的真實現況），網路請求全部走 `/api/v1/public/*`、零筆 401，確認不會再被彈回登入頁。

## 前台每小時趨勢圖接真實 API（2026-09-17）

前台戰情室最後兩個還在打 mock 的地方——每小時 FCU 啟用數長條圖、每小時室溫趨勢折線圖——這次
補上後端聚合查詢，前台戰情室至此全部接完真實 API。

- **新端點 `GET /api/v1/fcus/hourly-stats?date=`**（`FcuEndpoints.cs`，公開版在
  `PublicEndpoints.cs`）：給定一個本地日期（預設容器當地今天，`TZ=Asia/Taipei`），回傳
  24 小時 × B1/B2 的「啟用中設備數」與「平均室溫」，兩張圖共用同一支查詢。
- **時區換算刻意不在 SQL 做**：MariaDB 這個容器的 `time_zone` 是 `SYSTEM`，沒有載入具名時區表，
  `CONVERT_TZ` 不可靠。改成 `FcuRepository.GetReadingsForHourlyStatsAsync` 撈出區間內的原始列
  （一天約 13~14 萬筆），由 C# 用 `TimeZoneInfo.Local` 做 UTC → 本地時間換算與小時分桶——
  資料量小，這樣做效能沒有問題，還能避開 DB 時區設定不可靠的坑。
- **完全沒有成功讀值的小時，溫度回 `null` 不是 `0`**：折線圖會畫成斷點，不會冒充「量到 0 度」，
  跟這個專案其他地方「沒有真資料就不假裝有」的原則一致（`dashboard-types.ts` 的
  `HourlyTempTrend.b1Temp`/`b2Temp` 型別也跟著從 `number[]` 改成 `(number | null)[]`）。
- **`dashboard-mock-service.ts` 整支刪除**：這是最後兩支還在用它的函式，接上真實 API 後這個檔案
  完全沒有消費者，直接刪掉，不留死檔案。
- **已用真實流程驗證**：本機環境的 `fcu_reading` 全部 12.6 萬筆歷史資料 100% 是
  `read_status=Disconnected`（現場網路本來就連不到），所以真實情境下這兩張圖是空的——這是
  誠實反映現況，不是 bug。為了驗證聚合邏輯本身正確，另外插入三筆人工測試資料（B1 兩台
  switch_status=On、B2 一台），直接呼叫 API 確認 JOIN／UTC→Taipei 小時換算／平均溫度計算
  全部正確（B1 count=2 avgTemp=24.8、B2 count=1 avgTemp=22.0），驗證完立即刪除測試資料，
  沒有留在資料庫裡。

## 告警門檻設定接真實 API（2026-09-17）

冰水主機管理、FCU管理兩個後台頁面原本是寫死的假清單，這次換成真實資料，並新增「告警門檻設定」
功能，讓場館可以自己調整異常判定的門檻，不用改程式碼。

- **`AlarmEngine`（Collector）擴充**：原本只有 FCU 走 `alarm_rule` 這張可設定規則表，冰水主機
  的異常判定完全依賴供應商 14 個硬體旗標。這次讓冰水主機也能疊加可設定門檻規則（出水/回水/
  溫度差上下限、累積運轉時數保養提醒），兩種裝置共用同一個新的 `EvaluateThresholdRulesAsync`
  （debounce／進出場邏輯一致），冰水主機原本的 14 個硬體旗標判定完全保留，不受影響。
- **`alarm_rule` 加唯一鍵** `(device_type, scope, metric, operator)`：讓門檻可以用
  `INSERT ... ON DUPLICATE KEY UPDATE` 寫，畫面「清空一格＝刪掉這條規則」對應
  `DeleteRuleAsync`，不是存一個停用的規則。
- **規則熱重載**：這是先前 README 列在「已知缺口」的項目，這次順手補上——Collector 原本就有
  15 秒一次的看門狗迴圈，改成每 4 輪（約 60 秒）額外重新載入一次規則，不用重建 Collector 就能
  套用新門檻。風險低，因為是接到既有迴圈，不是新增一個 Timer。
- **新端點 `/api/v1/thresholds/{chillers/{code},fcus}`**（`ThresholdEndpoints.cs`）：
  GET/PUT 都掛 `hvac.thresholds` 權限（跟 `hvac.chillers`/`hvac.fcus` 分開，能看即時數據不代表
  能改門檻），PUT 會寫操作紀錄。FCU 端點刻意叫 `room-temp` 不是 `temp-diff`：供應商 SDK 沒有
  FCU 設定溫度，做不出「溫差」，改用絕對室溫上下限（`docs/BACKEND_INTEGRATION_PLAN.md` §2.1
  當初就是這樣決定的），前端文案跟著改成「室溫上下限設定」。冰水主機的水流量門檻沒有做：
  供應商 SDK 完全沒有水流量這個量測值，跟先前拿掉的「水流量」欄位是同一個已知硬體限制。
- **前端**：`AdminChillerPage.vue`／`AdminFcuPage.vue` 換成真實清單（`hvac-service.ts` 的
  `listChillersApi`/`listFcusApi`），三個超標旗標（供水/回水/溫差）改從「目前有效告警」反查
  （`threshold-service.ts` 的 `chillerExceededFlags`），不是前端自己拿門檻跟即時值比大小，這樣
  才會跟 `AlarmEngine` 的 debounce 判斷一致。監控中心總覽頁、前台戰情室的冰水主機卡片也换成
  這套真實旗標，不用再全部顯示 false。FCU 的 `mode`/`fanSpeed` 也順便從寫死的「冷氣／自動」
  改成讀 `FcuOperationMode`/`FcuFanSpeed` 真實列舉值（後端一直都有回傳，前端先前沒有解讀）。
- **已用真實流程驗證（含寫入路徑）**：`npm run typecheck`／`npm run build` 全過；瀏覽器登入
  `merchant_editor` 實測兩個管理頁面清單正確顯示真實裝置（無水流量欄位）。為了測完整的
  儲存流程，經使用者同意暫時給這個帳號對應的自訂角色 `hvac.thresholds` 的 `read`/`update`
  權限（測完立刻撤銷，過程見下方「發現的缺口」），完整跑過：開面板讀到空值 → 填五個門檻值存檔
  → `alarm_rule` 表出現 5 筆正確的規則（含 `AccumulatedRunningHours` 對應
  `AlarmSeverity.Info`、其餘對應 `Warning`）→ 重新整理讀回同樣的值 → `operation_log` 正確記錄
  `actor_username=merchant_editor`。
- **測試過程中抓到一個真的 bug 並修正**：清空「運轉保護值」欄位存檔時後端回 400，不是預期的
  「刪除規則」。原因是 `<input type="number">` 搭配 Vue 的 `v-model.number`，清空欄位時
  Vue 給的是空字串 `''` 不是 `null`（`parseFloat('')` 是 `NaN`，Vue 的 `looseToNumber` 遇到
  NaN 會原樣傳回輸入字串，不會轉成 null），空字串序列化成 JSON 送給後端的 `double?` 直接
  反序列化失敗。修法是在 `threshold-service.ts` 新增 `nullifyEmpty()`，送出前統一把空字串／
  `NaN` 轉成 `null`，兩個 save 函式都套用。修完重新測過：清空欄位存檔後 `alarm_rule` 對應的
  規則列正確被刪除，畫面上「留空代表不設定該門檻」這句提示文字現在是真的。
- **發現的缺口（不是這次功能的 bug，但值得記錄）**：測試帳號 `merchant_editor` 對應的自訂角色
  `member-1` 只有 `hvac.chillers`/`hvac.fcus`/`hvac.overview`/`hvac.reports`，沒有
  `hvac.alarms`/`hvac.thresholds`/`hvac.floor_plan`——這三個權限都是本專案後續階段才加進權限
  目錄的，migration 只更新了系統範本角色（`merchant-admin`/`editor`/`viewer`）的授權，既有的
  「自訂角色」（像 `member-1` 這種依範本複製出來、可能已經改過名字的角色）不會自動補上新權限，
  要場館管理員自己去角色管理頁勾選。這次順手把 `AdminChillerPage.vue`/`AdminFcuPage.vue`/
  `AdminOverviewPage.vue` 的權限錯誤處理拆開——清單本身的權限（`hvac.chillers`/`hvac.fcus`）
  沒有才擋整頁，`hvac.alarms` 沒有只顯示黃色提示、其餘畫面照常顯示，不會因為缺一個次要權限就
  整頁看不到。

## 重大 bug：DateTimeOffset 時間全部少 8 小時（2026-09-18 修復）

準備做冰水主機運轉報表（見下方「報表功能」進度）時，測試告警歷史報表發現插入
`2026-06-10 03:00:00`（原意是 UTC）的測試告警，畫面卻原封不動顯示「2026/6/10 03:00:00」
——如果時區換算正確，Asia/Taipei（UTC+8）應該顯示 11:00，不是 03:00。往下查才發現這不是
單一頁面的小問題，而是整個後端一個系統性的資料正確性 bug，範圍遠比報表功能本身大。

**根因**：好幾個 Repository 的私有 Row 類別把 DB 的 `DATETIME` 欄位直接宣告成
`DateTimeOffset`／`DateTimeOffset?` 屬性給 Dapper 自動對應（例如 `UserRepository`/
`MembershipRepository`/`MerchantRepository`/`PermissionRepository`/`RoleRepository` 的
`CreatedAt`/`UpdatedAt`/`LastLoginAt`/`LockedUntil`）。MySqlConnector 讀出來的原始值是
`DateTimeKind.Unspecified`，Dapper 在沒有註冊自訂 TypeHandler 時，會用 C# 內建的
`(DateTimeOffset)(DateTime)value` 轉換——這個轉換對 Unspecified／Local 的 DateTime 一律
套用**執行環境的 `TimeZoneInfo.Local`** 算 offset。API 容器設的是 `TZ=Asia/Taipei`
（UTC+8），所以每一個這樣轉出來的 `DateTimeOffset`，明明存的是 UTC 時間，卻被貼上了
`+08:00` 的 offset 標籤——這不是顯示格式問題，是**代表的絕對時間點在轉型那一刻就被
搬移了 8 小時**，比單純的顯示錯誤更嚴重。另外還有一個獨立但相關的問題：一般
`DateTime`（不是 `DateTimeOffset`）欄位序列化成 JSON 時，S.T.J 對 `Unspecified` 的
`DateTime` 預設不會加 `Z` 尾碼，前端 `new Date(...)` 遇到沒有時區標記的 ISO 字串
會當成「瀏覽器所在時區的本地時間」解讀——這台系統的前台戰情室就是跑在 Asia/Taipei，
一樣會把 UTC 值誤讀成本地時間。

**影響範圍**：幾乎整個後端有時間欄位的地方都中招，包括先前已經「驗證過」、寫進本文件
其他章節的功能——操作紀錄、使用者「上次登入」時間、告警歷史（本次新做的報表）都受影響。
換句話說，**先前幾次的瀏覽器驗證雖然功能邏輯是對的，但畫面上顯示的具體時間點其實一直是
錯的**，只是先前的測試剛好沒有交叉比對過絕對時間，沒發現。

**修法**（兩個獨立修法，缺一個都不夠）：
1. **`UtcDateTimeOffsetHandler`**（`Teco.Hvac.Infrastructure/UtcDateTimeOffsetHandler.cs`）：
   註冊為全域 Dapper `SqlMapper.TypeHandler<DateTimeOffset>`（在
   `TecoDbConnectionFactory` 的靜態建構子註冊一次，全域生效），明確把讀出來的 `DateTime`
   標記成 `Utc` 再轉換，讓 offset 一律是 0，不受容器系統時區影響。這修的是「Dapper 直接
   對應到 `DateTimeOffset` 屬性」的情況。
2. **`DateTimeUtcExtensions.AsUtcOffset()`**：給「Dapper 先讀進一個 `DateTime` 型別的私有
   Row 欄位，Repository 再手動指派給網域物件的 `DateTimeOffset` 屬性」這種寫法用——這種手動
   指派是 C# 語言層級的隱含轉換，不會經過 Dapper 的 TypeHandler。目前找到的實際案例是
   `AlarmRepository.ListAsync` 的 `StartedAtUtc`/`EndedAtUtc`/`AckAtUtc`，已改用
   `.AsUtcOffset()` 明確標記。`FloorPlanRepository.UpdatedAtUtc` 先前就已經用
   `new DateTimeOffset(r.UpdatedAt, TimeSpan.Zero)` 正確處理過，不需要再改。
3. **`UtcDateTimeConverter`/`UtcNullableDateTimeConverter`**
   （`Teco.Hvac.Api/Services/UtcDateTimeConverter.cs`）：給純 `DateTime`（不是
   `DateTimeOffset`）欄位的 JSON 序列化用，一律當作 UTC 處理、輸出帶 `Z` 的 ISO 字串。
   在 `Program.cs` 同時掛到 HTTP JSON 選項（`ConfigureHttpJsonOptions`）跟 SignalR 的
   JSON Hub Protocol（`AddSignalR().AddJsonProtocol(...)`）——兩邊的序列化設定是分開的，
   只設一邊還是會漏掉另一邊（SignalR 即時告警／遙測廣播就是走這條路）。

**已用真實流程驗證**：修復前後各測一次同一組資料——插入測試告警
`started_at='2026-06-10 03:00:00'`，修復前 API 回傳 `startedAt` 帶 `+08:00`
offset（代表的 UTC 瞬間是 `2026-06-09T19:00:00Z`，整整錯了 8 小時），修復後正確回傳
`+00:00`；瀏覽器 `new Date(...).toLocaleString('zh-TW', {hour12:false})` 從錯誤的
「2026/6/10 03:00:00」變成正確的「2026/6/10 11:00:00」。額外交叉驗證三個先前已上線的
既有端點，確認都同步修好，沒有殘留舊 bug：`/api/v1/merchant/operation-logs` 的
`timestamp` 正確帶 `Z`；`/api/v1/merchant/users` 的 `lastLoginAt`（`DateTimeOffset?`，
測過有值跟 `null` 兩種情況）正確帶 `+00:00`；告警歷史報表頁面實際重新整理後畫面時間
正確變成 11:00。測試資料驗證完全部清除。

## 報表功能接真實 API（2026-09-18）

最後三個還在打 mock service 的後台頁面——冰水主機運轉報表、FCU運轉報表、異常告警報表——
這次全部換成真實資料，至此**全部後台頁面跟前台戰情室都已經接真實 API，沒有任何頁面還在用
mock service**。

- **每小時聚合排程**（`RollupHostedService`，`Teco.Hvac.Api/Services/`）：API 專案內的
  `BackgroundService`，啟動時立刻跑一次、之後每 15 分鐘跑一次，每次重新聚合最近 26 小時
  （滾動視窗＋`ON DUPLICATE KEY UPDATE`，失敗了下次自動補回來）。`ChillerRepository`/
  `FcuRepository` 各自新增 `UpsertHourlyRollupAsync`，SQL 端一次 `INSERT...SELECT...
  GROUP BY` 聚合完，只吃 `read_status=Success` 的列（斷線/讀取失敗時欄位是 SDK 殘值，
  不是 NULL，混進去算平均會失真）。這是先前 README 列在「已知缺口」的項目，這次補上。
- **`rollup_chiller_1h` 補三個欄位**（migration `008`）：`min_chilled_in`/`max_chilled_in`/
  `avg_chilled_delta`（回水溫度、溫度差，原本只有出水溫度）、`running_hours`（累積運轉時數，
  單調遞增計數器，該小時代表值取 `MAX`）——全部是供應商真的有給的欄位，不是虛構數字。
- **`FcuRepository.GetHistoryAsync` 補 `interval=1h` 支援**，比照冰水主機既有寫法；
  `rollup_fcu_1h` 沒有 mode/fan_speed 的聚合欄位（沒有明確聚合意義），這兩欄在 1h 模式下
  固定回 `null`，前端顯示 `--`，不是漏資料。
- **報表沿用既有的 `/{code}/history?interval=1h` 端點**，沒有另外做 `/api/v1/reports/*`——
  原本規劃裡的這組端點其實跟既有的裝置歷史查詢端點功能重複，直接重用可以避免維護兩套
  邏輯。真正新增的只有 `/api/v1/alarms` 的 `from`/`to` 日期區間篩選（告警歷史報表用）。
- **異常判斷全部改成查 `alarm_event` 是否跟該小時的時間區間重疊**（`threshold-service.ts`
  的 `chillerExceededFlagsInWindow`/`fcuExceededInWindow`），不是前端自己拿門檻設定值跟
  歷史讀值比大小——理由跟先前 `chillerExceededFlags`（即時告警）的設計一致，這樣才會跟
  `AlarmEngine` 的 debounce 判斷一致，不會出現「報表說異常，但告警清單沒這筆」的矛盾。
- **FCU 報表拿掉「設定溫度」／「溫差」整套框架**，跟先前修正告警門檻設定頁的理由一樣：
  供應商 SDK 沒有 FCU 設定溫度，做不出「溫差」，改用「室溫上下限」的絕對溫度模型。
- **最後清理**：`admin-mock-service.ts` 到這裡已經沒有任何消費者，整支刪除；
  `admin-types.ts` 同步拿掉只有它在用的死型別。
- **已用真實流程驗證**：每個子項都有插入合成測試資料（含刻意超標的數值＋對應的
  `alarm_event`）→ 瀏覽器實測三個報表頁的清單/篩選/日期區間/表格/折線圖/超標標記全部正確
  → 全部驗證完清除測試資料。詳細驗證步驟見 `docs/REMAINING_WORK_PLAN.md` 的驗證紀錄。

## 資料保留策略接真實排程（2026-09-21）

`fcu_reading` 保留 90 天、`chiller_reading` 保留 180 天原始讀值（之後只留 rollup 永久保留）
的排程原本只有分割表結構，沒有排程本身，這次補上。

- **`PartitionMaintenanceHostedService`**（API 專案 `BackgroundService`，每 24 小時跑一次）：
  分割區自動增補（`p_future` 用完前先切出下個月，維持未來 3 個月的緩衝）+ 舊分割區清除
  （完全超出保留天數的月份分割區直接 `DROP PARTITION`，比逐筆 `DELETE` 快非常多）。
  跟 `RollupHostedService` 一樣跑在 API 專案內，不用 MariaDB Event Scheduler。
- **意外發現的基礎設施缺口**：分割區維護需要 `ALTER` 權限，但 app 的 DB 帳號 `teco_app`
  刻意只有 `SELECT/INSERT/UPDATE/DELETE`（最小權限設計，見 `000_create_app_user.sh` 原本的
  註解）。跟使用者確認後，決定額外授予 `ALTER`，範圍限定在 `teco_hvac.*`（不是全域），
  這是有意識放寬最小權限原則換取這個功能，不是疏漏——已同步更新
  `000_create_app_user.sh`（全新初始化用）跟新增 migration
  `009_grant_teco_app_alter.sql`（既有資料庫要另外用 root 手動執行這行 `GRANT`）。
- **已用真實流程驗證**：增補邏輯直接在真實的 `chiller_reading`/`fcu_reading` 表上測試
  （新增未來的空分割區是無害操作）——暫時把緩衝月數從 3 調大到 8，重啟容器後確認正確新增
  三個月份的分割區，驗證完改回生產值 3。清除邏輯因為會永久刪資料，改用一張分割結構一致的
  暫時測試表，手動執行跟 C# 產生的完全相同的 `DROP PARTITION` 語法確認機制正確（只刪該
  分割區的資料，其他分割區不受影響），測完捨棄測試表；C# 端的日期判斷邏輯（月份名稱解析、
  `p_before_YYYY_MM` 特殊格式、cutoff 比較）額外手算交叉驗證過，並確認在真實表上目前
  正確判斷為「還沒到清除時機」的 no-op（因為現有分割區都還沒過保留期）。

## Refresh Token 撤銷機制（2026-09-21）

Access token 只有 30 分鐘（`AccessTokenMinutes`），先前沒有真正的 refresh token 機制，
使用者每 30 分鐘就會被登出、要重新輸入密碼。`JwtOptions.RefreshTokenDays` 這個設定其實
早就存在（預設 14 天），但從來沒被用過——確認是先前規劃過但沒做完的功能。

- 新表 `refresh_token`（存 SHA-256 hash，不存明文；含 `auth_version_at_issue` 用來在
  密碼/權限變更時讓舊 refresh token 一起失效，理由跟 access token 本身的 auth_version
  檢查一致）。
- 新端點 `POST /api/v1/auth/refresh-token`（刻意不掛 `RequireAuthorization`，呼叫這支
  的當下 access token 通常已經過期）：驗證通過後採 **token 輪替**——撤銷舊的、發一顆
  新的 access token + refresh token 一起回傳，降低外洩後被重複使用的風險。
- `Login` 成功一併簽發 refresh token；`ChangePassword` 成功時撤銷該使用者名下全部
  refresh token。
- 前端 `authorizedFetchApi` 收到 401 時先嘗試 refresh 並重打原本的請求，只有沒有
  refresh token 或 refresh 也失敗時才導去登入頁；用 in-flight promise 擋同一時間多支
  API 同時 401 導致重複呼叫 refresh（token 輪替後舊的會失效，重複呼叫會互相打架）。
  `AdminAstroLayout.astro` 的頁面載入前同步 guard 也同步調整：access token 過期但還有
  refresh token 時「樂觀放行」，交給 `authorizedFetchApi` 的非同步 401 攔截處理。
- 附帶清理：`auth-service.ts` 原本有一支完全沒被呼叫過的死函式 `requireAuthApi`（真正的
  guard 是 `AdminAstroLayout.astro` 裡獨立重寫的 inline script），順手刪除。
- **已用真實流程驗證**：curl 測過完整的簽發/交換/輪替/重用拒絕流程；瀏覽器測過「access
  token 失效時無感換發並重打原請求」（含同時兩支 API 401 只觸發一次 refresh 呼叫的
  in-flight 去重）跟「AuthVersion 不符（模擬密碼變更）時 refresh 也正確失敗、使用者被
  導回登入頁」兩種情境，測試資料跟狀態變更全部還原。

## 使用者刪除功能（2026-09-21）

使用者管理頁的「刪除」按鈕原本是寫死 disabled，這次補上真正的刪除功能。

- 新端點 `DELETE /api/v1/merchant/users/{membershipId}`：刪的是這個人在本場館的成員資格
  （`merchant_membership`），不是整個 `app_user` 帳號——同一個人理論上可以是多個場館的成員，
  刪掉 membership 不影響這個人在其他場館的資格。`merchant.users` 的 `delete` 權限本來就
  已經授予 `merchant-admin` 角色（seed 資料早就有，只是一直沒有對應的端點）。
- 兩個安全擋板：不能刪自己（避免手滑把自己踢出場館）、不能刪掉最後一個在職的場館管理員
  （避免整個場館的使用者/角色管理變成沒有人能維護）。
- 成功後撤銷該使用者的全部 refresh token 並遞增 AuthVersion，讓目前登入中的工作階段立刻失效。
- 前端 `AdminUsersPage.vue` 比照「重設密碼」的 Modal 樣式加確認對話框；自己跟最後一位在職
  管理員的列會直接顯示對應原因的禁用提示，不用等 API 報錯才知道點不動。
- **已用真實流程驗證**：新增一個測試帳號 → 刪除 → 確認清單即時更新、`app_user` 帳號本身
  沒被刪（只刪 membership）、`operation_log` 正確記錄 → 確認自己那一列正確顯示「無法刪除
  自己的帳號」→ 測試資料清除。「最後一位在職管理員不能刪」這個分支需要暫時停用其他管理員
  帳號才能真正觸發（會影響測試當下登入中的帳號），改用程式碼審查驗證邏輯正確，沒有實際
  透過瀏覽器觸發這個特定分支。

## 平台層級重設密碼（2026-09-21）

先前只有場館管理員能重設「自己場館」成員的密碼，平台管理員完全沒有跨場館重設密碼的能力。

- 新端點 `POST /api/v1/platform/merchants/{merchantId}/memberships/{membershipId}/
  reset-password`，邏輯比照 `MerchantEndpoints.ResetUserPassword`，差別只在不檢查
  membership 屬於「呼叫者自己的場館」，改用 URL 上的 `merchantId` 核對 membership 真的
  屬於那個場館（平台管理員本來就能跨場館操作）。
- 權限掛在 `platform.merchants` 底下新增的 `reset_password` 子功能（migration
  `011_platform_reset_password_option.sql`，沿用 `merchant.users` 的 `reset_password`
  子功能命名慣例）。
- 附帶重構：隨機臨時密碼產生邏輯原本寫死在 `MerchantEndpoints.cs` 裡，抽成共用的
  `TemporaryPasswordGenerator`，場館跟平台兩邊端點一起用，不重複寫一份。
- 這個功能純後端，發布當下沒有對應前端頁面——整個平台管理層級當時還沒有前端 UI（2026-09-24
  已補上，見下方「平台管理前端＋雙軌身分切換」一節）。
- **已用真實流程驗證**：測試過程中發現測試帳號 `platform_admin` 的密碼跟
  `test-permissions.sh` 記錄的不一致，用 Python 產生跟 `PasswordHasher`
  （PBKDF2-HMACSHA256）相容的雜湊重設了這個測試/開發用 seed 帳號的密碼（不是
  `merchant_admin`）→ curl 測完整的跨場館重設密碼流程：登入 `platform_admin` → 重設
  另一個場館成員 `test_viewer` 的密碼 → 確認新密碼真的能登入 → 查資料庫確認目標使用者的
  AuthVersion 正確遞增（舊工作階段失效）、`platform_admin` 自己的 AuthVersion 完全沒被
  動到（沒有意外把呼叫者自己登出，這是先前「編輯成員面板」踩過的同一類 bug 的教訓）、
  `operation_log` 正確記錄 → 額外測試負向案例：場館範圍帳號呼叫這支平台端點正確回 403。

## 側邊選單依權限過濾（2026-09-21）

側邊選單原本是完全寫死的陣列，權限只擋 API、不擋選單顯示，導致沒有權限的帳號還是看得到
點了會 403 的項目。這次讓 `AdminSidebar.vue` 依登入帳號的實際 grants 過濾。

- **關鍵發現**：`hvac.overview`/`hvac.reports` 這兩個權限代碼其實早就存在權限目錄裡，
  也已經是 `MerchantEndpoints.MemberFeatureCodes`（「編輯成員」面板七個核取方塊）的成員，
  該陣列上的既有註解甚至明講「跟側邊欄的七個項目 1:1 對應」——這個功能的權限資料模型
  早就設計好了，只是前端接線一直沒做。
- 七個選單項目 ↔ 權限代碼對應：`hvac.overview`（監控中心）、`hvac.floor_plan`
  （空間設備配置）、`hvac.chillers`（冰水主機管理）、`hvac.fcus`（FCU管理）、
  `hvac.reports`（統計報表，涵蓋底下三個子報表，不細分）、`merchant.users`（使用者管理）、
  `merchant.operation_log`（操作紀錄）。
- 判斷邏輯用 `grants` 陣列且要求該代碼至少有 `read`，跟後端 `scope.Has(code,'read')` 的
  語意一致，比只看代碼是否出現在陣列裡更精確。平台範圍帳號（當時沒有對應前端頁面）一律
  不過濾。
- **過程中修好兩個真的 bug**：
  1. 註解裡寫了字面上的 `*/`，提早結束了 Vue SFC 的 block comment，導致整個元件編譯失敗。
  2. `getSessionApi()` 直接呼叫 `localStorage`，但 Vue 元件在瀏覽器 hydrate 前會先在
     Node.js 環境跑一次 SSR（即使是 `client:load`），SSR 沒有這個瀏覽器專屬 API，會讓
     整頁噴 `ReferenceError`。修在 `getSessionApi()` 本身（加
     `typeof localStorage === 'undefined'` 守衛），不是只修呼叫端——詳見
     `CLAUDE.md` 原則 5 的新增說明，這是給日後任何要在元件頂層存取瀏覽器 API 的程式碼
     的通用教訓。
- **已用真實流程驗證**：登入 `merchant-admin` 角色帳號確認七個項目全部顯示；登入自訂角色
  `member-1`（只有 4 個權限：`hvac.chillers`/`hvac.fcus`/`hvac.overview`/`hvac.reports`）
  確認選單正確只顯示對應的 4 個項目、正確隱藏其餘 3 個，跟資料庫查出來的實際權限清單逐一
  核對完全吻合；展開統計報表確認底下三個子報表都正確顯示。

## FCU 對照表管理：發現已經做完，不用另外開發（2026-09-21）

原本規劃要另外做一個「FCU ↔ 分區對照表」CRUD 頁面（§2.6 提到的需求），查證程式碼後發現
「空間設備配置」頁面（`/admin/floor-plan`，較早的階段就做完了）本來就是這個功能的完整
實作，只是介面形式不同：

- `FloorPlanEndpoints.cs` 的 `PUT /api/v1/floor-plan/{floor}/placements` 儲存配置時，
  會呼叫 `FloorPlanRepository.SyncFcuZoneCodesAsync`，把場館管理員在畫面上「這台 FCU
  放在哪個分區」的操作寫回 `device_fcu.zone_code`（格式 `B1-Z01`/`B2-E03`，跟
  `docs/BACKEND_INTEGRATION_PLAN.md` §2.6 描述的格式完全一致）。
- 前端已經把 72 個 B1 分區、41 個 B2 分區、64 台 B1／31 台 B2 真實 FCU 裝置全部列出來
  可供指派——這就是「管理介面」，用視覺化拖拉取代表格 CRUD。
- **如果真的另外做一個表格 CRUD 頁面會出真的 bug**：`SyncFcuZoneCodesAsync` 每次存檔會
  先把該樓層全部 `zone_code` 清空、再依畫面上的配置重新指派，另一套表格介面手動輸入的值
  會被下一次空間設備配置存檔整個蓋掉——兩套機制不是「多一個入口」，是真的會互相打架。
- **已用真實流程驗證**：實際在空間設備配置頁面把 FCU 裝置 B1-1 指派到分區 Z01 並儲存，
  查資料庫確認 `device_fcu.zone_code` 正確變成 `B1-Z01`，驗證完清除測試配置。
- **唯一剩下的是資料填寫，不是功能開發**：64/31 台真實 FCU 該對應到 72/41 個分區中的
  哪一個，需要現場人員拿著設備清單跟平面圖核對後，用這個已經做好的介面填進去。

### ⚠️ 2026-09-21 補充：目前 95 台 FCU 的分區對照全部是「模擬資料」，不是真實對照表

業主驗收時要求先把 FCU 對照表「用模擬的方式全部配置好」以便驗收整條鏈路（分區 → 設備 →
即時監控 → 熱力圖）能不能動，我因此用真正的 `PUT /api/v1/floor-plan/{floor}/placements`
端點（不是繞過去直接寫 SQL）把 64 台 B1 FCU 依 `position` 順序依序指派到 `B1-Z01`~`B1-Z64`
（留空 `B1-Z65`~`B1-Z72` 這 8 個分區，對應既有的「64 台 vs 72 分區」數量落差）、
31 台 B2 FCU 依序指派到 `B2-E01`~`B2-C06`（留空 10 個 `B2-S02`~`B2-S11`，對應「31 台 vs
41 分區」的落差）。座標用各分區在 CAD 圖上的 `label` 中心點，圖面上會正常對齊，不會飄在
分區外面。

**這組對照純粹是為了讓驗收時看得到熱力圖、FCU 清單、告警等功能正常運作，跟現場真實的
「哪一台實體 FCU 裝在哪個房間」完全無關**——真正的對照表要等現場人員拿著設備清單跟平面圖
核對後，直接在「空間設備配置」頁面重新拖拉、儲存，就會整層覆蓋掉這批模擬資料
（`SyncFcuZoneCodesAsync` 的行為就是每次存檔整層重算，不需要另外清除）。
交機前必須換成真實對照表，不能讓這批模擬資料留到正式上線。

### 業主驗收時抓到並修好的真的 bug：模擬資料自己違反了前端的最小間距規則，導致整層存檔全部失敗（2026-09-21）

業主回報「B2 空間設備配置頁面的『儲存配置』按鈕好像壞了」——實際重現後發現按鈕點下去
**完全沒有送出任何網路請求**，畫面也沒有明顯反應，很容易誤以為按鈕本身失靈。追下去才發現
真正原因：`AdminFloorPlanPage.vue` 的 `save()` 在送出前會先呼叫 `serializeLayout()` 做
一次前端驗證（`floor-plan.ts` 的 `parseLayout`/`canPlace`），檢查所有設備彼此的最小間距
（外接圓半徑 + `3 * unitScale` 的緩衝）跟是否都落在正確分區內——**這批驗證在畫面上完全
沒有網路請求可看，失敗時只丟一個容易被忽略的 toast**（「配置含有無效設備、重複點位或
不合法的位置」），跟「按鈕沒反應」的觀感一模一樣。

真正的根本原因是我自己在上面「模擬 FCU 對照表」那步埋下的：用各分區 CAD 圖的 `label`
中心點當座標，沒有檢查任何兩個分區之間的實際距離是否小於前端要求的最小安全間距。
B2 剛好有兩對這樣的分區——`B2-N06`(3.1㎡)/`B2-N07`(2.8㎡) 中心點只距 16 個圖面單位，
`B2-W05`(3.7㎡)/`B2-W06`(2.1㎡) 也是同樣狀況——兩個分區都小到中心點距離低於最小間距
要求（同尺寸 FCU 圖示大約需要 22 個圖面單位）。後端的 `PUT /api/v1/floor-plan/{floor}/
placements` 端點本身沒有做這層幾何檢查（只檔重複 deviceId／型別／角度範圍），所以當初
用模擬資料直接呼叫 API 寫得進去；但只要**任何人**之後想在這層做**任何**編輯並存檔，
`serializeLayout()` 都會把「整份」（不是只有新增/修改的那一筆）placements 重新驗證一次，
在跑到這兩對衝突設備時就整個拋錯，導致這個使用者完全不相關的操作也無法儲存。

**修法**：用瀏覽器 console 直接載入 app 自己的 `floor-plan.ts`（`canPlace`/`findFreeSpot`），
對 B2 現有 31 筆配置逐一驗證，抓出真正衝突的 4 筆（N06/N07/W05/W06 各一台 FCU）。
N06、W05 這兩個分區在拿掉衝突鄰居後可以用 `findFreeSpot` 重新找到不衝突的座標（其實還是
原本的中心點，因為衝突是鄰居造成的，不是自己分區本身太小）；但 N07、W06 這兩個分區真的
太小，`findFreeSpot` 在整個分區範圍內都找不到任何一點能同時滿足「在分區內」跟「離鄰居
夠遠」——這是 CAD 分區本身的實體限制，不是程式錯誤。處理方式比照一開始就刻意留白的
`B1-Z65~72`／`B2-S02~S11`：**這兩個分區的 FCU 先不放在圖上**（`device_fcu.zone_code`
會變回 `NULL`），B2 從「31 台已配置」變成「29 台已配置」。修復後用真正的
`PUT /api/v1/floor-plan/B2/placements` 端點存回去，並且實際做了一次「移動設備 → 儲存」
的操作，確認存檔會成功、`toast` 顯示「配置已儲存」，不再出現驗證錯誤。

**教訓**：往後任何直接呼叫 API 塞資料（不管是模擬資料還是資料修復）的座標，只要沒有經過
前端這層編輯器自己的 `canPlace` 驗證，都有可能悄悄留下這種「資料本身沒問題但擋住後續所有
編輯」的地雷——保險作法是塞完資料後，開這個分頁做一次隨便挑一台設備、移動一下再存檔的
操作，確認整層資料通得過前端自己的驗證，不要只看 API 回 200 就當作完工。

## 設備自訂代碼／名稱（客戶代碼與系統編號並存，2026-09-22）

業主希望「客戶可以自訂自己的設備代碼」，但系統原本的編號是有意義的、不能拿掉：FCU 的
`zone_code`（`B1-Z01`）對應圖面分區、底層的 `(channel, station_id, position)` 是現場實體
接線；冰水主機的 `code`（`CH-1`）與 `modbus_id` 要跟 Collector 的快照鍵值對得上。所以做法
是「兩組並存」，而不是讓客戶覆蓋系統編號。

- **存哪裡**：直接用 `device_fcu.display_name` / `device_chiller.display_name`——這兩個欄位
  schema 早就有了，FCU 的更是從來沒被使用過（這也正是 FCU 管理頁先前「設備名稱」和
  「設備編號」兩欄會顯示一模一樣的 `B1-Z01` 的原因：前端是 `displayName ?? zoneCode`，
  displayName 一直是 NULL 就退回顯示 zoneCode）。不另外加欄位，零 migration。
- **新增端點**：`PATCH /api/v1/fcus/{id}`（權限 `hvac.fcus:update`）、
  `PATCH /api/v1/chillers/{id}`（權限 `hvac.chillers:update`），body 都是 `{ displayName }`，
  兩支都只更新 display_name，不碰任何實體位址欄位，並寫入 `operation_log`（含 before/after）。
  這也是這兩個資源第一次有寫入端點——在此之前 `FcuEndpoints`／`ChillerEndpoints` 全是唯讀。
- **schema 的不對稱要注意**：`device_chiller.display_name` 是 **NOT NULL**、
  `device_fcu.display_name` 是 **NULL**（001_schema.sql 既有設計）。所以 FCU 允許留空＝清回
  「未設定」，冰水主機不接受留空，後端會回 400。前端的驗證訊息也跟著分開寫。
- **畫面怎麼顯示**：兩個管理頁的表頭改成「自訂代碼／自訂名稱」＋「系統編號」兩欄並存；
  FCU 沒設定時第一欄顯示灰色「未設定」而不是退回顯示系統編號——兩欄長得一樣的話，
  使用者根本看不出來哪一欄是自己可以改的。每列加「編輯」開小視窗，視窗標題列會把系統編號
  當唯讀資訊顯示出來，講清楚那個不能改。
- **其他頁面自動跟著變**：告警清單／報表、監控中心、前台戰情室本來就是
  `displayName ?? 系統編號` 的寫法，所以一旦設定就會自動顯示客戶代碼，不用改那些頁面。
  **圖面上的標籤刻意維持系統編號**（`FloorPlanCanvas` 用的是 `code` 不是 `name`），因為那標的是
  「分區」不是「設備」，換成客戶代碼反而會跟分區編號混淆。

**已驗證**：把 B1-Z01 的自訂代碼設成 `AC-B1-012` → FCU 管理頁兩欄正確並存、資料庫
`device_fcu.display_name` 正確寫入、`operation_log` 留下「更新了 FCU B1-Z01 的自訂代碼
（未設定 → AC-B1-012）」；異常告警報表的「設備名稱」欄自動變成 `AC-B1-012`、「設備編號」
仍是 `B1-Z01`，沒設定的其他 FCU 仍正確顯示 `FCU#63` 這類 fallback。冰水主機留空儲存會被擋下
並顯示「顯示名稱不能留空。」。另外補了一個小防呆：值沒有變更就直接關閉視窗、不打 API，
避免操作紀錄留下「冰水主機 1 → 冰水主機 1」這種空紀錄。

## 告警門檻設定補上前端防呆驗證（yup + vee-validate，2026-09-21）

業主驗收「冰水主機管理」的告警門檻設定面板時發現一個真實資料問題：回水溫度門檻存了
`0.6 ~ 0.5`（下限大於上限），畫面完全沒有提示，`儲存設定` 按鈕看起來能按。查證後發現
**前後端都沒有檢查「下限不能大於上限」**：`ThresholdEndpoints.cs` 把上/下限各自存成獨立的
`alarm_rule` 列（`LessThan`/`GreaterThan` 分開兩條規則），本來就不檢查兩者的大小關係；
前端 `AdminChillerPage.vue`／`AdminFcuPage.vue` 的表單也只有手寫的 dirty-check，沒有任何
數值驗證。下限大於上限雖然兩個數字本身都合法，但語意上等於「小於 0.6」跟「大於 0.5」兩條
規則同時生效，等於全部讀值都會告警。

新增 `yup`＋`vee-validate`（`@vee-validate/yup`）作為表單驗證方案，之後其他頁面若有類似的
數值表單可以照這個模式做，不用每個頁面各自手刻驗證邏輯：

- `src/apps/monitoring/pages/admin/_services/threshold-validation.ts`：共用的 yup schema
  （`chillerThresholdSchema`／`fcuThresholdSchema`），核心是掛在「上限」欄位上的
  `max-gte-min` test——兩側都有值時才比較，其中一側留空（代表不設定該側門檻）不算錯誤。
  數字輸入框留空時 `v-model` 給的是空字串，這裡用 yup 的 `.transform()` 在驗證階段就轉成
  `null`，跟 `threshold-service.ts` 既有的 `nullifyEmpty()`（送出前才轉）是同一個道理，
  只是要在驗證這一關也處理一次，不能只在送出前處理。
- `AdminChillerPage.vue`／`AdminFcuPage.vue` 改用 `vee-validate` 的 `useForm`／`defineField`：
  `meta.dirty` 直接取代原本手寫的 `changed` computed（不用自己維護一份 `original` 再比對），
  `handleSubmit()` 包住儲存邏輯，驗證沒過就不會呼叫到 API，`AdminRightPanel` 的
  `:can-save` 改成 `meta.dirty && meta.valid`。錯誤訊息顯示在對應的「上限」輸入框下方，
  跟畫面「最低 ~ 最高」的閱讀順序一致。

**已驗證**：開啟 CH-1 的門檻設定面板（DB 裡原本就有 `0.6 ~ 0.5` 這筆壞資料），把上限改成
不合法的值會即時顯示「回水溫度上限不能小於下限」且儲存鈕維持 disabled；改回合法值
（`0.6 ~ 1.5`）錯誤消失、儲存鈕恢復可按，實際存檔成功。FCU 的室溫上下限面板做了同樣的
測試（暫時改成 `16 ~ 10` 觸發錯誤、確認訊息「室內溫度上限不能小於下限」正確顯示，再改回
原值 `16 ~ 28` 未儲存直接關閉面板，資料庫沒有被異動）。`npm run typecheck`／`npm run build`
都過。

## 業主驗收時抓到並修好的真的 bug：4 個頁面的預設日期區間是寫死字串（2026-09-21）

驗收「操作紀錄」頁面時發現一打開就顯示「查無操作紀錄」，即使資料庫裡明明有當天剛產生的紀錄。
查證後發現 `AdminLogsPage.vue`／`AdminAlarmReportPage.vue`／`AdminFcuReportPage.vue`／
`AdminChillerReportPage.vue` 這 4 個頁面的預設（以及「重置」按鈕還原用）日期區間都是寫死的
字面字串（例如 `ref('2026-05-07')`、`ref('2026-09-01')`），不是用 `new Date()` 算出來的——
這些字串很可能是頁面剛開發、還在用當時的「今天」測試時留下來的，之後沒有人把它們改成動態
計算，導致日期一過就一直停在那個固定區間，使用者一打開頁面就看到空的報表，會誤以為系統
沒資料或壞掉。已全部改成用 `new Date()` 動態計算（操作紀錄／異常告警報表預設「近 3 個月」，
FCU／冰水主機運轉報表預設「今天」），瀏覽器重新整理後確認 4 個頁面都正確顯示今天涵蓋在
預設區間內、也正確顯示了當天的真實資料（操作紀錄頁面顯示了驗收過程中產生的角色 CRUD 與
空間設備配置操作紀錄）。日後任何「預設日期區間」都要用這個寫法，不要複製舊頁面的寫死字串。

## 現場 IoT 還沒連線期間的展示用模擬資料（2026-09-21）

現場 Collector 還沒連上真實設備（P6 卡在現場主機存取權限），統計報表／異常告警報表這幾個
頁面在拿去 demo 給同事看時會是空的，不容易理解功能。新增兩支腳本專門處理這個情境：

- `backend/deploy/seed-demo-data.sh [--days N]`（預設 7 天，上限 30 天）：直接灌
  `rollup_chiller_1h`／`rollup_fcu_1h`（報表頁固定查 1h rollup，不查 raw，見
  `hvac-service.ts` 的 `interval='1h'` 預設值）＋ 4 筆已結束的模擬告警（`alarm_event`，
  `memo` 固定帶 `[DEMO] 模擬資料，非真實告警` 前綴方便辨識/篩選）＋ **今天**
  （Asia/Taipei 當地日期）的 `fcu_reading` 原始讀值。最後這項是 2026-09-21 補上的：
  前台戰情室左側「每小時 FCU 啟用數」「每小時溫度變化」這兩張小圖表查的是
  `FcuEndpoints.BuildHourlyStatsAsync`，直接讀 `fcu_reading` 原始表按小時分組，
  不是查 `rollup_fcu_1h`——一開始沒注意到這點，只灌了 rollup 表，這兩張圖表還是空的，
  要另外照 `fcu_reading` 的原始粒度、只補「今天」這一天份就好（每台 FCU 每小時一筆，
  不用灌到跟現場 5 秒一筆一樣密）。可重複執行，用 `ON DUPLICATE KEY UPDATE` 覆蓋同一
  段時間，不會疊加出重複資料。
- `backend/deploy/clear-demo-data.sh`：讀 `seed-demo-data.sh` 寫的
  `backend/deploy/.demo-data-range`（已加進 `.gitignore`，記錄這次灌的確切時間範圍），
  只刪這個範圍內的 rollup 列 + `memo` 精準比對的模擬告警，**不用 `TRUNCATE` 整張表**——
  現場 Collector 之後開始寫入真實資料時，這兩張 rollup 表會混著真實跟模擬資料，
  只有用時間範圍/標記字串精準刪除才安全，全表清空會連真實資料一起洗掉。

**這個機制刻意只做「歷史資料表」，不動「即時記憶體狀態」**：監控中心／前台戰情室那些
「現在幾 %／運轉中」的即時卡片讀的是 `CurrentStateStore`（API 記憶體），只有 Collector
真的連線送資料才會更新，重開 API 就會消失——這兩支腳本管不到，也刻意不做，demo 時這些
卡片仍會顯示「離線」，這是預期中的行為，不是 bug。

**開發中踩到的 SQL 語法陷阱**：`INSERT ... SELECT ... FROM a CROSS JOIN b ON DUPLICATE
KEY UPDATE ...` 這個寫法在 MariaDB 會噴 `ERROR 1064 ... near 'KEY UPDATE'`——因為 MariaDB
把 `CROSS JOIN` 當 `INNER JOIN` 的同義詞，語法上允許接 `ON`，所以緊接在後面的
`ON DUPLICATE KEY UPDATE` 會被誤判成 JOIN 的條件式。改成逗號連接（`FROM a, b`）就沒有
這個歧義，效果完全相同。另外 `INSERT ... SELECT ... UNION ALL ...` 要在每個分支各自
`ORDER BY ... LIMIT` 時，每個分支要用括號包起來，不能直接包一層 `SELECT * FROM (子查詢)`
再 UNION——那樣容易在子查詢內部因為多個未命名欄位撞名而噴 `Duplicate column name`。

**已驗證**：跑過 `seed-demo-data.sh` 後瀏覽器確認冰水主機/FCU 運轉報表的表格與折線圖、
異常告警報表都正確顯示資料且日期在合理範圍內；`clear-demo-data.sh` 執行後三張表
（`rollup_chiller_1h`/`rollup_fcu_1h`/`alarm_event` 的 demo 列）都歸零，且 `.demo-data-range`
被正確刪除。

## 讓監控中心／前台戰情室的即時卡片也有模擬資料可看（2026-09-21）

上面的 `seed-demo-data.sh` 只填「歷史資料表」，監控中心／前台戰情室那些「現在幾 %／
運轉中」的即時卡片讀的是另一套機制（API 記憶體裡的 `CurrentStateStore`），業主要求
連這部分也要能展示不同 UI 樣式（有正常、有異常、有停止）給同事看，因此新增：

- `backend/deploy/simulate-live-data.sh`：用 Collector 真正會用的同一組端點
  （`POST /internal/ingest/connection`、`POST /internal/ingest/data`，見
  `IngestEndpoints.cs`）餵一份假快照進去，從 API 的角度看就是「Collector 傳來一筆
  資料」，不是繞過去改資料庫。冰水主機 1 正常、冰水主機 2 異常（軸承溫度過高）；
  95 台 FCU 裡各挑幾台設成異常（配一筆真的 `alarm_event`，因為 FCU 異常是查目前
  有效告警清單，不是看快照裡的欄位）、幾台設成停止，其餘正常運轉。
- `backend/deploy/stop-live-simulation.sh`：把三個通道的連線狀態改回未連線，
  即時卡片立刻變回離線，並清掉這批模擬告警。

### 花了很多時間才抓到的真相：`collector` 容器一直沒停過，會跟模擬資料打架

第一版腳本送出「已連線」快照後，畫面有時對、有時錯，且無法穩定重現、也不是每次
補送幾次就會收斂——一度懷疑是 ASP.NET Core／Kestrel 處理大 JSON 請求的時序問題，
甚至懷疑過 Docker build cache 沒真的套用最新程式碼（後來也確實一併用
`docker compose build --no-cache api` 排除了這個可能性，但排除後現象依舊）。
最後查 `docker compose logs collector` 才發現：**`collector` 容器雖然連不到
`192.168.10.0/24` 的現場設備，但它從來沒有停過，會照原本的邏輯不斷重試連線
（`Connecting → ConnectFailed → Connecting → ...`），每次狀態變化都誠實地呼叫
`POST /internal/ingest/connection` 回報「未連線」**——這跟模擬腳本假造的「已連線」
打的是同一支端點，兩個寫入者互相搶著改 `CurrentStateStore` 的連線狀態，才會看起來
像是隨機發生的怪 bug。修法很單純：`simulate-live-data.sh` 執行時先
`docker compose stop collector`，`stop-live-simulation.sh` 收尾時
`docker compose start collector` 恢復——不是程式邏輯錯誤，`CurrentStateStore`
本身的程式碼完全沒問題，純粹是「兩個資料來源同時寫同一份記憶體狀態」的環境問題。
日後如果在真正接上現場設備後，監控中心／前台戰情室無故顯示離線，第一個該檢查的
就是有沒有什麼東西在跟 Collector 搶著回報連線狀態。

**已驗證**：停用 collector 後執行 `simulate-live-data.sh`，等待 15 秒以上、
重新整理瀏覽器多次，監控中心／前台戰情室／後台 FCU 管理／冰水主機管理頁面
都穩定顯示混合的運轉中/異常/停止狀態，沒有再出現時好時壞的情況；執行
`stop-live-simulation.sh` 後全部正確變回離線，且 collector 容器確認恢復運作。

## 場館角色管理 CRUD 接真實 API（2026-09-21）

業主驗收時要求把角色 CRUD 補完。`RoleRepository` 原本就有完整的 `RenameAsync`/`DeleteAsync`，
只缺 API 端點與前端畫面——角色本身只能透過「編輯成員」的簡化 7 項勾選框間接調整，
看不到角色清單，也沒有獨立的改名/刪除入口。

- 新增端點（`MerchantEndpoints.cs`）：
  - `PATCH /api/v1/merchant/roles/{roleId}`（改名）、`DELETE /api/v1/merchant/roles/{roleId}`
    （刪除）——都先確認角色屬於呼叫者自己的場館（`role.MerchantId == scope.MerchantId`），
    系統範本角色（`is_system=1`）一律 400 拒絕。
  - `MembershipRepository.CountByRoleAsync`：刪除角色前的擋板，還有成員在用就回 400 並附上
    動態訊息（人數 + 引導語），不會讓成員在角色被刪除後失去所有權限。
  - `GET /api/v1/merchant/permissions`：場館範圍的權限目錄（原本只有平台範圍的
    `/platform/permissions`），供前端畫勾選格。
- 新增前端頁面 `src/apps/monitoring/pages/admin/roles/AdminRolesPage.vue`
  （路由 `/admin/roles`，側邊欄新增「角色管理」項目，`permissionCode: merchant.roles`）：
  角色清單（系統範本/自訂角色徽章、使用人數、依角色類型顯示不同操作按鈕）、新增/改名/刪除
  對話框、編輯權限側邊面板（依 `hvac.`/`merchant.` 前綴分組，CRUD 勾選格上限鎖在
  `merchant-admin` 角色目前的實際授權，跟後端 `MerchantRolePermissionCeilingRules` 同一份
  邏輯）。
- **開發中抓到並修好一個真的 bug**：權限面板 CRUD 勾選格用
  `v-for="(field, label) in {create:'新增', read:'讀取', ...}"` 迭代物件字面量，
  但 Vue 3 物件 `v-for` 的參數順序是 `(value, key)`，寫反了——`field` 收到的其實是中文顯示字，
  `label` 才是真正的 `create`/`read`/`update`/`delete` 鍵名，導致所有 CRUD 勾選格的
  `v-model`/`disabled` 全部綁到不存在的屬性上，畫面上看起來像「場館管理員自己什麼權限都沒有」
  （所有格子都被鎖住無法勾選）。子功能勾選格因為走 `:checked`+`@change`（沒有物件 `v-for`）
  沒受影響，才讓這個 bug 更難第一時間發現。用瀏覽器 devtools 直接查 DOM 的
  `checked`/`disabled` 屬性、再用 curl 直接打 API 確認後端資料本身正確
  （`merchant-admin` 的所有權限本來就是 `perCreate/perRead/perUpdate/perDelete=true`），
  才定位到問題其實在前端這行 `v-for` 參數順序，改成 `v-for="(label, field) in {...}"` 後解決。
- 瀏覽器驗證（`merchant-admin` 帳號 Anna Chen）：新增測試角色 → 編輯權限（勾選 FCU 讀取 +
  告警確認子功能）→ 儲存 → 重新整理後重新開啟面板確認持久化 → 改名成功 → 對「有人在用」的
  既有角色測試刪除，正確被擋下並顯示動態人數訊息 → 刪除剛剛的測試角色（0 人使用），成功且
  清單即時更新 → 資料庫確認 `app_role` 無殘留測試角色（`operation_log` 保留操作紀錄，
  屬於預期中的稽核軌跡）。

## 修好真的 bug：角色清單混入每個成員的個人專屬角色，出現多筆同名「自訂權限」（2026-09-23）

業主在 `/admin/users` 的「新增使用者」角色下拉選單回報看到兩筆一模一樣的「自訂權限」選項。

- **根本原因**：`RoleRepository.ListAsync` 沒有排除「編輯成員」六個核取方塊面板
  （`MerchantEndpoints.SetUserFeatures`）幫每個成員自動建立的個人專屬角色——這批角色
  `Code = "member-{membershipId}"`、`Name` 一律是「自訂權限」，本來就是內部實作細節，
  設計上就不該出現在給人挑選的一般角色清單裡。只要有兩個以上成員存過這個面板，
  `GET /api/v1/merchant/roles` 就會把每個人的專屬角色都混進來，變成好幾筆同名但 `id` 不同的
  「自訂權限」；同樣的問題也存在於 `/admin/roles` 角色管理頁與 `GET /api/v1/platform/roles`，
  三者共用同一個 `ListAsync`。
- **修法**：`RoleRepository.ListAsync` 的 SQL 加上 `WHERE code NOT LIKE 'member-%'`，
  三個呼叫端點一次修好。
- **連帶修正**：`AdminUsersPage.vue` 的 `roleLabel()` 原本是拿 `roleId` 去反查
  `/merchant/roles` 清單取名稱——一旦清單排除個人專屬角色，既有成員若剛好指派的就是自己的
  專屬角色，會被誤判成「（未指派）」。改成直接讀 `MerchantUserRow.roleName`
  （`MembershipRepository.ListAsync` 已經 JOIN `app_role` 算好的正確名稱），不再依賴那份
  清單反查。
- 瀏覽器驗證（`merchant-admin` 帳號 Anna Chen）：重新 `docker compose up -d --build api`
  套用後端修改後，「新增使用者」下拉選單只剩系統範本角色（編輯者/場館管理員/檢視者），
  沒有「自訂權限」；使用者列表裡已經用過六個核取方塊的成員（測試編輯者、測試檢視者）
  仍正確顯示「自訂權限」標籤。
- **提醒**：這個 repo 的 API 是跑在 `backend/deploy/compose.yaml` 的 docker 容器裡，不是本機
  `dotnet run`——改完後端程式碼後，`dotnet build` 只驗證編譯得過，容器裡的既有 image
  不會自動套用，必須 `docker compose up -d --build api` 重新建置並重啟容器才會生效。

## Docker 建置優化：補 .dockerignore、restore/publish 分層、web healthcheck 依賴（2026-09-23）

Docker 專家審查 `backend/deploy/` 底下的 Dockerfile 與 compose 設定，抓到幾個實際會造成問題的地方（不是純理論上的最佳實踐建議）：

- **缺 `.dockerignore` 造成的真實問題**：`api.Dockerfile`／`collector.Dockerfile` 的 build
  context 是 `backend/`，但完全沒有 `.dockerignore`。因為 Dockerfile 是用
  `COPY src/Teco.Hvac.Api/ ./src/Teco.Hvac.Api/` 這種整資料夾複製，本機開發留下的
  `bin/`／`obj/`（含綁定本機路徑的 `project.assets.json` 等中繼檔）會被一起複製進 build
  context，除了拖慢 build、讓 cache 常態失效，也有機率讓 container 內的 `dotnet restore`／
  `publish` 誤用到過期中繼資料。更嚴重的是 `backend/deploy/.env`（`DB_ROOT_PASSWORD`／
  `DB_APP_PASSWORD`／`INTERNAL_TOKEN`／`JWT_SIGNING_KEY` 明文機密）也在 context 目錄底下，
  會被整包送進 docker daemon（遠端部署時等於明文機密從 Mac 傳到 VM 的 daemon）。
  新增 [`backend/.dockerignore`](.dockerignore)，排除 `**/bin/`／`**/obj/`／`**/.vs/`／
  `**/.DS_Store`／`deploy/`／`tests/`／`tools/`／`*.user`。
- **Dockerfile 的 restore/publish 沒有分層，快取效益差**：原本是先 `COPY` 整個 `src/`
  原始碼才跑 `dotnet restore`，導致任何一行程式碼變更（即使套件相依完全沒動）都會讓
  restore layer 失效，每次 build 都要重新連網解析 NuGet。`Teco.Hvac.Api`／
  `Teco.Hvac.Collector` 的 `ProjectReference` 都是標準相對路徑，`Collector` 參照供應商 DLL
  也是走 `HintPath`（見 `Teco.Hvac.Collector.csproj`）而非 restore-time 相依，可以安全拆成
  「先複製 `*.csproj` 做 restore，再複製完整原始碼做 publish」兩層，並用 BuildKit 的
  `--mount=type=cache,target=/root/.nuget/packages` 讓 NuGet 套件快取跨 build 保留。
  `collector.Dockerfile` 的 `lib/`（廠商 DLL）挪到 restore 之後、publish 之前複製，
  不影響 restore layer 的快取命中率。
- **`web` 服務的 `depends_on` 沒等 `api` 真的 healthy**：`compose.yaml` 的 `web` 原本是
  `depends_on: [api]` 簡寫，只保證 `api` 容器啟動、不保證 `/readyz` 已回 200，跟
  `collector`／`api` 等 `mariadb` 的寫法（用 `condition: service_healthy`）不一致。
  已改成 `depends_on: { api: { condition: service_healthy } }`。
- **改完 restore/publish 分層後踩到的真的 bug：`sharing=locked` 沒加，平行 build 把 NuGet
  快取寫壞**：`docker compose build`（不加 `--parallelism`）預設會同時平行建置 `api`／
  `collector` 兩個 target，兩邊的 `RUN --mount=type=cache,target=/root/.nuget/packages`
  沒有指定 `sharing`，預設值 `shared` 不保證併發寫入安全——兩個 build 同時對同一份 NuGet
  快取寫入時，其中一邊的下載被取消（因為另一邊先完成），會留下寫到一半的暫存檔
  （`Could not find file '.../0da5rff1.qzc'`，`.qzc` 是 NuGet client 下載中的暫存檔名），
  下次 restore 直接失敗。因為兩個 Dockerfile 的 restore 有共同相依（`Contracts`／
  `Domain`／`Infrastructure`），想繼續共用快取又要避免競爭寫入，兩邊的 cache mount 都加上
  `sharing=locked`，讓平行 build 互斥存取同一份快取、不再同時寫。
- **驗證**：`docker compose config --quiet` 通過；`docker compose build api collector`
  加上 `sharing=locked` 後實際跑成功（兩個 image 都建出來）；`docker compose up -d --build`
  也跑過，`mariadb`／`api`／`collector` 三個服務都變成 `healthy`
  （`web` 這次在這台開發機上因為 host 的 127.0.0.1:8081 被另一個無關的本機專案占用而沒能
  實際啟動，純屬本機環境的 port 衝突，`compose.yaml` 的設定本身已經過 `config` 語法驗證，
  不影響改動正確性）。
- 改動檔案：[`backend/.dockerignore`](.dockerignore)（新增）、
  [`backend/deploy/api.Dockerfile`](deploy/api.Dockerfile)、
  [`backend/deploy/collector.Dockerfile`](deploy/collector.Dockerfile)、
  [`backend/deploy/compose.yaml`](deploy/compose.yaml)。

## `IsFullAccess` 角色權限改為動態計算，不再依賴 Seeder 手動同步（2026-09-24）

補上先前列在「已知缺口」的項目：`platform-admin`／`merchant-admin`（`app_role.is_full_access=1`）
先前的「完整權限」完全是靠 Seeder／migration 手動寫入 `app_role_permission` 撐出來的假象——
`PermissionGrantService.ForRolesAsync` 原本不管這個旗標，單純讀 `app_role_permission` 的實際
儲存列。這代表**每次權限目錄新增一項資源，都要記得回頭幫這兩個角色手動補一行 migration**，
漏補就會讓管理員角色本身反而缺權限（本次盤點時發現目前 10 個場館範圍權限、4 個平台範圍權限
剛好都有補齊，沒有實際踩雷，但這是僥倖，不是機制保證）。

- **修法**：`ForRolesAsync` 改成先查給定角色的 `is_full_access`，true 的角色不讀
  `app_role_permission`，直接把「目前 `app_permission` 裡同 `scope` 的全部權限代碼＋全部
  `sub_features_json` 子功能」即時組成 grants；一般角色（`is_full_access=0`）行為不變，仍照舊
  讀實際授予的列。之後新增權限只要寫進 `app_permission`，`platform-admin`/`merchant-admin`
  自動涵蓋，不必再補一支 migration 去更新 `role_permission`。
- **已用真實流程驗證**：`docker compose up -d --build api` 套用新程式碼後，直接在資料庫插入
  一筆全新的測試權限 `test.autosync_check`（scope=1，刻意不寫任何 `app_role_permission` 列）→
  用臨時建立的 `merchant-admin` 角色帳號登入，`POST /api/v1/auth/login` 回傳的 `grants`
  正確包含 `test.autosync_check` 且是完整 CRUD；用另一個臨時 `editor`（`is_full_access=0`）
  角色帳號登入，`grants` 正確**不含**這個測試權限，確認一般角色不會被誤波及。驗證完清除
  測試權限與兩個臨時帳號。另外用 SQL 比對確認這次修改前 `merchant-admin`/`platform-admin`
  的既有 `role_permission` 列本來就跟權限目錄一致（10/10、4/4），這次修改對現有登入行為
  沒有造成任何權限增減，純粹是面向未來的防呆。
- 改動檔案：[`backend/src/Teco.Hvac.Infrastructure/Permissions/PermissionGrantService.cs`](src/Teco.Hvac.Infrastructure/Permissions/PermissionGrantService.cs)。

## 平台管理前端＋雙軌身分切換（2026-09-24）

平台範圍（`/platform/*`）先前只有 API，完全沒有前端頁面——連「第一顆平台管理員帳號」都只能
用 SQL 灌（`deploy/test-permissions.sh` 就是這樣建 `platform_admin` 測試帳號的）。這次補上
場館管理、系統帳號、平台角色管理三個頁面，以及登入後在平台／場館兩種身分之間切換的畫面。

- **雙軌身分切換**：`AppUser` 一個人可以同時是平台帳號又是一或多個場館的成員（見「權限機制」
  一節），但先前 `POST /auth/login` 只會回傳其中一種身分的 JWT，前端也完全沒接已經寫好的
  `GET /auth/scopes`／`POST /auth/scopes/select`。這次補上：
  - `LoginPage.vue` 登入成功後多查一次 `/auth/scopes`，選項 > 1 才顯示「選擇要用哪個身分」
    畫面，只有 1 個選項（TECO 目前絕大多數帳號）維持原本直接跳轉的體驗。
  - `AdminHeader.vue`（後台/平台共用）的使用者選單加「切換身分」，同樣只在有多個選項時顯示。
  - `selectScopeApi()` 刻意合併舊 session 的 `refreshToken`——後端 `SelectScope` 不換發新的
    refresh token（呼叫當下的 access token 通常還沒過期，純粹切身分不是重新登入），整組覆寫
    會把 refreshToken 蓋成 `null`，之後 access token 一過期就沒有 refresh token 可用。
  - 登入或切身分後要去哪個首頁用 `defaultHomePathApi()` 判斷（platform → `/platform/merchants`，
    merchant → `/admin`），不是永遠導去 `/admin`。
- **新前端模組** `src/apps/monitoring/pages/platform/`（co-located 慣例跟 `admin/` 一致）：
  - `PlatformLayout.vue`／`PlatformSidebar.vue`：重用 `admin/_components/AdminHeader.vue`／
    `ChangePasswordModal.vue`（這兩個本質是登入者資訊列、改密碼，跟 hvac 業務無關，跨模組共用
    比複製一份更好維護），側邊欄選單是場館管理／系統帳號／角色管理三項。進頁面時做
    `isPlatformScopeApi()` 守衛，場館帳號手動打 `/platform/xxx` 網址會被導回 `/admin`（純 UX，
    真正防線仍是後端 JWT 檢查）。
  - `PlatformMerchantsPage.vue`（`/platform/merchants`）：場館清單、CRUD/子項細項模式開關
    （表格內直接切換即送出）、新增場館、以及「管理成員」面板——這是解決雞生蛋問題的關鍵：
    新場館剛建立時沒有任何成員，沒有人能用場館自己的 `/merchant/users` 端點（要求呼叫者已經是
    該場館 scope）新增第一個成員，只能從平台這邊建。
  - `PlatformSystemUsersPage.vue`（`/platform/system-users`）：平台帳號清單、新增系統帳號。
    刻意沒做停用/刪除——後端目前也沒有對應端點，只做「能新增」這個最急迫的雞生蛋問題。
  - `PlatformRolesPage.vue`（`/platform/roles`）：比照 `AdminRolesPage.vue` 的編輯權限面板，
    但刻意沒有「改名」「刪除」——`PlatformEndpoints` 目前只有 `List`/`Create`/
    `GetPermissions`/`SetPermissions`，沒有對應的 `PATCH`/`DELETE`，跟場館角色管理不對稱，
    照實反映後端現況，不是前端漏做。
- **後端補的端點/修正**（這次順手做，不是預先規劃好的）：
  - `PlatformEndpoints.CreateMerchantMembership` 擴充成 find-or-create（比照
    `MerchantEndpoints.AddUser` 的模式）：帶 `userId` 就掛既有帳號，不帶就用
    `username`/`displayName`/`password` 建全新帳號——這是新場館能不能有第一個成員的關鍵。
  - 新增 `GET /platform/merchants/{id}/memberships`（查某場館目前的成員，新增前要看得到
    現有名單）與 `GET /platform/merchants/{id}/roles`（該場館可指派的角色，重用
    `RoleRepository.ListAsync(scope, merchantId)`）。
  - **修好一個真的資安問題**：`GET /platform/system-users` 原本直接把 `AppUser` 實體整包
    `Results.Ok(...)`，`AppUser.PasswordHash` 是 public 屬性，等於把 PBKDF2 雜湊值＋鹽值
    整包送進 API 回應。新增 `PlatformSystemUserRow`（不含密碼欄位，JOIN 角色名稱），跟
    `MembershipRepository.MerchantUserRow` 是同一個理由、同一種修法。
  - **修好兩個 scope 過濾遺漏**：`ListRoles`／`ListPermissions` 原本都不帶 `scope` 篩選，
    會把場館範圍的自訂角色／`hvac.*`／`merchant.*` 權限也混進「平台角色管理」「平台權限目錄」，
    改成明確帶 `RoleScope.Platform`。
  - **修好一個真的 bug**：`merchant.code` 有唯一鍵，但 `CreateMerchant` 原本沒有先查重複就
    直接 `INSERT`，代碼重複時 `MySqlException` 沒人接住、一路變成 500——使用者看到「系統發生
    錯誤」，不知道是自己填了已存在的代碼。新增 `MerchantRepository.FindByCodeAsync`，
    建立前先查一次，重複回 409 並附清楚訊息。
- **已用真實流程驗證**（`docker compose up -d --build api` + curl，這台開發機因為
  `127.0.0.1:8081` 被另一個無關本機專案占用，`web`/Caddy 起不來，改直接打 `api` 容器
  `127.0.0.1:8082` 跟透過 `astro dev` 的 vite proxy 驗證，沒有用瀏覽器點過畫面——前端邏輯
  已用 `npm run typecheck`／`npm run build` 確認無型別錯誤，但按鈕點擊互動本身沒有實際跑過，
  這點誠實列在這裡，不要當作跟瀏覽器驗證等價）：
  - 建立測試場館 → 切換 CRUD 開關 → 查可指派角色（正確只有 merchant-admin/editor/viewer）→
    新增第一個成員（全新帳號）→ 查成員清單（正確顯示）→ 用新帳號實際登入確認真的能用
    （拿到 10 項場館權限的完整 CRUD，證實 `IsFullAccess` 動態計算也套用在這個新場館上）→
    重設該成員密碼 → 重複新增同帳號正確擋 409。
  - 讓 `platform_admin` 也加入測試場館（模擬雙軌身分）→ 重新登入預設仍拿 platform scope →
    `GET /auth/scopes` 正確回傳兩個選項 → `POST /auth/scopes/select` 切到 merchant scope
    正確換發 token 且 `refreshToken` 為 `null`（驗證前端合併邏輯的必要性）。
  - 系統帳號清單確認回應不含 `passwordHash` 欄位、新增系統帳號成功。
  - 平台角色清單確認只有 2 筆（`platform-admin`/`platform-operator`），權限目錄確認只有 4 筆
    `platform.*`；新增自訂平台角色 → 讀取權限（空）→ 設定權限 → 再讀一次確認存檔。
  - 場館帳號的 token 打 `/platform/merchants` 正確 403；未帶 token 正確 401。
  - 重複場館代碼建立正確回 409 並附訊息（`{"message":"場館代碼「test-branch」已經被使用，
    請換一個。"}`），修復前是無訊息的 500。
  - 驗證完清除全部測試資料（測試場館、測試成員、測試系統帳號、測試角色），只留下
    `platform_admin` 帳號本身（密碼因驗證需要重設過，這是既有測試帳號，不是正式帳號）。
- 改動檔案：`backend/src/Teco.Hvac.Api/Endpoints/PlatformEndpoints.cs`、
  `backend/src/Teco.Hvac.Infrastructure/Repositories/{UserRepository,MerchantRepository}.cs`、
  `src/apps/monitoring/pages/admin/_services/auth-service.ts`、
  `src/apps/monitoring/pages/admin/_components/AdminHeader.vue`、
  `src/apps/monitoring/pages/admin/auth/LoginPage.vue`、
  新增 `src/apps/monitoring/pages/platform/**`、`src/pages/platform/*.astro`。
- **已知缺口（誠實列出）**：
  - 平台角色沒有改名/刪除（後端沒有對應端點）；系統帳號沒有停用/刪除；場館沒有停用/刪除
    （`Merchant.Status` 欄位存在但沒有任何地方會把它改成 `suspended`）。
  - 「管理成員」面板新增成員只支援建全新帳號，不支援選擇「已存在的其他帳號」（雖然後端
    `CreateMerchantMembership` 的 API 有支援帶 `userId`）——因為沒有「搜尋既有使用者」的端點，
    UI 上讓人手動輸入一個 `userId` 數字體驗太差，先不做。
  - `SetRolePermissions` 對系統範本角色（`is_system=1`）的檢查是 `scope.IsPlatformAdmin`
    （`AppUser.IsPlatformAdmin` 這個獨立的超級旗標），不是「有 `system_role_id` 指到
    platform-admin 角色」就可以——目前包括 `platform_admin` 測試帳號在內，沒有任何帳號的
    `is_platform_admin=1`，所以目前沒有人能透過 API 編輯 `platform-admin`/`platform-operator`
    這兩個系統範本角色自己的權限（自訂平台角色不受此限）。這是刻意的多一層防線設計
    （`AppUser.cs` 上的註解本來就寫「即使沒有另外指派 SystemRoleId 也視為平台管理員」，
    是獨立於角色之外的超級旗標），不是這次的 bug，但要註記清楚：需要調整這兩個系統角色的
    權限時，只能直接改資料庫的 `is_platform_admin` 欄位。
  - 沒有做瀏覽器端的點擊互動驗證（見上方「已用真實流程驗證」的說明），只驗證到 API 行為與
    typecheck/build 通過。

## 開通 `platform_admin` 帳號的 `is_platform_admin` 超級旗標（2026-09-24）

上一節列的缺口——沒有任何帳號能編輯 `platform-admin`/`platform-operator` 這兩個系統範本
角色自己的權限——經使用者要求，直接在資料庫把測試/開發用帳號 `platform_admin` 的
`is_platform_admin` 從 `0` 改成 `1`（同時遞增 `AuthVersion`，讓下次登入立刻拿到新身分）。
純資料異動，沒有改程式碼，也沒有對應的 API/UI（這個開關刻意不開放自助操作，見上方「平台
管理前端＋雙軌身分切換」一節的說明）。

**已用真實流程驗證**：改資料庫前，用 `platform_admin` 呼叫
`POST /platform/roles/2/permissions`（`platform-operator` 角色）確認回 403；改完
`is_platform_admin=1` 後重新登入，JWT 的 `isPlatformAdmin` 正確變成 `true`，同一支 API
改成回 204。送出的內容跟 `platform-operator` 原本的 `platform.merchants` 權限值完全相同
（唯讀），純粹測試「擋板有沒有解除」，讀取確認沒有意外改動任何實際權限資料。

## 業主實測抓到的真的 bug：平台帳號登入後被送去場館專用的 `/admin` 頁面（2026-09-24 修正）

業主照著操作說明登入 `platform_admin`，卻沒有到 `/platform/merchants`，而是停在 `/admin`
（監控中心）——畫面側邊選單全部項目都顯示，但內容整頁顯示「目前登入的帳號沒有監控資料的
查看權限」，看起來像帳號權限設定壞了。

**根因**：業主應該是先打開過 `/admin`（那時候還沒登入），`AdminAstroLayout.astro` 的
inline guard 沒有 session 就把他導去 `/login?redirect=%2Fadmin`。`LoginPage.vue` 的
`redirectTarget()` 原本邏輯是「URL 有帶 `?redirect=` 就無條件採用，不管目前登入的是哪種
身分」，於是平台帳號登入成功後還是被送回 `/admin`，而不是 `defaultHomePathApi()` 算出來的
`/platform/merchants`。連帶暴露另一個既有問題：`AdminSidebar.vue` 的選單過濾邏輯
（`visibleCodes`）只有場館 scope 才會過濾，非 merchant scope（含 platform）一律「不過濾、
全部顯示」——這是設計時假設「平台帳號理論上不會走到這裡」留下的漏洞，一旦真的走到這裡就會
出現選單全開但內容全部 403 的破碎畫面。

**修法**（兩處都要修，缺一個都不夠）：
1. `LoginPage.vue` 的 `redirectTarget()` 改成：只有 `redirect` 參數跟目前登入身分屬於
   同一個區域（都是 `/platform/*` 或都不是）才採用，否則回退到 `defaultHomePathApi()`
   算出來的預設首頁——平台帳號無論 URL 帶什麼 `?redirect=/admin/xxx`，都會被送去
   `/platform/merchants`，不會再被舊的 redirect 參數牽著走。
2. `AdminLayout.vue`（`/admin/*` 的共用外殼）新增對稱守衛：`onMounted` 時若目前 session 是
   `platform` scope，直接導去 `/platform/merchants`——這樣就算之後又有其他路徑把平台帳號
   送到 `/admin` 頁面，也會在畫面渲染前就被攔截，不會再卡在破碎畫面上。跟
   `PlatformLayout.vue` 原本就有的「場館帳號誤入平台頁面」守衛互為鏡像。
3. 修這兩處時自己也踩到一次 CHANGELOG 早就記錄過的同一類 bug：註解裡寫
   `hvac.*/merchant.*` 這種帶 `*/` 的說明文字，在 `LoginPage.vue` 的 `/** */` 區塊註解裡
   提早把註解關掉，導致 `npm run build` 直接編譯失敗（`Unexpected token`）。改成
   「hvac 或 merchant 開頭的權限」這種不含 `*/` 字面組合的寫法。`AdminLayout.vue` 那邊雖然
   用的是 `//` 單行註解不受影響，但為了避免視覺上誤導也一併改了用詞。

**已用真實流程驗證**：`npm run typecheck`／`npm run build` 全過（`build` 一開始因為上述
註解 bug 直接失敗，修完重跑成功）；`curl` 確認 `/login`、`/admin`、`/platform/merchants`
三個路由開發伺服器都正常回應 200。**沒有實際用瀏覽器重現「先訪問 /admin 被彈回登入頁、
再登入」這個確切操作路徑**（那需要清掉瀏覽器工作階段重新走一次），這點誠實列出——邏輯
修正是照著業主回報的畫面症狀往回推導出的根因，程式碼審查後確認修法能解決該症狀，但這次
沒有機會重新用瀏覽器複現原始 bug 再驗證修好。

## 業主實測抓到的真的 UX 問題：場館管理頁的 CRUD/子項開關看起來像沒設定對（2026-09-24 修正）

業主實際點開平台管理的「場館管理」頁，看到「東元高爾夫球場」這列的兩個 checkbox 都沒打勾、
旁邊寫著「簡化模式」，但業主知道這個場館目前確實是用簡化模式在運作（對照「編輯成員」那種
簡化勾選面板），直覺覺得「應該要打勾才對」——資料其實是對的（`isRoleCrudConfigurationEnabled:
false` 就是簡化模式），問題出在 UI：checkbox 旁邊的文字會隨勾選狀態動態切換（勾了顯示
「細項模式」、沒勾顯示「簡化模式」），checkbox 本身的語意是「是否啟用細項設定」，但緊貼著
一個會變來變去的說明文字，容易讓人誤讀成「checkbox＝目前是不是這個模式」。

**修法**：拆成兩層資訊，不再用同一個 checkbox 承載「目前狀態」跟「操作意圖」兩種意思：
- 上面一個固定樣式的徽章，永遠顯示「目前：簡化模式／細項模式」，純顯示、不能互動。
- 下面的 checkbox 固定顯示「啟用細項設定」這句不會變的文字，勾選語意永遠一致
  （勾了＝要切到細項模式）。

跟「新增場館」彈窗裡本來就用固定文字（「啟用角色 CRUD 細項設定（關閉＝授予資源即自動取得
完整 CRUD...）」）的做法一致，這次只是把清單頁的動態文字也改成同一種模式，避免同一個功能
兩個地方呈現邏輯不一致。

**已驗證**：`npm run typecheck`／`npm run build` 全過。沒有機會用瀏覽器重新確認調整後的
實際畫面（同上一節的環境限制），邏輯上徽章與 checkbox 各自獨立顯示、互不影響對方文字，
業主下次操作時應該不會再誤讀。

## 業主實測踩到的真實事故：切換場館 CRUD 細項模式立即讓兩個帳號的權限被靜默限縮（2026-09-24）

業主在驗證上一節的 UI 調整時，實際把「東元高爾夫球場」（**真實在用的場館，不是測試場館**）
的「角色 CRUD 細項模式」勾選起來，想確認畫面反應——這個動作本身沒有任何警告或確認，勾了
立刻送出、立刻生效。

**造成的真實影響**：`merchant_editor`、`test_viewer` 這兩個帳號的權限是透過「編輯成員」
簡化面板設定的，對應的個人專屬角色（`member-{id}`）在資料庫裡**永遠只存最基本的
`read`**——查證 `app_role_permission` 確認 `hvac.chillers`/`hvac.fcus`/`hvac.overview`/
`hvac.reports` 四個資源全部是 `per_create=0, per_read=1, per_update=0, per_delete=0`。
這組資料能運作完全是靠場館的簡化模式在發 JWT 那一刻把 `read` 自動展開成完整 CRUD
（見「權限機制」一節）。業主一把 CRUD 開關切成細項模式，這個自動展開立刻停止。

**已用真實登入驗證**：`merchant_editor` 登入後 `hvac.chillers` 的 `actions` 從
`["create","read","update","delete"]` 掉到只剩 `["read"]`——新增/修改/刪除全部消失，
且畫面上（不管是場館管理頁還是「編輯成員」面板本身）完全沒有任何提示這件事發生了。

**這也回答了業主當下的疑問「勾了細項模式，為什麼使用者管理的編輯成員畫面沒有變成 CRUD
勾選格」**：CRUD 細項模式這個開關**只影響後端計算 JWT 的方式，不影響任何前端畫面長什麼
樣子**。「編輯成員」面板無論如何都是固定的簡化版（一個資源一個 checkbox），設計上就是
給場館管理員快速勾選用；真正能編輯每個資源完整「新增/讀取/修改/刪除」四格的地方是
「角色管理」頁面的「編輯權限」面板，兩者是完全獨立的功能，開關不會讓其中一個變成另一個。

**修復步驟**：
1. 立即把 `merchant.is_role_crud_configuration_enabled` 改回 `0`（簡化模式），恢復自動展開。
2. 因為是直接改資料庫繞過 `PATCH /platform/merchants/{id}` 這支 API，手動補做 API 原本會做
   的「遞增該場館全部成員的 `AuthVersion`」這一步（`UpdateMerchant` 的 `IncrementAuthVersion
   ForMerchantAsync`），確保受影響帳號手上如果還留著剛剛那顆「只剩讀取」的 access token
   （30 分鐘內都還有效），也會在下一次請求時因為 `AuthVersion` 不符被拒絕，強制重新登入
   拿到正確的權限，不用等 token 自然過期。
3. 重新登入 `merchant_editor` 確認 `hvac.chillers` 恢復完整 CRUD。

**產品面補的防呆**（`PlatformMerchantsPage.vue`）：這種「勾一下就立即、靜默改變別人實際
權限」的開關，原本設計成點了 checkbox 就直接送出，跟先前「編輯成員面板造成管理員自我降級」
那次事故是同一類問題——都是「操作本身合法，但後果嚴重又沒有任何提示」。補上確認對話框：
- 切到細項模式時，明確列出「用簡化面板設定過的成員權限會被限縮到只剩資料庫實際存的內容
  （通常只有讀取），且沒有畫面提示」的警告文字，並提醒切換後要記得去角色管理頁補權限。
- 切回簡化模式時，也提醒「登入時會自動展開成完整 CRUD，可能比畫面勾選的還多」，讓雙向切換
  都有清楚的後果說明，不是只擋危險的那一半。
- checkbox 本身改成 `@change` 觸發確認彈窗、不直接送出，取消的話畫面會維持原狀（`:checked`
  綁定伺服器資料，沒有另外用本地 state，取消後 Vue 重新渲染會自動把 checkbox 視覺狀態
  還原，不需要額外程式碼處理「取消要復原勾選」這件事）。

**已用真實流程驗證**：改資料庫恢復 + 遞增 AuthVersion 後，重新登入 `merchant_editor`
確認 `hvac.chillers` 的 `actions` 正確恢復成 `["create","read","update","delete"]`；
`npm run typecheck`／`npm run build` 確認新增的確認對話框沒有型別錯誤、能正常建置。
**沒有機會用瀏覽器實際點過新的確認對話框**（環境限制同上），這點誠實列出——邏輯是照著
剛發生的真實事故對應設計的，但彈窗本身的互動（取消／確認／checkbox 視覺還原）沒有經過
瀏覽器操作驗證。這個誠實列出的缺口馬上被業主自己點出來，見下一節。

## 業主實測抓到的真的 bug：取消確認對話框後 checkbox 視覺沒有跟著改回未勾選（2026-09-24 修正）

上一節新增確認對話框後，業主實際點了 checkbox → 跳出確認框 → 按「取消」，畫面卻停在
「badge 顯示『目前：簡化模式』，checkbox 卻還是勾選狀態」這種矛盾畫面——查證資料庫確認
`is_role_crud_configuration_enabled` 實際值一直是 `0`（簡化模式），**不是資料被誤改，
純粹是畫面沒有跟著恢復**，但視覺上完全看不出來哪個是對的，跟先前「按鈕壞了」那類 bug
一樣容易誤導。

**根因**：checkbox 是用 `:checked="m.field"`（純屬性綁定，不是 `v-model`）。Vue 只有在
`m.field` 這個響應式來源真的改變時，才會重新把 `checked` 這個 DOM 屬性寫回節點上；使用者
用滑鼠點擊 checkbox 是瀏覽器自己原生切換 DOM 的 checked 狀態，完全不會經過 Vue 的響應式
系統。取消按鈕的處理只是關閉確認框（`toggleConfirmOpen = false`），從頭到尾沒有呼叫 API、
`m.field` 這個值根本沒有變過——Vue 在這輪重新渲染時比對新舊 vnode 的 `checked` prop，發現
兩次都是 `false`（沒有實際改變過），判定不需要更新這個 DOM 屬性，於是瀏覽器自己造成的
勾選狀態就這樣被晾在那裡，沒有人把它改回來。這正是 `v-model` 對 checkbox 特別做了專屬指令
（`vModelCheckbox`）處理的問題——`v-model` 每次都會強制重新套用 `checked` 屬性，不管值有
沒有變，改用純 `:checked` 綁定就會漏掉這個保護。

**修法**：不修資料流本身（那個是對的），改成在使用者「取消」或「儲存失敗」時，用一個
`checkboxResetNonce` 計數器搭配 `:key` 強迫 Vue 把整個 checkbox 元素當成全新節點重建
（不是修補既有節點）——新建立的 DOM 節點一定會照 `m.field` 目前的值重新產生一次
`checked` 屬性，不會受到瀏覽器先前原生切換留下的殘留狀態影響。

**已用真實流程驗證**：`npm run typecheck`／`npm run build` 全過；資料庫確認
`is_role_crud_configuration_enabled=0` 全程沒有被這個顯示 bug 影響，純粹是畫面呈現問題。
**同樣沒有機會用瀏覽器實際點過修好後的畫面**（環境限制同上）——這次的 bug 本身就是靠業主
在瀏覽器操作才發現的活生生案例，證明這個環境限制不是隨口說說，之後有機會應該優先安排一次
完整的瀏覽器操作驗證，而不是每次都靠事後補測。

## 平台「系統診斷」頁＋現場接通驗證手冊（2026-09-29 新增）

**為什麼要做**：現場設備 IP 目前都是暫填值，要到現場才能測接通。到時需要一個地方一眼確認
「三條通道有沒有連上、數據內容合不合理、有沒有寫進資料庫、排程有沒有在跑」——原本這些資訊
散在 collector log、`channel_health`、collector `/healthz`，排程成功與否更是只寫在 log 裡。

**做了什麼**：
- **Collector 程式完全沒改**。診斷資料全部在 API 端取得：`CurrentStateStore`（最新快照、通道
  連線狀態與實際 IP:Port）、`device_fcu`／`device_chiller`（台數比對）、compose 內網直接打
  `http://collector:8080/healthz`（設定 `Diagnostics:CollectorHealthUrl`，逾時 2 秒）。
- `CurrentStateStore` 多保留「上一筆快照」與 API 自己的收件時間／累計筆數，用來判斷累計值倒退、
  UpdateTime 停住、推送中斷。
- 新增 `ScheduledJobStatusStore`：`RollupHostedService`／`PartitionMaintenanceHostedService`
  每次執行記錄開始／結束／成功與否／摘要（最近 20 次，只存記憶體）。持久證據另外用
  `rollup_*_1h` 的最新整點，兩者並列顯示。
- 新增 `Services/Diagnostics/`：`DataValidationRules`（合理範圍，暫定值集中一處）、
  `DiagnosticsService`（組報告，各段獨立 try/catch，資料庫掛掉也不會讓整頁 500）、
  `DiagnosticsRepository`（落地統計）；`ChannelHealthRepository.ListRecentAsync`。
- 端點 `/api/v1/platform/diagnostics`、`/raw`，權限 `platform.diagnostics:read`（僅 platform scope）。
  migration `012_platform_diagnostics_permission.sql`，**既有資料庫要手動執行**。
- 前端 `/platform/diagnostics`（`PlatformDiagnosticsPage.vue` + `diagnostics/_components/`），
  每 5 秒輪詢、可暫停。
- `simulate-live-data.sh --with-anomalies`：先送正常快照當上一筆，再送刻意做壞的快照，
  讓診斷頁的每條檢查不用去現場就能驗證會亮燈。
- `docs/IOT_現場接通驗證手冊.md`：分層排查步驟、症狀對照表、逐欄核對清單、24 小時觀察項目。

**開發時抓到的真實狀況**：
1. **斷線時 Collector 仍持續寫入 `read_status=Disconnected` 的空資料列**——實測 10 分鐘內
   `fcu_reading` 多了 950 筆、`chiller_reading` 96 筆，全部是 0 值。落地統計一開始沒過濾
   `read_status`，結果「連不上設備」被判成「資料庫寫入正常」。已改成只算 `read_status = 1`。
   Collector 要不要乾脆別寫這些列（一天約 14 萬筆垃圾資料）另外處理，這次沒動。
2. **時序表主鍵是 `(device_id, ts)`**，直接對整表 `MAX(ts)` 或 `WHERE ts >= …` 用不到索引，
   會掃整個月份分割區；診斷頁 5 秒輪詢一次，所以查詢一律改成從設備主檔逐台走主鍵
   （相關子查詢／`STRAIGHT_JOIN`）。
3. 模擬腳本只打 `/internal/ingest`、不寫資料庫，跑模擬時「資料庫寫入」與「Collector 程序」
   必定紅燈——這是診斷頁正確反映事實，不是 bug，已在訊息與手冊註明。

**驗證**：`dotnet build`、`npm run typecheck`、`npm run build` 全過；`docker compose up -d --build api`
後以 `platform_admin` 登入瀏覽器實際操作三種情境：
- 暫填 IP（真實 collector 在跑）：三條通道正確顯示「連線中／未連線」、Collector degraded、
  資料庫寫入「無法判斷」、連線變化紀錄每 30 秒一輪的重試循環清楚可見。
- `simulate-live-data.sh`：數據內容與台數比對全綠（64/64/64、31/31/31）。
- `--with-anomalies`：8 小時時間差（紅）、冰水出水 45°C 與累計運轉時數倒退（黃）、DDC1 缺 1 台、
  DDC2 未對照 1 台＋85°C 1 台＋疑似沒回應 1 台（黃）全部正確判定。
- 未帶 token 呼叫回 401。驗證完已執行 `stop-live-simulation.sh` 還原。
