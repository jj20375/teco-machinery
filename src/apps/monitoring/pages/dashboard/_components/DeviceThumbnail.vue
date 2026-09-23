<script setup lang="ts">
// 設備縮圖佔位。Figma 用的是 3D 產品渲染圖；這裡先用線稿等距圖示，
// 之後有正式渲染圖可換成 <img>。
withDefaults(defineProps<{ kind?: 'chiller' | 'fcu'; size?: number }>(), { kind: 'fcu', size: 44 });
</script>

<template>
  <div
    class="shrink-0 rounded-lg bg-gradient-to-br from-[#0b1a2b] to-[#0a2436] border border-[#1f3a52] grid place-items-center overflow-hidden"
    :style="{ width: `${size}px`, height: `${size}px` }"
  >
    <svg v-if="kind === 'chiller'" viewBox="0 0 48 40" class="w-3/4 h-3/4" fill="none" stroke="#5cc8f5" stroke-width="1.2" stroke-linejoin="round">
      <path d="M6 14 L24 6 L42 14 L42 30 L24 38 L6 30 Z" opacity=".9" />
      <path d="M6 14 L24 22 L42 14 M24 22 L24 38" opacity=".5" />
      <path d="M11 17 v8 M16 19 v8 M21 21 v8 M31 21 v8 M36 19 v8" opacity=".7" />
    </svg>
    <svg v-else viewBox="0 0 44 44" class="w-3/4 h-3/4" fill="none" stroke="#5cc8f5" stroke-width="1.3" stroke-linejoin="round">
      <path d="M22 5 L38 14 L38 30 L22 39 L6 30 L6 14 Z" opacity=".9" />
      <path d="M6 14 L22 23 L38 14 M22 23 L22 39" opacity=".45" />
      <path d="M13 12 L29 21 M31 12 L15 21" opacity=".55" />
    </svg>
  </div>
</template>
