using FlashSaleApi.Infrastructure.Data;
using FlashSaleApi.Infrastructure.Redis;
using FlashSaleApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FlashSaleApi.Workers;

/// <summary>
/// Background worker that continuously drains the Redis order queue
/// and persists orders to PostgreSQL.
///
/// Why a separate worker?
/// ──────────────────────
/// The HTTP request handler returns 202 Accepted in &lt; 10ms.
/// This worker handles the slower PostgreSQL write in the background,
/// decoupling request latency from database performance.
///
/// Failure handling:
/// ─────────────────
/// If a DB write fails, the worker logs the error and increments the stock
/// back in Redis (compensation / rollback). The order ID is logged so it can
/// be re-processed manually or via a dead-letter queue.
///
/// Scaling:
/// ---------
/// Multiple instances of this worker (or multiple replicas of the service)
/// can consume from the same Redis queue safely because RPOP is atomic.
/// </summary>
public class OrderProcessingWorker : BackgroundService
{
    // Use IServiceScopeFactory because DbContext is scoped,
    // and BackgroundService is a singleton.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRedisService _redis;
    private readonly ILogger<OrderProcessingWorker> _logger;

    // Delay between polls when the queue is empty (avoids busy-spinning)
    private static readonly TimeSpan EmptyQueueDelay = TimeSpan.FromMilliseconds(200);

    public OrderProcessingWorker(
        IServiceScopeFactory scopeFactory,
        IRedisService redis,
        ILogger<OrderProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OrderProcessingWorker started. Listening on Redis queue...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Try to dequeue one order payload
                var payload = await _redis.DequeueOrderAsync<OrderQueuePayload>(stoppingToken);

                if (payload is null)
                {
                    // Queue is empty — wait a bit before polling again
                    await Task.Delay(EmptyQueueDelay, stoppingToken);
                    continue;
                }

                _logger.LogInformation("Processing order {OrderId} for user {UserId}",
                    payload.OrderId, payload.UserId);

                await ProcessOrderAsync(payload, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown — swallow and exit
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in OrderProcessingWorker loop. Continuing...");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        _logger.LogInformation("OrderProcessingWorker stopped.");
    }

    private async Task ProcessOrderAsync(OrderQueuePayload payload, CancellationToken ct)
    {
        // Create a new DI scope so we get a fresh DbContext for each order
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            // Build the Order entity
            var order = new Order
            {
                Id             = payload.OrderId,
                UserId         = payload.UserId,
                IdempotencyKey = payload.IdempotencyKey,
                TotalAmount    = payload.TotalAmount,
                CreatedAt      = payload.CreatedAt,
                Status         = OrderStatus.Confirmed,
                Items = payload.Items.Select(i => new OrderItem
                {
                    ProductId = i.ProductId,
                    Quantity  = i.Quantity,
                    UnitPrice = i.UnitPrice
                }).ToList()
            };

            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);

            // Clear the user's cart now that order is confirmed
            await _redis.ClearCartAsync(payload.UserId);

            _logger.LogInformation("Order {OrderId} confirmed and persisted to DB", payload.OrderId);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Idempotency: order already exists in DB (e.g. worker restarted mid-write)
            _logger.LogWarning("Order {OrderId} already exists in DB. Skipping duplicate write.", payload.OrderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist order {OrderId}. Rolling back Redis stock...", payload.OrderId);

            // Compensate: restore stock for each item
            foreach (var item in payload.Items)
            {
                await _redis.IncrementStockAsync(item.ProductId, item.Quantity);
                _logger.LogWarning("Compensated stock: product {ProductId} +{Qty}", item.ProductId, item.Quantity);
            }
        }
    }

    /// <summary>
    /// Detects unique constraint violations (e.g. duplicate IdempotencyKey in DB).
    /// The exact exception type differs between database providers.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("unique", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("23505"); // PostgreSQL SQLSTATE for unique_violation
    }
}
