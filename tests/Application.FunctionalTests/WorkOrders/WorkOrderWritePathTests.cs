using Lingban.Application.WorkOrders.Commands;
using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;
using Lingban.Domain.Enums;

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
