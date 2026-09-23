/**
 * @file query-client.ts
 * 東元電機智慧環境監控 - 全站共用 TanStack Query Client
 *
 * TECO 目前只有一個 app（monitoring），不像美達特官網那種多個獨立 apps
 * （cms / anesthesia-app / public-site）各自需要一份，所以這裡只建立單一份
 * 全域共用的 QueryClient，由 vue-app.ts 透過 VueQueryPlugin 注入給每個 Vue island。
 *
 * retry 策略比照美達特 apps/cms/query-client.ts：4xx（授權/輸入問題）重送不會
 * 改善，只有網路錯誤與 5xx 允許重試一次。
 */
import { QueryClient } from '@tanstack/vue-query';
import { ApiError } from '@/apps/monitoring/pages/admin/_services/auth-service';

function shouldRetry(failureCount: number, error: unknown): boolean {
  if (failureCount >= 1) return false;
  return !(error instanceof ApiError) || error.status >= 500;
}

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: shouldRetry,
      refetchOnWindowFocus: false,
    },
    mutations: {
      retry: false,
    },
  },
});
