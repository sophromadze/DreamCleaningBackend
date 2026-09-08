using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models.Commercial
{
    /// <summary>
    /// The company's own billing identity and the bank details an ACH payment is sent to.
    /// A SINGLE ROW, seeded at startup and edited by a SuperAdmin.
    ///
    /// Centralised because the alternative is bank details copy-pasted into an email template, a
    /// PDF writer and a public page - three places to miss when the account changes, and a client
    /// wiring money to a closed account is not a bug you find in review.
    ///
    /// WHAT LIVES HERE, AND WHAT DOES NOT. These are RECEIVING coordinates: they authorise nobody
    /// to move money, and they are printed on every invoice by design - a client cannot pay
    /// without them. What must never be stored in this table, or anywhere in this database, is
    /// anything that grants ACCESS to the account: online banking credentials, a bank API key or
    /// secret, an OAuth refresh token. When the reconciliation integration in the backlog is
    /// eventually built, those belong in configuration or a secret manager, and this row keeps
    /// holding only what goes on the invoice.
    ///
    /// Reads are Admin-and-up (an admin sending an invoice needs to see what the client sees);
    /// writes are SuperAdmin-only and audit-logged, because changing the destination account is
    /// how invoice fraud is committed.
    /// </summary>
    public class BillingSettings
    {
        public int Id { get; set; }

        // -- Company identity, as printed on the invoice ---------------------------------------

        [Required, StringLength(200)]
        public string CompanyLegalName { get; set; } = string.Empty;

        [StringLength(200)]
        public string? CompanyDbaName { get; set; }

        [StringLength(300)]
        public string? CompanyAddress { get; set; }

        [StringLength(100)]
        public string? CompanyCity { get; set; }

        [StringLength(50)]
        public string? CompanyState { get; set; }

        [StringLength(20)]
        public string? CompanyZip { get; set; }

        [StringLength(50)]
        public string? CompanyPhone { get; set; }

        [StringLength(255)]
        public string? CompanyEmail { get; set; }

        // -- Bank coordinates for receiving ACH -------------------------------------------------

        [StringLength(200)]
        public string? BankName { get; set; }

        /// <summary>
        /// The name on the account. Usually the legal entity plus its DBA, and NOT assumed to
        /// equal <see cref="CompanyLegalName"/> - a mismatch between the two is exactly what makes
        /// a client's bank reject a transfer, so it is entered separately and deliberately.
        /// </summary>
        [StringLength(200)]
        public string? BankAccountHolder { get; set; }

        [StringLength(20)]
        public string? BankRoutingNumber { get; set; }

        [StringLength(40)]
        public string? BankAccountNumber { get; set; }

        /// <summary>Free text, e.g. "Business Checking".</summary>
        [StringLength(60)]
        public string? BankAccountType { get; set; }

        /// <summary>Client-facing prose shown under the ACH block on the invoice.</summary>
        [StringLength(1000)]
        public string? AchInstructions { get; set; }

        /// <summary>Optional; the wire block is hidden entirely when this is blank.</summary>
        [StringLength(1000)]
        public string? WireInstructions { get; set; }

        /// <summary>
        /// Optional. A separate routing number some banks use for wires. NOT required for ACH —
        /// asking for it before an ACH payment can be offered would block the primary method on
        /// an optional field.
        /// </summary>
        [StringLength(20)]
        public string? BankWireRoutingNumber { get; set; }

        // -- Which payment methods a commercial invoice offers ----------------------------------
        //
        // Centralized here so the public page, the PDF, the email and the Checkout endpoint all
        // read one answer. A method that is disabled must be unavailable at the ENDPOINT, not just
        // hidden in the UI — see InvoiceCheckoutService.

        /// <summary>
        /// Stripe ACH Direct Debit — the primary online method. On by default: it is the whole
        /// point of the online payment flow for commercial clients.
        /// </summary>
        public bool StripeAchEnabled { get; set; } = true;

        /// <summary>
        /// Stripe card payments. OFF by default and deliberately so: card fees on a four-figure
        /// commercial invoice are material, and turning them on is an owner's commercial decision
        /// rather than a default. No surcharge is ever added to the customer either way.
        /// </summary>
        public bool StripeCardEnabled { get; set; } = false;

        /// <summary>
        /// Manual ACH — the customer pushes a transfer from their own bank and an admin records
        /// it. On by default, and the fallback whenever Stripe is disabled or unreachable.
        /// </summary>
        public bool ManualAchEnabled { get; set; } = true;

        // -- Invoice defaults -------------------------------------------------------------------

        /// <summary>Seeds the tax mode on a new invoice. Never applied to an existing one.</summary>
        public InvoiceTaxType DefaultTaxType { get; set; } = InvoiceTaxType.Exempt;

        [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "decimal(6,3)")]
        public decimal? DefaultTaxRate { get; set; }

        public InvoiceDueTerms DefaultDueTerms { get; set; } = InvoiceDueTerms.Net15;

        /// <summary>Prefilled into the Customer Note box on a new invoice.</summary>
        [StringLength(2000)]
        public string? DefaultCustomerNote { get; set; }

        /// <summary>Printed at the foot of the invoice and the PDF.</summary>
        [StringLength(500)]
        public string? InvoiceFooterText { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public int? UpdatedByUserId { get; set; }
    }
}
