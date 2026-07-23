using System.Net;
using System.Net.Http.Json;
using Lingban.Domain.Constants;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lingban.Application.FunctionalTests.Agent;

/// <summary>
/// 角色矩阵的线级回归(九审 #5):写角色隐含读(ProductionReporter 单角色可用 MesData 面),
/// 只读角色到不了 MesWrite 面。走真实 HTTP 管道(bearer + 策略),不是行为层自证。
/// </summary>
public class AgentChatWireTests : TestBase
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
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.AccessToken;
    }

    [Test]
    public async Task MesReaderIsForbiddenFromConfirmEndpoint()
    {
        await TestApp.RunAsUserAsync("wire-reader@local", "Testing1234!", [Roles.MesReader]);
        using HttpClient http = CreateHttpClient();
        string token = await LoginAsync(http, "wire-reader@local", "Testing1234!");
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);

        HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/AgentChat/actions/1/confirm", new { approve = true });
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "只读角色不得进入生产写确认面");
    }

    [Test]
    public async Task ProductionReporterAloneCanReachBothReadAndWriteSurfaces()
    {
        await TestApp.RunAsUserAsync("wire-reporter@local", "Testing1234!", [Roles.ProductionReporter]);
        using HttpClient http = CreateHttpClient();
        string token = await LoginAsync(http, "wire-reporter@local", "Testing1234!");
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // MesWrite 面:授权放行后落到业务层 NotFound,而不是 401/403。
        HttpResponseMessage confirm = await http.PostAsJsonAsync(
            "/api/AgentChat/actions/999999/confirm", new { approve = true });
        confirm.StatusCode.ShouldBe(HttpStatusCode.NotFound, "写角色必须能通过 MesWrite 策略");

        // MesData 面(以 /mcp 为探针,不依赖 LLM):不得被 401/403 拦下。
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" })
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        HttpResponseMessage mes = await http.SendAsync(request);
        mes.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        mes.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, "写角色隐含读:单角色必须能进 MesData 面");
    }

    private sealed record LoginResponse(string AccessToken);
}
