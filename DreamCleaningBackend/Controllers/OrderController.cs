using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class OrderController : ControllerBase
    {
        private readonly IOrderService _orderService;
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _auditService;

        public OrderController(IOrderService orderService, ApplicationDbContext context, IAuditService auditService)
        {
            _orderService = orderService;
            _context = context;
            _auditService = auditService;
        }

        [HttpGet]
        public async Task<ActionResult> GetUserOrders()
        {
            try
            {
                var userId = GetUserId();
                var orders = await _orderService.GetUserOrders(userId);
                return Ok(orders);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("{orderId}")]
        public async Task<ActionResult> GetOrderById(int orderId)
        {
            try
            {
                var userId = GetUserId();
                var order = await _orderService.GetOrderById(orderId, userId);
                return Ok(order);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut("{orderId}")]
        public async Task<ActionResult> UpdateOrder(int orderId, UpdateOrderDto updateOrderDto)
        {
            try
            {
                var userId = GetUserId();

                // Get the order before update
                var orderBefore = await _context.Orders.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId);

                if (orderBefore == null)
                    return NotFound();

                var order = await _orderService.UpdateOrder(orderId, userId, updateOrderDto);

                // Get the order after update
                var orderAfter = await _context.Orders
                    .FirstOrDefaultAsync(o => o.Id == orderId);

                // Log the update
                if (orderAfter != null)
                {
                    try
                    {
                        await _auditService.LogUpdateAsync(orderBefore, orderAfter);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Audit logging failed: {ex.Message}");
                    }
                }

                return Ok(order);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{orderId}/cancel")]
        public async Task<ActionResult> CancelOrder(int orderId, CancelOrderDto cancelOrderDto)
        {
            try
            {
                var userId = GetUserId();

                // Get the order before cancellation
                var orderBefore = await _context.Orders.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId);

                if (orderBefore == null)
                    return NotFound();

                // Call the service to cancel (which will now save the reason)
                await _orderService.CancelOrder(orderId, userId, cancelOrderDto);

                // Get the order after cancellation
                var orderAfter = await _context.Orders
                    .FirstOrDefaultAsync(o => o.Id == orderId);

                // Log the update with cancellation reason
                if (orderAfter != null)
                {
                    try
                    {
                        // Create a copy for audit that includes the cancellation reason
                        var auditOrderBefore = new Order
                        {
                            Id = orderBefore.Id,
                            Status = orderBefore.Status,
                            CancellationReason = orderBefore.CancellationReason,
                            UpdatedAt = orderBefore.UpdatedAt,
                            UserId = orderBefore.UserId,
                            Total = orderBefore.Total,
                            ServiceDate = orderBefore.ServiceDate,
                            ContactEmail = orderBefore.ContactEmail
                        };

                        var auditOrderAfter = new Order
                        {
                            Id = orderAfter.Id,
                            Status = orderAfter.Status,
                            CancellationReason = orderAfter.CancellationReason,
                            UpdatedAt = orderAfter.UpdatedAt,
                            UserId = orderAfter.UserId,
                            Total = orderAfter.Total,
                            ServiceDate = orderAfter.ServiceDate,
                            ContactEmail = orderAfter.ContactEmail
                        };

                        await _auditService.LogUpdateAsync(auditOrderBefore, auditOrderAfter);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Audit logging failed: {ex.Message}");
                    }
                }

                return Ok(new { message = "Order cancelled successfully. Refund will be processed within 7 working days." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{orderId}/calculate-additional")]
        public async Task<ActionResult> CalculateAdditionalAmount(int orderId, UpdateOrderDto updateOrderDto)
        {
            try
            {
                var userId = GetUserId();

                // Validate the DTO
                if (updateOrderDto == null)
                {
                    return BadRequest(new { message = "Invalid request data" });
                }

                if (updateOrderDto.Services == null || !updateOrderDto.Services.Any())
                {
                    return BadRequest(new { message = "Services are required" });
                }

                // Check if the order belongs to the user
                var order = await _orderService.GetOrderById(orderId, userId);
                if (order == null)
                {
                    return NotFound(new { message = "Order not found" });
                }

                var additionalAmount = await _orderService.CalculateAdditionalAmount(orderId, updateOrderDto);
                return Ok(new { additionalAmount });
            }
            catch (Exception ex)
            {
                // Log the exception details for debugging
                Console.WriteLine($"Error calculating additional amount: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");

                return BadRequest(new { message = $"Failed to calculate additional amount: {ex.Message}" });
            }
        }

        private int GetUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                throw new Exception("Invalid user");

            return userId;
        }
    }
}