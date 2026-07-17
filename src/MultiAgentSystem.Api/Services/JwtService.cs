// ============================================================
// JwtService - JWT 签发与校验（轻量鉴权）
//
// 设计意图：为作品集增加"权限控制"亮点，对应企业系统的认证鉴权
// 实现：用 Microsoft.IdentityModel.Tokens 手动签发 HS256 JWT
//   - 避免引入完整 ASP.NET Core Identity（演示场景过重）
//   - Claim 携带 username + role，后端用 [Authorize] + 角色策略校验
//
// 角色：
//   - User：普通用户，可对话、查看自己的客户
//   - Admin：审核人/管理员，可审核、查看全部数据、操作工单
// ============================================================

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class JwtService
{
    private readonly string _secret;
    private readonly string _issuer;
    private readonly int _expireHours;

    public JwtService(string secret, string issuer = "MultiAgentSystem", int expireHours = 24)
    {
        _secret = secret;
        _issuer = issuer;
        _expireHours = expireHours;
    }

    /// <summary>签发 JWT</summary>
    public string Issue(User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim("display_name", user.DisplayName)
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _issuer,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_expireHours),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>校验 Token，返回 ClaimsPrincipal（失败返回 null）</summary>
    public ClaimsPrincipal? Validate(string token)
    {
        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
            var handler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _issuer,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromMinutes(1)
            };
            return handler.ValidateToken(token, parameters, out _);
        }
        catch { return null; }
    }

    /// <summary>从 Authorization 头解析当前用户（供非 [Authorize] 端点手动校验用）</summary>
    public (string user, string role) ParseUser(string? authHeader)
    {
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ")) return ("guest", "User");
        var token = authHeader["Bearer ".Length..];
        var principal = Validate(token);
        if (principal == null) return ("guest", "User");
        return (principal.Identity?.Name ?? "guest",
                principal.FindFirst(ClaimTypes.Role)?.Value ?? "User");
    }
}
