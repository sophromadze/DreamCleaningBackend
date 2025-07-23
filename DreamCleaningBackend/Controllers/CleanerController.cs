using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/cleaner")]
    [ApiController]
    [Authorize(Roles = "Cleaner,Admin,SuperAdmin,Moderator")]
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
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            var userRole = User.FindFirst("Role")?.Value ?? "";
            
            // For non-cleaner roles, show all orders. For cleaner role, show only assigned orders
            var calendar = await _cleanerService.GetCleanerCalendarAsync(userId, userRole);
            return Ok(calendar);
        }

        [HttpGet("orders/{orderId}")]
        public async Task<ActionResult<CleanerOrderDetailDto>> GetOrderDetails(int orderId)
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            var userRole = User.FindFirst("Role")?.Value ?? "";
            
            var orderDetails = await _cleanerService.GetOrderDetailsForCleanerAsync(orderId, userId, userRole);

            if (orderDetails == null)
                return NotFound("Order not found");

            return Ok(orderDetails);
        }
    }
}