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
        @"DateTime\.Now\b|DateTime\.Today\b|UtcNow\.Date\b",
        RegexOptions.Compiled);

    [Test]
    public void ProductionCodeDoesNotUseBannedTimeApis()
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

                if (Banned.IsMatch(lines[i]))
                {
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "禁止 DateTime.Now / DateTime.Today / UtcNow.Date——请传入 DateTimeOffset 或使用 ShiftCalendar。");
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
