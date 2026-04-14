namespace FlashSaleApi.DTOs.Responses;

/// <summary>Snapshot of the user's current cart from Redis.</summary>
public record CartResponse(
    string UserId,
    List<CartItemResponse> Items,
    decimal EstimatedTotal
);

public record CartItemResponse(
    int ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal
);
