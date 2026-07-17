// ============================================================
// CrmAdapter - CRM 系统适配器（IExternalSystemAdapter 首个实现）
//
// 设计思路：
//   - 把 ExternalOperationRequest 翻译成具体的 BusinessStore CRUD 调用
//   - 返回统一的 ExternalOperationResult（JSON 字符串数据）
//   - Agent 工具层只依赖 IExternalSystemAdapter，不直接碰 BusinessStore
//
// 真实接入时：把 BusinessStore 调用替换为 Salesforce/钉钉 API 即可，
//   Agent 代码与工具层零改动——这就是适配器模式的价值。
// ============================================================

using System.Text.Json;
using Microsoft.Data.Sqlite;
using MultiAgentSystem.Api.Adapters;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Adapters;

public class CrmAdapter : IExternalSystemAdapter
{
    private readonly BusinessStore _store;
    public ExternalSystemType System => ExternalSystemType.CRM;
    public IReadOnlyList<string> SupportedResources { get; } = new[] { "customers", "followups" };

    public CrmAdapter(BusinessStore store) => _store = store;

    public async Task<ExternalOperationResult> ExecuteAsync(ExternalOperationRequest req, CancellationToken ct = default)
    {
        try
        {
            return req.Resource.ToLowerInvariant() switch
            {
                "customers" => await HandleCustomersAsync(req, ct),
                "followups" => await HandleFollowUpsAsync(req, ct),
                _ => new ExternalOperationResult(false, "", $"CRM 不支持资源: {req.Resource}")
            };
        }
        catch (Exception ex)
        {
            return new ExternalOperationResult(false, "", ex.Message);
        }
    }

    private async Task<ExternalOperationResult> HandleCustomersAsync(ExternalOperationRequest req, CancellationToken ct)
    {
        var p = string.IsNullOrEmpty(req.Parameters) ? "{}" : req.Parameters;
        var json = JsonDocument.Parse(p).RootElement;

        switch (req.Operation)
        {
            case ExternalOperationType.Query:
                {
                    var keyword = json.TryGetProperty("keyword", out var k) ? k.GetString() : null;
                    var owner = json.TryGetProperty("owner", out var o) ? o.GetString() : null;
                    var list = await _store.ListCustomersAsync(owner, keyword);
                    return Ok(list);
                }
            case ExternalOperationType.Create:
                {
                    var c = new Customer
                    {
                        Name = json.GetProperty("name").GetString() ?? "",
                        Company = json.TryGetProperty("company", out var c1) ? c1.GetString() ?? "" : "",
                        Phone = json.TryGetProperty("phone", out var c2) ? c2.GetString() ?? "" : "",
                        Email = json.TryGetProperty("email", out var c3) ? c3.GetString() ?? "" : "",
                        Level = json.TryGetProperty("level", out var c4) ? c4.GetString() ?? "普通" : "普通",
                        Owner = json.TryGetProperty("owner", out var c5) ? c5.GetString() ?? "" : ""
                    };
                    var id = await _store.CreateCustomerAsync(c);
                    return Ok(new { id, message = $"已新建客户 {c.Name}" });
                }
            case ExternalOperationType.Update:
                {
                    if (string.IsNullOrEmpty(req.ResourceId)) return Fail("修改客户需提供 customerId");
                    var existing = await _store.GetCustomerAsync(int.Parse(req.ResourceId));
                    if (existing == null) return Fail("客户不存在");
                    if (json.TryGetProperty("name", out var n)) existing.Name = n.GetString() ?? existing.Name;
                    if (json.TryGetProperty("company", out var c)) existing.Company = c.GetString() ?? existing.Company;
                    if (json.TryGetProperty("phone", out var ph)) existing.Phone = ph.GetString() ?? existing.Phone;
                    if (json.TryGetProperty("email", out var e)) existing.Email = e.GetString() ?? existing.Email;
                    if (json.TryGetProperty("level", out var l)) existing.Level = l.GetString() ?? existing.Level;
                    if (json.TryGetProperty("owner", out var o)) existing.Owner = o.GetString() ?? existing.Owner;
                    await _store.UpdateCustomerAsync(existing);
                    return Ok(new { id = existing.Id, message = $"已修改客户 {existing.Name}" });
                }
            case ExternalOperationType.Delete:
                {
                    if (string.IsNullOrEmpty(req.ResourceId)) return Fail("删除客户需提供 customerId");
                    var actor = json.TryGetProperty("operator", out var op) ? op.GetString() ?? "agent" : "agent";
                    var ok = await _store.DeleteCustomerAsync(int.Parse(req.ResourceId), actor);
                    return ok ? Ok(new { message = $"已删除客户 #{req.ResourceId}" }) : Fail("客户不存在");
                }
        }
        return Fail("不支持的客户操作");
    }

    private async Task<ExternalOperationResult> HandleFollowUpsAsync(ExternalOperationRequest req, CancellationToken ct)
    {
        var p = string.IsNullOrEmpty(req.Parameters) ? "{}" : req.Parameters;
        var json = JsonDocument.Parse(p).RootElement;

        switch (req.Operation)
        {
            case ExternalOperationType.Query:
                {
                    var cid = json.GetProperty("customerId").GetInt32();
                    var list = await _store.ListFollowUpsAsync(cid);
                    return Ok(list);
                }
            case ExternalOperationType.Create:
                {
                    var f = new FollowUp
                    {
                        CustomerId = json.GetProperty("customerId").GetInt32(),
                        Method = json.TryGetProperty("method", out var m) ? m.GetString() ?? "电话" : "电话",
                        Content = json.GetProperty("content").GetString() ?? "",
                        Operator = json.TryGetProperty("operator", out var o) ? o.GetString() ?? "agent" : "agent"
                    };
                    var id = await _store.AddFollowUpAsync(f);
                    return Ok(new { id, message = $"已添加跟进 #{id}" });
                }
        }
        return Fail("不支持的跟进操作");
    }

    private static ExternalOperationResult Ok(object data) =>
        new(true, JsonSerializer.Serialize(data), null);
    private static ExternalOperationResult Fail(string err) =>
        new(false, "", err);
}
