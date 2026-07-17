// ============================================================
// ChatClientFactory - IChatClient 工厂
// 作用：构建连接 DeepSeek（OpenAI 兼容）的 IChatClient
// 这是 MAF 的底层依赖：所有 ChatClientAgent 都基于此 IChatClient 创建
// DeepSeek 兼容 OpenAI API 格式，因此用 OpenAIClient + 自定义 Endpoint 接入
// ============================================================

using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace MultiAgentSystem.Api.Services;

public static class ChatClientFactory
{
    /// <summary>
    /// 创建连接 DeepSeek 的 IChatClient
    /// </summary>
    /// <param name="apiKey">DeepSeek API Key</param>
    /// <param name="baseUrl">DeepSeek API 基地址，如 https://api.deepseek.com/v1</param>
    /// <param name="model">模型名，如 deepseek-v4-flash</param>
    /// <returns>可用于创建 Agent 的 IChatClient</returns>
    public static IChatClient Create(string apiKey, string baseUrl, string model)
    {
        var credential = new ApiKeyCredential(apiKey);

        // HttpClient 超时设 5 分钟（大上下文 + 慢网速的兜底）
        var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl.TrimEnd('/'))
        };

        var openAiClient = new OpenAIClient(credential, options);
        return openAiClient.GetChatClient(model).AsIChatClient();
    }
}
