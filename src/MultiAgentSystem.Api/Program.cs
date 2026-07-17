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
using MultiAgentSystem.Api.Endpoints;
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

// ========== 3. 数据库工厂（配置驱动：appsettings.json → Database:Provider） ==========
var dbCfg = builder.Configuration.GetSection("Database").Get<MultiAgentSystem.Api.Data.DatabaseConfig>() ?? new();
builder.Services.AddSingleton<MultiAgentSystem.Api.Data.IDbConnectionFactory>(_ =>
    dbCfg.Provider == "pgsql"
        ? new MultiAgentSystem.Api.Data.NpgsqlConnectionFactory(dbCfg.ConnectionString)
        : new MultiAgentSystem.Api.Data.SqliteConnectionFactory("multiagent.db"));

// ========== 4. 业务存储 ==========
builder.Services.AddSingleton<BusinessStore>();
builder.Services.AddSingleton<KnowledgeStore>();
builder.Services.AddSingleton<TestCaseStore>();
builder.Services.AddSingleton<EvalReportStore>();

// ConversationStore 暂时仍用直接字符串
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
builder.Services.AddSingleton<EmailAdapter>();
builder.Services.AddSingleton<ApprovalCoordinator>();
builder.Services.AddSingleton<CrmTools>();
// EmailService：集成 Demo —— CRM 跟进 → 邮件生成 → 发送
builder.Services.AddSingleton<EmailService>();

// ========== 8a. MVP-5：评测服务 ==========
builder.Services.AddSingleton<MetricCalculator>();
builder.Services.AddSingleton<JudgeService>();
builder.Services.AddSingleton<EvalService>();

// ========== 8a. MVP-3：RAG 知识库 ==========
// EmbeddingService 调 DeepSeek embedding API，失败降级为哈希向量
builder.Services.AddSingleton<EmbeddingService>();
// VectorStore：优先 Qdrant，不可用时降级为内存向量（无需 Docker 依赖）
var qdrantUrl = builder.Configuration.GetValue<string>("Qdrant:Url");
if (!string.IsNullOrEmpty(qdrantUrl))
{
    builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();
}
else
{
    builder.Services.AddSingleton<IVectorStore, InMemoryVectorStore>();
}
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

// ========== 12. 端点注册（按模块拆分） ==========
app.MapSystemEndpoints(tavilyActive);
app.MapAuthEndpoints();
app.MapCrmEndpoints();
app.MapChatEndpoints();
app.MapKbEndpoints();
app.MapEvalEndpoints();

// ========== 21. 启动 ==========
try
{
    app.Run("http://0.0.0.0:5000");
}
finally
{
    Log.CloseAndFlush();
}
