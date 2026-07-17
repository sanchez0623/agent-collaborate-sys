// ============================================================
// MultiAgentSystem.Api - MVP-4 启动入口
// 架构：分层 + 策略模式 + 适配器模式 + 人审协调
//
// MVP-4 新增（在 MVP-2 基础上）：
//   - JWT 鉴权：登录/角色（User/Admin），[Authorize] 保护敏感接口
//   - CRM 模块：客户/联系人/跟进 CRUD REST API
//   - CRM Agent + Approver Agent：工具调用 + 风险审核
//   - 人审机制：敏感操作暂停→前端弹窗→人决策→恢复执行
//   - 工单系统：状态流转（Pending/Processing/Done/Rejected）
//   - 审计日志：所有工具调用/人审/数据变更留痕
//   - 适配器模式：IExternalSystemAdapter + CrmAdapter（ERP/OA 预留）
//   - SSE 新事件：tool_call/tool_result/approval_required/approval_result
// ============================================================

using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Adapters;
using MultiAgentSystem.Api.Agents;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;
using MultiAgentSystem.Api.Strategies;
using MultiAgentSystem.Api.Tools;
using Serilog;
using Serilog.Events;

// ========== 1. Serilog 结构化日志 ==========
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/multiagent-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// ========== 2. WebApplication 构建 ==========
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// 上传 40+ PDF 时可能超过默认 30MB 限制；调大并明确错误信息
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 200L * 1024 * 1024; // 200 MB
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", p => p
        .WithOrigins("http://localhost:5173")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath)) options.IncludeXmlComments(xmlPath);
});

builder.Services.AddHttpClient();
builder.Services.AddAuthorization();

// ========== 2. 读取 LLM 配置 ==========
var llmApiKey = builder.Configuration["LLM:ApiKey"]
    ?? throw new InvalidOperationException("请在 appsettings.json 中配置 LLM:ApiKey");
var llmBaseUrl = builder.Configuration["LLM:BaseUrl"] ?? "https://api.deepseek.com";
var llmModel = builder.Configuration["LLM:ModelId"] ?? "deepseek-v4-flash";

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var raw = ChatClientFactory.Create(llmApiKey, llmBaseUrl, llmModel);
    var logger = sp.GetService<ILogger<ResilientChatClient>>();
    return new ResilientChatClient(raw, maxRetries: 3, logger);
});

// ========== 3. 业务存储（CRM/工单/审核/审计/用户 统一 SQLite） ==========
builder.Services.AddSingleton<BusinessStore>(_ => new BusinessStore("multiagent.db"));

// ========== 4. 对话历史存储 ==========
builder.Services.AddSingleton<ConversationStore>(_ => new ConversationStore("multiagent.db"));

// ========== 5. Tavily 搜索工具 ==========
var tavilyEnabled = builder.Configuration.GetValue<bool?>("Tavily:Enabled") ?? true;
var tavilyApiKey = builder.Configuration["Tavily:ApiKey"] ?? "";
var tavilyActive = tavilyEnabled && !string.IsNullOrWhiteSpace(tavilyApiKey);
if (tavilyActive)
{
    builder.Services.AddSingleton<TavilySearchTool>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        return new TavilySearchTool(http, tavilyApiKey);
    });
}

// ========== 6. JWT 鉴权服务 ==========
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("请在 appsettings.json 中配置 Jwt:Secret（生产环境必须替换默认演示密钥）");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "MultiAgentSystem";
builder.Services.AddSingleton<JwtService>(_ => new JwtService(jwtSecret, jwtIssuer, 24));

// ========== 7. 适配器 + 人审协调器 + CRM 工具 ==========
builder.Services.AddSingleton<CrmAdapter>();
builder.Services.AddSingleton<ApprovalCoordinator>();
builder.Services.AddSingleton<CrmTools>();

// ========== 8a. MVP-3：RAG 知识库 ==========
// KnowledgeStore 复用 multiagent.db 文件，新增 5 张表
builder.Services.AddSingleton<KnowledgeStore>(_ => new KnowledgeStore("multiagent.db"));
// EmbeddingService 调 DeepSeek embedding API，失败降级为哈希向量
builder.Services.AddSingleton<EmbeddingService>();
// VectorStore 内存向量存储（演示项目避免 Docker 依赖）
builder.Services.AddSingleton<VectorStore>();
// HybridRetriever 混合检索核心（向量+关键词+RRF融合+可选重排）
builder.Services.AddSingleton<HybridRetriever>();
// MemoryStore 长期记忆 + 用户画像
builder.Services.AddSingleton<MemoryStore>();
// KnowledgeTools RAG Agent 工具集
builder.Services.AddSingleton<KnowledgeTools>();

// ========== 8c. 速率限制 ==========
builder.Services.AddSingleton(new RateLimiter(maxRequests: 100, windowSeconds: 60));

// ========== 8b. Agent 注册中心（11 个 Agent） ==========
builder.Services.AddSingleton<AgentRegistry>(sp =>
{
    var client = sp.GetRequiredService<IChatClient>();
    var tool = sp.GetService<TavilySearchTool>();
    var crmTools = sp.GetRequiredService<CrmTools>();
    var knowledgeTools = sp.GetRequiredService<KnowledgeTools>();
    return new AgentRegistry(client, tool, crmTools, knowledgeTools);
});

// ========== 9. 7 种编排策略注册 ==========
builder.Services.AddSingleton<SequentialStrategy>();
builder.Services.AddSingleton<ConcurrentStrategy>();
builder.Services.AddSingleton<HandoffStrategy>();
builder.Services.AddSingleton<GroupChatStrategy>();
builder.Services.AddSingleton<MagenticStrategy>();
builder.Services.AddSingleton<CrmStrategy>();
builder.Services.AddSingleton<RagStrategy>();

var app = builder.Build();

// ========== 10. 中间件管道 ==========
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "MultiAgentSystem API v1");
    options.RoutePrefix = "swagger";
});

app.UseCors("AllowFrontend");
app.UseRouting();
app.UseAuthorization();

// 全局异常处理 —— 任何未捕获异常返回 JSON，避免前端 r.json() 抛 "Unexpected end of JSON input"
app.UseExceptionHandler(errApp =>
{
    errApp.Run(async ctx =>
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var msg = ex?.Error?.Message ?? "服务器内部错误";
        // 请求体过大 (413) 给出有用信息
        if (ex?.Error is Microsoft.AspNetCore.Http.BadHttpRequestException badHttp
            && badHttp.StatusCode == 413)
        {
            ctx.Response.StatusCode = 413;
            msg = $"上传文件过大（当前限制 200MB）：{badHttp.Message}";
        }
        await ctx.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(new { error = true, message = msg }));
    });
});

// ========== 11. 辅助函数 ==========
static IOrchestrationStrategy ResolveStrategy(
    string? mode,
    SequentialStrategy seq, ConcurrentStrategy con, HandoffStrategy hof,
    GroupChatStrategy grp, MagenticStrategy mag, CrmStrategy crm, RagStrategy rag) => (mode?.ToLowerInvariant()) switch
    {
        "sequential" => seq,
        "concurrent" => con,
        "handoff" => hof,
        "groupchat" or "group_chat" or "group" => grp,
        "magentic" or "route" or "router" => mag,
        "crm" => crm,
        "rag" or "knowledge" => rag,
        _ => seq,
    };

static string ToSnakeCase(string name)
{
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < name.Length; i++)
    {
        var c = name[i];
        if (i > 0 && char.IsUpper(c)) sb.Append('_');
        sb.Append(char.ToLowerInvariant(c));
    }
    return sb.ToString();
}

// 从请求头解析当前用户（JWT 校验）
static (string user, string role) GetCurrentUser(HttpContext ctx)
{
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ")) return ("guest", "User");
    var token = auth["Bearer ".Length..];
    var jwt = ctx.RequestServices.GetRequiredService<JwtService>();
    var principal = jwt.Validate(token);
    if (principal == null) return ("guest", "User");
    var user = principal.Identity?.Name ?? "guest";
    var role = principal.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "User";
    return (user, role);
}

// ========== 12. 系统与健康检查 ==========
app.MapGet("/api/health", () => new
{
    Status = "ok",
    Time = DateTime.Now,
    TavilyEnabled = tavilyActive,
    Agents = new[] { "Researcher", "Writer", "Critic", "Analyst", "Coder", "Consultant", "Support", "Coordinator", "CrmAgent", "Approver", "KnowledgeAgent" },
    Modes = new[] { "sequential", "concurrent", "handoff", "groupchat", "magentic", "crm", "rag" }
})
.WithTags("系统");

// ========== 13. 认证：登录 ==========
app.MapPost("/api/auth/login", async (LoginRequest req, BusinessStore store, JwtService jwt) =>
{
    var user = await store.GetUserAsync(req.Username);
    if (user == null || !BusinessStore.VerifyPwd(req.Password, user.PasswordHash))
        return Results.Unauthorized();

    await store.AuditAsync(AuditLogType.Auth, user.Username, "登录", null);
    var token = jwt.Issue(user);
    return Results.Ok(new LoginResponse(token, user.Username, user.Role.ToString(), user.DisplayName));
})
.WithTags("认证");

app.MapGet("/api/auth/me", (HttpContext ctx, JwtService jwt) =>
{
    var (user, role) = GetCurrentUser(ctx);
    return Results.Ok(new { username = user, role });
}).WithTags("认证");

// ========== 14. CRM 客户管理 REST API ==========
// 所有接口可选 JWT；带 JWT 时普通用户只能看自己的客户，Admin 看全部
app.MapGet("/api/crm/customers", async (HttpContext ctx, BusinessStore store, string? keyword) =>
{
    var (user, role) = GetCurrentUser(ctx);
    var owner = role == "Admin" ? null : user;
    var list = await store.ListCustomersAsync(owner, keyword);
    return Results.Ok(list);
}).WithTags("CRM客户");

app.MapGet("/api/crm/customers/{id}", async (int id, BusinessStore store) =>
{
    var c = await store.GetCustomerAsync(id);
    return c == null ? Results.NotFound() : Results.Ok(c);
}).WithTags("CRM客户");

app.MapPost("/api/crm/customers", async (HttpContext ctx, Customer c, BusinessStore store) =>
{
    var (user, _) = GetCurrentUser(ctx);
    if (string.IsNullOrEmpty(c.Owner)) c.Owner = user;
    var id = await store.CreateCustomerAsync(c);
    return Results.Created($"/api/crm/customers/{id}", new { id });
}).WithTags("CRM客户");

app.MapPut("/api/crm/customers/{id}", async (int id, Customer c, BusinessStore store) =>
{
    c.Id = id;
    var ok = await store.UpdateCustomerAsync(c);
    return ok ? Results.Ok() : Results.NotFound();
}).WithTags("CRM客户");

app.MapDelete("/api/crm/customers/{id}", async (int id, HttpContext ctx, BusinessStore store, JwtService jwt) =>
{
    var (user, role) = jwt.ParseUser(ctx.Request.Headers.Authorization);
    if (role != "Admin") return Results.Forbid();
    var ok = await store.DeleteCustomerAsync(id, user);
    return ok ? Results.Ok() : Results.NotFound();
}).WithTags("CRM客户");

// 跟进记录
app.MapGet("/api/crm/customers/{id}/followups", async (int id, BusinessStore store) =>
    Results.Ok(await store.ListFollowUpsAsync(id))).WithTags("CRM跟进");

app.MapPost("/api/crm/customers/{id}/followups", async (int id, HttpContext ctx, FollowUp f, BusinessStore store) =>
{
    var (user, _) = GetCurrentUser(ctx);
    f.CustomerId = id;
    if (string.IsNullOrEmpty(f.Operator)) f.Operator = user;
    var fid = await store.AddFollowUpAsync(f);
    return Results.Created($"/api/crm/followups/{fid}", new { id = fid });
}).WithTags("CRM跟进");

// ========== 15. 工单系统 ==========
app.MapGet("/api/tickets", async (TicketStatus? status, BusinessStore store) =>
    Results.Ok(await store.ListTicketsAsync(status))).WithTags("工单");

app.MapPost("/api/tickets", async (HttpContext ctx, Ticket t, BusinessStore store) =>
{
    var (user, _) = GetCurrentUser(ctx);
    t.CreatedBy = user;
    var id = await store.CreateTicketAsync(t);
    return Results.Created($"/api/tickets/{id}", new { id });
}).WithTags("工单");

app.MapPut("/api/tickets/{id}/status", async (int id, TicketStatus status, HttpContext ctx, BusinessStore store, JwtService jwt) =>
{
    var (user, role) = jwt.ParseUser(ctx.Request.Headers.Authorization);
    if (role != "Admin") return Results.Forbid();
    var ok = await store.UpdateTicketStatusAsync(id, status, user);
    return ok ? Results.Ok() : Results.NotFound();
}).WithTags("工单");

// ========== 16. 人审待办 ==========
app.MapGet("/api/approvals", async (ApprovalStatus? status, BusinessStore store) =>
    Results.Ok(await store.ListApprovalsAsync(status))).WithTags("人审");

app.MapGet("/api/approvals/{id}", async (int id, BusinessStore store) =>
{
    var a = await store.GetApprovalAsync(id);
    return a == null ? Results.NotFound() : Results.Ok(a);
}).WithTags("人审");

// 人审决策：前端回传 → 释放 Agent 等待（需 Admin 角色，手动校验 JWT）
app.MapPost("/api/approvals/decide", async (ApprovalDecisionRequest req, HttpContext ctx, ApprovalCoordinator coord, JwtService jwt) =>
{
    var (user, role) = jwt.ParseUser(ctx.Request.Headers.Authorization);
    if (role != "Admin") return Results.Forbid();
    var status = req.Decision.ToLowerInvariant() switch
    {
        "approved" => ApprovalStatus.Approved,
        "rejected" => ApprovalStatus.Rejected,
        "modified" => ApprovalStatus.Modified,
        _ => ApprovalStatus.Approved
    };
    var comment = req.Comment;
    if (status == ApprovalStatus.Modified && !string.IsNullOrEmpty(req.ModifiedParameters))
        comment = $"修改后参数：{req.ModifiedParameters}";

    var ok = await coord.ResolveAsync(req.ApprovalId, status, user, comment, req.ModifiedParameters);
    return ok ? Results.Ok(new { resolved = true }) : Results.NotFound();
}).WithTags("人审");

// ========== 17. 审计日志 ==========
app.MapGet("/api/audit", async (int? limit, HttpContext ctx, BusinessStore store, JwtService jwt) =>
{
    var (_, role) = jwt.ParseUser(ctx.Request.Headers.Authorization);
    if (role != "Admin") return Results.Forbid();
    return Results.Ok(await store.ListAuditLogsAsync(limit ?? 100));
}).WithTags("审计");

// ========== 18. 仪表盘统计 ==========
app.MapGet("/api/dashboard", async (BusinessStore store) =>
    Results.Ok(await store.GetStatsAsync())).WithTags("仪表盘");

// ========== 19. SSE 流式聊天（7 模式可切换） ==========
app.MapPost("/api/chat", async (
    ChatRequest req,
    SequentialStrategy seq, ConcurrentStrategy con, HandoffStrategy hof,
    GroupChatStrategy grp, MagenticStrategy mag, CrmStrategy crm, RagStrategy rag,
    ConversationStore store, ApprovalCoordinator approvals,
    HttpContext context) =>
{
    var sessionId = req.SessionId ?? Guid.NewGuid().ToString("N");
    await store.EnsureConversationAsync(sessionId);

    var history = await store.GetHistoryAsync(sessionId);
    await store.AddMessageAsync(sessionId, "user", req.Message);

    var strategy = ResolveStrategy(req.OrchestrationMode, seq, con, hof, grp, mag, crm, rag);

    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    var ct = context.RequestAborted;

    async Task SendEventAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        await context.Response.WriteAsync($"data: {json}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    await SendEventAsync(new { type = "session", sessionId });
    await SendEventAsync(new { type = "orchestration_start", mode = strategy.Mode.ToString().ToLowerInvariant() });

    // Channel 缓冲编排事件（线程安全）
    var channel = Channel.CreateUnbounded<OrchestrationEvent>();
    Action<OrchestrationEvent> onEvent = e => channel.Writer.TryWrite(e);

    // 人审协调器：按 sessionId 注册本会话专属的 SSE 回调（多用户并发隔离）
    // 替代原先的可变实例字段，A 用户的审核不会再串到 B 的 SSE 流
    approvals.RegisterSession(sessionId, async (ar) =>
    {
        onEvent(new OrchestrationEvent(OrchestrationEventType.ApprovalRequired,
            Agent: ar.Agent, ApprovalId: ar.Id, ApprovalAction: ar.Action,
            ApprovalParams: ar.Parameters, Reason: ar.RiskLevel));
        // 注意：此处不阻塞，Agent 在 WaitForDecisionAsync 中阻塞等待
    });

    // 后台执行编排
    var runTask = Task.Run(async () =>
    {
        // 设置当前异步流的 sessionId（AsyncLocal 自动传播到工具调用链，工具层据此隔离会话）
        ApprovalCoordinator.CurrentSessionId = sessionId;
        try { return await strategy.ExecuteAsync(req.Message, history, onEvent, ct); }
        finally { channel.Writer.Complete(); }
    }, ct);

    // 持续读取 Channel → 推送 orchestration_event
    try
    {
        await foreach (var e in channel.Reader.ReadAllAsync(ct))
        {
            await SendEventAsync(new
            {
                type = "orchestration_event",
                eventType = ToSnakeCase(e.Type.ToString()),
                agent = e.Agent,
                status = e.Status,
                output = e.Output,
                round = e.Round,
                fromAgent = e.FromAgent,
                toAgent = e.ToAgent,
                reason = e.Reason,
                parallelLane = e.ParallelLane,
                toolName = e.ToolName,
                toolArgs = e.ToolArgs,
                toolResult = e.ToolResult,
                approvalId = e.ApprovalId,
                approvalAction = e.ApprovalAction,
                approvalParams = e.ApprovalParams,
                approvalDecision = e.ApprovalDecision
            });
        }
    }
    catch (OperationCanceledException)
    {
        // 用户主动中止：取消本会话所有人审等待，防止 Agent Task 泄露
        approvals.CancelPendingForSession(sessionId);
        approvals.UnregisterSession(sessionId);
        return;
    }

    string finalContent;
    try { finalContent = await runTask; }
    catch (OperationCanceledException)
    {
        // runTask 因 ct 取消（用户中止）
        approvals.CancelPendingForSession(sessionId);
        approvals.UnregisterSession(sessionId);
        return;
    }
    catch (Exception ex)
    {
        await SendEventAsync(new { type = "error", message = $"编排执行失败：{ex.Message}" });
        return;
    }

    // 终稿分块推送（打字机）
    try
    {
        for (int i = 0; i < finalContent.Length; i += 2)
        {
            var len = Math.Min(2, finalContent.Length - i);
            await SendEventAsync(new { type = "token", content = finalContent.Substring(i, len) });
        }
        await SendEventAsync(new { type = "done", content = finalContent });
        await store.AddMessageAsync(sessionId, "assistant", finalContent);
    }
    catch (OperationCanceledException) { /* 推送阶段被中止，忽略 */ }

    // 会话结束：注销 SSE 回调，释放协调器中的会话上下文（防止回调字典无限增长）
    approvals.UnregisterSession(sessionId);
})
.WithTags("聊天");

// 获取会话历史消息（前端刷新/重连时恢复聊天记录）
app.MapGet("/api/chat/sessions/{sessionId}/history", async (string sessionId, ConversationStore store) =>
    Results.Ok(await store.GetHistoryAsync(sessionId)))
    .WithTags("聊天");

// ========== 20. RAG 知识库 API ==========
// 知识库 CRUD
app.MapGet("/api/kb/databases", async (KnowledgeStore store) =>
    Results.Ok(await store.ListDatabasesAsync())).WithTags("RAG知识库");

app.MapPost("/api/kb/databases", async (KbCreateRequest req, KnowledgeStore store) =>
{
    var id = await store.CreateDatabaseAsync(req.Name, req.Description ?? "");
    var kb = await store.GetDatabaseAsync(id);
    return Results.Created($"/api/kb/databases/{id}", kb);
}).WithTags("RAG知识库");

app.MapDelete("/api/kb/databases/{id}", async (int id, KnowledgeStore store, VectorStore vectors) =>
{
    // 删除知识库时同步清理内存向量
    vectors.RemoveByDatabase(id);
    var ok = await store.DeleteDatabaseAsync(id);
    return ok ? Results.Ok() : Results.NotFound();
}).WithTags("RAG知识库");

// 文档管理
app.MapGet("/api/kb/databases/{id}/documents", async (int id, KnowledgeStore store) =>
    Results.Ok(await store.ListDocumentsAsync(id))).WithTags("RAG文档");

app.MapDelete("/api/kb/documents/{id}", async (int id, KnowledgeStore store, VectorStore vectors) =>
{
    vectors.RemoveByDocument(id);
    var ok = await store.DeleteDocumentAsync(id);
    return ok ? Results.Ok() : Results.NotFound();
}).WithTags("RAG文档");

// 上传文档 - 异步处理（解析+嵌入+存入 VectorStore）
// 上传后立即返回 202 Accepted，后台 Task.Run 处理
app.MapPost("/api/kb/databases/{id}/upload", async (
    int id, HttpRequest request, KnowledgeStore store, HybridRetriever retriever,
    RateLimiter rateLimiter, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("RAG.上传");
    if (!request.HasFormContentType) return Results.BadRequest("需要 multipart/form-data");

    // 速率限制：每 IP 每分钟 100 次
    var ip = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!rateLimiter.TryAcquire(ip))
        return Results.Json(new { error = true, message = "上传过于频繁（每分钟最多100次），请稍后再试" }, statusCode: 429);
    var form = await request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0) return Results.BadRequest("未提供文件");

    // 先创建文档记录（Pending 状态）
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    var doc = new KnowledgeDocument
    {
        DatabaseId = id,
        FileName = file.FileName,
        FileSize = file.Length,
        FileType = ext,
        Status = DocumentStatus.Pending
    };
    var docId = await store.CreateDocumentAsync(doc);

    // 把文件读到内存（演示量级可接受；大文件应换流式处理或临时文件）
    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var bytes = ms.ToArray();

    // 更新为 Processing
    await store.UpdateDocumentStatusAsync(docId, DocumentStatus.Processing, 0);

    // 后台异步处理：解析 → 分片 → 嵌入 → 存 SQLite + VectorStore
    _ = Task.Run(async () =>
    {
        try
        {
            await retriever.IngestDocumentAsync(docId, file.FileName, bytes, id);
        }
        catch (Exception ex)
        {
            // 失败标记为 Failed，不抛异常（避免后台未捕获）
            await store.UpdateDocumentStatusAsync(docId, DocumentStatus.Failed, 0, ex.Message);
            logger.LogError(ex, "文档 {DocId}（{FileName}）处理失败", docId, file.FileName);
        }
    });

    return Results.Accepted($"/api/kb/documents/{docId}", new { id = docId, status = "processing", message = "文档已上传，正在后台解析与嵌入" });
}).WithTags("RAG文档").DisableAntiforgery();

// 重新解析文档
app.MapPost("/api/kb/documents/{id}/reparse", async (int id, KnowledgeStore store, HybridRetriever retriever, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("RAG.重解析");
    var doc = await store.GetDocumentAsync(id);
    if (doc == null) return Results.NotFound();

    // 重新解析需要原始文件 bytes，但本实现未持久化原始文件
    // 简化：仅基于已存储分片重新加载向量到内存（VectorStore 清空后恢复用）
    // 真实场景应保存原始文件或允许重新上传
    var chunks = await store.ListChunksByDocumentAsync(id);
    if (chunks.Count == 0) return Results.BadRequest("无原始分片可重新嵌入");

    await store.UpdateDocumentStatusAsync(id, DocumentStatus.Processing, chunks.Count);
    _ = Task.Run(async () =>
    {
        try
        {
            // 基于已存储的 embedding 重新加载到 VectorStore
            await retriever.EnsureLoadedAsync(doc.DatabaseId);
            await store.UpdateDocumentStatusAsync(id, DocumentStatus.Ready, chunks.Count);
        }
        catch (Exception ex)
        {
            await store.UpdateDocumentStatusAsync(id, DocumentStatus.Failed, chunks.Count, ex.Message);
            logger.LogError(ex, "文档 {DocId} 重新嵌入失败", id);
        }
    });
    return Results.Accepted($"/api/kb/documents/{id}", new { id, status = "reprocessing" });
}).WithTags("RAG文档");

// 检索测试
app.MapPost("/api/kb/search", async (SearchTestRequest req, HybridRetriever retriever) =>
{
    var resp = await retriever.SearchAsync(req.Query, req.DatabaseId, req.TopK, req.Rerank);
    return Results.Ok(resp);
}).WithTags("RAG检索");

// RAG 评测
app.MapPost("/api/kb/eval", async (EvalTestRequest req, HybridRetriever retriever, IChatClient chatClient) =>
{
    var details = new List<EvalCaseResult>();
    int recalled = 0;
    int correct = 0;

    foreach (var tc in req.TestCases)
    {
        var searchResp = await retriever.SearchAsync(tc.Question, req.DatabaseId, topK: 3, enableRerank: true);
        bool isRetrieved = searchResp.Results.Count > 0;
        if (isRetrieved) recalled++;

        // 用检索结果让 LLM 生成回答
        string actualAnswer;
        try
        {
            var context = string.Join("\n---\n", searchResp.Results.Select(r => r.Content));
            var prompt = $"基于以下检索内容回答问题。\n\n检索内容：\n{context}\n\n问题：{tc.Question}\n\n回答：";
            var msgs = new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.User, prompt)
            };
            var llmResp = await chatClient.GetResponseAsync(msgs);
            actualAnswer = llmResp.Text?.Trim() ?? "(无回答)";
        }
        catch
        {
            actualAnswer = "(LLM 调用失败)";
        }

        // 简化匹配：actualAnswer 包含 ExpectedAnswer 的关键词即视为正确
        bool isCorrect = !string.IsNullOrEmpty(tc.ExpectedAnswer)
            && actualAnswer.Contains(tc.ExpectedAnswer, StringComparison.OrdinalIgnoreCase);
        if (isCorrect) correct++;

        details.Add(new EvalCaseResult(tc.Question, tc.ExpectedAnswer, actualAnswer, isRetrieved, isCorrect));
    }

    var total = req.TestCases.Count;
    var result = new EvalResult(
        TotalCases: total,
        RecallRate: total > 0 ? (double)recalled / total : 0,
        AccuracyRate: total > 0 ? (double)correct / total : 0,
        Details: details);
    return Results.Ok(result);
}).WithTags("RAG评测");

// 知识库统计
app.MapGet("/api/kb/stats", async (KnowledgeStore store, VectorStore vectors, EmbeddingService emb) =>
{
    var stats = await store.GetRagStatsAsync();
    return Results.Ok(new
    {
        tables = stats,
        vectorCount = vectors.Count,
        embeddingProvider = emb.Provider,
        embeddingFallback = emb.IsFallback,
        embeddingDimension = emb.Dimension
    });
}).WithTags("RAG知识库");

// 长期记忆检索（手动测试用）
app.MapGet("/api/kb/memory/{sessionId}", async (string sessionId, MemoryStore memory) =>
{
    var messages = await memory.GetRecentMessagesAsync(sessionId, 100);
    var profiles = await memory.GetProfilesAsync(sessionId);
    var summary = await memory.GetSummaryAsync(sessionId);
    return Results.Ok(new { messages, profiles, summary });
}).WithTags("RAG记忆");

// ========== 21. 启动 ==========
try
{
    app.Run("http://0.0.0.0:5000");
}
finally
{
    Log.CloseAndFlush();
}
