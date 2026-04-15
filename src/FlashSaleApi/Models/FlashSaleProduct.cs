namespace FlashSaleApi.Models;

/// <summary>
/// Represents a product participating in a flash sale event.
/// Stock and pricing are managed at the application level for performance.
/// </summary>
public class FlashSaleProduct
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Original retail price before discount.</summary>
    public decimal OriginalPrice { get; set; }

    /// <summary>Flash-sale discounted price.</summary>
    public decimal DiscountPrice { get; set; }

    /// <summary>
    /// Initial stock quantity seeded into Redis on startup.
    /// Acts as the authoritative stock source for overflow/reset scenarios.
    /// </summary>
    public int StockQuantity { get; set; }

    /// <summary>
    /// Maximum units a single user may purchase of this product during the flash sale.
    /// Enforced in Redis before stock is decremented, protecting against bot/scalper abuse.
    /// Defaults to 5 if not set explicitly.
    /// </summary>
    public int MaxQuantityPerUser { get; set; } = 5;

    public DateTime StartTime { get; set; }

    public DateTime EndTime { get; set; }

    public string? ImageUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ─────────────────────────────────────────────────────────
    public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
}
