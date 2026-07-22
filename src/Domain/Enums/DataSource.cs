namespace Lingban.Domain.Enums;

/// <summary>
/// 领域铁律 #5:模拟器数据与真实采集数据必须可区分来源。
/// </summary>
public enum DataSource
{
    Manual = 0,
    Device = 1,
    Simulated = 2
}
