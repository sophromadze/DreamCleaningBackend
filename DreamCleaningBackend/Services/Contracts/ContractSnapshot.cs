using System.Text.Json;
using DreamCleaningBackend.Models.Contracts;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// The COMPLETE frozen record a contract version renders from. Nothing in here is an id
    /// pointing at a live row: a version rendered today must read the same in five years even if
    /// the client renames itself, moves, or the price list changes. Ids are kept alongside the
    /// copied values purely so the admin UI can link back - the RENDERER never follows them.
    ///
    /// A Draft may be re-populated from live rows; the moment a version is generated the
    /// snapshot is copied and becomes read-only.
    /// </summary>
    public class ContractSnapshot
    {
        // ── Identity ───────────────────────────────────────────────────────────
        public string ContractNumber { get; set; } = string.Empty;
        public int VersionNumber { get; set; } = 1;

        /// <summary>Rendered into the preamble and the certificate. Null until the admin sets it.</summary>
        public DateTime? EffectiveDate { get; set; }

        // ── Template ───────────────────────────────────────────────────────────
        public int ContractTemplateId { get; set; }
        public string ContractTemplateName { get; set; } = string.Empty;
        public string ContractTemplateVersion { get; set; } = string.Empty;

        /// <summary>
        /// The template body AS IT WAS when this version was generated. Copied, not referenced:
        /// a later SuperAdmin edit of the master template must not rewrite a signed document.
        /// </summary>
        public string TemplateBodyText { get; set; } = string.Empty;

        public int? ScopeTemplateId { get; set; }
        public string ScopeTemplateName { get; set; } = string.Empty;

        // ── Parties ────────────────────────────────────────────────────────────
        public ContractorSnapshot Contractor { get; set; } = new();
        public ClientSnapshot Client { get; set; } = new();
        public ServiceLocationSnapshot ServiceLocation { get; set; } = new();
        public SignerSnapshot ContractorSigner { get; set; } = new();
        public SignerSnapshot ClientSigner { get; set; } = new();

        // ── Body configuration ─────────────────────────────────────────────────
        public ScheduleSnapshot Schedule { get; set; } = new();
        public TermSnapshot Term { get; set; } = new();
        public PricingSnapshot Pricing { get; set; } = new();
        public AdvancedTermsSnapshot Advanced { get; set; } = new();
        public ScopeStructure Scope { get; set; } = new();

        /// <summary>
        /// The noun the document uses for the premises ("restaurant", "office"). Seeded from the
        /// scope template, editable per contract - Section 1(b) and 5(f) read from it.
        /// </summary>
        public string PremisesType { get; set; } = "premises";

        public static ContractSnapshot Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new ContractSnapshot();
            try
            {
                return JsonSerializer.Deserialize<ContractSnapshot>(json, ContractJson.Options)
                       ?? new ContractSnapshot();
            }
            catch (JsonException)
            {
                return new ContractSnapshot();
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, ContractJson.Options);

        /// <summary>Deep copy. Used whenever a draft becomes a version, or a version seeds a revision.</summary>
        public ContractSnapshot Clone() => Parse(ToJson());
    }

    public class ContractorSnapshot
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
    }

    public class ClientSnapshot
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

        /// <summary>
        /// The business-flagged customer account this client belongs to, frozen at generation.
        /// The live <c>ContractClient.SourceUserId</c> is what the My Contracts portal filters on;
        /// this copy exists so a rendered version records who it was linked to at the time.
        /// </summary>
        public int? SourceUserId { get; set; }
    }

    public class ServiceLocationSnapshot
    {
        public int Id { get; set; }
        public string? BusinessBrand { get; set; }
        public string? LocationName { get; set; }
        public string Address { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Zip { get; set; } = string.Empty;
    }

    public class SignerSnapshot
    {
        public int? ContactId { get; set; }

        /// <summary>
        /// The account entitled to sign this side from an authenticated session, frozen with the
        /// rest of the snapshot. Copied onto the signer row when signing opens; null means this
        /// side signs by emailed link only, which is the normal case for a client.
        /// </summary>
        public int? UserId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }

        public string FullName =>
            string.Join(" ", new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
    }

    public class ScheduleSnapshot
    {
        /// <summary>Rendered as the {{SERVICE_FREQUENCY_TEXT}} sentence fragment.</summary>
        public string FrequencyUnit { get; set; } = "calendar week";
        public int VisitsPerPeriod { get; set; } = 1;

        public string ServiceDay { get; set; } = "Sunday";
        public string ServiceTime { get; set; } = "9:00 AM";

        /// <summary>False pins the day/time; true keeps the "not permanently fixed" language.</summary>
        public bool FlexibleScheduling { get; set; } = true;

        public bool PerformedWhileClosed { get; set; } = true;

        /// <summary>e.g. "key or access credentials provided by Client".</summary>
        public string AccessType { get; set; } = "key or other access credentials provided by Client";
    }

    public class TermSnapshot
    {
        public int InitialTermMonths { get; set; } = 12;
        public int MinimumCommitmentMonths { get; set; } = 3;
        public int TerminationNoticeDays { get; set; } = 30;

        /// <summary>Phrase dropped into Section 3(d), e.g. "month-to-month".</summary>
        public string RenewalType { get; set; } = "month-to-month";

        public string GoverningLawState { get; set; } = "New York";
        public string VenueCounty { get; set; } = "Kings County";
    }

    /// <summary>
    /// What the admin typed (mode / amount / rate) plus what the SERVER derived from it. The
    /// derived figures are stored so the document and the snapshot can never disagree, but they
    /// are always recomputed on save - a client-submitted total is never trusted.
    /// </summary>
    public class PricingSnapshot
    {
        public ContractPriceMode PriceMode { get; set; } = ContractPriceMode.PreTax;

        /// <summary>The single amount the admin typed. Meaning depends on <see cref="PriceMode"/>.</summary>
        public decimal PriceInput { get; set; }

        public decimal SalesTaxRatePercent { get; set; } = 8.875m;

        // Server-derived - see ContractPricingCalculator.
        public decimal PreTaxPrice { get; set; }
        public decimal SalesTaxAmount { get; set; }
        public decimal TotalPrice { get; set; }
        public decimal CancellationPercent { get; set; } = 50m;
        public decimal CancellationAmount { get; set; }
        public decimal RemainingBalance { get; set; }
        public decimal LockoutFee { get; set; }

        public string InvoiceTiming { get; set; } =
            "In advance of each scheduled service visit, generally several days before service.";
        public int PaymentDeadlineHours { get; set; } = 48;
        public string PaymentMethod { get; set; } = "ACH or bank-to-bank transfer";
        public decimal LateChargePercent { get; set; } = 1.5m;
        public decimal ReturnedPaymentFee { get; set; } = 35m;
    }

    /// <summary>
    /// Every other fixed number the reference agreement carried. Collapsed by default on the
    /// form; each field defaults to the value in the reference contract so an admin who never
    /// opens the panel produces exactly the reference wording.
    /// </summary>
    public class AdvancedTermsSnapshot
    {
        public int TimelyRescheduleHours { get; set; } = 24;
        public int CurePeriodDays { get; set; } = 15;
        public int PastDueDays { get; set; } = 30;
        public int BillingDisputeDays { get; set; } = 10;
        public int QualityComplaintHours { get; set; } = 24;
        public int VisibleDamageHours { get; set; } = 48;
        public int LatentDamageDays { get; set; } = 30;
        public int ConfidentialityYears { get; set; } = 2;
        public int NonSolicitMonths { get; set; } = 12;
        public decimal NonHireDamages { get; set; } = 5000m;
        public decimal InsurancePerOccurrence { get; set; } = 1000000m;
        public decimal InsuranceAggregate { get; set; } = 2000000m;

        /// <summary>Jurisdiction whose workers' comp / disability rules Section 20(b) names.</summary>
        public string InsuranceJurisdiction { get; set; } = "New York";

        public int LiabilityCapLookbackMonths { get; set; } = 3;
        public int DisputeDiscussionDays { get; set; } = 30;

        /// <summary>Section 15(b-1)/(d): window to return an unapplied credit after the end.</summary>
        public int CreditReturnDays { get; set; } = 30;

        /// <summary>Where Section 34(a) mediation sits. Usually the same as the venue county.</summary>
        public string MediationVenue { get; set; } = "Kings County, New York";
    }
}
