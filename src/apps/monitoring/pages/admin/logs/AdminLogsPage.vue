<script setup lang="ts">
import { ref, computed } from 'vue';
import { useQuery } from '@tanstack/vue-query';
import AdminLayout from '../_components/AdminLayout.vue';
import AdminButton from '../_components/AdminButton.vue';
import AdminDatePicker from '../_components/AdminDatePicker.vue';
import AdminPagination from '../_components/AdminPagination.vue';
import EmptyState from '../_components/EmptyState.vue';
import { ApiError } from '../_services/auth-service';
import { listOperationLogsApi } from '../_services/operation-log-service';

/** 預設區間「近 3 個月」要用當下時間算，不能寫死日期字串——理由跟 AdminAlarmReportPage.vue 同一個。 */
function toDateInput(date: Date): string {
  return date.toISOString().slice(0, 10);
}
function defaultDateRange(): [string, string] {
  const today = new Date();
  const from = new Date(today);
  from.setMonth(from.getMonth() - 3);
  return [toDateInput(from), toDateInput(today)];
}

const [defaultFrom, defaultTo] = defaultDateRange();
const dateFrom = ref(defaultFrom);
const dateTo = ref(defaultTo);

// 查詢真的送出去的區間跟輸入框的草稿分開：只有按「查詢」才觸發 refetch，
// 不要使用者選個日期、還沒選第二個，就先打一次半套的區間。
const appliedFrom = ref(dateFrom.value);
const appliedTo = ref(dateTo.value);
function applyFilter() {
  appliedFrom.value = dateFrom.value;
  appliedTo.value = dateTo.value;
}

const logsQuery = useQuery({
  queryKey: computed(() => ['operation-logs', appliedFrom.value, appliedTo.value]),
  queryFn: () => listOperationLogsApi(appliedFrom.value, appliedTo.value),
});

const logs = computed(() => logsQuery.data.value ?? []);
const loadError = computed(() => {
  const err = logsQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「操作紀錄」權限，無法查看。'
    : err instanceof Error
      ? err.message
      : '載入操作紀錄失敗，請稍後再試。';
});

const page = ref(1);
const pageSize = ref(20);
const pagedLogs = computed(() => logs.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value));

function formatTimestamp(iso: string): string {
  return new Date(iso).toLocaleString('zh-TW', { hour12: false });
}
</script>

<template>
  <AdminLayout page-title="操作紀錄" current-path="/admin/operation-log">
    <div class="flex flex-col gap-4">
      <div v-if="loadError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ loadError }}</div>

      <!-- 篩選列 -->
      <div class="bg-white rounded-xl border border-[#E2E8F0] p-4 flex flex-wrap items-center gap-3">
        <div class="flex items-center gap-2 text-xs text-[#64748B]">
          <AdminDatePicker v-model="dateFrom" label="日期區間(起)：" :max="dateTo" />
          <span>～</span>
          <AdminDatePicker v-model="dateTo" label="日期區間(迄)：" :min="dateFrom" />
          <span class="text-[#94A3B8]">最大範圍：一年</span>
        </div>
        <AdminButton variant="primary" size="sm" @click="applyFilter">查詢</AdminButton>
      </div>

      <!-- 表格 -->
      <div class="bg-white rounded-xl border border-[#E2E8F0] overflow-hidden">
        <div class="overflow-x-auto">
          <table class="w-full text-left text-[13px] text-[#334155]">
            <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
              <tr>
                <th class="px-4 py-3 font-semibold whitespace-nowrap">時間</th>
                <th class="px-4 py-3 font-semibold">使用者</th>
                <th class="px-4 py-3 font-semibold">操作類別</th>
                <th class="px-4 py-3 font-semibold">操作內容</th>
                <th class="px-4 py-3 font-semibold whitespace-nowrap">IP位址</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="log in pagedLogs" :key="log.id" class="border-b border-[#F1F5F9] hover:bg-slate-50/70">
                <td class="px-4 py-3 font-tabular text-[#64748B] whitespace-nowrap">{{ formatTimestamp(log.timestamp) }}</td>
                <td class="px-4 py-3">{{ log.userFullName }}</td>
                <td class="px-4 py-3">
                  <span
                    class="px-2 py-0.5 rounded text-xs font-medium"
                    :class="log.isSuccess ? 'bg-[#F1F5F9] text-[#64748B]' : 'bg-[#FFF5F5] text-[#FF4757]'"
                  >
                    {{ log.actionType }}{{ log.isSuccess ? '' : '（失敗）' }}
                  </span>
                </td>
                <td class="px-4 py-3">{{ log.content }}</td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ log.ipAddress ?? '—' }}</td>
              </tr>
            </tbody>
          </table>
        </div>

        <EmptyState v-if="!loadError && logs.length === 0" title="查無操作紀錄" description="所選日期區間內沒有任何操作紀錄。" />

        <AdminPagination
          v-else-if="!loadError"
          :total="logs.length"
          v-model:page="page"
          v-model:page-size="pageSize"
        />
      </div>
    </div>
  </AdminLayout>
</template>
