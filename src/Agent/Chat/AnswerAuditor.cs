using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lingban.Application.Common.Verification;

namespace Lingban.Agent.Chat;

/// <summary>
/// 答案级事实审计(铁律 #1 最后一环):最终答案中的每个数字必须能在
/// "已通过校验的工具数据"或用户原话中找到出处;任何工具非 Verified 即整体降级。
/// 百分数形态(0.94 → 94 / 93.8 等舍入)计入允许集。
/// </summary>
public static class AnswerAuditor
{
    private static readonly Regex NumberPattern = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AnswerAuditEvent Audit(
        string answer,
        string userMessage,
        IReadOnlyList<ToolResultEvent> toolResults,
        IReadOnlyList<ToolErrorEvent> toolErrors)
    {
        List<string> nonVerifiedTools = toolResults
            .Where(result => result.Verification.Status != VerificationStatus.Verified)
            .Select(result => $"{result.ToolName}:{result.Verification.Status}")
            .Concat(toolErrors.Select(error => $"{error.ToolName}:Error"))
            .Distinct()
            .ToList();

        var allowed = new HashSet<string>();
        foreach (ToolResultEvent result in toolResults.Where(
            result => result.Verification.Status == VerificationStatus.Verified))
        {
            CollectNumbers(JsonSerializer.Serialize(result.Result, JsonOptions), allowed);
        }

        CollectNumbers(userMessage, allowed);

        if (string.IsNullOrWhiteSpace(answer))
        {
            // 六审 #7:工具预算耗尽等场景可能产生空答案——空答案不许静默通过审计。
            return new AnswerAuditEvent(false, new[] { "<EMPTY_ANSWER>" }, nonVerifiedTools);
        }

        List<string> unverified = NumberPattern.Matches(answer)
            .Select(match => match.Value)
            .Where(token => !IsAllowed(token, allowed))
            .Distinct()
            .ToList();

        return new AnswerAuditEvent(
            Passed: unverified.Count == 0 && nonVerifiedTools.Count == 0,
            UnverifiedNumbers: unverified,
            NonVerifiedTools: nonVerifiedTools);
    }

    private static void CollectNumbers(string text, HashSet<string> allowed)
    {
        foreach (Match match in NumberPattern.Matches(text))
        {
            if (!decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
            {
                continue;
            }

            AddForms(value, allowed);

            // 比率的百分数表达(0.9375 → 93.75 / 93.8 / 94)。
            if (value > 0m && value <= 1m)
            {
                AddForms(value * 100m, allowed);
            }
        }
    }

    private static void AddForms(decimal value, HashSet<string> allowed)
    {
        allowed.Add(Normalize(value));
        allowed.Add(Normalize(Math.Round(value, 0)));
        allowed.Add(Normalize(Math.Round(value, 1)));
        allowed.Add(Normalize(Math.Round(value, 2)));
    }

    private static bool IsAllowed(string token, HashSet<string> allowed)
        => decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value)
           && allowed.Contains(Normalize(value));

    private static string Normalize(decimal value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);
}
