# B1 Floor Plan Implementation Plan

**Goal:** 將已確認的簡化 B1 空間配置接入現有 Astro / Vue 系統。

**Architecture:** 區域多邊形、設備座標與驗證集中在 floor-plan.ts。SVG 和 Three.js 使用相同資料；後台編輯草稿，前台只讀取儲存版本。

**Tech Stack:** 現有 Vue 3、Astro、TypeScript、原生 SVG / Pointer Events / localStorage，新增使用者指定的 Three.js。

## Global Constraints

- 不繪製施工細節，不推測空間用途或設備實際位置。
- B2 缺圖須明示；本機儲存和模擬資料須明示。
- 沿用現有後台版型與設備 API，不新增後端或拖曳套件。

## Tasks

- [x] `src/apps/monitoring/floor-plan/floor-plan.ts`：B1 多邊形、設備清單、座標驗證與版本化儲存資料。`tests/floor-plan.test.mjs` 使用現有 esbuild 和 Node assert 檢查圖外/灰區/重複 ID/損壞資料/合法往返。
- [x] `FloorPlanCanvas.vue`：SVG 區域、標籤和設備，固定 viewBox；以 getScreenCTM().inverse() 換算 Pointer Events。方向鍵操作與點選區域放置作為非拖曳操作。
- [x] `FloorPlan3D.vue`：同一組多邊形產生簡單量體，同一組點位呈現設備；按需載入、ResizeObserver、卸載釋放資源及 WebGL 失敗提示。
- [x] `AdminFloorPlanPage.vue` 和 `src/pages/admin/floor-plan.astro`：現有設備選擇、區域選擇、放置/移動/旋轉/移除、儲存、髒資料提醒、錯誤處理；在 AdminSidebar 加入連結。
- [x] `FloorPlanViewer.vue`：讀取儲存資料與設備清單；storage / 自訂事件同步。替換 FloorHeatmapSection 與 AdminHeatmapCard 的示意圖，保留既有告警和統計。
- [x] 區域熱力：在 `floor-plan.ts` 集中判定區域狀態；2D SVG 使用區域遮罩與模糊光暈，3D 以自發光材質同步。異常優先紅光，正常運轉為藍光，離線／停止不發光。
- [x] 執行 `node tests/floor-plan.test.mjs`、`npm run typecheck`、`npm run build`；透過瀏覽器驗證配置流程、重載持久化與三維視圖。更新 README 說明入口和本機限制。

## 驗證結果

- Node 檢查通過：所有分區位於外框內、分區中心可配置 FCU、灰區禁止放置、設備重疊與儲存資料驗證、正常藍色與異常紅色。
- 瀏覽器驗證：點選 D05 加入冰水主機、點選 B01 加入正常 FCU、方向鍵移動、滑鼠拖曳、旋轉、儲存、重載保留位置、前台跨頁同步、後台總覽、B2 缺圖提示與 Three.js 立體顯示。
- 測試點位已從圖面移除並儲存，設備台帳保留；交付空白底圖供正式配置。
- 型別檢查與正式建置通過；建置仍有大型資源提示，Three.js 已獨立為按需載入的區塊。
