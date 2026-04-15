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
    /// BATCH atomic stock check-and-decrement for multiple products in a single
    /// Redis round-trip. Fixes the "Ghost Stock" bug where a per-item loop could
    /// partially commit, then crash before the C# rollback executes.
    ///
    /// Protocol:
    ///   KEYS[1..N]  → Redis stock keys  (e.g. "stock:1", "stock:2", ...)
    ///   ARGV[1..N]  → quantities requested (matching KEYS indices)
    ///   ARGV[N+1]   → N (number of items) passed explicitly so Lua knows the split
    ///
    /// Algorithm:
    ///   Pass 1 — validate ALL items have sufficient stock (read-only, touches nothing)
    ///   Pass 2 — decrement ALL items (only reached if pass 1 fully succeeds)
    ///
    /// Return values:
    ///   { 1 }         → all decremented successfully
    ///   { -1, idx }   → KEYS[idx] does not exist in Redis
    ///   { -2, idx }   → KEYS[idx] has insufficient stock
    ///
    /// Because both passes run inside a single Lua script, Redis guarantees
    /// atomicity: no other client can read or write those keys in between.
    /// </summary>
    public const string DecrementStockBatch = @"
local n = tonumber(ARGV[#ARGV])

-- Pass 1: validate all items (read-only, no side effects)
for i = 1, n do
    local stock = tonumber(redis.call('GET', KEYS[i]))
    if stock == nil then
        return { -1, i }
    end
    if stock < tonumber(ARGV[i]) then
        return { -2, i }
    end
end

-- Pass 2: decrement all items (all validations passed)
for i = 1, n do
    redis.call('DECRBY', KEYS[i], ARGV[i])
end

return { 1 }
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
