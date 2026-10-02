# 東元電機智慧環境監控 — 代理人規則

本檔案是這個 repo 的專案層級規則。修改程式碼前，先確認 [backend/README.md](backend/README.md)（後端現況與已驗證項目，只講「現在長什麼樣子」）、[backend/CHANGELOG.md](backend/CHANGELOG.md)（完整開發歷程，按日期記錄每次功能新增/bug 修復的細節與驗證過程）與 [docs/BACKEND_INTEGRATION_PLAN.md](docs/BACKEND_INTEGRATION_PLAN.md)（完整規劃）。

> 本檔案的部分規則是從 `義享樂漾叫號系統與電視牆` 專案的 `AGENTS.md` / `.agent/rules/`
> 篩選並改寫而來，只保留跟本專案架構相容的部分——例如原專案是 EF Core + 雙後端遷移，
> 這些跟本專案（單一 Dapper 後端、單一 MariaDB、單一場館為主）衝突的部分**沒有**照搬，
> 而是改寫成本專案自己的版本。

## 專案邊界

- 根目錄：Astro 5 + Vue 3 islands 前台/後台（`teco-smart-hvac-monitoring`），單一 app，沒有 CMS／public-site 之分。
- `backend/`：ASP.NET Core 10 modular 後端，`Teco.Hvac.{Domain,Infrastructure,Api,Collector,Contracts}`。
- `backend/deploy/`：docker compose（project name `teco-iot-area`）、Caddyfile、MariaDB init script。
- `backend/tools/probe/`：P0 技術驗證用的獨立探針，不是正式服務的一部分。

## 不可違反的原則

1. **資料存取一律用 Dapper + 參數化 SQL，這是既定架構決策，不是例外。**
   因為 Pomelo 的 EF Core MySQL provider 目前只支援到 EF Core 9.x，跟 .NET 10/EF Core 10 不相容，
   所以本專案不用 EF Core。這點跟義享樂漾「業務邏輯禁止寫原生 SQL、一律 EF Core／LINQ」
   剛好相反，**不要把那條規則套用到這個 repo**。
   - 一律用 Dapper 的參數化查詢（`@paramName`），**禁止字串拼接 SQL**（SQL injection 風險），這點跟來源專案的精神一致，只是換了工具。
   - Dapper 的 record-mapping 遇到原生 ADO.NET 型別（`uint`/`sbyte`/`DateTime`）會失敗，
     這類 row 型別要用 `class` + `init` 屬性，不要用 positional `record`（見既有的
     `MerchantUserRow`、`RolePermissionDetail`、`ChillerReadingRow`、`FcuReadingRow`）。
   - 真正的原生 SQL（非 Dapper 參數化查詢）僅限 migration DDL（`deploy/mariadb/init/*.sql`）。
   - **Row 類別的時間欄位絕對不要直接宣告成 `DateTimeOffset`，一律用 `DateTime`，需要
     `DateTimeOffset` 時用 `DateTimeUtcExtensions.AsUtcOffset()` 手動轉。** 這是 2026-09-18
     修過的真實 bug：Dapper 對 `DateTime→DateTimeOffset` 沒有自訂 TypeHandler 時，會用 C#
     內建轉換套用**執行環境的系統時區**（API 容器是 `TZ=Asia/Taipei`），把明明是 UTC 的值
     貼上錯的 `+08:00` offset，等於把時間點整整搬移 8 小時。已經註冊全域
     `UtcDateTimeOffsetHandler` 防住既有的直接對應寫法，但新寫的 Repository 程式碼還是不要
     依賴這個全域修法当唯一防線——遇到「先讀 DateTime 再手動指派給 DateTimeOffset 屬性」的
     寫法（例如 `AlarmRepository.ListAsync`），全域 handler 攔不到，一定要手動
     `.AsUtcOffset()`。詳見 `backend/CHANGELOG.md`「重大 bug：DateTimeOffset 時間全部少 8 小時」。

2. **權限範圍一律用已驗證 JWT 算出的 scope，不信任前端傳入的 id。**
   - 後端：`RequestScope` 讀 JWT claims 取得 `UserId`／`MerchantId`／`scopeKind`／permissions；
     merchant-scope 的查詢要同時檢查 `MerchantId`，不能只靠 URL 上的 id（例如編輯 membership 前
     要先確認 `membership.MerchantId == scope.MerchantId`，否則回 404）。
   - 前端：checkbox／選單這類 UI 只是顯示優化，真正的安全邊界永遠是後端 JWT + policy；
     不要因為前端隱藏了某個按鈕就假設可以少做後端檢查。
   - 修改別人角色/權限時，AuthVersion 只 increment 被影響的那個使用者，不要 increment 整個
     merchant（這是先前踩過的真實 bug：改別人權限時把自己也順便登出了）。
   - **前台（`/`）是不用登入的公開頁面，後台（`/admin/*`）才需要登入**——兩者是完全不同的
     安全模型，不要預設所有頁面都有 JWT。前台要用到即時監控資料時，打的是
     `/api/v1/public/*`（`PublicEndpoints.cs`，無 `RequireAuthorization()`），不是後台用的認證端點；
     這組公開端點只能放不涉及使用者/密碼/操作紀錄等機敏資訊的唯讀資料，也不提供任何寫入能力。
     這是先前踩過的真實 bug：直接讓前台打認證端點，導致沒登入的訪客被 401 一路彈回登入頁，
     整個公開大屏直接壞掉。

3. **前端：頁面專屬的元件/服務就近存放，不散落在全域目錄。**
   （沿用先前專案驗證過的 co-located 慣例，本專案已經照這個結構在做）
   - 頁面元件：`src/apps/monitoring/pages/<module>/_components/`
   - 頁面 API 服務：`src/apps/monitoring/pages/<module>/_services/<module>-service.ts`
   - 底線前綴（`_components`／`_services`）是為了避免 Astro 把資料夾誤判成路由。
   - 只有跨頁共用的元件才放到 `pages/admin/_components/` 以外的共用位置。

4. **所有對外打 API 的 service 函式一律以 `Api` 結尾。**
   例如 `loginApi`、`listMerchantUsersApi`、`resetMerchantUserPasswordApi`。這是既有慣例，
   讓呼叫端一眼就能分辨這是不是網路請求。

5. **Astro dev 環境要打真後端時，走 `astro.config.mjs` 的 vite proxy，不要在程式碼裡寫死 base URL。**
   正式部署（docker compose 的 `web` 服務）靠 Caddy 反代同源，相對路徑 `/api/v1/...` 就能打到；
   本機 `astro dev` 沒有 Caddy，所以另外設了 `server.proxy['/api'] → http://127.0.0.1:8082`。
   - **Vue 元件在瀏覽器 hydrate 前，Astro 會先在 Node.js 跑一次 SSR**，即使該元件是
     `client:load`。SSR 環境沒有 `localStorage`/`window`/`document` 這些瀏覽器專屬 API，
     元件的 `<script setup>` 頂層程式碼（包括立即求值的 `computed`）如果直接呼叫這些 API
     會讓整頁在 SSR 階段就噴 `ReferenceError`，不是只有互動時才出錯。這是 2026-09-21 修過的
     真實 bug（`AdminSidebar.vue` 加選單權限過濾時踩到）：`getSessionApi()` 現在會先判斷
     `typeof localStorage === 'undefined'`，SSR 階段回傳 `null`（等 hydrate 後用真正的
     session 重新算），新寫的程式碼如果要在元件頂層存取這類瀏覽器 API，一律要先加這種守衛，
     不要假設「只要不是在 `onMounted` 裡就一定在瀏覽器執行」。
   - **`v-for` 迭代物件字面量時，參數順序是 `(value, key)`，不是 `(key, value)`。**
     這是 2026-09-21 修過的真實 bug（`AdminRolesPage.vue` 的角色權限勾選格）：寫成
     `v-for="(field, label) in { create: '新增', read: '讀取' }"` 時，`field` 拿到的其實是
     `'新增'`/`'讀取'` 這些顯示字，`label` 才是 `create`/`read` 這些鍵名——順序寫反了。
     如果拿反的變數又被拿去當 `v-model`/`:disabled` 的判斷依據，畫面會整組看起來像資料錯誤
     （例如「權限全部被鎖住」），但其實資料是對的，是模板參數命名順序反了。日後任何
     `v-for="(a, b) in someObject"` 都要先確認 `a` 是值、`b` 才是鍵，不要憑變數名稱直覺猜。

6. **註解只寫「為什麼」，不要每行機械補註解。**
   （**不採用**義享樂漾「每個宣告都要中文註解、不可有無註解的裸代碼」那條——那條規則是為了它自己的多 AI 工具協作情境設計的，套到這裡只會製造雜訊。）
   - 只在隱藏限制、不明顯的業務決策、繞過某個 bug 的 workaround、會讓人意外的行為時才寫註解。
   - 如果拿掉這行註解不會讓後面的人看不懂，就不要寫。

7. **單一程式碼檔案原則上不超過 1000 行**，接近上限時優先評估拆分（component/service/repository/helper），不要用註解或 region 掩蓋職責過多的問題。

8. **API／DTO／資料表／環境變數變更時，同步更新文件。**
   - 後端結構或已驗證項目變化 → 更新 `backend/README.md`。
   - 規劃/決策變化（例如異常判定基準、資料保留策略）→ 更新 `docs/BACKEND_INTEGRATION_PLAN.md`。
   - 不要只改程式碼卻讓文件停在舊的架構決策。

9. **Git commit 用 Conventional Commits，subject 用繁體中文，一次只做一件事。**
   格式 `type(scope): 描述`（`feat`/`fix`/`docs`/`refactor`/`test`/`chore`），描述要講清楚實際
   結果，不要寫「更新一些東西」。commit message 不放密碼、token、連線字串。

10. **語言與命名：文件、程式碼註解、UI 文案一律繁體中文；API、類別、資料表、環境變數等技術名稱維持原文不翻譯。**

## 已知限制（目前刻意不處理，避免規則跟現況不符）

- **全部後台頁面跟前台戰情室都已接真實 API，沒有任何頁面還在打 mock service**
  （含兩張每小時趨勢圖、告警門檻設定、三個報表頁）。`admin-mock-service.ts` 已整支刪除。
- **告警門檻設定（`/api/v1/thresholds/*`）已完整驗證讀寫路徑**（見 `backend/CHANGELOG.md`
  「告警門檻設定接真實 API」一節）；驗證過程中抓到並修好一個真的 bug：清空數字欄位時
  `v-model.number` 給的是空字串不是 `null`，會讓後端 400——已在 `threshold-service.ts` 加
  `nullifyEmpty()` 修正，日後任何新增的門檻／數值設定表單都要注意這個 Vue 行為。
- **新增到權限目錄的權限不會自動出現在既有自訂角色上**：`hvac.alarms`/`hvac.thresholds`/
  `hvac.floor_plan` 都是後續才加入的，migration 只更新系統範本角色（`merchant-admin`/`editor`/
  `viewer`），場館自己複製出來的自訂角色不會自動取得，需要場館管理員自己到角色管理頁勾選。
- **場館角色管理（新增/改名/刪除/調整權限）已完整接真實 API 並通過瀏覽器驗證**（2026-09-21，
  見 `backend/CHANGELOG.md`「場館角色管理 CRUD 接真實 API」一節）。
- **平台管理層級（`/platform/*`）前端已於 2026-09-24 補上**（場館管理、系統帳號、平台角色管理
  三個頁面＋登入後的雙軌身分切換畫面），見 `backend/CHANGELOG.md`「平台管理前端＋雙軌身分切換」
  一節。平台角色目前**沒有**改名/刪除功能（後端本來就沒有對應端點，只有場館角色管理才有），
  不要以為是前端漏做。
- ⚠️ **目前 93 台 FCU 的 `device_fcu.zone_code` 全部是驗收用的模擬對照表，不是真實物理位置**
  （B1 64 台、B2 29 台；B2 另有 2 台因為分區太小放不下而沒有配置，見下一條）
  （2026-09-21，見 `backend/CHANGELOG.md` 同日期的補充說明）——正式上線前必須由現場人員在
  「空間設備配置」頁面重新拖拉、存檔覆蓋掉，不能讓這批模擬資料留到交機。
- **在「空間設備配置」頁面用 API 直接塞座標資料時，一定要跑過前端自己的驗證**
  （`floor-plan.ts` 的 `canPlace`/`serializeLayout`）——這是 2026-09-21 修過的真實 bug：
  模擬 FCU 對照表時用分區中心點當座標，沒檢查最小間距，導致 B2 有兩對分區（過小、彼此
  太近）的設備違反前端的最小間距規則，使用者之後在該樓層做任何編輯、按「儲存配置」都會
  在送出前的驗證階段整層失敗，且完全沒有網路請求可查、只有一個容易被忽略的 toast，
  外觀上跟「按鈕壞了」一模一樣。塞完資料後務必開這個分頁做一次移動設備再存檔的操作，
  確認真的能存，不要只看 API 回 200。細節見 `backend/CHANGELOG.md` 對應章節。
- **設備有「客戶自訂代碼」與「系統編號」兩組識別，兩者並存、不可互相覆蓋**（2026-09-22）：
  客戶自訂的那組存在 `display_name`（FCU 可留空＝未設定，冰水主機是 NOT NULL 不可留空），
  用 `PATCH /api/v1/fcus/{id}`／`PATCH /api/v1/chillers/{id}` 修改；系統這組
  （FCU 的 `zone_code` 與 `(channel, station_id, position)`、冰水主機的 `code`／`modbus_id`）
  是實體接線與 Modbus 位址決定的，**任何情況下都不開放後台修改**。「空間設備配置」頁面
  的清單／詳情面板（新增設備下拉選單、設備詳情、此區域設備清單）一律兩欄並存，沒設定時
  顯示「未設定」而不是退回顯示系統編號（否則兩欄一樣，使用者看不出哪欄可改）。
  **圖面標籤（2D／3D 設備 marker 上的文字、hover title、aria-label）則是自訂代碼優先，
  沒設定才退回顯示系統編號**——這條跟清單面板的「不退回」規則刻意不同：圖面 marker
  空間有限、又要讓人一眼認出現場裝置，所以允許 fallback（見
  `FloorPlanCanvas.vue` 的 `deviceLabel()`、`FloorPlan3D.vue` 的 `equipment?.name ?? equipment?.code`）。
  另外還有第三組「**供應商編號**」（例如 `FC_MC1_01`，說明書表 19 的命名規則），現場技術人員與供應商溝通時用的是這組；
  它在 DDC1、DDC2 之間會重複，**一律搭配 DDC 顯示**（`fcuVendorLabel()` →「DDC1 · FC_MC1_01」），不可單獨拿來當識別。
  這條規則只套用到「空間設備配置」（`/admin/floor-plan`）；`FloorPlanViewer.vue`
  （監控中心熱區圖、前台戰情室共用）目前仍只顯示系統編號，尚未套用這個規則。
  細節見 `backend/CHANGELOG.md`「設備自訂代碼／名稱」一節。
- **數值範圍表單（上限/下限）一律用 `yup` + `vee-validate` 做防呆驗證，不要手刻**
  （2026-09-21）：告警門檻設定原本前後端都沒檢查「下限不能大於上限」，DB 裡曾經存進
  `0.6 ~ 0.5` 這種語意錯誤但兩個數字各自合法的資料，畫面上也沒有任何提示。已建立共用的
  `src/apps/monitoring/pages/admin/_services/threshold-validation.ts`（yup schema，掛在
  「上限」欄位上的 `max-gte-min` test，兩側都有值才比較），`AdminChillerPage.vue`／
  `AdminFcuPage.vue` 改用 `vee-validate` 的 `useForm`/`defineField`/`handleSubmit`，
  `meta.dirty`/`meta.valid` 取代手寫的 dirty-check。日後任何新的「最低 ~ 最高」數值表單
  都比照這個模式，不要每頁重新手刻一套比大小的邏輯。細節見 `backend/CHANGELOG.md`
  「告警門檻設定補上前端防呆驗證」一節。
- **`backend/deploy/` 有 4 支 demo 用腳本**：`seed-demo-data.sh`/`clear-demo-data.sh`
  （歷史報表資料）、`simulate-live-data.sh`/`stop-live-simulation.sh`（監控中心／前台
  戰情室的即時卡片）。**手動測試 `/internal/ingest/*` 之前，一定要先 `docker compose
  stop collector`**——這支容器就算連不到現場設備也不會自己停，會一直重試並誠實回報
  「未連線」，跟任何手動塞進去的假「已連線」狀態互相打架，看起來像隨機發生的怪 bug
  （細節見 `backend/CHANGELOG.md`「花了很多時間才抓到的真相」一節）。
- **交機前清測試資料用 `backend/deploy/go-live-cleanup.sh`，不要手動 TRUNCATE**（2026-10-02）：預設只預覽、`--apply` 才動手，
  動手前先備份並要輸入確認字，只清資料不動設定（帳號、角色、設備、圖面配置、告警門檻）。**先清、再接現場設備**，順序與完整流程見
  `docs/現場交機清單.md`。這支腳本會對「最近 15 分鐘還有新讀值」的資料庫警告，那代表已經在收真實資料，清掉就回不來。
- **前台是不用登入的公開頁面**，打的是 `/api/v1/public/*`（`PublicEndpoints.cs`），不是後台用的
  認證端點——新增涉及即時監控資料的功能時，要意識到有兩組平行端點（認證版 + 公開版），
  公開版沒有 JWT 檢查，只能放不涉及使用者/密碼/操作紀錄等機敏資訊的唯讀資料。
- **平台「系統診斷」頁（`/platform/diagnostics`）是現場 IoT 接通驗證的主要工具**（2026-09-29），
  只開放給 platform scope＋`platform.diagnostics:read`，不要搬到場館後台或 `/api/v1/public/*`
  （含通道 IP、原始讀值等維運資訊）。數據合理範圍集中寫死在
  `backend/src/Teco.Hvac.Api/Services/Diagnostics/DataValidationRules.cs`，**是暫定值**，
  現場拿到真實讀數後要校正；新增判斷規則一律加在後端，前端只負責顯示。
  時序表的統計查詢要記得只算 `read_status = 1`——2026-09-29 起 Collector 讀取不成功時已不寫入，
  但更早的舊資料裡仍有 0 值的 Disconnected 列。
  操作流程見 `docs/IOT_現場接通驗證手冊.md`。
- P6（Hyper-V VM 現場部署）的腳本寫好了但沒有在真正的現場主機上跑過，因為沒有那台主機的存取權限。
- **場館成員管理有層級保護，新增任何「對別人帳號動手」的功能都要套用 `MerchantMembershipGuard`**（2026-10-02）：
  場館擁有者（`merchant_membership.is_owner`，第一位管理員）不能被刪除、停用、改角色；其他 `merchant-admin` 只有擁有者能動。
  只擋「刪除」不夠——能重設密碼、改角色、停用就能接管或癱瘓帳號，所以這幾條路都要擋。前端只是讓按鈕點不到，
  真正的防線在後端，且要先用 `membership.MerchantId == scope.MerchantId` 確認屬於自己的場館。
  平台建立場館的第一位管理員時自動成為擁有者（`PlatformEndpoints.CreateMerchantMembership`）。
- **角色清單查詢一律要排除 `member-%` 個人專屬角色**（`WHERE code NOT LIKE 'member-%'`）：
  「編輯成員」六個核取方塊面板會自動幫每個成員建立一個 `Code = "member-{membershipId}"`、
  `Name = "自訂權限"` 的專屬角色，這是內部實作細節，不該出現在給人挑選的一般角色下拉選單裡。
  2026-09-23 修過的真實 bug：`RoleRepository.ListAsync` 漏了這條 WHERE，導致多個成員都設定過
  個人權限後，新增使用者的角色選單出現好幾筆同名「自訂權限」。日後任何新寫的角色清單查詢
  都要記得加這條過濾，不要只複製舊的 `ListAsync` 就以為已經處理過。

## 驗證最低要求

- 後端變更：`dotnet build backend/Teco.Hvac.slnx`；有跑得動的環境時額外用
  `backend/deploy/test-permissions.sh` 驗證權限機制、`docker compose ps` 確認全部 healthy。
  **API／Collector 實際跑在 `backend/deploy/compose.yaml` 的 docker 容器裡，不是本機
  `dotnet run`**——`dotnet build` 只驗證編譯得過，容器裡的既有 image 不會自動套用新程式碼，
  要瀏覽器驗證或呼叫真的 API 之前必須先 `docker compose up -d --build <service>` 重新建置、
  重啟容器，否則會出現「明明改了程式碼，行為卻還是舊的」的假象。
- 前端變更：`npm run typecheck`（`astro check`）與 `npm run build`。
- Docker/deploy 相關變更：`docker compose config` 確認語法，並實際 `up` 後看 healthcheck。
- 涉及使用者可操作流程（登入、權限、密碼）的改動，用瀏覽器實際跑一次，不能只靠 build/typecheck 過關就回報完成。

## 明確不採用的來源規則（附原因，避免以後又誤套）

- ~~業務查詢禁止原生 SQL、一律 EF Core/LINQ~~ — 本專案用 Dapper，見上方原則 1。
- ~~每個宣告都要有中文語意註解，不可有無註解裸代碼~~ — 本專案只採用「只註解不明顯決策」那個版本，見原則 6。
- ~~多租戶雙資料庫（Platform DB／Customer DB 分離）~~ — 本專案單一 MariaDB，資料規模與租戶數不需要這種切分。
- ~~Node/ASP 雙後端相容路由、SSE/SignalR 語意統一 adapter~~ — 本專案只有一個 ASP.NET Core 後端，沒有遷移期雙軌問題。
- ~~CMS／public-site 分離、multi-site single-port 部署~~ — 本專案是單一場館的內部監控系統，不是多站台 CMS，不需要這套路由策略。
