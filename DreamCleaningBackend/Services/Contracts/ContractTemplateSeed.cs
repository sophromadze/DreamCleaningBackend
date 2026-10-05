namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// The seeded master agreement (v2.0, the attorney-drafted Master Service Agreement).
    ///
    /// The legal sentences are the drafted document UNCHANGED - only client-, location-, price-,
    /// schedule-, site- and term-specific values have been replaced by {{TOKENS}}, and structural
    /// line prefixes added so one body renders to both HTML and PDF. Do not rewrite or "improve"
    /// the prose: it was drafted to be enforceable, and an edit that reads better may not be.
    ///
    /// NOTHING BUSINESS-SPECIFIC IS HARDCODED. Not the client's legal name, not the brand of the
    /// premises, not an address, not an email, not a phone number, not a price, not a square
    /// footage. Every one of those is a token filled from the contract form and frozen into the
    /// version's snapshot. <c>ContractRenderingTests</c> renders this template for an unrelated
    /// client and asserts that no value from the reference agreement survives, because a missed
    /// hardcode stays correct for the one client it came from and is silently wrong for every
    /// client after.
    ///
    /// Line prefixes (stripped at render time):
    ///   #    document title
    ///   ##   numbered section / exhibit heading
    ///   ###  sub-heading inside an exhibit or the signature block
    ///   -    bullet
    ///   |    two-column exhibit row, written |Item|Terms
    ///   @    directive: @SIGNATURE_BLOCK marks where the executed marks are drawn
    ///   a blank line separates paragraphs
    ///
    /// Initials are deliberately absent everywhere - signing happens once, in the signature block.
    /// </summary>
    public static class ContractTemplateSeed
    {
        public const string TemplateName = "Commercial Cleaning Master Service Agreement";

        /// <summary>
        /// 3.3 - Section 17(e), Background Checks (2026-10-01). One sentence appended after 17(d):
        /// "All personnel with access to the Premises must have passed a background check before
        /// being assigned to the site." 17(a)-(d) and every other section are word-for-word 3.1, and
        /// no main section is renumbered. A new version rather than an edit in place, so only
        /// contracts generated from now on carry it: every stored version renders its own frozen
        /// body. WHY 3.3 AND NOT 3.2: Sweep It Real's seed already uses 3.2 (see the 3.0 note).
        ///
        /// 3.1 - a Detailed Exhibit A says only what was SELECTED (2026-10-01).
        ///
        /// A5 said "Routine restroom cleaning under ordinary sanitary conditions remains included"
        /// and 24(a) "Routine toilet and restroom cleaning ... remains included" whatever the restroom
        /// checklist held, so a client who bought three restroom tasks was promised toilets, mirrors
        /// and floors too. Both are now bounded by the Scope of Work. A7 (interior glass) printed
        /// its boilerplate with no glass selected. Every optional subsection is now inside an @IF:
        /// A2/A3/A4/A5 on their own scope group having a selected item (IF_SCOPE_ANY:key, resolved
        /// by ContractRenderer), A6/A7 on a selected included item naming trash/waste/recycling or
        /// glass/windows (ContractPlaceholders). Headings are written "A#n" and the renderer numbers
        /// them in the order they actually appear, remapping "in A4"-style references to match - so
        /// no gap (the old A8 -> A10 came from a hard-coded "A10. ADDITIONAL SCOPE"). 25(e) now
        /// excludes dishwashing only where it is not expressly included, and A3(d) no longer
        /// excludes a dishwashing task listed in A3(a).
        ///
        /// 3.0 - a WEEKLY FLAT FEE pricing basis (2026-09-30). A client may pay one fee per calendar
        /// week for all of that week's scheduled visits ($875 for six visits) instead of a fee per
        /// visit. 6(a), 7(b), 11(a) and the Exhibit B2 price, tax, total, deadline and cap rows each
        /// exist twice, guarded by {{IF_PER_VISIT_PRICING}} / {{IF_WEEKLY_FLAT_PRICING}}; the
        /// per-visit lines are word-for-word what 2.8 printed. In weekly mode the per-visit caps
        /// (Sections 14, 15, 24, 29) use one visit's ALLOCATED share of the weekly fee, and 6(a) and
        /// B2 say in one sentence that the allocation is for calculation only - the agreed price is
        /// the week, and "$145.83 per visit" appears nowhere as a price.
        ///
        /// WHY 3.0 AND NOT 2.9: Sweep It Real's seed is at 2.9 with a different body, and the two
        /// apps have shared a development database under the same template Name. A version number
        /// neither app has used cannot be adopted by the other.
        ///
        /// 2.8 - the v2.7 wording actually reaching the database, plus a shorter document
        /// (2026-09-30).
        ///
        /// WHY 2.7 NEVER RENDERED - TWO INDEPENDENT REASONS.
        ///   1. In development, the "2.7" row was NOT this file's. Sweep It Real (a fork with the
        ///      same template Name) shared the dev database and seeded ITS 2.7 there on 2026-09-26 -
        ///      the Ohio-move body, with no @IF blocks, no *_IF_SET tokens, no B3 and the A8 soap
        ///      sentence. The seeder matches on Name and Version, so it adopted that row and only
        ///      logged that the body differed. Every draft copied Sweep It Real's Ohio wording:
        ///      Simplified scope printed the full Exhibit A, "no commitment" printed "zero (0)
        ///      months", and the banner demanded site details nobody needed. The form, the DTO and
        ///      the snapshot were all correct; the body had no tokens to read them with.
        ///   2. Anywhere the row did NOT exist, 2.7 could not be inserted: its description was 539
        ///      characters against a 500-character column, Program.cs logged the failure and
        ///      started anyway, and 2.6 stayed the default.
        /// 2.8 is a fresh number with a description that fits.
        ///
        /// What else changed, all of it wording the owner asked for:
        ///   - Simplified Exhibit A is three short sentences; Omitted 1(a) is one sentence.
        ///   - No commitment: 3(a) "begins on the Service Commencement Date and continues on a
        ///     month-to-month basis", 3(b) "No minimum service commitment applies.", and B1 has a
        ///     single Term-and-termination row. No Initial Term or End Date exists at all.
        ///   - The completion time is mentioned (5(c), 5(e), 9(b)) only when one was agreed:
        ///     {{IF_COMPLETION_TIME}} / {{IF_NO_COMPLETION_TIME}} / {{SCHEDULE_CHANGE_TERMS}}.
        ///   - 16(c) and 35(f) stop naming Exhibit A / "A" references when it has no detail.
        ///   - Section 36 is a one-sentence-per-subject summary; every figure is still the token the
        ///     operative clause reads, and 36(a)'s deference and 36(o)'s non-incorporation stay.
        ///   - Exhibit B drops rows that restated the MSA with no contract-specific value (Effective
        ///     Date, Tax adjustment, Reconciliation, Payment conditions) and shortens the rest to the
        ///     figure plus the section it comes from; Exhibit A's FREQUENCY / REGULAR DAY lines
        ///     point at B1 instead of repeating it; B3(b) keeps only the special-product rule
        ///     (label use and tool separation are Sections 6(b) and 26(b)).
        ///
        /// 2.7 - contract-specific commitment, supplies and guarantee (2026-09-30). Four owner's
        /// decisions, all of which the body used to hardcode:
        ///
        /// NO STANDARD MINIMUM COMMITMENT. Sections 3, 4, 36(h)/(i) and Exhibit B1 carry each
        /// commitment-dependent sentence twice, guarded by {{IF_MINIMUM_COMMITMENT}} and
        /// {{IF_NO_MINIMUM_COMMITMENT}} (see ContractPlaceholders): the guard that does not apply
        /// resolves to the OMIT sentinel and drops its line. A contract with no commitment
        /// therefore mentions no Minimum Commitment Period, End Date or Initial Term, and says
        /// plainly that none applies; one with a commitment reads exactly as 2.6 did. Termination
        /// notice defaults to 30 days (TermSnapshot) - the wording is token-driven and unchanged.
        ///
        /// SUPPLIES AND CONSUMABLES ARE ALLOCATED PER AGREEMENT. 6(b) is {{SUPPLIES_FEE_CLAUSE}},
        /// composed from who provides the equipment; 6(c) defers to an A8 allocation table with a
        /// row per consumable. "All-inclusive" is gone from the heading and 6(d), because it is
        /// untrue for a client who supplies its own products.
        ///
        /// THE GUARANTEE IS 24 HOURS, WITH A NARROW 72-HOUR EXCEPTION. 22(a) reports within
        /// {{QUALITY_COMPLAINT_HOURS}} of completion, or - only for a deficiency not reasonably
        /// discoverable sooner - no later than {{QUALITY_LATENT_LIMIT_HOURS}}; it excludes
        /// conditions caused after the visit by other people. 22(b) is correction of the affected
        /// area, never a re-clean of the premises; 22(c) says a deficiency is not a full refund.
        ///
        /// NO HAND SOAP, IN EITHER DIRECTION. 2.0-2.6 excluded it expressly in A8(a); the owner
        /// does not want the subject raised at all, so the sentence is removed rather than
        /// reworded, and the body mentions it nowhere.
        ///
        /// EXHIBIT A IS DETAILED, SIMPLIFIED OR OMITTED, per contract (ContractSnapshot.ScopeDetail).
        /// The exhibit sits inside "@IF IF_SCOPE_DETAILED" / "@IF IF_SCOPE_SIMPLIFIED" blocks that
        /// ContractRenderer drops whole, and every body reference to it goes through
        /// {{SCOPE_REF}} / {{EXHIBITS_LIST}} / {{EXHIBITS_INCORPORATED}} / {{EXHIBITS_SIGNED}}, so an
        /// omitted Exhibit A leaves no dangling reference. 1(d) keeps the scope protection in every
        /// mode: unagreed work is excluded and needs approval under Section 9.
        ///
        /// BLANK MEANS OMITTED. Every optional field in the exhibits reads a *_IF_SET token that
        /// drops its whole line when empty - no ruled blank, no "None", no "Not applicable". They
        /// are twins of the older tokens, which keep their old behaviour so executed contracts
        /// re-render exactly as signed. The supplies allocation moved from A8 to B3 so it survives
        /// an omitted Exhibit A; contacts are B4; agreed insurance endorsements beyond Section 20
        /// are B5, printed only when one exists; the completion time moved to B1.
        ///
        /// Also 24(a) now names unsafe equipment or working conditions, an aggressive or
        /// uncontrolled animal, and threatening or harassing conduct toward personnel among the
        /// conditions that let Contractor stop work - the published policy's section 9 lists them.
        ///
        /// 2.6 - Section 36 consolidates the cancellation, rescheduling and termination terms into
        /// one place, and records the published policy version the agreement was signed against
        /// (2026-09-16).
        ///
        /// WHY A NEW SECTION RATHER THAN EDITS TO SECTIONS 4, 5, 14 AND 15. Those sections already
        /// state every one of these terms operatively, spread across the document exactly where
        /// each belongs - a visit cancellation beside the scheduling clause, a termination notice
        /// beside the term. Restating a term in a second operative clause is how an agreement comes
        /// to contradict itself the first time one of the two is edited. So 36 is expressly a
        /// CONSOLIDATED REFERENCE that creates no right, charge, notice requirement or restriction
        /// of its own, and 36(a) says the operative sections control over any inconsistency with it.
        ///
        /// EVERY FIGURE IN IT IS THE SAME TOKEN THE OPERATIVE CLAUSE USES. {{CANCELLATION_PERCENT}}
        /// in 36(c) is the identical token Section 15(b) and Exhibit B read, so the summary cannot
        /// quote a different number from the clause it summarises - not on a contract where an
        /// admin has changed the percentage, and not on one drafted years from now.
        ///
        /// 36(o) RECORDS the published policy version; it does not INCORPORATE it. Incorporating a
        /// web page into an executed agreement would let a later edit to that page change what a
        /// client already signed, which is the precise thing Section 35(b) and the published policy
        /// both say cannot happen. {{POLICY_VERSION}} and {{POLICY_EFFECTIVE_DATE}} come off the
        /// frozen snapshot rather than off CommercialPolicyDocument's constants, for the same
        /// reason the template body itself is copied rather than referenced.
        ///
        /// The SIGNATURES paragraph moved from "Sections 1 through 35" to "1 through 36" with it -
        /// a signature block that does not name a section of the document it executes is an
        /// invitation to argue that section was not agreed.
        ///
        /// 2.5 - the preamble now says WHERE the Services are performed. It identified Client by
        /// legal entity alone, so the only address on the first page was the CONTRACTOR's
        /// principal office; the reader had to reach Section 1(b) to learn which building the
        /// agreement is about (2026-09-16).
        ///
        /// IT IS THE SERVICE ADDRESS, AND IT IS WORDED AS ONE. "with Services to be performed at"
        /// - never "principal office", "registered office", "legal address" or "business mailing
        /// address", each of which asserts something about Client that the service location does
        /// not establish. A company registered in Delaware, reading its post at an accountant's
        /// office and operating a restaurant in Brooklyn is the ordinary case, which is why
        /// ServiceLocation is its own entity and why Section 1(b) says outright that the Premises
        /// address "is not necessarily Client's legal or principal business address".
        ///
        /// This is NOT the v2.1 mailing address coming back. That field was optional, so requiring
        /// it in the preamble printed a ruled blank on the first line a counterparty reads - see
        /// the v2.2 note below. The service location is required to save a contract at all
        /// (ContractService.ResolveServiceLocationAsync refuses without one, and every part of it
        /// is [Required] on SaveContractServiceLocationDto), so {{SERVICE_FULL_ADDRESS}} cannot
        /// come back empty here and needs no collapsing form.
        ///
        /// SECTION 1(b) AND EXHIBIT A ARE UNCHANGED. The repetition is deliberate: the preamble
        /// identifies the deal, Section 1(b) and Exhibit A define the Premises, and all three read
        /// the one {{SERVICE_FULL_ADDRESS}} token, so they cannot disagree.
        ///
        /// 2.4 - A2 no longer names example rooms. The sentence read "An area expressly identified
        /// as an Included Area in A1, SUCH AS THE OFFICE, the employee restroom or hallways,
        /// remains included..."; it now stops after "in A1" (2026-09-16).
        ///
        /// WHY THE EXAMPLES HAD TO GO. Included Areas is a checklist an admin ticks per contract,
        /// so a room named in the fixed prose can contradict it: an agreement whose A1 list omits
        /// the office, because the client has none, still told the reader the office was included.
        /// Naming a different room instead would only move the same bug, so the sentence names no
        /// room at all and the A1 list stays the single authority on what is in scope.
        ///
        /// A NEW ROW AGAIN, for the reason every bump since 2.1 has been: the sentence lives in
        /// BodyText, and the seeder matches on Name AND Version, so leaving the number at 2.3
        /// would have left every existing database printing the old examples while this file said
        /// otherwise. The matching Exhibit A change is NOT here - the "Hallways, office and doors"
        /// task label is scope DATA in ContractScopeTemplateSeed, repaired in place by
        /// ContractSeedService for the same insert-never-rewrite reason.
        ///
        /// THE SEEDER INSERTS ONLY WHAT IS MISSING, matched on Name AND Version, so once a row
        /// exists at a version no later edit to <c>BodyText</c> can ever reach it. A 2.0 seeded
        /// from the pre-correction body kept promising hand soap, and kept printing the signature
        /// block above both exhibits, on every contract drafted from it - with the seed file in
        /// front of you saying otherwise. Editing the body without moving the version is the
        /// failure this constant exists to prevent; <c>ContractSeedService</c> now also logs
        /// loudly when a stored body has drifted from the seed at the same version.
        ///
        /// 1.0/1.1 were the pre-review wording, 2.0 is the soap draft, 2.1 is the mailing-address
        /// draft, 2.2 obliged a backup contact, 2.3 named example rooms in A2, 2.4 left the
        /// premises out of the preamble entirely and 2.5/2.6 hardcoded a minimum commitment, one
        /// supplies arrangement and a 48-hour guarantee window. None of them stay in the picker: superseded
        /// legal text left selectable is an invitation to issue it by accident. The seeder retires them rather than deleting them, so a version that did
        /// render still resolves its frozen body - which is also why
        /// <c>{{CLIENT_NOTICE_MAILING_ADDRESS}}</c> is still mapped in <c>ContractPlaceholders</c>
        /// even though no current body references it.
        /// </summary>
        public const string TemplateVersion = "3.3";

        /// <summary>
        /// Must fit <c>ContractTemplate.Description</c> (500). The 2.7 text was 539 characters: on
        /// MariaDB the insert failed, <c>Program.cs</c> logged the exception and started anyway, and
        /// the previous version stayed the default - a second way for new wording to never arrive.
        /// The in-memory test database does not enforce lengths, so
        /// <c>ContractGeneratedDocumentRegressionTests</c> checks it against the attribute.
        /// </summary>
        public const string TemplateDescription =
            "Attorney-drafted commercial MSA: Sections 1-36, an optional Exhibit A (detailed, "
            + "simplified or omitted scope), Exhibit B (schedule, pricing, supplies allocation, "
            + "contacts and any agreed insurance endorsements), then the signatures. A minimum "
            + "commitment and who provides supplies are set per contract; 30 days' notice by "
            + "default; blank optional fields are left out of the document.";

        /// <summary>
        /// Seeded versions this template supersedes. <c>ContractSeedService</c> deactivates them
        /// on startup so they leave the picker; nothing is deleted, because a template row is
        /// still what an audit trail points at even when no version was ever generated from it.
        /// </summary>
        public static IReadOnlyList<string> SupersededVersions => new[] { "1.0", "1.1", "2.0", "2.1", "2.2", "2.3", "2.4", "2.5", "2.6", "2.7", "2.8", "3.0", "3.1" };

        public const string BodyText = """
# MASTER SERVICE AGREEMENT
# COMMERCIAL CLEANING SERVICES

This Master Service Agreement (the "Agreement") is entered into as of {{EFFECTIVE_DATE}} (the "Effective Date") by and between {{CONTRACTOR_LEGAL_NAME}}, {{CONTRACTOR_ENTITY_TYPE}} doing business as {{CONTRACTOR_DBA}}, with its principal office at {{CONTRACTOR_ADDRESS}} ("Contractor"), and {{CLIENT_LEGAL_NAME}}, {{CLIENT_ENTITY_DESCRIPTION}}, with Services to be performed at {{SERVICE_FULL_ADDRESS}} ("Client"). Contractor and Client are each a "Party" and together the "Parties."

Each Party represents that it is duly organized and authorized to enter into this Agreement, and each individual signing on behalf of a Party represents that the individual has authority to bind that Party. Client represents that it has authority to engage Contractor and grant access for the Services at the Premises. Neither the use of a brand name nor identification of the Premises makes any franchisor, landlord, affiliate, or individual signatory a party or guarantor.

## 1. SERVICES AND PREMISES
(a) Contractor shall provide commercial cleaning services (the "Services") at the location identified in Exhibit A (the "Premises"), in accordance with the scope, tasks and frequencies set out in Exhibit A.{{IF_SCOPE_DETAILED}}
(a) Contractor shall provide the commercial cleaning services mutually agreed by the Parties (the "Services" or the "Scope of Work") at the premises described in paragraph (b) (the "Premises"). The Scope of Work is recorded in Exhibit A.{{IF_SCOPE_SIMPLIFIED}}
(a) Contractor shall provide the commercial cleaning services mutually agreed by the Parties (the "Services" or the "Scope of Work") at the premises described in paragraph (b) (the "Premises").{{IF_SCOPE_OMITTED}}
(b) The Premises are the {{PREMISES_DESCRIPTION}} operated by Client and located at {{SERVICE_FULL_ADDRESS}}. The Premises address is the service location only and is not necessarily Client's legal or principal business address.
(c) {{EXHIBITS_INCORPORATED}} of this Agreement.
(d) Services not expressly described in Exhibit A are not included and are governed by Section 9.{{IF_SCOPE_DETAILED}}
(d) Services not expressly described in {{SCOPE_REF}} are not included. Materially additional services, and work outside the Scope of Work, require separate approval, and a material change to the Scope of Work requires a Change Order or written confirmation under Section 9.{{IF_SCOPE_NOT_DETAILED}}
(e) Contractor shall perform the expressly included Services with reasonable care and skill consistent with ordinary professional commercial cleaning. Contractor does not undertake continuous inspection, comprehensive premises management, or responsibility for conditions arising between visits or for excluded work. Client retains responsibility for operating and maintaining the Premises, including arranging cleaning and sanitation at other times. This allocation does not excuse Contractor's negligent performance or any duty imposed by applicable law.

## 2. ORDER OF PRECEDENCE
The documents forming this Agreement are this Agreement, {{EXHIBITS_LIST}}, and amendments or Change Orders signed by authorized representatives of both Parties. A later signed amendment or Change Order controls only the subjects it expressly changes. Otherwise, this Agreement controls legal terms, Exhibit B controls the expressly stated price and billing details, and {{SCOPE_REF}} controls the specifically included tasks and areas. No proposal, website, advertising material, purchase order, invoice boilerplate, vendor portal term, or other document changes this Agreement unless expressly incorporated by a signed amendment. No document authorizes unlawful work or overrides a mandatory legal requirement. Scheduling confirmations under Section 5 govern only the agreed schedule and do not amend other terms.

## 3. TERM, MINIMUM COMMITMENT PERIOD AND RENEWAL{{IF_MINIMUM_COMMITMENT}}
## 3. TERM AND RENEWAL{{IF_NO_MINIMUM_COMMITMENT}}
(a) This Agreement is effective on the Effective Date. Recurring Services commence on the Service Commencement Date stated in Exhibit B. The Initial Term runs for {{INITIAL_TERM_MONTHS}} months from the Service Commencement Date, subject to earlier termination under Section 4.{{IF_MINIMUM_COMMITMENT}}
(a) This Agreement is effective on the Effective Date. It begins on the Service Commencement Date stated in Exhibit B and continues on a {{RENEWAL_TYPE}} basis until terminated in accordance with Section 4.{{IF_NO_MINIMUM_COMMITMENT}}
(b) The Minimum Commitment Period begins on the Service Commencement Date and ends immediately before the Minimum Commitment End Date stated in Exhibit B, {{MINIMUM_COMMITMENT_MONTHS}} calendar months later. That End Date is the first date on which termination for convenience may take effect. The Minimum Commitment Period restricts termination for convenience only and does not restrict termination for cause, lawful safety measures, or termination under Section 30.{{IF_MINIMUM_COMMITMENT}}
(b) No minimum service commitment applies.{{IF_NO_MINIMUM_COMMITMENT}}
(c) The recurring contracted service frequency is {{SERVICE_FREQUENCY_TEXT}}, as described in {{EXHIBITS_LIST}}. That obligation applies throughout the service term, including the Minimum Commitment Period, the remainder of the Initial Term and any {{RENEWAL_TYPE}} continuation, subject to the express adjustments and remedies in Sections 11, 14, 15, 24 and 30.{{IF_MINIMUM_COMMITMENT}}
(c) The recurring contracted service frequency is {{SERVICE_FREQUENCY_TEXT}}, as described in {{EXHIBITS_LIST}}. That obligation applies throughout the service term, subject to the express adjustments and remedies in Sections 11, 14, 15, 24 and 30.{{IF_NO_MINIMUM_COMMITMENT}}
(d) Upon expiration of the Initial Term, this Agreement continues automatically on a {{RENEWAL_TYPE}} basis on the same terms and does not renew for a further fixed term. Either Party may terminate that continuation on at least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice under Section 4. "{{RENEWAL_TYPE_CAP}}" means successive periods of one calendar month, subject to earlier termination under this Agreement.{{IF_MINIMUM_COMMITMENT}}
(d) Either Party may terminate ongoing Services with at least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice under Section 4. "{{RENEWAL_TYPE_CAP}}" means successive periods of one calendar month, subject to earlier termination under this Agreement.{{IF_NO_MINIMUM_COMMITMENT}}

## 4. TERMINATION
(a) Either Party may terminate for convenience on at least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice. Notice may be delivered during the Minimum Commitment Period, but termination for convenience shall not take effect before the Minimum Commitment End Date. Services and payment obligations continue until the effective termination date, subject to the express cancellation, suspension, safety, and force-majeure provisions of this Agreement.{{IF_MINIMUM_COMMITMENT}}
(a) Either Party may terminate ongoing Services for convenience with at least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice. Services and payment obligations continue until the effective termination date, subject to the express cancellation, suspension, safety, and force-majeure provisions of this Agreement.{{IF_NO_MINIMUM_COMMITMENT}}
(b) Either Party may terminate this Agreement at any time, including during the Minimum Commitment Period, if the other Party materially breaches this Agreement and fails to cure the breach within {{CURE_PERIOD_DAYS}} calendar days after receiving written notice describing it.{{IF_MINIMUM_COMMITMENT}}
(b) Either Party may terminate this Agreement at any time if the other Party materially breaches this Agreement and fails to cure the breach within {{CURE_PERIOD_DAYS}} calendar days after receiving written notice describing it.{{IF_NO_MINIMUM_COMMITMENT}}
(c) Contractor may suspend Services as provided in Sections 11 and 24. Contractor may terminate for nonpayment of an undisputed amount after that amount remains unpaid for {{PAST_DUE_DAYS}} calendar days following written demand identifying the amount due. Either Party may terminate immediately by written notice if continued performance would be unlawful or expose persons to an imminent serious danger that cannot reasonably be eliminated through suspension or other protective measures. Other remediable safety-related breaches remain subject to paragraph (b).
(d) Upon termination, Client shall pay earned fees for Services performed and properly supported charges accrued under this Agreement. Contractor shall provide a final itemized statement and refund unearned prepayments and unapplied credits, after applying undisputed amounts due, within {{CREDIT_RETURN_DAYS}} calendar days. A disputed deduction shall be identified and supported; any undisputed refund shall not be delayed pending resolution of the dispute. No remaining annual fees are automatically accelerated. For wrongful termination or repudiation, the nonbreaching Party retains a claim for proven direct damages, subject to mitigation, Section 29, and no duplicate recovery. Contractor's claim for unperformed future visits shall be limited to its proven net loss through the earliest date on which Client could have validly terminated for convenience had notice been given on the breach date. Accrued payment and refund duties, confidentiality, claims procedures, lawful indemnity, liability limitations, governing law, dispute resolution, and provisions needed to enforce those rights survive to the extent their nature requires.

## 5. SCHEDULING AND SERVICE WINDOW
(a) Contractor shall perform {{SERVICE_FREQUENCY_TEXT}} during the service term, subject to Sections 11, 14, 15, 24 and 30. A {{SERVICE_PERIOD}} is {{WEEK_DEFINITION}}. A makeup visit satisfies the commitment for the {{SERVICE_PERIOD}} in which the original visit was scheduled and does not replace another {{SERVICE_PERIOD}}'s visit unless expressly agreed. Contractor shall maintain a service ledger identifying the original {{SERVICE_PERIOD}} and the status of each visit, makeup, charge and credit.
(b) The regular service {{SERVICE_DAY_NOUN}} {{SERVICE_DAY_VERB}} {{SERVICE_DAYS}}, subject to a mutually confirmed change under paragraph (c).
(c) Unless the Parties confirm a different window in writing, Contractor's arrival window is {{ARRIVAL_WINDOW}}, {{SERVICE_TIMEZONE}}. The required completion time is stated in Exhibit B. Changes to the day, arrival window, or completion deadline require mutual written confirmation and reasonable availability. Contractor shall promptly notify Client of a material delay. Failed-access charges do not apply when Contractor arrives outside the agreed window unless Client has accepted that revised arrival time.{{IF_COMPLETION_TIME}}
(c) Unless the Parties confirm a different window in writing, Contractor's arrival window is {{ARRIVAL_WINDOW}}, {{SERVICE_TIMEZONE}}. Changes to the day or arrival window require mutual written confirmation and reasonable availability. Contractor shall promptly notify Client of a material delay. Failed-access charges do not apply when Contractor arrives outside the agreed window unless Client has accepted that revised arrival time.{{IF_NO_COMPLETION_TIME}}
(d) Scheduling changes under this Section may be confirmed in writing, including by email, and do not constitute an amendment to substantive terms or a Change Order under Section 9. A request alone does not change the agreed schedule; both Parties must confirm the change.
(e) A change to the service {{SCHEDULE_CHANGE_TERMS}} does not change the commitment of {{SERVICE_FREQUENCY_TEXT}}, subject to Sections 14 and 15.
(f) {{CLOSED_PREMISES_TEXT}} Client shall provide the key or other access necessary to enter the Premises in accordance with Section 13.

## 6. FEES, PRICING AND SUPPLIES
(a) Client shall pay the fees in Exhibit B. The recurring service fee is {{PRE_TAX_PRICE}} before tax per completed scheduled visit. Applicable sales tax is added under Section 7. Charges for missed, interrupted or deficient visits are governed by the specific provisions of this Agreement.{{IF_PER_VISIT_PRICING}}
(a) Client shall pay the fees in Exhibit B. The recurring service fee is {{PRE_TAX_PRICE}} before tax per calendar week for the agreed recurring service schedule of {{SERVICE_FREQUENCY_TEXT}}. Applicable sales tax is added under Section 7. Where this Agreement refers to the fee for one visit, including in Sections 14, 15, 24 and 29, that amount is the weekly pre-tax fee divided by the number of visits scheduled in that week; this allocation is for calculation only and does not change the weekly fee. Charges for missed, interrupted or deficient visits are governed by the specific provisions of this Agreement.{{IF_WEEKLY_FLAT_PRICING}}
(b) {{SUPPLIES_FEE_CLAUSE}}
(c) Consumables, including trash bags and liners, paper towels and toilet tissue, are allocated between the Parties in Exhibit B, B3. Each Party shall supply, at its own cost, and maintain adequate stocks of the consumables allocated to it. If stocks of a Client-allocated consumable are insufficient, Contractor shall notify Client; Contractor is not required to purchase substitutes unless Client approves the purchase and price in writing.
(d) The recurring fee does not mean unlimited services. The fee covers only the tasks and frequencies in {{SCOPE_REF}}. Deep cleaning and restoration-level cleaning are not included unless expressly stated in {{SCOPE_REF}} or added by a signed Change Order under Section 9.

## 7. TAXES
(a) Applicable sales, use and similar transaction taxes shall be calculated on taxable charges, added to amounts due and separately stated on each invoice as required by law.
(b) Exhibit B shows the completed-visit calculation at {{SALES_TAX_RATE}} sales tax: {{PRE_TAX_PRICE}} before tax, {{SALES_TAX_AMOUNT}} sales tax and {{TOTAL_PRICE}} total. If the legally applicable rate changes, the pre-tax fee remains unchanged, and the tax and resulting total adjust as required by law.{{IF_PER_VISIT_PRICING}}
(b) Exhibit B shows the weekly calculation at {{SALES_TAX_RATE}} sales tax: {{PRE_TAX_PRICE}} before tax, {{SALES_TAX_AMOUNT}} sales tax and {{TOTAL_PRICE}} total per calendar week. Tax is calculated on the weekly fee. If the legally applicable rate changes, the pre-tax fee remains unchanged, and the tax and resulting total adjust as required by law.{{IF_WEEKLY_FLAT_PRICING}}
(c) Client claiming a tax exemption shall provide a valid exemption certificate before the first invoice date. Taxes shall be handled in accordance with law. Client is responsible for a later tax assessment attributable to its invalid or expired exemption claim. Any later assessment and related amount charged to Client must be supported and attributable to that claim, rather than Contractor's independent error.
(d) References to a "service fee" in cancellation, failed-access, or damage calculations mean the pre-tax fee unless expressly stated otherwise. Tax shall be added to a particular charge only to the extent required by law. Contractor shall separately identify taxable services, cancellation or damage charges, credits, and any related tax adjustment. Contractor is responsible for its own income, payroll, and employment taxes and for timely remittance of sales tax it collects. Client shall not bear penalties attributable to Contractor's failure to remit tax properly collected from Client.

## 8. CHANGES TO PRICING
(a) A change to the recurring pre-tax service fee requires mutual written agreement documented by a signed amendment or signed Change Order. Neither Party may change that fee unilaterally.
(b) A legally required sales-tax rate change adjusts tax and the invoice total under Section 7 and does not change the agreed pre-tax fee.
(c) Contractor may request a prospective price review by providing at least {{PRICE_REVIEW_NOTICE_DAYS}} calendar days' written notice explaining a material change in agreed scope, mandatory labor costs, or other performance costs. No requested increase takes effect unless both Parties sign the revised price. If agreement is not reached, either Party may exercise its existing termination rights under Section 4; no retroactive increase applies.

## 9. CHANGE ORDERS AND WORK OUTSIDE SCOPE
(a) Any material change to the scope, recurring service frequency, Premises or substantive service requirements requires a written Change Order signed by authorized representatives of both Parties.
(b) Ordinary mutually agreed adjustments to the {{SCHEDULE_CHANGE_TERMS}} of an individual scheduled visit do not require a Change Order. Email confirmation is sufficient under Section 5(d).
(c) No verbal request, text message or informal employee instruction obligates Contractor to perform work outside {{SCOPE_REF}}. Occasional performance of an extra task does not by itself amend the recurring scope, subject to applicable law.
(d) A Change Order shall identify the additional or changed service, location, frequency, price, effective date and whether it is one-time or recurring, together with any changed safety, access or insurance requirements.
(e) A Change Order may be signed through an electronic-signature platform or by an exchange of emails from the authorized representatives identified in Exhibit B, each expressly approving the attached scope and price and including a typed signature intended to authenticate that approval. Routine scheduling messages, employee instructions, and purchase-order or vendor-portal boilerplate do not constitute a Change Order. Contractor may decline additional work until approval is complete.

## 10. RECURRING SERVICE COMMITMENT
Client engages Contractor for {{SERVICE_FREQUENCY_TEXT}} during the service term. A scheduling request does not by itself cancel that commitment. Exceptions, makeup visits, and charges for missed visits are governed exclusively by Sections 11, 14, 15, 24 and 30. Payment of a permitted missed-visit charge settles the charge for that particular visit and does not authorize a continuing reduction in service frequency. Repeated refusal to schedule or permit the agreed Services may be addressed as a material breach only in accordance with Sections 4 and 15. Contractor may not charge both the full price of an unperformed visit and a cancellation, failed-access, or suspension charge for the same visit.

## 11. INVOICING, ADVANCE PAYMENT AND CHARGES
(a) Contractor shall ordinarily issue the invoice at least {{INVOICE_LEAD_DAYS}} calendar days before each scheduled visit. Payment of undisputed amounts is due {{PAYMENT_DEADLINE_HOURS}} hours before the agreed arrival window begins. If an invoice is delivered fewer than {{LATE_INVOICE_THRESHOLD_DAYS}} calendar days before that deadline, Client shall have at least {{LATE_INVOICE_GRACE_BUSINESS_DAYS}} business days after receipt to pay, and the Parties shall agree whether to proceed or reschedule without a Client cancellation charge caused solely by that late invoice. The first visit may use a different payment deadline expressly agreed in writing.{{IF_PER_VISIT_PRICING}}
(a) Contractor shall ordinarily issue each weekly invoice at least {{INVOICE_LEAD_DAYS}} calendar days before the first scheduled visit it covers. Payment of undisputed amounts is due {{PAYMENT_DEADLINE_HOURS}} hours before the agreed arrival window of that first visit begins. If an invoice is delivered fewer than {{LATE_INVOICE_THRESHOLD_DAYS}} calendar days before that deadline, Client shall have at least {{LATE_INVOICE_GRACE_BUSINESS_DAYS}} business days after receipt to pay, and the Parties shall agree whether to proceed or reschedule without a Client cancellation charge caused solely by that late invoice. The first week may use a different payment deadline expressly agreed in writing.{{IF_WEEKLY_FLAT_PRICING}}
(b) Client shall pay by {{PAYMENT_METHOD}}. A change to bank details must be confirmed through a previously verified telephone number. No email alone authorizes a change in payment destination.
(c) If an undisputed advance remains unpaid when due, Contractor shall notify Client promptly and may withhold the affected visit unless payment is received before dispatch or the Parties agree otherwise. Contractor is not required to extend credit. Contractor shall use reasonable efforts to avoid dispatching a crew to a visit already suspended for nonpayment.
(d) A visit properly suspended for Client's failure to pay is not a Contractor cancellation. It does not automatically earn the full service fee. Any missed-visit charge is limited to the reasonable, documented net loss and cap applicable under Section 15, without duplication. Future visits are not automatically billed at full price while suspended. Accrued, earned fees and lawful remedies for material breach remain available.
(e) Suspension shall be no broader or longer than reasonably necessary. Once undisputed arrears and the required advance are paid, Contractor shall resume on the next reasonably available agreed date. Nothing authorizes suspension in violation of law or excuses Contractor's own negligence.
(f) An undisputed earned amount unpaid for more than {{INTEREST_GRACE_DAYS}} calendar days after its due date accrues simple interest at {{LATE_CHARGE_PERCENT}} per month, calculated daily at {{LATE_CHARGE_ANNUAL_PERCENT}} per year, or the maximum lawful rate if lower. Interest is not compounded, does not accrue on interest or unearned charges, and is not imposed on a good-faith disputed amount while the Parties promptly pursue Section 12. Any post-judgment interest is governed by applicable law unless an enforceable agreement provides otherwise.
(g) For a returned or failed payment attributable to Client, Contractor may recover actual reasonable bank and processing costs up to {{RETURNED_PAYMENT_FEE_WORDS}} per failed transaction, supported on request, without duplicating other charges.
(h) Recovery of attorneys' fees and litigation costs is governed exclusively by Section 34(d).
(i) No additional submission or internal-processing requirement may delay the agreed payment date unless expressly stated in Exhibit B and permitted by applicable law. Nothing waives mandatory freelance-payment or anti-retaliation rights.

## 12. BILLING DISPUTES
Client shall identify any invoice dispute promptly and, for matters reasonably apparent from the invoice, within {{BILLING_DISPUTE_DAYS}} business days after receipt, stating the disputed amount and basis. Client shall pay the undisputed portion when due. The Parties shall exchange reasonably necessary supporting information and attempt to resolve the dispute within {{DISPUTE_RESPONSE_BUSINESS_DAYS}} business days. Failure to object within that period is evidence of acceptance of apparent billing details, but does not conclusively waive a claim involving fraud, duplicate billing, unperformed Services, a latent condition, or rights that cannot lawfully be waived. A good-faith dispute shall not be treated as undisputed solely because that notice period elapsed. Any amount determined payable shall be paid within {{RESOLUTION_PAYMENT_BUSINESS_DAYS}} business days after written resolution or as ordered by a court. Service-quality and damage claims are also subject to Sections 21 and 22.

## 13. ACCESS, KEYS AND ALARM CODES
(a) Client shall provide safe and timely access, including the keys, access cards, codes and accurate alarm instructions necessary for the Services. The Parties shall exchange actual access credentials securely, separately from this Agreement.
(b) Contractor shall limit access credentials to personnel who need them for the Services, prohibit unauthorized copying or sharing, and promptly notify Client of any known loss or compromise. Upon termination, Contractor shall return physical keys and delete or relinquish access credentials within {{KEY_RETURN_BUSINESS_DAYS}} business days, subject to a different safe transition agreed in writing. Client shall deactivate credentials when appropriate. Access credentials may not be withheld as security for payment.
(c) Each Party is responsible for reasonable, necessary re-keying, credential replacement, and false-alarm costs to the extent caused by its own negligence or failure to provide or follow accurate instructions. Contractor is responsible for its personnel's acts within the applicable legal and contractual standards. No charge may include an unnecessary system upgrade or costs attributable to the other Party's fault.
(d) At departure, Contractor shall lock the designated doors, follow the agreed alarm procedure, report incidents promptly and leave refrigeration and other powered equipment in its agreed operating condition. Client shall identify the applicable closeout procedure and any equipment that must remain on before service begins.

## 14. FAILED ACCESS AND LOCKOUT
(a) Failed access occurs only when Contractor arrives within the agreed window, is ready and able to perform, and cannot gain required access because of a matter within Client's reasonable control. Contractor shall attempt to contact the designated on-call representative and wait at least {{LOCKOUT_WAIT_MINUTES}} minutes, unless remaining would be unsafe. Contractor shall document arrival, attempted contact, and the reason access failed.
(b) If access is not provided, Contractor may recover its reasonable, documented net loss caused by the failed visit, calculated as the agreed pre-tax fee less reasonably avoided costs and net replacement earnings, with supporting records. The total may not exceed that visit's pre-tax service fee, plus any tax legally applicable to the charge. A full fee is not automatic. No charge applies to Contractor's own access error, arrival outside the agreed window without approval, or an emergency covered by Section 15(d).
(c) This charge replaces any cancellation charge for the same visit. If the visit is rescheduled within {{MAKEUP_WINDOW_DAYS}} calendar days, all amounts paid for the missed visit shall be credited to the makeup visit; aggregate charges for the original and makeup visit may not exceed one full service fee plus properly applicable tax. If no makeup is agreed, the properly calculated charge settles that {{SERVICE_PERIOD}}'s missed visit, without limiting remedies for a separate continuing material breach and without duplicate recovery.

## 15. CANCELLATION AND RESCHEDULING
(a) With notice at least {{TIMELY_RESCHEDULE_HOURS}} hours before the agreed arrival window begins, Client may request one rescheduling of a scheduled visit without an additional charge, to a mutually agreed date within {{MAKEUP_WINDOW_DAYS}} calendar days of the original visit. Contractor shall act reasonably in offering available dates consistent with safe access and {{PREMISES_TYPE}} operations. Advance payment carries to the makeup visit. Additional changes require mutual agreement. A makeup visit is assigned to its original service {{SERVICE_PERIOD}} and does not replace a different {{SERVICE_PERIOD}}'s visit unless expressly agreed.
(b) If Client cancels fewer than {{TIMELY_RESCHEDULE_HOURS}} hours before the agreed arrival window begins, or does not reasonably cooperate in arranging a makeup within {{MAKEUP_WINDOW_DAYS}} calendar days after a timely cancellation or rescheduling request, Contractor may charge its reasonable, documented net loss attributable to the missed visit after avoided costs and net replacement earnings, capped at {{CANCELLATION_PERCENT}} of the pre-tax visit fee rounded to the nearest cent, plus any legally applicable tax. If Contractor cannot offer a reasonable makeup opportunity after a timely request, no Client cancellation charge applies. Client has no unilateral right to reduce the recurring frequency. Payment of a permitted missed-visit charge settles only that visit; an agreed skip must be recorded in writing.
(c) If a charged visit is rescheduled within {{MAKEUP_WINDOW_DAYS}} calendar days, the charge and all other payments for that visit are credited to the makeup visit, so the aggregate price does not exceed one full service fee plus properly applicable tax. Any remaining prepayment for a visit that will not occur shall be credited to the next invoice, or refunded within {{CREDIT_RETURN_DAYS}} calendar days if the Agreement ends first. Contractor shall reconcile tax to the actual transaction. The same loss cannot be charged again under Sections 11, 14, 24 or a termination claim.
(d) No cancellation or reservation charge applies when an emergency beyond the affected Party's reasonable control, including fire, flood, utility failure, or a government closure not caused by that Party's breach, prevents performance. Prompt notice and reasonable mitigation are required. The Parties shall attempt a makeup within {{MAKEUP_WINDOW_DAYS}} calendar days; otherwise Contractor shall credit the unperformed Services, with any unused credit refunded within {{CREDIT_RETURN_DAYS}} calendar days after termination. A health closure caused by an unresolved condition within Client's control is assessed under Section 24 rather than automatically treated as force majeure. Contractor may not charge Client for a closure caused by Contractor's own breach or negligence.
(e) If Contractor cancels for a reason outside paragraph (d), Client may choose a reasonably prompt makeup or a credit for the unperformed Services. If a makeup is not reasonably useful or possible and Client requests a refund, Contractor shall issue it within {{REFUND_BUSINESS_DAYS}} business days after that request. Credits and refunds do not waive a material-breach remedy or liability that cannot lawfully be limited.
(f) {{MISSED_VISIT_THRESHOLD_CAP}} Client-attributable missed visits in a rolling {{MISSED_VISIT_WINDOW_WEEKS}}-week period may establish a material failure to maintain the agreed frequency. After the {{MISSED_VISIT_WARNING_ORDINAL}} such visit, Contractor shall give written notice and request a workable service plan within {{SERVICE_PLAN_DAYS}} calendar days. If Client fails to provide and follow that plan and a {{MISSED_VISIT_FINAL_ORDINAL}} such visit occurs, Contractor may invoke Section 4(b). Qualifying emergencies and Contractor cancellations do not count. No automatic price increase or additional penalty applies.

## 16. CLIENT RESPONSIBILITIES
(a) Client shall, at its own cost, provide working water, lighting and electrical power; access to designated lawful waste and recycling receptacles; adequate stocks of its allocated consumables; and surfaces and floors reasonably cleared for cleaning. Client shall secure cash, valuables and confidential materials and promptly disclose known hazards, damage and conditions affecting the Services.
(b) Client shall arrange all between-visit and excluded cleaning, food-contact sanitation, pest control, waste hauling, and maintenance required for its operation. Before the crew arrives, Client shall remove or protect exposed food and utensils, safely cool or shut down equipment that must be cleaned while off, identify equipment that must remain operating, and disclose manufacturer restrictions known to Client. Contractor shall not disconnect gas, move connected cooking equipment, alter refrigeration settings, or move heavy equipment unless specifically authorized through a safe, qualified procedure. Each Party remains responsible for the consequences of its own negligence; Client's responsibility to secure property does not waive a claim for negligent damage or theft attributable to Contractor.
(c) Client shall designate a primary on-call contact in Exhibit B and may designate a backup on-call contact if available, and shall provide applicable landlord, franchisor and site requirements affecting access, products, insurance or the Services before work begins. Requirements changing the agreed scope or cost are subject to Sections 8 and 9. Client remains responsible for its own food-service permits and operational compliance as provided in Section 26(b), whether or not a permit holder is identified in Exhibit A.{{IF_SCOPE_DETAILED}}
(c) Client shall designate a primary on-call contact in Exhibit B and may designate a backup on-call contact if available, and shall provide applicable landlord, franchisor and site requirements affecting access, products, insurance or the Services before work begins. Requirements changing the agreed scope or cost are subject to Sections 8 and 9. Client remains responsible for its own food-service permits and operational compliance as provided in Section 26(b).{{IF_SCOPE_NOT_DETAILED}}

## 17. PERSONNEL AND SUBCONTRACTORS
(a) Contractor controls the staffing, work scheduling and supervision of its personnel, subject to the agreed service schedule. Contractor may substitute qualified personnel; substitution alone is not a breach.
(b) Client may request removal of an individual for a documented, reasonable safety, security, or material performance concern. Requests may not be discriminatory, retaliatory, or otherwise unlawful. Contractor shall investigate promptly, take appropriate interim measures, and provide a qualified replacement within a reasonable time. Contractor retains employment and supervisory decisions, subject to applicable law.
(c) Contractor may perform through its employees or qualified subcontractors and remains responsible for performance of the Services in either case.
(d) Contractor shall use personnel qualified and trained for their assigned tasks and require subcontractors to comply with the applicable service, confidentiality, safety, employment, and insurance obligations. Contractor shall provide advance notice before giving a new subcontracting entity unattended access to the Premises. Client may raise reasonable documented security objections, and the Parties shall promptly resolve them without creating a general right to direct Contractor's workforce. Subcontracting does not release Contractor from its responsibilities under this Agreement.
(e) Background Checks. All personnel with access to the Premises must have passed a background check before being assigned to the site.

## 18. PERSONNEL COORDINATION
Client shall direct requests about service scope, quality, and staffing to Contractor's designated supervisor. Neither Party shall use or disclose the other Party's confidential information except as permitted by Section 27. This Agreement imposes no restriction or fee on lawful solicitation, recruitment, or hiring of any individual, and no provision shall be interpreted to restrict an individual's lawful employment opportunities.

## 19. INDEPENDENT CONTRACTOR
Contractor is an independent contractor. Nothing in this Agreement creates an employment, partnership, joint venture or agency relationship between the Parties. As between the Parties, Contractor is responsible for hiring, supervision, compensation, payroll, and employment obligations for its personnel. Client may specify service outcomes and reasonable site safety rules but shall ordinarily communicate instructions through Contractor's supervisor. The Parties' description of their relationship does not override an employment, joint-employment, tax, or other classification imposed by applicable law.

## 20. INSURANCE
(a) Contractor shall maintain throughout performance Commercial General Liability insurance, written on an occurrence basis, with limits of at least {{INSURANCE_PER_OCCURRENCE}} each occurrence and {{INSURANCE_AGGREGATE}} general aggregate.
(b) Contractor shall maintain workers' compensation, disability benefits, and Paid Family Leave coverage to the extent required by {{INSURANCE_JURISDICTION}} law, and shall provide the applicable evidence of coverage or valid exemption documentation. Contractor shall require legally appropriate coverage from its subcontractors.
(c) Before Services begin and upon renewal, Contractor shall provide certificates evidencing required coverage. Any agreed additional-insured status, primary and noncontributory wording, or waiver of subrogation must be stated in an actual insurer-issued endorsement and listed in Exhibit B, B5; a certificate alone does not amend the policy. Contractor shall promptly notify Client after learning of cancellation, lapse, or a material reduction affecting the Services. No insurance obligation requires indemnification for Client's own negligence contrary to law.
(d) Any insurance requirement exceeding the coverages above must be stated in Exhibit B or agreed in a signed writing. The Parties must agree on any additional premium or price adjustment under Section 8 before Contractor is obligated to obtain an additional endorsement. Any required endorsement is subject to insurer issuance and must be obtained before the work for which it is required.
(e) Client shall maintain premises-liability and property coverage appropriate to its {{PREMISES_TYPE}} operations. Neither Party's insurance reduces its responsibilities under this Agreement or liability that cannot lawfully be limited.

## 21. DAMAGE CLAIMS
Client shall notify Contractor of alleged damage promptly and, where reasonably practicable, within {{DAMAGE_NOTICE_BUSINESS_DAYS}} business days after discovery, with a description and available photographs or other evidence. Contractor shall likewise promptly report damage or an incident it discovers. Each Party shall reasonably preserve evidence and permit inspection. Emergency action reasonably necessary to protect persons, food safety, property, or continued safe operation may proceed without awaiting inspection, with notice and documentation as soon as practicable. Neither delayed notice nor emergency mitigation automatically waives a claim. Any consequence of delayed notice is limited to actual material prejudice proved by the affected Party and permitted by law. This Section does not shorten a statutory limitations period or exempt either Party from liability that cannot lawfully be limited. No double recovery is permitted; compensation shall reflect the legally recoverable loss rather than an unrelated upgrade.

## 22. QUALITY GUARANTEE AND CORRECTION
(a) Client shall notify Contractor of any material failure to complete an included task with the agreed standard of care within {{QUALITY_COMPLAINT_HOURS}} hours after completion of the visit. If a deficiency could not reasonably have been identified within that period, Client shall notify Contractor as soon as reasonably possible and in any event no later than {{QUALITY_LATENT_LIMIT_HOURS}} hours after completion of the visit; this is a limited exception for deficiencies not reasonably discoverable sooner, not a general extension of the reporting period. The notice should describe the task and condition and include reasonably available evidence. Later re-soiling, conditions caused after Contractor completed the visit, including by Client's employees, other contractors, construction crews, building staff, vendors or occupants, and work or items outside {{SCOPE_REF}} are not service deficiencies attributable to Contractor.
(b) Contractor shall promptly inspect as reasonably necessary and have a reasonable opportunity to correct the affected task or area without additional charge, ordinarily within {{QUALITY_CORRECTION_BUSINESS_DAYS}} business days after notice and access, or sooner when reasonably necessary for food safety. Correction is limited to the verified deficiency within the agreed scope and does not include re-cleaning of the entire Premises or additional work. Client shall provide reasonable corrective access.
(c) If Contractor declines or fails to correct within that period, or correction is no longer reasonably useful, Client may receive a reasonable credit or refund attributable to the deficient portion of the visit. A quality deficiency does not by itself entitle Client to a refund of the full visit fee. For ordinary quality deficiencies, that adjustment and re-performance are the agreed first remedies. No unrelated invoice amount may be withheld solely because of a quality complaint.
(d) This procedure does not bar reasonable emergency mitigation, termination for an uncured material breach, or claims for personal injury, property damage, fraud, gross negligence, willful misconduct, or other liability that cannot lawfully be limited. Damage claims are governed by Section 21, and applicable limitations by Section 29. Refunds and other recovery may not duplicate compensation for the same loss.

## 23. CLEANING CONDITIONS AND RESULTS
(a) The included Services address ordinary surface dirt, dust, and light grease within the baseline condition and tasks agreed in {{SCOPE_REF}}, using appropriate commercially reasonable methods and products. Heavy accumulation, restoration, and specialized degreasing are excluded unless added by signed Change Order.
(b) Results may vary with age, condition, material, finish, staining, deterioration, prior damage and maintenance history. Cleaning is not restoration, refinishing or repair. Contractor does not warrant removal of permanent stains, etching, rust, discoloration, worn or damaged finishes, deteriorated grout or soil bonded to a surface over time.
(c) Pre-existing wear or damage does not make Contractor responsible for restoration, but does not excuse new damage caused by negligent product selection or performance. Contractor shall use surface-appropriate products, follow available manufacturer instructions, and conduct a reasonable inconspicuous compatibility check where warranted. Contractor does not guarantee an inspection grade, regulatory approval, sterilization, or elimination of all microorganisms.
(d) The Parties shall document the baseline condition before the first recurring visit. Service photographs shall focus on surfaces and relevant damage, avoid people and sensitive information where reasonably practicable, and be used only for legitimate service, insurance or dispute purposes, subject to Section 27.

## 24. HAZARDOUS CONDITIONS AND STOPPING WORK
(a) Contractor may immediately stop the affected work when it reasonably encounters a serious safety hazard or a condition outside its personnel's training, equipment, or lawful scope, including sharps, uncontrolled blood or biohazard contamination, suspected asbestos, substantial mold, sewage backup, dangerous chemical spills, electrical hazards, unsafe structures, equipment or working conditions, an aggressive or uncontrolled animal in the work area, or threatening, harassing or abusive conduct toward Contractor's personnel. Contractor shall promptly notify Client, identify the affected area, and perform unaffected work where reasonably safe and practicable. Ordinary restroom cleaning is included only to the extent expressly included in {{SCOPE_REF}}; specialized remediation is not.
(b) If the condition was not caused by Contractor and results from a known hazard that Client failed to disclose or address, Client shall pay for work actually completed and reasonable, documented unavoidable loss attributable to the interruption, after avoided costs and net replacement earnings, with the aggregate limited to the affected visit's pre-tax fee plus legally applicable tax. No duplicate cancellation or failed-access charge applies. For an emergency beyond Client's reasonable control, Section 15(d) controls. No charge applies for work not performed because of a hazard caused by Contractor. Contractor shall resume once the condition is safely remedied and the Parties agree on access; persistent material conditions may be addressed under Section 4.

## 25. EXCLUDED SERVICES
(a) The following services are excluded unless expressly added by a signed Change Order, legally appropriate, and performed by a contractor with all required qualifications, training, licenses and insurance:
(b) Kitchen exhaust hood, hood-filter and exhaust duct cleaning; grease trap or grease interceptor service; internal cleaning of fryers, ovens, grills or other equipment; dismantling equipment; pulling out or moving connected appliances; and cleaning inaccessible areas behind equipment.
(c) Pest control and extermination; mold remediation; biohazard, bloodborne-pathogen and sewage remediation; handling needles or sharps; handling or disposal of hazardous materials or waste; and asbestos-related work.
(d) Exterior window cleaning; work requiring ladders, lifts, scaffolds, suspended equipment or work on elevated surfaces beyond safe reach from the floor with ordinary extension tools, unless a specific safe method and scope have been approved in a signed Change Order and all legal, training and insurance requirements have been satisfied.
(e) Food preparation and food handling; dishwashing, except dishwashing expressly included in {{SCOPE_REF}}; and food-contact surface cleaning and sanitizing, except a specifically identified task expressly included in {{SCOPE_REF}}. Routine wiping of cleared dining tables does not by itself include specialized food-contact sanitizing.
(f) Repairs, painting, electrical, plumbing and mechanical work; snow and ice removal; floor stripping, waxing, refinishing, polishing, grout restoration and floor-machine restoration; heavy degreasing and deep grease remediation; and restoration work of any kind.
(g) A request or additional payment does not authorize unqualified or unlawful work. Except as expressly agreed under Section 9, Contractor has no duty to perform or arrange excluded services.

## 26. COMPLIANCE WITH LAW AND SANITATION
(a) Each Party shall comply with applicable {{COMPLIANCE_JURISDICTIONS}} law in carrying out its own obligations. Contractor is responsible for the lawful performance of included Services, appropriate training, hazard communication, protective equipment, product handling, worker classification, payroll and employment obligations, and legally required employment-eligibility verification. Contractor shall require corresponding compliance from its subcontractors and remains responsible for their contractual performance.
(b) Client is responsible for its food-service permits, daily and between-visit sanitation, food handling, pest-control program, and excluded equipment or food-contact cleaning. Contractor's recurring cleaning does not replace those obligations. Contractor shall protect exposed food and food-contact items from contamination, follow product-label directions and applicable sanitation requirements for any included sanitizing task, and keep restroom tools separate from kitchen and dining-area tools. Neither Party guarantees that this Agreement alone ensures a health-inspection result. This allocation does not waive a governmental requirement or excuse either Party's own violation or negligent conduct.

## 27. CONFIDENTIALITY
(a) Each Party shall use the other Party's nonpublic business, financial, customer, operational, security, and access information only to perform this Agreement or enforce its lawful rights, and shall protect it with reasonable care. Disclosure is permitted to personnel, professional advisers, insurers, and approved subcontractors who need to know and are subject to confidentiality duties, and as required by law. Where legally permitted and reasonably practicable, the disclosing Party shall give advance notice of compelled disclosure and disclose only what is required.
(b) The obligation does not cover information lawfully public without breach, already lawfully known without restriction, independently developed without use of protected information, or lawfully received from an unrestricted third party. Ordinary confidentiality obligations continue for {{CONFIDENTIALITY_YEARS}} years after termination; trade secrets remain protected while qualifying as trade secrets, and access credentials while usable. Each Party shall promptly notify the other of a known unauthorized disclosure affecting the other and reasonably cooperate in mitigation. Statutory privacy, security, and breach-notification duties remain unaffected.
(c) On request or termination, protected information shall be returned or securely deleted, except records reasonably required by law, insurance, or preservation of legal claims, which remain protected. No photographs, names, logos, or details identifying the other Party may be used in marketing without written permission. Nothing restricts lawful worker communications, reporting to government, or employment mobility.

## 28. INDEMNIFICATION AND DEFENSE
(a) To the extent permitted by law, each Party shall indemnify and hold harmless the other Party and its officers, directors, and employees from third-party claims for bodily injury, death, or damage to tangible property to the extent caused by the negligence or willful misconduct of the indemnifying Party or persons for whom it is legally responsible in performing this Agreement. Each Party is also responsible under this Section for third-party employment claims to the extent caused by its violation of its own employment obligations. No Party indemnifies another for the other's negligence, willful misconduct, or independent violation of law. Purely contractual claims between the Parties are not third-party indemnity claims.
(b) The Party seeking protection shall give prompt notice and reasonable cooperation. Delayed notice reduces an obligation only to the extent of actual material prejudice and as permitted by law. The indemnifying Party may assume defense of a covered claim through its insurer or qualified counsel reasonably acceptable to the indemnified Party, subject to conflicts of interest. If it does not do so promptly, reasonable defense costs attributable to covered matters are recoverable subject to allocation under paragraph (a). The Parties shall cooperate in addressing mixed claims and allocating costs; neither is required to fund defense of the other's independent wrongdoing beyond its agreed lawful responsibility.
(c) No settlement may impose an admission, nonmonetary obligation, or unreimbursed payment on an indemnified Party without its written consent, which shall not be unreasonably withheld for a fully funded monetary settlement containing a complete release. Insurer rights and applicable law are preserved. There shall be no double recovery. First-party attorneys' fees are governed exclusively by Section 34(d).

## 29. LIMITATION OF LIABILITY
(a) Subject to paragraph (c), neither Party is liable to the other for indirect, special, incidental, or consequential damages arising from an ordinary breach of this Agreement. This exclusion does not eliminate earned fees, required refunds or credits, reasonable corrective-service costs otherwise recoverable under this Agreement, or proven direct net loss recoverable under Section 4(d). A Party may not relabel ordinary consequential loss as a payment obligation.
(b) Subject to paragraph (c), each Party's total aggregate liability to the other for ordinary contractual claims under this Agreement, including ordinary confidentiality breaches, shall not exceed {{LIABILITY_CAP_MULTIPLE}} times the recurring pre-tax per-visit fee in effect when the earliest event giving rise to any claim subject to this cap occurred, using the initial fee if Services had not yet begun. This is one aggregate limit for this Agreement, not a separate limit for each claim, visit, or {{RENEWAL_TYPE}} continuation. The amount is {{LIABILITY_CAP_AMOUNT}} at the initial fee. The cap does not limit Client's obligation to pay earned fees and properly supported charges, Contractor's obligation to return unearned money, or an award of costs and fees under Section 34(d).
(c) Neither the damages exclusion nor the aggregate cap applies to claims arising from bodily injury, death, or damage to tangible property caused by a Party's negligence; fraud, gross negligence, or willful misconduct; lawful third-party indemnification under Section 28; or any liability that cannot lawfully be excluded or limited, including liability governed by {{GOVERNING_LAW_STATE}} General Obligations Law Section 5-323. These exclusions apply regardless of how the claim is pleaded. Nothing requires a Party to indemnify another for that other Party's own wrongdoing contrary to law.
(d) These provisions do not create a cause of action where none otherwise exists, dispense with proof of duty, breach, causation or legally recoverable loss, or waive defenses available under law. Insurance requirements do not reduce liability that cannot lawfully be limited. No recovery may duplicate a refund, credit, settlement, insurance payment to the extent applicable law requires an offset, or other recovery for the same loss.

## 30. FORCE MAJEURE
(a) Neither Party is liable for delay or failure of an affected obligation caused by an event beyond its reasonable control, such as fire, flood, severe weather, epidemic-related restriction, utility failure, civil unrest, or governmental action, if the event was not caused by that Party's breach or negligence and could not reasonably have been avoided. The affected Party shall notify the other promptly, describe the impact and expected duration, and use reasonable efforts to mitigate and resume performance. Ordinary staffing shortages, increased costs, or inability to pay do not alone excuse performance.
(b) Earned fees and other lawful amounts accrued before the interruption remain payable. No service fee is earned for an unperformed visit solely because the Agreement remains in effect. Affected prepayments, makeup visits, and credits are governed by Section 15(d), which controls over inconsistent payment language. If substantial performance is prevented for {{FORCE_MAJEURE_DAYS}} consecutive calendar days, either Party may terminate the affected Services by written notice without an early-termination charge, subject to payment for completed work and return of unearned amounts under Section 4(d).

## 31. ASSIGNMENT
(a) Neither Party may assign this Agreement without the other Party's prior written consent, which shall not be unreasonably withheld, except that either Party may assign to a successor in connection with a merger or sale of substantially all of its assets upon written notice. Any permitted transfer must include this Agreement and comply with the requirements below.
(b) A permitted successor shall assume this Agreement in writing, possess the authority and practical ability to perform, and receive all relevant notices and records. Assignment does not release the assigning Party from accrued obligations or from further responsibility without the other Party's express written release. No assignment expands the Premises or Services or increases the other Party's burden without written agreement. Subcontracting permitted under Section 17 is not an assignment of this Agreement.

## 32. NOTICES
(a) Formal notices of breach, termination, assignment, or dispute shall be sent to the receiving Party's designated notice email. A Party is not required to designate a mailing address for notices; where a Party has designated one in Exhibit B, notice to that Party may instead be delivered personally or by recognized courier to that address. An email is deemed received on the next business day after sending if the sender retains a transmission record and receives no delivery-failure message; if the sender knows delivery failed, another permitted method must be used. Personal or courier delivery is effective on documented receipt. A business day excludes Saturdays, Sundays, and {{GOVERNING_LAW_STATE}} State public holidays. All times refer to {{SERVICE_TIMEZONE}}.
(b) Scheduling, cancellation, access, and urgent safety notices may be sent to the operational email and on-call contact designated in Exhibit B, including on weekends. For the {{TIMELY_RESCHEDULE_HOURS_WORDS}}-hour cancellation calculation, an email to the operational address is effective when sent, provided it is not returned undeliverable. Text messages are effective for operational notice only when acknowledged by the designated recipient. Operational communications do not amend price, substantive scope, or liability terms. A method expressly required by statute controls over this Section.
(c) The designated notice emails, operational emails, on-call contacts and any designated notice mailing address are stated in Exhibit B. A Party may update its contact information by formal notice under this Section. Actual access credentials shall not be included in those notices or in the circulated Agreement.

## 33. GOVERNING LAW AND VENUE
{{GOVERNING_LAW_STATE}} law governs this Agreement, without regard to its conflict-of-laws rules. Subject to Section 34 and mandatory law, the Parties consent to exclusive venue in the state courts sitting in {{VENUE_COUNTY}}, {{GOVERNING_LAW_STATE}}, and, only where federal subject-matter jurisdiction independently exists, {{FEDERAL_VENUE}}. Nothing restricts a lawful claim before an agency or a court or tribunal whose jurisdiction cannot be waived by agreement.

## 34. DISPUTE RESOLUTION AND LEGAL FEES
(a) A Party shall give written notice describing the dispute and requested resolution. Senior representatives shall confer in good faith within {{DISPUTE_DISCUSSION_DAYS}} calendar days and attempt resolution. If unresolved after {{MEDIATION_REQUEST_DAYS}} calendar days, either may request nonbinding mediation in {{MEDIATION_VENUE}} or remotely by agreement. The Parties shall attempt to select a mediator within {{MEDIATOR_SELECTION_DAYS}} calendar days of that request and share the mediator's charges equally. Either Party may sue {{SUIT_AFTER_DAYS}} calendar days after the original dispute notice if the dispute remains unresolved and that Party has reasonably cooperated. Refusal to participate or failure to agree on a mediator does not extend this period.
(b) These preliminary steps do not prevent emergency or provisional relief, filing reasonably necessary to preserve a claim before a limitations deadline, a lawful agency complaint, or an eligible small-claims or commercial-claims proceeding. They do not toll any statutory deadline by themselves.
(c) Either Party may pursue collection of an undisputed amount due under this Agreement after written demand and {{COLLECTION_DEMAND_BUSINESS_DAYS}} business days to pay, without mediation. This includes earned fees due to Contractor and an undisputed refund due to Client. A genuine dispute is not eliminated solely by missing an administrative objection period.
(d) In a direct action between the Parties to recover earned fees, properly supported contractual charges, or a required refund or credit, the substantially prevailing Party may recover reasonable attorneys' fees and court costs attributable to that payment dispute, as determined by the court. The court may apportion or decline an award where the outcome is mixed. This provision expressly covers direct claims between the Parties, including reasonable enforcement and appellate work concerning that payment dispute. For other direct disputes, each Party bears its own attorneys' fees unless a statute or other applicable law authorizes recovery. Third-party defense costs remain governed by Section 28. Duplicate recovery is prohibited.

## 35. GENERAL PROVISIONS
(a) This Agreement, together with {{EXHIBITS_LIST}} and any amendment or Change Order incorporated under Section 2, is the entire agreement on its subject matter and supersedes prior discussions, proposals and understandings.
(b) An amendment must be in writing and signed by both Parties' authorized representatives. Electronic approval meeting Section 9(e) is permitted. Scheduling adjustments under Section 5 may be confirmed by email without changing substantive terms.
(c) If a provision is invalid or unenforceable, it shall be severed to the extent required, and the remaining provisions remain effective to the extent they can operate consistently with the Parties' lawful agreement. A court may narrow a provision only where applicable law permits; this clause does not require enforcement or rewriting of an unlawful penalty, exculpation, or restraint on employment. The Parties shall cooperate in adopting a lawful substitute consistent with the original commercial purpose.
(d) A Party's failure to enforce a provision is not by itself a waiver of that provision or another provision. A waiver must be in writing by an authorized representative and applies only to its stated circumstances, subject to applicable law.
(e) This Agreement may be executed in counterparts and delivered electronically. Electronic signatures intended to authenticate execution and scanned signed copies have the same effect as original signatures to the extent permitted by law.
(f) Headings are for convenience and do not affect interpretation. Unless expressly stated otherwise, a reference to a Section means a section of this Agreement; {{EXHIBIT_CROSS_REFERENCES}}. "Calendar days" include weekends and holidays. "Business days" and applicable local time are defined in Section 32.
(g) Except for persons expressly protected by lawful indemnity or an applicable insurance endorsement, this Agreement creates no contractual enforcement right in a person other than the Parties and their permitted successors. It does not eliminate a nonparty's rights under tort law, employment law, or another applicable statute.

## 36. CANCELLATION, RESCHEDULING AND CONTRACT TERMINATION
(a) This Section is a summary for convenience of reference only. It creates no additional right, charge, notice requirement, restriction or remedy, and it removes none. Sections 3, 4, 5, 10, 11, 12, 14, 15, 24, 30 and 32 and Exhibit B state these terms operatively and control over any inconsistency with the summary in this Section.
(b) Rescheduling a visit. With notice at least {{TIMELY_RESCHEDULE_HOURS}} hours before the agreed arrival window begins, Client may reschedule a visit once without charge to a mutually agreed date within {{MAKEUP_WINDOW_DAYS}} calendar days of the original visit (Section 15(a)).
(c) Late cancellation. Later notice, or a failure reasonably to cooperate in arranging a makeup, permits a charge limited to Contractor's reasonable, documented net loss after avoided costs and net replacement earnings, capped at {{CANCELLATION_PERCENT}} of the pre-tax visit fee plus any legally applicable tax. The cap is a ceiling on proven loss and not an automatic charge, and no Client cancellation charge applies where Contractor cannot offer a reasonable makeup opportunity (Section 15(b)).
(d) Failed access. A failed-access charge arises only on the conditions of Section 14, including a wait of at least {{LOCKOUT_WAIT_MINUTES}} minutes, is limited to documented net loss not exceeding that visit's pre-tax service fee plus legally applicable tax, and replaces any cancellation charge for the same visit.
(e) Cancellation by Contractor. Client may choose a prompt makeup or a credit, or a refund within {{REFUND_BUSINESS_DAYS}} business days where a makeup is not reasonably useful or possible (Section 15(e)).
(f) Emergencies. No cancellation or reservation charge applies where an emergency beyond the affected Party's reasonable control prevents performance (Section 15(d)).
(g) Repeated missed visits. {{MISSED_VISIT_THRESHOLD_CAP}} Client-attributable missed visits in a rolling {{MISSED_VISIT_WINDOW_WEEKS}}-week period are addressed under Section 15(f). No automatic price increase or additional penalty applies.
(h) Minimum commitment. The Initial Term is {{INITIAL_TERM_MONTHS}} months and the Minimum Commitment Period is {{MINIMUM_COMMITMENT_MONTHS}} calendar months, each from the Service Commencement Date. The commitment restricts termination for convenience only (Section 3).{{IF_MINIMUM_COMMITMENT}}
(h) Minimum commitment. No minimum service commitment applies.{{IF_NO_MINIMUM_COMMITMENT}}
(i) Termination for convenience. Either Party may terminate for convenience with at least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice, taking effect no earlier than the Minimum Commitment End Date (Section 4(a)).{{IF_MINIMUM_COMMITMENT}}
(i) Termination for convenience. Either Party may terminate ongoing Services with at least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice (Section 4(a)).{{IF_NO_MINIMUM_COMMITMENT}}
(j) Early termination. Termination for an uncured material breach after {{CURE_PERIOD_DAYS}} calendar days' notice, for nonpayment of an undisputed amount unpaid {{PAST_DUE_DAYS}} calendar days after written demand, for unlawfulness or imminent serious danger, and after {{FORCE_MAJEURE_DAYS}} consecutive calendar days of force majeure is governed by Section 4(b), Section 4(c) and Section 30(b).
(k) No early-termination charge. No early-termination fee, exit charge or liquidated sum applies, and no remaining fees are automatically accelerated. A wrongful-termination claim is limited to proven direct damages (Section 4(d)), subject to Section 29.
(l) Refunds. On termination, earned fees are paid and unearned prepayments and unapplied credits are refunded within {{CREDIT_RETURN_DAYS}} calendar days (Section 4(d)).
(m) Outstanding payments. Accrued payment and refund duties survive termination. Undisputed overdue amounts accrue simple interest at {{LATE_CHARGE_PERCENT}} per month ({{LATE_CHARGE_ANNUAL_PERCENT}} per year) after {{INTEREST_GRACE_DAYS}} calendar days (Section 11(f)), and may be collected after written demand and {{COLLECTION_DEMAND_BUSINESS_DAYS}} business days (Section 34(c)).
(n) Notices. Cancellation, rescheduling and access notices go to the operational email and on-call contact in Exhibit B (Section 32(b)). Termination, breach, assignment and dispute notices go to the designated notice email in Exhibit B (Section 32(a)).
(o) Published policies. Contractor publishes general Commercial Cleaning Policies at {{CONTRACTOR_PUBLISHED_POLICY_URL}}. The version in effect on the date this document was prepared is version {{POLICY_VERSION}}, effective {{POLICY_EFFECTIVE_DATE}}. Those published policies are not incorporated into this Agreement and do not amend it, and a later revision of the published policies does not change any term of this Agreement.

@IF IF_SCOPE_DETAILED
## EXHIBIT A
## SCOPE OF WORK

SERVICE PREMISES: {{PREMISES_DESCRIPTION}} operated by Client, {{SERVICE_FULL_ADDRESS}}. Service location only; not necessarily the legal or principal business address of Client.
SCHEDULE: As stated in Exhibit B, B1.
CONDITIONS: {{CLOSED_PREMISES_TEXT}} Services are performed using {{ACCESS_TYPE}}, in accordance with Section 13.

### A#1. INCLUDED AREAS AND TASKS
Included Areas are {{SCOPE:included-areas}}. Each area is included only for the tasks expressly described in this exhibit. Additional areas or tasks require a Change Order under Section 9. The Included Areas control over a general back-of-house exclusion; the limited kitchen scope is not expanded.

Unless expressly assigned a different frequency, each listed task shall be completed at each scheduled visit. Completion means removal of ordinary visible soil reasonably removable with the agreed methods, subject to pre-existing-condition exclusions and applicable sanitation requirements for included sanitizing tasks. Staffing levels and work sequence are Contractor's responsibility; the fee is for completion of the agreed tasks, not a specified number of labor hours. Extra tasks require a Change Order.

|Area|Tasks and limits at each scheduled visit
{{SCOPE_TABLE:area-tasks}}

The Parties shall record the following site details, to the extent each applies to the Premises, and record the baseline before the first recurring visit. These details identify the agreed work as site information; a detail left unrecorded does not narrow or expand the tasks listed above, and an expansion beyond the listed tasks requires a Change Order.{{IF_ANY_SITE_DETAIL}}

APPROXIMATE SERVICED SQUARE FOOTAGE: {{SQUARE_FOOTAGE_IF_SET}}.
CUSTOMER RESTROOM AND FIXTURE COUNTS: {{CUSTOMER_RESTROOM_COUNTS_IF_SET}}.
EMPLOYEE RESTROOM AND FIXTURE COUNTS: {{EMPLOYEE_RESTROOM_COUNTS_IF_SET}}.
FLOOR AND SURFACE MATERIALS: {{FLOOR_MATERIALS_IF_SET}}.
INCLUDED KITCHEN EQUIPMENT AND EXTERIOR SURFACES: {{KITCHEN_EQUIPMENT_SURFACES_IF_SET}}.
TOUCHPOINTS AND CLEARED SURFACES: {{TOUCHPOINT_LOCATIONS_IF_SET}}.
INTERIOR GLASS AND WINDOW LOCATIONS: {{INTERIOR_GLASS_LOCATIONS_IF_SET}}.
INCLUDED FOOD-CONTACT OR DINING-TABLE SANITIZING TASK, SURFACE, FREQUENCY AND PROCEDURE: {{FOOD_CONTACT_SANITIZING_IF_SET}}.
ACCESS METHOD AND CLOSEOUT PROCEDURE REFERENCE: {{ACCESS_METHOD_REFERENCE_IF_SET}}. Actual keys and codes shall be exchanged separately.
EQUIPMENT THAT MUST REMAIN OPERATING AND MANUFACTURER RESTRICTIONS: {{EQUIPMENT_RESTRICTIONS_IF_SET}}.
WASTE, RECYCLING AND SEPARATELY COLLECTED ORGANICS RECEPTACLE LOCATIONS: {{WASTE_RECEPTACLE_LOCATIONS_IF_SET}}.
FOOD-SERVICE PERMIT HOLDER: {{FOOD_PERMIT_HOLDER_IF_SET}}.
SITE, LANDLORD OR BRAND REQUIREMENTS AFFECTING THE SERVICES: {{SITE_REQUIREMENTS_IF_SET}}.

@IF IF_SCOPE_ANY:excluded-areas
### A#2. EXCLUDED AREAS
Back-of-house areas are excluded except for Included Areas expressly identified in A1. The following are excluded: {{SCOPE:excluded-areas}}. An area expressly identified as an Included Area in A1 remains included even if it is physically located in a back-of-house portion of the Premises. The kitchen is included only to the extent stated in A3.
@ENDIF

@IF IF_SCOPE_ANY:kitchen-included
### A#3. LIMITED KITCHEN SCOPE
(a) Included work is {{SCOPE:kitchen-included}}, together with floor cleaning under A4. Ordinary light surface grease is included within the agreed baseline.
(b) Excluded work is {{SCOPE:kitchen-excluded}}, subject to Section 25.
(c) Contractor will not unplug refrigerators, freezers, alarms or equipment required to remain operating, change temperature controls, disconnect utility connections or move connected appliances. Client shall identify safe equipment conditions and manufacturer requirements before service. Contractor shall promptly report an observed condition that could affect food safety or property. No task requires working around exposed food or hot or energized equipment where unsafe.
(d) Food-contact cleaning and sanitizing are excluded except a task expressly identified in A1 or in paragraph (a), including any dishwashing listed there. For an included sanitizing task, Contractor shall follow the stated procedure, applicable sanitation requirements and product-label directions. Client remains responsible for all other operational sanitation and between-visit requirements.
@ENDIF

@IF IF_SCOPE_ANY:floor-included
### A#4. FLOOR CLEANING
(a) Included floor work is {{SCOPE:floor-included}} in the Included Areas, using commercially reasonable products and methods appropriate to the surfaces actually encountered and following available manufacturer instructions where applicable. Client shall disclose a surface requiring special treatment, and a resulting change in scope or cost is subject to Sections 8 and 9. Excluded floor work is {{SCOPE:floor-excluded}}, unless added under Section 9.
(b) Contractor shall use appropriate wet-floor warnings and reasonable barriers during work, leave floors as dry as reasonably practicable, and communicate any remaining hazard before departure. Client shall inspect before reopening and manage conditions occurring afterward; that inspection obligation does not waive the liability of Contractor for hazards it creates.
@ENDIF

@IF IF_SCOPE_ANY:restroom
### A#5. RESTROOM CLEANING
Included restroom work consists of {{SCOPE:restroom}}. Only the restroom tasks expressly listed above are included. Specialized biohazard, bloodborne-pathogen, sharps and sewage remediation remain excluded under Sections 24 and 25.
@ENDIF

@IF IF_TRASH_SCOPE
### A#6. TRASH AND WASTE
Contractor shall move ordinary collected trash to lawful receptacles or dumpsters designated by Client on or behind the property, or another mutually designated lawful disposal point at the Premises. Client shall identify accessible receptacles for ordinary waste, recycling and separately collected organics. Contractor shall preserve required separation and shall not place commercial waste in public litter baskets, discharge cleaning waste into the street or perform off-site hauling. Client arranges lawful waste collection.
@ENDIF

@IF IF_INTERIOR_GLASS_SCOPE
### A#7. INTERIOR GLASS AND WINDOWS
Cleaning of the interior windows and interior glass identified in A1 is included to the extent safely reachable from the floor with ordinary extension tools. Exterior window cleaning and elevated-access work remain subject to the exclusions and approval requirements in Section 25.
@ENDIF

### A#8. BASELINE AND DEEP CLEANING
Deep cleaning and restoration are excluded except as expressly included in this exhibit or added by signed Change Order. Section 25 applies. Initial deep cleaning or restoration and its price require separate approval. Heavier soil does not authorize a surcharge; Contractor shall explain the issue and request an agreed solution.

BASELINE WALKTHROUGH DATE AND RECORD: {{BASELINE_WALKTHROUGH_IF_SET}}.
INITIAL-WORK CHANGE ORDER: {{INITIAL_WORK_CHANGE_ORDER_IF_SET}}.

{{SCOPE_ADDITIONAL}}

@ENDIF
@IF IF_SCOPE_SIMPLIFIED
## EXHIBIT A
## SCOPE OF WORK

The Parties have agreed the service scope separately through their service discussions, walkthrough, written instructions, service specifications, or other mutually accepted directions.

Contractor will perform the mutually agreed commercial cleaning services at the Premises according to the agreed schedule.

Material additional work or services outside the agreed scope require separate approval in accordance with Section 9.

@ENDIF

## EXHIBIT B
## SCHEDULE, PRICING, SUPPLIES AND CONTACTS

### B1. SERVICE DATES AND SCHEDULE
|Service Commencement Date|{{SERVICE_COMMENCEMENT_DATE}}
|Minimum Commitment End Date|{{MINIMUM_COMMITMENT_END_DATE}}, being {{MINIMUM_COMMITMENT_MONTHS}} calendar months after the Service Commencement Date. This is the first date on which termination for convenience may take effect, provided the notice requirement of {{TERMINATION_NOTICE_DAYS}} calendar days is satisfied.{{IF_MINIMUM_COMMITMENT}}
|Initial Term End Date|{{INITIAL_TERM_END_DATE}}. The Initial Term runs from the Service Commencement Date through the day immediately preceding its {{INITIAL_TERM_MONTHS}}-month anniversary, subject to earlier termination. Thereafter the Agreement continues {{RENEWAL_TYPE}} under Section 3(d).{{IF_MINIMUM_COMMITMENT}}
|Term and termination|{{RENEWAL_TYPE_CAP}} from the Service Commencement Date. No minimum service commitment applies. Either Party may terminate ongoing Services with at least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice under Section 4.{{IF_NO_MINIMUM_COMMITMENT}}
|Frequency|{{SERVICE_FREQUENCY_TEXT_CAP}}, {{WEEK_DEFINITION}}.
|Regular service {{SERVICE_DAY_NOUN}}|{{SERVICE_DAYS}}, subject to mutually agreed scheduling changes under Section 5.
|Arrival window|{{ARRIVAL_WINDOW}}, {{SERVICE_TIMEZONE}}.
|Required completion time|{{COMPLETION_TIME_IF_SET}}
|Convenience termination|At least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice, taking effect no earlier than the Minimum Commitment End Date. Cause, safety and force-majeure rights remain as stated in the Agreement.{{IF_MINIMUM_COMMITMENT}}

### B2. PRICING AND PAYMENT
|Recurring pre-tax fee|{{PRE_TAX_PRICE}} per completed scheduled visit.{{IF_PER_VISIT_PRICING}}
|Weekly pre-tax service fee|{{PRE_TAX_PRICE}} per calendar week, for {{SERVICE_FREQUENCY_TEXT}}.{{IF_WEEKLY_FLAT_PRICING}}
|Sales tax at {{SALES_TAX_RATE}}|{{SALES_TAX_AMOUNT}} per completed scheduled visit, adjusted to the legally applicable rate under Section 7.{{IF_PER_VISIT_PRICING}}
|Sales tax at {{SALES_TAX_RATE}}|{{SALES_TAX_AMOUNT}} per weekly billing period, calculated on the weekly fee and adjusted to the legally applicable rate under Section 7.{{IF_WEEKLY_FLAT_PRICING}}
|Total at that tax rate|{{TOTAL_PRICE}} per completed scheduled visit.{{IF_PER_VISIT_PRICING}}
|Weekly total at that tax rate|{{TOTAL_PRICE}} per calendar week.{{IF_WEEKLY_FLAT_PRICING}}
|Billing cadence|Invoices are issued {{BILLING_CADENCE_TEXT}}.
|Invoicing|{{INVOICE_TIMING}}
|Payment deadline|{{PAYMENT_DEADLINE_HOURS}} hours before the agreed arrival window begins, under Section 11(a).{{IF_PER_VISIT_PRICING}}
|Payment deadline|{{PAYMENT_DEADLINE_HOURS}} hours before the arrival window of the first visit the weekly invoice covers, under Section 11(a).{{IF_WEEKLY_FLAT_PRICING}}
|Payment method|{{PAYMENT_METHOD}}, under Section 11(b).
|Makeup visits|By mutual agreement within {{MAKEUP_WINDOW_DAYS}} calendar days of the original visit, under Section 15.
|Short-notice cancellation|Capped at {{CANCELLATION_PERCENT}} of the pre-tax fee, initially {{CANCELLATION_AMOUNT}}, plus tax only if legally required. A ceiling on reasonable documented net loss under Sections 11 and 15, not an automatic charge.{{IF_PER_VISIT_PRICING}}
|Per-visit allocation|For the caps below and in Sections 14, 15, 24 and 29 only: one visit is the weekly pre-tax fee divided by the {{VISITS_PER_PERIOD}} visits scheduled in the week. This is not a separate price.{{IF_WEEKLY_FLAT_PRICING}}
|Short-notice cancellation|Capped at {{CANCELLATION_PERCENT}} of one visit's allocated share of the weekly pre-tax fee, initially {{CANCELLATION_AMOUNT}}, plus tax only if legally required. A ceiling on reasonable documented net loss under Sections 11 and 15, not an automatic charge.{{IF_WEEKLY_FLAT_PRICING}}
|Failed access|Capped at the pre-tax visit fee, initially {{LOCKOUT_FEE}}, plus legally applicable tax, on the conditions of Section 14. No duplicate cancellation charge.{{IF_PER_VISIT_PRICING}}
|Failed access|Capped at one visit's allocated share of the weekly pre-tax fee, initially {{LOCKOUT_FEE}}, plus legally applicable tax, on the conditions of Section 14. No duplicate cancellation charge.{{IF_WEEKLY_FLAT_PRICING}}
|Late payment|Simple interest at {{LATE_CHARGE_PERCENT}} per month ({{LATE_CHARGE_ANNUAL_PERCENT}} per year) on undisputed amounts overdue by more than {{INTEREST_GRACE_DAYS}} calendar days, under Section 11(f).
|Returned payments|Returned or failed payments attributable to Client permit recovery of actual reasonable costs up to {{RETURNED_PAYMENT_FEE_WORDS}} under Section 11(g).
|Price changes|Changes to the pre-tax fee require mutual signed agreement under Section 8.
|Aggregate liability cap|{{LIABILITY_CAP_MULTIPLE}} times the recurring pre-tax per-visit fee under Section 29(b), initially {{LIABILITY_CAP_AMOUNT}}.{{IF_PER_VISIT_PRICING}}
|Aggregate liability cap|{{LIABILITY_CAP_MULTIPLE}} times one visit's allocated share of the weekly pre-tax fee under Section 29(b), initially {{LIABILITY_CAP_AMOUNT}}.{{IF_WEEKLY_FLAT_PRICING}}

### B3. SUPPLIES, EQUIPMENT AND CONSUMABLES
(a) Each Party supplies, at its own cost, and maintains adequate stock of the items allocated to it below, under Section 6.

|Item|Provided by
|Cleaning equipment, tools, chemicals, products and ordinary cleaning supplies|{{EQUIPMENT_PROVIDED_BY}}
|Trash bags and liners|{{TRASH_LINERS_PROVIDED_BY}}
|Paper towels|{{PAPER_TOWELS_PROVIDED_BY}}
|Toilet tissue|{{TOILET_TISSUE_PROVIDED_BY}}
|Other agreed consumables|{{OTHER_CONSUMABLES}}

(b) Any special product or brand requirement must be identified in writing before service; a change affecting scope or cost is subject to Sections 8 and 9.

### B4. AUTHORIZED REPRESENTATIVES AND CONTACTS
The representatives designated below may approve amendments and Change Orders. Operational contacts may handle scheduling, access and urgent notices; that role alone does not confer authority to amend substantive terms. Each Party shall maintain monitored operational contact methods, including for weekend service.

|Contractor legal name|{{CONTRACTOR_DISPLAY_NAME}}
|Contractor authorized representative|{{CONTRACTOR_REPRESENTATIVE}}
|Contractor approval email|{{CONTRACTOR_APPROVAL_EMAIL_IF_SET}}
|Contractor notice mailing address|{{CONTRACTOR_ADDRESS}}
|Contractor notice email|{{CONTRACTOR_NOTICE_EMAIL}}
|Contractor operational email|{{CONTRACTOR_OPERATIONAL_EMAIL}}
|Contractor supervisor and primary on-call contact|{{CONTRACTOR_SUPERVISOR}}
|Contractor backup on-call contact|{{CONTRACTOR_BACKUP_CONTACT}}
|Client legal name|{{CLIENT_LEGAL_NAME}}
|Client authorized representative|{{CLIENT_REPRESENTATIVE}}
|Client approval email|{{CLIENT_APPROVAL_EMAIL_IF_SET}}
|Client notice email|{{CLIENT_NOTICE_EMAIL}}
|Client operational email|{{CLIENT_OPERATIONAL_EMAIL}}
|Client primary on-call contact|{{CLIENT_ON_CALL_CONTACT}}
|Client backup on-call contact|{{CLIENT_BACKUP_CONTACT}}

Contact changes shall be communicated under Section 32. Actual keys, alarm codes, passwords and bank credentials shall be exchanged separately through appropriate verified channels.

@IF IF_ANY_ENDORSEMENT
### B5. ADDITIONAL INSURANCE ENDORSEMENTS
The following endorsements beyond the coverage required by Section 20 are agreed for this engagement. Each must appear in an actual insurer-issued endorsement, is subject to Section 20(d), and must be obtained before the work for which it is required.

|Additional endorsements agreed for this engagement|{{AGREED_ENDORSEMENTS_IF_SET}}
|Insurer, policy, endorsement form and edition, protected entity and applicable work|{{ENDORSEMENT_DETAILS_IF_SET}}
|Agreed additional premium or price adjustment|{{ENDORSEMENT_PREMIUM_IF_SET}}
@ENDIF

## SIGNATURES
By signing below, each Party agrees to this Master Service Agreement, including Sections 1 through 36, {{EXHIBITS_SIGNED}}, and the representations concerning authority in the introductory paragraph.

@SIGNATURE_BLOCK
""";
    }
}
