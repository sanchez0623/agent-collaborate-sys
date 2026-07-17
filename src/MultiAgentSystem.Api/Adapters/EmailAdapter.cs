// ============================================================
// EmailAdapter - 邮件系统适配器（集成 Demo 核心）
//
// 面试价值：
//   证明 IExternalSystemAdapter 接口的可扩展性——
//   新增一个外部系统只需实现一个 Adapter，Agent 代码零改动。
//   本适配器"模拟"发送邮件（生产环境对接 SendGrid/SMTP），
//   邮件记录写入 SQLite audit_logs 表，保留完整审计轨迹。
//
// 端到端场景：
//   CRM 跟进 → FlowAgent 生成邮件 → 前端人审 → EmailAdapter "发送"
// ============================================================

using System.Text.Json;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Adapters;

public class EmailAdapter : IExternalSystemAdapter
{
    private readonly BusinessStore _store;
    private readonly ILogger<EmailAdapter> _logger;

    public ExternalSystemType System => ExternalSystemType.Email;

    public IReadOnlyList<string> SupportedResources { get; } = new[] { "email" };

    public EmailAdapter(BusinessStore store, ILogger<EmailAdapter> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<ExternalOperationResult> ExecuteAsync(ExternalOperationRequest request, CancellationToken ct = default)
    {
        if (request.System != ExternalSystemType.Email)
            return new ExternalOperationResult(false, "", $"EmailAdapter 不支持系统类型 {request.System}");

        return request.Operation switch
        {
            ExternalOperationType.Create => await SendEmailAsync(request.Parameters, ct),
            _ => new ExternalOperationResult(false, "", $"EmailAdapter 不支持操作 {request.Operation}")
        };
    }

    /// <summary>
    /// 模拟发送邮件（生产换 SMTP/SendGrid API）
    /// 写入 audit_logs 保留审计轨迹
    /// </summary>
    private async Task<ExternalOperationResult> SendEmailAsync(string parametersJson, CancellationToken ct)
    {
        try
        {
            var email = JsonSerializer.Deserialize<EmailPayload>(parametersJson);
            if (email == null || string.IsNullOrWhiteSpace(email.To))
                return new ExternalOperationResult(false, "", "邮件参数不完整：缺少收件人");

            // 模拟发送延迟
            await Task.Delay(300, ct);

            // 写入审计日志
            var auditPayload = JsonSerializer.Serialize(new
            {
                email.To,
                email.Subject,
                bodyLength = email.Body?.Length ?? 0,
                sentAt = DateTime.UtcNow.ToString("o")
            });
            await _store.AuditAsync(AuditLogType.Integration, "EmailAdapter", $"发送邮件至 {email.To}", auditPayload);

            _logger.LogInformation("邮件发送成功: To={To}, Subject={Subject}", email.To, email.Subject);

            return new ExternalOperationResult(true,
                JsonSerializer.Serialize(new { sent = true, to = email.To, subject = email.Subject }),
                null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "邮件发送失败");
            return new ExternalOperationResult(false, "", $"邮件发送异常: {ex.Message}");
        }
    }

    private record EmailPayload(string To, string? Cc, string Subject, string Body);
}
