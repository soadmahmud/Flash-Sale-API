using FlashSaleApi.Infrastructure.Data;
using FlashSaleApi.Models;
using FlashSaleApi.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FlashSaleApi.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IOrderRepository"/>.
/// Write operations use the tracked context for change detection.
/// Read operations use AsNoTracking for performance.
/// </summary>
public class OrderRepository : IOrderRepository
{
    private readonly AppDbContext _db;

    public OrderRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Order> CreateOrderAsync(Order order)
    {
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }

    public async Task<IEnumerable<Order>> GetOrdersByUserIdAsync(string userId)
    {
        return await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
                .ThenInclude(oi => oi.Product)
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
    }

    public async Task<Order?> GetOrderByIdAsync(Guid orderId)
    {
        return await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
                .ThenInclude(oi => oi.Product)
            .FirstOrDefaultAsync(o => o.Id == orderId);
    }

    public async Task UpdateOrderStatusAsync(Guid orderId, OrderStatus status)
    {
        // Use ExecuteUpdateAsync for a targeted UPDATE without loading the entity
        await _db.Orders
            .Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, status));
    }
}
