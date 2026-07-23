using System.ClientModel;
using Lingban.Agent.Chat;
using Lingban.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;

namespace Microsoft.Extensions.DependencyInjection;

public static class AgentDependencyInjection
{
    public static void AddAgentServices(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));

        builder.Services.AddScoped<IAgentInvocationClock, AgentInvocationClock>();
        builder.Services.AddScoped<AgentToolset>();
        builder.Services.AddScoped<IAgentChatService, AgentChatService>();

        builder.Services.AddScoped<IChatClient>(provider =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlmOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.Model))
            {
                throw new InvalidOperationException(
                    "LLM is not configured. Set Llm:ApiKey / Llm:BaseUrl / Llm:Model via user-secrets (id: lingban-web).");
            }

            var clientOptions = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                clientOptions.Endpoint = new Uri(options.BaseUrl);
            }

            IChatClient inner = new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions)
                .GetChatClient(options.Model)
                .AsIChatClient();

            // FunctionInvokingChatClient 驱动工具循环;工具内部完成钉钟/校验/SQL 分段。
            // 并行工具调用显式关闭:AgentToolset 事件缓冲、QueryLog 与 scoped DbContext
            // 均按串行设计;开启并行前必须按 CallId 建立独立作用域(四审 #8)。
            return inner.AsBuilder()
                .UseFunctionInvocation(configure: client => client.AllowConcurrentInvocation = false)
                .Build(provider);
        });
    }
}
