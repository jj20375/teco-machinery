<script setup lang="ts">
import {
  CONNECTION_STATE_LABEL, READ_STATUS_LABEL, formatDateTime, type ConnectionHistoryItem,
} from '../../_services/diagnostics-service';

defineProps<{ items: ConnectionHistoryItem[] }>();
</script>

<template>
  <div v-if="items.length === 0" class="text-sm text-[#94A3B8]">channel_health 目前沒有任何紀錄。</div>
  <div v-else class="max-h-[320px] overflow-y-auto">
    <table class="w-full text-left text-[13px] text-[#334155]">
      <thead class="text-[#64748B] border-b border-[#E2E8F0] sticky top-0 bg-white">
        <tr>
          <th class="py-2 pr-3 font-medium">時間</th>
          <th class="py-2 pr-3 font-medium">通道</th>
          <th class="py-2 pr-3 font-medium">連線狀態</th>
          <th class="py-2 font-medium">當時讀取狀態</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(h, i) in items" :key="i" class="border-b border-[#F1F5F9]">
          <td class="py-1.5 pr-3 whitespace-nowrap">{{ formatDateTime(h.changedAtUtc) }}</td>
          <td class="py-1.5 pr-3">{{ h.name }}</td>
          <td :class="['py-1.5 pr-3', h.connectionState === 2 ? 'text-[#00A38C]' : 'text-[#FF4757]']">
            {{ CONNECTION_STATE_LABEL[h.connectionState] ?? h.connectionState }}
          </td>
          <td class="py-1.5">{{ READ_STATUS_LABEL[h.readStatus] ?? h.readStatus }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
