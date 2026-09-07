namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// The seeded v1.0 master agreement. The legal sentences are the executed reference MSA
    /// unchanged - only client-, location-, price-, schedule- and term-specific values have been
    /// replaced by {{TOKENS}}, and structural line prefixes added so one body can render to both
    /// HTML and PDF. Nothing business-specific is left hardcoded.
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
        public const string TemplateVersion = "1.0";
        public const string TemplateDescription =
            "Standard commercial MSA: Sections 1-35, signature block, Exhibit A (scope) and Exhibit B (pricing).";

        public const string BodyText = """
# MASTER SERVICE AGREEMENT
# COMMERCIAL CLEANING SERVICES

This Master Service Agreement (the "Agreement") is entered into as of {{EFFECTIVE_DATE}} (the "Effective Date") by and between {{CONTRACTOR_LEGAL_NAME}}, {{CONTRACTOR_ENTITY_TYPE}} doing business as {{CONTRACTOR_DBA}}, with its principal office at {{CONTRACTOR_ADDRESS}} ("Contractor"), and {{CLIENT_LEGAL_NAME}}, {{CLIENT_ENTITY_TYPE}}, with its principal place of business at {{CLIENT_FULL_ADDRESS}} ("Client"). Contractor and Client are each a "Party" and together the "Parties."

## 1. SERVICES AND PREMISES
(a) Contractor shall provide commercial cleaning services (the "Services") at the location identified in Exhibit A (the "Premises"), in accordance with the scope, tasks and frequencies set out in Exhibit A.
(b) The Premises are the {{PREMISES_DESCRIPTION}} operated by Client and located at {{SERVICE_FULL_ADDRESS}}. The Premises address is the service location only and is not necessarily the legal or principal business address of Client.
(c) Exhibit A and Exhibit B are incorporated into and form part of this Agreement.
(d) Services not expressly described in Exhibit A are not included and are governed by Section 9.

## 2. ORDER OF PRECEDENCE
In the event of any conflict or inconsistency among the documents forming this Agreement, the following order of precedence shall apply, from highest to lowest authority: (a) any signed written amendment or Change Order; (b) this Agreement; (c) Exhibit B (Pricing and Billing Schedule); (d) Exhibit A (Scope of Work); (e) any proposal, quotation or other document issued by Contractor prior to the Effective Date.

## 3. TERM, MINIMUM COMMITMENT PERIOD AND RENEWAL
(a) This Agreement begins on the Effective Date and continues for an initial term of {{INITIAL_TERM_MONTHS}} months (the "Initial Term").
(b) Minimum Commitment Period. The first {{MINIMUM_COMMITMENT_MONTHS}} months of the Initial Term are the "Minimum Commitment Period." The Minimum Commitment Period governs the availability of termination for convenience only, as provided in Section 4(a).
(c) Recurring service frequency. The recurring contracted service frequency is {{SERVICE_FREQUENCY_TEXT}}, as described in Exhibit A and Exhibit B. That recurring service obligation applies for as long as this Agreement remains in effect, whether during the Minimum Commitment Period, the remainder of the Initial Term or any {{RENEWAL_TYPE}} continuation, and is not limited to the Minimum Commitment Period.
(d) Upon expiration of the Initial Term, this Agreement continues automatically on a {{RENEWAL_TYPE}} basis on the same terms, and does not renew for a further fixed term. During the {{RENEWAL_TYPE}} continuation, either Party may terminate upon {{TERMINATION_NOTICE_DAYS}} days prior written notice.

## 4. TERMINATION
(a) Termination for convenience. Termination for convenience is not available during the Minimum Commitment Period. After the Minimum Commitment Period has elapsed, either Party may terminate this Agreement for any reason upon {{TERMINATION_NOTICE_DAYS}} days prior written notice, including during the {{RENEWAL_TYPE}} continuation described in Section 3(d). Until a termination for convenience takes effect, this Agreement remains in full force and the recurring service obligation under Sections 3(c) and 10, together with the corresponding payment obligations, continues.
(b) Termination for cause. Either Party may terminate this Agreement at any time, including during the Minimum Commitment Period, if the other Party materially breaches this Agreement and fails to cure the breach within {{CURE_PERIOD_DAYS}} days after receiving written notice describing it.
(c) Immediate termination. Contractor may terminate immediately, including during the Minimum Commitment Period, if Client fails to pay undisputed amounts more than {{PAST_DUE_DAYS}} days past due, or if conditions at the Premises present a risk to the health or safety of Contractor's personnel that Client does not remedy.
(d) Effect of termination. Client shall pay for all Services performed through the effective date of termination, together with any amounts accrued under Sections 14 and 15 before that date. Sections 13, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 27, 28, 29, 33 and 34 survive termination.

## 5. SCHEDULING AND SERVICE WINDOW
(a) Frequency. Contractor shall perform {{SERVICE_FREQUENCY_TEXT}} for as long as this Agreement remains in effect. A rescheduled visit shall be deemed to satisfy the service commitment for the {{SERVICE_PERIOD}} in which the visit was originally scheduled, regardless of the date on which the rescheduled Services are actually performed.
(b) Regular service day. The regular anticipated service day is {{SERVICE_DAY}}. {{SERVICE_DAY}} is not a permanently fixed service day. Client may request an occasional different day, and the Parties may agree to move a particular scheduled cleaning to another mutually agreed day, subject to Contractor's reasonable availability.
(c) Start time. The Services are generally expected to begin at approximately {{SERVICE_TIME}} on the scheduled service day. That start time is approximate and is not guaranteed as a permanent service window. The start time and service window may be changed by mutual agreement based on Client's operational needs and Contractor's availability.
(d) Form of confirmation. Scheduling changes under this Section may be confirmed in writing, including by email, and do not constitute an amendment to this Agreement or a Change Order under Section 9.
(e) No change to frequency. A change to the service day, start time or service window does not change the commitment of {{SERVICE_FREQUENCY_TEXT}}.
(f) Access and closed premises. {{CLOSED_PREMISES_TEXT}} Client shall provide Contractor with the key or other access necessary to enter the Premises, in accordance with Section 13.

## 6. FEES, ALL-INCLUSIVE PRICING AND SUPPLIES
(a) Client shall pay the fees stated in Exhibit B. The recurring service fee is stated in Exhibit B as a pre-tax amount per scheduled visit, to which applicable sales tax is added under Section 7. At the sales tax rate in effect on the Effective Date, the resulting total payable per scheduled visit is the tax-inclusive amount stated in Exhibit B.
(b) The recurring service fee is all-inclusive as to Contractor's cost of performance. It covers all labor, supervision, cleaning equipment, cleaning tools, cleaning chemicals, cleaning products and ordinary cleaning supplies used by Contractor's personnel in performing the Services, in each case supplied by Contractor at its own expense. No separate charge applies for equipment or cleaning products.
(c) Consumables. Contractor supplies hand soap where hand soap replenishment is included in Exhibit A. Client supplies, at its own cost, toilet tissue, paper towels and trash can liners or trash bags, unless Exhibit B states otherwise.
(d) All-inclusive pricing does not mean unlimited services. The fee covers the tasks and frequencies described in Exhibit A only. Deep cleaning and restoration-level cleaning are not included unless expressly stated in Exhibit A or added by signed Change Order under Section 9.

## 7. TAXES
(a) Applicable sales, use and similar taxes shall be calculated on the pre-tax recurring service fee stated in Exhibit B, added to amounts due, and separately stated on each invoice as required by applicable law.
(b) The total per-visit amount stated in Exhibit B is tax-inclusive and is calculated using the sales tax rate stated in Exhibit B as being in effect for drafting purposes. If the legally applicable tax rate changes, the pre-tax recurring service fee remains unchanged and does not decrease automatically; the applicable tax and the resulting total payable adjust to reflect the then-applicable rate as required by law.
(c) If Client claims exemption from any such tax, Client shall provide Contractor with a valid exemption certificate before the first invoice date, and taxes shall then be handled in accordance with applicable law. Client remains responsible for any tax later assessed as a result of an invalid or expired exemption certificate.

## 8. CHANGES TO PRICING
(a) Any change to the recurring pre-tax service fee requires the mutual written agreement of the Parties, documented by a signed amendment or a signed Change Order. Neither Party may change that fee unilaterally.
(b) A change in the legally applicable sales tax rate is not a negotiated change to the service price. Where the applicable tax rate changes, the applicable tax and the total invoice amount adjust automatically as required by law, and the agreed pre-tax service fee remains unchanged unless the Parties agree otherwise in writing.

## 9. CHANGE ORDERS AND OUT-OF-SCOPE WORK
(a) Any material change to the scope of work, the recurring service frequency, the Premises or the substantive service requirements requires a written Change Order signed by authorized representatives of both Parties.
(b) Ordinary mutually agreed adjustments to the day, start time or service window of an individual service visit do not require a Change Order. Confirmation by email is sufficient for those scheduling adjustments, as provided in Section 5(d).
(c) No verbal request, text message or informal instruction shall obligate Contractor to perform work outside Exhibit A, nor shall Contractor's performance of such work on one or more occasions be deemed to add that work to the Scope of Work.
(d) A Change Order shall state the additional service, frequency, price, effective date, and whether it is one-time or recurring.

## 10. RECURRING SERVICE COMMITMENT
(a) The commercial arrangement is based on {{SERVICE_FREQUENCY_TEXT}}. Client remains responsible for that recurring commitment for as long as this Agreement remains in effect, subject to Sections 14 and 15. The service commitment is not limited to the Minimum Commitment Period.
(b) A Client-requested change to the service day, start time or service window does not eliminate or reduce the service commitment.
(c) Where a holiday, closure or similar scheduling issue prevents a scheduled visit, the Parties shall, whenever reasonably possible, reschedule that visit to another mutually agreed date. Reasonable rescheduling under this Section and Section 15 does not create a Change Order and does not permanently modify the service frequency. A rescheduled visit satisfies the service commitment for the {{SERVICE_PERIOD}} in which the visit was originally scheduled, regardless of the date on which the rescheduled Services are actually performed.
(d) Continuation until termination. Client may not keep this Agreement in effect while repeatedly cancelling scheduled visits in order to avoid the recurring service and payment obligations. After the Minimum Commitment Period, the {{TERMINATION_NOTICE_DAYS}} day written notice of termination for convenience under Section 4(a) is the means of ending the recurring arrangement, and the service commitment continues until that termination takes effect.

## 11. INVOICING AND ADVANCE PAYMENT
(a) Prepaid arrangement. The Services are prepaid. Contractor shall issue an invoice in advance of each scheduled cleaning visit, generally several days before the visit, on the schedule stated in Exhibit B.
(b) Payment deadline. Payment must be received by Contractor no later than {{PAYMENT_DEADLINE_HOURS}} hours before the scheduled cleaning visit begins.
(c) Payment method. Payment shall be made by {{PAYMENT_METHOD}} in accordance with the payment instructions stated on the invoice or otherwise provided to Client by Contractor.
(d) Non-payment before service. Contractor is not obligated to perform a scheduled visit for which the required advance payment has not been received by the deadline in Section 11(b), and may withhold, suspend or reschedule that visit without liability and without prejudice to any other remedy. A visit withheld under this Section is not a Contractor cancellation under Section 15(c).
(e) Continuing suspension. Contractor may continue to suspend the Services, without liability, for so long as any undisputed amount remains unpaid past its due date. Suspension does not relieve Client of accrued payment obligations.
(f) Late charges. Late payments accrue a late charge at the lesser of {{LATE_CHARGE_PERCENT}} per month or the maximum rate permitted by applicable law, calculated on the overdue balance from the due date until paid.
(g) A fee of {{RETURNED_PAYMENT_FEE_WORDS}} applies to any returned or failed payment.
(h) Client shall be responsible for reasonable costs of collection, including reasonable attorneys' fees, to the extent permitted by applicable law.

## 12. BILLING DISPUTES
Client shall notify Contractor in writing of any disputed invoice amount within {{BILLING_DISPUTE_DAYS}} days of the invoice date, describing the basis of the dispute in reasonable detail. Amounts not disputed within that period are deemed accepted. Undisputed amounts remain payable when due, including the advance payment required under Section 11(b). The Parties shall work in good faith to resolve any disputed amount promptly.

## 13. ACCESS, KEYS AND ALARM CODES
(a) Client shall provide Contractor with safe and timely access to the Premises, including any keys, access cards, codes or alarm instructions necessary to perform the Services.
(b) Contractor shall maintain reasonable controls over any keys or access credentials in its possession and shall return them upon termination.
(c) Contractor shall be responsible for reasonable re-keying costs caused by the negligence of Contractor's personnel. Client shall be responsible for charges, penalties or false alarm fees arising from inaccurate or outdated access or alarm information provided by Client.

## 14. FAILED ACCESS AND LOCKOUT
(a) If Contractor's personnel arrive at the Premises at the scheduled time and are unable to perform the Services because of an access failure within Client's control, including because the Premises are locked or closed without prior notice, a required key is missing or unavailable, access credentials or alarm instructions are invalid or incorrect, or access is otherwise denied, the full scheduled service fee for that visit applies as stated in Exhibit B.
(b) A charge under this Section is separate from, and does not apply in addition to, the short-notice cancellation charge under Section 15(b) for the same visit. A visit charged under this Section satisfies the service commitment for that {{SERVICE_PERIOD}}.

## 15. CANCELLATION AND RESCHEDULING
(a) Timely rescheduling with notice. This Agreement requires {{SERVICE_FREQUENCY_TEXT}} for as long as it remains in effect. By giving Contractor at least {{TIMELY_RESCHEDULE_HOURS}} hours prior notice, Client may reschedule that scheduled visit to another mutually agreed date, subject to Contractor's reasonable availability, without any separate rescheduling or cancellation charge. Notice given under this Section is a request to reschedule and does not create a right to cancel the visit outright or to eliminate or reduce the service and payment obligation, which remains in effect. Any advance payment previously received for a timely rescheduled visit shall automatically be applied to the rescheduled visit and shall not constitute a new or additional charge, and Contractor is not required to refund and re-invoice that amount. A rescheduled visit shall be deemed to satisfy the service commitment for the {{SERVICE_PERIOD}} in which the visit was originally scheduled, regardless of the date on which the rescheduled Services are actually performed. The service commitment under Section 10 remains in effect.
(b) Client cancellation on short notice. If Client cancels a scheduled visit on less than {{TIMELY_RESCHEDULE_HOURS}} hours prior notice, a cancellation charge equal to {{CANCELLATION_PERCENT}} of the scheduled service fee applies, as stated in Exhibit B. Only one such charge may be applied to any single cancelled visit. Where a visit is cancelled on short notice, Client may either (i) forgo the visit for that {{SERVICE_PERIOD}}, in which case the cancelled visit satisfies the service commitment for that period and the cancellation charge is the only amount payable in respect of that visit, or (ii) reschedule the visit to a mutually agreed date. If Client elects to reschedule a visit cancelled on less than {{TIMELY_RESCHEDULE_HOURS}} hours' notice, the cancellation charge paid or owed for that visit shall be credited toward the full scheduled service fee for the rescheduled visit, Client shall pay only the remaining amount necessary to reach the applicable full scheduled service fee, and the total charge associated with that service shall not exceed the applicable full scheduled service fee. At the rates stated in Exhibit B, the cancellation charge of {{CANCELLATION_AMOUNT}} credited against the full scheduled service fee of {{TOTAL_PRICE}} leaves a remaining balance of {{REMAINING_BALANCE}} payable for the rescheduled visit. A visit rescheduled under this Section satisfies the service commitment for the {{SERVICE_PERIOD}} in which the visit was originally scheduled.
(b-1) Treatment of amounts already prepaid. Because the full scheduled service fee is ordinarily paid in advance under Section 11 and Exhibit B, the following applies where Client has already paid the full scheduled service fee for a visit cancelled on less than {{TIMELY_RESCHEDULE_HOURS}} hours' notice and elects not to reschedule that visit. Contractor shall retain {{CANCELLATION_PERCENT}} of the scheduled service fee as the applicable cancellation charge, currently {{CANCELLATION_AMOUNT}}, and the remaining balance of the amount already paid, currently {{REMAINING_BALANCE}}, shall be applied as a credit toward Client's next scheduled cleaning visit. Contractor shall not retain the full prepaid amount while treating only {{CANCELLATION_PERCENT}} of the scheduled service fee as the contractual cancellation charge. If this Agreement terminates or expires before that credit has been applied, any remaining unapplied credit shall be returned to Client within {{CREDIT_RETURN_DAYS}} days after the effective date of termination or expiration. Where Client elects instead to reschedule the cancelled visit, the amount already paid is applied as provided in Section 15(b)(ii) and no separate credit arises under this Section.
(c) Contractor cancellation. If Contractor is unable to perform a scheduled visit, Contractor shall notify Client promptly and shall either reschedule within a reasonable period or credit the value of that visit against the next invoice.
(d) Emergency closure. No charge applies where the Premises are closed due to fire, flood, utility failure, order of a government or health authority, or similar emergency beyond Client's reasonable control, provided Client notifies Contractor as soon as practicable. The Parties shall reschedule the affected visit where reasonably possible. No cancellation charge applies to a qualifying emergency closure under this Section. Any advance payment already received for the affected visit shall be applied to the mutually agreed rescheduled visit. If the affected visit cannot reasonably be rescheduled, that advance payment shall be applied as a credit toward Client's next scheduled cleaning visit, and if this Agreement terminates or expires before the credit has been used, any unapplied credit shall be returned to Client within {{CREDIT_RETURN_DAYS}} days after the effective date of termination or expiration.
(e) Repeated cancellations may constitute a material change in the commercial basis of this Agreement and may result in a proposed change to pricing under Section 8 or termination under Section 4.

## 16. CLIENT RESPONSIBILITIES
Client shall, at its own cost: provide working utilities including water, lighting and electrical power at the Premises; provide access to designated trash and recycling disposal points; secure cash, valuables and confidential materials; clear surfaces and floor areas reasonably required for the Services; supply the consumables allocated to Client under Section 6(c); and promptly notify Contractor of any known hazard, damage or condition at the Premises affecting the Services.

## 17. PERSONNEL AND SUBCONTRACTORS
(a) Contractor controls the staffing, scheduling and supervision of its personnel. Contractor may substitute personnel at its discretion, and substitution does not constitute a breach of this Agreement.
(b) Client may request removal of any individual from the Premises for reasonable safety or security concerns, and Contractor shall provide a replacement within a reasonable period.
(c) Contractor may perform the Services through its own employees or through qualified subcontractors, and remains responsible for performance of the Services in either case.

## 18. NON-SOLICITATION AND NON-HIRE
During the term of this Agreement and for {{NON_SOLICIT_MONTHS}} months after its termination, Client shall not knowingly solicit for employment or directly engage any individual assigned by Contractor to perform Services at the Premises, except with Contractor's prior written consent. The Parties acknowledge that Contractor incurs substantial recruiting, screening, training and supervision costs for each such individual, and that actual damages arising from a breach of this Section would be difficult to determine. Accordingly, Client shall pay Contractor liquidated damages in the amount stated in Exhibit B for each such individual, which the Parties agree is a reasonable estimate of Contractor's loss and not a penalty. This Section applies to the extent permitted by applicable law.

## 19. INDEPENDENT CONTRACTOR
Contractor is an independent contractor. Nothing in this Agreement creates an employment, partnership, joint venture or agency relationship between the Parties. Contractor's personnel are not employees of Client.

## 20. INSURANCE
(a) Contractor maintains Commercial General Liability insurance with limits of not less than {{INSURANCE_PER_OCCURRENCE}} each occurrence and {{INSURANCE_AGGREGATE}} general aggregate, written on an occurrence basis.
(b) Contractor shall maintain workers' compensation and disability benefits coverage to the extent required by {{INSURANCE_JURISDICTION}} law, or shall provide a valid certificate of exemption where applicable.
(c) Contractor shall provide a Certificate of Insurance upon request and upon each policy renewal. Additional insured and waiver of subrogation endorsements are available on request, subject to policy terms and the insurer's approval.
(d) Any insurance requirement exceeding the coverages described above shall be stated in Exhibit B or agreed in writing, and may result in a change to pricing agreed under Section 8.

## 21. DAMAGE CLAIMS
(a) Visible damage. Client shall notify Contractor in writing of any visible damage alleged to have been caused by Contractor within {{VISIBLE_DAMAGE_HOURS}} hours after the Services are performed.
(b) Latent damage. For damage that is not reasonably discoverable within that period, Client shall notify Contractor in writing within a reasonable period after discovery and in no event later than {{LATENT_DAMAGE_DAYS}} days after the Services were performed.
(c) Client shall give Contractor a reasonable opportunity to inspect the alleged damage before repair or replacement.

## 22. QUALITY GUARANTEE
(a) If Client is not satisfied with any task included in Exhibit A, Client shall notify Contractor in writing within {{QUALITY_COMPLAINT_HOURS}} hours after the Services are performed. Contractor shall re-perform the affected task at no additional charge.
(b) This guarantee is limited to re-performance. It does not entitle Client to a refund, credit or set-off.
(c) Client shall provide reasonable access to the Premises for corrective work.
(d) This guarantee applies only to tasks expressly included in Exhibit A.

## 23. CLEANING CONDITIONS AND RESULTS
The Services are intended to remove ordinary dirt, dust, grease and buildup using commercially reasonable cleaning methods and products. Results may vary based on the age, condition, material, finish, staining, deterioration, prior damage and maintenance history of the Premises and its fixtures. Cleaning is not restoration, refinishing or repair. Contractor does not warrant the removal of permanent stains, etching, rust, discoloration, worn or damaged finishes, deteriorated grout, or soils that have bonded to a surface over time.

## 24. HAZARDOUS CONDITIONS AND RIGHT TO STOP WORK
Contractor may suspend or decline any portion of the Services, without liability and without reduction of fees for that visit, where its personnel encounter conditions including but not limited to exposed electrical hazards, bodily fluids, needles or sharps, chemical spills, suspected asbestos, significant mold growth, sewage backup, pest infestation or unsafe structural conditions. Contractor shall notify Client promptly and shall resume the affected work once the condition has been made safe.

## 25. EXCLUDED SERVICES
The following are not included in the Services unless expressly added by signed Change Order, where legally appropriate, and performed by an appropriately qualified and licensed contractor: kitchen exhaust hood cleaning; exhaust duct cleaning; grease trap and grease interceptor servicing; internal cleaning of fryers, ovens, grills or other cooking equipment, and any dismantling of equipment; pest control and extermination; mold remediation; biohazard, bloodborne pathogen and sewage remediation; handling of needles or sharps; handling or disposal of hazardous materials or hazardous waste; asbestos related work; exterior window cleaning and any work above ground level or requiring scaffolding or suspended equipment; food preparation, food handling and dishwashing; repairs, painting, electrical, plumbing and mechanical work; snow and ice removal; floor stripping, waxing, refinishing, polishing, grout restoration and floor-machine restoration; heavy degreasing and deep grease remediation; and restoration work of any kind.

## 26. COMPLIANCE WITH LAW
Each Party shall comply with all applicable federal, state and local laws in connection with this Agreement. Contractor represents that its personnel are authorized to work in the United States and that Contractor complies with applicable wage and hour requirements.

## 27. CONFIDENTIALITY
Each Party shall keep confidential any non-public business, financial, operational or customer information of the other Party learned in connection with this Agreement, and shall use it only as necessary to perform under this Agreement. This obligation continues for {{CONFIDENTIALITY_YEARS}} years after termination and does not apply to information that is publicly available, independently developed, or required to be disclosed by law.

## 28. INDEMNIFICATION
Each Party shall indemnify, defend and hold harmless the other Party and its officers, directors and employees from third party claims, damages, liabilities and reasonable costs to the extent arising from the indemnifying Party's negligence, willful misconduct, or breach of this Agreement. This Section does not apply to the extent a claim arises from the negligence or willful misconduct of the indemnified Party.

## 29. LIMITATION OF LIABILITY
(a) Neither Party shall be liable for indirect, incidental, special, consequential or punitive damages, or for lost profits, lost revenue, lost business or business interruption, arising out of or relating to this Agreement, regardless of the theory of liability.
(b) Contractor's total aggregate liability arising out of or relating to this Agreement shall not exceed the total fees paid by Client to Contractor under this Agreement during the {{LIABILITY_CAP_MONTHS}} months immediately preceding the event giving rise to the claim.
(c) These limitations do not apply to a Party's indemnification obligations under Section 28, to a breach of Section 27, or to liability that cannot be limited under applicable law.

## 30. FORCE MAJEURE
Neither Party shall be liable for delay or failure to perform, other than a payment obligation, caused by events beyond its reasonable control, including acts of God, fire, flood, severe weather, epidemic, labor disturbance, utility or telecommunications failure, civil unrest, or action of a government authority.

## 31. ASSIGNMENT
Neither Party may assign this Agreement without the other Party's prior written consent, which shall not be unreasonably withheld, except that either Party may assign to a successor in connection with a merger or sale of substantially all of its assets upon written notice.

## 32. NOTICES
All notices under this Agreement, including notices of termination, shall be in writing and shall be delivered by email to the addresses below, or by hand or nationally recognized courier at the sending Party's option. Notice by email is effective upon confirmed transmission to the address stated below, or to any other address a Party designates by notice given under this Section. Notices sent by courier are effective upon documented delivery.

### CONTRACTOR
{{CONTRACTOR_DISPLAY_NAME}}
{{CONTRACTOR_ADDRESS}}
{{CONTRACTOR_NOTICE_EMAIL}}
{{CONTRACTOR_PHONE}}

### CLIENT
{{CLIENT_LEGAL_NAME}}
{{CLIENT_FULL_ADDRESS}}
{{CLIENT_NOTICE_EMAIL}}
{{CLIENT_PHONE}}

## 33. GOVERNING LAW AND VENUE
This Agreement is governed by the laws of the State of {{GOVERNING_LAW_STATE}} without regard to its conflict of laws rules. The Parties consent to the exclusive jurisdiction of the state and federal courts located in {{VENUE_COUNTY}}, {{GOVERNING_LAW_STATE}}, subject to the small claims and dispute resolution provisions of Section 34.

## 34. DISPUTE RESOLUTION
(a) Before commencing any action, the Parties shall attempt in good faith to resolve any dispute through direct discussion between senior representatives for a period of {{DISPUTE_DISCUSSION_DAYS}} days. If unresolved, the Parties shall attempt non-binding mediation in {{MEDIATION_VENUE}}, with costs shared equally. Either Party may commence litigation after those steps have been attempted.
(b) This Section does not prevent either Party from seeking injunctive relief or from pursuing a claim in small claims court.
(c) Collection of undisputed amounts. Contractor is not required to complete the discussion or mediation steps in Section 34(a) before pursuing any lawful remedy to collect amounts that are due and unpaid and that Client has not disputed in accordance with Section 12. This exception applies only to undisputed payment obligations and does not affect the application of Section 34(a) to genuinely disputed invoice amounts.

## 35. GENERAL
(a) Entire agreement. This Agreement, together with its Exhibits, is the entire agreement between the Parties on its subject matter and supersedes all prior discussions, proposals and understandings.
(b) Amendments. This Agreement may be amended only by a written instrument signed by both Parties, except that scheduling adjustments under Section 5 may be confirmed by email.
(c) Severability. If any provision is held unenforceable, the remainder of this Agreement remains in full force and the unenforceable provision shall be modified to the minimum extent necessary to make it enforceable.
(d) Waiver. A Party's failure to enforce any provision is not a waiver of that provision or of any other provision.
(e) Counterparts and electronic signature. This Agreement may be executed in counterparts and delivered electronically. Electronic signatures and scanned copies have the same effect as original signatures.
(f) Headings. Section headings are for convenience only and do not affect interpretation.

## SIGNATURE BLOCK
The Parties have executed this Agreement as of the Effective Date.

@SIGNATURE_BLOCK

Exhibit A - Scope of Work and Exhibit B - Pricing and Billing Schedule are attached to and incorporated into this Agreement.

## EXHIBIT A
## SCOPE OF WORK

SERVICE PREMISES: {{PREMISES_DESCRIPTION}} operated by Client, {{SERVICE_FULL_ADDRESS}}. Service location only; not necessarily the legal or principal business address of Client.
FREQUENCY: {{SERVICE_FREQUENCY_TEXT_CAP}}.
REGULAR ANTICIPATED DAY: {{SERVICE_DAY}}, {{SCHEDULE_FLEXIBILITY_TEXT}}.
ANTICIPATED START TIME: Approximately {{SERVICE_TIME}}, {{SCHEDULE_FLEXIBILITY_TEXT}}.
CONDITIONS: {{CLOSED_PREMISES_TEXT}} Services are performed using {{ACCESS_TYPE}}.

### A1. INCLUDED AREAS
The Services cover the following areas of the Premises, in each case only to the extent of the tasks expressly described in this Exhibit A: {{SCOPE:included-areas}} (each an "Included Area"). Any additional area not expressly identified above is excluded unless the Parties agree in writing that it is included in the recurring Scope of Work or add it through an applicable Change Order under Section 9. The Included Areas listed in this Section A1 control, and no general exclusion elsewhere in this Exhibit A operates to exclude an area expressly listed above. The kitchen remains subject to, and is not broadened beyond, the limited kitchen scope described in Section A3.

### A2. EXCLUDED AREAS
All back-of-house areas are excluded except for areas expressly identified as Included Areas in Section A1. The kitchen is included only to the limited extent expressly described in Section A3. The following are excluded: {{SCOPE:excluded-areas}}. For the avoidance of doubt, an area expressly identified as an Included Area in Section A1, such as the office, the employee restroom or hallways, remains included even if it is physically located in a back-of-house portion of the Premises, and the Included Areas in Section A1 control over the general back-of-house exclusion.

### A3. KITCHEN SCOPE (LIMITED)
(a) Included. {{SCOPE:kitchen-included}}; cleaning of kitchen floors in accordance with Section A4.
(b) Not included. {{SCOPE:kitchen-excluded}}; restoration; repair; and heavy degreasing or deep restoration cleaning, unless separately authorized by signed Change Order.

### A4. FLOOR CLEANING
(a) Included. {{SCOPE:floor-included}} in the included areas.
(b) Not included. {{SCOPE:floor-excluded}}, unless added by signed Change Order.

### A5. RESTROOM CLEANING
{{SCOPE:restroom}}, in the customer restrooms and the employee restroom.

### A6. TRASH REMOVAL
Trash removal is included. Contractor will move ordinary collected trash to the Client-designated trash receptacles or dumpsters located on or behind the property, or to another disposal point mutually designated by Client and Contractor at the Premises. Contractor is not responsible for off-site waste hauling unless expressly agreed in writing.

### A7. GLASS AND WINDOWS
Cleaning of interior windows and interior glass is included. Exterior window cleaning is not included, and remains excluded under Section 25, including any work above ground level or requiring scaffolding or suspended equipment.

### A8. SUPPLIES AND CONSUMABLES
(a) Contractor supplies, at its own expense: cleaning equipment; cleaning tools; cleaning chemicals; cleaning products; ordinary cleaning supplies necessary to perform the Services; and hand soap replenishment.
(b) Client supplies, at its own cost: toilet tissue; paper towels; and trash can liners or trash bags.

### A9. DEEP CLEANING AND EXCLUSIONS
Deep cleaning and restoration-level cleaning are not part of the recurring service unless expressly stated in this Exhibit A or added by signed Change Order under Section 9. The exclusions in Section 25 of the Agreement apply in full to this Exhibit A.

{{SCOPE_ADDITIONAL}}

## EXHIBIT B
## PRICING AND BILLING SCHEDULE

|Recurring service frequency|{{SERVICE_FREQUENCY_TEXT_CAP}}, for as long as the Agreement remains in effect.
|Regular anticipated service day|{{SERVICE_DAY}}, subject to mutually agreed scheduling changes.
|Anticipated start time|Approximately {{SERVICE_TIME}}, subject to mutually agreed scheduling changes.
|Current pre-tax service fee|{{PRE_TAX_PRICE}} per scheduled visit.
|Current sales tax assumption|{{SALES_TAX_RATE}}, currently {{SALES_TAX_AMOUNT}} per scheduled visit.
|Current total per visit|{{TOTAL_PRICE}} per scheduled visit, including applicable sales tax at the current rate.
|Tax adjustment|If the legally applicable sales tax rate changes, the agreed pre-tax service fee remains unchanged and the final invoice total adjusts accordingly.
|Invoicing|{{INVOICE_TIMING}}
|Payment deadline|Payment must be received no later than {{PAYMENT_DEADLINE_HOURS}} hours before the scheduled service begins.
|Payment method|{{PAYMENT_METHOD}}, per the instructions on the invoice.
|Initial Term|{{INITIAL_TERM_MONTHS}} months from the Effective Date.
|Minimum Commitment Period|First {{MINIMUM_COMMITMENT_MONTHS}} months of the Initial Term. Governs the availability of termination for convenience only; the recurring service commitment continues for the full period the Agreement remains in effect.
|Rescheduled visits|A rescheduled visit satisfies the service commitment for the {{SERVICE_PERIOD}} in which it was originally scheduled, regardless of the date it is performed. Any advance payment already received for a timely rescheduled visit is applied to the rescheduled visit and is not a new or additional charge. At least {{TIMELY_RESCHEDULE_HOURS}} hours' notice permits rescheduling, subject to Contractor's reasonable availability, and does not permit cancellation of the visit without rescheduling.
|Termination for convenience|Not available during the Minimum Commitment Period, except as otherwise expressly provided in the Agreement. After the first {{MINIMUM_COMMITMENT_MONTHS}} months: {{TERMINATION_NOTICE_DAYS}} days written notice.
|Post-Initial-Term continuation|{{RENEWAL_TYPE}}, terminable by either Party on {{TERMINATION_NOTICE_DAYS}} days written notice.
|Short-notice cancellation|Less than {{TIMELY_RESCHEDULE_HOURS}} hours notice: {{CANCELLATION_PERCENT}} of the scheduled service fee. Current amount: {{CANCELLATION_AMOUNT}}. If Client reschedules the cancelled visit, that amount is credited toward the full scheduled service fee for the rescheduled visit and the total for that service does not exceed the full scheduled service fee (currently {{CANCELLATION_AMOUNT}} credit plus {{REMAINING_BALANCE}} balance, totaling {{TOTAL_PRICE}}). If the full fee was already prepaid and Client elects not to reschedule, Contractor retains {{CANCELLATION_AMOUNT}} as the cancellation charge and the remaining {{REMAINING_BALANCE}} is credited toward the next scheduled visit; any unapplied credit is returned to Client if the Agreement ends first.
|Failed access / lockout|Full scheduled service fee. Current amount: {{LOCKOUT_FEE}}.
|Non-solicitation / non-hire liquidated damages|{{NON_HIRE_DAMAGES}} per individual, to the extent enforceable under applicable law.
|Price adjustments|Only by mutual written agreement under Section 8, except changes caused solely by the applicable tax rate.
|Consumables supplied by Client|Toilet tissue, paper towels, and trash can liners or trash bags.
|Late charge and fees|Late charge of {{LATE_CHARGE_PERCENT_SHORT}} per month or the maximum lawful rate, whichever is lower; {{RETURNED_PAYMENT_FEE}} returned or failed payment fee.
""";
    }
}
