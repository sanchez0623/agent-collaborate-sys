using System.Data.Common;
// ============================================================
// TestCaseStore - 评测用例仓储（SQLite）
// 预置 30 个标准用例覆盖 6 大场景
// ============================================================

using System.Text.Json;
using MultiAgentSystem.Api.Data;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using MultiAgentSystem.Api.Data;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class TestCaseStore
{
    private readonly IDbConnectionFactory _db;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TestCaseStore(IDbConnectionFactory db)
    {
        _db = db;
        InitAsync().GetAwaiter().GetResult();
    }

    private async Task InitAsync()
    {
        await WithLockAsync(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS eval_testcases (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL, category TEXT NOT NULL, tags TEXT,
                    question TEXT NOT NULL, expected_keypoints TEXT,
                    applicable_modes TEXT, requires_kb INTEGER DEFAULT 0,
                    kb_id INTEGER, expected_tool_calls TEXT,
                    weights TEXT, is_preset INTEGER DEFAULT 1,
                    created_at TEXT DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS idx_eval_cases_cat ON eval_testcases(category);
                """;
            await cmd.ExecuteNonQueryAsync();
        });

        await WithLockAsync(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM eval_testcases WHERE is_preset=1;";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
                foreach (var tc in PresetCases)
                    await InsertInternalAsync(conn, tc);
        });
    }

    public async Task<List<EvalTestCase>> GetAllAsync(string? category = null)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(category)
                ? "SELECT * FROM eval_testcases ORDER BY category, id;"
                : "SELECT * FROM eval_testcases WHERE category=@cat ORDER BY id;";
            if (!string.IsNullOrWhiteSpace(category)) cmd.AddParam("@cat", category);
            return await ReadCasesAsync(cmd);
        });
    }

    public async Task<EvalTestCase?> GetAsync(int id)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM eval_testcases WHERE id=@id;";
            cmd.AddParam("@id", id);
            return (await ReadCasesAsync(cmd)).FirstOrDefault();
        });
    }

    public async Task<int> AddAsync(EvalTestCase tc)
    {
        tc.IsPreset = false;
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            return await InsertInternalAsync(conn, tc);
        });
    }

    public async Task<bool> DeleteAsync(int id)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM eval_testcases WHERE id=@id AND is_preset=0;";
            cmd.AddParam("@id", id);
            return await cmd.ExecuteNonQueryAsync() > 0;
        });
    }

    public async Task<Dictionary<string, List<string>>> GetCaseSetsAsync()
    {
        return await WithLock(async () =>
        {
            var sets = new Dictionary<string, List<string>>
            {
                ["full"] = new(), ["quick-smoke"] = new(), ["rag"] = new(),
                ["tool"] = new(), ["crm"] = new()
            };
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,title,category,tags FROM eval_testcases ORDER BY id;";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var title = r.GetString(1);
                var cat = r.GetString(2);
                var tags = r.GetString(3);
                sets["full"].Add(title);
                if (cat is "RAG") sets["rag"].Add(title);
                if (cat is "工具调用") sets["tool"].Add(title);
                if (cat is "CRM" or "人审") sets["crm"].Add(title);
                if (tags.Contains("快速")) sets["quick-smoke"].Add(title);
            }
            return sets;
        });
    }

    // ---------- 内部 ----------

    private async Task<int> InsertInternalAsync(DbConnection conn, EvalTestCase tc)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO eval_testcases
            (title,category,tags,question,expected_keypoints,applicable_modes,
             requires_kb,kb_id,expected_tool_calls,weights,is_preset)
            VALUES (@t,@c,@tag,@q,@ek,@am,@rk,@ki,@etc,@w,@ip);
            SELECT last_insert_rowid();
            """;
        cmd.AddParam("@t", tc.Title);
        cmd.AddParam("@c", tc.Category);
        cmd.AddParam("@tag", tc.Tags ?? "");
        cmd.AddParam("@q", tc.Question);
        cmd.AddParam("@ek", tc.ExpectedKeyPoints ?? "");
        cmd.AddParam("@am", tc.ApplicableModes ?? "");
        cmd.AddParam("@rk", tc.RequiresKnowledgeBase ? 1 : 0);
        cmd.AddParam("@ki", tc.KnowledgeBaseId as object ?? DBNull.Value);
        cmd.AddParam("@etc", tc.ExpectedToolCalls ?? "");
        cmd.AddParam("@w", JsonSerializer.Serialize(tc.Weights));
        cmd.AddParam("@ip", tc.IsPreset ? 1 : 0);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()!);
    }

    private static async Task<List<EvalTestCase>> ReadCasesAsync(DbCommand cmd)
    {
        var list = new List<EvalTestCase>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            EvalWeights? w = null;
            try { w = JsonSerializer.Deserialize<EvalWeights>(r.GetString(10)); } catch { }
            list.Add(new EvalTestCase
            {
                Id = r.GetInt32(0), Title = r.GetString(1), Category = r.GetString(2),
                Tags = r.GetString(3), Question = r.GetString(4),
                ExpectedKeyPoints = r.GetString(5), ApplicableModes = r.GetString(6),
                RequiresKnowledgeBase = r.GetInt32(7) == 1,
                KnowledgeBaseId = r.IsDBNull(8) ? null : r.GetInt32(8),
                ExpectedToolCalls = r.GetString(9), Weights = w ?? new(),
                IsPreset = r.GetInt32(11) == 1, CreatedAt = r.GetDateTime(12)
            });
        }
        return list;
    }

    private async Task<T> WithLock<T>(Func<Task<T>> action)
    {
        await _lock.WaitAsync();
        try { return await action(); }
        finally { _lock.Release(); }
    }

    private async Task WithLockAsync(Func<Task> action)
    {
        await _lock.WaitAsync();
        try { await action(); }
        finally { _lock.Release(); }
    }

    // ========== 30 个预置测试用例 ==========

    private static readonly EvalTestCase[] PresetCases =
    [
        // ===== 通用问答 (5) =====
        new() { Title="AI概念解释", Category="通用问答", Tags="基础,快速",
            Question="什么是人工智能中的Transformer架构？",
            ExpectedKeyPoints="自注意力机制\n编码器-解码器\n并行计算\nBERT/GPT应用",
            ApplicableModes="Sequential,Concurrent,Handoff,GroupChat,Magentic" },
        new() { Title="数学计算", Category="通用问答", Tags="基础,快速",
            Question="计算 256 * 128 + 512 / 16 的结果",
            ExpectedKeyPoints="32768 + 32\n结果 32800",
            ApplicableModes="Sequential,Concurrent" },
        new() { Title="编程问题", Category="通用问答", Tags="代码,快速",
            Question="用Python写一个函数判断字符串是否为回文",
            ExpectedKeyPoints="定义回文\n实现算法\n时间复杂度分析",
            ApplicableModes="Sequential,Concurrent,Handoff,GroupChat,Magentic" },
        new() { Title="历史事件", Category="通用问答", Tags="知识",
            Question="简述中国改革开放的标志性事件",
            ExpectedKeyPoints="十一届三中全会\n家庭联产承包\n特区设立\n南方谈话",
            ApplicableModes="Sequential,Concurrent,Handoff" },
        new() { Title="逻辑推理", Category="通用问答", Tags="推理",
            Question="有三个盒子,一个金一个银一个空。标签全贴错了,你最少开几个盒子确定哪个是金的？",
            ExpectedKeyPoints="最少开1个\n利用标签全错信息\n逻辑推理过程",
            ApplicableModes="Sequential,GroupChat" },
        // ===== 多Agent协作 (5) =====
        new() { Title="研究报告任务", Category="多Agent协作", Tags="研究,写作,审核",
            Question="写一篇300字短文介绍量子计算的最新进展",
            ExpectedKeyPoints="量子比特\n量子霸权\n应用领域\n中国进展\n300字",
            ApplicableModes="Sequential,GroupChat,Magentic" },
        new() { Title="技术方案评估", Category="多Agent协作", Tags="研究,分析",
            Question="Kubernetes和Docker Swarm,哪个更适合中小企业的容器化部署？请给出分析",
            ExpectedKeyPoints="对比分析\n成本\n学习曲线\n社区生态\n推荐结论",
            ApplicableModes="Sequential,Handoff,GroupChat,Magentic" },
        new() { Title="代码审查", Category="多Agent协作", Tags="代码,审核",
            Question="审查以下代码: function add(a,b){ return a-b }",
            ExpectedKeyPoints="函数名与实现不符\n参数校验缺失\n命名规范\n类型安全",
            ApplicableModes="Sequential,Handoff" },
        new() { Title="方案策划", Category="多Agent协作", Tags="创意,写作",
            Question="设计一个小型SaaS产品的Go-to-Market策略",
            ExpectedKeyPoints="目标用户\n定价策略\n渠道选择\nMVP\n获客成本",
            ApplicableModes="Sequential,GroupChat,Magentic" },
        // ===== RAG (5) =====
        new() { Title="知识库事实查询", Category="RAG", Tags="知识库,检索",
            Question="根据知识库内容,DeepSeek V4的核心技术创新有哪些？",
            ExpectedKeyPoints="基于知识库内容\nMoE架构\n参数规模\n性能对比",
            ApplicableModes="Sequential", RequiresKnowledgeBase=true },
        new() { Title="文档要点提取", Category="RAG", Tags="知识库,摘要",
            Question="总结知识库中关于微服务架构优势的文档要点",
            ExpectedKeyPoints="独立部署\n技术栈灵活\n故障隔离\n团队自治",
            ApplicableModes="Sequential", RequiresKnowledgeBase=true },
        new() { Title="跨文档关联", Category="RAG", Tags="知识库,推理",
            Question="知识库中AI安全相关的文档,它们的共同建议是什么？",
            ExpectedKeyPoints="多文档检索\n交叉验证\n共同主题提取",
            ApplicableModes="Sequential", RequiresKnowledgeBase=true },
        new() { Title="精确数据检索", Category="RAG", Tags="知识库,数值",
            Question="知识库中2025年的预算数据是多少？请引用出处",
            ExpectedKeyPoints="精确数值\n引用来源\n上下文窗口",
            ApplicableModes="Sequential", RequiresKnowledgeBase=true },
        new() { Title="知识库+推理", Category="RAG", Tags="知识库,推理",
            Question="根据知识库中Python和Go的性能文档,分析各自适合的微服务场景",
            ExpectedKeyPoints="检索→分析→对比\n语言特性\n生态\n性能场景",
            ApplicableModes="Sequential", RequiresKnowledgeBase=true },
        // ===== CRM (5) =====
        new() { Title="客户列表查询", Category="CRM", Tags="CRM,查询,工具",
            Question="帮我查询所有战略级别的客户",
            ExpectedKeyPoints="调用CRM工具\n按等级过滤\n返回客户列表",
            ApplicableModes="Crm", ExpectedToolCalls="[\"search_customers\"]" },
        new() { Title="新增客户记录", Category="CRM", Tags="CRM,创建,工具",
            Question="帮我添加一个新客户：张三,腾讯科技,电话13800138000,等级重要",
            ExpectedKeyPoints="调用create_customer\n参数正确\n返回新客户ID",
            ApplicableModes="Crm", ExpectedToolCalls="[\"create_customer\"]" },
        new() { Title="客户详情+跟进", Category="CRM", Tags="CRM,查询,跟进",
            Question="查看客户ID为1的详细信息,并记录下来电话沟通了合作事宜",
            ExpectedKeyPoints="get_customer_detail\nadd_followup\n两次工具调用",
            ApplicableModes="Crm", ExpectedToolCalls="[\"get_customer_detail\",\"add_followup\"]" },
        new() { Title="敏感操作删除", Category="CRM", Tags="CRM,删除,人审",
            Question="删除客户ID999,原因是重复录入",
            ExpectedKeyPoints="触发delete_customer\n创建审批请求\n等待人审",
            ApplicableModes="Crm", ExpectedToolCalls="[\"delete_customer\"]" },
        new() { Title="CRM数据统计建议", Category="CRM", Tags="CRM,分析",
            Question="分析现有客户的等级分布,给出合理的客户分级管理建议",
            ExpectedKeyPoints="调用search_customers\n分析等级分布\n给出管理建议",
            ApplicableModes="Crm", ExpectedToolCalls="[\"search_customers\"]" },
        // ===== 工具调用 (5) =====
        new() { Title="搜索+分析", Category="工具调用", Tags="搜索,Tavily,分析",
            Question="搜索2024年人工智能领域最重要的三个突破,并简要分析其影响",
            ExpectedKeyPoints="调用搜索工具\n列出突破\n分析影响",
            ApplicableModes="Sequential,Handoff,GroupChat" },
        new() { Title="知识库+搜索混合", Category="工具调用", Tags="知识库,搜索,混合",
            Question="对比知识库中关于Docker的信息和网上最新关于Podman的信息",
            ExpectedKeyPoints="知识库检索\n网络搜索\n对比分析",
            ApplicableModes="Sequential,Handoff", RequiresKnowledgeBase=true },
        new() { Title="多工具串联", Category="工具调用", Tags="CRM,搜索,串联",
            Question="搜索最新AI芯片新闻,然后帮我创建一个新客户NVIDIA,备注为AI芯片",
            ExpectedKeyPoints="Tavily搜索\ncreate_customer\n工具串联",
            ApplicableModes="Crm", ExpectedToolCalls="[\"create_customer\"]" },
        new() { Title="工具调用链验证", Category="工具调用", Tags="工具,CRM",
            Question="用search_customers查'腾讯',然后用get_customer_detail看第一个结果的详情",
            ExpectedKeyPoints="两次工具调用\n参数传递\n结果正确",
            ApplicableModes="Crm", ExpectedToolCalls="[\"search_customers\",\"get_customer_detail\"]" },
        new() { Title="工具容错处理", Category="工具调用", Tags="容错",
            Question="查询一个不存在的客户ID 99999999的详情",
            ExpectedKeyPoints="调用工具\n处理错误\n友好提示",
            ApplicableModes="Crm", ExpectedToolCalls="[\"get_customer_detail\"]" },
        // ===== 人审 (5) =====
        new() { Title="高风险操作触发人审", Category="人审", Tags="CRM,删除,人审,快速",
            Question="删除客户ID1,原因是测试数据需要清理",
            ExpectedKeyPoints="触发人审流程\n等待审批\n执行结果",
            ApplicableModes="Crm", ExpectedToolCalls="[\"delete_customer\"]" },
        new() { Title="连续删除测试", Category="人审", Tags="人审",
            Question="连续删除3个测试客户ID 10,11,12",
            ExpectedKeyPoints="串行审批\n每次人审\n状态流转",
            ApplicableModes="Crm", ExpectedToolCalls="[\"delete_customer\"]" },
        new() { Title="审批拒绝场景", Category="人审", Tags="人审,拒绝",
            Question="删除一个不应该被删的客户ID1",
            ExpectedKeyPoints="人审拒绝\n操作不执行\n原因记录",
            ApplicableModes="Crm", ExpectedToolCalls="[\"delete_customer\"]" },
        new() { Title="批量添加跟进", Category="人审", Tags="人审,批量",
            Question="给客户ID为1和2都添加跟进记录:电话沟通项目进度",
            ExpectedKeyPoints="批量add_followup\n无敏感操作\n正常执行",
            ApplicableModes="Crm", ExpectedToolCalls="[\"add_followup\"]" },
        new() { Title="审批完整链路", Category="人审", Tags="人审,完整链路",
            Question="删除客户ID3,然后查询客户列表确认已删除",
            ExpectedKeyPoints="delete_customer→人审→通过→search_customers\n完整审计链路",
            ApplicableModes="Crm", ExpectedToolCalls="[\"delete_customer\",\"search_customers\"]" },
    ];
}
