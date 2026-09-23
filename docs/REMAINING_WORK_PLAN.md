# 剩餘工作追蹤（Remaining Work Plan）

建立日期：2026-09-17
用途：整理 `backend/README.md`「尚未實作/已知缺口」與 `CLAUDE.md`「已知限制」列出的項目，
依序開發、逐項打勾。**每項完成都要親自驗證過才能打勾**——有前端畫面的項目用瀏覽器實際操作
一次；純後端、沒有畫面可看的項目（例如排程本身）用資料庫查詢或直接呼叫 API 驗證，並在下方
「驗證紀錄」寫清楚驗證方式，不能只憑 `dotnet build`/`npm run build` 過關就打勾。

標記 🚧 的項目目前卡在「需要現場主機權限」或「需要供應商回覆」，不是我可以獨立完成的，
會先跳過、留在清單最後，等外部條件到位再處理。

進度總覽：**可獨立完成的項目全部完成**（Phase 1-8）。Phase 5（FCU 對照表管理）發現
其實已經透過「空間設備配置」頁面實作完成，不需要另外開發。Phase 8（場館角色 CRUD）是驗收
過程中新增的範圍，後端 Repository 層原本就已完整，只補了 API 端點與前端畫面。剩下的兩個
🚧 區塊（P6 現場部署、供應商回覆）不是我可以獨立完成的工作。

---

## Phase 1｜報表功能完整鏈路

**重要發現（實際讀過三個 mock 頁面的程式碼後才知道，原本估的範圍太小）：**
- `AdminChillerReportPage.vue` 的表格有「水流量」欄位——供應商 SDK 沒有這個量測值，
  跟先前拿掉的「水流量」是同一個已知硬體限制，要整欄拿掉。
- `AdminFcuReportPage.vue` 整頁是用「設定溫度」＋「溫差」的框架設計的（`setTemp`/
  `tempDiffRange`/折線圖的「設定溫度」虛線），但供應商 SDK 沒有 FCU 設定溫度這個欄位，
  跟已經改過的告警門檻設定（「室溫上下限」不是「溫差」）是同一件事——這頁要照同樣邏輯
  重新設計，不是單純把 mock 函式換成真的 API。
- `AdminAlarmReportPage.vue` 相對單純，資料形狀幾乎等同現有 `/api/v1/alarms` 的
  `AlarmRow`，不太需要新的後端工作，主要是前端重寫 + 可能要幫 `/api/v1/alarms` 加日期
  區間篩選參數。
- `rollup_chiller_1h` 表目前只有 `min_chilled_out`/`max_chilled_out`（出水/供水），
  沒有回水溫度（`chilled_water_in`）的欄位，但報表要同時顯示供水跟回水，需要先加 migration
  補欄位。
- 冰水主機的 `GET /chillers/{code}/history?interval=1h` 端點跟 `ChillerRepository.
  GetHistoryAsync` 其實已經存在（早期就寫好了），只是 `rollup_chiller_1h` 從來沒有人寫入過，
  一直是空表；FCU 目前沒有對應的 `interval=1h` 支援，只能查 raw。

依執行順序拆成更小的子項，方便個別驗證跟打勾：

- [x] 1a. Migration：`rollup_chiller_1h` 加回水溫度欄位（`min_chilled_in`/`max_chilled_in`
      或等等實作時再定精確欄位名）
- [x] 1b. 每小時聚合排程（API 專案內 `BackgroundService`，比照 Collector 既有定期任務寫法，
      不用 DB Event Scheduler）
      - `rollup_chiller_1h`：來源 `chiller_reading` 的 `load_percentage`/`chilled_water_out`/
        `chilled_water_in`/`input_power_kw`/`accumulated_kwh`（`kwh_delta` = 該小時
        `MAX-MIN`，因為是累積電表）——這些都是供應商真的有給的欄位，不是虛構數字。
      - `rollup_fcu_1h`：來源 `fcu_reading` 的 `temperature`/`switch_status`。
      - 時區換算在 C# 用 `TimeZoneInfo.Local` 做，不在 SQL 用 `CONVERT_TZ`（比照既有原則）。
- [x] 1c. FCU 的 `history` 端點/Repository 補 `interval=1h` 支援（比照冰水主機現有寫法）
- [x] 1d. 告警歷史報表：`/api/v1/alarms` 加日期區間篩選（`from`/`to`），前端
      `AdminAlarmReportPage.vue` 改接真實 API（相對單純，這項可以最先做完）
- [x] 1e. 冰水主機報表：`AdminChillerReportPage.vue` 改接真實 history 端點；拿掉水流量欄位；
      改用真實裝置清單；異常旗標改成查該時間區間內 `alarm_event` 是否有對應規則觸發
      （不是前端自己拿門檻跟歷史值比大小，理由跟先前 `chillerExceededFlags` 的設計一致）
- [x] 1f. FCU 報表：`AdminFcuReportPage.vue` 移除「設定溫度」／「溫差」框架，改成
      「室溫上下限」語彙（比照告警門檻設定頁的用詞），折線圖拿掉「設定溫度」虛線，
      改標示上下限帶狀區域或門檻線；異常旗標同樣改查 `alarm_event` 區間

**驗證方式**：每個子項有獨立可查的結果就個別驗證（migration 用 `SHOW CREATE TABLE`、
排程用手動觸發 + 查 `rollup_*` 表）；1d/1e/1f 這三個前端項目要開瀏覽器實際操作
（切換日期區間、切換裝置、切表格/折線圖、匯出按鈕）確認資料正確才打勾。

---

## Phase 2｜資料保留策略 ✅ 已完成（2026-09-21）

- [x] 90/180 天分割區清除排程（`fcu_reading` 90 天／`chiller_reading` 180 天，只留
      `rollup_*` 永久保留）+ 分割區自動增補（分割表目前只建到 2027-02，需要有排程持續往後補）
      - `PartitionMaintenanceHostedService`，API 專案的 `BackgroundService`，每 24 小時跑一次。

**驗證方式**：這項沒有前端畫面，用資料庫查詢驗證。

**驗證紀錄**：
- 過程中發現一個真實的基礎設施缺口：分割區維護需要 `ALTER` 權限，但 app 的 DB 帳號
  `teco_app` 刻意只有 `SELECT/INSERT/UPDATE/DELETE`（最小權限設計）。跟使用者確認後
  （2026-09-21），額外授予 `ALTER`，範圍限定在 `teco_hvac.*`——已寫進
  `000_create_app_user.sh`（全新初始化）跟 migration `009_grant_teco_app_alter.sql`
  （既有資料庫手動執行）。
- **增補邏輯**：對 SQL 機制的驗證直接在真實的 `chiller_reading`/`fcu_reading` 表上做
  （新增未來的空分割區是無害操作）——暫時把緩衝月數從 3 調到 8，重啟容器，確認正確新增
  `p_2027_03`/`p_2027_04`/`p_2027_05` 兩張表都有，`SHOW CREATE TABLE` 確認分割區正確，
  驗證完改回生產值 3 並重新建置/重啟。
- **清除邏輯**：`DROP PARTITION` 會永久刪資料，不能拿真實表（`p_before_2026_10` 裡有
  Collector 寫入的大量真實資料）冒險測試。改用一張欄位/分割結構一致的暫時測試表
  （`test_partition_demo`），手動執行跟 C# 產生的完全相同的 `ALTER TABLE ... DROP
  PARTITION` 語法，確認只刪該分割區的資料、其他分割區資料不受影響，測完整張表捨棄。
  C# 端的日期判斷邏輯（月份分割區名稱解析、`p_before_YYYY_MM` 特殊格式、cutoff 比較）
  另外用真實日期（2026-09-21）手算交叉驗證過，且在真實表上確認目前確實是正確的
  no-op（所有分割區都還沒過保留期，不應該被清除，實際查詢結果也是如此）。

---

## Phase 3｜Refresh Token 撤銷機制 ✅ 已完成（2026-09-21）

- [x] `/api/v1/auth/refresh-token` 端點 + refresh token 存 DB

**重新確認過實際現況（跟計畫原本寫的不完全一樣）**：後端本來就有一支 `/api/v1/auth/refresh`，
但那支掛了 `RequireAuthorization()`，是「用還沒過期的 access token 換一顆新的（重新拉最新
權限）」，不是真正的 refresh-token 機制——access token 一旦過期（`AccessTokenMinutes=30`），
使用者就是直接被登出，沒有辦法不重新輸入密碼延續 session。`JwtOptions.RefreshTokenDays`
這個設定其實已經存在（預設 14 天），但從來沒有被用過，確認是先前規劃過但沒做完的功能。

**做了什麼**：
- 新表 `refresh_token`（存 SHA-256 hash 不存明文，欄位含 `auth_version_at_issue`）。
- 新的 `POST /api/v1/auth/refresh-token`（刻意不掛 `RequireAuthorization`，因為呼叫這支的
  當下 access token 通常已經過期）：驗證 refresh token 沒過期沒撤銷、比對使用者「現在」的
  `auth_version` 是否跟簽發當下一致（密碼被改/權限被調整過就算沒過期也要失效，理由跟
  access token 本身的 auth_version 檢查一致）→ 採 token 輪替，每次交換都撤銷舊的、發一顆
  新的，降低外洩後被重複使用的風險。
- `Login` 成功時一併簽發 refresh token；`ChangePassword` 成功時撤銷該使用者名下全部
  refresh token（不然舊 session 能繞過「改密碼要重新登入」）。
- 前端 `auth-service.ts`：`AuthSession` 加 `refreshToken` 欄位；`authorizedFetchApi` 收到
  401 時先嘗試用 refresh token 換新的 access token 並重打一次原本的請求，只有在沒有
  refresh token 或 refresh 也失敗時才清掉工作階段導去登入頁；用 in-flight promise 擋同一
  時間多支 API 同時 401 時重複呼叫 refresh（token 輪替後舊的會失效，重複呼叫會互相打架）。
  `AdminAstroLayout.astro` 的頁面載入前同步 guard 腳本也同步更新：access token 過期但還有
  refresh token 時「樂觀放行」不擋在這裡，交給 `authorizedFetchApi` 的非同步 401 攔截處理。
- 清理：`auth-service.ts` 原本有一支完全沒被呼叫過的死函式 `requireAuthApi`（真正的 guard
  其實是 `AdminAstroLayout.astro` 裡獨立重寫的 inline script），順手刪除。

**已用真實流程驗證**：
- curl 測完整流程：登入拿到 refreshToken → 用它換新 accessToken 成功 → 重複用同一顆舊的
  refreshToken 換發，正確回 401（token 輪替生效）→ 查 DB 確認舊的那筆 `revoked_at` 有值、
  新的那筆是 NULL。
- 瀏覽器測完整流程：登入後故意把 localStorage 裡的 accessToken 竄改成無效簽章 → 觸發頁面
  導覽（兩支 API 同時打）→ 確認兩支都先各自收到 401，接著**只有一次** `POST
  /auth/refresh-token`（證明 in-flight 去重生效，不是打兩次）→ 兩支原本的請求都自動重打
  成功拿到 200，畫面正常顯示資料，使用者完全沒有被彈回登入頁、也不用重新輸入密碼。
- 模擬密碼變更情境：直接在 DB 把該使用者的 `auth_version` +1（跟真正改密碼時
  `IncrementAuthVersionAsync` 效果一致）→ 觸發頁面導覽 → 確認這次 access token 401 後
  refresh 也正確失敗（AuthVersion 不符）→ 使用者正確被導回登入頁 → 查 DB 確認該筆
  refresh token 也被標記撤銷。測完把 `auth_version` 還原、刪除測試期間產生的 refresh token
  紀錄，`SELECT COUNT(*)` 確認回到 0 筆。

---

## Phase 4｜使用者管理補完 ✅ 已完成（2026-09-21）

- [x] 使用者「刪除」功能（後端 API + 前端按鈕解除 disabled）

**做了什麼**：新增 `DELETE /api/v1/merchant/users/{membershipId}`，刪的是這個人在本場館的
成員資格（`merchant_membership`），不是整個 `app_user` 帳號——同一個人理論上可以是多個場館
的成員。兩個安全擋板：不能刪自己、不能刪掉最後一個在職的場館管理員（避免場館被鎖死沒人能
管理）；成功後撤銷該使用者的全部 refresh token 並遞增 AuthVersion，讓目前登入中的工作階段
立刻失效。前端 `AdminUsersPage.vue` 的「刪除」連結解除 disabled，比照「重設密碼」的
Modal 樣式加確認對話框；自己跟最後一位在職管理員的列會顯示對應原因的禁用提示，不用等
API 報錯才知道點不動。`merchant.users` 的 `delete` 權限本來就已經授予 `merchant-admin`
角色（seed 資料早就有，只是一直沒有對應的端點可以用）。

**已用真實流程驗證**：瀏覽器登入 `oplog_admin`（場館管理員）→ 新增一個測試用檢視者帳號
→ 確認自己那一列的刪除連結正確顯示「無法刪除自己的帳號」禁用提示 → 點測試帳號的刪除、
確認對話框正確顯示姓名/帳號 → 確認刪除、清單即時更新（8 筆變 7 筆）→ 查資料庫確認
`app_user` 帳號本身還在（沒有被整個刪掉）、`merchant_membership` 那筆真的被刪除、
`operation_log` 正確記錄刪除事件 → 測試資料（測試帳號、對應操作紀錄）全部清除。
「最後一位在職管理員不能刪」這個擋板因為需要暫時停用其他管理員帳號才能真正觸發（會影響
目前登入中的測試帳號本身），改用程式碼審查驗證邏輯正確（跟後端 `activeAdminCount <= 1`
判斷式逐行核對一致），沒有透過瀏覽器實際觸發這個特定分支。

---

## Phase 5｜FCU 對照表管理 ✅ 發現已經做完（2026-09-21，不用再開發）

- [x] FCU ↔ 分區對照表管理 Repository/Endpoint + 前端管理頁面

**重大發現：這個功能其實已經存在，只是換了一個名字**。原本以為要另外做一個「FCU 對照表」
CRUD 頁面，實際去查程式碼才發現「空間設備配置」（`/admin/floor-plan`，早在這個計畫檔建立
之前的階段就做完了）本來就是這個功能的完整實作，只是用「拖拉式視覺化放置」取代「表格
CRUD」這種介面形式：

- `FloorPlanEndpoints.cs` 的 `PUT /api/v1/floor-plan/{floor}/placements` 儲存配置時，
  會呼叫 `FloorPlanRepository.SyncFcuZoneCodesAsync`——這支方法做的事情就是
  `docs/BACKEND_INTEGRATION_PLAN.md` §2.6 講的「FCU ↔ 分區對照表」：把場館管理員在
  畫面上「這台 FCU 放在哪個分區」的操作，寫回 `device_fcu.zone_code`
  （格式 `B1-Z01`/`B2-E03`，跟規劃文件講的完全一致）。
- 前端 `AdminFloorPlanPage.vue`／`FloorPlanCanvas.vue` 已經把 72 個 B1 分區、41 個 B2
  分區（跟規劃文件的數字一致）全部畫出來，下拉選單也列出全部 64 台 B1／31 台 B2 真實
  FCU 裝置可供選擇指派——這就是「管理介面」，不需要再另外做一個功能重複的表格頁面。
  如果真的另外做一個表格 CRUD，`SyncFcuZoneCodesAsync` 每次存檔會先把該樓層全部
  `zone_code` 清空再依畫面上的配置重新指派，等於會覆蓋掉表格頁面手動輸入的值——
  兩套機制會互相打架，不是「多一個入口」那麼單純，是真的會出 bug。

**已用真實流程驗證**：登入 `oplog_admin` → 開啟「空間設備配置」B1 樓層 → 選擇分區 Z01 →
切換到 FCU 類型、選擇真實裝置 B1-1 → 加入此區域 → 儲存配置，畫面顯示「已同步」→ 查資料庫
確認 `device_fcu.zone_code` 正確變成 `B1-Z01`（跟規劃文件描述的格式完全吻合）→ 驗證完畢後
清除測試用的配置（刪除 `device_floor_placement` 測試列、`zone_code` 還原為 `NULL`），
確認畫面重新整理後正確顯示「0 台已配置」。

**唯一剩下、無法由我完成的部分**：64 台（B1）/31 台（B2）FCU 究竟該對應到 72/41 個分區
中的哪一個，需要現場人員拿著設備清單跟平面圖現場核對後，用這個已經做好的介面把資料填進去
——這是「資料填寫」工作，不是「功能開發」工作，等供應商/現場人員提供分區對照資料後，
場館管理員自己就能透過現有介面完成，不需要我再開發任何東西。

### 2026-09-21 補充：業主驗收要求先填一份「模擬對照表」跑通整條鏈路

業主驗收時明確要求「FCU 的配置，你幫我先模擬的方式全部配置好」，目的是驗證分區→設備→
即時監控→熱力圖這條鏈路能不能動，不是要提供真實對照表（現場真實對照表仍要等供應商/
現場人員資料，見上方）。做法：

- 用真正的 `PUT /api/v1/floor-plan/{floor}/placements` 端點（透過已登入的 `merchant-admin`
  瀏覽器 session 呼叫，不是繞過去直接寫 SQL）：64 台 B1 FCU 依 `device_fcu.position` 排序
  依序指派到 `B1-Z01`~`B1-Z64`（`B1-Z65`~`B1-Z72` 這 8 個分區留空，對應既有的「64 台
  FCU vs 72 分區」數量落差）；31 台 B2 FCU 依序指派到 `B2-E01`~`B2-C06`（`B2-S02`~`B2-S11`
  這 10 個分區留空，對應「31 台 vs 41 分區」的落差）。每個分區的 X/Y 座標取自
  `b1-areas.generated.ts`/`b2-areas.generated.ts` 裡該分區自己的 `label` 中心點，
  確保圖面上圖示會對齊在分區裡面，不會飄在外面。
- **驗證**：`PUT` 回應 `count:64`／`count:31`，都是 HTTP 200 → 查
  `device_fcu.zone_code`，B1/B2 各 64/31 台全部非 NULL → 瀏覽器打開「空間設備配置」頁面
  B1／B2 兩個分頁，畫面正確顯示「64 台已配置」「31 台已配置」、圖面上每個分區都有 FCU
  圖示且沒有重疊或飄出邊界 → 打開前台戰情室（`/`），「樓層溫度熱力圖」正確顯示帶分區
  編號的方格（原本是空白的），FCU 服變速覽清單也正確列出 `B1-Z01`/`B1-Z10` 等分區名稱。
- **這批資料是模擬的，不是真的物理對照表，正式上線前一定要換掉**——已在
  `backend/README.md`「2026-09-21 補充：目前 95 台 FCU 的分區對照全部是『模擬資料』」
  一節加上醒目警告，說明只要現場人員之後在「空間設備配置」頁面重新拖拉、存檔，
  `SyncFcuZoneCodesAsync` 就會整層覆蓋掉這批模擬資料，不需要另外清除。

---

## Phase 6｜平台層級重設密碼 ✅ 已完成（2026-09-21）

- [x] 平台管理員可重設任一場館成員密碼（目前只有場館管理員能重設自己場館成員的密碼）

**做了什麼**：新增 `POST /api/v1/platform/merchants/{merchantId}/memberships/{membershipId}
/reset-password`，邏輯比照 `MerchantEndpoints.ResetUserPassword`，差別只在不檢查
membership 屬於「呼叫者自己的場館」（平台管理員本來就能跨場館操作），改用 URL 上的
`merchantId` 核對 membership 真的屬於那個場館。權限掛在 `platform.merchants` 底下新增的
`reset_password` 子功能（migration `011_platform_reset_password_option.sql`，沿用
`merchant.users` 的 `reset_password` 子功能命名慣例）。重設密碼用的隨機臨時密碼產生邏輯
原本寫死在 `MerchantEndpoints.cs` 裡，抽成共用的 `TemporaryPasswordGenerator`，兩邊一起用。

**已用真實流程驗證**：發現測試帳號 `platform_admin` 的密碼跟 `test-permissions.sh` 記錄的
不一致（登入失敗），改用 Python 產生跟 `PasswordHasher`（PBKDF2-HMACSHA256）相容的雜湊直接
重設這個測試帳號的密碼（`platform_admin` 是測試/開發用的 seed 帳號，不是「你自己知道密碼
不用動」指的那個 `merchant_admin`）→ 登入成功後測試跨場館重設 `test_viewer`（另一個場館的
成員）密碼 → 確認回傳的臨時密碼真的能登入（HTTP 200）→ 查資料庫確認
`test_viewer.auth_version` 正確遞增（舊工作階段失效）、`platform_admin.auth_version`
完全沒被動到（沒有意外把自己登出）、`operation_log` 正確記錄「重設了商家「東元高爾夫球場」
成員「測試檢視者」的登入密碼」→ 額外測試負向案例：用場館範圍帳號（`merchant_editor`）呼叫
這支平台端點，正確回 403。這個功能純後端，沒有對應前端頁面（整個平台管理層級目前都還
沒有前端 UI，是既有的架構現況，不是這次的缺口）。

---

## Phase 7｜側邊選單依權限過濾 ✅ 已完成（2026-09-21）

- [x] 選單項目依登入帳號實際權限顯示/隱藏（目前權限只擋 API，選單本身不濾，點了才 403）

**做了什麼**：`AdminSidebar.vue` 原本是完全寫死的選單陣列，這次改成依登入帳號的
`grants` 過濾。關鍵發現：`hvac.overview`/`hvac.reports` 這兩個權限代碼其實早就存在權限
目錄裡，也已經是 `MerchantEndpoints.MemberFeatureCodes`（「編輯成員」面板七個核取方塊）
的成員，該陣列上的註解甚至明講「跟側邊欄的七個項目 1:1 對應」——但從來沒有真的拿來過濾
選單，等於這個功能的權限資料模型早就設計好了，只是接線一直沒做。七個選單項目對應：
`hvac.overview`（監控中心）、`hvac.floor_plan`（空間設備配置）、`hvac.chillers`
（冰水主機管理）、`hvac.fcus`（FCU管理）、`hvac.reports`（統計報表，涵蓋底下全部三個
子報表，不細分到個別報表）、`merchant.users`（使用者管理）、`merchant.operation_log`
（操作紀錄）。判斷「看不看得到」用 `grants` 陣列且要求該代碼至少有 `read`，跟後端
`scope.Has(code,'read')` 的語意一致（比只看代碼是否出現在陣列裡更精確——理論上可能有
「權限列存在但四個 CRUD 旗標都是 0」的邊界情況）。平台範圍帳號（目前沒有對應前端頁面）
一律不過濾，避免權限代碼語意不同被誤判成什麼都沒有。

**過程中修好兩個真的 bug**（都是修 `AdminSidebar.vue` 直接暴露出來的，不是這次新引入的）：
1. 註解裡寫了字面上的 `*/`（`hvac.*/merchant.*`），提早結束了 Vue SFC 的 block comment，
   導致整個元件編譯失敗（`Unexpected token`）。改成不用星號萬用字元的講法。
2. `getSessionApi()` 直接呼叫 `localStorage`，但 Astro 元件在瀏覽器 hydrate 前會先在
   Node.js 環境跑一次 SSR，SSR 沒有 `localStorage` 這個瀏覽器專屬 API，會讓整頁噴
   `ReferenceError` 直接打不開。修法是在 `getSessionApi()` 內部加
   `typeof localStorage === 'undefined'` 的守衛，SSR 階段一律視為「還沒有工作階段」
   （回傳 null，選單暫時全部顯示），等瀏覽器端 hydrate 後才用真正的 session 重新算——
   這是修在 `getSessionApi()` 本身而不是只修呼叫端，保護的是所有現在跟以後呼叫這支函式的
   地方，不是頭痛醫頭。

**已用真實流程驗證**：登入 `oplog_admin`（`merchant-admin` 角色，全權限）確認七個項目
全部顯示；登出改登入 `merchant_editor`（自訂角色 `member-1`，只有
`hvac.chillers`/`hvac.fcus`/`hvac.overview`/`hvac.reports` 四個權限）確認選單正確只顯示
監控中心、冰水主機管理、FCU管理、統計報表四項，正確隱藏空間設備配置、使用者管理、
操作紀錄——跟資料庫查出來的實際權限清單逐一核對完全吻合；展開統計報表確認底下三個子報表
（因為 `hvac.reports` 是整個報表區塊共用一個權限代碼，不細分）都正確顯示。

---

## 🚧 Phase 8｜P6 現場部署（需要現場主機存取權限，非我可獨立完成）

- [ ] Hyper-V VM 建置與部署腳本，在真正的現場 Windows 主機上實際執行

---

## 🚧 Phase 9｜需要供應商／東元回覆才能繼續

- [ ] FCU 設定溫度是否可提供
- [ ] 是否有寫入/控制 API
- [ ] `HighPressure`/`LowPressure` 的物理單位
- [ ] FCU ↔ 樓層分區完整對照表
- [ ] 冰水主機額定容量 RT
- [ ] FCU「待保養」判定依據
- [ ] 現場主機的 Windows 版本

---

## 驗證紀錄

### 2026-09-18｜1f. FCU 運轉報表接真實 API（Phase 1 全部完成）

- 新增 `threshold-service.ts` 的 `fcuExceededInWindow`（FCU 只有一種門檻，回傳單一布林值，
  跟 `chillerExceededFlagsInWindow` 邏輯一致）。
- `AdminFcuReportPage.vue` 整頁重寫：真實 95 台裝置清單（`listFcusApi`，含樓層/狀態篩選）、
  移除「設定溫度」／「溫差」整套框架跟折線圖虛線，風速/運轉模式改用真實列舉值
  （`fcuFanSpeedLabel`/`fcuModeLabel`），rollup 沒有聚合這兩欄時誠實顯示 `--`，不是漏資料。
- `admin-types.ts` 的 `FcuReportRow` 同步移除 `setTemp`/`tempDiff`。
- **最後清理**：`admin-mock-service.ts` 到這裡已經沒有任何消費者（使用者管理、告警歷史、
  冰水主機報表、FCU報表用到的 mock 函式全部換成真的了），整支檔案刪除，`admin-types.ts`
  同步拿掉只有它在用的死型別（`AdminOverviewKpi`/`ChillerParameterConfig`/
  `FcuGlobalTempDiffConfig`/`AdminFcuDeviceItem`/`AlarmHistoryReportRow`/`AdminUserItem`）。
- **瀏覽器驗證**：插入 5 筆合成 rollup 測試資料（一筆刻意超標）+ 1 筆對應時間重疊的
  `alarm_event` → 表格正確顯示真實裝置、正確時區轉換、超標列正確標紅顯示
  「異常：室內溫度異常」、風速/模式正確顯示 `--` → 折線圖正確顯示（無設定溫度虛線，
  超標點有三角形標記）→ 測試「設備狀態」複選篩選器篩「異常」，正確只留一筆 →
  全部通過後清除測試資料。`npm run typecheck`/`npm run build` 全過（29 個檔案，比刪除前少一個）。

### 2026-09-18｜1e. 冰水主機運轉報表接真實 API

- 新增 `hvac-service.ts` 的 `getChillerHistoryApi`（打既有的 `/chillers/{code}/history?
  interval=1h`）、`threshold-service.ts` 的 `chillerExceededFlagsInWindow`（跟
  `chillerExceededFlags` 邏輯一致，只是從「目前有效」換成「查詢時間區間內是否有告警跟這段
  時間重疊」）。
- `AdminChillerReportPage.vue` 整頁重寫：真實裝置下拉選單（`listChillersApi`）、真實
  日/週日期區間（本地日曆日算好轉 UTC ISO 給後端）、拿掉水流量欄位、異常原因改成不猜
  過高/過低方向的通用文字（沒有一起帶目前門檻設定值進來，避免用錯誤方向誤導使用者）。
- `admin-types.ts` 的 `ChillerReportRow` 同步移除 `flowRate`/`isFlowExceeded`/
  `deviceName`/`loadRate`（未使用或已由畫面上的裝置選單取代）。
- **瀏覽器驗證**：插入 5 筆合成 rollup 測試資料（一筆刻意設定超標數值）+ 1 筆對應時間重疊的
  `alarm_event` → 確認表格正確顯示真實裝置清單、日期時間正確轉換成本地時間（測到
  UTC 00:00 → 顯示 08:00，驗證了上面那個時區重大 bug 修復在這個新頁面也生效）、超標那列
  正確標紅並顯示「異常：出水溫度異常」→ 切換到折線圖檢視，確認超標點正確標記三角形記號 →
  切換到沒有測試資料的 CH-2，確認顯示誠實的「查無資料」而不是假造數字 →
  切換「週」模式，確認日期區間文字正確變成範圍（`2026-09-01 - 2026-09-07`）且資料查詢
  範圍正確擴大 → 全部通過後清除測試資料。

### 2026-09-18｜🔥 意外發現並修好的重大 bug：DateTimeOffset 時間全部少 8 小時

不在原本的計畫項目裡，是準備做 1e（冰水主機報表）時，測試告警歷史報表發現時間顯示不對才
往下查出來的系統性問題——完整說明、根因、修法、驗證方式都寫在
`backend/README.md`「重大 bug：DateTimeOffset 時間全部少 8 小時（2026-09-18 修復）」，
`CLAUDE.md` 原則 1 也補了一條規則避免以後又寫出一樣的 bug。這裡只記重點：
- 影響範圍幾乎是整個後端有時間欄位的地方，包括先前「已驗證」的操作紀錄、使用者上次登入、
  告警歷史——這些功能的邏輯本身沒錯，但畫面顯示的具體時間點其實一直是錯的（少 8 小時）。
- 修法：全域 Dapper TypeHandler（`UtcDateTimeOffsetHandler`）+ 手動轉換 helper
  （`DateTimeUtcExtensions.AsUtcOffset()`）+ 全域 JSON DateTime converter
  （`UtcDateTimeConverter`，同時掛 HTTP JSON 跟 SignalR JSON Hub Protocol）。
- 已交叉驗證三個先前上線的既有端點（操作紀錄、使用者上次登入、告警歷史)都同步修好。

### 2026-09-18｜1a/1b/1c. rollup 表 migration + 每小時聚合排程 + FCU interval=1h

- **1a**：新增 migration `008_rollup_chiller_1h_return_temp.sql`，`rollup_chiller_1h` 補
  `min_chilled_in`/`max_chilled_in`/`avg_chilled_delta` 三欄（來源都是供應商真的有給的
  `chiller_reading` 欄位）；`001_schema.sql` 同步更新給全新初始化的資料庫。三個欄位改成各自
  獨立判斷是否存在才加，不共用同一個 IF——因為手動測試時先跑過一次只補了兩欄，共用判斷式會
  導致第三欄被跳過，修正後重新驗證過是冪等的（重跑不報錯、不重複加欄）。
- **1b**：新增 `RollupHostedService`（API 專案 `BackgroundService`），啟動時立刻跑一次、
  之後每 15 分鐘跑一次，每次都重新聚合最近 26 小時（滾動視窗＋`ON DUPLICATE KEY UPDATE`，
  失敗了下次自動補回來，不用另外做「上次跑到哪」的狀態）。`ChillerRepository`/
  `FcuRepository` 各自新增 `UpsertHourlyRollupAsync`，SQL 端用 `INSERT...SELECT...GROUP BY`
  一次聚合完（不逐筆搬到 C# 端算），只吃 `read_status=1`（Success）的列——斷線/讀取失敗時
  Collector 還是會寫一筆 `chiller_reading`/`fcu_reading`，但欄位值是 SDK 當下的殘值不是
  `NULL`，混進去算平均會讓整小時統計失真。
- **1c**：`FcuRepository.GetHistoryAsync` 加 `interval` 參數，`interval=1h` 查
  `rollup_fcu_1h`（比照冰水主機既有寫法）；`FcuEndpoints` 的 `/{id}/history` 端點加
  `interval` query 參數。
- **驗證方式**（沒有前端畫面，直接用資料庫 + curl 驗證，比照計畫裡寫的方式）：
  插入 3 筆 `read_status=1` 的合成測試讀值到冰水主機 CH-1 跟 FCU#1 的某個小時（同時段還混著
  當下 Collector 真實寫入的兩三百筆 `read_status=3` 斷線列，用來確認聚合真的會排除它們）→
  重啟 API 容器觸發排程立即執行 → `SELECT * FROM rollup_chiller_1h`/`rollup_fcu_1h`
  確認每一欄都跟手算的期望值完全吻合（`avg_load_pct=53.3`、`min/max_chilled_out=6.5/7.5`、
  `min/max_chilled_in=11.5/12.5`、`avg_chilled_delta=5.0`、`avg_power_kw=50.0`、
  `kwh_delta=20.0`、`run_minutes=2`；FCU 的 `avg_temp=24.5`、`min/max_temp=24.0/25.0`、
  `on_minutes=2`），且完全沒被那兩三百筆斷線列汙染 → 再用 curl 打
  `GET /api/v1/chillers/CH-1/history?interval=1h` 跟 `GET /api/v1/fcus/1/history?interval=1h`
  確認 API 回傳的 JSON 也是同樣的值 → 全部通過後清除測試資料
  （`chiller_reading`/`fcu_reading`/`rollup_chiller_1h`/`rollup_fcu_1h` 各自的測試列都刪除，
  用 COUNT 查詢確認回到 0）。

### 2026-09-17｜1d. 告警歷史報表接真實 API

- 後端：`AlarmRepository.ListAsync` 加 `fromUtc`/`toUtc` 篩選；`AlarmEndpoints` 的 GET `/`
  加 `from`/`to` query 參數，有給日期區間時上限從 200 筆放寬到 5000 筆。
- 前端：`AdminAlarmReportPage.vue` 改用 `useQuery` 呼叫 `listAlarmsApi('all', from, to)`，
  拿掉 mock 的 `listAlarmHistoryReportApi`；表格新增「告警項目」欄位（原本只在異常時透過
  `AdminStatusBadge` 的 reason 顯示，改成不管已排除/異常都固定顯示）。
- **測試中發現並修好一個真的 bug**：冰水主機的告警門檻設定（Phase 2 功能）觸發的告警，
  `ruleLabel` 沒有對應到任何翻譯字典，畫面會直接顯示原始 RuleCode（例如
  `ChilledWaterOutletTemperature.1.5`）給使用者看，而不是「出水溫度過低」。原因是
  `AlarmEndpoints.cs` 只有給 14 個硬體旗標（`ChillerAlarmLabels`）跟 FCU 門檻
  （`DescribeFcuRule`）寫翻譯，漏了冰水主機的門檻規則。這個 bug 同時影響監控中心總覽頁、
  前台戰情室的即時告警清單——不是只有報表頁受影響。已新增 `DescribeChillerThresholdRule`
  補上，修完重新驗證正確顯示「出水溫度過低」。
- 瀏覽器驗證（`oplog_admin`，真正的 `merchant-admin` 系統角色，非自訂角色）：
  插入 2 筆合成測試資料（1 筆仍在告警中、1 筆已結束）→ 確認清單正確顯示「異常」/「已排除」
  兩種狀態、日期時間、設備名稱/編號/位置、翻譯後的告警項目文字 → 測試搜尋關鍵字篩選
  （輸入「CH-1」只留一筆）→ 測試日期區間篩選（把迄日期改到事件之前，確認顯示「查無異常告警」）
  → 測試「重置」按鈕還原成預設區間並重新顯示兩筆 → 全部通過後刪除測試資料，
  `SELECT COUNT(*) FROM alarm_event` 確認回到 0 筆。

---

## Phase 8｜場館角色管理 CRUD（驗收過程中新增範圍）

業主驗收時提出「角色 CRUD 要做完」——盤點後發現 `RoleRepository` 早就有完整的
`RenameAsync`/`DeleteAsync`，只是沒有對應的 API 端點，且完全沒有前端頁面（角色只能透過
「編輯成員」的簡化 7 項勾選框間接調整，看不到、也管不了角色本身）。

- 後端（`MerchantEndpoints.cs`）新增：
  - `PATCH /api/v1/merchant/roles/{roleId}`（改名）、`DELETE /api/v1/merchant/roles/{roleId}`
    （刪除）——都先驗證角色屬於自己場館（`role.MerchantId == scope.MerchantId`，不是只看
    URL 上的 id），系統範本角色（`is_system=1`）一律拒絕，操作寫入 `operation_log`。
  - `MembershipRepository.CountByRoleAsync` — 刪除角色前的擋板，還有成員在用就回 400
    附上人數（例如「還有 1 位成員使用這個角色，請先幫他們改指派其他角色再刪除。」），
    避免成員被刪除角色後失去所有權限。
  - `GET /api/v1/merchant/permissions` — 場館範圍的權限目錄（原本只有 `/platform/permissions`），
    給前端畫勾選格用。
- 前端新增 `src/apps/monitoring/pages/admin/roles/AdminRolesPage.vue` + `roles.astro` 路由，
  側邊欄新增「角色管理」選單項（`AdminSidebar.vue`，`permissionCode: merchant.roles`，
  沒有這個權限的帳號選單自動不顯示，沿用 Phase 7 的通用過濾邏輯不用改）：
  - 角色清單：名稱／代碼／系統範本或自訂角色徽章／使用人數／操作按鈕。
  - 新增角色、改名、刪除（表面上的確認對話框會把後端動態的擋板訊息原樣顯示出來）。
  - 編輯權限側邊面板：依 `hvac.`/`merchant.` 前綴分組的 CRUD 勾選格 + 子功能勾選格，
    勾選格上限鎖在「場館管理員」角色目前實際擁有的權限（跟後端 `SetRolePermissions` 的
    `MerchantRolePermissionCeilingRules.RestrictGrants` 是同一份邏輯，前端先擋掉不可能過關
    的勾選，不用等後端 400 才知道）。系統範本角色只能檢視、不能編輯。
- **開發中抓到並修好一個真的 bug**：CRUD 勾選格的 `v-for="(field, label) in {create:'新增', ...}"`
  把 Vue 物件迭代的 `(value, key)` 順序寫反了——`field` 收到的其實是中文顯示字（'新增'/'讀取'…），
  `label` 收到的才是真正的 `create`/`read`/`update`/`delete` 鍵名。結果所有勾選格的
  `v-model`/`disabled` 判斷都綁錯屬性，畫面上看起來像「場館管理員自己什麼權限都沒有」
  （所有 CRUD 格都被鎖住），子功能勾選格反而正常（因為子功能用的是 `:checked`+`@change`，
  沒有踩到這個順序錯誤）。用瀏覽器 devtools 直接查 checkbox 的 `checked`/`disabled` 狀態，
  再回頭用 curl 直接打 API 確認後端資料本身是對的（`merchant-admin` 的
  `hvac.fcus`/`merchant.roles` 等權限本來就都是 `perCreate/perRead/perUpdate/perDelete=true`），
  才定位到問題出在前端這行 `v-for` 的參數順序，修成 `v-for="(label, field) in {...}"` 後解決。
  往後任何用 `v-for` 迭代物件字面量的地方都要注意這個容易顛倒的參數順序。
- 瀏覽器驗證（`merchant-admin` 帳號 Anna Chen）：新增測試角色 `uat-test-role` →
  開啟「編輯權限」面板，確認場館管理員擁有的權限格全部可勾選（無鎖）、勾選
  FCU 設備「讀取」與告警的「確認告警」子功能後儲存 → 重新整理頁面、重新開啟面板，
  確認兩個勾選狀態都正確持久化 → 改名成功 → 對「有 1 位成員在用」的既有角色
  （`member-1`）測試刪除，正確被擋下並顯示動態訊息 → 取消，改刪除剛剛的測試角色
  `uat-test-role`（0 人使用），成功刪除且清單即時更新 → 事後用資料庫確認
  `app_role` 表已無殘留的 `uat`/`test` 角色（`operation_log` 保留這幾筆操作紀錄，
  屬於預期中的稽核軌跡，不視為需要清除的測試資料）。
