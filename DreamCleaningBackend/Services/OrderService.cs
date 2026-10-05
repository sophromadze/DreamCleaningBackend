using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Recurring;
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

        /// <param name="includeHidden">When false (the default list view), soft-hidden orders are
        /// left out. Hiding is a VIEW filter only — it changes no order data and no status.</param>
        public async Task<List<OrderListDto>> GetAllOrdersForAdmin(bool includeHidden = false)
        {
            // Get ALL orders from the database without filtering by userId
            var query = _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.User)
                .Include(o => o.AssignedAdmin)
                // Lazy loading is off; without this the recurrence label is always null.
                .Include(o => o.RecurringSeries)
                .AsQueryable();

            if (!includeHidden)
                query = query.Where(o => !o.IsHidden);

            var orders = await query
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

            var contractLabels = await ContractBilledOrders.LoadLabelsAsync(_context, orders);

            return orders.Select(o => new OrderListDto
            {
                Id = o.Id,
                BilledByContractLabel = contractLabels.GetValueOrDefault(o.Id),
                UserId = o.UserId,
                ContactEmail = o.ContactEmail,
                ContactFirstName = o.ContactFirstName,
                ContactLastName = o.ContactLastName,
                ContactPhone = o.ContactPhone,
                ServiceTypeName = o.GetDisplayServiceTypeName(),
                ServiceTypeKey = o.GetRecognisableServiceTypeKey(),
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
                AmountPaid = o.AmountPaid,
                AmountDue = OrderBalance.AmountDue(o),
                IsPartiallyPaid = OrderBalance.IsPartiallyPaid(o),
                PaidAt = o.PaidAt,
                CancellationReason = o.CancellationReason,
                IsLateCancellation = o.IsLateCancellation,
                LoyaltyDiscountAmount = o.LoyaltyDiscountAmount,
                LoyaltyDiscountPercentage = o.LoyaltyDiscountPercentage,
                PaymentMethod = o.PaymentMethod.ToString(),
                PaymentReference = o.PaymentReference,
                PaymentNotes = o.PaymentNotes,
                InvoicePaidAt = o.InvoicePaidAt,
                ContractClientId = o.ContractClientId,
                RecurringSeriesId = o.RecurringSeriesId,
                IsGeneratedByRecurringSeries = o.IsGeneratedByRecurringSeries,
                RecurrenceLabel = o.RecurringSeries == null
                    ? null
                    : RecurrenceCalculator.Describe(o.RecurringSeries.IntervalUnit, o.RecurringSeries.IntervalValue),
                AssignedAdminId = o.AssignedAdminId,
                AssignedAdminFirstName = o.AssignedAdmin != null ? o.AssignedAdmin.FirstName : null,
                AssignedAdminLastName = o.AssignedAdmin != null ? o.AssignedAdmin.LastName : null,
                AssignedAdminDisplayName = o.AssignedAdmin != null
                    ? AdminBonusService.FormatDisplayName(o.AssignedAdmin.FirstName, o.AssignedAdmin.LastName)
                    : null,
                BookedByAdmin = o.IsBookedByAdmin(),
                Flag = (o.User?.Flag ?? CustomerFlagLevel.None).ToString(),
                FlagReason = o.User?.FlagReason,
                TotalRefundedAmount = o.TotalRefundedAmount,
                IsHidden = o.IsHidden
            }).ToList();
        }

        public async Task<List<OrderListDto>> GetUserOrders(int userId)
        {
            var orders = await _orderRepository.GetUserOrdersAsync(userId);

            await AutoCancelExpiredUnpaidOrdersIfNeeded(orders);

            // Pending additional payment = current total − original total (tips INCLUDED — see OrderAdditionalCharge), less what was already collected.
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
                    FirstOriginalTotal = g.OrderBy(x => x.UpdatedAt).Select(x => x.OriginalTotal).FirstOrDefault()
                })
                .ToListAsync();
            var firstOriginalByOrderId = firstOriginalList.ToDictionary(x => x.OrderId, x => x.FirstOriginalTotal);
            // Amount already paid by customer (sum of paid update-history rows) so we only show
            // unpaid portion. POSITIVE rows only — a negative row is a price decrease, and
            // subtracting one adds to the bill (OrderAdditionalCharge). EF cannot translate the
            // helper inside the bigger Where, so the shared predicate is chained on its own.
            var alreadyPaidList = await _context.OrderUpdateHistories
                .Where(h => unpaidOrderIds.Contains(h.OrderId))
                .Where(OrderAdditionalCharge.WasCollected)
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

            var contractLabels = await ContractBilledOrders.LoadLabelsAsync(_context, orders);

            return orders.Select(o => new OrderListDto
            {
                Id = o.Id,
                BilledByContractLabel = contractLabels.GetValueOrDefault(o.Id),
                UserId = o.UserId,  
                ContactEmail = o.ContactEmail,  
                ContactFirstName = o.ContactFirstName,  
                ContactLastName = o.ContactLastName,  
                ServiceTypeName = o.GetDisplayServiceTypeName(),
                ServiceTypeKey = o.GetRecognisableServiceTypeKey(),
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
                AmountPaid = o.AmountPaid,
                AmountDue = OrderBalance.AmountDue(o),
                IsPartiallyPaid = OrderBalance.IsPartiallyPaid(o),
                PaidAt = o.PaidAt,
                PendingUpdateAmount = o.IsPaid && unpaidOrderIds.Contains(o.Id)
                    ? OrderAdditionalCharge.Outstanding(
                        o,
                        firstOriginalByOrderId.TryGetValue(o.Id, out var firstOrig) ? firstOrig : (decimal?)null,
                        alreadyPaidByOrderId.TryGetValue(o.Id, out var paid) ? paid : 0m)
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
                InvoicePaidAt = o.InvoicePaidAt,
                ContractClientId = o.ContractClientId,
                RecurringSeriesId = o.RecurringSeriesId,
                IsGeneratedByRecurringSeries = o.IsGeneratedByRecurringSeries,
                RecurrenceLabel = o.RecurringSeries == null
                    ? null
                    : RecurrenceCalculator.Describe(o.RecurringSeries.IntervalUnit, o.RecurringSeries.IntervalValue),
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
            dto.BilledByContractLabel = await ContractBilledOrders.LoadLabelAsync(_context, order);

            // Pending additional payment = current total − original total (tips INCLUDED — see OrderAdditionalCharge), less what was already collected.
            if (order.IsPaid)
            {
                var hasUnpaid = await _context.OrderUpdateHistories
                    .AnyAsync(h => h.OrderId == order.Id && !h.IsPaid && h.AdditionalAmount > 0.01m);
                if (hasUnpaid)
                {
                    // This is the figure the customer's payment page charges, so it goes through
                    // the shared resolver — no local copy of the subtraction (OrderAdditionalCharge).
                    dto.PendingUpdateAmount = await OrderAdditionalCharge.OutstandingAsync(_context, order);
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
                // Part-paid orders are NOT abandoned checkouts. Money has actually arrived, and
                // quietly cancelling the order it arrived for would leave it owed back to a
                // customer nobody has been told to refund. An admin decides what happens to these.
                if (OrderBalance.IsPartiallyPaid(order)) continue;
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
            // Part-paid orders are NOT abandoned checkouts — see the batch sweep above.
            if (OrderBalance.IsPartiallyPaid(order)) return;
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

            if (order.RecurringSeriesId.HasValue)
                throw new Exception("Please contact Dream Cleaning to cancel or reschedule a recurring cleaning.");

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
            // CompanyDevelopmentTips is retired and intentionally NOT assigned: no client sends
            // it, and a legacy order's stored amount is part of what the customer already paid,
            // so it must survive every edit untouched.
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

            // Display-only property columns, from the same input the lines were priced from.
            PropertyDetailsHelper.Apply(order, updateOrderDto.PropertyType, quoteInput, updateOrderDto.LevelsQuantity);

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
            // Routed through the payroll calculator, and the assignments are loaded rather than
            // skipped on purpose: a customer editing their order must not silently revert a
            // per-cleaner rate or hours a SuperAdmin set on the Outgoing Payments page.
            var assignmentsForSalary = await _context.OrderCleaners
                .Where(oc => oc.OrderId == order.Id)
                .ToListAsync();
            CleanerPayrollCalculator.ApplyOrderTotalSalary(
                order, quote.HasCleanerService, assignmentsForSalary);

            // Recalculate totals. The discounts are re-derived server-side exactly as booking
            // derives them, from the rule recorded on the order (ResolveEditedDiscounts - the same
            // function the order-edit page previews with), so the client's discount dollar figures
            // in the DTO are never trusted. Must read the order BEFORE SubTotal is overwritten.
            (order.DiscountAmount, order.SubscriptionDiscountAmount, order.LoyaltyDiscountAmount) =
                OrderPricingCalculator.ResolveEditedDiscounts(order, quote.SubTotal);
            order.SubTotal = quote.SubTotal;

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
            //
            // AdditionalAmount is floored at zero (and ONLY that field — every Original*/New*
            // value below is recorded as it happened, so the audit record stays complete). A
            // negative stored here is read as money the customer handed over by every "what is
            // still owed" sum, which doubled order #359's bill — see OrderAdditionalCharge.
            // This path already refuses a decrease outright above; the clamp states the rule
            // where the row is written, so it holds if that guard is ever relaxed.
            var collectableAmount = OrderAdditionalCharge.Collectable(additionalAmount);

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
                AdditionalAmount = collectableAmount,
                IsPaid = collectableAmount <= OrderAdditionalCharge.MinimumCollectableAmount // Mark as paid if no additional amount required
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

            if (order.RecurringSeriesId.HasValue)
                throw new Exception("Please contact Dream Cleaning to cancel or reschedule a recurring cleaning.");

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

            // This endpoint is owner-only (checked above), so the owner's Stripe Customer can
            // always ride along — lets the order-edit payment step offer their saved card.
            var ownerStripeCustomerId = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == order.UserId)
                .Select(u => u.StripeCustomerId)
                .FirstOrDefaultAsync();

            var paymentIntent = await _stripeService.CreatePaymentIntentAsync(additionalAmount, metadata,
                customerId: ownerStripeCustomerId);

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

            if (order.RecurringSeriesId.HasValue)
                throw new Exception("Please contact Dream Cleaning to cancel or reschedule a recurring cleaning.");
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

            if (order.RecurringSeriesId.HasValue)
                throw new Exception("Please contact Dream Cleaning to cancel or reschedule a recurring cleaning.");

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

            // Server-derived discounts, matching exactly what UpdateOrder will persist (the same
            // shared rule). The DTO's discount fields are intentionally ignored (client dollar
            // figures are never trusted).
            var (discountAmount, subscriptionDiscountAmount, loyaltyDiscountAmount) =
                OrderPricingCalculator.ResolveEditedDiscounts(order, quote.SubTotal);

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
                // Retired field: the STORED amount, never a client-supplied one. Dropping it here
                // would make an untouched legacy order look cheaper than what was charged and
                // produce a bogus negative "additional amount".
                CompanyDevelopmentTips = order.CompanyDevelopmentTips
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
                // Lazy loading is off; without this the recurrence label is always null.
                .Include(o => o.RecurringSeries)
                .Where(o => o.UserId == userId)
                .OrderByDescending(o => o.OrderDate)
                .ToListAsync();

            var contractLabels = await ContractBilledOrders.LoadLabelsAsync(_context, orders);

            return orders.Select(o => new OrderListDto
            {
                Id = o.Id,
                BilledByContractLabel = contractLabels.GetValueOrDefault(o.Id),
                UserId = o.UserId,
                ContactEmail = o.ContactEmail,
                ContactFirstName = o.ContactFirstName,
                ContactLastName = o.ContactLastName,
                ServiceTypeName = o.GetDisplayServiceTypeName(),
                ServiceTypeKey = o.GetRecognisableServiceTypeKey(),
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
                AmountPaid = o.AmountPaid,
                AmountDue = OrderBalance.AmountDue(o),
                IsPartiallyPaid = OrderBalance.IsPartiallyPaid(o),
                PaidAt = o.PaidAt,
                CancellationReason = o.CancellationReason,
                IsLateCancellation = o.IsLateCancellation,
                LoyaltyDiscountAmount = o.LoyaltyDiscountAmount,
                LoyaltyDiscountPercentage = o.LoyaltyDiscountPercentage,
                PaymentMethod = o.PaymentMethod.ToString(),
                PaymentReference = o.PaymentReference,
                PaymentNotes = o.PaymentNotes,
                InvoicePaidAt = o.InvoicePaidAt,
                ContractClientId = o.ContractClientId,
                RecurringSeriesId = o.RecurringSeriesId,
                IsGeneratedByRecurringSeries = o.IsGeneratedByRecurringSeries,
                RecurrenceLabel = o.RecurringSeries == null
                    ? null
                    : RecurrenceCalculator.Describe(o.RecurringSeries.IntervalUnit, o.RecurringSeries.IntervalValue),
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
            var dto = OrderDtoMapper.ToOrderDto(order);
            dto.BilledByContractLabel = await ContractBilledOrders.LoadLabelAsync(_context, order);
            return dto;
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

        /// <summary>Full order update without 48h or "can't reduce" checks. All changes must be audit-logged by the
        /// caller, which is also where the "may this admin apply an edit directly?" decision lives
        /// (Helpers/OrderEditApprovalPolicy) - this method performs no authorization of its own.</summary>
        /// <summary>
        /// Adds the priced LEVELS line an admin edit brought into being (2026-10).
        ///
        /// An order booked as an apartment has no levels row at all - booking only adds one when a
        /// house's level chip is clicked. Switching it to a house in the admin editor used to fall
        /// back to an unpriced "Levels (informational)" box, because this endpoint could only
        /// update rows that already existed, so the level count never reached the price. The
        /// editor now adds the row and prices it through the shared calculator exactly as booking
        /// does; this persists it. The subtotal itself still arrives as dto.SubTotal, priced
        /// client-side by that same calculator, like every other admin line edit.
        ///
        /// Deliberately narrow: only the levels service of THIS order's service type, only for a
        /// house, only when the order has no levels row yet. Anything else is ignored, so a
        /// crafted payload cannot attach arbitrary services to an order.
        /// </summary>
        private async Task AddAdminLevelsLineAsync(Order order, SuperAdminOrderServiceUpdateDto row, string? requestedPropertyType)
        {
            if (row.ServiceId is not int serviceId || serviceId <= 0) return;

            // Null property type on this DTO means "no change", so fall back to what the order holds.
            var effectivePropertyType = requestedPropertyType ?? order.PropertyType;
            if (!PropertyDetailsHelper.IsHouse(effectivePropertyType)) return;

            order.OrderServices ??= new List<Models.OrderService>();
            if (order.OrderServices.Any(os => os.ServiceId == serviceId
                    || (os.Service != null && os.Service.ServiceKey == PropertyDetailsHelper.LevelsServiceKey)))
                return;

            var service = await _context.Services.FirstOrDefaultAsync(sv => sv.Id == serviceId);
            if (service == null
                || service.ServiceTypeId != order.ServiceTypeId
                || service.ServiceKey != PropertyDetailsHelper.LevelsServiceKey
                || !service.IsActive)
                return;

            // Same range OrderPricingInputBuilder clamps a booked level count to.
            var quantity = row.Quantity;
            if (service.MinValue.HasValue) quantity = Math.Max(quantity, service.MinValue.Value);
            if (service.MaxValue.HasValue) quantity = Math.Min(quantity, service.MaxValue.Value);

            order.OrderServices.Add(new Models.OrderService
            {
                Order = order,
                ServiceId = service.Id,
                // Navigation set so PropertyDetailsHelper.ApplyFromOrderLines finds the row below
                // and writes Order.LevelsQuantity from it.
                Service = service,
                Quantity = quantity,
                Cost = row.Cost,
                Duration = row.Duration ?? 0m,
                PriceMultiplier = order.OrderServices.FirstOrDefault()?.PriceMultiplier ?? 1.0m,
                CreatedAt = DateTime.UtcNow
            });
        }

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

            // What the order looked like BEFORE this edit, for the server re-price below: which
            // lines were selected (so an edit that touches no line never re-prices a booked
            // order at today's catalogue), and the discounts with the rules they were booked with.
            var selectionsBefore = LineSelectionSignature(order);
            var propertyTypeBefore = order.PropertyType;
            var totalDurationBefore = order.TotalDuration;
            var discountsBefore = new OrderPricingCalculator.EditDiscountInput
            {
                OriginalSubTotal = order.SubTotal,
                DiscountAmount = order.DiscountAmount,
                DiscountPercent = order.DiscountPercent,
                DiscountFixedAmount = order.DiscountFixedAmount,
                SubscriptionDiscountAmount = order.SubscriptionDiscountAmount,
                SubscriptionDiscountPercent = order.SubscriptionDiscountPercent,
                LoyaltyDiscountPercentage = order.LoyaltyDiscountPercentage,
                LoyaltyDiscountAmount = order.LoyaltyDiscountAmount
            };

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
            // CompanyDevelopmentTips is retired: not on the DTO, never assigned here. A legacy
            // order keeps its stored amount through every admin edit.
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
            // SubTotal and the three discounts are resolved AFTER the line edits below, by
            // ResolveAdminEditPricingAsync - the server prices the lines itself (2026-10).
            if (dto.CleanerHourlyRate.HasValue) order.CleanerHourlyRate = dto.CleanerHourlyRate.Value;

            // Who is actually on this job? Needed twice below, and loaded here because the very
            // next line depends on it.
            var assignmentsForSalary = await _context.OrderCleaners
                .Where(oc => oc.OrderId == order.Id)
                .ToListAsync();
            var hasAssignedCleaners = assignmentsForSalary.Count > 0;

            // A client-submitted CleanerTotalSalary is REFUSED outright once anybody is assigned.
            //
            // The recompute below already overwrites it, so this gate changes no outcome - it
            // makes the refusal intentional rather than incidental (owner's call, 2026-08-31),
            // per the project rule that client-submitted pricing fields are never trusted. The
            // admin order editor derives that number from MaidsCount x the order's single rate,
            // which cannot express two cleaners paid different rates or hours, so accepting it
            // would mean a stray client - or a future reordering of this method - could revert
            // figures a SuperAdmin set on the Outgoing Payments page.
            //
            // With nobody assigned there is nothing to sum from, so an explicit figure still wins.
            if (dto.CleanerTotalSalary.HasValue && !hasAssignedCleaners)
                order.CleanerTotalSalary = dto.CleanerTotalSalary.Value;
            // Custom ("Pre-Arranged") orders only: relabel the display name. null = no change,
            // empty string = clear back to "Arranged". Mirrors OrderController.SetCustomServiceName.
            if (dto.CustomServiceDisplayName != null && order.ServiceType?.IsCustom == true)
            {
                var trimmedName = dto.CustomServiceDisplayName.Trim();
                order.CustomServiceDisplayName = string.IsNullOrWhiteSpace(trimmedName) ? null : trimmedName;
            }

            // Cleaner total salary.
            //
            // Once ANYBODY is assigned, the per-cleaner lines are the truth and the total is
            // summed from them — see CleanerPayrollCalculator. The client's figure was already
            // refused above for exactly that reason.
            //
            // With nobody assigned there is nothing to sum, so the historical behaviour stands:
            // an explicit figure wins, otherwise recompute from rate × duration × maids.
            if (hasAssignedCleaners)
            {
                bool hasCleanersService = order.OrderServices.Any(os =>
                    os.Service?.ServiceRelationType == "cleaner");
                CleanerPayrollCalculator.ApplyOrderTotalSalary(
                    order, hasCleanersService, assignmentsForSalary);
            }
            else if (dto.CleanerHourlyRate.HasValue || dto.TotalDuration.HasValue || dto.MaidsCount.HasValue)
            {
                if (!dto.CleanerTotalSalary.HasValue)
                {
                    bool hasCleanersService = order.OrderServices.Any(os =>
                        os.Service?.ServiceRelationType == "cleaner");
                    CleanerPayrollCalculator.ApplyOrderTotalSalary(order, hasCleanersService, null);
                }
            }

            if (dto.Services != null)
            {
                foreach (var s in dto.Services)
                {
                    if (s.OrderServiceId == 0)
                    {
                        await AddAdminLevelsLineAsync(order, s, dto.PropertyType);
                        continue;
                    }
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

            // Display-only property columns. Deliberately AFTER the dto.Services loop above:
            // ApplyFromOrderLines reads the levels row back, so it has to run once that row
            // carries the admin's new quantity. Running it earlier would store the pre-edit
            // count and hand the crew a stale number of levels.
            PropertyDetailsHelper.ApplyFromOrderLines(order, dto.PropertyType, dto.LevelsQuantity);

            // Price the edited lines exactly like booking (server-authoritative), then the
            // discounts from their booking rules. See ResolveAdminEditPricingAsync.
            await ResolveAdminEditPricingAsync(order, dto, selectionsBefore, propertyTypeBefore,
                totalDurationBefore, discountsBefore);

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
                CompanyDevelopmentTips = order.CompanyDevelopmentTips,
                // Present only when the admin typed a TOTAL instead of a subtotal. The base makes
                // this a verification rather than a trust: if it does not match the subtotal this
                // order's discounts actually leave behind, CalculateTotals ignores the override
                // and prices the order the ordinary way.
                TaxOverride = dto.TaxOverride,
                TaxOverrideBase = dto.TaxOverrideBase
                // gift card + points/rewards applied below to mirror the user edit path
            });
            order.Tax = totals.Tax;
            var pointsAndRewardCredits = order.PointsRedeemedDiscount + order.RewardBalanceUsed;
            await ApplyEditGiftCardAsync(order, totals.TotalBeforeGiftCard, pointsAndRewardCredits);

            // Did this save actually change anything?
            //
            // Every admin save used to bump UpdatedAt and insert an OrderUpdateHistory row even
            // when the submitted DTO changed nothing - which is how a no-op edit produced a $0
            // history row plus an audit row whose only changed field was UpdatedAt, rendering as a
            // literally blank row in the Audits tab (found 2026-08-31 on order #315: the admin
            // typed a new Cleaners Total Salary, which this endpoint discards by design once
            // cleaners are assigned, so nothing else moved). DetectChanges is called explicitly
            // because this runs before SaveChangesAsync would do it for us.
            _context.ChangeTracker.DetectChanges();

            var orderScalarsChanged = _context.Entry(order).Properties
                .Any(p => p.IsModified && p.Metadata.Name != nameof(Order.UpdatedAt));

            // Line edits touch no scalar on the order itself, so they have to be asked for
            // separately: a service swapped for one of identical price moves no money and is
            // still absolutely a change to the job. Models.OrderService is qualified because this
            // class is also called OrderService.
            var orderLinesChanged = _context.ChangeTracker.Entries().Any(e =>
                (e.State == EntityState.Added || e.State == EntityState.Modified || e.State == EntityState.Deleted) &&
                (e.Entity is Models.OrderService || e.Entity is OrderExtraService));

            var anythingChanged = orderScalarsChanged || orderLinesChanged;

            if (anythingChanged)
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

            // Create an update-history record (used to show "additional payments" + allow customer to pay later).
            //
            // Only a TRUE no-op skips it (owner's call, 2026-08-31). Money moving is deliberately
            // NOT the test: a service swapped for one of equal price, or a change to TotalDuration
            // or MaidsCount, moves no money and still has to leave a record. Every save that
            // changes anything at all keeps writing a row exactly as before.
            if (anythingChanged)
            {
                // AdditionalAmount is floored at zero — and ONLY that field. A downward edit keeps
                // its complete record (OriginalTotal/NewTotal and every other value below are
                // written as they happened) and still shows up in the Update History panel; it
                // simply stops claiming to be a settled payment. Storing the decrease as a
                // negative made every "what is still owed" sum treat it as money the customer had
                // handed over, which is what doubled order #359's bill. This is the path that
                // could actually produce one — see OrderAdditionalCharge for the worked example.
                var collectableAmount = OrderAdditionalCharge.Collectable(additionalAmount);

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
                    AdditionalAmount = collectableAmount,
                    IsPaid = collectableAmount <= OrderAdditionalCharge.MinimumCollectableAmount
                };

                _context.OrderUpdateHistories.Add(updateHistory);
            }

            // NOTE: The "additional payment required" email + SMS used to fire here automatically.
            // That is now admin-triggered via POST /api/admin/orders/{orderId}/send-updated-payment
            // so a back-office edit doesn't immediately blast the customer. The new endpoint stamps
            // OrderUpdateHistory.UpdatedPaymentNotificationSentAt, which the admin UI uses to flip
            // between "Send Updated Payment" (first send) and "Send Payment Reminder" (follow-ups).

            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// Server-side pricing for an admin order edit (owner's rules, 2026-10):
        ///
        /// SUBTOTAL - priced from the order's lines with the shared calculator, the same way
        /// booking prices them, and the editor's figure is ignored (a warning is logged when they
        /// differ by more than a cent). Three exceptions keep the editor's figure on purpose:
        ///   - the admin TYPED the price in this edit (dto.PriceTypedByAdmin - SubTotal or Total);
        ///   - a custom ("Pre-Arranged") order, whose agreed amount is the price;
        ///   - lines the calculator cannot price (cleaner+hours with fractional hours).
        /// An edit that changed no line (and no property type / cleaner hours) is never re-priced:
        /// the order keeps the price it was booked at, rather than moving to today's catalogue
        /// because somebody fixed a phone number.
        ///
        /// DISCOUNTS - the booking rules (OrderPricingCalculator.ResolveEditedDiscounts). A figure
        /// that differs from what those rules give on the EDITOR'S OWN subtotal is a deliberate
        /// manual discount - only a SuperAdmin can send one (the controller refuses anybody else) -
        /// and is kept, with that slot's rule cleared so later edits re-scale it proportionally
        /// instead of overwriting it. Loyalty is never typed (read-only in the editor).
        /// </summary>
        private async Task ResolveAdminEditPricingAsync(Order order, SuperAdminUpdateOrderDto dto,
            string selectionsBefore, string? propertyTypeBefore, decimal totalDurationBefore,
            OrderPricingCalculator.EditDiscountInput discountsBefore)
        {
            var isCustom = order.ServiceType?.IsCustom == true;
            var priceTyped = dto.PriceTypedByAdmin == true;
            var hasCleanerLine = order.OrderServices?.Any(os => os.Service?.ServiceRelationType == "cleaner") == true;
            var selectionsChanged = LineSelectionSignature(order) != selectionsBefore
                || !string.Equals(order.PropertyType, propertyTypeBefore, StringComparison.Ordinal)
                || (hasCleanerLine && order.TotalDuration != totalDurationBefore);

            if (isCustom || priceTyped)
            {
                if (dto.SubTotal.HasValue) order.SubTotal = dto.SubTotal.Value;
                if (priceTyped && !isCustom)
                    _logger.LogInformation("Order {OrderId}: the admin typed the price ({SubTotal:0.00}); kept as a manual price.",
                        order.Id, order.SubTotal);
            }
            else if (selectionsChanged)
            {
                var input = await OrderPricingInputBuilder.FromOrderLinesAsync(_context, order, order.PropertyType);
                if (input == null)
                {
                    if (dto.SubTotal.HasValue) order.SubTotal = dto.SubTotal.Value;
                    _logger.LogWarning("Order {OrderId}: the lines cannot be priced by the shared calculator (fractional cleaner hours); kept the editor's subtotal {SubTotal:0.00}.",
                        order.Id, order.SubTotal);
                }
                else
                {
                    var quote = OrderPricingCalculator.CalculateQuote(input);
                    ApplyQuoteToExistingLines(order, quote);
                    if (dto.SubTotal.HasValue && Math.Abs(dto.SubTotal.Value - quote.SubTotal) > 0.01m)
                        _logger.LogWarning("Order {OrderId}: the admin editor sent subtotal {ClientSubTotal:0.00} but the shared calculator prices the lines at {ServerSubTotal:0.00}; using the server value.",
                            order.Id, dto.SubTotal.Value, quote.SubTotal);
                    order.SubTotal = quote.SubTotal;
                }
            }
            else if (dto.SubTotal.HasValue && Math.Abs(dto.SubTotal.Value - order.SubTotal) > 0.01m)
            {
                _logger.LogWarning("Order {OrderId}: the admin editor sent subtotal {ClientSubTotal:0.00} with no line or price change; kept the stored {StoredSubTotal:0.00}.",
                    order.Id, dto.SubTotal.Value, order.SubTotal);
            }

            // What the rules give on the editor's subtotal tells a typed discount from a derived one.
            discountsBefore.NewSubTotal = dto.SubTotal ?? order.SubTotal;
            var editorExpected = OrderPricingCalculator.ResolveEditedDiscounts(discountsBefore);
            discountsBefore.NewSubTotal = order.SubTotal;
            var (promo, subscription, loyalty) = OrderPricingCalculator.ResolveEditedDiscounts(discountsBefore);

            if (dto.DiscountAmount.HasValue && Math.Abs(dto.DiscountAmount.Value - editorExpected.discount) > 0.01m)
            {
                order.DiscountAmount = dto.DiscountAmount.Value;
                order.DiscountPercent = null;
                order.DiscountFixedAmount = null;
            }
            else order.DiscountAmount = promo;

            if (dto.SubscriptionDiscountAmount.HasValue &&
                Math.Abs(dto.SubscriptionDiscountAmount.Value - editorExpected.subscription) > 0.01m)
            {
                order.SubscriptionDiscountAmount = dto.SubscriptionDiscountAmount.Value;
                order.SubscriptionDiscountPercent = null;
            }
            else order.SubscriptionDiscountAmount = subscription;

            order.LoyaltyDiscountAmount = loyalty;
        }

        /// <summary>
        /// Writes the calculator's per-line cost, minutes and quantity back onto the order's EXISTING
        /// rows (matched by service / extra id), in place - the admin path keeps row ids, unlike the
        /// customer edit, which replaces its rows (AddOrderLinesFromQuote). The quantity is written
        /// too because the shared floors (studio house -> 1 bedroom, sq.ft per bedrooms, levels range)
        /// may have raised it, exactly as booking would.
        /// </summary>
        private static void ApplyQuoteToExistingLines(Order order, OrderPricingCalculator.QuoteResult quote)
        {
            var serviceRows = (order.OrderServices ?? new List<Models.OrderService>()).ToList();
            var usedServices = new HashSet<Models.OrderService>();
            foreach (var line in quote.ServiceLines)
            {
                if (!line.ShouldAddToOrder || line.ServiceId == 0) continue;
                var row = serviceRows.FirstOrDefault(r => r.ServiceId == line.ServiceId && !usedServices.Contains(r));
                if (row == null) continue;
                usedServices.Add(row);
                row.Quantity = line.Quantity;
                row.Cost = line.Cost;
                row.Duration = line.Duration;
                row.PriceMultiplier = quote.PriceMultiplier;
            }

            var extraRows = (order.OrderExtraServices ?? new List<OrderExtraService>()).ToList();
            var usedExtras = new HashSet<OrderExtraService>();
            foreach (var line in quote.ExtraServiceLines)
            {
                var row = extraRows.FirstOrDefault(r => r.ExtraServiceId == line.ExtraServiceId && !usedExtras.Contains(r));
                if (row == null) continue;
                usedExtras.Add(row);
                row.Cost = line.Cost;
                row.Duration = line.Duration;
            }
        }

        /// <summary>Which lines are selected, in what quantity - order-independent.</summary>
        private static string LineSelectionSignature(Order order) =>
            string.Join("|", (order.OrderServices ?? new List<Models.OrderService>())
                .OrderBy(x => x.ServiceId).ThenBy(x => x.Quantity)
                .Select(x => $"s{x.ServiceId}:{x.Quantity}"))
            + "#" + string.Join("|", (order.OrderExtraServices ?? new List<OrderExtraService>())
                .OrderBy(x => x.ExtraServiceId).ThenBy(x => x.Quantity).ThenBy(x => x.Hours)
                .Select(x => $"e{x.ExtraServiceId}:{x.Quantity}:{x.Hours.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
    }
}