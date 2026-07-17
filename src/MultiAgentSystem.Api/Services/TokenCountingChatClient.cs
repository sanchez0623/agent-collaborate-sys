// ============================================================
// TokenCountingChatClient - IChatClient 包装器，统计 Token 用量
// 用 AsyncLocal 隔离每次评测的 token 累计
// 用法：EvalService 启动用例前调 StartSession()，用例结束调 EndSession()
// ============================================================

using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Services;

/// <summary>Token 用量累计（按会话隔离）</summary>
public class TokenUsage
{
    public int InputTokens;
    public int OutputTokens;
    public int TotalTokens => InputTokens + OutputTokens;
    public int CallCount;
}

/// <summary>Token 计数包装器，捕获 IChatClient 的 Usage 并累加到 AsyncLocal</summary>
public class TokenCountingChatClient : DelegatingChatClient
{
    private static readonly AsyncLocal<TokenUsage?> _current = new();
    private static readonly ConcurrentDictionary<string, TokenUsage> _bySession = new();

    /// <summary>开始新计数（每次评测用例前调用）</summary>
    public static void StartSession(string sessionId)
    {
        var usage = new TokenUsage();
        _bySession[sessionId] = usage;
        _current.Value = usage;
    }

    /// <summary>取走会话累计并清空</summary>
    public static TokenUsage EndSession(string sessionId)
    {
        if (_bySession.TryRemove(sessionId, out var usage))
        {
            _current.Value = null;
            return usage;
        }
        return new TokenUsage();
    }

    public TokenCountingChatClient(IChatClient inner) : base(inner) { }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var resp = await base.GetResponseAsync(messages, options, ct);
        AccumulateFromDetails(resp.Usage);
        return resp;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        long inTok = 0, outTok = 0;
        await foreach (var upd in base.GetStreamingResponseAsync(messages, options, ct))
        {
            if (upd.Contents != null)
            {
                foreach (var c in upd.Contents)
                {
                    if (c is UsageContent uc && uc.Details != null)
                    {
                        inTok += uc.Details.InputTokenCount ?? 0;
                        outTok += uc.Details.OutputTokenCount ?? 0;
                    }
                }
            }
            yield return upd;
        }
        var local = _current.Value;
        if (local != null && (inTok > 0 || outTok > 0))
        {
            local.InputTokens += (int)inTok;
            local.OutputTokens += (int)outTok;
            local.CallCount++;
        }
    }

    private static void AccumulateFromDetails(UsageDetails? details)
    {
        if (details == null) return;
        var local = _current.Value;
        if (local == null) return;
        local.InputTokens += (int)(details.InputTokenCount ?? 0);
        local.OutputTokens += (int)(details.OutputTokenCount ?? 0);
        local.CallCount++;
    }
}
