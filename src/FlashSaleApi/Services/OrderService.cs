using FlashSaleApi.DTOs.Requests;
using FlashSaleApi.DTOs.Responses;
using FlashSaleApi.Infrastructure.Redis;
using FlashSaleApi.Models;
using FlashSaleApi.Repositories.Interfaces;
using FlashSaleApi.Services.Interfaces;
using FlashSaleApi.Workers;

namespace FlashSaleApi.Services;

/// <summary>
/// Orchestrates the high-throughput order placement flow.
///
/// Order Placement Flow (step by step):
/// ──────────────────────────────────────
///  1. Validate request (products are active, quantities > 0)
///  2. Check idempotency key in Redis (SET NX) → reject duplicates
///  3. For each item: atomically decrement stock via Lua script
///     If stock is insufficient, rollback all already-decremented items.
///  4. Build an OrderQueuePayload and push it to Redis queue
///  5. Return 202 Accepted immediately (no DB write on the hot path)
///
/// The background worker (OrderProcessingWorker) handles step 6:
///  6. Dequeue payload → persist Order + OrderItems to PostgreSQL
///     → update status to Confirmed → clear user cart
///
/// Why this design?
/// ─────────────────
/// The HTTP request returns in &lt; 10ms. PostgreSQL writes (which are slower)
/// happen asynchronously. Under 100k concurrent users, the queue absorbs
/// traffic spikes without overwhelming the DB.
/// </summary>
public class OrderService : IOrderService
{
    private readonly IFlashSaleRepository _flashSaleRepo;
    private readonly IOrderRepository _orderRepo;
    private readonly IRedisService _redis;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        IFlashSaleRepository flashSaleRepo,
        IOrderRepository orderRepo,
        IRedisService redis,
        ILogger<OrderService> logger)
    {
        _flashSaleRepo = flashSaleRepo;
        _orderRepo = orderRepo;
        _redis = redis;
        _logger = logger;
    }

    public async Task<OrderAcceptedResponse> PlaceOrderAsync(string userId, PlaceOrderRequest request)
    {
        // ── Step 1: Basic validation ──────────────────────────────────────────
        if (request.Items is null || request.Items.Count == 0)
            throw new ArgumentException("Order must contain at least one item.");

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey is required.");

        // ── Step 2: Idempotency check ─────────────────────────────────────────
        // The key format: idem:{userId}:{idempotencyKey}
        var idemKey = $"{userId}:{request.IdempotencyKey}";
        var isNew = await _redis.SetIdempotencyKeyAsync(idemKey);
        if (!isNew)
        {
            _logger.LogWarning("Duplicate order attempt by user {UserId} with key {IdemKey}", userId, idemKey);
            throw new InvalidOperationException("Duplicate order: this idempotency key has already been used.");
        }

        // ── Step 3: Validate products and decrement stock atomically ──────────
        var now = DateTime.UtcNow;
        var decremented = new List<(int ProductId, int Quantity)>(); // track for rollback
        var orderItems = new List<OrderQueueItem>();
        decimal totalAmount = 0;

        foreach (var item in request.Items)
        {
            if (item.Quantity <= 0)
                throw new ArgumentException($"Quantity for product {item.ProductId} must be > 0.");

            // Validate product is in active flash sale window
            var product = await _flashSaleRepo.GetProductByIdAsync(item.ProductId)
                ?? throw new KeyNotFoundException($"Product {item.ProductId} not found.");

            if (product.StartTime > now || product.EndTime < now)
                throw new InvalidOperationException($"Product '{product.Name}' is not in an active flash sale.");

            // Atomic stock decrement via Lua script
            var remaining = await _redis.DecrementStockAsync(item.ProductId, item.Quantity);

            if (remaining == -1)
            {
                // Stock key not found in Redis — compensate and abort
                await RollbackStockAsync(decremented);
                await _redis.SetIdempotencyKeyAsync(idemKey, 0); // release idempotency key
                throw new InvalidOperationException($"Stock not available for product {item.ProductId}.");
            }

            if (remaining == -2)
            {
                // Not enough stock — compensate previously decremented items
                await RollbackStockAsync(decremented);
                await _redis.SetIdempotencyKeyAsync(idemKey, 0);
                throw new InvalidOperationException($"Insufficient stock for '{product.Name}'. Available: check the product listing.");
            }

            decremented.Add((item.ProductId, item.Quantity));
            orderItems.Add(new OrderQueueItem(
                ProductId: item.ProductId,
                ProductName: product.Name,
                Quantity: item.Quantity,
                UnitPrice: product.DiscountPrice));

            totalAmount += product.DiscountPrice * item.Quantity;
            _logger.LogInformation("Stock decremented for product {ProductId}: {Remaining} remaining", item.ProductId, remaining);
        }

        // ── Step 4: Enqueue order for async DB persistence ────────────────────
        var orderId = Guid.NewGuid();
        var payload = new OrderQueuePayload(
            OrderId:        orderId,
            UserId:         userId,
            IdempotencyKey: request.IdempotencyKey,
            Items:          orderItems,
            TotalAmount:    totalAmount,
            CreatedAt:      now);

        await _redis.EnqueueOrderAsync(payload);
        _logger.LogInformation("Order {OrderId} enqueued for user {UserId}", orderId, userId);

        // ── Step 5: Return immediately ────────────────────────────────────────
        return new OrderAcceptedResponse(
            OrderId:        orderId,
            Message:        "Your order has been received and is being processed.",
            IdempotencyKey: request.IdempotencyKey);
    }

    public async Task<IEnumerable<OrderResponse>> GetUserOrdersAsync(string userId)
    {
        var orders = await _orderRepo.GetOrdersByUserIdAsync(userId);

        return orders.Select(o => new OrderResponse(
            Id:          o.Id,
            UserId:      o.UserId,
            Status:      o.Status,
            TotalAmount: o.TotalAmount,
            CreatedAt:   o.CreatedAt,
            Items: o.Items.Select(i => new OrderItemResponse(
                ProductId:   i.ProductId,
                ProductName: i.Product?.Name ?? "Unknown",
                Quantity:    i.Quantity,
                UnitPrice:   i.UnitPrice,
                LineTotal:   i.UnitPrice * i.Quantity)).ToList()));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Compensates (restores) stock for items that were already decremented
    /// before a failure mid-order. This is the rollback mechanism.
    /// </summary>
    private async Task RollbackStockAsync(IEnumerable<(int ProductId, int Quantity)> items)
    {
        foreach (var (productId, qty) in items)
        {
            await _redis.IncrementStockAsync(productId, qty);
            _logger.LogWarning("Rolled back stock for product {ProductId}: +{Qty}", productId, qty);
        }
    }
}
