/**
 * @file floor-plan-service.ts
 * 東元電機智慧環境監控 - 樓層平面圖配置服務
 * 嚴格遵守專案規範：所有對外呼叫函式一律以 *Api 結尾
 *
 * 打的是 backend/ 的 /api/v1/floor-plan/*（見
 * backend/src/Teco.Hvac.Api/Endpoints/FloorPlanEndpoints.cs）。
 * 先前配置只存在瀏覽器 localStorage，換裝置就消失；改打這支 API 之後才是跨裝置共用的真資料。
 */

import { authorizedJsonApi, publicJsonApi } from './auth-service';

/** 後端的 AlarmDeviceType：0=冰水主機、1=FCU。列舉以整數序列化，不是字串。 */
export const PlacementDeviceType = {
  /** 冰水主機 (Chiller) */
  Chiller: 0,
  /** 小型送風機 (FCU) */
  Fcu: 1,
} as const;

/**
 * 樓層設備平面圖座標配置資料傳輸物件介面
 */
export interface FloorPlacementDto {
  /** 設備類型 (0: Chiller, 1: Fcu) */
  deviceType: number;
  /** 設備資料表主鍵 ID */
  deviceId: number;
  /** 所屬空間分區 ID */
  areaId: string;
  /** X 軸座標（圖面單位） */
  x: number;
  /** Y 軸座標（圖面單位） */
  y: number;
  /** 旋轉角度 (0 ~ 359 度) */
  rotation: number;
}

/**
 * 樓層平面圖點位配置回應結構介面
 */
export interface FloorPlacementsResponse {
  /** 樓層代碼 (B1 / B2) */
  floor: string;
  /** 整層的版本（該層最新的 updated_at）；null 代表這層還沒被配置過。存檔時要原樣帶回去。 */
  version: string | null;
  /** 該樓層所有已放置設備的點位陣列 */
  placements: FloorPlacementDto[];
}

/**
 * 取得指定樓層之平面圖配置點位清單（需登入與權限）。
 * @param floor 樓層代碼 (B1 / B2)
 * @returns 樓層配置回應資料 Promise
 */
export function getFloorPlacementsApi(floor: string): Promise<FloorPlacementsResponse> {
  return authorizedJsonApi(`/api/v1/floor-plan/${encodeURIComponent(floor)}/placements`);
}

/**
 * 公開唯讀版本：給前台戰情室（`/`）的熱區圖用。FloorPlanViewer.vue 同時被後台監控中心
 * （需要登入）跟前台戰情室（不需要登入）共用，所以兩支都要有——理由見 auth-service.ts
 * 的 publicJsonApi 說明。
 * @param floor 樓層代碼
 * @returns 樓層配置回應資料 Promise
 */
export function getPublicFloorPlacementsApi(floor: string): Promise<FloorPlacementsResponse> {
  return publicJsonApi(`/api/v1/public/floor-plan/${encodeURIComponent(floor)}/placements`);
}

/**
 * 整層覆寫。expectedVersion 要帶上次讀到的 version，後端會比對；
 * 期間若有別人存過檔，後端回 409，呼叫端要提示使用者重新載入而不是直接覆蓋。
 * @param floor 樓層代碼
 * @param placements 該樓層完整設備點位陣列
 * @param expectedVersion 預期的樂觀鎖定版本戳記
 * @returns 儲存成功後之版本與筆數結果
 */
export function saveFloorPlacementsApi(
  floor: string,
  placements: FloorPlacementDto[],
  expectedVersion: string | null,
): Promise<{ floor: string; version: string; count: number }> {
  return authorizedJsonApi(`/api/v1/floor-plan/${encodeURIComponent(floor)}/placements`, {
    method: 'PUT',
    body: JSON.stringify({ placements, expectedVersion }),
  });
}

