using Lingban.Application.Materials.Queries.TraceLot;
using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lingban.Application.FunctionalTests.Materials;

/// <summary>
/// M1 验收①②:批次谱系正向/反向追溯(真实 PostgreSQL 上执行)。
/// 场景:来料(裸板/IC)→ WO-T1 产出 PCBA 批次 → WO-T2 产出整机批次。
/// 全部领域操作在同一 DbContext 内完成,库存扣减与谱系边原子入库。
/// </summary>
public class LotTraceabilityTests : TestBase
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    private int _pcbLotId;
    private int _icLotId;
    private int _fgLotId;

    private async Task SeedGenealogyAsync()
    {
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            var pcb = new Product { Code = "RM-PCB", Name = "裸板" };
            var ic = new Product { Code = "RM-IC", Name = "主控 IC" };
            var pcba = new Product { Code = "SFG-PCBA", Name = "PCBA" };
            var fg = new Product { Code = "FG-CTRL", Name = "控制器" };
            var line = new ProductionLine { Code = "L1", Name = "一线" };
            var station = new Workstation { Code = "S1", Name = "工位一", ProductionLine = line };
            context.AddRange(pcb, ic, pcba, fg, line, station);
            await context.SaveChangesAsync();

            var pcbLot = MaterialLot.Create("L-PCB-A", pcb.Id, 100m, "PCS", T0, "SUP-PCB-1");
            var icLot = MaterialLot.Create("L-IC-A", ic.Id, 100m, "PCS", T0, "SUP-IC-1");
            context.AddRange(pcbLot, icLot);
            await context.SaveChangesAsync();

            var order1 = WorkOrder.Create("WO-T1", pcba.Id, line.Id, 50m, "PCS");
            order1.Release();
            order1.Start(T0.AddHours(1));
            order1.RecordConsumption(pcbLot, 50m, station.Id, T0.AddHours(2));
            order1.RecordConsumption(icLot, 50m, station.Id, T0.AddHours(2));
            var pcbaLot = order1.ProduceLot("L-PCBA-A", 50m, T0.AddHours(8));
            order1.Complete(T0.AddHours(9));
            context.Add(order1);
            await context.SaveChangesAsync();

            var order2 = WorkOrder.Create("WO-T2", fg.Id, line.Id, 30m, "PCS");
            order2.Release();
            order2.Start(T0.AddDays(1));
            order2.RecordConsumption(pcbaLot, 30m, station.Id, T0.AddDays(1).AddHours(1));
            var fgLot = order2.ProduceLot("L-FG-A", 30m, T0.AddDays(1).AddHours(8));
            order2.Complete(T0.AddDays(1).AddHours(9));
            context.Add(order2);
            await context.SaveChangesAsync();

            _pcbLotId = pcbLot.Id;
            _icLotId = icLot.Id;
            _fgLotId = fgLot.Id;
        });
    }

    [Test]
    public async Task ForwardTraceFindsAllAffectedDownstreamLots()
    {
        // 召回场景:供应商裸板批次有问题,必须找到它流入的每一个下游批次。
        await SeedGenealogyAsync();

        LotTraceNode root = await TestApp.SendAsync(new TraceLotForwardQuery(_pcbLotId));

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

        LotTraceNode root = await TestApp.SendAsync(new TraceLotBackwardQuery(_fgLotId));

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
    public async Task StockDebitIsPersistedAtomicallyWithGenealogyEdges()
    {
        // Codex 审查发现#10 的回归钉:扣账必须与连边一起落库,不能只证明"能连边"。
        await SeedGenealogyAsync();

        MaterialLot? pcbLot = await TestApp.FindAsync<MaterialLot>(_pcbLotId);
        pcbLot.ShouldNotBeNull();
        pcbLot.QuantityOnHand.ShouldBe(50m);

        MaterialLot? icLot = await TestApp.FindAsync<MaterialLot>(_icLotId);
        icLot.ShouldNotBeNull();
        icLot.QuantityOnHand.ShouldBe(50m);
    }

    [Test]
    public async Task DiamondGenealogyShowsSharedLotOnEveryBranch()
    {
        // Codex 审查发现#2 的回归钉:菱形谱系(R 同时流入 A、B,A+B 组成 C)
        // 反向追溯 C 时,R 必须在 A、B 两条支路都如实出现。
        int rLotId = 0, cLotId = 0;
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            var raw = new Product { Code = "RM-R", Name = "共用原料" };
            var subA = new Product { Code = "SFG-A", Name = "子件A" };
            var subB = new Product { Code = "SFG-B", Name = "子件B" };
            var final = new Product { Code = "FG-C", Name = "成品C" };
            var line = new ProductionLine { Code = "L9", Name = "九线" };
            var station = new Workstation { Code = "S9", Name = "工位九", ProductionLine = line };
            context.AddRange(raw, subA, subB, final, line, station);
            await context.SaveChangesAsync();

            var rLot = MaterialLot.Create("L-R", raw.Id, 100m, "PCS", T0, "SUP-R");
            context.Add(rLot);
            await context.SaveChangesAsync();

            var orderA = WorkOrder.Create("WO-A", subA.Id, line.Id, 10m, "PCS");
            orderA.Release();
            orderA.Start(T0);
            orderA.RecordConsumption(rLot, 10m, station.Id, T0);
            var aLot = orderA.ProduceLot("L-A", 10m, T0.AddHours(1));
            orderA.Complete(T0.AddHours(2));
            context.Add(orderA);

            var orderB = WorkOrder.Create("WO-B", subB.Id, line.Id, 10m, "PCS");
            orderB.Release();
            orderB.Start(T0);
            orderB.RecordConsumption(rLot, 10m, station.Id, T0);
            var bLot = orderB.ProduceLot("L-B", 10m, T0.AddHours(1));
            orderB.Complete(T0.AddHours(2));
            context.Add(orderB);
            await context.SaveChangesAsync();

            var orderC = WorkOrder.Create("WO-C", final.Id, line.Id, 5m, "PCS");
            orderC.Release();
            orderC.Start(T0.AddHours(3));
            orderC.RecordConsumption(aLot, 5m, station.Id, T0.AddHours(3));
            orderC.RecordConsumption(bLot, 5m, station.Id, T0.AddHours(3));
            var cLot = orderC.ProduceLot("L-C", 5m, T0.AddHours(4));
            orderC.Complete(T0.AddHours(5));
            context.Add(orderC);
            await context.SaveChangesAsync();

            rLotId = rLot.Id;
            cLotId = cLot.Id;
        });

        LotTraceNode backward = await TestApp.SendAsync(new TraceLotBackwardQuery(cLotId));
        backward.Children.Select(node => node.LotNumber).ShouldBe(new[] { "L-A", "L-B" }, ignoreOrder: true);
        foreach (LotTraceNode branch in backward.Children)
        {
            branch.Children.Count.ShouldBe(1, $"支路 {branch.LotNumber} 必须包含共用原料批次");
            branch.Children[0].LotNumber.ShouldBe("L-R");
        }

        LotTraceNode forward = await TestApp.SendAsync(new TraceLotForwardQuery(rLotId));
        forward.Children.Select(node => node.LotNumber).ShouldBe(new[] { "L-A", "L-B" }, ignoreOrder: true);
        foreach (LotTraceNode branch in forward.Children)
        {
            branch.Children.Count.ShouldBe(1);
            branch.Children[0].LotNumber.ShouldBe("L-C");
        }
    }

    [Test]
    public async Task TraceUnknownLotThrowsNotFound()
    {
        await Should.ThrowAsync<NotFoundException>(
            () => TestApp.SendAsync(new TraceLotForwardQuery(999999)));
    }
}
