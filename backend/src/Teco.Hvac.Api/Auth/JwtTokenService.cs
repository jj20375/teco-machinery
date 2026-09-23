using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Teco.Hvac.Domain.Entities;
using Teco.Hvac.Domain.Permissions;

namespace Teco.Hvac.Api.Auth;

/// <summary>
/// 簽發含 scope、AuthVersion 與資源型 grants 的 JWT（比照美達特 JwtTokenService）。
/// 注意：Program.cs 的 JwtBearerOptions 必須設 MapInboundClaims=false，
/// 否則 ASP.NET Core 預設會把 "sub" 這類短名稱改寫成長版 XML URI，RequestScope 就讀不到。
/// </summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

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

        foreach (var grant in grants)
        {
            foreach (var action in grant.Actions.Distinct(StringComparer.Ordinal))
                claims.Add(new Claim("permission", $"{grant.Code}:{action}"));
            foreach (var option in grant.Options.Distinct(StringComparer.Ordinal))
                claims.Add(new Claim("permission_option", $"{grant.Code}:{option}"));
        }

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
