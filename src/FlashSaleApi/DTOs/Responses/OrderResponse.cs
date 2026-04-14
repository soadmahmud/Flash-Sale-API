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
    string IdempotencyKey
);
