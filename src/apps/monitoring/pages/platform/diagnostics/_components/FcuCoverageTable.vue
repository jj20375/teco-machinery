<script setup lang="ts">
import { ref, watch } from 'vue';
import LevelBadge from './LevelBadge.vue';
import {
  getRawSnapshotApi, FCU_FAN_LABEL, FCU_MODE_LABEL, FCU_SWITCH_LABEL, READ_STATUS_LABEL,
  type FcuCoverage, type RawSnapshot,
} from '../../_services/diagnostics-service';

const props = defineProps<{ coverage: FcuCoverage[]; refreshKey: string }>();

// 原始讀值 95 台一次列出來很長，預設收起；展開後才打 /raw，並跟著主報告一起更新。
const rawOpen = ref(false);
const raw = ref<RawSnapshot | null>(null);
const rawError = ref('');

async function loadRaw() {
  try {
    raw.value = await getRawSnapshotApi();
    rawError.value = '';
  } catch (err) {
    rawError.value = err instanceof Error ? err.message : '讀取原始資料失敗';
  }
}

watch(rawOpen, (open) => { if (open) loadRaw(); });
watch(() => props.refreshKey, () => { if (rawOpen.value) loadRaw(); });
</script>

<template>
  <div class="flex flex-col gap-4">
    <div v-if="coverage.length === 0" class="text-sm text-[#94A3B8]">尚未收到 DDC 資料。</div>
    <div v-else class="grid grid-cols-1 xl:grid-cols-2 gap-4">
      <div v-for="c in coverage" :key="c.channel" class="rounded-lg border border-[#E2E8F0] p-4 flex flex-col gap-3">
        <div class="flex items-center justify-between">
          <span class="font-bold">{{ c.name }}</span>
          <LevelBadge :level="c.level" />
        </div>
        <div class="grid grid-cols-3 gap-2 text-center">
          <div class="rounded bg-[#F8FAFC] py-2">
            <div class="text-[20px] font-bold">{{ c.received }}</div>
            <div class="text-[12px] text-[#64748B]">現場回報台數</div>
          </div>
          <div class="rounded bg-[#F8FAFC] py-2">
            <div class="text-[20px] font-bold">{{ c.mapped }}</div>
            <div class="text-[12px] text-[#64748B]">資料庫登記台數</div>
          </div>
          <div class="rounded bg-[#F8FAFC] py-2">
            <div class="text-[20px] font-bold">{{ c.matched }}</div>
            <div class="text-[12px] text-[#64748B]">兩邊對得上</div>
          </div>
        </div>
        <div class="text-[12px] text-[#64748B]">
          疑似沒回應 {{ c.unresponsive }} 台；溫度超出範圍 {{ c.temperatureOutOfRange }} 台
        </div>
        <div v-if="c.unmappedKeys.length" class="text-[12px]">
          <div class="font-bold text-[#D97706]">現場有回報、資料庫沒有對照（不會寫入資料庫）：</div>
          <div class="text-[#4A5568] break-all">{{ c.unmappedKeys.join('、') }}</div>
        </div>
        <div v-if="c.missingKeys.length" class="text-[12px]">
          <div class="font-bold text-[#D97706]">資料庫有登記、現場沒有回報：</div>
          <div class="text-[#4A5568] break-all">{{ c.missingKeys.join('、') }}</div>
        </div>
      </div>
    </div>

    <div>
      <button class="text-[13px] text-[#0066CC] font-medium hover:underline" @click="rawOpen = !rawOpen">
        {{ rawOpen ? '▾ 收起 FCU 原始讀值' : '▸ 展開 FCU 原始讀值（逐台）' }}
      </button>
      <div v-if="rawOpen" class="mt-3">
        <div v-if="rawError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ rawError }}</div>
        <div v-else-if="!raw" class="text-sm text-[#94A3B8]">尚未收到任何 Collector 資料。</div>
        <div v-else class="grid grid-cols-1 xl:grid-cols-2 gap-4">
          <div v-for="ddc in [raw.ddc1, raw.ddc2]" :key="ddc.channel" class="rounded-lg border border-[#E2E8F0] overflow-hidden">
            <div class="px-4 py-2 bg-[#F8FAFC] border-b border-[#E2E8F0] text-[13px] font-bold">
              DDC{{ ddc.channel }}：{{ READ_STATUS_LABEL[ddc.readStatus] ?? ddc.readStatus }}，共 {{ ddc.fcuList.length }} 台
            </div>
            <div class="max-h-[360px] overflow-y-auto">
              <table class="w-full text-left text-[12px] text-[#334155]">
                <thead class="text-[#64748B] sticky top-0 bg-white">
                  <tr class="border-b border-[#F1F5F9]">
                    <th class="px-3 py-1.5 font-medium">ID</th>
                    <th class="px-2 py-1.5 font-medium">站號／位置</th>
                    <th class="px-2 py-1.5 font-medium">開關</th>
                    <th class="px-2 py-1.5 font-medium">模式</th>
                    <th class="px-2 py-1.5 font-medium">風速</th>
                    <th class="px-3 py-1.5 font-medium text-right">溫度 °C</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="f in ddc.fcuList" :key="`${f.stationId}-${f.position}`" class="border-b border-[#F8FAFC]">
                    <td class="px-3 py-1 font-mono">{{ f.id }}</td>
                    <td class="px-2 py-1">{{ f.stationId }}／{{ f.position }}</td>
                    <td class="px-2 py-1">{{ FCU_SWITCH_LABEL[f.switchStatus] ?? f.switchStatus }}</td>
                    <td class="px-2 py-1">{{ FCU_MODE_LABEL[f.mode] ?? f.mode }}</td>
                    <td class="px-2 py-1">{{ FCU_FAN_LABEL[f.fanSpeed] ?? f.fanSpeed }}</td>
                    <td class="px-3 py-1 text-right font-mono">{{ f.temperature.toFixed(1) }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
