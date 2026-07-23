namespace Lingban.Domain.Entities.Actions;

/// <summary>
/// 写操作的 HITL 挂起动作(铁律 #5):模型只能"提议",人确认后才执行。
/// PayloadJson 是提议参数的完整快照;ResultJson 记录执行结果,构成审计闭环。
/// </summary>
public class PendingAction : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string OwnerUserId { get; set; } = string.Empty;

    public int? ConversationId { get; set; }

    public string ActionType { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public PendingActionStatus Status { get; private set; } = PendingActionStatus.Pending;

    public string? ResultJson { get; private set; }

    public void Approve(string resultJson)
    {
        EnsurePending();
        Status = PendingActionStatus.Approved;
        ResultJson = resultJson;
    }

    public void Reject()
    {
        EnsurePending();
        Status = PendingActionStatus.Rejected;
    }

    private void EnsurePending()
    {
        if (Status != PendingActionStatus.Pending)
        {
            throw new InvalidOperationException($"动作已是 {Status},不可重复处理。");
        }
    }
}
