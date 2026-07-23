using System.Text.Json;
using Lingban.Agent.Tools;

namespace Lingban.Application.FunctionalTests.Agent;

/// <summary>
/// M4 验收:MCP 工具面与 Agent 循环共用同一实现——返回真实数据 + 独立路径校验结论 + 真实 SQL。
/// 协议层(stdio 握手)由官方 SDK 保证,另以 MCP Inspector 手工验证。
/// </summary>
public class McpToolsTests : TestBase
{
    [Test]
    public async Task McpToolReturnsVerifiedDataWithRealSql()
    {
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            context.AddRange(
                new Domain.Entities.Calendar.Shift { Code = "DAY", Name = "白班", StartLocalTime = new TimeOnly(8, 0), EndLocalTime = new TimeOnly(20, 0) },
                new Domain.Entities.Calendar.Shift { Code = "NIGHT", Name = "夜班", StartLocalTime = new TimeOnly(20, 0), EndLocalTime = new TimeOnly(8, 0) });
            await context.SaveChangesAsync();
        });

        var tools = new LingbanMesTools(FunctionalTestSetup.ScopeFactory);
        string json = await tools.GetTodayWorkOrdersAsync();

        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("verification").GetProperty("status").GetString().ShouldBe("Verified");
        document.RootElement.GetProperty("toolSql").GetArrayLength().ShouldBeGreaterThan(0);
        document.RootElement.GetProperty("verificationSql").GetArrayLength().ShouldBeGreaterThan(0);
        document.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task McpToolReturnsRecoverableStructuredErrors()
    {
        var tools = new LingbanMesTools(FunctionalTestSetup.ScopeFactory);

        string badDate = await tools.CalculateOeeAsync("EQ-NONE", "2026-02-30");
        badDate.ShouldContain("yyyy-MM-dd");

        string badLine = await tools.AnalyzeDelayedOrdersAsync("不存在的线");
        badLine.ShouldContain("recoverable");
    }
}
