namespace Lingban.Domain.Entities.Materials;

/// <summary>
/// BOM 行:父产品对某组件的单位用量。
/// </summary>
public class BomLine : BaseAuditableEntity, ITenantEntity
{
    public string TenantId { get; set; } = string.Empty;

    public int ProductId { get; set; }

    public Product Product { get; set; } = null!;

    public int ComponentProductId { get; set; }

    public Product ComponentProduct { get; set; } = null!;

    /// <summary>生产 1 单位父产品所需的组件数量。</summary>
    public decimal QuantityPer { get; set; }

    public string UnitOfMeasure { get; set; } = "PCS";
}
