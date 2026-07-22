namespace Lingban.Domain.Entities.Materials;

/// <summary>
/// 物料/产品主数据(原材料、半成品、成品统一建模)。
/// </summary>
public class Product : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>计量单位码,如 PCS/KG/M。离散制造不做单位换算。</summary>
    public string UnitOfMeasure { get; set; } = "PCS";

    /// <summary>本产品的 BOM(部件清单)。原材料该集合为空。</summary>
    public ICollection<BomLine> BomLines { get; private set; } = new List<BomLine>();
}
