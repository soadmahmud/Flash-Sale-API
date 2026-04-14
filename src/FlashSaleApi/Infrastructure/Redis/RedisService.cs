using StackExchange.Redis;
using System.Text.Json;

namespace FlashSaleApi.Infrastructure.Redis;

/// <summary>
/// Central facade for all Redis interactions in the Flash Sale system.
///
/// Responsibilities:
///  • Stock management (atomic Lua-based decrement/increment)
///  • Cart storage per user (Redis Hash, 2-hour TTL)
///  • Order queue (Redis List used as a FIFO queue)
///  • Idempotency key management (SET NX pattern)
/// </summary>
public interface IRedisService
{
    // ── Stock ───────────────────────────────────────────────────────────────
    Task<long> DecrementStockAsync(int productId, int quantity);
    Task SetStockAsync(int productId, int quantity);
    Task<long?> GetStockAsync(int productId);
    Task IncrementStockAsync(int productId, int quantity); // compensate on failure

    // ── Cart ────────────────────────────────────────────────────────────────
    Task SetCartItemAsync(string userId, int productId, int quantity);
    Task<Dictionary<int, int>> GetCartAsync(string userId);
    Task RemoveCartItemAsync(string userId, int productId);
    Task ClearCartAsync(string userId);

    // ── Order Queue ──────────────────────────────────────────────────────────
    Task EnqueueOrderAsync<T>(T payload);
    Task<T?> DequeueOrderAsync<T>(CancellationToken ct);

    // ── Idempotency ──────────────────────────────────────────────────────────
    Task<bool> SetIdempotencyKeyAsync(string key, int ttlSeconds = 86400);
}

public class RedisService : IRedisService
{
    private readonly IDatabase _db;
    private readonly ILogger<RedisService> _logger;

    // Key prefix constants keep keys consistent across the application
    private const string StockKeyPrefix      = "stock:";
    private const string CartKeyPrefix       = "cart:";
    private const string OrderQueueKey       = "order:queue";
    private const string IdempotencyPrefix   = "idem:";

    public RedisService(IConnectionMultiplexer redis, ILogger<RedisService> logger)
    {
        _db = redis.GetDatabase();
        _logger = logger;
    }

    // ── Stock ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Atomically decrements Redis stock using Lua script.
    /// Returns the remaining stock, or a negative sentinel:
    ///   -1 = key not found (product not seeded)
    ///   -2 = insufficient stock
    /// </summary>
    public async Task<long> DecrementStockAsync(int productId, int quantity)
    {
        var key = StockKeyPrefix + productId;
        var result = await _db.ScriptEvaluateAsync(
            LuaScripts.DecrementStock,
            new RedisKey[] { key },
            new RedisValue[] { quantity });

        return (long)result;
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

    /// <summary>Compensate stock on worker processing failure (rollback).</summary>
    public async Task IncrementStockAsync(int productId, int quantity)
    {
        await _db.StringIncrementAsync(StockKeyPrefix + productId, quantity);
        _logger.LogWarning("Compensated stock for product {ProductId} by {Quantity}", productId, quantity);
    }

    // ── Cart ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores a cart item in a Redis Hash keyed by userId.
    /// The hash field is the productId; the value is the quantity.
    /// TTL is reset to 2 hours on every write.
    /// </summary>
    public async Task SetCartItemAsync(string userId, int productId, int quantity)
    {
        var cartKey = CartKeyPrefix + userId;
        await _db.HashSetAsync(cartKey, productId.ToString(), quantity);
        await _db.KeyExpireAsync(cartKey, TimeSpan.FromHours(2));
    }

    public async Task<Dictionary<int, int>> GetCartAsync(string userId)
    {
        var entries = await _db.HashGetAllAsync(CartKeyPrefix + userId);
        return entries.ToDictionary(
            e => int.Parse(e.Name!),
            e => (int)e.Value);
    }

    public async Task RemoveCartItemAsync(string userId, int productId)
        => await _db.HashDeleteAsync(CartKeyPrefix + userId, productId.ToString());

    public async Task ClearCartAsync(string userId)
        => await _db.KeyDeleteAsync(CartKeyPrefix + userId);

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
    /// Blocks for up to 5 seconds waiting for an item on the queue (BRPOP).
    /// Returns null if nothing arrives within the timeout.
    /// This prevents the worker from busy-spinning when the queue is empty.
    /// </summary>
    public async Task<T?> DequeueOrderAsync<T>(CancellationToken ct)
    {
        // BRPOP returns [key, value]; we want index 1
        var result = await _db.ListRightPopAsync(OrderQueueKey);

        if (result.IsNullOrEmpty)
            return default;

        return JsonSerializer.Deserialize<T>(result!);
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
}
