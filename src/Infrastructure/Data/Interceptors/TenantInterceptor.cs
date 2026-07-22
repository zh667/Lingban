using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lingban.Infrastructure.Data.Interceptors;

/// <summary>
/// 租户写隔离:新增实体盖当前租户章,预填了别的租户直接拒收;
/// 修改/删除时原值与现值都必须属于当前租户。读隔离由 DbContext 全局过滤器负责。
/// </summary>
public class TenantInterceptor : SaveChangesInterceptor
{
    private readonly ITenantContext _tenantContext;

    public TenantInterceptor(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        EnforceTenant(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        EnforceTenant(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void EnforceTenant(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        string tenantId = _tenantContext.TenantId;

        foreach (EntityEntry<ITenantEntity> entry in context.ChangeTracker.Entries<ITenantEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (string.IsNullOrEmpty(entry.Entity.TenantId))
                    {
                        entry.Entity.TenantId = tenantId;
                    }
                    else if (entry.Entity.TenantId != tenantId)
                    {
                        throw new InvalidOperationException(
                            $"Cannot create {entry.Metadata.ClrType.Name} for tenant '{entry.Entity.TenantId}' " +
                            $"while operating as tenant '{tenantId}'.");
                    }

                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    string original = entry.Property(nameof(ITenantEntity.TenantId)).OriginalValue as string ?? string.Empty;
                    if (original != tenantId || entry.Entity.TenantId != tenantId)
                    {
                        throw new InvalidOperationException(
                            $"Cannot {entry.State.ToString().ToLowerInvariant()} {entry.Metadata.ClrType.Name} " +
                            $"of tenant '{(original.Length > 0 ? original : entry.Entity.TenantId)}' " +
                            $"while operating as tenant '{tenantId}'.");
                    }

                    break;
            }
        }
    }
}
