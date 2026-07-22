using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;
using Lingban.Domain.Enums;
using Lingban.Domain.Exceptions;
using NUnit.Framework;
using Shouldly;

namespace Lingban.Domain.UnitTests.Production;

public class WorkOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 22, 1, 0, 0, TimeSpan.Zero);

    private static WorkOrder NewOrder() => WorkOrder.Create("WO-001", 1, 1, 100m, "PCS");

    private static WorkOrder InProgressOrder()
    {
        var order = NewOrder();
        order.Release();
        order.Start(T0);
        return order;
    }

    [Test]
    public void FullLifecycleTransitionsSucceed()
    {
        var order = NewOrder();
        order.Status.ShouldBe(WorkOrderStatus.Draft);

        order.Release();
        order.Status.ShouldBe(WorkOrderStatus.Released);

        order.Start(T0);
        order.Status.ShouldBe(WorkOrderStatus.InProgress);
        order.ActualStartUtc.ShouldBe(T0);

        order.Complete(T0.AddHours(8));
        order.Status.ShouldBe(WorkOrderStatus.Completed);
        order.ActualEndUtc.ShouldBe(T0.AddHours(8));
    }

    [Test]
    public void IllegalTransitionsThrow()
    {
        // 验收:非法状态转换必须抛领域异常,Completed 是终态。
        Should.Throw<InvalidWorkOrderTransitionException>(() => NewOrder().Start(T0));
        Should.Throw<InvalidWorkOrderTransitionException>(() => NewOrder().Complete(T0));

        var completed = InProgressOrder();
        completed.Complete(T0);
        Should.Throw<InvalidWorkOrderTransitionException>(() => completed.Release());
        Should.Throw<InvalidWorkOrderTransitionException>(() => completed.Start(T0));
        Should.Throw<InvalidWorkOrderTransitionException>(() => completed.Cancel());

        var released = NewOrder();
        released.Release();
        Should.Throw<InvalidWorkOrderTransitionException>(() => released.Release());
    }

    [Test]
    public void CancelOnlyAllowedBeforeStart()
    {
        var draft = NewOrder();
        draft.Cancel();
        draft.Status.ShouldBe(WorkOrderStatus.Cancelled);

        Should.Throw<InvalidWorkOrderTransitionException>(() => InProgressOrder().Cancel());
    }

    [Test]
    public void OverproductionIsExposedNotClamped()
    {
        var order = InProgressOrder();

        order.ReportProduction(completed: 120m, qualified: 110m, scrap: 6m, rework: 4m);

        order.CompletedQuantity.ShouldBe(120m);
        order.IsOverproduced.ShouldBeTrue();
        order.ScrapQuantity.ShouldBe(6m);
        order.ReworkQuantity.ShouldBe(4m);
    }

    [Test]
    public void ReportingRejectsNegativeAndWrongState()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => InProgressOrder().ReportProduction(-1m, 0m, 0m, 0m));
        Should.Throw<InvalidOperationException>(
            () => NewOrder().ReportProduction(1m, 1m, 0m, 0m));
    }

    [Test]
    public void ConsumptionDebitsLotAndBuildsGenealogyEdge()
    {
        var order = InProgressOrder();
        var lot = MaterialLot.Create("L-RM-01", 2, 50m, "PCS", T0, supplierLotNumber: "SUP-9");

        var consumption = order.RecordConsumption(lot, 20m, workstationId: 7, recordedAtUtc: T0.AddHours(1));

        lot.QuantityOnHand.ShouldBe(30m);
        consumption.Quantity.ShouldBe(20m);
        consumption.WorkOrder.ShouldBeSameAs(order);
        consumption.MaterialLot.ShouldBeSameAs(lot);
        order.Consumptions.Count.ShouldBe(1);
    }

    [Test]
    public void ConsumptionGuards()
    {
        var order = InProgressOrder();
        var lot = MaterialLot.Create("L-RM-02", 2, 10m, "PCS", T0);

        // 超扣、块料、非进行中工单都必须拒绝。
        Should.Throw<InvalidOperationException>(
            () => order.RecordConsumption(lot, 11m, 7, T0));

        lot.Block();
        Should.Throw<InvalidOperationException>(
            () => order.RecordConsumption(lot, 1m, 7, T0));

        var draft = NewOrder();
        lot.Unblock();
        Should.Throw<InvalidOperationException>(
            () => draft.RecordConsumption(lot, 1m, 7, T0));
    }

    [Test]
    public void SelfConsumptionIsRejected()
    {
        // Codex 审查发现#3 的回归钉:工单不能消耗自己产出的批次(自耗环)。
        var order = InProgressOrder();
        var output = order.ProduceLot("L-SELF", 10m, T0);

        Should.Throw<InvalidOperationException>(
            () => order.RecordConsumption(output, 1m, 7, T0));
    }

    [Test]
    public void CrossTenantConsumptionIsRejected()
    {
        var order = InProgressOrder();
        order.TenantId = "tenant-a";
        var lot = MaterialLot.Create("L-XT", 2, 10m, "PCS", T0);
        lot.TenantId = "tenant-b";

        Should.Throw<InvalidOperationException>(
            () => order.RecordConsumption(lot, 1m, 7, T0));
    }

    [Test]
    public void DispositionsCannotExceedCompleted()
    {
        // Codex 审查发现#7 的回归钉:合格+报废+返工不得超过完工;违规调用整体回滚。
        var order = InProgressOrder();

        Should.Throw<InvalidOperationException>(
            () => order.ReportProduction(completed: 10m, qualified: 100m, scrap: 0m, rework: 0m));

        order.CompletedQuantity.ShouldBe(0m);
        order.QualifiedQuantity.ShouldBe(0m);

        order.ReportProduction(10m, 8m, 1m, 1m);
        Should.Throw<InvalidOperationException>(
            () => order.ReportProduction(0m, 1m, 0m, 0m));
        order.QualifiedQuantity.ShouldBe(8m);
    }

    [Test]
    public void DomainTimestampsAreNormalizedToUtc()
    {
        // Codex 审查发现#14 的回归钉:*Utc 字段必须真是 UTC,+08:00 输入被归一。
        var plusEight = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.FromHours(8));

        var order = NewOrder();
        order.Release();
        order.Start(plusEight);
        order.ActualStartUtc!.Value.Offset.ShouldBe(TimeSpan.Zero);
        order.ActualStartUtc.Value.ShouldBe(plusEight);

        var lot = MaterialLot.Create("L-UTC", 2, 10m, "PCS", plusEight);
        lot.ReceivedAtUtc.Offset.ShouldBe(TimeSpan.Zero);

        var consumption = order.RecordConsumption(lot, 1m, 7, plusEight);
        consumption.RecordedAtUtc.Offset.ShouldBe(TimeSpan.Zero);

        order.Complete(plusEight.AddHours(1));
        order.ActualEndUtc!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Test]
    public void ProducedLotIsLinkedToWorkOrder()
    {
        var order = InProgressOrder();

        var output = order.ProduceLot("L-FG-01", 100m, T0.AddHours(7));

        output.ProducedByWorkOrder.ShouldBeSameAs(order);
        output.SupplierLotNumber.ShouldBeNull();
        order.OutputLots.ShouldContain(output);
    }
}
