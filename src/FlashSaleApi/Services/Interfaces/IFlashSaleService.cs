using FlashSaleApi.DTOs.Responses;

namespace FlashSaleApi.Services.Interfaces;

public interface IFlashSaleService
{
    /// <summary>Returns currently active flash sale products enriched with live Redis stock.</summary>
    Task<IEnumerable<FlashSaleProductResponse>> GetActiveProductsAsync();

    /// <summary>Seeds all product stock quantities into Redis. Called once on startup.</summary>
    Task SeedStockToRedisAsync();
}
