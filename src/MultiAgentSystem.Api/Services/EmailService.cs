// ============================================================
// EmailService - 跟进邮件生成服务（集成 Demo 核心逻辑）
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

    public async Task<EmailDraft?> GenerateFollowUpEmailAsync(int followUpId)
    {
        _logger.LogInformation("邮件生成开始: followUpId={Id}", followUpId);

        // 1. 获取跟进记录
        var followUp = await _store.GetFollowUpAsync(followUpId);
        if (followUp == null)
        {
            _logger.LogWarning("邮件生成失败: 跟进记录不存在 Id={Id}", followUpId);
            return null;
        }
        _logger.LogDebug("获取跟进记录: customerId={Cid} method={Method} content={Content}",
            followUp.CustomerId, followUp.Method, followUp.Content.Truncate(100));

        // 2. 获取客户信息
        var customer = await _store.GetCustomerAsync(followUp.CustomerId);
        if (customer == null)
        {
            _logger.LogWarning("邮件生成失败: 客户不存在 Id={Id}", followUp.CustomerId);
            return null;
        }
        _logger.LogDebug("获取客户: name={Name} company={Company} level={Level} email={Email}",
            customer.Name, customer.Company, customer.Level, customer.Email);

        // 3. 历史跟进
        var historyFollowUps = (await _store.ListFollowUpsAsync(followUp.CustomerId)).Take(3).ToList();
        _logger.LogDebug("历史跟进记录数: {Count}", historyFollowUps.Count);

        // 4. 调用 LLM
        var prompt = BuildEmailPrompt(customer, followUp, historyFollowUps);
        _logger.LogDebug("LLM prompt 长度: {Len}", prompt.Length);

        var messages = new List<ChatMsg>
        {
            new(ChatRole.System, "你是一名专业的商务助理，根据客户跟进记录生成跟进邮件。邮件应专业、简洁、个性化，针对客户等级调整语气。输出严格 JSON 格式。"),
            new(ChatRole.User, prompt)
        };

        try
        {
            _logger.LogInformation("调用 LLM 生成邮件...");
            var response = await _chatClient.GetResponseAsync(messages,
                new ChatOptions { MaxOutputTokens = 2048 },
                cancellationToken: CancellationToken.None);
            var rawText = response.Text ?? response.Messages.FirstOrDefault()?.Text ?? "";

            _logger.LogInformation("LLM 返回: length={Len} tokens(out={Out} in={In}) first={First}",
                rawText.Length,
                response.Usage?.OutputTokenCount ?? 0,
                response.Usage?.InputTokenCount ?? 0,
                rawText.Truncate(80));

            // 5. 解析 JSON
            var draft = ParseEmailDraft(rawText);
            if (draft == null)
            {
                _logger.LogWarning("JSON 解析失败: raw前80={First80} raw末80={Last80}",
                    rawText.Truncate(80), rawText.Length > 80 ? rawText[^80..] : "");
                return null;
            }

            draft.CustomerId = customer.Id;
            draft.CustomerName = customer.Name;
            draft.CustomerEmail = customer.Email ?? $"{customer.Name}@example.com";
            draft.FollowUpId = followUpId;
            draft.GeneratedAt = DateTime.UtcNow;

            _logger.LogInformation("邮件生成成功: subject={Subject} customer={Customer} email={Email}",
                draft.Subject, draft.CustomerName, draft.CustomerEmail);
            return draft;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM 调用异常");
            return null;
        }
    }

    /// <summary>发送邮件（经 EmailAdapter）</summary>
    public async Task<bool> SendEmailAsync(EmailDraft draft)
    {
        _logger.LogInformation("发送邮件: to={To} subject={Subject}", draft.CustomerEmail, draft.Subject);
        var result = await _emailAdapter.ExecuteAsync(new ExternalOperationRequest(
            ExternalSystemType.Email, "email", ExternalOperationType.Create, null,
            JsonSerializer.Serialize(new
            {
                to = draft.CustomerEmail,
                subject = draft.Subject,
                body = draft.Body
            })));

        _logger.LogInformation("发送结果: success={Success} err={Error}", result.Success, result.Error);
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
        var json = text.Trim();
        if (json.StartsWith("```")) json = json.Substring(json.IndexOf('\n') + 1);
        if (json.EndsWith("```")) json = json[..json.LastIndexOf("```")];
        json = json.Trim();

        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var doc = JsonSerializer.Deserialize<EmailDraftJson>(json, opts);
            if (doc == null || string.IsNullOrWhiteSpace(doc.Subject) || string.IsNullOrWhiteSpace(doc.Body))
                return null;
            return new EmailDraft { Subject = doc.Subject, Body = doc.Body };
        }
        catch
        {
            // 尝试修复截断的 JSON（LLM 输出被 token 限制截断时）
            // 缺少 } 是常见模式
            if (!json.EndsWith("}"))
            {
                try
                {
                    var repaired = json + @"""}";
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var doc = JsonSerializer.Deserialize<EmailDraftJson>(repaired, opts);
                    if (doc != null && !string.IsNullOrWhiteSpace(doc.Subject) && !string.IsNullOrWhiteSpace(doc.Body))
                        return new EmailDraft { Subject = doc.Subject, Body = doc.Body };
                }
                catch { }
            }
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

file static class EmailStringExtensions
{
    public static string Truncate(this string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
