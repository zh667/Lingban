using Lingban.Application.Common.Interfaces;
using Lingban.Domain.Entities.Equipment;
using Lingban.Domain.Enums;

namespace Lingban.Application.Equipment.Commands;

// 设备事实(状态/停机)的受控写入入口(债 #11):
// 来源必须显式标注;状态区间由"关旧开新"保证不重叠;停机写入前做区间重叠拒绝。

public record SetEquipmentStateCommand : IRequest
{
    public int EquipmentId { get; init; }

    public EquipmentState State { get; init; }

    public DataSource Source { get; init; }

    public DateTimeOffset? AtUtc { get; init; }
}

public class SetEquipmentStateCommandValidator : AbstractValidator<SetEquipmentStateCommand>
{
    public SetEquipmentStateCommandValidator()
    {
        RuleFor(command => command.EquipmentId).GreaterThan(0);
        RuleFor(command => command.Source).NotEqual(DataSource.Unspecified);
    }
}

public class SetEquipmentStateCommandHandler : IRequestHandler<SetEquipmentStateCommand>
{
    private readonly IApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public SetEquipmentStateCommandHandler(IApplicationDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task Handle(SetEquipmentStateCommand request, CancellationToken cancellationToken)
    {
        bool equipmentExists = await _context.Equipment
            .AnyAsync(equipment => equipment.Id == request.EquipmentId, cancellationToken);
        Guard.Against.NotFound(request.EquipmentId, equipmentExists ? request.EquipmentId : (int?)null);

        DateTimeOffset at = (request.AtUtc ?? _timeProvider.GetUtcNow()).ToUniversalTime();

        EquipmentStatusRecord? open = await _context.EquipmentStatusRecords
            .Where(record => record.EquipmentId == request.EquipmentId && record.EndUtc == null)
            .OrderByDescending(record => record.StartUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (open is not null)
        {
            if (open.State == request.State)
            {
                return;
            }

            if (at <= open.StartUtc)
            {
                throw new InvalidOperationException(
                    $"Equipment {request.EquipmentId}: new state at {at:O} does not come after " +
                    $"the open status started at {open.StartUtc:O}.");
            }

            open.EndUtc = at;
        }

        _context.EquipmentStatusRecords.Add(new EquipmentStatusRecord
        {
            EquipmentId = request.EquipmentId,
            State = request.State,
            StartUtc = at,
            Source = request.Source
        });

        await _context.SaveChangesAsync(cancellationToken);
    }
}

public record RecordDowntimeCommand : IRequest<int>
{
    public int EquipmentId { get; init; }

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset StartUtc { get; init; }

    public DateTimeOffset? EndUtc { get; init; }

    public DataSource Source { get; init; }

    public string? Description { get; init; }
}

public class RecordDowntimeCommandValidator : AbstractValidator<RecordDowntimeCommand>
{
    public RecordDowntimeCommandValidator()
    {
        RuleFor(command => command.EquipmentId).GreaterThan(0);
        RuleFor(command => command.Reason).NotEmpty().MaximumLength(256);
        RuleFor(command => command.Source).NotEqual(DataSource.Unspecified);
        RuleFor(command => command.EndUtc)
            .GreaterThan(command => command.StartUtc)
            .When(command => command.EndUtc.HasValue);
    }
}

public class RecordDowntimeCommandHandler : IRequestHandler<RecordDowntimeCommand, int>
{
    private readonly IApplicationDbContext _context;

    public RecordDowntimeCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<int> Handle(RecordDowntimeCommand request, CancellationToken cancellationToken)
    {
        bool equipmentExists = await _context.Equipment
            .AnyAsync(equipment => equipment.Id == request.EquipmentId, cancellationToken);
        Guard.Against.NotFound(request.EquipmentId, equipmentExists ? request.EquipmentId : (int?)null);

        DateTimeOffset start = request.StartUtc.ToUniversalTime();
        DateTimeOffset? end = request.EndUtc?.ToUniversalTime();

        // 重叠拒绝(债 #11):同设备的停机区间不得相交,开放区间视为延伸到无穷。
        bool overlaps = await _context.DowntimeRecords.AnyAsync(
            record => record.EquipmentId == request.EquipmentId
                && (end == null || record.StartUtc < end)
                && (record.EndUtc == null || record.EndUtc > start),
            cancellationToken);
        if (overlaps)
        {
            throw new InvalidOperationException(
                $"Equipment {request.EquipmentId} already has a downtime record overlapping " +
                $"[{start:O}, {(end?.ToString("O") ?? "open")}).");
        }

        var downtime = new DowntimeRecord
        {
            EquipmentId = request.EquipmentId,
            Reason = request.Reason,
            StartUtc = start,
            EndUtc = end,
            Source = request.Source,
            Description = request.Description
        };
        _context.DowntimeRecords.Add(downtime);
        await _context.SaveChangesAsync(cancellationToken);
        return downtime.Id;
    }
}

public record EndDowntimeCommand(int EquipmentId, DateTimeOffset? EndUtc = null) : IRequest;

public class EndDowntimeCommandHandler : IRequestHandler<EndDowntimeCommand>
{
    private readonly IApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public EndDowntimeCommandHandler(IApplicationDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task Handle(EndDowntimeCommand request, CancellationToken cancellationToken)
    {
        DowntimeRecord? open = await _context.DowntimeRecords
            .Where(record => record.EquipmentId == request.EquipmentId && record.EndUtc == null)
            .OrderByDescending(record => record.StartUtc)
            .FirstOrDefaultAsync(cancellationToken);
        Guard.Against.NotFound(request.EquipmentId, open);

        DateTimeOffset end = (request.EndUtc ?? _timeProvider.GetUtcNow()).ToUniversalTime();
        if (end <= open.StartUtc)
        {
            throw new InvalidOperationException(
                $"Downtime end {end:O} must come after start {open.StartUtc:O}.");
        }

        open.EndUtc = end;
        await _context.SaveChangesAsync(cancellationToken);
    }
}
