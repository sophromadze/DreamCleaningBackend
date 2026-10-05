using DreamCleaningBackend.Models.Contracts;
using System.Text;
using DreamCleaningBackend.Helpers.Contracts;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// Turns a frozen <see cref="ContractSnapshot"/> into the {{TOKEN}} -> text map the template
    /// body is rendered against. This is the single definition of what every placeholder means.
    ///
    /// Two token families exist:
    ///   {{NAME}}        - a scalar from the snapshot, already formatted for legal prose
    ///                     (counts come back as "twelve (12)", money as "$925.43").
    ///   {{SCOPE:key}}   - the selected items of one scope group, resolved by
    ///                     <see cref="ContractRenderer"/> because it also has to know which
    ///                     groups the body consumed so the rest can be appended.
    ///
    /// A handful of tokens exist in two forms because the source agreement quotes the same figure
    /// both ways - e.g. {{RETURNED_PAYMENT_FEE}} ("$35.00") in Exhibit B and
    /// {{RETURNED_PAYMENT_FEE_WORDS}} ("thirty-five dollars ($35.00)") in Section 11(g).
    /// </summary>
    public static class ContractPlaceholders
    {
        /// <summary>
        /// What an unanswered site detail or contact prints. See <c>PutBlank</c> in
        /// <see cref="Build"/> for why this is not an empty string and not "None".
        ///
        /// Aliases <see cref="ContractTextFormat.RuledBlank"/> rather than repeating the literal:
        /// an unset effective date comes back from <c>LongDate</c> as the same blank, and the
        /// renderer recognises this exact string to flag the field in the preview banner.
        /// </summary>
        public const string RuledBlank = ContractTextFormat.RuledBlank;

        /// <summary>
        /// The value a token resolves to when its whole LINE should be dropped from the document.
        /// <see cref="ContractRenderer"/> discards any line containing it.
        ///
        /// Deliberately a string no legal text could contain. It exists so a clause can be made
        /// conditional without a control structure in the template body - see
        /// {{RETURNED_PAYMENT_FEE_WORDS}} below.
        /// </summary>
        public const string OmitLineSentinel = "OMIT_LINE";

        /// <summary>
        /// Line guards: a template line ending in {{IF_MINIMUM_COMMITMENT}} is printed only when a
        /// minimum commitment was agreed, and one ending in {{IF_NO_MINIMUM_COMMITMENT}} only when
        /// none was. The applicable guard resolves to an empty string, the other to
        /// <see cref="OmitLineSentinel"/>.
        /// </summary>
        public const string GuardMinimumCommitment = "IF_MINIMUM_COMMITMENT";
        public const string GuardNoMinimumCommitment = "IF_NO_MINIMUM_COMMITMENT";

        /// <summary>
        /// Whether a token is a LINE GUARD - every guard's name starts with IF_. A guard that
        /// applies is EMPTY by design, so the renderer must not report it as unresolved; one that
        /// does not apply is the OMIT sentinel. The same names drive the block directive
        /// "@IF IF_NAME" ... "@ENDIF", which drops a whole run of lines - Exhibit A, or the
        /// optional endorsements subsection - when its guard does not apply.
        /// </summary>
        public static bool IsLineGuard(string tokenName) =>
            tokenName.StartsWith("IF_", StringComparison.Ordinal);

        /// <summary>
        /// What an unresolved token means to the admin filling in the form, for the preview banner
        /// and the refusal to send. The renderer only reports a token whose line SURVIVED the
        /// contract's own configuration (scope mode, commitment, optional rows), so by the time a
        /// name reaches here it is a value this document genuinely prints and nobody supplied.
        /// </summary>
        public static string DescribeToken(string tokenName) => tokenName switch
        {
            "EFFECTIVE_DATE" => "Effective date",
            "SERVICE_COMMENCEMENT_DATE" => "First recurring service date",
            "MINIMUM_COMMITMENT_END_DATE" or "INITIAL_TERM_END_DATE" =>
                "First recurring service date (the commitment dates are derived from it)",
            "EQUIPMENT_PROVIDED_BY" => "Supplies: who provides cleaning supplies and equipment",
            "TRASH_LINERS_PROVIDED_BY" => "Supplies: who provides trash bags and liners",
            "PAPER_TOWELS_PROVIDED_BY" => "Supplies: who provides paper towels",
            "TOILET_TISSUE_PROVIDED_BY" => "Supplies: who provides toilet tissue",
            "OTHER_CONSUMABLES" => "Supplies: who provides each other consumable",
            "CONTRACTOR_REPRESENTATIVE" => "Contractor signer",
            "CLIENT_REPRESENTATIVE" => "Client signer",
            "CONTRACTOR_OPERATIONAL_EMAIL" => "Contacts: contractor operational email",
            "CLIENT_OPERATIONAL_EMAIL" => "Contacts: client operational email",
            "CONTRACTOR_SUPERVISOR" => "Contacts: contractor supervisor / on-call contact",
            "CLIENT_ON_CALL_CONTACT" => "Contacts: client primary on-call contact",
            _ => tokenName
        };

        public static Dictionary<string, string> Build(ContractSnapshot s)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            void Put(string key, string? value) => map[key] = value ?? string.Empty;

            // An unfilled site detail or contact: a VISIBLE ruled blank, never a silent gap.
            //
            // Deliberately not an empty string and not "None". An empty string leaves a sentence
            // ending in a stray colon, and "None" is a positive statement - "Employee restroom and
            // fixture counts: None" tells a reader there is no employee restroom, when the truth
            // is that nobody counted it yet. A blank is an unanswered question, which is what it
            // is, and the preview's unresolved-token banner is the separate net for tokens that
            // are not mapped at all.
            void PutBlank(string key, string? value) =>
                map[key] = string.IsNullOrWhiteSpace(value) ? RuledBlank : value.Trim();

            // A field whose EMPTY ANSWER IS THE ANSWER - the drafted agreement's "[... OR NONE]".
            // "Required completion time: None" is a term the Parties agreed; a blank there would
            // leave a crew guessing whether one exists.
            void PutOrNone(string key, string? value) =>
                map[key] = string.IsNullOrWhiteSpace(value) ? "None" : value.Trim();

            // A GENUINELY OPTIONAL detail - a site fact, or a backup on-call contact: printed
            // when it was recorded, and its whole LINE dropped from the document when it was not.
            //
            // Distinct from both of the above, and the distinction is what "optional" means here.
            // A ruled blank says the question is still open and puts the field in the preview's
            // warning banner, which is exactly how an optional field comes to look mandatory to
            // the admin filling the form in. "None" is worse still - it is a positive statement
            // about the premises ("Food-service permit holder: None") that nobody made. Omitting
            // the line leaves an exhibit that is complete and internally consistent either way,
            // which is the same line-level mechanism the retired returned-payment fee uses.
            void PutOrOmitLine(string key, string? value) =>
                map[key] = string.IsNullOrWhiteSpace(value) ? OmitLineSentinel : value.Trim();

            // ── Dates / identity ───────────────────────────────────────────────
            Put("EFFECTIVE_DATE", ContractTextFormat.LongDate(s.EffectiveDate));
            Put("CONTRACT_NUMBER", s.ContractNumber);
            Put("CONTRACT_VERSION", s.VersionNumber.ToString());

            // ── Published policies (Section 36(o)) ─────────────────────────────
            // Read from the SNAPSHOT, never from CommercialPolicyDocument.Version. The published
            // policy is revised over time, and resolving it live would rewrite what an executed
            // agreement records as having been in force on the day it was signed.
            //
            // A snapshot frozen before Section 36 existed carries neither value, and its body
            // references neither token - but it falls back to the current constant rather than to
            // a ruled blank, because the only way to reach these tokens with an empty snapshot is
            // a draft saved in the window between deploying this and the draft being re-saved,
            // where the current version genuinely is the one in force.
            Put("POLICY_VERSION", string.IsNullOrWhiteSpace(s.PolicyVersion)
                ? Helpers.Commercial.CommercialPolicyDocument.Version
                : s.PolicyVersion);
            Put("POLICY_EFFECTIVE_DATE", Helpers.Commercial.CommercialPolicyDocument.FormatEffectiveDate(
                string.IsNullOrWhiteSpace(s.PolicyEffectiveDate)
                    ? Helpers.Commercial.CommercialPolicyDocument.EffectiveDate
                    : s.PolicyEffectiveDate));

            // The address the policies are published at - a pointer, deliberately NOT snapshotted
            // alongside the version, so a historical contract points somewhere that still resolves.
            Put("CONTRACTOR_PUBLISHED_POLICY_URL",
                Helpers.Commercial.CommercialPolicyDocument.PublishedPolicyUrl);

            // ── Contractor ─────────────────────────────────────────────────────
            var c = s.Contractor;
            Put("CONTRACTOR_LEGAL_NAME", c.LegalEntityName);
            Put("CONTRACTOR_ENTITY_TYPE", c.EntityType);
            Put("CONTRACTOR_DBA", c.Dba);
            // The "X d/b/a Y" form used in headings and the notice block; collapses cleanly when
            // there is no DBA rather than leaving a dangling "d/b/a".
            Put("CONTRACTOR_DISPLAY_NAME", string.IsNullOrWhiteSpace(c.Dba)
                ? c.LegalEntityName
                : $"{c.LegalEntityName} d/b/a {c.Dba}");
            Put("CONTRACTOR_ADDRESS", JoinAddress(c.Address, c.City, c.State, c.Zip));
            Put("CONTRACTOR_STREET", c.Address);
            Put("CONTRACTOR_CITY", c.City);
            Put("CONTRACTOR_STATE", c.State);
            Put("CONTRACTOR_ZIP", c.Zip);
            Put("CONTRACTOR_NOTICE_EMAIL", c.NoticeEmail);
            Put("CONTRACTOR_PHONE", ContractTextFormat.Phone(c.Phone));

            // ── Client ─────────────────────────────────────────────────────────
            var cl = s.Client;
            Put("CLIENT_LEGAL_NAME", cl.LegalEntityName);
            Put("CLIENT_ENTITY_TYPE", cl.EntityType);
            Put("CLIENT_FORMATION_STATE", cl.FormationState);

            // "a limited liability company organized under the laws of New York" - how the
            // preamble identifies the counterparty.
            //
            // Composed rather than stored, and it COLLAPSES when no formation state is recorded:
            // the two halves are separate columns, and a contract whose client was entered without
            // one must not read "...organized under the laws of ." on the first line a
            // counterparty sees. Which state a company was formed in is a real legal fact, so it
            // is stated when known and simply left out when it is not.
            Put("CLIENT_ENTITY_DESCRIPTION", string.IsNullOrWhiteSpace(cl.FormationState)
                ? cl.EntityType
                : $"{cl.EntityType} organized under the laws of {cl.FormationState.Trim()}");
            Put("CLIENT_ADDRESS", cl.PrincipalAddress);
            Put("CLIENT_FULL_ADDRESS", JoinAddress(cl.PrincipalAddress, cl.City, cl.State, cl.Zip));
            Put("CLIENT_CITY", cl.City);
            Put("CLIENT_STATE", cl.State);
            Put("CLIENT_ZIP", cl.Zip);
            Put("CLIENT_NOTICE_EMAIL", ResolveClientNoticeEmail(s.Contacts, cl));
            Put("CLIENT_PHONE", ContractTextFormat.Phone(cl.Phone));

            // KEPT ONLY FOR BODIES WRITTEN BEFORE v2.2. The current agreement does not ask Client
            // for a notice mailing address at all: the preamble identifies Client by legal entity
            // name, entity type and formation state, Exhibit B4 no longer carries the row, and
            // Section 32 serves formal notice on the designated notice EMAIL. Removing the token
            // would leave a frozen v2.0/v2.1 snapshot rendering a literal {{...}} years after it
            // was executed, which is why it stays mapped - exactly as the retired $35 returned-
            // payment fee does.
            //
            // The fallback to the principal address is likewise preserved so such a document
            // re-renders byte-for-byte the way it did when it was signed.
            var clientNoticeAddress = string.IsNullOrWhiteSpace(s.Contacts?.ClientNoticeMailingAddress)
                ? JoinAddress(cl.PrincipalAddress, cl.City, cl.State, cl.Zip)
                : s.Contacts!.ClientNoticeMailingAddress!.Trim();
            PutBlank("CLIENT_NOTICE_MAILING_ADDRESS", clientNoticeAddress);

            // ── Service location (never assumed equal to the client address) ────
            var loc = s.ServiceLocation;
            Put("SERVICE_BRAND", loc.BusinessBrand);
            Put("SERVICE_LOCATION_NAME", loc.LocationName);
            Put("SERVICE_ADDRESS", loc.Address);
            Put("SERVICE_FULL_ADDRESS", JoinAddress(loc.Address, loc.City, loc.State, loc.Zip));
            Put("SERVICE_CITY", loc.City);
            Put("SERVICE_STATE", loc.State);
            Put("SERVICE_ZIP", loc.Zip);
            Put("PREMISES_TYPE", s.PremisesType);
            // "the Chick-fil-A restaurant" / "the restaurant" when no brand is recorded.
            Put("PREMISES_DESCRIPTION", string.IsNullOrWhiteSpace(loc.BusinessBrand)
                ? s.PremisesType
                : $"{loc.BusinessBrand} {s.PremisesType}");

            // ── Signers ────────────────────────────────────────────────────────
            Put("CONTRACTOR_SIGNER_NAME", s.ContractorSigner.FullName);
            Put("CONTRACTOR_SIGNER_TITLE", s.ContractorSigner.Title);
            Put("CONTRACTOR_SIGNER_EMAIL", s.ContractorSigner.Email);
            Put("CLIENT_SIGNER_NAME", s.ClientSigner.FullName);
            Put("CLIENT_SIGNER_TITLE", s.ClientSigner.Title);
            Put("CLIENT_SIGNER_EMAIL", s.ClientSigner.Email);

            // ── Schedule ───────────────────────────────────────────────────────
            var sc = s.Schedule;
            var frequencyText = FrequencyText(sc);
            Put("SERVICE_FREQUENCY_TEXT", frequencyText);
            // Exhibit A and B open a cell with the frequency, so they need it sentence-cased.
            Put("SERVICE_FREQUENCY_TEXT_CAP", Capitalize(frequencyText));
            Put("VISITS_PER_PERIOD", ContractTextFormat.WordsWithDigits(Math.Max(1, sc.VisitsPerPeriod)));
            Put("SERVICE_PERIOD", sc.FrequencyUnit);

            // ── Service days ───────────────────────────────────────────────────
            // A contract may be cleaned on several weekdays, so every one of these is derived from
            // the resolved LIST rather than the legacy single column. {{SERVICE_DAY}} is kept as an
            // alias of {{SERVICE_DAYS}} so a template body written before multiple days existed -
            // including one a SuperAdmin has since edited - keeps rendering the right weekdays.
            var dayNames = sc.ResolveServiceDayNames();
            var multiple = dayNames.Count > 1;
            var daysText = ContractTextFormat.JoinWithAnd(dayNames);

            Put("SERVICE_DAYS", daysText);
            Put("SERVICE_DAY", daysText);
            Put("SERVICE_DAY_NOUN", multiple ? "days" : "day");
            Put("SERVICE_DAY_NOUN_UPPER", multiple ? "DAYS" : "DAY");
            Put("SERVICE_DAY_VERB", multiple ? "are" : "is");
            // The sentence that follows the list. Both halves have to agree in number with the
            // list above them, which is the whole reason it is composed here rather than written
            // into the template with a hardcoded singular.
            Put("SERVICE_DAY_FIXED_TEXT", sc.FlexibleScheduling
                ? (multiple
                    ? "Those days are not permanently fixed service days."
                    : "That day is not a permanently fixed service day.")
                : (multiple
                    ? "Those days are fixed service days and may be changed only by signed Change Order."
                    : "That day is a fixed service day and may be changed only by signed Change Order."));

            Put("SERVICE_TIME", sc.ServiceTime);
            Put("ACCESS_TYPE", sc.AccessType);

            // The agreed ARRIVAL WINDOW, resolved so a contract drafted before windows existed
            // still renders its single start time rather than an empty phrase mid-sentence.
            Put("ARRIVAL_WINDOW", sc.ResolveArrivalWindow());
            Put("ARRIVAL_WINDOW_START", sc.ArrivalWindowStart);
            Put("ARRIVAL_WINDOW_END", sc.ArrivalWindowEnd);
            Put("SERVICE_TIMEZONE", sc.TimeZoneLabel);
            Put("WEEK_DEFINITION", sc.WeekDefinition);

            // "[COMPLETION TIME OR NONE]" in the drafted agreement: no agreed deadline is a term,
            // not an omission.
            PutOrNone("COMPLETION_TIME", sc.CompletionTime);

            // ── Billing cadence ────────────────────────────────────────────────
            // How often the client is INVOICED, which Exhibit B states separately from how often
            // the premises are cleaned.
            Put("BILLING_CADENCE_TEXT", BillingCadenceText(s.Billing));

            // ── Term ───────────────────────────────────────────────────────────
            var t = s.Term;
            Put("INITIAL_TERM_MONTHS", ContractTextFormat.WordsWithDigits(t.InitialTermMonths));
            Put("MINIMUM_COMMITMENT_MONTHS", ContractTextFormat.WordsWithDigits(t.MinimumCommitmentMonths));
            Put("TERMINATION_NOTICE_DAYS", ContractTextFormat.WordsWithDigits(t.TerminationNoticeDays));
            Put("RENEWAL_TYPE", t.RenewalType);
            // Section 3(d) quotes the phrase back as a defined term at the head of a sentence.
            Put("RENEWAL_TYPE_CAP", Capitalize(t.RenewalType));
            Put("GOVERNING_LAW_STATE", t.GoverningLawState);
            Put("VENUE_COUNTY", t.VenueCounty);

            // The three dates Section 3 and Exhibit B1 turn on. The two end dates are DERIVED from
            // the commencement date rather than typed, so the document cannot state a Minimum
            // Commitment End Date that disagrees with the number of months in the sentence above
            // it. An unset commencement date leaves all three blank rather than guessing from the
            // effective date - guessing would silently shorten the commitment being agreed to.
            PutBlank("SERVICE_COMMENCEMENT_DATE", DateOrNull(t.ServiceCommencementDate));
            PutBlank("MINIMUM_COMMITMENT_END_DATE", DateOrNull(t.ResolveMinimumCommitmentEndDate()));
            PutBlank("INITIAL_TERM_END_DATE", DateOrNull(t.ResolveInitialTermEndDate()));

            // LINE GUARDS for the minimum commitment (template v2.7). There is no company-wide
            // commitment; one exists only where this client agreed one. So Sections 3, 4 and 36
            // and Exhibit B1 carry each affected sentence twice - once for each case - and every
            // copy ends in one of these two tokens. The guard that does not apply resolves to the
            // OMIT sentinel and takes its line with it; the one that does resolves to nothing and
            // leaves the line as written. The legal text stays in the template, where a SuperAdmin
            // can read and edit it, rather than being composed here.
            //
            // A contract with no commitment therefore contains no Minimum Commitment Period, no
            // Minimum Commitment End Date and no Initial Term - only the statement that none
            // applies. ContractRenderer treats these two names as guards, so their empty value is
            // never reported as an unresolved token.
            map[GuardMinimumCommitment] = t.HasMinimumCommitment ? string.Empty : OmitLineSentinel;
            map[GuardNoMinimumCommitment] = t.HasMinimumCommitment ? OmitLineSentinel : string.Empty;

            // ── Pricing (all figures server-derived) ───────────────────────────
            var p = s.Pricing;
            Put("PRE_TAX_PRICE", ContractTextFormat.Money(p.PreTaxPrice));
            Put("SALES_TAX_RATE", ContractTextFormat.FormatPercent(p.SalesTaxRatePercent));
            Put("SALES_TAX_AMOUNT", ContractTextFormat.Money(p.SalesTaxAmount));
            Put("TOTAL_PRICE", ContractTextFormat.Money(p.TotalPrice));
            Put("CANCELLATION_PERCENT", ContractTextFormat.PercentWithDigits(p.CancellationPercent));
            Put("CANCELLATION_PERCENT_SHORT", ContractTextFormat.FormatPercent(p.CancellationPercent));
            Put("CANCELLATION_AMOUNT", ContractTextFormat.Money(p.CancellationAmount));
            Put("REMAINING_BALANCE", ContractTextFormat.Money(p.RemainingBalance));
            Put("LOCKOUT_FEE", ContractTextFormat.Money(p.LockoutFee));
            Put("INVOICE_TIMING", p.InvoiceTiming);
            Put("PAYMENT_DEADLINE_HOURS", ContractTextFormat.WordsWithDigits(p.PaymentDeadlineHours));
            Put("PAYMENT_METHOD", p.PaymentMethod);
            Put("LATE_CHARGE_PERCENT", ContractTextFormat.PercentWithDigits(p.LateChargePercent));
            Put("LATE_CHARGE_PERCENT_SHORT", ContractTextFormat.FormatPercent(p.LateChargePercent));
            // Section 11(f) states the same charge monthly AND annually; both come off the one
            // stored rate so the pair can never contradict each other.
            Put("LATE_CHARGE_ANNUAL_PERCENT", ContractTextFormat.PercentWithDigits(p.LateChargeAnnualPercent));
            Put("LATE_CHARGE_ANNUAL_PERCENT_SHORT", ContractTextFormat.FormatPercent(p.LateChargeAnnualPercent));

            // Section 29(b) / Exhibit B2: the aggregate liability cap as a multiple of the pre-tax
            // per-visit fee, and the dollar figure that multiple currently produces.
            Put("LIABILITY_CAP_MULTIPLE", ContractTextFormat.WordsWithDigits(p.LiabilityCapMultiple));
            Put("LIABILITY_CAP_AMOUNT", ContractTextFormat.Money(p.LiabilityCapAmount));
            // The returned-payment fee is RETIRED for new contracts (2026-09) and defaults to zero.
            // At zero the Exhibit B row reads "...; no returned or failed payment fee", and Section
            // 11(g) drops out entirely through the OMIT sentinel rather than promising a $0.00
            // charge. A historical contract that agreed to $35 renders exactly as it always did,
            // because its frozen snapshot still carries the figure.
            Put("RETURNED_PAYMENT_FEE", p.ReturnedPaymentFee > 0m
                ? ContractTextFormat.Money(p.ReturnedPaymentFee)
                : "no");
            Put("RETURNED_PAYMENT_FEE_WORDS", p.ReturnedPaymentFee > 0m
                ? ContractTextFormat.MoneyWithWords(p.ReturnedPaymentFee)
                : OmitLineSentinel);

            // ── Advanced terms ─────────────────────────────────────────────────
            var a = s.Advanced;

            // Scheduling, cancellation and makeup.
            Put("TIMELY_RESCHEDULE_HOURS", ContractTextFormat.WordsWithDigits(a.TimelyRescheduleHours));
            // Section 32(b) hyphenates the same figure into "the twenty-four-hour cancellation
            // calculation", where a parenthesised digit would not survive the hyphen.
            Put("TIMELY_RESCHEDULE_HOURS_WORDS", ContractTextFormat.Words(a.TimelyRescheduleHours));
            Put("MAKEUP_WINDOW_DAYS", ContractTextFormat.WordsWithDigits(a.MakeupWindowDays));
            Put("LOCKOUT_WAIT_MINUTES", ContractTextFormat.WordsWithDigits(a.LockoutWaitMinutes));

            // Section 15(f). The threshold OPENS its sentence, and the two ordinals that follow
            // are derived from it so raising the threshold cannot leave the clause warning after
            // the second visit and terminating on the third.
            Put("MISSED_VISIT_THRESHOLD", ContractTextFormat.WordsWithDigits(a.MissedVisitThreshold));
            Put("MISSED_VISIT_THRESHOLD_CAP", ContractTextFormat.WordsCapitalized(a.MissedVisitThreshold));
            Put("MISSED_VISIT_WINDOW_WEEKS", ContractTextFormat.Words(a.MissedVisitWindowWeeks));
            Put("MISSED_VISIT_WARNING_ORDINAL", ContractTextFormat.Ordinal(Math.Max(1, a.MissedVisitThreshold - 1)));
            Put("MISSED_VISIT_FINAL_ORDINAL", ContractTextFormat.Ordinal(Math.Max(1, a.MissedVisitThreshold)));
            Put("SERVICE_PLAN_DAYS", ContractTextFormat.WordsWithDigits(a.ServicePlanDays));

            // Termination and cure.
            Put("CURE_PERIOD_DAYS", ContractTextFormat.WordsWithDigits(a.CurePeriodDays));
            Put("PAST_DUE_DAYS", ContractTextFormat.WordsWithDigits(a.PastDueDays));
            Put("CREDIT_RETURN_DAYS", ContractTextFormat.WordsWithDigits(a.CreditReturnDays));
            Put("FORCE_MAJEURE_DAYS", ContractTextFormat.WordsWithDigits(a.ForceMajeureDays));

            // Invoicing and money.
            Put("INVOICE_LEAD_DAYS", ContractTextFormat.WordsWithDigits(a.InvoiceLeadDays));
            Put("LATE_INVOICE_THRESHOLD_DAYS", ContractTextFormat.WordsWithDigits(a.LateInvoiceThresholdDays));
            Put("LATE_INVOICE_GRACE_BUSINESS_DAYS", ContractTextFormat.WordsWithDigits(a.LateInvoiceGraceBusinessDays));
            Put("INTEREST_GRACE_DAYS", ContractTextFormat.WordsWithDigits(a.InterestGraceDays));

            // Disputes, damage and quality.
            Put("BILLING_DISPUTE_DAYS", ContractTextFormat.WordsWithDigits(a.BillingDisputeDays));
            Put("DISPUTE_RESPONSE_BUSINESS_DAYS", ContractTextFormat.WordsWithDigits(a.DisputeResponseBusinessDays));
            Put("RESOLUTION_PAYMENT_BUSINESS_DAYS", ContractTextFormat.WordsWithDigits(a.ResolutionPaymentBusinessDays));
            Put("DAMAGE_NOTICE_BUSINESS_DAYS", ContractTextFormat.WordsWithDigits(a.DamageNoticeBusinessDays));
            Put("QUALITY_COMPLAINT_HOURS", ContractTextFormat.WordsWithDigits(a.QualityComplaintHours));
            // Section 22(a)'s narrow outer limit for a deficiency not reasonably discoverable
            // inside the standard window. Never shorter than that window: an outer limit that
            // closed before the standard period would contradict the sentence before it.
            Put("QUALITY_LATENT_LIMIT_HOURS", ContractTextFormat.WordsWithDigits(
                Math.Max(a.QualityLatentDeficiencyLimitHours, a.QualityComplaintHours)));
            Put("QUALITY_CORRECTION_BUSINESS_DAYS", ContractTextFormat.WordsWithDigits(a.QualityCorrectionBusinessDays));
            Put("REFUND_BUSINESS_DAYS", ContractTextFormat.WordsWithDigits(a.RefundBusinessDays));

            // Access.
            Put("KEY_RETURN_BUSINESS_DAYS", ContractTextFormat.WordsWithDigits(a.KeyReturnBusinessDays));

            // Confidentiality.
            Put("CONFIDENTIALITY_YEARS", ContractTextFormat.WordsWithDigits(a.ConfidentialityYears));

            // Insurance.
            Put("INSURANCE_PER_OCCURRENCE", ContractTextFormat.Money(a.InsurancePerOccurrence));
            Put("INSURANCE_AGGREGATE", ContractTextFormat.Money(a.InsuranceAggregate));
            Put("INSURANCE_JURISDICTION", a.InsuranceJurisdiction);

            // Pricing review.
            Put("PRICE_REVIEW_NOTICE_DAYS", ContractTextFormat.WordsWithDigits(a.PriceReviewNoticeDays));

            // Compliance and dispute resolution.
            Put("COMPLIANCE_JURISDICTIONS", a.ComplianceJurisdictions);
            Put("DISPUTE_DISCUSSION_DAYS", ContractTextFormat.WordsWithDigits(a.DisputeDiscussionDays));
            Put("MEDIATION_REQUEST_DAYS", ContractTextFormat.WordsWithDigits(a.MediationRequestDays));
            Put("MEDIATOR_SELECTION_DAYS", ContractTextFormat.WordsWithDigits(a.MediatorSelectionDays));
            Put("SUIT_AFTER_DAYS", ContractTextFormat.WordsWithDigits(a.SuitAfterDays));
            Put("COLLECTION_DEMAND_BUSINESS_DAYS", ContractTextFormat.WordsWithDigits(a.CollectionDemandBusinessDays));
            Put("MEDIATION_VENUE", a.MediationVenue);
            Put("FEDERAL_VENUE", a.FederalVenue);

            // ── Exhibit A: recorded site details ───────────────────────────────
            // Most of these are blanks the Parties fill in. An unanswered one renders as a ruled
            // blank so it is visibly unanswered, rather than as "None" - which would assert that
            // the premises has no employee restroom. The two PutOrOmitLine fields below are the
            // exception and are marked as such.
            var sd = s.SiteDetails ?? new SiteDetailsSnapshot();
            PutBlank("SQUARE_FOOTAGE", sd.ApproximateSquareFootage);
            PutBlank("CUSTOMER_RESTROOM_COUNTS", sd.CustomerRestroomCounts);
            PutBlank("EMPLOYEE_RESTROOM_COUNTS", sd.EmployeeRestroomCounts);
            // FULLY OPTIONAL (2026-09-15). A4 no longer hangs the floor-cleaning obligation on
            // this field being populated - Contractor uses commercially reasonable,
            // surface-appropriate products and methods whether or not the materials were written
            // down - so an unanswered one is not an open question and must not print a blank.
            PutOrOmitLine("FLOOR_MATERIALS", sd.FloorMaterials);
            PutBlank("KITCHEN_EQUIPMENT_SURFACES", sd.KitchenEquipmentAndSurfaces);
            PutBlank("TOUCHPOINT_LOCATIONS", sd.TouchpointLocations);
            PutBlank("INTERIOR_GLASS_LOCATIONS", sd.InteriorGlassLocations);
            PutBlank("ACCESS_METHOD_REFERENCE", sd.AccessMethodReference);
            PutBlank("EQUIPMENT_RESTRICTIONS", sd.EquipmentRestrictions);
            PutBlank("WASTE_RECEPTACLE_LOCATIONS", sd.WasteReceptacleLocations);
            // FULLY OPTIONAL (2026-09-15), same rule and the same reason: Section 16(c) no longer
            // requires Client to identify the permit holder before work begins, and Section 26(b)
            // leaves Client responsible for its own permits, sanitation and food handling whether
            // or not a name was recorded here. A blank line would imply a missing precondition
            // that no longer exists.
            PutOrOmitLine("FOOD_PERMIT_HOLDER", sd.FoodServicePermitHolder);
            PutBlank("BASELINE_WALKTHROUGH", sd.BaselineWalkthroughRecord);

            // These three the drafted agreement writes as "[... OR NONE]": an empty answer is a
            // term of the deal. Food-contact sanitizing especially - Section 25(e) and A3(d)
            // exclude it unless a task is expressly identified here, so "None" is the agreement
            // saying Client keeps that responsibility.
            PutOrNone("FOOD_CONTACT_SANITIZING", sd.FoodContactSanitizing);
            PutOrNone("SITE_REQUIREMENTS", sd.SiteRequirements);
            PutOrNone("INITIAL_WORK_CHANGE_ORDER", sd.InitialWorkChangeOrder);

            // ── Insurance endorsements: B3 in bodies before v2.7 (these legacy tokens), B5 since ──
            var ins = s.Insurance ?? new InsuranceEndorsementsSnapshot();
            PutOrNone("AGREED_ENDORSEMENTS", ins.AgreedEndorsements);
            PutOrNone("ENDORSEMENT_PREMIUM", ins.AdditionalPremium);
            // "[DETAILS OR NOT APPLICABLE]" - with no endorsement agreed there is nothing to
            // identify, which is a different statement from "no details were recorded".
            map["ENDORSEMENT_DETAILS"] = string.IsNullOrWhiteSpace(ins.EndorsementDetails)
                ? "Not applicable"
                : ins.EndorsementDetails.Trim();

            // ── Exhibit B4: operational contacts ───────────────────────────────
            // The authorized REPRESENTATIVE is the signer - the person who executes the agreement
            // is the person who may approve a Change Order under Section 9(e), and naming them
            // twice from two sources is how the two come to disagree.
            var contacts = s.Contacts ?? new OperationalContactsSnapshot();
            PutBlank("CONTRACTOR_REPRESENTATIVE", NameAndTitle(s.ContractorSigner));
            PutBlank("CLIENT_REPRESENTATIVE", NameAndTitle(s.ClientSigner));

            // Marked optional in the drafted agreement: a Party may simply approve from its
            // notice address instead of nominating a separate approval mailbox.
            PutOrNone("CONTRACTOR_APPROVAL_EMAIL", contacts.ContractorApprovalEmail);
            PutOrNone("CLIENT_APPROVAL_EMAIL", contacts.ClientApprovalEmail);

            // The operational mailbox is where weekend scheduling and access notices land. It
            // falls back to the notice address rather than blanking: Section 32(b) makes a
            // cancellation effective when SENT to it, so a blank would leave the clause with no
            // destination and no way for a client to cancel in time.
            PutBlank("CONTRACTOR_OPERATIONAL_EMAIL",
                Coalesce(contacts.ContractorOperationalEmail, c.NoticeEmail));
            PutBlank("CLIENT_OPERATIONAL_EMAIL",
                Coalesce(contacts.ClientOperationalEmail, cl.NoticeEmail));

            PutBlank("CONTRACTOR_SUPERVISOR", NameAndPhone(
                contacts.ContractorSupervisorName,
                Coalesce(contacts.ContractorSupervisorPhone, c.Phone)));
            PutBlank("CLIENT_ON_CALL_CONTACT", NameAndPhone(
                contacts.ClientOnCallName,
                Coalesce(contacts.ClientOnCallPhone, cl.Phone)));

            // THE BACKUP ON-CALL CONTACTS ARE FULLY OPTIONAL (2026-09-16), on both sides.
            //
            // Section 16(c) asks Client for a PRIMARY contact and says a backup is one it "may
            // designate ... if available", so an empty one is not an unanswered question - and a
            // ruled blank would both say it was and put the field in the preview's warning
            // banner, which is how an optional field comes to look mandatory to the admin filling
            // the form in. "None" is no better on an Exhibit B4 row: it reads as a positive
            // statement that no second person exists, which nobody made.
            //
            // So the whole ROW leaves the exhibit - the same line-level mechanism the retired
            // returned-payment fee and Exhibit A's optional site details use. A backup that WAS
            // recorded still prints its row exactly as before, which is what keeps every existing
            // contract and draft rendering unchanged.
            //
            // The primaries above deliberately keep PutBlank: Section 14 hangs a failed-access
            // charge on Contractor having tried to reach one, so a missing primary IS an open
            // question the preview must chase.
            PutOrOmitLine("CONTRACTOR_BACKUP_CONTACT", contacts.ContractorBackupContact);
            PutOrOmitLine("CLIENT_BACKUP_CONTACT", contacts.ClientBackupContact);

            // ── Section 6(b) / Exhibit B, B3: supplies, equipment and consumables ──
            // No company-wide allocation exists (2026-09-30): each answer is a term of this
            // agreement. An unanswered one prints a RULED BLANK in B3, which puts it in the
            // preview's unresolved banner - never a guessed "Client", which would quietly
            // reinstate the allocation the older templates hardcoded.
            var sup = s.Supplies ?? new SuppliesSnapshot();
            Put("SUPPLIES_FEE_CLAUSE", SuppliesFeeClause(sup.EquipmentProvidedBy));
            map["EQUIPMENT_PROVIDED_BY"] = EquipmentProvidedByText(sup);
            PutBlank("TRASH_LINERS_PROVIDED_BY", ProviderName(sup.TrashLinersProvidedBy));
            PutBlank("PAPER_TOWELS_PROVIDED_BY", ProviderName(sup.PaperTowelsProvidedBy));
            PutBlank("TOILET_TISSUE_PROVIDED_BY", ProviderName(sup.ToiletTissueProvidedBy));
            // Genuinely optional: with no further consumables agreed the row leaves the exhibit.
            PutOrOmitLine("OTHER_CONSUMABLES", OtherConsumablesText(sup.OtherConsumables));

            // ── Template v2.7: Scope of Work detail and blank-means-omitted ─────
            // Exhibit A is Detailed, Simplified or Omitted per contract. The body's references to
            // it go through {{SCOPE_REF}} and the EXHIBITS_* phrases, so no clause can point at an
            // exhibit the document does not contain; the exhibit itself sits inside
            // "@IF IF_SCOPE_DETAILED" / "@IF IF_SCOPE_SIMPLIFIED" blocks.
            var mode = s.ScopeDetail;
            void Guard(string name, bool applies) => map[name] = applies ? string.Empty : OmitLineSentinel;
            Guard("IF_SCOPE_DETAILED", mode == ScopeDetailMode.Detailed);
            Guard("IF_SCOPE_NOT_DETAILED", mode != ScopeDetailMode.Detailed);
            Guard("IF_SCOPE_SIMPLIFIED", mode == ScopeDetailMode.Simplified);
            Guard("IF_SCOPE_OMITTED", mode == ScopeDetailMode.Omitted);
            Put("SCOPE_REF", mode switch
            {
                ScopeDetailMode.Simplified => "the Scope of Work recorded in Exhibit A",
                ScopeDetailMode.Omitted => "the agreed Scope of Work",
                _ => "Exhibit A"
            });
            var hasExhibitA = mode != ScopeDetailMode.Omitted;
            Put("EXHIBITS_LIST", hasExhibitA ? "Exhibits A and B" : "Exhibit B");
            Put("EXHIBITS_INCORPORATED", hasExhibitA
                ? "Exhibit A and Exhibit B are incorporated into and form part"
                : "Exhibit B is incorporated into and forms part");
            Put("EXHIBITS_SIGNED", hasExhibitA ? "Exhibit A and Exhibit B" : "Exhibit B");

            // The v2.7 body prints an OPTIONAL field only when it holds a value: no ruled blank,
            // no "None", no "Not applicable" - the whole line or row leaves the document. These are
            // TWINS of the older tokens rather than a change to them, because an executed contract
            // re-renders its frozen body through this map, and the older tokens must keep printing
            // exactly what was signed. Nothing here is load-bearing when blank: Section 25(e) and
            // A3(d) exclude food-contact sanitizing unless a task is named, Section 5(c) means no
            // deadline when none is stated, and the Section 20 coverage stands on its own.
            string OptionalValue(string? value) =>
                string.IsNullOrWhiteSpace(value) ? OmitLineSentinel : value.Trim();
            var siteValues = new (string Key, string? Value)[]
            {
                ("SQUARE_FOOTAGE", sd.ApproximateSquareFootage),
                ("CUSTOMER_RESTROOM_COUNTS", sd.CustomerRestroomCounts),
                ("EMPLOYEE_RESTROOM_COUNTS", sd.EmployeeRestroomCounts),
                ("FLOOR_MATERIALS", sd.FloorMaterials),
                ("KITCHEN_EQUIPMENT_SURFACES", sd.KitchenEquipmentAndSurfaces),
                ("TOUCHPOINT_LOCATIONS", sd.TouchpointLocations),
                ("INTERIOR_GLASS_LOCATIONS", sd.InteriorGlassLocations),
                ("FOOD_CONTACT_SANITIZING", sd.FoodContactSanitizing),
                ("ACCESS_METHOD_REFERENCE", sd.AccessMethodReference),
                ("EQUIPMENT_RESTRICTIONS", sd.EquipmentRestrictions),
                ("WASTE_RECEPTACLE_LOCATIONS", sd.WasteReceptacleLocations),
                ("FOOD_PERMIT_HOLDER", sd.FoodServicePermitHolder),
                ("SITE_REQUIREMENTS", sd.SiteRequirements),
                ("BASELINE_WALKTHROUGH", sd.BaselineWalkthroughRecord),
                ("INITIAL_WORK_CHANGE_ORDER", sd.InitialWorkChangeOrder)
            };
            foreach (var (key, value) in siteValues) map[key + "_IF_SET"] = OptionalValue(value);
            // The introduction to the A1 site-details list prints only when the list has an entry.
            // The two baseline rows sit under A8, so they do not count here.
            Guard("IF_ANY_SITE_DETAIL", siteValues
                .Where(v => v.Key is not ("BASELINE_WALKTHROUGH" or "INITIAL_WORK_CHANGE_ORDER"))
                .Any(v => !string.IsNullOrWhiteSpace(v.Value)));

            map["COMPLETION_TIME_IF_SET"] = OptionalValue(sc.CompletionTime);

            // Template v2.8: a completion time is MENTIONED only when one was agreed. Without one,
            // Section 5(c) does not point at an Exhibit B row that is not there, and 5(e) / 9(b)
            // speak of changes to "the day or arrival window" rather than a deadline that does
            // not exist.
            var hasCompletionTime = !string.IsNullOrWhiteSpace(sc.CompletionTime);
            Guard("IF_COMPLETION_TIME", hasCompletionTime);
            Guard("IF_NO_COMPLETION_TIME", !hasCompletionTime);
            Put("SCHEDULE_CHANGE_TERMS", hasCompletionTime
                ? "day, arrival window or completion deadline"
                : "day or arrival window");

            // Template v3.1: two Detailed Exhibit A subsections have no scope group of their own, so
            // whether their work was bought is read off the SELECTED included items - label or
            // detail naming the subject, in any included group (area/task rows, checklists and custom
            // categories alike). An interior-glass site detail also counts. Unselected rows never do.
            bool IncludesWork(params string[] words) => (s.Scope?.Groups ?? new List<ScopeGroup>())
                .Where(g => !string.Equals(g.Kind, "excluded", StringComparison.OrdinalIgnoreCase) && !g.Archived)
                .SelectMany(g => g.Items)
                .Where(i => i.Selected && !i.Archived)
                .Any(i => words.Any(w =>
                    (i.Label ?? string.Empty).Contains(w, StringComparison.OrdinalIgnoreCase)
                    || (i.Detail ?? string.Empty).Contains(w, StringComparison.OrdinalIgnoreCase)));
            Guard("IF_TRASH_SCOPE", IncludesWork("trash", "waste", "recycling"));
            Guard("IF_INTERIOR_GLASS_SCOPE", IncludesWork("glass", "window")
                || !string.IsNullOrWhiteSpace(sd.InteriorGlassLocations));

            // Template v3.0: per-visit or weekly flat pricing. Each pricing sentence exists once per
            // basis; the one that does not apply drops its line. PRE_TAX_PRICE / SALES_TAX_AMOUNT /
            // TOTAL_PRICE are already the WEEKLY figures in weekly mode (see the calculator), and the
            // caps are already built on the per-visit allocation - so no other token changes.
            var weekly = s.Pricing?.PricingBasis == ContractPricingBasis.WeeklyFlatFee;
            Guard("IF_PER_VISIT_PRICING", !weekly);
            Guard("IF_WEEKLY_FLAT_PRICING", weekly);

            // Section 35(f): "A1" / "A8" style references exist only in a Detailed Exhibit A.
            Put("EXHIBIT_CROSS_REFERENCES", mode == ScopeDetailMode.Detailed
                ? "references beginning with A or B refer to the corresponding exhibit"
                : "references beginning with B refer to the corresponding part of Exhibit B");
            map["CONTRACTOR_APPROVAL_EMAIL_IF_SET"] = OptionalValue(contacts.ContractorApprovalEmail);
            map["CLIENT_APPROVAL_EMAIL_IF_SET"] = OptionalValue(contacts.ClientApprovalEmail);

            // Exhibit B5 exists only when an endorsement beyond Section 20 was agreed.
            map["AGREED_ENDORSEMENTS_IF_SET"] = OptionalValue(ins.AgreedEndorsements);
            map["ENDORSEMENT_DETAILS_IF_SET"] = OptionalValue(ins.EndorsementDetails);
            map["ENDORSEMENT_PREMIUM_IF_SET"] = OptionalValue(ins.AdditionalPremium);
            Guard("IF_ANY_ENDORSEMENT",
                !string.IsNullOrWhiteSpace(ins.AgreedEndorsements)
                || !string.IsNullOrWhiteSpace(ins.EndorsementDetails)
                || !string.IsNullOrWhiteSpace(ins.AdditionalPremium));

            // ── Derived scheduling prose ───────────────────────────────────────
            // Section 5(f) / Exhibit A CONDITIONS: whether the premises are open during service.
            Put("CLOSED_PREMISES_TEXT", sc.PerformedWhileClosed
                ? $"The Services are generally performed while the {s.PremisesType} is closed."
                : $"The Services are generally performed while the {s.PremisesType} is open.");
            Put("SCHEDULE_FLEXIBILITY_TEXT", sc.FlexibleScheduling
                ? "subject to mutually agreed scheduling changes under Section 5"
                : "as a fixed service day and time, changeable only by signed Change Order");

            return map;
        }

        /// <summary>
        /// "monthly", "every two weeks", "for each scheduled service visit" - the phrase Exhibit B
        /// uses to state how often an invoice is issued.
        ///
        /// Composed rather than stored so the interval count and the frequency can never contradict
        /// each other in the document: "every 1 weeks" is the kind of thing a client notices.
        /// </summary>
        private static string BillingCadenceText(BillingCadenceSnapshot b)
        {
            var n = Math.Max(1, b?.IntervalCount ?? 1);

            return (b?.Frequency ?? ContractBillingFrequency.Monthly) switch
            {
                ContractBillingFrequency.PerServiceVisit => "for each scheduled service visit",
                ContractBillingFrequency.Weekly => n == 1
                    ? "weekly"
                    : $"every {ContractTextFormat.WordsWithDigits(n)} weeks",
                ContractBillingFrequency.CustomDays => $"every {ContractTextFormat.WordsWithDigits(n)} days",
                _ => n == 1
                    ? "monthly"
                    : $"every {ContractTextFormat.WordsWithDigits(n)} months"
            };
        }

        /// <summary>
        /// Section 6(b) - what the recurring fee includes, which depends on who provides the
        /// cleaning equipment and supplies. Where the Client provides some or all of them,
        /// Contractor may decline an item it reasonably considers unsafe or unsuitable: a crew
        /// cannot be obliged to use a product it has no way to vouch for.
        /// </summary>
        private static string SuppliesFeeClause(SupplyProvider? provider) => provider switch
        {
            SupplyProvider.Contractor =>
                "The recurring fee includes all labor, supervision, cleaning equipment, tools, "
                + "chemicals, products and ordinary cleaning supplies used by Contractor's personnel "
                + "to perform the included Services. Contractor supplies them at its own expense. No "
                + "separate equipment or cleaning-product charge applies.",
            SupplyProvider.Client =>
                "The recurring fee includes all labor and supervision. Client supplies, at its own "
                + "cost, the cleaning equipment, tools, chemicals, products and ordinary cleaning "
                + "supplies needed for the included Services, in adequate quantity and safe working "
                + "order, as recorded in Exhibit B, B3. Contractor shall use Client-supplied products "
                + "according to their labels and may decline to use a product or item of equipment it "
                + "reasonably considers unsafe or unsuitable for the task, notifying Client promptly.",
            SupplyProvider.Shared =>
                "The recurring fee includes all labor and supervision and the cleaning equipment, "
                + "tools, chemicals, products and ordinary cleaning supplies allocated to Contractor in "
                + "Exhibit B, B3, which Contractor supplies at its own expense; no separate charge "
                + "applies for them. Client supplies, at its own cost, the items allocated to Client "
                + "there, in adequate quantity and safe working order. Contractor shall use "
                + "Client-supplied products according to their labels and may decline to use a product "
                + "or item of equipment it reasonably considers unsafe or unsuitable for the task, "
                + "notifying Client promptly.",
            _ =>
                "Cleaning equipment, tools, chemicals, products and ordinary cleaning supplies are "
                + "provided, at its own expense, by the Party identified in Exhibit B, B3."
        };

        /// <summary>
        /// Exhibit B, B3's equipment row. A shared arrangement must say HOW it is shared, so one
        /// with no description is an unanswered question and prints a ruled blank.
        /// </summary>
        private static string EquipmentProvidedByText(SuppliesSnapshot sup)
        {
            if (sup.EquipmentProvidedBy == SupplyProvider.Shared)
            {
                return string.IsNullOrWhiteSpace(sup.EquipmentArrangementNotes)
                    ? RuledBlank
                    : $"Divided between the Parties: {sup.EquipmentArrangementNotes.Trim()}";
            }
            return ProviderName(sup.EquipmentProvidedBy) ?? RuledBlank;
        }

        /// <summary>"Contractor" / "Client", or null when nobody has answered.</summary>
        private static string? ProviderName(SupplyProvider? provider) => provider switch
        {
            SupplyProvider.Contractor => "Contractor",
            SupplyProvider.Client => "Client",
            _ => null
        };

        /// <summary>
        /// "Contractor: hand towels and seat covers; Client: coffee filters". Grouped by Party so
        /// the row reads as an allocation rather than a list to cross-reference. An item nobody has
        /// assigned yet is listed with a ruled blank rather than guessed.
        /// </summary>
        private static string? OtherConsumablesText(List<ConsumableAllocation>? items)
        {
            var named = (items ?? new List<ConsumableAllocation>())
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Item))
                .ToList();
            if (named.Count == 0) return null;

            var parts = new List<string>();
            foreach (var (provider, label) in new[]
            {
                ((SupplyProvider?)SupplyProvider.Contractor, "Contractor"),
                ((SupplyProvider?)SupplyProvider.Client, "Client")
            })
            {
                var names = named.Where(i => i.ProvidedBy == provider).Select(i => i.Item.Trim()).ToList();
                if (names.Count > 0) parts.Add($"{label}: {ContractTextFormat.JoinWithAnd(names)}");
            }

            var unassigned = named
                .Where(i => i.ProvidedBy != SupplyProvider.Contractor && i.ProvidedBy != SupplyProvider.Client)
                .Select(i => i.Item.Trim())
                .ToList();
            if (unassigned.Count > 0)
                parts.Add($"{ContractTextFormat.JoinWithAnd(unassigned)}: {RuledBlank}");

            return string.Join("; ", parts);
        }

        /// <summary>"one (1) scheduled cleaning visit per calendar week".</summary>
        private static string FrequencyText(ScheduleSnapshot sc)
        {
            var visits = Math.Max(1, sc.VisitsPerPeriod);
            var noun = visits == 1 ? "scheduled cleaning visit" : "scheduled cleaning visits";
            return $"{ContractTextFormat.WordsWithDigits(visits)} {noun} per {sc.FrequencyUnit}";
        }

        private static string Capitalize(string value) =>
            string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value.Substring(1);

        /// <summary>First non-blank of the two. Used where a field falls back to a Party's own record.</summary>
        private static string? Coalesce(string? preferred, string? fallback) =>
            string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

        /// <summary>
        /// Exhibit B4's "Client notice email". See <see cref="OperationalContactsSnapshot.ClientNoticeEmail"/>.
        ///
        /// A snapshot written before the field existed (null) renders the client record's notice
        /// email and nothing else - exactly what it printed when it was generated, so a signed
        /// version re-rendered for its executed PDF says what was signed. Otherwise the field's own
        /// value wins, then the client record, then the approval email, then the operational one.
        /// </summary>
        internal static string? ResolveClientNoticeEmail(OperationalContactsSnapshot? contacts, ClientSnapshot client)
        {
            if (contacts?.ClientNoticeEmail is null) return client.NoticeEmail;

            return new[]
                {
                    contacts.ClientNoticeEmail, client.NoticeEmail,
                    contacts.ClientApprovalEmail, contacts.ClientOperationalEmail
                }
                .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))?.Trim();
        }

        /// <summary>
        /// "Nodar Alania, CEO" - the authorized-representative line in Exhibit B4.
        ///
        /// Collapses to the bare name when no title was recorded, rather than leaving a dangling
        /// comma. A title is an assertion of authority printed in a legal document, so an absent
        /// one is left absent rather than guessed at.
        /// </summary>
        private static string NameAndTitle(SignerSnapshot? signer)
        {
            var name = signer?.FullName?.Trim() ?? string.Empty;
            var title = signer?.Title?.Trim() ?? string.Empty;
            if (name.Length == 0) return string.Empty;
            return title.Length == 0 ? name : $"{name}, {title}";
        }

        /// <summary>"Nodar Alania, (929) 930-1525" - an on-call contact line.</summary>
        private static string NameAndPhone(string? name, string? phone)
        {
            var n = name?.Trim() ?? string.Empty;
            var p = ContractTextFormat.Phone(phone);
            if (n.Length == 0) return p;
            return p.Length == 0 ? n : $"{n}, {p}";
        }

        /// <summary>Contract-style long date, or null when unset so the caller can rule a blank.</summary>
        private static string? DateOrNull(DateTime? value) =>
            value.HasValue ? ContractTextFormat.LongDate(value) : null;

        /// <summary>
        /// "1569 Flatbush Ave., Brooklyn, NY 11210" - one address line, with each part written
        /// EXACTLY ONCE.
        ///
        /// THE STREET FIELD ROUTINELY ALREADY CARRIES THE CITY AND STATE. A service location is
        /// typed by a person, pasted out of a listing or filled from an autocomplete, so
        /// "1569 Flatbush Ave., Brooklyn, NY" in the street box is ordinary rather than a mistake
        /// - and appending the structured city and state to it produced
        /// "1569 Flatbush Ave., Brooklyn, NY, Brooklyn, NY 11210" in Section 1(b), in Exhibit A's
        /// SERVICE PREMISES line, and in the preamble's contractor address. Printed in an
        /// executed agreement it reads as a broken document, on the line identifying where the
        /// work happens.
        ///
        /// So the street is split on its commas and any TRAILING component that merely restates
        /// the structured city, state or ZIP is dropped before the structured parts are appended.
        /// Only a trailing component qualifies, which is what keeps the rule safe: "Brooklyn
        /// Bridge Blvd" in Brooklyn is one component that is not the word "Brooklyn", and a
        /// "Floor 3" or "Apt 2B" component is never mistaken for a city. Matching ignores case
        /// and punctuation so "NY", "ny" and "N.Y." are the same state, and a component that
        /// carries the pair ("NY 11210", "Brooklyn, NY") is recognised as well.
        ///
        /// The structured columns win because they are the fields the rest of the system reads -
        /// nothing here rewrites them from the free-text box.
        /// </summary>
        private static string JoinAddress(string? street, string? city, string? state, string? zip)
        {
            var sb = new StringBuilder();

            foreach (var component in StripRepeatedTail(street, city, state, zip))
                sb.Append(sb.Length > 0 ? ", " : string.Empty).Append(component);

            if (!string.IsNullOrWhiteSpace(city)) sb.Append(sb.Length > 0 ? ", " : "").Append(city.Trim());
            if (!string.IsNullOrWhiteSpace(state)) sb.Append(sb.Length > 0 ? ", " : "").Append(state.Trim());
            if (!string.IsNullOrWhiteSpace(zip)) sb.Append(sb.Length > 0 ? " " : "").Append(zip.Trim());
            return sb.ToString();
        }

        /// <summary>
        /// The same one-line address the DOCUMENT prints, for the admin list and the customer
        /// portal to label a service location with.
        ///
        /// Exposed rather than copied: those labels are read beside the contract they belong to,
        /// and a list row saying "1569 Flatbush Ave., Brooklyn, NY, Brooklyn, NY 11210" next to a
        /// PDF that says it once is the same defect in a second place. See
        /// <see cref="JoinAddress"/> for why the duplication happens at all.
        /// </summary>
        public static string ComposeAddress(string? street, string? city, string? state, string? zip) =>
            JoinAddress(street, city, state, zip);

        /// <summary>
        /// The comma-separated components of a street line, with any trailing ones that repeat the
        /// structured city / state / ZIP removed. Repeated until nothing more matches, so
        /// "1569 Flatbush Ave., Brooklyn, NY 11210" comes back as just "1569 Flatbush Ave.".
        /// </summary>
        private static List<string> StripRepeatedTail(string? street, string? city, string? state, string? zip)
        {
            var components = (street ?? string.Empty)
                .Split(',')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            // Every spelling of the tail this line could already be carrying. Compared on letters
            // and digits alone, so "NY 11210" and "NY, 11210" collapse to the same key.
            var repeats = new[]
            {
                zip, state, city,
                $"{state} {zip}", $"{city} {state}", $"{city} {state} {zip}"
            }
            .Select(Alphanumeric)
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

            while (components.Count > 0 && repeats.Contains(Alphanumeric(components[^1])))
                components.RemoveAt(components.Count - 1);

            return components;
        }

        /// <summary>Letters and digits only, upper-cased: the comparison key for an address part.</summary>
        private static string Alphanumeric(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
            return sb.ToString();
        }
    }
}
