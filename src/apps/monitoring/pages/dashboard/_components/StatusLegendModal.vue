<script setup lang="ts">
import UiModal from '@/ui/components/UiModal.vue';
import UiBadge from '@/ui/components/UiBadge.vue';
import UiButton from '@/ui/components/UiButton.vue';

defineProps<{
  open: boolean;
}>();

const emit = defineEmits<{
  (e: 'update:open', val: boolean): void;
  (e: 'close'): void;
}>();

const STATUS_ITEMS = [
  {
    type: 'RUNNING',
    name: '運轉中 (Running)',
    desc: '設備正常通電啟動，各項量測值均在安全門檻內。',
    device: '冰水主機 / FCU',
  },
  {
    type: 'STOPPED',
    name: '停止 (Stopped)',
    desc: '設備手動關閉或處於排程停機狀態。',
    device: '冰水主機 / FCU',
  },
  {
    type: 'ABNORMAL',
    name: '異常 (Abnormal)',
    desc: '溫度差、回水溫度或水流量超出後台設定門檻。',
    device: '冰水主機 / FCU',
  },
  {
    type: 'OFFLINE',
    name: '離線 (Offline)',
    desc: '通訊訊號中斷，感測器超過時限未回報資料。',
    device: '冰水主機 / FCU',
  },
  {
    type: 'MAINTENANCE',
    name: '待保養 (Maintenance)',
    desc: '累積運轉時數達標或系統排程定期保養。',
    device: '冰水主機',
  },
  {
    type: 'TEMP_DIFF_HIGH',
    name: '溫差過高',
    desc: '出回水溫差或室內外溫差大於設定上限值。',
    device: '冰水主機 / FCU',
  },
  {
    type: 'TEMP_DIFF_LOW',
    name: '溫差過低',
    desc: '熱交換效率不足，溫差小於設定下限值。',
    device: '冰水主機',
  },
  {
    type: 'FLOW_ABNORMAL',
    name: '流量異常',
    desc: '冰水管路流量高於上限或低於下限閥值。',
    device: '冰水主機',
  },
];
</script>

<template>
  <UiModal
    :open="open"
    width="720px"
    @update:open="(val) => emit('update:open', val)"
    @close="emit('close')"
  >
    <template #title>
      <div class="flex items-center gap-2">
        <svg class="w-5 h-5 text-teal-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
        </svg>
        <h3 class="text-base font-bold text-[#F0F6FC]">
          系統設備 8 大狀態對照標準 (Status Matrix)
        </h3>
      </div>
    </template>

    <div class="flex flex-col gap-3">
      <p class="text-xs text-[#8B949E]">
        依照東元電機智慧環境監控規格手冊，全廠區設備採用以下 8 種標準狀態燈號與異常判定標準：
      </p>

      <div class="divide-y divide-[#21262D] border border-[#30363D] rounded-lg overflow-hidden bg-[#0D1117]/60">
        <div
          v-for="item in STATUS_ITEMS"
          :key="item.type"
          class="p-3 flex items-center justify-between text-xs hover:bg-[#161B22] transition-colors"
        >
          <div class="flex items-center gap-3">
            <UiBadge :status="item.type" size="md" />
            <div>
              <span class="font-bold text-white block">{{ item.name }}</span>
              <span class="text-[#8B949E] text-[11px]">{{ item.desc }}</span>
            </div>
          </div>
          <span class="px-2 py-0.5 rounded bg-slate-800 text-[11px] text-[#8B949E] font-medium">
            {{ item.device }}
          </span>
        </div>
      </div>
    </div>

    <template #footer>
      <UiButton variant="primary" size="sm" @click="emit('update:open', false)">
        了解並關閉
      </UiButton>
    </template>
  </UiModal>
</template>
