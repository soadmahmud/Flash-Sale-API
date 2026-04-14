namespace FlashSaleApi.DTOs.Requests;

/// <summary>
/// Request payload for placing a flash sale order.
/// </summary>
/// <param name="IdempotencyKey">
/// A unique key (e.g. UUID) supplied by the client to prevent duplicate orders.
/// Re-submitting with the same key returns 409 Conflict instead of placing a second order.
/// </param>
/// <param name="Items">One or more products and the desired quantities.</param>
public record PlaceOrderRequest(
    string IdempotencyKey,
    List<OrderItemRequest> Items
);

/// <summary>Single line item within a <see cref="PlaceOrderRequest"/>.</summary>
/// <param name="ProductId">The ID of the flash sale product.</param>
/// <param name="Quantity">Quantity to purchase (must be ≥ 1).</param>
public record OrderItemRequest(
    int ProductId,
    int Quantity
);
