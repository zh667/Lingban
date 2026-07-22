namespace Lingban.Application.Common.Verification;

public enum VerificationStatus
{
    Verified = 0,
    Discrepancy = 1,
    Unverified = 2,
    Failed = 3
}

public record VerificationCheck(string Field, string Claimed, string Actual, bool Match);

public record VerificationResult
{
    public VerificationStatus Status { get; init; }

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<VerificationCheck> Checks { get; init; } = Array.Empty<VerificationCheck>();

    public static VerificationResult FromChecks(IEnumerable<VerificationCheck> checks)
    {
        var list = checks.ToList();
        bool allMatch = list.All(check => check.Match);
        return new VerificationResult
        {
            Status = allMatch ? VerificationStatus.Verified : VerificationStatus.Discrepancy,
            Summary = allMatch
                ? $"All {list.Count} checks passed against an independent query path."
                : $"{list.Count(check => !check.Match)} of {list.Count} checks disagree with the independent query path.",
            Checks = list
        };
    }
}

/// <summary>
/// 事实校验器:对工具结果按注册规则复核。
/// Agent 铁律 #1:规则的复核查询必须与工具走不同代码路径(见 IVerificationQueryService 的原生 SQL 实现)。
/// </summary>
public interface IFactVerifier
{
    Task<VerificationResult> VerifyAsync(string toolName, object toolResult, CancellationToken cancellationToken = default);
}

public interface IVerificationRule
{
    bool Supports(string toolName);

    Task<VerificationResult> VerifyAsync(object toolResult, CancellationToken cancellationToken);
}

public class FactVerifier : IFactVerifier
{
    private readonly IEnumerable<IVerificationRule> _rules;

    public FactVerifier(IEnumerable<IVerificationRule> rules)
    {
        _rules = rules;
    }

    public async Task<VerificationResult> VerifyAsync(
        string toolName, object toolResult, CancellationToken cancellationToken = default)
    {
        IVerificationRule? rule = _rules.FirstOrDefault(candidate => candidate.Supports(toolName));
        if (rule is null)
        {
            return new VerificationResult
            {
                Status = VerificationStatus.Unverified,
                Summary = $"No verification rule registered for tool '{toolName}'."
            };
        }

        try
        {
            return await rule.VerifyAsync(toolResult, cancellationToken);
        }
        catch (Exception exception)
        {
            return new VerificationResult
            {
                Status = VerificationStatus.Failed,
                Summary = $"Verification rule threw: {exception.Message}"
            };
        }
    }
}
