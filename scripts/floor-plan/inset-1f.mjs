// B1 圖右上角的「1F 原始圖」小圖。
//
// 現場最終版 PPT 把 1F 大門、1F 電梯前的 2 台 FCU（MC5，FC_MC5_01／02）畫在建築外的一張小圖上，
// 系統原本的 SVG 沒有這塊。這裡依 PPT 裡的小圖重繪簡化版（外牆、電梯、梯間、柱子），風格跟其他
// 底圖一樣只留輪廓，並給兩個分區。之後若從 CAD／Illustrator 匯出了向量版，直接換掉這個檔。
//
// 座標系：旋轉後的最終圖面（右上方的空白三角區），不是來源檔座標。
// 內部用「PPT 小圖放大 4 倍的裁切圖」像素座標描述形狀（cx, cy），再用 toPlan() 換成圖面單位：
//   縮放 = PPT→圖面 0.479 × 縮小到 85%（才放得進空白三角區）÷ 4 ≈ 0.10175
const SCALE = 0.10175;
const ORIGIN = [610.7, 15.93];
const TONE = '#8fa8ba';

const toPlan = ([cx, cy]) => [ORIGIN[0] + SCALE * cx, ORIGIN[1] + SCALE * cy];
const fix = (value) => Math.round(value * 100) / 100;
const pair = (point) => toPlan(point).map(fix).join(' ');

const line = (a, b, width = 7) => `<path d="M${pair(a)}L${pair(b)}" stroke="${TONE}" stroke-width="${fix(width * SCALE)}" fill="none"/>`;
const frame = (x0, y0, x1, y1, width = 7) => `<path d="M${pair([x0, y0])}L${pair([x1, y0])}L${pair([x1, y1])}L${pair([x0, y1])}Z" stroke="${TONE}" stroke-width="${fix(width * SCALE)}" fill="none"/>`;
const solid = (x0, y0, x1, y1, opacity = 1) => `<path d="M${pair([x0, y0])}L${pair([x1, y0])}L${pair([x1, y1])}L${pair([x0, y1])}Z" fill="${TONE}" fill-opacity="${opacity}"/>`;
const cross = (x0, y0, x1, y1) => line([x0, y0], [x1, y1], 4) + line([x0, y1], [x1, y0], 4);

const parts = [
  // 外牆與入口方向標記（PPT 小圖上方的 ▼）
  frame(135, 80, 880, 750, 8),
  `<path d="M${pair([280, 15])}L${pair([315, 15])}L${pair([297, 45])}Z" fill="${TONE}"/>`,
  // 北側門廳隔間與核心牆
  frame(135, 80, 450, 210, 6),
  line([455, 215], [455, 590], 6),
  // LED 牆那一大塊（PPT 上是灰底，這裡用淡色填滿）
  solid(460, 100, 870, 470, 0.22),
  // 四根柱子
  solid(455, 80, 520, 150), solid(840, 80, 920, 160), solid(445, 755, 525, 835), solid(840, 755, 920, 835),
  // 電梯：外殼與兩部車廂
  frame(55, 525, 230, 760, 6),
  frame(95, 555, 195, 640, 5), cross(95, 555, 195, 640),
  frame(95, 650, 195, 740, 5), cross(95, 650, 195, 740),
  // 樓梯：核心井＋下段（直條）＋上段（橫條）
  frame(530, 485, 700, 598, 5), cross(530, 485, 700, 598),
  frame(520, 600, 705, 745, 5),
  ...[541, 562, 583, 604, 625, 646, 667, 688].map((x) => line([x, 600], [x, 745], 3)),
  frame(705, 485, 835, 598, 5),
  ...[505, 523, 541, 559, 577].map((y) => line([705, y], [835, y], 3)),
];

/** 由 PPT 的標籤位置判斷：1F大門 在門廳北半、1F電梯前 在靠電梯的南半 */
const ZONE_BOUNDS = [
  { name: '1F 大門', box: [140, 85, 445, 400] },
  { name: '1F 電梯前', box: [140, 405, 445, 740] },
];

const rectPoints = ([x0, y0, x1, y1]) => [[x0, y0], [x1, y0], [x1, y1], [x0, y1]].map(toPlan).map(([x, y]) => [fix(x), fix(y)]);

/**
 * @returns {{ baseSvg: string, zones: {name: string, points: number[][]}[], outline: number[][] }}
 *   baseSvg 是要接在底圖群組後面的 SVG 片段；outline 是小圖的外框（判定設備能不能放用）
 */
export function buildInset1f() {
  return {
    baseSvg: `<g id="b1-1f-inset">${parts.join('')}</g>`,
    zones: ZONE_BOUNDS.map(({ name, box }) => ({ name, points: rectPoints(box) })),
    outline: rectPoints([50, 10, 925, 840]),
  };
}
