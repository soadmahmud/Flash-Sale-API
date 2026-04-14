using FlashSaleApi.Models;

namespace FlashSaleApi.Repositories.Interfaces;

/// <summary>Data access contract for flash sale products.</summary>
public interface IFlashSaleRepository
{
    /// <summary>Returns all products where the current UTC time falls within [StartTime, EndTime].</summary>
    Task<IEnumerable<FlashSaleProduct>> GetActiveProductsAsync();

    Task<FlashSaleProduct?> GetProductByIdAsync(int id);

    /// <summary>Returns ALL products (including inactive). Used for Redis seeding on startup.</summary>
    Task<IEnumerable<FlashSaleProduct>> GetAllProductsAsync();
}
