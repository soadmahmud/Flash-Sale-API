using FlashSaleApi.DTOs.Requests;
using FlashSaleApi.DTOs.Responses;

namespace FlashSaleApi.Services.Interfaces;

public interface ICartService
{
    /// <summary>
    /// Adds or updates a product in the user's cart.
    /// Validates that the product is currently in an active flash sale window.
    /// </summary>
    Task<CartResponse> AddToCartAsync(string userId, AddToCartRequest request);

    Task<CartResponse?> GetCartAsync(string userId);

    Task ClearCartAsync(string userId);

    Task RemoveItemFromCartAsync(string userId, int productId);
}
