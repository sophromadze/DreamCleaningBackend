using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Stripe;
using PaymentMethod = DreamCleaningBackend.Models.PaymentMethod;

namespace DreamCleaningBackend.Services.Billing
{
    public interface ISavedCardChargeService
    {
        /// <summary>Charges an order's balance due with the owner's saved card(s).</summary>
        Task<SavedCardChargeResponseDto> ChargeOrderAsync(int orderId, BillingAttemptTrigger trigger,
            int? initiatedByUserId, CancellationToken ct = default);

        /// <summary>Charges a commercial invoice's balance with the linked account holder's card.</summary>
        Task<SavedCardChargeResponseDto> ChargeInvoiceAsync(int invoiceId, int payerUserId, BillingAttemptTrigger trigger,
            int? selectedCardId, int? initiatedByUserId, CancellationToken ct = default);

        /// <summary>True while a saved-card charge could still move money for this obligation.</summary>
        Task<bool> HasActiveAttemptAsync(string obligationKey);

        /// <summary>Resolves attempts whose outcome is not yet known. Worker only.</summary>
        Task<int> ReconcileStaleAttemptsAsync(CancellationToken ct);

        /// <summary>A payment_intent.* webhook for an intent this service created.</summary>
        Task HandleStripeIntentEventAsync(PaymentIntent intent, string eventType);

        /// <summary>After a customer-present 3DS challenge: re-read the intent and apply it.</summary>
        Task<SavedCardChargeResponseDto> RefreshCustomerAttemptAsync(int userId, string paymentIntentId);

        /// <summary>Everything the admin "Charge saved card" button needs to decide what to show.</summary>
        Task<AdminOrderSavedCardInfoDto> GetAdminOrderChargeInfoAsync(int orderId);
    }

    /// <summary>
    /// EVERY SERVER-INITIATED SAVED-CARD CHARGE GOES THROUGH HERE — the admin "Charge saved card"
    /// button, recurring AutoPay, commercial AutoPay, and a customer paying an invoice with a
    /// saved card. One implementation, so the rules below hold on every path at once.
    ///
    /// ══ WHAT PREVENTS A DOUBLE CHARGE ══
    ///  1. <b>The lock.</b> An attempt row carrying <c>ActiveLockKey = order:{id}</c> is inserted
    ///     BEFORE Stripe is called; the unique index refuses a second one. Two admins, an admin and
    ///     AutoPay, two workers: exactly one gets to charge.
    ///  2. <b>The customer's own checkout is serialised against it.</b> The lock is taken inside the
    ///     same Users-row lock (<c>RecurringPaymentAttemptGuard.LockAsync</c>) that
    ///     create-payment-intent now takes, and create-payment-intent refuses while a lock exists.
    ///     Any intent the customer already holds is CANCELLED at Stripe before we charge — or, if
    ///     Stripe says it is already paying, we stop. Stripe arbitrates a simultaneous confirm and
    ///     cancel, so exactly one of them wins.
    ///  3. <b>Stable idempotency.</b> The key is derived from the attempt row, never the clock. A
    ///     retry after a timeout returns the SAME intent.
    ///  4. <b>An Unknown outcome stops everything.</b> A timeout does not prove a failure: no Backup,
    ///     no new key, the lock stays held, and the reconciler asks Stripe what really happened.
    ///  5. <b>Settlement is conditional.</b> The order is marked paid by one UPDATE … WHERE NOT
    ///     IsPaid. If some other path paid it first after all, this charge is refunded automatically.
    ///
    /// ══ AMOUNTS ══
    /// Always <see cref="OrderBalance.AmountDue"/> (orders) or <c>BalanceDue</c> (invoices), read
    /// fresh inside the lock — never <c>Order.Total</c>, which re-took part-payments (audit finding).
    ///
    /// ══ PRIMARY → BACKUP ══
    /// Only when the authorisation allows it, only after a DEFINITE failure of the Primary (decline
    /// or authentication required — the intent of the latter is cancelled first), and as a second
    /// attempt on the SAME obligation and run. A Backup attempt goes through every guard again.
    /// </summary>
    public class SavedCardChargeService : ISavedCardChargeService
    {
        private readonly ApplicationDbContext _context;
        private readonly IStripeService _stripe;
        private readonly IPaymentAuthorizationService _authorizations;
        private readonly IPaymentMethodService _cards;
        private readonly IBillingNotificationService _notifications;
        private readonly IAuditService _audit;
        private readonly BillingFeatures _features;
        private readonly IServiceProvider _services;
        private readonly ILogger<SavedCardChargeService> _logger;

        public const string OrderMetadataType = "billing_order";
        public const string AttemptIdMetadataKey = "billing_attempt_id";
        public const string RunMetadataKey = "billing_run";

        private const decimal StripeMinimumChargeAmount = 0.50m;

        public SavedCardChargeService(
            ApplicationDbContext context,
            IStripeService stripe,
            IPaymentAuthorizationService authorizations,
            IPaymentMethodService cards,
            IBillingNotificationService notifications,
            IAuditService audit,
            BillingFeatures features,
            IServiceProvider services,
            ILogger<SavedCardChargeService> logger)
        {
            _context = context;
            _stripe = stripe;
            _authorizations = authorizations;
            _cards = cards;
            _notifications = notifications;
            _audit = audit;
            _features = features;
            _services = services;
            _logger = logger;
        }

        // ═════════════════════════════════════════════════════════════════════════════════════
        //  ORDERS
        // ═════════════════════════════════════════════════════════════════════════════════════

        public async Task<SavedCardChargeResponseDto> ChargeOrderAsync(int orderId, BillingAttemptTrigger trigger,
            int? initiatedByUserId, CancellationToken ct = default)
        {
            if (!_features.SavedCardsEnabled)
                return Response("not_authorized", "Saved-card payments are switched off.");
            if (trigger == BillingAttemptTrigger.AutoPayRecurring && !_features.AutoPayEnabled)
                return Response("not_authorized", "Automatic payments are switched off.");

            var order = await _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order == null) return Response("blocked", "Order not found.");

            var blocked = CheckOrderPayable(order, out var amountDue);
            if (blocked != null) return blocked;

            var authorization = trigger switch
            {
                BillingAttemptTrigger.AdminCharge => await _authorizations.ResolveEffectiveAsync(
                    order.UserId, PaymentAuthorizationScope.OfficeBookedOrders),
                BillingAttemptTrigger.AutoPayRecurring when order.RecurringSeriesId.HasValue => await _authorizations.ResolveEffectiveAsync(
                    order.UserId, PaymentAuthorizationScope.RecurringSeries, seriesId: order.RecurringSeriesId),
                _ => null
            };

            if (authorization == null)
            {
                return Response("not_authorized", trigger == BillingAttemptTrigger.AdminCharge
                    ? "This customer has not authorized card charges for orders booked by our office. Send them a payment link instead."
                    : "No active automatic-payment authorization covers this order.", amountDue);
            }

            var run = new ChargeRun
            {
                RunKey = Guid.NewGuid().ToString("N"),
                ObligationType = BillingObligationType.Order,
                ObligationKey = BillingPaymentAttempt.OrderObligationKey(orderId),
                OrderId = orderId,
                UserId = order.UserId,
                Trigger = trigger,
                InitiatedByUserId = initiatedByUserId,
                AuthorizationId = authorization.Authorization.Id,
                AllowBackup = authorization.AllowBackupFallback
            };

            return await RunAsync(run, selectedCardId: null, ct);
        }

        /// <summary>The reasons an order can never be charged here, checked before anything else.</summary>
        private static SavedCardChargeResponseDto? CheckOrderPayable(Order order, out decimal amountDue)
        {
            amountDue = OrderBalance.AmountDue(order);

            if (order.IsPaid || order.InvoicePaidAt != null)
                return Response("nothing_due", "This order is already paid.");
            // Cash / Zelle / Check / Other were settled outside the website, and Invoice orders are
            // paid through their commercial invoice — never charge them individually.
            if (order.PaymentMethod != PaymentMethod.Normal)
                return Response("nothing_due", order.PaymentMethod == PaymentMethod.Invoice
                    ? "This order is billed through a commercial invoice."
                    : "This order was recorded as paid outside the website — nothing to charge.");
            if (OrderStatuses.IsCancelled(order.Status) || OrderStatuses.IsRefunded(order.Status))
                return Response("nothing_due", "This order is cancelled — nothing to charge.");
            if (amountDue < StripeMinimumChargeAmount)
                return Response("nothing_due", "Nothing is owed on this order.", amountDue);
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════════════════════
        //  COMMERCIAL INVOICES
        // ═════════════════════════════════════════════════════════════════════════════════════

        public async Task<SavedCardChargeResponseDto> ChargeInvoiceAsync(int invoiceId, int payerUserId, BillingAttemptTrigger trigger,
            int? selectedCardId, int? initiatedByUserId, CancellationToken ct = default)
        {
            if (!_features.SavedCardsEnabled)
                return Response("not_authorized", "Saved-card payments are switched off.");
            if (trigger == BillingAttemptTrigger.AutoPayCommercial && !_features.AutoPayEnabled)
                return Response("not_authorized", "Automatic payments are switched off.");

            var invoice = await _context.CommercialInvoices.AsNoTracking()
                .Include(i => i.Client)
                .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
            if (invoice == null) return Response("blocked", "Invoice not found.");

            // The payer must be the account the business client is linked to — the existing
            // ownership rule for "My Invoices". Nobody else's card may pay a company's invoice.
            if (invoice.Client == null || invoice.Client.SourceUserId != payerUserId || !invoice.Client.IsActive)
                return Response("blocked", "Invoice not found.");

            var blocked = CheckInvoicePayable(invoice);
            if (blocked != null) return blocked;

            var settings = await _services.GetRequiredService<BillingSettingsService>().GetOrCreateAsync(ct);
            if (!settings.StripeCardEnabled)
                return Response("blocked", "Card payments for invoices are currently unavailable. Please use bank transfer.");

            int? authorizationId = null;
            var allowBackup = false;
            if (trigger == BillingAttemptTrigger.AutoPayCommercial)
            {
                var authorization = await _authorizations.ResolveEffectiveAsync(payerUserId,
                    PaymentAuthorizationScope.CommercialClient, clientId: invoice.ContractClientId);
                if (authorization == null)
                    return Response("not_authorized", "No active automatic-payment authorization covers this invoice.");
                authorizationId = authorization.Authorization.Id;
                allowBackup = authorization.AllowBackupFallback;
            }
            else if (trigger != BillingAttemptTrigger.CustomerSavedCard)
            {
                return Response("not_authorized", "Invoices can only be charged by the customer or their AutoPay.");
            }

            var run = new ChargeRun
            {
                RunKey = Guid.NewGuid().ToString("N"),
                ObligationType = BillingObligationType.CommercialInvoice,
                ObligationKey = BillingPaymentAttempt.InvoiceObligationKey(invoiceId),
                InvoiceId = invoiceId,
                UserId = payerUserId,
                Trigger = trigger,
                InitiatedByUserId = initiatedByUserId,
                AuthorizationId = authorizationId,
                AllowBackup = allowBackup
            };

            return await RunAsync(run, selectedCardId, ct);
        }

        private static SavedCardChargeResponseDto? CheckInvoicePayable(CommercialInvoice invoice)
        {
            if (invoice.Status is InvoiceStatus.Draft)
                return Response("blocked", "This invoice has not been issued yet.");
            if (invoice.Status is InvoiceStatus.Void)
                return Response("nothing_due", "This invoice was cancelled.");
            if (invoice.Status is InvoiceStatus.Paid || invoice.BalanceDue < StripeMinimumChargeAmount)
                return Response("nothing_due", "This invoice is already paid.");
            if (!string.Equals(invoice.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                return Response("blocked", "Only USD invoices can be paid by card.");
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════════════════════
        //  THE RUN: Primary, then (maybe) Backup
        // ═════════════════════════════════════════════════════════════════════════════════════

        private sealed class ChargeRun
        {
            public string RunKey { get; set; } = string.Empty;
            public BillingObligationType ObligationType { get; set; }
            public string ObligationKey { get; set; } = string.Empty;
            public int? OrderId { get; set; }
            public int? InvoiceId { get; set; }
            public int UserId { get; set; }
            public BillingAttemptTrigger Trigger { get; set; }
            public int? InitiatedByUserId { get; set; }
            public int? AuthorizationId { get; set; }
            public bool AllowBackup { get; set; }
        }

        private async Task<SavedCardChargeResponseDto> RunAsync(ChargeRun run, int? selectedCardId, CancellationToken ct)
        {
            var roles = await _context.Users.AsNoTracking()
                .Where(u => u.Id == run.UserId)
                .Select(u => new { u.PrimaryPaymentMethodId, u.BackupPaymentMethodId })
                .FirstOrDefaultAsync(ct);
            if (roles == null) return Response("blocked", "Customer not found.");

            // The first card: one the customer picked (customer-present invoice payment), else the Primary.
            var firstRole = selectedCardId.HasValue ? BillingCardRole.Selected : BillingCardRole.Primary;
            var first = await _cards.GetUsableCardAsync(run.UserId, selectedCardId ?? roles.PrimaryPaymentMethodId);
            var primaryLabel = await LabelForAsync(selectedCardId ?? roles.PrimaryPaymentMethodId);

            AttemptResult firstResult;
            if (first == null)
            {
                // No usable Primary (none, expired, blocked by the issuer). Nothing is charged; it
                // counts as a definite failure of the first card so an authorised Backup can step in.
                if (selectedCardId.HasValue)
                    return Response("blocked", "That card can't be used. Please choose another card.");
                firstResult = AttemptResult.NoCard(primaryLabel);
            }
            else
            {
                firstResult = await AttemptAsync(run, first, firstRole, sequence: 1, ct);
            }

            var finalResult = firstResult;

            if (firstResult.IsDefiniteFailure && run.AllowBackup && !selectedCardId.HasValue
                && roles.BackupPaymentMethodId.HasValue && roles.BackupPaymentMethodId != first?.Id)
            {
                var backup = await _cards.GetUsableCardAsync(run.UserId, roles.BackupPaymentMethodId);
                if (backup != null)
                {
                    var backupResult = await AttemptAsync(run, backup, BillingCardRole.Backup, sequence: 2, ct);

                    // A Backup that never reached Stripe (something else started paying, or the
                    // obligation stopped being owed) does not replace the Primary's outcome — but a
                    // concurrent continuation of this same run owns the rest of it.
                    if (backupResult.ConcurrentRun) return ToResponse(run, firstResult, backupResult);
                    if (backupResult.AttemptId != null || backupResult.NothingDue) finalResult = backupResult;
                }
            }

            await NotifyRunAsync(run, firstResult, finalResult, primaryLabel, finalResult.CardLabel);

            // A run whose outcome is still open is NOT finished: the webhook / reconciler will
            // continue it (Backup, notifications) once Stripe answers.
            if (finalResult.Status is not (BillingAttemptStatus.Unknown or BillingAttemptStatus.Processing or BillingAttemptStatus.Pending))
                await MarkRunFinalizedAsync(run.RunKey);

            return ToResponse(run, firstResult, finalResult);
        }

        private async Task MarkRunFinalizedAsync(string runKey)
        {
            try
            {
                await _context.BillingPaymentAttempts
                    .Where(a => a.RunKey == runKey && a.Sequence == 1 && a.RunFinalizedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.RunFinalizedAt, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not mark run {RunKey} finalized.", runKey);
            }
        }

        private sealed class AttemptResult
        {
            public BillingAttemptStatus Status { get; init; }

            /// <summary>Stopped before Stripe was called (lock held elsewhere, customer paying, etc.).</summary>
            public string? BlockedReason { get; init; }
            public bool NothingDue { get; init; }
            public bool NoUsableCard { get; init; }

            /// <summary>Another path (a webhook, the reconciler) is already continuing this run.</summary>
            public bool ConcurrentRun { get; init; }
            public int? AttemptId { get; init; }
            public string? PaymentIntentId { get; init; }
            public string? ClientSecret { get; init; }
            public decimal Amount { get; init; }
            public string? CardLabel { get; init; }
            public string? FailureCode { get; init; }
            public string? DeclineCode { get; init; }

            /// <summary>A failure that PROVES no money moved — the only kind a Backup may follow.</summary>
            public bool IsDefiniteFailure =>
                NoUsableCard || Status is BillingAttemptStatus.Failed or BillingAttemptStatus.RequiresAction;

            public static AttemptResult NoCard(string? label) => new()
            {
                Status = BillingAttemptStatus.Failed, NoUsableCard = true, CardLabel = label,
                FailureCode = "no_usable_card"
            };
        }

        /// <summary>
        /// ONE attempt on ONE card, through every guard. Returns without calling Stripe when
        /// anything says it should not charge right now.
        /// </summary>
        private async Task<AttemptResult> AttemptAsync(ChargeRun run, CustomerPaymentMethod card, BillingCardRole role,
            int sequence, CancellationToken ct)
        {
            var label = PaymentMethodService.Label(card);

            // ── 1. Take the lock, inside the customer's row lock ─────────────────────────────
            var (attempt, amount, pre) = await AcquireLockAsync(run, card, role, sequence, ct);
            if (attempt == null) return pre!;

            // ── 2. Make sure nothing else can still collect this money ───────────────────────
            var conflict = run.ObligationType == BillingObligationType.Order
                ? await NeutraliseOpenOrderIntentAsync(run.OrderId!.Value, ct)
                : await NeutraliseOpenInvoiceCheckoutAsync(run.InvoiceId!.Value, ct);
            if (conflict != null)
            {
                await ReleaseAsync(attempt.Id, BillingAttemptStatus.Canceled, "blocked", conflict);
                return new AttemptResult { Status = BillingAttemptStatus.Canceled, BlockedReason = conflict, CardLabel = label, Amount = amount };
            }

            // Commercial: the ledger's own attempt row goes in BEFORE the charge, as Processing, so
            // the public invoice page's "a payment is already being processed" guard sees it.
            int? ledgerAttemptId = null;
            if (run.ObligationType == BillingObligationType.CommercialInvoice)
            {
                var ledger = new CommercialInvoicePaymentAttempt
                {
                    CommercialInvoiceId = run.InvoiceId!.Value,
                    Provider = InvoicePaymentProvider.Stripe,
                    PaymentMethod = InvoicePaymentRecordMethod.Card,
                    Amount = amount,
                    ProcessingFee = 0m,   // cards carry no fee (AchProcessingFeeCalculator)
                    TotalCharged = amount,
                    Currency = "USD",
                    Status = InvoicePaymentAttemptStatus.Processing,
                    PaymentSourceLabel = label,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.CommercialInvoicePaymentAttempts.Add(ledger);
                await _context.SaveChangesAsync(ct);
                ledgerAttemptId = ledger.Id;

                await _context.BillingPaymentAttempts.Where(a => a.Id == attempt.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.CommercialInvoicePaymentAttemptId, ledgerAttemptId), ct);
                attempt.CommercialInvoicePaymentAttemptId = ledgerAttemptId;
            }

            // ── 3. Charge ─────────────────────────────────────────────────────────────────────
            var request = await BuildChargeRequestAsync(attempt, run, card, ct);
            var result = await _stripe.ChargeSavedCardAsync(request);

            // A timeout proves nothing. Ask again with the SAME key: Stripe answers with the intent
            // the first request created (or creates it now, exactly once).
            if (result.Outcome == SavedCardChargeOutcome.Unknown)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                result = await _stripe.ChargeSavedCardAsync(request);
            }

            // ── 4. Record what happened ───────────────────────────────────────────────────────
            var status = await ApplyOutcomeAsync(attempt.Id, result, ct);

            return new AttemptResult
            {
                Status = status,
                AttemptId = attempt.Id,
                PaymentIntentId = result.PaymentIntentId,
                ClientSecret = status == BillingAttemptStatus.RequiresAction && !request.OffSession ? result.ClientSecret : null,
                Amount = amount,
                CardLabel = label,
                FailureCode = result.FailureCode,
                DeclineCode = result.DeclineCode
            };
        }

        /// <summary>
        /// Re-reads the obligation and inserts the attempt row that IS the lock — all inside the
        /// customer's Users-row lock, the same one create-payment-intent takes, so the customer's
        /// own checkout and this charge can never both be "first".
        /// </summary>
        private async Task<(BillingPaymentAttempt? Attempt, decimal Amount, AttemptResult? Blocked)> AcquireLockAsync(
            ChargeRun run, CustomerPaymentMethod card, BillingCardRole role, int sequence, CancellationToken ct)
        {
            var label = PaymentMethodService.Label(card);

            await using var transaction = await RecurringPaymentAttemptGuard.LockAsync(_context, run.UserId);

            decimal amount;
            if (run.ObligationType == BillingObligationType.Order)
            {
                var order = await _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == run.OrderId, ct);
                if (order == null)
                    return (null, 0m, new AttemptResult { Status = BillingAttemptStatus.Canceled, BlockedReason = "Order not found.", CardLabel = label });

                var stop = CheckOrderPayable(order, out amount);
                if (stop != null)
                    return (null, amount, new AttemptResult { Status = BillingAttemptStatus.Canceled, NothingDue = stop.Result == "nothing_due",
                        BlockedReason = stop.Message, CardLabel = label, Amount = amount });

                // A part-payment request is an agreed slice of THIS money; charging the whole
                // balance underneath it would contradict what the customer was asked for.
                var openPartial = await _context.OrderPartialPayments.AnyAsync(p =>
                    p.OrderId == order.Id && p.Status == OrderPartialPaymentStatus.Pending, ct);
                if (openPartial)
                    return (null, amount, new AttemptResult { Status = BillingAttemptStatus.Canceled, CardLabel = label, Amount = amount,
                        BlockedReason = "A part-payment request is open for this order. Cancel it first, or let the customer pay it." });

                // A "Pay all upcoming" batch that may still be paying for this order.
                var batchInFlight = await _context.OrderPaymentBatchItems.AnyAsync(i =>
                    i.OrderId == order.Id
                    && (i.Batch!.Status == OrderPaymentBatchStatus.Processing
                        || (i.Batch.Status == OrderPaymentBatchStatus.Pending && i.Batch.CreatedAt > DateTime.UtcNow.AddHours(-1))), ct);
                if (batchInFlight)
                    return (null, amount, new AttemptResult { Status = BillingAttemptStatus.Canceled, CardLabel = label, Amount = amount,
                        BlockedReason = "The customer is paying for this cleaning together with others right now." });
            }
            else
            {
                var invoice = await _context.CommercialInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == run.InvoiceId, ct);
                if (invoice == null)
                    return (null, 0m, new AttemptResult { Status = BillingAttemptStatus.Canceled, BlockedReason = "Invoice not found.", CardLabel = label });
                var stop = CheckInvoicePayable(invoice);
                amount = invoice.BalanceDue;
                if (stop != null)
                    return (null, amount, new AttemptResult { Status = BillingAttemptStatus.Canceled, NothingDue = stop.Result == "nothing_due",
                        BlockedReason = stop.Message, CardLabel = label, Amount = amount });

                // An ACH debit still settling may yet pay this invoice.
                var settling = await _context.CommercialInvoicePaymentAttempts.AnyAsync(a =>
                    a.CommercialInvoiceId == invoice.Id && a.Status == InvoicePaymentAttemptStatus.Processing, ct);
                if (settling)
                    return (null, amount, new AttemptResult { Status = BillingAttemptStatus.Canceled, CardLabel = label, Amount = amount,
                        BlockedReason = "A payment for this invoice is already being processed." });
            }

            var attempt = new BillingPaymentAttempt
            {
                ObligationKey = run.ObligationKey,
                ObligationType = run.ObligationType,
                OrderId = run.OrderId,
                CommercialInvoiceId = run.InvoiceId,
                UserId = run.UserId,
                RunKey = run.RunKey,
                Sequence = sequence,
                Trigger = run.Trigger,
                CardRole = role,
                CustomerPaymentMethodId = card.Id,
                StripePaymentMethodId = card.StripePaymentMethodId,
                CardBrand = card.Brand,
                CardLast4 = card.Last4,
                PaymentAuthorizationId = run.AuthorizationId,
                InitiatedByUserId = run.InitiatedByUserId,
                Amount = amount,
                // Random, not derived from the row id: ids restart in every fresh database (a reset dev
                // DB, a restored clone, a test run) while the Stripe account and its 24-hour key memory
                // do not, and a reused key with different parameters is refused. Stable for the life of
                // the attempt all the same — every retry reads it back from this row.
                IdempotencyKey = $"dc-billing-attempt-{Guid.NewGuid():N}",
                Status = BillingAttemptStatus.Pending,
                ActiveLockKey = run.ObligationKey,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.BillingPaymentAttempts.Add(attempt);
            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _context.Entry(attempt).State = EntityState.Detached;

                // Which index refused it? The run/sequence one means this very run is being
                // continued elsewhere; the lock means another run holds the obligation.
                var sameRunStep = await _context.BillingPaymentAttempts.AsNoTracking()
                    .AnyAsync(a => a.RunKey == run.RunKey && a.Sequence == sequence, ct);

                return (null, amount, new AttemptResult { Status = BillingAttemptStatus.Canceled, CardLabel = label, Amount = amount,
                    ConcurrentRun = sameRunStep,
                    BlockedReason = "A payment for this is already being processed. Please wait a moment and refresh." });
            }

            if (transaction != null) await transaction.CommitAsync(ct);
            _context.Entry(attempt).State = EntityState.Detached;

            return (attempt, amount, null);
        }

        /// <summary>
        /// The customer may be holding a client secret for this order from the payment page.
        /// Cancel it at Stripe before charging — or stop, if Stripe says that payment is already
        /// on its way. Returns the reason to stop, or null to go ahead.
        /// </summary>
        private async Task<string?> NeutraliseOpenOrderIntentAsync(int orderId, CancellationToken ct)
        {
            var intentId = await _context.Orders.AsNoTracking()
                .Where(o => o.Id == orderId).Select(o => o.PaymentIntentId).FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(intentId) || !intentId.StartsWith("pi_")) return null;

            try
            {
                await RecurringPaymentAttemptGuard.CancelOpenAsync(_stripe, intentId);
                return null;
            }
            catch (CombinedPaymentException ex)
            {
                return ex.Message.Contains("already being processed")
                    ? "The customer is already paying for this order online. Wait for that payment to finish."
                    : "The customer's own payment for this order could not be safely replaced. Please try again shortly.";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not verify the open payment intent on order {OrderId}.", orderId);
                return "Could not verify the customer's own payment for this order. Please try again shortly.";
            }
        }

        /// <summary>Same idea for an invoice: expire any open Checkout Session, unless it completed.</summary>
        private async Task<string?> NeutraliseOpenInvoiceCheckoutAsync(int invoiceId, CancellationToken ct)
        {
            var open = await _context.CommercialInvoicePaymentAttempts
                .Where(a => a.CommercialInvoiceId == invoiceId && a.Status == InvoicePaymentAttemptStatus.CheckoutOpen)
                .ToListAsync(ct);

            foreach (var ledger in open)
            {
                if (string.IsNullOrWhiteSpace(ledger.StripeCheckoutSessionId)) continue;

                var state = await _stripe.GetCheckoutSessionStateAsync(ledger.StripeCheckoutSessionId);
                if (state == null) return "Could not verify an open online payment for this invoice. Please try again shortly.";
                if (state.Status == "complete" || state.PaymentStatus == "paid")
                    return "A payment for this invoice was just made online and is being recorded.";

                if (state.Status == "open" && !await _stripe.ExpireCheckoutSessionAsync(ledger.StripeCheckoutSessionId))
                {
                    var again = await _stripe.GetCheckoutSessionStateAsync(ledger.StripeCheckoutSessionId);
                    if (again == null || again.Status != "expired")
                        return "An online payment for this invoice is in progress. Please try again shortly.";
                }

                ledger.Status = InvoicePaymentAttemptStatus.Expired;
                ledger.FailureMessage = "Replaced by a saved-card payment.";
                ledger.CompletedAt = DateTime.UtcNow;
                ledger.UpdatedAt = DateTime.UtcNow;
            }

            if (open.Count > 0) await _context.SaveChangesAsync(ct);
            return null;
        }

        private async Task<SavedCardChargeRequest> BuildChargeRequestAsync(BillingPaymentAttempt attempt, ChargeRun? run,
            CustomerPaymentMethod card, CancellationToken ct)
        {
            var metadata = new Dictionary<string, string>
            {
                [AttemptIdMetadataKey] = attempt.Id.ToString(),
                [RunMetadataKey] = attempt.RunKey,
                ["userId"] = attempt.UserId.ToString(),
                ["trigger"] = attempt.Trigger.ToString()
            };

            string? receiptEmail = null;
            string description;

            if (attempt.ObligationType == BillingObligationType.Order)
            {
                metadata["type"] = OrderMetadataType;
                metadata["orderId"] = attempt.OrderId!.Value.ToString();
                var order = await _context.Orders.AsNoTracking().Include(o => o.User)
                    .FirstOrDefaultAsync(o => o.Id == attempt.OrderId, ct);
                receiptEmail = order == null ? null : NoEmailHelper.ResolveOrderNotificationEmail(order.ContactEmail, order.User);
                description = $"Dream Cleaning NYC order #{attempt.OrderId}";
            }
            else
            {
                var invoice = await _context.CommercialInvoices.AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Id == attempt.CommercialInvoiceId, ct);
                metadata["type"] = StripeCommercialInvoiceMetadata.TypeValue;
                metadata[StripeCommercialInvoiceMetadata.InvoiceIdKey] = attempt.CommercialInvoiceId!.Value.ToString();
                metadata[StripeCommercialInvoiceMetadata.AttemptIdKey] = attempt.CommercialInvoicePaymentAttemptId?.ToString() ?? "";
                metadata[StripeCommercialInvoiceMetadata.InvoiceNumberKey] = invoice?.InvoiceNumber ?? "";
                metadata[StripeCommercialInvoiceMetadata.ClientIdKey] = invoice?.ContractClientId.ToString() ?? "";
                metadata[StripeCommercialInvoiceMetadata.BaseAmountKey] = attempt.Amount.ToString("0.00");
                metadata[StripeCommercialInvoiceMetadata.ProcessingFeeKey] = "0.00";
                description = $"Dream Cleaning NYC invoice {invoice?.InvoiceNumber}";
            }

            return new SavedCardChargeRequest
            {
                Amount = attempt.Amount,
                CustomerId = card.StripeCustomerId,
                PaymentMethodId = card.StripePaymentMethodId,
                Metadata = metadata,
                IdempotencyKey = attempt.IdempotencyKey,
                ReceiptEmail = receiptEmail,
                Description = description,
                // The customer is present (and can answer a 3DS challenge) only when they pressed
                // Pay themselves. Everything else is merchant-initiated.
                OffSession = attempt.Trigger != BillingAttemptTrigger.CustomerSavedCard
            };
        }

        // ═════════════════════════════════════════════════════════════════════════════════════
        //  OUTCOMES — shared by the synchronous path, the webhook and the reconciler
        // ═════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Moves an attempt to its outcome and settles the obligation. Idempotent and safe to race:
        /// the status move is a conditional UPDATE that only an attempt still holding its lock can
        /// take, and settlement is itself conditional. Returns the attempt's resulting status.
        /// </summary>
        private async Task<BillingAttemptStatus> ApplyOutcomeAsync(int attemptId, SavedCardChargeResult result, CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            if (!string.IsNullOrEmpty(result.PaymentIntentId))
            {
                await _context.BillingPaymentAttempts
                    .Where(a => a.Id == attemptId && a.StripePaymentIntentId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.StripePaymentIntentId, result.PaymentIntentId), ct);
            }

            switch (result.Outcome)
            {
                case SavedCardChargeOutcome.Succeeded:
                {
                    var claimed = await ClaimAsync(attemptId, BillingAttemptStatus.Succeeded, null, null, null, releaseLock: true, ct);
                    if (!claimed)
                    {
                        // Money moved for an attempt already written off (e.g. a late webhook after
                        // the reconciler found nothing). The record must say what really happened.
                        await _context.BillingPaymentAttempts
                            .Where(a => a.Id == attemptId && a.Status != BillingAttemptStatus.Succeeded)
                            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, BillingAttemptStatus.Succeeded)
                                .SetProperty(a => a.ActiveLockKey, (string?)null)
                                .SetProperty(a => a.CompletedAt, now)
                                .SetProperty(a => a.UpdatedAt, now), ct);
                    }
                    // Settle whether or not WE made the status move: if the webhook got there first
                    // and crashed before settling, this is what finishes the job.
                    await SettleAsync(attemptId, result.PaymentIntentId!, ct);
                    return BillingAttemptStatus.Succeeded;
                }

                case SavedCardChargeOutcome.Processing:
                    await _context.BillingPaymentAttempts
                        .Where(a => a.Id == attemptId && (a.Status == BillingAttemptStatus.Pending || a.Status == BillingAttemptStatus.Unknown))
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, BillingAttemptStatus.Processing)
                            .SetProperty(a => a.UpdatedAt, now), ct);
                    return BillingAttemptStatus.Processing;

                case SavedCardChargeOutcome.Unknown:
                    await _context.BillingPaymentAttempts
                        .Where(a => a.Id == attemptId && a.Status == BillingAttemptStatus.Pending)
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, BillingAttemptStatus.Unknown)
                            .SetProperty(a => a.FailureCode, Truncate(result.FailureCode, 100))
                            .SetProperty(a => a.UpdatedAt, now), ct);
                    _logger.LogWarning("Saved-card attempt {AttemptId}: outcome unknown — lock kept until reconciled.", attemptId);
                    return BillingAttemptStatus.Unknown;

                case SavedCardChargeOutcome.RequiresAction:
                {
                    // Nothing can complete it with the customer absent, and a live intent could be
                    // confirmed later behind our back. Cancel it. If Stripe says it actually went
                    // through in the meantime, it is a success, not a failure.
                    if (!string.IsNullOrEmpty(result.PaymentIntentId) && await IsOffSessionAttemptAsync(attemptId))
                    {
                        var cancelled = await TryCancelAsync(result.PaymentIntentId);
                        if (cancelled?.Status == "succeeded")
                            return await ApplyOutcomeAsync(attemptId, StripeService.MapIntent(cancelled), ct);
                        if (cancelled?.Status is "processing")
                            return await ApplyOutcomeAsync(attemptId, new SavedCardChargeResult
                            {
                                Outcome = SavedCardChargeOutcome.Processing, PaymentIntentId = result.PaymentIntentId
                            }, ct);
                        if (cancelled == null || cancelled.Status != "canceled")
                        {
                            // Could not prove it is dead: keep the lock and let the reconciler look again.
                            await _context.BillingPaymentAttempts
                                .Where(a => a.Id == attemptId && a.Status == BillingAttemptStatus.Pending)
                                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, BillingAttemptStatus.Unknown)
                                    .SetProperty(a => a.FailureCode, "authentication_required")
                                    .SetProperty(a => a.UpdatedAt, now), ct);
                            return BillingAttemptStatus.Unknown;
                        }
                    }

                    // A customer-present attempt keeps its lock (Pending) while the browser runs the
                    // challenge; the webhook or the reconciler settles it.
                    if (!await IsOffSessionAttemptAsync(attemptId))
                        return BillingAttemptStatus.RequiresAction;

                    var claimed = await ClaimAsync(attemptId, BillingAttemptStatus.RequiresAction, "authentication_required",
                        result.DeclineCode, result.Message, releaseLock: true, ct);
                    if (claimed) await MarkLedgerFailedAsync(attemptId, "authentication_required", "The bank requires the customer to authenticate.");
                    return BillingAttemptStatus.RequiresAction;
                }

                default: // Declined
                {
                    var claimed = await ClaimAsync(attemptId, BillingAttemptStatus.Failed, result.FailureCode, result.DeclineCode,
                        result.Message, releaseLock: true, ct);
                    if (claimed)
                    {
                        var cardId = await _context.BillingPaymentAttempts.Where(a => a.Id == attemptId)
                            .Select(a => a.CustomerPaymentMethodId).FirstOrDefaultAsync(ct);
                        if (cardId.HasValue) await _cards.RecordChargeFailureAsync(cardId.Value, result.FailureCode, result.DeclineCode);
                        await MarkLedgerFailedAsync(attemptId, result.DeclineCode ?? result.FailureCode, result.Message);
                    }
                    return await _context.BillingPaymentAttempts.Where(a => a.Id == attemptId)
                        .Select(a => a.Status).FirstAsync(ct);
                }
            }
        }

        /// <summary>
        /// Conditional status move: only an attempt still holding its lock (Pending / Unknown /
        /// Processing) can be moved, so the webhook and the synchronous path cannot both record an
        /// outcome. Returns whether THIS call made the move.
        /// </summary>
        private async Task<bool> ClaimAsync(int attemptId, BillingAttemptStatus status, string? failureCode, string? declineCode,
            string? message, bool releaseLock, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var moved = await _context.BillingPaymentAttempts
                .Where(a => a.Id == attemptId && (a.Status == BillingAttemptStatus.Pending
                                                  || a.Status == BillingAttemptStatus.Unknown
                                                  || a.Status == BillingAttemptStatus.Processing))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, status)
                    .SetProperty(a => a.FailureCode, Truncate(failureCode, 100))
                    .SetProperty(a => a.DeclineCode, Truncate(declineCode, 100))
                    .SetProperty(a => a.FailureMessage, Truncate(message, 300))
                    .SetProperty(a => a.ActiveLockKey, a => releaseLock ? null : a.ActiveLockKey)
                    .SetProperty(a => a.CompletedAt, now)
                    .SetProperty(a => a.UpdatedAt, now), ct);

            if (moved > 0)
            {
                var attempt = await _context.BillingPaymentAttempts.AsNoTracking().FirstAsync(a => a.Id == attemptId, ct);
                await AuditAttemptAsync(attempt);
            }

            return moved > 0;
        }

        private async Task ReleaseAsync(int attemptId, BillingAttemptStatus status, string code, string reason)
        {
            await ClaimAsync(attemptId, status, code, null, reason, releaseLock: true, CancellationToken.None);
        }

        private async Task<bool> IsOffSessionAttemptAsync(int attemptId) =>
            await _context.BillingPaymentAttempts.Where(a => a.Id == attemptId)
                .Select(a => a.Trigger).FirstAsync() != BillingAttemptTrigger.CustomerSavedCard;

        private async Task<PaymentIntent?> TryCancelAsync(string paymentIntentId)
        {
            try
            {
                return await _stripe.CancelPaymentIntentAsync(paymentIntentId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cancel of {PaymentIntentId} failed; reading its state instead.", paymentIntentId);
                try { return await _stripe.GetPaymentIntentAsync(paymentIntentId); }
                catch { return null; }
            }
        }

        /// <summary>
        /// Books a succeeded charge against its obligation. Safe to call any number of times.
        /// </summary>
        private async Task SettleAsync(int attemptId, string paymentIntentId, CancellationToken ct)
        {
            var attempt = await _context.BillingPaymentAttempts.AsNoTracking().FirstAsync(a => a.Id == attemptId, ct);

            try
            {
                var card = attempt.CustomerPaymentMethodId;
                if (card.HasValue) await _cards.RecordChargeSuccessAsync(card.Value);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not stamp card usage for attempt {AttemptId}.", attemptId); }

            if (attempt.ObligationType == BillingObligationType.Order)
                await SettleOrderAsync(attempt, paymentIntentId, ct);
            else
                await SettleInvoiceAsync(attempt, paymentIntentId, ct);

            await _notifications.ResolveForObligationAsync(attempt.ObligationKey);
        }

        private async Task SettleOrderAsync(BillingPaymentAttempt attempt, string paymentIntentId, CancellationToken ct)
        {
            var orderId = attempt.OrderId!.Value;
            var now = DateTime.UtcNow;

            // ONE conditional UPDATE marks the order paid. MySQL evaluates SET assignments left to
            // right, so every snapshot condition reads InitialTotal — which is assigned LAST.
            // Status moves Pending → Active only; a cleaning already Done stays Done (audit finding).
            var updated = await _context.Orders
                .Where(o => o.Id == orderId && !o.IsPaid && o.InvoicePaidAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.IsPaid, true)
                    .SetProperty(o => o.PaidAt, now)
                    .SetProperty(o => o.PaymentIntentId, paymentIntentId)
                    .SetProperty(o => o.Status, o => o.Status == OrderStatuses.Pending ? OrderStatuses.Active : o.Status)
                    .SetProperty(o => o.InitialSubTotal, o => o.InitialTotal == 0 ? o.SubTotal : o.InitialSubTotal)
                    .SetProperty(o => o.InitialTax, o => o.InitialTotal == 0 ? o.Tax : o.InitialTax)
                    .SetProperty(o => o.InitialTips, o => o.InitialTotal == 0 ? o.Tips : o.InitialTips)
                    .SetProperty(o => o.InitialCompanyDevelopmentTips, o => o.InitialTotal == 0 ? o.CompanyDevelopmentTips : o.InitialCompanyDevelopmentTips)
                    .SetProperty(o => o.InitialTotal, o => o.InitialTotal == 0 ? o.Total : o.InitialTotal)
                    .SetProperty(o => o.UpdatedAt, now), ct);

            if (updated == 0)
            {
                var current = await _context.Orders.AsNoTracking()
                    .Where(o => o.Id == orderId).Select(o => o.PaymentIntentId).FirstOrDefaultAsync(ct);
                if (current == paymentIntentId) return; // already settled by this very charge

                // Paid by something else while this charge was in flight. The customer must not
                // keep paying twice: refund this one, automatically, exactly once.
                await RefundDuplicateAsync(attempt, paymentIntentId, "The order was already paid by another payment.");
                return;
            }

            _logger.LogInformation("Order {OrderId} paid by saved card (attempt {AttemptId}, {Role}).",
                orderId, attempt.Id, attempt.CardRole);

            // The same post-payment bookkeeping every other payment path runs. Each step is
            // non-fatal: the money is in and the order is paid whatever happens below.
            await RunPostPaymentBookkeepingAsync(orderId);

            await AuditOrderAsync(orderId, "SavedCardCharged", new
            {
                AttemptId = attempt.Id,
                attempt.Amount,
                Card = attempt.CardLast4 == null ? null : $"{attempt.CardBrand} ••••{attempt.CardLast4}",
                CardRole = attempt.CardRole.ToString(),
                Trigger = attempt.Trigger.ToString(),
                PaymentIntentId = paymentIntentId
            }, attempt.InitiatedByUserId);
        }

        private async Task RunPostPaymentBookkeepingAsync(int orderId)
        {
            try
            {
                var order = await _context.Orders.Include(o => o.User).FirstOrDefaultAsync(o => o.Id == orderId);
                if (order == null) return;

                if (order.LoyaltyDiscountAmount > 0m && order.LoyaltyDiscountPercentage > 0m)
                {
                    try { await _services.GetRequiredService<ILoyaltyDiscountService>().ApplyToOrderAsync(order.Id); }
                    catch (Exception ex) { _logger.LogError(ex, "Loyalty apply failed for saved-card-paid order {OrderId}", orderId); }
                }

                try
                {
                    if (order.SubscriptionId > 0)
                    {
                        var subscription = await _context.Subscriptions.FindAsync(order.SubscriptionId);
                        if (subscription != null && subscription.SubscriptionDays > 0)
                        {
                            var subscriptions = _services.GetRequiredService<ISubscriptionService>();
                            var hasActive = await subscriptions.CheckAndUpdateSubscriptionStatus(order.UserId);
                            if (!hasActive) await subscriptions.ActivateSubscription(order.UserId, subscription.Id, order.ServiceDate);
                            else await subscriptions.RenewSubscription(order.UserId, order.ServiceDate);
                        }
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Subscription activation failed for saved-card-paid order {OrderId}", orderId); }

                if (order.User != null && order.User.FirstTimeOrder)
                {
                    order.User.FirstTimeOrder = false;
                    order.User.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Post-payment bookkeeping failed for order {OrderId}; the payment stands.", orderId);
            }
        }

        private async Task SettleInvoiceAsync(BillingPaymentAttempt attempt, string paymentIntentId, CancellationToken ct)
        {
            try
            {
                var intent = await _stripe.GetPaymentIntentAsync(paymentIntentId);
                var ledgerService = _services.GetRequiredService<InvoiceStripePaymentService>();
                var result = await ledgerService.RecordSucceededAsync(
                    paymentIntentId,
                    intent.AmountReceived > 0 ? intent.AmountReceived : intent.Amount,
                    intent.Currency ?? "usd",
                    intent.LatestChargeId,
                    intent.Metadata,
                    attempt.CardLast4 == null ? null : $"{attempt.CardBrand} ••••{attempt.CardLast4}");

                if (result.Recorded && result.InvoiceBecamePaid && result.InvoiceId.HasValue)
                {
                    try
                    {
                        var alreadySent = await _context.CommercialInvoiceEmailLogs.AnyAsync(e =>
                            e.CommercialInvoiceId == result.InvoiceId.Value
                            && e.EmailType == InvoiceEmailType.PaymentReceipt
                            && e.Status == InvoiceEmailStatus.Sent, ct);
                        if (!alreadySent)
                            await _services.GetRequiredService<InvoiceEmailService>().SendPaymentReceiptAsync(result.InvoiceId.Value, 0);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Invoice {InvoiceId} paid by saved card, but the receipt email failed.", result.InvoiceId);
                    }
                }
            }
            catch (Exception ex)
            {
                // The charge succeeded; the ledger write will be retried by the webhook, which
                // records the same intent idempotently.
                _logger.LogError(ex, "Recording saved-card payment {PaymentIntentId} on invoice {InvoiceId} failed; the webhook will retry.",
                    paymentIntentId, attempt.CommercialInvoiceId);
            }
        }

        private async Task MarkLedgerFailedAsync(int attemptId, string? code, string? message)
        {
            var ledgerId = await _context.BillingPaymentAttempts.Where(a => a.Id == attemptId)
                .Select(a => a.CommercialInvoicePaymentAttemptId).FirstOrDefaultAsync();
            if (ledgerId == null) return;

            var ledger = await _context.CommercialInvoicePaymentAttempts.FirstOrDefaultAsync(a => a.Id == ledgerId);
            if (ledger == null || ledger.Status == InvoicePaymentAttemptStatus.Succeeded) return;
            ledger.Status = InvoicePaymentAttemptStatus.Failed;
            ledger.FailureCode = Truncate(code, 100);
            ledger.FailureMessage = Truncate(message, 500);
            ledger.CompletedAt = DateTime.UtcNow;
            ledger.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        private async Task RefundDuplicateAsync(BillingPaymentAttempt attempt, string paymentIntentId, string reason)
        {
            try
            {
                await _stripe.CreateRefundAsync(paymentIntentId, null,
                    idempotencyKey: $"dc-billing-duplicate-refund-{paymentIntentId}",
                    metadata: new Dictionary<string, string>
                    {
                        [AttemptIdMetadataKey] = attempt.Id.ToString(),
                        ["reason"] = "duplicate_saved_card_charge"
                    });
                _logger.LogWarning("Saved-card charge {PaymentIntentId} (attempt {AttemptId}) refunded as a duplicate: {Reason}",
                    paymentIntentId, attempt.Id, reason);
                await AuditOrderAsync(attempt.OrderId ?? 0, "DuplicateChargeRefunded",
                    new { AttemptId = attempt.Id, PaymentIntentId = paymentIntentId, attempt.Amount, Reason = reason }, null);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "DUPLICATE CHARGE NOT REFUNDED: {PaymentIntentId} (attempt {AttemptId}) — refund it manually.",
                    paymentIntentId, attempt.Id);
                await AuditOrderAsync(attempt.OrderId ?? 0, "DuplicateChargeRefundFailed",
                    new { AttemptId = attempt.Id, PaymentIntentId = paymentIntentId, attempt.Amount, Error = ex.Message }, null);
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════════════
        //  WEBHOOKS AND RECONCILIATION
        // ═════════════════════════════════════════════════════════════════════════════════════

        public async Task HandleStripeIntentEventAsync(PaymentIntent intent, string eventType)
        {
            if (intent.Metadata == null || !intent.Metadata.TryGetValue(AttemptIdMetadataKey, out var raw)
                || !int.TryParse(raw, out var attemptId))
                return;

            var attempt = await _context.BillingPaymentAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attemptId);
            if (attempt == null)
            {
                _logger.LogWarning("Webhook {EventType} for {PaymentIntentId} names unknown billing attempt {AttemptId}.",
                    eventType, intent.Id, attemptId);
                return;
            }

            // Metadata is a hint; the intent id is the proof. An attempt already bound to a
            // different intent is never moved by this one.
            if (attempt.StripePaymentIntentId != null && attempt.StripePaymentIntentId != intent.Id)
            {
                _logger.LogWarning("Webhook intent {PaymentIntentId} does not match attempt {AttemptId}'s {Bound}.",
                    intent.Id, attemptId, attempt.StripePaymentIntentId);
                return;
            }

            var before = attempt.Status;
            var mapped = StripeService.MapIntent(intent);

            if (eventType == "payment_intent.canceled")
            {
                if (attempt.HoldsLock)
                    await ReleaseAsync(attemptId, BillingAttemptStatus.Canceled, "canceled", "The payment was canceled.");
                return;
            }

            // A customer-present 3DS challenge that is still open is not an outcome yet.
            if (mapped.Outcome == SavedCardChargeOutcome.RequiresAction && attempt.Trigger == BillingAttemptTrigger.CustomerSavedCard)
                return;

            // A success ALWAYS settles, even for an attempt we had written off — money that
            // arrived is real (SettleAsync refunds it if the obligation was paid another way).
            var status = await ApplyOutcomeAsync(attemptId, mapped, CancellationToken.None);

            // Continuing the run (Backup, notifications) is the synchronous path's job while it is
            // alive — Stripe's webhook for an off-session charge usually lands within a second of
            // the API response. Only a run that has clearly been abandoned is continued from here;
            // the reconciler's orphan scan covers the rest. (RunKey+Sequence is unique, so even a
            // collision cannot produce two Backup attempts.)
            var lockHeld = before is BillingAttemptStatus.Unknown or BillingAttemptStatus.Processing or BillingAttemptStatus.Pending;
            var decided = status is not (BillingAttemptStatus.Unknown or BillingAttemptStatus.Processing or BillingAttemptStatus.Pending);
            if (lockHeld && decided && attempt.CreatedAt < DateTime.UtcNow.AddMinutes(-2))
                await ContinueRunAsync(attemptId, CancellationToken.None);
        }

        /// <summary>
        /// The customer's browser finished (or abandoned) a 3DS challenge on a saved-card invoice
        /// payment. Reads the intent back from Stripe and applies it — the browser's word is never
        /// taken for it.
        /// </summary>
        public async Task<SavedCardChargeResponseDto> RefreshCustomerAttemptAsync(int userId, string paymentIntentId)
        {
            var attempt = await _context.BillingPaymentAttempts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.StripePaymentIntentId == paymentIntentId && a.UserId == userId);
            if (attempt == null) return Response("blocked", "Payment not found.");

            var intent = await _stripe.GetPaymentIntentAsync(paymentIntentId);
            var mapped = StripeService.MapIntent(intent);

            if (mapped.Outcome == SavedCardChargeOutcome.RequiresAction)
                return Response("requires_action", "Your bank still needs you to confirm this payment.", attempt.Amount);

            var status = await ApplyOutcomeAsync(attempt.Id, mapped, CancellationToken.None);
            await MarkRunFinalizedAsync(attempt.RunKey);

            return status switch
            {
                BillingAttemptStatus.Succeeded => new SavedCardChargeResponseDto
                {
                    Result = "paid", Charged = true, Amount = attempt.Amount, PaymentIntentId = paymentIntentId,
                    Message = $"Paid {attempt.Amount:C}. Thank you!"
                },
                BillingAttemptStatus.Processing => Response("pending", "The payment is processing.", attempt.Amount),
                _ => Response("failed", "The payment was not completed. Nothing was charged — you can try again.", attempt.Amount)
            };
        }

        public async Task<int> ReconcileStaleAttemptsAsync(CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var stale = await _context.BillingPaymentAttempts.AsNoTracking()
                .Where(a => (a.Status == BillingAttemptStatus.Unknown && a.UpdatedAt < now.AddMinutes(-2))
                            || (a.Status == BillingAttemptStatus.Pending && a.UpdatedAt < now.AddMinutes(-10))
                            || (a.Status == BillingAttemptStatus.Processing && a.UpdatedAt < now.AddMinutes(-30)))
                .OrderBy(a => a.Id)
                .Take(25)
                .ToListAsync(ct);

            foreach (var attempt in stale)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ReconcileOneAsync(attempt, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Reconciling billing attempt {AttemptId} failed; will retry.", attempt.Id);
                }
            }

            // Orphaned runs: the first attempt has a definite outcome but the run was never
            // finished (the process died before the Backup or the notification). Finish them.
            var orphanCutoff = now.AddMinutes(-5);
            var orphans = await _context.BillingPaymentAttempts.AsNoTracking()
                .Where(a => a.Sequence == 1 && a.RunFinalizedAt == null
                            && a.CompletedAt != null && a.CompletedAt < orphanCutoff && a.CompletedAt > now.AddDays(-2)
                            && (a.Status == BillingAttemptStatus.Failed || a.Status == BillingAttemptStatus.RequiresAction
                                || a.Status == BillingAttemptStatus.Succeeded))
                .OrderBy(a => a.Id)
                .Take(25)
                .ToListAsync(ct);

            foreach (var orphan in orphans)
            {
                try
                {
                    var last = await _context.BillingPaymentAttempts.AsNoTracking()
                        .Where(a => a.RunKey == orphan.RunKey)
                        .OrderByDescending(a => a.Sequence)
                        .FirstAsync(ct);
                    if (last.HoldsLock) continue; // the Backup itself is still being resolved
                    await ContinueRunAsync(last.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Finishing orphaned run {RunKey} failed; will retry.", orphan.RunKey);
                }
            }

            return stale.Count + orphans.Count;
        }

        /// <summary>
        /// What really happened to an attempt whose answer never came back. In order of trust:
        /// the intent we already know → the SAME idempotent request replayed (inside Stripe's 24h
        /// key window, and only while the obligation is still owed) → Stripe Search by our attempt
        /// id. Only when all three prove there is no charge is the lock released as a failure.
        /// </summary>
        private async Task ReconcileOneAsync(BillingPaymentAttempt attempt, CancellationToken ct)
        {
            await _context.BillingPaymentAttempts.Where(a => a.Id == attempt.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReconcileAttempts, a => a.ReconcileAttempts + 1)
                    .SetProperty(a => a.UpdatedAt, DateTime.UtcNow), ct);

            SavedCardChargeResult? result = null;

            if (!string.IsNullOrEmpty(attempt.StripePaymentIntentId))
            {
                result = StripeService.MapIntent(await _stripe.GetPaymentIntentAsync(attempt.StripePaymentIntentId));
            }
            else
            {
                var found = await _stripe.FindPaymentIntentByMetadataAsync(AttemptIdMetadataKey, attempt.Id.ToString());
                if (found != null)
                {
                    result = StripeService.MapIntent(found);
                }
                else if (attempt.CreatedAt > DateTime.UtcNow.AddHours(-23) && await IsStillOwedAsync(attempt, ct))
                {
                    var card = attempt.CustomerPaymentMethodId.HasValue
                        ? await _context.CustomerPaymentMethods.AsNoTracking().FirstOrDefaultAsync(c => c.Id == attempt.CustomerPaymentMethodId, ct)
                        : null;
                    if (card != null)
                        result = await _stripe.ChargeSavedCardAsync(await BuildChargeRequestAsync(attempt, null, card, ct));
                }
                else if (attempt.ReconcileAttempts >= 3)
                {
                    // Stripe has no intent carrying this attempt's id, and the key window is past.
                    // No charge happened.
                    await ReleaseAsync(attempt.Id, BillingAttemptStatus.Failed, "no_charge_found",
                        "No payment was found at Stripe for this attempt.");
                    await ContinueRunAsync(attempt.Id, ct);
                    return;
                }
            }

            if (result == null || result.Outcome == SavedCardChargeOutcome.Unknown) return;

            // A customer who opened a 3DS challenge and walked away must not hold the obligation
            // hostage: after 30 minutes the intent is cancelled and the lock released.
            if (result.Outcome == SavedCardChargeOutcome.RequiresAction && attempt.Trigger == BillingAttemptTrigger.CustomerSavedCard)
            {
                if (attempt.CreatedAt > DateTime.UtcNow.AddMinutes(-30) || string.IsNullOrEmpty(result.PaymentIntentId)) return;
                var cancelled = await TryCancelAsync(result.PaymentIntentId);
                if (cancelled == null) return;
                result = StripeService.MapIntent(cancelled);
                if (result.Outcome == SavedCardChargeOutcome.RequiresAction) return;
            }

            var status = await ApplyOutcomeAsync(attempt.Id, result, ct);
            if (status is not (BillingAttemptStatus.Unknown or BillingAttemptStatus.Processing or BillingAttemptStatus.Pending))
                await ContinueRunAsync(attempt.Id, ct);
        }

        private async Task<bool> IsStillOwedAsync(BillingPaymentAttempt attempt, CancellationToken ct)
        {
            if (attempt.ObligationType == BillingObligationType.Order)
            {
                var order = await _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == attempt.OrderId, ct);
                return order != null && CheckOrderPayable(order, out _) == null;
            }

            var invoice = await _context.CommercialInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == attempt.CommercialInvoiceId, ct);
            return invoice != null && CheckInvoicePayable(invoice) == null;
        }

        /// <summary>
        /// After a DELAYED outcome (webhook / reconciler), finish the run the way the synchronous
        /// path would have: try the Backup after a definite Primary failure, then notify.
        /// </summary>
        private async Task ContinueRunAsync(int attemptId, CancellationToken ct)
        {
            var attempt = await _context.BillingPaymentAttempts.AsNoTracking().FirstAsync(a => a.Id == attemptId, ct);

            // Only the last attempt of a run moves it on.
            var later = await _context.BillingPaymentAttempts.AnyAsync(a => a.RunKey == attempt.RunKey && a.Sequence > attempt.Sequence, ct);
            if (later) return;

            var run = new ChargeRun
            {
                RunKey = attempt.RunKey,
                ObligationType = attempt.ObligationType,
                ObligationKey = attempt.ObligationKey,
                OrderId = attempt.OrderId,
                InvoiceId = attempt.CommercialInvoiceId,
                UserId = attempt.UserId,
                Trigger = attempt.Trigger,
                InitiatedByUserId = attempt.InitiatedByUserId,
                AuthorizationId = attempt.PaymentAuthorizationId
            };

            var firstResult = new AttemptResult
            {
                Status = attempt.Status, AttemptId = attempt.Id, Amount = attempt.Amount,
                CardLabel = LabelFrom(attempt), FailureCode = attempt.FailureCode, DeclineCode = attempt.DeclineCode,
                PaymentIntentId = attempt.StripePaymentIntentId
            };
            var finalResult = firstResult;
            string? primaryLabel = firstResult.CardLabel;

            if (attempt.Sequence == 1 && firstResult.IsDefiniteFailure && attempt.CardRole == BillingCardRole.Primary)
            {
                // Re-resolve the authorisation NOW — the customer may have revoked it, or turned
                // the Backup off, while the outcome was unknown.
                var scope = attempt.Trigger switch
                {
                    BillingAttemptTrigger.AdminCharge => PaymentAuthorizationScope.OfficeBookedOrders,
                    BillingAttemptTrigger.AutoPayRecurring => PaymentAuthorizationScope.RecurringSeries,
                    BillingAttemptTrigger.AutoPayCommercial => PaymentAuthorizationScope.CommercialClient,
                    _ => (PaymentAuthorizationScope?)null
                };

                EffectiveAuthorization? authorization = null;
                if (scope != null)
                {
                    int? seriesId = null, clientId = null;
                    if (scope == PaymentAuthorizationScope.RecurringSeries)
                        seriesId = await _context.Orders.Where(o => o.Id == attempt.OrderId).Select(o => o.RecurringSeriesId).FirstOrDefaultAsync(ct);
                    if (scope == PaymentAuthorizationScope.CommercialClient)
                        clientId = await _context.CommercialInvoices.Where(i => i.Id == attempt.CommercialInvoiceId).Select(i => (int?)i.ContractClientId).FirstOrDefaultAsync(ct);
                    authorization = await _authorizations.ResolveEffectiveAsync(attempt.UserId, scope.Value, seriesId, clientId);
                }

                if (authorization?.AllowBackupFallback == true)
                {
                    run.AllowBackup = true;
                    var backupId = await _context.Users.Where(u => u.Id == attempt.UserId).Select(u => u.BackupPaymentMethodId).FirstOrDefaultAsync(ct);
                    var backup = backupId.HasValue && backupId != attempt.CustomerPaymentMethodId
                        ? await _cards.GetUsableCardAsync(attempt.UserId, backupId)
                        : null;
                    if (backup != null)
                    {
                        var backupResult = await AttemptAsync(run, backup, BillingCardRole.Backup, sequence: 2, ct);
                        if (backupResult.ConcurrentRun) return; // someone else is finishing this run
                        if (backupResult.AttemptId != null || backupResult.NothingDue) finalResult = backupResult;
                        if (backupResult.Status is BillingAttemptStatus.Unknown or BillingAttemptStatus.Processing)
                            return; // the reconciler continues once the Backup's outcome is known
                    }
                }
            }
            else if (attempt.Sequence == 2)
            {
                var first = await _context.BillingPaymentAttempts.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.RunKey == attempt.RunKey && a.Sequence == 1, ct);
                if (first != null)
                {
                    primaryLabel = LabelFrom(first);
                    firstResult = new AttemptResult { Status = first.Status, AttemptId = first.Id, CardLabel = primaryLabel, Amount = first.Amount };
                }
            }

            await NotifyRunAsync(run, firstResult, finalResult, primaryLabel, finalResult.CardLabel);
            await MarkRunFinalizedAsync(run.RunKey);
        }

        public Task<bool> HasActiveAttemptAsync(string obligationKey) =>
            _context.BillingPaymentAttempts.AnyAsync(a => a.ActiveLockKey == obligationKey);

        // ═════════════════════════════════════════════════════════════════════════════════════
        //  NOTIFYING AND ANSWERING
        // ═════════════════════════════════════════════════════════════════════════════════════

        private async Task NotifyRunAsync(ChargeRun run, AttemptResult first, AttemptResult final, string? primaryLabel, string? chargedLabel)
        {
            // A run that never reached Stripe (blocked, nothing due), or whose outcome is not known
            // yet, is not news to the customer — the reconciler notifies when it is.
            if (final.ConcurrentRun) return;
            if (final.AttemptId == null && !final.NoUsableCard) return;
            if (final.Status is BillingAttemptStatus.Unknown or BillingAttemptStatus.Processing
                or BillingAttemptStatus.Pending or BillingAttemptStatus.Canceled) return;

            var automatic = run.Trigger is BillingAttemptTrigger.AutoPayRecurring or BillingAttemptTrigger.AutoPayCommercial;
            var backupPaid = final.Status == BillingAttemptStatus.Succeeded && final.AttemptId != first.AttemptId;

            BillingNotificationType? type = null;
            if (backupPaid)
                type = BillingNotificationType.PrimaryFailedBackupSucceeded; // owner's rule: always told
            else if (automatic && final.Status == BillingAttemptStatus.Succeeded)
                type = BillingNotificationType.AutoPaySucceeded;
            else if (automatic && final.Status == BillingAttemptStatus.RequiresAction && final.AttemptId == first.AttemptId)
                type = BillingNotificationType.AuthenticationRequired;
            else if (automatic && (final.IsDefiniteFailure || final.Status == BillingAttemptStatus.Canceled))
                type = BillingNotificationType.AutoPayFailed;

            if (type == null) return;

            await _notifications.NotifyChargeOutcomeAsync(new ChargeNotice
            {
                UserId = run.UserId,
                Type = type.Value,
                ObligationType = run.ObligationType,
                OrderId = run.OrderId,
                InvoiceId = run.InvoiceId,
                ObligationKey = run.ObligationKey,
                RunKey = run.RunKey,
                PaymentAttemptId = final.AttemptId ?? first.AttemptId,
                Amount = final.Amount > 0 ? final.Amount : first.Amount,
                PrimaryCardLabel = primaryLabel,
                ChargedCardLabel = type == BillingNotificationType.AutoPayFailed && final.AttemptId == first.AttemptId ? primaryLabel : chargedLabel
            });
        }

        private static SavedCardChargeResponseDto ToResponse(ChargeRun run, AttemptResult first, AttemptResult final)
        {
            var amount = final.Amount > 0 ? final.Amount : first.Amount;

            if (final.Status == BillingAttemptStatus.Succeeded)
            {
                var byBackup = final.AttemptId != first.AttemptId;
                return new SavedCardChargeResponseDto
                {
                    Result = byBackup ? "paid_by_backup" : "paid",
                    Charged = true,
                    Amount = amount,
                    PaymentIntentId = final.PaymentIntentId,
                    Message = byBackup
                        ? $"The Primary card ({first.CardLabel}) failed, so {amount:C} was charged to the Backup card ({final.CardLabel}). It is paid."
                        : $"Charged {amount:C} to {final.CardLabel}. It is paid."
                };
            }

            if (final.ConcurrentRun)
                return Response("pending", "This payment is being completed right now. Refresh in a moment — do not charge again.", amount);

            if (final.BlockedReason != null && final.AttemptId == null && !final.NoUsableCard)
                return Response(final.NothingDue ? "nothing_due" : "blocked", final.BlockedReason, amount);

            if (final.Status == BillingAttemptStatus.Canceled && final.BlockedReason != null)
                return Response("blocked", final.BlockedReason, amount);

            return final.Status switch
            {
                BillingAttemptStatus.Unknown => Response("unknown",
                    "We couldn't confirm whether the charge went through. Do NOT charge again — it is being checked automatically and this page will update.", amount),
                BillingAttemptStatus.Processing => Response("pending", "The payment is processing. Do not charge again.", amount),
                BillingAttemptStatus.Pending => new SavedCardChargeResponseDto
                {
                    Result = "requires_action",
                    Amount = amount,
                    ClientSecret = final.ClientSecret,
                    PaymentIntentId = final.PaymentIntentId,
                    Message = "Your bank needs you to confirm this payment."
                },
                BillingAttemptStatus.RequiresAction when final.ClientSecret != null => new SavedCardChargeResponseDto
                {
                    Result = "requires_action",
                    Amount = amount,
                    ClientSecret = final.ClientSecret,
                    PaymentIntentId = final.PaymentIntentId,
                    Message = "Your bank needs you to confirm this payment."
                },
                BillingAttemptStatus.RequiresAction => Response("requires_action",
                    "The bank asked for the customer to verify this charge, which can't happen without them present. Nothing was charged — send a payment link instead.", amount),
                _ => Response("failed", final.NoUsableCard
                    ? "There is no usable saved card to charge. Nothing was charged."
                    : $"The card was declined ({final.DeclineCode ?? final.FailureCode ?? "declined"}). Nothing was charged.", amount)
            };
        }

        private static SavedCardChargeResponseDto Response(string result, string message, decimal amount = 0m) =>
            new() { Result = result, Message = message, Amount = amount };

        // ═════════════════════════════════════════════════════════════════════════════════════
        //  ADMIN
        // ═════════════════════════════════════════════════════════════════════════════════════

        public async Task<AdminOrderSavedCardInfoDto> GetAdminOrderChargeInfoAsync(int orderId)
        {
            var info = new AdminOrderSavedCardInfoDto { FeatureEnabled = _features.SavedCardsEnabled };
            var order = await _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null)
            {
                info.UnavailableReason = "Order not found.";
                return info;
            }

            var blocked = CheckOrderPayable(order, out var due);
            info.AmountDue = due;

            var primary = await _cards.GetUsableCardAsync(order.UserId, null);
            info.HasCard = primary != null;
            info.Brand = primary?.Brand;
            info.Last4 = primary?.Last4;

            var authorization = await _authorizations.ResolveEffectiveAsync(order.UserId, PaymentAuthorizationScope.OfficeBookedOrders);
            info.HasOfficeAuthorization = authorization != null;
            info.BackupAllowed = authorization?.AllowBackupFallback ?? false;

            info.UnavailableReason = !_features.SavedCardsEnabled ? "Saved-card payments are switched off."
                : blocked != null ? blocked.Message
                : primary == null ? "The customer has no usable saved card."
                : authorization == null ? "The customer has not authorized card charges for office-booked orders — send a payment link."
                : await HasActiveAttemptAsync(BillingPaymentAttempt.OrderObligationKey(orderId)) ? "A payment for this order is already being processed."
                : null;

            return info;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        private async Task<string?> LabelForAsync(int? cardId)
        {
            if (cardId == null) return null;
            var card = await _context.CustomerPaymentMethods.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cardId);
            return card == null ? null : PaymentMethodService.Label(card);
        }

        private static string? LabelFrom(BillingPaymentAttempt attempt)
        {
            if (attempt.CardLast4 == null && attempt.CardBrand == null) return null;
            var brand = string.IsNullOrWhiteSpace(attempt.CardBrand) ? "Card"
                : char.ToUpperInvariant(attempt.CardBrand[0]) + attempt.CardBrand[1..];
            return attempt.CardLast4 == null ? brand : $"{brand} ending {attempt.CardLast4}";
        }

        private async Task AuditAttemptAsync(BillingPaymentAttempt attempt)
        {
            try
            {
                var entityId = attempt.OrderId ?? -(attempt.CommercialInvoiceId ?? 0);
                await _audit.LogActionAsync(AuditEntityTypes.SavedCardChargeAction, entityId, $"Attempt{attempt.Status}", null, new
                {
                    AttemptId = attempt.Id,
                    Obligation = attempt.ObligationKey,
                    Trigger = attempt.Trigger.ToString(),
                    CardRole = attempt.CardRole.ToString(),
                    Card = attempt.CardLast4 == null ? null : $"{attempt.CardBrand} ••••{attempt.CardLast4}",
                    attempt.Amount,
                    Status = attempt.Status.ToString(),
                    attempt.FailureCode,
                    attempt.DeclineCode,
                    PaymentIntentId = attempt.StripePaymentIntentId
                }, actingUserId: attempt.InitiatedByUserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not audit billing attempt {AttemptId}.", attempt.Id);
            }
        }

        private async Task AuditOrderAsync(int orderId, string action, object payload, int? actingUserId)
        {
            try
            {
                await _audit.LogActionAsync(AuditEntityTypes.OrderPaymentAction, orderId, action, null, payload, actingUserId: actingUserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not audit {Action} on order {OrderId}.", action, orderId);
            }
        }

        private static bool IsUniqueViolation(DbUpdateException ex) =>
            ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
    }
}
