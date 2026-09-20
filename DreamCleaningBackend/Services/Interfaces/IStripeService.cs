using DreamCleaningBackend.Models;
using Stripe;

namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>What a saved-card charge did. <see cref="Unknown"/> is the one that matters most.</summary>
    public enum SavedCardChargeOutcome
    {
        /// <summary>Money moved.</summary>
        Succeeded = 0,

        /// <summary>A definite refusal — decline, expired card, detached card. No money moved.</summary>
        Declined = 1,

        /// <summary>The bank wants the customer to authenticate (SCA/3DS). No money moved.</summary>
        RequiresAction = 2,

        /// <summary>Stripe accepted it and is still working (async methods). Money MAY move.</summary>
        Processing = 3,

        /// <summary>
        /// We do not know — the call timed out, the network dropped, Stripe answered 5xx. The charge
        /// MAY have succeeded. Callers must never treat this as a failure: no Backup card, no retry
        /// with a new key, no second payment path, until it has been reconciled.
        /// </summary>
        Unknown = 4
    }

    /// <summary>A saved-card charge request. Amount and card are always resolved by the server.</summary>
    public class SavedCardChargeRequest
    {
        public decimal Amount { get; set; }
        public string CustomerId { get; set; } = string.Empty;
        public string PaymentMethodId { get; set; } = string.Empty;
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>Stable for the life of one attempt — a retry must return the SAME intent.</summary>
        public string IdempotencyKey { get; set; } = string.Empty;

        public string? ReceiptEmail { get; set; }
        public string? Description { get; set; }

        /// <summary>True for AutoPay and admin charges (customer absent). False when the signed-in
        /// customer pressed Pay with a saved card, so a 3DS challenge can be completed in-browser.</summary>
        public bool OffSession { get; set; } = true;
    }

    public class SavedCardChargeResult
    {
        public SavedCardChargeOutcome Outcome { get; set; }
        public string? PaymentIntentId { get; set; }

        /// <summary>Only for an on-session charge that needs 3DS: the browser finishes it.</summary>
        public string? ClientSecret { get; set; }

        public string? FailureCode { get; set; }
        public string? DeclineCode { get; set; }

        /// <summary>Stripe's message, for admins and logs only.</summary>
        public string? Message { get; set; }
    }

    /// <summary>The two Checkout Session facts the AutoPay sweep needs before charging an invoice.</summary>
    public class CheckoutSessionState
    {
        public string? Status { get; set; }          // open / complete / expired
        public string? PaymentStatus { get; set; }   // paid / unpaid / no_payment_required
        public string? PaymentIntentId { get; set; }
    }

    /// <summary>
    /// Live refund state of ONE Stripe charge, in dollars. Read straight from Stripe rather than
    /// derived from our OrderRefunds table, because refunds issued from the Stripe Dashboard leave
    /// no row on our side — trusting our own table would let the same money be refunded twice.
    /// </summary>
    public class ChargeRefundState
    {
        /// <summary>True when the intent actually settled money we could give back.</summary>
        public bool IsRefundable { get; set; }
        /// <summary>Amount Stripe actually captured on this intent.</summary>
        public decimal AmountReceived { get; set; }
        /// <summary>Already refunded against this intent, by us OR from the Stripe Dashboard.</summary>
        public decimal AmountRefunded { get; set; }
        public decimal RemainingRefundable => Math.Max(0m, AmountReceived - AmountRefunded);
        /// <summary>Why it isn't refundable (never settled, lookup failed, …). Admin-facing.</summary>
        public string? UnavailableReason { get; set; }

        /// <summary>
        /// This charge has a dispute (chargeback) against it. IMPORTANT: a dispute is NOT a refund
        /// and does NOT appear in AmountRefunded — Stripe withdraws disputed funds through a
        /// separate Dispute object and leaves amount_refunded at zero. So a charged-back order
        /// would otherwise reconcile to "$0 refunded, nothing to do" and look settled. The sync
        /// surfaces this as a warning for a human rather than importing an amount: a chargeback
        /// has different fees from a refund, and a won dispute reverses the withdrawal again.
        /// </summary>
        public bool HasDispute { get; set; }
    }

    public interface IStripeService
    {
        /// <param name="customerId">Stripe Customer to attach the intent to. Required when
        /// <paramref name="saveCardForOffSession"/> is true.</param>
        /// <param name="saveCardForOffSession">Sets setup_future_usage=off_session so the card
        /// used for THIS payment is saved for later recurring charges — no separate card-entry
        /// step (the booking flow's auto-charge checkbox).</param>
        /// <param name="idempotencyKey">Pass one wherever a repeated call is the SAME logical
        /// payment attempt, so a retry reuses the intent Stripe already created instead of a
        /// second chargeable one. The booking flow passes its prepare-session id.</param>
        Task<PaymentIntent> CreatePaymentIntentAsync(decimal amount, Dictionary<string, string> metadata = null,
            string receiptEmail = null, string customerId = null, bool saveCardForOffSession = false,
            string idempotencyKey = null);
        Task<PaymentIntent> ConfirmPaymentIntentAsync(string paymentIntentId);
        Task<PaymentIntent> GetPaymentIntentAsync(string paymentIntentId);
        Task<PaymentIntent> CancelPaymentIntentAsync(string paymentIntentId);
        /// <param name="idempotencyKey">Pass one for any admin-triggered refund so a double-click
        /// or retry can never refund twice. Callers use the OrderRefund row's PK, which is unique
        /// per intended refund and stable across retries of that same row.</param>
        /// <param name="metadata">Traceability only (orderId, adminUserId). The internal admin
        /// reason must NOT go here — Stripe's own `reason` field takes only its fixed enum.</param>
        Task<Refund> CreateRefundAsync(string paymentIntentId, decimal? amount = null,
            string idempotencyKey = null, Dictionary<string, string> metadata = null);

        /// <summary>How much of this charge is still refundable, read live from Stripe.
        /// Never throws — a lookup failure comes back as IsRefundable=false with a reason, so a
        /// Stripe outage disables the refund button instead of breaking the order panel.</summary>
        Task<ChargeRefundState> GetChargeRefundStateAsync(string paymentIntentId);

        /// <summary>Returns the user's Stripe Customer id, creating the Customer (and setting
        /// user.StripeCustomerId on the tracked entity — the CALLER SaveChanges) if missing or
        /// deleted on Stripe's side. Never sends a no-email placeholder address to Stripe.</summary>
        Task<string> CreateOrGetCustomerAsync(User user);

        /// <summary>SetupIntent (usage=off_session) for adding a card outside a payment — the
        /// Billing tab's "Add card" and the post-payment "Save your card" modal. Checkout saves the
        /// card through setup_future_usage on the payment's own PaymentIntent instead.</summary>
        Task<SetupIntent> CreateSetupIntentAsync(string stripeCustomerId, Dictionary<string, string>? metadata = null);

        /// <summary>
        /// Charges a saved card (create + confirm in one call). Card-only
        /// (<c>payment_method_types=[card]</c>) so the pinned API version's automatic payment
        /// methods can never demand a return_url. Never throws for a card-level outcome, and maps
        /// a transport failure or a Stripe 5xx to <see cref="SavedCardChargeOutcome.Unknown"/>
        /// rather than to a decline. Repeating the call with the same idempotency key returns the
        /// intent Stripe already created — that is how an Unknown is reconciled.
        /// </summary>
        Task<SavedCardChargeResult> ChargeSavedCardAsync(SavedCardChargeRequest request);

        /// <summary>Finds a PaymentIntent by one metadata value (Stripe Search). Null when none,
        /// or when Search is unavailable. Used only to reconcile an Unknown attempt older than
        /// Stripe's 24-hour idempotency window.</summary>
        Task<PaymentIntent?> FindPaymentIntentByMetadataAsync(string key, string value);

        /// <summary>A commercial Checkout Session's state, or null if it cannot be read.</summary>
        Task<CheckoutSessionState?> GetCheckoutSessionStateAsync(string sessionId);

        /// <summary>Expires an open Checkout Session. False when Stripe refused (already complete/expired).</summary>
        Task<bool> ExpireCheckoutSessionAsync(string sessionId);

        /// <summary>Retrieves a SetupIntent (to verify a card-save completed and read its pm).</summary>
        Task<SetupIntent> GetSetupIntentAsync(string setupIntentId);

        /// <summary>Card details (brand/last4) for display copies on the plan.
        /// Fully qualified: Models.PaymentMethod (the manual-payment enum) is also in scope.</summary>
        Task<Stripe.PaymentMethod> GetPaymentMethodAsync(string paymentMethodId);

        /// <summary>Detaches a replaced card from the Customer so old cards don't pile up.
        /// Best-effort at call sites — a failed detach must never block a card update.</summary>
        Task DetachPaymentMethodAsync(string paymentMethodId);

        /// <summary>Every CARD currently attached to this Stripe Customer. Used to reconcile what
        /// Stripe holds against what this application recorded (2026-09).</summary>
        Task<List<Stripe.PaymentMethod>> ListCustomerCardsAsync(string stripeCustomerId);

        /// <summary>
        /// Did this customer AUTHORISE saving this card? Returns the evidence ("setup_intent" /
        /// "payment_intent") or null. The only two ways a card legitimately reaches a Customer of
        /// ours are a SetupIntent we created for them, or a payment they confirmed with
        /// setup_future_usage — so a card with neither is never adopted into their wallet.
        /// </summary>
        Task<string?> FindCardSaveConsentAsync(string stripeCustomerId, string paymentMethodId);

        /// <summary>Merges keys into an existing PaymentIntent's metadata (e.g. stamping the
        /// orderId onto a recurring charge after the order is created, for support tracing).</summary>
        Task UpdatePaymentIntentMetadataAsync(string paymentIntentId, Dictionary<string, string> metadata);
    }
}
