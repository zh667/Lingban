namespace Lingban.Domain.Enums;

/// <summary>
/// 领域铁律 #5:模拟器数据与真实采集数据必须可区分来源。
/// </summary>
public enum DataSource
{
    /// <summary>未标注——数据库 CHECK 约束拒绝写入,防止默认值冒充真实来源。</summary>
    Unspecified = 0,
    Manual = 1,
    Device = 2,
    Simulated = 3
}
