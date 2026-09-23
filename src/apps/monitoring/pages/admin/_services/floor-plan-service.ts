/**
 * @file floor-plan-service.ts
 * 東元電機智慧環境監控 - 樓層平面圖配置服務
 * 嚴格遵守 Metat 規範：所有對外呼叫函式一律以 *Api 結尾
 *
 * 打的是 backend/ 的 /api/v1/floor-plan/*（見
 * backend/src/Teco.Hvac.Api/Endpoints/FloorPlanEndpoints.cs）。
 * 先前配置只存在瀏覽器 localStorage，換裝置就消失；改打這支 API 之後才是跨裝置共用的真資料。
 */

import { authorizedJsonApi, publicJsonApi } from './auth-service';

/** 後端的 AlarmDeviceType：0=冰水主機、1=FCU。列舉以整數序列化，不是字串。 */
export const PlacementDeviceType = { Chiller: 0, Fcu: 1 } as const;

export interface FloorPlacementDto {
  deviceType: number;
  deviceId: number;
  areaId: string;
  x: number;
  y: number;
  rotation: number;
}

export interface FloorPlacementsResponse {
  floor: string;
  /** 整層的版本（該層最新的 updated_at）；null 代表這層還沒被配置過。存檔時要原樣帶回去。 */
  version: string | null;
  placements: FloorPlacementDto[];
}

export function getFloorPlacementsApi(floor: string): Promise<FloorPlacementsResponse> {
  return authorizedJsonApi(`/api/v1/floor-plan/${encodeURIComponent(floor)}/placements`);
}

/**
 * 公開唯讀版本：給前台戰情室（`/`）的熱區圖用。FloorPlanViewer.vue 同時被後台監控中心
 * （需要登入）跟前台戰情室（不需要登入）共用，所以兩支都要有——理由見 auth-service.ts
 * 的 publicJsonApi 說明。
 */
export function getPublicFloorPlacementsApi(floor: string): Promise<FloorPlacementsResponse> {
  return publicJsonApi(`/api/v1/public/floor-plan/${encodeURIComponent(floor)}/placements`);
}

/**
 * 整層覆寫。expectedVersion 要帶上次讀到的 version，後端會比對；
 * 期間若有別人存過檔，後端回 409，呼叫端要提示使用者重新載入而不是直接覆蓋。
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
