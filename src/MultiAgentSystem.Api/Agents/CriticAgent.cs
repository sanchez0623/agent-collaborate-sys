// ============================================================
// CriticAgent - 审核员 Agent
// 职责：审核 Writer 的输出，决定通过（APPROVE）或退回（REJECT）
// 这是流水线的第 3 步：撰写回答 → 质量审核
//
// 关键设计：输出固定格式的判定结果，编排服务据此解析是否需要重写
//   - 第一行必须是 [APPROVE] 或 [REJECT]
//   - 第二行起为审核意见（REJECT 时供 Writer 改进参考）
//
// 退回重写机制：
//   - 编排服务解析到 REJECT → 把意见反馈给 Writer 重写
//   - 最多重写 2 轮，仍不通过则强制输出最后一版（避免死循环）
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Agents;

public static class CriticAgent
{
    /// <summary>Agent 标识名（前端工作流面板据此显示）</summary>
    public const string Name = "Critic";

    /// <summary>审核通过的关键字（编排服务据此判定是否退回）</summary>
    public const string ApproveTag = "[APPROVE]";

    /// <summary>审核退回的关键字（编排服务据此判定是否退回）</summary>
    public const string RejectTag = "[REJECT]";

    /// <summary>
    /// 创建审核员 Agent
    /// </summary>
    /// <param name="chatClient">共享的 IChatClient（连接 DeepSeek）</param>
    public static ChatClientAgent Create(IChatClient chatClient)
    {
        var instructions = """
            你是一名严格的审核员（Critic）。你的任务：
            评估 Writer 写作的回答是否满足质量要求，并输出判定结果。

            审核维度：
            1. 准确性：事实和数据是否与研究员素材一致，有无臆造
            2. 完整性：是否覆盖了用户问题的关键点，有无遗漏
            3. 清晰度：表达是否清楚、结构是否合理
            4. 相关性：是否切题，有无跑题或冗余内容

            【输出格式要求 - 必须严格遵守】
            第一行只能是以下二者之一：
              [APPROVE]   - 当回答整体达标时使用
              [REJECT]    - 当回答存在明显缺陷需要重写时使用

            第二行起为审核意见：
              - APPROVE：简要说明亮点（1-2 句话）
              - REJECT：列出具体问题及改进建议（Writer 会据此重写）

            判定原则：
            - 只有严重问题才 REJECT（事实错误、严重遗漏、答非所问）
            - 小瑕疵（措辞、排版）应 APPROVE，不必苛求完美
            - 避免连续 REJECT 导致无法收敛

            示例输出（APPROVE）：
            [APPROVE]
            回答准确完整，结构清晰。

            示例输出（REJECT）：
            [REJECT]
            1. 第二段的数据与素材不一致，请核对
            2. 缺少对成本部分的说明，需补充
            """;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "审核员：审核回答质量，决定通过或退回",
            tools: null,                // Critic 无工具，专注审核
            loggerFactory: null,
            null);
    }

    /// <summary>
    /// 解析 Critic 输出，判断是否通过
    /// 规则：首行非空内容以 [APPROVE] 开头则通过，否则视为退回
    /// </summary>
    /// <param name="criticOutput">Critic Agent 的原始输出</param>
    /// <param name="feedback">退回时的审核意见（通过时为空）</param>
    /// <returns>true=通过，false=退回</returns>
    public static bool TryParseVerdict(string criticOutput, out string feedback)
    {
        feedback = "";
        if (string.IsNullOrWhiteSpace(criticOutput)) return false;

        // 取首行非空内容作为判定行
        var lines = criticOutput.Split('\n', StringSplitOptions.None);
        var verdictLine = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim()
                          ?? "";
        var isApproved = verdictLine.StartsWith(ApproveTag, StringComparison.OrdinalIgnoreCase);

        if (!isApproved)
        {
            // 退回时：去掉判定行，剩余部分作为反馈意见
            feedback = string.Join('\n', lines.Skip(1)).Trim();
        }
        return isApproved;
    }
}
