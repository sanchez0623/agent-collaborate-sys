// ============================================================
// TestCaseStore - 评测用例仓储（EF Core）
// ============================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MultiAgentSystem.Api.Data;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class TestCaseStore
{
    private readonly MultiAgentDbContext _db;

    public TestCaseStore(MultiAgentDbContext db)
    {
        _db = db;
        _db.Database.EnsureCreated();
        SeedPresetAsync().GetAwaiter().GetResult();
    }

    private async Task SeedPresetAsync()
    {
        if (!await _db.EvalTestCases.AnyAsync())
            _db.EvalTestCases.AddRange(PresetCases);
        await _db.SaveChangesAsync();
    }

    public async Task<List<EvalTestCase>> GetAllAsync(string? category = null)
        => string.IsNullOrWhiteSpace(category)
            ? await _db.EvalTestCases.OrderBy(x => x.Category).ThenBy(x => x.Id).ToListAsync()
            : await _db.EvalTestCases.Where(x => x.Category == category).OrderBy(x => x.Id).ToListAsync();

    public async Task<EvalTestCase?> GetAsync(int id)
        => await _db.EvalTestCases.FindAsync(id);

    public async Task<int> AddAsync(EvalTestCase tc)
    {
        tc.IsPreset = false;
        _db.EvalTestCases.Add(tc);
        await _db.SaveChangesAsync();
        return tc.Id;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var tc = await _db.EvalTestCases.FindAsync(id);
        if (tc == null || tc.IsPreset) return false;
        _db.EvalTestCases.Remove(tc);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<Dictionary<string, List<string>>> GetCaseSetsAsync()
    {
        var cases = await _db.EvalTestCases.ToListAsync();
        return new Dictionary<string, List<string>>
        {
            ["full"] = cases.Select(c => c.Title).ToList(),
            ["quick-smoke"] = cases.Where(c => c.Tags.Contains("快速")).Select(c => c.Title).ToList(),
            ["rag"] = cases.Where(c => c.Category == "RAG").Select(c => c.Title).ToList(),
            ["tool"] = cases.Where(c => c.Category == "工具调用").Select(c => c.Title).ToList(),
            ["crm"] = cases.Where(c => c.Category is "CRM" or "人审").Select(c => c.Title).ToList(),
        };
    }

    // ========== 30 预置用例 ==========

    private static readonly EvalTestCase[] PresetCases =
    [
        new() { Title="AI概念解释", Category="通用问答", Tags="基础,快速", Question="什么是AI Transformer架构？", ExpectedKeyPoints="自注意力\n编码器-解码器", ApplicableModes="Sequential,Concurrent,Handoff,GroupChat,Magentic" },
        new() { Title="数学计算", Category="通用问答", Tags="基础,快速", Question="计算 256 * 128 + 512 / 16", ExpectedKeyPoints="32768 + 32\n结果 32800", ApplicableModes="Sequential,Concurrent" },
        new() { Title="编程问题", Category="通用问答", Tags="代码,快速", Question="用Python写一个函数判断回文", ExpectedKeyPoints="回文定义\n算法\n复杂度", ApplicableModes="Sequential,Concurrent,Handoff,GroupChat,Magentic" },
        new() { Title="历史事件", Category="通用问答", Tags="知识", Question="简述中国改革开放标志性事件", ExpectedKeyPoints="十一届三中全会\n特区\n南方谈话", ApplicableModes="Sequential,Concurrent,Handoff" },
        new() { Title="逻辑推理", Category="通用问答", Tags="推理", Question="三个盒子,标签全贴错,最少开几个确定哪个是金的？", ExpectedKeyPoints="最少1个\n逻辑推理", ApplicableModes="Sequential,GroupChat" },
        new() { Title="研究报告任务", Category="多Agent协作", Tags="研究,写作,审核", Question="写300字短文介绍量子计算最新进展", ExpectedKeyPoints="量子比特\n超导\n应用\n中国进展\n300字", ApplicableModes="Sequential,GroupChat,Magentic" },
        new() { Title="技术方案评估", Category="多Agent协作", Tags="研究,分析", Question="Kubernetes和Docker Swarm,哪个更适合中小企业容器化部署？", ExpectedKeyPoints="对比\n成本\n学习曲线\n社区\n推荐", ApplicableModes="Sequential,Handoff,GroupChat,Magentic" },
        new() { Title="代码审查", Category="多Agent协作", Tags="代码,审核", Question="审查: function add(a,b){ return a-b }", ExpectedKeyPoints="函数名不符\n校验缺失\n命名", ApplicableModes="Sequential,Handoff" },
        new() { Title="方案策划", Category="多Agent协作", Tags="创意,写作", Question="设计一个SaaS产品的Go-to-Market策略", ExpectedKeyPoints="目标用户\n定价\n渠道\nMVP\n获客成本", ApplicableModes="Sequential,GroupChat,Magentic" },
        new() { Title="知识库查询", Category="RAG", Tags="知识库,检索", Question="根据知识库,DeepSeek V4技术创新有哪些？", ExpectedKeyPoints="知识库检索\nMoE\n参数\n性能", ApplicableModes="Sequential", RequiresKnowledgeBase=true },
        new() { Title="文档要点", Category="RAG", Tags="知识库,摘要", Question="总结知识库中微服务架构优势", ExpectedKeyPoints="独立部署\n技术栈\n隔离\n自治", ApplicableModes="Sequential", RequiresKnowledgeBase=true },
        new() { Title="跨文档关联", Category="RAG", Tags="知识库,推理", Question="知识库中AI安全相关文档,共同建议是什么？", ExpectedKeyPoints="多文档检索\n交叉验证\n主题提取", ApplicableModes="Sequential", RequiresKnowledgeBase=true },
        new() { Title="精确数据", Category="RAG", Tags="知识库,数值", Question="知识库2025年预算数据是多少？请引用出处", ExpectedKeyPoints="精确数值\n引用来源", ApplicableModes="Sequential", RequiresKnowledgeBase=true },
        new() { Title="知识库推理", Category="RAG", Tags="知识库,推理", Question="根据库中Python和Go文档,分析各自适合的微服务场景", ExpectedKeyPoints="检索→对比\n语言特性\n生态\n场景", ApplicableModes="Sequential", RequiresKnowledgeBase=true },
        new() { Title="客户列表查询", Category="CRM", Tags="CRM,查询", Question="查询所有战略级别的客户", ExpectedKeyPoints="CRM搜索\n等级过滤", ApplicableModes="Crm", ExpectedToolCalls="[\"search_customers\"]" },
        new() { Title="新增客户", Category="CRM", Tags="CRM,创建", Question="添加新客户:张三,腾讯,电话13800138000,等级重要", ExpectedKeyPoints="create_customer\n参数正确", ApplicableModes="Crm", ExpectedToolCalls="[\"create_customer\"]" },
        new() { Title="客户详情+跟进", Category="CRM", Tags="CRM,查询,跟进", Question="查看客户ID1详情,记录电话沟通了合作事宜", ExpectedKeyPoints="get_customer_detail\nadd_followup", ApplicableModes="Crm", ExpectedToolCalls="[\"get_customer_detail\",\"add_followup\"]" },
        new() { Title="敏感操作", Category="CRM", Tags="CRM,删除,人审", Question="删除客户ID999,原因重复录入", ExpectedKeyPoints="delete_customer\n审批\n人审", ApplicableModes="Crm", ExpectedToolCalls="[\"delete_customer\"]" },
        new() { Title="CRM统计", Category="CRM", Tags="CRM,分析", Question="分析客户等级分布,给出分级管理建议", ExpectedKeyPoints="search_customers\n分析\n建议", ApplicableModes="Crm", ExpectedToolCalls="[\"search_customers\"]" },
        new() { Title="搜索+分析", Category="工具调用", Tags="搜索,Tavily", Question="搜索2024 AI领域三个最重要突破并分析影响", ExpectedKeyPoints="搜索\n列突破\n分析", ApplicableModes="Sequential,Handoff,GroupChat" },
        new() { Title="知识库+搜索", Category="工具调用", Tags="知识库,搜索,混合", Question="对比知识库中Docker和网上Podman最新信息", ExpectedKeyPoints="知识库检索\n网络搜索\n对比", ApplicableModes="Sequential,Handoff", RequiresKnowledgeBase=true },
        new() { Title="多工具串联", Category="工具调用", Tags="CRM,搜索,串联", Question="搜索最新AI芯片新闻,然后创建新客户NVIDIA", ExpectedKeyPoints="搜索\ncreate_customer\n串联", ApplicableModes="Crm", ExpectedToolCalls="[\"create_customer\"]" },
        new() { Title="工具验证", Category="工具调用", Tags="工具,CRM", Question="用search_customers查腾讯,然后get_customer_detail看第一个", ExpectedKeyPoints="两次调用\n参数传递", ApplicableModes="Crm", ExpectedToolCalls="[\"search_customers\",\"get_customer_detail\"]" },
        new() { Title="工具容错", Category="工具调用", Tags="容错", Question="查询不存在的客户ID 99999999", ExpectedKeyPoints="调用工具\n错误处理\n友好提示", ApplicableModes="Crm", ExpectedToolCalls="[\"get_customer_detail\"]" },
        new() { Title="高风险操作", Category="人审", Tags="CRM,删除,人审,快速", Question="删除客户ID1,测试数据清理", ExpectedKeyPoints="触发人审\n等待审批", ApplicableModes="Crm", ExpectedToolCalls="[\"delete_customer\"]" },
        new() { Title="连续删除", Category="人审", Tags="人审", Question="连续删除3个测试客户ID 10,11,12", ExpectedKeyPoints="串行审批\n每次人审", ApplicableModes="Crm", ExpectedToolCalls="[\"delete_customer\"]" },
        new() { Title="审批拒绝", Category="人审", Tags="人审,拒绝", Question="删除不应该被删的客户ID1", ExpectedKeyPoints="人审拒绝\n不执行\n记录原因", ApplicableModes="Crm", ExpectedToolCalls="[\"delete_customer\"]" },
        new() { Title="批量跟进", Category="人审", Tags="人审,批量", Question="给客户ID1和2都添加跟进:电话沟通进度", ExpectedKeyPoints="批量add_followup\n正常执行", ApplicableModes="Crm", ExpectedToolCalls="[\"add_followup\"]" },
        new() { Title="审批链路完整", Category="人审", Tags="人审,完整链路", Question="删除客户ID3,然后查询确认已删除", ExpectedKeyPoints="delete→审批→search\n完整审计", ApplicableModes="Crm", ExpectedToolCalls="[\"delete_customer\",\"search_customers\"]" },
    ];
}
