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

        /// <summary>Exhibit A's recorded site facts - restroom counts, floor materials, glass locations.</summary>
        public SiteDetailsSnapshot SiteDetails { get; set; } = new();

        /// <summary>Exhibit B4 - approval, notice and on-call contacts for both Parties.</summary>
        public OperationalContactsSnapshot Contacts { get; set; } = new();

        /// <summary>Exhibit B3 - endorsements agreed beyond the Section 20 baseline.</summary>
        public InsuranceEndorsementsSnapshot Insurance { get; set; } = new();

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

        /// <summary>
        /// LEGACY single start time. The v2.0 agreement quotes an arrival WINDOW instead
        /// (Section 5(c), "8:30 AM to 9:30 AM"), because a crew that is told one minute is late
        /// the moment traffic moves and a failed-access charge hangs on whether Contractor
        /// arrived inside the agreed window at all.
        ///
        /// Kept because a contract drafted before the window existed carries its time here and
        /// nowhere else; <see cref="ResolveArrivalWindow"/> is the one place the two are
        /// reconciled, and nothing reads either field directly.
        /// </summary>
        public string ServiceTime { get; set; } = "9:00 AM";

        /// <summary>Opening of the agreed arrival window - Section 5(c) and Exhibit A/B.</summary>
        public string ArrivalWindowStart { get; set; } = "8:30 AM";

        /// <summary>
        /// Close of the agreed arrival window. Blank collapses the window back to a single start
        /// time rather than rendering a dangling "8:30 AM to".
        /// </summary>
        public string ArrivalWindowEnd { get; set; } = "9:30 AM";

        /// <summary>
        /// The clock every time in the document refers to. Stated because Section 32 makes the
        /// twenty-four-hour cancellation calculation turn on it, and "9:30 AM" with no zone is
        /// ambiguous the first time a client emails from another one.
        /// </summary>
        public string TimeZoneLabel { get; set; } = "local New York time";

        /// <summary>
        /// Section 5(c) / Exhibit A: a completion deadline, where the Parties agreed one. Blank
        /// renders "None" - an unanswered completion time and an agreed absence of one are
        /// different facts, and the draft asks for the second explicitly.
        /// </summary>
        public string? CompletionTime { get; set; }

        /// <summary>
        /// What a "calendar week" means for the weekly commitment (Section 5(a)). Stored rather
        /// than assumed because a makeup visit is assigned to the week its original visit fell in,
        /// so where the week boundary sits decides whether a Monday makeup settles last week.
        /// </summary>
        public string WeekDefinition { get; set; } = "Monday through Sunday";

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

        /// <summary>
        /// The arrival window as the agreement states it: "8:30 AM to 9:30 AM", or a bare start
        /// time when no end was given.
        ///
        /// A METHOD, not a property, for the same reason <see cref="ResolveServiceDays"/> is: a
        /// getter would be serialised into every frozen snapshot, leaving a third copy of a fact
        /// already stored twice.
        ///
        /// Falls back to <see cref="ServiceTime"/> when neither window bound was recorded, so a
        /// contract drafted before the window existed keeps rendering the time it was drafted
        /// with instead of an empty phrase in the middle of Section 5(c).
        /// </summary>
        public string ResolveArrivalWindow()
        {
            var start = (ArrivalWindowStart ?? string.Empty).Trim();
            var end = (ArrivalWindowEnd ?? string.Empty).Trim();

            if (start.Length == 0 && end.Length == 0) return (ServiceTime ?? string.Empty).Trim();
            if (start.Length == 0) return end;
            if (end.Length == 0 || string.Equals(start, end, StringComparison.OrdinalIgnoreCase)) return start;
            return $"{start} to {end}";
        }
    }

    /// <summary>
    /// The site facts Exhibit A asks the Parties to record before the first recurring visit -
    /// restroom counts, floor materials, which glass is included, where the waste goes.
    ///
    /// These are the "[COUNTS]" / "[LOCATIONS]" blanks of the drafted agreement. They are stored
    /// as free text rather than as structured counts on purpose: they describe a building, and
    /// every attempt to enumerate what a commercial kitchen contains produces a form that cannot
    /// express the next one. What matters legally is that the description in the executed document
    /// is the one both sides agreed to, which the frozen snapshot guarantees whatever its shape.
    ///
    /// Absent from any snapshot written before this existed, where every field deserialises to
    /// null and renders as a ruled blank - a visible unanswered question rather than a silent
    /// assertion that the premises has no employee restroom.
    /// </summary>
    public class SiteDetailsSnapshot
    {
        /// <summary>e.g. "5,000 square feet". Free text - "approximately" is the point.</summary>
        public string? ApproximateSquareFootage { get; set; }

        public string? CustomerRestroomCounts { get; set; }
        public string? EmployeeRestroomCounts { get; set; }
        public string? FloorMaterials { get; set; }
        public string? KitchenEquipmentAndSurfaces { get; set; }
        public string? TouchpointLocations { get; set; }
        public string? InteriorGlassLocations { get; set; }

        // NO SOAP FIELDS. Hand soap and its dispensers are outside the Services entirely -
        // Contractor never supplies, replenishes, repairs or replaces them (Exhibit A, A8(a)), so
        // there is nothing about them for the Parties to record.

        /// <summary>
        /// Exhibit A: any included food-contact or dining-table sanitizing task, its surface,
        /// frequency and the required wash/rinse/sanitize procedure.
        ///
        /// Blank means NONE, and that is load-bearing rather than a formatting nicety: Section
        /// 25(e) and A3(d) exclude food-contact sanitizing "except a specifically identified task
        /// expressly included in Exhibit A", so an unfilled box is the agreement saying Client
        /// keeps that responsibility.
        /// </summary>
        public string? FoodContactSanitizing { get; set; }

        public string? AccessMethodReference { get; set; }
        public string? EquipmentRestrictions { get; set; }
        public string? WasteReceptacleLocations { get; set; }

        /// <summary>Legal name of the food-service permit holder - not necessarily the Client.</summary>
        public string? FoodServicePermitHolder { get; set; }

        /// <summary>Landlord, franchisor or brand requirements affecting access, products or insurance.</summary>
        public string? SiteRequirements { get; set; }

        public string? BaselineWalkthroughRecord { get; set; }
        public string? InitialWorkChangeOrder { get; set; }
    }

    /// <summary>
    /// Exhibit B4 - who may approve a Change Order and who answers the phone on a Sunday morning.
    ///
    /// Deliberately separate from the SIGNER snapshots. A signer is the person who executes the
    /// agreement; an operational contact is whoever the crew calls when the door is locked, and on
    /// most commercial accounts those are different people. Section 14 hangs a failed-access charge
    /// on Contractor having attempted to reach the on-call contact, so the document has to name
    /// one.
    ///
    /// Every field is optional and renders as a ruled blank when unset, except the approval
    /// emails, which the drafted agreement marks optional outright and which render "None".
    /// </summary>
    public class OperationalContactsSnapshot
    {
        public string? ContractorApprovalEmail { get; set; }
        public string? ContractorOperationalEmail { get; set; }
        public string? ContractorSupervisorName { get; set; }
        public string? ContractorSupervisorPhone { get; set; }
        public string? ContractorBackupContact { get; set; }

        public string? ClientApprovalEmail { get; set; }

        /// <summary>
        /// Where FORMAL notice is served on Client. Kept apart from the client's principal
        /// address and from the service location: Section 32 serves breach and termination
        /// notices here, and a business that is registered at an accountant's office, served at a
        /// restaurant and reads its mail at a third address is the ordinary case, not the corner
        /// one. Blank falls back to the client's principal address.
        /// </summary>
        public string? ClientNoticeMailingAddress { get; set; }

        public string? ClientOperationalEmail { get; set; }
        public string? ClientOnCallName { get; set; }
        public string? ClientOnCallPhone { get; set; }
        public string? ClientBackupContact { get; set; }
    }

    /// <summary>
    /// Exhibit B3 - endorsements agreed beyond the baseline coverage in Section 20.
    ///
    /// Three separate fields rather than one note, because Section 20(c) says a certificate alone
    /// does not amend a policy: naming the endorsement, identifying the actual insurer/form/edition
    /// that issues it, and agreeing who pays for it are three different commitments, and a client
    /// who is promised the first without the second has been promised nothing.
    /// </summary>
    public class InsuranceEndorsementsSnapshot
    {
        /// <summary>Blank renders "None" - the drafted agreement asks for "[ENDORSEMENTS OR NONE]".</summary>
        public string? AgreedEndorsements { get; set; }

        /// <summary>Insurer, policy, endorsement form and edition, protected entity, applicable work.</summary>
        public string? EndorsementDetails { get; set; }

        /// <summary>Agreed additional premium or price adjustment. Blank renders "None".</summary>
        public string? AdditionalPremium { get; set; }
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

        /// <summary>
        /// The first RECURRING service date. Distinct from the Effective Date, and Section 3 hangs
        /// the whole term on it: the Initial Term and the Minimum Commitment Period both run from
        /// here, not from signature. An agreement signed in March for a May start commits six
        /// months of cleaning, not four.
        ///
        /// Null leaves Exhibit B's date row a ruled blank rather than guessing the effective date,
        /// because guessing would silently shorten the commitment the client is being asked to make.
        /// </summary>
        public DateTime? ServiceCommencementDate { get; set; }

        /// <summary>
        /// End of the Minimum Commitment Period - the first date a termination for convenience may
        /// take effect. Derived from <see cref="ServiceCommencementDate"/> plus
        /// <see cref="MinimumCommitmentMonths"/>, never stored: two dates that are supposed to be
        /// the same arithmetic eventually disagree, and this one is quoted in Section 3(b), Section
        /// 4(a) and Exhibit B1.
        /// </summary>
        public DateTime? ResolveMinimumCommitmentEndDate() =>
            ServiceCommencementDate?.AddMonths(Math.Max(0, MinimumCommitmentMonths));

        /// <summary>
        /// Last day of the Initial Term: the day immediately preceding the commencement date's
        /// N-month anniversary, which is what Exhibit B1's wording describes.
        /// </summary>
        public DateTime? ResolveInitialTermEndDate() =>
            ServiceCommencementDate?.AddMonths(Math.Max(0, InitialTermMonths)).AddDays(-1);

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
        /// <summary>
        /// Cap on a short-notice cancellation charge, as a percentage of the PRE-TAX fee.
        ///
        /// The basis changed with the v2.0 agreement (Section 15(b), Exhibit B2(d)): it is
        /// "fifty percent of the pre-tax visit fee", not of the tax-inclusive total. Sales tax is
        /// charged on a taxable supply, and a cancelled visit is not one - so building the cap on
        /// the tax-inclusive figure would quietly bill the client half a tax that was never owed.
        /// See <c>ContractPricingCalculator</c>.
        /// </summary>
        public decimal CancellationPercent { get; set; } = 50m;
        public decimal CancellationAmount { get; set; }
        public decimal RemainingBalance { get; set; }

        /// <summary>
        /// Section 14(b): a failed-access charge is capped at that visit's PRE-TAX service fee,
        /// plus any tax legally applicable to the charge. The pre-tax figure is what the agreement
        /// quotes, so this holds the pre-tax figure - it is a CAP on documented net loss, not an
        /// automatic charge, which is why it is never the tax-inclusive total.
        /// </summary>
        public decimal LockoutFee { get; set; }

        /// <summary>
        /// Section 29(b): the aggregate liability cap is a MULTIPLE of the recurring pre-tax
        /// per-visit fee - thirteen in the drafted agreement.
        ///
        /// It replaced a lookback in months, and the difference matters: a months-based cap moves
        /// every time the visit frequency or the billing cadence changes, so the ceiling a client
        /// agreed to would silently drift. A multiple of a stated per-visit fee is a number both
        /// sides can compute from the face of the document.
        /// </summary>
        public int LiabilityCapMultiple { get; set; } = 13;

        /// <summary>Server-derived: <see cref="LiabilityCapMultiple"/> x <see cref="PreTaxPrice"/>.</summary>
        public decimal LiabilityCapAmount { get; set; }

        public string InvoiceTiming { get; set; } =
            "In advance of each scheduled service visit, generally several days before service.";
        public int PaymentDeadlineHours { get; set; } = 48;
        public string PaymentMethod { get; set; } = "ACH or bank transfer using verified instructions";

        /// <summary>
        /// Simple monthly interest on an overdue undisputed amount. One percent in the v2.0
        /// agreement (Section 11(f)), down from 1.5%.
        /// </summary>
        public decimal LateChargePercent { get; set; } = 1m;

        /// <summary>
        /// The same rate stated annually, because Section 11(f) quotes both - "one percent per
        /// month, calculated daily at twelve percent per year".
        ///
        /// DERIVED, never typed: quoting two rates that are supposed to describe one charge is how
        /// a document ends up contradicting itself, and a usury argument turns on exactly that
        /// figure.
        /// </summary>
        public decimal LateChargeAnnualPercent { get; set; }

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
        // ── Scheduling, cancellation and makeup ────────────────────────────────
        public int TimelyRescheduleHours { get; set; } = 24;

        /// <summary>
        /// Section 14(c) / 15: the window inside which a missed visit may be made up before the
        /// charge for it becomes final. One number, used by every clause that talks about a
        /// makeup, so the failed-access path and the cancellation path can never offer the client
        /// two different deadlines.
        /// </summary>
        public int MakeupWindowDays { get; set; } = 14;

        /// <summary>Section 14(a): how long a crew waits at a locked door before it is failed access.</summary>
        public int LockoutWaitMinutes { get; set; } = 20;

        /// <summary>
        /// Section 15(f): Client-attributable missed visits, in a rolling window of
        /// <see cref="MissedVisitWindowWeeks"/> weeks, that may establish a material failure to
        /// maintain the agreed frequency.
        /// </summary>
        public int MissedVisitThreshold { get; set; } = 3;
        public int MissedVisitWindowWeeks { get; set; } = 8;

        /// <summary>Section 15(f): days Client has to supply a workable service plan after warning.</summary>
        public int ServicePlanDays { get; set; } = 7;

        // ── Termination and cure ───────────────────────────────────────────────
        public int CurePeriodDays { get; set; } = 15;

        /// <summary>Section 4(c): days an undisputed amount may stand after written demand.</summary>
        public int PastDueDays { get; set; } = 15;

        /// <summary>Section 4(d) / 15: window to return unearned prepayments and unapplied credits.</summary>
        public int CreditReturnDays { get; set; } = 30;

        /// <summary>Section 30(b): consecutive days of prevented performance that permit termination.</summary>
        public int ForceMajeureDays { get; set; } = 30;

        // ── Invoicing and money ────────────────────────────────────────────────
        /// <summary>Section 11(a): how far ahead of a visit the invoice is ordinarily issued.</summary>
        public int InvoiceLeadDays { get; set; } = 7;

        /// <summary>
        /// Section 11(a): an invoice delivered fewer than this many days before the payment
        /// deadline is a LATE invoice, and buys Client
        /// <see cref="LateInvoiceGraceBusinessDays"/> business days from receipt instead.
        ///
        /// The pair exists so a late invoice cannot manufacture a cancellation charge: without it,
        /// Contractor could invoice inside the payment window and then charge for the visit the
        /// client had no chance to pay for.
        /// </summary>
        public int LateInvoiceThresholdDays { get; set; } = 5;
        public int LateInvoiceGraceBusinessDays { get; set; } = 3;

        /// <summary>Section 11(f): days an earned undisputed amount may stand before interest runs.</summary>
        public int InterestGraceDays { get; set; } = 5;

        // ── Disputes, damage and quality ───────────────────────────────────────
        /// <summary>Section 12: business days to raise a dispute apparent from the invoice.</summary>
        public int BillingDisputeDays { get; set; } = 10;

        /// <summary>Section 12: business days the Parties have to exchange information and resolve.</summary>
        public int DisputeResponseBusinessDays { get; set; } = 10;

        /// <summary>Section 12: business days to pay an amount determined payable after resolution.</summary>
        public int ResolutionPaymentBusinessDays { get; set; } = 5;

        /// <summary>Section 21: business days after discovery to notify alleged damage.</summary>
        public int DamageNoticeBusinessDays { get; set; } = 5;

        /// <summary>Section 22(a): hours to identify a material failure to complete an included task.</summary>
        public int QualityComplaintHours { get; set; } = 48;

        /// <summary>Section 22(b): business days Contractor has to re-perform a deficient task.</summary>
        public int QualityCorrectionBusinessDays { get; set; } = 2;

        /// <summary>Section 15(e): business days to issue a refund Client has requested.</summary>
        public int RefundBusinessDays { get; set; } = 10;

        // ── Access ─────────────────────────────────────────────────────────────
        /// <summary>Section 13(b): business days to return keys and relinquish credentials.</summary>
        public int KeyReturnBusinessDays { get; set; } = 2;

        // ── Confidentiality ────────────────────────────────────────────────────
        public int ConfidentialityYears { get; set; } = 2;

        // ── Insurance ──────────────────────────────────────────────────────────
        public decimal InsurancePerOccurrence { get; set; } = 1000000m;
        public decimal InsuranceAggregate { get; set; } = 2000000m;

        /// <summary>Jurisdiction whose workers' comp / disability rules Section 20(b) names.</summary>
        public string InsuranceJurisdiction { get; set; } = "New York";

        // ── Compliance ─────────────────────────────────────────────────────────
        /// <summary>
        /// The layers of law Section 26(a) names - "federal, New York State, and New York City".
        ///
        /// Stored as a phrase rather than composed from the governing-law state, because the third
        /// layer is a CITY and no rule derives it: a Brooklyn premises is governed by New York City
        /// law, a Yonkers one is not, and the service location's city field says "Brooklyn" either
        /// way. A phrase an admin can read and correct beats a derivation that is quietly wrong.
        /// </summary>
        public string ComplianceJurisdictions { get; set; } = "federal, New York State, and New York City";

        // ── Pricing review ─────────────────────────────────────────────────────
        /// <summary>Section 8(c): notice Contractor must give to request a prospective price review.</summary>
        public int PriceReviewNoticeDays { get; set; } = 45;

        // ── Dispute resolution ─────────────────────────────────────────────────
        /// <summary>Section 34(a): days for senior representatives to confer after a dispute notice.</summary>
        public int DisputeDiscussionDays { get; set; } = 10;

        /// <summary>Section 34(a): days after which either Party may request nonbinding mediation.</summary>
        public int MediationRequestDays { get; set; } = 15;

        /// <summary>Section 34(a): days to select a mediator once mediation is requested.</summary>
        public int MediatorSelectionDays { get; set; } = 10;

        /// <summary>Section 34(a): days after the original dispute notice before suit may be filed.</summary>
        public int SuitAfterDays { get; set; } = 30;

        /// <summary>Section 34(c): business days to pay after demand before collection may begin.</summary>
        public int CollectionDemandBusinessDays { get; set; } = 5;

        /// <summary>Where Section 34(a) mediation sits. Usually the same as the venue county.</summary>
        public string MediationVenue { get; set; } = "Kings County, New York";

        /// <summary>
        /// The federal court Section 33 names, for the case where federal subject-matter
        /// jurisdiction independently exists. Stored rather than hardcoded because it follows the
        /// venue county, and the two moving apart is a venue clause that names a court with no
        /// jurisdiction over the parties.
        /// </summary>
        public string FederalVenue { get; set; } =
            "the United States District Court for the Eastern District of New York sitting in Brooklyn";
    }
}
