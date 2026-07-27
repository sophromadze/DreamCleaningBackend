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
            /// <summary>Chargeback against this charge. NOT included in AmountRefunded — see
            /// ChargeRefundState.HasDispute. Surfaced as a warning, never imported as a refund.</summary>
            public bool HasDispute { get; set; }
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
                    Remaining = state.RemainingRefundable,
                    HasDispute = state.HasDispute
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
                    Source = r.Source.ToString(),
                    RefundedByName = r.Source == RefundSource.Stripe
                        ? "Stripe"
                        : (r.RefundedByUser == null
                            ? "Unknown"
                            : ((r.RefundedByUser.FirstName ?? "") + " " + (r.RefundedByUser.LastName ?? "")).Trim()),
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
                HasDispute = charges.Any(c => c.HasDispute),
                // A gap here means money was refunded outside the CRM (Stripe Dashboard) and has
                // no OrderRefund row yet — the "Sync from Stripe" prompt keys off this.
                UnrecordedRefundAmount = Math.Max(0m,
                    charges.Sum(c => c.AmountRefunded) - await GetRecordedRefundTotalAsync(order.Id)),
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

        /// <summary>
        /// Amount already recorded as refunded on this order, counting only rows whose money
        /// actually moved. "Failed" rows are excluded deliberately — a failed attempt must not
        /// make the sync think a Dashboard refund is already accounted for.
        /// </summary>
        private async Task<decimal> GetRecordedRefundTotalAsync(int orderId, string? paymentIntentId = null)
        {
            var q = _context.OrderRefunds.AsNoTracking().Where(r => r.OrderId == orderId);
            if (paymentIntentId != null)
                q = q.Where(r => r.PaymentIntentId == paymentIntentId);

            return await q
                .Where(r => r.Status == "succeeded" || r.Status == "pending")
                .SumAsync(r => (decimal?)r.Amount) ?? 0m;
        }

        public async Task<RefundSyncResultDto> SyncRefundsFromStripeAsync(int orderId)
        {
            var order = await _context.Orders
                .Include(o => o.UpdateHistory)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return new RefundSyncResultDto { Success = false, Message = "Order not found." };

            List<RefundableCharge> charges;
            try
            {
                charges = await GetChargesAsync(order);
            }
            catch (Exception ex)
            {
                // GetChargeRefundStateAsync already swallows Stripe errors per charge, so this is
                // belt-and-braces: the sync must never surface a raw provider failure.
                _logger.LogError(ex, "Refund sync could not read charges for order {OrderId}", orderId);
                return new RefundSyncResultDto
                {
                    Success = false,
                    Message = "Could not reach the payment provider. Please try again."
                };
            }

            if (charges.Count == 0)
            {
                return new RefundSyncResultDto
                {
                    Success = true,
                    Message = "This order has no card payment to reconcile.",
                    Summary = await BuildSummaryAsync(order)
                };
            }

            decimal imported = 0m;
            var importedRows = 0;

            foreach (var charge in charges)
            {
                // Per-CHARGE comparison, not per-order: an order can hold several intents, and a
                // Dashboard refund against one of them must not be masked by CRM refunds recorded
                // against another.
                var recorded = await GetRecordedRefundTotalAsync(order.Id, charge.PaymentIntentId);
                var gap = charge.AmountRefunded - recorded;

                // Sub-cent gaps are rounding noise, not a real refund.
                if (gap < 0.01m) continue;

                _context.OrderRefunds.Add(new OrderRefund
                {
                    OrderId = order.Id,
                    Amount = gap,
                    PaymentIntentId = charge.PaymentIntentId,
                    // Stripe already settled it; there is no pending state to resolve.
                    Status = "succeeded",
                    Source = RefundSource.Stripe,
                    RefundedByUserId = null,
                    Reason = "Imported from Stripe — refund issued outside the CRM.",
                    CreatedAt = DateTime.UtcNow,
                    // No automatic customer email: this refund already happened and the customer
                    // may have been told elsewhere. An admin can send it manually from the history.
                    EmailSent = false
                });

                imported += gap;
                importedRows++;
            }

            ApplyRefundTotals(order, charges.Sum(c => c.AmountReceived), charges.Sum(c => c.AmountRefunded));

            // Loyalty spend moves only for money newly discovered here, and only when this order's
            // spend is actually counted — see ApplyLoyaltySpendAdjustmentAsync. Re-running the sync
            // imports nothing, so it cannot subtract twice.
            await ApplyLoyaltySpendAdjustmentAsync(order, imported);

            await _context.SaveChangesAsync();

            if (importedRows > 0)
            {
                _logger.LogInformation("Refund sync imported {Count} refund(s) totalling {Amount} for order {OrderId}",
                    importedRows, imported, order.Id);
            }

            var summary = await BuildSummaryAsync(order);
            var disputed = charges.Any(c => c.HasDispute);

            var message = importedRows == 0
                ? "Already up to date — nothing new found in Stripe."
                : $"Imported {importedRows} refund(s) totalling {imported:C} from Stripe.";

            if (disputed)
            {
                // Stated explicitly because amount_refunded stays at zero for a chargeback, so
                // "nothing new found" would otherwise read as "this order is settled".
                message += " ⚠ This order has a disputed charge (chargeback). Disputes are NOT refunds "
                         + "and were not imported — review it in the Stripe Dashboard.";
            }

            return new RefundSyncResultDto
            {
                Success = true,
                Message = message,
                RefundsImported = importedRows,
                AmountImported = imported,
                HasDispute = disputed,
                Summary = summary
            };
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
                    Source = RefundSource.Crm,
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
            var totalCharged = chargesBefore.Sum(c => c.AmountReceived);
            var refundedTotal = chargesBefore.Sum(c => c.AmountRefunded) + refundedNow;

            ApplyRefundTotals(order, totalCharged, refundedTotal);
            await ApplyLoyaltySpendAdjustmentAsync(order, refundedNow);

            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// Sets the cached refund total and flips the status when nothing is left to refund.
        /// Shared by the CRM refund path and the Stripe sync so the two can never drift.
        ///
        /// <paramref name="refundedTotal"/> is the ABSOLUTE amount refunded across the order's
        /// charges, not a delta — assigning rather than accumulating is what makes re-running the
        /// sync idempotent. Guarded with Max so a partial charge read (one intent unreachable
        /// because Stripe was down) can never erase refunds already recorded.
        /// </summary>
        private static void ApplyRefundTotals(Order order, decimal totalCharged, decimal refundedTotal)
        {
            order.TotalRefundedAmount = Math.Max(order.TotalRefundedAmount, refundedTotal);

            var fullyRefunded = totalCharged > 0m && refundedTotal >= totalCharged;

            if (fullyRefunded && !OrderStatuses.IsRefunded(order.Status))
            {
                // Preserved so reporting can still tell "cleaned, then refunded" (cleaner was paid)
                // from "refunded before service" (no cost). Never overwritten on a second refund.
                // Refunded deliberately outranks Cancelled: a cancelled order that was then fully
                // refunded reads as Refunded, and StatusBeforeRefund keeps "Cancelled" so it stays
                // out of revenue and cleaner-cost reporting exactly as before.
                order.StatusBeforeRefund ??= order.Status;
                order.Status = OrderStatuses.Refunded;
            }
        }

        /// <summary>
        /// Takes refunded money back out of the customer's lifetime spend, which drives loyalty
        /// tiers and CRM lifetime value.
        ///
        /// CRITICAL: TotalSpentAmount is tied to the DONE status, not to payment — BubblePointsService
        /// adds it in ProcessOrderCompletion when an order is marked Done and reverses it in
        /// ReverseOrderCompletion when the order leaves Done. So for an order that was never Done,
        /// or that was cancelled (already reversed), this order's money is NOT currently in the
        /// balance and subtracting again would corrupt that customer's tier. WasPerformed is the
        /// test for "is this order's spend currently counted".
        /// </summary>
        private async Task ApplyLoyaltySpendAdjustmentAsync(Order order, decimal refundedNow)
        {
            if (refundedNow <= 0m) return;

            if (!OrderStatuses.WasPerformed(order.Status, order.StatusBeforeRefund))
            {
                _logger.LogInformation(
                    "Order {OrderId} refunded {Amount} but its spend is not currently counted (status {Status}) — leaving TotalSpentAmount alone",
                    order.Id, refundedNow, order.Status);
                return;
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == order.UserId);
            if (user != null)
            {
                // Floored at zero — a legacy order refunded for more than the account ever
                // accumulated must not push the balance negative.
                user.TotalSpentAmount = Math.Max(0m, user.TotalSpentAmount - refundedNow);
            }
        }

        /// <summary>
        /// Backfill sweep: reconciles a page of orders that hold a real Stripe charge. Paged rather
        /// than "all orders" because every charge costs a Stripe round trip, and an unbounded sweep
        /// would time out the HTTP request long before it finished. Safe to re-run — the per-order
        /// sync is idempotent, so a repeat pass imports nothing.
        /// </summary>
        public async Task<RefundBackfillResultDto> BackfillRefundsFromStripeAsync(int limit, int? afterOrderId)
        {
            if (limit <= 0) limit = 200;
            if (limit > 500) limit = 500;   // hard ceiling: keeps one request inside a sane runtime

            // Only orders that actually settled money through Stripe. Manual/cash orders, unpaid
            // orders and gift-card-covered orders (synthetic giftcard_full_ reference) have nothing
            // at Stripe to reconcile, and skipping them is most of the speed-up.
            var query = _context.Orders
                .Where(o => o.PaymentIntentId != null && o.PaymentIntentId.StartsWith("pi_"));

            if (afterOrderId.HasValue)
                query = query.Where(o => o.Id > afterOrderId.Value);

            var orderIds = await query
                .OrderBy(o => o.Id)
                .Select(o => o.Id)
                .Take(limit + 1)          // one extra row purely to detect "there is another page"
                .ToListAsync();

            var hasMore = orderIds.Count > limit;
            if (hasMore) orderIds = orderIds.Take(limit).ToList();

            var result = new RefundBackfillResultDto { HasMore = hasMore };

            foreach (var id in orderIds)
            {
                try
                {
                    var sync = await SyncRefundsFromStripeAsync(id);
                    result.OrdersScanned++;

                    if (!sync.Success) { result.Failures++; continue; }
                    if (sync.RefundsImported > 0)
                    {
                        result.OrdersWithImports++;
                        result.RefundsImported += sync.RefundsImported;
                        result.AmountImported += sync.AmountImported;
                    }
                    if (sync.HasDispute) result.DisputesFound++;
                }
                catch (Exception ex)
                {
                    // One bad order must not abort the sweep — record it and keep going.
                    _logger.LogError(ex, "Refund backfill failed on order {OrderId}", id);
                    result.Failures++;
                }

                result.LastOrderId = id;

                // Gentle pacing so a long sweep stays well under Stripe's read rate limit.
                await Task.Delay(60);
            }

            result.Message = $"Scanned {result.OrdersScanned} order(s); imported {result.RefundsImported} refund(s) "
                           + $"totalling {result.AmountImported:C} across {result.OrdersWithImports} order(s)."
                           + (result.DisputesFound > 0 ? $" {result.DisputesFound} order(s) have a disputed charge — review those in Stripe." : "")
                           + (result.Failures > 0 ? $" {result.Failures} order(s) could not be checked." : "")
                           + (hasMore ? " More orders remain — run again to continue." : "");

            return result;
        }

        /// <summary>
        /// Sends (or re-sends) the customer's refund confirmation for one recorded refund, on
        /// explicit admin request. Exists because Stripe-sourced rows never mail automatically —
        /// the refund already happened and the customer may have been told elsewhere — and because
        /// a CRM refund whose email failed needs a retry.
        /// </summary>
        public async Task<RefundResultDto> SendRefundEmailAsync(int orderId, int refundId)
        {
            var refund = await _context.OrderRefunds
                .FirstOrDefaultAsync(r => r.Id == refundId && r.OrderId == orderId);

            if (refund == null)
                return Fail("Refund record not found.");

            if (refund.Status is not ("succeeded" or "pending"))
                return Fail("This refund did not go through, so there is nothing to confirm.");

            var order = await _context.Orders
                .Include(o => o.UpdateHistory)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return Fail("Order not found.");

            var charges = await GetChargesAsync(order);

            // "Full refund" is judged on the order's CURRENT state, not this row alone: the
            // customer cares whether they got everything back, not which row paid it out.
            var totalCharged = charges.Sum(c => c.AmountReceived);
            var totalRefunded = charges.Sum(c => c.AmountRefunded);
            var isFullRefund = totalCharged > 0m && totalRefunded >= totalCharged;

            try
            {
                await _emailService.SendRefundConfirmationEmailAsync(
                    order.ContactEmail,
                    order.ContactFirstName,
                    order.Id,
                    refund.Amount,
                    isFullRefund,
                    order.ServiceDate,
                    BuildServiceAddress(order));

                refund.EmailSent = true;
                await _context.SaveChangesAsync();

                return new RefundResultDto
                {
                    Success = true,
                    Message = $"Confirmation email sent to {order.ContactEmail}.",
                    AmountRefunded = refund.Amount,
                    EmailSent = true,
                    Summary = await BuildSummaryAsync(order)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual refund email failed for refund {RefundId} on order {OrderId}", refundId, orderId);
                return Fail("The email could not be sent. Please try again.", await BuildSummaryAsync(order));
            }
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
