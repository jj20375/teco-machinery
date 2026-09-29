<script setup lang="ts">
import LevelBadge from './LevelBadge.vue';
import { formatAgo, formatDateTime, type JobReport, type PersistenceStatus } from '../../_services/diagnostics-service';

defineProps<{ jobs: JobReport[]; persistence: PersistenceStatus | null; nowMs: number }>();

function interval(seconds: number): string {
  return seconds >= 3600 ? `每 ${seconds / 3600} 小時` : `每 ${seconds / 60} 分鐘`;
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <table class="w-full text-left text-[13px] text-[#334155]">
      <thead class="text-[#64748B] border-b border-[#E2E8F0]">
        <tr>
          <th class="py-2 pr-3 font-medium">排程</th>
          <th class="py-2 pr-3 font-medium">頻率</th>
          <th class="py-2 pr-3 font-medium">上次執行</th>
          <th class="py-2 pr-3 font-medium">結果</th>
          <th class="py-2 pr-3 font-medium">下次預計</th>
          <th class="py-2 font-medium">資料庫證據</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="j in jobs" :key="j.key" class="border-b border-[#F1F5F9] align-top">
          <td class="py-2 pr-3">
            <div class="font-medium">{{ j.name }}</div>
            <div v-if="j.lastRun?.summary" class="text-[12px] text-[#64748B]">{{ j.lastRun.summary }}</div>
            <div v-if="j.lastRun?.error" class="text-[12px] text-[#FF4757] break-all">{{ j.lastRun.error }}</div>
          </td>
          <td class="py-2 pr-3 whitespace-nowrap">{{ interval(j.intervalSeconds) }}</td>
          <td class="py-2 pr-3 whitespace-nowrap" :title="formatDateTime(j.lastRun?.startedAtUtc)">
            {{ j.lastRun ? formatAgo(j.lastRun.startedAtUtc, nowMs) : 'API 啟動後尚未執行' }}
          </td>
          <td class="py-2 pr-3"><LevelBadge :level="j.level" /></td>
          <td class="py-2 pr-3 whitespace-nowrap">{{ formatDateTime(j.nextRunAtUtc) }}</td>
          <td class="py-2 text-[12px]">
            <template v-if="j.evidenceLabel">{{ j.evidenceLabel }}：{{ formatDateTime(j.evidenceUtc) }}</template>
            <template v-else>—</template>
          </td>
        </tr>
      </tbody>
    </table>

    <div v-if="persistence">
      <div class="text-[13px] font-bold text-[#1A202C] mb-2">
        資料庫落地（只算讀取成功的資料，最近 {{ persistence.recentWindowMinutes }} 分鐘）
      </div>
      <div v-if="persistence.error" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ persistence.error }}</div>
      <div v-else class="grid grid-cols-1 md:grid-cols-2 gap-3 text-[13px]">
        <div class="rounded-lg bg-[#F8FAFC] p-3">
          <div class="font-medium">chiller_reading</div>
          <div class="text-[#4A5568]">寫入 {{ persistence.chillerRecentRows }} 筆，涵蓋 {{ persistence.chillerRecentDevices }}/{{ persistence.chillerActiveDevices }} 台</div>
          <div class="text-[12px] text-[#64748B]">最新一筆：{{ formatDateTime(persistence.chillerLatestTsUtc) }}（{{ formatAgo(persistence.chillerLatestTsUtc, nowMs) }}）</div>
          <div class="text-[12px] text-[#64748B]">最新聚合整點：{{ formatDateTime(persistence.rollupChillerLatestBucketUtc) }}</div>
        </div>
        <div class="rounded-lg bg-[#F8FAFC] p-3">
          <div class="font-medium">fcu_reading</div>
          <div class="text-[#4A5568]">寫入 {{ persistence.fcuRecentRows }} 筆，涵蓋 {{ persistence.fcuRecentDevices }}/{{ persistence.fcuActiveDevices }} 台</div>
          <div class="text-[12px] text-[#64748B]">最新一筆：{{ formatDateTime(persistence.fcuLatestTsUtc) }}（{{ formatAgo(persistence.fcuLatestTsUtc, nowMs) }}）</div>
          <div class="text-[12px] text-[#64748B]">最新聚合整點：{{ formatDateTime(persistence.rollupFcuLatestBucketUtc) }}</div>
        </div>
      </div>
    </div>
  </div>
</template>
