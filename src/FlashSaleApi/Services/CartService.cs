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
///           __sale_end__:{productId} → {UTC ticks of sale end time}
///   TTL:    2 hours (refreshed on every write)
///
/// FIX for "Incomplete Cart Invalidation" bug:
/// ─────────────────────────────────────────────
/// Previously, the cart only used Redis TTL (2-hour timeout) for expiry.
/// This meant an item added 5 minutes before a flash sale ends would stay in
/// the cart for another 1h55m AFTER the sale closed, misleading the user.
///
/// Now, every cart item stores the flash sale end time. On every read,
/// StripExpiredCartItemsAsync removes items whose sale has ended, keeping
/// the cart accurate regardless of the TTL.
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

        // FIX: Pass the flash sale end time so it can be stored in the cart hash.
        // StripExpiredCartItemsAsync uses this to evict items when the sale ends,
        // independent of the 2-hour cart TTL.
        await _redis.SetCartItemAsync(userId, request.ProductId, request.Quantity, product.EndTime);
        _logger.LogInformation("User {UserId} added product {ProductId} x{Qty} to cart (sale ends {EndTime})",
            userId, request.ProductId, request.Quantity, product.EndTime);

        return await BuildCartResponseAsync(userId);
    }

    public async Task<CartResponse?> GetCartAsync(string userId)
    {
        // FIX: Strip expired items first — this removes items whose flash sale has ended
        // even if the cart's 2-hour absolute TTL hasn't expired yet.
        await _redis.StripExpiredCartItemsAsync(userId);

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
        var now = DateTime.UtcNow;

        foreach (var (productId, (qty, saleEndTime)) in cartItems)
        {
            // Double-check: skip any item whose sale ended (belt-and-suspenders)
            if (saleEndTime < now) continue;

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
