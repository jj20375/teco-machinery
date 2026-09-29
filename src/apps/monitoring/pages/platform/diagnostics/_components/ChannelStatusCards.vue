<script setup lang="ts">
import LevelBadge from './LevelBadge.vue';
import {
  CONNECTION_STATE_LABEL, READ_STATUS_LABEL, formatAgo, formatDateTime,
  type ChannelStatus, type CollectorStatus, type IngestStatus,
} from '../../_services/diagnostics-service';

defineProps<{ channels: ChannelStatus[]; collector: CollectorStatus; ingest: IngestStatus; nowMs: number }>();
</script>

<template>
  <div class="grid grid-cols-1 lg:grid-cols-2 xl:grid-cols-4 gap-4">
    <div class="rounded-lg border border-[#E2E8F0] p-4 flex flex-col gap-2">
      <div class="flex items-center justify-between gap-2">
        <span class="font-bold text-[#1A202C]">Collector 程序</span>
        <LevelBadge :level="collector.level" />
      </div>
      <div class="text-[13px] text-[#4A5568]">{{ collector.detail }}</div>
      <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-[12px] text-[#64748B] mt-1">
        <dt>最後推送</dt><dd class="text-[#334155]">{{ formatAgo(ingest.lastIngestAtUtc, nowMs) }}</dd>
        <dt>累計筆數</dt><dd class="text-[#334155]">{{ ingest.ingestCountSinceApiStart }}（API 啟動後）</dd>
      </dl>
    </div>

    <div v-for="c in channels" :key="c.channel" class="rounded-lg border border-[#E2E8F0] p-4 flex flex-col gap-2">
      <div class="flex items-center justify-between gap-2">
        <span class="font-bold text-[#1A202C]">{{ c.name }}</span>
        <LevelBadge :level="c.level" />
      </div>
      <div class="font-mono text-[13px] text-[#334155]">{{ c.ip ?? '—' }}:{{ c.port ?? '—' }}</div>
      <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-[12px] text-[#64748B]">
        <dt>連線狀態</dt>
        <dd class="text-[#334155]">{{ c.connectionState === null ? '尚未回報' : CONNECTION_STATE_LABEL[c.connectionState] ?? c.connectionState }}</dd>
        <dt>讀取狀態</dt>
        <dd class="text-[#334155]">{{ c.readStatus === null ? '—' : READ_STATUS_LABEL[c.readStatus] ?? c.readStatus }}</dd>
        <dt>最後成功讀取</dt>
        <dd class="text-[#334155]" :title="formatDateTime(c.lastSuccessAtUtc)">{{ formatAgo(c.lastSuccessAtUtc, nowMs) }}</dd>
        <dt>最後狀態變化</dt>
        <dd class="text-[#334155]">{{ formatDateTime(c.lastConnectionChangeAtUtc) }}</dd>
      </dl>
    </div>
  </div>
</template>
