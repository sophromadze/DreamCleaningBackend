using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for "must the payer accept the SMS / cancellation-fee / terms
    /// consents before this order's FIRST payment can be taken?".
    ///
    /// The three consents are collected on /booking for every self-service booking, and the
    /// booking form cannot be submitted without them. An admin creating an order through
    /// create-for-user ticks them on the customer's behalf, so the customer themselves never
    /// agreed to anything — they just receive a payment link. Those orders re-ask on the
    /// payment page, and the gate lives on create-payment-intent: without a PaymentIntent
    /// there is no client secret, so no card can be charged.
    ///
    /// Deliberately scoped to the INITIAL payment. A pending additional amount from an order
    /// edit is a follow-up charge on an order whose consents were already given.
    /// </summary>
    public static class PaymentConsentPolicy
    {
        /// <summary>True when this order still needs consent before it can be paid.
        /// False once accepted, once paid, and for every customer-booked order.</summary>
        public static bool RequiresConsent(Order order) =>
            !order.IsPaid &&
            order.PaymentConsentAcceptedAt == null &&
            order.IsBookedByAdmin();

        /// <summary>Message shown when the gate rejects a payment attempt.</summary>
        public const string ConsentRequiredMessage =
            "Please review and accept the SMS, cancellation-fee and terms consents before paying.";
    }
}
