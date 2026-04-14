namespace FlashSaleApi.Workers;

/// <summary>
/// The serializable payload pushed onto the Redis order queue.
/// Contains everything the background worker needs to persist the order
/// to PostgreSQL — no additional DB lookups needed during processing.
/// </summary>
public record OrderQueuePayload(
    Guid OrderId,
    string UserId,
    string IdempotencyKey,
    List<OrderQueueItem> Items,
    decimal TotalAmount,
    DateTime CreatedAt
);

public record OrderQueueItem(
    int ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice
);
