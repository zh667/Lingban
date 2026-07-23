using Lingban.Agent.Chat;
using Lingban.Application.Common;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.Common.Verification;
using Lingban.Application.Knowledge.Queries;

namespace Lingban.Application.FunctionalTests.Agent;

/// <summary>七审 #4 回归钉:引用执法——正确引用通过、伪造引用与缺失引用失败。纯函数测试。</summary>
public class AnswerAuditorCitationTests
{
    private static ToolResultEvent KnowledgeResult() => new(
        "call-1",
        ToolNames.SearchKnowledge,
        new KnowledgeSearchResultDto("返修温度", new List<KnowledgeHit>
        {
            new(1, "连锡缺陷处理规程", "连锡缺陷处理规程 > 4.1 返修流程", "使用恒温烙铁(温度 320 摄氏度)返修。", 0.9)
        }),
        VerificationResult.FromChecks(new[] { new VerificationCheck("x", "1", "1", true) }),
        Array.Empty<string>(),
        Array.Empty<string>(),
        1);

    [Test]
    public void NumberFromVerificationSummaryIsAllowed()
    {
        // eval 补验抓出的口径缺口:校验摘要(如 "All 5 checks passed")也是模型看到的工具事实,
        // 如实转述"5 项检查通过"不得判为无出处数字。
        var result = new ToolResultEvent(
            "call-1", ToolNames.GetDefectSummary,
            new { totalQuantity = 3m },
            new VerificationResult
            {
                Status = VerificationStatus.Verified,
                Summary = "All 5 checks passed against an independent query path."
            },
            Array.Empty<string>(), Array.Empty<string>(), 1);

        var audit = AnswerAuditor.Audit(
            "最近缺陷共 3 件,数据经独立复核,全部 5 项检查通过。",
            "最近缺陷情况?",
            new[] { result },
            Array.Empty<ToolErrorEvent>());
        audit.UnverifiedNumbers.ShouldBeEmpty();
        audit.Passed.ShouldBeTrue();
    }

    [Test]
    public void ValidCitationPasses()
    {
        var audit = AnswerAuditor.Audit(
            "返修使用 320 摄氏度恒温烙铁 [连锡缺陷处理规程§4.1 返修流程]。",
            "返修温度是多少?",
            new[] { KnowledgeResult() },
            Array.Empty<ToolErrorEvent>());
        audit.InvalidCitations.ShouldBeEmpty();
        audit.Passed.ShouldBeTrue();
    }

    [Test]
    public void FabricatedCitationFails()
    {
        var audit = AnswerAuditor.Audit(
            "返修使用 320 摄氏度恒温烙铁 [不存在文档§伪造章节]。",
            "返修温度是多少?",
            new[] { KnowledgeResult() },
            Array.Empty<ToolErrorEvent>());
        audit.Passed.ShouldBeFalse();
        audit.InvalidCitations.ShouldContain("[不存在文档§伪造章节]");
    }

    [Test]
    public void MissingCitationFailsWhenKnowledgeWasHit()
    {
        var audit = AnswerAuditor.Audit(
            "返修使用 320 摄氏度恒温烙铁。",
            "返修温度是多少?",
            new[] { KnowledgeResult() },
            Array.Empty<ToolErrorEvent>());
        audit.Passed.ShouldBeFalse();
        audit.InvalidCitations.ShouldContain("<MISSING_CITATION>");
    }
}
