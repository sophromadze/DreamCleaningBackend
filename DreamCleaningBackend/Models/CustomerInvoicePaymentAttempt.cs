using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Where one online BANK (ACH) payment of a regular customer invoice stands. Persisted as an
    /// int: append, never insert.
    /// </summary>
    public enum CustomerInvoicePaymentAttemptStatus
    {
        /// <summary>Row written, Stripe session not opened yet.</summary>
        Created = 0,
        /// <summary>The customer is on Stripe's hosted page. No debit has been submitted.</summary>
        CheckoutOpen = 1,
        /// <summary>The customer authorized the debit; Stripe is moving the money (days).</summary>
        Processing = 2,
        /// <summary>The money arrived and was credited to the invoice's part-payment request.</summary>
        Succeeded = 3,
        /// <summary>The debit failed or was returned. Nothing was credited.</summary>
        Failed = 4,
        /// <summary>The session lapsed or was replaced unused. Nothing was credited.</summary>
        Expired = 5
    }

    /// <summary>
    /// One online bank (Stripe ACH Direct Debit) payment of a REGULAR customer invoice (DCR-…),
    /// 2026-09 — the regular-invoice twin of <c>CommercialInvoicePaymentAttempt</c>.
    ///
    /// Why a row at all: a card charge settles in a second, an ACH debit is authorized on Monday
    /// and settles on Thursday. For those days the invoice is genuinely UNPAID while the customer
    /// believes they have paid, so this row is the "conversation with Stripe" and the money is
    /// written only when Stripe says it arrived — through the invoice's own
    /// <see cref="OrderPartialPayment"/> request, the one place a regular invoice's money is kept.
    ///
    /// <see cref="Amount"/> / <see cref="ProcessingFee"/> / <see cref="TotalCharged"/> are frozen
    /// when Checkout opens: the customer authorized that exact figure, and settlement days later
    /// must credit the BILL (<see cref="Amount"/>), never the gross debit — the ACH processing fee
    /// is a payment-method charge that is not part of the order.
    /// </summary>
    public class CustomerInvoicePaymentAttempt
    {
        public int Id { get; set; }

        public int CustomerInvoiceId { get; set; }
        public virtual CustomerInvoice? CustomerInvoice { get; set; }

        /// <summary>Denormalized for the webhook and the guards — the invoice's order.</summary>
        public int OrderId { get; set; }

        /// <summary>The part-payment request the money is credited to.</summary>
        public int OrderPartialPaymentId { get; set; }

        public decimal Amount { get; set; }
        public decimal ProcessingFee { get; set; }
        public decimal TotalCharged { get; set; }

        public CustomerInvoicePaymentAttemptStatus Status { get; set; } = CustomerInvoicePaymentAttemptStatus.Created;

        /// <summary>UNIQUE — one attempt per Stripe session.</summary>
        [MaxLength(255)]
        public string? StripeCheckoutSessionId { get; set; }

        /// <summary>UNIQUE — one attempt per PaymentIntent, so a settlement cannot be claimed twice.</summary>
        [MaxLength(255)]
        public string? StripePaymentIntentId { get; set; }

        [MaxLength(100)]
        public string? FailureCode { get; set; }

        [MaxLength(500)]
        public string? FailureMessage { get; set; }

        [MaxLength(64)]
        public string? ClientIp { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
    }
}
