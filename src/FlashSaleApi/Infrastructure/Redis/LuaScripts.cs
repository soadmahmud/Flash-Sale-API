namespace FlashSaleApi.Infrastructure.Redis;

/// <summary>
/// Contains all Lua scripts used for atomic Redis operations.
///
/// Why Lua scripts?
/// ────────────────
/// Redis executes Lua scripts atomically — no other command can run between
/// the script's read and write operations. This eliminates the classic
/// race condition:
///
///   Thread A reads stock = 1
///   Thread B reads stock = 1
///   Thread A decrements → stock = 0 ✅
///   Thread B decrements → stock = -1 ❌ (oversell!)
///
/// With a Lua script the entire check-and-decrement is one indivisible unit.
/// </summary>
public static class LuaScripts
{
    /// <summary>
    /// Atomically checks stock and decrements if sufficient.
    ///
    /// KEYS[1]  → Redis key for the product's stock, e.g. "stock:42"
    /// ARGV[1]  → quantity requested (as string)
    ///
    /// Return values:
    ///   -1  → key does not exist (product not seeded or already fully depleted)
    ///   -2  → insufficient stock (requested > available)
    ///    N  → remaining stock after decrement (N >= 0)
    /// </summary>
    public const string DecrementStock = @"
local stock = tonumber(redis.call('GET', KEYS[1]))
if stock == nil then
    return -1
end
if stock < tonumber(ARGV[1]) then
    return -2
end
return redis.call('DECRBY', KEYS[1], ARGV[1])
";

    /// <summary>
    /// Atomically sets an idempotency key only if it does NOT already exist (SET NX).
    ///
    /// KEYS[1]  → idempotency key, e.g. "idem:user42:orderABC"
    /// ARGV[1]  → TTL in seconds (typically 86400 = 24h)
    ///
    /// Returns:
    ///   1  → key was set (first time, proceed)
    ///   0  → key already existed (duplicate request, reject)
    /// </summary>
    public const string SetIdempotencyKey = @"
local set = redis.call('SET', KEYS[1], '1', 'NX', 'EX', ARGV[1])
if set then
    return 1
else
    return 0
end
";
}
