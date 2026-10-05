using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Whether a customer invoice bills everything still owed on its order, or one agreed slice
    /// of it (the customer asked to split the payment, so the order carries several invoices),
    /// or — Additional — the extra an edit added AFTER the order was paid (the order-edit top-up,
    /// <c>Helpers/OrderAdditionalCharge</c>). Persisted as an int: append, never insert.
    /// </summary>
    public enum CustomerInvoiceKind
    {
        Full = 0,
        Split = 1,
        Additional = 2
    }

    /// <summary>
    /// A REGULAR invoice for an ordinary (residential) customer — Admin → Invoices (2026-09).
    ///
    /// Deliberately NOT the commercial invoice (<c>CommercialInvoice</c>): that one bills a
    /// company (<c>ContractClient</c>), keeps its own payment ledger and settles by ACH. A regular
    /// customer invoice is a formal BILL wrapped around one order's own payment, so it carries no
    /// money of its own:
    ///
    ///  - Every invoice is backed by ONE <see cref="OrderPartialPayment"/> request — the existing
    ///    part-payment machinery. A Full invoice's request is for the whole balance, a Split
    ///    invoice's for its slice. Paying by card goes through that request's ordinary Stripe
    ///    path (idempotent settlement, final slice completes the order exactly like a normal
    ///    card payment), and a bank transfer is recorded by an admin against the same request.
    ///  - So the invoice's status is DERIVED from the request and the order
    ///    (<c>Helpers/CustomerInvoiceStatusPolicy</c>) — it can never claim a payment the order
    ///    does not show, or the other way round.
    ///  - The public page is addressed by <see cref="PublicToken"/> and needs no login, so the
    ///    customer (or whoever pays for them) can open it from the email or text.
    /// </summary>
    public class CustomerInvoice
    {
        public int Id { get; set; }

        /// <summary>DCR-YYYY-XXXXXXXX. Generated server-side, frozen, never reused.</summary>
        [Required, MaxLength(32)]
        public string InvoiceNumber { get; set; } = string.Empty;

        /// <summary>48-hex secret that addresses the public page. The row id is never exposed.</summary>
        [Required, MaxLength(64)]
        public string PublicToken { get; set; } = string.Empty;

        public int OrderId { get; set; }
        public virtual Order? Order { get; set; }

        /// <summary>
        /// The part-payment request this invoice collects. Set on every Full/Split invoice; NULL
        /// on an Additional one, which bills the order-edit top-up instead (that money is owed on
        /// top of a SETTLED order, so there is no balance for a part-payment request to slice).
        /// </summary>
        public int? OrderPartialPaymentId { get; set; }
        public virtual OrderPartialPayment? OrderPartialPayment { get; set; }

        public CustomerInvoiceKind Kind { get; set; } = CustomerInvoiceKind.Full;

        /// <summary>
        /// What the invoice billed when it was issued. A Split invoice always bills this figure;
        /// a Full invoice bills the order's LIVE balance (an edit after issue moves it), and this
        /// column records what it said on the day it was created.
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        /// Additional invoices only: the order's collected-top-up total
        /// (<c>OrderAdditionalCharge.CollectedToDate</c>) at which this invoice counts as PAID —
        /// what had been collected when it was issued, plus <see cref="Amount"/>. Collected money
        /// only ever grows, so the invoice stays Paid even when a later edit adds another charge;
        /// deriving "paid" from "nothing outstanding" would flip it back to owed.
        /// </summary>
        public decimal? TopUpCollectedTarget { get; set; }

        /// <summary>Optional customer-facing note printed on the invoice.</summary>
        [MaxLength(1000)]
        public string? Note { get; set; }

        public int? CreatedByUserId { get; set; }
        public virtual User? CreatedByUser { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? FirstSentAt { get; set; }
        public DateTime? LastSentAt { get; set; }
        public int SendCount { get; set; }

        public DateTime? VoidedAt { get; set; }
        public int? VoidedByUserId { get; set; }

        [MaxLength(500)]
        public string? VoidReason { get; set; }
    }
}
