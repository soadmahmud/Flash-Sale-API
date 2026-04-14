namespace FlashSaleApi.Models;

/// <summary>
/// Represents a customer order placed during a flash sale.
/// Orders are initially created with <see cref="OrderStatus.Pending"/> status
/// and are confirmed asynchronously by the background worker.
/// </summary>
public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Identifier of the user who placed the order.
    /// Populated from the <c>X-User-Id</c> request header.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    public decimal TotalAmount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Client-supplied idempotency key to prevent duplicate submissions.
    /// Stored in Redis with NX semantics before enqueuing.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    // ── Navigation ─────────────────────────────────────────────────────────
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}

public enum OrderStatus
{
    Pending = 0,
    Confirmed = 1,
    Failed = 2
}
