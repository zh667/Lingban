using Lingban.Application.Equipment.Commands;
using Lingban.Domain.Entities.Equipment;
using Lingban.Domain.Entities.Production;
using Lingban.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AppDbContext = Lingban.Infrastructure.Data.ApplicationDbContext;

namespace Lingban.Application.FunctionalTests.Equipment;

/// <summary>
/// 债 #11 的回归钉:设备事实只能经受控命令写入——状态区间关旧开新不重叠,
/// 停机区间写入前重叠拒绝,来源必须显式标注。
/// </summary>
public class EquipmentFactsTests : TestBase
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    private int _equipmentId;

    private async Task SeedEquipmentAsync()
    {
        await TestApp.ExecuteDbContextAsync(async context =>
        {
            var line = new ProductionLine { Code = "L8", Name = "八线" };
            var station = new Workstation { Code = "S8", Name = "工位八", ProductionLine = line };
            var equipment = new Domain.Entities.Equipment.Equipment
            {
                Code = "EQ-FACT",
                Name = "事实测试机",
                Workstation = station
            };
            context.AddRange(line, station, equipment);
            await context.SaveChangesAsync();
            _equipmentId = equipment.Id;
        });
    }

    [Test]
    public async Task StateTransitionsCloseThePreviousIntervalAndNeverOverlap()
    {
        await SeedEquipmentAsync();

        await TestApp.SendAsync(new SetEquipmentStateCommand
        {
            EquipmentId = _equipmentId,
            State = EquipmentState.Running,
            Source = DataSource.Simulated,
            AtUtc = T0
        });
        // 同状态重复上报:幂等,不产生新区间。
        await TestApp.SendAsync(new SetEquipmentStateCommand
        {
            EquipmentId = _equipmentId,
            State = EquipmentState.Running,
            Source = DataSource.Simulated,
            AtUtc = T0.AddMinutes(5)
        });
        await TestApp.SendAsync(new SetEquipmentStateCommand
        {
            EquipmentId = _equipmentId,
            State = EquipmentState.Down,
            Source = DataSource.Simulated,
            AtUtc = T0.AddMinutes(10)
        });

        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var records = await context.EquipmentStatusRecords
            .Where(record => record.EquipmentId == _equipmentId)
            .OrderBy(record => record.StartUtc)
            .ToListAsync();

        records.Count.ShouldBe(2);
        records[0].State.ShouldBe(EquipmentState.Running);
        records[0].EndUtc.ShouldBe(T0.AddMinutes(10));
        records[1].State.ShouldBe(EquipmentState.Down);
        records[1].EndUtc.ShouldBeNull();
        records.ShouldAllBe(record => record.Source == DataSource.Simulated);
    }

    [Test]
    public async Task OverlappingDowntimeIsRejectedAtWriteTime()
    {
        await SeedEquipmentAsync();

        await TestApp.SendAsync(new RecordDowntimeCommand
        {
            EquipmentId = _equipmentId,
            Reason = "首次停机",
            StartUtc = T0,
            EndUtc = T0.AddHours(2),
            Source = DataSource.Simulated
        });

        // 与已有区间相交 → 拒绝。
        var overlapping = new RecordDowntimeCommand
        {
            EquipmentId = _equipmentId,
            Reason = "重叠停机",
            StartUtc = T0.AddHours(1),
            EndUtc = T0.AddHours(3),
            Source = DataSource.Simulated
        };
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => TestApp.SendAsync(overlapping));
        exception.Message.ShouldContain("overlapping");

        // 不相交 → 允许。
        await TestApp.SendAsync(new RecordDowntimeCommand
        {
            EquipmentId = _equipmentId,
            Reason = "后续停机",
            StartUtc = T0.AddHours(3),
            EndUtc = T0.AddHours(4),
            Source = DataSource.Simulated
        });
    }

    [Test]
    public async Task OpenDowntimeBlocksNewRecordsUntilEnded()
    {
        await SeedEquipmentAsync();

        await TestApp.SendAsync(new RecordDowntimeCommand
        {
            EquipmentId = _equipmentId,
            Reason = "进行中停机",
            StartUtc = T0,
            Source = DataSource.Simulated
        });

        // 开放区间视为延伸到无穷:任何后续记录都重叠。
        await Should.ThrowAsync<InvalidOperationException>(
            () => TestApp.SendAsync(new RecordDowntimeCommand
            {
                EquipmentId = _equipmentId,
                Reason = "第二次停机",
                StartUtc = T0.AddHours(5),
                Source = DataSource.Simulated
            }));

        await TestApp.SendAsync(new EndDowntimeCommand(_equipmentId, T0.AddHours(1)));

        // 关闭后,其后的区间可以写入。
        await TestApp.SendAsync(new RecordDowntimeCommand
        {
            EquipmentId = _equipmentId,
            Reason = "第二次停机",
            StartUtc = T0.AddHours(5),
            EndUtc = T0.AddHours(6),
            Source = DataSource.Simulated
        });
    }

    [Test]
    public async Task UnspecifiedSourceIsRejectedByValidation()
    {
        await SeedEquipmentAsync();

        await Should.ThrowAsync<Lingban.Application.Common.Exceptions.ValidationException>(
            () => TestApp.SendAsync(new SetEquipmentStateCommand
            {
                EquipmentId = _equipmentId,
                State = EquipmentState.Running,
                Source = DataSource.Unspecified
            }));
    }
}
