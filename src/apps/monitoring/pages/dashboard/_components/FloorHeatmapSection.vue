<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue';
import FloorPlanViewer from '../../../floor-plan/FloorPlanViewer.vue';
import StatusPill from './StatusPill.vue';
import type { FloorId, FloorHeatmapData, AlarmItem } from '../_types/dashboard-types';

const props = defineProps<{
  currentFloor: FloorId;
  floorLocked: boolean;
  countdown: number;
  heatmapData: FloorHeatmapData | null;
  alarms: AlarmItem[];
  b1AvgTemp: number;
  b2AvgTemp: number;
}>();

const nextFloor = computed<FloorId>(() => (props.currentFloor === 'B1' ? 'B2' : 'B1'));

const emit = defineEmits<{
  (e: 'floor-badge', floor: FloorId): void;
  (e: 'open-alarm-modal'): void;
}>();

// 告警 > 2 筆時自動輪播（一次顯示兩列）
const idx = ref(0);
let timer: number | null = null;
const rows = computed(() => {
  const a = props.alarms;
  if (a.length <= 2) return a;
  return [a[idx.value % a.length], a[(idx.value + 1) % a.length]];
});
onMounted(() => {
  timer = window.setInterval(() => {
    if (props.alarms.length > 2) idx.value = (idx.value + 1) % props.alarms.length;
  }, 4000);
});
onUnmounted(() => { if (timer) clearInterval(timer); });

const badges = computed(() => [
  { floor: 'B1' as FloorId, temp: props.b1AvgTemp },
  { floor: 'B2' as FloorId, temp: props.b2AvgTemp },
]);

function alarmStatus(text: string) {
  if (text.includes('離線')) return 'OFFLINE';
  if (text.includes('停止') || text.includes('排程')) return 'STOPPED';
  if (text.includes('保養')) return 'MAINTENANCE';
  return 'ABNORMAL';
}
</script>

<template>
  <div class="flex flex-col gap-4 h-full min-h-0">
    <!-- 樓層溫度熱感圖：外面包一層流動漸層光暈邊框，示意「持續輪詢中」。
         光暈用獨立外層 wrapper 而不是直接加在卡片本身，因為卡片有 overflow-hidden
         （地圖跟疊層要被裁成圓角），光暈的環跟模糊暈影都得往外溢出卡片邊界，
         放在同一層會被那個 overflow-hidden 整圈裁掉。 -->
    <div class="glow-border-live flex-1 min-h-0">
    <!-- 貪食蛇光點：疊在彩虹光暈之上，一段亮點帶拖尾沿邊框繞圈爬，比整圈同時亮更有
         「正在跑」的感覺。獨立用一個真實元素（不是 ::before/::after），因為
         .glow-border-live 的兩個偽元素已經被彩虹環跟貼邊暈影用掉了。 -->
    <span class="glow-snake" aria-hidden="true" />
    <!-- relative + z-1：模糊暈影（::after）用 z-index 疊層，卡片若維持 position:static
         就會永遠墊在任何有 z-index 的定位元素下面（CSS 疊層規則，跟數值無關），暈影會
         直接蓋到地圖內容上面而不是躲在卡片後面。給卡片一個明確層級才能排到暈影上方。 -->
    <div class="relative z-[1] bg-[#161B22] border border-[#30363D] rounded-xl flex flex-col h-full min-h-0 overflow-hidden">
      <div class="flex items-center justify-between px-4 py-3 shrink-0">
        <h2 class="text-[17px] font-bold text-[#F0F6FC] flex items-center gap-2">
          <svg class="w-4 h-4 text-[#00D1B2]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8"
              d="M9 20l-5.447-2.724A1 1 0 013 16.382V5.618a1 1 0 011.447-.894L9 7m0 13l6-3m-6 3V7m6 10l4.553 2.276A1 1 0 0021 18.382V7.618a1 1 0 00-.553-.894L15 4m0 13V4m0 0L9 7" />
          </svg>
          樓層溫度熱感圖
        </h2>
        <div class="flex items-center gap-2">
          <!-- 輪詢倒數：未鎖定時顯示還有幾秒自動切換到另一樓層，與均溫膠囊同排、明顯可見 -->
          <span
            v-if="!floorLocked"
            class="flex items-center gap-1.5 px-3 py-1 rounded-md text-sm font-tabular font-semibold border border-[#30363D] bg-[#0D1117] text-[#00D1B2]"
            title="自動輪詢：定時切換樓層顯示"
          >
            <svg class="w-3.5 h-3.5 shrink-0 animate-spin-slow" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            {{ countdown }}s 後切換 {{ nextFloor }}
          </span>
          <button
            v-for="b in badges" :key="b.floor" type="button"
            class="px-3 py-1 rounded-md text-sm font-tabular font-semibold border transition-colors"
            :class="props.currentFloor === b.floor
              ? 'bg-[#0e2e2b] border-[#00D1B2]/60 text-[#5EEAD4]'
              : 'bg-[#0D1117] border-[#30363D] text-[#8B949E] hover:text-[#C9D1D9]'"
            @click="emit('floor-badge', b.floor)"
          >
            {{ b.floor }}均溫 {{ b.temp }}°C
          </button>
        </div>
      </div>

      <!-- 地圖（沿用設備配置圖 component） -->
      <div class="relative flex-1 min-h-0 bg-[#0C1925]">
        <FloorPlanViewer :floor="currentFloor" embedded zoomable />
        <span class="absolute top-3 left-3 text-sm font-semibold text-[#5EEAD4] pointer-events-none">
          目前顯示：{{ currentFloor }} 樓層<span v-if="floorLocked" class="text-[#8B949E] font-normal">（已鎖定，點選均溫可解除）</span>
        </span>
        <div v-if="heatmapData" class="absolute bottom-3 left-3 flex gap-2 pointer-events-none">
          <div class="px-2.5 py-1.5 rounded-md bg-[#0d1117cc] border border-[#30363D] leading-tight">
            <div class="text-[10px] text-[#8B949E]">{{ currentFloor }} FCU 總數</div>
            <div class="text-xs font-bold text-[#F0F6FC]">{{ heatmapData.fcuTotal }}台 (運轉{{ heatmapData.fcuRunning }}台)</div>
          </div>
          <div class="px-2.5 py-1.5 rounded-md bg-[#0d1117cc] border border-[#30363D] leading-tight">
            <div class="text-[10px] text-[#8B949E]">當前運轉率</div>
            <div class="text-xs font-bold text-[#5EEAD4]">{{ heatmapData.runRate }}%</div>
          </div>
        </div>
      </div>
    </div>
    </div>

    <!-- 即時告警 -->
    <div class="bg-[#161B22] border border-[#30363D] rounded-xl p-4 shrink-0">
      <div class="flex items-center justify-between mb-2">
        <h2 class="text-base font-bold text-[#F0F6FC] flex items-center gap-2">
          <svg class="w-4 h-4 text-[#FF4D4F]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8"
              d="M12 9v2m0 4h.01M5.07 19h13.86a2 2 0 001.74-3L13.73 4a2 2 0 00-3.46 0L3.34 16a2 2 0 001.73 3z" />
          </svg>
          即時告警
        </h2>
        <button
          type="button"
          class="px-2.5 py-1 rounded-md bg-[#F87171] text-white text-xs font-bold flex items-center gap-1.5 hover:bg-[#ef5f5f] transition-colors"
          @click="emit('open-alarm-modal')"
        >
          <span class="w-1.5 h-1.5 rounded-full bg-white" />
          即時告警 {{ alarms.length }} 筆
        </button>
      </div>

      <table class="w-full text-sm border-collapse">
        <thead>
          <tr class="text-xs text-[#8B949E] bg-[#0D1117]">
            <th class="text-left font-normal py-2 px-2 rounded-l-md">時間</th>
            <th class="text-left font-normal py-2 px-2">設備名稱</th>
            <th class="text-left font-normal py-2 px-2">設備編號</th>
            <th class="text-left font-normal py-2 px-2">安裝位置</th>
            <th class="text-left font-normal py-2 px-2 rounded-r-md">狀態</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="a in rows" :key="a.id" class="border-b border-[#21262D] last:border-0">
            <td class="py-2.5 px-2 font-tabular text-[#8B949E]">{{ a.time }}</td>
            <td class="py-2.5 px-2 font-bold text-[#F0F6FC]">{{ a.deviceName }}</td>
            <td class="py-2.5 px-2 font-tabular text-[#8B949E]">{{ a.deviceCode }}</td>
            <td class="py-2.5 px-2 text-[#C9D1D9]">{{ a.location }}</td>
            <td class="py-2.5 px-2"><StatusPill :status="alarmStatus(a.statusText)" :text="a.statusText" /></td>
          </tr>
          <tr v-if="alarms.length === 0">
            <td colspan="5" class="py-6 text-center text-[#5EEAD4] text-xs">✓ 目前無未解除的即時告警</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<style scoped>
/*
 * 流動漸層光暈邊框：純 CSS，不用 three.js。
 * ::before 是貼著卡片邊緣的一圈細光（用 mask-composite: exclude 把 conic-gradient
 * 挖成「只留邊框」的環，跟平常那種漸層背景板不一樣，中間是鏤空的，不會擋住卡片內容）；
 * ::after 是同一個漸層再放大、模糊，疊在最底層當柔和暈影，做出「玻璃反光」的厚度感。
 * 兩層共用同一個 conic-gradient 角度、同步轉動，看起來就是一圈色彩持續繞著卡片流動——
 * 拿來示意這張卡片的樓層資料是活的、持續在輪詢，不是靜態畫面。
 *
 * 踩過的雷：
 * 1. 一開始用 `transform: rotate()` 轉整個 ::before/::after 的矩形貼圖盒子，結果矩形
 *    （不是正方形）轉到接近 45° 時四個角會甩到外面很遠，因為沒有任何東西裁切它，畫面上
 *    看起來像一條貫穿整個看板的斜線。正確做法是只轉 conic-gradient 本身的角度（靠
 *    @property 註冊一個可動畫的自訂屬性），矩形外框完全不動，動的只有裡面的顏色。
 * 2. 光暈原本用負的 inset 往卡片外面溢出，但這張卡片外層在 DashboardApp.vue 是包在
 *    `<section class="overflow-hidden">` 裡、跟卡片零間距，往外溢出的部分整圈被那個
 *    overflow-hidden 切掉，幾乎看不到。所以光暈跟環都收在 inset: 0（卡片本身邊界內），
 *    不再往外溢出——犧牲「暈影飄出卡片外」的效果，換成穩定看得到的貼邊發光。
 */
@property --glow-angle {
  syntax: '<angle>';
  inherits: false;
  initial-value: 0deg;
}
.glow-border-live {
  position: relative;
  border-radius: 0.75rem; /* 對齊卡片的 rounded-xl，光暈的圓角才會跟卡片邊框重疊 */
  --glow-angle: 0deg;
  animation: glow-border-spin 5s linear infinite;
}
.glow-border-live::before,
.glow-border-live::after {
  content: '';
  position: absolute;
  inset: 0;   /* 不再用負值往外溢出，整圈收在卡片自己的邊界內，才不會被祖先的 overflow-hidden 切掉 */
  border-radius: inherit;
  background: conic-gradient(from var(--glow-angle), #00D1B2, #38BDF8, #A78BFA, #F472B6, #00D1B2);
  pointer-events: none;
}
.glow-border-live::before {
  z-index: 2;
  padding: 4px;   /* 光圈粗細；直接畫在卡片邊界上，夠粗才看得清楚 */
  -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  -webkit-mask-composite: xor;
  mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  mask-composite: exclude;
}
.glow-border-live::after {
  /* 貼邊內暈：同一個漸層鋪在卡片邊緣、模糊化，做出「邊緣在發光」的感覺；
     不整圈鋪滿卡片，用同一招 mask 挖成環狀，只有靠邊界的一圈會糊出顏色，中間仍是純黑透明。 */
  z-index: 0;
  padding: 16px;
  -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  -webkit-mask-composite: xor;
  mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  mask-composite: exclude;
  filter: blur(14px);
  opacity: .8;
}
@keyframes glow-border-spin {
  to { --glow-angle: 360deg; }
}

/*
 * 貪食蛇光點：conic-gradient 大部分是透明的，只在靠近 from 角度前留一小段
 * 由暗到亮的「拖尾 → 亮點」（336deg→360deg），其餘 336 度都是 transparent。
 * 旋轉 --snake-angle 帶著這一小段掃過整圈，視覺上就是一顆帶拖尾的光點繞著邊框爬，
 * 而不是整圈同時亮——這才是「貪食蛇」的感覺，跟上面那圈彩虹光暈是疊加、不是取代。
 * 遮罩挖環的手法跟彩虹環（::before）同一招，套在真實元素而不是偽元素上。
 */
@property --snake-angle {
  syntax: '<angle>';
  inherits: false;
  initial-value: 0deg;
}
.glow-snake {
  position: absolute;
  inset: 0;
  z-index: 3;   /* 蓋在彩虹環（z-index:2）之上，光點掃過去才會明顯蓋過底下的顏色 */
  display: block;
  border-radius: inherit;
  padding: 4px;   /* 跟彩虹環同粗細，疊在同一條邊框路徑上 */
  pointer-events: none;
  --snake-angle: 0deg;
  background: conic-gradient(
    from var(--snake-angle),
    transparent 0deg,
    transparent 300deg,
    rgba(94, 234, 212, .5) 332deg,
    #E9FFFB 354deg,
    #ffffff 360deg
  );
  -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  -webkit-mask-composite: xor;
  mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  mask-composite: exclude;
  filter: drop-shadow(0 0 5px rgba(94, 234, 212, .9)) drop-shadow(0 0 10px rgba(94, 234, 212, .5));
  animation: glow-snake-crawl 6s linear infinite;
}
@keyframes glow-snake-crawl {
  to { --snake-angle: 360deg; }
}

@media (prefers-reduced-motion: reduce) {
  .glow-border-live::before,
  .glow-border-live::after,
  .glow-snake {
    animation: none;
  }
}
</style>
