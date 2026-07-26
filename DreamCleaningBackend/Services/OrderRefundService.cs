using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Admin-initiated Stripe refunds. Named OrderRefundService, NOT RefundService, because
    /// Stripe.RefundService is a real SDK type used in StripeService — a same-named type of ours
    /// would make every `using Stripe;` file ambiguous.
    ///
    /// Two things drive this design:
    ///
    /// 1. An order can hold SEVERAL Stripe charges. The base booking charge lives on
    ///    Order.PaymentIntentId, and every paid order-edit adds its own on
    ///    OrderUpdateHistory.PaymentIntentId. Stripe refunds one intent at a time, so an amount
    ///    that spans charges is allocated across them oldest-first. The admin still types one number.
    ///
    /// 2. The refundable ceiling is read LIVE FROM STRIPE, never summed from our OrderRefunds
    ///    table. Refunds issued from the Stripe Dashboard (how it was done before this feature)
    ///    leave no row here; trusting our own table would let that money be refunded a second time.
    /// </summary>
    public class OrderRefundService : IOrderRefundService
    {
        private readonly ApplicationDbContext _context;
        private readonly IStripeService _stripeService;
        private readonly IEmailService _emailService;
        private readonly IAuditService _auditService;
        private readonly ILogger<OrderRefundService> _logger;

        public OrderRefundService(
            ApplicationDbContext context,
            IStripeService stripeService,
            IEmailService emailService,
            IAuditService auditService,
            ILogger<OrderRefundService> logger)
        {
            _context = context;
            _stripeService = stripeService;
            _emailService = emailService;
            _auditService = auditService;
            _logger = logger;
        }

        /// <summary>One card charge belonging to an order, with its live remaining balance.</summary>
        private class RefundableCharge
        {
            public string PaymentIntentId { get; set; } = string.Empty;
            public decimal AmountReceived { get; set; }
            public decimal AmountRefunded { get; set; }
            public decimal Remaining { get; set; }
        }

        /// <summary>
        /// Every card charge on this order, oldest first, each annotated with its live Stripe
        /// balance. Non-card orders (manual payment, gift-card-covered, unpaid) contribute nothing.
        /// </summary>
        private async Task<List<RefundableCharge>> GetChargesAsync(Order order)
        {
            // Ordered oldest-first so refunds come off the original booking charge before any
            // later top-ups — that is what a customer reading their statement expects.
            var intentIds = new List<string>();

            if (IsStripeIntent(order.PaymentIntentId))
                intentIds.Add(order.PaymentIntentId!);

            foreach (var history in order.UpdateHistory
                         .Where(h => h.IsPaid && IsStripeIntent(h.PaymentIntentId))
                         .OrderBy(h => h.PaidAt ?? h.UpdatedAt))
            {
                intentIds.Add(history.PaymentIntentId!);
            }

            var charges = new List<RefundableCharge>();

            // Distinct guards against the same intent being recorded on both the order and an
            // update row, which would otherwise double-count the refundable ceiling.
            foreach (var intentId in intentIds.Distinct(StringComparer.Ordinal))
            {
                var state = await _stripeService.GetChargeRefundStateAsync(intentId);
                if (!state.IsRefundable)
                    continue;

                charges.Add(new RefundableCharge
                {
                    PaymentIntentId = intentId,
                    AmountReceived = state.AmountReceived,
                    AmountRefunded = state.AmountRefunded,
                    Remaining = state.RemainingRefundable
                });
            }

            return charges;
        }

        private static bool IsStripeIntent(string? paymentIntentId) =>
            !string.IsNullOrWhiteSpace(paymentIntentId) && paymentIntentId.StartsWith("pi_");

        public async Task<OrderRefundSummaryDto> GetRefundSummaryAsync(int orderId)
        {
            var order = await _context.Orders
                .Include(o => o.UpdateHistory)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                throw new KeyNotFoundException($"Order {orderId} not found.");

            return await BuildSummaryAsync(order);
        }

        private async Task<OrderRefundSummaryDto> BuildSummaryAsync(Order order)
        {
            var charges = await GetChargesAsync(order);

            // Queried fresh rather than read off order.Refunds: rows added earlier in this same
            // request are tracked but have no RefundedByUser loaded, which would render the
            // admin's name as "Unknown" on the refund they just issued.
            var refunds = await _context.OrderRefunds
                .AsNoTracking()
                .Where(r => r.OrderId == order.Id)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new OrderRefundDto
                {
                    Id = r.Id,
                    Amount = r.Amount,
                    Status = r.Status,
                    Reason = r.Reason,
                    FailureReason = r.FailureReason,
                    RefundedByName = r.RefundedByUser == null
                        ? "Unknown"
                        : ((r.RefundedByUser.FirstName ?? "") + " " + (r.RefundedByUser.LastName ?? "")).Trim(),
                    CreatedAt = r.CreatedAt,
                    EmailSent = r.EmailSent
                })
                .ToListAsync();

            var summary = new OrderRefundSummaryDto
            {
                OrderId = order.Id,
                TotalCharged = charges.Sum(c => c.AmountReceived),
                TotalRefunded = charges.Sum(c => c.AmountRefunded),
                RemainingRefundable = charges.Sum(c => c.Remaining),
                Refunds = refunds
            };

            // Opportunistic reconcile: Stripe is authoritative and we just paid for that lookup, so
            // if the cached total has drifted (a refund issued straight from the Stripe Dashboard
            // never runs through our code) correct it here. Only ever moves the cache UP toward
            // Stripe's figure — never down, so a transient partial read can't erase recorded refunds.
            if (charges.Count > 0 && summary.TotalRefunded > order.TotalRefundedAmount)
            {
                _logger.LogInformation(
                    "Reconciling cached refund total for order {OrderId}: {Cached} → {Actual} (refund issued outside this panel)",
                    order.Id, order.TotalRefundedAmount, summary.TotalRefunded);

                order.TotalRefundedAmount = summary.TotalRefunded;
                await _context.SaveChangesAsync();
            }

            summary.CanRefund = summary.RemainingRefundable > 0m;

            if (!summary.CanRefund)
            {
                summary.UnavailableReason = charges.Count == 0
                    ? "This order has no card payment to refund."
                    : "This order has already been fully refunded.";
            }

            return summary;
        }

        public async Task<RefundResultDto> IssueRefundAsync(
            int orderId, decimal? amount, string? reason, int adminUserId, bool sendEmail)
        {
            var order = await _context.Orders
                .Include(o => o.UpdateHistory)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return Fail("Order not found.");

            var charges = await GetChargesAsync(order);
            var totalRemaining = charges.Sum(c => c.Remaining);

            if (totalRemaining <= 0m)
            {
                return Fail(charges.Count == 0
                    ? "This order has no card payment to refund."
                    : "This order has already been fully refunded.",
                    await BuildSummaryAsync(order));
            }

            // Null amount = "everything still refundable".
            var requested = Math.Round(amount ?? totalRemaining, 2, MidpointRounding.AwayFromZero);

            if (requested <= 0m)
                return Fail("Refund amount must be greater than zero.", await BuildSummaryAsync(order));

            if (requested > totalRemaining)
            {
                return Fail(
                    $"Refund amount exceeds what is still refundable on this order ({totalRemaining:C}).",
                    await BuildSummaryAsync(order));
            }

            // Allocate across charges oldest-first. Each slice becomes its own OrderRefund row and
            // its own Stripe call, because Stripe refunds a single PaymentIntent at a time.
            var outstanding = requested;
            var refundIds = new List<string>();
            var createdRows = new List<OrderRefund>();
            decimal actuallyRefunded = 0m;
            string? firstFailure = null;

            foreach (var charge in charges)
            {
                if (outstanding <= 0m) break;

                var slice = Math.Min(outstanding, charge.Remaining);
                if (slice <= 0m) continue;

                // The row is persisted BEFORE the Stripe call for two reasons: a crash mid-call
                // still leaves evidence that a refund was attempted, and the row's PK becomes the
                // idempotency key — unique per intended refund, stable across retries of that row.
                var row = new OrderRefund
                {
                    OrderId = order.Id,
                    Amount = slice,
                    PaymentIntentId = charge.PaymentIntentId,
                    Status = "Pending",
                    Reason = reason,
                    RefundedByUserId = adminUserId,
                    CreatedAt = DateTime.UtcNow,
                    EmailSent = false
                };

                _context.OrderRefunds.Add(row);
                await _context.SaveChangesAsync();
                createdRows.Add(row);

                try
                {
                    var refund = await _stripeService.CreateRefundAsync(
                        charge.PaymentIntentId,
                        slice,
                        idempotencyKey: $"order-{order.Id}-refund-{row.Id}",
                        metadata: new Dictionary<string, string>
                        {
                            { "orderId", order.Id.ToString() },
                            { "adminUserId", adminUserId.ToString() },
                            { "orderRefundId", row.Id.ToString() }
                        });

                    row.StripeRefundId = refund.Id;
                    row.Status = refund.Status ?? "succeeded";

                    if (!string.IsNullOrEmpty(refund.Id))
                        refundIds.Add(refund.Id);

                    // "pending" is a normal ACH/slow-rail outcome — the money is committed, so it
                    // counts toward the refunded total just like "succeeded".
                    if (row.Status is "succeeded" or "pending")
                    {
                        actuallyRefunded += slice;
                        outstanding -= slice;
                    }
                    else
                    {
                        firstFailure ??= $"The payment provider returned status \"{row.Status}\".";
                    }
                }
                catch (Exception ex)
                {
                    // StripeService wraps StripeException in ApplicationException carrying Stripe's
                    // raw text — logged in full here, but never surfaced to the UI.
                    _logger.LogError(ex, "Refund of {Amount} on intent {PaymentIntentId} for order {OrderId} failed",
                        slice, charge.PaymentIntentId, order.Id);

                    row.Status = "Failed";
                    row.FailureReason = Truncate(ex.Message, 500);
                    firstFailure ??= "The payment provider rejected the refund.";
                }

                await _context.SaveChangesAsync();

                if (firstFailure != null)
                {
                    // Stop at the first failure rather than trying the next charge. Refunds already
                    // issued in this loop CANNOT be undone, so the admin is told exactly what went
                    // through and can retry the remainder deliberately.
                    break;
                }
            }

            if (actuallyRefunded <= 0m)
            {
                return Fail(firstFailure ?? "The refund could not be completed.", await BuildSummaryAsync(order));
            }

            await ApplyRefundToReportingAsync(order, actuallyRefunded, charges);
            await LogAuditAsync(order, actuallyRefunded, reason, adminUserId);

            var emailSent = false;
            if (sendEmail)
            {
                emailSent = await TrySendRefundEmailAsync(order, actuallyRefunded, charges);

                if (emailSent)
                {
                    foreach (var row in createdRows.Where(r => r.Status is "succeeded" or "pending"))
                        row.EmailSent = true;

                    await _context.SaveChangesAsync();
                }
            }

            var partial = actuallyRefunded < requested;
            var message = partial
                ? $"Refunded {actuallyRefunded:C} of the requested {requested:C}. {firstFailure} Please retry the remainder."
                : $"Refunded {actuallyRefunded:C} successfully.";

            if (sendEmail && !emailSent)
                message += " The confirmation email could not be sent — the refund itself went through.";

            return new RefundResultDto
            {
                Success = !partial,
                Message = message,
                RefundIds = refundIds,
                AmountRefunded = actuallyRefunded,
                EmailSent = emailSent,
                Summary = await BuildSummaryAsync(order)
            };
        }

        /// <summary>
        /// Propagates a completed refund into everything that reports money: the order's cached
        /// refund total, its status when nothing is left to refund, and the customer's lifetime
        /// spend (which drives loyalty tiers and CRM LTV).
        ///
        /// The revenue rule this implements: refunded money stops being income, and whatever the
        /// company KEEPS stays income. A $70 cancellation fee retained on a $300 order leaves $70
        /// counting everywhere. That falls out of subtracting TotalRefundedAmount rather than
        /// dropping refunded orders from the queries — which is also why a fully-refunded order
        /// keeps its cleaner salary as a real cost.
        /// </summary>
        private async Task ApplyRefundToReportingAsync(Order order, decimal refundedNow, List<RefundableCharge> chargesBefore)
        {
            order.TotalRefundedAmount += refundedNow;

            var totalCharged = chargesBefore.Sum(c => c.AmountReceived);
            var refundedBefore = chargesBefore.Sum(c => c.AmountRefunded);
            var fullyRefunded = totalCharged > 0m && (refundedBefore + refundedNow) >= totalCharged;

            if (fullyRefunded && !OrderStatuses.IsRefunded(order.Status))
            {
                // Preserved so reporting can still tell "cleaned, then refunded" (cleaner was paid)
                // from "refunded before service" (no cost). Never overwritten on a second refund.
                order.StatusBeforeRefund ??= order.Status;
                order.Status = OrderStatuses.Refunded;
            }

            // Lifetime spend feeds loyalty tiers and CRM lifetime value, so refunded money must
            // come back out of it. Floored at zero — a legacy order refunded for more than the
            // account ever accumulated must not push the balance negative.
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == order.UserId);
            if (user != null)
            {
                user.TotalSpentAmount = Math.Max(0m, user.TotalSpentAmount - refundedNow);
            }

            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// Sends the customer's refund confirmation. Returns false instead of throwing: a refund
        /// is real money already returned, so a mail failure must never look like a failed refund.
        /// </summary>
        private async Task<bool> TrySendRefundEmailAsync(Order order, decimal refundedNow, List<RefundableCharge> chargesBefore)
        {
            try
            {
                // "Full" means the customer is getting back everything they paid by card, counting
                // anything refunded earlier — not merely that this one click covered its own ask.
                var totalCharged = chargesBefore.Sum(c => c.AmountReceived);
                var refundedBefore = chargesBefore.Sum(c => c.AmountRefunded);
                var isFullRefund = totalCharged > 0m && (refundedBefore + refundedNow) >= totalCharged;

                await _emailService.SendRefundConfirmationEmailAsync(
                    order.ContactEmail,
                    order.ContactFirstName,
                    order.Id,
                    refundedNow,
                    isFullRefund,
                    order.ServiceDate,
                    BuildServiceAddress(order));

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refund confirmation email failed for order {OrderId}; refund itself succeeded", order.Id);
                return false;
            }
        }

        private static string BuildServiceAddress(Order order)
        {
            var street = string.IsNullOrWhiteSpace(order.AptSuite)
                ? order.ServiceAddress
                : $"{order.ServiceAddress}, {order.AptSuite}";

            return $"{street}, {order.City}, {order.State} {order.ZipCode}";
        }

        private async Task LogAuditAsync(Order order, decimal amount, string? reason, int adminUserId)
        {
            try
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    EntityType = "OrderRefund",
                    EntityId = order.Id,
                    Action = "Refund",
                    UserId = adminUserId,
                    CreatedAt = DateTime.UtcNow,
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        OrderId = order.Id,
                        Amount = amount,
                        Reason = reason
                    })
                });

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Never let an audit write failure undo or obscure a completed refund.
                _logger.LogError(ex, "Could not write audit entry for refund on order {OrderId}", order.Id);
            }
        }

        private static RefundResultDto Fail(string message, OrderRefundSummaryDto? summary = null) =>
            new() { Success = false, Message = message, Summary = summary };

        private static string Truncate(string value, int max) =>
            string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);
    }
}
