<script setup lang="ts">
/**
 * @file PlatformDiagnosticsPage.vue
 * 平台管理 - 系統診斷（/platform/diagnostics）
 * 現場 IoT 接通驗證用：一頁看完「通道有沒有連上、數據內容合不合理、有沒有寫進資料庫、
 * 排程有沒有在跑」。所有判斷都在後端 DiagnosticsService 做完，這裡只負責顯示與輪詢。
 * 操作流程與症狀對照見 docs/IOT_現場接通驗證手冊.md。
 */
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { useQuery } from '@tanstack/vue-query';
import PlatformLayout from '../_components/PlatformLayout.vue';
import DiagnosticsSection from './_components/DiagnosticsSection.vue';
import LevelBadge from './_components/LevelBadge.vue';
import ChannelStatusCards from './_components/ChannelStatusCards.vue';
import DataCheckList from './_components/DataCheckList.vue';
import ChillerFieldTable from './_components/ChillerFieldTable.vue';
import FcuCoverageTable from './_components/FcuCoverageTable.vue';
import JobStatusTable from './_components/JobStatusTable.vue';
import ConnectionHistoryList from './_components/ConnectionHistoryList.vue';
import { ApiError } from '../../admin/_services/auth-service';
import { getDiagnosticsApi, formatDateTime, type DiagnosticLevel } from '../_services/diagnostics-service';

const POLL_MS = 5000;
const autoRefresh = ref(true);

const query = useQuery({
  queryKey: ['platform-diagnostics'],
  queryFn: getDiagnosticsApi,
  refetchInterval: () => (autoRefresh.value ? POLL_MS : false),
});
const report = computed(() => query.data.value);

const loadError = computed(() => {
  const err = query.error.value;
  if (!err) return '';
  if (err instanceof ApiError && err.status === 403) return '目前登入的帳號沒有「系統診斷」權限（platform.diagnostics）。';
  return err instanceof Error ? err.message : '載入診斷資料失敗，請稍後再試。';
});

// 「N 秒前」要每秒跳動，但不需要每秒打 API。只在瀏覽器掛載後才啟動計時器（SSR 沒有 window）。
const nowMs = ref(0);
let timer: ReturnType<typeof setInterval> | undefined;
onMounted(() => {
  nowMs.value = Date.now();
  timer = setInterval(() => { nowMs.value = Date.now(); }, 1000);
});
onBeforeUnmount(() => { if (timer) clearInterval(timer); });

const OVERALL_TEXT: Record<DiagnosticLevel, string> = {
  ok: '全部正常：設備已連上、數據在合理範圍、資料庫與排程都有在動。',
  warn: '大致運作中，但有項目需要人工確認（見下方黃色項目）。',
  error: '有項目異常，依下方紅色項目逐一排查。',
  unknown: '資料不足，暫時無法判斷。',
};

const checksByCategory = computed(() => {
  const checks = report.value?.checks ?? [];
  const counts = { ok: 0, warn: 0, error: 0, unknown: 0 } as Record<DiagnosticLevel, number>;
  for (const c of checks) counts[c.level]++;
  return counts;
});
</script>

<template>
  <PlatformLayout page-title="系統診斷" current-path="/platform/diagnostics">
    <div class="flex flex-col gap-4">
      <div v-if="loadError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ loadError }}</div>
      <div v-else-if="!report" class="p-6 text-center text-sm text-[#94A3B8] bg-white rounded-xl border border-[#E2E8F0]">載入中…</div>

      <template v-else>
        <div class="bg-white rounded-xl border border-[#E2E8F0] px-5 py-4 flex items-center gap-4 flex-wrap">
          <LevelBadge :level="report.overall" />
          <span class="text-[15px] font-bold text-[#1A202C]">{{ OVERALL_TEXT[report.overall] }}</span>
          <span class="text-[12px] text-[#64748B]">
            正常 {{ checksByCategory.ok }}／需確認 {{ checksByCategory.warn }}／異常 {{ checksByCategory.error }}／無法判斷 {{ checksByCategory.unknown }}
          </span>
          <div class="ml-auto flex items-center gap-3 text-[12px] text-[#64748B]">
            <span>更新時間 {{ formatDateTime(report.generatedAtUtc) }}</span>
            <label class="flex items-center gap-1 cursor-pointer">
              <input v-model="autoRefresh" type="checkbox" class="accent-[#00D1B2]" />
              每 5 秒自動更新
            </label>
            <button class="text-[#0066CC] font-medium hover:underline" @click="query.refetch()">立即更新</button>
          </div>
        </div>

        <DiagnosticsSection title="通道連線" hint="三條實體通道的 IP、連線與讀取狀態">
          <ChannelStatusCards :channels="report.channels" :collector="report.collector" :ingest="report.ingest" :now-ms="nowMs" />
        </DiagnosticsSection>

        <DiagnosticsSection title="檢查結果" hint="推送、時間、數據內容、資料庫寫入逐項判定">
          <DataCheckList :checks="report.checks" />
        </DiagnosticsSection>

        <DiagnosticsSection title="冰水主機逐欄數據" hint="範圍是暫定值，現場要跟設備面板讀數核對">
          <ChillerFieldTable :chillers="report.chillers" />
        </DiagnosticsSection>

        <DiagnosticsSection title="FCU 台數比對" hint="現場回報 vs 資料庫登記（device_fcu）">
          <FcuCoverageTable :coverage="report.fcuCoverage" :refresh-key="report.generatedAtUtc" />
        </DiagnosticsSection>

        <DiagnosticsSection title="排程與資料庫落地" hint="排程紀錄存在 API 記憶體，重啟後會清空；資料庫證據不受影響">
          <JobStatusTable :jobs="report.jobs" :persistence="report.persistence" :now-ms="nowMs" />
        </DiagnosticsSection>

        <DiagnosticsSection title="連線變化紀錄" hint="channel_health 最近 30 筆">
          <ConnectionHistoryList :items="report.connectionHistory" />
        </DiagnosticsSection>
      </template>
    </div>
  </PlatformLayout>
</template>
