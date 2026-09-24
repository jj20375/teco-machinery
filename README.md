# 東元電機智慧環境監控 (TECO Smart HVAC Environmental Monitoring)

本專案為東元電機工業環境監控與 HVAC 暖通空調節能戰情室系統。本檔案只說明前端（空間設備配置、
CAD 底圖產生流程）；完整代理人規則見 [`CLAUDE.md`](CLAUDE.md)，後端現況與已驗證項目見
[`backend/README.md`](backend/README.md)，整體規劃見 [`docs/BACKEND_INTEGRATION_PLAN.md`](docs/BACKEND_INTEGRATION_PLAN.md)。

## 空間設備配置（B1／B2）

- `/admin/floor-plan`：可切換 B1／B2 樓層頁籤；點選區域，選擇現有冰水主機或 FCU 加入，可拖曳或用方向鍵移動、旋轉、移除位置、儲存與匯出 JSON。切換樓層若有未儲存變更會先詢問確認。
- 底圖取自 CLUB-M 的 B1／B2 CAD 圖（見下節），2D SVG 與 Three.js 立體瀏覽共用同一份座標。兩層各有自己的底圖、建築外框與分區；B1／B2 的設備清單與儲存配置也各自獨立（chiller 目前僅歸屬 B1，B2 尚無主機資料）。
- 分區範圍是**設計端直接在 CAD 圖上畫的紅框**，紅框沒有框到的地方（B1 的大梯廳挑空、B2 的停車場車道）不是分區，設備放不進去。中文名稱尚未指定，目前顯示編號。

| 樓層 | 分區數 | 編號 | 說明 |
| --- | ---: | --- | --- |
| B1 | 72 | `B1-Z01`～`B1-Z72` | 編號依圖面位置自動給（由上而下、由左而右） |
| B2 | 41 | `B2-E01`…、`N`、`W`、`C`、`S` | 沿用設計稿紅框 id，字母代表方位（E東／N北／W西／C中／S南） |

- 正常設備為藍色，異常或待保養為紅色，離線或停止為灰色；設備資料與即時狀態來自真實後端 API
  （`GET /chillers`／`GET /fcus`／`GET /alarms`，登入後台走認證版、前台戰情室走
  `/api/v1/public/*` 公開版），不是 mock service。
- 配置已改由後端保存（`device_floor_placement` 表，`GET/PUT /api/v1/floor-plan/{floor}/placements`，
  2026-09-16），跨瀏覽器／跨裝置都會看到相同配置，不再只存在單一瀏覽器的 localStorage；儲存採
  整層覆寫＋樂觀鎖（`expectedVersion`），期間有別人存過檔會回 409。細節見
  [`backend/README.md`](backend/README.md)「平面圖配置改由後端保存」一節。
- ⚠️ 目前 95 台 FCU 的分區對照（`zone_code`）全部是驗收用的模擬對照表，不是真實物理位置，
  正式上線前必須由現場人員在這個頁面重新拖拉、存檔覆蓋掉，細節見 `backend/README.md` 同名章節。
- 檢查：`node tests/floor-plan.test.mjs`、`npm run typecheck`、`npm run build`。

## 底圖與分區（CAD 原圖）

兩層都是**兩個來源檔**：精簡版當底圖、紅框版當分區來源。兩檔的 viewBox 與座標一致，紅框直接就是分區座標，不需要對位。各層設定寫在 [`scripts/floor-plan/zone-plan.mjs`](scripts/floor-plan/zone-plan.mjs) 的 `FLOOR_PLANS`：

| 樓層 | 底圖（精簡版） | 分區（紅框版） | 紅框怎麼取 |
| --- | --- | --- | --- |
| B1 | `assets/cad/CLUB-M-B1.svg` | `assets/cad/CLUB-M-B1-zones.svg` | Illustrator 匯出，紅框和底圖混在同一層，篩 `class="st32"` |
| B2 | `assets/cad/CLUB-M-B2.svg` | `assets/cad/CLUB-M-B2-zones.svg` | PDF 轉出，紅框自己一個 `<g id="B2-red-areas">` |

- **底圖一定要用精簡版**：完整的設備配置圖線稿太密（B2 幾乎整層是停車場），平均亮度會逼近分區填色，分區會糊進背景看不清楚。精簡版把空調、大樑、排水等 MEP 圖層設為 `display:none`（id 以底線開頭），腳本只取沒有底線前綴的可見圖層。
- 底圖線稿的不透明度依樓層而定（B1 `.42`、B2 `.22`），設定在 `floor-plan.ts` 的 `BASE_OPACITY`：B2 的停車格斜線鋪滿整張圖，不壓低分區會讀不出來。線寬與字級兩層是一致的（實測都是 4.8px／0.69px）。
- 產生：`npm run extract:floor-plan`（只跑單層加 `-- --floor B2`），輸出 `public/floor-plans/<floor>-base.svg`（底圖，以 `<image>` 引用，勿 inline）、`src/apps/monitoring/floor-plan/<floor>-areas.generated.ts`（`floor-plan.ts` 由此組出每層的 `planOf(floor)`）、[`docs/floor-plan-extract-report.md`](docs/floor-plan-extract-report.md)（兩層的分區清單、面積、被濾掉的框）。
- **要調整分區就回去改來源圖的紅框再重跑**；要補中文名稱請改 `FLOOR_PLANS.<floor>.names`。兩者都不要手改產生出來的 `*-areas.generated.ts`。
- 過濾條件依樓層而定：B1 濾掉面積小於 4 ㎡ 或短邊小於 12 圖面單位的紅框（那種框連一台 FCU 標記都放不下），被濾掉的列在報告裡；**B2 不過濾**，因為 41 個框都是設計端逐一畫好並編號的，濾掉會讓編號跳號。B2 有 3 個框（`B2-N07`、`B2-W06`、`B2-S11`）小到放不下 FCU 標記，保留但放不進設備。
- 產生腳本會檢查分區互相重疊、頂點超出建築外框、多邊形自相交、編號重複，有問題會在報告的「警告」欄列出。
- 比例尺：B1 以 B1019 訓練打擊空間 328 ㎡ 校準，約 1 ㎡ ≈ 79.2 圖面單位²。B2 圖面是同一棟建築但匯出縮放不同（線性約 1.334 倍），以兩張圖的建築外框面積比回推為 1 ㎡ ≈ 140.9 單位²；設備標記尺寸也依這個比例放大，兩層看起來才一樣大。`sqm` 欄位僅供判讀分區大小，**非施工尺寸**。
- 分區編號對照圖：[`docs/b1-zone-reference.png`](docs/b1-zone-reference.png)（B1）。
- 換版計畫與各階段完成狀態見 [`docs/superpowers/plans/2026-09-10-b1-cad-floor-plan-migration.md`](docs/superpowers/plans/2026-09-10-b1-cad-floor-plan-migration.md)。

## 專案規格文件

- **後端現況與已驗證項目（權威來源）**：[`backend/README.md`](backend/README.md)
- **整合規劃全文（權威來源）**：[`docs/BACKEND_INTEGRATION_PLAN.md`](docs/BACKEND_INTEGRATION_PLAN.md)
- Figma 初期規格書（**多處已過時，僅供追溯原始設計意圖**，內容如與上述兩份文件衝突以上述為準）：
  [`docs/TECO_HVAC_SPECIFICATION.md`](docs/TECO_HVAC_SPECIFICATION.md)
  - **前台戰情室 (1920x1080 Fixed Dashboard)**：冰水主機卡片、FCU 監控、樓層輪播、告警輪播與狀態對照。
  - **後台管理系統 (Console)**：溫度設定＆超標警示（獨立事件/全場 FCU 共用溫差）、運算公式規範 (`formula-spec`)、4 大報表引擎（日/週報表/折線圖/空狀態）、設備台帳、使用者管理與密碼重設。
  - **Figma 決策對照表**：完整收錄 11 筆歷史需求變更與決策點。
