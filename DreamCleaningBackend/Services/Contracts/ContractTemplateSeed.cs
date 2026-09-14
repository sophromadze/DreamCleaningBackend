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
        /// 2.1 - the attorney-drafted agreement, with hand soap struck out of it entirely.
        ///
        /// IT IS A NEW ROW RATHER THAN A CORRECTION TO 2.0, and the reason is the seeder: it
        /// matches on Name AND Version and INSERTS ONLY WHAT IS MISSING, so once a "2.0" row
        /// exists in a database no later edit to <c>BodyText</c> can ever reach it. A 2.0 seeded
        /// from the pre-correction body therefore kept promising hand soap, and kept printing the
        /// signature block above both exhibits, on every contract drafted from it - with the seed
        /// file in front of you saying otherwise. Editing the body without moving the version is
        /// the failure this constant exists to prevent; <c>ContractSeedService</c> now also logs
        /// loudly when a stored body has drifted from the seed at the same version.
        ///
        /// 1.0/1.1 were the pre-review wording and 2.0 is the soap draft. None of them stay in
        /// the picker: superseded legal text left selectable is an invitation to issue it by
        /// accident. <c>ContractSeedService</c> retires them rather than deleting them, so a
        /// version that did render still resolves its frozen body.
        /// </summary>
        public const string TemplateVersion = "2.1";

        public const string TemplateDescription =
            "Attorney-drafted commercial MSA: Sections 1-35, Exhibit A (scope of work and recorded "
            + "site details), Exhibit B (pricing, billing, insurance and contacts), then the "
            + "signature block last. Contractor supplies no hand soap.";

        /// <summary>
        /// Seeded versions this template supersedes. <c>ContractSeedService</c> deactivates them
        /// on startup so they leave the picker; nothing is deleted, because a template row is
        /// still what an audit trail points at even when no version was ever generated from it.
        /// </summary>
        public static IReadOnlyList<string> SupersededVersions => new[] { "1.0", "1.1", "2.0" };

        public const string BodyText = """
# MASTER SERVICE AGREEMENT
# COMMERCIAL CLEANING SERVICES

This Master Service Agreement (the "Agreement") is entered into as of {{EFFECTIVE_DATE}} (the "Effective Date") by and between {{CONTRACTOR_LEGAL_NAME}}, {{CONTRACTOR_ENTITY_TYPE}} doing business as {{CONTRACTOR_DBA}}, with its principal office at {{CONTRACTOR_ADDRESS}} ("Contractor"), and {{CLIENT_LEGAL_NAME}}, {{CLIENT_ENTITY_DESCRIPTION}}, with its business mailing address at {{CLIENT_NOTICE_MAILING_ADDRESS}} ("Client"). Contractor and Client are each a "Party" and together the "Parties."

Each Party represents that it is duly organized and authorized to enter into this Agreement, and each individual signing on behalf of a Party represents that the individual has authority to bind that Party. Client represents that it has authority to engage Contractor and grant access for the Services at the Premises. Neither the use of a brand name nor identification of the Premises makes any franchisor, landlord, affiliate, or individual signatory a party or guarantor.

## 1. SERVICES AND PREMISES
(a) Contractor shall provide commercial cleaning services (the "Services") at the location identified in Exhibit A (the "Premises"), in accordance with the scope, tasks and frequencies set out in Exhibit A.
(b) The Premises are the {{PREMISES_DESCRIPTION}} operated by Client and located at {{SERVICE_FULL_ADDRESS}}. The Premises address is the service location only and is not necessarily Client's legal or principal business address.
(c) Exhibit A and Exhibit B are incorporated into and form part of this Agreement.
(d) Services not expressly described in Exhibit A are not included and are governed by Section 9.
(e) Contractor shall perform the expressly included Services with reasonable care and skill consistent with ordinary professional commercial cleaning. Contractor does not undertake continuous inspection, comprehensive premises management, or responsibility for conditions arising between visits or for excluded work. Client retains responsibility for operating and maintaining the Premises, including arranging cleaning and sanitation at other times. This allocation does not excuse Contractor's negligent performance or any duty imposed by applicable law.

## 2. ORDER OF PRECEDENCE
The documents forming this Agreement are this Agreement, Exhibits A and B, and amendments or Change Orders signed by authorized representatives of both Parties. A later signed amendment or Change Order controls only the subjects it expressly changes. Otherwise, this Agreement controls legal terms, Exhibit B controls the expressly stated price and billing details, and Exhibit A controls the specifically included tasks and areas. No proposal, website, advertising material, purchase order, invoice boilerplate, vendor portal term, or other document changes this Agreement unless expressly incorporated by a signed amendment. No document authorizes unlawful work or overrides a mandatory legal requirement. Scheduling confirmations under Section 5 govern only the agreed schedule and do not amend other terms.

## 3. TERM, MINIMUM COMMITMENT PERIOD AND RENEWAL
(a) This Agreement is effective on the Effective Date. Recurring Services commence on the Service Commencement Date stated in Exhibit B. The Initial Term runs for {{INITIAL_TERM_MONTHS}} months from the Service Commencement Date, subject to earlier termination under Section 4.
(b) The Minimum Commitment Period begins on the Service Commencement Date and ends immediately before the Minimum Commitment End Date stated in Exhibit B, {{MINIMUM_COMMITMENT_MONTHS}} calendar months later. That End Date is the first date on which termination for convenience may take effect. The Minimum Commitment Period restricts termination for convenience only and does not restrict termination for cause, lawful safety measures, or termination under Section 30.
(c) The recurring contracted service frequency is {{SERVICE_FREQUENCY_TEXT}}, as described in Exhibits A and B. That obligation applies throughout the service term, including the Minimum Commitment Period, the remainder of the Initial Term and any {{RENEWAL_TYPE}} continuation, subject to the express adjustments and remedies in Sections 11, 14, 15, 24 and 30.
(d) Upon expiration of the Initial Term, this Agreement continues automatically on a {{RENEWAL_TYPE}} basis on the same terms and does not renew for a further fixed term. Either Party may terminate that continuation on at least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice under Section 4. "{{RENEWAL_TYPE_CAP}}" means successive periods of one calendar month, subject to earlier termination under this Agreement.

## 4. TERMINATION
(a) Either Party may terminate for convenience on at least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice. Notice may be delivered during the Minimum Commitment Period, but termination for convenience shall not take effect before the Minimum Commitment End Date. Services and payment obligations continue until the effective termination date, subject to the express cancellation, suspension, safety, and force-majeure provisions of this Agreement.
(b) Either Party may terminate this Agreement at any time, including during the Minimum Commitment Period, if the other Party materially breaches this Agreement and fails to cure the breach within {{CURE_PERIOD_DAYS}} calendar days after receiving written notice describing it.
(c) Contractor may suspend Services as provided in Sections 11 and 24. Contractor may terminate for nonpayment of an undisputed amount after that amount remains unpaid for {{PAST_DUE_DAYS}} calendar days following written demand identifying the amount due. Either Party may terminate immediately by written notice if continued performance would be unlawful or expose persons to an imminent serious danger that cannot reasonably be eliminated through suspension or other protective measures. Other remediable safety-related breaches remain subject to paragraph (b).
(d) Upon termination, Client shall pay earned fees for Services performed and properly supported charges accrued under this Agreement. Contractor shall provide a final itemized statement and refund unearned prepayments and unapplied credits, after applying undisputed amounts due, within {{CREDIT_RETURN_DAYS}} calendar days. A disputed deduction shall be identified and supported; any undisputed refund shall not be delayed pending resolution of the dispute. No remaining annual fees are automatically accelerated. For wrongful termination or repudiation, the nonbreaching Party retains a claim for proven direct damages, subject to mitigation, Section 29, and no duplicate recovery. Contractor's claim for unperformed future visits shall be limited to its proven net loss through the earliest date on which Client could have validly terminated for convenience had notice been given on the breach date. Accrued payment and refund duties, confidentiality, claims procedures, lawful indemnity, liability limitations, governing law, dispute resolution, and provisions needed to enforce those rights survive to the extent their nature requires.

## 5. SCHEDULING AND SERVICE WINDOW
(a) Contractor shall perform {{SERVICE_FREQUENCY_TEXT}} during the service term, subject to Sections 11, 14, 15, 24 and 30. A {{SERVICE_PERIOD}} is {{WEEK_DEFINITION}}. A makeup visit satisfies the commitment for the {{SERVICE_PERIOD}} in which the original visit was scheduled and does not replace another {{SERVICE_PERIOD}}'s visit unless expressly agreed. Contractor shall maintain a service ledger identifying the original {{SERVICE_PERIOD}} and the status of each visit, makeup, charge and credit.
(b) The regular service {{SERVICE_DAY_NOUN}} {{SERVICE_DAY_VERB}} {{SERVICE_DAYS}}, subject to a mutually confirmed change under paragraph (c).
(c) Unless the Parties confirm a different window in writing, Contractor's arrival window is {{ARRIVAL_WINDOW}}, {{SERVICE_TIMEZONE}}. The Parties shall state any required completion time in Exhibit A. Changes to the day, arrival window, or completion deadline require mutual written confirmation and reasonable availability. Contractor shall promptly notify Client of a material delay. Failed-access charges do not apply when Contractor arrives outside the agreed window unless Client has accepted that revised arrival time.
(d) Scheduling changes under this Section may be confirmed in writing, including by email, and do not constitute an amendment to substantive terms or a Change Order under Section 9. A request alone does not change the agreed schedule; both Parties must confirm the change.
(e) A change to the service day, arrival window or completion deadline does not change the commitment of {{SERVICE_FREQUENCY_TEXT}}, subject to Sections 14 and 15.
(f) {{CLOSED_PREMISES_TEXT}} Client shall provide the key or other access necessary to enter the Premises in accordance with Section 13.

## 6. FEES, ALL-INCLUSIVE PRICING AND SUPPLIES
(a) Client shall pay the fees in Exhibit B. The recurring service fee is {{PRE_TAX_PRICE}} before tax per completed scheduled visit. Applicable sales tax is added under Section 7. Charges for missed, interrupted or deficient visits are governed by the specific provisions of this Agreement.
(b) The recurring fee includes all labor, supervision, cleaning equipment, tools, chemicals, products and ordinary cleaning supplies used by Contractor's personnel to perform the included Services. Contractor supplies them at its own expense. No separate equipment or cleaning-product charge applies.
(c) Client supplies, at its own cost, toilet tissue, paper towels and trash can liners or trash bags. Client shall maintain adequate stocks of its allocated consumables. If stocks are insufficient, Contractor shall notify Client; Contractor is not required to purchase substitutes unless Client approves the purchase and price in writing.
(d) All-inclusive pricing does not mean unlimited services. The fee covers only the tasks and frequencies in Exhibit A. Deep cleaning and restoration-level cleaning are not included unless expressly stated in Exhibit A or added by a signed Change Order under Section 9.

## 7. TAXES
(a) Applicable sales, use and similar transaction taxes shall be calculated on taxable charges, added to amounts due and separately stated on each invoice as required by law.
(b) Exhibit B shows the completed-visit calculation at {{SALES_TAX_RATE}} sales tax: {{PRE_TAX_PRICE}} before tax, {{SALES_TAX_AMOUNT}} sales tax and {{TOTAL_PRICE}} total. If the legally applicable rate changes, the pre-tax fee remains unchanged, and the tax and resulting total adjust as required by law.
(c) Client claiming a tax exemption shall provide a valid exemption certificate before the first invoice date. Taxes shall be handled in accordance with law. Client is responsible for a later tax assessment attributable to its invalid or expired exemption claim. Any later assessment and related amount charged to Client must be supported and attributable to that claim, rather than Contractor's independent error.
(d) References to a "service fee" in cancellation, failed-access, or damage calculations mean the pre-tax fee unless expressly stated otherwise. Tax shall be added to a particular charge only to the extent required by law. Contractor shall separately identify taxable services, cancellation or damage charges, credits, and any related tax adjustment. Contractor is responsible for its own income, payroll, and employment taxes and for timely remittance of sales tax it collects. Client shall not bear penalties attributable to Contractor's failure to remit tax properly collected from Client.

## 8. CHANGES TO PRICING
(a) A change to the recurring pre-tax service fee requires mutual written agreement documented by a signed amendment or signed Change Order. Neither Party may change that fee unilaterally.
(b) A legally required sales-tax rate change adjusts tax and the invoice total under Section 7 and does not change the agreed pre-tax fee.
(c) Contractor may request a prospective price review by providing at least {{PRICE_REVIEW_NOTICE_DAYS}} calendar days' written notice explaining a material change in agreed scope, mandatory labor costs, or other performance costs. No requested increase takes effect unless both Parties sign the revised price. If agreement is not reached, either Party may exercise its existing termination rights under Section 4; no retroactive increase applies.

## 9. CHANGE ORDERS AND WORK OUTSIDE SCOPE
(a) Any material change to the scope, recurring service frequency, Premises or substantive service requirements requires a written Change Order signed by authorized representatives of both Parties.
(b) Ordinary mutually agreed adjustments to the day, arrival window or completion deadline of an individual scheduled visit do not require a Change Order. Email confirmation is sufficient under Section 5(d).
(c) No verbal request, text message or informal employee instruction obligates Contractor to perform work outside Exhibit A. Occasional performance of an extra task does not by itself amend the recurring scope, subject to applicable law.
(d) A Change Order shall identify the additional or changed service, location, frequency, price, effective date and whether it is one-time or recurring, together with any changed safety, access or insurance requirements.
(e) A Change Order may be signed through an electronic-signature platform or by an exchange of emails from the authorized representatives identified in Exhibit B, each expressly approving the attached scope and price and including a typed signature intended to authenticate that approval. Routine scheduling messages, employee instructions, and purchase-order or vendor-portal boilerplate do not constitute a Change Order. Contractor may decline additional work until approval is complete.

## 10. RECURRING SERVICE COMMITMENT
Client engages Contractor for {{SERVICE_FREQUENCY_TEXT}} during the service term. A scheduling request does not by itself cancel that commitment. Exceptions, makeup visits, and charges for missed visits are governed exclusively by Sections 11, 14, 15, 24 and 30. Payment of a permitted missed-visit charge settles the charge for that particular visit and does not authorize a continuing reduction in service frequency. Repeated refusal to schedule or permit the agreed Services may be addressed as a material breach only in accordance with Sections 4 and 15. Contractor may not charge both the full price of an unperformed visit and a cancellation, failed-access, or suspension charge for the same visit.

## 11. INVOICING, ADVANCE PAYMENT AND CHARGES
(a) Contractor shall ordinarily issue the invoice at least {{INVOICE_LEAD_DAYS}} calendar days before each scheduled visit. Payment of undisputed amounts is due {{PAYMENT_DEADLINE_HOURS}} hours before the agreed arrival window begins. If an invoice is delivered fewer than {{LATE_INVOICE_THRESHOLD_DAYS}} calendar days before that deadline, Client shall have at least {{LATE_INVOICE_GRACE_BUSINESS_DAYS}} business days after receipt to pay, and the Parties shall agree whether to proceed or reschedule without a Client cancellation charge caused solely by that late invoice. The first visit may use a different payment deadline expressly agreed in writing.
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
(c) Client shall designate primary and backup on-call contacts in Exhibit B, identify the food-service permit holder and provide applicable landlord, franchisor and site requirements affecting access, products, insurance or the Services before work begins. Requirements changing the agreed scope or cost are subject to Sections 8 and 9.

## 17. PERSONNEL AND SUBCONTRACTORS
(a) Contractor controls the staffing, work scheduling and supervision of its personnel, subject to the agreed service schedule. Contractor may substitute qualified personnel; substitution alone is not a breach.
(b) Client may request removal of an individual for a documented, reasonable safety, security, or material performance concern. Requests may not be discriminatory, retaliatory, or otherwise unlawful. Contractor shall investigate promptly, take appropriate interim measures, and provide a qualified replacement within a reasonable time. Contractor retains employment and supervisory decisions, subject to applicable law.
(c) Contractor may perform through its employees or qualified subcontractors and remains responsible for performance of the Services in either case.
(d) Contractor shall use personnel qualified and trained for their assigned tasks and require subcontractors to comply with the applicable service, confidentiality, safety, employment, and insurance obligations. Contractor shall provide advance notice before giving a new subcontracting entity unattended access to the Premises. Client may raise reasonable documented security objections, and the Parties shall promptly resolve them without creating a general right to direct Contractor's workforce. Subcontracting does not release Contractor from its responsibilities under this Agreement.

## 18. PERSONNEL COORDINATION
Client shall direct requests about service scope, quality, and staffing to Contractor's designated supervisor. Neither Party shall use or disclose the other Party's confidential information except as permitted by Section 27. This Agreement imposes no restriction or fee on lawful solicitation, recruitment, or hiring of any individual, and no provision shall be interpreted to restrict an individual's lawful employment opportunities.

## 19. INDEPENDENT CONTRACTOR
Contractor is an independent contractor. Nothing in this Agreement creates an employment, partnership, joint venture or agency relationship between the Parties. As between the Parties, Contractor is responsible for hiring, supervision, compensation, payroll, and employment obligations for its personnel. Client may specify service outcomes and reasonable site safety rules but shall ordinarily communicate instructions through Contractor's supervisor. The Parties' description of their relationship does not override an employment, joint-employment, tax, or other classification imposed by applicable law.

## 20. INSURANCE
(a) Contractor shall maintain throughout performance Commercial General Liability insurance, written on an occurrence basis, with limits of at least {{INSURANCE_PER_OCCURRENCE}} each occurrence and {{INSURANCE_AGGREGATE}} general aggregate.
(b) Contractor shall maintain workers' compensation, disability benefits, and Paid Family Leave coverage to the extent required by {{INSURANCE_JURISDICTION}} law, and shall provide the applicable evidence of coverage or valid exemption documentation. Contractor shall require legally appropriate coverage from its subcontractors.
(c) Before Services begin and upon renewal, Contractor shall provide certificates evidencing required coverage. Any agreed additional-insured status, primary and noncontributory wording, or waiver of subrogation must be stated in an actual insurer-issued endorsement and listed in Exhibit B; a certificate alone does not amend the policy. Contractor shall promptly notify Client after learning of cancellation, lapse, or a material reduction affecting the Services. No insurance obligation requires indemnification for Client's own negligence contrary to law.
(d) Any insurance requirement exceeding the coverages above must be stated in Exhibit B or agreed in a signed writing. The Parties must agree on any additional premium or price adjustment under Section 8 before Contractor is obligated to obtain an additional endorsement. Any required endorsement is subject to insurer issuance and must be obtained before the work for which it is required.
(e) Client shall maintain premises-liability and property coverage appropriate to its {{PREMISES_TYPE}} operations. Neither Party's insurance reduces its responsibilities under this Agreement or liability that cannot lawfully be limited.

## 21. DAMAGE CLAIMS
Client shall notify Contractor of alleged damage promptly and, where reasonably practicable, within {{DAMAGE_NOTICE_BUSINESS_DAYS}} business days after discovery, with a description and available photographs or other evidence. Contractor shall likewise promptly report damage or an incident it discovers. Each Party shall reasonably preserve evidence and permit inspection. Emergency action reasonably necessary to protect persons, food safety, property, or continued safe operation may proceed without awaiting inspection, with notice and documentation as soon as practicable. Neither delayed notice nor emergency mitigation automatically waives a claim. Any consequence of delayed notice is limited to actual material prejudice proved by the affected Party and permitted by law. This Section does not shorten a statutory limitations period or exempt either Party from liability that cannot lawfully be limited. No double recovery is permitted; compensation shall reflect the legally recoverable loss rather than an unrelated upgrade.

## 22. QUALITY GUARANTEE AND CORRECTION
(a) Client shall identify any material failure to complete an included task with the agreed standard of care within {{QUALITY_COMPLAINT_HOURS}} hours after the visit, or promptly after discovery if it was not reasonably apparent earlier. The notice should describe the task and condition and include reasonably available evidence. Later re-soiling or work outside Exhibit A is not a service deficiency attributable to Contractor.
(b) Contractor shall promptly inspect as reasonably necessary and have a reasonable first opportunity to re-perform the deficient task without charge, ordinarily within {{QUALITY_CORRECTION_BUSINESS_DAYS}} business days after notice and access, or sooner when reasonably necessary for food safety. Client shall provide reasonable corrective access.
(c) If Contractor declines or fails to correct within that period, or correction is no longer reasonably useful, Client may receive a reasonable credit or refund attributable to the deficient portion of the visit. For ordinary quality deficiencies, that adjustment and re-performance are the agreed first remedies. No unrelated invoice amount may be withheld solely because of a quality complaint.
(d) This procedure does not bar reasonable emergency mitigation, termination for an uncured material breach, or claims for personal injury, property damage, fraud, gross negligence, willful misconduct, or other liability that cannot lawfully be limited. Damage claims are governed by Section 21, and applicable limitations by Section 29. Refunds and other recovery may not duplicate compensation for the same loss.

## 23. CLEANING CONDITIONS AND RESULTS
(a) The included Services address ordinary surface dirt, dust, and light grease within the baseline condition and tasks agreed in Exhibit A, using appropriate commercially reasonable methods and products. Heavy accumulation, restoration, and specialized degreasing are excluded unless added by signed Change Order.
(b) Results may vary with age, condition, material, finish, staining, deterioration, prior damage and maintenance history. Cleaning is not restoration, refinishing or repair. Contractor does not warrant removal of permanent stains, etching, rust, discoloration, worn or damaged finishes, deteriorated grout or soil bonded to a surface over time.
(c) Pre-existing wear or damage does not make Contractor responsible for restoration, but does not excuse new damage caused by negligent product selection or performance. Contractor shall use surface-appropriate products, follow available manufacturer instructions, and conduct a reasonable inconspicuous compatibility check where warranted. Contractor does not guarantee an inspection grade, regulatory approval, sterilization, or elimination of all microorganisms.
(d) The Parties shall document the baseline condition before the first recurring visit. Service photographs shall focus on surfaces and relevant damage, avoid people and sensitive information where reasonably practicable, and be used only for legitimate service, insurance or dispute purposes, subject to Section 27.

## 24. HAZARDOUS CONDITIONS AND STOPPING WORK
(a) Contractor may immediately stop the affected work when it reasonably encounters a serious safety hazard or a condition outside its personnel's training, equipment, or lawful scope, including sharps, uncontrolled blood or biohazard contamination, suspected asbestos, substantial mold, sewage backup, dangerous chemical spills, electrical hazards, or unsafe structures. Contractor shall promptly notify Client, identify the affected area, and perform unaffected work where reasonably safe and practicable. Routine toilet and restroom cleaning within ordinary sanitary conditions remains included; specialized remediation does not.
(b) If the condition was not caused by Contractor and results from a known hazard that Client failed to disclose or address, Client shall pay for work actually completed and reasonable, documented unavoidable loss attributable to the interruption, after avoided costs and net replacement earnings, with the aggregate limited to the affected visit's pre-tax fee plus legally applicable tax. No duplicate cancellation or failed-access charge applies. For an emergency beyond Client's reasonable control, Section 15(d) controls. No charge applies for work not performed because of a hazard caused by Contractor. Contractor shall resume once the condition is safely remedied and the Parties agree on access; persistent material conditions may be addressed under Section 4.

## 25. EXCLUDED SERVICES
(a) The following services are excluded unless expressly added by a signed Change Order, legally appropriate, and performed by a contractor with all required qualifications, training, licenses and insurance:
(b) Kitchen exhaust hood, hood-filter and exhaust duct cleaning; grease trap or grease interceptor service; internal cleaning of fryers, ovens, grills or other equipment; dismantling equipment; pulling out or moving connected appliances; and cleaning inaccessible areas behind equipment.
(c) Pest control and extermination; mold remediation; biohazard, bloodborne-pathogen and sewage remediation; handling needles or sharps; handling or disposal of hazardous materials or waste; and asbestos-related work.
(d) Exterior window cleaning; work requiring ladders, lifts, scaffolds, suspended equipment or work on elevated surfaces beyond safe reach from the floor with ordinary extension tools, unless a specific safe method and scope have been approved in a signed Change Order and all legal, training and insurance requirements have been satisfied.
(e) Food preparation, food handling and dishwashing; and food-contact surface cleaning and sanitizing except a specifically identified task expressly included in Exhibit A. Routine wiping of cleared dining tables does not by itself include specialized food-contact sanitizing.
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
(a) Formal notices of breach, termination, assignment, or dispute shall be sent to the designated notice email or delivered personally or by recognized courier to the designated mailing address. An email is deemed received on the next business day after sending if the sender retains a transmission record and receives no delivery-failure message; if the sender knows delivery failed, another permitted method must be used. Personal or courier delivery is effective on documented receipt. A business day excludes Saturdays, Sundays, and {{GOVERNING_LAW_STATE}} State public holidays. All times refer to {{SERVICE_TIMEZONE}}.
(b) Scheduling, cancellation, access, and urgent safety notices may be sent to the operational email and on-call contact designated in Exhibit B, including on weekends. For the {{TIMELY_RESCHEDULE_HOURS_WORDS}}-hour cancellation calculation, an email to the operational address is effective when sent, provided it is not returned undeliverable. Text messages are effective for operational notice only when acknowledged by the designated recipient. Operational communications do not amend price, substantive scope, or liability terms. A method expressly required by statute controls over this Section.
(c) The designated notice mailing addresses, notice emails, operational emails and on-call contacts are stated in Exhibit B. A Party may update its contact information by formal notice under this Section. Actual access credentials shall not be included in those notices or in the circulated Agreement.

## 33. GOVERNING LAW AND VENUE
{{GOVERNING_LAW_STATE}} law governs this Agreement, without regard to its conflict-of-laws rules. Subject to Section 34 and mandatory law, the Parties consent to exclusive venue in the state courts sitting in {{VENUE_COUNTY}}, {{GOVERNING_LAW_STATE}}, and, only where federal subject-matter jurisdiction independently exists, {{FEDERAL_VENUE}}. Nothing restricts a lawful claim before an agency or a court or tribunal whose jurisdiction cannot be waived by agreement.

## 34. DISPUTE RESOLUTION AND LEGAL FEES
(a) A Party shall give written notice describing the dispute and requested resolution. Senior representatives shall confer in good faith within {{DISPUTE_DISCUSSION_DAYS}} calendar days and attempt resolution. If unresolved after {{MEDIATION_REQUEST_DAYS}} calendar days, either may request nonbinding mediation in {{MEDIATION_VENUE}} or remotely by agreement. The Parties shall attempt to select a mediator within {{MEDIATOR_SELECTION_DAYS}} calendar days of that request and share the mediator's charges equally. Either Party may sue {{SUIT_AFTER_DAYS}} calendar days after the original dispute notice if the dispute remains unresolved and that Party has reasonably cooperated. Refusal to participate or failure to agree on a mediator does not extend this period.
(b) These preliminary steps do not prevent emergency or provisional relief, filing reasonably necessary to preserve a claim before a limitations deadline, a lawful agency complaint, or an eligible small-claims or commercial-claims proceeding. They do not toll any statutory deadline by themselves.
(c) Either Party may pursue collection of an undisputed amount due under this Agreement after written demand and {{COLLECTION_DEMAND_BUSINESS_DAYS}} business days to pay, without mediation. This includes earned fees due to Contractor and an undisputed refund due to Client. A genuine dispute is not eliminated solely by missing an administrative objection period.
(d) In a direct action between the Parties to recover earned fees, properly supported contractual charges, or a required refund or credit, the substantially prevailing Party may recover reasonable attorneys' fees and court costs attributable to that payment dispute, as determined by the court. The court may apportion or decline an award where the outcome is mixed. This provision expressly covers direct claims between the Parties, including reasonable enforcement and appellate work concerning that payment dispute. For other direct disputes, each Party bears its own attorneys' fees unless a statute or other applicable law authorizes recovery. Third-party defense costs remain governed by Section 28. Duplicate recovery is prohibited.

## 35. GENERAL PROVISIONS
(a) This Agreement, together with Exhibits A and B and any amendment or Change Order incorporated under Section 2, is the entire agreement on its subject matter and supersedes prior discussions, proposals and understandings.
(b) An amendment must be in writing and signed by both Parties' authorized representatives. Electronic approval meeting Section 9(e) is permitted. Scheduling adjustments under Section 5 may be confirmed by email without changing substantive terms.
(c) If a provision is invalid or unenforceable, it shall be severed to the extent required, and the remaining provisions remain effective to the extent they can operate consistently with the Parties' lawful agreement. A court may narrow a provision only where applicable law permits; this clause does not require enforcement or rewriting of an unlawful penalty, exculpation, or restraint on employment. The Parties shall cooperate in adopting a lawful substitute consistent with the original commercial purpose.
(d) A Party's failure to enforce a provision is not by itself a waiver of that provision or another provision. A waiver must be in writing by an authorized representative and applies only to its stated circumstances, subject to applicable law.
(e) This Agreement may be executed in counterparts and delivered electronically. Electronic signatures intended to authenticate execution and scanned signed copies have the same effect as original signatures to the extent permitted by law.
(f) Headings are for convenience and do not affect interpretation. Unless expressly stated otherwise, a reference to a Section means a section of this Agreement; references beginning with A or B refer to the corresponding exhibit. "Calendar days" include weekends and holidays. "Business days" and applicable local time are defined in Section 32.
(g) Except for persons expressly protected by lawful indemnity or an applicable insurance endorsement, this Agreement creates no contractual enforcement right in a person other than the Parties and their permitted successors. It does not eliminate a nonparty's rights under tort law, employment law, or another applicable statute.

## EXHIBIT A
## SCOPE OF WORK

SERVICE PREMISES: {{PREMISES_DESCRIPTION}} operated by Client, {{SERVICE_FULL_ADDRESS}}. Service location only; not necessarily the legal or principal business address of Client.
FREQUENCY: {{SERVICE_FREQUENCY_TEXT_CAP}}, {{WEEK_DEFINITION}}, subject to the express adjustments and remedies of this Agreement.
REGULAR SERVICE {{SERVICE_DAY_NOUN_UPPER}} AND ARRIVAL: {{SERVICE_DAYS}}, {{ARRIVAL_WINDOW}}, {{SERVICE_TIMEZONE}}, subject to mutual written confirmation of changes under Section 5.
REQUIRED COMPLETION TIME: {{COMPLETION_TIME}}.
CONDITIONS: {{CLOSED_PREMISES_TEXT}} Services are performed using {{ACCESS_TYPE}}, in accordance with Section 13.

### A1. INCLUDED AREAS AND TASKS
Included Areas are {{SCOPE:included-areas}}. Each area is included only for the tasks expressly described in this exhibit. Additional areas or tasks require a Change Order under Section 9. The Included Areas control over a general back-of-house exclusion; the limited kitchen scope is not expanded.

Unless expressly assigned a different frequency, each listed task shall be completed at each scheduled visit. Completion means removal of ordinary visible soil reasonably removable with the agreed methods, subject to pre-existing-condition exclusions and applicable sanitation requirements for included sanitizing tasks. Staffing levels and work sequence are Contractor's responsibility; the fee is for completion of the agreed tasks, not a specified number of labor hours. Extra tasks require a Change Order.

|Area|Tasks and limits at each scheduled visit
{{SCOPE_TABLE:area-tasks}}

The Parties shall complete the following site details and record the baseline before the first recurring visit. These details identify the agreed work; an expansion beyond the listed tasks requires a Change Order.

APPROXIMATE SERVICED SQUARE FOOTAGE: {{SQUARE_FOOTAGE}}.
CUSTOMER RESTROOM AND FIXTURE COUNTS: {{CUSTOMER_RESTROOM_COUNTS}}.
EMPLOYEE RESTROOM AND FIXTURE COUNTS: {{EMPLOYEE_RESTROOM_COUNTS}}.
FLOOR AND SURFACE MATERIALS: {{FLOOR_MATERIALS}}.
INCLUDED KITCHEN EQUIPMENT AND EXTERIOR SURFACES: {{KITCHEN_EQUIPMENT_SURFACES}}.
TOUCHPOINTS AND CLEARED SURFACES: {{TOUCHPOINT_LOCATIONS}}.
INTERIOR GLASS AND WINDOW LOCATIONS: {{INTERIOR_GLASS_LOCATIONS}}.
INCLUDED FOOD-CONTACT OR DINING-TABLE SANITIZING TASK, SURFACE, FREQUENCY AND PROCEDURE: {{FOOD_CONTACT_SANITIZING}}. Unless expressly identified here, food-contact cleaning and sanitizing remain the responsibility of Client.
ACCESS METHOD AND CLOSEOUT PROCEDURE REFERENCE: {{ACCESS_METHOD_REFERENCE}}. Actual keys and codes shall be exchanged separately.
EQUIPMENT THAT MUST REMAIN OPERATING AND MANUFACTURER RESTRICTIONS: {{EQUIPMENT_RESTRICTIONS}}.
WASTE, RECYCLING AND SEPARATELY COLLECTED ORGANICS RECEPTACLE LOCATIONS: {{WASTE_RECEPTACLE_LOCATIONS}}.
FOOD-SERVICE PERMIT HOLDER: {{FOOD_PERMIT_HOLDER}}.
SITE, LANDLORD OR BRAND REQUIREMENTS AFFECTING THE SERVICES: {{SITE_REQUIREMENTS}}.

### A2. EXCLUDED AREAS
Back-of-house areas are excluded except for Included Areas expressly identified in A1. The following are excluded: {{SCOPE:excluded-areas}}. An area expressly identified as an Included Area in A1, such as the office, the employee restroom or hallways, remains included even if it is physically located in a back-of-house portion of the Premises. The kitchen is included only to the extent stated in A3.

### A3. LIMITED KITCHEN SCOPE
(a) Included work is {{SCOPE:kitchen-included}}, together with floor cleaning under A4. Ordinary light surface grease is included within the agreed baseline.
(b) Excluded work is {{SCOPE:kitchen-excluded}}, subject to Section 25.
(c) Contractor will not unplug refrigerators, freezers, alarms or equipment required to remain operating, change temperature controls, disconnect utility connections or move connected appliances. Client shall identify safe equipment conditions and manufacturer requirements before service. Contractor shall promptly report an observed condition that could affect food safety or property. No task requires working around exposed food or hot or energized equipment where unsafe.
(d) Food-contact cleaning and sanitizing are excluded except a task expressly identified in A1. For an included sanitizing task, Contractor shall follow the stated procedure, applicable sanitation requirements and product-label directions. Client remains responsible for all other operational sanitation and between-visit requirements.

### A4. FLOOR CLEANING
(a) Included floor work is {{SCOPE:floor-included}} in the Included Areas, using products and methods compatible with the identified floor materials. Excluded floor work is {{SCOPE:floor-excluded}}, unless added under Section 9.
(b) Contractor shall use appropriate wet-floor warnings and reasonable barriers during work, leave floors as dry as reasonably practicable, and communicate any remaining hazard before departure. Client shall inspect before reopening and manage conditions occurring afterward; that inspection obligation does not waive the liability of Contractor for hazards it creates.

### A5. RESTROOM CLEANING
Included work in the customer and employee restrooms consists of {{SCOPE:restroom}}. Routine restroom cleaning under ordinary sanitary conditions remains included. Specialized biohazard, bloodborne-pathogen, sharps and sewage remediation are excluded under Sections 24 and 25.

### A6. TRASH AND WASTE
Contractor shall move ordinary collected trash to lawful receptacles or dumpsters designated by Client on or behind the property, or another mutually designated lawful disposal point at the Premises. Client shall identify accessible receptacles for ordinary waste, recycling and separately collected organics. Contractor shall preserve required separation and shall not place commercial waste in public litter baskets, discharge cleaning waste into the street or perform off-site hauling. Client arranges lawful waste collection.

### A7. INTERIOR GLASS AND WINDOWS
Cleaning of the interior windows and interior glass identified in A1 is included to the extent safely reachable from the floor with ordinary extension tools. Exterior window cleaning and elevated-access work remain subject to the exclusions and approval requirements in Section 25.

### A8. SUPPLIES AND CONSUMABLES
(a) Contractor supplies the cleaning equipment, tools, chemicals, products and ordinary cleaning supplies necessary for the included Services. Hand soap and its dispensers are not included: Contractor does not supply, replenish, repair or replace them at any time.
(b) Client supplies toilet tissue, paper towels and trash can liners or trash bags and maintains adequate stock. Contractor shall report shortages. Purchase of substitutes by Contractor requires the prior written approval of Client as to the purchase and price under Section 6(c).
(c) Products shall be used and stored according to their labels and applicable requirements; incompatible chemicals shall not be mixed. Restroom tools and cloths shall be kept separate from kitchen and dining tools. Any special product or brand requirement must be identified in A1 before service; a change affecting scope or cost is subject to Sections 8 and 9.

### A9. BASELINE AND DEEP CLEANING
Deep cleaning and restoration are excluded except as expressly included in this exhibit or added by signed Change Order. Section 25 applies. Initial deep cleaning or restoration and its price require separate approval. Heavier soil does not authorize a surcharge; Contractor shall explain the issue and request an agreed solution.

BASELINE WALKTHROUGH DATE AND RECORD: {{BASELINE_WALKTHROUGH}}.
INITIAL-WORK CHANGE ORDER: {{INITIAL_WORK_CHANGE_ORDER}}.

{{SCOPE_ADDITIONAL}}

## EXHIBIT B
## PRICING, BILLING AND CONTACT DETAILS

### B1. SERVICE DATES AND SCHEDULE
|Effective Date|As stated in the introductory paragraph of the Agreement.
|Service Commencement Date|{{SERVICE_COMMENCEMENT_DATE}}
|Minimum Commitment End Date|{{MINIMUM_COMMITMENT_END_DATE}}, being {{MINIMUM_COMMITMENT_MONTHS}} calendar months after the Service Commencement Date. This is the first date on which termination for convenience may take effect, provided the notice requirement of {{TERMINATION_NOTICE_DAYS}} calendar days is satisfied.
|Initial Term End Date|{{INITIAL_TERM_END_DATE}}. The Initial Term runs from the Service Commencement Date through the day immediately preceding its {{INITIAL_TERM_MONTHS}}-month anniversary, subject to earlier termination. Thereafter the Agreement continues {{RENEWAL_TYPE}} under Section 3(d).
|Frequency|{{SERVICE_FREQUENCY_TEXT_CAP}} throughout the service term, subject to Sections 11, 14, 15, 24 and 30.
|Regular service {{SERVICE_DAY_NOUN}}|{{SERVICE_DAYS}}, subject to mutually agreed scheduling changes under Section 5.
|Arrival window|{{ARRIVAL_WINDOW}}, {{SERVICE_TIMEZONE}}, subject to mutual written confirmation under Section 5. Any completion deadline is stated in Exhibit A.
|Convenience termination|At least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice. Notice may be given during the Minimum Commitment Period, but termination may not take effect before the Minimum Commitment End Date. Cause, safety and force-majeure rights remain as stated in the Agreement.

### B2. PRICING AND PAYMENT
|Recurring pre-tax fee|{{PRE_TAX_PRICE}} per completed scheduled visit.
|Sales tax at {{SALES_TAX_RATE}}|{{SALES_TAX_AMOUNT}} per completed scheduled visit.
|Total at that tax rate|{{TOTAL_PRICE}} per completed scheduled visit.
|Tax adjustment|The completed-visit calculation adjusts to the legally applicable tax rate under Section 7. A charge for an unperformed visit, interruption or other event receives its own legally required tax treatment; no fixed tax-inclusive cancellation or failed-access amount is established.
|Billing cadence|Invoices are issued {{BILLING_CADENCE_TEXT}}.
|Invoicing|{{INVOICE_TIMING}}
|Payment deadline|Undisputed advance payment is due {{PAYMENT_DEADLINE_HOURS}} hours before the agreed arrival window begins, subject to the late-invoice rule in Section 11(a). Any different first-visit deadline must be expressly agreed in writing.
|Payment method|{{PAYMENT_METHOD}}, under Section 11(b).
|Makeup visits|One timely requested makeup visit may be scheduled by mutual agreement within {{MAKEUP_WINDOW_DAYS}} calendar days of the original visit under Section 15. Payments carry to the makeup, which is assigned to its original service {{SERVICE_PERIOD}}. Aggregate charges for the original visit and the makeup may not exceed one full service fee plus properly applicable tax.
|Short-notice cancellation|For short-notice cancellation, failure reasonably to cooperate in arranging a makeup after a timely cancellation or rescheduling request, or a missed visit properly suspended for nonpayment, a permitted charge is limited to reasonable documented net loss under Sections 11 and 15, capped at {{CANCELLATION_PERCENT}} of the pre-tax fee, rounded to the nearest cent, initially {{CANCELLATION_AMOUNT}}, plus tax only if legally required. This is a ceiling, not an automatic charge.
|Failed access|The permitted charge is reasonable documented net loss under Section 14, capped at the pre-tax visit fee, initially {{LOCKOUT_FEE}}, plus legally applicable tax. The agreed arrival window, contact attempt and wait requirement of {{LOCKOUT_WAIT_MINUTES}} minutes apply. No duplicate cancellation charge is permitted.
|Late payment|Earned undisputed amounts overdue by more than {{INTEREST_GRACE_DAYS}} calendar days accrue simple interest under Section 11(f) at {{LATE_CHARGE_PERCENT}} per month, calculated daily at {{LATE_CHARGE_ANNUAL_PERCENT}} per year or the lower lawful maximum.
|Returned payments|Returned or failed payments attributable to Client permit recovery of actual reasonable costs up to {{RETURNED_PAYMENT_FEE_WORDS}} under Section 11(g).
|Reconciliation|Prepayments, credits, refunds and tax adjustments shall be reconciled under Sections 4, 7, 14, 15 and 24. No amount may be retained or charged twice for the same loss. Proper suspension does not automatically earn full fees for unperformed visits.
|Price changes|Changes to the pre-tax fee require mutual signed agreement under Section 8. Contractor supplies the included labor, equipment, products and ordinary cleaning supplies. Client supplies toilet tissue, paper towels and trash can liners or trash bags under Section 6 and Exhibit A.
|Payment conditions|No invoice submission or internal-processing condition beyond Section 11 is required to make payment due unless added by a signed agreement consistent with applicable law.
|Aggregate liability cap|{{LIABILITY_CAP_MULTIPLE}} times the recurring pre-tax per-visit fee under Section 29(b), initially {{LIABILITY_CAP_AMOUNT}}.

### B3. INSURANCE
Commercial General Liability minimums required of Contractor are {{INSURANCE_PER_OCCURRENCE}} each occurrence and {{INSURANCE_AGGREGATE}} general aggregate, on an occurrence basis, throughout performance. Workers' compensation, disability benefits and Paid Family Leave coverage or valid exemption documentation are required as provided in Section 20.

Evidence of required coverage is due before Services begin and upon renewal. Any agreed additional-insured status, primary and noncontributory wording or waiver of subrogation must appear in an actual insurer-issued endorsement. A certificate alone does not modify coverage.

|Additional endorsements agreed for this engagement|{{AGREED_ENDORSEMENTS}}
|Insurer, policy, endorsement form and edition, protected entity and applicable work|{{ENDORSEMENT_DETAILS}}
|Agreed additional premium or price adjustment|{{ENDORSEMENT_PREMIUM}}

Any additional requirement is subject to Section 20(d), including prior agreement on cost and issuance before the work for which it is required. Listing an endorsement here does not substitute for obtaining it.

### B4. AUTHORIZED REPRESENTATIVES AND CONTACTS
The representatives designated below may approve amendments and Change Orders. Operational contacts may handle scheduling, access and urgent notices; that role alone does not confer authority to amend substantive terms. Each Party shall maintain monitored operational contact methods, including for weekend service.

|Contractor legal name|{{CONTRACTOR_DISPLAY_NAME}}
|Contractor authorized representative|{{CONTRACTOR_REPRESENTATIVE}}
|Contractor approval email|{{CONTRACTOR_APPROVAL_EMAIL}}
|Contractor notice mailing address|{{CONTRACTOR_ADDRESS}}
|Contractor notice email|{{CONTRACTOR_NOTICE_EMAIL}}
|Contractor operational email|{{CONTRACTOR_OPERATIONAL_EMAIL}}
|Contractor supervisor and primary on-call contact|{{CONTRACTOR_SUPERVISOR}}
|Contractor backup on-call contact|{{CONTRACTOR_BACKUP_CONTACT}}
|Client legal name|{{CLIENT_LEGAL_NAME}}
|Client authorized representative|{{CLIENT_REPRESENTATIVE}}
|Client approval email|{{CLIENT_APPROVAL_EMAIL}}
|Client notice mailing address|{{CLIENT_NOTICE_MAILING_ADDRESS}}
|Client notice email|{{CLIENT_NOTICE_EMAIL}}
|Client operational email|{{CLIENT_OPERATIONAL_EMAIL}}
|Client primary on-call contact|{{CLIENT_ON_CALL_CONTACT}}
|Client backup on-call contact|{{CLIENT_BACKUP_CONTACT}}

Contact changes shall be communicated under Section 32. Actual keys, alarm codes, passwords and bank credentials shall be exchanged separately through appropriate verified channels.

## SIGNATURES
By signing below, each Party agrees to this Master Service Agreement, including Sections 1 through 35, Exhibit A and Exhibit B, and the representations concerning authority in the introductory paragraph.

@SIGNATURE_BLOCK
""";
    }
}
