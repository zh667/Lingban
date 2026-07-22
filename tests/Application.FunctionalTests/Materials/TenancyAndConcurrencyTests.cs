using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;
using Lingban.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lingban.Application.FunctionalTests.Materials;

/// <summary>
/// Codex 审查发现#1(并发超卖)与#4(租户写隔离)的回归钉。
/// </summary>
public class TenancyAndConcurrencyTests : TestBase
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ForgedTenantOnCreateIsRejected()
    {
        var forged = new Product { Code = "P-FORGED", Name = "伪造租户", TenantId = "someone-else" };

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => TestApp.AddAsync(forged));
        exception.Message.ShouldContain("someone-else");
    }

    [Test]
    public async Task ConcurrentLotUpdatesConflictInsteadOfSilentlyOverwriting()
    {
        int lotId = 0;
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            var product = new Product { Code = "RM-CC", Name = "并发测试料" };
            context.Add(product);
            await context.SaveChangesAsync();

            var lot = MaterialLot.Create("L-CC", product.Id, 10m, "PCS", T0);
            context.Add(lot);
            await context.SaveChangesAsync();
            lotId = lot.Id;
        });

        using var scope1 = FunctionalTestSetup.ScopeFactory.CreateScope();
        using var scope2 = FunctionalTestSetup.ScopeFactory.CreateScope();
        var context1 = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var context2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        MaterialLot lot1 = await context1.MaterialLots.FirstAsync(lot => lot.Id == lotId);
        MaterialLot lot2 = await context2.MaterialLots.FirstAsync(lot => lot.Id == lotId);

        lot1.Block();
        await context1.SaveChangesAsync();

        // 第二个写方基于过期的 xmin,必须冲突而不是静默覆盖。
        lot2.Block();
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => context2.SaveChangesAsync());
    }
}
