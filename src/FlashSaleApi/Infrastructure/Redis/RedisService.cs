using StackExchange.Redis;
using System.Text.Json;

namespace FlashSaleApi.Infrastructure.Redis;

/// <summary>
/// Central facade for all Redis interactions in the Flash Sale system.

/// Responsibilities:
///  • Stock management (atomic batch Lua-based decrement/increment)
///  • Cart storage per user (Redis Hash, 2-hour TTL + sale-end eviction)
///  • Order queue (Redis List used as a FIFO queue)
///  • Order status tracking (Redis Hash with 24h TTL for client polling)
///  • Idempotency key management (SET NX pattern)
///  • Per-user purchase quota tracking (Redis counter per user+product)
/// </summary>
public interface IRedisService
{
    // ── Stock ───────────────────────────────────────────────────────────────
    /// <summary>
    /// Atomically checks and decrements stock for ALL items in a single Lua call.
    /// Returns (true, _) on success; (false, failedProductId) if any item fails.
    /// </summary>
    Task<(bool Success, int FailedProductId, string FailureReason)> DecrementStockBatchAsync(
        IReadOnlyList<(int ProductId, int Quantity)> items);

    Task SetStockAsync(int productId, int quantity);
    Task<long?> GetStockAsync(int productId);
    Task<long?[]> GetStocksAsync(IEnumerable<int> productIds);
    Task IncrementStockAsync(int productId, int quantity);

    // ── Cart ────────────────────────────────────────────────────────────────
    Task SetCartItemAsync(string userId, int productId, int quantity, DateTime saleEndTime);
    Task<Dictionary<int, (int Quantity, DateTime SaleEndTime)>> GetCartAsync(string userId);
    Task RemoveCartItemAsync(string userId, int productId);
    Task ClearCartAsync(string userId);
    Task StripExpiredCartItemsAsync(string userId);

    // ── Order Queue ──────────────────────────────────────────────────────────
    Task EnqueueOrderAsync<T>(T payload);
    Task<T?> DequeueOrderAsync<T>(CancellationToken ct);

    // ── Order Status ─────────────────────────────────────────────────────────
    Task SetOrderStatusAsync(Guid orderId, string status, string? failureReason = null);
    Task<(string? Status, string? FailureReason, DateTime LastUpdatedAt)> GetOrderStatusAsync(Guid orderId);

    // ── Idempotency ──────────────────────────────────────────────────────────
    Task<bool> SetIdempotencyKeyAsync(string key, int ttlSeconds = 86400);
    Task DeleteIdempotencyKeyAsync(string key);

    // ── Per-User Purchase Quota ───────────────────────────────────────────────
    Task<long> GetUserPurchasedQtyAsync(string userId, int productId);
    Task IncrementUserPurchasedQtyAsync(string userId, int productId, int quantity, TimeSpan ttl);
    Task DecrementUserPurchasedQtyAsync(string userId, int productId, int quantity);
}

public class RedisService : IRedisService
{
    private readonly IDatabase _db;
    private readonly ILogger<RedisService> _logger;

    // Key prefix constants keep keys consistent across the application
    private const string StockKeyPrefix        = "stock:";
    private const string CartKeyPrefix         = "cart:";
    private const string CartEndTimeField      = "__sale_end__:";   // field suffix in cart hash
    private const string OrderQueueKey         = "order:queue";
    private const string OrderStatusKeyPrefix  = "order:status:";
    private const string IdempotencyPrefix     = "idem:";
    private const string UserQuotaKeyPrefix    = "quota:";          // quota:{userId}:{productId}

    public RedisService(IConnectionMultiplexer redis, ILogger<RedisService> logger)
    {
        _db = redis.GetDatabase();
        _logger = logger;
    }

    // ── Stock ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Atomically checks and decrements stock for ALL items in a single Lua script call.
    ///
    /// FIX for "Ghost Stock" bug:
    /// The previous implementation looped through items one-by-one and relied on C# to
    /// rollback already-decremented items if a later item failed. If the pod crashed between
    /// decrement and rollback, that stock was permanently lost.
    ///
    /// This implementation uses a two-pass Lua script: validate all → decrement all.
    /// Because it's a single Redis Lua execution, it is fully atomic — either everything
    /// succeeds or nothing changes.
    /// </summary>
    public async Task<(bool Success, int FailedProductId, string FailureReason)> DecrementStockBatchAsync(
        IReadOnlyList<(int ProductId, int Quantity)> items)
    {
        var keys = items.Select(i => (RedisKey)(StockKeyPrefix + i.ProductId)).ToArray();

        // ARGV: quantities[1..N] then N itself (so Lua knows where quantities end)
        var args = items
            .Select(i => (RedisValue)i.Quantity)
            .Append((RedisValue)items.Count)
            .ToArray();

        var result = (RedisValue[])(await _db.ScriptEvaluateAsync(
            LuaScripts.DecrementStockBatch,
            keys,
            args))!;

        var code = (long)result[0];

        if (code == 1)
            return (true, 0, string.Empty);

        // result[1] is the 1-based index of the failing item
        var failIndex = (int)(long)result[1] - 1;
        var failedProductId = items[failIndex].ProductId;
        var reason = code == -1 ? "Stock key not found in Redis" : "Insufficient stock";

        _logger.LogWarning("Batch stock decrement failed for product {ProductId}: {Reason}", failedProductId, reason);
        return (false, failedProductId, reason);
    }

    public async Task SetStockAsync(int productId, int quantity)
    {
        var key = StockKeyPrefix + productId;
        await _db.StringSetAsync(key, quantity);
        _logger.LogInformation("Seeded stock for product {ProductId}: {Quantity}", productId, quantity);
    }

    public async Task<long?> GetStockAsync(int productId)
    {
        var val = await _db.StringGetAsync(StockKeyPrefix + productId);
        return val.HasValue ? (long?)val : null;
    }

    public async Task<long?[]> GetStocksAsync(IEnumerable<int> productIds)
    {
        var keys = productIds.Select(id => (RedisKey)(StockKeyPrefix + id)).ToArray();
        var vals = await _db.StringGetAsync(keys);
        return vals.Select(v => v.HasValue ? (long?)v : null).ToArray();
    }

    /// <summary>Compensate stock on worker processing failure (rollback).</summary>
    public async Task IncrementStockAsync(int productId, int quantity)
    {
        await _db.StringIncrementAsync(StockKeyPrefix + productId, quantity);
        _logger.LogWarning("Compensated stock for product {ProductId} by +{Quantity}", productId, quantity);
    }

    // ── Cart ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores a cart item in a Redis Hash keyed by userId.
    /// Field: productId → quantity
    /// Field: __sale_end__:{productId} → UTC ticks of flash sale end time
    ///
    /// Storing the sale end time alongside the quantity enables active cart invalidation
    /// when the flash sale window closes, not just TTL-based expiry.
    /// TTL is reset to 2 hours on every write.
    /// </summary>
    public async Task SetCartItemAsync(string userId, int productId, int quantity, DateTime saleEndTime)
    {
        var cartKey = CartKeyPrefix + userId;
        // Store the quantity and sale end time in the same hash
        await _db.HashSetAsync(cartKey, productId.ToString(), quantity);
        await _db.HashSetAsync(cartKey, CartEndTimeField + productId, saleEndTime.Ticks.ToString());
        // Reset the 2-hour absolute TTL on every write
        await _db.KeyExpireAsync(cartKey, TimeSpan.FromHours(2));
    }

    /// <summary>
    /// Returns all cart items as {ProductId → (Quantity, SaleEndTime)}.
    /// Skips internal metadata fields (sale_end__ prefixed).
    /// </summary>
    public async Task<Dictionary<int, (int Quantity, DateTime SaleEndTime)>> GetCartAsync(string userId)
    {
        var entries = await _db.HashGetAllAsync(CartKeyPrefix + userId);
        var result = new Dictionary<int, (int, DateTime)>();

        // Build a lookup of end times first
        var endTimes = new Dictionary<int, DateTime>();
        foreach (var entry in entries)
        {
            var name = entry.Name.ToString();
            if (name.StartsWith(CartEndTimeField))
            {
                if (int.TryParse(name[CartEndTimeField.Length..], out var pid) &&
                    long.TryParse(entry.Value.ToString(), out var ticks))
                {
                    endTimes[pid] = new DateTime(ticks, DateTimeKind.Utc);
                }
            }
        }

        foreach (var entry in entries)
        {
            var name = entry.Name.ToString();
            if (name.StartsWith(CartEndTimeField)) continue; // skip metadata fields

            if (int.TryParse(name, out var productId) && int.TryParse(entry.Value.ToString(), out var qty))
            {
                var endTime = endTimes.GetValueOrDefault(productId, DateTime.MaxValue);
                result[productId] = (qty, endTime);
            }
        }

        return result;
    }

    public async Task RemoveCartItemAsync(string userId, int productId)
    {
        var cartKey = CartKeyPrefix + userId;
        await _db.HashDeleteAsync(cartKey, productId.ToString());
        await _db.HashDeleteAsync(cartKey, CartEndTimeField + productId);
    }

    public async Task ClearCartAsync(string userId)
        => await _db.KeyDeleteAsync(CartKeyPrefix + userId);

    /// <summary>
    /// FIX for "Incomplete Cart Invalidation" bug:
    /// Scans all cart items and removes any whose flash sale EndTime has passed.
    /// Called on every GetCart to keep the cart clean without a separate background job.
    /// </summary>
    public async Task StripExpiredCartItemsAsync(string userId)
    {
        var cartKey = CartKeyPrefix + userId;
        var entries = await _db.HashGetAllAsync(cartKey);
        var now = DateTime.UtcNow;

        foreach (var entry in entries)
        {
            var name = entry.Name.ToString();
            if (!name.StartsWith(CartEndTimeField)) continue;

            if (int.TryParse(name[CartEndTimeField.Length..], out var productId) &&
                long.TryParse(entry.Value.ToString(), out var ticks))
            {
                var endTime = new DateTime(ticks, DateTimeKind.Utc);
                if (endTime < now)
                {
                    // Sale ended — remove item and its metadata from cart
                    await _db.HashDeleteAsync(cartKey, productId.ToString());
                    await _db.HashDeleteAsync(cartKey, CartEndTimeField + productId);
                    _logger.LogInformation(
                        "Stripped expired cart item: user={UserId} product={ProductId} (sale ended {EndTime})",
                        userId, productId, endTime);
                }
            }
        }
    }

    // ── Order Queue ────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes a serialized order payload to the left end of the Redis list.
    /// The background worker consumes from the right (FIFO).
    /// </summary>
    public async Task EnqueueOrderAsync<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload);
        await _db.ListLeftPushAsync(OrderQueueKey, json);
    }

    /// <summary>
    /// Pops one item from the right end of the queue (FIFO).
    /// Returns null if the queue is empty.
    /// </summary>
    public async Task<T?> DequeueOrderAsync<T>(CancellationToken ct)
    {
        var result = await _db.ListRightPopAsync(OrderQueueKey);
        if (result.IsNullOrEmpty)
            return default;

        return JsonSerializer.Deserialize<T>(result!);
    }

    // ── Order Status ───────────────────────────────────────────────────────────

    /// <summary>
    /// FIX for "False Promise" bug:
    /// Writes the order processing status to a Redis Hash so clients can poll.
    /// Key: order:status:{orderId}
    /// Fields: status, failureReason, updatedAt
    /// TTL: 24 hours (enough time for the client to poll the result)
    /// </summary>
    public async Task SetOrderStatusAsync(Guid orderId, string status, string? failureReason = null)
    {
        var key = OrderStatusKeyPrefix + orderId;
        var fields = new HashEntry[]
        {
            new("status",        status),
            new("failureReason", failureReason ?? string.Empty),
            new("updatedAt",     DateTime.UtcNow.ToString("O"))
        };
        await _db.HashSetAsync(key, fields);
        await _db.KeyExpireAsync(key, TimeSpan.FromHours(24));
    }

    public async Task<(string? Status, string? FailureReason, DateTime LastUpdatedAt)> GetOrderStatusAsync(Guid orderId)
    {
        var key = OrderStatusKeyPrefix + orderId;
        var fields = await _db.HashGetAllAsync(key);

        if (fields.Length == 0)
            return (null, null, default);

        var dict = fields.ToDictionary(f => f.Name.ToString(), f => f.Value.ToString());
        dict.TryGetValue("status", out var status);
        dict.TryGetValue("failureReason", out var failureReason);
        dict.TryGetValue("updatedAt", out var updatedAtStr);

        DateTime.TryParse(updatedAtStr, out var updatedAt);
        return (status, failureReason, updatedAt);
    }

    // ── Idempotency ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets an idempotency key using SET NX (only if it doesn't exist).
    /// Returns true if successfully set (first-time request).
    /// Returns false if the key already existed (duplicate request).
    /// </summary>
    public async Task<bool> SetIdempotencyKeyAsync(string key, int ttlSeconds = 86400)
    {
        var redisKey = IdempotencyPrefix + key;
        var result = await _db.ScriptEvaluateAsync(
            LuaScripts.SetIdempotencyKey,
            new RedisKey[] { redisKey },
            new RedisValue[] { ttlSeconds });

        return (long)result == 1;
    }

    /// <summary>
    /// FIX for idempotency rollback bug:
    /// The previous code called SetIdempotencyKeyAsync(key, 0) on failure, but since the
    /// key already existed, the NX condition prevented it from updating, making it a no-op.
    /// This method properly deletes the key so the user can retry after a transient failure.
    /// </summary>
    public async Task DeleteIdempotencyKeyAsync(string key)
    {
        var redisKey = IdempotencyPrefix + key;
        await _db.KeyDeleteAsync(redisKey);
        _logger.LogInformation("Released idempotency key: {Key}", redisKey);
    }

    // ── Per-User Purchase Quota ────────────────────────────────────────────────

    /// <summary>
    /// FIX for "Missing Purchase Limits" bug:
    /// Returns how many units the given user has already purchased of a product
    /// during the current flash sale. Tracked in Redis to avoid DB lookups on the hot path.
    /// Key: quota:{userId}:{productId}
    /// </summary>
    public async Task<long> GetUserPurchasedQtyAsync(string userId, int productId)
    {
        var key = UserQuotaKeyPrefix + userId + ":" + productId;
        var val = await _db.StringGetAsync(key);
        return val.HasValue ? (long)val : 0L;
    }

    /// <summary>
    /// Increments the user's purchase counter after a successful stock decrement.
    /// TTL is set to the flash sale end time so the quota auto-expires with the sale.
    /// </summary>
    public async Task IncrementUserPurchasedQtyAsync(string userId, int productId, int quantity, TimeSpan ttl)
    {
        var key = UserQuotaKeyPrefix + userId + ":" + productId;
        await _db.StringIncrementAsync(key, quantity);
        await _db.KeyExpireAsync(key, ttl);
    }

    /// <summary>
    /// Decrements the user's purchase counter. Used in DB rollbacks.
    /// </summary>
    public async Task DecrementUserPurchasedQtyAsync(string userId, int productId, int quantity)
    {
        var key = UserQuotaKeyPrefix + userId + ":" + productId;
        await _db.StringDecrementAsync(key, quantity);
    }
}
