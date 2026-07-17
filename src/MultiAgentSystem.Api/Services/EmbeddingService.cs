// ============================================================
// EmbeddingService - 文本向量化服务（多 Provider 可切换）
//
// 设计意图：
//   - DeepSeek 不提供 embedding API，只保留作 chat 模型
//   - Embedding 独立配置段，支持多 provider 切换
//   - 当前默认：硅基流动 BAAI/bge-m3（中英多语，8192 tokens，1024维）
//   - 可切换：智谱 GLM embedding-2 / OpenAI text-embedding-3-small / 本地哈希降级
//   - API 不可用时降级为哈希向量，保证演示流程不中断
//
// 配置示例（appsettings.json）：
//   "Embedding": {
//     "Provider": "siliconflow",          // siliconflow | zhipu | openai | hash
//     "BaseUrl": "https://api.siliconflow.cn",
//     "ModelId": "BAAI/bge-m3",
//     "ApiKey": "sk-xxx",
//     "Dimension": 1024                   // bge-m3 固定 1024 维；仅 Qwen3 系列支持自定义维度
//   }
//
// 重试与降级策略（修复批量上传 429 限流导致大面积失败）：
//   - 429 限流 / 5xx 服务端错误 → 指数退避重试（1s / 2s / 4s），最多 3 次
//   - 4xx（非 429，如 401 鉴权失败）不重试，直接降级
//   - 重试耗尽 / 网络异常 → 本次返回哈希降级向量，但不影响后续请求
//   - 不再「一次失败永久降级」：批量上传时偶发限流不应废掉后续所有文档的嵌入质量
// ============================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace MultiAgentSystem.Api.Services;

public class EmbeddingService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _provider;
    private readonly int _dimension;
    private readonly bool _forceFallback;

    public EmbeddingService(IHttpClientFactory httpFactory, IConfiguration config)
    {
        _httpFactory = httpFactory;
        // 读 Embedding 配置段（独立于 LLM 段，因 DeepSeek 不支持 embedding）
        var embSection = config.GetSection("Embedding");
        _provider = embSection["Provider"] ?? "hash";
        _baseUrl = (embSection["BaseUrl"] ?? "https://api.siliconflow.cn").TrimEnd('/');
        _model = embSection["ModelId"] ?? "BAAI/bge-m3";
        _apiKey = embSection["ApiKey"] ?? "";
        _dimension = int.TryParse(embSection["Dimension"], out var d) ? d : 1024;

        // 无 ApiKey 或显式选 hash provider → 直接走降级
        _forceFallback = string.IsNullOrEmpty(_apiKey) || _provider == "hash";
    }

    /// <summary>
    /// 获取文本向量
    /// </summary>
    /// <param name="text">待向量化的文本</param>
    /// <returns>float[] 向量；API 重试耗尽时返回哈希降级向量（仅本次降级，不影响后续）</returns>
    public async Task<float[]> GetEmbeddingAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return new float[_dimension];
        // 无 ApiKey 或显式选 hash → 直接走降级
        if (_forceFallback) return HashEmbedding(text, _dimension);

        // 429 限流 / 5xx → 指数退避重试（1s / 2s / 4s），最多 3 次
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var http = _httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(30);

                // 按 provider 拼端点路径（各厂商路径不同）
                var url = _provider.ToLowerInvariant() switch
                {
                    "zhipu" => $"{_baseUrl}/api/paas/v4/embeddings",
                    "siliconflow" or "openai" => $"{_baseUrl}/v1/embeddings",
                    _ => $"{_baseUrl}/v1/embeddings"
                };

                var body = JsonSerializer.Serialize(new
                {
                    model = _model,
                    input = text
                });
                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("Authorization", $"Bearer {_apiKey}");

                var resp = await http.SendAsync(req);
                var code = (int)resp.StatusCode;

                // 429 限流 / 5xx 服务端错误 → 指数退避重试
                if (code == 429 || code >= 500)
                {
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
                        continue;
                    }
                    return HashEmbedding(text, _dimension); // 重试耗尽，本次降级
                }

                // 4xx（非 429）不重试，直接降级（如 401 鉴权失败、400 参数错误）
                if (!resp.IsSuccessStatusCode) return HashEmbedding(text, _dimension);

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                // 所有 provider 都兼容 OpenAI 格式：data[0].embedding
                // 硅基流动响应示例：{object:"list", data:[{object:"embedding", embedding:[0.1,...], index:0}], usage:{...}}
                if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.GetArrayLength() > 0)
                {
                    var embArr = dataEl[0].GetProperty("embedding");
                    var vec = new float[embArr.GetArrayLength()];
                    int i = 0;
                    foreach (var v in embArr.EnumerateArray())
                    {
                        // 兼容 float / int 两种 ValueKind（文档示例 "embedding": [0] 是 int）
                        vec[i++] = v.ValueKind == JsonValueKind.Number
                            ? v.GetSingle()
                            : 0f;
                    }
                    return vec;
                }
                return HashEmbedding(text, _dimension); // 响应格式异常，降级
            }
            catch
            {
                // 网络异常 / 超时 → 指数退避重试
                if (attempt < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
                    continue;
                }
                return HashEmbedding(text, _dimension); // 重试耗尽，降级
            }
        }
        return HashEmbedding(text, _dimension);
    }

    /// <summary>
    /// 哈希向量降级方案
    ///
    /// 实现：
    ///   - 对文本做 SHA256，得到 32 字节
    ///   - 循环填充到 dim 维（每维用 1 字节 / 255 归一化到 [0,1]）
    ///   - 同一文本 → 同一向量（确定性）；不同文本 → 不同分布
    ///
    /// 局限（面试必讲）：
    ///   - 无语义相似性：相似文本的向量不相似
    ///   - 仅保证"检索流程可跑通"，不保证检索质量
    ///   - 生产环境必须用真实 embedding 模型
    /// </summary>
    private static float[] HashEmbedding(string text, int dim)
    {
        var vec = new float[dim];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        for (int i = 0; i < dim; i++)
        {
            var b = hash[i % hash.Length];
            vec[i] = b / 255f;
        }
        // L2 归一化，方便余弦相似度计算
        var norm = Math.Sqrt(vec.Sum(v => v * v));
        if (norm > 0)
        {
            for (int i = 0; i < dim; i++) vec[i] = (float)(vec[i] / norm);
        }
        return vec;
    }

    /// <summary>当前是否走降级路径（供前端展示降级状态）</summary>
    public bool IsFallback => _forceFallback;

    /// <summary>当前 provider 名称（供前端展示）</summary>
    public string Provider => _provider;

    /// <summary>向量维度</summary>
    public int Dimension => _dimension;
}
