namespace FlashSaleApi.DTOs.Responses;

/// <summary>Public representation of an active flash sale product.</summary>
public record FlashSaleProductResponse(
    int Id,
    string Name,
    string Description,
    decimal OriginalPrice,
    decimal DiscountPrice,
    int DiscountPercentage,
    long StockRemaining,  // live value from Redis
    DateTime StartTime,
    DateTime EndTime,
    string? ImageUrl,
    TimeSpan TimeRemaining
);
