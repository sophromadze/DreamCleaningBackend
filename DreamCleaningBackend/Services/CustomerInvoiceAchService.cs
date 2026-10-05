using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;

namespace DreamCleaningBackend.Services
{
    /// <summary>The ACH figures and state the public regular-invoice page renders.</summary>
    public record CustomerInvoiceAchState(
        bool Available,
        bool Processing,
        decimal ProcessingFee,
        decimal TotalCharge);

    /// <summary>What the webhook needs back after crediting an ACH settlement.</summary>
    public record CustomerInvoiceAchSettlement(bool Applied, int OrderId, bool OrderNowFullyPaid);

    /// <summary>
    /// Online BANK payment (Stripe ACH Direct Debit) for REGULAR customer invoices (DCR-…),
    /// 2026-09. The commercial invoices already had it; this brings the same lower-fee option to
    /// the ordinary customer's invoice page.
    ///
    /// Deliberately built on the pieces that already decide the money, rather than a second copy:
    ///
    ///  - FEES and the on/off switch come from the one <see cref="BillingSettings"/> row
    ///    (<c>StripeAchEnabled</c>, <see cref="AchProcessingFeeCalculator"/>), so the owner prices
    ///    ACH once for both kinds of invoice.
    ///  - The money is credited through <see cref="IOrderPartialPaymentService.SettleAsync"/> —
    ///    the one writer of <c>Order.AmountPaid</c> — against the invoice's own part-payment
    ///    request, exactly where a card payment of the same invoice lands.
    ///
    /// ACH IS ASYNCHRONOUS. <c>checkout.session.completed</c> is an authorization, not a payment:
    /// nothing is credited until <c>payment_intent.succeeded</c> (or
    /// <c>checkout.session.async_payment_succeeded</c>) arrives days later. While an attempt is
    /// Processing, the invoice's card button, a second bank payment and an admin's manual record
    /// are all refused, because each would collect the same money twice.
    ///
    /// Only Full and Split invoices (backed by a request) are offered ACH. An Additional invoice
    /// bills the order-edit top-up, which has no request to credit; it keeps card + manual
    /// bank transfer.
    /// </summary>
    public class CustomerInvoiceAchService
    {
        /// <summary>The Stripe metadata <c>type</c> — its own discriminator, so the residential
        /// part-payment handler (which credits the gross amount) never sees these intents.</summary>
        public const string StripeMetadataType = "customer_invoice_ach";

        public const string InvoiceIdKey = "customer_invoice_id";
        public const string AttemptIdKey = "payment_attempt_id";
        public const string OrderIdKey = "order_id";
        public const string RequestIdKey = "partial_payment_id";

        /// <summary>Stripe's cap for one ACH debit.</summary>
        private const decimal MaxAchAmount = 1_000_000m;

        private const string UnavailableMessage =
            "Online bank payment is temporarily unavailable. Please pay by card or bank transfer instead.";

        private readonly ApplicationDbContext _context;
        private readonly BillingSettingsService _billing;
        private readonly IOrderPartialPaymentService _partialPayments;
        private readonly IConfiguration _configuration;
        private readonly ILogger<CustomerInvoiceAchService> _logger;
        private readonly IEmailService? _email;

        public CustomerInvoiceAchService(
            ApplicationDbContext context,
            BillingSettingsService billing,
            IOrderPartialPaymentService partialPayments,
            IConfiguration configuration,
            ILogger<CustomerInvoiceAchService> logger,
            IEmailService? email = null)
        {
            _email = email;
            _context = context;
            _billing = billing;
            _partialPayments = partialPayments;
            _configuration = configuration;
            _logger = logger;
        }

        private string FrontendUrl =>
            (_configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com").TrimEnd('/');

        public static bool IsCustomerInvoiceAch(IDictionary<string, string>? metadata) =>
            metadata != null && metadata.TryGetValue("type", out var type) && type == StripeMetadataType;

        // ── What the public page shows ────────────────────────────────────────────────────────

        /// <summary>
        /// Whether "Pay from bank" can be offered, whether a bank payment is already moving, and
        /// the fee quote for <paramref name="amountDue"/>. The page only DISPLAYS the fee; the
        /// checkout endpoint recomputes it from the invoice's own balance.
        /// </summary>
        public Task<CustomerInvoiceAchState> GetStateAsync(
            CustomerInvoice invoice, decimal amountDue, bool payable, BillingSettings settings, CancellationToken ct = default) =>
            ComputeStateAsync(_context, invoice, amountDue, payable, settings, ct);

        /// <summary>Static so <c>CustomerInvoiceService.GetPublicAsync</c> can answer it with its
        /// own context — the one rule, without a second service in that constructor.</summary>
        public static async Task<CustomerInvoiceAchState> ComputeStateAsync(
            ApplicationDbContext context, CustomerInvoice invoice, decimal amountDue, bool payable,
            BillingSettings settings, CancellationToken ct = default)
        {
            var processing = await context.CustomerInvoicePaymentAttempts.AnyAsync(a =>
                a.CustomerInvoiceId == invoice.Id && a.Status == CustomerInvoicePaymentAttemptStatus.Processing, ct);
            var available = payable
                && !processing
                && settings.StripeAchEnabled
                && invoice.Kind != CustomerInvoiceKind.Additional
                && invoice.OrderPartialPaymentId.HasValue
                && invoice.VoidedAt == null
                && amountDue >= OrderBalance.StripeMinimumChargeAmount
                && amountDue <= MaxAchAmount;

            // No customer-facing ACH fee on a REGULAR invoice (owner's decision, 2026-09-29): the
            // "ACH Processing Fee" is a commercial-client charge only. Stripe's own ACH cost is
            // still counted in statistics (AdminStatisticsController.StripeFeesByOrderAsync).
            var fee = 0m;

            return new CustomerInvoiceAchState(
                available, processing, fee,
                available ? AchProcessingFeeCalculator.ResolveTotalCharge(amountDue, fee) : 0m);
        }

        public Task<bool> IsProcessingForInvoiceAsync(int invoiceId, CancellationToken ct = default) =>
            _context.CustomerInvoicePaymentAttempts.AnyAsync(a =>
                a.CustomerInvoiceId == invoiceId && a.Status == CustomerInvoicePaymentAttemptStatus.Processing, ct);

        /// <summary>A bank payment is settling against this part-payment request — the guard the
        /// card path and the admin's manual record both check.</summary>
        public Task<bool> IsProcessingForRequestAsync(int requestId, CancellationToken ct = default) =>
            _context.CustomerInvoicePaymentAttempts.AnyAsync(a =>
                a.OrderPartialPaymentId == requestId && a.Status == CustomerInvoicePaymentAttemptStatus.Processing, ct);

        // ── Starting a payment ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Opens a Stripe-hosted Checkout Session for the invoice's CURRENT amount due plus the
        /// ACH processing fee. No amount ever comes from the browser.
        /// </summary>
        /// <exception cref="CustomerInvoiceException">The invoice cannot be paid this way; the
        /// message is written for the customer.</exception>
        /// <exception cref="InvoiceCheckoutUnavailableException">Switched off or Stripe is down —
        /// the page falls back to card / manual bank transfer.</exception>
        public async Task<(CustomerInvoicePaymentAttempt Attempt, string CheckoutUrl)> StartCheckoutAsync(
            string token, string? clientIp, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 64)
                throw new CustomerInvoiceException("This invoice link is not valid.");

            var invoice = await _context.CustomerInvoices
                .Include(i => i.Order).ThenInclude(o => o!.User)
                .Include(i => i.OrderPartialPayment)
                .FirstOrDefaultAsync(i => i.PublicToken == token, ct)
                ?? throw new CustomerInvoiceException("This invoice link is not valid.");

            var order = invoice.Order!;
            var request = invoice.OrderPartialPayment;

            if (invoice.Kind == CustomerInvoiceKind.Additional || request == null)
                throw new CustomerInvoiceException("This invoice can be paid by card or bank transfer.");

            var status = CustomerInvoiceStatusPolicy.Resolve(invoice, order, request, null);
            if (!CustomerInvoiceStatusPolicy.IsPayable(status) || request.Status != OrderPartialPaymentStatus.Pending)
                throw new CustomerInvoiceException("This invoice has nothing left to pay.");

            var settings = await _billing.GetOrCreateAsync(ct);
            if (!settings.StripeAchEnabled)
                throw new InvoiceCheckoutUnavailableException(UnavailableMessage);

            var amount = CustomerInvoiceStatusPolicy.AmountDue(invoice, order, request, status);
            if (amount < OrderBalance.StripeMinimumChargeAmount)
                throw new CustomerInvoiceException("This invoice has nothing left to pay.");
            if (amount > MaxAchAmount)
                throw new InvoiceCheckoutUnavailableException(
                    "This invoice is above the limit for online bank payment. Please pay by bank transfer.");

            // Money already moving: a second debit would take it twice. Only PROCESSING blocks —
            // an attempt sitting at CheckoutOpen is usually a closed tab, and is retired below.
            if (await IsProcessingForRequestAsync(request.Id, ct))
                throw new CustomerInvoiceException(
                    "A bank payment for this invoice is already processing. You do not need to pay again.");

            await ExpireOpenSessionsAsync(invoice.Id, "Replaced by a new bank payment.", ct);

            // Regular invoices carry no customer ACH fee — see ComputeStateAsync.
            var fee = 0m;
            var total = amount;

            var attempt = new CustomerInvoicePaymentAttempt
            {
                CustomerInvoiceId = invoice.Id,
                OrderId = order.Id,
                OrderPartialPaymentId = request.Id,
                Amount = amount,
                ProcessingFee = fee,
                TotalCharged = total,
                Status = CustomerInvoicePaymentAttemptStatus.Created,
                ClientIp = clientIp is { Length: > 64 } ? clientIp[..64] : clientIp,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.CustomerInvoicePaymentAttempts.Add(attempt);
            await _context.SaveChangesAsync(ct);

            try
            {
                var session = await CreateSessionAsync(invoice, order, attempt);

                attempt.StripeCheckoutSessionId = session.Id;
                attempt.StripePaymentIntentId = string.IsNullOrWhiteSpace(session.PaymentIntentId) ? null : session.PaymentIntentId;
                attempt.Status = CustomerInvoicePaymentAttemptStatus.CheckoutOpen;
                attempt.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                if (string.IsNullOrWhiteSpace(session.Url))
                    throw new InvoiceCheckoutUnavailableException(UnavailableMessage);

                _logger.LogInformation(
                    "Regular invoice {Number}: bank (ACH) checkout opened for {Amount} + fee {Fee} (attempt {AttemptId}).",
                    invoice.InvoiceNumber, amount, fee, attempt.Id);

                return (attempt, session.Url);
            }
            catch (StripeException ex)
            {
                attempt.Status = CustomerInvoicePaymentAttemptStatus.Failed;
                attempt.FailureCode = Truncate(ex.StripeError?.Code, 100);
                attempt.FailureMessage = Truncate(ex.Message, 500);
                attempt.CompletedAt = DateTime.UtcNow;
                attempt.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                _logger.LogError(ex, "Stripe ACH checkout failed for regular invoice {Number}: {Code} {Message}",
                    invoice.InvoiceNumber, ex.StripeError?.Code, ex.Message);
                throw new InvoiceCheckoutUnavailableException(UnavailableMessage);
            }
        }

        private async Task<Session> CreateSessionAsync(CustomerInvoice invoice, Order order, CustomerInvoicePaymentAttempt attempt)
        {
            // What the webhook reconciles by. Repeated onto the PaymentIntent, because the
            // payment_intent.* events that actually move money do not carry session metadata.
            var metadata = new Dictionary<string, string>
            {
                ["type"] = StripeMetadataType,
                [InvoiceIdKey] = invoice.Id.ToString(),
                ["invoice_number"] = invoice.InvoiceNumber,
                [AttemptIdKey] = attempt.Id.ToString(),
                [OrderIdKey] = order.Id.ToString(),
                [RequestIdKey] = attempt.OrderPartialPaymentId.ToString(),
                [StripeCommercialInvoiceMetadata.BaseAmountKey] = attempt.Amount.ToString("0.00"),
                [StripeCommercialInvoiceMetadata.ProcessingFeeKey] = attempt.ProcessingFee.ToString("0.00")
            };

            // Two lines: the bill, and the named fee — the customer sees on Stripe's page the same
            // figures the invoice page showed them before they clicked.
            var lineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = InvoiceCheckoutService.ToCents(attempt.Amount),
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = $"Invoice {invoice.InvoiceNumber}",
                            Description = $"Cleaning order #{order.Id}"
                        }
                    }
                }
            };
            if (attempt.ProcessingFee > 0m)
            {
                lineItems.Add(new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = InvoiceCheckoutService.ToCents(attempt.ProcessingFee),
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = AchProcessingFeeCalculator.CustomerFacingLabel,
                            Description = "Bank payment processing fee. Not applied to the invoice balance."
                        }
                    }
                });
            }

            var options = new SessionCreateOptions
            {
                Mode = "payment",
                PaymentMethodTypes = new List<string> { "us_bank_account" },
                LineItems = lineItems,
                // ?payment=processing is a hint for the page, never a claim: it re-reads the
                // invoice from the server, and nothing is marked paid from a redirect.
                SuccessUrl = $"{FrontendUrl}/pay-invoice/{invoice.PublicToken}?payment=processing",
                CancelUrl = $"{FrontendUrl}/pay-invoice/{invoice.PublicToken}?payment=cancelled",
                Metadata = metadata,
                PaymentIntentData = new SessionPaymentIntentDataOptions
                {
                    Metadata = metadata,
                    Description = $"Invoice {invoice.InvoiceNumber} (order #{order.Id})"
                },
                ExpiresAt = DateTime.UtcNow.AddHours(23)
            };

            // The residential Stripe customer when the account already has one; an email otherwise.
            if (!string.IsNullOrWhiteSpace(order.User?.StripeCustomerId))
                options.Customer = order.User!.StripeCustomerId;
            else
            {
                var email = NoEmailHelper.ResolveOrderNotificationEmail(order.ContactEmail, order.User);
                if (!string.IsNullOrWhiteSpace(email)) options.CustomerEmail = email;
            }

            try
            {
                return await new SessionService().CreateAsync(options);
            }
            catch (StripeException ex) when (options.Customer != null && ex.StripeError?.Code == "resource_missing")
            {
                // The stored customer was deleted in the dashboard — retry as a guest rather than
                // refusing the payment.
                options.Customer = null;
                var email = NoEmailHelper.ResolveOrderNotificationEmail(order.ContactEmail, order.User);
                if (!string.IsNullOrWhiteSpace(email)) options.CustomerEmail = email;
                return await new SessionService().CreateAsync(options);
            }
        }

        /// <summary>Closes still-open sessions AT STRIPE (so an old tab cannot authorize a second
        /// debit) and locally. A Stripe failure is logged and swallowed — it has usually expired.</summary>
        private async Task ExpireOpenSessionsAsync(int invoiceId, string reason, CancellationToken ct)
        {
            var open = await _context.CustomerInvoicePaymentAttempts
                .Where(a => a.CustomerInvoiceId == invoiceId
                            && (a.Status == CustomerInvoicePaymentAttemptStatus.CheckoutOpen
                                || a.Status == CustomerInvoicePaymentAttemptStatus.Created))
                .ToListAsync(ct);
            if (open.Count == 0) return;

            var sessions = new SessionService();
            foreach (var attempt in open)
            {
                if (!string.IsNullOrWhiteSpace(attempt.StripeCheckoutSessionId))
                {
                    try { await sessions.ExpireAsync(attempt.StripeCheckoutSessionId); }
                    catch (StripeException ex)
                    {
                        _logger.LogInformation(ex, "Could not expire Stripe session {SessionId}; most likely expired already.",
                            attempt.StripeCheckoutSessionId);
                    }
                }
                attempt.Status = CustomerInvoicePaymentAttemptStatus.Expired;
                attempt.FailureMessage = reason;
                attempt.CompletedAt = DateTime.UtcNow;
                attempt.UpdatedAt = DateTime.UtcNow;
            }
            await _context.SaveChangesAsync(ct);
        }

        // ── Webhook side ──────────────────────────────────────────────────────────────────────

        private async Task<CustomerInvoicePaymentAttempt?> FindAttemptAsync(
            IDictionary<string, string>? metadata, string? sessionId, string? paymentIntentId, CancellationToken ct)
        {
            if (metadata != null && metadata.TryGetValue(AttemptIdKey, out var idText) && int.TryParse(idText, out var id))
            {
                var byId = await _context.CustomerInvoicePaymentAttempts.FirstOrDefaultAsync(a => a.Id == id, ct);
                if (byId != null) return byId;
            }
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var bySession = await _context.CustomerInvoicePaymentAttempts
                    .FirstOrDefaultAsync(a => a.StripeCheckoutSessionId == sessionId, ct);
                if (bySession != null) return bySession;
            }
            if (!string.IsNullOrWhiteSpace(paymentIntentId))
                return await _context.CustomerInvoicePaymentAttempts
                    .FirstOrDefaultAsync(a => a.StripePaymentIntentId == paymentIntentId, ct);
            return null;
        }

        /// <summary>The customer authorized the debit. Records the PaymentIntent; credits nothing.</summary>
        public async Task HandleAuthorizedAsync(
            string? sessionId, string? paymentIntentId, IDictionary<string, string>? metadata, CancellationToken ct = default)
        {
            var attempt = await FindAttemptAsync(metadata, sessionId, paymentIntentId, ct);
            if (attempt == null)
            {
                _logger.LogWarning("Regular-invoice ACH authorization for unknown attempt (session {SessionId}, intent {IntentId}).",
                    sessionId, paymentIntentId);
                return;
            }
            if (attempt.Status is CustomerInvoicePaymentAttemptStatus.Succeeded or CustomerInvoicePaymentAttemptStatus.Failed)
                return; // Stripe delivers out of order; a finished attempt never moves back.

            if (!string.IsNullOrWhiteSpace(paymentIntentId)) attempt.StripePaymentIntentId ??= paymentIntentId;
            attempt.Status = CustomerInvoicePaymentAttemptStatus.Processing;
            attempt.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
        }

        /// <summary>
        /// The debit SETTLED. Credits the invoice's part-payment request with the BILL amount
        /// frozen on the attempt — never the gross debit, which includes the ACH fee. Idempotent:
        /// the attempt's Succeeded status and SettleAsync's own conditional claim both stop a
        /// second delivery from moving anything.
        /// </summary>
        public async Task<CustomerInvoiceAchSettlement?> RecordSucceededAsync(
            string paymentIntentId, IDictionary<string, string>? metadata, CancellationToken ct = default)
        {
            var attempt = await FindAttemptAsync(metadata, null, paymentIntentId, ct);
            if (attempt == null)
            {
                _logger.LogError("Regular-invoice ACH payment {IntentId} settled but no attempt matches it — money is unrecorded; review in Stripe.",
                    paymentIntentId);
                return null;
            }
            if (attempt.Status == CustomerInvoicePaymentAttemptStatus.Succeeded)
                return new CustomerInvoiceAchSettlement(false, attempt.OrderId, false);

            var request = await _context.OrderPartialPayments
                .FirstOrDefaultAsync(p => p.Id == attempt.OrderPartialPaymentId, ct);

            // Point the request at THIS intent so SettleAsync finds and claims exactly this row. A
            // request still Pending may carry an abandoned card intent's id; the ACH money is what
            // actually arrived, so it takes the row.
            if (request != null && request.Status != OrderPartialPaymentStatus.Paid && request.PaymentIntentId != paymentIntentId)
            {
                request.PaymentIntentId = paymentIntentId;
                request.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
            }

            var settlement = await _partialPayments.SettleAsync(attempt.OrderId, paymentIntentId, attempt.Amount, ct);

            attempt.StripePaymentIntentId ??= paymentIntentId;
            attempt.Status = CustomerInvoicePaymentAttemptStatus.Succeeded;
            attempt.CompletedAt = DateTime.UtcNow;
            attempt.UpdatedAt = DateTime.UtcNow;
            if (!settlement.Applied && request?.PaymentIntentId != paymentIntentId)
            {
                // The request had already been settled some other way (a manual record slipped
                // past the guard, or a card payment). The bank money is still real: say so loudly
                // for a person to refund or re-apply — never discard it silently.
                attempt.FailureMessage = "Settled after the invoice was already paid — review for a refund.";
                _logger.LogError(
                    "Regular-invoice ACH {IntentId} of {Amount} settled on order {OrderId} after its request {RequestId} was already paid. Review for refund.",
                    paymentIntentId, attempt.Amount, attempt.OrderId, attempt.OrderPartialPaymentId);
            }
            await _context.SaveChangesAsync(ct);

            if (settlement.Applied && settlement.OrderNowFullyPaid)
                await MarkOrderPaidAsync(attempt.OrderId, paymentIntentId, ct);

            // Tell the office, exactly once: only the delivery (webhook or page sync) that actually
            // credited the money gets here with Applied = true.
            if (settlement.Applied)
                await NotifyCompanyAsync(attempt, settlement.OrderNowFullyPaid, ct);

            return new CustomerInvoiceAchSettlement(settlement.Applied, attempt.OrderId, settlement.OrderNowFullyPaid);
        }

        /// <summary>
        /// The bank payment cleared the order's balance: mark it paid exactly as the part-payment
        /// webhook backstop does (IsPaid, PaidAt, Active, the initial-pricing snapshot the order-edit
        /// top-up measures against). Lives here so the webhook AND the page-load sync finish an
        /// order the same way. Idempotent — an order already paid is left alone.
        /// </summary>
        private async Task MarkOrderPaidAsync(int orderId, string paymentIntentId, CancellationToken ct)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order == null || order.IsPaid) return;

            order.IsPaid = true;
            order.PaidAt = DateTime.UtcNow;
            order.Status = OrderStatuses.Active;
            order.PaymentIntentId ??= paymentIntentId;
            if (order.InitialSubTotal == 0 && order.InitialTax == 0 && order.InitialTotal == 0)
            {
                order.InitialSubTotal = order.SubTotal;
                order.InitialTax = order.Tax;
                order.InitialTips = order.Tips;
                order.InitialCompanyDevelopmentTips = order.CompanyDevelopmentTips;
                order.InitialTotal = order.Total;
            }
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Order {OrderId} fully paid by a regular-invoice bank payment {IntentId}.", orderId, paymentIntentId);
        }

        private async Task NotifyCompanyAsync(CustomerInvoicePaymentAttempt attempt, bool orderFullyPaid, CancellationToken ct)
        {
            if (_email == null) return;
            try
            {
                var invoice = await _context.CustomerInvoices
                    .Include(i => i.Order).ThenInclude(o => o!.User)
                    .FirstOrDefaultAsync(i => i.Id == attempt.CustomerInvoiceId, ct);
                var order = invoice?.Order;
                if (invoice == null || order == null) return;

                var name = $"{order.ContactFirstName?.Trim()} {order.ContactLastName?.Trim()}".Trim();
                if (string.IsNullOrWhiteSpace(name)) name = $"{order.User?.FirstName} {order.User?.LastName}".Trim();

                await _email.SendCompanyInvoicePaymentReceivedAsync(order.Id, invoice.InvoiceNumber,
                    NoEmailHelper.ResolveOrderNotificationEmail(order.ContactEmail, order.User),
                    string.IsNullOrWhiteSpace(name) ? "Customer" : name,
                    attempt.Amount, "Bank (ACH) — paid online from the customer's bank", orderFullyPaid);
            }
            catch (Exception ex)
            {
                // A failed notice must never undo a recorded payment.
                _logger.LogError(ex, "ACH payment recorded for attempt {AttemptId}, but the company notification failed.", attempt.Id);
            }
        }

        // ── Page-load sync with Stripe ────────────────────────────────────────────────────────

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> LastSync = new();

        /// <summary>
        /// Asks Stripe directly where this invoice's open bank payments stand and applies the
        /// answer — the SAME transitions the webhook applies, through the same idempotent methods.
        ///
        /// Why: the webhook is the primary path, but when it is late, not subscribed to the
        /// checkout events, or (in development) not forwarded at all, the customer came back from
        /// Stripe to "Awaiting payment" forever. Reading the session is authoritative — it is
        /// Stripe's own record, fetched server-side by id, never anything the browser says.
        /// Throttled per invoice so a polling page cannot hammer the Stripe API.
        /// </summary>
        public async Task SyncWithStripeAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return;
            var invoiceId = await _context.CustomerInvoices
                .Where(i => i.PublicToken == token).Select(i => (int?)i.Id).FirstOrDefaultAsync(ct);
            if (invoiceId == null) return;

            var now = DateTime.UtcNow;
            if (LastSync.TryGetValue(invoiceId.Value, out var last) && now - last < TimeSpan.FromSeconds(4)) return;
            LastSync[invoiceId.Value] = now;

            var open = await _context.CustomerInvoicePaymentAttempts
                .Where(a => a.CustomerInvoiceId == invoiceId.Value
                            && a.StripeCheckoutSessionId != null
                            && (a.Status == CustomerInvoicePaymentAttemptStatus.CheckoutOpen
                                || a.Status == CustomerInvoicePaymentAttemptStatus.Processing))
                .ToListAsync(ct);

            foreach (var attempt in open)
            {
                try
                {
                    var session = await new SessionService().GetAsync(attempt.StripeCheckoutSessionId,
                        new SessionGetOptions { Expand = new List<string> { "payment_intent" } }, cancellationToken: ct);
                    await ApplyStripeStateAsync(attempt, session, ct);
                }
                catch (StripeException ex)
                {
                    _logger.LogWarning(ex, "Could not read Stripe session {SessionId} for attempt {AttemptId}.",
                        attempt.StripeCheckoutSessionId, attempt.Id);
                }
            }
        }

        private async Task ApplyStripeStateAsync(CustomerInvoicePaymentAttempt attempt, Session session, CancellationToken ct)
        {
            var metadata = new Dictionary<string, string> { [AttemptIdKey] = attempt.Id.ToString() };

            if (session.Status == "expired")
            {
                await HandleExpiredAsync(session.Id, metadata, ct);
                return;
            }
            if (session.Status != "complete") return; // still on Stripe's page

            var intent = session.PaymentIntent;
            var intentId = intent?.Id ?? session.PaymentIntentId;
            switch (intent?.Status)
            {
                case "succeeded":
                    await RecordSucceededAsync(intentId!, metadata, ct);
                    break;
                case "requires_payment_method" when intent.LastPaymentError != null:
                case "canceled":
                    await HandleFailedAsync(session.Id, intentId, intent.LastPaymentError?.Code,
                        intent.LastPaymentError?.Message ?? "The bank payment did not go through.", metadata, ct);
                    break;
                default:
                    // processing, requires_action (micro-deposit verification), or unknown: the
                    // customer authorized it; money is not here yet.
                    await HandleAuthorizedAsync(session.Id, intentId, metadata, ct);
                    break;
            }
        }

        /// <summary>The debit failed or was returned. Nothing is credited; the invoice is payable again.</summary>
        public async Task HandleFailedAsync(
            string? sessionId, string? paymentIntentId, string? code, string? message,
            IDictionary<string, string>? metadata, CancellationToken ct = default)
        {
            var attempt = await FindAttemptAsync(metadata, sessionId, paymentIntentId, ct);
            if (attempt == null || attempt.Status == CustomerInvoicePaymentAttemptStatus.Succeeded) return;

            attempt.Status = CustomerInvoicePaymentAttemptStatus.Failed;
            attempt.FailureCode = Truncate(code, 100);
            attempt.FailureMessage = Truncate(message ?? "The bank payment did not go through.", 500);
            attempt.CompletedAt = DateTime.UtcNow;
            attempt.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            _logger.LogWarning("Regular-invoice ACH attempt {AttemptId} failed: {Code} {Message}", attempt.Id, code, message);
        }

        /// <summary>The session lapsed unused.</summary>
        public async Task HandleExpiredAsync(string sessionId, IDictionary<string, string>? metadata, CancellationToken ct = default)
        {
            var attempt = await FindAttemptAsync(metadata, sessionId, null, ct);
            if (attempt == null
                || attempt.Status is not (CustomerInvoicePaymentAttemptStatus.Created or CustomerInvoicePaymentAttemptStatus.CheckoutOpen))
                return;

            attempt.Status = CustomerInvoicePaymentAttemptStatus.Expired;
            attempt.CompletedAt = DateTime.UtcNow;
            attempt.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
        }

        private static string? Truncate(string? value, int max) =>
            value == null ? null : value.Length <= max ? value : value[..max];
    }
}
