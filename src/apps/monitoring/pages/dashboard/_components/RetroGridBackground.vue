<script setup lang="ts">
// 前台看板背景：Synthwave 復古透視格線，紫色（遠）→粉色（近）漸層，格線往前捲動。
// 純裝飾、蓋在最底層，卡片本身都是不透明底色，這層只有卡片間的縫隙、頁首、
// 縮放置中留白（letterbox）才看得到，所以填滿實際視窗（不是 1920×1080 設計稿）即可。
//
// 用 defineAsyncComponent 延後載入（見 DashboardApp.vue），three.js 不擋看板資料的初始渲染；
// astro.config.mjs 的 optimizeDeps.include 已經把 three 排進 dev server 啟動時的預打包，
// 不會重踩 FloorPlan3D 那次「點開才發現新依賴、觸發 504」的雷。
import { onMounted, onUnmounted, ref } from 'vue';
import * as THREE from 'three';

const host = ref<HTMLDivElement | null>(null);
let renderer: THREE.WebGLRenderer | undefined;
let camera: THREE.PerspectiveCamera | undefined;
let scene: THREE.Scene | undefined;
let gridTexture: THREE.Texture | undefined;
let rafId = 0;
const clock = new THREE.Clock();

/** 白色格線畫在透明 canvas 上，靠材質的漸層色（vertexColors）上色，貼圖本身不帶顏色 */
function buildGridTexture(): THREE.Texture {
  const size = 256;
  const canvas = document.createElement('canvas');
  canvas.width = size;
  canvas.height = size;
  const ctx = canvas.getContext('2d')!;
  ctx.strokeStyle = '#ffffff';
  ctx.lineWidth = 4;
  ctx.strokeRect(0, 0, size, size);
  const texture = new THREE.CanvasTexture(canvas);
  texture.wrapS = THREE.RepeatWrapping;
  texture.wrapT = THREE.RepeatWrapping;
  texture.repeat.set(40, 70);
  texture.anisotropy = 4;
  return texture;
}

function resize() {
  if (!renderer || !camera || !host.value) return;
  const { width, height } = host.value.getBoundingClientRect();
  if (!width || !height) return;
  camera.aspect = width / height;
  camera.updateProjectionMatrix();
  renderer.setSize(width, height);
}

function tick() {
  rafId = requestAnimationFrame(tick);
  if (!renderer || !camera || !scene || !gridTexture) return;
  // 貼圖 UV 往前捲，比逐幀重建幾何體便宜很多——格線本身固定不動，動的只有貼圖偏移。
  gridTexture.offset.y = (clock.getElapsedTime() * .12) % 1;
  renderer.render(scene, camera);
}

onMounted(() => {
  if (!host.value) return;
  try {
    scene = new THREE.Scene();
    // 黑紫色霧化，讓格線往遠方自然淡出、融進背景，是這個復古效果的關鍵。
    scene.fog = new THREE.FogExp2(0x0a0518, 0.05);

    camera = new THREE.PerspectiveCamera(70, 1, .1, 200);
    camera.position.set(0, 2.6, 5.4);
    camera.rotation.x = -.32;   // 負值讓鏡頭略微朝下，格線地板才會往畫面上方的地平線收攏

    renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    host.value.appendChild(renderer.domElement);

    gridTexture = buildGridTexture();
    const geometry = new THREE.PlaneGeometry(70, 160, 1, 1);
    // 每個頂點各自上色，四角只有兩種深度（近／遠），插值出來就是沿地板深度的漸層。
    const purple = new THREE.Color('#7C3AED');
    const pink = new THREE.Color('#EC4899');
    const position = geometry.attributes.position;
    const colors = new Float32Array(position.count * 3);
    for (let i = 0; i < position.count; i += 1) {
      const depthRatio = (position.getY(i) + 80) / 160;   // 0 = 遠端、1 = 近端
      const c = purple.clone().lerp(pink, depthRatio);
      colors[i * 3] = c.r; colors[i * 3 + 1] = c.g; colors[i * 3 + 2] = c.b;
    }
    geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));

    const material = new THREE.MeshBasicMaterial({
      map: gridTexture, vertexColors: true, transparent: true, opacity: .85,
      blending: THREE.AdditiveBlending, depthWrite: false,
    });
    const grid = new THREE.Mesh(geometry, material);
    grid.rotation.x = -Math.PI / 2;
    grid.position.y = -.6;
    scene.add(grid);

    window.addEventListener('resize', resize);
    resize();
    rafId = requestAnimationFrame(tick);
  } catch {
    // 純裝飾效果；WebGL 不可用就直接放棄，不影響看板本身的監控資料。
  }
});
onUnmounted(() => {
  cancelAnimationFrame(rafId);
  window.removeEventListener('resize', resize);
  gridTexture?.dispose();
  renderer?.dispose();
  renderer?.domElement.remove();
});
</script>

<template>
  <div ref="host" class="absolute inset-0" aria-hidden="true" />
</template>
