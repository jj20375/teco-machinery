/**
 * @file auth-service.ts
 * 東元電機智慧環境監控 - 後台登入與工作階段服務
 * 嚴格遵守 Metat 規範：所有對外呼叫函式一律以 *Api 結尾
 *
 * 打的是 backend/ 那套真實 API（見 ../../../../../../backend/README.md 的「權限機制」章節），
 * 不是這個檔案旁邊的 admin-mock-service.ts。透過 Caddy 反代，相對路徑 /api/v1/... 在正式部署
 * （docker compose 的 web 服務）下就是同源請求，不需要額外設定 CORS；但用 `astro dev`
 * 開發伺服器直接跑（沒有經過 Caddy）時，相對路徑打不到後端，需要另外設 dev proxy。
 */

const STORAGE_KEY = 'teco_auth_session';

export interface PermissionGrant {
  code: string;
  actions: string[];
  options: string[];
}

export interface AuthUser {
  id: number;
  username: string;
  displayName: string;
  scopeKind: 'platform' | 'merchant';
  merchantId: number | null;
  merchantName: string | null;
  isPlatformAdmin: boolean;
  isRoleCrudConfigurationEnabled: boolean;
  isRoleOptionConfigurationEnabled: boolean;
  grants: PermissionGrant[];
  permissions: string[];
}

export interface AuthSession {
  accessToken: string;
  /** 舊工作階段（升級前登入的）可能沒有這個欄位，一律當可為 null 處理。 */
  refreshToken: string | null;
  user: AuthUser;
}

interface LoginFailure {
  ok: false;
  message: string;
}

interface LoginSuccess {
  ok: true;
  session: AuthSession;
}

/** 呼叫後端登入 API；成功時會把工作階段存進 localStorage，失敗時回傳可直接顯示的錯誤訊息。 */
export async function loginApi(username: string, password: string): Promise<LoginSuccess | LoginFailure> {
  let response: Response;
  try {
    response = await fetch('/api/v1/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
    });
  } catch {
    return { ok: false, message: '無法連線到伺服器，請確認後端服務是否已啟動。' };
  }

  if (response.status === 401) {
    return { ok: false, message: '帳號或密碼不正確，或帳號已停用。' };
  }
  if (!response.ok) {
    return { ok: false, message: `登入失敗（HTTP ${response.status}），請稍後再試。` };
  }

  const session = (await response.json()) as AuthSession;
  localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
  return { ok: true, session };
}

/**
 * 讀取目前存在 localStorage 的工作階段；沒有或格式毀損一律回傳 null（fail-closed）。
 * Astro 元件在 hydrate 前會先跑一次 SSR（即使是 client:load），SSR 是在 Node.js 執行，
 * 沒有 `localStorage` 這個瀏覽器專屬 API，直接呼叫會整頁噴 ReferenceError——SSR 階段一律
 * 當作「還沒有工作階段」，等瀏覽器端 hydrate 後這個 computed/呼叫會重新算出正確結果。
 */
export function getSessionApi(): AuthSession | null {
  if (typeof localStorage === 'undefined') return null;
  const raw = localStorage.getItem(STORAGE_KEY);
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as AuthSession;
    return parsed?.accessToken ? parsed : null;
  } catch {
    return null;
  }
}

/** 判斷工作階段是否仍在有效期內（只解 JWT payload 做 UX 判斷，不驗簽章——真正的授權檢查在後端）。 */
export function isSessionValidApi(session: AuthSession | null): boolean {
  if (!session?.accessToken) return false;
  try {
    const payloadBase64 = session.accessToken.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const payload = JSON.parse(atob(payloadBase64)) as { exp?: number };
    return typeof payload.exp === 'number' && payload.exp * 1000 > Date.now();
  } catch {
    return false;
  }
}

/** 清掉工作階段（登出）。 */
export function clearSessionApi(): void {
  localStorage.removeItem(STORAGE_KEY);
}


export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
  ) {
    super(message);
  }
}

/**
 * 拿目前工作階段的 refresh token 換一組新的 access token（不用重新輸入密碼）。後端採 token
 * 輪替，成功時回傳的 refreshToken 會不一樣，這裡要整組覆寫回 localStorage，不能只換
 * accessToken——舊的 refresh token 換完就被後端撤銷了，留著沒用。
 *
 * 用同一個 in-flight promise 擋重複呼叫：頁面一次觸發好幾支 API、同時撞到 401 時，
 * 只會真的打一次 /refresh-token，其餘呼叫都等同一個結果，避免用同一顆（换新後即撤銷的）
 * refresh token 打好幾次，導致後面幾次全部失敗。
 */
let refreshInFlight: Promise<boolean> | null = null;

async function refreshSessionApi(): Promise<boolean> {
  if (refreshInFlight) return refreshInFlight;
  refreshInFlight = doRefreshSessionApi().finally(() => {
    refreshInFlight = null;
  });
  return refreshInFlight;
}

async function doRefreshSessionApi(): Promise<boolean> {
  const session = getSessionApi();
  if (!session?.refreshToken) return false;

  let response: Response;
  try {
    response = await fetch('/api/v1/auth/refresh-token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: session.refreshToken }),
    });
  } catch {
    return false;
  }
  if (!response.ok) return false;

  const newSession = (await response.json()) as AuthSession;
  localStorage.setItem(STORAGE_KEY, JSON.stringify(newSession));
  return true;
}

/**
 * 帶 JWT 呼叫後端 API 的共用入口，給其餘 *-service.ts（例如 user-service.ts）使用。
 * 401 時先試著用 refresh token 換一顆新的 access token、重打一次原本的請求；只有在沒有
 * refresh token 或 refresh 也失敗時（密碼重設、角色變更、場館開關變更都會讓 refresh token
 * 一起失效），才清掉工作階段並導去登入頁——呼叫端不需要每個地方都重複處理這個情境。
 */
export async function authorizedFetchApi(path: string, init: RequestInit = {}): Promise<Response> {
  const session = getSessionApi();
  const headers = new Headers(init.headers);
  headers.set('Content-Type', 'application/json');
  if (session?.accessToken) headers.set('Authorization', `Bearer ${session.accessToken}`);

  const response = await fetch(path, { ...init, headers });
  if (response.status === 401) {
    if (await refreshSessionApi()) {
      const retrySession = getSessionApi();
      const retryHeaders = new Headers(init.headers);
      retryHeaders.set('Content-Type', 'application/json');
      if (retrySession?.accessToken) retryHeaders.set('Authorization', `Bearer ${retrySession.accessToken}`);
      const retryResponse = await fetch(path, { ...init, headers: retryHeaders });
      if (retryResponse.status !== 401) return retryResponse;
    }

    clearSessionApi();
    const redirectTo = encodeURIComponent(location.pathname + location.search);
    location.replace(`/login?redirect=${redirectTo}`);
    throw new ApiError('登入已逾期，請重新登入。', 401);
  }
  return response;
}

/**
 * 修改自己的密碼。成功後後端會遞增 AuthVersion，讓目前這顆 token 立即失效——
 * 呼叫端成功後要自己清掉工作階段並導去 /login，不要期待原本的 token 還能再用。
 */
export function changePasswordApi(currentPassword: string, newPassword: string): Promise<void> {
  return authorizedJsonApi('/api/v1/auth/change-password', {
    method: 'POST',
    body: JSON.stringify({ currentPassword, newPassword }),
  });
}

/** 呼叫成功回傳 JSON；失敗時把後端訊息（若有）包成 ApiError 丟出，方便畫面直接顯示。 */
export async function authorizedJsonApi<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await authorizedFetchApi(path, init);
  if (!response.ok) {
    let message = `發生錯誤（HTTP ${response.status}）`;
    try {
      const body = await response.json();
      message = body?.message || body?.title || message;
    } catch {
      // 回應不是 JSON（例如 403 的空內容），維持預設訊息。
    }
    throw new ApiError(message, response.status);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

/**
 * 給前台戰情室（`/`）用：那個頁面刻意設計成不用登入的大廳螢幕（DashboardLayout.astro
 * 沒有登入檢查），打的是 `/api/v1/public/*`（不掛 JWT 驗證）。跟 authorizedJsonApi 分開實作，
 * 不能共用——authorizedFetchApi 遇到 401 會清工作階段並導去 /login，那是給後台管理頁設計的行為，
 * 套到公開大螢幕上會變成「沒人登入，螢幕就一直被彈回登入頁」。
 */
export async function publicJsonApi<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(path, { ...init, headers: new Headers({ 'Content-Type': 'application/json', ...init.headers }) });
  if (!response.ok) {
    let message = `發生錯誤（HTTP ${response.status}）`;
    try {
      const body = await response.json();
      message = body?.message || body?.title || message;
    } catch {
      // 回應不是 JSON，維持預設訊息。
    }
    throw new ApiError(message, response.status);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}
