<script setup lang="ts">
import { ref, computed } from 'vue';
import { useQuery } from '@tanstack/vue-query';
import AdminLayout from '../_components/AdminLayout.vue';
import AdminButton from '../_components/AdminButton.vue';
import AdminDatePicker from '../_components/AdminDatePicker.vue';
import AdminStatusBadge from '../_components/AdminStatusBadge.vue';
import AdminPagination from '../_components/AdminPagination.vue';
import EmptyState from '../_components/EmptyState.vue';
import { ApiError } from '../_services/auth-service';
import { listAlarmsApi } from '../_services/hvac-service';

/** 預設區間「近 3 個月」要用當下時間算，不能寫死日期字串——寫死的日期一過就會讓使用者
 * 一打開頁面就看到「查無異常告警」，還以為系統壞了，其實只是預設區間已經跟今天脫節。 */
function toDateInput(date: Date): string {
  return date.toISOString().slice(0, 10);
}
function defaultDateRange(): [string, string] {
  const today = new Date();
  const from = new Date(today);
  from.setMonth(from.getMonth() - 3);
  return [toDateInput(from), toDateInput(today)];
}

const keyword = ref('');
const [defaultFrom, defaultTo] = defaultDateRange();
const dateFrom = ref(defaultFrom);
const dateTo = ref(defaultTo);
const appliedFrom = ref(dateFrom.value);
const appliedTo = ref(dateTo.value);

const page = ref(1);
const pageSize = ref(20);

/**
 * 日期區間查詢用 useQuery（不是 onMounted 手動 fetch），這樣「查詢」按鈕改變
 * appliedFrom/appliedTo 時會自動重新打 API，跟其他已接真實 API 的頁面一致寫法。
 * `to` 要含當天全部時間，所以送出時補到 23:59:59。
 */
const alarmsQuery = useQuery({
  queryKey: ['alarm-report', appliedFrom, appliedTo],
  queryFn: () => listAlarmsApi('all', `${appliedFrom.value}T00:00:00`, `${appliedTo.value}T23:59:59`),
});

const loadError = computed(() => {
  const err = alarmsQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「告警」查看權限，無法查看歷史報表。'
    : err instanceof Error
      ? err.message
      : '載入告警歷史失敗，請稍後再試。';
});

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString('zh-TW', { hour12: false });
}

const rows = computed(() =>
  (alarmsQuery.data.value ?? []).map((a) => ({
    id: a.id,
    triggeredAt: formatTime(a.startedAt),
    isResolved: a.endedAt !== null,
    deviceName: a.deviceName,
    deviceCode: a.deviceCode,
    location: a.location,
    alarmItem: a.ruleLabel,
  })),
);

const filtered = computed(() => {
  const kw = keyword.value.trim().toLowerCase();
  if (!kw) return rows.value;
  return rows.value.filter(
    (r) => r.deviceName.toLowerCase().includes(kw) || r.deviceCode.toLowerCase().includes(kw),
  );
});

const paged = computed(() => filtered.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value));

function fetchData() {
  appliedFrom.value = dateFrom.value;
  appliedTo.value = dateTo.value;
  page.value = 1;
}

function reset() {
  keyword.value = '';
  [dateFrom.value, dateTo.value] = defaultDateRange();
  appliedFrom.value = dateFrom.value;
  appliedTo.value = dateTo.value;
  page.value = 1;
}

function exportExcel() {
  window.alert('已匯出異常告警報表 (Excel)');
}
</script>

<template>
  <AdminLayout page-title="異常告警報表" current-path="/admin/reports/alarms">
    <div class="flex flex-col gap-4">
      <!-- 篩選列 -->
      <div class="bg-white rounded-xl border border-[#E2E8F0] p-4 flex flex-wrap items-center gap-3">
        <div class="relative w-72">
          <svg class="w-4 h-4 text-[#94A3B8] absolute left-3 top-2.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
          </svg>
          <input
            v-model="keyword"
            type="text"
            placeholder="搜尋冰水主機、FCU 設備名稱、設備編號"
            class="w-full pl-9 pr-3 py-2 text-xs rounded-lg border border-[#CBD5E1] focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8] text-[#334155]"
          />
        </div>
        <div class="flex items-center gap-2 text-xs text-[#64748B]">
          <AdminDatePicker v-model="dateFrom" label="日期區間(起)：" :max="dateTo" />
          <span>～</span>
          <AdminDatePicker v-model="dateTo" label="日期區間(迄)：" :min="dateFrom" />
        </div>
        <AdminButton variant="primary" size="sm" @click="fetchData">查詢</AdminButton>
        <AdminButton variant="secondary" size="sm" @click="reset">重置</AdminButton>
      </div>

      <div v-if="loadError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ loadError }}</div>

      <!-- 表格 -->
      <div v-else class="bg-white rounded-xl border border-[#E2E8F0] overflow-hidden">
        <div class="flex justify-end px-4 pt-4">
          <AdminButton variant="primary" size="sm" @click="exportExcel">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
            </svg>
            匯出 Excel
          </AdminButton>
        </div>

        <div v-if="alarmsQuery.isPending.value" class="p-6 text-center text-sm text-[#94A3B8]">載入中…</div>

        <template v-else>
          <div class="overflow-x-auto mt-3">
            <table class="w-full text-left text-[13px] text-[#334155]">
              <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
                <tr>
                  <th class="px-4 py-3 font-semibold whitespace-nowrap">通報日期時間</th>
                  <th class="px-4 py-3 font-semibold">設備名稱</th>
                  <th class="px-4 py-3 font-semibold">設備編號</th>
                  <th class="px-4 py-3 font-semibold">安裝位置</th>
                  <th class="px-4 py-3 font-semibold">告警項目</th>
                  <th class="px-4 py-3 font-semibold">狀態</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="r in paged" :key="r.id" class="border-b border-[#F1F5F9] hover:bg-slate-50/70">
                  <td class="px-4 py-3 font-tabular text-[#64748B] whitespace-nowrap">{{ r.triggeredAt }}</td>
                  <td class="px-4 py-3 font-medium">{{ r.deviceName }}</td>
                  <td class="px-4 py-3 font-tabular text-[#64748B]">{{ r.deviceCode }}</td>
                  <td class="px-4 py-3 text-[#64748B]">{{ r.location }}</td>
                  <td class="px-4 py-3 text-[#64748B]">{{ r.alarmItem }}</td>
                  <td class="px-4 py-3">
                    <AdminStatusBadge :status="r.isResolved ? '已排除' : 'ABNORMAL'" />
                  </td>
                </tr>
              </tbody>
            </table>
          </div>

          <EmptyState v-if="filtered.length === 0" title="查無異常告警" description="所選條件下沒有任何告警紀錄。" />

          <AdminPagination v-else :total="filtered.length" v-model:page="page" v-model:page-size="pageSize" />
        </template>
      </div>
    </div>
  </AdminLayout>
</template>
