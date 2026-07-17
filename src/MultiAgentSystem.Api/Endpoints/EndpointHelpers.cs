// ============================================================
// 共享辅助函数（从 Program.cs 提取，供各 Endpoint 文件使用）
// ============================================================

using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Services;
using MultiAgentSystem.Api.Strategies;

namespace MultiAgentSystem.Api.Endpoints;

public static class EndpointHelpers
{
    public static (string user, string role) GetCurrentUser(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ")) return ("guest", "User");
        var token = auth["Bearer ".Length..];
        var jwt = ctx.RequestServices.GetRequiredService<JwtService>();
        return jwt.ParseUser("Bearer " + token);
    }

    public static IOrchestrationStrategy ResolveStrategy(
        string? mode,
        Dictionary<string, IOrchestrationStrategy> strategies,
        IOrchestrationStrategy seq)
    {
        return mode?.ToLowerInvariant() switch
        {
            "sequential" => seq,
            "concurrent" => strategies["concurrent"],
            "handoff" => strategies["handoff"],
            "groupchat" or "group_chat" or "group" => strategies["groupchat"],
            "magentic" or "route" or "router" => strategies["magentic"],
            "crm" => strategies["crm"],
            "rag" or "knowledge" => strategies["rag"],
            _ => seq,
        };
    }

    public static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c)) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
