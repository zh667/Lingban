using System.Linq.Expressions;
using System.Reflection;
using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Common;
using Lingban.Domain.Entities.Calendar;
using Lingban.Domain.Entities.Equipment;
using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;
using Lingban.Domain.Entities.Quality;
using Lingban.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Lingban.Infrastructure.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>, IApplicationDbContext
{
    private readonly ITenantContext _tenantContext;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>全局租户过滤器引用的当前租户;表达式在查询时对它求值。</summary>
    public string CurrentTenantId => _tenantContext.TenantId;

    public DbSet<Product> Products => Set<Product>();

    public DbSet<BomLine> BomLines => Set<BomLine>();

    public DbSet<MaterialLot> MaterialLots => Set<MaterialLot>();

    public DbSet<MaterialConsumption> MaterialConsumptions => Set<MaterialConsumption>();

    public DbSet<ProductionLine> ProductionLines => Set<ProductionLine>();

    public DbSet<Workstation> Workstations => Set<Workstation>();

    public DbSet<ProcessRoute> ProcessRoutes => Set<ProcessRoute>();

    public DbSet<ProcessStep> ProcessSteps => Set<ProcessStep>();

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

    public DbSet<WorkOrderOperation> WorkOrderOperations => Set<WorkOrderOperation>();

    public DbSet<DefectType> DefectTypes => Set<DefectType>();

    public DbSet<DefectRecord> DefectRecords => Set<DefectRecord>();

    public DbSet<QualityInspection> QualityInspections => Set<QualityInspection>();

    public DbSet<Shift> Shifts => Set<Shift>();

    public DbSet<Domain.Entities.Equipment.Equipment> Equipment => Set<Domain.Entities.Equipment.Equipment>();

    public DbSet<EquipmentStatusRecord> EquipmentStatusRecords => Set<EquipmentStatusRecord>();

    public DbSet<DowntimeRecord> DowntimeRecords => Set<DowntimeRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // 离散制造数量统一 decimal(18,3)。
        configurationBuilder.Properties<decimal>().HavePrecision(18, 3);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        ApplyTenantQueryFilters(builder);
    }

    /// <summary>为所有 ITenantEntity 实体套全局过滤器:entity => entity.TenantId == CurrentTenantId。</summary>
    private void ApplyTenantQueryFilters(ModelBuilder builder)
    {
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType) || entityType.IsOwned())
            {
                continue;
            }

            ParameterExpression parameter = Expression.Parameter(entityType.ClrType, "entity");
            MemberExpression entityTenantId = Expression.Property(parameter, nameof(ITenantEntity.TenantId));
            MemberExpression currentTenantId = Expression.Property(
                Expression.Constant(this), nameof(CurrentTenantId));

            LambdaExpression filter = Expression.Lambda(
                Expression.Equal(entityTenantId, currentTenantId), parameter);
            entityType.SetQueryFilter(filter);
        }
    }
}
