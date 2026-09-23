/**
 * @file threshold-validation.ts
 * 東元電機智慧環境監控 - 告警門檻設定表單的防呆驗證（yup schema，供 vee-validate 使用）
 *
 * 後端 ThresholdEndpoints.cs 把每個上/下限各自存成獨立的 alarm_rule 列（LessThan/GreaterThan
 * 分開兩條規則），完全沒有檢查「下限不能大於上限」這種語意——留空代表不設定該側門檻是合法的，
 * 但兩側都有值時下限大於上限沒有任何意義（例如回水溫度 0.6~0.5，代表「小於 0.6」跟「大於 0.5」
 * 兩條規則同時生效，等於全部溫度都在告警），後端不擋，這裡在前端擋掉。
 */

import * as yup from 'yup';

/**
 * 數字輸入框留空時 `v-model.number` 給的是空字串，yup 預設的 number() 會把空字串當成
 * 驗證失敗（NaN）而不是「沒填」；這裡統一轉成 null，跟 threshold-service.ts 的
 * nullifyEmpty() 是同一個道理，只是這裡要在驗證階段就處理，不能等到送出前才轉。
 */
const nullableNumber = yup
  .number()
  .transform((value, originalValue) => (originalValue === '' || originalValue === null ? null : value))
  .nullable()
  .typeError('請輸入數字');

/**
 * 建一個「下限不能大於上限」的 yup test，掛在「上限」欄位上——這樣 vee-validate 顯示錯誤時
 * 會自然出現在上限輸入框下方，跟畫面上「最低 ~ 最高」的閱讀順序一致。兩側只要有一側留空
 * 就不比較（留空代表不設定該側門檻，是合法狀態）。
 */
function maxNotLessThanMin(minField: string, label: string) {
  return nullableNumber.test('max-gte-min', `${label}上限不能小於下限`, function (max) {
    const min = (this.parent as Record<string, number | null>)[minField];
    if (min == null || max == null) return true;
    return max >= min;
  });
}

export const chillerThresholdSchema = yup.object({
  supplyTempMin: nullableNumber,
  supplyTempMax: maxNotLessThanMin('supplyTempMin', '出水溫度'),
  returnTempMin: nullableNumber,
  returnTempMax: maxNotLessThanMin('returnTempMin', '回水溫度'),
  tempDiffMin: nullableNumber,
  tempDiffMax: maxNotLessThanMin('tempDiffMin', '溫度差'),
  maintenanceHoursLimit: nullableNumber.min(0, '運轉保護值不能是負數'),
});

export const fcuThresholdSchema = yup.object({
  roomTempMin: nullableNumber,
  roomTempMax: maxNotLessThanMin('roomTempMin', '室內溫度'),
});
