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
///  3. Enforce per-user purchase limits (bot/scalper protection)
///  4. Atomically decrement stock for ALL items in a single Lua call (all-or-nothing)
///  5. Increment per-user quota counters
///  6. Write initial "Queued" status to Redis for client polling
///  7. Build an OrderQueuePayload and push it to Redis queue
///  8. Return 202 Accepted immediately (no DB write on the hot path)
///
/// The background worker (OrderProcessingWorker) handles step 9:
///  9. Dequeue payload → persist Order + OrderItems to PostgreSQL
///     → update Redis status to Confirmed/Failed → clear user cart
///
/// Why this design?
/// ─────────────────
/// The HTTP request returns in under 10ms. PostgreSQL writes (which are slower)
/// happen asynchronously. Under 100k concurrent users, the queue absorbs
/// traffic spikes without overwhelming the DB.
///
/// Bug Fixes in this version:
/// ──────────────────────────
/// • FIX 1 (Ghost Stock): Stock is now decremented in a single atomic Lua call
///   for ALL items — not in a per-item loop. If any item fails, NO stock is touched.
///
/// • FIX 4 (Bot Protection): Per-user purchase quotas are checked before decrement.
///
/// • FIX 6 (Idempotency Rollback): Uses DeleteIdempotencyKeyAsync instead of
///   SetIdempotencyKeyAsync(key, 0) which was a no-op and blocked retries.
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

        try
        {
            // ── Step 3: Validate products + enforce per-user purchase limits ────
            var now = DateTime.UtcNow;
            var orderItems = new List<OrderQueueItem>();
            decimal totalAmount = 0;

            // FIX 1: Aggregate duplicate products to prevent Lua bypass
            var consolidatedItems = request.Items
                .GroupBy(i => i.ProductId)
                .Select(g => new OrderItemRequest(g.Key, g.Sum(x => x.Quantity)))
                .ToList();

            // Load all product metadata in advance (before touching Redis stock)
            var productLookup = new Dictionary<int, FlashSaleProduct>();
            foreach (var item in consolidatedItems)
            {
                if (item.Quantity <= 0)
                    throw new ArgumentException($"Quantity for product {item.ProductId} must be > 0.");

                var product = await _flashSaleRepo.GetProductByIdAsync(item.ProductId)
                    ?? throw new KeyNotFoundException($"Product {item.ProductId} not found.");

                if (product.StartTime > now || product.EndTime < now)
                    throw new InvalidOperationException($"Product '{product.Name}' is not in an active flash sale.");

                // ── FIX 4: Per-user purchase limit enforcement ────────────────
                var alreadyPurchased = await _redis.GetUserPurchasedQtyAsync(userId, item.ProductId);
                var wouldPurchase = alreadyPurchased + item.Quantity;

                if (wouldPurchase > product.MaxQuantityPerUser)
                {
                    var remaining = product.MaxQuantityPerUser - (int)alreadyPurchased;
                    throw new InvalidOperationException(
                        $"Purchase limit exceeded for '{product.Name}'. " +
                        $"You may buy at most {product.MaxQuantityPerUser} unit(s) per user. " +
                        $"You have already purchased {alreadyPurchased} unit(s). " +
                        $"Remaining allowance: {Math.Max(0, remaining)}.");
                }

                productLookup[item.ProductId] = product;
            }

            // ── Step 4: Atomic batch stock decrement (ALL-OR-NOTHING) ──────────
            // FIX 1 (Ghost Stock): Both validation AND decrement happen inside one
            // Lua script. If any product is out of stock, ZERO keys are modified.
            // There is no C# rollback needed — Redis guarantees atomicity.
            var stockItems = consolidatedItems
                .Select(i => (i.ProductId, i.Quantity))
                .ToList();

            var (success, failedProductId, failureReason) =
                await _redis.DecrementStockBatchAsync(stockItems);

            if (!success)
            {
                var failedProduct = productLookup.GetValueOrDefault(failedProductId);
                var productName = failedProduct?.Name ?? $"Product {failedProductId}";
                throw new InvalidOperationException($"Cannot place order: {failureReason} for '{productName}'.");
            }

            // ── Step 5: Increment per-user quota counters ─────────────────────
            foreach (var item in consolidatedItems)
            {
                var product = productLookup[item.ProductId];
                var saleDuration = product.EndTime - now;
                // Quota key lives until the sale ends (+ 1 hour buffer)
                var quotaTtl = saleDuration.Add(TimeSpan.FromHours(1));
                if (quotaTtl < TimeSpan.FromMinutes(5)) quotaTtl = TimeSpan.FromHours(24);

                await _redis.IncrementUserPurchasedQtyAsync(userId, item.ProductId, item.Quantity, quotaTtl);

                // NOTE: Price is LOCKED HERE at the moment of the 202 response.
                // The worker uses this exact UnitPrice — it never re-reads the price from DB.
                // This prevents price discrepancy if the sale ends while the order is queued.
                orderItems.Add(new OrderQueueItem(
                    ProductId:   item.ProductId,
                    ProductName: product.Name,
                    Quantity:    item.Quantity,
                    UnitPrice:   product.DiscountPrice));   // ← price locked at this instant

                totalAmount += product.DiscountPrice * item.Quantity;
                _logger.LogInformation("Stock decremented for product {ProductId}", item.ProductId);
            }

            // ── Step 6: Write initial "Queued" status for client polling ──────
            // FIX 2 (False Promise): Clients can poll GET /api/orders/status/{orderId}
            // to learn the true final status (Confirmed/Failed) after async processing.
            var orderId = Guid.NewGuid();
            await _redis.SetOrderStatusAsync(orderId, "Queued");

            // ── Step 7: Enqueue order for async DB persistence ─────────────────
            var payload = new OrderQueuePayload(
                OrderId:        orderId,
                UserId:         userId,
                IdempotencyKey: request.IdempotencyKey,
                Items:          orderItems,
                TotalAmount:    totalAmount,
                CreatedAt:      now);

            await _redis.EnqueueOrderAsync(payload);
            _logger.LogInformation("Order {OrderId} enqueued for user {UserId}", orderId, userId);

            // ── Step 8: Return immediately ────────────────────────────────────
            var statusPollUrl = $"/api/orders/status/{orderId}";
            return new OrderAcceptedResponse(
                OrderId:        orderId,
                Message:        "Your order has been received and is being processed. Poll the status URL to confirm.",
                IdempotencyKey: request.IdempotencyKey,
                StatusPollUrl:  statusPollUrl);
        }
        catch
        {
            // ── FIX 6: Proper idempotency key cleanup on failure ──────────────
            // The previous code called SetIdempotencyKeyAsync(idemKey, 0) which is a no-op
            // because the key already exists and the NX condition prevents overwriting it.
            // Using KeyDeleteAsync allows the user to safely retry after a transient failure.
            await _redis.DeleteIdempotencyKeyAsync(idemKey);
            throw;
        }
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
}
