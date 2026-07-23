using Lingban.Application.Common.Interfaces;
using Lingban.Application.Equipment.Commands;
using Lingban.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lingban.DeviceSimulator;

public class SimulatorOptions
{
    public const string SectionName = "Simulator";

    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 15;
}

/// <summary>
/// 设备数据模拟器。领域铁律 #5:所有写入 Source=Simulated,与真实采集可区分;
/// 只走命令入口(SetEquipmentState / RecordDowntime / EndDowntime),
/// 受与真实调用方相同的校验与架构守卫约束。
/// </summary>
public class SimulatorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<SimulatorOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SimulatorWorker> _logger;
    private readonly Random _random = new();

    public SimulatorWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<SimulatorOptions> options,
        TimeProvider timeProvider,
        ILogger<SimulatorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("Device simulator is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.Value.IntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                // 数据库尚未初始化等瞬态问题:记录并等下一个周期,不让宿主崩溃。
                _logger.LogWarning(exception, "Simulator tick failed; retrying next interval.");
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        List<int> equipmentIds = await context.Equipment
            .Where(equipment => equipment.IsActive)
            .Select(equipment => equipment.Id)
            .ToListAsync(cancellationToken);

        foreach (int equipmentId in equipmentIds)
        {
            int roll = _random.Next(100);
            EquipmentState state = roll < 80
                ? EquipmentState.Running
                : roll < 90 ? EquipmentState.Idle : EquipmentState.Down;

            bool hasOpenDowntime = await context.DowntimeRecords.AnyAsync(
                record => record.EquipmentId == equipmentId && record.EndUtc == null,
                cancellationToken);

            if (state == EquipmentState.Down && !hasOpenDowntime)
            {
                await sender.Send(new RecordDowntimeCommand
                {
                    EquipmentId = equipmentId,
                    Reason = "模拟故障",
                    StartUtc = _timeProvider.GetUtcNow(),
                    Source = DataSource.Simulated
                }, cancellationToken);
            }
            else if (state != EquipmentState.Down && hasOpenDowntime)
            {
                await sender.Send(new EndDowntimeCommand(equipmentId), cancellationToken);
            }

            await sender.Send(new SetEquipmentStateCommand
            {
                EquipmentId = equipmentId,
                State = state,
                Source = DataSource.Simulated
            }, cancellationToken);
        }
    }
}
