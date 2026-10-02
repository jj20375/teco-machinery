<script setup lang="ts">
import { ref, computed } from 'vue';
import StatusPill from './StatusPill.vue';
import DeviceThumbnail from './DeviceThumbnail.vue';
import type { FloorId, FcuItem, DashboardOverview } from '../_types/dashboard-types';

const props = defineProps<{
  overview: DashboardOverview | null;
  fcuByFloor: Record<FloorId, FcuItem[]>;
}>();

const DOT_COLOR: Record<string, string> = {
  RUNNING: '#5EEAD4', ABNORMAL: '#F87171', OFFLINE: '#3B5BDB', STOPPED: '#9CA3AF', MAINTENANCE: '#FBBF24',
};
const FLOORS: FloorId[] = ['B1', 'B2'];

function tally(list: FcuItem[]) {
  const t = { running: 0, abnormal: 0, offline: 0, stopped: 0 };
  for (const f of list) {
    if (f.status === 'RUNNING') t.running += 1;
    else if (f.status === 'ABNORMAL') t.abnormal += 1;
    else if (f.status === 'OFFLINE') t.offline += 1;
    else t.stopped += 1;
  }
  return t;
}

const allFcus = computed(() => [...props.fcuByFloor.B1, ...props.fcuByFloor.B2]);
const total = computed(() => allFcus.value.length || props.overview?.totalFcu || 90);
const overall = computed(() => tally(allFcus.value));
const runRate = computed(() => (total.value ? Math.round((overall.value.running / total.value) * 100) : props.overview?.overallRunRate ?? 67));

// 設備總覽分頁
const PAGE_SIZE = 9;
const page = ref(0);
const pageCount = computed(() => Math.max(1, Math.ceil(allFcus.value.length / PAGE_SIZE)));
const pageRows = computed(() => allFcus.value.slice(page.value * PAGE_SIZE, page.value * PAGE_SIZE + PAGE_SIZE));
function go(p: number) { page.value = (p + pageCount.value) % pageCount.value; }

function reading(f: FcuItem): string {
  return Number.isFinite(f.roomTemp) ? `${f.roomTemp.toFixed(1)}°C` : '--';
}
function readingColor(f: FcuItem): string {
  if (f.status === 'ABNORMAL') return 'text-[#FF4D4F]';
  if (f.status === 'RUNNING') return 'text-[#5EEAD4]';
  return 'text-[#8B949E]';
}
</script>

<template>
  <div class="flex flex-col gap-4 h-full min-h-0">
    <!-- FCU 即時運轉狀態 -->
    <div class="bg-[#161B22] border border-[#30363D] rounded-xl px-4 py-3 shrink-0">
      <div class="flex items-start justify-between">
        <h2 class="text-[17px] font-bold text-[#F0F6FC] flex items-center gap-2">
          <svg class="w-4 h-4 text-[#00D1B2]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <rect x="3" y="3" width="18" height="18" rx="2" stroke-width="1.8" /><path stroke-width="1.8" d="M3 9h18M3 15h18M9 3v18M15 3v18" />
          </svg>
          FCU 即時運轉狀態
        </h2>
        <div class="relative w-[62px] h-[62px] grid place-items-center shrink-0">
          <svg class="w-full h-full -rotate-90" viewBox="0 0 36 36">
            <path class="text-[#30363D]" stroke-width="3.5" stroke="currentColor" fill="none"
              d="M18 2.0845 a 15.9155 15.9155 0 0 1 0 31.831 a 15.9155 15.9155 0 0 1 0 -31.831" />
            <path :stroke-dasharray="`${runRate}, 100`" stroke-linecap="round" stroke-width="3.5" stroke="#5EEAD4" fill="none"
              d="M18 2.0845 a 15.9155 15.9155 0 0 1 0 31.831 a 15.9155 15.9155 0 0 1 0 -31.831" />
          </svg>
          <div class="absolute inset-0 flex flex-col items-center justify-center leading-none">
            <span class="text-sm font-extrabold font-tabular text-[#F0F6FC]">{{ runRate }}%</span>
            <span class="text-[9px] text-[#8B949E] mt-0.5">運轉率</span>
          </div>
        </div>
      </div>

      <p class="text-sm text-[#8B949E] mt-2">
        總數量 <b class="text-[#F0F6FC] font-tabular">{{ total }}</b> 台
      </p>
      <div class="flex flex-wrap gap-x-4 gap-y-1 text-sm text-[#C9D1D9] mt-1">
        <span class="flex items-center gap-1.5"><i class="w-2.5 h-2.5 rounded-[2px] bg-[#5EEAD4]" />運轉 {{ overall.running }}</span>
        <span class="flex items-center gap-1.5"><i class="w-2.5 h-2.5 rounded-[2px] bg-[#F87171]" />異常 {{ overall.abnormal }}</span>
        <span class="flex items-center gap-1.5"><i class="w-2.5 h-2.5 rounded-[2px] bg-[#3B5BDB]" />離線 {{ overall.offline }}</span>
        <span class="flex items-center gap-1.5"><i class="w-2.5 h-2.5 rounded-[2px] bg-[#9CA3AF]" />停止 {{ overall.stopped }}</span>
      </div>

      <!-- 分樓層點陣 -->
      <div v-for="floor in FLOORS" :key="floor" class="mt-2.5 pt-2.5 border-t border-[#21262D]">
        <p class="text-sm font-bold text-[#F0F6FC]">
          {{ floor }} 樓層 <span class="text-[#8B949E] font-normal">({{ fcuByFloor[floor].length }}台)</span>
        </p>
        <p class="text-xs text-[#8B949E] mt-0.5 mb-2">
          <template v-for="(v, k, idx) in tally(fcuByFloor[floor])" :key="k">
            <span v-if="idx" class="mx-1.5 text-[#30363D]">｜</span>{{ ({ running: '運轉', abnormal: '異常', offline: '離線', stopped: '停止' } as Record<string, string>)[k] }} {{ v }}台
          </template>
        </p>
        <div class="grid gap-[3px]" style="grid-template-columns: repeat(15, 1fr);">
          <span
            v-for="f in fcuByFloor[floor]"
            :key="f.id"
            class="aspect-square rounded-[2px]"
            :style="{ background: DOT_COLOR[f.status] ?? '#9CA3AF' }"
            :title="`${f.code} · ${reading(f)}`"
          />
        </div>
      </div>
    </div>

    <!-- FCU 設備總覽 -->
    <div class="bg-[#161B22] border border-[#30363D] rounded-xl p-4 flex flex-col flex-1 min-h-0">
      <div class="flex items-center justify-between shrink-0">
        <h2 class="text-base font-bold text-[#F0F6FC]">FCU 設備總覽</h2>
        <span class="text-sm text-[#8B949E]">共 {{ allFcus.length }} 台</span>
      </div>

      <div class="flex-1 min-h-0 mt-2 divide-y divide-[#21262D] overflow-hidden">
        <div v-for="f in pageRows" :key="f.id" class="flex items-center gap-3 py-[7px]">
          <DeviceThumbnail kind="fcu" :size="36" />
          <span class="font-bold text-[#F0F6FC] font-tabular w-[92px] shrink-0">{{ f.code }}</span>
          <StatusPill :status="f.status" class="shrink-0" />
          <div class="ml-auto text-center leading-tight">
            <div class="text-[10px] text-[#8B949E]">室溫</div>
            <div class="text-sm font-bold font-tabular" :class="readingColor(f)">{{ reading(f) }}</div>
          </div>
        </div>
      </div>

      <div class="flex items-center justify-center gap-3 pt-2 shrink-0">
        <button type="button" aria-label="上一頁" class="text-[#8B949E] hover:text-white text-xs" @click="go(page - 1)">‹</button>
        <div class="flex gap-1.5">
          <span
            v-for="p in pageCount" :key="p"
            class="w-1.5 h-1.5 rounded-full transition-colors"
            :class="p - 1 === page ? 'bg-[#00D1B2]' : 'bg-[#30363D]'"
          />
        </div>
        <button type="button" aria-label="下一頁" class="text-[#8B949E] hover:text-white text-xs" @click="go(page + 1)">›</button>
        <span class="text-[11px] text-[#8B949E] font-tabular ml-1">{{ page + 1 }} / {{ pageCount }}</span>
      </div>
    </div>
  </div>
</template>
