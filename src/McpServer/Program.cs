using Lingban.Agent.Chat;
using Lingban.Agent.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// lingban-mes MCP Server(stdio):由 MCP 客户端(Claude Code / Claude Desktop)拉起。
// 数据库连接串经环境变量 ConnectionStrings__LingbanDb 传入。
// stdio 协议约束:stdout 只属于协议,日志一律走 stderr。
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.AddApplicationServices();
builder.AddInfrastructureServices();
builder.Services.AddSingleton<Lingban.Application.Common.Interfaces.IUser>(
    new Lingban.Application.Common.Models.ServiceUser("mcp-local"));
builder.Services.AddScoped<IAgentInvocationClock, AgentInvocationClock>();
builder.Services.AddScoped<MesToolExecutor>();

builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "lingban-mes", Version = "0.4.0" })
    .WithStdioServerTransport()
    .WithTools<LingbanMesTools>();

await builder.Build().RunAsync();
