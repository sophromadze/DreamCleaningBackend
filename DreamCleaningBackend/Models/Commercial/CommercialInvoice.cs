using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DreamCleaningBackend.Models.Contracts;

namespace DreamCleaningBackend.Models.Commercial
{
    /// <summary>
    /// One invoice issued to a commercial client, normally against a
    /// <see cref="Models.Contracts.Contract"/> but not necessarily.
    ///
    /// COMPLETELY SEPARATE FROM THE RESIDENTIAL CHECKOUT. A residential order is charged through
    /// Stripe at booking time and <see cref="Order"/> owns its own money columns. This entity
    /// never touches that flow: a commercial client is billed after the fact and pays by ACH into
    /// the company bank account, and an admin records the payment when it lands. Nothing here
    /// creates a PaymentIntent.
    ///
    /// Every monetary column is decimal(10,2) - never a double. See <c>InvoiceCalculator</c>,
    /// which is the only thing allowed to compute these values; the figures a browser submits are
    /// display-only and are recomputed server-side on every write.
    ///
    /// The primary key is an int, matching every other entity in this database. The public
    /// reference is <see cref="InvoiceNumber"/> and the public URL is keyed by
    /// <see cref="PublicToken"/>, so the sequential id is never exposed to a client either way.
    /// </summary>
    public class CommercialInvoice
    {
        public int Id { get; set; }

        /// <summary>
        /// DCI-YYYY-XXXXXXXX. Unique, and FROZEN once the row exists - it is quoted in the
        /// client's payment memo, so changing it would orphan a payment already in flight.
        /// A number is never reused, including after a void.
        /// </summary>
        [Required, StringLength(32)]
        public string InvoiceNumber { get; set; } = string.Empty;

        /// <summary>
        /// 48 hex chars behind /invoice/{token}. The whole authorization for the public page, in
        /// the same shape as the contract review token and the tokenized customer payment links -
        /// a commercial client has no account here, so there is nothing else to authenticate.
        /// Deliberately NOT the row id: an id is guessable and enumerable, a token is not.
        /// </summary>
        [Required, StringLength(64)]
        public string PublicToken { get; set; } = string.Empty;

        public int ContractClientId { get; set; }
        [ForeignKey("ContractClientId")]
        public virtual ContractClient? Client { get; set; }

        /// <summary>
        /// The agreement being billed against. Optional: ad-hoc commercial work is invoiced
        /// without a contract, and an invoice must never be blocked on paperwork existing.
        /// </summary>
        public int? ContractId { get; set; }
        [ForeignKey("ContractId")]
        public virtual Contract? Contract { get; set; }

        /// <summary>Which of the client's premises this invoice covers. Optional.</summary>
        public int? ContractServiceLocationId { get; set; }
        [ForeignKey("ContractServiceLocationId")]
        public virtual ContractServiceLocation? ServiceLocation { get; set; }

        public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

        /// <summary>Date-only in intent; the time component is never displayed.</summary>
        public DateTime InvoiceDate { get; set; }

        public DateTime DueDate { get; set; }

        /// <summary>Which preset produced <see cref="DueDate"/>, kept so the form round-trips.</summary>
        public InvoiceDueTerms DueTerms { get; set; } = InvoiceDueTerms.Net15;

        /// <summary>
        /// The period the work covers. A single date sets both ends; a month sets a range. Both
        /// null is valid - not every invoice describes a period.
        /// </summary>
        public DateTime? ServiceStartDate { get; set; }
        public DateTime? ServiceEndDate { get; set; }

        /// <summary>
        /// The service address AS BILLED, copied from the location at creation rather than joined
        /// at read time. Same reasoning as the contract version snapshot: renaming or re-addressing
        /// a location two years from now must not silently rewrite an invoice already paid.
        /// </summary>
        [StringLength(400)]
        public string? ServiceAddress { get; set; }

        /// <summary>Client's own PO reference, printed on the invoice when supplied.</summary>
        [StringLength(100)]
        public string? PoNumber { get; set; }

        [StringLength(100)]
        public string? ClientReference { get; set; }

        // -- Money. All server-computed; see InvoiceCalculator. -------------------------------

        [Column(TypeName = "decimal(10,2)")]
        public decimal SubTotal { get; set; }

        public InvoiceDiscountType DiscountType { get; set; } = InvoiceDiscountType.None;

        /// <summary>The typed figure: dollars for a fixed discount, percent for a percentage.</summary>
        [Column(TypeName = "decimal(10,2)")]
        public decimal? DiscountValue { get; set; }

        /// <summary>Always dollars, always derived, never trusted from the client.</summary>
        [Column(TypeName = "decimal(10,2)")]
        public decimal DiscountAmount { get; set; }

        public InvoiceTaxType TaxType { get; set; } = InvoiceTaxType.Exempt;

        /// <summary>Percent, e.g. 8.875. Null when exempt.</summary>
        [Column(TypeName = "decimal(6,3)")]
        public decimal? TaxRate { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal TaxAmount { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal Total { get; set; }

        /// <summary>Sum of the payment rows. Recalculated from them, never incremented in place.</summary>
        [Column(TypeName = "decimal(10,2)")]
        public decimal AmountPaid { get; set; }

        /// <summary>
        /// Total - AmountPaid, floored at zero. An overpayment shows as a credit rather than a
        /// negative balance - see <c>InvoiceCalculator.ResolveBalance</c>.
        /// </summary>
        [Column(TypeName = "decimal(10,2)")]
        public decimal BalanceDue { get; set; }

        [Required, StringLength(3)]
        public string Currency { get; set; } = "USD";

        public InvoicePaymentMethod PaymentMethod { get; set; } = InvoicePaymentMethod.AchBankTransfer;

        // -- Notes ----------------------------------------------------------------------------

        /// <summary>Printed on the invoice, the PDF and the email. Client-visible.</summary>
        [StringLength(2000)]
        public string? CustomerNote { get; set; }

        /// <summary>
        /// ADMIN EYES ONLY. Never serialised into the public invoice DTO, never rendered into the
        /// PDF, never quoted in an email. The public endpoint projects an explicit field list for
        /// exactly this reason rather than reusing the admin DTO.
        /// </summary>
        [StringLength(2000)]
        public string? InternalNote { get; set; }

        // -- Lifecycle timestamps ---------------------------------------------------------------

        public DateTime? FirstSentAt { get; set; }
        public DateTime? LastSentAt { get; set; }
        public DateTime? FirstViewedAt { get; set; }
        public DateTime? LastViewedAt { get; set; }
        public int ViewCount { get; set; }
        public DateTime? PaidAt { get; set; }
        public DateTime? VoidedAt { get; set; }

        [StringLength(500)]
        public string? VoidReason { get; set; }

        public int? VoidedByUserId { get; set; }
        [ForeignKey("VoidedByUserId")]
        public virtual User? VoidedByUser { get; set; }

        public int CreatedByUserId { get; set; }
        [ForeignKey("CreatedByUserId")]
        public virtual User? CreatedByUser { get; set; }

        /// <summary>Set when this invoice came from "Duplicate invoice".</summary>
        public int? DuplicatedFromInvoiceId { get; set; }

        /// <summary>Set when a recurring template generated this invoice. Unused in v1.</summary>
        public int? RecurringTemplateId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public virtual ICollection<CommercialInvoiceItem> Items { get; set; } = new List<CommercialInvoiceItem>();
        public virtual ICollection<CommercialInvoicePayment> Payments { get; set; } = new List<CommercialInvoicePayment>();
    }

    /// <summary>
    /// One billable line. <see cref="Amount"/> is always quantity times unit price recomputed on
    /// the server - the browser's figure is a preview.
    /// </summary>
    public class CommercialInvoiceItem
    {
        public int Id { get; set; }

        public int CommercialInvoiceId { get; set; }
        [ForeignKey("CommercialInvoiceId")]
        public virtual CommercialInvoice? Invoice { get; set; }

        [Required, StringLength(500)]
        public string Description { get; set; } = string.Empty;

        /// <summary>Fractional quantities are supported - half a day, 2.5 hours.</summary>
        [Column(TypeName = "decimal(10,2)")]
        public decimal Quantity { get; set; } = 1m;

        [Column(TypeName = "decimal(10,2)")]
        public decimal UnitPrice { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal Amount { get; set; }

        public int SortOrder { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Money actually received against an invoice. TREATED AS IMMUTABLE FINANCIAL HISTORY: there
    /// is no edit endpoint and no delete endpoint. A mistake is corrected by recording a reversing
    /// entry (<see cref="IsReversal"/>), so the trail shows what happened rather than pretending
    /// it did not.
    ///
    /// This is what makes "Mark as Paid" honest - it opens the same modal and writes one of these,
    /// instead of assigning Status = Paid with no record of who received what, or when.
    /// </summary>
    public class CommercialInvoicePayment
    {
        public int Id { get; set; }

        public int CommercialInvoiceId { get; set; }
        [ForeignKey("CommercialInvoiceId")]
        public virtual CommercialInvoice? Invoice { get; set; }

        /// <summary>Negative on a reversal row; that is the only way it is ever below zero.</summary>
        [Column(TypeName = "decimal(10,2)")]
        public decimal Amount { get; set; }

        public DateTime PaymentDate { get; set; }

        public InvoicePaymentRecordMethod PaymentMethod { get; set; } = InvoicePaymentRecordMethod.AchBankTransfer;

        /// <summary>The bank's own reference for the transfer - what reconciliation is done against.</summary>
        [StringLength(200)]
        public string? TransactionReference { get; set; }

        [StringLength(1000)]
        public string? InternalNote { get; set; }

        /// <summary>True when this row cancels an earlier one rather than recording new money.</summary>
        public bool IsReversal { get; set; }

        /// <summary>The payment this row reverses. Set only on a reversal.</summary>
        public int? ReversesPaymentId { get; set; }

        /// <summary>
        /// Who processed this money. Manual is an admin reading a bank statement; Stripe is an
        /// online payment confirmed by webhook. Both produce identical rows in every other
        /// respect and flow through the same recalculation pipeline — the provider is a record of
        /// origin, never a branch in the money logic.
        /// </summary>
        public InvoicePaymentProvider Provider { get; set; } = InvoicePaymentProvider.Manual;

        /// <summary>
        /// UNIQUE (filtered to non-null). THE IDEMPOTENCY GUARD for online payments.
        ///
        /// Stripe retries webhooks, and can deliver the same event concurrently. Neither an
        /// in-memory flag nor the best-effort WebhookEvents table can be relied on to stop a
        /// duplicate — the latter swallows its own exceptions by design. A unique index cannot be
        /// talked out of it: the second insert for the same PaymentIntent fails at the database,
        /// and the handler treats that as "already recorded" and returns success to Stripe.
        /// </summary>
        [StringLength(255)]
        public string? StripePaymentIntentId { get; set; }

        /// <summary>The settled charge, when Stripe reports one. Useful for reconciliation.</summary>
        [StringLength(255)]
        public string? StripeChargeId { get; set; }

        /// <summary>
        /// Null for a payment recorded by the system from a webhook — there is no admin behind it.
        /// Nullable for exactly that reason; a manual payment always names the admin who recorded
        /// it, and <see cref="RecordedByLabel"/> is what surfaces on screen.
        /// </summary>
        public int? RecordedByUserId { get; set; }
        [ForeignKey("RecordedByUserId")]
        public virtual User? RecordedByUser { get; set; }

        /// <summary>
        /// Who to name in the ledger when there is no user account behind the row — "Stripe
        /// webhook". Stored rather than derived so the trail still reads correctly if the provider
        /// enum ever gains members.
        /// </summary>
        [StringLength(100)]
        public string? RecordedByLabel { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>One outbound mail about an invoice. Feeds the detail page's Email History.</summary>
    public class CommercialInvoiceEmailLog
    {
        public long Id { get; set; }

        public int CommercialInvoiceId { get; set; }
        [ForeignKey("CommercialInvoiceId")]
        public virtual CommercialInvoice? Invoice { get; set; }

        public InvoiceEmailType EmailType { get; set; }

        [Required, StringLength(255)]
        public string Recipient { get; set; } = string.Empty;

        [Required, StringLength(300)]
        public string Subject { get; set; } = string.Empty;

        public InvoiceEmailStatus Status { get; set; } = InvoiceEmailStatus.Sent;

        /// <summary>Why a send failed, when it did. Null on success.</summary>
        [StringLength(500)]
        public string? FailureReason { get; set; }

        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The provider's id for the message, when the transport gives us one. SMTP via MailKit
        /// does not, so this is null today - the column exists so delivered/opened/bounced
        /// tracking can be added later without a migration.
        /// </summary>
        [StringLength(200)]
        public string? ProviderMessageId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// The invoice's plain-language timeline. Deliberately separate from the app-wide
    /// <see cref="AuditLog"/>, for the same reason ContractAuditLog is: this one is READ BY PEOPLE
    /// on the detail page, so it stores a sentence rather than a field-level diff. Financial
    /// actions are additionally written to the app-wide audit log.
    /// </summary>
    public class CommercialInvoiceActivityLog
    {
        public long Id { get; set; }

        public int CommercialInvoiceId { get; set; }
        [ForeignKey("CommercialInvoiceId")]
        public virtual CommercialInvoice? Invoice { get; set; }

        /// <summary>Machine key: invoice_created, invoice_sent, payment_recorded, and so on.</summary>
        [Required, StringLength(60)]
        public string Action { get; set; } = string.Empty;

        [Required, StringLength(1000)]
        public string Description { get; set; } = string.Empty;

        /// <summary>Null for something the client or the system did.</summary>
        public int? UserId { get; set; }
        [ForeignKey("UserId")]
        public virtual User? User { get; set; }

        /// <summary>Whoever the description names - an admin, a client email, or "System".</summary>
        [StringLength(255)]
        public string? ActorName { get; set; }

        [Column(TypeName = "LONGTEXT")]
        public string? MetadataJson { get; set; }

        [StringLength(45)]
        public string? IpAddress { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A reminder that has been sent about an unpaid invoice. Exists so a reminder cannot be sent
    /// twice for the same reason on the same day - the duplicate guard is the point of the table,
    /// not the history.
    /// </summary>
    public class CommercialInvoiceReminder
    {
        public int Id { get; set; }

        public int CommercialInvoiceId { get; set; }
        [ForeignKey("CommercialInvoiceId")]
        public virtual CommercialInvoice? Invoice { get; set; }

        /// <summary>manual, before_due_3, on_due, overdue_3, overdue_7.</summary>
        [Required, StringLength(40)]
        public string ReminderKey { get; set; } = string.Empty;

        [Required, StringLength(255)]
        public string Recipient { get; set; } = string.Empty;

        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        public int? SentByUserId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A template for recurring commercial billing.
    ///
    /// NOTHING GENERATES FROM THIS YET. It is defined in v1 so the scheduled generator can be
    /// added later as a background service plus one endpoint, with no schema change to the
    /// invoice tables. <see cref="CommercialInvoice.RecurringTemplateId"/> is the link the
    /// generator will stamp on what it produces.
    /// </summary>
    public class CommercialRecurringInvoiceTemplate
    {
        public int Id { get; set; }

        [Required, StringLength(200)]
        public string Name { get; set; } = string.Empty;

        public int ContractClientId { get; set; }
        [ForeignKey("ContractClientId")]
        public virtual ContractClient? Client { get; set; }

        public int? ContractId { get; set; }
        [ForeignKey("ContractId")]
        public virtual Contract? Contract { get; set; }

        public int? ContractServiceLocationId { get; set; }

        public InvoiceRecurrenceFrequency Frequency { get; set; } = InvoiceRecurrenceFrequency.Monthly;

        /// <summary>Only meaningful for <see cref="InvoiceRecurrenceFrequency.Custom"/>.</summary>
        public int? CustomIntervalDays { get; set; }

        public DateTime NextInvoiceDate { get; set; }

        public InvoiceDueTerms DefaultDueTerms { get; set; } = InvoiceDueTerms.Net15;

        public InvoiceTaxType TaxType { get; set; } = InvoiceTaxType.Exempt;

        [Column(TypeName = "decimal(6,3)")]
        public decimal? TaxRate { get; set; }

        public InvoicePaymentMethod PaymentMethod { get; set; } = InvoicePaymentMethod.AchBankTransfer;

        /// <summary>Serialized line items, same shape as the invoice form posts.</summary>
        [Column(TypeName = "LONGTEXT")]
        public string LineItemsJson { get; set; } = "[]";

        [StringLength(400)]
        public string? ServiceAddress { get; set; }

        [StringLength(2000)]
        public string? CustomerNote { get; set; }

        public bool IsActive { get; set; } = true;

        public int CreatedByUserId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
