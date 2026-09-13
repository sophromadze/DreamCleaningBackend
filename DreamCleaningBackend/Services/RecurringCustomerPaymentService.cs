using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Recurring;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>A rule the customer can act on. Mapped to 400.</summary>
    public class CombinedPaymentException : Exception
    {
        public CombinedPaymentException(string message) : base(message) { }
    }

    public interface IRecurringCustomerPaymentService
    {
        Task<UpcomingRecurringOrdersDto> GetUpcomingAsync(int userId);
        Task<CombinedPaymentDto> StartCombinedPaymentAsync(int userId, StartCombinedPaymentDto dto);

        /// <summary>Settles a batch. Idempotent, and safe to call from a webhook.</summary>
        Task<bool> SettleBatchAsync(string paymentIntentId, int? batchId = null);

        Task MarkBatchFailedAsync(string paymentIntentId, string? reason);
        Task RefreshBatchStateAsync(string paymentIntentId);
        Task PrepareIndividualPaymentAsync(int orderId);
    }

    /// <summary>
    /// THE CUSTOMER'S SIDE of recurring orders: what is coming up, what they may pay, and paying
    /// several at once.
    ///
    /// ══ WHAT MAY BE PAID, AND WHEN ══
    ///
    /// <see cref="RecurringPaymentPolicy"/> owns both rules and this service only applies them.
    /// The nearest unpaid cleaning is payable; paying it opens the next. A customer who wants to
    /// prepay the whole horizon can, one at a time or through
    /// <see cref="StartCombinedPaymentAsync"/> — the sequential gate exists so the page does not
    /// open with four bills on it, not to stop anybody who has decided to settle the lot.
    ///
    /// ══ THE MONEY IS DERIVED, NEVER ACCEPTED ══
    ///
    /// <see cref="StartCombinedPaymentDto"/> carries no amount and no order ids. This service reads
    /// the SIGNED-IN customer's own upcoming occurrences, sums their stored totals, and writes the
    /// figure to <see cref="OrderPaymentBatch"/> before Stripe is ever called. There is therefore
    /// no request shape in which somebody can name another customer's order or a total of their
    /// choosing.
    ///
    /// ══ NOTHING IS PAID TWICE ══
    ///
    /// Three layers, in order of how much they can be trusted:
    ///  1. An order already in an in-flight batch is excluded when a new batch is assembled.
    ///  2. Settlement re-reads every order INSIDE the transaction and skips any that became paid
    ///     in the meantime, recording that on the batch instead of pretending it did not happen.
    ///  3. The unique index on <c>OrderPaymentBatches.PaymentIntentId</c> is the guarantee — a
    ///     retried or concurrently-delivered webhook cannot settle the same batch twice.
    /// </summary>
    public class RecurringCustomerPaymentService : IRecurringCustomerPaymentService
    {
        private readonly ApplicationDbContext _context;
        private readonly IStripeService _stripe;
        private readonly IAuditService _audit;
        private readonly ILogger<RecurringCustomerPaymentService> _logger;

        /// <summary>Below this Stripe refuses the charge outright.</summary>
        private const decimal StripeMinimumChargeAmount = 0.50m;

        /// <summary>The webhook discriminator. Distinct from "booking", "order_update" and
        /// "gift_card" so the shared endpoint routes it without ambiguity.</summary>
        public const string StripeMetadataType = "recurring_batch";

        public RecurringCustomerPaymentService(
            ApplicationDbContext context,
            IStripeService stripe,
            IAuditService audit,
            ILogger<RecurringCustomerPaymentService> logger)
        {
            _context = context;
            _stripe = stripe;
            _audit = audit;
            _logger = logger;
        }

        // ── What is coming up ─────────────────────────────────────────────────────────────────

        public async Task<UpcomingRecurringOrdersDto> GetUpcomingAsync(int userId)
        {
            await RefreshCustomerBatchesAsync(userId);
            var orders = await LoadUpcomingAsync(userId, null);

            var occurrences = orders.Select(ToOccurrence).ToList();
            await MarkInFlightAsync(occurrences);
            var payability = RecurringPaymentPolicy.ResolvePayability(occurrences)
                .ToDictionary(p => p.OrderId);

            var result = new UpcomingRecurringOrdersDto();
            var payable = RecurringPaymentPolicy.ResolveCombinedPaymentSet(occurrences);

            foreach (var order in orders)
            {
                var verdict = payability[order.Id];
                var settled = OrderPaymentFilter.IsSettledInMemory(order);

                result.Orders.Add(new UpcomingRecurringOrderDto
                {
                    OrderId = order.Id,
                    IncludedInPayAll = payable.Contains(order.Id),
                    PaymentMethod = order.PaymentMethod.ToString(),
                    ServiceDate = order.ServiceDate,
                    ServiceTime = order.ServiceTime,
                    ServiceTypeName = order.GetDisplayServiceTypeName(),
                    Status = order.Status,
                    Total = order.Total,
                    AmountDue = settled ? 0m : order.Total,
                    IsPaid = settled,
                    IsPayable = verdict.IsPayable,
                    QueuePosition = verdict.QueuePosition,
                    BlockedReason = verdict.BlockedReason
                });
            }

            result.PayAllCount = payable.Count;
            result.PayAllTotal = orders.Where(o => payable.Contains(o.Id)).Sum(o => o.Total);

            // Not offered for a single order: it is already individually payable, and a second
            // button doing the same thing is noise.
            result.CanPayAll = payable.Count > 1 && result.PayAllTotal >= StripeMinimumChargeAmount;

            return result;
        }

        // ── Pay all upcoming ──────────────────────────────────────────────────────────────────

        public async Task<CombinedPaymentDto> StartCombinedPaymentAsync(int userId, StartCombinedPaymentDto dto)
        {
            if (dto.RecurringSeriesId.HasValue)
            {
                // The series has to be the CALLER'S. Checked rather than filtered, so naming
                // somebody else's series is refused rather than silently returning their orders.
                var ownsSeries = await _context.RecurringOrderSeries
                    .AnyAsync(s => s.Id == dto.RecurringSeriesId.Value && s.UserId == userId);

                if (!ownsSeries)
                    throw new CombinedPaymentException("That recurring plan could not be found.");
            }

            // Share the customer lock with individual payments until the replacement intent is saved.
            using var selectionTransaction = await RecurringPaymentAttemptGuard.LockAsync(_context, userId);
            await RefreshCustomerBatchesAsync(userId);
            var orders = await LoadUpcomingAsync(userId, dto.RecurringSeriesId);
            var occurrences = orders.Select(ToOccurrence).ToList();
            await MarkInFlightAsync(occurrences);
            var payableIds = RecurringPaymentPolicy.ResolveCombinedPaymentSet(occurrences);

            if (payableIds.Count == 0)
                throw new CombinedPaymentException("There is nothing left to pay for on this plan.");

            // Orders already sitting in an authorized-but-unsettled batch are excluded. Without
            // this an impatient customer who pressed the button twice would authorize the same
            // cleanings on two intents and be debited for both.
            var inFlight = await _context.OrderPaymentBatchItems
                .Where(i => payableIds.Contains(i.OrderId)
                            && i.Batch!.Status == OrderPaymentBatchStatus.Processing)
                .Select(i => i.OrderId)
                .ToListAsync();

            if (inFlight.Count > 0)
            {
                throw new CombinedPaymentException(
                    "A payment for these cleanings is already being processed. "
                    + "Give it a moment and refresh the page before trying again.");
            }

            var selected = orders.Where(o => payableIds.Contains(o.Id)).OrderBy(o => o.ServiceDate).ToList();
            await CancelOpenBatchesAsync(payableIds);
            foreach (var order in selected.Where(o => !string.IsNullOrEmpty(o.PaymentIntentId)))
                await RecurringPaymentAttemptGuard.CancelOpenAsync(_stripe, order.PaymentIntentId!);
            var amount = OrderPricingCalculator.Round2(selected.Sum(o => o.Total));

            if (amount < StripeMinimumChargeAmount)
                throw new CombinedPaymentException("The amount due is below the minimum a card can be charged.");

            var batch = new OrderPaymentBatch
            {
                UserId = userId,
                RecurringSeriesId = dto.RecurringSeriesId,
                Amount = amount,
                Status = OrderPaymentBatchStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            foreach (var order in selected)
            {
                batch.Items.Add(new OrderPaymentBatchItem
                {
                    OrderId = order.Id,
                    // Frozen: re-reading the total at settlement would let an admin edit between
                    // authorization and capture change what the customer is treated as having paid.
                    Amount = order.Total,
                    CreatedAt = DateTime.UtcNow
                });
            }

            _context.OrderPaymentBatches.Add(batch);
            await _context.SaveChangesAsync();


            var customerStripeId = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.StripeCustomerId)
                .FirstOrDefaultAsync();

            var intent = await _stripe.CreatePaymentIntentAsync(
                amount,
                new Dictionary<string, string>
                {
                    { "type", StripeMetadataType },
                    { "batchId", batch.Id.ToString() },
                    { "userId", userId.ToString() },
                    // Not read by anything — it is here so a Stripe dashboard row can be tied back
                    // to the cleanings without opening this database.
                    { "orderIds", string.Join(",", selected.Select(o => o.Id)) }
                },
                receiptEmail: await ResolveReceiptEmailAsync(selected.First()),
                customerId: customerStripeId);

            batch.PaymentIntentId = intent.Id;
            batch.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            if (selectionTransaction != null) { await selectionTransaction.CommitAsync(); await selectionTransaction.DisposeAsync(); }

            await _audit.LogActionAsync(
                AuditEntityTypes.OrderPaymentBatchAction, batch.Id, "CombinedPaymentStarted",
                oldValues: null,
                newValues: new
                {
                    BatchId = batch.Id,
                    CustomerUserId = userId,
                    batch.Amount,
                    OrderIds = selected.Select(o => o.Id).ToArray(),
                    dto.RecurringSeriesId
                },
                actingUserId: userId);

            return new CombinedPaymentDto
            {
                BatchId = batch.Id,
                Amount = amount,
                OrderIds = selected.Select(o => o.Id).ToList(),
                PaymentIntentId = intent.Id,
                PaymentClientSecret = intent.ClientSecret,
                RequiresPayment = true
            };
        }

        // ── Settlement ────────────────────────────────────────────────────────────────────────

        public async Task<bool> SettleBatchAsync(string paymentIntentId, int? batchId = null)
        {
            var ownerId = await _context.OrderPaymentBatches.Where(b => b.PaymentIntentId == paymentIntentId
                || (batchId != null && b.Id == batchId.Value)).Select(b => (int?)b.UserId).FirstOrDefaultAsync();
            if (ownerId == null) return false;
            using var transaction = await RecurringPaymentAttemptGuard.LockAsync(_context, ownerId.Value);
            var batch = await _context.OrderPaymentBatches
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.PaymentIntentId == paymentIntentId
                                          || (batchId != null && b.Id == batchId.Value));

            if (batch == null)
            {
                _logger.LogWarning(
                    "No combined-payment batch found for PaymentIntent {PaymentIntentId}.", paymentIntentId);
                return false;
            }

            await _context.Entry(batch).ReloadAsync();
            // Already settled by an earlier delivery. Exactly what a webhook retry should find.
            if (batch.Status == OrderPaymentBatchStatus.Paid) return false;

            var orderIds = batch.Items.Select(i => i.OrderId).ToList();



            try
            {
                // Re-read INSIDE the transaction: an admin may have recorded a manual payment, or
                // the customer may have paid one of these individually, between authorization and
                // capture.
                var orders = await _context.Orders
                    .Where(o => orderIds.Contains(o.Id))
                    .ToListAsync();

                var alreadyPaid = new List<int>();

                foreach (var item in batch.Items)
                {
                    var order = orders.FirstOrDefault(o => o.Id == item.OrderId);
                    if (order == null) continue;

                    if (OrderPaymentFilter.IsSettledInMemory(order) || order.PaymentMethod != PaymentMethod.Normal
                        || OrderStatuses.IsCancelled(order.Status) || OrderStatuses.IsRefunded(order.Status))
                    {
                        // The money still arrived, so the item stays on the batch — deleting it
                        // would hide the duplicate rather than record it. Flagged for a person.
                        alreadyPaid.Add(order.Id);
                        item.AppliedToOrder = false;
                        continue;
                    }

                    order.IsPaid = true;
                    order.PaidAt = DateTime.UtcNow;
                    order.PaymentIntentId = paymentIntentId;

                    // Never downgrade: a cleaning already performed stays Done.
                    if (OrderStatuses.Is(order.Status, OrderStatuses.Pending))
                        order.Status = OrderStatuses.Active;

                    // The "initial" snapshot is what later order edits measure a top-up against.
                    // Seeded here on first payment, exactly as confirm-payment does.
                    if (order.InitialSubTotal == 0 && order.InitialTax == 0 && order.InitialTotal == 0)
                    {
                        order.InitialSubTotal = order.SubTotal;
                        order.InitialTax = order.Tax;
                        order.InitialTips = order.Tips;
                        order.InitialCompanyDevelopmentTips = order.CompanyDevelopmentTips;
                        order.InitialTotal = order.Total;
                    }

                    order.UpdatedAt = DateTime.UtcNow;
                    item.AppliedToOrder = true;
                }

                batch.Status = OrderPaymentBatchStatus.Paid;
                batch.PaidAt = DateTime.UtcNow;
                batch.UpdatedAt = DateTime.UtcNow;

                if (alreadyPaid.Count > 0)
                {
                    batch.SettlementWarning =
                        "Paid by another route before this payment settled: "
                        + string.Join(", ", alreadyPaid.Select(id => $"order #{id}"))
                        + ". This money needs refunding.";
                }

                await _context.SaveChangesAsync();
                if (transaction != null) { await transaction.CommitAsync(); await transaction.DisposeAsync(); }
            }
            catch
            {
                if (transaction != null) await transaction.RollbackAsync();
                throw;
            }

            await _audit.LogActionAsync(
                AuditEntityTypes.OrderPaymentBatchAction, batch.Id, "CombinedPaymentSettled",
                oldValues: null,
                newValues: new
                {
                    BatchId = batch.Id,
                    batch.PaymentIntentId,
                    batch.Amount,
                    CoveredOrderIds = batch.Items.Where(i => i.AppliedToOrder).Select(i => i.OrderId).ToArray(),
                    AlreadyPaidOrderIds = batch.Items.Where(i => !i.AppliedToOrder).Select(i => i.OrderId).ToArray(),
                    batch.SettlementWarning
                },
                actingUserId: null);

            if (batch.SettlementWarning != null)
            {
                _logger.LogWarning(
                    "Combined payment batch {BatchId} settled with a discrepancy: {Warning}",
                    batch.Id, batch.SettlementWarning);
            }

            return true;
        }

        public async Task MarkBatchFailedAsync(string paymentIntentId, string? reason)
        {
            await RefreshBatchStateAsync(paymentIntentId);
        }

        public async Task RefreshBatchStateAsync(string paymentIntentId)
        {
            var batch = await _context.OrderPaymentBatches.FirstOrDefaultAsync(b => b.PaymentIntentId == paymentIntentId);
            if (batch == null || batch.Status == OrderPaymentBatchStatus.Paid) return;
            using var transaction = await RecurringPaymentAttemptGuard.LockAsync(_context, batch.UserId);
            await _context.Entry(batch).ReloadAsync();
            if (batch.Status == OrderPaymentBatchStatus.Paid) return;
            var intent = await _stripe.GetPaymentIntentAsync(paymentIntentId);
            if (intent.Status == "succeeded") await SettleBatchAsync(paymentIntentId);
            else {
                batch.Status = intent.Status switch {
                    "processing" or "requires_capture" => OrderPaymentBatchStatus.Processing,
                    "canceled" => OrderPaymentBatchStatus.Canceled,
                    "requires_payment_method" when intent.LastPaymentError != null => OrderPaymentBatchStatus.Failed,
                    "requires_payment_method" or "requires_confirmation" or "requires_action" => OrderPaymentBatchStatus.Pending,
                    _ => throw new CombinedPaymentException("Could not verify the payment status. Please try again.")
                };
                batch.FailureReason = intent.LastPaymentError?.Message;
                batch.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }
            if (transaction != null) await transaction.CommitAsync();
        }

        private async Task RefreshCustomerBatchesAsync(int userId)
        {
            var intents = await _context.OrderPaymentBatches.Where(b => b.UserId == userId
                && b.Status != OrderPaymentBatchStatus.Paid && b.Status != OrderPaymentBatchStatus.Canceled
                && b.PaymentIntentId != null).Select(b => b.PaymentIntentId!).ToListAsync();
            foreach (var id in intents) await RefreshBatchStateAsync(id);
        }

        private async Task CancelOpenBatchesAsync(List<int> orderIds)
        {
            var batches = await _context.OrderPaymentBatches.Where(b => b.Items.Any(i => orderIds.Contains(i.OrderId))
                && b.Status != OrderPaymentBatchStatus.Paid && b.Status != OrderPaymentBatchStatus.Canceled).ToListAsync();
            foreach (var batch in batches) {
                if (batch.PaymentIntentId == null) {
                    if (batch.Status == OrderPaymentBatchStatus.Processing)
                        throw new CombinedPaymentException("A combined payment is already being processed.");
                    // A legacy crash before the secret was returned cannot authorize a browser payment.
                    batch.Status = OrderPaymentBatchStatus.Canceled;
                } else {
                    await RecurringPaymentAttemptGuard.CancelOpenAsync(_stripe, batch.PaymentIntentId);
                    batch.Status = OrderPaymentBatchStatus.Canceled;
                }
                batch.UpdatedAt = DateTime.UtcNow;
                batch.FailureReason ??= "Open payment superseded before submission.";
            }
            await _context.SaveChangesAsync();
        }

        /// <summary>Called under the same customer lock as combined checkout preparation.</summary>
        public async Task PrepareIndividualPaymentAsync(int orderId)
        {
            var order = await _context.Orders.FirstAsync(o => o.Id == orderId);
            await RefreshCustomerBatchesAsync(order.UserId);
            if (order.IsPaid || order.InvoicePaidAt != null)
                throw new CombinedPaymentException("Order is already paid.");
            await CancelOpenBatchesAsync(new List<int> { orderId });
            if (!string.IsNullOrEmpty(order.PaymentIntentId))
                await RecurringPaymentAttemptGuard.CancelOpenAsync(_stripe, order.PaymentIntentId);
        }

        // ── Shared reads ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The customer's upcoming recurring cleanings, NEAREST FIRST.
        ///
        /// Scoped to generated/recurring orders on purpose: this page answers "what does my
        /// standing arrangement look like", and folding in unrelated one-off bookings would make
        /// "Pay all upcoming" mean something the customer did not ask for.
        ///
        /// Today's cleaning is included — it has not happened yet as far as anybody knows at 8am,
        /// and it is still payable.
        /// </summary>
        private async Task<List<Order>> LoadUpcomingAsync(int userId, int? seriesId)
        {
            var today = NyTimeHelper.NowNy.Date;

            var query = _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.User)
                .Where(o => o.UserId == userId
                            && o.RecurringSeriesId != null
                            && o.ServiceDate >= today
                            && o.Status != OrderStatuses.Cancelled
                            && o.Status != OrderStatuses.Refunded);

            if (seriesId.HasValue) query = query.Where(o => o.RecurringSeriesId == seriesId.Value);

            return await query
                .OrderBy(o => o.ServiceDate)
                .ThenBy(o => o.ServiceTime)
                .ThenBy(o => o.Id)
                .ToListAsync();
        }

        private static RecurringPayableOccurrence ToOccurrence(Order o) => new()
        {
            IsOnlinePayable = o.PaymentMethod == PaymentMethod.Normal,
            PaymentIntentId = o.PaymentIntentId,
            OrderId = o.Id,
            ServiceDateTime = o.ServiceDate.Date.Add(o.ServiceTime),
            IsPaid = OrderPaymentFilter.IsSettledInMemory(o),
            // Money handled outside Stripe is not payable on the website at all, so it never sits
            // in the queue blocking the cleaning behind it.
            AmountDue = o.PaymentMethod == PaymentMethod.Normal ? o.Total : 0m,
            IsCancelled = OrderStatuses.IsCancelled(o.Status) || OrderStatuses.IsRefunded(o.Status)
        };

        private async Task MarkInFlightAsync(List<RecurringPayableOccurrence> occurrences)
        {
            var ids = occurrences.Select(o => o.OrderId).ToList();
            var busy = await _context.OrderPaymentBatchItems.Where(i => ids.Contains(i.OrderId)
                && i.Batch!.Status == OrderPaymentBatchStatus.Processing).Select(i => i.OrderId).ToListAsync();
            foreach (var occurrence in occurrences)
            {
                occurrence.PaymentInFlight = busy.Contains(occurrence.OrderId);
                if (occurrence.IsPaid || !occurrence.IsOnlinePayable || string.IsNullOrEmpty(occurrence.PaymentIntentId)) continue;
                try
                {
                    var intent = await _stripe.GetPaymentIntentAsync(occurrence.PaymentIntentId);
                    occurrence.PaymentInFlight |= RecurringPaymentAttemptGuard.IsSubmitted(intent.Status);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cannot verify payment state for recurring order {OrderId}", occurrence.OrderId);
                    throw new CombinedPaymentException("Could not verify the previous payment. Please refresh and try again.");
                }
            }
        }

        private Task<string?> ResolveReceiptEmailAsync(Order order) =>
            Task.FromResult(NoEmailHelper.ResolveOrderNotificationEmail(order.ContactEmail, order.User));
    }
}
