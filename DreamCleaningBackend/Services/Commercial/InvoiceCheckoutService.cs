using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// Raised when online payment cannot be offered right now for a reason the CUSTOMER should
    /// see — a disabled method, a Stripe outage. The public endpoint turns it into a calm message
    /// pointing at manual ACH, never a stack trace or a Stripe error code.
    /// </summary>
    public class InvoiceCheckoutUnavailableException : Exception
    {
        public InvoiceCheckoutUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// Creates Stripe Checkout Sessions for commercial invoices.
    ///
    /// STRIPE-HOSTED CHECKOUT, NOT A CUSTOM FORM. The customer's bank credentials never touch a
    /// Dream Cleaning server: Stripe collects the account, handles micro-deposit or instant
    /// verification, and captures the ACH authorization mandate. Building our own routing/account
    /// form would move all of that liability here for no benefit.
    ///
    /// Checkout rather than the Payment Element because the residential side has no Payment
    /// Element architecture to reuse — it uses bare PaymentIntents with a custom card form — and
    /// ACH needs the mandate and verification UI that Checkout provides out of the box.
    ///
    /// THE AMOUNT IS NEVER TAKEN FROM THE BROWSER. It is read from the invoice's own
    /// <c>BalanceDue</c> at the moment of creation and converted to integer cents. The public
    /// endpoint accepts no amount parameter at all, so a tampered request cannot express one.
    /// </summary>
    public class InvoiceCheckoutService
    {
        private readonly ApplicationDbContext _context;
        private readonly InvoiceService _invoices;
        private readonly BillingSettingsService _billing;
        private readonly IConfiguration _configuration;
        private readonly ILogger<InvoiceCheckoutService> _logger;

        /// <summary>Stripe's own cap for ACH debits; a larger invoice must be paid manually.</summary>
        private const decimal MaxAchAmount = 1_000_000m;

        public InvoiceCheckoutService(
            ApplicationDbContext context,
            InvoiceService invoices,
            BillingSettingsService billing,
            IConfiguration configuration,
            ILogger<InvoiceCheckoutService> logger)
        {
            _context = context;
            _invoices = invoices;
            _billing = billing;
            _configuration = configuration;
            _logger = logger;
        }

        private string FrontendUrl =>
            (_configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com").TrimEnd('/');

        /// <summary>
        /// Dollars to integer cents, the unit Stripe takes. Via decimal throughout —
        /// <c>(long)(925.43d * 100)</c> is 92542 in binary floating point, which would undercharge
        /// by a cent on a real invoice.
        /// </summary>
        public static long ToCents(decimal amount) =>
            (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

        // ── Creating a session ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates everything, then creates a Checkout Session and the attempt row that tracks
        /// it.
        ///
        /// The validation order matters: state checks that are the CUSTOMER's business come first
        /// and produce plain refusals, then configuration checks that are the BUSINESS's problem
        /// produce the "temporarily unavailable" path so the customer is steered to manual ACH
        /// rather than shown a fault.
        /// </summary>
        public async Task<(CommercialInvoicePaymentAttempt Attempt, string CheckoutUrl)> CreateCheckoutAsync(
            CommercialInvoice invoice, InvoicePaymentRecordMethod method, string? clientIp)
        {
            var settings = await _billing.GetOrCreateAsync();

            // ── Invoice must be payable at all ──
            if (invoice.Status == InvoiceStatus.Draft)
                throw new InvoiceWorkflowException("This invoice is not available for payment.");

            if (invoice.Status == InvoiceStatus.Void)
                throw new InvoiceWorkflowException("This invoice has been cancelled and cannot be paid.");

            if (invoice.BalanceDue <= 0m)
                throw new InvoiceWorkflowException("This invoice is already paid in full.");

            if (!string.Equals(invoice.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                throw new InvoiceWorkflowException("Online payment is only available for USD invoices.");

            // ── The method must actually be switched on ──
            var methodEnabled = method switch
            {
                InvoicePaymentRecordMethod.AchBankTransfer => settings.StripeAchEnabled,
                InvoicePaymentRecordMethod.Card => settings.StripeCardEnabled,
                _ => false
            };

            // Enforced HERE, not only in the UI. Hiding a button is presentation; a hand-rolled
            // POST would otherwise still open a card session on an ACH-only invoice.
            if (!methodEnabled)
                throw new InvoiceCheckoutUnavailableException(
                    "Online bank payment is temporarily unavailable. Please use the alternative payment method.");

            if (method == InvoicePaymentRecordMethod.AchBankTransfer && invoice.BalanceDue > MaxAchAmount)
                throw new InvoiceCheckoutUnavailableException(
                    "This invoice is above the limit for online bank payment. Please use the alternative payment method.");

            // ── No conflicting attempt already in flight ──
            var inFlight = await FindInFlightAttemptAsync(invoice.Id);
            if (inFlight != null)
            {
                throw new InvoiceWorkflowException(
                    "A payment for this invoice is already being processed. You do not need to "
                    + "submit another payment while it completes.");
            }

            var amount = invoice.BalanceDue;
            var attempt = new CommercialInvoicePaymentAttempt
            {
                CommercialInvoiceId = invoice.Id,
                Provider = InvoicePaymentProvider.Stripe,
                PaymentMethod = method,
                Amount = amount,
                Currency = "USD",
                Status = InvoicePaymentAttemptStatus.Created,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.CommercialInvoicePaymentAttempts.Add(attempt);
            await _context.SaveChangesAsync();

            try
            {
                var session = await CreateStripeSessionAsync(invoice, attempt, method, amount, settings);

                attempt.StripeCheckoutSessionId = session.Id;
                attempt.StripePaymentIntentId = session.PaymentIntentId;
                attempt.Status = InvoicePaymentAttemptStatus.CheckoutOpen;
                attempt.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                await _invoices.LogActivityAsync(invoice.Id, "stripe_checkout_created",
                    $"Customer started a {MethodLabel(method)} payment of {amount:C} online.",
                    null, "Customer", clientIp);

                // Stripe always returns a URL for a session created in "payment" mode; treating a
                // missing one as an outage is safer than handing the browser an empty redirect.
                if (string.IsNullOrWhiteSpace(session.Url))
                {
                    throw new InvoiceCheckoutUnavailableException(
                        "Online bank payment is temporarily unavailable. Please use the alternative payment method.");
                }

                return (attempt, session.Url);
            }
            catch (StripeException ex)
            {
                // The session never opened, so the attempt must not sit around looking in-flight
                // and blocking the customer from trying again.
                attempt.Status = InvoicePaymentAttemptStatus.Failed;
                attempt.FailureCode = ex.StripeError?.Code;
                attempt.FailureMessage = Truncate(ex.Message, 500);
                attempt.CompletedAt = DateTime.UtcNow;
                attempt.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                // Logged in full for the admin; the customer gets the calm message below.
                _logger.LogError(ex,
                    "Stripe Checkout creation failed for invoice {Number}: {Code} {Message}",
                    invoice.InvoiceNumber, ex.StripeError?.Code, ex.Message);

                throw new InvoiceCheckoutUnavailableException(
                    "Online bank payment is temporarily unavailable. Please use the alternative payment method.");
            }
        }

        private async Task<Session> CreateStripeSessionAsync(
            CommercialInvoice invoice,
            CommercialInvoicePaymentAttempt attempt,
            InvoicePaymentRecordMethod method,
            decimal amount,
            BillingSettings settings)
        {
            var customerId = await ResolveStripeCustomerAsync(invoice);

            // Metadata is what the webhook reconciles by. "type" matches the discriminator the
            // existing residential webhook already switches on (booking / order_update /
            // gift_card), so commercial events are distinguishable at a glance and no second
            // webhook system is needed.
            //
            // Internal ids are fine here — Stripe metadata is not the public invoice URL, which is
            // keyed by an unrelated high-entropy token. Bank details never go in it.
            var metadata = new Dictionary<string, string>
            {
                ["type"] = StripeCommercialInvoiceMetadata.TypeValue,
                ["commercial_invoice_id"] = invoice.Id.ToString(),
                ["invoice_number"] = invoice.InvoiceNumber,
                ["commercial_client_id"] = invoice.ContractClientId.ToString(),
                ["payment_attempt_id"] = attempt.Id.ToString()
            };

            var options = new SessionCreateOptions
            {
                Mode = "payment",
                PaymentMethodTypes = new List<string>
                {
                    method == InvoicePaymentRecordMethod.Card ? "card" : "us_bank_account"
                },
                LineItems = new List<SessionLineItemOptions>
                {
                    new()
                    {
                        Quantity = 1,
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            Currency = "usd",
                            UnitAmount = ToCents(amount),
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name = $"Invoice {invoice.InvoiceNumber}",
                                Description = BuildLineDescription(invoice)
                            }
                        }
                    }
                },
                // Both carry the PUBLIC TOKEN, never the row id — the customer returns to the same
                // page they came from, addressed the only way that page is addressable.
                //
                // ?payment=processing is a HINT FOR THE UI, not a claim. The page re-fetches its
                // state from the backend on return; nothing is ever marked paid from a redirect.
                SuccessUrl = $"{FrontendUrl}/invoice/{invoice.PublicToken}?payment=processing",
                CancelUrl = $"{FrontendUrl}/invoice/{invoice.PublicToken}?payment=cancelled",
                Metadata = metadata,
                PaymentIntentData = new SessionPaymentIntentDataOptions
                {
                    // Repeated onto the PaymentIntent: payment_intent.* events do not carry the
                    // session's metadata, and those are the events that actually move money.
                    Metadata = metadata,
                    Description = $"Dream Cleaning NYC invoice {invoice.InvoiceNumber}"
                },
                // Stripe emails its own receipt only if we ask; we send our own from the invoice
                // pipeline, so the customer gets one confirmation, not two that disagree.
                ExpiresAt = DateTime.UtcNow.AddHours(23)
            };

            if (!string.IsNullOrEmpty(customerId))
                options.Customer = customerId;
            else if (!string.IsNullOrWhiteSpace(invoice.Client?.NoticeEmail))
                options.CustomerEmail = invoice.Client!.NoticeEmail;

            var service = new SessionService();
            return await service.CreateAsync(options);
        }

        private static string BuildLineDescription(CommercialInvoice invoice)
        {
            var parts = new List<string> { "Commercial cleaning services" };
            if (!string.IsNullOrWhiteSpace(invoice.ServiceAddress)) parts.Add(invoice.ServiceAddress!);
            var text = string.Join(" — ", parts);
            return text.Length > 200 ? text[..200] : text;
        }

        // ── Stripe Customer for the COMPANY ───────────────────────────────────────────────────

        /// <summary>
        /// Reuses the client's Stripe Customer, creating one only when there is none.
        ///
        /// Kept on <see cref="ContractClient"/> rather than borrowing the residential
        /// <c>User.StripeCustomerId</c>: those identify different things, and most commercial
        /// clients have no user account at all. A failure here is NOT fatal — Checkout works
        /// perfectly well with a bare email, and refusing a payment because a Customer record
        /// could not be created would be the wrong trade.
        /// </summary>
        private async Task<string?> ResolveStripeCustomerAsync(CommercialInvoice invoice)
        {
            var client = invoice.Client
                ?? await _context.ContractClients.FirstOrDefaultAsync(c => c.Id == invoice.ContractClientId);

            if (client == null) return null;

            var customerService = new CustomerService();

            if (!string.IsNullOrEmpty(client.StripeCustomerId))
            {
                try
                {
                    var existing = await customerService.GetAsync(client.StripeCustomerId);
                    if (existing != null && existing.Deleted != true)
                        return client.StripeCustomerId;
                }
                catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
                {
                    // Deleted from the dashboard — recreate rather than failing forever. Same
                    // self-healing shape as StripeService.CreateOrGetCustomerAsync.
                    _logger.LogWarning(
                        "Stripe customer {CustomerId} for commercial client {ClientId} no longer exists — recreating.",
                        client.StripeCustomerId, client.Id);
                }
            }

            try
            {
                var contact = await _invoices.ResolveBillingContactAsync(client.Id);

                var customer = await customerService.CreateAsync(new CustomerCreateOptions
                {
                    Email = contact?.Email ?? client.NoticeEmail,
                    Name = client.LegalEntityName,
                    Description = $"Commercial client #{client.Id}",
                    Metadata = new Dictionary<string, string>
                    {
                        ["type"] = "commercial_client",
                        ["commercial_client_id"] = client.Id.ToString()
                    }
                });

                client.StripeCustomerId = customer.Id;
                await _context.SaveChangesAsync();
                return customer.Id;
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex,
                    "Could not create a Stripe customer for commercial client {ClientId}; continuing with email only.",
                    client.Id);
                return null;
            }
        }

        // ── In-flight attempts ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The attempt currently holding money in flight for this invoice, if any.
        ///
        /// This is what stops a customer paying twice. ACH takes days to settle, during which the
        /// invoice still reads as unpaid — without this guard an impatient customer would quite
        /// reasonably press the button again and be debited a second time.
        ///
        /// A CheckoutOpen attempt older than a day is ignored: Stripe expires sessions at 24h, and
        /// the expiry webhook may never arrive if the customer simply closed the tab. Leaving one
        /// to block payment forever would be worse than the duplicate it prevents.
        /// </summary>
        public async Task<CommercialInvoicePaymentAttempt?> FindInFlightAttemptAsync(int invoiceId)
        {
            var attempts = await _context.CommercialInvoicePaymentAttempts
                .Where(a => a.CommercialInvoiceId == invoiceId
                            && (a.Status == InvoicePaymentAttemptStatus.CheckoutOpen
                                || a.Status == InvoicePaymentAttemptStatus.Processing))
                .OrderByDescending(a => a.Id)
                .ToListAsync();

            var cutoff = DateTime.UtcNow.AddHours(-24);

            return attempts.FirstOrDefault(a =>
                a.Status == InvoicePaymentAttemptStatus.Processing
                || a.CreatedAt >= cutoff);
        }

        /// <summary>
        /// Sweeps stale CheckoutOpen attempts to Expired so the button unblocks even if Stripe's
        /// expiry webhook never arrives. Called on read from the public page — cheap, and it means
        /// the page a customer is looking at is the thing that unsticks them.
        /// </summary>
        public async Task ExpireStaleAttemptsAsync(int invoiceId)
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);

            var stale = await _context.CommercialInvoicePaymentAttempts
                .Where(a => a.CommercialInvoiceId == invoiceId
                            && a.Status == InvoicePaymentAttemptStatus.CheckoutOpen
                            && a.CreatedAt < cutoff)
                .ToListAsync();

            if (stale.Count == 0) return;

            foreach (var attempt in stale)
            {
                attempt.Status = InvoicePaymentAttemptStatus.Expired;
                attempt.CompletedAt = DateTime.UtcNow;
                attempt.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
        }

        public static string MethodLabel(InvoicePaymentRecordMethod method) => method switch
        {
            InvoicePaymentRecordMethod.AchBankTransfer => "bank (ACH)",
            InvoicePaymentRecordMethod.Card => "card",
            _ => "online"
        };

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..max];
    }

    /// <summary>
    /// The metadata contract between Checkout creation and the webhook.
    ///
    /// One place, because the two halves live in different files and a typo in either would make a
    /// paid invoice silently fail to reconcile — the webhook would log "unknown payment type" and
    /// the customer's money would sit unmatched.
    /// </summary>
    public static class StripeCommercialInvoiceMetadata
    {
        /// <summary>
        /// The <c>type</c> discriminator. Sits alongside the residential values the existing
        /// webhook already switches on — "booking", "order_update", "gift_card" — so commercial
        /// payments route through the same endpoint while staying clearly distinguishable.
        /// </summary>
        public const string TypeValue = "commercial_invoice";

        public const string InvoiceIdKey = "commercial_invoice_id";
        public const string InvoiceNumberKey = "invoice_number";
        public const string ClientIdKey = "commercial_client_id";
        public const string AttemptIdKey = "payment_attempt_id";

        /// <summary>True when this Stripe object belongs to the commercial invoicing flow.</summary>
        public static bool IsCommercialInvoice(IDictionary<string, string>? metadata) =>
            metadata != null
            && metadata.TryGetValue("type", out var type)
            && type == TypeValue;
    }
}
