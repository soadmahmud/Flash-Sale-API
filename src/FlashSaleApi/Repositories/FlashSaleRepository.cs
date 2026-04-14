using FlashSaleApi.Infrastructure.Data;
using FlashSaleApi.Models;
using FlashSaleApi.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FlashSaleApi.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IFlashSaleRepository"/>.
/// All reads use AsNoTracking() for read-only performance.
/// </summary>
public class FlashSaleRepository : IFlashSaleRepository
{
    private readonly AppDbContext _db;

    public FlashSaleRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IEnumerable<FlashSaleProduct>> GetActiveProductsAsync()
    {
        var now = DateTime.UtcNow;
        return await _db.FlashSaleProducts
            .AsNoTracking()
            .Where(p => p.StartTime <= now && p.EndTime >= now)
            .OrderBy(p => p.EndTime) // soonest to expire first
            .ToListAsync();
    }

    public async Task<FlashSaleProduct?> GetProductByIdAsync(int id)
    {
        return await _db.FlashSaleProducts
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<IEnumerable<FlashSaleProduct>> GetAllProductsAsync()
    {
        return await _db.FlashSaleProducts
            .AsNoTracking()
            .ToListAsync();
    }
}
