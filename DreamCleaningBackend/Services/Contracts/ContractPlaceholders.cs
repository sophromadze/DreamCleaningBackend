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

            // ── Dates / identity ───────────────────────────────────────────────
            Put("EFFECTIVE_DATE", ContractTextFormat.LongDate(s.EffectiveDate));
            Put("CONTRACT_NUMBER", s.ContractNumber);
            Put("CONTRACT_VERSION", s.VersionNumber.ToString());

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
            Put("CLIENT_NOTICE_EMAIL", cl.NoticeEmail);
            Put("CLIENT_PHONE", ContractTextFormat.Phone(cl.Phone));

            // Where FORMAL notice is served (the preamble and Section 32 / Exhibit B4).
            //
            // Falls back to the principal address rather than blanking, because the two are the
            // same for most clients and leaving the PREAMBLE of the agreement a ruled blank would
            // look like a drafting error on the first line a counterparty reads. A client that
            // reads its mail somewhere else overrides it on the form.
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
            // Every one of these is a blank the Parties fill in. An unanswered one renders as a
            // ruled blank so it is visibly unanswered, rather than as "None" - which would assert
            // that the premises has no employee restroom.
            var sd = s.SiteDetails ?? new SiteDetailsSnapshot();
            PutBlank("SQUARE_FOOTAGE", sd.ApproximateSquareFootage);
            PutBlank("CUSTOMER_RESTROOM_COUNTS", sd.CustomerRestroomCounts);
            PutBlank("EMPLOYEE_RESTROOM_COUNTS", sd.EmployeeRestroomCounts);
            PutBlank("FLOOR_MATERIALS", sd.FloorMaterials);
            PutBlank("KITCHEN_EQUIPMENT_SURFACES", sd.KitchenEquipmentAndSurfaces);
            PutBlank("TOUCHPOINT_LOCATIONS", sd.TouchpointLocations);
            PutBlank("INTERIOR_GLASS_LOCATIONS", sd.InteriorGlassLocations);
            PutBlank("ACCESS_METHOD_REFERENCE", sd.AccessMethodReference);
            PutBlank("EQUIPMENT_RESTRICTIONS", sd.EquipmentRestrictions);
            PutBlank("WASTE_RECEPTACLE_LOCATIONS", sd.WasteReceptacleLocations);
            PutBlank("FOOD_PERMIT_HOLDER", sd.FoodServicePermitHolder);
            PutBlank("BASELINE_WALKTHROUGH", sd.BaselineWalkthroughRecord);

            // These three the drafted agreement writes as "[... OR NONE]": an empty answer is a
            // term of the deal. Food-contact sanitizing especially - Section 25(e) and A3(d)
            // exclude it unless a task is expressly identified here, so "None" is the agreement
            // saying Client keeps that responsibility.
            PutOrNone("FOOD_CONTACT_SANITIZING", sd.FoodContactSanitizing);
            PutOrNone("SITE_REQUIREMENTS", sd.SiteRequirements);
            PutOrNone("INITIAL_WORK_CHANGE_ORDER", sd.InitialWorkChangeOrder);

            // ── Exhibit B3: insurance endorsements ─────────────────────────────
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
            PutBlank("CONTRACTOR_BACKUP_CONTACT", contacts.ContractorBackupContact);
            PutBlank("CLIENT_ON_CALL_CONTACT", NameAndPhone(
                contacts.ClientOnCallName,
                Coalesce(contacts.ClientOnCallPhone, cl.Phone)));
            PutBlank("CLIENT_BACKUP_CONTACT", contacts.ClientBackupContact);

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

        private static string JoinAddress(string? street, string? city, string? state, string? zip)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(street)) sb.Append(street.Trim());
            if (!string.IsNullOrWhiteSpace(city)) sb.Append(sb.Length > 0 ? ", " : "").Append(city.Trim());
            if (!string.IsNullOrWhiteSpace(state)) sb.Append(sb.Length > 0 ? ", " : "").Append(state.Trim());
            if (!string.IsNullOrWhiteSpace(zip)) sb.Append(sb.Length > 0 ? " " : "").Append(zip.Trim());
            return sb.ToString();
        }
    }
}
