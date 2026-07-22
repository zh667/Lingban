using Lingban.Application.Common.Interfaces;

namespace Lingban.Application.Quality.Queries;

public record DefectTypeSummaryDto(string Code, string Name, decimal Quantity, double Share);

public record DefectSummaryDto(
    DateTimeOffset SinceUtc,
    DateTimeOffset AsOfUtc,
    decimal TotalQuantity,
    IReadOnlyList<DefectTypeSummaryDto> ByType);

/// <summary>近 N 天缺陷分布(帕累托底料)。</summary>
public record GetDefectSummaryQuery(int Days = 7, DateTimeOffset? AsOfUtc = null) : IRequest<DefectSummaryDto>;

public class GetDefectSummaryQueryValidator : AbstractValidator<GetDefectSummaryQuery>
{
    public GetDefectSummaryQueryValidator()
    {
        RuleFor(query => query.Days).InclusiveBetween(1, 365);
    }
}

public class GetDefectSummaryQueryHandler : IRequestHandler<GetDefectSummaryQuery, DefectSummaryDto>
{
    private readonly IApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public GetDefectSummaryQueryHandler(IApplicationDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<DefectSummaryDto> Handle(GetDefectSummaryQuery request, CancellationToken cancellationToken)
    {
        DateTimeOffset asOf = request.AsOfUtc ?? _timeProvider.GetUtcNow();
        DateTimeOffset since = asOf.AddDays(-request.Days);

        var grouped = await _context.DefectRecords.AsNoTracking()
            .Where(record => record.RecordedAtUtc >= since && record.RecordedAtUtc < asOf)
            .GroupBy(record => new { record.DefectType.Code, record.DefectType.Name })
            .Select(group => new
            {
                group.Key.Code,
                group.Key.Name,
                Quantity = group.Sum(record => record.Quantity)
            })
            .OrderByDescending(item => item.Quantity)
            .ToListAsync(cancellationToken);

        decimal total = grouped.Sum(item => item.Quantity);
        var byType = grouped
            .Select(item => new DefectTypeSummaryDto(
                item.Code,
                item.Name,
                item.Quantity,
                total > 0 ? (double)(item.Quantity / total) : 0d))
            .ToList();

        return new DefectSummaryDto(since, asOf, total, byType);
    }
}
