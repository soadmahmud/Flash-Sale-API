using FlashSaleApi.Models;

namespace FlashSaleApi.Repositories.Interfaces;

/// <summary>Data access contract for orders.</summary>
public interface IOrderRepository
{
    Task<Order> CreateOrderAsync(Order order);

    Task<IEnumerable<Order>> GetOrdersByUserIdAsync(string userId);

    Task<Order?> GetOrderByIdAsync(Guid orderId);

    Task UpdateOrderStatusAsync(Guid orderId, OrderStatus status);
}
