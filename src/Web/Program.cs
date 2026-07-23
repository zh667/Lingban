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
    options.AddPolicy("MesData", policy =>
        policy.RequireRole(Lingban.Domain.Constants.Roles.Administrator, Lingban.Domain.Constants.Roles.MesReader)));

static string RatePartition(HttpContext httpContext) =>
    httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } userId
        ? $"user:{userId}"
        : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous"}";

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (rejectedContext, token) =>
    {
        rejectedContext.HttpContext.Response.Headers.RetryAfter = "60";
        await rejectedContext.HttpContext.Response.WriteAsJsonAsync(
            new { error = "RATE_LIMITED", retryAfterSeconds = 60 }, token);
    };
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
