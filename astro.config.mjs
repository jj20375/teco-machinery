import { defineConfig } from 'astro/config';
import vue from '@astrojs/vue';
import tailwindcss from '@tailwindcss/vite';
import { fileURLToPath, URL } from 'node:url';

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
