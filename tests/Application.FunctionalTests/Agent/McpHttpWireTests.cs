using System.Net;
using System.Net.Http.Json;
using Lingban.Domain.Constants;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;

namespace Lingban.Application.FunctionalTests.Agent;

/// <summary>
/// M6 还债项:HTTP `/mcp` 的线级回归——鉴权(401)、限速(429 + Retry-After)、
/// 业务错误经完整 Streamable HTTP 编解码后 isError=true。
/// stdio 冒烟测试不覆盖 Web 管道(授权策略、限速器),此处补齐。
/// </summary>
public class McpHttpWireTests : TestBase
{
    private static HttpClient CreateHttpClient() =>
        FunctionalTestSetup.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task<string> LoginAsync(HttpClient http, string email, string password)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/Users/login", new { email, password });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, "登录必须成功才能测线级鉴权之后的行为");
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.AccessToken;
    }

    [Test]
    public async Task UnauthenticatedMcpRequestIsRejectedWith401()
    {
        using HttpClient http = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" })
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        HttpResponseMessage response = await http.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task AuthorizedClientListsToolsAndBusinessErrorSurfacesAsIsError()
    {
        await TestApp.RunAsUserAsync("mcp-wire@local", "Testing1234!", [Roles.MesReader]);
        using HttpClient http = CreateHttpClient();
        string token = await LoginAsync(http, "mcp-wire@local", "Testing1234!");

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "lingban-mes-http-wire",
            Endpoint = new Uri("https://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" }
        }, http, ownsHttpClient: false);

        await using McpClient client = await McpClient.CreateAsync(transport);

        IList<McpClientTool> tools = await client.ListToolsAsync();
        tools.Select(tool => tool.Name).OrderBy(name => name).ShouldBe(new[]
        {
            "mes_analyze_delayed_orders", "mes_calculate_oee",
            "mes_get_defect_summary", "mes_get_today_work_orders", "mes_search_knowledge"
        });

        var badDate = await client.CallToolAsync(
            "mes_calculate_oee",
            new Dictionary<string, object?> { ["equipmentCode"] = "EQ-X", ["productionDate"] = "2026-02-30" });
        badDate.IsError.ShouldBe(true);
        ((ModelContextProtocol.Protocol.TextContentBlock)badDate.Content[0]).Text.ShouldContain("yyyy-MM-dd");
    }

    [Test]
    public async Task McpEndpointEnforcesPerUserRateLimitWith429AndRetryAfter()
    {
        // 专用用户隔离限速分区,避免与其他测试共享固定窗口预算。
        await TestApp.RunAsUserAsync("mcp-hammer@local", "Testing1234!", [Roles.MesReader]);
        using HttpClient http = CreateHttpClient();
        string token = await LoginAsync(http, "mcp-hammer@local", "Testing1234!");
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // mcp 策略 60/min(全局兜底 80/min,先到先触发);窗口内连发必然撞限。
        // 上限 130 次防固定窗口翻转时假阴性;计数在限速中间件,body 合法与否无关。
        HttpResponseMessage? limited = null;
        for (int attempt = 0; attempt < 130; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = JsonContent.Create(new { jsonrpc = "2.0", id = attempt, method = "ping" })
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            HttpResponseMessage response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
                break;
            }

            response.Dispose();
        }

        limited.ShouldNotBeNull("130 次连发未见 429,限速未生效");
        limited.Headers.RetryAfter.ShouldNotBeNull();
        (await limited.Content.ReadAsStringAsync()).ShouldContain("RATE_LIMITED");
    }

    private sealed record LoginResponse(string AccessToken);
}
