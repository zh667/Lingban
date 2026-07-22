using Lingban.Domain.Entities;
using Lingban.Domain.Entities.Calendar;
using Lingban.Domain.Entities.Equipment;
using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;
using Lingban.Domain.Entities.Quality;

namespace Lingban.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<TodoList> TodoLists { get; }

    DbSet<TodoItem> TodoItems { get; }

    DbSet<Product> Products { get; }

    DbSet<BomLine> BomLines { get; }

    DbSet<MaterialLot> MaterialLots { get; }

    DbSet<MaterialConsumption> MaterialConsumptions { get; }

    DbSet<ProductionLine> ProductionLines { get; }

    DbSet<Workstation> Workstations { get; }

    DbSet<ProcessRoute> ProcessRoutes { get; }

    DbSet<ProcessStep> ProcessSteps { get; }

    DbSet<WorkOrder> WorkOrders { get; }

    DbSet<WorkOrderOperation> WorkOrderOperations { get; }

    DbSet<DefectType> DefectTypes { get; }

    DbSet<DefectRecord> DefectRecords { get; }

    DbSet<QualityInspection> QualityInspections { get; }

    DbSet<Shift> Shifts { get; }

    DbSet<Domain.Entities.Equipment.Equipment> Equipment { get; }

    DbSet<EquipmentStatusRecord> EquipmentStatusRecords { get; }

    DbSet<DowntimeRecord> DowntimeRecords { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
