using FlashSaleApi.DTOs.Requests;
using FlashSaleApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FlashSaleApi.Controllers;

/// <summary>
/// Manages user shopping carts stored in Redis.
/// Cart data expires automatically after 2 hours of inactivity.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class CartController : ControllerBase
{
    private readonly ICartService _service;

    public CartController(ICartService service)
    {
        _service = service;
    }

    /// <summary>
    /// Retrieves the current cart for a user.
    /// </summary>
    /// <param name="userId">The user identifier (passed as route parameter for simplicity).</param>
    /// <response code="200">Cart contents with estimated total.</response>
    /// <response code="404">No active cart found for this user.</response>
    [HttpGet("{userId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCart([FromRoute] string userId)
    {
        var cart = await _service.GetCartAsync(userId);
        if (cart is null)
            return NotFound(new { message = $"No active cart found for user '{userId}'." });

        return Ok(cart);
    }

    /// <summary>
    /// Adds or updates an item in the user's cart.
    /// The user ID is taken from the X-User-Id request header.
    /// </summary>
    /// <response code="200">Updated cart after adding the item.</response>
    /// <response code="400">Invalid product or quantity.</response>
    /// <response code="404">Product not found.</response>
    /// <response code="409">Product is out of stock or not in active flash sale.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddToCart(
        [FromHeader(Name = "X-User-Id")] string userId,
        [FromBody] AddToCartRequest request)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest(new { message = "X-User-Id header is required." });

        var cart = await _service.AddToCartAsync(userId, request);
        return Ok(cart);
    }

    /// <summary>Removes a single product from the user's cart.</summary>
    /// <response code="204">Item removed successfully.</response>
    [HttpDelete("{userId}/items/{productId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveItem(
        [FromRoute] string userId,
        [FromRoute] int productId)
    {
        await _service.RemoveItemFromCartAsync(userId, productId);
        return NoContent();
    }

    /// <summary>Clears the entire cart for a user.</summary>
    /// <response code="204">Cart was cleared.</response>
    [HttpDelete("{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearCart([FromRoute] string userId)
    {
        await _service.ClearCartAsync(userId);
        return NoContent();
    }
}
