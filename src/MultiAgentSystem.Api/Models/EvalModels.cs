// ============================================================
// EvalModels - MVP-5 评测体系数据模型
// ============================================================

namespace MultiAgentSystem.Api.Models;

/// <summary>6 维度评分名称</summary>
public enum EvalDimension
{
    Accuracy,      // 准确性 — LLM-Judge
    Completeness,  // 完整性 — LLM-Judge
    Relevance,     // 相关性 — LLM-Judge
    Hallucination, // 幻觉率 — LLM-Judge（反向）
    ToolAccuracy,  // 工具调用正确率 — 自动
    Efficiency     // 响应效率 — 自动（延迟+Token）
}

/// <summary>维度单项评分</summary>
public class DimensionScore
{
    public EvalDimension Dimension { get; set; }
    public double Score { get; set; }           // 0-10
    public double Weight { get; set; } = 1.0;   // 权重
    public string Reasoning { get; set; } = ""; // Judge 评语
    public double WeightedScore => Score * Weight;
}

/// <summary>评测维度权重配置</summary>
public class EvalWeights
{
    public double Accuracy { get; set; } = 1.5;
    public double Completeness { get; set; } = 1.2;
    public double Relevance { get; set; } = 1.0;
    public double Hallucination { get; set; } = 2.0;
    public double ToolAccuracy { get; set; } = 1.2;
    public double Efficiency { get; set; } = 0.8;

    public double GetWeight(EvalDimension dim) => dim switch
    {
        EvalDimension.Accuracy => Accuracy,
        EvalDimension.Completeness => Completeness,
        EvalDimension.Relevance => Relevance,
        EvalDimension.Hallucination => Hallucination,
        EvalDimension.ToolAccuracy => ToolAccuracy,
        EvalDimension.Efficiency => Efficiency,
        _ => 1.0
    };
}

/// <summary>测试用例</summary>
public class EvalTestCase
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";         // 通用问答/多Agent协作/RAG/CRM/工具调用/人审
    public string Tags { get; set; } = "";              // 逗号分隔
    public string Question { get; set; } = "";          // 用户输入
    public string ExpectedKeyPoints { get; set; } = ""; // 期望答案要点（换行分隔）
    public string ApplicableModes { get; set; } = "";   // 逗号分隔：Sequential,Concurrent,Handoff,GroupChat,Magentic,Crm
    public bool RequiresKnowledgeBase { get; set; }
    public int? KnowledgeBaseId { get; set; }
    public string ExpectedToolCalls { get; set; } = ""; // JSON: ["search_customers","add_followup"]
    public EvalWeights Weights { get; set; } = new();
    public bool IsPreset { get; set; } = true;          // true=预置 false=自定义
    public DateTime CreatedAt { get; set; }
}

/// <summary>评测任务</summary>
public class EvalTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string CaseSet { get; set; } = "full";       // full/quick-smoke/rag/tool/crm
    public List<string> Modes { get; set; } = new();     // 编排模式列表
    public bool EnableRag { get; set; } = true;
    public bool DisableRag { get; set; } = false;        // A/B：RAG 开关对比
    public int JudgeCount { get; set; } = 1;             // Judge 次数（1/3）
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxConcurrency { get; set; } = 1;
    public string Status { get; set; } = "pending";      // pending/running/completed/failed
    public int TotalCases { get; set; }
    public int CompletedCases { get; set; }
    public int FailedCases { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>单用例评测结果</summary>
public class EvalCaseResult
{
    public int Id { get; set; }
    public string TaskId { get; set; } = "";
    public int TestCaseId { get; set; }
    public string Mode { get; set; } = "";
    public bool RagEnabled { get; set; } = true;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    // 6 维度分数
    public List<DimensionScore> Dimensions { get; set; } = new();

    // 自动指标
    public long ResponseTimeMs { get; set; }
    public int TotalTokens { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double ToolCallAccuracy { get; set; }
    public int ExpectedToolCount { get; set; }
    public int ActualToolCount { get; set; }

    // 对话记录
    public string ConversationLog { get; set; } = "";
    public string AgentOutputs { get; set; } = "";
    public string ToolCallLog { get; set; } = "";

    // 原始 Judge 输出
    public string JudgeRawOutput { get; set; } = "";

    public double WeightedTotal => Dimensions.Sum(d => d.WeightedScore);
    public double SimpleAverage => Dimensions.Count > 0
        ? Dimensions.Average(d => d.Score) : 0;

    public DateTime ExecutedAt { get; set; }
}

/// <summary>A/B 对比结果</summary>
public class ABComparison
{
    public string TaskId { get; set; } = "";
    public List<ModeComparison> ModeComparisons { get; set; } = new();
    public List<RagComparison> RagComparisons { get; set; } = new();
    public string Summary { get; set; } = "";
}

public class ModeComparison
{
    public string Mode { get; set; } = "";
    public int CasesRun { get; set; }
    public int SuccessCount { get; set; }
    public Dictionary<EvalDimension, double> AvgScores { get; set; } = new();
    public double WeightedTotal { get; set; }
    public long AvgResponseMs { get; set; }
    public int TotalTokens { get; set; }
    public double AvgToolAccuracy { get; set; }
}

public class RagComparison
{
    public string Mode { get; set; } = "";
    public bool RagEnabled { get; set; }
    public double WeightedTotal { get; set; }
    public double AvgHallucination { get; set; }
    public double AvgAccuracy { get; set; }
}

/// <summary>评测报告（汇总）</summary>
public class EvalReport
{
    public string TaskId { get; set; } = "";
    public string CaseSet { get; set; } = "";
    public List<string> Modes { get; set; } = new();
    public string Status { get; set; } = "";
    public int TotalCases { get; set; }
    public int SuccessCases { get; set; }
    public int FailedCases { get; set; }
    public double OverallScore { get; set; }
    public double AvgResponseMs { get; set; }
    public int TotalTokens { get; set; }
    public List<EvalCaseResult> CaseResults { get; set; } = new();
    public ABComparison? Comparison { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>启用评测请求</summary>
public class EvalRunRequest
{
    public string CaseSet { get; set; } = "full";
    public List<string> Modes { get; set; } = new() { "Sequential" };
    public bool EnableRag { get; set; } = true;
    public bool DisableRag { get; set; } = false;
    public int JudgeCount { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxConcurrency { get; set; } = 1;
}

/// <summary>Markdown 报告导出结果</summary>
public class ReportExport
{
    public string TaskId { get; set; } = "";
    public string Format { get; set; } = "markdown";   // markdown / json
    public string Content { get; set; } = "";
}
