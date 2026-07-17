// ============================================================
// ApproverAgent - 人审决策 Agent
//
// 职责：判断 AI 回复中是否涉及敏感操作，决定是否触发人工审核
// 适用场景：在 Sequential/Concurrent 等编排的末端，对其他 Agent 的输出做风险审查
//
// 设计思路：
//   - 输入：另一个 Agent 的产出文本
//   - 输出：[APPROVE] 直接通过 / [REVIEW] 需人审（附风险点说明）
//   - 与 CriticAgent 区别：Critic 看内容质量，Approver 看操作风险
//
// 敏感操作判定维度（system prompt 定义）：
//   1. 删除数据（删除客户/订单/记录）
//   2. 修改金额（价格/合同金额）
//   3. 发送对外正式消息（邮件/通知客户）
//   4. 批量操作（影响多条数据）
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Agents;

public static class ApproverAgent
{
    public const string Name = "Approver";

    /// <summary>需人审的标记</summary>
    public const string ReviewTag = "[REVIEW]";
    public const string ApproveTag = "[APPROVE]";

    public static ChatClientAgent Create(IChatClient chatClient)
    {
        var instructions = """
            你是风险审核员（Approver）。你的职责是判断 AI 助手的回复/操作是否涉及敏感操作，是否需要人工审核。

            【敏感操作判定标准】
            1. 删除数据：删除客户、订单、记录等
            2. 修改金额：修改价格、合同金额、折扣
            3. 对外发送：发送正式邮件、通知客户、对外公告
            4. 批量操作：影响多条数据或全体用户的操作
            5. 不可逆操作：任何无法撤销的操作

            【输出格式】
            - 不涉及敏感操作：输出 [APPROVE] 后简述判断理由
            - 涉及敏感操作：输出 [REVIEW] 后说明风险点和建议审核要点

            【示例】
            输入："已帮您删除客户 #12"
            输出：
            [REVIEW]
            该操作为删除客户数据，属于不可逆操作。建议审核：
            - 确认客户 #12 是否确实应删除
            - 检查是否有关联的跟进记录或订单

            输入："已新建客户 张三"
            输出：
            [APPROVE]
            新建客户为可逆操作，且仅影响单条数据，风险低。
            """;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "风险审核员：判断是否需人工审核",
            tools: null,
            loggerFactory: null,
            null);
    }

    /// <summary>
    /// 解析 Approver 输出是否要求人审
    /// </summary>
    public static bool TryParseReview(string output, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(output)) return false;
        var firstLine = output.Split('\n', StringSplitOptions.None)
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        if (!firstLine.StartsWith(ReviewTag, StringComparison.OrdinalIgnoreCase)) return false;
        reason = string.Join('\n', output.Split('\n').Skip(1)).Trim();
        return true;
    }
}
