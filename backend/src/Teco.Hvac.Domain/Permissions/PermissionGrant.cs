namespace Teco.Hvac.Domain.Permissions;

/// <summary>
/// 單一權限資源的 CRUD 動作與附加子功能之授予記錄實體 (<see cref="PermissionGrant"/>)。
/// </summary>
/// <param name="Code">權限資源代碼（例如 "hvac.chiller"、"system.users"）。</param>
/// <param name="Actions">已授予的 CRUD 動作清單（如 ["read", "update"]）。</param>
/// <param name="Options">已授予的附加子功能項目清單（如 ["export_report", "override_setpoint"]）。</param>
public sealed record PermissionGrant(string Code, IReadOnlyCollection<string> Actions, IReadOnlyCollection<string> Options);
