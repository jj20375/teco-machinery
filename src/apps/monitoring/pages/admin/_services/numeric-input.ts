/**
 * @file numeric-input.ts
 * 東元電機智慧環境監控 - 數字輸入框防呆指令 `v-numeric-input`
 *
 * `<input type="number">` 只擋掉大部分字母，仍放行 e、E、+、-，而且內容不合法（例如 "1e"、"--3"、
 * 貼上一段文字）時瀏覽器會把 `value` 當成空字串——表單端分不出「使用者清空」跟「打了不合法的字」，
 * 結果就是被當成「不設定」存進去。這個指令在輸入當下就擋掉不合法字元，貼上時只接受合法數字，
 * 離開欄位時若仍殘留瀏覽器判定不合法的內容（例如單獨一個 "-"）就還原成最後一次合法的值。
 *
 * 用法：`<input v-numeric-input type="number" />`（小數、可負數）；
 * 整數、不可負數用 `v-numeric-input="{ integer: true, negative: false }"`。
 */

import type { Directive } from 'vue';

export interface NumericInputOptions {
  /** 只允許整數（不接受小數點）。預設 false。 */
  integer?: boolean;
  /** 允許負號。預設 true。 */
  negative?: boolean;
}

const lastValidKey = Symbol('numericInputLastValid');
type NumericEl = HTMLInputElement & { [lastValidKey]?: string };

/** number 輸入框拿不到游標位置（selectionStart 是 null），只能用 document selection 判斷「整欄被選取、即將被取代」。 */
function replacesAll(el: HTMLInputElement): boolean {
  return el.value !== '' && document.getSelection()?.toString() === el.value;
}

/**
 * `current` 是插入前欄位的內容。負號只能出現在最前面；number 輸入框打到一半（例如只有 "-"）value 會是
 * 空字串，所以用 `current === ''` 判斷「還沒有內容」，再用 badInput 排除「已經打了 "-" 還要再打 "-"」。
 */
function isAllowedChar(current: string, ch: string, opts: NumericInputOptions, badInput: boolean): boolean {
  if (ch >= '0' && ch <= '9') return true;
  if (ch === '.') return !opts.integer && !current.includes('.');
  if (ch === '-') return opts.negative !== false && current === '' && !badInput;
  return false;
}

function isValidNumberText(text: string, opts: NumericInputOptions): boolean {
  const pattern = opts.integer
    ? opts.negative === false ? /^\d+$/ : /^-?\d+$/
    : opts.negative === false ? /^(\d+\.?\d*|\.\d+)$/ : /^-?(\d+\.?\d*|\.\d+)$/;
  return pattern.test(text);
}

function remember(el: NumericEl) {
  if (!el.validity.badInput) el[lastValidKey] = el.value;
}

/** 還原成最後一次合法的值，並通知 v-model（程式改 value 不會自動觸發 input 事件）。 */
function setValue(el: NumericEl, value: string) {
  el.value = value;
  el.dispatchEvent(new Event('input', { bubbles: true }));
}

export const vNumericInput: Directive<HTMLInputElement, NumericInputOptions | undefined> = {
  mounted(el: NumericEl, binding) {
    const opts = binding.value ?? {};
    remember(el);

    el.addEventListener('beforeinput', (e) => {
      // 刪除、復原等沒有帶字元的輸入類型不用管；只擋「插入」。
      if (!e.inputType.startsWith('insert') || e.inputType === 'insertFromPaste' || e.data == null) return;
      const replacing = replacesAll(el);
      const base = replacing ? '' : el.value;
      const badInput = !replacing && el.validity.badInput;
      // 一次插入多個字元（輸入法確認、自動填入）：濾掉不合法的字元後整串套用，濾完不是合法數字就整串捨棄。
      if (e.data.length > 1) {
        e.preventDefault();
        let next = base;
        for (const ch of e.data) {
          if (isAllowedChar(next, ch, opts, false)) next += ch;
        }
        if (next !== base && isValidNumberText(next, opts)) setValue(el, next);
        return;
      }
      if (!isAllowedChar(base, e.data, opts, badInput)) e.preventDefault();
    });

    el.addEventListener('paste', (e) => {
      e.preventDefault();
      const text = (e.clipboardData?.getData('text') ?? '').trim();
      if (isValidNumberText(text, opts)) setValue(el, text);
    });

    // mounted 當下 v-model 可能還沒把值寫進 DOM，所以進入欄位時再記一次，這時畫面上看到的就是要還原的值。
    el.addEventListener('focus', () => remember(el));
    el.addEventListener('input', () => remember(el));

    el.addEventListener('blur', () => {
      if (el.validity.badInput) setValue(el, el[lastValidKey] ?? '');
    });
  },
  // 表單載入／重設時 v-model 會從外面改 value，同步更新「最後一次合法的值」。正在編輯的欄位不能在這裡記：
  // 打到不合法內容時 v-model 會把 DOM 的 value 重設成空字串（badInput 旗標跟著消失），記下來就會把「空」當成合法值。
  updated(el: NumericEl) {
    if (document.activeElement !== el) remember(el);
  },
};
