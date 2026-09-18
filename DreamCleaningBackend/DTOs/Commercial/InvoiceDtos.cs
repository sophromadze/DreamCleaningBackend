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
        /// <summary>Null preserves existing links; an explicit empty list switches to standalone.</summary>
        public List<int>? OrderIds { get; set; }
        [Range(typeof(decimal), "0", "10000000")]
        public decimal? NegotiatedGroupTotal { get; set; }
        public List<string> DraftDriftChoices { get; set; } = new();
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

        /// <summary>
        /// The individual visits this invoice covers, when they are known. Editable while the
        /// invoice is a Draft, which is what "admin must be able to review/edit the generated
        /// dates" means - an empty list simply falls back to the period bounds.
        /// </summary>
        public List<DateTime> ServiceDates { get; set; } = new();

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

        /// <summary>
        /// Ticked when the admin edited the tax rate and wants it to become the default for future
        /// invoices and contracts.
        ///
        /// It writes to <c>BillingSettings</c> only. It cannot reach a finalized invoice or a
        /// signed contract, both of which carry their own rate snapshot - which is exactly why the
        /// rate is snapshotted per document.
        /// </summary>
        public bool SaveTaxRateAsDefault { get; set; }

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

        /// <summary>
        /// Explicit acknowledgement that a Stripe ACH payment is already authorized and settling.
        ///
        /// ACH IS ASYNCHRONOUS: the customer authorized a debit days ago, no money has moved yet,
        /// and the invoice legitimately still reads unpaid. Marking it paid by hand in that window
        /// is how the same money gets counted twice - so the record is refused until an admin says
        /// they know, and the override is recorded on the payment's note trail.
        /// </summary>
        public bool AcknowledgeProcessingPayment { get; set; }
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
        public bool IsArchived { get; set; }
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

        /// <summary>The ACH fee paid on top, when this came through Stripe. Zero otherwise.</summary>
        public decimal ProcessingFee { get; set; }

        /// <summary>Amount + fee - what the customer's bank statement actually shows.</summary>
        public decimal TotalCharged { get; set; }

        public DateTime PaymentDate { get; set; }
        public InvoicePaymentRecordMethod PaymentMethod { get; set; }
        public string PaymentMethodLabel { get; set; } = string.Empty;

        /// <summary>
        /// Who processed it. Surfaced so the payment history can say "Manual" or "Stripe" plainly:
        /// a bank transfer an admin recorded from a statement must never be presented as though it
        /// had come through the processor.
        /// </summary>
        public InvoicePaymentProvider Provider { get; set; }
        public string ProviderLabel { get; set; } = string.Empty;

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

        /// <summary>The invoice balance being settled. Echoed back so the page can confirm it.</summary>
        public decimal Amount { get; set; }

        /// <summary>The ACH processing fee added on top, as the server computed it.</summary>
        public decimal ProcessingFee { get; set; }

        /// <summary>
        /// What the customer's bank will be debited: balance + fee. Sent back so the page can
        /// verify that the figure it showed matches what was actually created - if a stale
        /// balance made them disagree, the SERVER's number is the one that stands.
        /// </summary>
        public decimal TotalCharged { get; set; }
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

        /// <summary>The individual visits, when known. Editable while the invoice is a Draft.</summary>
        public List<DateTime> ServiceDates { get; set; } = new();

        /// <summary>"Service date" / "Service dates" / "Service period" - whichever fits.</summary>
        public string? ServiceDateLabel { get; set; }

        /// <summary>The formatted dates, or null when the invoice records none.</summary>
        public string? ServiceDateText { get; set; }

        public string? PoNumber { get; set; }
        public string? ClientReference { get; set; }

        /// <summary>
        /// Non-blocking warnings raised when this draft was generated — contract price drift, tax
        /// drift, a schedule that could not be worked out.
        ///
        /// They live on the INVOICE, not in the create response, because "Create Next Invoice"
        /// launched from the Contracts list navigates away instantly and a banner on the previous
        /// page is a banner nobody reads. Empty on every invoice that was not generated, and
        /// cleared once the invoice leaves Draft or an admin saves an edit — at that point the
        /// figures have been seen and the warning is either acted on or deliberately accepted.
        /// </summary>
        public List<string> DraftWarnings { get; set; } = new();
        public decimal? CurrentContractUnitPrice { get; set; }
        public InvoiceTaxType? CurrentContractTaxType { get; set; }
        public decimal? CurrentContractTaxRate { get; set; }
        public List<InvoiceOrderAllocationDto> CleaningsCovered { get; set; } = new();

        /// <summary>The agreed group total an admin negotiated for the ORDERS this invoice covers,
        /// when they set one. Null means the invoice simply totals what the cleanings cost.</summary>
        public decimal? NegotiatedOrderGroupTotal { get; set; }

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

        /// <summary>Archived: off the admin default list, everything preserved. Not a status.</summary>
        public bool IsArchived { get; set; }
        public DateTime? ArchivedAt { get; set; }

        /// <summary>
        /// Whether the PERMANENT delete option is offered in the invoice action dialog.
        /// <c>InvoiceHardDeletePolicy</c> decides; the endpoint applies the same policy.
        /// </summary>
        public bool CanHardDelete { get; set; }

        /// <summary>
        /// Why permanent deletion is refused - a payment row, money recorded, Stripe activity,
        /// claimed cleanings. Null when it is allowed. Shown in the dialog so the admin reads the
        /// reason instead of finding a disabled button.
        /// </summary>
        public string? CannotHardDeleteReason { get; set; }

        /// <summary>
        /// True when this invoice has BOTH a Stripe payment and a manual one, and more has been
        /// received than was billed.
        ///
        /// The shape of the accident it catches: an admin marks an invoice paid from a bank
        /// statement while a Stripe ACH debit is still settling, and days later the debit lands.
        /// Nothing is auto-corrected - the money genuinely arrived twice and only a person can
        /// decide which half to refund - but it is FLAGGED rather than quietly banked, because the
        /// customer notices a duplicate debit long before a reconciliation does.
        /// </summary>
        public bool PotentialDuplicatePayment { get; set; }

        /// <summary>
        /// True when a Stripe ACH payment is authorized and settling. The "Mark as Paid" dialog
        /// warns off this before letting an admin record a manual payment on top of it.
        /// </summary>
        public bool HasProcessingStripePayment { get; set; }

        /// <summary>
        /// The contract this invoice can be regenerated from, when it has one. Drives the
        /// "Create Next Invoice" action on the contract, and the pricing-drift warning here.
        /// </summary>
        public string? ContractPricingWarning { get; set; }
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

        /// <summary>The individual visits this invoice covers, when the schedule is known.</summary>
        public List<DateTime> ServiceDates { get; set; } = new();

        /// <summary>
        /// "Service date" / "Service dates" / "Service period" - the label that matches whichever
        /// shape the invoice actually records. Null together with
        /// <see cref="ServiceDateText"/> when the invoice covers no stated period; the surfaces
        /// then print no service line at all rather than inventing one.
        /// </summary>
        public string? ServiceDateLabel { get; set; }

        public string? ServiceDateText { get; set; }

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

        /// <summary>The INVOICE amount being settled — what the payment pays off.</summary>
        public decimal? ProcessingAmount { get; set; }

        /// <summary>The ACH fee that rode along with it. Zero when none applied.</summary>
        public decimal? ProcessingFeeAmount { get; set; }

        /// <summary>
        /// What the customer's bank is actually debited: invoice amount + fee.
        ///
        /// The banner MUST name this. Saying "your bank payment of $925.43 has been initiated"
        /// when $930.43 leaves their account is the kind of small discrepancy that costs trust
        /// precisely because the customer can check it against their statement.
        /// </summary>
        public decimal? ProcessingTotalCharged { get; set; }

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

        // ── The ACH processing fee, quoted BEFORE the customer authorizes anything ────────────
        //
        // Computed server-side from the invoice's own balance and sent down so the page can show
        // the exact extra amount next to the "Pay from Bank" button and again in the confirmation
        // summary. The browser NEVER computes or submits it - the checkout endpoint recalculates
        // from the same settings, so a tampered page can change what is displayed and nothing else.

        /// <summary>The fee in dollars for paying this balance by Stripe ACH. Zero when disabled.</summary>
        public decimal AchProcessingFee { get; set; }

        /// <summary>Balance + fee - the figure the customer's bank will actually be debited.</summary>
        public decimal AchTotalWithFee { get; set; }

        /// <summary>
        /// "ACH Processing Fee". Never a bare "Fee": a vague label on a payment page reads as a
        /// hidden markup, and the customer is entitled to know what the charge is for.
        /// </summary>
        public string AchProcessingFeeLabel { get; set; } = string.Empty;

        /// <summary>
        /// What the manual bank-transfer block says about cost. Deliberately not "No fee" - the
        /// customer's OWN bank may charge them for sending a transfer, and that is not ours to
        /// promise about.
        /// </summary>
        public string ManualAchFeeNote { get; set; } = string.Empty;
    }

    /// <summary>
    /// The company block on the public invoice, the PDF and the invoice email.
    ///
    /// THE TRADING NAME COMES FIRST (2026-09). The company header used to read
    /// "Nodar Alania Inc." with "DBA Dream Cleaning NYC" underneath in small grey text, which is
    /// the wrong way round for a customer: the name they recognise, booked with and will look for
    /// on a bank statement is Dream Cleaning NYC, and the registered entity is the legal footnote.
    /// <see cref="PrimaryName"/> and <see cref="SecondaryName"/> exist so all three surfaces render
    /// the same hierarchy without each one re-deciding it.
    /// </summary>
    public class PublicCompanyDto
    {
        public string LegalName { get; set; } = string.Empty;
        public string? DbaName { get; set; }
        public string? Address { get; set; }
        public string? CityStateZip { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? FooterText { get; set; }

        /// <summary>
        /// What is shown large, bold and in brand blue: "DBA Dream Cleaning NYC". Falls back to
        /// the legal name when no trading name is configured, so the header is never empty.
        /// </summary>
        public string PrimaryName =>
            string.IsNullOrWhiteSpace(DbaName) ? LegalName : $"DBA {DbaName!.Trim()}";

        /// <summary>
        /// The registered entity, immediately underneath in smaller secondary type. Null when
        /// there is no trading name, because printing the legal name twice reads as a bug.
        /// </summary>
        public string? SecondaryName =>
            string.IsNullOrWhiteSpace(DbaName) ? null : LegalName;
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

        // -- The customer-facing Stripe ACH fee --
        public bool AchCustomerFeeEnabled { get; set; }
        public decimal AchCustomerFeeRatePercent { get; set; }
        public decimal AchCustomerFeeCapAmount { get; set; }

        public InvoiceTaxType DefaultTaxType { get; set; }
        public decimal? DefaultTaxRate { get; set; }
        public Models.Contracts.ContractPriceMode DefaultContractPriceMode { get; set; }
        public InvoiceDueTerms DefaultDueTerms { get; set; }
        public string? DefaultCustomerNote { get; set; }
        public string? InvoiceFooterText { get; set; }

        public DateTime UpdatedAt { get; set; }

        /// <summary>Whether the current caller may write these settings (SuperAdmin only).</summary>
        public bool CanEdit { get; set; }
    }

    /// <summary>
    /// The commercial billing defaults a NEW contract or invoice form starts from.
    ///
    /// Served to any admin who can open either form - deliberately WITHOUT the bank details that
    /// live on the same settings row, because neither form displays them and a payload carrying an
    /// account number it never renders is a leak waiting for a future copy-paste.
    /// </summary>
    public class CommercialBillingDefaultsDto
    {
        public InvoiceTaxType DefaultTaxType { get; set; }
        public decimal? DefaultTaxRate { get; set; }
        public Models.Contracts.ContractPriceMode DefaultContractPriceMode { get; set; }
        public InvoiceDueTerms DefaultDueTerms { get; set; }

        public bool AchCustomerFeeEnabled { get; set; }
        public decimal AchCustomerFeeRatePercent { get; set; }
        public decimal AchCustomerFeeCapAmount { get; set; }
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

        public bool AchCustomerFeeEnabled { get; set; } = true;

        [Range(typeof(decimal), "0", "10")]
        public decimal AchCustomerFeeRatePercent { get; set; } = 0.8m;

        [Range(typeof(decimal), "0", "1000")]
        public decimal AchCustomerFeeCapAmount { get; set; } = 5.00m;

        public InvoiceTaxType DefaultTaxType { get; set; } = InvoiceTaxType.Included;
        public decimal? DefaultTaxRate { get; set; } = 8.875m;

        public Models.Contracts.ContractPriceMode DefaultContractPriceMode { get; set; }
            = Models.Contracts.ContractPriceMode.TaxInclusive;

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

    // ── The business customer's own invoices ──────────────────────────────────────────────────

    /// <summary>
    /// One row in the customer's My Invoices list.
    ///
    /// A THIRD DTO rather than a reuse of either admin type, for the reason
    /// <see cref="PublicInvoiceDto"/> spells out: a customer-facing projection whose default is
    /// "carry nothing" cannot leak a field somebody adds to the admin view later. It deliberately
    /// carries no internal note, no activity, no view counts and no row id - the invoice is opened
    /// by its <see cref="PublicToken"/>, the same address the emailed link uses.
    /// </summary>
    public class MyInvoiceListItemDto
    {
        public string InvoiceNumber { get; set; } = string.Empty;

        /// <summary>How the customer opens it: /invoice/{token}, exactly as the email link does.</summary>
        public string PublicToken { get; set; } = string.Empty;

        public string? ContractNumber { get; set; }
        public string? ServiceAddress { get; set; }

        public DateTime InvoiceDate { get; set; }
        public DateTime DueDate { get; set; }

        public DateTime? ServiceStartDate { get; set; }
        public DateTime? ServiceEndDate { get; set; }
        public string? ServiceDateLabel { get; set; }
        public string? ServiceDateText { get; set; }

        public decimal Total { get; set; }
        public decimal AmountPaid { get; set; }
        public decimal BalanceDue { get; set; }
        public string Currency { get; set; } = "USD";

        public InvoiceStatus Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;

        /// <summary>
        /// True while a Stripe ACH debit is authorized and settling. The list shows "Processing"
        /// rather than the underlying status, because for those few days the customer HAS paid as
        /// far as they are concerned and chasing them would be wrong.
        /// </summary>
        public bool PaymentInProgress { get; set; }

        public DateTime? PaidAt { get; set; }
    }

    // ── Recurring generation ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The result of "Create Next Invoice" on a contract: the draft, plus anything the admin
    /// should look at before sending it.
    /// </summary>
    public class CreateNextInvoiceResultDto
    {
        public InvoiceDetailDto Invoice { get; set; } = new();

        /// <summary>The invoice this one was modelled on, when there was a previous sent one.</summary>
        public string? ClonedFromInvoiceNumber { get; set; }

        /// <summary>
        /// NON-BLOCKING warnings. Nothing here stops the draft existing - it is a draft, and the
        /// admin is about to review it. Silently "fixing" any of these instead would be the actual
        /// failure: re-pricing a cloned invoice from the contract, or inventing a service period.
        /// </summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>True when the service dates could not be derived and must be chosen by hand.</summary>
        public bool NeedsServiceDates { get; set; }
    }

    /// <summary>Body of "Create Next Invoice". Carries only what the admin can legitimately override.</summary>
    public class CreateNextInvoiceDto
    {
        public bool AcknowledgeUndatedDraft { get; set; }
        /// <summary>
        /// Proceed even though an issued invoice already covers this billing period.
        ///
        /// The guard exists because generating twice for one month is both easy to do and hard to
        /// notice: the two invoices look identical apart from their numbers, and the client is
        /// billed twice. There are legitimate reasons to override it, so it is a confirmation
        /// rather than a refusal.
        /// </summary>
        public bool AllowDuplicatePeriod { get; set; }
    }

    // ── "Which invoices touch this order / this customer?" ─────────────────────────────────────

    /// <summary>
    /// An invoice as a surface OUTSIDE the Commercial section sees it — the admin Orders panel and
    /// a customer's detail panel.
    ///
    /// A SEPARATE projection rather than a reuse of <see cref="InvoiceListItemDto"/>, for the same
    /// reason <c>PublicInvoiceDto</c> is its own type: these two surfaces need the workflow flags
    /// (<see cref="CanSend"/>) and the per-order allocation, and neither needs the invoice table's
    /// filters. One shape for both would mean every field added for one of them silently appears
    /// on the other.
    /// </summary>
    public class LinkedInvoiceSummaryDto
    {
        public int Id { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public int ContractClientId { get; set; }
        public string ClientName { get; set; } = string.Empty;

        public DateTime InvoiceDate { get; set; }
        public DateTime DueDate { get; set; }

        public decimal Total { get; set; }
        public decimal AmountPaid { get; set; }
        public decimal BalanceDue { get; set; }

        public InvoiceStatus Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;

        public bool HasBeenSent { get; set; }
        public DateTime? LastSentAt { get; set; }
        public DateTime? PaidAt { get; set; }

        /// <summary>
        /// From <c>InvoiceStatusPolicy</c>, never re-derived in the browser — the same rules the
        /// send endpoints enforce, so a button cannot offer something the server refuses.
        /// </summary>
        public bool CanSend { get; set; }
        public bool CanSendReminder { get; set; }

        /// <summary>
        /// Where this invoice would be emailed. Null means it cannot be sent at all yet, which is
        /// worth saying BEFORE somebody presses Send — the same rule the no-account-email warning
        /// on the orders panel follows.
        /// </summary>
        public string? BillingEmail { get; set; }

        /// <summary>How many cleanings this invoice covers in total.</summary>
        public int CoveredOrderCount { get; set; }

        /// <summary>
        /// What this invoice allocates to the ORDER that was asked about, and whether that
        /// allocation is still a draft proposal. Null on the per-customer listing, where there is
        /// no single order in view.
        /// </summary>
        public decimal? AllocatedAmount { get; set; }
        public bool? AllocationIsProposal { get; set; }
    }

    /// <summary>
    /// The Orders panel's answer to "can I bill this cleaning, and on what?".
    ///
    /// Deliberately answers the WHOLE question in one call: the invoices that already cover the
    /// order, and — when none does — whether a client is known so a draft could be started. The
    /// panel must never have to infer the second half from the absence of the first.
    /// </summary>
    public class OrderInvoicesDto
    {
        public int OrderId { get; set; }

        /// <summary>The commercial client this cleaning is billed to, if any.</summary>
        public int? ContractClientId { get; set; }
        public string? ClientName { get; set; }

        /// <summary>
        /// Set when the order carries no client of its own but its customer account is linked to
        /// one — the ordinary case for a business customer's normal booking. It is what lets the
        /// panel offer "put this cleaning on an invoice" without the admin first having to know
        /// that the account is flagged as a business.
        /// </summary>
        public int? SuggestedContractClientId { get; set; }
        public string? SuggestedClientName { get; set; }

        /// <summary>
        /// False when this cleaning can no longer be put on an invoice — already paid, cancelled,
        /// refunded — with <see cref="BlockedReason"/> saying which. Resolved by the SAME rule the
        /// invoice form's picker blocks on.
        /// </summary>
        public bool CanBeInvoiced { get; set; }
        public string? BlockedReason { get; set; }

        public List<LinkedInvoiceSummaryDto> Invoices { get; set; } = new();
    }
}
