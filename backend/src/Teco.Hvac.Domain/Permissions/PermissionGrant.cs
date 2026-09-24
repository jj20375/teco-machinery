namespace Teco.Hvac.Domain.Permissions;

/// <summary>單一資源的 CRUD 動作與子功能授予。</summary>
public sealed record PermissionGrant(string Code, IReadOnlyCollection<string> Actions, IReadOnlyCollection<string> Options);
