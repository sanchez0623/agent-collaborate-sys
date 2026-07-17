// ============================================================
// AgentRegistry - Agent 注册中心
// 作用：集中创建和持有所有 ChatClientAgent，按名称检索
// 所有编排策略都从这里按需取 Agent，避免各策略重复创建
//
// Per-Agent Temperature：
//   不同 Agent 需要不同温度（创造性 vs 确定性），统一在此配置
//   - Researcher/Coder/Analyst：0.3（偏确定性，避免幻觉）
//   - Writer/Consultant：0.7（偏创造性，语言流畅）
//   - Critic/Approver：0.1（严格判定，偏离即误判）
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Agents;
using MultiAgentSystem.Api.Tools;

namespace MultiAgentSystem.Api.Services;

public class AgentRegistry
{
    private readonly Dictionary<string, ChatClientAgent> _agents;
    private readonly TavilySearchTool? _searchTool;

    /// <summary>Per-Agent ChatOptions（温度等参数）</summary>
    private static readonly Dictionary<string, ChatOptions> AgentOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Critic"]    = new() { Temperature = 0.1f },  // 严格判定
        ["Approver"]  = new() { Temperature = 0.1f },
        ["Researcher"]= new() { Temperature = 0.3f },  // 偏确定性
        ["Coder"]     = new() { Temperature = 0.3f },
        ["Analyst"]   = new() { Temperature = 0.3f },
        ["Writer"]    = new() { Temperature = 0.7f },  // 偏创造性
        ["Consultant"]= new() { Temperature = 0.7f },
        ["Support"]   = new() { Temperature = 0.5f },
        ["Coordinator"] = new() { Temperature = 0.3f },
        ["CrmAgent"]  = new() { Temperature = 0.3f },
        ["KnowledgeAgent"] = new() { Temperature = 0.3f },
    };

    /// <summary>
    /// 获取指定 Agent 的 ChatOptions（含温度等参数）
    /// </summary>
    public static ChatOptions? GetAgentOptions(string agentName)
        => AgentOptions.TryGetValue(agentName, out var opts) ? opts : null;

    public AgentRegistry(IChatClient chatClient, TavilySearchTool? searchTool, CrmTools? crmTools = null, KnowledgeTools? knowledgeTools = null)
    {
        _searchTool = searchTool;

        // 为每个 Agent 创建独立的温度包装器
        var clientFor = (string name) =>
        {
            var temp = AgentOptions.TryGetValue(name, out var opts) ? opts.Temperature ?? 0.7f : 0.7f;
            return new TemperatureChatClient(chatClient, temp);
        };

        _agents = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Critic"]         = CriticAgent.Create(clientFor("Critic")),
            ["Researcher"]     = ResearcherAgent.Create(clientFor("Researcher"), searchTool),
            ["Writer"]         = WriterAgent.Create(clientFor("Writer")),
            ["Analyst"]        = AnalystAgent.Create(clientFor("Analyst")),
            ["Coder"]          = CoderAgent.Create(clientFor("Coder")),
            ["Consultant"]     = ConsultantAgent.Create(clientFor("Consultant")),
            ["Support"]        = SupportAgent.Create(clientFor("Support")),
            ["Coordinator"]    = CoordinatorAgent.Create(clientFor("Coordinator")),
            ["CrmAgent"]       = CrmAgent.Create(clientFor("CrmAgent"), crmTools!),
            ["Approver"]       = ApproverAgent.Create(clientFor("Approver")),
            ["KnowledgeAgent"] = KnowledgeAgent.Create(clientFor("KnowledgeAgent"), knowledgeTools!),
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
