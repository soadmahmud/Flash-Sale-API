using FlashSaleApi.DTOs.Requests;
using FlashSaleApi.DTOs.Responses;

namespace FlashSaleApi.Services.Interfaces;

public interface IOrderService
{
    /// <summary>
    /// Validates request, atomically decrements Redis stock, checks idempotency,
    /// and pushes the order to the processing queue.
    /// Returns 202 Accepted immediately without waiting for DB persistence.
    /// </summary>
    Task<OrderAcceptedResponse> PlaceOrderAsync(string userId, PlaceOrderRequest request);

    Task<IEnumerable<OrderResponse>> GetUserOrdersAsync(string userId);
}
