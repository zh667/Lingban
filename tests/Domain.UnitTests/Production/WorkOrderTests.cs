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
    public void ProducedLotIsLinkedToWorkOrder()
    {
        var order = InProgressOrder();

        var output = order.ProduceLot("L-FG-01", 100m, T0.AddHours(7));

        output.ProducedByWorkOrder.ShouldBeSameAs(order);
        output.SupplierLotNumber.ShouldBeNull();
        order.OutputLots.ShouldContain(output);
    }
}
