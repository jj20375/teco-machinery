/**
 * @file report-export.ts
 * 東元電機智慧環境監控 - 統計報表「匯出 Excel」共用工具
 *
 * 在瀏覽器端直接產生 .xlsx（不經過後端），內容是報表目前篩選條件下的全部資料（不是只有目前這一頁）。
 * 套件用動態 import：只有按下「匯出」才會載入，不增加報表頁本身的載入大小。
 */

import { ref } from 'vue';

export interface ExportColumn<T> {
  header: string;
  /** 欄寬（字元數）。 */
  width: number;
  value: (row: T) => string | number | null | undefined;
  /** 數字欄位的顯示格式，例如 '0.0'；只對 number 有作用。 */
  format?: string;
}

/** 檔名不能含的字元換成「-」，其餘照原樣（中文檔名沒問題）。 */
export function reportFileName(...parts: string[]): string {
  const name = parts
    .map((p) => p.replace(/[\\/:*?"<>|]/g, '-').trim())
    .filter(Boolean)
    .join('_');
  return `${name}.xlsx`;
}

export async function downloadReportXlsx<T>(options: {
  fileName: string;
  sheet: string;
  columns: ExportColumn<T>[];
  rows: readonly T[];
}): Promise<void> {
  // 沒資料就明講，不要默默下載一個只有表頭的空檔案，使用者會以為匯出壞了。
  if (options.rows.length === 0) throw new Error('目前的篩選條件下沒有可匯出的資料。');

  const { default: writeXlsxFile } = await import('write-excel-file/browser');
  const header = options.columns.map((c) => ({
    value: c.header,
    fontWeight: 'bold' as const,
    backgroundColor: '#E2E8F0',
    align: 'center' as const,
  }));
  const body = options.rows.map((row) =>
    options.columns.map((c) => {
      const v = c.value(row);
      if (v === null || v === undefined || v === '') return null;
      return typeof v === 'number' ? { value: v, type: Number, format: c.format } : String(v);
    }),
  );

  await writeXlsxFile([header, ...body], {
    sheet: options.sheet,
    columns: options.columns.map((c) => ({ width: c.width })),
    stickyRowsCount: 1,
  }).toFile(options.fileName);
}

/** 匯出按鈕共用的狀態：匯出中停用按鈕、失敗時留下訊息給頁面顯示。 */
export function useReportExport() {
  const exporting = ref(false);
  const exportError = ref('');
  async function run(task: () => Promise<void>) {
    if (exporting.value) return;
    exporting.value = true;
    exportError.value = '';
    try {
      await task();
    } catch (err) {
      exportError.value = err instanceof Error ? err.message : '匯出失敗，請稍後再試。';
    } finally {
      exporting.value = false;
    }
  }
  return { exporting, exportError, run };
}
