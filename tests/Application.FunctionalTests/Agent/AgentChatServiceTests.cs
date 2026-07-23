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

        var toolset = new AgentToolset(
            scope.ServiceProvider.GetRequiredService<MesToolExecutor>(),
            scope.ServiceProvider.GetRequiredService<Lingban.Application.Common.Interfaces.IUser>());

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
        // 写路径要求 ProductionReporter 角色(八审 #3)。
        await SeedFactoryAsync();
        string ownerId = await TestApp.RunAsUserAsync(
            "reporter@local", "Testing1234!", [Lingban.Domain.Constants.Roles.ProductionReporter]);
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

        // 确认 → 执行(属主与提议同一用户)。
        var confirm = new Lingban.Application.Actions.ConfirmPendingActionCommand(pending.ActionId, true);
        var confirmHandler = CreateConfirmHandler(scope, ownerId);
        var approved = await confirmHandler.Handle(confirm, CancellationToken.None);
        approved.Status.ShouldBe(Lingban.Domain.Enums.PendingActionStatus.Approved);

        (await db.WorkOrders.AsNoTracking().FirstAsync(o => o.Id == orderId))
            .CompletedQuantity.ShouldBe(5m);

        // 重复确认 → 409 语义的冲突(八审 #9)。
        await Should.ThrowAsync<Lingban.Application.Common.Exceptions.ConflictException>(
            () => confirmHandler.Handle(confirm, CancellationToken.None));

        // 非属主确认 → NotFound。
        var intruderHandler = CreateConfirmHandler(scope, "intruder");
        await Should.ThrowAsync<NotFoundException>(
            () => intruderHandler.Handle(new Lingban.Application.Actions.ConfirmPendingActionCommand(pending.ActionId, true), CancellationToken.None));
    }

    private static Lingban.Application.Actions.ConfirmPendingActionCommandHandler CreateConfirmHandler(
        IServiceScope scope, string userId) => new(
        scope.ServiceProvider.GetRequiredService<IApplicationDbContext>(),
        scope.ServiceProvider.GetRequiredService<Lingban.Application.Common.Interfaces.IGenealogySerializedExecutor>(),
        new FakeUser(userId));

    [Test]
    public async Task ConcurrentDoubleApproveExecutesExactlyOnce()
    {
        // 八审 #1(实锤复现过:双击双报工)回归钉:并发确认被闸门串行,后到者 409,报工只落一次。
        await SeedFactoryAsync();
        string ownerId = await TestApp.RunAsUserAsync(
            "concurrent@local", "Testing1234!", [Lingban.Domain.Constants.Roles.ProductionReporter]);
        int actionId = await SeedPendingActionAsync(ownerId, "WO-CHAT", 5m, 5m);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
            try
            {
                await CreateConfirmHandler(scope, ownerId).Handle(
                    new Lingban.Application.Actions.ConfirmPendingActionCommand(actionId, true),
                    CancellationToken.None);
                return (Success: true, Exception: (Exception?)null);
            }
            catch (Exception exception)
            {
                return (Success: false, Exception: exception);
            }
        })));

        outcomes.Count(outcome => outcome.Success).ShouldBe(1, "并发双确认必须恰好一次成功");
        outcomes.Single(outcome => !outcome.Success).Exception
            .ShouldBeOfType<Lingban.Application.Common.Exceptions.ConflictException>();

        using var checkScope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var db = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.WorkOrders.AsNoTracking().FirstAsync(o => o.Code == "WO-CHAT"))
            .CompletedQuantity.ShouldBe(5m, "双确认不得双报工");
    }

    [Test]
    public async Task ConcurrentApproveAndRejectStaysConsistent()
    {
        // 八审 #1 变体:批准与拒绝并发,最终状态与生产数据必须一致——
        // Approved ⇒ 报工已落;Rejected ⇒ 零改动;不存在"已报工但显示 Rejected"。
        await SeedFactoryAsync();
        string ownerId = await TestApp.RunAsUserAsync(
            "race@local", "Testing1234!", [Lingban.Domain.Constants.Roles.ProductionReporter]);
        int actionId = await SeedPendingActionAsync(ownerId, "WO-CHAT", 5m, 5m);

        var outcomes = await Task.WhenAll(new[] { true, false }.Select(approve => Task.Run(async () =>
        {
            using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
            try
            {
                await CreateConfirmHandler(scope, ownerId).Handle(
                    new Lingban.Application.Actions.ConfirmPendingActionCommand(actionId, approve),
                    CancellationToken.None);
                return true;
            }
            catch (Lingban.Application.Common.Exceptions.ConflictException)
            {
                return false;
            }
        })));

        outcomes.Count(success => success).ShouldBe(1, "批准/拒绝并发必须恰好一方生效");

        using var checkScope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var db = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var action = await db.PendingActions.AsNoTracking().FirstAsync(a => a.Id == actionId);
        decimal completed = (await db.WorkOrders.AsNoTracking().FirstAsync(o => o.Code == "WO-CHAT")).CompletedQuantity;
        if (action.Status == Lingban.Domain.Enums.PendingActionStatus.Approved)
        {
            completed.ShouldBe(5m);
        }
        else
        {
            action.Status.ShouldBe(Lingban.Domain.Enums.PendingActionStatus.Rejected);
            completed.ShouldBe(0m);
        }
    }

    [Test]
    public async Task MesReaderCannotConfirmProductionWrite()
    {
        // 八审 #3 回归钉:只读角色走命令层也被 [Authorize] 拦下(端点策略之外的第二道闸)。
        await SeedFactoryAsync();
        string ownerId = await TestApp.RunAsUserAsync(
            "writer@local", "Testing1234!", [Lingban.Domain.Constants.Roles.ProductionReporter]);
        int actionId = await SeedPendingActionAsync(ownerId, "WO-CHAT", 5m, 5m);

        await TestApp.RunAsUserAsync("reader@local", "Testing1234!", [Lingban.Domain.Constants.Roles.MesReader]);
        await Should.ThrowAsync<Lingban.Application.Common.Exceptions.ForbiddenAccessException>(
            () => TestApp.SendAsync(new Lingban.Application.Actions.ConfirmPendingActionCommand(actionId, true)));
    }

    private static async Task<int> SeedPendingActionAsync(
        string ownerId, string workOrderCode, decimal completed, decimal qualified)
    {
        var action = new Lingban.Domain.Entities.Actions.PendingAction
        {
            OwnerUserId = ownerId,
            ActionType = "ReportProduction",
            Summary = $"报工 {workOrderCode}:完工 {completed}",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new Lingban.Application.Actions.ReportProductionProposal(workOrderCode, completed, qualified, 0m, 0m),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
        };
        await TestApp.AddAsync(action);
        return action.Id;
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
    public async Task DuplicateKeyWithNullConversationIdIsRejectedWithoutNewConversation()
    {
        // 八审 #2 回归钉:首次请求响应丢失后,客户端带 null conversationId 重放同一键——
        // 预检按属主而非会话,必须拦下,且不得多出一个会话。
        await SeedFactoryAsync();
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        AgentChatService service = CreateService(scope, new ScriptedChatClient(null, new[] { "答一。" }));
        var key = Guid.NewGuid();
        await foreach (AgentEvent _ in service.ChatAsync(null, "问题一", key))
        {
        }

        int conversationsAfterFirst = await TestApp.CountAsync<Conversation>();

        using var scope2 = FunctionalTestSetup.ScopeFactory.CreateScope();
        AgentChatService retry = CreateService(scope2, new ScriptedChatClient(null, new[] { "不该执行。" }));
        var events = new List<AgentEvent>();
        await foreach (AgentEvent e in retry.ChatAsync(null, "问题一", key))
        {
            events.Add(e);
        }

        events.OfType<ErrorEvent>().ShouldHaveSingleItem().Message.ShouldContain("DUPLICATE");
        events.OfType<DoneEvent>().ShouldBeEmpty();
        (await TestApp.CountAsync<Conversation>()).ShouldBe(conversationsAfterFirst, "重放不得新建会话");
    }

    [Test]
    public async Task ConcurrentFirstRequestsWithSameKeyLeaveNoOrphanConversation()
    {
        // 九审 #4 回归钉:并发同键首请求,输家整体回滚——恰一个会话、一条用户消息、一次完成。
        await SeedFactoryAsync();
        var key = Guid.NewGuid();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
            AgentChatService service = CreateService(scope, new ScriptedChatClient(null, new[] { "答。" }));
            var events = new List<AgentEvent>();
            await foreach (AgentEvent e in service.ChatAsync(null, "并发首问", key))
            {
                events.Add(e);
            }

            return events;
        })));

        int doneCount = outcomes.Sum(events => events.OfType<DoneEvent>().Count());
        int duplicateCount = outcomes.Sum(events =>
            events.OfType<ErrorEvent>().Count(error => error.Message.Contains("DUPLICATE")));
        doneCount.ShouldBe(1, "恰好一方完成回合");
        duplicateCount.ShouldBe(1, "另一方必须收到重复拒绝");
        (await TestApp.CountAsync<Conversation>()).ShouldBe(1, "输家不得留下孤儿会话");

        using var checkScope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var db = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.ConversationMessages.CountAsync(message => message.ClientMessageId == key))
            .ShouldBe(1, "同键用户消息恰一条");
    }

    [Test]
    public void OnlyIdempotencyIndexViolationCountsAsDuplicate()
    {
        // 九审 #2 回归钉:连接中断/外键冲突/其他约束不得伪装成 DUPLICATE_MESSAGE。
        static Microsoft.EntityFrameworkCore.DbUpdateException Wrap(Exception inner) => new("save failed", inner);

        AgentChatService.IsIdempotencyKeyViolation(Wrap(new Npgsql.PostgresException(
            "duplicate key", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation,
            constraintName: AgentChatService.IdempotencyIndexName))).ShouldBeTrue();

        // 同为 23505 但别的唯一索引 → 不是幂等命中。
        AgentChatService.IsIdempotencyKeyViolation(Wrap(new Npgsql.PostgresException(
            "duplicate key", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation,
            constraintName: "IX_Something_Else"))).ShouldBeFalse();

        // 外键冲突(如会话被并发删除)→ 必须继续抛出。
        AgentChatService.IsIdempotencyKeyViolation(Wrap(new Npgsql.PostgresException(
            "fk violation", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.ForeignKeyViolation,
            constraintName: AgentChatService.IdempotencyIndexName))).ShouldBeFalse();

        // 非 Postgres 异常(连接中断等包装)→ 不是幂等命中。
        AgentChatService.IsIdempotencyKeyViolation(Wrap(new TimeoutException())).ShouldBeFalse();
    }

    [Test]
    public async Task TamperedProposalDtoIsCaughtByIndependentVerification()
    {
        // 九审 #3 回归钉:校验对象是模型看到的 DTO——映射层把 5 写成 500 必须被独立 SQL 抓出。
        await SeedFactoryAsync();
        string ownerId = await TestApp.RunAsUserAsync(
            "verify-dto@local", "Testing1234!", [Lingban.Domain.Constants.Roles.ProductionReporter]);

        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var command = new Lingban.Application.Actions.ProposeReportProductionCommand(
            new Lingban.Application.Actions.ReportProductionProposal("WO-CHAT", 5m, 5m, 0m, 0m), null);
        var action = await sender.Send(command);

        var verifier = scope.ServiceProvider.GetRequiredService<IFactVerifier>();
        var honest = new Lingban.Application.Actions.ReportProductionProposalDto(
            action.Id, action.ActionType, action.Summary, action.Status.ToString(),
            "WO-CHAT", 5m, 5m, 0m, 0m, action.PayloadJson);
        (await verifier.VerifyAsync(ToolNames.ReportProduction, command, honest))
            .Status.ShouldBe(VerificationStatus.Verified);

        (await verifier.VerifyAsync(ToolNames.ReportProduction, command, honest with { Completed = 500m }))
            .Status.ShouldBe(VerificationStatus.Discrepancy, "DTO 数量失真必须被抓");
        (await verifier.VerifyAsync(ToolNames.ReportProduction, command, honest with { WorkOrderCode = "WO-FAKE" }))
            .Status.ShouldBe(VerificationStatus.Discrepancy, "DTO 工单号失真必须被抓");
        (await verifier.VerifyAsync(ToolNames.ReportProduction, command, honest with { Status = "Approved" }))
            .Status.ShouldBe(VerificationStatus.Discrepancy, "DTO 状态失真必须被抓");
        (await verifier.VerifyAsync(ToolNames.ReportProduction, command, honest with { Summary = "报工 WO-CHAT:完工 500" }))
            .Status.ShouldBe(VerificationStatus.Discrepancy, "DTO 摘要失真必须被抓");
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
            new AgentToolset(
                scope2.ServiceProvider.GetRequiredService<MesToolExecutor>(),
                scope2.ServiceProvider.GetRequiredService<Lingban.Application.Common.Interfaces.IUser>()),
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
