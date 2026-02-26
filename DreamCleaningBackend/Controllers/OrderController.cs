using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;

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
        private readonly IStripeService _stripeService;
        private readonly IEmailService _emailService;

        public OrderController(IOrderService orderService, ApplicationDbContext context, IAuditService auditService, IStripeService stripeService, IEmailService emailService)
        {
            _orderService = orderService;
            _context = context;
            _auditService = auditService;
            _stripeService = stripeService;
            _emailService = emailService;
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

                // Get the order before update - INCLUDE ALL navigation properties for audit
                var orderBefore = await _context.Orders
                    .Include(o => o.OrderServices)
                        .ThenInclude(os => os.Service)
                    .Include(o => o.OrderExtraServices)
                        .ThenInclude(oes => oes.ExtraService)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId);

                if (orderBefore == null)
                    return NotFound();

                // Perform the update
                var order = await _orderService.UpdateOrder(orderId, userId, updateOrderDto);

                // Clear the change tracker to ensure fresh data
                _context.ChangeTracker.Clear();

                // Get the order after update - INCLUDE ALL navigation properties for audit
                var orderAfter = await _context.Orders
                    .Include(o => o.OrderServices)
                        .ThenInclude(os => os.Service)
                    .Include(o => o.OrderExtraServices)
                        .ThenInclude(oes => oes.ExtraService)
                    .AsNoTracking()
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
                        Console.WriteLine($"Stack trace: {ex.StackTrace}");
                    }
                }

                return Ok(order);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{orderId}/create-update-payment")]
        public async Task<ActionResult> CreateUpdatePayment(int orderId, UpdateOrderDto updateOrderDto)
        {
            try
            {
                var userId = GetUserId();
                var result = await _orderService.CreateUpdatePaymentIntent(orderId, userId, updateOrderDto);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{orderId}/confirm-update-payment")]
        public async Task<ActionResult> ConfirmUpdatePayment(int orderId, [FromBody] ConfirmUpdatePaymentDto dto)
        {
            try
            {
                var userId = GetUserId();

                // Verify payment with Stripe
                var paymentIntent = await _stripeService.GetPaymentIntentAsync(dto.PaymentIntentId);
                if (paymentIntent.Status != "succeeded")
                {
                    return BadRequest(new { message = "Payment not completed" });
                }

                // Get the order before update - INCLUDE ALL navigation properties for audit
                var orderBefore = await _context.Orders
                    .Include(o => o.OrderServices)
                        .ThenInclude(os => os.Service)
                    .Include(o => o.OrderExtraServices)
                        .ThenInclude(oes => oes.ExtraService)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId);

                if (orderBefore == null)
                    return NotFound();

                // Perform the update
                var updatedOrder = await _orderService.UpdateOrder(orderId, userId, dto.UpdateOrderData);

                // Mark the update history as paid
                var updateHistory = await _context.OrderUpdateHistories
                    .Where(h => h.OrderId == orderId && !h.IsPaid)
                    .OrderByDescending(h => h.UpdatedAt)
                    .FirstOrDefaultAsync();

                if (updateHistory != null)
                {
                    updateHistory.PaymentIntentId = dto.PaymentIntentId;
                    updateHistory.IsPaid = true;
                    updateHistory.PaidAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                }

                // Clear the change tracker to ensure fresh data
                _context.ChangeTracker.Clear();

                // Get the order after update - INCLUDE ALL navigation properties for audit
                var orderAfter = await _context.Orders
                    .Include(o => o.OrderServices)
                        .ThenInclude(os => os.Service)
                    .Include(o => o.OrderExtraServices)
                        .ThenInclude(oes => oes.ExtraService)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == orderId);

                if (orderAfter != null)
                {
                    try
                    {
                        await _auditService.LogUpdateAsync(orderBefore, orderAfter);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Audit logging failed: {ex.Message}");
                        Console.WriteLine($"Stack trace: {ex.StackTrace}");
                    }
                }

                return Ok(updatedOrder);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Creates a Stripe PaymentIntent for any pending (unpaid) additional payments created by prior order updates.
        /// This is used when an admin/superadmin increases the order total and the customer needs to pay the difference later.
        /// </summary>
        [HttpPost("{orderId}/create-pending-update-payment-intent")]
        public async Task<ActionResult> CreatePendingUpdatePaymentIntent(int orderId)
        {
            try
            {
                var userId = GetUserId();

                var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId);
                if (order == null)
                    return NotFound(new { message = "Order not found" });

                if (order.Status == "Cancelled" || order.Status == "Done")
                    return BadRequest(new { message = $"Cannot pay for a {order.Status.ToLower()} order" });

                // Use same unpaid amount as displayed: (current total − tips) − (original total − tips) − already paid
                var currentWithoutTips = order.Total - order.Tips - order.CompanyDevelopmentTips;
                decimal originalWithoutTips;
                if (order.InitialTotal != 0m || order.InitialTips != 0m || order.InitialCompanyDevelopmentTips != 0m)
                    originalWithoutTips = order.InitialTotal - order.InitialTips - order.InitialCompanyDevelopmentTips;
                else
                {
                    var firstHist = await _context.OrderUpdateHistories
                        .Where(h => h.OrderId == orderId)
                        .OrderBy(h => h.UpdatedAt)
                        .Select(h => (decimal?)(h.OriginalTotal - h.OriginalTips - h.OriginalCompanyDevelopmentTips))
                        .FirstOrDefaultAsync();
                    originalWithoutTips = firstHist ?? 0m;
                }
                var totalDelta = Math.Max(0m, currentWithoutTips - originalWithoutTips);
                var alreadyPaid = await _context.OrderUpdateHistories
                    .Where(h => h.OrderId == orderId && h.IsPaid)
                    .SumAsync(h => h.AdditionalAmount);
                var amountToCharge = Math.Max(0m, totalDelta - alreadyPaid);
                amountToCharge = Math.Round(amountToCharge, 2);

                if (amountToCharge < 0.01m)
                    return BadRequest(new { message = "No pending additional payment found for this order" });

                var unpaidHistories = await _context.OrderUpdateHistories
                    .Where(h => h.OrderId == orderId && !h.IsPaid && h.AdditionalAmount > 0.01m)
                    .OrderByDescending(h => h.UpdatedAt)
                    .ToListAsync();

                var metadata = new Dictionary<string, string>
                {
                    { "orderId", order.Id.ToString() },
                    { "userId", userId.ToString() },
                    { "type", "order_update" },
                    { "additionalAmount", amountToCharge.ToString("F2") }
                };

                var paymentIntent = await _stripeService.CreatePaymentIntentAsync(amountToCharge, metadata);

                // Attach the payment intent id to all unpaid histories so we can mark them paid when they pay this amount
                foreach (var h in unpaidHistories)
                {
                    h.PaymentIntentId = paymentIntent.Id;
                }
                await _context.SaveChangesAsync();

                return Ok(new OrderUpdatePaymentDto
                {
                    OrderId = order.Id,
                    AdditionalAmount = amountToCharge,
                    UpdateHistoryId = unpaidHistories.FirstOrDefault()?.Id,
                    PaymentIntentId = paymentIntent.Id,
                    PaymentClientSecret = paymentIntent.ClientSecret
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Confirms a pending additional payment (created by prior order updates) and marks the related update-history rows as paid.
        /// </summary>
        [HttpPost("{orderId}/confirm-pending-update-payment")]
        public async Task<ActionResult> ConfirmPendingUpdatePayment(int orderId, [FromBody] ConfirmPendingUpdatePaymentDto dto)
        {
            try
            {
                var userId = GetUserId();

                var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId);
                if (order == null)
                    return NotFound(new { message = "Order not found" });

                // Verify payment with Stripe
                var paymentIntent = await _stripeService.GetPaymentIntentAsync(dto.PaymentIntentId);
                if (paymentIntent.Status != "succeeded")
                {
                    return BadRequest(new { message = "Payment not completed" });
                }

                var historiesToMarkPaid = await _context.OrderUpdateHistories
                    .Where(h => h.OrderId == orderId && !h.IsPaid && h.PaymentIntentId == dto.PaymentIntentId)
                    .ToListAsync();

                if (!historiesToMarkPaid.Any())
                {
                    // Fallback: mark the latest unpaid history if (for any reason) the PaymentIntentId wasn't stored yet.
                    var latest = await _context.OrderUpdateHistories
                        .Where(h => h.OrderId == orderId && !h.IsPaid && h.AdditionalAmount > 0.01m)
                        .OrderByDescending(h => h.UpdatedAt)
                        .FirstOrDefaultAsync();

                    if (latest == null)
                        return BadRequest(new { message = "No pending additional payment found for this order" });

                    latest.PaymentIntentId = dto.PaymentIntentId;
                    historiesToMarkPaid.Add(latest);
                }

                var amountPaid = historiesToMarkPaid.Sum(h => h.AdditionalAmount);

                foreach (var h in historiesToMarkPaid)
                {
                    h.IsPaid = true;
                    h.PaidAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();

                // Notify company that customer paid the additional amount
                if (amountPaid > 0.01m)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _emailService.SendCompanyAdditionalPaymentReceivedAsync(
                                order.Id,
                                order.ContactEmail ?? "",
                                $"{order.ContactFirstName} {order.ContactLastName}".Trim(),
                                amountPaid
                            );
                        }
                        catch { /* best-effort */ }
                    });
                }

                // If there are no more unpaid update-payments, switch status Pending -> Active.
                // (Don't touch Done/Cancelled.)
                var hasRemainingUnpaid = await _context.OrderUpdateHistories.AnyAsync(h =>
                    h.OrderId == orderId &&
                    !h.IsPaid &&
                    h.AdditionalAmount > 0.01m);

                if (!hasRemainingUnpaid &&
                    order.IsPaid &&
                    string.Equals(order.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                {
                    order.Status = "Active";
                    await _context.SaveChangesAsync();
                }

                // Return refreshed order details (pending amount should now be 0)
                var refreshed = await _orderService.GetOrderById(orderId, userId);
                return Ok(refreshed);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{orderId}/cancel")]
        public async Task<ActionResult> CancelOrder(int orderId, [FromBody] CancelOrderDto cancelOrderDto)
        {
            try
            {
                if (cancelOrderDto == null || string.IsNullOrWhiteSpace(cancelOrderDto.Reason))
                {
                    return BadRequest(new { message = "Cancellation reason is required" });
                }

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