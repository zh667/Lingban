using Lingban.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lingban.Infrastructure.Data;

public class GenealogySerializedExecutor : IGenealogySerializedExecutor
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;

    public GenealogySerializedExecutor(ApplicationDbContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        IExecutionStrategy strategy = _context.Database.CreateExecutionStrategy();
        string lockKey = _tenantContext.TenantId + ":genealogy";

        return await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await _context.Database.BeginTransactionAsync(cancellationToken);
            await _context.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", cancellationToken);

            T result = await action(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }
}
