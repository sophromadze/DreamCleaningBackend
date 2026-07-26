using DreamCleaningBackend.Models;
using Stripe;

namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>
    /// Outcome of an off-session (saved card, no customer present) charge. Exactly one of
    /// three shapes: Success (charged), RequiresAction (SCA/3DS — cannot be completed
    /// automatically; customer must re-authenticate), or a decline (both flags false,
    /// FailureReason set). Never throws for card-level outcomes so the recurring billing
    /// service can branch and record real decline reasons.
    /// </summary>
    public class OffSessionChargeResult
    {
        public bool Success { get; set; }
        public bool RequiresAction { get; set; }
        public string? PaymentIntentId { get; set; }
        /// <summary>Human-readable decline/error detail (e.g. "card_declined: insufficient funds").
        /// Stored on RecurringBillingAttempt.FailureReason, so keep it admin-friendly.</summary>
        public string? FailureReason { get; set; }
    }

    public interface IStripeService
    {
        /// <param name="customerId">Stripe Customer to attach the intent to. Required when
        /// <paramref name="saveCardForOffSession"/> is true.</param>
        /// <param name="saveCardForOffSession">Sets setup_future_usage=off_session so the card
        /// used for THIS payment is saved for later recurring charges — no separate card-entry
        /// step (the booking flow's auto-charge checkbox).</param>
        Task<PaymentIntent> CreatePaymentIntentAsync(decimal amount, Dictionary<string, string> metadata = null,
            string receiptEmail = null, string customerId = null, bool saveCardForOffSession = false);
        Task<PaymentIntent> ConfirmPaymentIntentAsync(string paymentIntentId);
        Task<PaymentIntent> GetPaymentIntentAsync(string paymentIntentId);
        Task<Refund> CreateRefundAsync(string paymentIntentId, decimal? amount = null);

        /// <summary>Returns the user's Stripe Customer id, creating the Customer (and setting
        /// user.StripeCustomerId on the tracked entity — the CALLER SaveChanges) if missing or
        /// deleted on Stripe's side. Never sends a no-email placeholder address to Stripe.</summary>
        Task<string> CreateOrGetCustomerAsync(User user);

        /// <summary>SetupIntent (usage=off_session) for saving/replacing a card outside a
        /// payment — the profile page's "Update Card" flow only; checkout saves the card via
        /// saveCardForOffSession on the booking's own PaymentIntent instead.</summary>
        Task<SetupIntent> CreateSetupIntentAsync(string stripeCustomerId);

        /// <summary>Charges a saved card with the customer absent (confirm+off_session in one
        /// call). Pass a per-attempt idempotency key (recurring:{planId}:{cycleDate}:attempt{n})
        /// so a crashed/re-run cycle can never double-charge.</summary>
        Task<OffSessionChargeResult> CreateOffSessionPaymentIntentAsync(decimal amount, string customerId,
            string paymentMethodId, Dictionary<string, string> metadata, string idempotencyKey,
            string receiptEmail = null);

        /// <summary>Card details (brand/last4) for display copies on the plan.
        /// Fully qualified: Models.PaymentMethod (the manual-payment enum) is also in scope.</summary>
        Task<Stripe.PaymentMethod> GetPaymentMethodAsync(string paymentMethodId);

        /// <summary>Detaches a replaced card from the Customer so old cards don't pile up.
        /// Best-effort at call sites — a failed detach must never block a card update.</summary>
        Task DetachPaymentMethodAsync(string paymentMethodId);

        /// <summary>Merges keys into an existing PaymentIntent's metadata (e.g. stamping the
        /// orderId onto a recurring charge after the order is created, for support tracing).</summary>
        Task UpdatePaymentIntentMetadataAsync(string paymentIntentId, Dictionary<string, string> metadata);
    }
}
