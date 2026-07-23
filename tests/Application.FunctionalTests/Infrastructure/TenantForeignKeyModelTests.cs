using Lingban.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using AppDbContext = Lingban.Infrastructure.Data.ApplicationDbContext;

namespace Lingban.Application.FunctionalTests.Infrastructure;

/// <summary>
/// 五审 #5 的机械守卫:所有"租户实体 → 租户实体"外键必须包含 TenantId,
/// 白名单仅限声明过的 SetNull 工位类关系(可空、非谱系关键)。
/// </summary>
public class TenantForeignKeyModelTests : TestBase
{
    private static readonly string[] AllowedSingleColumn =
    {
        "ProcessStep->Workstation",
        "WorkOrderOperation->Workstation",
        "Equipment->Workstation"
    };

    [Test]
    public void AllTenantToTenantForeignKeysIncludeTenantId()
    {
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var offenders = new List<string>();
        foreach (IEntityType entityType in context.Model.GetEntityTypes())
        {
            if (!typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            foreach (IForeignKey foreignKey in entityType.GetForeignKeys())
            {
                if (!typeof(ITenantEntity).IsAssignableFrom(foreignKey.PrincipalEntityType.ClrType))
                {
                    continue;
                }

                string name = $"{entityType.ClrType.Name}->{foreignKey.PrincipalEntityType.ClrType.Name}";
                bool hasTenant = foreignKey.Properties.Any(
                    property => property.Name == nameof(ITenantEntity.TenantId));
                if (!hasTenant && !AllowedSingleColumn.Contains(name))
                {
                    offenders.Add(name);
                }
            }
        }

        offenders.ShouldBeEmpty("租户实体间外键必须含 TenantId(白名单外):" + string.Join(", ", offenders));
    }
}
