using System.ComponentModel.DataAnnotations;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;

namespace DreamCleaningBackend.DTOs
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Directory (contractor profiles, clients, contacts, locations, templates)
    // ══════════════════════════════════════════════════════════════════════════

    public class ContractorProfileDto
    {
        public int Id { get; set; }
        public string LegalEntityName { get; set; } = string.Empty;
        public string? Dba { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Zip { get; set; } = string.Empty;
        public string NoticeEmail { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public bool IsDefault { get; set; }
    }

    public class SaveContractorProfileDto
    {
        [Required, StringLength(200)] public string LegalEntityName { get; set; } = string.Empty;
        [StringLength(200)] public string? Dba { get; set; }
        [Required, StringLength(120)] public string EntityType { get; set; } = string.Empty;
        [Required, StringLength(300)] public string Address { get; set; } = string.Empty;
        [Required, StringLength(100)] public string City { get; set; } = string.Empty;
        [Required, StringLength(50)] public string State { get; set; } = string.Empty;
        [Required, StringLength(20)] public string Zip { get; set; } = string.Empty;
        [Required, StringLength(255)] public string NoticeEmail { get; set; } = string.Empty;
        [StringLength(50)] public string? Phone { get; set; }
        public bool IsDefault { get; set; }
    }

    public class ContractClientDto
    {
        public int Id { get; set; }
        public string LegalEntityName { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string? FormationState { get; set; }
        public string PrincipalAddress { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Zip { get; set; } = string.Empty;
        public string? NoticeEmail { get; set; }
        public string? Phone { get; set; }
        public bool IsActive { get; set; }

        /// <summary>Linked business account, if any. Drives the customer's My Contracts access.</summary>
        public int? SourceUserId { get; set; }
        public string? SourceUserName { get; set; }
        public string? SourceUserEmail { get; set; }

        public List<ContractServiceLocationDto> ServiceLocations { get; set; } = new();
        public List<ContractContactDto> Contacts { get; set; } = new();
    }

    public class SaveContractClientDto
    {
        [Required, StringLength(200)] public string LegalEntityName { get; set; } = string.Empty;
        [Required, StringLength(120)] public string EntityType { get; set; } = string.Empty;
        [StringLength(50)] public string? FormationState { get; set; }
        [Required, StringLength(300)] public string PrincipalAddress { get; set; } = string.Empty;
        [Required, StringLength(100)] public string City { get; set; } = string.Empty;
        [Required, StringLength(50)] public string State { get; set; } = string.Empty;
        [Required, StringLength(20)] public string Zip { get; set; } = string.Empty;
        [StringLength(255)] public string? NoticeEmail { get; set; }
        [StringLength(50)] public string? Phone { get; set; }

        /// <summary>
        /// Links this commercial client to a business-flagged customer account, which is what
        /// opens the self-service My Contracts area for them. Null leaves the contract reachable
        /// by emailed link only. The server rejects an account that is not business-flagged.
        /// </summary>
        public int? SourceUserId { get; set; }
    }

    /// <summary>
    /// The body of <c>POST api/crm/contract-directory/clients</c> — creating a commercial client
    /// on its own, with no contract involved.
    ///
    /// It EXTENDS <see cref="SaveContractClientDto"/> rather than redeclaring its fields, so the
    /// standalone form and the contract form can never end up validating the company differently.
    /// The two additions are optional and exist because a client created here would otherwise have
    /// no billing contact and no service location — the two things the invoice form reads off a
    /// client — and there would be nowhere to enter them except by creating the contract we are
    /// trying to avoid.
    ///
    /// <b>The nested <c>ContractClientId</c> fields are ignored.</b> The client is being created in
    /// this same call, so its id does not exist when the body is written; the server sets both from
    /// the row it just inserted.
    /// </summary>
    public class CreateCommercialClientDto : SaveContractClientDto
    {
        /// <summary>
        /// Who the invoices are addressed to. Optional: a client can be created now and the
        /// contact added later, and the invoice form already warns when one is missing.
        /// </summary>
        public SaveContractContactDto? BillingContact { get; set; }

        /// <summary>
        /// One premises. Optional, and deliberately singular — a client with several sites gets the
        /// first one here and the rest through the contract flow, rather than this form growing
        /// into a second location editor.
        /// </summary>
        public SaveContractServiceLocationDto? ServiceLocation { get; set; }
    }

    /// <summary>
    /// Editing a commercial client from Commercial → Clients. Same body as creation minus one
    /// thing: <b>the link cannot be re-pointed by an edit.</b> <c>SourceUserId</c> is inherited but
    /// deliberately not read by <c>UpdateClient</c> — the link is made and unmade by the business
    /// flag on the account (and by Delete), because it grants that customer sight of the client's
    /// contracts, and a billing edit is not where that decision belongs.
    /// </summary>
    public class UpdateCommercialClientDto : CreateCommercialClientDto
    {
    }

    public class ContractServiceLocationDto
    {
        public int Id { get; set; }
        public int ContractClientId { get; set; }
        public string? BusinessBrand { get; set; }
        public string? LocationName { get; set; }
        public string Address { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Zip { get; set; } = string.Empty;
        /// <summary>"Chick-fil-A - 1569 Flatbush Ave., Brooklyn, NY" for pickers and list rows.</summary>
        public string DisplayLabel { get; set; } = string.Empty;
    }

    public class SaveContractServiceLocationDto
    {
        public int ContractClientId { get; set; }
        [StringLength(200)] public string? BusinessBrand { get; set; }
        [StringLength(200)] public string? LocationName { get; set; }
        [Required, StringLength(300)] public string Address { get; set; } = string.Empty;
        [Required, StringLength(100)] public string City { get; set; } = string.Empty;
        [Required, StringLength(50)] public string State { get; set; } = string.Empty;
        [Required, StringLength(20)] public string Zip { get; set; } = string.Empty;
    }

    public class ContractContactDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public ContractContactRole Role { get; set; }
        public int? ContractClientId { get; set; }
    }

    public class SaveContractContactDto
    {
        [Required, StringLength(100)] public string FirstName { get; set; } = string.Empty;
        [Required, StringLength(100)] public string LastName { get; set; } = string.Empty;
        [StringLength(120)] public string? Title { get; set; }
        [StringLength(255)] public string? Email { get; set; }
        [StringLength(50)] public string? Phone { get; set; }
        public ContractContactRole Role { get; set; } = ContractContactRole.ClientSigner;
        public int? ContractClientId { get; set; }
    }

    /// <summary>
    /// A BUSINESS TYPE and its default scope-of-work checklist - Restaurant, Gym/Studio, Office,
    /// and whatever an admin adds next. The categories and items live inside
    /// <see cref="Structure"/>, so a new premises type is a data row rather than a code change.
    /// </summary>
    public class ScopeTemplateDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;

        /// <summary>The noun the agreement uses: "restaurant", "studio", "office".</summary>
        public string PremisesType { get; set; } = string.Empty;

        public bool AllowsCustomRows { get; set; }
        public int SortOrder { get; set; }

        /// <summary>False for an archived type: still resolvable by old contracts, no longer offered.</summary>
        public bool IsActive { get; set; } = true;

        public ScopeStructure Structure { get; set; } = new();
    }

    /// <summary>
    /// Creating or editing a business type. The whole checklist is sent as one document because
    /// the editor manipulates a tree - categories reordered, items added, renamed, re-flagged or
    /// archived - and per-row endpoints would turn one screenful of edits into a dozen requests
    /// that can half-fail.
    /// </summary>
    public class SaveScopeTemplateDto
    {
        [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
        [StringLength(60)] public string? PremisesType { get; set; }
        public bool AllowsCustomRows { get; set; }
        public int SortOrder { get; set; }

        /// <summary>Null leaves the stored checklist untouched, so a rename need not resend it.</summary>
        public ScopeStructure? Structure { get; set; }
    }

    public class ContractTemplateDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; }

        /// <summary>
        /// The body a NEW contract starts from. At most one row carries it.
        ///
        /// The create form MUST preselect this rather than the first row it happens to receive:
        /// the master agreement is versioned by adding a row, so "first" is the OLDEST version and
        /// preselecting it silently keeps issuing superseded language.
        /// </summary>
        public bool IsDefault { get; set; }

        /// <summary>Only returned on the single-template read used by the SuperAdmin editor.</summary>
        public string? BodyText { get; set; }
    }

    public class SaveContractTemplateDto
    {
        [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
        [Required, StringLength(40)] public string Version { get; set; } = "1.0";
        [StringLength(500)] public string? Description { get; set; }
        [Required] public string BodyText { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Create / edit contract
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The whole Create Contract form. Everything the admin can set lives here; the SERVER
    /// derives the pricing figures, the contract number, and the rendered document.
    /// </summary>
    public class SaveContractDto
    {
        public int ContractTemplateId { get; set; }
        public int? ScopeTemplateId { get; set; }
        public int ContractorProfileId { get; set; }

        public DateTime? EffectiveDate { get; set; }

        /// <summary>Existing client, or null when <see cref="NewClient"/> carries a new one.</summary>
        public int? ContractClientId { get; set; }
        public SaveContractClientDto? NewClient { get; set; }

        public int? ContractServiceLocationId { get; set; }
        public SaveContractServiceLocationDto? NewServiceLocation { get; set; }

        /// <summary>
        /// Contractor signer NAME and TITLE come from the contractor profile and are not
        /// per-contract editable; only the email may be overridden here.
        /// </summary>
        [StringLength(255)] public string? ContractorSignerEmail { get; set; }
        public int? ContractorSignerContactId { get; set; }

        public int? ClientSignerContactId { get; set; }
        public SaveContractContactDto? NewClientSigner { get; set; }

        [StringLength(60)] public string? PremisesType { get; set; }

        public ScheduleSnapshot Schedule { get; set; } = new();

        /// <summary>
        /// How often this contract is INVOICED - separate from how often it is cleaned. Absent
        /// from an older client's payload, where it deserialises to monthly-every-one, which is
        /// what every existing commercial arrangement is on.
        /// </summary>
        public BillingCadenceSnapshot Billing { get; set; } = new();

        public TermSnapshot Term { get; set; } = new();
        public ContractPricingInputDto Pricing { get; set; } = new();
        public AdvancedTermsSnapshot Advanced { get; set; } = new();

        /// <summary>
        /// Exhibit A's recorded site facts - restroom counts, floor materials, glass locations.
        /// Every field is optional; an unfilled one renders as a visible ruled blank rather than
        /// asserting the premises does not have that thing.
        /// </summary>
        public SiteDetailsSnapshot SiteDetails { get; set; } = new();

        /// <summary>Exhibit B4 - approval, notice and on-call contacts for both Parties.</summary>
        public OperationalContactsSnapshot Contacts { get; set; } = new();

        /// <summary>Exhibit B3 - endorsements agreed beyond the Section 20 baseline.</summary>
        public InsuranceEndorsementsSnapshot Insurance { get; set; } = new();

        /// <summary>The toggled scope checklist. Sent whole; unchecked items simply arrive false.</summary>
        public ScopeStructure Scope { get; set; } = new();
    }

    /// <summary>
    /// The only pricing fields a client may send. Pre-tax / tax / total / cancellation /
    /// remaining / lockout are computed by <see cref="ContractPricingCalculator"/> and any values
    /// posted for them are discarded.
    /// </summary>
    public class ContractPricingInputDto
    {
        public ContractPriceMode PriceMode { get; set; } = ContractPriceMode.PreTax;
        public decimal PriceInput { get; set; }
        public decimal SalesTaxRatePercent { get; set; } = 8.875m;
        public decimal CancellationPercent { get; set; } = 50m;

        [StringLength(500)] public string InvoiceTiming { get; set; } =
            "In advance of each scheduled service visit, generally several days before service.";
        public int PaymentDeadlineHours { get; set; } = 48;
        [StringLength(200)] public string PaymentMethod { get; set; } = "ACH or bank transfer using verified instructions";
        public decimal LateChargePercent { get; set; } = 1m;

        /// <summary>
        /// Section 29(b): the aggregate liability cap as a multiple of the pre-tax per-visit fee.
        /// The resulting AMOUNT is derived server-side and has no field here, same as every other
        /// figure the agreement quotes.
        /// </summary>
        public int LiabilityCapMultiple { get; set; } = 13;

        /// <summary>
        /// RETIRED FOR NEW CONTRACTS (2026-09) and no longer offered on the form, so it arrives as
        /// zero and the clause is dropped from the document.
        ///
        /// The field is KEPT rather than removed: historical contracts agreed to $35 and their
        /// frozen snapshots still carry it, and a contract being amended must be able to round-trip
        /// what it actually says. A flat fee passed to the client for a processor's own failed-debit
        /// cost is what stopped being a default, not the ability to record one.
        /// </summary>
        public decimal ReturnedPaymentFee { get; set; }

        /// <summary>
        /// Ticked when the admin edited the tax rate or price mode here and wants it to become the
        /// default for FUTURE contracts and invoices. Writes to <c>BillingSettings</c> only - it
        /// cannot reach a signed contract or a finalized invoice, both of which carry their own
        /// snapshot.
        /// </summary>
        public bool SaveAsDefault { get; set; }
    }

    /// <summary>Live echo of the derived figures while the admin is still typing.</summary>
    public class ContractPricingPreviewDto
    {
        public decimal PreTaxPrice { get; set; }
        public decimal SalesTaxAmount { get; set; }
        public decimal TotalPrice { get; set; }

        /// <summary>
        /// Cap on a short-notice cancellation charge. A percentage of the PRE-TAX fee, not of the
        /// tax-inclusive total - see <c>ContractPricingCalculator</c>.
        /// </summary>
        public decimal CancellationAmount { get; set; }
        public decimal RemainingBalance { get; set; }

        /// <summary>Cap on a failed-access charge: the pre-tax visit fee (Section 14(b)).</summary>
        public decimal LockoutFee { get; set; }

        /// <summary>Aggregate liability cap: the multiple times the pre-tax fee (Section 29(b)).</summary>
        public decimal LiabilityCapAmount { get; set; }

        /// <summary>The monthly late charge stated annually, as Section 11(f) quotes it.</summary>
        public decimal LateChargeAnnualPercent { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Admin read models
    // ══════════════════════════════════════════════════════════════════════════

    public class ContractListItemDto
    {
        public int Id { get; set; }
        public string ContractNumber { get; set; } = string.Empty;
        public string ClientLegalName { get; set; } = string.Empty;
        public string ServiceLocationLabel { get; set; } = string.Empty;
        public ContractStatus Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;
        public int CurrentVersionNumber { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? EffectiveDate { get; set; }
        public decimal TotalPrice { get; set; }
        public string? CreatedByAdminName { get; set; }
        public int SignedCount { get; set; }
        public int SignerCount { get; set; }

        /// <summary>Only ever true in the list when "Show hidden contracts" is on.</summary>
        public bool IsHidden { get; set; }

        /// <summary>
        /// Whether "Create Next Invoice" is available for this contract.
        ///
        /// Resolved on the SERVER by <c>ContractInvoiceEligibility</c> — the same rule the endpoint
        /// enforces — so the button and the authorization can never disagree. Hiding the button is
        /// the convenience; the check in <c>RecurringInvoiceService</c> is the control.
        /// </summary>
        public bool CanCreateNextInvoice { get; set; }

        /// <summary>Why not, in words an admin can act on. Null when it is available.</summary>
        public string? CannotCreateNextInvoiceReason { get; set; }
    }

    public class ContractVersionDto
    {
        public int Id { get; set; }
        public int VersionNumber { get; set; }
        public DateTime GeneratedAt { get; set; }
        public string? GeneratedByAdminName { get; set; }
        public string DocumentHashSha256 { get; set; } = string.Empty;
        public bool IsSuperseded { get; set; }
        public bool IsCurrent { get; set; }
        public List<ContractFileDto> Files { get; set; } = new();
    }

    public class ContractFileDto
    {
        public int Id { get; set; }
        public ContractFileType FileType { get; set; }
        public string FileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class ContractSignerDto
    {
        public int Id { get; set; }
        public ContractSignerRole Role { get; set; }
        public string InvitedName { get; set; } = string.Empty;
        public string? InvitedTitle { get; set; }
        public string? InvitedEmail { get; set; }
        public ContractSignerStatus Status { get; set; }
        public DateTime? InviteSentAt { get; set; }
        public DateTime TokenExpiresAt { get; set; }
        public DateTime? SignedAt { get; set; }
        public ContractSignatureMethod? SignatureMethod { get; set; }
        /// <summary>Full signing URL, so an admin can re-send or hand it over directly.</summary>
        public string? SigningUrl { get; set; }
    }

    public class ContractAuditEntryDto
    {
        public long Id { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string EventDescription { get; set; } = string.Empty;
        public ContractActorType ActorType { get; set; }
        public string? ActorIdentifier { get; set; }
        public int? ContractVersionId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ContractDetailDto
    {
        public int Id { get; set; }
        public string ContractNumber { get; set; } = string.Empty;
        public ContractStatus Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? CreatedByAdminName { get; set; }
        public string? VoidReason { get; set; }
        public int? DuplicatedFromContractId { get; set; }

        public int? CurrentVersionId { get; set; }
        public int CurrentVersionNumber { get; set; }

        /// <summary>Rendered HTML of the current version. Empty while still a Draft.</summary>
        public string DocumentHtml { get; set; } = string.Empty;
        public string DocumentHash { get; set; } = string.Empty;
        public List<string> UnresolvedTokens { get; set; } = new();

        /// <summary>
        /// The signature block for the current version, composed server-side and carrying the
        /// actual marks once signed. Sent rather than rebuilt in the browser so the admin preview
        /// shows the same executed block the client and the PDF do.
        /// </summary>
        public ContractSignatureBlockDto SignatureBlock { get; set; } = new();

        /// <summary>The editable draft the form re-opens on "Back to Edit".</summary>
        public ContractSnapshot Draft { get; set; } = new();

        /// <summary>The frozen snapshot of the current version. Null while still a Draft.</summary>
        public ContractSnapshot? CurrentSnapshot { get; set; }

        public string? ClientReviewUrl { get; set; }

        public List<ContractVersionDto> Versions { get; set; } = new();
        public List<ContractSignerDto> Signers { get; set; } = new();
        public List<ContractAuditEntryDto> AuditLog { get; set; } = new();

        // What the UI may offer, resolved server-side so the panel and the API agree.
        public bool CanEdit { get; set; }
        public bool CanGeneratePreview { get; set; }
        public bool CanSendForReview { get; set; }
        public bool CanSendForSignature { get; set; }
        /// <summary>Soft-deleted: hidden from the default list, restorable, purged after 6 months.</summary>
        public bool IsHidden { get; set; }
        public DateTime? HiddenAt { get; set; }
        public bool CanDelete { get; set; }
        public bool CanRestore { get; set; }
        public bool IsLocked { get; set; }

        /// <summary>
        /// Whether "Create Next Invoice" is available. Same server-resolved rule as the list's
        /// flag (<c>ContractInvoiceEligibility</c>) — the detail page and the list must not be able
        /// to disagree about whether a contract can be billed.
        /// </summary>
        public bool CanCreateNextInvoice { get; set; }

        /// <summary>Why not, in words an admin can act on. Null when it is available.</summary>
        public string? CannotCreateNextInvoiceReason { get; set; }

        /// <summary>
        /// True only when the signed-in account IS this contract's contractor signer and has not
        /// signed yet. Purely about identity — whether they hold the CEO/CTO authority to use it
        /// is the matrix's job, and the API re-checks both regardless of what the UI rendered.
        /// </summary>
        public bool IsPendingContractorSigner { get; set; }
    }

    public class VoidContractDto
    {
        [StringLength(500)] public string? Reason { get; set; }
    }

    public class SendForSignatureDto
    {
        /// <summary>Days the signing links stay valid. Defaults to 30 when omitted.</summary>
        public int? ExpiryDays { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Officer titles and the business flag
    // ══════════════════════════════════════════════════════════════════════════

    public class OrgTitleHolderDto
    {
        public int UserId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public OrgTitle OrgTitle { get; set; }
    }

    public class OrgTitleOverviewDto
    {
        public List<OrgTitleHolderDto> Holders { get; set; } = new();

        /// <summary>Null while no CTO exists, which is exactly when bootstrap mode is open.</summary>
        public int? CurrentCtoUserId { get; set; }

        /// <summary>True while any SuperAdmin may still grant a title to another SuperAdmin.</summary>
        public bool IsBootstrapMode { get; set; }

        /// <summary>Whether the CALLER may assign titles right now.</summary>
        public bool CanAssign { get; set; }
    }

    public class SetOrgTitleDto
    {
        public OrgTitle OrgTitle { get; set; } = OrgTitle.None;
    }

    public class SetBusinessFlagDto
    {
        public bool IsBusiness { get; set; }
    }

    /// <summary>
    /// A business-flagged customer, carrying enough of their account to PRE-FILL the contract
    /// form's client and signer fields when one is picked.
    ///
    /// Everything here is a starting point, not a binding: the admin edits any of it before
    /// generating, and what reaches the document is the contract's own snapshot. The account is
    /// never re-read at render time.
    /// </summary>
    public class BusinessCustomerDto
    {
        public int UserId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }

        // From the account's primary address. A customer may have none, in which case these are
        // empty and the admin types the address as they would for an unlinked client.
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? Zip { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Customer self-service ("My Contracts")
    // ══════════════════════════════════════════════════════════════════════════

    public class MyContractListItemDto
    {
        public int Id { get; set; }
        public string ContractNumber { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public ContractStatus Status { get; set; }
        public int VersionNumber { get; set; }

        /// <summary>When the version they are looking at was produced.</summary>
        public DateTime VersionDate { get; set; }

        public string ServiceLocationLabel { get; set; } = string.Empty;

        /// <summary>True when this customer's signature is the one being waited on.</summary>
        public bool AwaitingYourSignature { get; set; }

        public bool IsExecuted { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Client-facing (token addressed, no login)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The "Your Information" block on the client review page.</summary>
    public class ClientReviewInfoDto
    {
        [Required, StringLength(200)] public string CompanyLegalName { get; set; } = string.Empty;
        [Required, StringLength(100)] public string FirstName { get; set; } = string.Empty;
        [Required, StringLength(100)] public string LastName { get; set; } = string.Empty;
        [StringLength(120)] public string? Title { get; set; }
        [StringLength(255)] public string? Email { get; set; }
        [StringLength(50)] public string? Phone { get; set; }
        [Required, StringLength(300)] public string CompanyAddress { get; set; } = string.Empty;
        [StringLength(100)] public string City { get; set; } = string.Empty;
        [StringLength(50)] public string State { get; set; } = string.Empty;
        [StringLength(20)] public string Zip { get; set; } = string.Empty;
    }

    public class ContractReviewPageDto
    {
        public string ContractNumber { get; set; } = string.Empty;
        public string DocumentTitle { get; set; } = "Master Service Agreement";
        public string ContractorDisplayName { get; set; } = string.Empty;
        public string ClientLegalName { get; set; } = string.Empty;
        public int VersionNumber { get; set; }
        public ContractStatus Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;

        public string DocumentHtml { get; set; } = string.Empty;
        public ContractSignatureBlockDto SignatureBlock { get; set; } = new();
        public ClientReviewInfoDto YourInformation { get; set; } = new();

        /// <summary>False once the contract has moved past review (revision pending, signed, void).</summary>
        public bool CanEdit { get; set; }
        /// <summary>True when a signing link exists for the client on the CURRENT version.</summary>
        public bool CanContinueToSignature { get; set; }
        public string? SigningToken { get; set; }
        /// <summary>Set when a client edit created a revision that an admin has still to approve.</summary>
        public string? Message { get; set; }
    }

    /// <summary>What the review/signing pages draw where the document says "signature block".</summary>
    public class ContractSignatureBlockDto
    {
        public ContractSignaturePartyDto Contractor { get; set; } = new();
        public ContractSignaturePartyDto Client { get; set; } = new();
    }

    public class ContractSignaturePartyDto
    {
        public string PartyLabel { get; set; } = string.Empty;
        public string EntityName { get; set; } = string.Empty;
        public string SignerName { get; set; } = string.Empty;
        public string? SignerTitle { get; set; }
        public bool HasSigned { get; set; }
        public DateTime? SignedAt { get; set; }
        public ContractSignatureMethod? Method { get; set; }
        /// <summary>Drawn mark as a data URI, or the typed name. Empty until signed.</summary>
        public string SignatureMark { get; set; } = string.Empty;
    }

    public class ClientEditResultDto
    {
        /// <summary>True when the edit was a contract MODIFICATION and produced a new version.</summary>
        public bool CreatedRevision { get; set; }
        public int VersionNumber { get; set; }
        public ContractStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        /// <summary>Fields that triggered the revision, so the page can say what changed.</summary>
        public List<string> ChangedFields { get; set; } = new();
    }

    public class ContractSigningPageDto
    {
        public string ContractNumber { get; set; } = string.Empty;
        public string DocumentTitle { get; set; } = "Master Service Agreement";
        public int VersionNumber { get; set; }
        public string DocumentHtml { get; set; } = string.Empty;
        public ContractSignatureBlockDto SignatureBlock { get; set; } = new();

        public ContractSignerRole Role { get; set; }
        public string PartyEntityName { get; set; } = string.Empty;
        public string SignerName { get; set; } = string.Empty;
        public string? SignerTitle { get; set; }
        public string? SignerEmail { get; set; }

        /// <summary>Contractor name/title come from the contractor profile and are locked.</summary>
        public bool NameLocked { get; set; }
        public bool TitleLocked { get; set; }

        public bool AlreadySigned { get; set; }
        public DateTime? SignedAt { get; set; }
        public bool Expired { get; set; }
        public bool Superseded { get; set; }
        public string ConsentText { get; set; } = string.Empty;
        public string? Message { get; set; }
    }

    public class SignContractDto
    {
        [Required, StringLength(200)] public string SignerName { get; set; } = string.Empty;
        [StringLength(120)] public string? SignerTitle { get; set; }
        [StringLength(255)] public string? SignerEmail { get; set; }

        public ContractSignatureMethod SignatureMethod { get; set; } = ContractSignatureMethod.Type;

        /// <summary>PNG data URI for Draw, the typed name for Type.</summary>
        [Required] public string SignatureData { get; set; } = string.Empty;

        public bool ConsentAccepted { get; set; }
    }

    public class SignContractResultDto
    {
        public ContractStatus Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;
        public bool FullyExecuted { get; set; }
        public DateTime SignedAt { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
