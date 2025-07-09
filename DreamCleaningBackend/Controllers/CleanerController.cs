using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/cleaner")]
    [ApiController]
    [Authorize(Roles = "Cleaner")]
    public class CleanerController : ControllerBase
    {
        private readonly ICleanerService _cleanerService;

        public CleanerController(ICleanerService cleanerService)
        {
            _cleanerService = cleanerService;
        }

        [HttpGet("calendar")]
        public async Task<ActionResult<List<CleanerCalendarDto>>> GetCalendar()
        {
            var cleanerId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            var calendar = await _cleanerService.GetCleanerCalendarAsync(cleanerId);
            return Ok(calendar);
        }

        [HttpGet("orders/{orderId}")]
        public async Task<ActionResult<CleanerOrderDetailDto>> GetOrderDetails(int orderId)
        {
            var cleanerId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            var orderDetails = await _cleanerService.GetOrderDetailsForCleanerAsync(orderId, cleanerId);

            if (orderDetails == null)
                return NotFound("Order not found or not assigned to you");

            return Ok(orderDetails);
        }
    }
}