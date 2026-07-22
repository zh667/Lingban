using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;

namespace Lingban.Domain.UnitTests.Architecture;

/// <summary>
/// 领域铁律的机械保证:存储一律 UTC,"今天/班次"必须走 ShiftCalendar,
/// 禁止本地时钟与 UTC 日历日切分(DateTime.Now / DateTime.Today / *.UtcNow.Date)。
/// </summary>
public class BannedTimeApisTests
{
    private static readonly Regex Banned = new(
        @"DateTime\.Now\b|DateTime\.Today\b|DateTimeOffset\.Now\b|UtcNow\.Date\b",
        RegexOptions.Compiled);

    // 谱系表只能经聚合方法写入:禁止批量改写与直接增删消耗记录(采购批次 Add 是合法入库,不禁)。
    private static readonly Regex GenealogyBypass = new(
        @"MaterialConsumptions\s*\.\s*(Add|AddRange|Remove|RemoveRange|ExecuteUpdate|ExecuteDelete)|MaterialLots\s*\.\s*(ExecuteUpdate|ExecuteDelete)",
        RegexOptions.Compiled);

    [Test]
    public void ProductionCodeDoesNotUseBannedTimeApis()
    {
        ScanSources(Banned).ShouldBeEmpty(
            "禁止 DateTime.Now / DateTime.Today / DateTimeOffset.Now / UtcNow.Date——请传入 DateTimeOffset 或使用 ShiftCalendar。");
    }

    // 完工只许经 CompleteWorkOrderCommand(前置校验 + 谱系闸门)或领域/种子内部调用。
    private static readonly Regex CompleteBypass = new(@"\.Complete\(", RegexOptions.Compiled);

    private static readonly string[] CompleteAllowedFiles =
    {
        "WorkOrder.cs",
        "ReportAndComplete.cs",
        "ApplicationDbContextInitialiser.cs"
    };

    [Test]
    public void WorkOrderCompleteIsOnlyCalledFromSanctionedEntryPoints()
    {
        List<string> offenders = ScanSources(CompleteBypass)
            .Where(offender => !CompleteAllowedFiles.Any(allowed => offender.Contains(allowed)))
            .ToList();

        offenders.ShouldBeEmpty(
            "WorkOrder.Complete() 绕过完工前置校验;请走 CompleteWorkOrderCommand。");
    }

    [Test]
    public void GenealogyTablesAreOnlyWrittenThroughAggregates()
    {
        ScanSources(GenealogyBypass).ShouldBeEmpty(
            "MaterialConsumption 只能经 WorkOrder.RecordConsumption 创建;禁止对谱系表直接增删或批量改写。");
    }

    private static List<string> ScanSources(Regex pattern)
    {
        string repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (pattern.IsMatch(lines[i]))
                {
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        return offenders;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lingban.slnx")))
        {
            dir = dir.Parent!;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root (Lingban.slnx) not found.");
    }
}
