using Lingban.Application.Common.Interfaces;
using Lingban.Application.WorkOrders.Commands;
using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;
using Lingban.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Lingban.Application.FunctionalTests.WorkOrders;

/// <summary>
/// M2 写路径 + 三条 M1 审查债的回归钉:
/// 幂等键(#8)、跨工单环检测(#3 余下)、完工前置校验(#7 余下)。
/// </summary>
public class WorkOrderWritePathTests : TestBase
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    private int _productId;
    private int _rawProductId;
    private int _lineId;
    private int _stationId;
    private int _rawLotId;

    private async Task SeedMasterDataAsync()
    {
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            var raw = new Product { Code = "RM-X", Name = "原料X" };
            var fg = new Product { Code = "FG-X", Name = "成品X" };
            var line = new ProductionLine { Code = "L2", Name = "二线" };
            var station = new Workstation { Code = "S2", Name = "工位二", ProductionLine = line };
            context.AddRange(raw, fg, line, station);
            await context.SaveChangesAsync();

            var rawLot = MaterialLot.Create("L-RAW-X", raw.Id, 100m, "PCS", T0, "SUP-X");
            context.Add(rawLot);
            await context.SaveChangesAsync();

            _rawProductId = raw.Id;
            _productId = fg.Id;
            _lineId = line.Id;
            _stationId = station.Id;
            _rawLotId = rawLot.Id;
        });
    }

    private async Task<int> CreateStartedOrderAsync(string code, int? productId = null)
    {
        int orderId = await TestApp.SendAsync(new CreateWorkOrderCommand
        {
            Code = code,
            ProductId = productId ?? _productId,
            ProductionLineId = _lineId,
            PlannedQuantity = 10m,
            UnitOfMeasure = "PCS"
        });
        await TestApp.SendAsync(new ReleaseWorkOrderCommand(orderId));
        await TestApp.SendAsync(new StartWorkOrderCommand(orderId));
        return orderId;
    }

    [Test]
    public async Task ReplayedConsumptionEventDebitsStockExactlyOnce()
    {
        await SeedMasterDataAsync();
        int orderId = await CreateStartedOrderAsync("WO-IDEM");
        var eventId = Guid.NewGuid();

        var command = new RecordConsumptionCommand
        {
            WorkOrderId = orderId,
            MaterialLotId = _rawLotId,
            Quantity = 30m,
            WorkstationId = _stationId,
            EventId = eventId
        };

        int firstId = await TestApp.SendAsync(command);
        int replayId = await TestApp.SendAsync(command);

        replayId.ShouldBe(firstId);

        MaterialLot? lot = await TestApp.FindAsync<MaterialLot>(_rawLotId);
        lot.ShouldNotBeNull();
        lot.QuantityOnHand.ShouldBe(70m);

        (await TestApp.CountAsync<MaterialConsumption>()).ShouldBe(1);
    }

    [Test]
    public async Task CrossOrderGenealogyCycleIsRejected()
    {
        // WA(进行中)产出 LA;WB 消耗 LA 产出 LB;WA 再想消耗 LB → 环,必须拒绝。
        await SeedMasterDataAsync();

        int orderAId = await CreateStartedOrderAsync("WO-CYC-A");
        await TestApp.SendAsync(new RecordConsumptionCommand
        {
            WorkOrderId = orderAId,
            MaterialLotId = _rawLotId,
            Quantity = 10m,
            WorkstationId = _stationId,
            EventId = Guid.NewGuid()
        });
        int lotAId = await TestApp.SendAsync(new ProduceLotCommand
        {
            WorkOrderId = orderAId,
            LotNumber = "L-CYC-A",
            Quantity = 10m
        });

        int orderBId = await CreateStartedOrderAsync("WO-CYC-B", _rawProductId);
        await TestApp.SendAsync(new RecordConsumptionCommand
        {
            WorkOrderId = orderBId,
            MaterialLotId = lotAId,
            Quantity = 5m,
            WorkstationId = _stationId,
            EventId = Guid.NewGuid()
        });
        int lotBId = await TestApp.SendAsync(new ProduceLotCommand
        {
            WorkOrderId = orderBId,
            LotNumber = "L-CYC-B",
            Quantity = 5m
        });

        var cycleAttempt = new RecordConsumptionCommand
        {
            WorkOrderId = orderAId,
            MaterialLotId = lotBId,
            Quantity = 1m,
            WorkstationId = _stationId,
            EventId = Guid.NewGuid()
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => TestApp.SendAsync(cycleAttempt));
        exception.Message.ShouldContain("cycle");
    }

    [Test]
    public async Task ReusedEventIdWithDifferentPayloadIsRejected()
    {
        // Codex 二审 #1:同键不同 payload 不许被静默当作成功。
        await SeedMasterDataAsync();
        int orderId = await CreateStartedOrderAsync("WO-FPRINT");
        var eventId = Guid.NewGuid();

        await TestApp.SendAsync(new RecordConsumptionCommand
        {
            WorkOrderId = orderId,
            MaterialLotId = _rawLotId,
            Quantity = 10m,
            WorkstationId = _stationId,
            EventId = eventId
        });

        var mismatched = new RecordConsumptionCommand
        {
            WorkOrderId = orderId,
            MaterialLotId = _rawLotId,
            Quantity = 20m,
            WorkstationId = _stationId,
            EventId = eventId
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => TestApp.SendAsync(mismatched));
        exception.Message.ShouldContain("Idempotency");
    }

    [Test]
    public async Task ConcurrentReverseEdgesCannotFormCycle()
    {
        // Codex 二审 #2:互为反向的两条边并发写入,谱系闸门必须串行化,
        // 恰好一边成功、另一边被环检测拒绝——事后谱系必须无环。
        await SeedMasterDataAsync();

        int orderAId = await CreateStartedOrderAsync("WO-RACE-A");
        await TestApp.SendAsync(new RecordConsumptionCommand
        {
            WorkOrderId = orderAId,
            MaterialLotId = _rawLotId,
            Quantity = 5m,
            WorkstationId = _stationId,
            EventId = Guid.NewGuid()
        });
        int lotAId = await TestApp.SendAsync(new ProduceLotCommand
        {
            WorkOrderId = orderAId,
            LotNumber = "L-RACE-A",
            Quantity = 5m
        });

        int orderBId = await CreateStartedOrderAsync("WO-RACE-B", _rawProductId);
        await TestApp.SendAsync(new RecordConsumptionCommand
        {
            WorkOrderId = orderBId,
            MaterialLotId = _rawLotId,
            Quantity = 5m,
            WorkstationId = _stationId,
            EventId = Guid.NewGuid()
        });
        int lotBId = await TestApp.SendAsync(new ProduceLotCommand
        {
            WorkOrderId = orderBId,
            LotNumber = "L-RACE-B",
            Quantity = 5m
        });

        // 并发:A 吃 LB,B 吃 LA。
        Task<int> aEatsB = TestApp.SendAsync(new RecordConsumptionCommand
        {
            WorkOrderId = orderAId,
            MaterialLotId = lotBId,
            Quantity = 1m,
            WorkstationId = _stationId,
            EventId = Guid.NewGuid()
        });
        Task<int> bEatsA = TestApp.SendAsync(new RecordConsumptionCommand
        {
            WorkOrderId = orderBId,
            MaterialLotId = lotAId,
            Quantity = 1m,
            WorkstationId = _stationId,
            EventId = Guid.NewGuid()
        });

        var failures = new List<Exception>();
        try { await aEatsB; } catch (Exception exception) { failures.Add(exception); }
        try { await bEatsA; } catch (Exception exception) { failures.Add(exception); }

        failures.Count.ShouldBe(1, "两条反向边必须恰好一条被拒绝");
        failures[0].ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("cycle");
    }

    [Test]
    public async Task GenealogyExecutorIsMutuallyExclusivePerTenant()
    {
        // Codex 三审 #6:确定性互斥测试——T1 持锁期间 T2 不得进入。
        using var scope1 = FunctionalTestSetup.ScopeFactory.CreateScope();
        using var scope2 = FunctionalTestSetup.ScopeFactory.CreateScope();
        var executor1 = scope1.ServiceProvider.GetRequiredService<IGenealogySerializedExecutor>();
        var executor2 = scope2.ServiceProvider.GetRequiredService<IGenealogySerializedExecutor>();

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        bool secondEntered = false;

        Task first = executor1.ExecuteAsync<object?>(async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return null;
        }, CancellationToken.None);

        await firstEntered.Task;

        Task second = executor2.ExecuteAsync<object?>(_ =>
        {
            secondEntered = true;
            return Task.FromResult<object?>(null);
        }, CancellationToken.None);

        await Task.Delay(500);
        secondEntered.ShouldBeFalse("T1 持锁期间 T2 不得进入闸门");

        releaseFirst.SetResult();
        await first;
        await second;
        secondEntered.ShouldBeTrue();
    }

    [Test]
    public async Task ConcurrentCompleteAndReportKeepInvariant()
    {
        // Codex 三审 #1:完工校验与改报工量并发,闸门串行化后
        // 不变量必须成立:Completed 状态 ⇒ 产出总量 == 报工完工量。
        await SeedMasterDataAsync();
        int orderId = await CreateStartedOrderAsync("WO-CVR");

        await TestApp.SendAsync(new RecordConsumptionCommand
        {
            WorkOrderId = orderId,
            MaterialLotId = _rawLotId,
            Quantity = 10m,
            WorkstationId = _stationId,
            EventId = Guid.NewGuid()
        });
        await TestApp.SendAsync(new ProduceLotCommand
        {
            WorkOrderId = orderId,
            LotNumber = "L-CVR",
            Quantity = 10m
        });
        await TestApp.SendAsync(new ReportProductionCommand
        {
            WorkOrderId = orderId,
            Completed = 10m,
            Qualified = 10m
        });

        // 并发:完工 vs 追加报工(+1 会破坏 产出=报工)。
        Task complete = TestApp.SendAsync(new CompleteWorkOrderCommand(orderId));
        Task extraReport = TestApp.SendAsync(new ReportProductionCommand
        {
            WorkOrderId = orderId,
            Completed = 1m,
            Qualified = 1m
        });

        var failures = new List<Exception>();
        try { await complete; } catch (Exception exception) { failures.Add(exception); }
        try { await extraReport; } catch (Exception exception) { failures.Add(exception); }

        var order = await TestApp.FindAsync<WorkOrder>(orderId);
        order.ShouldNotBeNull();

        if (order.Status == WorkOrderStatus.Completed)
        {
            // 完工先赢:追加报工必须被拒(非 InProgress)。
            order.CompletedQuantity.ShouldBe(10m);
            failures.Count.ShouldBe(1);
        }
        else
        {
            // 报工先赢:数量变 11,完工校验必须拒绝(产出 10 ≠ 报工 11)。
            order.CompletedQuantity.ShouldBe(11m);
            failures.Count.ShouldBe(1);
            failures[0].Message.ShouldContain("does not match");
        }
    }

    [Test]
    public async Task CompleteRequiresConsumptionOutputAndMatchingQuantities()
    {
        await SeedMasterDataAsync();
        int orderId = await CreateStartedOrderAsync("WO-COMP");

        // 无消耗 → 拒绝。
        var noConsumption = await Should.ThrowAsync<InvalidOperationException>(
            () => TestApp.SendAsync(new CompleteWorkOrderCommand(orderId)));
        noConsumption.Message.ShouldContain("no material consumption");

        await TestApp.SendAsync(new RecordConsumptionCommand
        {
            WorkOrderId = orderId,
            MaterialLotId = _rawLotId,
            Quantity = 10m,
            WorkstationId = _stationId,
            EventId = Guid.NewGuid()
        });

        // 有消耗无产出 → 拒绝。
        var noOutput = await Should.ThrowAsync<InvalidOperationException>(
            () => TestApp.SendAsync(new CompleteWorkOrderCommand(orderId)));
        noOutput.Message.ShouldContain("no output lot");

        await TestApp.SendAsync(new ProduceLotCommand
        {
            WorkOrderId = orderId,
            LotNumber = "L-COMP",
            Quantity = 10m
        });

        // 产出 10 但报工 0 → 数量不一致,拒绝。
        var mismatch = await Should.ThrowAsync<InvalidOperationException>(
            () => TestApp.SendAsync(new CompleteWorkOrderCommand(orderId)));
        mismatch.Message.ShouldContain("does not match");

        await TestApp.SendAsync(new ReportProductionCommand
        {
            WorkOrderId = orderId,
            Completed = 10m,
            Qualified = 10m
        });

        await TestApp.SendAsync(new CompleteWorkOrderCommand(orderId));

        var order = await TestApp.FindAsync<Domain.Entities.Production.WorkOrder>(orderId);
        order.ShouldNotBeNull();
        order.Status.ShouldBe(WorkOrderStatus.Completed);
    }
}
