<script setup lang="ts">
import LevelBadge from './LevelBadge.vue';
import { READ_STATUS_LABEL, type ChillerFieldReport } from '../../_services/diagnostics-service';

defineProps<{ chillers: ChillerFieldReport[] }>();

function range(min: number | null, max: number | null): string {
  if (min === null && max === null) return '—';
  if (max === null) return `≥ ${min}`;
  if (min === null) return `≤ ${max}`;
  return `${min} ~ ${max}`;
}

function fmt(value: number | null): string {
  if (value === null) return '—';
  return Number.isInteger(value) ? String(value) : value.toFixed(2);
}
</script>

<template>
  <div v-if="chillers.length === 0" class="text-sm text-[#94A3B8]">尚未收到冰水主機資料。</div>
  <div v-else class="grid grid-cols-1 xl:grid-cols-2 gap-4">
    <div v-for="c in chillers" :key="c.modbusId" class="rounded-lg border border-[#E2E8F0] overflow-hidden">
      <div class="px-4 py-2 bg-[#F8FAFC] border-b border-[#E2E8F0] flex items-center gap-2 flex-wrap">
        <span class="font-bold">{{ c.name ?? '未登記主機' }}</span>
        <span class="text-[12px] text-[#64748B]">{{ c.code ?? '—' }}／ModbusId {{ c.modbusId }}</span>
        <span class="text-[12px] text-[#64748B]">{{ READ_STATUS_LABEL[c.readStatus] ?? c.readStatus }}</span>
        <span class="ml-auto"><LevelBadge :level="c.level" /></span>
      </div>
      <table class="w-full text-left text-[12px] text-[#334155]">
        <thead class="text-[#64748B]">
          <tr class="border-b border-[#F1F5F9]">
            <th class="px-4 py-1.5 font-medium">欄位</th>
            <th class="px-2 py-1.5 font-medium text-right">目前值</th>
            <th class="px-2 py-1.5 font-medium text-right">上一筆</th>
            <th class="px-2 py-1.5 font-medium">暫定範圍</th>
            <th class="px-4 py-1.5 font-medium">判定</th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="f in c.fields" :key="f.key"
            :class="['border-b border-[#F8FAFC]', f.level === 'warn' ? 'bg-[#FFFBEB]' : '']"
          >
            <td class="px-4 py-1.5">{{ f.label }} <span class="text-[#94A3B8]">{{ f.unit }}</span></td>
            <td :class="['px-2 py-1.5 text-right font-mono', f.level === 'warn' ? 'text-[#D97706] font-bold' : '']">{{ fmt(f.value) }}</td>
            <td class="px-2 py-1.5 text-right font-mono text-[#94A3B8]">{{ fmt(f.previousValue) }}</td>
            <td class="px-2 py-1.5 text-[#64748B]">{{ range(f.min, f.max) }}</td>
            <td class="px-4 py-1.5">
              <LevelBadge :level="f.level" />
              <span v-if="f.note" class="ml-1 text-[#D97706]">{{ f.note }}</span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
