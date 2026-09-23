// 各樓層的圖面設定。
//
// 兩層都是「兩個檔案」：沒有紅框的精簡版當底圖、有紅框的當分區來源。兩檔的 viewBox
// 與座標完全一致，所以紅框直接就是分區座標，不需要任何對位計算。
//
// 底圖一律用**精簡版**，這很重要：完整的設備配置圖（尤其 B2 幾乎整層是停車場）線稿
// 密到平均亮度會逼近分區填色，分區會糊進背景看不清楚。精簡版只留隔間輪廓。
//
// `pick.base` / `pick.zones` 分開設定，是因為兩層的紅框檔匯出管道不同：
// - B1 紅框檔是 Illustrator 匯出，紅框和底圖混在同一層，只能靠 class 篩（`kind: 'class'`）
// - B2 紅框檔是 PDF 轉出，設計端已把紅框放進獨立的 <g>（`kind: 'group'`）
//
// 要調整分區就回去改紅框檔再重跑 `npm run extract:floor-plan`。

/** B1 圖面的比例尺基準：以 B1019 訓練打擊空間 328 ㎡ 對應 25,964 單位² 校準 */
const B1_UNITS_PER_SQM = 79.16;

/**
 * B2 圖面比 B1 大一圈（同一棟建築、不同匯出縮放），所以比例尺要另外算。
 * 用兩張圖的建築外框面積比回推：558,659 ÷ 313,950 = 1.7795 倍面積，
 * 亦即線性 1.334 倍，故 79.16 x 1.7795 ≈ 140.86。
 * 註：兩張圖的長寬比差約 4%（B1 1.296、B2 1.349），推測是 B1「精簡版」在邊緣
 * 修掉了一些外牆細節，所以取面積比（等於兩軸的幾何平均）而不是單一邊長比。
 * 與 B1 一樣，換算出的 ㎡ 僅供判讀分區大小，不是施工尺寸。
 */
const B2_UNITS_PER_SQM = 140.86;

export const FLOOR_PLANS = {
  B1: {
    base: 'assets/cad/CLUB-M-B1.svg',
    zones: 'assets/cad/CLUB-M-B1-zones.svg',
    pick: {
      /** 底圖：頂層 <g> 且 id 不是底線開頭（底線開頭的是 display:none 的 CAD 圖層） */
      base: { kind: 'layers' },
      /** 紅框的 class；Illustrator 匯出時給的樣式名稱（fill:#FFFFFF; stroke:#FF0000） */
      zones: { kind: 'class', className: 'st32' },
    },
    /** 編號依圖面位置自動給（由上而下、由左而右），產生 B1-Z01…B1-Z72 */
    numbering: 'position',
    idPrefix: 'B1-Z',
    unitsPerSqm: B1_UNITS_PER_SQM,
    /**
     * 小於此面積的紅框不視為分區（㎡）。門檻取 4 ㎡ 的理由：再小的框連一台 FCU 標記
     * 加上間距都擺不下，留著只會變成點得到卻放不了東西的死區。被濾掉的框會列在報告裡。
     */
    minZoneSqm: 4,
    /**
     * 紅框的短邊小於此值也不視為分區（圖面單位，約 1.35 m）。
     * 只看面積會漏掉細長的框：例如 10 x 40 單位的管道間面積有 4.8 ㎡ 過得了門檻，
     * 但寬度放不下一台 FCU 標記（11 單位寬），點得到卻放不了東西。
     */
    minZoneWidth: 12,
    /**
     * 分區中文名稱。編號由腳本自動給，這裡只放要覆寫的名稱，沒填的就顯示編號。
     * 例：'B1-Z01': 'VIP 包廂 A'
     */
    names: {},
  },

  B2: {
    /** 精簡版（Illustrator 匯出），沒有紅框、只留隔間輪廓，密度和 B1 底圖一致 */
    base: 'assets/cad/CLUB-M-B2.svg',
    /** 設備配置圖（PDF 轉出），只拿裡面的紅框；它的完整線稿太密不適合當底圖 */
    zones: 'assets/cad/CLUB-M-B2-zones.svg',
    pick: {
      base: { kind: 'layers' },
      /** 41 個紅框都在這個 <g> 底下，每個 polygon 還帶著設計端給的 id */
      zones: { kind: 'group', group: 'B2-red-areas' },
    },
    /**
     * 沿用設計稿的編號（B2-E01…、N01…、W01…、C01…、S01…，E東/N北/W西/C中/S南）。
     * 不重編成 B2-Z01… 是因為重編會把方位資訊洗掉。順序保持原圖的文件順序。
     */
    numbering: 'id',
    unitsPerSqm: B2_UNITS_PER_SQM,
    /**
     * B2 不過濾小框：41 個框都是設計端逐一畫好並編號的，濾掉任何一個都會讓編號跳號、
     * 也等於擅自推翻設計端的判斷。太小塞不下設備的框由報告列出來提醒，而不是直接丟掉。
     */
    minZoneSqm: 0,
    minZoneWidth: 0,
    names: {},
  },
};

/** 設備標記尺寸是以 B1 圖面單位定的，其他樓層依比例尺換算（線性比 = 面積比開根號） */
export function unitScaleOf(floor) {
  return Math.sqrt(FLOOR_PLANS[floor].unitsPerSqm / B1_UNITS_PER_SQM);
}

export const FLOOR_IDS = Object.keys(FLOOR_PLANS);
