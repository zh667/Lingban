using Lingban.Application.Materials.Queries.TraceLot;
using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;

namespace Lingban.Application.FunctionalTests.Materials;

/// <summary>
/// M1 验收①②:批次谱系正向/反向追溯(真实 PostgreSQL 上执行)。
/// 场景:来料(裸板/IC)→ WO-T1 产出 PCBA 批次 → WO-T2 产出整机批次。
/// </summary>
public class LotTraceabilityTests : TestBase
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    private MaterialLot _pcbLot = null!;
    private MaterialLot _icLot = null!;
    private MaterialLot _pcbaLot = null!;
    private MaterialLot _fgLot = null!;

    private async Task SeedGenealogyAsync()
    {
        var pcb = new Product { Code = "RM-PCB", Name = "裸板" };
        var ic = new Product { Code = "RM-IC", Name = "主控 IC" };
        var pcba = new Product { Code = "SFG-PCBA", Name = "PCBA" };
        var fg = new Product { Code = "FG-CTRL", Name = "控制器" };
        await TestApp.AddAsync(pcb);
        await TestApp.AddAsync(ic);
        await TestApp.AddAsync(pcba);
        await TestApp.AddAsync(fg);

        var line = new ProductionLine { Code = "L1", Name = "一线" };
        await TestApp.AddAsync(line);
        var station = new Workstation { Code = "S1", Name = "工位一", ProductionLineId = line.Id };
        await TestApp.AddAsync(station);

        _pcbLot = MaterialLot.Create("L-PCB-A", pcb.Id, 100m, "PCS", T0, "SUP-PCB-1");
        _icLot = MaterialLot.Create("L-IC-A", ic.Id, 100m, "PCS", T0, "SUP-IC-1");
        await TestApp.AddAsync(_pcbLot);
        await TestApp.AddAsync(_icLot);

        var order1 = WorkOrder.Create("WO-T1", pcba.Id, line.Id, 50m, "PCS");
        order1.Release();
        order1.Start(T0.AddHours(1));
        order1.RecordConsumption(_pcbLot, 50m, station.Id, T0.AddHours(2));
        order1.RecordConsumption(_icLot, 50m, station.Id, T0.AddHours(2));
        _pcbaLot = order1.ProduceLot("L-PCBA-A", 50m, T0.AddHours(8));
        order1.Complete(T0.AddHours(9));
        await TestApp.AttachGraphAsync(order1);

        var order2 = WorkOrder.Create("WO-T2", fg.Id, line.Id, 30m, "PCS");
        order2.Release();
        order2.Start(T0.AddDays(1));
        order2.RecordConsumption(_pcbaLot, 30m, station.Id, T0.AddDays(1).AddHours(1));
        _fgLot = order2.ProduceLot("L-FG-A", 30m, T0.AddDays(1).AddHours(8));
        order2.Complete(T0.AddDays(1).AddHours(9));
        await TestApp.AttachGraphAsync(order2);
    }

    [Test]
    public async Task ForwardTraceFindsAllAffectedDownstreamLots()
    {
        // 召回场景:供应商裸板批次有问题,必须找到它流入的每一个下游批次。
        await SeedGenealogyAsync();

        LotTraceNode root = await TestApp.SendAsync(new TraceLotForwardQuery(_pcbLot.Id));

        root.LotNumber.ShouldBe("L-PCB-A");
        root.Children.Count.ShouldBe(1);

        LotTraceNode pcbaNode = root.Children[0];
        pcbaNode.LotNumber.ShouldBe("L-PCBA-A");
        pcbaNode.ViaWorkOrderCode.ShouldBe("WO-T1");

        pcbaNode.Children.Count.ShouldBe(1);
        LotTraceNode fgNode = pcbaNode.Children[0];
        fgNode.LotNumber.ShouldBe("L-FG-A");
        fgNode.ViaWorkOrderCode.ShouldBe("WO-T2");
        fgNode.Children.ShouldBeEmpty();
    }

    [Test]
    public async Task BackwardTraceFindsAllSourceLotsDownToSuppliers()
    {
        // 客诉场景:这台成品用了哪些料?必须能一路挖到供应商批号。
        await SeedGenealogyAsync();

        LotTraceNode root = await TestApp.SendAsync(new TraceLotBackwardQuery(_fgLot.Id));

        root.LotNumber.ShouldBe("L-FG-A");
        root.Children.Count.ShouldBe(1);

        LotTraceNode pcbaNode = root.Children[0];
        pcbaNode.LotNumber.ShouldBe("L-PCBA-A");
        pcbaNode.ViaWorkOrderCode.ShouldBe("WO-T2");

        pcbaNode.Children.Select(node => node.LotNumber)
            .ShouldBe(new[] { "L-PCB-A", "L-IC-A" }, ignoreOrder: true);
        pcbaNode.Children.ShouldAllBe(node => node.SupplierLotNumber != null);
        pcbaNode.Children.ShouldAllBe(node => node.Children.Count == 0);
    }

    [Test]
    public async Task TraceUnknownLotThrowsNotFound()
    {
        await Should.ThrowAsync<NotFoundException>(
            () => TestApp.SendAsync(new TraceLotForwardQuery(999999)));
    }
}
