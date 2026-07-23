using Lingban.Agent.Chat;
using Lingban.Agent.Tools;
using Lingban.Application.Common;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.Common.Verification;
using Lingban.Domain.Entities.Calendar;
using Lingban.Domain.Entities.Conversations;
using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using AppDbContext = Lingban.Infrastructure.Data.ApplicationDbContext;

namespace Lingban.Application.FunctionalTests.Agent;

/// <summary>
/// Agent 主循环功能测试(脚本化 IChatClient,不依赖真实 LLM):
/// 工具由 LLM 决定并真实执行、AsOf 钉死(校验 Verified 即为证明)、
/// token 事件与最终答案一致、会话与工具结果(含真实 SQL)持久化、多轮上下文喂给模型。
/// </summary>
public class AgentChatServiceTests : TestBase
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);

    private async Task SeedFactoryAsync()
    {
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            context.AddRange(
                new Shift { Code = "DAY", Name = "白班", StartLocalTime = new TimeOnly(8, 0), EndLocalTime = new TimeOnly(20, 0) },
                new Shift { Code = "NIGHT", Name = "夜班", StartLocalTime = new TimeOnly(20, 0), EndLocalTime = new TimeOnly(8, 0) });
            var product = new Product { Code = "FG-A", Name = "成品A" };
            var line = new ProductionLine { Code = "L1", Name = "一线" };
            var station = new Workstation { Code = "S1", Name = "工位一", ProductionLine = line };
            context.AddRange(product, line, station);
            await context.SaveChangesAsync();

            var order = WorkOrder.Create("WO-CHAT", product.Id, line.Id, 10m, "PCS");
            order.Release();
            order.Start(T0);
            context.Add(order);
            await context.SaveChangesAsync();
        });
    }

    private static AgentChatService CreateService(IServiceScope scope, IChatClient scripted)
    {
        IChatClient pipeline = scripted.AsBuilder()
            .UseFunctionInvocation()
            .Build(scope.ServiceProvider);

        var toolset = new AgentToolset(scope.ServiceProvider.GetRequiredService<MesToolExecutor>(), scope.ServiceProvider.GetRequiredService<ISender>());

        return new AgentChatService(
            pipeline,
            toolset,
            scope.ServiceProvider.GetRequiredService<IAgentInvocationClock>(),
            scope.ServiceProvider.GetRequiredService<IApplicationDbContext>(),
            new PinnedTimeProvider(T0.AddHours(1)),
            new FakeUser());
    }

    [Test]
    public async Task ToolLoopExecutesVerifiesStreamsAndPersists()
    {
        await SeedFactoryAsync();

        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAgentInvocationClock>();

        var scripted = new ScriptedChatClient(
            firstTurnToolCall: (ToolNames.GetTodayWorkOrders, "{}"),
            finalTokens: new[] { "今天共有 ", "1 张工单在产", ",数字已经过独立校验。" });
        AgentChatService service = CreateService(scope, scripted);

        var events = new List<AgentEvent>();
        await foreach (AgentEvent agentEvent in service.ChatAsync(null, "今天有几张工单?"))
        {
            events.Add(agentEvent);
        }

        // 工具真实执行且校验通过——Verified 同时证明 AsOf 已钉死(未钉死会 Unverified)。
        ToolResultEvent toolResult = events.OfType<ToolResultEvent>().ShouldHaveSingleItem();
        toolResult.ToolName.ShouldBe(ToolNames.GetTodayWorkOrders);
        toolResult.Verification.Status.ShouldBe(VerificationStatus.Verified);
        toolResult.ToolSql.ShouldNotBeEmpty();
        toolResult.VerificationSql.ShouldNotBeEmpty();
        toolResult.CallId.ShouldNotBeNullOrEmpty();

        // token 流拼起来 == 最终答案,不许伪造或缺漏。
        string streamed = string.Concat(events.OfType<TokenEvent>().Select(token => token.Text));
        streamed.ShouldBe("今天共有 1 张工单在产,数字已经过独立校验。");

        DoneEvent done = events.OfType<DoneEvent>().ShouldHaveSingleItem();

        // 持久化:助手消息内容 + 工具结果(含真实 SQL)。
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ConversationMessage assistant = await context.ConversationMessages
            .SingleAsync(message => message.Id == done.AssistantMessageId);
        assistant.Content.ShouldBe(streamed);
        assistant.ToolResultsJson.ShouldNotBeNull();
        assistant.ToolResultsJson.ShouldContain("SELECT");
        assistant.ToolResultsJson.ShouldContain("Verified");
    }

    [Test]
    public async Task AnswerAuditFlagsNumbersNotBackedByVerifiedToolData()
    {
        // 四审 #1 的回归钉:工具查到 1 张工单,模型嘴硬说 9 张——审计必须抓住。
        await SeedFactoryAsync();
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var scripted = new ScriptedChatClient(
            (ToolNames.GetTodayWorkOrders, "{}"),
            new[] { "今天有 9 张工单在生产。" });
        AgentChatService service = CreateService(scope, scripted);

        var events = new List<AgentEvent>();
        await foreach (AgentEvent agentEvent in service.ChatAsync(null, "今天有几张工单?"))
        {
            events.Add(agentEvent);
        }

        AnswerAuditEvent audit = events.OfType<AnswerAuditEvent>().ShouldHaveSingleItem();
        audit.Passed.ShouldBeFalse();
        audit.UnverifiedNumbers.ShouldContain("9");
    }

    [Test]
    public async Task InvalidToolDateReturnsStructuredRecoverableError()
    {
        // 四审 #5 的回归钉:模型给非法日期,得到含字段与格式的结构化错误而不是断流。
        await SeedFactoryAsync();
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var scripted = new ScriptedChatClient(
            (ToolNames.CalculateOee, "{\"equipmentCode\":\"EQ-X\",\"productionDate\":\"2026-02-30\"}"),
            new[] { "日期无效,请确认。" });
        AgentChatService service = CreateService(scope, scripted);

        var events = new List<AgentEvent>();
        await foreach (AgentEvent agentEvent in service.ChatAsync(null, "算一下 EQ-X 在 2026-02-30 的 OEE"))
        {
            events.Add(agentEvent);
        }

        events.OfType<ErrorEvent>().ShouldBeEmpty("参数错误必须可恢复,不许断流");
        IList<ChatMessage> seen = scripted.LastReceivedMessages.ShouldNotBeNull();
        string toolFeedback = string.Concat(seen.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(c => c.Result?.ToString() ?? string.Empty));
        toolFeedback.ShouldContain("productionDate");
        toolFeedback.ShouldContain("yyyy-MM-dd");
    }

    [Test]
    public async Task HitlProposalDoesNotMutateUntilConfirmed()
    {
        // 铁律 #5 全链路回归钉:提议 → 零改动;确认 → 执行;拒绝 → 不执行。
        await SeedFactoryAsync();
        string ownerId = await TestApp.RunAsDefaultUserAsync();
        int orderId;
        using (var seedScope = FunctionalTestSetup.ScopeFactory.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var order = await context.WorkOrders.FirstAsync(o => o.Code == "WO-CHAT");
            orderId = order.Id;
        }

        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var scripted = new ScriptedChatClient(
            ("ReportProduction", "{\"workOrderCode\":\"WO-CHAT\",\"completed\":5,\"qualified\":5}"),
            new[] { "已提交报工申请,等待车间确认。" });
        AgentChatService service = CreateService(scope, scripted);

        var events = new List<AgentEvent>();
        await foreach (AgentEvent agentEvent in service.ChatAsync(null, "帮 WO-CHAT 报工 5 件,全部合格"))
        {
            events.Add(agentEvent);
        }

        HitlPendingEvent pending = events.OfType<HitlPendingEvent>().ShouldHaveSingleItem();
        pending.ActionType.ShouldBe("ReportProduction");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.WorkOrders.AsNoTracking().FirstAsync(o => o.Id == orderId))
            .CompletedQuantity.ShouldBe(0m, "提议阶段禁止改动生产数据");

        // 确认 → 执行(属主 = test-user,与提议同一 FakeUser)。
        var confirm = new Lingban.Application.Actions.ConfirmPendingActionCommand(pending.ActionId, true);
        var confirmHandler = new Lingban.Application.Actions.ConfirmPendingActionCommandHandler(
            scope.ServiceProvider.GetRequiredService<IApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<ISender>(),
            new FakeUser(ownerId));
        var approved = await confirmHandler.Handle(confirm, CancellationToken.None);
        approved.Status.ShouldBe(Lingban.Domain.Enums.PendingActionStatus.Approved);

        (await db.WorkOrders.AsNoTracking().FirstAsync(o => o.Id == orderId))
            .CompletedQuantity.ShouldBe(5m);

        // 重复确认 → 拒绝(状态机)。
        await Should.ThrowAsync<InvalidOperationException>(
            () => confirmHandler.Handle(confirm, CancellationToken.None));

        // 非属主确认 → NotFound。
        var intruderHandler = new Lingban.Application.Actions.ConfirmPendingActionCommandHandler(
            scope.ServiceProvider.GetRequiredService<IApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<ISender>(),
            new FakeUser("intruder"));
        await Should.ThrowAsync<NotFoundException>(
            () => intruderHandler.Handle(new Lingban.Application.Actions.ConfirmPendingActionCommand(pending.ActionId, true), CancellationToken.None));
    }

    [Test]
    public async Task DuplicateClientMessageIdIsRejected()
    {
        // 六审遗留债回归钉:同 clientMessageId 重试不产生重复回合。
        await SeedFactoryAsync();
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        AgentChatService service = CreateService(scope, new ScriptedChatClient(null, new[] { "答一。" }));
        var key = Guid.NewGuid();
        DoneEvent done = null!;
        await foreach (AgentEvent e in service.ChatAsync(null, "问题一", key))
        {
            if (e is DoneEvent d) done = d;
        }

        using var scope2 = FunctionalTestSetup.ScopeFactory.CreateScope();
        AgentChatService retry = CreateService(scope2, new ScriptedChatClient(null, new[] { "不该执行。" }));
        var events = new List<AgentEvent>();
        await foreach (AgentEvent e in retry.ChatAsync(done.ConversationId, "问题一", key))
        {
            events.Add(e);
        }

        events.OfType<ErrorEvent>().ShouldHaveSingleItem().Message.ShouldContain("DUPLICATE");
        events.OfType<DoneEvent>().ShouldBeEmpty();
    }

    [Test]
    public async Task ConversationIsInvisibleToNonOwner()
    {
        // M4 债回归钉:非属主访问会话 = 不存在(NotFound),防 conversationId 枚举。
        await SeedFactoryAsync();
        int conversationId;
        using (var scope1 = FunctionalTestSetup.ScopeFactory.CreateScope())
        {
            AgentChatService owner = CreateService(scope1, new ScriptedChatClient(null, new[] { "答。" }));
            DoneEvent done = null!;
            await foreach (AgentEvent e in owner.ChatAsync(null, "属主的问题"))
            {
                if (e is DoneEvent d) done = d;
            }

            conversationId = done.ConversationId;
        }

        using var scope2 = FunctionalTestSetup.ScopeFactory.CreateScope();
        var pipeline = new ScriptedChatClient(null, new[] { "不该到这。" });
        IChatClient client = pipeline.AsBuilder().UseFunctionInvocation().Build(scope2.ServiceProvider);
        var intruder = new AgentChatService(
            client,
            new AgentToolset(scope2.ServiceProvider.GetRequiredService<MesToolExecutor>(), scope2.ServiceProvider.GetRequiredService<ISender>()),
            scope2.ServiceProvider.GetRequiredService<IAgentInvocationClock>(),
            scope2.ServiceProvider.GetRequiredService<IApplicationDbContext>(),
            new PinnedTimeProvider(T0.AddHours(2)),
            new FakeUser("intruder"));

        await Should.ThrowAsync<NotFoundException>(async () =>
        {
            await foreach (AgentEvent _ in intruder.ChatAsync(conversationId, "复述此前对话"))
            {
            }
        });
    }

    [Test]
    public async Task SecondTurnFeedsConversationHistoryToTheModel()
    {
        await SeedFactoryAsync();

        int conversationId;
        using (var scope1 = FunctionalTestSetup.ScopeFactory.CreateScope())
        {
            var first = new ScriptedChatClient(null, new[] { "第一轮回答。" });
            AgentChatService service1 = CreateService(scope1, first);
            DoneEvent done = null!;
            await foreach (AgentEvent agentEvent in service1.ChatAsync(null, "第一轮问题"))
            {
                if (agentEvent is DoneEvent doneEvent)
                {
                    done = doneEvent;
                }
            }

            conversationId = done.ConversationId;
        }

        using var scope2 = FunctionalTestSetup.ScopeFactory.CreateScope();
        var second = new ScriptedChatClient(null, new[] { "第二轮回答。" });
        AgentChatService service2 = CreateService(scope2, second);
        await foreach (AgentEvent _ in service2.ChatAsync(conversationId, "第二轮问题"))
        {
        }

        // 旧项目病根治点:历史必须真实进入模型上下文。
        IList<ChatMessage> seen = second.LastReceivedMessages.ShouldNotBeNull();
        seen.Count(message => message.Role == ChatRole.User).ShouldBe(2);
        seen.Any(message => message.Text.Contains("第一轮问题")).ShouldBeTrue();
        seen.Any(message => message.Text.Contains("第一轮回答")).ShouldBeTrue();
        seen[0].Role.ShouldBe(ChatRole.System);
    }

    internal sealed class FakeUser : Lingban.Application.Common.Interfaces.IUser
    {
        private readonly string _id;

        public FakeUser(string id = "test-user") => _id = id;

        public string? Id => _id;

        public List<string>? Roles => null;
    }

    private sealed class PinnedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public PinnedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>脚本化 LLM:首轮(可选)发一个工具调用,末轮吐 token。记录收到的消息供断言。</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly (string Name, string ArgsJson)? _firstTurnToolCall;
        private readonly string[] _finalTokens;
        private int _turn;

        public ScriptedChatClient((string, string)? firstTurnToolCall, string[] finalTokens)
        {
            _firstTurnToolCall = firstTurnToolCall;
            _finalTokens = finalTokens;
        }

        public IList<ChatMessage>? LastReceivedMessages { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastReceivedMessages = messages.ToList();
            _turn++;

            if (_turn == 1 && _firstTurnToolCall is (string name, string argsJson))
            {
                var arguments = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, object?>>(argsJson) ?? new Dictionary<string, object?>();
                yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("call-1", name, arguments)
                });
                yield break;
            }

            foreach (string token in _finalTokens)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, token);
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming only.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
