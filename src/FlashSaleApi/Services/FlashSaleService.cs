using FlashSaleApi.DTOs.Responses;
using FlashSaleApi.Infrastructure.Redis;
using FlashSaleApi.Repositories.Interfaces;
using FlashSaleApi.Services.Interfaces;

namespace FlashSaleApi.Services;

/// <summary>
/// Handles flash sale product listing and Redis stock seeding.
///
/// Stock seeding strategy:
/// ──────────────────────
/// On application startup, all product stock values are written to Redis.
/// This avoids hitting PostgreSQL on every order placement — Redis becomes
/// the source of truth for real-time stock while PostgreSQL holds the
/// authoritative transactional record.
/// </summary>
public class FlashSaleService : IFlashSaleService
{
    private readonly IFlashSaleRepository _repo;
    private readonly IRedisService _redis;
    private readonly ILogger<FlashSaleService> _logger;

    public FlashSaleService(
        IFlashSaleRepository repo,
        IRedisService redis,
        ILogger<FlashSaleService> logger)
    {
        _repo = repo;
        _redis = redis;
        _logger = logger;
    }

    public async Task<IEnumerable<FlashSaleProductResponse>> GetActiveProductsAsync()
    {
        var products = await _repo.GetActiveProductsAsync();
        var now = DateTime.UtcNow;
        var responses = new List<FlashSaleProductResponse>();

        foreach (var p in products)
        {
            // Fetch live stock from Redis (never from DB for performance)
            var stock = await _redis.GetStockAsync(p.Id) ?? 0L;

            var discountPct = p.OriginalPrice > 0
                ? (int)Math.Round((1 - p.DiscountPrice / p.OriginalPrice) * 100)
                : 0;

            responses.Add(new FlashSaleProductResponse(
                Id:                 p.Id,
                Name:               p.Name,
                Description:        p.Description,
                OriginalPrice:      p.OriginalPrice,
                DiscountPrice:      p.DiscountPrice,
                DiscountPercentage: discountPct,
                StockRemaining:     Math.Max(0, stock),
                StartTime:          p.StartTime,
                EndTime:            p.EndTime,
                ImageUrl:           p.ImageUrl,
                TimeRemaining:      p.EndTime - now
            ));
        }

        return responses;
    }

    public async Task SeedStockToRedisAsync()
    {
        _logger.LogInformation("Seeding product stock to Redis...");
        var products = await _repo.GetAllProductsAsync();

        foreach (var p in products)
        {
            // Only set if key doesn't already exist to avoid resetting during rolling restarts
            var existing = await _redis.GetStockAsync(p.Id);
            if (existing is null)
            {
                await _redis.SetStockAsync(p.Id, p.StockQuantity);
                _logger.LogInformation("Seeded: Product {Id} ({Name}) → stock={Stock}", p.Id, p.Name, p.StockQuantity);
            }
            else
            {
                _logger.LogInformation("Skipped seed: Product {Id} already has stock={Stock} in Redis", p.Id, existing);
            }
        }

        _logger.LogInformation("Stock seeding complete.");
    }
}
