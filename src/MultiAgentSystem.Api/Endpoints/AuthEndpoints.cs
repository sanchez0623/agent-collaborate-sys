// ============================================================
// 认证端点：登录、获取当前用户
// ============================================================

using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Endpoints;

public static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest req, BusinessStore store, JwtService jwt) =>
        {
            var user = await store.GetUserAsync(req.Username);
            if (user == null || !BusinessStore.VerifyPwd(req.Password, user.PasswordHash))
                return Results.Unauthorized();

            await store.AuditAsync(AuditLogType.Auth, user.Username, "登录", null);
            var token = jwt.Issue(user);
            return Results.Ok(new LoginResponse(token, user.Username, user.Role.ToString(), user.DisplayName));
        }).WithTags("认证");

        app.MapGet("/api/auth/me", (HttpContext ctx, JwtService jwt) =>
        {
            var (user, role) = EndpointHelpers.GetCurrentUser(ctx);
            return Results.Ok(new { username = user, role });
        }).WithTags("认证");

        return app;
    }
}
