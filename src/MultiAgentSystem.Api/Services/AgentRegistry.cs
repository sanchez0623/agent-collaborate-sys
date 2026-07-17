// ============================================================
// AgentRegistry - Agent 注册中心
// 作用：集中创建和持有所有 ChatClientAgent，按名称检索
// 所有编排策略都从这里按需取 Agent，避免各策略重复创建
//
// 当前注册的 8 个 Agent：
//   MVP-1 复用：Researcher / Writer / Critic
//   MVP-2 新增：Analyst / Coder / Consultant / Support / Coordinator
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Agents;
using MultiAgentSystem.Api.Tools;

namespace MultiAgentSystem.Api.Services;

public class AgentRegistry
{
    /// <summary>按名称索引的 Agent 字典</summary>
    private readonly Dictionary<string, ChatClientAgent> _agents;

    /// <summary>Tavily 搜索工具（Researcher 用）</summary>
    private readonly TavilySearchTool? _searchTool;

    public AgentRegistry(IChatClient chatClient, TavilySearchTool? searchTool, CrmTools? crmTools = null, KnowledgeTools? knowledgeTools = null)
    {
        _searchTool = searchTool;
        _agents = new(StringComparer.OrdinalIgnoreCase)
        {
            // MVP-1 复用
            [ResearcherAgent.Name] = ResearcherAgent.Create(chatClient, searchTool),
            [WriterAgent.Name] = WriterAgent.Create(chatClient),
            [CriticAgent.Name] = CriticAgent.Create(chatClient),
            // MVP-2 新增
            [AnalystAgent.Name] = AnalystAgent.Create(chatClient),
            [CoderAgent.Name] = CoderAgent.Create(chatClient),
            [ConsultantAgent.Name] = ConsultantAgent.Create(chatClient),
            [SupportAgent.Name] = SupportAgent.Create(chatClient),
            [CoordinatorAgent.Name] = CoordinatorAgent.Create(chatClient),
            // MVP-4 新增
            [CrmAgent.Name] = CrmAgent.Create(chatClient, crmTools!),
            [ApproverAgent.Name] = ApproverAgent.Create(chatClient),
            // MVP-3 新增
            [KnowledgeAgent.Name] = KnowledgeAgent.Create(chatClient, knowledgeTools!),
        };
    }

    /// <summary>按名称获取 Agent（不存在抛异常）</summary>
    public ChatClientAgent Get(string name)
    {
        if (_agents.TryGetValue(name, out var agent)) return agent;
        throw new KeyNotFoundException($"未注册的 Agent: {name}。可用: {string.Join(", ", _agents.Keys)}");
    }

    /// <summary>尝试获取 Agent（不存在返回 false）</summary>
    public bool TryGet(string name, out ChatClientAgent? agent) => _agents.TryGetValue(name, out agent);

    /// <summary>所有已注册 Agent 名称</summary>
    public IReadOnlyCollection<string> AllNames => _agents.Keys;

    /// <summary>按名称批量获取</summary>
    public List<ChatClientAgent> GetMany(params string[] names)
        => names.Select(n => Get(n)).ToList();
}
