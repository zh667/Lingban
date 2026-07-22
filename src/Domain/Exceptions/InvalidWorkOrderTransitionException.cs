namespace Lingban.Domain.Exceptions;

public class InvalidWorkOrderTransitionException : Exception
{
    public InvalidWorkOrderTransitionException(WorkOrderStatus from, WorkOrderStatus to)
        : base($"Work order cannot transition from {from} to {to}.")
    {
        From = from;
        To = to;
    }

    public WorkOrderStatus From { get; }

    public WorkOrderStatus To { get; }
}
