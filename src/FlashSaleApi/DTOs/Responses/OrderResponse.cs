using FlashSaleApi.Models;

namespace FlashSaleApi.DTOs.Responses;

/// <summary>Order confirmation or history response.</summary>
public record OrderResponse(
    Guid Id,
    string UserId,
    OrderStatus Status,
    decimal TotalAmount,
    DateTime CreatedAt,
    List<OrderItemResponse> Items
);

public record OrderItemResponse(
    int ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal
);

/// <summary>Lightweight response returned immediately after a successful enqueue (202 Accepted).</summary>
public record OrderAcceptedResponse(
    Guid OrderId,
    string Message,
    string IdempotencyKey,
    string StatusPollUrl
);

/// <summary>
/// Real-time order status response for the polling endpoint.
/// Backed by a Redis key with 24h TTL — no database hit required.
/// Clients poll GET /api/orders/status/{orderId} until Status is "Confirmed" or "Failed".
/// </summary>
public record OrderStatusResponse(
    Guid OrderId,
    string Status,
    string? FailureReason,
    DateTime LastUpdatedAt
);
