using System.ComponentModel.DataAnnotations;
using DreamCleaningBackend.DTOs.Commercial;

namespace DreamCleaningBackend.DTOs
{
    // Regular customer invoices — Admin → Invoices (2026-09). See Models/CustomerInvoice.cs.

    /// <summary>
    /// Create one or more invoices for one order. With no <see cref="SplitAmounts"/> a single
    /// FULL invoice bills everything still owed; with amounts, one SPLIT invoice is issued per
    /// amount. There is deliberately no total field — every figure is checked against the order's
    /// own balance on the server.
    /// </summary>
    public class CreateCustomerInvoiceDto
    {
        public int OrderId { get; set; }
        public List<decimal>? SplitAmounts { get; set; }

        [MaxLength(1000)]
        public string? Note { get; set; }

        public bool SendEmail { get; set; } = true;
        public bool SendSms { get; set; } = true;
    }

    public class SendCustomerInvoiceDto
    {
        public bool SendEmail { get; set; } = true;
        public bool SendSms { get; set; } = true;
    }

    public class VoidCustomerInvoiceDto
    {
        [MaxLength(500)]
        public string? Reason { get; set; }
    }

    /// <summary>One row of the admin Invoices tab, and the admin detail of one invoice.</summary>
    public class CustomerInvoiceDto
    {
        public int Id { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        public int OrderId { get; set; }
        public int UserId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public string? CustomerEmail { get; set; }
        public string? CustomerPhone { get; set; }

        public string ServiceTypeName { get; set; } = string.Empty;
        public DateTime ServiceDate { get; set; }

        /// <summary>What the invoice billed when issued.</summary>
        public decimal Amount { get; set; }
        /// <summary>What is still owed on THIS invoice right now (0 unless payable).</summary>
        public decimal AmountDue { get; set; }
        public decimal OrderTotal { get; set; }
        public decimal OrderAmountDue { get; set; }

        /// <summary>The part-payment request behind it — what "Mark paid (bank transfer)" records against.
        /// Null on an Additional invoice (it bills the order-edit top-up, not a request).</summary>
        public int? PartialPaymentId { get; set; }
        public string? PaidVia { get; set; }
        public string? PaymentReference { get; set; }
        public DateTime? PaidAt { get; set; }

        public string? Note { get; set; }
        public string PublicUrl { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
        public string? CreatedByName { get; set; }
        public DateTime? FirstSentAt { get; set; }
        public DateTime? LastSentAt { get; set; }
        public int SendCount { get; set; }
        public DateTime? VoidedAt { get; set; }
        public string? VoidReason { get; set; }

        public bool CanSend { get; set; }
        public bool CanVoid { get; set; }
        public bool CanRecordPayment { get; set; }
    }

    /// <summary>An order the Create Invoice form can bill, with the reason it can't when it can't.</summary>
    public class CustomerInvoiceOrderOptionDto
    {
        public int OrderId { get; set; }
        public string ServiceTypeName { get; set; } = string.Empty;
        public DateTime ServiceDate { get; set; }
        public string Status { get; set; } = string.Empty;
        public decimal Total { get; set; }
        public decimal AmountDue { get; set; }
        /// <summary>Owed and not already on an open invoice or payment request.</summary>
        public decimal AvailableToInvoice { get; set; }
        public bool CanInvoice { get; set; }
        /// <summary>A PAID order whose price an edit raised: the invoice bills the extra only,
        /// as one amount (Additional kind — it can't be split).</summary>
        public bool IsAdditionalCharge { get; set; }
        public string? CannotInvoiceReason { get; set; }
        public List<string> OpenInvoiceNumbers { get; set; } = new();
    }

    /// <summary>
    /// The public, token-addressed invoice page. A SEPARATE TYPE from <see cref="CustomerInvoiceDto"/>
    /// rather than a filtered copy, for the reason <c>PublicInvoiceDto</c> gives: a field added to
    /// the admin view must not reach a stranger holding the link by default. No ids other than the
    /// order number, no internal names, no void reason.
    /// </summary>
    /// <summary>The Stripe-hosted bank payment page to send the customer to.</summary>
    public class StartCustomerInvoiceAchResponseDto
    {
        public string CheckoutUrl { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal ProcessingFee { get; set; }
        public decimal TotalCharged { get; set; }
    }

    public class PublicCustomerInvoiceDto
    {
        public string InvoiceNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public DateTime IssuedAt { get; set; }

        public int OrderNumber { get; set; }
        public string ServiceTypeName { get; set; } = string.Empty;
        public DateTime ServiceDate { get; set; }
        public string ServiceTime { get; set; } = string.Empty;
        public string ServiceAddress { get; set; } = string.Empty;

        public string BilledToName { get; set; } = string.Empty;
        public string? BilledToEmail { get; set; }
        public string? BilledToPhone { get; set; }

        // The order's own breakdown, so the bill explains itself.
        public decimal SubTotal { get; set; }
        public decimal Discounts { get; set; }
        public decimal Credits { get; set; }
        public decimal Tax { get; set; }
        public decimal Tips { get; set; }
        public decimal OrderTotal { get; set; }
        public decimal OrderAmountPaid { get; set; }

        /// <summary>What this invoice bills when issued, and what is still owed on it now.</summary>
        public decimal Amount { get; set; }
        public decimal AmountDue { get; set; }
        public string? Note { get; set; }

        /// <summary>When the money arrived, once Paid (printed on the PDF).</summary>
        public DateTime? PaidAt { get; set; }

        /// <summary>
        /// The card payment page for THIS invoice's request (order payment link + request id).
        /// Null when nothing can be paid.
        /// </summary>
        public string? CardPaymentPath { get; set; }

        /// <summary>Bank-transfer instructions from Billing Settings; null when not configured.</summary>
        public PublicPaymentInstructionsDto? BankTransfer { get; set; }

        /// <summary>Online bank payment (Stripe ACH) can be started now (2026-09).</summary>
        public bool AchAvailable { get; set; }

        /// <summary>A bank payment was authorized and is settling (takes a few business days).
        /// While true the page offers no payment button at all — each would collect twice.</summary>
        public bool AchProcessing { get; set; }

        /// <summary>The "ACH Processing Fee" quote for paying the amount due from a bank, and the
        /// total debit. Display only — the checkout endpoint recomputes both.</summary>
        public decimal AchProcessingFee { get; set; }
        public decimal AchTotalCharge { get; set; }
        public PublicCompanyDto? Company { get; set; }
    }

    /// <summary>
    /// One row of the customer's OWN Invoices tab in their profile (2026-09). A separate, narrow
    /// type for the same reason as <see cref="PublicCustomerInvoiceDto"/>: nothing internal (no
    /// admin names, void reasons or request ids) reaches the customer by default.
    /// </summary>
    public class MyCustomerInvoiceDto
    {
        public string InvoiceNumber { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int OrderId { get; set; }
        public string ServiceTypeName { get; set; } = string.Empty;
        public DateTime ServiceDate { get; set; }
        public DateTime IssuedAt { get; set; }
        public decimal Amount { get; set; }
        public decimal AmountDue { get; set; }
        public DateTime? PaidAt { get; set; }
        public string? PaidVia { get; set; }
        /// <summary>The invoice's own page (view, pay, download the PDF).</summary>
        public string PublicUrl { get; set; } = string.Empty;
    }
}
