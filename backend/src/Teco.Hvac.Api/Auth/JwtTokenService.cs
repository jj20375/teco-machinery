using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Domain.Permissions;

namespace Teco.Hvac.Api.Auth;

/// <summary>
/// 簽發含 scope、AuthVersion 與資源型 grants 的 JWT。
/// 注意：Program.cs 的 JwtBearerOptions 必須設 MapInboundClaims=false，
/// 否則 ASP.NET Core 預設會把 "sub" 這類短名稱改寫成長版 XML URI，RequestScope 就讀不到。
/// </summary>
/// <param name="options">注入的 JWT 組態選項。</param>
public sealed class JwtTokenService(IOptions<JwtOptions> options)
{
    /// <summary>快取的 JWT 選項實體。</summary>
    private readonly JwtOptions _options = options.Value;

    /// <summary>
    /// 依使用者身分、目標範圍與被授予之權限簽發簽名之 JWT 字串。
    /// </summary>
    /// <param name="user">登入的使用者實體物件。</param>
    /// <param name="scopeKind">範圍類型 ("platform" 或 "merchant")。</param>
    /// <param name="merchantId">鎖定的場館 ID（若為場館範圍則必填；平台總管可為 null）。</param>
    /// <param name="grants">授予此使用者的權限資源集合。</param>
    /// <param name="isRoleCrudConfigurationEnabled">是否允許角色 CRUD 配置（僅供前端 UI 旗標使用）。</param>
    /// <param name="isRoleOptionConfigurationEnabled">是否允許角色細項選項配置（僅供前端 UI 旗標使用）。</param>
    /// <param name="expiresIn">自訂權杖存活時間；若未指定則使用組態設定的預設分鐘數。</param>
    /// <returns>經 HMAC-SHA256 簽署完成的 JWT 緊湊字串。</returns>
    public string Create(
        AppUser user,
        string scopeKind,
        int? merchantId,
        IReadOnlyCollection<PermissionGrant> grants,
        bool isRoleCrudConfigurationEnabled = true,
        bool isRoleOptionConfigurationEnabled = true,
        TimeSpan? expiresIn = null)
    {
        var now = DateTimeOffset.UtcNow;
        // 基本身分與安全認證 Claims
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            new("displayName", user.DisplayName),
            new("scope_kind", scopeKind),
            new("auth_version", user.AuthVersion.ToString()),
        };
        if (user.IsPlatformAdmin) claims.Add(new Claim("is_platform_admin", "true"));
        if (merchantId is int id) claims.Add(new Claim("merchant_id", id.ToString()));

        // 場館選擇簡化模式時，有效 grant 在簽發前已展開；這兩個 claim 只供前端顯示用，不是授權邊界。
        if (string.Equals(scopeKind, "merchant", StringComparison.OrdinalIgnoreCase))
        {
            claims.Add(new Claim("role_crud_configuration_enabled", isRoleCrudConfigurationEnabled ? "true" : "false"));
            claims.Add(new Claim("role_option_configuration_enabled", isRoleOptionConfigurationEnabled ? "true" : "false"));
        }

        // 展開各項權限資源為 claim: permission="code:action" 與 permission_option="code:option"
        foreach (var grant in grants)
        {
            foreach (var action in grant.Actions.Distinct(StringComparer.Ordinal))
                claims.Add(new Claim("permission", $"{grant.Code}:{action}"));
            foreach (var option in grant.Options.Distinct(StringComparer.Ordinal))
                claims.Add(new Claim("permission_option", $"{grant.Code}:{option}"));
        }

        // 使用 HMAC-SHA256 簽章
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(expiresIn ?? TimeSpan.FromMinutes(_options.AccessTokenMinutes)).UtcDateTime,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
