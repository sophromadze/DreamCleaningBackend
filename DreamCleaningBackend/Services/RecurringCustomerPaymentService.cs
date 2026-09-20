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

        /// <summary>
        /// The background safety net: re-checks every batch Stripe may have charged but this
        /// database has not recorded (a failed settlement, a missed webhook) and settles it.
        /// Idempotent — a settled batch is skipped. Returns how many were settled.
        /// </summary>
        Task<int> ReconcileUnsettledBatchesAsync(CancellationToken ct = default);

        /// <summary>
        /// Records any of this customer's combined payments that Stripe has already taken, each in
        /// its OWN committed transaction. Call it BEFORE taking the customer lock for a new payment:
        /// a settlement performed inside that lock's transaction is rolled back with it whenever the
        /// payment attempt is then refused ("nothing left to pay", "already paid"). Never throws.
        /// </summary>
        Task SettleSucceededBatchesAsync(int userId);
    }

    /// <summary>
    /// An order moved underneath a settlement (an admin edit, another payment landing) between
    /// the settlement reading it and writing it. Nothing was written; the batch is retried.
    /// </summary>
    public class BatchSettlementConflictException : Exception
    {
        public BatchSettlementConflictException(string message) : base(message) { }
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
    /// the SIGNED-IN customer's own upcoming occurrences, sums what each still OWES (OrderBalance), and writes the
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
        private readonly IOrderPartialPaymentService _partialPayments;
        private readonly IServiceScopeFactory? _scopeFactory;
        private readonly Billing.BillingFeatures? _features;

        /// <summary>Written on a batch whose charge succeeded but whose recording failed. The
        /// batch sits in Processing, which blocks its cleanings from being paid again, until the
        /// webhook retry or the reconciler records it.</summary>
        public const string SettlementDeferredReason =
            "Payment received by Stripe; recording it failed and is being retried automatically. Do not charge again.";

        /// <summary>Below this Stripe refuses the charge outright.</summary>
        private const decimal StripeMinimumChargeAmount = 0.50m;

        /// <summary>The webhook discriminator. Distinct from "booking", "order_update" and
        /// "gift_card" so the shared endpoint routes it without ambiguity.</summary>
        public const string StripeMetadataType = "recurring_batch";

        public RecurringCustomerPaymentService(
            ApplicationDbContext context,
            IStripeService stripe,
            IAuditService audit,
            ILogger<RecurringCustomerPaymentService> logger,
            IOrderPartialPaymentService? partialPayments = null,
            IServiceScopeFactory? scopeFactory = null,
            Billing.BillingFeatures? features = null)
        {
            _context = context;
            _stripe = stripe;
            _audit = audit;
            _logger = logger;
            _scopeFactory = scopeFactory;
            _features = features;
            // Optional only so older direct constructions keep compiling; DI always supplies it.
            // Built over the SAME context, so its writes join this service's transaction.
            _partialPayments = partialPayments ?? new OrderPartialPaymentService(
                context, audit, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrderPartialPaymentService>.Instance);
        }

        // ── What is coming up ─────────────────────────────────────────────────────────────────

        public async Task<UpcomingRecurringOrdersDto> GetUpcomingAsync(int userId)
        {
            // A READ must not fail because one batch could not be checked — but a batch that could
            // not be checked is treated as in flight, so its cleanings are never offered for
            // payment again on the strength of a lookup that did not happen.
            var unverified = await RefreshCustomerBatchesAsync(userId, failClosed: false);
            var orders = await LoadUpcomingAsync(userId, null);

            var occurrences = orders.Select(ToOccurrence).ToList();
            await MarkInFlightAsync(occurrences, unverified);
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
                    AmountDue = settled ? 0m : OrderBalance.AmountDue(order),
                    IsPaid = settled,
                    IsPayable = verdict.IsPayable,
                    QueuePosition = verdict.QueuePosition,
                    BlockedReason = verdict.BlockedReason
                });
            }

            result.PayAllCount = payable.Count;
            // What is still OWED, never the totals: a deposit already taken is not charged again.
            result.PayAllTotal = OrderPricingCalculator.Round2(
                orders.Where(o => payable.Contains(o.Id)).Sum(OrderBalance.AmountDue));

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

            // Record anything already paid FIRST, outside the lock's transaction — see
            // SettleSucceededBatchesAsync. The refresh under the lock below stays authoritative.
            await SettleSucceededBatchesAsync(userId);

            // Share the customer lock with individual payments until the replacement intent is saved.
            using var selectionTransaction = await RecurringPaymentAttemptGuard.LockAsync(_context, userId);
            await RefreshCustomerBatchesAsync(userId, failClosed: true);
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
            // The BALANCE of each order, not its total — charging the total took a deposit a second
            // time. Same rule create-payment-intent follows.
            var amount = OrderPricingCalculator.Round2(selected.Sum(OrderBalance.AmountDue));

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
                    Amount = OrderBalance.AmountDue(order),
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

            // Saved cards on: make sure the customer HAS a Stripe Customer, so the card they type
            // can be saved if they choose "Save Card & Pay" in the pre-payment modal (2026-09) —
            // applied by the browser at confirmation, on this same intent. Best-effort: paying
            // never depends on it.
            var savedCardsEnabled = _features?.SavedCardsEnabled == true;
            if (string.IsNullOrEmpty(customerStripeId) && savedCardsEnabled)
            {
                try
                {
                    var owner = await _context.Users.FirstAsync(u => u.Id == userId);
                    customerStripeId = await _stripe.CreateOrGetCustomerAsync(owner);
                    await _context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not create a Stripe customer for user {UserId}; paying all upcoming without card saving.", userId);
                    customerStripeId = null;
                }
            }

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
                RequiresPayment = true,
                CanSaveCard = savedCardsEnabled && !string.IsNullOrEmpty(customerStripeId)
            };
        }

        // ── Settlement ────────────────────────────────────────────────────────────────────────
        //
        // ══ ONE CHARGE, MANY ORDERS — WHERE THE LINK LIVES ══
        //
        // A combined payment is ONE Stripe PaymentIntent covering several orders. The link from
        // an order to that charge is its OrderPaymentBatchItem row, and ONLY that row. Settlement
        // used to stamp the batch intent onto every covered Order.PaymentIntentId, which carries a
        // UNIQUE index (IX_Orders_PaymentIntentId — the guard that stops one booking intent from
        // ever producing two orders). The second order's write violated it, the whole transaction
        // rolled back, and the webhook swallowed the error: the customer was charged and every
        // cleaning stayed unpaid. The index is right and stays; the order row simply is not where
        // a shared charge is recorded. Refunds, billing history and the reconciler all read the
        // batch items to find it.
        //
        // ══ ATOMIC, CONDITIONAL, RETRYABLE ══
        //
        // Every order is written with a conditional UPDATE against the exact figures the share was
        // computed from, so an admin edit or another payment landing mid-settlement makes the
        // whole batch roll back and retry rather than record a wrong allocation. When already
        // inside a caller's transaction a SAVEPOINT scopes the rollback to this batch, so a failed
        // settlement can never leave half its orders paid inside a transaction that commits.

        public async Task<bool> SettleBatchAsync(string paymentIntentId, int? batchId = null)
        {
            var header = await _context.OrderPaymentBatches.AsNoTracking()
                .Where(b => b.PaymentIntentId == paymentIntentId || (batchId != null && b.Id == batchId.Value))
                .OrderByDescending(b => b.PaymentIntentId == paymentIntentId)
                .Select(b => new { b.Id, b.UserId, b.PaymentIntentId })
                .FirstOrDefaultAsync();

            if (header == null)
            {
                _logger.LogWarning(
                    "No combined-payment batch found for PaymentIntent {PaymentIntentId}.", paymentIntentId);
                return false;
            }

            if (!string.Equals(header.PaymentIntentId, paymentIntentId, StringComparison.Ordinal))
            {
                // The metadata named a batch that records a DIFFERENT intent. Crediting it would
                // settle cleanings against money that was never taken for them.
                _logger.LogError(
                    "PaymentIntent {PaymentIntentId} names combined-payment batch {BatchId}, which records intent {Recorded}. Not settled — needs a person.",
                    paymentIntentId, header.Id, header.PaymentIntentId);
                return false;
            }

            var ownTransaction = await RecurringPaymentAttemptGuard.LockAsync(_context, header.UserId);
            var outerTransaction = ownTransaction == null ? _context.Database.CurrentTransaction : null;
            const string savepoint = "settle_combined_payment";
            if (outerTransaction != null) await outerTransaction.CreateSavepointAsync(savepoint);

            OrderPaymentBatch? batch = null;
            try
            {
                batch = await _context.OrderPaymentBatches.Include(b => b.Items).FirstAsync(b => b.Id == header.Id);
                await _context.Entry(batch).ReloadAsync();

                // Already settled by an earlier delivery. Exactly what a webhook retry should find.
                if (batch.Status == OrderPaymentBatchStatus.Paid)
                {
                    if (outerTransaction != null) await outerTransaction.ReleaseSavepointAsync(savepoint);
                    return false;
                }

                await ApplyBatchToOrdersAsync(batch, paymentIntentId);

                batch.Status = OrderPaymentBatchStatus.Paid;
                batch.PaidAt = DateTime.UtcNow;
                batch.UpdatedAt = DateTime.UtcNow;
                batch.FailureReason = null;
                await _context.SaveChangesAsync();

                if (ownTransaction != null) await ownTransaction.CommitAsync();
                else if (outerTransaction != null) await outerTransaction.ReleaseSavepointAsync(savepoint);
            }
            catch (Exception ex)
            {
                if (ownTransaction != null) await ownTransaction.RollbackAsync();
                else if (outerTransaction != null) await outerTransaction.RollbackToSavepointAsync(savepoint);
                DetachBatch(batch);

                _logger.LogError(ex,
                    "Combined payment {PaymentIntentId} (batch {BatchId}) was charged but could not be recorded; it will be retried.",
                    paymentIntentId, header.Id);
                await DeferSettlementAsync(header.Id);
                throw;
            }
            finally
            {
                if (ownTransaction != null) await ownTransaction.DisposeAsync();
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
                    NotAppliedOrderIds = batch.Items.Where(i => !i.AppliedToOrder).Select(i => i.OrderId).ToArray(),
                    batch.SettlementWarning
                },
                actingUserId: null);

            if (batch.SettlementWarning != null)
            {
                _logger.LogWarning(
                    "Combined payment batch {BatchId} settled with a discrepancy: {Warning}",
                    batch.Id, batch.SettlementWarning);
            }

            // What a single payment does next (loyalty, subscription, confirmation email/SMS) —
            // DETACHED, in its own scope, so nothing about it can delay or fail the settlement
            // that just committed. Exactly-once is the follow-up service's own guarantee; if this
            // trigger is lost (or runs before an enclosing transaction commits), AutoPayWorker's
            // pass picks the orders up within minutes.
            if (_scopeFactory != null)
            {
                var settledBatchId = batch.Id;
                BackgroundWork.Run(_scopeFactory, _logger, $"combined payment #{settledBatchId} follow-up",
                    services => services.GetRequiredService<ICombinedPaymentFollowUpService>().ProcessAsync(settledBatchId));
            }

            return true;
        }

        /// <summary>
        /// Applies each item's frozen share to its order. Three outcomes per order:
        /// the share settles it (marked paid — Order.PaymentIntentId untouched), the share falls
        /// short because the price rose (credited as a slice, balance kept), or the order was
        /// paid/closed by another route (not applied, flagged for a refund decision).
        /// </summary>
        private async Task ApplyBatchToOrdersAsync(OrderPaymentBatch batch, string paymentIntentId)
        {
            var orderIds = batch.Items.Select(i => i.OrderId).ToList();
            var orders = await _context.Orders.AsNoTracking()
                .Where(o => orderIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id);

            var notes = new List<string>();
            var now = DateTime.UtcNow;

            foreach (var item in batch.Items.OrderBy(i => i.Id))
            {
                if (!orders.TryGetValue(item.OrderId, out var order))
                {
                    item.AppliedToOrder = false;
                    notes.Add($"order #{item.OrderId} no longer exists (${item.Amount:F2} needs refunding)");
                    continue;
                }

                if (OrderPaymentFilter.IsSettledInMemory(order) || order.PaymentMethod != PaymentMethod.Normal
                    || OrderStatuses.IsCancelled(order.Status) || OrderStatuses.IsRefunded(order.Status))
                {
                    // The money still arrived, so the item stays on the batch — deleting it would
                    // hide the duplicate rather than record it. Flagged for a person.
                    item.AppliedToOrder = false;
                    notes.Add($"order #{order.Id} was paid or closed by another route (${item.Amount:F2} needs refunding)");
                    continue;
                }

                var due = OrderBalance.AmountDue(order);
                var remainder = OrderPricingCalculator.Round2(due - item.Amount);

                if (OrderBalance.SettlesOrder(remainder))
                {
                    // ONE conditional UPDATE, pinned to the figures the share was computed from.
                    // MySQL evaluates SET left to right, so the snapshot conditions read
                    // InitialTotal, which is assigned LAST. Pending → Active only; Done stays Done.
                    var expectedTotal = order.Total;
                    var expectedPaid = order.AmountPaid;
                    var updated = await _context.Orders
                        .Where(o => o.Id == order.Id && !o.IsPaid && o.InvoicePaidAt == null
                                    && o.PaymentMethod == PaymentMethod.Normal
                                    && o.Total == expectedTotal && o.AmountPaid == expectedPaid)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(o => o.IsPaid, true)
                            .SetProperty(o => o.PaidAt, now)
                            .SetProperty(o => o.Status, o => o.Status == OrderStatuses.Pending ? OrderStatuses.Active : o.Status)
                            .SetProperty(o => o.InitialSubTotal, o => o.InitialTotal == 0 ? o.SubTotal : o.InitialSubTotal)
                            .SetProperty(o => o.InitialTax, o => o.InitialTotal == 0 ? o.Tax : o.InitialTax)
                            .SetProperty(o => o.InitialTips, o => o.InitialTotal == 0 ? o.Tips : o.InitialTips)
                            .SetProperty(o => o.InitialCompanyDevelopmentTips, o => o.InitialTotal == 0 ? o.CompanyDevelopmentTips : o.InitialCompanyDevelopmentTips)
                            .SetProperty(o => o.InitialTotal, o => o.InitialTotal == 0 ? o.Total : o.InitialTotal)
                            .SetProperty(o => o.UpdatedAt, now));

                    if (updated == 0)
                        throw new BatchSettlementConflictException($"Order #{order.Id} changed while combined payment #{batch.Id} was being recorded.");

                    if (remainder <= -OrderBalance.MinimumMeaningfulAmount)
                        notes.Add($"order #{order.Id} was charged ${item.Amount:F2} but owed ${due:F2} (${-remainder:F2} needs refunding)");
                }
                else
                {
                    // The price rose after the customer authorised. Credit what was actually paid
                    // and leave the rest owing — never mark it paid for money that did not arrive.
                    var credited = await _partialPayments.RecordCombinedPaymentSliceAsync(
                        order.Id, item.Amount, order.Total, order.AmountPaid, paymentIntentId, batch.Id);
                    if (!credited)
                        throw new BatchSettlementConflictException($"Order #{order.Id} changed while combined payment #{batch.Id} was being recorded.");

                    notes.Add($"order #{order.Id} now costs more than when it was paid: ${item.Amount:F2} applied, ${remainder:F2} still due");
                }

                item.AppliedToOrder = true;
            }

            batch.SettlementWarning = notes.Count == 0 ? null : Truncate(string.Join("; ", notes) + ".", 500);
        }

        /// <summary>
        /// Best effort, never throws: a batch whose charge succeeded but whose recording failed is
        /// parked in Processing. That status is what every payment-start path reads as "in
        /// flight", so the cleanings cannot be offered for payment again while the retry is owed.
        /// Stripe's own record (a succeeded intent cannot be cancelled) is the second guard behind
        /// it, for the case where even this write fails.
        /// </summary>
        private async Task DeferSettlementAsync(int batchId)
        {
            try
            {
                await _context.OrderPaymentBatches
                    .Where(b => b.Id == batchId && b.Status != OrderPaymentBatchStatus.Paid)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.Status, OrderPaymentBatchStatus.Processing)
                        .SetProperty(b => b.FailureReason, SettlementDeferredReason)
                        .SetProperty(b => b.UpdatedAt, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not park combined-payment batch {BatchId} for retry.", batchId);
            }
        }

        private void DetachBatch(OrderPaymentBatch? batch)
        {
            if (batch == null) return;
            foreach (var item in batch.Items.ToList())
                _context.Entry(item).State = EntityState.Detached;
            _context.Entry(batch).State = EntityState.Detached;
        }

        private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

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
            if (intent.Status == "succeeded")
            {
                // The charge happened. A failure to RECORD it is never reported as a failed
                // payment — SettleBatchAsync has already parked the batch as in flight, and the
                // webhook retry or the reconciler will finish it. Nobody is told to pay again.
                try { await SettleBatchAsync(paymentIntentId, batch.Id); }
                catch (Exception ex) when (ex is not CombinedPaymentException)
                {
                    _logger.LogWarning(ex, "Combined payment {PaymentIntentId} settlement deferred.", paymentIntentId);
                }
            }
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

        public async Task<int> ReconcileUnsettledBatchesAsync(CancellationToken ct = default)
        {
            // Processing (submitted, or charged-but-unrecorded) is checked for a month; an
            // abandoned Pending/Failed intent is checked for two days, which covers a customer
            // confirming late or a webhook that never arrived without polling Stripe forever.
            var now = DateTime.UtcNow;
            var candidates = await _context.OrderPaymentBatches.AsNoTracking()
                .Where(b => b.PaymentIntentId != null
                            && ((b.Status == OrderPaymentBatchStatus.Processing && b.CreatedAt > now.AddDays(-30))
                                || ((b.Status == OrderPaymentBatchStatus.Pending || b.Status == OrderPaymentBatchStatus.Failed)
                                    && b.UpdatedAt > now.AddDays(-2) && b.CreatedAt < now.AddMinutes(-2))))
                .OrderBy(b => b.Id)
                .Select(b => new { b.Id, b.PaymentIntentId })
                .Take(200)
                .ToListAsync(ct);

            var settled = 0;
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await RefreshBatchStateAsync(candidate.PaymentIntentId!);
                    settled += await _context.OrderPaymentBatches.AsNoTracking()
                        .CountAsync(b => b.Id == candidate.Id && b.Status == OrderPaymentBatchStatus.Paid, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Combined-payment batch {BatchId} could not be reconciled this pass.", candidate.Id);
                }
                finally
                {
                    _context.ChangeTracker.Clear();
                }
            }
            return settled;
        }

        public async Task SettleSucceededBatchesAsync(int userId)
        {
            if (_context.Database.CurrentTransaction != null) return; // would not commit on its own
            await RefreshCustomerBatchesAsync(userId, failClosed: false);
        }

        /// <summary>Returns the orders of batches that could NOT be checked (only when
        /// <paramref name="failClosed"/> is false; otherwise the failure is thrown).</summary>
        private async Task<HashSet<int>> RefreshCustomerBatchesAsync(int userId, bool failClosed)
        {
            var batches = await _context.OrderPaymentBatches.AsNoTracking().Where(b => b.UserId == userId
                && b.Status != OrderPaymentBatchStatus.Paid && b.Status != OrderPaymentBatchStatus.Canceled
                && b.PaymentIntentId != null).Select(b => new { b.Id, b.PaymentIntentId }).ToListAsync();

            var unverified = new HashSet<int>();
            foreach (var batch in batches)
            {
                try
                {
                    await RefreshBatchStateAsync(batch.PaymentIntentId!);
                }
                catch (Exception ex) when (!failClosed)
                {
                    _logger.LogWarning(ex, "Combined-payment batch {BatchId} could not be verified; its cleanings are held.", batch.Id);
                    var ids = await _context.OrderPaymentBatchItems.AsNoTracking()
                        .Where(i => i.OrderPaymentBatchId == batch.Id).Select(i => i.OrderId).ToListAsync();
                    unverified.UnionWith(ids);
                }
            }
            return unverified;
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
            await RefreshCustomerBatchesAsync(order.UserId, failClosed: true);
            // Settlement writes with conditional UPDATEs, not through the tracker, so the tracked
            // instance (the caller's, too) must be re-read before "is it already paid?" is asked.
            await _context.Entry(order).ReloadAsync();
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
            AmountDue = o.PaymentMethod == PaymentMethod.Normal ? OrderBalance.AmountDue(o) : 0m,
            IsCancelled = OrderStatuses.IsCancelled(o.Status) || OrderStatuses.IsRefunded(o.Status)
        };

        private async Task MarkInFlightAsync(List<RecurringPayableOccurrence> occurrences, ICollection<int>? unverifiedOrderIds = null)
        {
            var ids = occurrences.Select(o => o.OrderId).ToList();
            var busy = await _context.OrderPaymentBatchItems.Where(i => ids.Contains(i.OrderId)
                && i.Batch!.Status == OrderPaymentBatchStatus.Processing).Select(i => i.OrderId).ToListAsync();

            // A saved-card charge (AutoPay / admin) holding one of these cleanings is a payment in
            // flight too — a combined payment must not be assembled around it (2026-09).
            var lockKeys = ids.Select(Models.Billing.BillingPaymentAttempt.OrderObligationKey).ToList();
            var savedCardBusy = await _context.BillingPaymentAttempts
                .Where(a => a.ActiveLockKey != null && lockKeys.Contains(a.ActiveLockKey) && a.OrderId != null)
                .Select(a => a.OrderId!.Value)
                .ToListAsync();
            busy.AddRange(savedCardBusy);
            if (unverifiedOrderIds != null) busy.AddRange(unverifiedOrderIds);
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
