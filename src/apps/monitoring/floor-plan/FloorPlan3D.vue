<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue';
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { AREA_COLOR, planOf, areaHeat, equipmentSize, equipmentColor, type Point, type Equipment, type Placement } from './floor-plan';
import type { FloorId } from '../pages/dashboard/_types/dashboard-types';

const props = withDefaults(defineProps<{ floor?: FloorId; placements: Placement[]; equipment: Equipment[]; selectedDeviceId?: string }>(), { floor: 'B1' });
// 兩層的圖面尺寸、外框與分區都不同；場景是在 onMounted 一次建好的，這裡取當下樓層即可，
// 樓層切換由外層重新掛載（見 AdminFloorPlanPage 的 :key）。
const plan = () => planOf(props.floor);
defineEmits<{ fallback: [] }>();
const host = ref<HTMLDivElement | null>(null);
const error = ref('');
let renderer: THREE.WebGLRenderer | undefined;
let camera: THREE.OrthographicCamera;
let controls: OrbitControls | undefined;
let observer: ResizeObserver | undefined;
const scene = new THREE.Scene();
const deviceGroup = new THREE.Group();
const areaMaterials = new Map<string, THREE.MeshStandardMaterial>();

// ---- 效果：環境粒子、異常設備的多邊形警示環、懸浮裝飾多邊形 ----
// 原本這支元件沒有連續動畫迴圈，只在狀態變化時手動 render 一次；這幾個效果都要
// 逐幀動，所以另外開一個 requestAnimationFrame 迴圈，只在 3D 檢視掛載時跑。
const PARTICLE_COUNT = 240;
let particles: THREE.Points | undefined;
let particleSpeed: Float32Array | undefined;
let particlePhase: Float32Array | undefined;
/** 異常設備的警示環；設備清單一變就整批重建，所以不用個別追蹤要不要移除 */
const alertRings: { mesh: THREE.Mesh; phase: number }[] = [];
let hologram: THREE.Mesh | undefined;
/** 地板雷達掃描光束；外層 Group 只負責繞世界 Y 軸旋轉，內層 disc 才是攤平的貼圖網格，
 *  這樣不用去推算 Euler XYZ 複合旋轉的順序，一定是繞垂直軸轉。 */
let radarSweepGroup: THREE.Group | undefined;
/** FCU → 最近冰水主機的資料流線；設備清單一變就整批重建（見 updateDataFlow）。 */
const dataFlowGroup = new THREE.Group();
const DATA_DOTS_PER_LINE = 2;
let flowMeta: { from: THREE.Vector3; to: THREE.Vector3; phase: number; speed: number }[] = [];
let flowPoints: THREE.Points | undefined;
let rafId = 0;
const clock = new THREE.Clock();

function buildParticles() {
  const w = plan().width, h = plan().height;
  const positions = new Float32Array(PARTICLE_COUNT * 3);
  const speed = new Float32Array(PARTICLE_COUNT);
  const phase = new Float32Array(PARTICLE_COUNT);
  for (let i = 0; i < PARTICLE_COUNT; i += 1) {
    positions[i * 3] = (Math.random() - .5) * (w + 160);
    positions[i * 3 + 1] = 20 + Math.random() * 260;
    positions[i * 3 + 2] = (Math.random() - .5) * (h + 160);
    speed[i] = 6 + Math.random() * 10;
    phase[i] = Math.random() * Math.PI * 2;
  }
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  const material = new THREE.PointsMaterial({
    color: '#5EEAD4', size: 3.4, sizeAttenuation: true, transparent: true,
    opacity: .55, blending: THREE.AdditiveBlending, depthWrite: false,
  });
  particles = new THREE.Points(geometry, material);
  particleSpeed = speed;
  particlePhase = phase;
  scene.add(particles);
}

function buildHologram() {
  // 純裝飾用的懸浮多邊形，擺在角落當點綴，不要蓋到平面圖本身要看的內容。
  const geometry = new THREE.IcosahedronGeometry(56, 0);
  const material = new THREE.MeshBasicMaterial({ color: '#00D1B2', wireframe: true, transparent: true, opacity: .32 });
  hologram = new THREE.Mesh(geometry, material);
  hologram.position.set(plan().width / 2 - 70, 210, -plan().height / 2 + 70);
  scene.add(hologram);
}

function buildRadarSweep() {
  // 用 conic gradient 畫一道「亮 → 透明」的扇形光束貼在整片圓上，靠旋轉整個 disc 來製造掃描效果，
  // 不用逐幀重畫貼圖。
  const size = 512;
  const canvas = document.createElement('canvas');
  canvas.width = size;
  canvas.height = size;
  const context = canvas.getContext('2d')!;
  const center = size / 2;
  const gradient = context.createConicGradient(0, center, center);
  gradient.addColorStop(0, 'rgba(94,234,212,.5)');
  gradient.addColorStop(.14, 'rgba(94,234,212,0)');
  gradient.addColorStop(1, 'rgba(94,234,212,0)');
  context.fillStyle = gradient;
  context.beginPath();
  context.arc(center, center, center, 0, Math.PI * 2);
  context.fill();
  const radius = Math.max(plan().width, plan().height) * .75;
  const disc = new THREE.Mesh(
    new THREE.CircleGeometry(radius, 64),
    new THREE.MeshBasicMaterial({ map: new THREE.CanvasTexture(canvas), transparent: true, blending: THREE.AdditiveBlending, depthWrite: false }),
  );
  disc.rotation.x = -Math.PI / 2;
  radarSweepGroup = new THREE.Group();
  radarSweepGroup.position.y = 6.4;   // 貼在分區量體（高度 6）上方一點，不會被蓋掉
  radarSweepGroup.add(disc);
  scene.add(radarSweepGroup);
}
/** FCU 連到樓層內最近的冰水主機，畫一條淡淡的連線＋沿線流動的光點；B2 目前沒有主機，自然不會有連線。 */
function updateDataFlow() {
  disposeObject(dataFlowGroup);
  dataFlowGroup.clear();
  flowMeta = [];
  flowPoints = undefined;
  const chillerPlacements = props.placements.filter((p) => p.kind === 'chiller');
  const fcuPlacements = props.placements.filter((p) => p.kind === 'fcu');
  if (!chillerPlacements.length || !fcuPlacements.length) return;

  const linePositions: number[] = [];
  const lineHeight = 8;
  for (const fcu of fcuPlacements) {
    let nearest = chillerPlacements[0];
    let bestDist = Infinity;
    for (const chiller of chillerPlacements) {
      const dist = (chiller.x - fcu.x) ** 2 + (chiller.y - fcu.y) ** 2;
      if (dist < bestDist) { bestDist = dist; nearest = chiller; }
    }
    const from = new THREE.Vector3(fcu.x - plan().width / 2, lineHeight, fcu.y - plan().height / 2);
    const to = new THREE.Vector3(nearest.x - plan().width / 2, lineHeight, nearest.y - plan().height / 2);
    linePositions.push(from.x, from.y, from.z, to.x, to.y, to.z);
    for (let i = 0; i < DATA_DOTS_PER_LINE; i += 1) {
      flowMeta.push({ from, to, phase: Math.random(), speed: .18 + Math.random() * .12 });
    }
  }

  const lineGeometry = new THREE.BufferGeometry();
  lineGeometry.setAttribute('position', new THREE.Float32BufferAttribute(linePositions, 3));
  const lineMaterial = new THREE.LineBasicMaterial({ color: '#5EEAD4', transparent: true, opacity: .16, blending: THREE.AdditiveBlending, depthWrite: false });
  dataFlowGroup.add(new THREE.LineSegments(lineGeometry, lineMaterial));

  const dotGeometry = new THREE.BufferGeometry();
  dotGeometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(flowMeta.length * 3), 3));
  const dotMaterial = new THREE.PointsMaterial({ color: '#5EEAD4', size: 4.2, sizeAttenuation: true, transparent: true, opacity: .9, blending: THREE.AdditiveBlending, depthWrite: false });
  flowPoints = new THREE.Points(dotGeometry, dotMaterial);
  dataFlowGroup.add(flowPoints);
}
/** 異常設備上方套一個六邊形警示環；隨 updateDevices() 一起重建，舊的環跟著 deviceGroup 一併釋放 */
function rebuildAlertRings() {
  alertRings.length = 0;
  for (const p of props.placements) {
    const equipment = props.equipment.find((e) => e.id === p.deviceId);
    if (equipmentColor(equipment?.status) !== '#f87171') continue;
    const [, depth] = equipmentSize(p.kind, props.floor);
    const height = p.kind === 'chiller' ? 17 : 10;
    const ring = new THREE.Mesh(
      new THREE.RingGeometry(depth / 2 + 4, depth / 2 + 9, 6),
      new THREE.MeshBasicMaterial({ color: '#F87171', transparent: true, opacity: .85, side: THREE.DoubleSide, blending: THREE.AdditiveBlending, depthWrite: false }),
    );
    ring.rotation.x = -Math.PI / 2;
    ring.position.set(p.x - plan().width / 2, height + 2, p.y - plan().height / 2);
    deviceGroup.add(ring);
    alertRings.push({ mesh: ring, phase: Math.random() * Math.PI * 2 });
  }
}

function tick() {
  rafId = requestAnimationFrame(tick);
  const t = clock.getElapsedTime();
  if (particles && particleSpeed && particlePhase) {
    const pos = particles.geometry.attributes.position as THREE.BufferAttribute;
    for (let i = 0; i < PARTICLE_COUNT; i += 1) {
      let y = pos.getY(i) + particleSpeed[i] * .016;
      if (y > 280) y = 20;   // 飄到頂就從底部重新開始，維持粒子數量固定
      pos.setY(i, y);
      pos.setX(i, pos.getX(i) + Math.sin(t * .6 + particlePhase[i]) * .04);
    }
    pos.needsUpdate = true;
  }
  for (const { mesh, phase } of alertRings) {
    const cycle = ((t + phase) % 1.4) / 1.4;   // 每個環錯開起始相位，不會同步閃爍
    mesh.scale.setScalar(1 + cycle * 1.6);
    (mesh.material as THREE.MeshBasicMaterial).opacity = .85 * (1 - cycle);
  }
  if (hologram) {
    hologram.rotation.y = t * .18;
    hologram.rotation.x = t * .09;
    hologram.position.y = 210 + Math.sin(t * .5) * 8;
  }
  if (radarSweepGroup) radarSweepGroup.rotation.y = t * .52;   // 約 12 秒轉一圈
  if (flowPoints && flowMeta.length) {
    const pos = flowPoints.geometry.attributes.position as THREE.BufferAttribute;
    flowMeta.forEach((m, i) => {
      const progress = (t * m.speed + m.phase) % 1;
      pos.setXYZ(i, m.from.x + (m.to.x - m.from.x) * progress, m.from.y, m.from.z + (m.to.z - m.from.z) * progress);
    });
    pos.needsUpdate = true;
  }
  render();
}

function shape(points: Point[]) {
  // SVG 的向下 Y 軸對應到 Three.js 地面的 Z 軸，設備沿用同一座標。
  return new THREE.Shape(points.map(([x,y]) => new THREE.Vector2(x - plan().width / 2, plan().height / 2 - y)));
}
function render() {
  if (renderer && camera && !error.value) renderer.render(scene, camera);
}
function disposeObject(root: THREE.Object3D) {
  root.traverse((object) => {
    const mesh = object as THREE.Mesh;
    mesh.geometry?.dispose();
    if (mesh.material) {
      const materials = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
      materials.forEach((material) => {
        (material as THREE.SpriteMaterial).map?.dispose();
        material.dispose();
      });
    }
  });
}
function label(text: string, x: number, y: number, z: number, width: number) {
  const canvas = document.createElement('canvas');
  const context = canvas.getContext('2d')!;
  context.font = '500 32px sans-serif';
  canvas.width = Math.ceil(context.measureText(text).width) + 24;
  canvas.height = 64;
  context.font = '500 32px sans-serif';
  context.textAlign = 'center';
  context.textBaseline = 'middle';
  context.lineWidth = 7;
  context.strokeStyle = '#0c1925';
  context.strokeText(text, canvas.width/2, 32);
  context.fillStyle = '#e2edf3';
  context.fillText(text, canvas.width/2, 32);
  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: new THREE.CanvasTexture(canvas), depthTest: false }));
  sprite.position.set(x - plan().width/2, y, z - plan().height/2);
  sprite.scale.set(width, width * canvas.height/canvas.width, 1);
  return sprite;
}
function updateDevices() {
  disposeObject(deviceGroup);
  deviceGroup.clear();
  for (const p of props.placements) {
    const equipment = props.equipment.find((e) => e.id === p.deviceId);
    const [width, depth] = equipmentSize(p.kind, props.floor);
    const height = p.kind === 'chiller' ? 17 : 10;
    const block = new THREE.Mesh(new THREE.BoxGeometry(width, height, depth), new THREE.MeshStandardMaterial({ color: equipmentColor(equipment?.status), roughness: .55 }));
    block.position.set(p.x - plan().width/2, 7 + height/2, p.y - plan().height/2);
    block.rotation.y = -p.rotation * Math.PI/180;
    deviceGroup.add(block);
    const edges = new THREE.LineSegments(new THREE.EdgesGeometry(block.geometry), new THREE.LineBasicMaterial({ color: p.deviceId === props.selectedDeviceId ? '#ffffff' : '#bfd5e4' }));
    edges.position.copy(block.position);
    edges.rotation.copy(block.rotation);
    deviceGroup.add(edges, label(equipment?.name ?? equipment?.code ?? p.deviceId, p.x, height + 21, p.y, 24));
  }
  rebuildAlertRings();
  render();
}
function updateAreaHeat() {
  for (const area of plan().areas) {
    const material = areaMaterials.get(area.id);
    if (!material) continue;
    const heat = areaHeat(area.id, props.placements, props.equipment);
    material.color.set(heat.color ?? AREA_COLOR);
    material.emissive.set(heat.color ?? '#000000');
    material.emissiveIntensity = heat.tone === 'alert' ? .5 + heat.intensity : heat.tone === 'normal' ? .35 : 0;
  }
  render();
}
function fit() {
  if (!renderer || !host.value) return;
  const { width, height } = host.value.getBoundingClientRect();
  if (!width || !height) return;
  const aspect = width/height;
  camera.updateMatrixWorld();
  const projected = plan().outline.map(([x,y]) => new THREE.Vector3(x-plan().width/2, 0, y-plan().height/2).applyMatrix4(camera.matrixWorldInverse));
  const halfWidth = Math.max(...projected.map((p) => Math.abs(p.x))) + 43;
  const halfHeight = Math.max(...projected.map((p) => Math.abs(p.y))) + 43;
  const vertical = Math.max(halfHeight, halfWidth/aspect);
  camera.left = -vertical * aspect;
  camera.right = vertical * aspect;
  camera.top = vertical;
  camera.bottom = -vertical;
  camera.updateProjectionMatrix();
  renderer.setSize(width, height);
  render();
}
function resetView() {
  if (!camera || !controls) return;
  camera.position.set(480, 768, 672);
  camera.zoom = 1;
  camera.updateProjectionMatrix();
  controls.target.set(0,0,0);
  controls.update();
  fit();
}
function contextLost(event: Event) {
  event.preventDefault();
  error.value = '立體顯示已中斷，請切回平面圖繼續配置。';
}
onMounted(() => {
  try {
    renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.domElement.setAttribute('aria-label', `${props.floor} 立體空間圖；拖曳旋轉，滾輪縮放。設備編輯請切回平面圖。`);
    renderer.domElement.setAttribute('role', 'img');
    renderer.domElement.addEventListener('webglcontextlost', contextLost);
    host.value!.appendChild(renderer.domElement);
    camera = new THREE.OrthographicCamera(-480,480,432,-432,1,10000);
    controls = new OrbitControls(camera, renderer.domElement);
    controls.minPolarAngle = .15;
    controls.maxPolarAngle = 1.35;
    controls.minZoom = .5;
    controls.maxZoom = 3;
    controls.addEventListener('change', render);
    scene.add(new THREE.HemisphereLight('#e4f4ff', '#506378', 2.5));
    const sun = new THREE.DirectionalLight('#ffffff', 2);
    sun.position.set(-192,480,336);
    scene.add(sun);
    // OUTLINE 是底圖輪廓的凸包（見 scripts/extract-floor-plan.mjs），左側斜牆等非凸角會被填平，
    // 樓地板量體因此略大於實際建築輪廓——這是刻意的安全誤差方向，只作示意用。
    const base = new THREE.Mesh(new THREE.ExtrudeGeometry(shape(plan().outline), { depth: 4, bevelEnabled: false }), new THREE.MeshStandardMaterial({ color: '#223e50' }));
    base.rotation.x = -Math.PI/2;
    base.position.y = -4;
    scene.add(base);
    // 底圖線稿：跟 2D 平面圖同一張圖，貼在地板正上方。紅框沒圈到的地方（例如 B2
    // 幾乎整層的停車場）原本只有一片素色地板，貼上線稿才看得出那裡其實是什麼。
    // 圖片本身除了畫出來的線之外都是透明，蓋在分區量體上也不會擋到分區顏色。
    // 貼圖方向沿用 three.js 預設的 flipY：平面轉 -90° 後 world z = svg_y - height/2，
    // 跟標籤、設備、分區外框用的換算一致，v=1 正好對到圖片上緣。不要改成 flipY=false，
    // 那會讓底圖上下顛倒——在平面圖上看起來就像整張轉了 180 度。
    const baseTexture = new THREE.TextureLoader().load(plan().baseImage, () => render());
    baseTexture.colorSpace = THREE.SRGBColorSpace;
    const baseImage = new THREE.Mesh(
      new THREE.PlaneGeometry(plan().width, plan().height),
      new THREE.MeshBasicMaterial({ map: baseTexture, transparent: true, opacity: plan().baseOpacity, depthWrite: false }),
    );
    baseImage.rotation.x = -Math.PI/2;
    baseImage.position.y = .1;   // 貼著地板頂面，但要高過一點才不會被地板本身的頂面蓋掉
    scene.add(baseImage);
    for (const area of plan().areas) {
      const height = 6;
      const material = new THREE.MeshStandardMaterial({ color: AREA_COLOR });
      const mesh = new THREE.Mesh(new THREE.ExtrudeGeometry(shape(area.points), { depth: height, bevelEnabled: false }), material);
      mesh.rotation.x = -Math.PI/2;
      areaMaterials.set(area.id, material);
      scene.add(mesh);
      const outline = new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(area.points.map(([x,y]) => new THREE.Vector3(x-plan().width/2,height+.5,y-plan().height/2))), new THREE.LineBasicMaterial({ color: '#648797' }));
      scene.add(outline, label(area.id, area.label[0], 12, area.label[1], 24));
    }
    scene.add(deviceGroup, dataFlowGroup);
    buildParticles();
    buildHologram();
    buildRadarSweep();
    resetView();
    updateAreaHeat();
    updateDevices();
    updateDataFlow();
    observer = new ResizeObserver(fit);
    observer.observe(host.value!);
    fit();
    rafId = requestAnimationFrame(tick);
  } catch {
    error.value = '此瀏覽器無法開啟立體顯示，請使用平面圖配置設備。';
  }
});
watch(() => [props.placements, props.equipment, props.selectedDeviceId], () => {
  updateAreaHeat();
  updateDevices();
  updateDataFlow();
}, { deep: true });
onUnmounted(() => {
  cancelAnimationFrame(rafId);
  observer?.disconnect();
  controls?.dispose();
  disposeObject(scene);
  areaMaterials.clear();
  renderer?.domElement.removeEventListener('webglcontextlost', contextLost);
  renderer?.dispose();
  renderer?.domElement.remove();
});
</script>

<template>
  <div class="three-view">
    <div ref="host" class="three-host" />
    <div v-if="error" role="alert" class="fallback"><p>{{ error }}</p><button type="button" @click="$emit('fallback')">返回 2D 平面</button></div>
    <template v-else><div class="three-help">拖曳旋轉 · 滾輪縮放 · 量體高度為示意</div><button type="button" class="reset" @click="resetView">重設視角</button></template>
  </div>
</template>

<style scoped>
.three-view { position: relative; height: clamp(440px,65vh,850px); }.three-host { width: 100%; height: 100%; }.three-help { position: absolute; bottom: 16px; left: 18px; font-size: 11px; color: #a0b7c7; pointer-events: none; }.reset { position: absolute; top: 16px; right: 18px; }.fallback { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 15px; padding: 28px; background: #0c1925; font-size: 14px; }button { color: #e2edf5; background: #294559; border: 1px solid #4d738b; border-radius: 7px; padding: 8px 12px; font-size: 12px; cursor: pointer; }button:focus-visible { outline: 2px solid white; outline-offset: 3px; }
</style>
