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
    // 白名单键 = 依赖类型->主类型:FK属性;每项必须恰好命中一次、可空、SetNull(六审 #6)。
    private static readonly string[] AllowedSingleColumn =
    {
        "ProcessStep->Workstation:WorkstationId",
        "WorkOrderOperation->Workstation:WorkstationId",
        "Equipment->Workstation:WorkstationId"
    };

    [Test]
    public void AllTenantToTenantForeignKeysIncludeTenantId()
    {
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var offenders = new List<string>();
        var consumed = new HashSet<string>();
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

                string key = $"{entityType.ClrType.Name}->{foreignKey.PrincipalEntityType.ClrType.Name}:" +
                    string.Join("+", foreignKey.Properties.Select(property => property.Name));
                bool hasTenant = foreignKey.Properties.Any(
                    property => property.Name == nameof(ITenantEntity.TenantId));
                if (hasTenant)
                {
                    continue;
                }

                if (AllowedSingleColumn.Contains(key))
                {
                    // 白名单项还必须保持"可空 + SetNull"语义,变更即报警。
                    if (!consumed.Add(key))
                    {
                        offenders.Add(key + " (duplicated)");
                    }

                    if (!foreignKey.Properties.All(property => property.IsNullable) ||
                        foreignKey.DeleteBehavior != DeleteBehavior.SetNull)
                    {
                        offenders.Add(key + " (semantics changed: must be nullable SetNull)");
                    }

                    continue;
                }

                offenders.Add(key);
            }
        }

        foreach (string allowed in AllowedSingleColumn.Where(entry => !consumed.Contains(entry)))
        {
            offenders.Add(allowed + " (whitelist entry unused — remove it)");
        }

        offenders.ShouldBeEmpty("租户实体间外键必须含 TenantId(白名单外):" + string.Join(", ", offenders));
    }
}
