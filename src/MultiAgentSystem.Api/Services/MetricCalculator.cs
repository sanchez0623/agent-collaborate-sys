// ============================================================
// MetricCalculator - 自动指标计算器（不需要 LLM 参与）
// 计算：响应时间、Token 消耗、工具调用正确率、完成率
// ============================================================

using System.Text.Json;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class MetricCalculator
{
    /// <summary>
    /// 计算工具调用正确率
    /// 对比实际调用的工具序列 vs 期望的工具序列
    /// </summary>
    /// <param name="expectedTools">JSON 数组：["search_customers","add_followup"]</param>
    /// <param name="actualToolCalls">JSON 数组：实际调用的工具名列表</param>
    /// <returns>0-1 正确率</returns>
    public double CalculateToolAccuracy(string? expectedTools, string? actualToolCalls)
    {
        var expected = ParseToolList(expectedTools);
        var actual = ParseToolList(actualToolCalls);

        if (expected.Count == 0 && actual.Count == 0) return 1.0;
        if (expected.Count == 0) return 0.0;

        int matched = 0;
        foreach (var tool in expected)
        {
            if (actual.Contains(tool, StringComparer.OrdinalIgnoreCase))
                matched++;
        }

        double precision = expected.Count > 0 ? (double)matched / expected.Count : 0;
        double recall = actual.Count > 0 ? (double)matched / actual.Count : 0;

        // F1 score
        if (precision + recall == 0) return 0;
        return 2 * precision * recall / (precision + recall);
    }

    /// <summary>
    /// 计算效率维度分数（0-10）
    /// 响应时间 + Token 消耗归一化后加权
    /// </summary>
    public double CalculateEfficiencyScore(long responseTimeMs, int totalTokens,
        long baselineResponseMs = 5000, int baselineTokens = 500)
    {
        // 响应时间得分：基线 5000ms 得 5 分，翻倍接近 0 分
        double timeScore = baselineResponseMs > 0
            ? Math.Max(0, 10 - (responseTimeMs / (double)baselineResponseMs) * 5)
            : 10;

        // Token 效率得分：基线 500 tokens 得 5 分
        double tokenScore = baselineTokens > 0
            ? Math.Max(0, 10 - (totalTokens / (double)baselineTokens) * 5)
            : 10;

        // 加权：延迟权重 0.6，Token 0.4
        return Math.Round(timeScore * 0.6 + tokenScore * 0.4, 1);
    }

    /// <summary>
    /// 检查任务是否完成（回答了问题且无异常）
    /// </summary>
    public bool IsCompleted(string? finalOutput, string? errorMessage)
    {
        if (!string.IsNullOrWhiteSpace(errorMessage)) return false;
        if (string.IsNullOrWhiteSpace(finalOutput)) return false;
        // 至少有一些实质性内容
        return finalOutput.Trim().Length > 10;
    }

    /// <summary>
    /// 计算单用例的所有自动指标
    /// </summary>
    public void CalculateMetrics(EvalCaseResult result, string? expectedTools, string? actualToolCalls,
        string? agentOutputs, string? errorMessage)
    {
        result.ToolCallAccuracy = Math.Round(CalculateToolAccuracy(expectedTools, actualToolCalls), 3);
        result.ExpectedToolCount = ParseToolList(expectedTools).Count;
        result.ActualToolCount = ParseToolList(actualToolCalls).Count;

        var dims = result.Dimensions;
        dims.Add(new DimensionScore
        {
            Dimension = EvalDimension.ToolAccuracy,
            Score = Math.Round(result.ToolCallAccuracy * 10, 1),
            Weight = new EvalWeights().ToolAccuracy,
            Reasoning = $"期望工具{result.ExpectedToolCount}个,实际{result.ActualToolCount}个,F1={result.ToolCallAccuracy:F3}"
        });

        dims.Add(new DimensionScore
        {
            Dimension = EvalDimension.Efficiency,
            Score = CalculateEfficiencyScore(result.ResponseTimeMs, result.TotalTokens),
            Weight = new EvalWeights().Efficiency,
            Reasoning = $"响应{result.ResponseTimeMs}ms,Token{result.TotalTokens}(in{result.InputTokens}/out{result.OutputTokens})"
        });
    }

    private static List<string> ParseToolList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }
}
