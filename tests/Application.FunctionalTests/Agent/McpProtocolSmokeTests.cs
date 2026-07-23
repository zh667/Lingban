using Lingban.Domain.Entities.Calendar;
using ModelContextProtocol.Client;

namespace Lingban.Application.FunctionalTests.Agent;

/// <summary>
/// 六审 #1/#8 的回归钉:真实启动 stdio 宿主,走完整协议
/// initialize → tools/list → tools/call,直接方法测试不再自证。
/// </summary>
public class McpProtocolSmokeTests : TestBase
{
    [Test]
    public async Task StdioServerHandshakesListsAndExecutesTools()
    {
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            context.AddRange(
                new Shift { Code = "DAY", Name = "白班", StartLocalTime = new TimeOnly(8, 0), EndLocalTime = new TimeOnly(20, 0) },
                new Shift { Code = "NIGHT", Name = "夜班", StartLocalTime = new TimeOnly(20, 0), EndLocalTime = new TimeOnly(8, 0) });
            await context.SaveChangesAsync();
        });

        string repoRoot = FindRepoRoot();
        // 构建配置从当前测试程序集路径推导(本地 debug / CI release),不许写死。
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
        string serverDll = Path.Combine(
            repoRoot, "artifacts", "bin", "McpServer", configuration, "Lingban.McpServer.dll");
        File.Exists(serverDll).ShouldBeTrue($"先构建解决方案:{serverDll} 不存在");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "lingban-mes-smoke",
            Command = "dotnet",
            Arguments = [serverDll],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["ConnectionStrings__LingbanDb"] = FunctionalTestSetup.DatabaseConnectionString
            }
        });

        await using McpClient client = await McpClient.CreateAsync(transport);

        // tools/list:四个工具 + 幂等注解。
        IList<McpClientTool> tools = await client.ListToolsAsync();
        tools.Select(tool => tool.Name).OrderBy(name => name).ShouldBe(new[]
        {
            "mes_analyze_delayed_orders", "mes_calculate_oee",
            "mes_get_defect_summary", "mes_get_today_work_orders"
        });

        // 成功调用:真实数据 + 校验结论走完整协议编解码。
        var success = await client.CallToolAsync(
            "mes_get_today_work_orders", new Dictionary<string, object?>());
        success.IsError.ShouldNotBe(true);
        string payload = ((ModelContextProtocol.Protocol.TextContentBlock)success.Content[0]).Text;
        payload.ShouldContain("verification");
        payload.ShouldContain("Verified");

        // 业务错误:协议层 isError=true(六审实测曾为 SDK 通用错误)。
        var badDate = await client.CallToolAsync(
            "mes_calculate_oee",
            new Dictionary<string, object?> { ["equipmentCode"] = "EQ-X", ["productionDate"] = "2026-02-30" });
        badDate.IsError.ShouldBe(true);
        ((ModelContextProtocol.Protocol.TextContentBlock)badDate.Content[0]).Text.ShouldContain("yyyy-MM-dd");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lingban.slnx")))
        {
            dir = dir.Parent!;
        }

        return dir!.FullName;
    }
}
