// ============================================================
// CrmAgent - CRM 业务 Agent
//
// 职责：处理客户信息查询、新建客户、添加跟进、删除客户等 CRM 操作
// 能力：通过 AIFunctionFactory 注册的 5 个工具自主判断调用增删改查
//
// 适用编排：Magentic 路由（用户问 CRM 类问题→路由给本 Agent）/ 直接 CRM 模式
//
// 工具列表（见 CrmTools）：
//   search_customers / get_customer_detail / create_customer /
//   add_followup / delete_customer（含人审）
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Tools;

namespace MultiAgentSystem.Api.Agents;

public static class CrmAgent
{
    public const string Name = "CrmAgent";

    public static ChatClientAgent Create(IChatClient chatClient, CrmTools tools)
    {
        var instructions = """
            你是 CRM 业务助手（CrmAgent），负责管理客户关系数据。你可以调用以下工具：

            1. search_customers(keyword?) - 查询客户列表
            2. get_customer_detail(customerId) - 查看客户详情和跟进记录
            3. create_customer(name, company?, phone?, email?, level?, owner?) - 新建客户
            4. add_followup(customerId, content, method?, operator?) - 添加跟进记录
            5. delete_customer(customerId, reason) - 删除客户（需人工审核）

            工作原则：
            - 用户提到"查客户/有哪些客户/看下某某" → 调 search_customers
            - 用户提到"新建/录入/添加客户" → 调 create_customer（必填 name）
            - 用户提到"记录跟进/今天沟通了" → 调 add_followup
            - 用户提到"删除客户" → 调 delete_customer（会触发人审，告诉用户需等待审核）
            - 用户问具体客户情况且知道 ID → 调 get_customer_detail
            - 工具返回结果后，用自然语言简明总结，不要直接堆 JSON
            - 对模糊请求（如"看看客户"）先调 search_customers 列出，再问要不要看详情

            输出要求：中文，简洁，用 Markdown 列表/表格呈现客户信息。
            """;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "CRM业务助手：客户查询/新建/跟进/删除",
            tools: tools.AsAIFunctions(),
            loggerFactory: null,
            null);
    }
}
