using FlashSaleApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FlashSaleApi.Controllers;

/// <summary>
/// Provides endpoints for querying active flash sale products.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class FlashSaleController : ControllerBase
{
    private readonly IFlashSaleService _service;

    public FlashSaleController(IFlashSaleService service)
    {
        _service = service;
    }

    /// <summary>
    /// Returns all currently active flash sale products with live stock counts.
    /// </summary>
    /// <remarks>
    /// A product is "active" when the current UTC time falls within its [StartTime, EndTime] window.
    /// Stock remaining is read directly from Redis for real-time accuracy.
    /// </remarks>
    /// <response code="200">List of active flash sale products (may be empty).</response>
    [HttpGet("active")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActiveProducts()
    {
        var products = await _service.GetActiveProductsAsync();
        return Ok(products);
    }
}
