using Lingban.Infrastructure.Data;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.AddServiceDefaults();

builder.AddKeyVaultIfConfigured();
builder.AddApplicationServices();
builder.AddInfrastructureServices();
builder.AddAgentServices();
builder.AddWebServices();

// lingban-mes 的 HTTP 暴露(与 stdio 同一工具实现);须携带 Identity bearer。
builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "lingban-mes", Version = "0.4.0" })
    .WithHttpTransport()
    .WithTools<Lingban.Agent.Tools.LingbanMesTools>();

// 数据访问策略(五审 #1):自注册用户默认无角色,读不到 MES 数据;角色由管理员授予。
builder.Services.AddAuthorization(options =>
{
    // 写角色隐含读(九审 #5 口径拍定):报工者必须能看工单才能报工,
    // ProductionReporter 单角色即可用聊天与只读工具。
    options.AddPolicy("MesData", policy =>
        policy.RequireRole(
            Lingban.Domain.Constants.Roles.Administrator,
            Lingban.Domain.Constants.Roles.MesReader,
            Lingban.Domain.Constants.Roles.ProductionReporter));
    // 知识库写与读分权(七审 #2):MesReader 只能读,投毒面关闭。
    options.AddPolicy("KnowledgeWrite", policy =>
        policy.RequireRole(Lingban.Domain.Constants.Roles.Administrator, Lingban.Domain.Constants.Roles.KnowledgeManager));
    // 生产写分权(八审 #3):MesReader 不得确认报工;写路径要求专门角色。
    options.AddPolicy("MesWrite", policy =>
        policy.RequireRole(Lingban.Domain.Constants.Roles.Administrator, Lingban.Domain.Constants.Roles.ProductionReporter));
});

static string RatePartition(HttpContext httpContext) =>
    httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } userId
        ? $"user:{userId}"
        : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous"}";

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (rejectedContext, token) =>
    {
        int retryAfter = rejectedContext.Lease.TryGetMetadata(
                System.Threading.RateLimiting.MetadataName.RetryAfter, out TimeSpan value)
            ? (int)Math.Ceiling(value.TotalSeconds)
            : 60;
        rejectedContext.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString();
        await rejectedContext.HttpContext.Response.WriteAsJsonAsync(
            new { error = "RATE_LIMITED", retryAfterSeconds = retryAfter }, token);
    };
    // 六审 #5:聊天与 MCP 的策略不共享预算,叠加可达 70/min——全局用户级预算兜底。
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(
        httpContext => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            RatePartition(httpContext),
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 80
            }));
    options.AddPolicy("agent-chat", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            RatePartition(httpContext),
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 10
            }));
    options.AddPolicy("mcp", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            RatePartition(httpContext),
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 60
            }));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    await app.InitialiseDatabaseAsync();
}
else
{
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseRateLimiter();
app.UseHttpsRedirection();
app.UseCors(static builder =>
    builder.AllowAnyMethod()
        .AllowAnyHeader()
        .AllowAnyOrigin());

app.UseFileServer();

app.MapOpenApi();
app.MapScalarApiReference();

app.UseExceptionHandler(options => { });

app.Map("/", () => Results.Redirect("/scalar"));

app.MapDefaultEndpoints();
app.MapEndpoints(typeof(Program).Assembly);
app.MapMcp("/mcp").RequireAuthorization("MesData").RequireRateLimiting("mcp");


app.Run();
