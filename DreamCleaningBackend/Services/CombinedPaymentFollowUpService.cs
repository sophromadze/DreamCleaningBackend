using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public interface ICombinedPaymentFollowUpService
    {
        /// <summary>
        /// Runs the post-payment follow-up for every order a settled "Pay all upcoming" charge
        /// covered and that has not had it yet (one batch, or all of them). Safe to call any number
        /// of times, concurrently: each order's follow-up happens exactly once. Never throws for
        /// one order's failure. Returns how many orders were processed.
        /// </summary>
        Task<int> ProcessAsync(int? batchId = null, CancellationToken ct = default);
    }

    /// <summary>
    /// WHAT A SINGLE PAYMENT DOES AFTER IT SETTLES AN ORDER, done for each order a combined payment
    /// settled (2026-09). The rule it applies is the one part-payments already state: an order
    /// paid another way must end in exactly the state a single payment leaves it in.
    ///
    /// <list type="bullet">
    /// <item><b>Loyalty</b> — <see cref="ILoyaltyDiscountService.ApplyToOrderAsync"/>, unchanged. It
    /// already refuses to consume anything on a GENERATED occurrence (a standing series agreement
    /// is not a one-time award); only an unpaid series TEMPLATE can carry a consumable discount.</item>
    /// <item><b>Subscription</b> — the plan the occurrence carries is activated or renewed from its
    /// service date, exactly as confirm-payment does. Orders are processed NEAREST FIRST, so paying
    /// three visits at once leaves the plan where paying them one by one would.</item>
    /// <item><b>First-order flag</b> cleared.</item>
    /// <item><b>Booking confirmation</b> email + SMS to the customer and the company notification —
    /// one per cleaning, as an individual payment sends. Generation mails nobody, so payment is
    /// the only confirmation a recurring cleaning ever gets.</item>
    /// </list>
    ///
    /// <b>Exactly once.</b> The bookkeeping and its <c>BookkeepingAppliedAt</c> stamp commit in ONE
    /// transaction, so it cannot run twice. The confirmation is AT MOST once: its stamp is claimed
    /// before anything is sent, so a retry never mails the customer a second time — the same
    /// no-retry behaviour an individual payment's confirmation has. A lease on the item keeps two
    /// workers off the same order, and a follow-up that keeps failing stops after a few tries.
    /// None of this can touch the payment: the order is already paid and committed.
    /// </summary>
    public class CombinedPaymentFollowUpService : ICombinedPaymentFollowUpService
    {
        public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);
        public const int MaxAttempts = 5;

        /// <summary>Only recent settlements are followed up — a second guard, beside the migration
        /// stamping every pre-existing item done, against mailing about an old payment.</summary>
        public static readonly TimeSpan FollowUpWindow = TimeSpan.FromDays(14);

        private readonly ApplicationDbContext _context;
        private readonly ILoyaltyDiscountService _loyalty;
        private readonly ISubscriptionService _subscriptions;
        private readonly IEmailService _emailService;
        private readonly ISmsService _smsService;
        private readonly ILogger<CombinedPaymentFollowUpService> _logger;

        public CombinedPaymentFollowUpService(
            ApplicationDbContext context,
            ILoyaltyDiscountService loyalty,
            ISubscriptionService subscriptions,
            IEmailService emailService,
            ISmsService smsService,
            ILogger<CombinedPaymentFollowUpService> logger)
        {
            _context = context;
            _loyalty = loyalty;
            _subscriptions = subscriptions;
            _emailService = emailService;
            _smsService = smsService;
            _logger = logger;
        }

        public async Task<int> ProcessAsync(int? batchId = null, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var leaseCutoff = now - ClaimLease;
            var paidSince = now - FollowUpWindow;

            var itemIds = await _context.OrderPaymentBatchItems.AsNoTracking()
                .Where(i => i.AppliedToOrder
                            && (i.BookkeepingAppliedAt == null || i.ConfirmationSentAt == null)
                            && i.FollowUpAttempts < MaxAttempts
                            && i.Batch!.Status == OrderPaymentBatchStatus.Paid
                            && i.Batch.PaidAt >= paidSince
                            && (batchId == null || i.OrderPaymentBatchId == batchId)
                            && (i.FollowUpClaimedAt == null || i.FollowUpClaimedAt < leaseCutoff))
                .OrderBy(i => i.Order!.ServiceDate).ThenBy(i => i.Order!.ServiceTime).ThenBy(i => i.Id)
                .Select(i => i.Id)
                .Take(100)
                .ToListAsync(ct);

            var processed = 0;
            foreach (var itemId in itemIds)
            {
                ct.ThrowIfCancellationRequested();

                var claimedAt = DateTime.UtcNow;
                var claimed = await _context.OrderPaymentBatchItems
                    .Where(i => i.Id == itemId
                                && (i.BookkeepingAppliedAt == null || i.ConfirmationSentAt == null)
                                && (i.FollowUpClaimedAt == null || i.FollowUpClaimedAt < claimedAt - ClaimLease))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.FollowUpClaimedAt, claimedAt)
                        .SetProperty(i => i.FollowUpAttempts, i => i.FollowUpAttempts + 1), ct);
                if (claimed == 0) continue;   // another worker has it

                try
                {
                    await ProcessItemAsync(itemId, ct);
                    processed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Post-payment follow-up failed for combined-payment item {ItemId}; it will be retried.", itemId);
                }
                finally
                {
                    _context.ChangeTracker.Clear();
                }
            }
            return processed;
        }

        private async Task ProcessItemAsync(int itemId, CancellationToken ct)
        {
            var item = await _context.OrderPaymentBatchItems.AsNoTracking().FirstAsync(i => i.Id == itemId, ct);
            var order = await _context.Orders.AsNoTracking()
                .Include(o => o.ServiceType)
                .Include(o => o.User)
                .Include(o => o.OrderExtraServices).ThenInclude(e => e.ExtraService)
                .FirstOrDefaultAsync(o => o.Id == item.OrderId, ct);

            // Nothing to follow up: the order is gone, or the share only PART-covered it (its price
            // rose after authorising). A part-covered order is confirmed by whichever payment
            // finally settles it, exactly like any part-paid order.
            if (order == null || !order.IsPaid)
            {
                await StampAsync(itemId, bookkeeping: true, confirmation: true, ct);
                return;
            }

            if (item.BookkeepingAppliedAt == null)
            {
                try
                {
                    await ApplyBookkeepingAsync(itemId, order, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Retried on the next claim (bounded by MaxAttempts). It never blocks the
                    // confirmation below, as a loyalty failure never blocks it on confirm-payment.
                    _logger.LogError(ex, "Loyalty/subscription bookkeeping failed for order {OrderId} (combined payment).", order.Id);
                    _context.ChangeTracker.Clear();
                }
            }

            if (item.ConfirmationSentAt == null)
                await SendConfirmationOnceAsync(itemId, order, ct);
        }

        /// <summary>All three in ONE transaction with the stamp: either everything happened and the
        /// stamp says so, or nothing did and it will be tried again. Never half.</summary>
        private async Task ApplyBookkeepingAsync(int itemId, Order order, CancellationToken ct)
        {
            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(ct)
                : null;

            var stamped = await _context.OrderPaymentBatchItems
                .Where(i => i.Id == itemId && i.BookkeepingAppliedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.BookkeepingAppliedAt, DateTime.UtcNow), ct);
            if (stamped == 0) return;   // already applied — the transaction rolls back on dispose

            if (order.LoyaltyDiscountAmount > 0m && order.LoyaltyDiscountPercentage > 0m)
                await _loyalty.ApplyToOrderAsync(order.Id);

            // Same rule confirm-payment applies to a paid order (BookingController).
            var subscription = order.SubscriptionId.HasValue
                ? await _context.Subscriptions.FindAsync(new object[] { order.SubscriptionId.Value }, ct)
                : null;
            if (subscription != null && subscription.SubscriptionDays > 0)
            {
                var hasActive = await _subscriptions.CheckAndUpdateSubscriptionStatus(order.UserId);
                if (!hasActive)
                {
                    var plan = await _context.Subscriptions
                        .FirstOrDefaultAsync(s => s.SubscriptionDays == subscription.SubscriptionDays, ct);
                    if (plan != null) await _subscriptions.ActivateSubscription(order.UserId, plan.Id, order.ServiceDate);
                }
                else
                {
                    var owner = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == order.UserId, ct);
                    if (owner?.SubscriptionId != null)
                        await _subscriptions.RenewSubscription(order.UserId, order.ServiceDate);
                }
            }

            await _context.Users
                .Where(u => u.Id == order.UserId && u.FirstTimeOrder)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.FirstTimeOrder, false)
                    .SetProperty(u => u.UpdatedAt, DateTime.UtcNow), ct);

            if (transaction != null) await transaction.CommitAsync(ct);
        }

        private async Task SendConfirmationOnceAsync(int itemId, Order order, CancellationToken ct)
        {
            // Claimed BEFORE sending: whoever wins this sends; nobody ever sends twice.
            var won = await _context.OrderPaymentBatchItems
                .Where(i => i.Id == itemId && i.ConfirmationSentAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ConfirmationSentAt, DateTime.UtcNow), ct);
            if (won == 0) return;

            if (!ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(order))
            {
                _logger.LogInformation("Order {OrderId}: {Reason}", order.Id,
                    ResidentialBookingCommunicationPolicy.SuppressionReason(order));
                return;
            }

            // A confirmation for a cleaning that has already happened (a charge recorded late)
            // would confuse rather than confirm.
            if (order.ServiceDate.Date < NyTimeHelper.NowNy.Date)
            {
                _logger.LogInformation("Order {OrderId}: service date has passed; booking confirmation not sent.", order.Id);
                return;
            }

            var extraNames = (order.OrderExtraServices ?? new List<OrderExtraService>())
                .Select(x => x.ExtraService?.Name ?? "")
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            var isCustom = order.ServiceType?.IsCustom ?? false;
            var supplyChecklist = CustomerSupplyChecklist.Resolve(extraNames, isCustom);
            var customerName = Capitalize(order.ContactFirstName);
            var serviceTime = order.ServiceTime.ToString();
            var address = $"{order.ServiceAddress}{(!string.IsNullOrEmpty(order.AptSuite) ? $", {order.AptSuite}" : "")}";
            var contactEmail = order.ContactEmail;
            var contactPhone = !string.IsNullOrWhiteSpace(order.ContactPhone) ? order.ContactPhone : order.User?.Phone;

            var isAppleRelay = !string.IsNullOrEmpty(contactEmail)
                && contactEmail.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase);
            if (!isAppleRelay && !string.IsNullOrWhiteSpace(contactEmail))
            {
                try
                {
                    await _emailService.SendCustomerBookingConfirmationAsync(
                        contactEmail, customerName, order.ServiceDate, serviceTime,
                        order.GetDisplayServiceTypeName(), address, order.Id, supplyChecklist,
                        order.FloorTypes, order.FloorTypeOther,
                        propertyType: order.PropertyType, levelsQuantity: order.LevelsQuantity);
                }
                catch (Exception ex) { _logger.LogError(ex, "Confirmation email failed for order {OrderId} (combined payment).", order.Id); }
            }

            if (!string.IsNullOrWhiteSpace(contactPhone))
            {
                try
                {
                    await _smsService.SendBookingConfirmationSmsAsync(contactPhone, customerName, order.ServiceDate, serviceTime, supplyChecklist);
                }
                catch (Exception ex) { _logger.LogError(ex, "Confirmation SMS failed for order {OrderId} (combined payment).", order.Id); }
            }

            try
            {
                await _emailService.SendCompanyBookingNotificationAsync(
                    order.ContactFirstName, order.ContactLastName, order.ContactEmail, order.ContactPhone,
                    order.ServiceDate, serviceTime, order.GetDisplayServiceTypeName(), order.ServiceAddress,
                    order.AptSuite, order.City, order.State, order.ZipCode, order.Id, isCustom,
                    order.SpecialInstructions);
            }
            catch (Exception ex) { _logger.LogError(ex, "Company notification failed for order {OrderId} (combined payment).", order.Id); }
        }

        private async Task StampAsync(int itemId, bool bookkeeping, bool confirmation, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            await _context.OrderPaymentBatchItems.Where(i => i.Id == itemId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.BookkeepingAppliedAt, i => bookkeeping && i.BookkeepingAppliedAt == null ? now : i.BookkeepingAppliedAt)
                    .SetProperty(i => i.ConfirmationSentAt, i => confirmation && i.ConfirmationSentAt == null ? now : i.ConfirmationSentAt), ct);
        }

        private static string Capitalize(string? name) =>
            string.IsNullOrWhiteSpace(name) ? string.Empty : char.ToUpper(name.Trim()[0]) + name.Trim()[1..];
    }
}
