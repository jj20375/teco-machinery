<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { MEMBER_FEATURE_OPTIONS, READ_ONLY_CAPABLE_FEATURES } from '../../_services/user-service';
import { FEATURE_NOTES, featureHints, toggleFeature } from '../../_services/member-feature-rules';
import { getSessionApi } from '../../_services/auth-service';

const props = defineProps<{ modelValue: string[]; readOnly: string[]; disabled?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: string[]]; 'update:readOnly': [value: string[]] }>();

const selected = computed(() => new Set(props.modelValue));
const readOnlySet = computed(() => new Set(props.readOnly));

// 操作者自己沒有的功能不能變更（加上或拿掉都不行），跟後端 NormalizeFeatures 同一條規則；這裡只是先鎖住，防線在後端。
// session 只在瀏覽器讀得到，掛載後才取，SSR 與第一次渲染一律當成「都能改」，避免 hydration 結果不一致。
const grants = ref<Map<string, string[]> | null>(null);
onMounted(() => {
  const session = getSessionApi();
  if (session?.user.scopeKind === 'merchant') grants.value = new Map(session.user.grants.map((g) => [g.code, g.actions]));
});
function canManage(code: string): boolean {
  return grants.value === null || (grants.value.get(code)?.includes('read') ?? false);
}
function canManageEdit(code: string): boolean {
  return grants.value === null || (grants.value.get(code)?.includes('update') ?? false);
}
const manageable = computed(() => MEMBER_FEATURE_OPTIONS.filter((o) => canManage(o.code)).map((o) => o.code as string));
const allManageableChecked = computed(() => manageable.value.every((c) => selected.value.has(c)));
const isAdmin = computed(() => MEMBER_FEATURE_OPTIONS.every((o) => selected.value.has(o.code)) && props.readOnly.length === 0);
const hasLocked = computed(() => manageable.value.length < MEMBER_FEATURE_OPTIONS.length);

// 連動勾選／取消時的說明，下一次操作才清掉，讓使用者看得到剛剛發生了什麼。
const notice = ref('');
const hints = computed(() => featureHints(props.modelValue));

function emitSelection(features: string[]) {
  emit('update:modelValue', features);
  // 取消勾選的功能不再保留「僅檢視」；新勾選的可唯讀功能，操作者自己不能編輯時只能給僅檢視。
  const next = new Set(features);
  const readOnly = props.readOnly.filter((c) => next.has(c));
  for (const code of READ_ONLY_CAPABLE_FEATURES) {
    if (next.has(code) && !selected.value.has(code) && !canManageEdit(code) && !readOnly.includes(code)) readOnly.push(code);
  }
  emit('update:readOnly', readOnly);
}

function toggle(code: string) {
  if (!canManage(code)) return;
  const result = toggleFeature(props.modelValue, code, !selected.value.has(code));
  // 連動帶入／取消的功能如果有操作者不能動的，整個動作就不做，否則送出時會被後端擋下。
  const before = selected.value;
  const after = new Set(result.features);
  const blocked = MEMBER_FEATURE_OPTIONS.filter((o) => !canManage(o.code) && before.has(o.code) !== after.has(o.code));
  if (blocked.length > 0) {
    const name = MEMBER_FEATURE_OPTIONS.find((o) => o.code === code)?.name;
    notice.value = `${name}會連動${blocked.map((o) => o.name).join('、')}，但您本身沒有這些權限，無法變更。`;
    return;
  }
  notice.value = result.notice;
  emitSelection(result.features);
}

function setAll(checked: boolean) {
  notice.value = '';
  const locked = MEMBER_FEATURE_OPTIONS.filter((o) => !canManage(o.code) && selected.value.has(o.code)).map((o) => o.code as string);
  emitSelection(MEMBER_FEATURE_OPTIONS.map((o) => o.code as string)
    .filter((c) => locked.includes(c) || (checked && manageable.value.includes(c))));
}

function setReadOnly(code: string, readOnly: boolean) {
  if (!canManageEdit(code)) return;
  emit('update:readOnly', readOnly ? [...new Set([...props.readOnly, code])] : props.readOnly.filter((c) => c !== code));
}
</script>

<template>
  <div>
    <div class="flex items-center justify-between mb-2">
      <h4 class="text-sm font-bold text-[#1A202C]">可使用的功能</h4>
      <button v-if="!disabled && manageable.length > 0" type="button" class="link-action text-xs" @click="setAll(!allManageableChecked)">
        {{ allManageableChecked ? '全部取消' : '全部勾選' }}
      </button>
    </div>
    <div class="rounded-lg border border-[#E2E8F0] divide-y divide-[#E2E8F0]">
      <div v-for="opt in MEMBER_FEATURE_OPTIONS" :key="opt.code" class="px-3 py-2.5 text-sm">
        <label
          class="flex items-center justify-between"
          :class="disabled || !canManage(opt.code) ? 'cursor-not-allowed opacity-60' : 'cursor-pointer'"
          :title="canManage(opt.code) ? undefined : '您本身沒有這項權限，無法替他人開啟或取消'"
        >
          <span class="min-w-0 pr-3">
            <span class="text-[#334155]">{{ opt.name }}</span>
            <span v-if="!canManage(opt.code)" class="block text-xs text-[#94A3B8] mt-0.5">您本身沒有這項權限，無法變更</span>
            <span v-else-if="hints.warnings[opt.code]" class="block text-xs text-[#B45309] mt-0.5">{{ hints.warnings[opt.code] }}</span>
            <span v-else-if="FEATURE_NOTES[opt.code]" class="block text-xs text-[#94A3B8] mt-0.5">{{ FEATURE_NOTES[opt.code] }}</span>
          </span>
          <input
            type="checkbox"
            class="w-4 h-4 rounded accent-[#4C7DF0] shrink-0"
            :checked="selected.has(opt.code)"
            :disabled="disabled || !canManage(opt.code)"
            @change="toggle(opt.code)"
          />
        </label>
        <div v-if="READ_ONLY_CAPABLE_FEATURES.includes(opt.code) && selected.has(opt.code)" class="mt-2 flex items-center gap-2">
          <div class="inline-flex rounded-md border border-[#CBD5E1] overflow-hidden text-xs" role="radiogroup" :aria-label="`${opt.name}的使用範圍`">
            <button
              v-for="level in [{ ro: false, label: '可編輯' }, { ro: true, label: '僅檢視' }]"
              :key="level.label"
              type="button"
              role="radio"
              :aria-checked="readOnlySet.has(opt.code) === level.ro"
              class="px-3 py-1 transition-colors"
              :class="readOnlySet.has(opt.code) === level.ro ? 'bg-[#4C7DF0] text-white' : 'bg-white text-[#475569] hover:bg-slate-50'"
              :disabled="disabled || !canManageEdit(opt.code)"
              @click="setReadOnly(opt.code, level.ro)"
            >{{ level.label }}</button>
          </div>
          <span v-if="!canManageEdit(opt.code)" class="text-xs text-[#94A3B8]">您本身只能檢視，無法開放編輯</span>
          <span v-else-if="readOnlySet.has(opt.code)" class="text-xs text-[#94A3B8]">只能查看配置，不能新增、移動或儲存</span>
        </div>
      </div>
    </div>
    <p v-if="notice" class="text-xs text-[#2563EB] mt-1.5">{{ notice }}</p>
    <p v-if="hints.general" class="text-xs text-[#B45309] mt-1.5">{{ hints.general }}</p>
    <p v-if="hasLocked" class="text-xs text-[#94A3B8] mt-1.5">灰色的功能是您本身沒有的權限，維持對方目前的狀態，不能開啟或取消。</p>
    <p v-if="isAdmin" class="text-xs text-[#10B981] font-medium mt-1.5">全部功能都已勾選：這個使用者就是「場館管理員」。</p>
    <p v-else class="text-xs text-[#94A3B8] mt-1.5">
      勾選後即可使用該功能的所有操作（查看、新增、修改、刪除）；空間設備配置可另外設成「僅檢視」。全部勾選且都可編輯就是「場館管理員」。
    </p>
  </div>
</template>
