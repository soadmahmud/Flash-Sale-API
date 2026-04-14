using System.ComponentModel.DataAnnotations;

namespace FlashSaleApi.DTOs.Requests;

/// <summary>Request payload for adding an item to the cart.</summary>
public record AddToCartRequest(
    [property: Range(1, int.MaxValue, ErrorMessage = "ProductId must be positive.")]
    int ProductId,

    [property: Range(1, 10, ErrorMessage = "Quantity must be between 1 and 10.")]
    int Quantity
);
