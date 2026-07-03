using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Hubs;
using System.Linq;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>Order management: list/details/status/cancel/edits, cleaner assignment, payment reminders, order reminders.
    /// Split out of the monolithic AdminController; same api/admin route prefix, so URLs are unchanged.</summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminOrdersController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IOrderService _orderService;
        private readonly IAuditService _auditService;
        private readonly IConfiguration _configuration;
        private readonly ICleanerService _cleanerService;
        private readonly IEmailService _emailService;
        private readonly ISmsService _smsService;
        private readonly IHubContext<UserManagementHub> _hubContext;
        private readonly IBubblePointsService _bubblePointsService;
        private readonly ILoyaltyDiscountService _loyaltyDiscountService;
        private readonly IOrderTransferService _orderTransferService;
        private readonly ILogger<AdminOrdersController> _logger;

        public AdminOrdersController(ApplicationDbContext context,
            IOrderService orderService,
            IAuditService auditService,
            IConfiguration configuration,
            ICleanerService cleanerService,
            IEmailService emailService,
            ISmsService smsService,
            IHubContext<UserManagementHub> hubContext,
            IBubblePointsService bubblePointsService,
            ILoyaltyDiscountService loyaltyDiscountService,
            IOrderTransferService orderTransferService,
            ILogger<AdminOrdersController> logger)
        {
            _logger = logger;
            _context = context;
            _orderService = orderService;
            _auditService = auditService;
            _configuration = configuration;
            _cleanerService = cleanerService;
            _emailService = emailService;
            _smsService = smsService;
            _hubContext = hubContext;
            _bubblePointsService = bubblePointsService;
            _loyaltyDiscountService = loyaltyDiscountService;
            _orderTransferService = orderTransferService;
        }

        // ── SuperAdmin order transfer (move an order between user accounts, undoable) ──

        /// <summary>SuperAdmin-only: move an order — and everything it gave its current owner
        /// (points, spent amount, first-time flag, photos, service address) — to another user.</summary>
        [HttpPost("orders/{orderId}/transfer")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<OrderTransferDto>> TransferOrder(int orderId, [FromBody] TransferOrderRequestDto dto)
        {
            try
            {
                var result = await _orderTransferService.TransferAsync(orderId, dto.TargetUserId, GetCurrentUserId(), dto.Notes);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>SuperAdmin-only: transfer history for one order (newest first).</summary>
        [HttpGet("orders/{orderId}/transfers")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<List<OrderTransferDto>>> GetOrderTransfers(int orderId)
        {
            return Ok(await _orderTransferService.GetTransfersForOrderAsync(orderId));
        }

        /// <summary>SuperAdmin-only: revert a transfer using its recorded snapshot.</summary>
        [HttpPost("order-transfers/{transferId}/undo")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<OrderTransferDto>> UndoOrderTransfer(int transferId)
        {
            try
            {
                var result = await _orderTransferService.UndoAsync(transferId, GetCurrentUserId());
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Orders Management
        [HttpGet("orders")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<OrderListDto>>> GetAllOrders()
        {
            try
            {
                var orders = await _orderService.GetAllOrdersForAdmin();
                return Ok(orders);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("orders/{orderId}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<OrderDto>> GetOrderDetails(int orderId)
        {
            try
            {
                // For admin, we don't need to check userId
                var order = await _context.Orders
                    .Include(o => o.ServiceType)
                    .Include(o => o.Subscription)
                    .Include(o => o.OrderServices)
                        .ThenInclude(os => os.Service)
                    .Include(o => o.OrderExtraServices)
                        .ThenInclude(oes => oes.ExtraService)
                    .Include(o => o.AssignedAdmin)
                    .FirstOrDefaultAsync(o => o.Id == orderId);

                if (order == null)
                    return NotFound();

                // Single source of truth for the order-details shape (see OrderDtoMapper).
                return OrderDtoMapper.ToOrderDto(order);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Promo/special-offer/gift-card display helpers live in OrderDtoMapper.

        [HttpPut("orders/{orderId}/status")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdateOrderStatus(int orderId, [FromBody] UpdateOrderStatusDto dto)
        {
            try
            {
                var order = await _context.Orders.FindAsync(orderId);
                if (order == null)
                    return NotFound();

                // CREATE A COPY FOR AUDITING with only relevant fields
                var originalOrder = new Order
                {
                    Id = order.Id,
                    Status = order.Status,
                    UserId = order.UserId,
                    Total = order.Total,
                    ServiceDate = order.ServiceDate,
                    ServiceTime = order.ServiceTime,
                    OrderDate = order.OrderDate,
                    ContactFirstName = order.ContactFirstName,
                    ContactLastName = order.ContactLastName,
                    ContactEmail = order.ContactEmail,
                    UpdatedAt = order.UpdatedAt
                };

                // Store the previous status for checking
                var previousStatus = order.Status;

                // Update the status
                order.Status = dto.Status;
                order.UpdatedAt = DateTime.UtcNow; // Use UTC for consistency

                // When admin reactivates a cancelled order, exempt it from auto-cancellation
                if (string.Equals(previousStatus, "Cancelled", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(dto.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    order.IsAutoCancelExempt = true;
                }

                // Payment method recording on Done transition (Phase 1). The Done modal sends
                // the selected payment method; if the admin changed it (e.g. order was created
                // expecting Stripe but customer paid Zelle), we persist the correction here.
                // ManualPaymentRecordedAt / RecordedBy are only stamped when the method is NOT
                // Normal — those fields are reserved for genuine manual-payment audit trails.
                // When dto.PaymentMethod is omitted, existing values on the order are preserved
                // (no clobber to null).
                if (string.Equals(dto.Status, "Done", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(dto.PaymentMethod))
                {
                    if (Enum.TryParse<PaymentMethod>(dto.PaymentMethod, ignoreCase: true, out var pm))
                    {
                        order.PaymentMethod = pm;
                        order.PaymentReference = pm != PaymentMethod.Normal ? dto.PaymentReference : null;
                        order.PaymentNotes = pm != PaymentMethod.Normal ? dto.PaymentNotes : null;
                        if (pm != PaymentMethod.Normal)
                        {
                            order.ManualPaymentRecordedAt = DateTime.UtcNow;
                            order.ManualPaymentRecordedByUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                        }
                    }
                }

                // Save changes FIRST
                await _context.SaveChangesAsync();

                // Bubble Rewards: process order completion when status changes to Done
                if (dto.Status == "Done" && previousStatus != "Done")
                {
                    try { await _bubblePointsService.ProcessOrderCompletion(orderId); }
                    catch (Exception rewardsEx) { _logger.LogError(rewardsEx, $"[BubbleRewards] ProcessOrderCompletion failed for order {orderId}"); }
                }

                // Bubble Rewards: reverse points when order moves away from Done
                if (previousStatus == "Done" && dto.Status != "Done")
                {
                    try { await _bubblePointsService.ReverseOrderCompletion(orderId); }
                    catch (Exception rewardsEx) { _logger.LogError(rewardsEx, $"[BubbleRewards] ReverseOrderCompletion failed for order {orderId}"); }
                }

                // Handle special offer re-marking when reactivating from cancelled status
                if (previousStatus == "Cancelled" && dto.Status == "Active")
                {
                    // Loyalty Discount: symmetric to the reverse-on-cancel hook in this same
                    // controller's CancelOrder. Re-consume the order's loyalty snapshot via
                    // ApplyToOrderAsync, which zeros the user's current % and stamps LastUsedAt.
                    //
                    // Trade-off (flagged for review): if the user's loyalty journey advanced
                    // after the cancellation (e.g. cron upgraded them to 15% during the gap),
                    // reactivation consumes that current % too. Spec section 2.7 only covers
                    // the cancellation direction explicitly; reactivation is rare and admin-
                    // driven, and the audit log makes the consumption traceable.
                    if (order.LoyaltyDiscountAmount > 0m && order.LoyaltyDiscountPercentage > 0m)
                    {
                        try
                        {
                            await _loyaltyDiscountService.ApplyToOrderAsync(orderId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Loyalty discount re-apply failed for order {orderId} on admin reactivation — order is Active but user state may be stale");
                        }
                    }

                    // Check if order had a discount amount (indicating a special offer was used)
                    if (order.DiscountAmount > 0)
                    {
                        // Find any special offer for this user that might have been the one used
                        var userSpecialOffers = await _context.UserSpecialOffers
                            .Where(uso => uso.UserId == order.UserId && !uso.IsUsed)
                            .Include(uso => uso.SpecialOffer)
                            .ToListAsync();

                        // Try to find a special offer that matches the discount amount
                        var matchingOffer = userSpecialOffers.FirstOrDefault(uso =>
                            (uso.SpecialOffer.IsPercentage &&
                             Math.Round(order.SubTotal * (uso.SpecialOffer.DiscountValue / 100), 2) == order.DiscountAmount) ||
                            (!uso.SpecialOffer.IsPercentage &&
                             uso.SpecialOffer.DiscountValue == order.DiscountAmount));

                        if (matchingOffer != null)
                        {
                            matchingOffer.IsUsed = true;
                            matchingOffer.UsedAt = DateTime.UtcNow;
                            matchingOffer.UsedOnOrderId = orderId;
                            await _context.SaveChangesAsync();

                            _logger.LogInformation($"Re-marked special offer {matchingOffer.SpecialOfferId} as used for user {matchingOffer.UserId} after reactivating order {orderId}");
                        }
                    }
                }

                // Create a copy of the updated order for auditing
                var updatedOrder = new Order
                {
                    Id = order.Id,
                    Status = order.Status,
                    UserId = order.UserId,
                    Total = order.Total,
                    ServiceDate = order.ServiceDate,
                    ServiceTime = order.ServiceTime,
                    OrderDate = order.OrderDate,
                    ContactFirstName = order.ContactFirstName,
                    ContactLastName = order.ContactLastName,
                    ContactEmail = order.ContactEmail,
                    UpdatedAt = order.UpdatedAt
                };

                // LOG THE UPDATE AFTER saving
                try
                {
                    await _auditService.LogUpdateAsync(originalOrder, updatedOrder);
                }
                catch (Exception auditEx)
                {
                    // Log the audit failure but don't fail the main operation
                    _logger.LogError(auditEx, "Audit logging failed");
                }

                return Ok(new { message = $"Order status updated to {dto.Status}" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// SuperAdmin-only: change an existing order's payment method from the admin panel —
        /// e.g. it was created expecting Stripe but the customer decided to pay cash/Zelle.
        /// Switching away from Normal moves the order onto the manual-payment flow: the manual
        /// tracking fields are stamped, IsPaid stays false, statistics stop counting Stripe fees
        /// for it, auto-cancel treats it as settled, the customer's pay-online endpoints reject
        /// it, and a Pending order (waiting for the Stripe payment) becomes Active — matching how
        /// admin-created manual orders start. Switching back to Normal reverses all of that
        /// (an unpaid Active order returns to Pending, i.e. awaiting online payment). Orders
        /// Stripe actually charged (IsPaid) can't be relabelled — correct those with a refund.
        /// </summary>
        [HttpPut("orders/{orderId}/payment-method")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> UpdateOrderPaymentMethod(int orderId, [FromBody] UpdateOrderPaymentMethodDto dto)
        {
            try
            {
                if (!Enum.TryParse<PaymentMethod>(dto.PaymentMethod, ignoreCase: true, out var pm))
                    return BadRequest(new { message = "PaymentMethod must be one of: Normal, Cash, Zelle, Check, Other." });

                var order = await _context.Orders.FindAsync(orderId);
                if (order == null)
                    return NotFound();

                if (order.IsPaid && pm != PaymentMethod.Normal)
                    return BadRequest(new { message = "This order was already paid through Stripe. Refund the charge before recording a manual payment method." });

                var originalOrder = new Order
                {
                    Id = order.Id,
                    UserId = order.UserId,
                    Status = order.Status,
                    PaymentMethod = order.PaymentMethod,
                    PaymentReference = order.PaymentReference,
                    PaymentNotes = order.PaymentNotes,
                    UpdatedAt = order.UpdatedAt
                };

                order.PaymentMethod = pm;
                order.PaymentReference = pm != PaymentMethod.Normal ? dto.PaymentReference : null;
                order.PaymentNotes = pm != PaymentMethod.Normal ? dto.PaymentNotes : null;
                if (pm != PaymentMethod.Normal)
                {
                    order.ManualPaymentRecordedAt = DateTime.UtcNow;
                    order.ManualPaymentRecordedByUserId = GetCurrentUserId();
                }
                else
                {
                    order.ManualPaymentRecordedAt = null;
                    order.ManualPaymentRecordedByUserId = null;
                }

                // Keep the status consistent with the payment flow, mirroring admin booking
                // creation (Stripe orders start Pending until paid; manual orders start Active).
                // Done/Cancelled orders keep their status.
                if (!string.Equals(order.Status, "Done", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(order.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    if (pm != PaymentMethod.Normal &&
                        string.Equals(order.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                    {
                        order.Status = "Active";
                    }
                    else if (pm == PaymentMethod.Normal && !order.IsPaid &&
                             string.Equals(order.Status, "Active", StringComparison.OrdinalIgnoreCase))
                    {
                        order.Status = "Pending";
                    }
                }

                order.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                var updatedOrder = new Order
                {
                    Id = order.Id,
                    UserId = order.UserId,
                    Status = order.Status,
                    PaymentMethod = order.PaymentMethod,
                    PaymentReference = order.PaymentReference,
                    PaymentNotes = order.PaymentNotes,
                    UpdatedAt = order.UpdatedAt
                };

                try
                {
                    await _auditService.LogUpdateAsync(originalOrder, updatedOrder);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Audit logging failed");
                }

                return Ok(new
                {
                    message = pm == PaymentMethod.Normal
                        ? $"Order #{orderId} moved back to the Stripe payment flow."
                        : $"Order #{orderId} payment method changed to {pm}.",
                    paymentMethod = pm.ToString(),
                    paymentReference = order.PaymentReference,
                    paymentNotes = order.PaymentNotes,
                    status = order.Status
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("orders/{orderId}/send-review-request")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> SendReviewRequest(int orderId)
        {
            try
            {
                var order = await _context.Orders
                    .Include(o => o.User)
                    .FirstOrDefaultAsync(o => o.Id == orderId);
                if (order == null)
                    return NotFound();

                var customerName = $"{order.ContactFirstName} {order.ContactLastName}".Trim();
                var email = order.ContactEmail;
                var phone = order.User?.Phone ?? order.ContactPhone;

                // Send email if available
                if (!string.IsNullOrWhiteSpace(email))
                {
                    try
                    {
                        await _emailService.SendReviewRequestEmailAsync(email, customerName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to send review email for order #{orderId}");
                    }
                }

                // Send SMS if phone available and SMS enabled
                if (!string.IsNullOrWhiteSpace(phone) && _smsService.IsSmsEnabled())
                {
                    try
                    {
                        await _smsService.SendReviewRequestSmsAsync(phone, customerName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to send review SMS for order #{orderId}");
                    }
                }

                return Ok(new { message = "Review request sent" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("orders/{orderId}/cancel")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> CancelOrder(int orderId, [FromBody] CancelOrderDto dto)
        {
            try
            {
                var order = await _context.Orders.FindAsync(orderId);
                if (order == null)
                    return NotFound();

                if (order.Status == "Cancelled" || order.Status == "Done")
                    return BadRequest(new { message = "Cannot cancel an order that is already cancelled or done." });

                // CREATE A COPY FOR AUDITING including cancellation fields
                var originalOrder = new Order
                {
                    Id = order.Id,
                    Status = order.Status,
                    CancellationReason = order.CancellationReason,
                    UserId = order.UserId,
                    Total = order.Total,
                    ServiceDate = order.ServiceDate,
                    ServiceTime = order.ServiceTime,
                    OrderDate = order.OrderDate,
                    ContactFirstName = order.ContactFirstName,
                    ContactLastName = order.ContactLastName,
                    ContactEmail = order.ContactEmail,
                    UpdatedAt = order.UpdatedAt
                };

                // Determine if late cancellation fee applies.
                // ServiceDate/ServiceTime are NY wall-clock; convert to UTC before comparing.
                var serviceUtc = NyTimeHelper.ToUtc(order.ServiceDate.Date.Add(order.ServiceTime));
                bool isLateCancellation = order.IsPaid && serviceUtc <= DateTime.UtcNow.AddHours(48);

                // Update the order with cancellation info
                order.Status = "Cancelled";
                order.CancellationReason = dto.Reason;
                order.IsLateCancellation = isLateCancellation;
                order.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                // Restore special offer if one was used
                var userSpecialOffer = await _context.UserSpecialOffers
                    .FirstOrDefaultAsync(uso => uso.UsedOnOrderId == orderId);

                if (userSpecialOffer != null)
                {
                    userSpecialOffer.IsUsed = false;
                    userSpecialOffer.UsedAt = null;
                    userSpecialOffer.UsedOnOrderId = null;
                    await _context.SaveChangesAsync();

                    _logger.LogInformation($"Restored special offer {userSpecialOffer.SpecialOfferId} for user {userSpecialOffer.UserId} after admin cancelled order {orderId}");
                }

                // Restore loyalty discount snapshot to the user's account if this order had one.
                // Same pattern as the special-offer reset above. Failures don't unwind the cancel.
                if (order.LoyaltyDiscountAmount > 0m && order.LoyaltyDiscountPercentage > 0m)
                {
                    try
                    {
                        await _loyaltyDiscountService.ReverseFromOrderAsync(orderId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Loyalty discount reverse failed for order {orderId} on admin cancel — order is cancelled but user state may be stale");
                    }
                }

                // Create updated copy for auditing
                var updatedOrder = new Order
                {
                    Id = order.Id,
                    Status = order.Status,
                    CancellationReason = order.CancellationReason,
                    UserId = order.UserId,
                    Total = order.Total,
                    ServiceDate = order.ServiceDate,
                    ServiceTime = order.ServiceTime,
                    OrderDate = order.OrderDate,
                    ContactFirstName = order.ContactFirstName,
                    ContactLastName = order.ContactLastName,
                    ContactEmail = order.ContactEmail,
                    UpdatedAt = order.UpdatedAt
                };

                // LOG THE UPDATE
                try
                {
                    await _auditService.LogUpdateAsync(originalOrder, updatedOrder);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Audit logging failed");
                }

                // Send cancellation emails
                try
                {
                    var user = await _context.Users.FindAsync(order.UserId);
                    var fullAddress = $"{order.ServiceAddress}{(!string.IsNullOrEmpty(order.AptSuite) ? $", {order.AptSuite}" : "")}, {order.City}, {order.State} {order.ZipCode}";

                    await _emailService.SendCancellationNotificationToCompanyAsync(
                        orderId,
                        user?.Email ?? order.ContactEmail,
                        order.UserId,
                        dto.Reason,
                        isLateCancellation,
                        order.ServiceDate,
                        order.ServiceTime.ToString(@"hh\:mm")
                    );

                    var assignedCleaners = await _context.OrderCleaners
                        .Where(oc => oc.OrderId == orderId)
                        .Include(oc => oc.Cleaner)
                        .ToListAsync();

                    foreach (var oc in assignedCleaners)
                    {
                        if (!string.IsNullOrEmpty(oc.Cleaner?.Email))
                        {
                            await _emailService.SendCancellationNotificationToCleanerAsync(
                                oc.Cleaner.Email,
                                orderId,
                                order.ServiceDate,
                                order.ServiceTime.ToString(@"hh\:mm"),
                                fullAddress
                            );
                        }
                    }
                }
                catch (Exception emailEx)
                {
                    _logger.LogError(emailEx, $"Cancellation email failed for order {orderId}");
                }

                return Ok(new { message = "Order cancelled successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>SuperAdmin-only: edit any order field. All changes are audit-logged.</summary>
        [HttpPut("orders/{orderId}/superadmin-full-update")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> SuperAdminFullUpdateOrder(int orderId, [FromBody] SuperAdminUpdateOrderDto dto)
        {
            if (GetCurrentUserRole() != UserRole.SuperAdmin)
                return Forbid();

            var orderBefore = await _context.Orders
                .AsNoTracking()
                .Include(o => o.OrderServices)
                    .ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices)
                    .ThenInclude(oes => oes.ExtraService)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (orderBefore == null)
                return NotFound();

            try
            {
                var currentUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                await _orderService.SuperAdminFullUpdateOrder(orderId, currentUserId, dto);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            var orderAfter = await _context.Orders
                .Include(o => o.OrderServices)
                    .ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices)
                    .ThenInclude(oes => oes.ExtraService)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (orderAfter != null)
            {
                try
                {
                    await _auditService.LogUpdateAsync(orderBefore, orderAfter);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Audit logging failed");
                }
            }

            return Ok(new { message = "Order updated successfully" });
        }

        /// <summary>SuperAdmin-only: permanently delete an order with its dependent rows and photo files.
        /// No Stripe refund is issued — run the cancel flow first if the customer needs a refund.</summary>
        [HttpDelete("orders/{orderId}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> DeleteOrder(int orderId)
        {
            if (GetCurrentUserRole() != UserRole.SuperAdmin)
                return Forbid();

            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null)
                return NotFound(new { message = "Order not found" });

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Cleaning photos: the FK would only null OrderId, leaving them as
                // unassigned photos (no longer allowed) — delete rows and files instead.
                var photos = await _context.UserCleaningPhotos
                    .Where(p => p.OrderId == orderId)
                    .ToListAsync();
                foreach (var photo in photos)
                    DeleteUploadedFileIfExists(photo.PhotoUrl);
                _context.UserCleaningPhotos.RemoveRange(photos);

                // Gift-card usages reference the order with Restrict — remove them explicitly.
                // The card balance stays as already deducted; only the usage record goes.
                var giftCardUsages = await _context.GiftCardUsages
                    .Where(g => g.OrderId == orderId)
                    .ToListAsync();
                _context.GiftCardUsages.RemoveRange(giftCardUsages);

                // Notification logs have no cascade on OrderId — drop the ones tied to this order.
                var notificationLogs = await _context.NotificationLogs
                    .Where(n => n.OrderId == orderId)
                    .ToListAsync();
                _context.NotificationLogs.RemoveRange(notificationLogs);

                // Services, extras, cleaner assignments, update history, admin assignment
                // history, pending edits and reminder acks all cascade from the order.
                // BubblePointsHistory / CleanerNote / UserSpecialOffer order refs are SET NULL.
                _context.Orders.Remove(order);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to delete order {OrderId}", orderId);
                return BadRequest(new { message = "Failed to delete order: " + ex.Message });
            }

            try
            {
                await _auditService.LogDeleteAsync(order);
            }
            catch (Exception auditEx)
            {
                _logger.LogError(auditEx, "Audit logging failed for order delete {OrderId}", orderId);
            }

            return Ok(new { message = "Order deleted permanently." });
        }

        private void DeleteUploadedFileIfExists(string? publicUrl)
        {
            if (string.IsNullOrWhiteSpace(publicUrl)) return;

            var basePath = _configuration["FileUpload:Path"];
            if (string.IsNullOrWhiteSpace(basePath)) return;

            var fullPath = Path.Combine(basePath, publicUrl.TrimStart('/'));
            try
            {
                if (System.IO.File.Exists(fullPath))
                    System.IO.File.Delete(fullPath);
            }
            catch
            {
                // ignore — the DB row is being removed regardless
            }
        }

        /// <summary>Admin-only: submit proposed order changes for SuperAdmin approval. SuperAdmins should use direct save.</summary>
        [HttpPost("orders/{orderId}/pending-edit")]
        [Authorize(Roles = "Admin")]
        public async Task<ActionResult<PendingOrderEditListDto>> SubmitPendingOrderEdit(int orderId, [FromBody] SuperAdminUpdateOrderDto dto)
        {
            var order = await _context.Orders.FindAsync(orderId);
            if (order == null)
                return NotFound(new { message = "Order not found" });

            var currentUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            var proposedJson = JsonConvert.SerializeObject(dto);

            var pending = new PendingOrderEdit
            {
                OrderId = orderId,
                RequestedByUserId = currentUserId,
                RequestedAt = DateTime.UtcNow,
                ProposedChangesJson = proposedJson,
                Status = "Pending"
            };
            _context.PendingOrderEdits.Add(pending);
            await _context.SaveChangesAsync();

            var requestedBy = await _context.Users.FindAsync(currentUserId);
            var summary = $"Order #{orderId} - {order.ContactFirstName} {order.ContactLastName} - {order.ServiceDate:yyyy-MM-dd}";
            return CreatedAtAction(nameof(GetPendingOrderEditDetail), new { id = pending.Id }, new PendingOrderEditListDto
            {
                Id = pending.Id,
                OrderId = pending.OrderId,
                OrderSummary = summary,
                RequestedByUserId = pending.RequestedByUserId,
                RequestedByName = requestedBy != null ? $"{requestedBy.FirstName} {requestedBy.LastName}" : "",
                RequestedAt = pending.RequestedAt,
                Status = pending.Status
            });
        }

        /// <summary>SuperAdmin-only: list all pending order edits.</summary>
        [HttpGet("orders/pending-edits")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<List<PendingOrderEditListDto>>> GetPendingOrderEdits()
        {
            var list = await _context.PendingOrderEdits
                .Where(poe => poe.Status == "Pending")
                .Include(poe => poe.Order)
                .Include(poe => poe.RequestedByUser)
                .OrderByDescending(poe => poe.RequestedAt)
                .Select(poe => new PendingOrderEditListDto
                {
                    Id = poe.Id,
                    OrderId = poe.OrderId,
                    OrderSummary = "Order #" + poe.Order.Id + " - " + poe.Order.ContactFirstName + " " + poe.Order.ContactLastName + " - " + poe.Order.ServiceDate.ToString("yyyy-MM-dd"),
                    RequestedByUserId = poe.RequestedByUserId,
                    RequestedByName = poe.RequestedByUser.FirstName + " " + poe.RequestedByUser.LastName,
                    RequestedAt = poe.RequestedAt,
                    Status = poe.Status
                })
                .ToListAsync();
            return Ok(list);
        }

        /// <summary>SuperAdmin-only: get one pending edit with current order state and proposed changes (for diff and approve/reject).</summary>
        [HttpGet("orders/pending-edits/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<PendingOrderEditDetailDto>> GetPendingOrderEditDetail(int id)
        {
            var pending = await _context.PendingOrderEdits
                .Include(poe => poe.Order)
                .Include(poe => poe.RequestedByUser)
                .FirstOrDefaultAsync(poe => poe.Id == id);
            if (pending == null)
                return NotFound(new { message = "Pending edit not found" });
            if (pending.Status != "Pending")
                return BadRequest(new { message = "This edit was already approved or rejected" });

            var order = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.Subscription)
                .Include(o => o.OrderServices).ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices).ThenInclude(oes => oes.ExtraService)
                .FirstOrDefaultAsync(o => o.Id == pending.OrderId);
            if (order == null)
                return NotFound(new { message = "Order not found" });

            // Single source of truth for the order-details shape (see OrderDtoMapper).
            var currentOrder = OrderDtoMapper.ToOrderDto(order);

            SuperAdminUpdateOrderDto? proposed = null;
            try
            {
                proposed = JsonConvert.DeserializeObject<SuperAdminUpdateOrderDto>(pending.ProposedChangesJson);
            }
            catch { /* ignore */ }

            return Ok(new PendingOrderEditDetailDto
            {
                Id = pending.Id,
                OrderId = pending.OrderId,
                RequestedByUserId = pending.RequestedByUserId,
                RequestedByName = pending.RequestedByUser != null ? $"{pending.RequestedByUser.FirstName} {pending.RequestedByUser.LastName}" : "",
                RequestedAt = pending.RequestedAt,
                Status = pending.Status,
                CurrentOrder = currentOrder,
                ProposedChanges = proposed
            });
        }

        /// <summary>SuperAdmin-only: approve and apply the pending order edit.</summary>
        [HttpPost("orders/pending-edits/{id}/approve")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> ApprovePendingOrderEdit(int id)
        {
            if (GetCurrentUserRole() != UserRole.SuperAdmin)
                return Forbid();

            var pending = await _context.PendingOrderEdits
                .Include(poe => poe.Order)
                .FirstOrDefaultAsync(poe => poe.Id == id);
            if (pending == null)
                return NotFound(new { message = "Pending edit not found" });
            if (pending.Status != "Pending")
                return BadRequest(new { message = "This edit was already approved or rejected" });

            SuperAdminUpdateOrderDto? dto;
            try
            {
                dto = JsonConvert.DeserializeObject<SuperAdminUpdateOrderDto>(pending.ProposedChangesJson);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Invalid proposed changes: " + ex.Message });
            }
            if (dto == null)
                return BadRequest(new { message = "Invalid proposed changes" });

            var currentUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

            var orderBefore = await _context.Orders
                .AsNoTracking()
                .Include(o => o.OrderServices).ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices).ThenInclude(oes => oes.ExtraService)
                .FirstOrDefaultAsync(o => o.Id == pending.OrderId);
            if (orderBefore == null)
                return NotFound(new { message = "Order not found" });

            await _orderService.SuperAdminFullUpdateOrder(pending.OrderId, currentUserId, dto);

            pending.Status = "Approved";
            pending.ReviewedByUserId = currentUserId;
            pending.ReviewedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var orderAfter = await _context.Orders
                .Include(o => o.OrderServices).ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices).ThenInclude(oes => oes.ExtraService)
                .FirstOrDefaultAsync(o => o.Id == pending.OrderId);
            if (orderAfter != null)
            {
                try
                {
                    await _auditService.LogUpdateAsync(orderBefore, orderAfter);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Audit logging failed");
                }
            }

            return Ok(new { message = "Order edit approved and applied successfully" });
        }

        /// <summary>SuperAdmin-only: reject the pending order edit.</summary>
        [HttpPost("orders/pending-edits/{id}/reject")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> RejectPendingOrderEdit(int id, [FromBody] RejectPendingOrderEditDto? dto = null)
        {
            if (GetCurrentUserRole() != UserRole.SuperAdmin)
                return Forbid();

            var pending = await _context.PendingOrderEdits.FindAsync(id);
            if (pending == null)
                return NotFound(new { message = "Pending edit not found" });
            if (pending.Status != "Pending")
                return BadRequest(new { message = "This edit was already approved or rejected" });

            var currentUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            pending.Status = "Rejected";
            pending.ReviewedByUserId = currentUserId;
            pending.ReviewedAt = DateTime.UtcNow;
            pending.RejectReason = dto?.RejectReason;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Order edit rejected" });
        }

        [HttpGet("orders/{orderId}/available-cleaners")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<AvailableCleanerDto>>> GetAvailableCleaners(int orderId)
        {
            var order = await _context.Orders
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return NotFound();

            var availableCleaners = await _cleanerService.GetAvailableCleanersAsync(order);

            return Ok(availableCleaners);
        }

        [HttpPost("orders/{orderId}/assign-cleaners")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> AssignCleaners(int orderId, AssignCleanersDto dto)
        {
            dto.OrderId = orderId;
            var assignedBy = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

            bool success;
            try
            {
                success = await _cleanerService.AssignCleanersToOrderAsync(dto, assignedBy);
            }
            catch (CleanerAssignmentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            if (success)
                return Ok(new { message = "Cleaners assigned successfully. Use “Send assignment email” when you are ready to notify cleaners." });

            return BadRequest(new { message = "Failed to assign cleaners" });
        }

        [HttpPost("orders/{orderId}/send-cleaner-assignment-mails")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<SendCleanerAssignmentMailsResultDto>> SendCleanerAssignmentMails(int orderId)
        {
            var result = await _cleanerService.SendPendingCleanerAssignmentMailsAsync(orderId);
            if (result == null)
                return NotFound(new { message = "Order not found" });

            return Ok(result);
        }

        [HttpPost("orders/{orderId}/cleaners/{cleanerId}/resend-assignment-mail")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<SendCleanerAssignmentMailsResultDto>> ResendCleanerAssignmentMail(int orderId, int cleanerId)
        {
            var result = await _cleanerService.ResendCleanerAssignmentMailAsync(orderId, cleanerId);
            if (result == null)
                return NotFound(new { message = "Order not found" });

            if (result.EmailsSent <= 0)
                return BadRequest(new { message = result.Message });

            return Ok(result);
        }

        [HttpGet("orders/{orderId}/assigned-cleaners")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<string>>> GetAssignedCleaners(int orderId)
        {
            var assignedCleaners = await _context.OrderCleaners
                .Where(oc => oc.OrderId == orderId)
                .Include(oc => oc.Cleaner)
                .Select(oc => $"{oc.Cleaner.FirstName} {oc.Cleaner.LastName}")
                .ToListAsync();

            return Ok(assignedCleaners);
        }

        [HttpGet("orders/{orderId}/assigned-cleaners-with-ids")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<object>>> GetAssignedCleanersWithIds(int orderId)
        {
            var assignedCleaners = await _context.OrderCleaners
                .Where(oc => oc.OrderId == orderId)
                .Include(oc => oc.Cleaner)
                .Select(oc => new
                {
                    id = oc.CleanerId,
                    name = $"{oc.Cleaner.FirstName} {oc.Cleaner.LastName}",
                    assignmentNotificationSentAt = oc.AssignmentNotificationSentAt
                })
                .ToListAsync();

            return Ok(assignedCleaners);
        }

        /// <summary>
        /// Bulk variant of GetAssignedCleanersWithIds: returns assigned cleaners for ALL orders in a
        /// single query, keyed by orderId. The admin orders page used to call the per-order endpoint
        /// once per row (N+1), which took several seconds with many orders; this collapses it to one round-trip.
        /// </summary>
        [HttpGet("orders/assigned-cleaners-with-ids/bulk")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> GetAssignedCleanersWithIdsBulk()
        {
            var rows = await _context.OrderCleaners
                .Include(oc => oc.Cleaner)
                .Select(oc => new
                {
                    orderId = oc.OrderId,
                    id = oc.CleanerId,
                    name = $"{oc.Cleaner.FirstName} {oc.Cleaner.LastName}",
                    assignmentNotificationSentAt = oc.AssignmentNotificationSentAt
                })
                .ToListAsync();

            var grouped = rows
                .GroupBy(r => r.orderId)
                .ToDictionary(
                    g => g.Key.ToString(),
                    g => g.Select(r => new { r.id, r.name, r.assignmentNotificationSentAt }).ToList());

            return Ok(grouped);
        }

        [HttpDelete("orders/{orderId}/cleaners/{cleanerId}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> RemoveCleanerFromOrder(int orderId, int cleanerId)
        {
            // Pass current user ID to the service for audit logging
            var currentUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

            var success = await _cleanerService.UnassignCleanerFromOrderAsync(orderId, cleanerId, currentUserId);

            if (success)
                return Ok(new { message = "Cleaner removed successfully and notified via email" });

            return NotFound(new { message = "Cleaner assignment not found" });
        }

        [HttpGet("orders/{orderId}/update-history")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> GetOrderUpdateHistory(int orderId)
        {
            var history = await _context.OrderUpdateHistories
                .Where(h => h.OrderId == orderId)
                .Include(h => h.UpdatedByUser)
                .OrderBy(h => h.UpdatedAt)
                .Select(h => new
                {
                    h.Id,
                    h.UpdatedAt,
                    UpdatedBy = h.UpdatedByUser.FirstName + " " + h.UpdatedByUser.LastName,
                    UpdatedByEmail = h.UpdatedByUser.Email,
                    h.OriginalSubTotal,
                    h.OriginalTax,
                    h.OriginalTips,
                    h.OriginalCompanyDevelopmentTips,
                    h.OriginalTotal,
                    h.NewSubTotal,
                    h.NewTax,
                    h.NewTips,
                    h.NewCompanyDevelopmentTips,
                    h.NewTotal,
                    h.AdditionalAmount,
                    h.PaymentIntentId,
                    h.IsPaid,
                    h.PaidAt,
                    h.UpdateNotes,
                    h.UpdatedPaymentNotificationSentAt,
                    PaymentMethod = h.PaymentMethod.ToString(),
                    h.PaymentReference,
                    h.PaymentNotes,
                    h.ManualPaymentRecordedAt
                })
                .ToListAsync();

            return Ok(history);
        }

        /// <summary>
        /// Record a manual (non-Stripe) payment for a single additional-amount row. Used when the
        /// customer paid the order top-up outside Stripe (e.g. Zelle/Cash/Check) — typically after
        /// the cleaning ran longer and the order was edited up. Marks just this history row paid via
        /// the chosen method; the base order's PaymentMethod stays Normal so it still counts as a
        /// Stripe order. Statistics exclude this manually-paid additional from the Stripe-fee base.
        /// Gated on the Update permission (Admins with order-update rights + SuperAdmin), matching
        /// the order status-update and cancel endpoints.
        /// </summary>
        [HttpPost("orders/{orderId}/update-history/{historyId}/record-manual-payment")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> RecordManualAdditionalPayment(
            int orderId, int historyId, [FromBody] RecordManualAdditionalPaymentDto dto)
        {
            if (!Enum.TryParse<PaymentMethod>(dto.PaymentMethod, ignoreCase: true, out var pm) ||
                pm == PaymentMethod.Normal)
            {
                return BadRequest(new { message = "PaymentMethod must be one of: Cash, Zelle, Check, Other." });
            }

            var history = await _context.OrderUpdateHistories
                .FirstOrDefaultAsync(h => h.Id == historyId && h.OrderId == orderId);
            if (history == null)
                return NotFound(new { message = "Update-history record not found for this order." });

            if (history.AdditionalAmount <= 0.01m)
                return BadRequest(new { message = "This update has no additional amount to collect." });

            if (history.IsPaid)
                return BadRequest(new { message = "This additional amount is already marked as paid." });

            history.IsPaid = true;
            history.PaidAt = DateTime.UtcNow;
            history.PaymentMethod = pm;
            history.PaymentReference = dto.PaymentReference;
            history.PaymentNotes = dto.PaymentNotes;
            history.ManualPaymentRecordedAt = DateTime.UtcNow;
            history.ManualPaymentRecordedByUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

            await _context.SaveChangesAsync();

            // Mirror the Stripe additional-payment confirmation (OrderController): once there are
            // no more unpaid additional amounts, flip the order Pending -> Active. The edit that
            // created the additional amount moved it to Pending ("awaiting payment"); collecting
            // the top-up (here, manually) completes it. Don't touch Done/Cancelled.
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            bool statusReactivated = false;
            if (order != null)
            {
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
                    statusReactivated = true;
                }
            }

            return Ok(new
            {
                message = $"Recorded {pm} payment of ${history.AdditionalAmount:F2} for the additional charge.",
                historyId = history.Id,
                paymentMethod = pm.ToString(),
                paidAt = history.PaidAt,
                statusReactivated,
                status = order?.Status
            });
        }

        /// <summary>Send a gentle reminder (email + SMS) to the customer about their unpaid additional payment. Requires View or Update.</summary>
        [HttpPost("orders/{orderId}/send-payment-reminder")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> SendPaymentReminder(int orderId)
        {
            var order = await _context.Orders
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null)
                return NotFound(new { message = "Order not found" });

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
            var amountToSend = Math.Round(Math.Max(0m, totalDelta - alreadyPaid), 2);
            if (amountToSend < 0.01m)
                return BadRequest(new { message = "No unpaid additional payment for this order." });

            var customerName = !string.IsNullOrWhiteSpace(order.ContactFirstName) || !string.IsNullOrWhiteSpace(order.ContactLastName)
                ? $"{order.ContactFirstName?.Trim()} {order.ContactLastName?.Trim()}".Trim()
                : (order.User != null ? $"{order.User.FirstName?.Trim()} {order.User.LastName?.Trim()}".Trim() : "Valued Customer");
            if (string.IsNullOrWhiteSpace(customerName))
                customerName = order.User?.FirstName ?? order.ContactFirstName ?? "Valued Customer";
            var customerEmail = !string.IsNullOrWhiteSpace(order.ContactEmail) ? order.ContactEmail : order.User?.Email;
            // No-email (cash) accounts carry a non-routable placeholder — treat as no email.
            if (NoEmailHelper.IsPlaceholder(customerEmail)) customerEmail = null;
            var customerPhone = !string.IsNullOrWhiteSpace(order.ContactPhone) ? order.ContactPhone : order.User?.Phone;

            var frontendUrl = _configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com";
            // Tokenized link: opens the payment page without login while something is unpaid.
            var paymentLink = await PaymentLinkHelper.BuildPaymentLinkAsync(_context, order, frontendUrl);

            // Track per-channel outcome so the admin sees a clear "email sent, SMS skipped" hint
            // when RingCentral rejects the phone number, rather than a hard 400.
            bool emailSent = false, smsSent = false, smsInvalid = false;

            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                try
                {
                    await _emailService.SendAdditionalPaymentReminderEmailAsync(customerEmail, customerName, amountToSend, orderId, paymentLink);
                    emailSent = true;
                }
                catch (Exception ex)
                {
                    return BadRequest(new { message = "Reminder email could not be sent: " + ex.Message });
                }
            }
            if (!string.IsNullOrWhiteSpace(customerPhone) && _smsService.IsSmsEnabled())
            {
                try
                {
                    var e164 = SmsService.NormalizePhoneToE164(customerPhone);
                    if (!string.IsNullOrEmpty(e164))
                    {
                        await _smsService.SendAdditionalPaymentReminderSmsAsync(e164, customerName, amountToSend, orderId, paymentLink);
                        smsSent = true;
                    }
                }
                catch (InvalidPhoneNumberException)
                {
                    smsInvalid = true;
                }
                catch (Exception ex)
                {
                    return BadRequest(new { message = "Reminder SMS could not be sent: " + ex.Message });
                }
            }
            if (string.IsNullOrWhiteSpace(customerEmail) && (string.IsNullOrWhiteSpace(customerPhone) || !_smsService.IsSmsEnabled()))
                return BadRequest(new { message = "No email or phone available to send the reminder." });

            return Ok(new { message = BuildSendResultMessage("Payment reminder", emailSent, smsSent, smsInvalid) });
        }

        /// <summary>
        /// One-shot re-send of the SAME payment-link email + SMS that the create-for-user flow
        /// sends at order creation (`SendPaymentReminderEmailAsync` / `SendPaymentReminderSmsAsync`,
        /// link /order/{id}/pay, amount = order.Total). Built for the case where an admin typed a
        /// wrong email/phone when registering the user and has since corrected it on the user
        /// account — so contact is resolved from the USER ACCOUNT, never the frozen order contact.
        /// Fires only on click; touches no other flow, schedules nothing, writes no logs.
        /// </summary>
        [HttpPost("orders/{orderId}/send-payment-link")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> SendPaymentLink(int orderId, [FromBody] SendPaymentLinkDto dto)
        {
            if (dto == null || (!dto.SendEmail && !dto.SendSms))
                return BadRequest(new { message = "Select at least one channel (email or phone)." });

            var order = await _context.Orders
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null)
                return NotFound(new { message = "Order not found" });

            // Contact comes from the live user account (the corrected value), not order.ContactEmail.
            var customerEmail = order.User?.Email;
            var customerPhone = order.User?.Phone;

            var customerName = order.User != null
                ? $"{order.User.FirstName?.Trim()} {order.User.LastName?.Trim()}".Trim()
                : $"{order.ContactFirstName?.Trim()} {order.ContactLastName?.Trim()}".Trim();
            if (string.IsNullOrWhiteSpace(customerName))
                customerName = order.User?.FirstName ?? order.ContactFirstName ?? "Valued Customer";

            var frontendUrl = _configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com";
            // Tokenized link: opens the payment page without login while something is unpaid.
            var paymentLink = await PaymentLinkHelper.BuildPaymentLinkAsync(_context, order, frontendUrl);

            bool emailSent = false, smsSent = false, smsInvalid = false;
            string? sentToEmail = null, sentToPhone = null;

            if (dto.SendEmail)
            {
                // Apple "Hide My Email" relay addresses can't actually receive — same skip the
                // creation path uses. Surface it so the admin knows why nothing went out.
                var isAppleHiddenMail = !string.IsNullOrEmpty(customerEmail) &&
                    customerEmail.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(customerEmail) || NoEmailHelper.IsPlaceholder(customerEmail))
                    return BadRequest(new { message = "No email on the customer's account to send to." });
                if (isAppleHiddenMail)
                    return BadRequest(new { message = "The customer's account email is an Apple private-relay address and can't receive mail." });

                try
                {
                    await _emailService.SendPaymentReminderEmailAsync(customerEmail, customerName, order.Total, orderId, paymentLink);
                    emailSent = true;
                    sentToEmail = customerEmail;
                }
                catch (Exception ex)
                {
                    return BadRequest(new { message = "Payment-link email could not be sent: " + ex.Message });
                }
            }

            if (dto.SendSms)
            {
                if (string.IsNullOrWhiteSpace(customerPhone))
                    return BadRequest(new { message = "No phone number on the customer's account to send to." });
                if (!_smsService.IsSmsEnabled())
                    return BadRequest(new { message = "SMS sending is currently disabled." });

                try
                {
                    var e164 = SmsService.NormalizePhoneToE164(customerPhone);
                    if (string.IsNullOrEmpty(e164))
                        smsInvalid = true;
                    else
                    {
                        await _smsService.SendPaymentReminderSmsAsync(e164, customerName, order.Total, orderId, paymentLink);
                        smsSent = true;
                        sentToPhone = customerPhone;
                    }
                }
                catch (InvalidPhoneNumberException)
                {
                    smsInvalid = true;
                }
                catch (Exception ex)
                {
                    return BadRequest(new { message = "Payment-link SMS could not be sent: " + ex.Message });
                }
            }

            return Ok(new
            {
                message = BuildSendResultMessage("Payment link", emailSent, smsSent, smsInvalid),
                sentToEmail,
                sentToPhone
            });
        }

        /// <summary>Send the first "your order was updated, please pay the additional amount" email + SMS
        /// to the customer for a manual back-office order edit. Replaces the auto-send that used to fire
        /// inside SuperAdminFullUpdateOrder. After this fires, the admin UI flips to the regular
        /// "Send Payment Reminder" button for follow-ups.</summary>
        [HttpPost("orders/{orderId}/send-updated-payment")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> SendUpdatedPaymentNotification(int orderId)
        {
            var order = await _context.Orders
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null)
                return NotFound(new { message = "Order not found" });

            // Compute outstanding amount the same way SendPaymentReminder does — unpaid delta
            // since the original booking, less anything already paid via prior update rows.
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
            var amountToSend = Math.Round(Math.Max(0m, totalDelta - alreadyPaid), 2);
            if (amountToSend < 0.01m)
                return BadRequest(new { message = "No unpaid additional payment for this order." });

            var customerName = !string.IsNullOrWhiteSpace(order.ContactFirstName) || !string.IsNullOrWhiteSpace(order.ContactLastName)
                ? $"{order.ContactFirstName?.Trim()} {order.ContactLastName?.Trim()}".Trim()
                : (order.User != null ? $"{order.User.FirstName?.Trim()} {order.User.LastName?.Trim()}".Trim() : "Valued Customer");
            if (string.IsNullOrWhiteSpace(customerName))
                customerName = order.User?.FirstName ?? order.ContactFirstName ?? "Valued Customer";
            var customerEmail = !string.IsNullOrWhiteSpace(order.ContactEmail) ? order.ContactEmail : order.User?.Email;
            // No-email (cash) accounts carry a non-routable placeholder — treat as no email.
            if (NoEmailHelper.IsPlaceholder(customerEmail)) customerEmail = null;
            var customerPhone = !string.IsNullOrWhiteSpace(order.ContactPhone) ? order.ContactPhone : order.User?.Phone;

            var frontendUrl = _configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com";
            // Tokenized link: opens the payment page without login while something is unpaid.
            var paymentLink = await PaymentLinkHelper.BuildPaymentLinkAsync(_context, order, frontendUrl);

            bool emailSent = false, smsSent = false, smsInvalid = false;

            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                try
                {
                    await _emailService.SendAdditionalPaymentRequiredEmailAsync(customerEmail, customerName, amountToSend, orderId, paymentLink);
                    emailSent = true;
                }
                catch (Exception ex)
                {
                    return BadRequest(new { message = "Updated-payment email could not be sent: " + ex.Message });
                }
            }
            if (!string.IsNullOrWhiteSpace(customerPhone) && _smsService.IsSmsEnabled())
            {
                try
                {
                    var e164 = SmsService.NormalizePhoneToE164(customerPhone);
                    if (!string.IsNullOrEmpty(e164))
                    {
                        await _smsService.SendAdditionalPaymentRequiredSmsAsync(e164, customerName, amountToSend, orderId, paymentLink);
                        smsSent = true;
                    }
                }
                catch (InvalidPhoneNumberException)
                {
                    smsInvalid = true;
                }
                catch (Exception ex)
                {
                    return BadRequest(new { message = "Updated-payment SMS could not be sent: " + ex.Message });
                }
            }
            if (string.IsNullOrWhiteSpace(customerEmail) && (string.IsNullOrWhiteSpace(customerPhone) || !_smsService.IsSmsEnabled()))
                return BadRequest(new { message = "No email or phone available to send the updated-payment notification." });

            // Stamp every unpaid, not-yet-notified history row for this order. We mark them all
            // (rather than only the latest) because the notification covers the full outstanding
            // balance — once it's sent, none of those rows should show "first send" any longer.
            // Stamp even when only the email went through — the customer has been informed; the
            // bad-phone-number issue is on the admin to fix, not a reason to re-show "Send updated
            // payment" indefinitely.
            var rowsToStamp = await _context.OrderUpdateHistories
                .Where(h => h.OrderId == orderId && !h.IsPaid && h.UpdatedPaymentNotificationSentAt == null)
                .ToListAsync();
            var stampedAt = DateTime.UtcNow;
            foreach (var row in rowsToStamp)
                row.UpdatedPaymentNotificationSentAt = stampedAt;
            if (rowsToStamp.Count > 0)
                await _context.SaveChangesAsync();

            return Ok(new { message = BuildSendResultMessage("Updated-payment notification", emailSent, smsSent, smsInvalid) });
        }

        // Compose an admin-friendly summary across the two channels so callers can show one
        // toast instead of guessing at partial-success state. Used by both reminder flavors.
        private static string BuildSendResultMessage(string label, bool emailSent, bool smsSent, bool smsInvalid)
        {
            if (emailSent && smsSent) return $"{label} sent (email + SMS).";
            if (emailSent && smsInvalid) return $"{label}: email sent. SMS was not sent because the phone number on file is invalid.";
            if (emailSent) return $"{label}: email sent.";
            if (smsSent) return $"{label}: SMS sent.";
            if (smsInvalid) return $"{label}: not sent — the phone number on file is invalid and there is no email.";
            return $"{label} sent.";
        }
        // ── Order Reminder Acknowledgments ────────────────────

        /// <summary>
        /// Get all currently active (unacknowledged) order reminders.
        /// Returns reminders for orders whose service window hasn't passed yet.
        /// </summary>
        [HttpGet("orders/active-reminders")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> GetActiveOrderReminders()
        {
            try
            {
                // Get NY time zone
                var nyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                var nowNy = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nyTz);

                // Get all orders that are Pending or Active and have a service date today or in the future
                var todayNy = nowNy.Date;

                // Get acknowledged reminder keys (orderId + type) so we exclude them
                // AcknowledgedAt is stored as UTC, so compare against UTC
                var cutoffUtc = DateTime.UtcNow.AddDays(-1);
                var acknowledged = await _context.OrderReminderAcknowledgments
                    .Where(a => a.AcknowledgedAt >= cutoffUtc)
                    .Select(a => new { a.OrderId, a.Type })
                    .ToListAsync();

                var acknowledgedKeys = acknowledged
                    .Select(a => $"{a.OrderId}_{a.Type}")
                    .ToHashSet();

                // Get relevant orders
                var orders = await _context.Orders
                    .Where(o => o.Status != "Cancelled" && o.Status != "Done")
                    .Where(o => o.ServiceDate >= todayNy.AddDays(-1))
                    .Select(o => new { o.Id, o.ServiceDate, o.ServiceTime, o.TotalDuration })
                    .ToListAsync();

                var activeReminders = new List<object>();

                foreach (var order in orders)
                {
                    var startNy = order.ServiceDate.Date.Add(order.ServiceTime);
                    var endNy = startNy.AddMinutes((double)order.TotalDuration);

                    // Check 30 min before start
                    var alertStart = startNy.AddMinutes(-30);
                    var startKey = $"{order.Id}_start";
                    if (!acknowledgedKeys.Contains(startKey) && nowNy >= alertStart && nowNy <= startNy)
                    {
                        activeReminders.Add(new
                        {
                            orderId = order.Id,
                            type = "start",
                            triggeredAt = alertStart.ToString("o")
                        });
                    }

                    // Check 30 min before end
                    var alertEnd = endNy.AddMinutes(-30);
                    var endKey = $"{order.Id}_end";
                    if (!acknowledgedKeys.Contains(endKey) && nowNy >= alertEnd && nowNy <= endNy)
                    {
                        activeReminders.Add(new
                        {
                            orderId = order.Id,
                            type = "end",
                            triggeredAt = alertEnd.ToString("o")
                        });
                    }
                }

                return Ok(activeReminders);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Get the set of order reminders ("start"/"end") that have already been acknowledged
        /// by any admin. This is the authoritative source the client uses so an acknowledged
        /// reminder never re-appears for any admin, even if the realtime SignalR event was missed.
        /// </summary>
        [HttpGet("orders/acknowledged-reminders")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> GetAcknowledgedOrderReminders()
        {
            try
            {
                // Acknowledgments are made near the service time; the last 2 days covers every
                // currently-relevant reminder window while keeping the payload tiny.
                var cutoffUtc = DateTime.UtcNow.AddDays(-2);
                var acknowledged = await _context.OrderReminderAcknowledgments
                    .Where(a => a.AcknowledgedAt >= cutoffUtc)
                    .Where(a => a.Type == "start" || a.Type == "end")
                    .Select(a => new { orderId = a.OrderId, type = a.Type })
                    .Distinct()
                    .ToListAsync();

                return Ok(acknowledged);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Acknowledge an order reminder. Broadcasts to all connected admins via SignalR.
        /// </summary>
        [HttpPost("orders/{orderId}/acknowledge-reminder")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> AcknowledgeOrderReminder(int orderId, [FromBody] AcknowledgeReminderDto dto)
        {
            try
            {
                var userIdClaim = User.FindFirst("UserId")?.Value;
                if (!int.TryParse(userIdClaim, out int userId))
                    return Unauthorized();

                // Check order exists
                var orderExists = await _context.Orders.AnyAsync(o => o.Id == orderId);
                if (!orderExists)
                    return NotFound(new { message = "Order not found" });

                // Check if already acknowledged
                var alreadyAcked = await _context.OrderReminderAcknowledgments
                    .AnyAsync(a => a.OrderId == orderId && a.Type == dto.Type);

                if (!alreadyAcked)
                {
                    // Save acknowledgment to DB
                    var ack = new OrderReminderAcknowledgment
                    {
                        OrderId = orderId,
                        Type = dto.Type,
                        AcknowledgedByUserId = userId,
                        AcknowledgedAt = DateTime.UtcNow,
                        TriggeredAt = DateTime.UtcNow
                    };
                    _context.OrderReminderAcknowledgments.Add(ack);
                    await _context.SaveChangesAsync();
                }

                // Broadcast to ALL connected admin/superadmin users via SignalR
                var adminUserIds = await _context.Users
                    .Where(u => u.Role == UserRole.Admin || u.Role == UserRole.SuperAdmin)
                    .Select(u => u.Id)
                    .ToListAsync();

                foreach (var adminId in adminUserIds)
                {
                    await _hubContext.Clients.Group($"User_{adminId}")
                        .SendAsync("OrderReminderAcknowledged", new
                        {
                            orderId = orderId,
                            type = dto.Type,
                            acknowledgedByUserId = userId
                        });
                }

                return Ok(new { message = "Reminder acknowledged" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Get order IDs that have not been viewed by any admin yet.
        /// </summary>
        [HttpGet("orders/unviewed-new")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> GetUnviewedNewOrders()
        {
            try
            {
                // Get all order IDs that have been acknowledged with type "new_order"
                var viewedOrderIds = await _context.OrderReminderAcknowledgments
                    .Where(a => a.Type == "new_order")
                    .Select(a => a.OrderId)
                    .Distinct()
                    .ToListAsync();

                // Get all non-cancelled order IDs that haven't been viewed
                var unviewedOrderIds = await _context.Orders
                    .Where(o => o.Status != "Cancelled" && !viewedOrderIds.Contains(o.Id))
                    .OrderByDescending(o => o.CreatedAt)
                    .Select(o => o.Id)
                    .ToListAsync();

                return Ok(unviewedOrderIds);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Mark a new order as viewed. Broadcasts to all admins via SignalR.
        /// </summary>
        [HttpPost("orders/{orderId}/mark-viewed")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> MarkOrderViewed(int orderId)
        {
            try
            {
                var userIdClaim = User.FindFirst("UserId")?.Value;
                if (!int.TryParse(userIdClaim, out int userId))
                    return Unauthorized();

                var orderExists = await _context.Orders.AnyAsync(o => o.Id == orderId);
                if (!orderExists)
                    return NotFound(new { message = "Order not found" });

                // Check if already marked as viewed
                var alreadyViewed = await _context.OrderReminderAcknowledgments
                    .AnyAsync(a => a.OrderId == orderId && a.Type == "new_order");

                if (!alreadyViewed)
                {
                    var ack = new OrderReminderAcknowledgment
                    {
                        OrderId = orderId,
                        Type = "new_order",
                        AcknowledgedByUserId = userId,
                        AcknowledgedAt = DateTime.UtcNow,
                        TriggeredAt = DateTime.UtcNow
                    };
                    _context.OrderReminderAcknowledgments.Add(ack);
                    await _context.SaveChangesAsync();
                }

                // Broadcast to all admins so their indicators update in real-time
                var adminUserIds = await _context.Users
                    .Where(u => u.Role == UserRole.Admin || u.Role == UserRole.SuperAdmin)
                    .Select(u => u.Id)
                    .ToListAsync();

                foreach (var adminId in adminUserIds)
                {
                    await _hubContext.Clients.Group($"User_{adminId}")
                        .SendAsync("NewOrderViewed", new { orderId });
                }

                return Ok(new { message = "Order marked as viewed" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

    }

    /// <summary>Channel selection for the one-shot "Send Payment Link" action.</summary>
    public class SendPaymentLinkDto
    {
        public bool SendEmail { get; set; }
        public bool SendSms { get; set; }
    }
}
