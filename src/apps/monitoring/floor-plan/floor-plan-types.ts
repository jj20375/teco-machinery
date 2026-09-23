// 產生檔（*-areas.generated.ts）與 floor-plan.ts 共用的基本型別。
// 獨立成一個檔案是為了讓產生檔不用反過來 import floor-plan.ts，避免循環相依。

export type Point = [number, number];

/** 由 scripts/extract-floor-plan.mjs 從 CAD 紅框產生的分區 */
export interface GeneratedArea {
  id: string;
  /** 中文名稱；尚未命名時為空字串，UI 會退回顯示編號 */
  name: string;
  points: Point[];
  label: Point;
  /** 圖面單位² */
  area: number;
  /** 換算面積（㎡），僅供判讀分區大小，非施工尺寸 */
  sqm: number;
}
