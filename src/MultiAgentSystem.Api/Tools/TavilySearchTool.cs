// ============================================================
// TavilySearchTool - Tavily 网络搜索工具
// 作用：为 Researcher Agent 提供实时网络搜索能力
// 这是 MVP-1 中第一个 Agent 可调用的工具（MVP-0 无工具调用能力）
//
// 工作原理：
// 1. 用 AIFunctionFactory.Create 把 SearchAsync 方法包装成 AIFunction
// 2. Agent 在推理时可自主决定调用 search_web 工具获取信息
// 3. 工具内部调用 Tavily Search API，返回网页摘要供 Agent 使用
//
// Tavily API：POST https://api.tavily.com/search
// 认证方式：Authorization: Bearer <api_key>
// ============================================================

using Microsoft.Extensions.AI;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MultiAgentSystem.Api.Tools;

public class TavilySearchTool
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public TavilySearchTool(HttpClient http, string apiKey)
    {
        _http = http;
        _apiKey = apiKey;
    }

    /// <summary>
    /// 把搜索方法注册为 MAF 可识别的 AIFunction 工具
    /// Agent 会根据工具的 name 和 description 决定是否调用
    /// </summary>
    public AIFunction AsAIFunction()
    {
        // AIFunctionFactory.Create 会自动从方法签名推断参数 schema
        // 参数名 query 会成为工具的入参，Agent 调用时传入搜索关键词
        return AIFunctionFactory.Create(
            SearchAsync,
            name: "search_web",
            description: "搜索互联网获取最新信息。输入搜索关键词，返回相关网页标题和摘要内容。当需要查找实时信息、事实数据或最新事件时使用此工具。");
    }

    /// <summary>
    /// 实际调用 Tavily Search API
    /// </summary>
    /// <param name="query">搜索关键词</param>
    /// <returns>格式化的搜索结果文本（供 Agent 阅读）</returns>
    private async Task<string> SearchAsync(string query)
    {
        try
        {
            // 构造 Tavily 请求体
            var requestBody = new
            {
                query = query,
                max_results = 3,            // 返回3条结果，平衡信息量和响应速度
                include_answer = true,      // 让 Tavily 生成一段摘要答案
                search_depth = "basic"      // 基础搜索深度，速度快
            };

            // Tavily 要求 Bearer token 认证
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
            {
                Content = JsonContent.Create(requestBody)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            // 解析返回的 JSON
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;

            var sb = new System.Text.StringBuilder();

            // 提取 Tavily 生成的摘要答案
            if (root.TryGetProperty("answer", out var answerEl) && answerEl.ValueKind == JsonValueKind.String)
            {
                sb.AppendLine($"摘要：{answerEl.GetString()}");
                sb.AppendLine();
            }

            // 提取每条网页结果
            if (root.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
            {
                int idx = 1;
                foreach (var item in resultsEl.EnumerateArray())
                {
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() : "";
                    var content = item.TryGetProperty("content", out var c) ? c.GetString() : "";
                    sb.AppendLine($"[{idx}] {title}");
                    sb.AppendLine(content);
                    sb.AppendLine();
                    idx++;
                }
            }

            return sb.Length > 0 ? sb.ToString() : "未找到相关搜索结果。";
        }
        catch (Exception ex)
        {
            // 工具调用失败时返回错误信息，Agent 可据此调整策略而非崩溃
            return $"搜索失败：{ex.Message}";
        }
    }
}
