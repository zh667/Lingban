using System.Text.Json;
using Lingban.Application.Common.Exceptions;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.Common.Security;
using Lingban.Domain.Constants;
using Lingban.Domain.Entities.Actions;
using Lingban.Domain.Entities.Production;
using Lingban.Domain.Enums;
using ValidationException = Lingban.Application.Common.Exceptions.ValidationException;

namespace Lingban.Application.Actions;

public record ReportProductionProposal(
    string WorkOrderCode, decimal Completed, decimal Qualified, decimal Scrap, decimal Rework);

/// <summary>报工提议的工具面 DTO(铁律 #3 四件套之一):进 LLM 上下文与校验规则的形状。</summary>
public record ReportProductionProposalDto(
    int ActionId,
    string ActionType,
    string Summary,
    string Status,
    string WorkOrderCode,
    decimal Completed,
    decimal Qualified,
    decimal Scrap,
    decimal Rework,
    string PayloadJson);

/// <summary>创建报工提议(HITL 第一步):只落挂起动作,不改任何生产数据。</summary>
[Authorize(Roles = $"{Roles.Administrator},{Roles.ProductionReporter}")]
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
        // 工单必须存在且进行中——业务输入问题按校验错误透出(工具面可恢复错误,非 500)。
        bool exists = await _context.WorkOrders.AnyAsync(
            order => order.Code == request.Proposal.WorkOrderCode
                && order.Status == WorkOrderStatus.InProgress,
            cancellationToken);
        if (!exists)
        {
            throw new ValidationException(new[]
            {
                new FluentValidation.Results.ValidationFailure(
                    "WorkOrderCode", $"工单 {request.Proposal.WorkOrderCode} 不存在或不在进行中,无法报工。")
            });
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
[Authorize(Roles = $"{Roles.Administrator},{Roles.ProductionReporter}")]
public record ConfirmPendingActionCommand(int ActionId, bool Approve) : IRequest<PendingAction>;

/// <summary>
/// 八审 #1(实锤复现:双击双报工):确认必须原子。
/// 整个"重读状态 → 状态检查 → 报工领域操作 → Approve → 单次 SaveChanges"
/// 放进租户级谱系闸门的同一个事务:并发确认被 pg_advisory_xact_lock 串行,
/// 后到者重读时看到非 Pending,得到 409;报工与动作状态永远同生共死,
/// 不存在"报工已提交、动作还是 Pending"的中间态。
/// </summary>
public class ConfirmPendingActionCommandHandler : IRequestHandler<ConfirmPendingActionCommand, PendingAction>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IApplicationDbContext _context;
    private readonly IGenealogySerializedExecutor _serializedExecutor;
    private readonly IUser _user;

    public ConfirmPendingActionCommandHandler(
        IApplicationDbContext context, IGenealogySerializedExecutor serializedExecutor, IUser user)
    {
        _context = context;
        _serializedExecutor = serializedExecutor;
        _user = user;
    }

    public async Task<PendingAction> Handle(
        ConfirmPendingActionCommand request, CancellationToken cancellationToken)
    {
        string userId = _user.Id ?? throw new UnauthorizedAccessException("需要登录用户。");

        return await _serializedExecutor.ExecuteAsync(async innerToken =>
        {
            // 闸门内重读:锁已持有,读到的状态在本事务内不会被并发确认改写。
            PendingAction? action = await _context.PendingActions.FirstOrDefaultAsync(
                item => item.Id == request.ActionId && item.OwnerUserId == userId, innerToken);
            Guard.Against.NotFound(request.ActionId, action);

            if (action.Status != PendingActionStatus.Pending)
            {
                throw new ConflictException($"动作 #{action.Id} 已处理(当前状态 {action.Status}),不能重复确认。");
            }

            if (!request.Approve)
            {
                action.Reject();
                await _context.SaveChangesAsync(innerToken);
                return action;
            }

            if (action.ActionType != "ReportProduction")
            {
                throw new ConflictException($"未知动作类型 {action.ActionType}。");
            }

            var proposal = JsonSerializer.Deserialize<ReportProductionProposal>(action.PayloadJson, JsonOptions)!;
            WorkOrder? order = await _context.WorkOrders
                .FirstOrDefaultAsync(order => order.Code == proposal.WorkOrderCode, innerToken);
            Guard.Against.NotFound(proposal.WorkOrderCode, order);
            if (order.Status != WorkOrderStatus.InProgress)
            {
                throw new ConflictException(
                    $"工单 {order.Code} 已不在进行中(当前状态 {order.Status}),报工提议作废,请拒绝该动作。");
            }

            // 与 ReportProductionCommand 相同的领域入口,但在同一事务内直接调用:
            // 嵌套发送会各自开事务,破坏"报工与 Approve 同生共死"。
            order.ReportProduction(proposal.Completed, proposal.Qualified, proposal.Scrap, proposal.Rework);

            action.Approve(JsonSerializer.Serialize(new
            {
                executed = true,
                workOrderCode = proposal.WorkOrderCode,
                proposal.Completed,
                proposal.Qualified,
                proposal.Scrap,
                proposal.Rework
            }, JsonOptions));

            await _context.SaveChangesAsync(innerToken);
            return action;
        }, cancellationToken);
    }
}
