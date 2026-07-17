// ============================================================
// EmailService - 跟进邮件生成服务（集成 Demo 核心逻辑）
//
// 端到端流程：
//   1. 用户创建 CRM 跟进记录
//   2. 调用本服务生成邮件（LLM 根据客户画像 + 跟进内容生成）
//   3. 前端展示邮件预览，人工审核
//   4. 审核通过后 EmailAdapter 发送
//
// 面试价值：
//   - 证明"CRM 数据 → Agent 智能生成 → 外部系统发送"的完整链路
//   - IExternalSystemAdapter 的 EmailAdapter 实现证明接口扩展性
//   - 无需修改 Agent 代码，新增外部系统零侵入
// ============================================================

using System.Text.Json;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Adapters;
using MultiAgentSystem.Api.Models;
using ChatMsg = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace MultiAgentSystem.Api.Services;

public class EmailService
{
    private readonly IChatClient _chatClient;
    private readonly BusinessStore _store;
    private readonly EmailAdapter _emailAdapter;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IChatClient chatClient, BusinessStore store, EmailAdapter emailAdapter, ILogger<EmailService> logger)
    {
        _chatClient = chatClient;
        _store = store;
        _emailAdapter = emailAdapter;
        _logger = logger;
    }

    /// <summary>
    /// 根据跟进记录生成邮件
    /// </summary>
    public async Task<EmailDraft?> GenerateFollowUpEmailAsync(int followUpId)
    {
        // 1. 获取跟进记录 + 客户信息
        var followUp = await _store.GetFollowUpAsync(followUpId);
        if (followUp == null)
        {
            _logger.LogWarning("跟进记录不存在: {Id}", followUpId);
            return null;
        }

        var customer = await _store.GetCustomerAsync(followUp.CustomerId);
        if (customer == null)
        {
            _logger.LogWarning("客户不存在: {Id}", followUp.CustomerId);
            return null;
        }

        // 2. 获取该客户的历史跟进（上下文）
        var historyFollowUps = (await _store.ListFollowUpsAsync(followUp.CustomerId)).Take(3).ToList();

        // 3. 构建 LLM prompt
        var prompt = BuildEmailPrompt(customer, followUp, historyFollowUps);

        // 4. 调用 LLM 生成邮件
        var messages = new List<ChatMsg>
        {
            new(ChatRole.System, "你是一名专业的商务助理，根据客户跟进记录生成跟进邮件。邮件应专业、简洁、个性化，针对客户等级调整语气。输出严格 JSON 格式。"),
            new(ChatRole.User, prompt)
        };

        try
        {
            var response = await _chatClient.GetResponseAsync(messages, cancellationToken: CancellationToken.None);
            var rawText = response.Text ?? response.Messages.FirstOrDefault()?.Text ?? "";

            // 5. 解析 JSON
            var draft = ParseEmailDraft(rawText);
            if (draft == null)
            {
                _logger.LogWarning("LLM 返回非 JSON 格式: {Raw}", rawText.Truncate(200));
                return null;
            }

            draft.CustomerId = customer.Id;
            draft.CustomerName = customer.Name;
            draft.CustomerEmail = customer.Email ?? $"{customer.Name}@example.com";
            draft.FollowUpId = followUpId;
            draft.GeneratedAt = DateTime.UtcNow;

            return draft;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM 生成邮件失败");
            return null;
        }
    }

    /// <summary>
    /// 发送邮件（经 EmailAdapter）
    /// </summary>
    public async Task<bool> SendEmailAsync(EmailDraft draft)
    {
        var result = await _emailAdapter.ExecuteAsync(new ExternalOperationRequest(
            ExternalSystemType.Email, "email", ExternalOperationType.Create, null,
            JsonSerializer.Serialize(new
            {
                to = draft.CustomerEmail,
                subject = draft.Subject,
                body = draft.Body
            })));

        return result.Success;
    }

    // ---------- 私有方法 ----------

    private static string BuildEmailPrompt(Customer customer, FollowUp followUp, List<FollowUp> history)
    {
        var historyText = history.Count > 0
            ? string.Join("\n", history.Select(h =>
                $"- [{h.CreatedAt:yyyy-MM-dd}] ({h.Method ?? "电话"}) {h.Content}"))
            : "无历史记录";

        var jsonExample = @"{""subject"":""邮件标题"",""body"":""邮件正文""}";

        return $"请根据以下信息生成一封专业的客户跟进邮件。\n\n" +
               $"客户信息：\n" +
               $"- 客户：{customer.Name}\n" +
               $"- 公司：{customer.Company ?? "未填写"}\n" +
               $"- 级别：{customer.Level ?? "普通"}\n" +
               $"- 电话：{customer.Phone ?? "未填写"}\n" +
               $"- 负责人：{customer.Owner ?? "未指定"}\n\n" +
               $"本次跟进：\n" +
               $"- 方式：{followUp.Method ?? "电话"}\n" +
               $"- 内容：{followUp.Content}\n\n" +
               $"历史跟进：\n{historyText}\n\n" +
               $"要求：\n" +
               $"1. 邮件标题简洁有力\n" +
               $"2. 正文 150-300 字，语气根据客户级别调整（战略/重要→正式，普通→友好）\n" +
               $"3. 提及本次跟进的具体话题\n" +
               $"4. 留自然的话头引导下次沟通\n\n" +
               $"输出严格 JSON 格式（不要 markdown 代码块）：\n{jsonExample}";
    }

    private static EmailDraft? ParseEmailDraft(string text)
    {
        // 去除可能的 markdown 代码块包裹
        var json = text.Trim();
        if (json.StartsWith("```")) json = json.Substring(json.IndexOf('\n') + 1);
        if (json.EndsWith("```")) json = json[..json.LastIndexOf("```")];
        json = json.Trim();

        try
        {
            var doc = JsonSerializer.Deserialize<EmailDraftJson>(json);
            if (doc == null || string.IsNullOrWhiteSpace(doc.Subject) || string.IsNullOrWhiteSpace(doc.Body))
                return null;
            return new EmailDraft { Subject = doc.Subject, Body = doc.Body };
        }
        catch
        {
            return null;
        }
    }

    private record EmailDraftJson(string Subject, string Body);
}

/// <summary>邮件草稿</summary>
public class EmailDraft
{
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public int FollowUpId { get; set; }
    public DateTime GeneratedAt { get; set; }
}

/// <summary>字符串截断</summary>
file static class EmailStringExtensions
{
    public static string Truncate(this string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
