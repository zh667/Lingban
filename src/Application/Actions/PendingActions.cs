using System.Text.Json;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.WorkOrders.Commands;
using Lingban.Domain.Entities.Actions;
using Lingban.Domain.Enums;

namespace Lingban.Application.Actions;

public record ReportProductionProposal(
    string WorkOrderCode, decimal Completed, decimal Qualified, decimal Scrap, decimal Rework);

/// <summary>创建报工提议(HITL 第一步):只落挂起动作,不改任何生产数据。</summary>
public record ProposeReportProductionCommand(ReportProductionProposal Proposal, int? ConversationId)
    : IRequest<PendingAction>;

public class ProposeReportProductionCommandValidator : AbstractValidator<ProposeReportProductionCommand>
{
    public ProposeReportProductionCommandValidator()
    {
        RuleFor(command => command.Proposal.WorkOrderCode).NotEmpty().MaximumLength(64);
        RuleFor(command => command.Proposal.Completed).GreaterThanOrEqualTo(0);
        RuleFor(command => command.Proposal.Qualified).GreaterThanOrEqualTo(0);
        RuleFor(command => command.Proposal.Scrap).GreaterThanOrEqualTo(0);
        RuleFor(command => command.Proposal.Rework).GreaterThanOrEqualTo(0);
    }
}

public class ProposeReportProductionCommandHandler
    : IRequestHandler<ProposeReportProductionCommand, PendingAction>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IApplicationDbContext _context;
    private readonly IUser _user;

    public ProposeReportProductionCommandHandler(IApplicationDbContext context, IUser user)
    {
        _context = context;
        _user = user;
    }

    public async Task<PendingAction> Handle(
        ProposeReportProductionCommand request, CancellationToken cancellationToken)
    {
        // 工单必须存在且进行中,提议阶段就把明显无效的挡掉。
        bool exists = await _context.WorkOrders.AnyAsync(
            order => order.Code == request.Proposal.WorkOrderCode
                && order.Status == WorkOrderStatus.InProgress,
            cancellationToken);
        if (!exists)
        {
            throw new InvalidOperationException(
                $"工单 {request.Proposal.WorkOrderCode} 不存在或不在进行中,无法报工。");
        }

        var action = new PendingAction
        {
            OwnerUserId = _user.Id ?? throw new UnauthorizedAccessException("需要登录用户。"),
            ConversationId = request.ConversationId,
            ActionType = "ReportProduction",
            Summary = $"报工 {request.Proposal.WorkOrderCode}:完工 {request.Proposal.Completed}," +
                      $"合格 {request.Proposal.Qualified},报废 {request.Proposal.Scrap},返工 {request.Proposal.Rework}",
            PayloadJson = JsonSerializer.Serialize(request.Proposal, JsonOptions)
        };
        _context.PendingActions.Add(action);
        await _context.SaveChangesAsync(cancellationToken);
        return action;
    }
}

/// <summary>确认/拒绝挂起动作(HITL 第二步):仅属主可操作;批准后才真正执行写命令。</summary>
public record ConfirmPendingActionCommand(int ActionId, bool Approve) : IRequest<PendingAction>;

public class ConfirmPendingActionCommandHandler : IRequestHandler<ConfirmPendingActionCommand, PendingAction>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IApplicationDbContext _context;
    private readonly ISender _sender;
    private readonly IUser _user;

    public ConfirmPendingActionCommandHandler(IApplicationDbContext context, ISender sender, IUser user)
    {
        _context = context;
        _sender = sender;
        _user = user;
    }

    public async Task<PendingAction> Handle(
        ConfirmPendingActionCommand request, CancellationToken cancellationToken)
    {
        string userId = _user.Id ?? throw new UnauthorizedAccessException("需要登录用户。");
        PendingAction? action = await _context.PendingActions.FirstOrDefaultAsync(
            item => item.Id == request.ActionId && item.OwnerUserId == userId, cancellationToken);
        Guard.Against.NotFound(request.ActionId, action);

        if (!request.Approve)
        {
            action.Reject();
            await _context.SaveChangesAsync(cancellationToken);
            return action;
        }

        if (action.ActionType != "ReportProduction")
        {
            throw new InvalidOperationException($"未知动作类型 {action.ActionType}。");
        }

        var proposal = JsonSerializer.Deserialize<ReportProductionProposal>(action.PayloadJson, JsonOptions)!;
        int orderId = await _context.WorkOrders
            .Where(order => order.Code == proposal.WorkOrderCode)
            .Select(order => order.Id)
            .FirstAsync(cancellationToken);

        await _sender.Send(new ReportProductionCommand
        {
            WorkOrderId = orderId,
            Completed = proposal.Completed,
            Qualified = proposal.Qualified,
            Scrap = proposal.Scrap,
            Rework = proposal.Rework
        }, cancellationToken);

        action.Approve(JsonSerializer.Serialize(new
        {
            executed = true,
            workOrderCode = proposal.WorkOrderCode,
            proposal.Completed,
            proposal.Qualified,
            proposal.Scrap,
            proposal.Rework
        }, JsonOptions));
        await _context.SaveChangesAsync(cancellationToken);
        return action;
    }
}
