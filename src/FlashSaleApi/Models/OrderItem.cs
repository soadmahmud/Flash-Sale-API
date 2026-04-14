namespace FlashSaleApi.Models;

/// <summary>
/// Represents a single line item within an <see cref="Order"/>.
/// Unit price is snapshotted at order time so historical records remain accurate
/// even after a product's discount price changes.
/// </summary>
public class OrderItem
{
    public int Id { get; set; }

    public Guid OrderId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    /// <summary>Price captured at the moment the order was placed.</summary>
    public decimal UnitPrice { get; set; }

    // ── Navigation ─────────────────────────────────────────────────────────
    public Order Order { get; set; } = null!;

    public FlashSaleProduct Product { get; set; } = null!;
}
