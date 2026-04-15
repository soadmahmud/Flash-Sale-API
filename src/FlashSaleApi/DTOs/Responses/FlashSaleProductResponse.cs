namespace FlashSaleApi.DTOs.Responses;

/// <summary>Public representation of an active flash sale product.</summary>
public record FlashSaleProductResponse(
    int Id,
    string Name,
    string Description,
    decimal OriginalPrice,
    decimal DiscountPrice,
    int DiscountPercentage,
    long StockRemaining,        // live value from Redis
    int MaxQuantityPerUser,     // per-user purchase limit (displayed so clients can enforce client-side too)
    DateTime StartTime,
    DateTime EndTime,
    string? ImageUrl,
    TimeSpan TimeRemaining
);
