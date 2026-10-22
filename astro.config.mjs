import { defineConfig } from 'astro/config';
import vue from '@astrojs/vue';
import tailwindcss from '@tailwindcss/vite';
import { fileURLToPath, URL } from 'node:url';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

// 樓層底圖放在 public/，檔名不會帶 hash，伺服器也沒有快取指示，瀏覽器會一直沿用舊圖。
// 網址加上兩張底圖的內容雜湊（floor-plan.ts 的 baseImage），底圖一改網址就變，所有人自動抓新圖。
// 2026-10-22 修過的真實問題：圖面旋轉 180° 部署後，設備位置是新的、底圖卻是快取的舊圖，整張對不上。
const floorPlanVersion = createHash('sha256')
  .update(readFileSync(fileURLToPath(new URL('./public/floor-plans/b1-base.svg', import.meta.url))))
  .update(readFileSync(fileURLToPath(new URL('./public/floor-plans/b2-base.svg', import.meta.url))))
  .digest('hex')
  .slice(0, 10);

export default defineConfig({
  srcDir: './src',
  outDir: './dist',
  integrations: [
    vue({
      appEntrypoint: './src/entry/vue-app.ts',
    }),
  ],
  vite: {
    plugins: [
      tailwindcss(),
    ],
    define: {
      __FLOOR_PLAN_VERSION__: JSON.stringify(floorPlanVersion),
    },
    resolve: {
      alias: {
        '@': fileURLToPath(new URL('./src', import.meta.url)),
      },
    },
    // three 只被 FloorPlan3D.vue 用到，而那支是動態 import 的（3D 檢視才載）。
    // 不預先宣告的話，dev server 會等到使用者第一次點開 3D 才發現這個新依賴，
    // 當場重新預打包 → 既有模組 URL 全部失效（504 Outdated Optimize Dep）→
    // 那個當下的動態 import 直接 reject，3D 就變成一片空白。
    // 列進 include 讓它在 server 啟動時就打包好，這個時序問題就不會發生。
    optimizeDeps: {
      include: ['three', 'three/addons/controls/OrbitControls.js'],
    },
    // astro dev 沒有經過 Caddy 反代，相對路徑 /api/v1/... 打不到後端；
    // 開發時代理到本機 docker compose 開的 api 服務（127.0.0.1:8082）。
    server: {
      proxy: {
        '/api': 'http://127.0.0.1:8082',
      },
    },
  },
});
