import type { App } from 'vue';
import { VueQueryPlugin } from '@tanstack/vue-query';
import { queryClient } from './query-client';

/**
 * Vue island 共用初始化。
 * 註：先前全域註冊 ant-design-vue 但專案未使用任何 <a-*> 元件，
 * 其 reset.css 以未分層的 `button { color: inherit }` 覆蓋 Tailwind 文字色工具，
 * 造成按鈕文字顏色失效，故移除。
 *
 * Astro 的每個 island 都是獨立的 Vue app 實例（不是單一 SPA），但 appEntrypoint
 * 會對每一個實例都跑一次，所以在這裡裝 VueQueryPlugin、傳入同一個 queryClient
 * 單例，就能讓所有 island 共用同一份快取——例如冰水主機清單如果同時被總覽頁跟
 * 報表頁用到，不會各自重打一次 API。
 */
export default (app: App) => {
  app.use(VueQueryPlugin, { queryClient });
};
