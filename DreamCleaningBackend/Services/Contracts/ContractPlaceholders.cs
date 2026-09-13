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
            Put("CLIENT_ADDRESS", cl.PrincipalAddress);
            Put("CLIENT_FULL_ADDRESS", JoinAddress(cl.PrincipalAddress, cl.City, cl.State, cl.Zip));
            Put("CLIENT_CITY", cl.City);
            Put("CLIENT_STATE", cl.State);
            Put("CLIENT_ZIP", cl.Zip);
            Put("CLIENT_NOTICE_EMAIL", cl.NoticeEmail);
            Put("CLIENT_PHONE", ContractTextFormat.Phone(cl.Phone));

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
            Put("GOVERNING_LAW_STATE", t.GoverningLawState);
            Put("VENUE_COUNTY", t.VenueCounty);

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
            Put("TIMELY_RESCHEDULE_HOURS", ContractTextFormat.WordsWithDigits(a.TimelyRescheduleHours));
            Put("CURE_PERIOD_DAYS", ContractTextFormat.WordsWithDigits(a.CurePeriodDays));
            Put("PAST_DUE_DAYS", ContractTextFormat.WordsWithDigits(a.PastDueDays));
            Put("BILLING_DISPUTE_DAYS", ContractTextFormat.WordsWithDigits(a.BillingDisputeDays));
            Put("QUALITY_COMPLAINT_HOURS", ContractTextFormat.WordsWithDigits(a.QualityComplaintHours));
            Put("VISIBLE_DAMAGE_HOURS", ContractTextFormat.WordsWithDigits(a.VisibleDamageHours));
            Put("LATENT_DAMAGE_DAYS", ContractTextFormat.WordsWithDigits(a.LatentDamageDays));
            Put("CONFIDENTIALITY_YEARS", ContractTextFormat.WordsWithDigits(a.ConfidentialityYears));
            Put("NON_SOLICIT_MONTHS", ContractTextFormat.WordsWithDigits(a.NonSolicitMonths));
            Put("NON_HIRE_DAMAGES", ContractTextFormat.Money(a.NonHireDamages));
            Put("INSURANCE_PER_OCCURRENCE", ContractTextFormat.Money(a.InsurancePerOccurrence));
            Put("INSURANCE_AGGREGATE", ContractTextFormat.Money(a.InsuranceAggregate));
            Put("INSURANCE_JURISDICTION", a.InsuranceJurisdiction);
            Put("LIABILITY_CAP_MONTHS", ContractTextFormat.WordsWithDigits(a.LiabilityCapLookbackMonths));
            Put("DISPUTE_DISCUSSION_DAYS", ContractTextFormat.WordsWithDigits(a.DisputeDiscussionDays));
            Put("CREDIT_RETURN_DAYS", ContractTextFormat.WordsWithDigits(a.CreditReturnDays));
            Put("MEDIATION_VENUE", a.MediationVenue);

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
