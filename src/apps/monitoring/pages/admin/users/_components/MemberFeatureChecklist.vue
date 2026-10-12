<script setup lang="ts">
import { computed, ref } from 'vue';
import { MEMBER_FEATURE_OPTIONS } from '../../_services/user-service';
import { FEATURE_NOTES, featureHints, toggleFeature } from '../../_services/member-feature-rules';

const props = defineProps<{ modelValue: string[]; disabled?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: string[]] }>();

const selected = computed(() => new Set(props.modelValue));
const allChecked = computed(() => MEMBER_FEATURE_OPTIONS.every((o) => selected.value.has(o.code)));

// 連動勾選／取消時的說明，下一次操作才清掉，讓使用者看得到剛剛發生了什麼。
const notice = ref('');
const hints = computed(() => featureHints(props.modelValue));

function toggle(code: string) {
  const result = toggleFeature(props.modelValue, code, !selected.value.has(code));
  notice.value = result.notice;
  emit('update:modelValue', result.features);
}
function setAll(checked: boolean) {
  notice.value = '';
  emit('update:modelValue', checked ? MEMBER_FEATURE_OPTIONS.map((o) => o.code) : []);
}
</script>

<template>
  <div>
    <div class="flex items-center justify-between mb-2">
      <h4 class="text-sm font-bold text-[#1A202C]">可使用的功能</h4>
      <button v-if="!disabled" type="button" class="link-action text-xs" @click="setAll(!allChecked)">
        {{ allChecked ? '全部取消' : '全部勾選' }}
      </button>
    </div>
    <div class="rounded-lg border border-[#E2E8F0] divide-y divide-[#E2E8F0]">
      <label
        v-for="opt in MEMBER_FEATURE_OPTIONS"
        :key="opt.code"
        class="flex items-center justify-between px-3 py-2.5 text-sm"
        :class="disabled ? 'cursor-not-allowed opacity-60' : 'cursor-pointer'"
      >
        <span class="min-w-0 pr-3">
          <span class="text-[#334155]">{{ opt.name }}</span>
          <span v-if="hints.warnings[opt.code]" class="block text-xs text-[#B45309] mt-0.5">{{ hints.warnings[opt.code] }}</span>
          <span v-else-if="FEATURE_NOTES[opt.code]" class="block text-xs text-[#94A3B8] mt-0.5">{{ FEATURE_NOTES[opt.code] }}</span>
        </span>
        <input
          type="checkbox"
          class="w-4 h-4 rounded accent-[#4C7DF0] shrink-0"
          :checked="selected.has(opt.code)"
          :disabled="disabled"
          @change="toggle(opt.code)"
        />
      </label>
    </div>
    <p v-if="notice" class="text-xs text-[#2563EB] mt-1.5">{{ notice }}</p>
    <p v-if="hints.general" class="text-xs text-[#B45309] mt-1.5">{{ hints.general }}</p>
    <p v-if="allChecked" class="text-xs text-[#10B981] font-medium mt-1.5">全部功能都已勾選：這個使用者就是「場館管理員」。</p>
    <p v-else class="text-xs text-[#94A3B8] mt-1.5">
      勾選後即可使用該功能的所有操作（查看、新增、修改、刪除）。全部勾選就是「場館管理員」。
    </p>
  </div>
</template>
