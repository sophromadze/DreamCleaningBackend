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

        /// <summary>
        /// How often this contract is invoiced. Deliberately its own block beside Schedule rather
        /// than a field on it: "when do we clean" and "how often do we bill" are separate
        /// arrangements, and every previous attempt to answer both from one setting produced
        /// invoices whose service period nobody could justify.
        /// </summary>
        public BillingCadenceSnapshot Billing { get; set; } = new();

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

        /// <summary>
        /// LEGACY, and kept forever. Before multiple service days existed (2026-09) this single
        /// column WAS the schedule, so every contract signed until then has its regular day here
        /// and nowhere else. It is still written for a one-day schedule so an older reader, an
        /// export or a partially-deployed instance keeps seeing something sensible.
        ///
        /// Read through <see cref="ResolveServiceDays"/>, never directly - that is the one place
        /// the old single value and the new list are reconciled.
        /// </summary>
        public string ServiceDay { get; set; } = "Sunday";

        /// <summary>
        /// The regular service weekdays, e.g. Monday / Wednesday / Friday for three visits a week.
        ///
        /// EMPTY ON EVERY PRE-2026-09 SNAPSHOT, which is exactly why nothing reads it directly:
        /// deserialising an older version yields an empty list, and
        /// <see cref="ResolveServiceDays"/> falls back to <see cref="ServiceDay"/> so a signed
        /// contract renders identically to the day it was signed.
        /// </summary>
        public List<string> ServiceDays { get; set; } = new();

        public string ServiceTime { get; set; } = "9:00 AM";

        /// <summary>False pins the day/time; true keeps the "not permanently fixed" language.</summary>
        public bool FlexibleScheduling { get; set; } = true;

        public bool PerformedWhileClosed { get; set; } = true;

        /// <summary>e.g. "key or access credentials provided by Client".</summary>
        public string AccessType { get; set; } = "key or other access credentials provided by Client";

        /// <summary>
        /// The regular service weekdays as .NET days, resolved from the list when a contract has
        /// one and from the legacy single day otherwise.
        ///
        /// A METHOD RATHER THAN A PROPERTY, deliberately: a getter-only property would be
        /// serialised into every frozen snapshot, putting a third copy of the same fact beside the
        /// two stored fields - which is how the three would eventually disagree.
        /// </summary>
        public List<DayOfWeek> ResolveServiceDays()
        {
            var names = ServiceDays != null && ServiceDays.Count > 0
                ? ServiceDays
                : new List<string> { ServiceDay };

            // Monday-first, deduplicated. A day picker hands them back in click order, and
            // "Friday, Monday and Wednesday" printed in an agreement reads as a mistake.
            return names
                .Select(Helpers.Commercial.ServiceScheduleCalculator.ParseWeekday)
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .Distinct()
                .OrderBy(d => d == DayOfWeek.Sunday ? 6 : (int)d - 1)
                .ToList();
        }

        /// <summary>The same, as display names - what the agreement and the admin panel print.</summary>
        public List<string> ResolveServiceDayNames() =>
            ResolveServiceDays().Select(d => d.ToString()).ToList();
    }

    /// <summary>
    /// How often the contract is INVOICED. Separate from <see cref="ScheduleSnapshot"/> because
    /// "when do we clean" and "how often do we bill" are different questions with different
    /// answers - see <c>ContractBillingFrequency</c>.
    ///
    /// Absent from every pre-2026-09 snapshot, where deserialisation yields the defaults below.
    /// Monthly-with-interval-one is the arrangement almost every existing commercial client is on,
    /// so an old contract reads correctly without being touched.
    /// </summary>
    public class BillingCadenceSnapshot
    {
        public ContractBillingFrequency Frequency { get; set; } = ContractBillingFrequency.Monthly;

        /// <summary>N in "every N weeks" / "every N months" / "every N days". Minimum 1.</summary>
        public int IntervalCount { get; set; } = 1;

        /// <summary>
        /// Where the first billing period starts when no invoice has been raised yet. Null falls
        /// back to the contract's effective date, which is what it means in practice.
        /// </summary>
        public DateTime? AnchorDate { get; set; }
    }

    public class TermSnapshot
    {
        // Defaults changed 2026-09 to the commercial terms actually being offered: committed for
        // six months, then month-to-month with sixty days notice. They apply to NEW drafts only -
        // every generated version carries its own frozen copy, so nothing already signed moves.
        public int InitialTermMonths { get; set; } = 6;
        public int MinimumCommitmentMonths { get; set; } = 6;
        public int TerminationNoticeDays { get; set; } = 60;

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
        /// <summary>
        /// Tax-inclusive by default since 2026-09: the amount a commercial client agrees to is the
        /// amount they pay, and quoting pre-tax then adding 8.875% on the invoice is not what was
        /// discussed. New drafts only - a generated version keeps whatever mode it froze.
        /// </summary>
        public ContractPriceMode PriceMode { get; set; } = ContractPriceMode.TaxInclusive;

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

        /// <summary>
        /// The returned/failed payment fee. DEFAULTS TO ZERO SINCE 2026-09, and the field is no
        /// longer offered on the contract form.
        ///
        /// The column and the token stay because HISTORICAL CONTRACTS AGREED TO $35 and must keep
        /// rendering exactly what was signed - a frozen version carries its own copy, so an
        /// executed agreement is unaffected by this default changing. What went away is charging a
        /// new client a flat fee for a processor's own failed-debit cost.
        ///
        /// At zero the clause is dropped from the document rather than printed as "$0.00" - see
        /// <c>ContractPlaceholders</c> and the OMIT sentinel in <c>ContractRenderer</c>.
        /// </summary>
        public decimal ReturnedPaymentFee { get; set; } = 0m;
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
