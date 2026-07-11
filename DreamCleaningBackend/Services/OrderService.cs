using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Repositories.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DreamCleaningBackend.Services
{
    public class OrderService : IOrderService
    {
        private readonly IOrderRepository _orderRepository;
        private readonly ApplicationDbContext _context;
        private readonly IStripeService _stripeService;
        private readonly IEmailService _emailService;
        private readonly ISmsService _smsService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<OrderService> _logger;
        private readonly ILoyaltyDiscountService _loyaltyDiscountService;

        public OrderService(IOrderRepository orderRepository, ApplicationDbContext context, IStripeService stripeService, IEmailService emailService, ISmsService smsService, IConfiguration configuration, ILogger<OrderService> logger, ILoyaltyDiscountService loyaltyDiscountService)
        {
            _orderRepository = orderRepository;
            _context = context;
            _stripeService = stripeService;
            _emailService = emailService;
            _smsService = smsService;
            _configuration = configuration;
            _logger = logger;
            _loyaltyDiscountService = loyaltyDiscountService;
        }

        public async Task<List<OrderListDto>> GetAllOrdersForAdmin()
        {
            // Get ALL orders from the database without filtering by userId
            var orders = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.User)
                .Include(o => o.AssignedAdmin)
                .OrderByDescending(o => o.OrderDate)
                .ToListAsync();

            await AutoCancelExpiredUnpaidOrdersIfNeeded(orders);

            // Cleaner+hours detection for the staffing-review badge, without loading every
            // order's service lines: one grouped ID query (same pattern as the Excel export).
            var cleanerServiceOrderIds = new HashSet<int>(await _context.OrderServices
                .Where(os => os.Service.ServiceRelationType == "cleaner")
                .Select(os => os.OrderId)
                .Distinct()
                .ToListAsync());

            return orders.Select(o => new OrderListDto
            {
                Id = o.Id,
                UserId = o.UserId,
                ContactEmail = o.ContactEmail,
                ContactFirstName = o.ContactFirstName,
                ContactLastName = o.ContactLastName,
                ServiceTypeName = o.GetDisplayServiceTypeName(),
                IsCustomServiceType = o.ServiceType?.IsCustom ?? false,
                CustomServiceDisplayName = o.CustomServiceDisplayName,
                ServiceDate = o.ServiceDate,
                ServiceTime = o.ServiceTime,
                Status = o.Status,
                Total = o.Total,
                ServiceAddress = o.ServiceAddress + (string.IsNullOrEmpty(o.AptSuite) ? "" : $", {o.AptSuite}"),
                City = o.City,
                OrderDate = o.OrderDate,
                TotalDuration = o.TotalDuration,
                MaidsCount = o.MaidsCount,
                HasCleanersService = cleanerServiceOrderIds.Contains(o.Id),
                Tips = o.Tips,
                CompanyDevelopmentTips = o.CompanyDevelopmentTips,
                IsPaid = o.IsPaid,
                PaidAt = o.PaidAt,
                CancellationReason = o.CancellationReason,
                IsLateCancellation = o.IsLateCancellation,
                LoyaltyDiscountAmount = o.LoyaltyDiscountAmount,
                LoyaltyDiscountPercentage = o.LoyaltyDiscountPercentage,
                PaymentMethod = o.PaymentMethod.ToString(),
                PaymentReference = o.PaymentReference,
                PaymentNotes = o.PaymentNotes,
                AssignedAdminId = o.AssignedAdminId,
                AssignedAdminFirstName = o.AssignedAdmin != null ? o.AssignedAdmin.FirstName : null,
                AssignedAdminLastName = o.AssignedAdmin != null ? o.AssignedAdmin.LastName : null,
                AssignedAdminDisplayName = o.AssignedAdmin != null
                    ? AdminBonusService.FormatDisplayName(o.AssignedAdmin.FirstName, o.AssignedAdmin.LastName)
                    : null,
                // Legacy orders predate BookedByAdminUserId; a manual-payment recorder means
                // an admin created it, so those still count as admin-booked.
                BookedByAdmin = o.BookedByAdminUserId != null || o.ManualPaymentRecordedByUserId != null
            }).ToList();
        }

        public async Task<List<OrderListDto>> GetUserOrders(int userId)
        {
            var orders = await _orderRepository.GetUserOrdersAsync(userId);

            await AutoCancelExpiredUnpaidOrdersIfNeeded(orders);

            // Pending additional payment = difference (current total − tips) − (original total − tips), not sum of update amounts.
            // Only show when order is paid and there are unpaid update history rows.
            var orderIds = orders.Where(o => o.IsPaid).Select(o => o.Id).ToList();
            var unpaidInfo = await _context.OrderUpdateHistories
                .Where(h => orderIds.Contains(h.OrderId) && !h.IsPaid && h.AdditionalAmount > 0.01m)
                .GroupBy(h => h.OrderId)
                .Select(g => new
                {
                    OrderId = g.Key,
                    LatestHistoryId = g.OrderByDescending(x => x.UpdatedAt).Select(x => (int?)x.Id).FirstOrDefault()
                })
                .ToListAsync();
            var unpaidOrderIds = new HashSet<int>(unpaidInfo.Select(x => x.OrderId));
            var latestHistoryByOrderId = unpaidInfo.ToDictionary(x => x.OrderId, x => x.LatestHistoryId);
            // When order.InitialTotal is 0, use the earliest (any) update history row's original values for the difference
            var firstOriginalList = await _context.OrderUpdateHistories
                .Where(h => unpaidOrderIds.Contains(h.OrderId))
                .GroupBy(h => h.OrderId)
                .Select(g => new
                {
                    OrderId = g.Key,
                    FirstOriginalWithoutTips = g.OrderBy(x => x.UpdatedAt).Select(x => x.OriginalTotal - x.OriginalTips - x.OriginalCompanyDevelopmentTips).FirstOrDefault()
                })
                .ToListAsync();
            var firstOriginalByOrderId = firstOriginalList.ToDictionary(x => x.OrderId, x => x.FirstOriginalWithoutTips);
            // Amount already paid by customer (sum of paid update-history rows) so we only show unpaid portion
            var alreadyPaidList = await _context.OrderUpdateHistories
                .Where(h => unpaidOrderIds.Contains(h.OrderId) && h.IsPaid)
                .GroupBy(h => h.OrderId)
                .Select(g => new { OrderId = g.Key, AlreadyPaid = g.Sum(x => x.AdditionalAmount) })
                .ToListAsync();
            var alreadyPaidByOrderId = alreadyPaidList.ToDictionary(x => x.OrderId, x => x.AlreadyPaid);

            // Points earned per order (positive history rows only)
            var allOrderIds = orders.Select(o => o.Id).ToList();
            var pointsEarnedByOrderId = await _context.BubblePointsHistories
                .Where(h => allOrderIds.Contains(h.OrderId ?? -1) && h.Points > 0)
                .GroupBy(h => h.OrderId)
                .Select(g => new { OrderId = g.Key, Points = g.Sum(h => h.Points) })
                .ToListAsync();
            var pointsEarnedMap = pointsEarnedByOrderId
                .Where(x => x.OrderId.HasValue)
                .ToDictionary(x => x.OrderId!.Value, x => x.Points);

            return orders.Select(o => new OrderListDto
            {
                Id = o.Id,
                UserId = o.UserId,  
                ContactEmail = o.ContactEmail,  
                ContactFirstName = o.ContactFirstName,  
                ContactLastName = o.ContactLastName,  
                ServiceTypeName = o.GetDisplayServiceTypeName(),
                IsCustomServiceType = o.ServiceType?.IsCustom ?? false,
                CustomServiceDisplayName = o.CustomServiceDisplayName,
                ServiceDate = o.ServiceDate,
                ServiceTime = o.ServiceTime,
                Status = o.Status,
                Total = o.Total,
                ServiceAddress = o.ServiceAddress + (string.IsNullOrEmpty(o.AptSuite) ? "" : $", {o.AptSuite}"),
                City = o.City,
                OrderDate = o.OrderDate,
                TotalDuration = o.TotalDuration,
                Tips = o.Tips,
                CompanyDevelopmentTips = o.CompanyDevelopmentTips,
                IsPaid = o.IsPaid,
                PaidAt = o.PaidAt,
                PendingUpdateAmount = o.IsPaid && unpaidOrderIds.Contains(o.Id)
                    ? Math.Max(0m, (o.Total - o.Tips - o.CompanyDevelopmentTips) - (
                        (o.InitialTotal != 0 || o.InitialTips != 0 || o.InitialCompanyDevelopmentTips != 0)
                            ? (o.InitialTotal - o.InitialTips - o.InitialCompanyDevelopmentTips)
                            : (firstOriginalByOrderId.TryGetValue(o.Id, out var firstOrig) ? firstOrig : 0m))
                        - (alreadyPaidByOrderId.TryGetValue(o.Id, out var paid) ? paid : 0m))
                    : 0m,
                PendingUpdateHistoryId = latestHistoryByOrderId.TryGetValue(o.Id, out var lid) ? lid : null,
                CancellationReason = o.CancellationReason,
                IsLateCancellation = o.IsLateCancellation,
                PointsEarned = pointsEarnedMap.TryGetValue(o.Id, out var pe) ? pe : 0,
                LoyaltyDiscountAmount = o.LoyaltyDiscountAmount,
                LoyaltyDiscountPercentage = o.LoyaltyDiscountPercentage,
                PaymentMethod = o.PaymentMethod.ToString(),
                PaymentReference = o.PaymentReference,
                PaymentNotes = o.PaymentNotes,
                AssignedAdminId = o.AssignedAdminId,
                AssignedAdminFirstName = o.AssignedAdmin != null ? o.AssignedAdmin.FirstName : null,
                AssignedAdminLastName = o.AssignedAdmin != null ? o.AssignedAdmin.LastName : null,
                AssignedAdminDisplayName = o.AssignedAdmin != null
                    ? AdminBonusService.FormatDisplayName(o.AssignedAdmin.FirstName, o.AssignedAdmin.LastName)
                    : null
            }).ToList();
        }

        public async Task<OrderDto> GetOrderById(int orderId, int userId)
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId);

            if (order == null || order.UserId != userId)
                throw new Exception("Order not found");

            await AutoCancelExpiredUnpaidOrderIfNeeded(order);

            var dto = MapOrderToDto(order);

            // Pending additional payment = difference (current total − tips) − (original total − tips), not sum of update amounts.
            if (order.IsPaid)
            {
                var hasUnpaid = await _context.OrderUpdateHistories
                    .AnyAsync(h => h.OrderId == order.Id && !h.IsPaid && h.AdditionalAmount > 0.01m);
                if (hasUnpaid)
                {
                    var currentWithoutTips = order.Total - order.Tips - order.CompanyDevelopmentTips;
                    decimal originalWithoutTips;
                    if (order.InitialTotal != 0 || order.InitialTips != 0 || order.InitialCompanyDevelopmentTips != 0)
                        originalWithoutTips = order.InitialTotal - order.InitialTips - order.InitialCompanyDevelopmentTips;
                    else
                    {
                        var firstHist = await _context.OrderUpdateHistories
                            .Where(h => h.OrderId == order.Id)
                            .OrderBy(h => h.UpdatedAt)
                            .Select(h => new { h.OriginalTotal, h.OriginalTips, h.OriginalCompanyDevelopmentTips })
                            .FirstOrDefaultAsync();
                        originalWithoutTips = firstHist != null ? (firstHist.OriginalTotal - firstHist.OriginalTips - firstHist.OriginalCompanyDevelopmentTips) : 0m;
                    }
                    var alreadyPaid = await _context.OrderUpdateHistories
                        .Where(h => h.OrderId == order.Id && h.IsPaid)
                        .SumAsync(h => h.AdditionalAmount);
                    dto.PendingUpdateAmount = Math.Max(0m, currentWithoutTips - originalWithoutTips - alreadyPaid);
                    var latest = await _context.OrderUpdateHistories
                        .Where(h => h.OrderId == order.Id && !h.IsPaid && h.AdditionalAmount > 0.01m)
                        .OrderByDescending(h => h.UpdatedAt)
                        .Select(h => (int?)h.Id)
                        .FirstOrDefaultAsync();
                    dto.PendingUpdateHistoryId = latest;
                }
                else
                {
                    dto.PendingUpdateAmount = 0m;
                    dto.PendingUpdateHistoryId = null;
                }
            }
            else
            {
                dto.PendingUpdateAmount = 0m;
                dto.PendingUpdateHistoryId = null;
            }

            return dto;
        }

        private static DateTime GetServiceDateTimeUtc(Order order)
        {
            // ServiceDate is stored as a DateTime (date portion); ServiceTime is stored separately.
            // Combine them and convert from business timezone (Eastern) to UTC.
            var combined = order.ServiceDate.Date.Add(order.ServiceTime);
            var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var localTime = DateTime.SpecifyKind(combined, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(localTime, eastern);
        }

        private async Task AutoCancelExpiredUnpaidOrdersIfNeeded(IReadOnlyCollection<Order> orders)
        {
            if (orders == null || orders.Count == 0) return;

            var nowUtc = DateTime.UtcNow;
            var changed = false;

            foreach (var order in orders)
            {
                if (order == null) continue;
                // Phase 1: manual-paid orders (PaymentMethod != Normal) have IsPaid=false by
                // design — IsPaid is Stripe-only — so treating them as "unpaid" here would
                // auto-cancel them once their service date passes. Treat both Stripe-paid
                // and manual-paid orders as "paid" for the purposes of auto-cancel.
                if (order.IsPaid || order.PaymentMethod != PaymentMethod.Normal) continue;
                if (order.IsAutoCancelExempt) continue;
                if (string.Equals(order.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(order.Status, "Done", StringComparison.OrdinalIgnoreCase)) continue;

                var serviceUtc = GetServiceDateTimeUtc(order);
                if (serviceUtc < nowUtc)
                {
                    order.Status = "Cancelled";
                    order.CancellationReason ??= "Order expired (unpaid)";
                    order.UpdatedAt = nowUtc;
                    changed = true;
                }
            }

            if (changed)
            {
                await _context.SaveChangesAsync();
            }
        }

        private async Task AutoCancelExpiredUnpaidOrderIfNeeded(Order order)
        {
            if (order == null) return;
            // Phase 1: manual-paid orders have IsPaid=false by design. Treat them as "paid"
            // here so they don't get auto-cancelled when their service date passes.
            if (order.IsPaid || order.PaymentMethod != PaymentMethod.Normal) return;
            if (order.IsAutoCancelExempt) return;
            if (string.Equals(order.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(order.Status, "Done", StringComparison.OrdinalIgnoreCase)) return;

            var nowUtc = DateTime.UtcNow;
            var serviceUtc = GetServiceDateTimeUtc(order);
            if (serviceUtc < nowUtc)
            {
                order.Status = "Cancelled";
                order.CancellationReason ??= "Order expired (unpaid)";
                order.UpdatedAt = nowUtc;
                await _context.SaveChangesAsync();
            }
        }

        public async Task<OrderDto> UpdateOrder(int orderId, int userId, UpdateOrderDto updateOrderDto)
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId);

            if (order == null || order.UserId != userId)
                throw new Exception("Order not found");

            if (order.Status == "Cancelled")
                throw new Exception("Cannot update a cancelled order");

            if (order.Status == "Done")
                throw new Exception("Cannot update a completed order");

            // 48-HOUR VALIDATION CHECK (ServiceDate/ServiceTime are NY wall-clock; compare in UTC)
            var hoursUntilService = (GetServiceDateTimeUtc(order) - DateTime.UtcNow).TotalHours;
            if (hoursUntilService <= 48)
            {
                throw new Exception("Orders can only be edited at least 48 hours before the scheduled service time");
            }

            // Define tolerance for floating-point comparisons
            const decimal tolerance = 0.01m; // 1 cent tolerance

            // Store the original values
            var originalTotal = order.Total;

            // store original values before they're modified:
            var originalSubTotal = order.SubTotal;
            var originalTax = order.Tax;
            var originalTips = order.Tips;
            var originalCompanyDevelopmentTips = order.CompanyDevelopmentTips;

            // Calculate the additional amount before updating
            var additionalAmount = await CalculateAdditionalAmount(orderId, updateOrderDto);

            // Check if the new total would be less than the original
            // Use the tolerance to handle floating-point precision issues
            if (additionalAmount < -tolerance)
            {
                var newTotal = originalTotal + additionalAmount;
                throw new Exception($"Cannot reduce order total. Original: ${originalTotal:F2}, New: ${newTotal:F2}, Difference: ${Math.Abs(additionalAmount):F2}");
            }

            // Update basic order information
            order.ServiceDate = updateOrderDto.ServiceDate;
            order.ServiceTime = TimeSpan.Parse(updateOrderDto.ServiceTime);
            order.EntryMethod = updateOrderDto.EntryMethod;
            order.SpecialInstructions = updateOrderDto.SpecialInstructions;
            order.FloorTypes = updateOrderDto.FloorTypes;
            order.FloorTypeOther = updateOrderDto.FloorTypeOther;
            order.ContactFirstName = updateOrderDto.ContactFirstName;
            order.ContactLastName = updateOrderDto.ContactLastName;
            order.ContactEmail = updateOrderDto.ContactEmail;
            order.ContactPhone = updateOrderDto.ContactPhone;
            order.ServiceAddress = updateOrderDto.ServiceAddress;
            order.AptSuite = updateOrderDto.AptSuite;
            order.City = updateOrderDto.City;
            order.State = updateOrderDto.State;
            order.ZipCode = updateOrderDto.ZipCode;
            order.Tips = updateOrderDto.Tips;
            order.CompanyDevelopmentTips = updateOrderDto.CompanyDevelopmentTips;
            if (updateOrderDto.BedroomsQuantity.HasValue)
                order.BedroomsQuantity = updateOrderDto.BedroomsQuantity.Value;
            if (updateOrderDto.BathroomsQuantity.HasValue)
                order.BathroomsQuantity = updateOrderDto.BathroomsQuantity.Value;
            order.UpdatedAt = DateTime.UtcNow;

            // Update user's phone number if they don't have one
            var user = await _context.Users.FindAsync(userId);
            if (user != null && string.IsNullOrEmpty(user.Phone) && !string.IsNullOrEmpty(updateOrderDto.ContactPhone))
            {
                user.Phone = updateOrderDto.ContactPhone;
                user.UpdatedAt = DateTime.UtcNow;
                _logger.LogInformation("Backfilled missing phone number for user {UserId} from order edit", userId);
            }

            // Price the updated selections through the shared calculator (single source of
            // truth — see OrderPricingCalculator). Replaces the old inline subtotal/duration math.
            var quoteInput = await BuildUpdateQuoteInputAsync(order, updateOrderDto);
            var quote = OrderPricingCalculator.CalculateQuote(quoteInput);

            if (Math.Abs(updateOrderDto.TotalDuration - quote.TotalDuration) > 5)
            {
                _logger.LogWarning("Duration mismatch — frontend sent {FrontendDuration}, backend calculated {BackendDuration}. Backend value wins.",
                    updateOrderDto.TotalDuration, quote.TotalDuration);
            }

            // Replace order lines with the recalculated ones.
            _context.OrderServices.RemoveRange(order.OrderServices);
            _context.OrderExtraServices.RemoveRange(order.OrderExtraServices);
            OrderPricingCalculator.AddOrderLinesFromQuote(order, quote);

            // Backend-authoritative, like TotalDuration below — the calculator derives the
            // maids count from the same selections, so the client's copy is redundant at best.
            // With auto-staffing off, the count is an admin decision for regular service types,
            // so a customer edit must not reset it; cleaner-hours types still follow the
            // customer's explicit cleaner quantity.
            if (OrderPricingCalculator.AutoAddCleanersByDuration || quote.HasCleanerService)
                order.MaidsCount = quote.MaidsCount;
            else
                order.MaidsCount = Math.Max(order.MaidsCount, quote.MaidsCount);
            order.TotalDuration = quote.TotalDuration;

            // Recompute cleaner total salary so it stays in sync with the new duration/maids.
            order.CleanerTotalSalary = OrderPricingCalculator.CalculateCleanerTotalSalary(
                order.TotalDuration, order.MaidsCount, quote.HasCleanerService, order.CleanerHourlyRate);

            // Recalculate totals. Edit flows rescale the ORIGINAL discounts server-side:
            // promo/subscription by the subtotal ratio, loyalty from the locked percentage
            // snapshot — the same math as the order-edit page preview (calculateNewTotal),
            // so the client's discount dollar figures in the DTO are never trusted.
            order.SubTotal = quote.SubTotal;
            if (originalSubTotal > 0)
            {
                order.DiscountAmount = OrderPricingCalculator.Round2(
                    quote.SubTotal * (order.DiscountAmount / originalSubTotal));
                order.SubscriptionDiscountAmount = OrderPricingCalculator.Round2(
                    quote.SubTotal * (order.SubscriptionDiscountAmount / originalSubTotal));
            }
            else
            {
                order.DiscountAmount = 0m;
                order.SubscriptionDiscountAmount = 0m;
            }
            order.LoyaltyDiscountAmount = order.LoyaltyDiscountPercentage > 0
                ? OrderPricingCalculator.Round2(quote.SubTotal * (order.LoyaltyDiscountPercentage / 100m))
                : 0m;

            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = order.SubTotal,
                DiscountAmount = order.DiscountAmount,
                SubscriptionDiscountAmount = order.SubscriptionDiscountAmount,
                LoyaltyDiscountAmount = order.LoyaltyDiscountAmount,
                Tips = order.Tips,
                CompanyDevelopmentTips = order.CompanyDevelopmentTips
            });
            order.Tax = totals.Tax;

            // Bubble points + reward balance credits applied at booking time. They must be
            // subtracted (like gift cards), otherwise editing an order silently inflates the total.
            var pointsAndRewardCredits = order.PointsRedeemedDiscount + order.RewardBalanceUsed;

            // Re-resolve + re-apply the gift card against the new pre-gift-card total (shared with
            // the admin edit path). Sets order.GiftCardAmountUsed and order.Total, and mutates the
            // gift card balance/usage. A price increase is absorbed by leftover gift-card funds
            // before any card charge — see ApplyEditGiftCardAsync.
            await ApplyEditGiftCardAsync(order, totals.TotalBeforeGiftCard, pointsAndRewardCredits);

            // Final check to ensure the new total is not less than the original
            // Use the tolerance to handle floating-point precision issues
            if (order.Total < originalTotal - tolerance)
            {
                throw new Exception($"Cannot save changes. The new total (${order.Total:F2}) is less than the original amount paid (${originalTotal:F2}). Please add more services or keep the current selection.");
            }

            try
            {
                await _emailService.SendOrderUpdateNotificationAsync(
                    orderId: order.Id,
                    customerEmail: order.ContactEmail,
                    additionalAmount: additionalAmount
                );
            }
            catch (Exception ex)
            {
                // Log the error but don't fail the order update
                _logger.LogError(ex, $"Failed to send order update notification for Order #{order.Id}");
            }

            // Create update history record for ALL changes (not just when there's additional amount)
            // This ensures audit logs show changes even when there's no monetary difference
            var updateHistory = new OrderUpdateHistory
            {
                OrderId = order.Id,
                UpdatedByUserId = userId,
                UpdatedAt = DateTime.UtcNow,
                // Use the stored original values
                OriginalSubTotal = originalSubTotal,
                OriginalTax = originalTax,
                OriginalTips = originalTips,
                OriginalCompanyDevelopmentTips = originalCompanyDevelopmentTips,
                OriginalTotal = originalTotal,
                // New values after update
                NewSubTotal = order.SubTotal,
                NewTax = order.Tax,
                NewTips = order.Tips,
                NewCompanyDevelopmentTips = order.CompanyDevelopmentTips,
                NewTotal = order.Total,
                AdditionalAmount = additionalAmount,
                IsPaid = additionalAmount <= 0.01m // Mark as paid if no additional amount required
            };

            _context.OrderUpdateHistories.Add(updateHistory);

            await _orderRepository.UpdateAsync(order);
            await _orderRepository.SaveChangesAsync();

            return await GetOrderById(orderId, userId);
        }

        public async Task<OrderUpdatePaymentDto> CreateUpdatePaymentIntent(int orderId, int userId, UpdateOrderDto updateOrderDto)
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId);

            if (order == null || order.UserId != userId)
                throw new Exception("Order not found");

            if (order.Status == "Cancelled" || order.Status == "Done")
                throw new Exception($"Cannot update a {order.Status.ToLower()} order");

            // Calculate additional amount
            var additionalAmount = await CalculateAdditionalAmount(orderId, updateOrderDto);

            if (additionalAmount <= 0)
                throw new Exception("No additional payment required");

            // Create payment intent for the additional amount
            var metadata = new Dictionary<string, string>
    {
        { "orderId", order.Id.ToString() },
        { "userId", userId.ToString() },
        { "type", "order_update" },
        { "additionalAmount", additionalAmount.ToString("F2") }
    };

            var paymentIntent = await _stripeService.CreatePaymentIntentAsync(additionalAmount, metadata);

            return new OrderUpdatePaymentDto
            {
                OrderId = order.Id,
                AdditionalAmount = additionalAmount,
                PaymentIntentId = paymentIntent.Id,
                PaymentClientSecret = paymentIntent.ClientSecret
            };
        }

        public async Task<bool> CancelOrder(int orderId, int userId, CancelOrderDto cancelOrderDto)
        {
            var order = await _orderRepository.GetByIdAsync(orderId);
            if (order == null || order.UserId != userId)
                throw new Exception("Order not found");
            if (order.Status == "Cancelled")
                throw new Exception("Order is already cancelled");
            if (order.Status == "Done")
                throw new Exception("Cannot cancel a completed order");

            // Determine if late cancellation fee applies (within 48 hours of service for paid orders).
            // ServiceDate/ServiceTime are NY wall-clock; convert to UTC before comparing.
            bool isLateCancellation = order.IsPaid && GetServiceDateTimeUtc(order) <= DateTime.UtcNow.AddHours(48);

            order.Status = "Cancelled";
            order.CancellationReason = cancelOrderDto.Reason;
            order.IsLateCancellation = isLateCancellation;
            order.UpdatedAt = DateTime.UtcNow;
            await _orderRepository.UpdateAsync(order);
            await _orderRepository.SaveChangesAsync();

            // Restore special offer if one was used
            var userSpecialOffer = await _context.UserSpecialOffers
                .FirstOrDefaultAsync(uso => uso.UsedOnOrderId == orderId);

            if (userSpecialOffer != null)
            {
                userSpecialOffer.IsUsed = false;
                userSpecialOffer.UsedAt = null;
                userSpecialOffer.UsedOnOrderId = null;
                await _context.SaveChangesAsync();
            }

            // Restore loyalty discount snapshot to the user's account if this order had one.
            // No-ops when the order didn't consume loyalty. Mirrors the UserSpecialOffer reset
            // pattern above.
            if (order.LoyaltyDiscountAmount > 0m && order.LoyaltyDiscountPercentage > 0m)
            {
                try
                {
                    await _loyaltyDiscountService.ReverseFromOrderAsync(orderId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Loyalty discount reverse failed for order {OrderId} on user cancel — order is cancelled but user state may be stale", orderId);
                }
            }

            return true;
        }

        // ===== Shared pricing (single source of truth: OrderPricingCalculator) =====
        // DTO→input mapping (incl. the original-hours fallback) lives in OrderPricingInputBuilder.
        private Task<OrderPricingCalculator.QuoteInput> BuildUpdateQuoteInputAsync(Order order, UpdateOrderDto dto)
            => OrderPricingInputBuilder.FromUpdateDtoAsync(_context, order, dto);

        // ===== Gift card re-resolution on edit (single source for user + admin edits) =====
        // When an order's total changes during an edit, the gift card is re-applied: what this
        // order already consumed is "given back" to the live balance first, then re-drawn up to
        // min(availableBalance, newTotalBeforeGiftCard). So a price increase is absorbed by any
        // leftover gift-card funds BEFORE the customer's card is charged, and a decrease releases
        // funds back to the gift card. The amount formula lives here once.
        private static decimal ResolveEditGiftCardUse(decimal giftCardCurrentBalance, decimal originalAmountUsed, decimal totalBeforeGiftCard)
            => Math.Min(giftCardCurrentBalance + originalAmountUsed, totalBeforeGiftCard);

        // Read-only: the order's would-be Total after re-resolving the gift card (no mutation).
        // Used by CalculateAdditionalAmount so its preview matches what UpdateOrder will persist.
        private async Task<decimal> CalculateEditTotalAfterGiftCardAsync(Order order, decimal totalBeforeGiftCard, decimal pointsAndRewardCredits)
        {
            var newGiftCardAmountToUse = order.GiftCardAmountUsed;
            if (!string.IsNullOrEmpty(order.GiftCardCode) && order.GiftCardAmountUsed > 0)
            {
                var giftCard = await _context.GiftCards.AsNoTracking()
                    .FirstOrDefaultAsync(g => g.Code == order.GiftCardCode);
                if (giftCard != null && giftCard.IsActive)
                    newGiftCardAmountToUse = ResolveEditGiftCardUse(giftCard.CurrentBalance, order.GiftCardAmountUsed, totalBeforeGiftCard);
                // else: can't re-resolve — keep what was already used (matches ApplyEditGiftCardAsync)
            }
            return Math.Max(0m, totalBeforeGiftCard - newGiftCardAmountToUse - pointsAndRewardCredits);
        }

        // Mutating: re-resolves the gift card, updates its balance + usage record, and sets
        // order.GiftCardAmountUsed and order.Total. Used by BOTH user (UpdateOrder) and admin
        // (SuperAdminFullUpdateOrder) edits so they stay consistent.
        private async Task ApplyEditGiftCardAsync(Order order, decimal totalBeforeGiftCard, decimal pointsAndRewardCredits)
        {
            var originalGiftCardAmountUsed = order.GiftCardAmountUsed;

            if (string.IsNullOrEmpty(order.GiftCardCode) || originalGiftCardAmountUsed <= 0)
            {
                order.Total = Math.Max(0m, totalBeforeGiftCard - pointsAndRewardCredits);
                return;
            }

            var giftCard = await _context.GiftCards.FirstOrDefaultAsync(g => g.Code == order.GiftCardCode);
            if (giftCard == null || !giftCard.IsActive)
            {
                _logger.LogWarning("Gift card {GiftCardCode} not found or inactive while editing order {OrderId} — keeping original amount used", order.GiftCardCode, order.Id);
                order.Total = Math.Max(0m, totalBeforeGiftCard - originalGiftCardAmountUsed - pointsAndRewardCredits);
                return;
            }

            var availableBalance = giftCard.CurrentBalance + originalGiftCardAmountUsed;
            var newGiftCardAmountToUse = ResolveEditGiftCardUse(giftCard.CurrentBalance, originalGiftCardAmountUsed, totalBeforeGiftCard);
            var giftCardDifference = newGiftCardAmountToUse - originalGiftCardAmountUsed;

            if (Math.Abs(giftCardDifference) > 0.01m) // only touch records on a meaningful change
            {
                giftCard.CurrentBalance = availableBalance - newGiftCardAmountToUse;
                giftCard.UpdatedAt = DateTime.UtcNow;
                order.GiftCardAmountUsed = newGiftCardAmountToUse;

                var existingUsage = await _context.GiftCardUsages
                    .FirstOrDefaultAsync(u => u.GiftCardId == giftCard.Id && u.OrderId == order.Id);
                if (existingUsage != null)
                {
                    existingUsage.AmountUsed = newGiftCardAmountToUse;
                    existingUsage.BalanceAfterUsage = giftCard.CurrentBalance;
                    existingUsage.UsedAt = DateTime.UtcNow;
                }
                else
                {
                    _logger.LogWarning("No existing GiftCardUsage record for gift card {GiftCardId} on order {OrderId} — creating one during order edit", giftCard.Id, order.Id);
                    _context.GiftCardUsages.Add(new GiftCardUsage
                    {
                        GiftCardId = giftCard.Id,
                        OrderId = order.Id,
                        UserId = order.UserId,
                        AmountUsed = newGiftCardAmountToUse,
                        BalanceAfterUsage = giftCard.CurrentBalance,
                        UsedAt = DateTime.UtcNow
                    });
                }
            }

            // Same rounding + clamping the calculator applies everywhere else.
            order.Total = OrderPricingCalculator.Round2(
                Math.Max(0m, totalBeforeGiftCard - order.GiftCardAmountUsed - pointsAndRewardCredits));
        }

        public async Task<decimal> CalculateAdditionalAmount(int orderId, UpdateOrderDto updateOrderDto)
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId);

            if (order == null)
                throw new Exception("Order not found");

            // Store original values for comparison - DO NOT MODIFY THE ORDER OBJECT!
            var originalTotal = order.Total;

            // Price the updated selections through the shared calculator (single source of
            // truth — see OrderPricingCalculator). Read-only: the order object is not modified.
            var quoteInput = await BuildUpdateQuoteInputAsync(order, updateOrderDto);
            var quote = OrderPricingCalculator.CalculateQuote(quoteInput);

            if (Math.Abs(updateOrderDto.TotalDuration - quote.TotalDuration) > 5)
            {
                _logger.LogWarning("Duration mismatch — frontend sent {FrontendDuration}, backend calculated {BackendDuration}. Backend value wins.",
                    updateOrderDto.TotalDuration, quote.TotalDuration);
            }

            // Server-derived discounts, matching exactly what UpdateOrder will persist:
            // promo/subscription rescaled by the subtotal ratio, loyalty re-derived from the
            // locked percentage snapshot. The DTO's discount fields are intentionally ignored
            // (client dollar figures are never trusted).
            var discountAmount = order.SubTotal > 0
                ? OrderPricingCalculator.Round2(quote.SubTotal * (order.DiscountAmount / order.SubTotal))
                : 0m;
            var subscriptionDiscountAmount = order.SubTotal > 0
                ? OrderPricingCalculator.Round2(quote.SubTotal * (order.SubscriptionDiscountAmount / order.SubTotal))
                : 0m;
            var loyaltyDiscountAmount = order.LoyaltyDiscountPercentage > 0
                ? OrderPricingCalculator.Round2(quote.SubTotal * (order.LoyaltyDiscountPercentage / 100m))
                : 0m;

            // Compute the pre-gift-card total, then re-resolve the gift card the SAME way
            // UpdateOrder will persist it (via the shared helpers). This is what makes the
            // additional charge reflect any leftover gift-card balance absorbing the increase —
            // otherwise the customer is charged the full difference even though the gift card
            // covered part/all of it.
            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = quote.SubTotal,
                DiscountAmount = discountAmount,
                SubscriptionDiscountAmount = subscriptionDiscountAmount,
                LoyaltyDiscountAmount = loyaltyDiscountAmount,
                Tips = updateOrderDto.Tips,
                CompanyDevelopmentTips = updateOrderDto.CompanyDevelopmentTips
                // gift card + points/rewards applied below to mirror UpdateOrder
            });
            var pointsAndRewardCredits = order.PointsRedeemedDiscount + order.RewardBalanceUsed;
            var newTotal = await CalculateEditTotalAfterGiftCardAsync(order, totals.TotalBeforeGiftCard, pointsAndRewardCredits);

            var finalAdditionalAmount = newTotal - originalTotal;

            // If the difference is within tolerance (1 cent), consider it zero
            if (Math.Abs(finalAdditionalAmount) < 0.01m)
            {
                finalAdditionalAmount = 0;
            }

            return finalAdditionalAmount;
        }

        public async Task<bool> MarkOrderAsDone(int orderId)
        {
            var order = await _orderRepository.GetByIdAsync(orderId);

            if (order == null)
                throw new Exception("Order not found");

            if (order.Status == "Cancelled")
                throw new Exception("Cannot complete a cancelled order");

            order.Status = "Done";
            order.UpdatedAt = DateTime.UtcNow;

            await _orderRepository.UpdateAsync(order);
            await _orderRepository.SaveChangesAsync();

            return true;
        }

        public async Task<List<OrderListDto>> GetUserOrdersForAdmin(int userId)
        {
            var orders = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.User)
                .Include(o => o.AssignedAdmin)
                .Where(o => o.UserId == userId)
                .OrderByDescending(o => o.OrderDate)
                .ToListAsync();

            return orders.Select(o => new OrderListDto
            {
                Id = o.Id,
                UserId = o.UserId,
                ContactEmail = o.ContactEmail,
                ContactFirstName = o.ContactFirstName,
                ContactLastName = o.ContactLastName,
                ServiceTypeName = o.GetDisplayServiceTypeName(),
                IsCustomServiceType = o.ServiceType?.IsCustom ?? false,
                CustomServiceDisplayName = o.CustomServiceDisplayName,
                ServiceDate = o.ServiceDate,
                ServiceTime = o.ServiceTime,
                Status = o.Status,
                Total = o.Total,
                ServiceAddress = o.ServiceAddress + (string.IsNullOrEmpty(o.AptSuite) ? "" : $", {o.AptSuite}"),
                City = o.City,
                OrderDate = o.OrderDate,
                TotalDuration = o.TotalDuration,
                Tips = o.Tips,
                CompanyDevelopmentTips = o.CompanyDevelopmentTips,
                IsPaid = o.IsPaid,
                PaidAt = o.PaidAt,
                CancellationReason = o.CancellationReason,
                IsLateCancellation = o.IsLateCancellation,
                LoyaltyDiscountAmount = o.LoyaltyDiscountAmount,
                LoyaltyDiscountPercentage = o.LoyaltyDiscountPercentage,
                PaymentMethod = o.PaymentMethod.ToString(),
                PaymentReference = o.PaymentReference,
                PaymentNotes = o.PaymentNotes,
                AssignedAdminId = o.AssignedAdminId,
                AssignedAdminFirstName = o.AssignedAdmin != null ? o.AssignedAdmin.FirstName : null,
                AssignedAdminLastName = o.AssignedAdmin != null ? o.AssignedAdmin.LastName : null,
                AssignedAdminDisplayName = o.AssignedAdmin != null
                    ? AdminBonusService.FormatDisplayName(o.AssignedAdmin.FirstName, o.AssignedAdmin.LastName)
                    : null
            }).ToList();
        }

        public async Task<OrderDto> GetOrderByIdForAdmin(int orderId)
        {
            var order = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.Subscription)
                .Include(o => o.OrderServices)
                    .ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices)
                    .ThenInclude(oes => oes.ExtraService)
                .Include(o => o.User)
                .Include(o => o.AssignedAdmin)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                throw new Exception("Order not found");

            // Single source of truth for the order-details shape (see OrderDtoMapper).
            return OrderDtoMapper.ToOrderDto(order);
        }

        // Promo/special-offer/gift-card display helpers live in OrderDtoMapper.

        private OrderDto MapOrderToDto(Order order)
        {
            // Single source of truth for the order-details shape (see OrderDtoMapper).
            var pointsEarned = _context.BubblePointsHistories
                .Where(h => h.OrderId == order.Id && h.Points > 0)
                .Sum(h => h.Points);
            return OrderDtoMapper.ToOrderDto(order, pointsEarned);
        }

        /// <summary>SuperAdmin-only: full order update without 48h or "can't reduce" checks. All changes must be audit-logged by the caller.</summary>
        public async Task SuperAdminFullUpdateOrder(int orderId, int updatedByUserId, SuperAdminUpdateOrderDto dto)
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId);
            if (order == null)
                throw new Exception("Order not found");

            // Store original values for update-history/payment tracking
            var originalSubTotal = order.SubTotal;
            var originalTax = order.Tax;
            var originalTips = order.Tips;
            var originalCompanyDevelopmentTips = order.CompanyDevelopmentTips;
            var originalTotal = order.Total;

            if (dto.ContactFirstName != null) order.ContactFirstName = dto.ContactFirstName;
            if (dto.ContactLastName != null) order.ContactLastName = dto.ContactLastName;
            if (dto.ContactEmail != null) order.ContactEmail = dto.ContactEmail;
            if (dto.ContactPhone != null) order.ContactPhone = dto.ContactPhone;
            if (dto.ServiceAddress != null) order.ServiceAddress = dto.ServiceAddress;
            if (dto.AptSuite != null) order.AptSuite = dto.AptSuite;
            if (dto.City != null) order.City = dto.City;
            if (dto.State != null) order.State = dto.State;
            if (dto.ZipCode != null) order.ZipCode = dto.ZipCode;
            if (dto.ServiceDate.HasValue) order.ServiceDate = dto.ServiceDate.Value;
            if (dto.ServiceTime != null && TimeSpan.TryParse(dto.ServiceTime, out var st)) order.ServiceTime = st;
            if (dto.MaidsCount.HasValue) order.MaidsCount = dto.MaidsCount.Value;
            if (dto.TotalDuration.HasValue) order.TotalDuration = dto.TotalDuration.Value;
            if (dto.BedroomsQuantity.HasValue) order.BedroomsQuantity = dto.BedroomsQuantity.Value;
            if (dto.BathroomsQuantity.HasValue) order.BathroomsQuantity = dto.BathroomsQuantity.Value;
            if (dto.EntryMethod != null) order.EntryMethod = dto.EntryMethod;
            if (dto.SpecialInstructions != null) order.SpecialInstructions = dto.SpecialInstructions;
            if (dto.FloorTypes != null) order.FloorTypes = dto.FloorTypes;
            if (dto.FloorTypeOther != null) order.FloorTypeOther = dto.FloorTypeOther;
            if (dto.Tips.HasValue) order.Tips = dto.Tips.Value;
            if (dto.CompanyDevelopmentTips.HasValue) order.CompanyDevelopmentTips = dto.CompanyDevelopmentTips.Value;
            if (dto.Status != null)
            {
                // When reactivating a cancelled order, exempt it from auto-cancellation
                if (string.Equals(order.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(dto.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    order.IsAutoCancelExempt = true;
                }
                order.Status = dto.Status;
            }
            if (dto.CancellationReason != null) order.CancellationReason = dto.CancellationReason;
            if (dto.SubTotal.HasValue) order.SubTotal = dto.SubTotal.Value;
            if (dto.DiscountAmount.HasValue) order.DiscountAmount = dto.DiscountAmount.Value;
            if (dto.SubscriptionDiscountAmount.HasValue) order.SubscriptionDiscountAmount = dto.SubscriptionDiscountAmount.Value;
            // Loyalty discount amount may shift with subtotal edits; the percentage snapshot
            // is invariant (see Order.LoyaltyDiscountPercentage comment) so we never change it
            // through this endpoint — only the recalculated $ amount.
            if (dto.LoyaltyDiscountAmount.HasValue) order.LoyaltyDiscountAmount = dto.LoyaltyDiscountAmount.Value;
            if (dto.CleanerHourlyRate.HasValue) order.CleanerHourlyRate = dto.CleanerHourlyRate.Value;
            if (dto.CleanerTotalSalary.HasValue) order.CleanerTotalSalary = dto.CleanerTotalSalary.Value;
            // Custom ("Pre-Arranged") orders only: relabel the display name. null = no change,
            // empty string = clear back to "Arranged". Mirrors OrderController.SetCustomServiceName.
            if (dto.CustomServiceDisplayName != null && order.ServiceType?.IsCustom == true)
            {
                var trimmedName = dto.CustomServiceDisplayName.Trim();
                order.CustomServiceDisplayName = string.IsNullOrWhiteSpace(trimmedName) ? null : trimmedName;
            }

            // Auto-recalculate cleaner total salary when hourly rate or duration or maids count changes
            if (dto.CleanerHourlyRate.HasValue || dto.TotalDuration.HasValue || dto.MaidsCount.HasValue)
            {
                // Only auto-recalculate if CleanerTotalSalary was NOT explicitly provided
                if (!dto.CleanerTotalSalary.HasValue)
                {
                    bool hasCleanersService = order.OrderServices.Any(os =>
                        os.Service?.ServiceRelationType == "cleaner");
                    order.CleanerTotalSalary = OrderPricingCalculator.CalculateCleanerTotalSalary(
                        order.TotalDuration, order.MaidsCount, hasCleanersService, order.CleanerHourlyRate);
                }
            }

            // Auto-calculate tax/total through the shared calculator (so SuperAdmin only needs
            // to edit SubTotal). Loyalty is included alongside subscription + promo. Gift card is
            // applied via the shared re-resolution below (NOT baked in here) so an increased total
            // draws additional funds from any leftover gift-card balance before the customer pays.
            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = order.SubTotal,
                DiscountAmount = order.DiscountAmount,
                SubscriptionDiscountAmount = order.SubscriptionDiscountAmount,
                LoyaltyDiscountAmount = order.LoyaltyDiscountAmount,
                Tips = order.Tips,
                CompanyDevelopmentTips = order.CompanyDevelopmentTips
                // gift card + points/rewards applied below to mirror the user edit path
            });
            order.Tax = totals.Tax;
            var pointsAndRewardCredits = order.PointsRedeemedDiscount + order.RewardBalanceUsed;
            await ApplyEditGiftCardAsync(order, totals.TotalBeforeGiftCard, pointsAndRewardCredits);

            if (dto.Services != null)
            {
                foreach (var s in dto.Services)
                {
                    var os = order.OrderServices?.FirstOrDefault(x => x.Id == s.OrderServiceId);
                    if (os != null) { os.Quantity = s.Quantity; os.Cost = s.Cost; }
                }
            }
            if (dto.ExtraServices != null)
            {
                var existingExtraIdsInDto = dto.ExtraServices.Where(x => x.OrderExtraServiceId != 0).Select(x => x.OrderExtraServiceId).ToHashSet();
                foreach (var e in dto.ExtraServices)
                {
                    if (e.OrderExtraServiceId != 0)
                    {
                        var oes = order.OrderExtraServices?.FirstOrDefault(x => x.Id == e.OrderExtraServiceId);
                        if (oes != null) { oes.Quantity = e.Quantity; oes.Hours = e.Hours; oes.Cost = e.Cost; }
                    }
                    else if (e.ExtraServiceId.HasValue && e.ExtraServiceId.Value != 0)
                    {
                        // Add new extra service to the order
                        var extraService = await _context.ExtraServices.FindAsync(e.ExtraServiceId.Value);
                        if (extraService != null)
                        {
                            if (order.OrderExtraServices == null) order.OrderExtraServices = new List<OrderExtraService>();
                            order.OrderExtraServices.Add(new OrderExtraService
                            {
                                Order = order,
                                ExtraServiceId = extraService.Id,
                                Quantity = e.Quantity,
                                Hours = e.Hours,
                                Cost = e.Cost,
                                Duration = 0,
                                CreatedAt = DateTime.UtcNow
                            });
                        }
                    }
                }
                // Remove only existing (persisted) order extra services that are no longer in the DTO. Do not remove newly added items (Id == 0).
                if (order.OrderExtraServices != null)
                {
                    foreach (var oes in order.OrderExtraServices.Where(x => x.Id != 0 && !existingExtraIdsInDto.Contains(x.Id)).ToList())
                        order.OrderExtraServices.Remove(oes);
                }
            }

            order.UpdatedAt = DateTime.UtcNow;

            // Track additional amount (if any) for this update
            var additionalAmount = order.Total - originalTotal;
            if (Math.Abs(additionalAmount) < 0.01m)
            {
                additionalAmount = 0m;
            }

            // If this update creates an additional payment for an already-paid order,
            // move status Active -> Pending so admins can clearly see "awaiting payment".
            // Once the customer pays, status will be switched back to Active.
            if (additionalAmount > 0.01m &&
                order.IsPaid &&
                !string.Equals(order.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(order.Status, "Done", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(order.Status, "Active", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(order.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    order.Status = "Pending";
                }
            }

            // Create an update-history record (used to show "additional payments" + allow customer to pay later)
            var updateHistory = new OrderUpdateHistory
            {
                OrderId = order.Id,
                UpdatedByUserId = updatedByUserId,
                UpdatedAt = DateTime.UtcNow,
                OriginalSubTotal = originalSubTotal,
                OriginalTax = originalTax,
                OriginalTips = originalTips,
                OriginalCompanyDevelopmentTips = originalCompanyDevelopmentTips,
                OriginalTotal = originalTotal,
                NewSubTotal = order.SubTotal,
                NewTax = order.Tax,
                NewTips = order.Tips,
                NewCompanyDevelopmentTips = order.CompanyDevelopmentTips,
                NewTotal = order.Total,
                AdditionalAmount = additionalAmount,
                IsPaid = additionalAmount <= 0.01m
            };

            _context.OrderUpdateHistories.Add(updateHistory);

            // NOTE: The "additional payment required" email + SMS used to fire here automatically.
            // That is now admin-triggered via POST /api/admin/orders/{orderId}/send-updated-payment
            // so a back-office edit doesn't immediately blast the customer. The new endpoint stamps
            // OrderUpdateHistory.UpdatedPaymentNotificationSentAt, which the admin UI uses to flip
            // between "Send Updated Payment" (first send) and "Send Payment Reminder" (follow-ups).

            await _context.SaveChangesAsync();
        }
    }
}