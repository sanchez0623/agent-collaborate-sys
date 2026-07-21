// ============================================================
// JudgeService - LLM-as-Judge 评分服务
// DeepSeek T=0.1, 6 维度结构化打分, 1/3 次投票取中位数
// ============================================================

using System.Text.Json;
using Microsoft.Extensions.AI;
using ChatMsg = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class JudgeService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<JudgeService> _logger;

    public JudgeService(IChatClient chatClient, ILogger<JudgeService> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// LLM-Judge 评分（支持多次投票取中位数）
    /// </summary>
    public async Task<List<DimensionScore>> JudgeAsync(
        EvalTestCase testCase, string question, string? answer, int voteCount = 1)
    {
        var allScores = new List<List<DimensionScore>>();

        for (int i = 0; i < voteCount; i++)
        {
            try
            {
                var scores = await JudgeOnceAsync(testCase, question, answer ?? "(无回答)");
                if (scores != null) allScores.Add(scores);
                _logger.LogDebug("Judge 第{Attempt}次打分完成", i + 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Judge 第{Attempt}次打分失败", i + 1);
            }
        }

        if (allScores.Count == 0) return GetDefaultScores("Judge 全部失败");

        // 取中位数：对每个维度取所有次数的中位分数
        return voteCount switch
        {
            1 => allScores[0],
            _ => MedianMerge(allScores)
        };
    }

    private async Task<List<DimensionScore>?> JudgeOnceAsync(
        EvalTestCase testCase, string question, string answer)
    {
        var prompt = BuildJudgePrompt(testCase, question, answer);
        var messages = new List<ChatMsg>
        {
            new(ChatRole.System, JudgeSystemPrompt),
            new(ChatRole.User, prompt)
        };

        var response = await _chatClient.GetResponseAsync(messages,
            new ChatOptions { MaxOutputTokens = 2048, Temperature = 0.1f },
            cancellationToken: CancellationToken.None);

        var text = response.Text ?? response.Messages.FirstOrDefault()?.Text ?? "";
        return ParseJudgeResponse(text);
    }

    private List<DimensionScore>? ParseJudgeResponse(string text)
    {
        var json = text.Trim();
        if (json.StartsWith("```json")) json = json[7..];
        if (json.StartsWith("```")) json = json[3..];
        if (json.EndsWith("```")) json = json[..^3];
        json = json.Trim();

        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var items = JsonSerializer.Deserialize<List<JudgeScoreItem>>(json, opts);
            if (items == null || items.Count == 0) return null;

            var results = new List<DimensionScore>();
            foreach (var item in items)
            {
                var dim = ParseDimension(item.Dimension);
                if (dim == null)
                {
                    _logger.LogWarning("Judge 返回未知维度 '{Dim}'，已丢弃", item.Dimension);
                    continue;
                }
                results.Add(new DimensionScore
                {
                    Dimension = dim.Value,
                    Score = Math.Clamp(item.Score, 0, 10),
                    Weight = new EvalWeights().GetWeight(dim.Value),
                    Reasoning = item.Reasoning ?? ""
                });
            }

            // 有效维度不足 4 个视为解析失败，触发重试
            if (results.Count < 4)
            {
                _logger.LogWarning("Judge 有效维度仅 {Count}/4，视为解析失败", results.Count);
                return null;
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Judge JSON 解析失败: {Err} raw={Raw}", ex.Message, json.Truncate(200));
            return null;
        }
    }

    private static EvalDimension? ParseDimension(string name) => name?.ToLowerInvariant() switch
    {
        "accuracy" => EvalDimension.Accuracy,
        "completeness" => EvalDimension.Completeness,
        "relevance" => EvalDimension.Relevance,
        "hallucination" => EvalDimension.Hallucination,
        _ => null  // 未知维度丢弃，不再 fallback 到 Accuracy
    };

    private static List<DimensionScore> GetDefaultScores(string reason)
        => Enum.GetValues<EvalDimension>().Select(d => new DimensionScore
        {
            Dimension = d, Score = 0, Weight = 1,
            Reasoning = reason
        }).ToList();

    private static List<DimensionScore> MedianMerge(List<List<DimensionScore>> allVotes)
    {
        var merged = new List<DimensionScore>();
        foreach (var dim in Enum.GetValues<EvalDimension>())
        {
            var scores = allVotes.SelectMany(v => v.Where(s => s.Dimension == dim)).Select(s => s.Score).OrderBy(x => x).ToList();
            double median = scores.Count > 0 ? (scores[scores.Count / 2]) : 0;
            var reasoning = allVotes.SelectMany(v => v.Where(s => s.Dimension == dim))
                .Select(s => $"[{s.Score}] {s.Reasoning}").FirstOrDefault() ?? "";
            merged.Add(new DimensionScore
            {
                Dimension = dim, Score = median,
                Weight = new EvalWeights().GetWeight(dim),
                Reasoning = $"(中位数:{median}) {reasoning}"
            });
        }
        return merged;
    }

    private record JudgeScoreItem(string Dimension, double Score, string Reasoning);

    // ========== Judge Prompt 工程 ==========

    private const string JudgeSystemPrompt = """
        你是一名专业的 AI 回答质量评审员。根据以下评分标准对回答进行结构化打分。

        ## 评分标准（0-10分）
        - **Accuracy 准确性**：回答中的事实是否准确？与期望答案的关键点是否一致？有无事实错误？
          (10=完全准确，0=严重错误)
        - **Completeness 完整性**：是否覆盖了问题的所有方面？有无遗漏关键点？
          (10=完全覆盖，0=严重遗漏)
        - **Relevance 相关性**：回答是否切题？有无跑题或无关冗余？
          (10=完全切题，0=答非所问)
        - **Hallucination 幻觉**：是否编造了不存在的信息、工具结果或客户数据？
          (10=无幻觉，0=严重编造——注意这是正向计分，高分=低幻觉)

        ## 输出格式
        严格输出 JSON 数组，不要包含其他文字：
        [
          {"dimension":"Accuracy","score":8,"reasoning":"评分理由:..."},
          {"dimension":"Completeness","score":7,"reasoning":"评分理由:..."},
          {"dimension":"Relevance","score":9,"reasoning":"评分理由:..."},
          {"dimension":"Hallucination","score":9,"reasoning":"评分理由:..."}
        ]

        ## 评分尺度
        - 10：完美，无任何问题
        - 7-9：良好，少量小瑕疵
        - 4-6：一般，有明显不足
        - 1-3：差，严重问题
        - 0：完全错误/与问题无关

        ## 重要原则
        - 基于事实评分，不要凭感觉
        - 如有工具调用失败或超时，直接0分并标注原因
        - 每项评分必须附简短理由
        """;

    private static string BuildJudgePrompt(EvalTestCase tc, string question, string answer)
    {
        return $"""
            请评审以下回答质量。

            **场景分类**：{tc.Category}
            **用户问题**：{question}
            **期望答案关键点**：{tc.ExpectedKeyPoints}

            **实际回答**：
            {answer}

            请按4个维度(Accuracy/Completeness/Relevance/Hallucination)打分，输出JSON数组。
            """;
    }
}

file static class JudgeStringExtensions
{
    public static string Truncate(this string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
