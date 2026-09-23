<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue';

// Figma 前台 header 只有：品牌標題（左）＋ 即時時鐘 / SYSTEM ACTIVE（右）。
// 樓層切換是自動輪播（點中欄均溫膠囊可手動鎖定），告警從中欄「即時告警」卡進入，
// 所以這裡不放樓層鈕、鈴鐺、圖例、後台連結、全螢幕鈕。

const currentTime = ref('');
let timerId: number | null = null;

function updateClock() {
  const now = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  currentTime.value =
    `${now.getFullYear()}/${p(now.getMonth() + 1)}/${p(now.getDate())} ` +
    `${p(now.getHours())}:${p(now.getMinutes())}:${p(now.getSeconds())}`;
}

onMounted(() => {
  updateClock();
  timerId = window.setInterval(updateClock, 1000);
});
onUnmounted(() => {
  if (timerId) clearInterval(timerId);
});
</script>

<template>
  <header class="h-[72px] shrink-0 px-6 bg-[#0D1117] border-b border-[#30363D] flex items-center justify-between select-none">
    <!-- 左：品牌 -->
    <div class="flex items-center gap-3">
      <div class="w-10 h-6 rounded border border-[#1E88E5] grid place-items-center text-[#1E88E5] font-extrabold text-[11px] tracking-tighter">
        TECO
      </div>
      <div class="leading-tight">
        <h1 class="text-xl font-bold text-[#F0F6FC] tracking-wide">冰水空調智慧監控中心</h1>
        <p class="text-[11px] text-[#00D1B2] tracking-[0.14em] font-semibold">
          TECO ELECTRIC &amp; MACHINERY CO., LTD.
        </p>
      </div>
    </div>

    <!-- 右：時鐘 + 系統狀態 -->
    <div class="text-right leading-tight">
      <div class="text-sm font-tabular text-[#F0F6FC] tracking-wider">{{ currentTime }}</div>
      <div class="text-[11px] text-[#8B949E] tracking-[0.18em]">SYSTEM ACTIVE</div>
    </div>
  </header>
</template>
