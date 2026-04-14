using FlashSaleApi.DTOs.Requests;
using FlashSaleApi.DTOs.Responses;
using FlashSaleApi.Infrastructure.Redis;
using FlashSaleApi.Repositories.Interfaces;
using FlashSaleApi.Services.Interfaces;

namespace FlashSaleApi.Services;

/// <summary>
/// Manages the user's shopping cart stored in Redis.
///
/// Cart design:
/// ────────────
/// Each cart is a Redis Hash:
///   Key:    cart:{userId}
///   Fields: {productId} → {quantity}
///   TTL:    2 hours (refreshed on every write)
///
/// This means if a user doesn't interact for 2 hours, their cart is automatically
/// cleaned up — no manual expiry job needed.
/// </summary>
public class CartService : ICartService
{
    private readonly IRedisService _redis;
    private readonly IFlashSaleRepository _repo;
    private readonly ILogger<CartService> _logger;

    public CartService(
        IRedisService redis,
        IFlashSaleRepository repo,
        ILogger<CartService> logger)
    {
        _redis = redis;
        _repo = repo;
        _logger = logger;
    }

    public async Task<CartResponse> AddToCartAsync(string userId, AddToCartRequest request)
    {
        // Validate the product exists and is currently active
        var product = await _repo.GetProductByIdAsync(request.ProductId)
            ?? throw new KeyNotFoundException($"Product {request.ProductId} not found.");

        var now = DateTime.UtcNow;
        if (product.StartTime > now || product.EndTime < now)
            throw new InvalidOperationException($"Product '{product.Name}' is not part of an active flash sale.");

        // Validate live stock (informational — hard enforcement is in OrderService)
        var stock = await _redis.GetStockAsync(request.ProductId) ?? 0L;
        if (stock <= 0)
            throw new InvalidOperationException($"Product '{product.Name}' is out of stock.");

        await _redis.SetCartItemAsync(userId, request.ProductId, request.Quantity);
        _logger.LogInformation("User {UserId} added product {ProductId} x{Qty} to cart", userId, request.ProductId, request.Quantity);

        return await BuildCartResponseAsync(userId);
    }

    public async Task<CartResponse?> GetCartAsync(string userId)
    {
        var items = await _redis.GetCartAsync(userId);
        if (items.Count == 0)
            return null;

        return await BuildCartResponseAsync(userId);
    }

    public async Task ClearCartAsync(string userId)
    {
        await _redis.ClearCartAsync(userId);
        _logger.LogInformation("Cleared cart for user {UserId}", userId);
    }

    public async Task RemoveItemFromCartAsync(string userId, int productId)
    {
        await _redis.RemoveCartItemAsync(userId, productId);
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private async Task<CartResponse> BuildCartResponseAsync(string userId)
    {
        var cartItems = await _redis.GetCartAsync(userId);
        var responseItems = new List<CartItemResponse>();
        decimal total = 0;

        foreach (var (productId, qty) in cartItems)
        {
            var product = await _repo.GetProductByIdAsync(productId);
            if (product is null) continue;

            var lineTotal = product.DiscountPrice * qty;
            total += lineTotal;
            responseItems.Add(new CartItemResponse(
                ProductId:   productId,
                ProductName: product.Name,
                Quantity:    qty,
                UnitPrice:   product.DiscountPrice,
                LineTotal:   lineTotal));
        }

        return new CartResponse(userId, responseItems, total);
    }
}
