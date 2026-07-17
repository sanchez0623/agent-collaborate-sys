// ============================================================
// EvalService - 评测引擎（真实编排管道版）
// 不再 mock 调用，直接走 AgentOrchestrator 的 7 种策略
// 工具调用从 OrchestrationEvent 流中真实捕获
// ============================================================

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using ChatMsg = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Strategies;

namespace MultiAgentSystem.Api.Services;

public class EvalService
{
    private readonly IChatClient _chatClient;
    private readonly TestCaseStore _caseStore;
    private readonly EvalReportStore _reportStore;
    private readonly JudgeService _judge;
    private readonly MetricCalculator _metrics;
    private readonly ILogger<EvalService> _logger;
    private readonly ApprovalCoordinator _approvals;

    // 7 种编排策略
    private readonly SequentialStrategy _sequential;
    private readonly ConcurrentStrategy _concurrent;
    private readonly HandoffStrategy _handoff;
    private readonly GroupChatStrategy _groupChat;
    private readonly MagenticStrategy _magentic;
    private readonly CrmStrategy _crm;
    private readonly RagStrategy _rag;

    private static readonly ConcurrentDictionary<string, EvalTask> RunningTasks = new();
    private static readonly ConcurrentDictionary<string, Channel<OrchestrationEvent>> ProgressChannels = new();

    public EvalService(
        IChatClient chatClient, TestCaseStore caseStore, EvalReportStore reportStore,
        JudgeService judge, MetricCalculator metrics, ILogger<EvalService> logger,
        ApprovalCoordinator approvals,
        SequentialStrategy sequential, ConcurrentStrategy concurrent, HandoffStrategy handoff,
        GroupChatStrategy groupChat, MagenticStrategy magentic, CrmStrategy crm, RagStrategy rag)
    {
        _chatClient = chatClient; _caseStore = caseStore; _reportStore = reportStore;
        _judge = judge; _metrics = metrics; _logger = logger; _approvals = approvals;
        _sequential = sequential; _concurrent = concurrent; _handoff = handoff;
        _groupChat = groupChat; _magentic = magentic; _crm = crm; _rag = rag;
    }

    // ===== 公开 API =====

    public async Task<string> RunAsync(EvalRunRequest req)
    {
        var task = new EvalTask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            CaseSet = req.CaseSet, Modes = req.Modes,
            Status = "running", CreatedAt = DateTime.UtcNow
        };
        RunningTasks[task.Id] = task;
        ProgressChannels[task.Id] = Channel.CreateUnbounded<OrchestrationEvent>();

        await _reportStore.SaveTaskAsync(task);
        _ = ExecuteAsync(task, req, ProgressChannels[task.Id]);
        return task.Id;
    }

    public ChannelReader<OrchestrationEvent>? GetProgressChannel(string taskId)
        => ProgressChannels.TryGetValue(taskId, out var ch) ? ch.Reader : null;

    public async Task<EvalTask?> GetTaskAsync(string taskId)
        => await _reportStore.GetTaskAsync(taskId);

    public async Task<EvalReport?> GetReportAsync(string taskId)
        => await _reportStore.GetReportAsync(taskId);

    public async Task<List<EvalReport>> ListReportsAsync(int limit = 20)
        => await _reportStore.ListReportsAsync(limit);

    // ===== 后台执行主循环 =====

    private async Task ExecuteAsync(EvalTask task, EvalRunRequest req, Channel<OrchestrationEvent> channel)
    {
        try
        {
            var cases = req.CaseSet == "all" || req.CaseSet == "full"
                ? await _caseStore.GetAllAsync()
                : await GetCasesBySetAsync(req.CaseSet);

            if (cases.Count == 0)
            {
                _logger.LogWarning("评测无可用用例: caseSet={Set}", req.CaseSet);
                channel.Writer.TryComplete();
                return;
            }

            var ragModes = new List<bool> { req.EnableRag };
            if (req.DisableRag) ragModes.Add(false);

            task.TotalCases = cases.Count;
            _logger.LogInformation("评测开始: taskId={Id} caseSet={Set} cases={Cases} modes={Modes} rag={Rag} abRag={AbRag} concurrency={Conc} timeout={T}s",
                task.Id, req.CaseSet, cases.Count, string.Join(",", req.Modes), req.EnableRag, req.DisableRag, req.MaxConcurrency, req.TimeoutSeconds);

            await _reportStore.UpdateTaskProgressAsync(task.Id, 0, 0);

            var allResults = new List<EvalCaseResult>();
            var semaphore = new SemaphoreSlim(req.MaxConcurrency > 0 ? req.MaxConcurrency : 1);
            int completed = 0, failed = 0;

            foreach (var mode in req.Modes)
            foreach (var tc in cases)
            foreach (var ragOn in ragModes)
            {
                await semaphore.WaitAsync();
                try
                {
                    _logger.LogInformation("评测用例开始: case=#{Id} {Title} mode={Mode} rag={Rag}", tc.Id, tc.Title, mode, ragOn);

                    var result = await ExecuteRealCaseAsync(tc, mode, ragOn, req.TimeoutSeconds);
                    allResults.Add(result);

                    if (result.Success) completed++; else failed++;
                    task.CompletedCases = completed;
                    task.FailedCases = failed;

                    result.TaskId = task.Id;
                    await _reportStore.SaveCaseResultAsync(result);
                    await _reportStore.UpdateTaskProgressAsync(task.Id, completed, failed);

                    _logger.LogInformation("评测用例完成: case=#{Id} success={Ok} score={Score:F1} dims={Dims} ms={Ms} tokens={Tk} tools={Tl} err={Err}",
                        tc.Id, result.Success,
                        result.Dimensions.Count > 0 ? result.Dimensions.Average(d => d.Score) : 0,
                        result.Dimensions.Count,
                        result.ResponseTimeMs, result.TotalTokens,
                        result.ToolCallLog,
                        result.ErrorMessage ?? "-");

                    PushProgress(channel, task, completed + failed, cases.Count * req.Modes.Count * ragModes.Count);
                }
                finally { semaphore.Release(); }
            }

            // A/B 对比 + LLM 分析
            _logger.LogInformation("评测全部用例完成,生成A/B对比: totalResults={Count}", allResults.Count);
            var comparison = GenerateComparison(allResults, req.Modes);
            try { comparison.Summary = await AnalyzeBestModeAsync(comparison, allResults); }
            catch (Exception ex) { _logger.LogWarning(ex, "LLM分析最佳模式失败"); }

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

            _logger.LogInformation("保存评测报告: taskId={Id} overall={Score:F1} success={Ok}/{Total} ms={Ms} tokens={Tk}",
                task.Id, report.OverallScore, completed, allResults.Count, report.AvgResponseMs, report.TotalTokens);

            await _reportStore.FinalizeTaskAsync(report);
            task.Status = "completed";
            task.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("评测任务完成: taskId={Id}", task.Id);
            channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "评测执行失败: taskId={Id}", task.Id);
            task.Status = "failed";
            channel.Writer.TryComplete();
        }
        finally { RunningTasks.TryRemove(task.Id, out _); }
    }

    // ===== 真实编排管道执行 =====

    private async Task<EvalCaseResult> ExecuteRealCaseAsync(EvalTestCase tc, string mode, bool ragOn, int timeoutSec)
    {
        var result = new EvalCaseResult
        {
            TaskId = "", TestCaseId = tc.Id, Mode = mode, RagEnabled = ragOn, Success = false
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var capturedEvents = new List<OrchestrationEvent>();

        try
        {
            var strategy = ResolveStrategy(mode, ragOn);
            var evalSessionId = $"eval_{Guid.NewGuid():N[..8]}";
            ApprovalCoordinator.CurrentSessionId = evalSessionId;

            // 注册人审自动决策（评测场景：第1次拒绝，后续自动同意）
            var approvalCount = 0;
            _approvals.RegisterSession(evalSessionId, async (req) =>
            {
                var decision = Interlocked.Increment(ref approvalCount) <= 1 ? ApprovalStatus.Rejected : ApprovalStatus.Approved;
                await _approvals.ResolveAsync(req.Id, decision, "system", "评测自动决策", null);
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

            _logger.LogDebug("策略开始执行: case=#{Id} mode={Mode} strategy={Strategy}",
                tc.Id, mode, strategy.GetType().Name);

            var finalOutput = await strategy.ExecuteAsync(
                tc.Question,
                new List<MultiAgentSystem.Api.Models.ChatMessage>() as IReadOnlyList<MultiAgentSystem.Api.Models.ChatMessage>,
                e => { lock (capturedEvents) capturedEvents.Add(e); },
                cts.Token);

            _logger.LogDebug("策略执行完成: case=#{Id} events={Count} outputLen={Len}",
                tc.Id, capturedEvents.Count, finalOutput.Length);

            result.AgentOutputs = finalOutput;
            result.ConversationLog = BuildConversationLog(capturedEvents, tc.Question, finalOutput);

            // 提取真实工具调用
            var toolEvents = capturedEvents.Where(e => e.Type == OrchestrationEventType.ToolCall).ToList();
            result.ToolCallLog = JsonSerializer.Serialize(toolEvents.Select(t => new { tool = t.ToolName, args = t.ToolArgs, result = t.ToolResult }));
            result.ActualToolCount = toolEvents.Count;

            // 提取审批事件
            var approvalEvents = capturedEvents.Where(e => e.Type is OrchestrationEventType.ApprovalRequired or OrchestrationEventType.ApprovalResult).ToList();

        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = $"超时 ({timeoutSec}s)";
            result.Dimensions = GetErrorDimensions(result.ErrorMessage);
            result.Success = false;
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            result.Dimensions = GetErrorDimensions(ex.Message);
            return result;
        }
        finally { sw.Stop(); }

        result.ResponseTimeMs = sw.ElapsedMilliseconds;

        // 自动指标（基于真实数据）
        _metrics.CalculateMetrics(result, tc.ExpectedToolCalls, result.ToolCallLog, result.AgentOutputs, null);

        // LLM Judge 评分（基于真实对话记录）
        _logger.LogDebug("用例开始Judge评分: case=#{Id}", tc.Id);
        var judgeScores = await _judge.JudgeAsync(tc, tc.Question, result.AgentOutputs, 1);
        result.JudgeRawOutput = JsonSerializer.Serialize(judgeScores);
        result.Dimensions.AddRange(judgeScores);
        result.Success = true;

        _logger.LogDebug("用例Judge完成: case=#{Id} score={Avg:F1} dims={Count} tools={Actual}/{Expected}",
            tc.Id, judgeScores.Average(d => d.Score), judgeScores.Count,
            result.ActualToolCount, tc.ExpectedToolCalls?.Length ?? 0);

        return result;
    }

    // ===== 策略选择 =====

    private IOrchestrationStrategy ResolveStrategy(string mode, bool ragOn)
        => mode switch
        {
            "Sequential" => _sequential,
            "Concurrent" => _concurrent,
            "Handoff" => _handoff,
            "GroupChat" => _groupChat,
            "Magentic" => _magentic,
            "Crm" => _crm,
            "Rag" => _rag,
            _ => _sequential
        };

    // ===== 对话日志构建 =====

    private static string BuildConversationLog(List<OrchestrationEvent> events, string question, string finalOutput)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[User] {question}");
        foreach (var e in events)
        {
            if (e.Type == OrchestrationEventType.AgentStarted)
                sb.AppendLine($"[Agent] {e.Agent} started (round {e.Round})");
            else if (e.Type == OrchestrationEventType.AgentCompleted)
                sb.AppendLine($"[Agent] {e.Agent} completed → {e.Output}");
            else if (e.Type == OrchestrationEventType.ToolCall)
                sb.AppendLine($"[Tool] {e.ToolName}({e.ToolArgs}) → {e.ToolResult}");
            else if (e.Type == OrchestrationEventType.Handoff)
                sb.AppendLine($"[Handoff] {e.FromAgent} → {e.ToAgent}");
        }
        sb.AppendLine($"[FinalAnswer] {finalOutput}");
        return sb.ToString();
    }

    // ===== 用例集 =====

    private async Task<List<EvalTestCase>> GetCasesBySetAsync(string setName)
    {
        var all = await _caseStore.GetAllAsync();
        var filtered = all.Where(tc => setName switch
        {
            "quick-smoke" => tc.Tags.Contains("快速"),
            "rag" => tc.Category == "RAG",
            "tool" => tc.Category == "工具调用",
            "crm" => tc.Category is "CRM" or "人审",
            _ => true
        }).ToList();

        _logger.LogInformation("用例筛选: set={Set} total={Total} filtered={Filtered}",
            setName, all.Count, filtered.Count);
        return filtered;
    }

    // ===== SSE 进度 + 其他工具方法 =====

    private static void PushProgress(Channel<OrchestrationEvent> channel, EvalTask task, int done, int total)
    {
        channel.Writer.TryWrite(new OrchestrationEvent(
            OrchestrationEventType.AgentDelta,
            Status: $"{done}/{total}",
            Output: JsonSerializer.Serialize(new
            {
                taskId = task.Id, done, total,
                completed = task.CompletedCases, failed = task.FailedCases
            })));
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

    // ===== A/B 对比 =====

    private async Task<string> AnalyzeBestModeAsync(ABComparison comp, List<EvalCaseResult> results)
    {
        if (comp.ModeComparisons.Count < 2) return comp.Summary;
        var best = comp.ModeComparisons.OrderByDescending(m => m.WeightedTotal).First();
        var others = comp.ModeComparisons.Where(m => m.Mode != best.Mode).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("你是一个多Agent系统评测分析师。请用1-2句话分析为什么以下模式表现最佳：\n");
        sb.AppendLine($"## 最佳模式：{best.Mode}");
        sb.AppendLine($"加权总分 {best.WeightedTotal} | 延迟 {best.AvgResponseMs}ms | 工具准确率 {best.AvgToolAccuracy:P0}\n");
        sb.AppendLine("维度对比：");
        foreach (var dim in Enum.GetValues<EvalDimension>())
        {
            var bestScore = best.AvgScores.GetValueOrDefault(dim, 0);
            var otherAvg = others.Average(m => m.AvgScores.GetValueOrDefault(dim, 0));
            sb.AppendLine($"- {dim}: {best.Mode}={bestScore:F1} vs 其他平均={otherAvg:F1}");
        }
        sb.AppendLine($"\n其他模式：{string.Join(",", others.Select(m => $"{m.Mode}({m.WeightedTotal})"))}");
        sb.AppendLine("\n请分析该模式表现最优的**架构原理**，不是复述数字。输出纯文本，不要markdown格式。");
        var msg = new ChatMsg(ChatRole.User, sb.ToString());
        var resp = await _chatClient.GetResponseAsync([msg], new() { MaxOutputTokens = 256 });
        return resp.Text?.Trim() ?? comp.Summary;
    }

    private ABComparison GenerateComparison(List<EvalCaseResult> allResults, List<string> modes)
    {
        var comparison = new ABComparison { Summary = "" };
        foreach (var mode in modes)
        {
            var modeResults = allResults.Where(r => r.Mode == mode && r.Success).ToList();
            if (modeResults.Count == 0) continue;
            var dimScores = new Dictionary<EvalDimension, double>();
            foreach (var dim in Enum.GetValues<EvalDimension>())
            {
                var scores = modeResults.SelectMany(r => r.Dimensions).Where(d => d.Dimension == dim).ToList();
                dimScores[dim] = scores.Count > 0 ? scores.Average(d => d.Score) : 0;
            }
            comparison.ModeComparisons.Add(new ModeComparison
            {
                Mode = mode,
                WeightedTotal = Math.Round(modeResults.Average(r => r.WeightedTotal), 1),
                AvgResponseMs = (long)modeResults.Average(r => r.ResponseTimeMs),
                AvgToolAccuracy = Math.Round(modeResults.Average(r => r.ToolCallAccuracy), 2),
                AvgScores = dimScores,
                CasesRun = modeResults.Count,
                TotalTokens = (int)modeResults.Average(r => r.TotalTokens)
            });
        }
        if (comparison.ModeComparisons.Count > 1)
        {
            var best = comparison.ModeComparisons.OrderByDescending(m => m.WeightedTotal).First();
            var runner = comparison.ModeComparisons.OrderByDescending(m => m.WeightedTotal).Skip(1).First();
            comparison.Summary = $"{best.Mode}: 总分{best.WeightedTotal} 工具准确率{best.AvgToolAccuracy:P0} / {runner.Mode}: 总分{runner.WeightedTotal} 工具准确率{runner.AvgToolAccuracy:P0}";
        }
        return comparison;
    }
}
