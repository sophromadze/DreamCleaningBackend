using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>What settling a PaymentIntent did, so the caller knows whether to send a receipt.</summary>
    public class StripeSettlementResult
    {
        public bool Recorded { get; set; }
        public bool AlreadyProcessed { get; set; }
        public bool InvoiceBecamePaid { get; set; }
        public bool Overpaid { get; set; }
        public int? InvoiceId { get; set; }
    }

    /// <summary>
    /// Turns verified Stripe webhook events into commercial invoice ledger entries.
    ///
    /// THE WEBHOOK IS THE SOURCE OF TRUTH. Nothing here is ever driven by a success redirect, a
    /// query string or a browser callback — for ACH those arrive days before the money does, and
    /// acting on them would mark invoices paid for funds that may still bounce.
    ///
    /// IDEMPOTENCY IS ENFORCED BY THE DATABASE, not by a flag. Stripe retries deliveries and can
    /// send the same event twice at once; the unique index on
    /// <c>CommercialInvoicePayments.StripePaymentIntentId</c> is what makes a second insert
    /// impossible, and <see cref="RecordSucceededAsync"/> treats that violation as "already
    /// recorded" and reports success so Stripe stops retrying.
    ///
    /// The money pipeline is NOT duplicated per payment method. ACH and Card both land here and
    /// both go through the same recalculation and the same <c>InvoiceStatusPolicy</c>, exactly
    /// like a manually recorded payment.
    /// </summary>
    public class InvoiceStripePaymentService
    {
        private readonly ApplicationDbContext _context;
        private readonly InvoiceService _invoices;
        private readonly InvoiceOrderLinkService _orderLinks;
        private readonly ILogger<InvoiceStripePaymentService> _logger;

        public InvoiceStripePaymentService(
            ApplicationDbContext context,
            InvoiceService invoices,
            InvoiceOrderLinkService orderLinks,
            ILogger<InvoiceStripePaymentService> logger)
        {
            _context = context;
            _invoices = invoices;
            _orderLinks = orderLinks;
            _logger = logger;
        }

        // ── checkout.session.completed ────────────────────────────────────────────────────────

        /// <summary>
        /// Associates the Checkout Session with its PaymentIntent.
        ///
        /// FOR ACH THIS DOES NOT MEAN THE MONEY ARRIVED. The customer authorized a debit; Stripe
        /// has not moved anything yet. So this writes no payment row and changes no invoice
        /// status — it only records the PaymentIntent id that the later events resolve by, which
        /// is the one thing that would otherwise be missing.
        /// </summary>
        public async Task HandleCheckoutCompletedAsync(
            string sessionId, string? paymentIntentId, IDictionary<string, string>? metadata)
        {
            var attempt = await FindAttemptAsync(sessionId: sessionId, paymentIntentId: null, metadata: metadata);

            if (attempt == null)
            {
                _logger.LogWarning(
                    "checkout.session.completed for {SessionId} matched no commercial payment attempt.",
                    sessionId);
                return;
            }

            if (!string.IsNullOrEmpty(paymentIntentId))
                attempt.StripePaymentIntentId = paymentIntentId;

            // Only advance a still-open attempt. A card payment can deliver
            // payment_intent.succeeded BEFORE checkout.session.completed, and this event must not
            // drag a settled attempt back into Processing.
            if (attempt.Status == InvoicePaymentAttemptStatus.CheckoutOpen)
                attempt.Status = InvoicePaymentAttemptStatus.Processing;

            attempt.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _invoices.LogActivityAsync(attempt.CommercialInvoiceId, "stripe_ach_processing",
                "Customer authorized the bank payment. Waiting for the funds to settle.",
                null, "Stripe");
        }

        // ── payment_intent.processing ─────────────────────────────────────────────────────────

        /// <summary>
        /// Stripe is moving the money. Days may pass before it settles.
        ///
        /// Marks the attempt only. No payment row, no status change, no receipt — the invoice is
        /// still genuinely unpaid, and saying otherwise would be a lie that survives until the
        /// debit either lands or fails.
        /// </summary>
        public async Task HandleProcessingAsync(
            string paymentIntentId, IDictionary<string, string>? metadata, string? sourceLabel)
        {
            var attempt = await FindAttemptAsync(null, paymentIntentId, metadata);
            if (attempt == null) return;

            if (attempt.Status is InvoicePaymentAttemptStatus.Created
                              or InvoicePaymentAttemptStatus.CheckoutOpen
                              or InvoicePaymentAttemptStatus.Processing)
            {
                attempt.Status = InvoicePaymentAttemptStatus.Processing;
            }

            attempt.StripePaymentIntentId ??= paymentIntentId;
            if (!string.IsNullOrWhiteSpace(sourceLabel)) attempt.PaymentSourceLabel = sourceLabel;
            attempt.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
        }

        // ── payment_intent.succeeded ──────────────────────────────────────────────────────────

        /// <summary>
        /// THE CRITICAL EVENT. Money has settled; record it.
        ///
        /// Everything below runs in ONE transaction: attempt → payment row → recalculated totals →
        /// status → PaidAt → activity. A partial commit here is either money received that the
        /// ledger does not show, or an invoice marked paid with nothing behind it.
        ///
        /// CONCURRENCY: the invoice and its payments are re-read INSIDE the transaction, so an
        /// admin recording a manual payment at the same moment is accounted for rather than
        /// overwritten. If the two together exceed the total, the Stripe money is still recorded —
        /// it genuinely arrived — and the overpayment is flagged for review. Discarding a real
        /// settlement to keep the arithmetic tidy would be the worse error.
        /// </summary>
        public async Task<StripeSettlementResult> RecordSucceededAsync(
            string paymentIntentId,
            long amountReceivedCents,
            string currency,
            string? chargeId,
            IDictionary<string, string>? metadata,
            string? sourceLabel)
        {
            var result = new StripeSettlementResult();

            // Cheap pre-check outside the transaction; the unique index below is the real guard.
            if (await _context.CommercialInvoicePayments
                    .AnyAsync(p => p.StripePaymentIntentId == paymentIntentId))
            {
                _logger.LogInformation(
                    "PaymentIntent {PaymentIntentId} already recorded; ignoring duplicate delivery.",
                    paymentIntentId);
                result.AlreadyProcessed = true;
                return result;
            }

            var attempt = await FindAttemptAsync(null, paymentIntentId, metadata);
            var invoiceId = attempt?.CommercialInvoiceId ?? ResolveInvoiceIdFromMetadata(metadata);

            if (invoiceId == null)
            {
                _logger.LogError(
                    "PaymentIntent {PaymentIntentId} is a commercial invoice payment but no invoice could be resolved.",
                    paymentIntentId);
                return result;
            }

            result.InvoiceId = invoiceId;

            if (!string.Equals(currency, "usd", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "PaymentIntent {PaymentIntentId} settled in {Currency}, which does not match the invoice currency.",
                    paymentIntentId, currency);
                return result;
            }

            // Stripe reports cents; the ledger is decimal dollars. THIS IS THE GROSS DEBIT - it
            // includes the ACH processing fee the customer agreed to on top of the invoice.
            var charged = decimal.Round(amountReceivedCents / 100m, 2, MidpointRounding.AwayFromZero);
            if (charged <= 0m) return result;

            // ── Split the debit back into "what the invoice was paid" and "what the payment
            //    method cost" ──
            //
            // The fee is taken from the ATTEMPT, which froze the figure quoted to the customer
            // when they authorized - never recomputed from today's settings, because an ACH debit
            // takes days to clear and the settings could legitimately have moved in between. What
            // they agreed to is what is recorded.
            //
            // Crediting the gross to the invoice would make a $925.43 bill read $5.00 overpaid on
            // every single online payment; dropping the fee instead would leave $5.00 of the
            // customer's money with no record anywhere. So it is stored beside the amount.
            var processingFee = attempt?.ProcessingFee ?? 0m;
            if (processingFee > charged) processingFee = 0m;   // defensive: never a negative credit
            var amount = InvoiceCalculator.Round2(charged - processingFee);
            if (amount <= 0m) return result;

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var invoice = await _context.CommercialInvoices
                    .Include(i => i.Items)
                    .FirstOrDefaultAsync(i => i.Id == invoiceId.Value);

                if (invoice == null)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError("Invoice {InvoiceId} vanished while settling {PaymentIntentId}.",
                        invoiceId, paymentIntentId);
                    return result;
                }

                var wasPaid = invoice.Status == InvoiceStatus.Paid;

                var payment = new CommercialInvoicePayment
                {
                    CommercialInvoiceId = invoice.Id,
                    Amount = amount,
                    ProcessingFee = processingFee,
                    PaymentDate = DateTime.UtcNow.Date,
                    PaymentMethod = attempt?.PaymentMethod ?? InvoicePaymentRecordMethod.AchBankTransfer,
                    Provider = InvoicePaymentProvider.Stripe,
                    StripePaymentIntentId = paymentIntentId,
                    StripeChargeId = chargeId,
                    TransactionReference = chargeId ?? paymentIntentId,
                    // No admin behind this row — it names itself instead.
                    RecordedByUserId = null,
                    RecordedByLabel = "Stripe webhook",
                    InternalNote = string.IsNullOrWhiteSpace(sourceLabel) ? null : $"Paid from {sourceLabel}",
                    CreatedAt = DateTime.UtcNow
                };

                _context.CommercialInvoicePayments.Add(payment);
                await _context.SaveChangesAsync();

                // Re-summed from the rows inside the transaction, so a manual payment landing at
                // the same moment is included rather than clobbered.
                var amounts = await _context.CommercialInvoicePayments
                    .Where(p => p.CommercialInvoiceId == invoice.Id)
                    .Select(p => p.Amount)
                    .ToListAsync();

                invoice.AmountPaid = InvoiceCalculator.ResolveAmountPaid(amounts);
                invoice.UpdatedAt = DateTime.UtcNow;
                InvoiceService.RecalculateStatus(invoice);

                if (attempt != null)
                {
                    attempt.Status = InvoicePaymentAttemptStatus.Succeeded;
                    attempt.CommercialInvoicePaymentId = payment.Id;
                    attempt.CompletedAt = DateTime.UtcNow;
                    attempt.UpdatedAt = DateTime.UtcNow;
                    if (!string.IsNullOrWhiteSpace(sourceLabel)) attempt.PaymentSourceLabel = sourceLabel;
                }

                await _context.SaveChangesAsync();

                var overpayment = InvoiceCalculator.ResolveOverpayment(invoice.Total, invoice.AmountPaid);

                _context.CommercialInvoiceActivityLogs.Add(new CommercialInvoiceActivityLog
                {
                    CommercialInvoiceId = invoice.Id,
                    Action = "stripe_ach_succeeded",
                    Description =
                        $"Stripe confirmed a {InvoiceCheckoutService.MethodLabel(payment.PaymentMethod)} "
                        + $"payment of {amount:C}."
                        + (processingFee > 0m
                            ? $" A {Helpers.Commercial.AchProcessingFeeCalculator.CustomerFacingLabel} "
                              + $"of {processingFee:C} was collected on top ({charged:C} debited in total); "
                              + "it is not applied to the invoice."
                            : string.Empty)
                        + (string.IsNullOrWhiteSpace(sourceLabel) ? "" : $" Paid from {sourceLabel}."),
                    ActorName = "Stripe",
                    CreatedAt = DateTime.UtcNow
                });

                if (!wasPaid && invoice.Status == InvoiceStatus.Paid)
                {
                    _context.CommercialInvoiceActivityLogs.Add(new CommercialInvoiceActivityLog
                    {
                        CommercialInvoiceId = invoice.Id,
                        Action = "invoice_paid",
                        Description = "Invoice paid in full.",
                        ActorName = "System",
                        CreatedAt = DateTime.UtcNow
                    });
                    result.InvoiceBecamePaid = true;
                }

                // Surfaced rather than swallowed: this normally means a manual payment and a
                // Stripe settlement covered the same money, and a human has to decide what to
                // refund.
                if (overpayment > 0m)
                {
                    // Was any of the other money recorded by hand? If so this is not merely an
                    // overpayment, it is the SPECIFIC accident of an invoice marked paid manually
                    // while a Stripe debit was still settling - and it is named as such, because
                    // "possible duplicate payment" tells an admin what to go and check while
                    // "overpaid" leaves them guessing.
                    var manuallyPaid = await _context.CommercialInvoicePayments
                        .AnyAsync(p => p.CommercialInvoiceId == invoice.Id
                                       && p.Provider == InvoicePaymentProvider.Manual
                                       && !p.IsReversal);

                    _context.CommercialInvoiceActivityLogs.Add(new CommercialInvoiceActivityLog
                    {
                        CommercialInvoiceId = invoice.Id,
                        Action = manuallyPaid ? "payment_duplicate_suspected" : "payment_overpaid",
                        Description = manuallyPaid
                            ? $"POSSIBLE DUPLICATE PAYMENT. This invoice was already marked paid manually, "
                              + $"and Stripe has now settled {amount:C} as well - it is overpaid by "
                              + $"{overpayment:C}. Neither payment has been altered: both are real money "
                              + "that arrived. Review the payment history and refund whichever is the "
                              + "duplicate."
                            : $"This invoice is overpaid by {overpayment:C} after the Stripe payment was "
                              + "recorded. Please review and refund.",
                        ActorName = "System",
                        CreatedAt = DateTime.UtcNow
                    });
                    result.Overpaid = true;

                    _logger.LogWarning(
                        "Invoice {Number} is overpaid by {Overpayment} after Stripe settlement "
                        + "{PaymentIntentId}. Manual payment present: {ManuallyPaid}.",
                        invoice.InvoiceNumber, overpayment, paymentIntentId, manuallyPaid);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                result.Recorded = true;

                // The cleanings this invoice covers become Active now the money has actually
                // settled — AFTER the commit, so the activation can never be part of a
                // transaction that later rolls back and leaves orders claiming to be paid.
                //
                // THE SAME CALL "Mark as Paid" MAKES. One activation path, so a manual bank
                // transfer and a Stripe ACH settlement produce identical results. It is idempotent
                // (Order.InvoicePaidAt is the marker) and swallows its own failures, so a retried
                // delivery does nothing and a bookkeeping problem never makes Stripe retry a
                // payment that already succeeded.
                if (result.InvoiceBecamePaid && result.InvoiceId.HasValue)
                    await _orderLinks.ActivateCoveredOrdersAsync(result.InvoiceId.Value);

                return result;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Two deliveries of the same event raced and the other won. That is the unique
                // index doing its job, not an error — the payment IS recorded.
                await transaction.RollbackAsync();
                _logger.LogInformation(
                    "Concurrent delivery for PaymentIntent {PaymentIntentId}; the payment is already recorded.",
                    paymentIntentId);
                result.AlreadyProcessed = true;
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        // ── payment_intent.payment_failed ─────────────────────────────────────────────────────

        /// <summary>
        /// The debit failed — insufficient funds, a closed account, a revoked mandate.
        ///
        /// Marks the attempt Failed, which by itself re-enables the pay button: the guard looks
        /// for in-flight attempts, and a failed one is not in flight. No payment row is written and
        /// the invoice's own status is untouched, so normal Overdue rules simply resume.
        /// </summary>
        public async Task HandleFailedAsync(
            string paymentIntentId, string? failureCode, string? failureMessage,
            IDictionary<string, string>? metadata)
        {
            var attempt = await FindAttemptAsync(null, paymentIntentId, metadata);
            if (attempt == null) return;

            attempt.Status = InvoicePaymentAttemptStatus.Failed;
            attempt.FailureCode = Truncate(failureCode, 100);
            // Stored for admins only. The customer sees fixed wording — a processor's internal
            // message is not something to put in front of them.
            attempt.FailureMessage = Truncate(failureMessage, 500);
            attempt.CompletedAt = DateTime.UtcNow;
            attempt.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _invoices.LogActivityAsync(attempt.CommercialInvoiceId, "stripe_ach_failed",
                $"The customer's bank payment of {attempt.Amount:C} was unsuccessful"
                + (string.IsNullOrWhiteSpace(failureCode) ? "." : $" ({failureCode}).")
                + " They can try again or pay by manual transfer.",
                null, "Stripe");
        }

        // ── checkout.session.expired ──────────────────────────────────────────────────────────

        /// <summary>The customer never finished. Frees the invoice for another attempt.</summary>
        public async Task HandleCheckoutExpiredAsync(
            string sessionId, IDictionary<string, string>? metadata)
        {
            var attempt = await FindAttemptAsync(sessionId, null, metadata);
            if (attempt == null) return;

            // Never expire something already settled or in flight at the PaymentIntent stage.
            if (attempt.Status != InvoicePaymentAttemptStatus.CheckoutOpen
                && attempt.Status != InvoicePaymentAttemptStatus.Created)
                return;

            attempt.Status = InvoicePaymentAttemptStatus.Expired;
            attempt.CompletedAt = DateTime.UtcNow;
            attempt.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// An ACH debit that had been authorized has failed on settlement — returned, insufficient
        /// funds, account closed. Days may have passed since the customer pressed the button.
        ///
        /// NO MONEY IS WRITTEN, and the invoice's own status is deliberately not touched: with no
        /// payment row the balance is unchanged, so <c>InvoiceStatusPolicy</c> already reads it as
        /// Sent / Viewed / Overdue and the pay button returns on its own. The attempt keeps the
        /// failure so the history explains the gap.
        /// </summary>
        public async Task HandleAsyncPaymentFailedAsync(
            string sessionId, string? paymentIntentId, IDictionary<string, string>? metadata)
        {
            var attempt = await FindAttemptAsync(sessionId, paymentIntentId, metadata);
            if (attempt == null)
            {
                _logger.LogWarning(
                    "async_payment_failed for {SessionId} matched no commercial payment attempt.",
                    sessionId);
                return;
            }

            // A settled attempt is never dragged backwards. Stripe can deliver out of order, and a
            // succeeded payment that later genuinely reverses is an ACH RETURN - corrected by a
            // reversing entry on the ledger, not by rewriting this row.
            if (attempt.Status == InvoicePaymentAttemptStatus.Succeeded) return;

            attempt.Status = InvoicePaymentAttemptStatus.Failed;
            attempt.StripePaymentIntentId ??= paymentIntentId;
            attempt.FailureMessage = "The bank rejected the ACH debit after it was authorized.";
            attempt.CompletedAt = DateTime.UtcNow;
            attempt.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _invoices.LogActivityAsync(attempt.CommercialInvoiceId, "stripe_ach_failed",
                $"The bank payment of {attempt.TotalCharged:C} failed to settle. The invoice is "
                + "payable again and no money was recorded.",
                null, "Stripe");
        }

        // ── Resolution ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Finds the attempt a Stripe object belongs to.
        ///
        /// Stripe ids are tried FIRST and metadata only as a fallback, deliberately: an id is
        /// something Stripe itself assigned, whereas metadata is a string we attached and could in
        /// principle be stale or hand-edited in the dashboard. Metadata is never trusted on its
        /// own — whatever it points at is loaded and checked against the database.
        /// </summary>
        private async Task<CommercialInvoicePaymentAttempt?> FindAttemptAsync(
            string? sessionId, string? paymentIntentId, IDictionary<string, string>? metadata)
        {
            if (!string.IsNullOrEmpty(paymentIntentId))
            {
                var byIntent = await _context.CommercialInvoicePaymentAttempts
                    .FirstOrDefaultAsync(a => a.StripePaymentIntentId == paymentIntentId);
                if (byIntent != null) return byIntent;
            }

            if (!string.IsNullOrEmpty(sessionId))
            {
                var bySession = await _context.CommercialInvoicePaymentAttempts
                    .FirstOrDefaultAsync(a => a.StripeCheckoutSessionId == sessionId);
                if (bySession != null) return bySession;
            }

            if (metadata != null
                && metadata.TryGetValue(StripeCommercialInvoiceMetadata.AttemptIdKey, out var raw)
                && int.TryParse(raw, out var attemptId))
            {
                var byMetadata = await _context.CommercialInvoicePaymentAttempts
                    .FirstOrDefaultAsync(a => a.Id == attemptId);

                // Validated against the invoice the metadata also claims, so a stale or edited
                // value cannot attach a payment to somebody else's invoice.
                var claimedInvoiceId = ResolveInvoiceIdFromMetadata(metadata);
                if (byMetadata != null
                    && (claimedInvoiceId == null || byMetadata.CommercialInvoiceId == claimedInvoiceId))
                {
                    return byMetadata;
                }
            }

            return null;
        }

        private static int? ResolveInvoiceIdFromMetadata(IDictionary<string, string>? metadata)
        {
            if (metadata != null
                && metadata.TryGetValue(StripeCommercialInvoiceMetadata.InvoiceIdKey, out var raw)
                && int.TryParse(raw, out var id))
            {
                return id;
            }
            return null;
        }

        /// <summary>
        /// A safe label for the funding source: bank name and last four only, which is all Stripe
        /// exposes and all anyone needs. Full account and routing numbers are never stored.
        /// </summary>
        public static string? BuildSourceLabel(PaymentIntent? intent)
        {
            var details = intent?.LatestCharge?.PaymentMethodDetails;

            if (details?.UsBankAccount != null)
            {
                var bank = details.UsBankAccount.BankName;
                var last4 = details.UsBankAccount.Last4;
                if (string.IsNullOrWhiteSpace(bank) && string.IsNullOrWhiteSpace(last4)) return null;
                return string.IsNullOrWhiteSpace(last4) ? bank : $"{bank} ••••{last4}".Trim();
            }

            if (details?.Card != null)
            {
                var brand = details.Card.Brand;
                var last4 = details.Card.Last4;
                if (string.IsNullOrWhiteSpace(last4)) return brand;
                return $"{brand} ••••{last4}".Trim();
            }

            return null;
        }

        private static bool IsUniqueViolation(DbUpdateException ex) =>
            ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
    }
}
