using System.ComponentModel.DataAnnotations;
using DreamCleaningBackend.Models.Commercial;

namespace DreamCleaningBackend.DTOs.Commercial
{
    // ── Write side ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One line as the browser submits it.
    ///
    /// NOTE WHAT IS ABSENT: there is no Amount. The line total is derived server-side by
    /// <c>InvoiceCalculator.LineAmount</c>, so a caller literally cannot submit one - the same
    /// shape ContractPricingInputDto uses to make a browser-supplied total unrepresentable rather
    /// than merely ignored.
    /// </summary>
    public class SaveInvoiceItemDto
    {
        public int? Id { get; set; }

        [Required, StringLength(500)]
        public string Description { get; set; } = string.Empty;

        [Range(typeof(decimal), "0", "100000")]
        public decimal Quantity { get; set; } = 1m;

        [Range(typeof(decimal), "0", "1000000")]
        public decimal UnitPrice { get; set; }

        public int SortOrder { get; set; }
    }

    /// <summary>
    /// Create or update an invoice. Also carries no SubTotal, TaxAmount, Total or BalanceDue,
    /// for the same reason.
    /// </summary>
    public class SaveInvoiceDto
    {
        [Required]
        public int ContractClientId { get; set; }

        public int? ContractId { get; set; }
        public int? ContractServiceLocationId { get; set; }

        public DateTime? InvoiceDate { get; set; }

        public InvoiceDueTerms DueTerms { get; set; } = InvoiceDueTerms.Net15;

        /// <summary>Only read when <see cref="DueTerms"/> is Custom.</summary>
        public DateTime? CustomDueDate { get; set; }

        public DateTime? ServiceStartDate { get; set; }
        public DateTime? ServiceEndDate { get; set; }

        [StringLength(400)]
        public string? ServiceAddress { get; set; }

        [StringLength(100)]
        public string? PoNumber { get; set; }

        [StringLength(100)]
        public string? ClientReference { get; set; }

        public InvoiceDiscountType DiscountType { get; set; } = InvoiceDiscountType.None;
        public decimal? DiscountValue { get; set; }

        public InvoiceTaxType TaxType { get; set; } = InvoiceTaxType.Exempt;
        public decimal? TaxRate { get; set; }

        public InvoicePaymentMethod PaymentMethod { get; set; } = InvoicePaymentMethod.AchBankTransfer;

        [StringLength(2000)]
        public string? CustomerNote { get; set; }

        [StringLength(2000)]
        public string? InternalNote { get; set; }

        public List<SaveInvoiceItemDto> Items { get; set; } = new();
    }

    /// <summary>Record money received. The amount is validated against the balance server-side.</summary>
    public class RecordInvoicePaymentDto
    {
        [Range(typeof(decimal), "0.01", "1000000")]
        public decimal Amount { get; set; }

        public DateTime? PaymentDate { get; set; }

        public InvoicePaymentRecordMethod PaymentMethod { get; set; } = InvoicePaymentRecordMethod.AchBankTransfer;

        [StringLength(200)]
        public string? TransactionReference { get; set; }

        [StringLength(1000)]
        public string? InternalNote { get; set; }

        /// <summary>
        /// Explicit acknowledgement that the amount exceeds the balance. Without it an
        /// overpayment is REFUSED rather than silently accepted - see InvoicePaymentService.
        /// </summary>
        public bool AllowOverpayment { get; set; }
    }

    public class VoidInvoiceDto
    {
        [Required, StringLength(500)]
        public string Reason { get; set; } = string.Empty;
    }

    public class SendInvoiceDto
    {
        /// <summary>Overrides the client's billing email for this send only.</summary>
        [StringLength(255)]
        public string? RecipientEmail { get; set; }

        /// <summary>Adds the PDF as an attachment alongside the link.</summary>
        public bool AttachPdf { get; set; } = true;

        [StringLength(1000)]
        public string? Message { get; set; }
    }

    public class SendInvoiceReminderDto
    {
        [StringLength(255)]
        public string? RecipientEmail { get; set; }

        /// <summary>Bypasses the same-day duplicate guard when an admin insists.</summary>
        public bool Force { get; set; }
    }

    // ── Read side ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A row in the invoice table. Deliberately narrow - the list must stay cheap.</summary>
    public class InvoiceListItemDto
    {
        public int Id { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public int ContractClientId { get; set; }
        public string ClientName { get; set; } = string.Empty;
        public string? ServiceAddress { get; set; }
        public string? ContractNumber { get; set; }
        public DateTime InvoiceDate { get; set; }
        public DateTime DueDate { get; set; }
        public decimal Total { get; set; }
        public decimal AmountPaid { get; set; }
        public decimal BalanceDue { get; set; }
        public InvoiceStatus Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;
        public InvoicePaymentMethod PaymentMethod { get; set; }
        public DateTime? PaidAt { get; set; }
        public DateTime? LastSentAt { get; set; }
        public bool HasBeenSent { get; set; }
    }

    /// <summary>The summary cards above the table.</summary>
    public class InvoiceSummaryDto
    {
        /// <summary>Everything issued, unpaid and not void. Includes overdue.</summary>
        public decimal TotalOutstanding { get; set; }

        /// <summary>Money RECEIVED this calendar month, by payment date - not invoices issued.</summary>
        public decimal PaidThisMonth { get; set; }

        public decimal Overdue { get; set; }
        public int DraftCount { get; set; }
        public int OverdueCount { get; set; }
        public int OutstandingCount { get; set; }
    }

    public class InvoiceListResponseDto
    {
        public List<InvoiceListItemDto> Invoices { get; set; } = new();
        public InvoiceSummaryDto Summary { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }

    public class InvoiceItemDto
    {
        public int Id { get; set; }
        public string Description { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Amount { get; set; }
        public int SortOrder { get; set; }
    }

    public class InvoicePaymentDto
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
        public DateTime PaymentDate { get; set; }
        public InvoicePaymentRecordMethod PaymentMethod { get; set; }
        public string PaymentMethodLabel { get; set; } = string.Empty;
        public string? TransactionReference { get; set; }
        public string? InternalNote { get; set; }
        public bool IsReversal { get; set; }
        public string? RecordedByName { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// An online payment attempt, for the admin detail page.
    ///
    /// Carries a SAFE funding-source label only — "Wells Fargo ••••6789", which is all Stripe
    /// exposes. Full customer bank details are never stored and so cannot be shown.
    /// </summary>
    public class InvoicePaymentAttemptDto
    {
        public int Id { get; set; }
        public InvoicePaymentProvider Provider { get; set; }
        public InvoicePaymentRecordMethod PaymentMethod { get; set; }
        public string PaymentMethodLabel { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public InvoicePaymentAttemptStatus Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;

        /// <summary>Safe to show an admin; useful for looking the payment up in Stripe.</summary>
        public string? StripePaymentIntentId { get; set; }

        /// <summary>Bank/card name and last four only.</summary>
        public string? PaymentSourceLabel { get; set; }

        /// <summary>Stripe's own code. Admin-facing only; the customer never sees it.</summary>
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool IsInFlight { get; set; }
    }

    /// <summary>What the browser needs to send the customer into Stripe's hosted flow.</summary>
    public class StartInvoiceCheckoutResponseDto
    {
        /// <summary>Stripe's hosted Checkout URL. The browser navigates to it; nothing is embedded.</summary>
        public string CheckoutUrl { get; set; } = string.Empty;

        public int AttemptId { get; set; }

        /// <summary>Echoed back so the page can confirm what is being charged.</summary>
        public decimal Amount { get; set; }
    }

    /// <summary>
    /// Which method the customer picked. Deliberately carries NO amount — the server reads the
    /// balance from the invoice, so a tampered request cannot express a different figure.
    /// </summary>
    public class StartInvoiceCheckoutDto
    {
        public InvoicePaymentRecordMethod Method { get; set; } = InvoicePaymentRecordMethod.AchBankTransfer;
    }

    public class InvoiceEmailLogDto
    {
        public long Id { get; set; }
        public InvoiceEmailType EmailType { get; set; }
        public string EmailTypeLabel { get; set; } = string.Empty;
        public string Recipient { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public InvoiceEmailStatus Status { get; set; }
        public string? FailureReason { get; set; }
        public DateTime SentAt { get; set; }
    }

    public class InvoiceActivityLogDto
    {
        public long Id { get; set; }
        public string Action { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? ActorName { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// The full admin view of one invoice. Carries the internal note and the activity trail, so
    /// it must NEVER be returned from a public endpoint - see <see cref="PublicInvoiceDto"/>.
    /// </summary>
    public class InvoiceDetailDto
    {
        public int Id { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;

        /// <summary>The admin's copy of the public link, for "Copy invoice link".</summary>
        public string PublicUrl { get; set; } = string.Empty;

        public InvoiceStatus Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;

        public int ContractClientId { get; set; }
        public string ClientName { get; set; } = string.Empty;
        public string? BillingContactName { get; set; }
        public string? BillingEmail { get; set; }
        public string? BillingPhone { get; set; }
        public string? BillingAddress { get; set; }

        public int? ContractId { get; set; }
        public string? ContractNumber { get; set; }

        public int? ContractServiceLocationId { get; set; }
        public string? ServiceAddress { get; set; }

        public DateTime InvoiceDate { get; set; }
        public DateTime DueDate { get; set; }
        public InvoiceDueTerms DueTerms { get; set; }
        public DateTime? ServiceStartDate { get; set; }
        public DateTime? ServiceEndDate { get; set; }

        public string? PoNumber { get; set; }
        public string? ClientReference { get; set; }

        public decimal SubTotal { get; set; }
        public InvoiceDiscountType DiscountType { get; set; }
        public decimal? DiscountValue { get; set; }
        public decimal DiscountAmount { get; set; }
        public InvoiceTaxType TaxType { get; set; }
        public decimal? TaxRate { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal Total { get; set; }
        public decimal AmountPaid { get; set; }
        public decimal BalanceDue { get; set; }

        /// <summary>Above zero only when more was received than billed.</summary>
        public decimal Overpayment { get; set; }

        public string Currency { get; set; } = "USD";
        public InvoicePaymentMethod PaymentMethod { get; set; }

        public string? CustomerNote { get; set; }
        public string? InternalNote { get; set; }

        public DateTime? FirstSentAt { get; set; }
        public DateTime? LastSentAt { get; set; }
        public DateTime? FirstViewedAt { get; set; }
        public DateTime? LastViewedAt { get; set; }
        public int ViewCount { get; set; }
        public DateTime? PaidAt { get; set; }
        public DateTime? VoidedAt { get; set; }
        public string? VoidReason { get; set; }

        public string? CreatedByName { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public int? DuplicatedFromInvoiceId { get; set; }

        public List<InvoiceItemDto> Items { get; set; } = new();
        public List<InvoicePaymentDto> Payments { get; set; } = new();
        public List<InvoiceEmailLogDto> EmailHistory { get; set; } = new();
        public List<InvoiceActivityLogDto> Activity { get; set; } = new();

        /// <summary>Online payment attempts, newest first. Empty for a manual-only invoice.</summary>
        public List<InvoicePaymentAttemptDto> PaymentAttempts { get; set; } = new();

        /// <summary>
        /// True while a Stripe payment is authorized or settling. The admin panel shows "ACH
        /// payment processing" rather than chasing an invoice whose money is already on its way.
        /// </summary>
        public bool HasPaymentInProgress { get; set; }

        // What the UI is allowed to offer, decided by InvoiceStatusPolicy on the server so the
        // action menu and the endpoints cannot disagree about what is permitted.
        public bool CanEdit { get; set; }
        public bool CanEditMonetaryValues { get; set; }
        public bool EditRequiresWarning { get; set; }
        public bool CanSend { get; set; }
        public bool CanRecordPayment { get; set; }
        public bool CanVoid { get; set; }
        public bool CanDelete { get; set; }
        public bool CanSendReminder { get; set; }
    }

    // ── The public page ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What an unauthenticated holder of the invoice link may see.
    ///
    /// A SEPARATE TYPE FROM <see cref="InvoiceDetailDto"/> ON PURPOSE, not a filtered copy of it.
    /// If the public endpoint projected the admin DTO and blanked fields, then every future field
    /// added to the admin view would be exposed by default and would have to be remembered. Here
    /// the default is the opposite: a new admin field appears publicly only if someone adds it to
    /// this class deliberately.
    ///
    /// Never present: the internal note, the activity log, the row id, the public token, view
    /// counts, and who created it.
    /// </summary>
    public class PublicInvoiceDto
    {
        public string InvoiceNumber { get; set; } = string.Empty;
        public InvoiceStatus Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;

        public DateTime InvoiceDate { get; set; }
        public DateTime DueDate { get; set; }
        public DateTime? ServiceStartDate { get; set; }
        public DateTime? ServiceEndDate { get; set; }

        public string ClientName { get; set; } = string.Empty;
        public string? BillingContactName { get; set; }
        public string? BillingAddress { get; set; }
        public string? ServiceAddress { get; set; }

        /// <summary>Shown as a reference only; the contract itself is not reachable from here.</summary>
        public string? ContractNumber { get; set; }

        public string? PoNumber { get; set; }
        public string? ClientReference { get; set; }

        public List<InvoiceItemDto> Items { get; set; } = new();

        public decimal SubTotal { get; set; }
        public decimal DiscountAmount { get; set; }
        public InvoiceTaxType TaxType { get; set; }
        public decimal? TaxRate { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal Total { get; set; }
        public decimal AmountPaid { get; set; }
        public decimal BalanceDue { get; set; }
        public string Currency { get; set; } = "USD";

        public DateTime? PaidAt { get; set; }
        public string? CustomerNote { get; set; }

        public InvoicePaymentMethod PaymentMethod { get; set; }

        /// <summary>
        /// The manual ACH bank block. NULL unless manual ACH is both enabled AND fully configured —
        /// partial instructions are worse than none, because the customer only finds out at their
        /// bank. Also null on a void invoice, which must not invite a payment.
        /// </summary>
        public PublicPaymentInstructionsDto? PaymentInstructions { get; set; }

        /// <summary>What the customer can actually do right now. Decided entirely server-side.</summary>
        public PublicPaymentOptionsDto PaymentOptions { get; set; } = new();

        public PublicCompanyDto Company { get; set; } = new();
    }

    /// <summary>
    /// Which payment routes the public page may offer, and whether one is already under way.
    ///
    /// EVERY FLAG IS DECIDED ON THE SERVER. The page renders what it is told rather than deriving
    /// availability from settings it would have to be given — and the Checkout endpoint re-checks
    /// all of it anyway, so hiding a button is presentation and the endpoint is the control.
    /// </summary>
    public class PublicPaymentOptionsDto
    {
        /// <summary>"Pay from Bank" — Stripe ACH Direct Debit.</summary>
        public bool StripeAchAvailable { get; set; }

        /// <summary>"Pay by Card" — off by default, an owner's decision to enable.</summary>
        public bool StripeCardAvailable { get; set; }

        /// <summary>The collapsible manual bank-transfer block.</summary>
        public bool ManualAchAvailable { get; set; }

        /// <summary>
        /// True while a Stripe payment for this invoice is authorized or settling. The page shows
        /// "payment processing" and disables the pay buttons — ACH takes days, during which the
        /// invoice still reads unpaid and an impatient customer would otherwise pay twice.
        /// </summary>
        public bool PaymentInProgress { get; set; }

        /// <summary>The amount already in flight, so the banner can name it.</summary>
        public decimal? ProcessingAmount { get; set; }

        /// <summary>When that payment was started.</summary>
        public DateTime? ProcessingStartedAt { get; set; }

        /// <summary>
        /// True when the customer's last attempt failed and nothing is in flight, so the page can
        /// say so and invite a retry. Carries no Stripe error detail — see LastFailureMessage.
        /// </summary>
        public bool LastAttemptFailed { get; set; }

        /// <summary>
        /// FIXED, CUSTOMER-SAFE WORDING. Never Stripe's own failure message: a processor's
        /// internal text ("account_closed", "debit not authorized on this mandate") is not
        /// something to put in front of a customer. The detail is kept admin-side on the attempt.
        /// </summary>
        public string? LastFailureMessage { get; set; }
    }

    /// <summary>The company block on the public invoice and the PDF.</summary>
    public class PublicCompanyDto
    {
        public string LegalName { get; set; } = string.Empty;
        public string? DbaName { get; set; }
        public string? Address { get; set; }
        public string? CityStateZip { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? FooterText { get; set; }
    }

    /// <summary>
    /// The ACH block.
    ///
    /// These are RECEIVING coordinates and the client cannot pay without them, so they are shown
    /// in full on the invoice - that is the entire purpose of the page. What is NOT here, and is
    /// not anywhere in the API, is anything that grants access to the account.
    /// </summary>
    public class PublicPaymentInstructionsDto
    {
        public string Method { get; set; } = "ACH Bank Transfer";
        public string? BankName { get; set; }
        public string? AccountHolder { get; set; }
        public string? RoutingNumber { get; set; }
        public string? AccountNumber { get; set; }
        public string? AccountType { get; set; }
        public string? AchInstructions { get; set; }
        public string? WireInstructions { get; set; }

        /// <summary>Always the invoice number - what the client is asked to put in the memo.</summary>
        public string PaymentReference { get; set; } = string.Empty;
    }

    // ── Billing settings ──────────────────────────────────────────────────────────────────────

    public class BillingSettingsDto
    {
        public string CompanyLegalName { get; set; } = string.Empty;
        public string? CompanyDbaName { get; set; }
        public string? CompanyAddress { get; set; }
        public string? CompanyCity { get; set; }
        public string? CompanyState { get; set; }
        public string? CompanyZip { get; set; }
        public string? CompanyPhone { get; set; }
        public string? CompanyEmail { get; set; }

        public string? BankName { get; set; }
        public string? BankAccountHolder { get; set; }
        public string? BankRoutingNumber { get; set; }

        /// <summary>
        /// MASKED for a caller who may not edit these settings - only the last four digits, so an
        /// admin can confirm which account an invoice points at without the list view or a browser
        /// cache holding the full number. The unmasked value is served only to a SuperAdmin
        /// opening the settings form, and to the client on their own invoice, who needs it to pay.
        /// </summary>
        public string? BankAccountNumber { get; set; }

        public bool BankAccountNumberMasked { get; set; }

        public string? BankAccountType { get; set; }
        public string? AchInstructions { get; set; }
        public string? WireInstructions { get; set; }
        public string? BankWireRoutingNumber { get; set; }

        // -- Which methods commercial invoices offer --
        public bool StripeAchEnabled { get; set; }
        public bool StripeCardEnabled { get; set; }
        public bool ManualAchEnabled { get; set; }

        /// <summary>
        /// Whether the manual bank block has everything a customer needs to actually send money.
        /// Drives the admin warning; the public page refuses to render a partial block regardless.
        /// </summary>
        public bool ManualAchComplete { get; set; }

        /// <summary>Field NAMES only — never values.</summary>
        public List<string> MissingManualAchFields { get; set; } = new();

        public InvoiceTaxType DefaultTaxType { get; set; }
        public decimal? DefaultTaxRate { get; set; }
        public InvoiceDueTerms DefaultDueTerms { get; set; }
        public string? DefaultCustomerNote { get; set; }
        public string? InvoiceFooterText { get; set; }

        public DateTime UpdatedAt { get; set; }

        /// <summary>Whether the current caller may write these settings (SuperAdmin only).</summary>
        public bool CanEdit { get; set; }
    }

    public class SaveBillingSettingsDto
    {
        [Required, StringLength(200)]
        public string CompanyLegalName { get; set; } = string.Empty;

        [StringLength(200)] public string? CompanyDbaName { get; set; }
        [StringLength(300)] public string? CompanyAddress { get; set; }
        [StringLength(100)] public string? CompanyCity { get; set; }
        [StringLength(50)] public string? CompanyState { get; set; }
        [StringLength(20)] public string? CompanyZip { get; set; }
        [StringLength(50)] public string? CompanyPhone { get; set; }
        [StringLength(255)] public string? CompanyEmail { get; set; }

        [StringLength(200)] public string? BankName { get; set; }
        [StringLength(200)] public string? BankAccountHolder { get; set; }
        [StringLength(20)] public string? BankRoutingNumber { get; set; }

        /// <summary>
        /// Left null to KEEP the stored number. The settings form sends null when the admin did
        /// not retype it, so loading the page and pressing Save cannot blank the account number.
        /// </summary>
        [StringLength(40)] public string? BankAccountNumber { get; set; }

        [StringLength(60)] public string? BankAccountType { get; set; }
        [StringLength(1000)] public string? AchInstructions { get; set; }
        [StringLength(1000)] public string? WireInstructions { get; set; }
        [StringLength(20)] public string? BankWireRoutingNumber { get; set; }

        public bool StripeAchEnabled { get; set; } = true;
        public bool StripeCardEnabled { get; set; } = false;
        public bool ManualAchEnabled { get; set; } = true;

        public InvoiceTaxType DefaultTaxType { get; set; } = InvoiceTaxType.Exempt;
        public decimal? DefaultTaxRate { get; set; }
        public InvoiceDueTerms DefaultDueTerms { get; set; } = InvoiceDueTerms.Net15;

        [StringLength(2000)] public string? DefaultCustomerNote { get; set; }
        [StringLength(500)] public string? InvoiceFooterText { get; set; }
    }

    // ── Supporting lookups for the create form ────────────────────────────────────────────────

    /// <summary>A client option, with everything the form prefills from.</summary>
    public class InvoiceClientOptionDto
    {
        public int Id { get; set; }
        public string LegalEntityName { get; set; } = string.Empty;
        public string? BillingContactName { get; set; }
        public string? BillingEmail { get; set; }
        public string? BillingPhone { get; set; }
        public string? BillingAddress { get; set; }
        public List<InvoiceLocationOptionDto> Locations { get; set; } = new();
        public List<InvoiceContractOptionDto> Contracts { get; set; } = new();

        // ── Fields the Commercial → Clients screen renders. The invoice form ignores them; they
        //    live here because that screen shows exactly this join (client + billing + locations +
        //    contracts) and a second endpoint returning the same rows would be the thing that
        //    drifts. ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// False only ever on the Clients screen with "show inactive" on — the invoice and contract
        /// pickers never receive an inactive client at all.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// The business-flagged account this client belongs to, or null for a standalone client.
        /// Drives the "Linked account" / "Standalone" badge, and the delete confirmation, which has
        /// to warn that removing a linked client also drops that customer's business designation.
        /// </summary>
        public int? SourceUserId { get; set; }
        public string? LinkedAccountName { get; set; }

        /// <summary>
        /// The ACCOUNT's own email, shown beside the client's billing email rather than merged
        /// into it. They are allowed to differ — one is a login, the other is where invoices go —
        /// so the screen makes the difference visible instead of silently reconciling it.
        /// </summary>
        public string? LinkedAccountEmail { get; set; }

        /// <summary>How much history a delete would leave behind. Counts, never amounts.</summary>
        public int InvoiceCount { get; set; }

        /// <summary>
        /// The client's first service location in RAW components, so the edit form can round-trip
        /// it. The <c>Locations</c> list below carries the same row formatted for display; editing
        /// from that string would mean re-parsing an address the server just joined together, and
        /// a parse that got it slightly wrong would silently rewrite where we send cleaners.
        /// </summary>
        public InvoiceClientLocationFieldsDto? PrimaryLocation { get; set; }

        /// <summary>The company record itself, for the edit form. Address is already split above.</summary>
        public string? EntityType { get; set; }
        public string? FormationState { get; set; }
        public string? PrincipalAddress { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? Zip { get; set; }
        public string? NoticeEmail { get; set; }

        /// <summary>Split first/last so the edit form can round-trip the contact it renders.</summary>
        public string? BillingContactFirstName { get; set; }
        public string? BillingContactLastName { get; set; }
        public string? BillingContactTitle { get; set; }
    }

    public class InvoiceLocationOptionDto
    {
        public int Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
    }

    /// <summary>One service location as the edit form needs it — unjoined, field by field.</summary>
    public class InvoiceClientLocationFieldsDto
    {
        public int Id { get; set; }
        public string? BusinessBrand { get; set; }
        public string? LocationName { get; set; }
        public string Address { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Zip { get; set; } = string.Empty;
    }

    /// <summary>
    /// A contract offered in the "Related contract" dropdown, carrying the figures the invoice
    /// prefills from so selecting one does not need a second round trip.
    /// </summary>
    public class InvoiceContractOptionDto
    {
        public int Id { get; set; }
        public string ContractNumber { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public string? ServiceAddress { get; set; }
        public int? ServiceLocationId { get; set; }
        public string? ServiceDescription { get; set; }

        /// <summary>The agreed recurring amount, pre-tax where the contract separates it.</summary>
        public decimal? AgreedAmount { get; set; }

        public decimal? TaxRate { get; set; }
        public InvoiceTaxType? TaxType { get; set; }
        public string? PaymentTerms { get; set; }
    }
}
