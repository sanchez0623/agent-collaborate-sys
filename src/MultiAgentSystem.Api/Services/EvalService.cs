// ============================================================
// EvalService - 评测引擎主调度器
// 流程：加载用例→执行对话→收集指标→Judge评分→汇总A/B对比报告
// 通过 Channel<OrchestrationEvent> 推送 SSE 进度
// ============================================================

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using ChatMsg = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class EvalService
{
    private readonly IChatClient _chatClient;
    private readonly TestCaseStore _caseStore;
    private readonly EvalReportStore _reportStore;
    private readonly JudgeService _judge;
    private readonly MetricCalculator _metrics;
    private readonly ILogger<EvalService> _logger;

    // 运行中的任务 + SSE 通道
    private static readonly ConcurrentDictionary<string, EvalTask> RunningTasks = new();
    private static readonly ConcurrentDictionary<string, Channel<OrchestrationEvent>> ProgressChannels = new();

    public EvalService(IChatClient chatClient, TestCaseStore caseStore, EvalReportStore reportStore,
        JudgeService judge, MetricCalculator metrics, ILogger<EvalService> logger)
    {
        _chatClient = chatClient;
        _caseStore = caseStore;
        _reportStore = reportStore;
        _judge = judge;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// 启动评测任务（后台执行），返回 taskId
    /// </summary>
    public async Task<string> RunAsync(EvalRunRequest req)
    {
        var task = new EvalTask
        {
            CaseSet = req.CaseSet,
            Modes = req.Modes,
            EnableRag = req.EnableRag,
            DisableRag = req.DisableRag,
            JudgeCount = req.JudgeCount,
            TimeoutSeconds = req.TimeoutSeconds,
            MaxConcurrency = req.MaxConcurrency,
            Status = "running",
            CreatedAt = DateTime.UtcNow
        };

        RunningTasks[task.Id] = task;
        var channel = Channel.CreateUnbounded<OrchestrationEvent>();
        ProgressChannels[task.Id] = channel;

        await _reportStore.SaveTaskAsync(task);

        // 后台执行评测
        _ = Task.Run(() => ExecuteAsync(task, req, channel));

        return task.Id;
    }

    /// <summary>
    /// 获取 SSE 进度通道
    /// </summary>
    public ChannelReader<OrchestrationEvent>? GetProgressChannel(string taskId)
        => ProgressChannels.TryGetValue(taskId, out var ch) ? ch.Reader : null;

    /// <summary>
    /// 获取任务状态
    /// </summary>
    public async Task<EvalTask?> GetTaskAsync(string taskId)
        => await _reportStore.GetTaskAsync(taskId);

    /// <summary>
    /// 获取评测报告
    /// </summary>
    public async Task<EvalReport?> GetReportAsync(string taskId)
        => await _reportStore.GetReportAsync(taskId);

    /// <summary>
    /// 历史报告列表
    /// </summary>
    public async Task<List<EvalReport>> ListReportsAsync(int limit = 20)
        => await _reportStore.ListReportsAsync(limit);

    // ========== 核心执行逻辑 ==========

    private async Task ExecuteAsync(EvalTask task, EvalRunRequest req, Channel<OrchestrationEvent> channel)
    {
        try
        {
            // 加载用例
            var cases = new List<EvalTestCase>();
            if (req.CaseSet == "all" || req.CaseSet == "full")
                cases = await _caseStore.GetAllAsync();
            else
                cases = GetCasesBySet(req.CaseSet);

            task.TotalCases = cases.Count;
            await _reportStore.UpdateTaskProgressAsync(task.Id, 0, 0);

            var allResults = new List<EvalCaseResult>();
            var semaphore = new SemaphoreSlim(req.MaxConcurrency > 0 ? req.MaxConcurrency : 1);

            // A/B: RAG 开关
            var ragModes = new List<bool> { req.EnableRag };
            if (req.DisableRag) ragModes.Add(false);

            int completed = 0, failed = 0;

            foreach (var mode in req.Modes)
            foreach (var tc in cases)
            foreach (var ragOn in ragModes)
            {
                await semaphore.WaitAsync();
                try
                {
                    var result = await ExecuteCaseAsync(tc, mode, ragOn, req.TimeoutSeconds);
                    allResults.Add(result);

                    if (result.Success) completed++; else failed++;
                    task.CompletedCases = completed;
                    task.FailedCases = failed;

                    await _reportStore.SaveCaseResultAsync(result);
                    await _reportStore.UpdateTaskProgressAsync(task.Id, completed, failed);

                    // SSE 推送进度
                    PushProgress(channel, task, completed + failed, cases.Count * req.Modes.Count * ragModes.Count);
                }
                finally { semaphore.Release(); }
            }

            // 生成 A/B 对比 + 汇总报告
            var comparison = GenerateComparison(allResults, req.Modes);
            var report = new EvalReport
            {
                TaskId = task.Id, CaseSet = task.CaseSet, Modes = req.Modes,
                Status = "completed", TotalCases = allResults.Count,
                SuccessCases = completed, FailedCases = failed,
                OverallScore = allResults.Count > 0 ? allResults.Average(r => r.WeightedTotal) : 0,
                AvgResponseMs = allResults.Count > 0 ? (long)allResults.Average(r => r.ResponseTimeMs) : 0,
                TotalTokens = allResults.Sum(r => r.TotalTokens),
                CaseResults = allResults, Comparison = comparison,
                CreatedAt = task.CreatedAt, CompletedAt = DateTime.UtcNow
            };

            await _reportStore.FinalizeTaskAsync(report);
            task.Status = "completed";
            task.CompletedAt = DateTime.UtcNow;

            channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "评测执行失败: taskId={Id}", task.Id);
            task.Status = "failed";
            channel.Writer.TryComplete();
        }
        finally
        {
            RunningTasks.TryRemove(task.Id, out _);
        }
    }

    private async Task<EvalCaseResult> ExecuteCaseAsync(
        EvalTestCase tc, string mode, bool ragOn, int timeoutSec)
    {
        var result = new EvalCaseResult
        {
            TaskId = "", // set later
            TestCaseId = tc.Id, Mode = mode, RagEnabled = ragOn,
            Success = false
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 构建 prompt（模拟编排模式的前缀）
            var prompt = BuildEvalPrompt(tc.Question, mode, ragOn);

            var messages = new List<ChatMsg>
            {
                new(ChatRole.System, $"你正在以{mode}编排模式回答用户问题。回答应准确、完整、专业。"),
                new(ChatRole.User, prompt)
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cts.Token);

            sw.Stop();
            result.ResponseTimeMs = sw.ElapsedMilliseconds;
            result.TotalTokens = ((int)(response.Usage?.InputTokenCount ?? 0)) + ((int)(response.Usage?.OutputTokenCount ?? 0));
            result.InputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
            result.OutputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);

            var answer = response.Text ?? response.Messages.FirstOrDefault()?.Text ?? "";
            result.AgentOutputs = answer;
            result.ConversationLog = $"[System] 模式:{mode} RAG:{ragOn}\n[User] {tc.Question}\n[Assistant] {answer}";

            // 工具调用检测（从 answer 中提取工具名）
            result.ToolCallLog = ExtractToolCalls(answer);

            // 自动指标
            _metrics.CalculateMetrics(result, tc.ExpectedToolCalls, result.ToolCallLog,
                answer, null);

            // LLM Judge 评分
            var judgeScores = await _judge.JudgeAsync(tc, tc.Question, answer, 1);
            result.JudgeRawOutput = JsonSerializer.Serialize(judgeScores);
            result.Dimensions.AddRange(judgeScores);

            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            result.ResponseTimeMs = sw.ElapsedMilliseconds;
            result.ErrorMessage = $"超时({timeoutSec}s)";
            result.Dimensions = GetErrorDimensions("执行超时");
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.ResponseTimeMs = sw.ElapsedMilliseconds;
            result.ErrorMessage = ex.Message;
            result.Dimensions = GetErrorDimensions($"异常:{ex.Message}");
        }

        return result;
    }

    private static string BuildEvalPrompt(string question, string mode, bool ragOn)
    {
        var prefix = mode switch
        {
            "Sequential" => "[Sequential模式—Researcher→Writer→Critic流水线]\n",
            "Concurrent" => "[Concurrent模式—多Agent并行回答]\n",
            "Handoff" => "[Handoff模式—根据问题类型路由到专业Agent]\n",
            "GroupChat" => "[GroupChat模式—多Agent讨论后汇总]\n",
            "Magentic" => "[Magentic模式—自主编排决策]\n",
            "Crm" => "[CRM模式—客户管理专用Agent]\n",
            _ => ""
        };
        if (ragOn) prefix += "[RAG已启用—优先检索知识库]\n";
        return prefix + question;
    }

    private static string ExtractToolCalls(string text)
    {
        var tools = new List<string>();
        var patterns = new[] { "search_customers", "create_customer", "get_customer_detail",
            "add_followup", "delete_customer", "search_knowledge_base", "search_memory" };
        foreach (var tool in patterns)
            if (text.Contains(tool, StringComparison.OrdinalIgnoreCase))
                tools.Add(tool);
        return tools.Count > 0 ? JsonSerializer.Serialize(tools) : "[]";
    }

    private static List<DimensionScore> GetErrorDimensions(string reason)
        => new()
        {
            new() { Dimension = EvalDimension.Accuracy, Score = 0, Weight = 1.5, Reasoning = reason },
            new() { Dimension = EvalDimension.Completeness, Score = 0, Weight = 1.2, Reasoning = reason },
            new() { Dimension = EvalDimension.Relevance, Score = 0, Weight = 1.0, Reasoning = reason },
            new() { Dimension = EvalDimension.Hallucination, Score = 0, Weight = 2.0, Reasoning = reason },
            new() { Dimension = EvalDimension.ToolAccuracy, Score = 0, Weight = 1.2, Reasoning = reason },
            new() { Dimension = EvalDimension.Efficiency, Score = 0, Weight = 0.8, Reasoning = reason },
        };

    private ABComparison GenerateComparison(List<EvalCaseResult> allResults, List<string> modes)
    {
        var comparison = new ABComparison { Summary = "" };
        var summaries = new List<string>();

        foreach (var mode in modes)
        {
            var modeResults = allResults.Where(r => r.Mode == mode).ToList();
            if (modeResults.Count == 0) continue;

            var mc = new ModeComparison
            {
                Mode = mode,
                CasesRun = modeResults.Count,
                SuccessCount = modeResults.Count(r => r.Success),
                WeightedTotal = Math.Round(modeResults.Average(r => r.WeightedTotal), 1),
                AvgResponseMs = (long)modeResults.Average(r => r.ResponseTimeMs),
                TotalTokens = modeResults.Sum(r => r.TotalTokens),
                AvgToolAccuracy = Math.Round(modeResults.Average(r => r.ToolCallAccuracy), 3)
            };

            foreach (var dim in Enum.GetValues<EvalDimension>())
                mc.AvgScores[dim] = Math.Round(
                    modeResults.SelectMany(r => r.Dimensions).Where(d => d.Dimension == dim).Average(d => d.Score), 1);

            comparison.ModeComparisons.Add(mc);
            summaries.Add($"{mode}: 总分{ mc.WeightedTotal} 工具准确率{mc.AvgToolAccuracy * 100:F0}%");
        }

        // RAG 开关对比
        var ragOn = allResults.Where(r => r.RagEnabled).ToList();
        var ragOff = allResults.Where(r => !r.RagEnabled).ToList();
        if (ragOn.Count > 0 && ragOff.Count > 0)
        {
            comparison.RagComparisons.Add(new RagComparison
            {
                RagEnabled = true,
                WeightedTotal = Math.Round(ragOn.Average(r => r.WeightedTotal), 1),
                AvgHallucination = Math.Round(ragOn.SelectMany(r => r.Dimensions)
                    .Where(d => d.Dimension == EvalDimension.Hallucination).Average(d => d.Score), 1),
                AvgAccuracy = Math.Round(ragOn.SelectMany(r => r.Dimensions)
                    .Where(d => d.Dimension == EvalDimension.Accuracy).Average(d => d.Score), 1)
            });
            comparison.RagComparisons.Add(new RagComparison
            {
                RagEnabled = false,
                WeightedTotal = Math.Round(ragOff.Average(r => r.WeightedTotal), 1),
                AvgHallucination = Math.Round(ragOff.SelectMany(r => r.Dimensions)
                    .Where(d => d.Dimension == EvalDimension.Hallucination).Average(d => d.Score), 1),
                AvgAccuracy = Math.Round(ragOff.SelectMany(r => r.Dimensions)
                    .Where(d => d.Dimension == EvalDimension.Accuracy).Average(d => d.Score), 1)
            });

            var gain = ragOn.Average(r => r.WeightedTotal) - ragOff.Average(r => r.WeightedTotal);
            summaries.Add($"RAG 开关对比: 开启RAG总分{ragOn.Average(r => r.WeightedTotal):F1} vs 关闭{ragOff.Average(r => r.WeightedTotal):F1}, 提升{gain:F1}分");
        }

        comparison.Summary = string.Join(" / ", summaries);
        return comparison;
    }

    private static void PushProgress(Channel<OrchestrationEvent> channel, EvalTask task,
        int done, int total)
    {
        channel.Writer.TryWrite(new OrchestrationEvent(
            OrchestrationEventType.AgentCompleted,
            Agent: "Eval", Status: "progress",
            Output: $"{done}/{total}",
            Round: done * 100 / Math.Max(total, 1)));
    }

    private List<EvalTestCase> GetCasesBySet(string set) => set switch
    {
        "quick-smoke" => PresetCases.Where(c => c.Tags.Contains("快速")).ToList(),
        "rag" => PresetCases.Where(c => c.Category == "RAG").ToList(),
        "tool" => PresetCases.Where(c => c.Category == "工具调用").ToList(),
        "crm" => PresetCases.Where(c => c.Category is "CRM" or "人审").ToList(),
        _ => PresetCases.ToList()
    };

    private static readonly EvalTestCase[] PresetCases =
    [
        new() { Id=1, Title="AI概念解释", Category="通用问答", Tags="基础,快速", Question="什么是AI Transformer架构？", ExpectedKeyPoints="自注意力\n编码器-解码器", ApplicableModes="Sequential,Concurrent" },
        new() { Id=6, Title="研究报告任务", Category="多Agent协作", Tags="研究,写作", Question="写一篇300字短文介绍量子计算", ExpectedKeyPoints="量子比特\n应用", ApplicableModes="Sequential,GroupChat" },
        new() { Id=11, Title="知识库事实查询", Category="RAG", Tags="知识库", Question="根据知识库,DeepSeek V4特性？", ExpectedKeyPoints="MoE\n参数规模", ApplicableModes="Sequential", RequiresKnowledgeBase=true },
        new() { Id=16, Title="客户列表查询", Category="CRM", Tags="CRM,查询", Question="查询战略级别客户", ExpectedKeyPoints="搜索CRM\n过滤等级", ApplicableModes="Crm", ExpectedToolCalls="[\"search_customers\"]" },
        new() { Id=21, Title="搜索+分析", Category="工具调用", Tags="搜索,Tavily", Question="搜索2024 AI三大突破并分析", ExpectedKeyPoints="搜索\n列突破\n分析", ApplicableModes="Sequential" },
        new() { Id=26, Title="高风险操作触发人审", Category="人审", Tags="CRM,人审", Question="删除客户ID1原因测试", ExpectedKeyPoints="触发人审\n等待审批", ApplicableModes="Crm", ExpectedToolCalls="[\"delete_customer\"]" },
    ];
}
