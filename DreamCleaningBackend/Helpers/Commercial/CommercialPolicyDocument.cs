namespace DreamCleaningBackend.Helpers.Commercial
{
    public enum PolicyBlockKind
    {
        /// <summary>Ordinary prose.</summary>
        Paragraph,

        /// <summary>A bulleted item.</summary>
        Bullet,

        /// <summary>
        /// A set-apart statement - the precedence rule, or a "this is a cap, not an automatic
        /// charge" qualifier. Rendered as a callout on the page and as an indented ruled note in
        /// the PDF, so the qualifier cannot be read past.
        /// </summary>
        Note
    }

    public class PolicyBlock
    {
        public PolicyBlockKind Kind { get; set; } = PolicyBlockKind.Paragraph;
        public string Text { get; set; } = string.Empty;

        public static PolicyBlock P(string text) => new() { Kind = PolicyBlockKind.Paragraph, Text = text };
        public static PolicyBlock B(string text) => new() { Kind = PolicyBlockKind.Bullet, Text = text };
        public static PolicyBlock N(string text) => new() { Kind = PolicyBlockKind.Note, Text = text };
    }

    public class PolicySection
    {
        /// <summary>"1", "2" ... Rendered as the heading number and used to build the anchor.</summary>
        public string Number { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;

        /// <summary>The URL fragment / DOM id the table of contents links to.</summary>
        public string Anchor { get; set; } = string.Empty;

        public List<PolicyBlock> Blocks { get; set; } = new();
    }

    public class PolicyDocument
    {
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;

        /// <summary>Issued by - printed under the title on both the page and the PDF.</summary>
        public string LegalIdentity { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// ISO-8601. A string rather than a DateOnly so the frontend copy of this document is
        /// comparable field for field without a date-format convention in the middle.
        /// </summary>
        public string EffectiveDate { get; set; } = string.Empty;

        /// <summary>The PDF's download filename, also what the page's button asks for.</summary>
        public string PdfFileName { get; set; } = string.Empty;

        public List<PolicyBlock> Intro { get; set; } = new();
        public List<PolicySection> Sections { get; set; } = new();
    }

    /// <summary>
    /// The published Commercial Cleaning Policies, and the standalone Cancellation and
    /// Termination Policy carved out of them.
    ///
    /// THIS FILE IS THE CANONICAL CONTENT. The public page, both downloadable PDFs and the
    /// consolidated Section 36 of the Master Service Agreement all resolve back to it, so a term
    /// cannot say one thing on the website and another in a document a client downloads.
    /// <c>DreamCleaningNG/src/app/shared/commercial-policies/commercial-policy.content.ts</c> is
    /// the frontend's copy, generated from this file, and <c>CommercialPolicyContentTests</c>
    /// asserts it is deep-equal to what this class builds.
    ///
    /// VERSION 2.0 IS A SUMMARY, NOT THE CONTRACT (2026-09-30). Version 1.0 restated most of the
    /// Master Service Agreement in eighteen sections and read like one. The public page is now an
    /// operational summary in eleven; the detailed legal mechanics - the damages calculations,
    /// the liability cap and its carve-outs, the dispute-resolution timetable - live in the
    /// executed agreement, which is what a client signs and what governs.
    ///
    /// THE BUSINESS RULES IT STATES (owner's decisions, 2026-09-30):
    ///   - There is NO company-wide minimum commitment. One exists only where a particular client
    ///     agreed one, for the period in that client's agreement.
    ///   - Ongoing service ends on 30 days' written notice from either party.
    ///   - Satisfaction Guarantee: report within 24 hours of completion; a NARROW exception up to
    ///     72 hours for an issue that could not reasonably have been found sooner; the remedy is
    ///     correction of the affected in-scope area, never an automatic full refund or a re-clean
    ///     of the whole premises.
    ///   - Supplies, equipment and consumables are allocated per agreement - by us, by the
    ///     client, or split. Nothing here assigns them to either party by default.
    ///   - Hand soap is not discussed at all.
    ///
    /// EVERY OPERATIVE FIGURE HERE IS READ OUT OF THE MSA, not chosen. The notice windows, caps,
    /// insurance limits and interest rate are the defaults on <c>ContractSnapshot</c>'s
    /// <c>TermSnapshot</c> / <c>AdvancedTermsSnapshot</c> / <c>PricingSnapshot</c>, and
    /// <c>CommercialPolicyContentTests</c> asserts each one against those defaults.
    ///
    /// WHAT IT MUST NEVER DO:
    ///   - state a charge as automatic. The agreement caps a REASONABLE DOCUMENTED NET LOSS; a
    ///     published "50% cancellation fee" would describe a liquidated-damages clause the
    ///     contract does not contain, and New York treats an amount plainly disproportionate to
    ///     the probable loss as an unenforceable penalty (JMD Holding Corp. v Congress Financial
    ///     Corp., 4 NY3d 373 [2005]; Coast Cleaning Servs. LLC v Ambrosio Italian Rest. of S.I.
    ///     Inc., 2022 NY Slip Op 50535[U], where a cleaning-services liquidated-damages clause
    ///     yielding $10,916.48 was refused and recovery limited to $813.18 of proven loss).
    ///   - promise coverage, a limit, a certification or a guarantee the MSA does not.
    ///   - claim these policies bind anybody on their own. The executed agreement governs, and
    ///     the intro and section 11 say so on the page as well as in the PDF.
    /// </summary>
    public static class CommercialPolicyDocument
    {
        /// <summary>
        /// The published policy version. Frozen onto each generated contract version's snapshot
        /// (<c>ContractSnapshot.PolicyVersion</c>) so a later revision of this file cannot change
        /// what an executed agreement says was in force when it was signed.
        ///
        /// Raise it, and <see cref="EffectiveDate"/> with it, whenever the substance below changes.
        /// 1.0 (2026-09-16) - the eighteen-section first publication.
        /// 2.0 (2026-09-30) - the simplified eleven-section summary; no standard minimum
        /// commitment; 30-day termination notice; the 24-hour / 72-hour guarantee; supplies and
        /// consumables allocated per agreement.
        /// </summary>
        public const string Version = "2.0";

        /// <summary>ISO-8601. Printed on the page, both PDFs and Section 36 of every new contract.</summary>
        public const string EffectiveDate = "2026-09-30";

        public const string LegalIdentity = "Nodar Alania Inc. d/b/a Dream Cleaning NYC";
        public const string ContactEmail = "hello@dreamcleaningnyc.com";
        public const string ContactPhone = "(929) 930-1525";
        public const string Website = "https://dreamcleaningnyc.com/";

        /// <summary>
        /// Where the policies are published. Named in Section 36(o) of the agreement, which is why
        /// it is a constant here rather than a literal in the template body - the leak guard in
        /// <c>ContractRenderingTests</c> exists because a business-specific value hardcoded in the
        /// body stays correct for one contract and is silently wrong for the rest.
        ///
        /// Unlike <see cref="Version"/> this is NOT frozen onto the snapshot: the version is the
        /// legally meaningful record of what was published at signature, while the address is only
        /// a pointer, and a pointer is more useful current than historical.
        /// </summary>
        public const string PublishedPolicyUrl = "https://dreamcleaningnyc.com/commercial-cleaning-policies";

        public const string CompleteKey = "complete";
        public const string CancellationKey = "cancellation-termination";

        public const string CompletePdfFileName = "Dream-Cleaning-NYC-Commercial-Cleaning-Policies.pdf";
        public const string CancellationPdfFileName = "Dream-Cleaning-NYC-Cancellation-Termination-Policy.pdf";

        /// <summary>
        /// The one sentence every surface repeats: these are general policies, and an executed
        /// agreement outranks them. Declared once so the page, both PDFs and Section 36 of the
        /// agreement cannot word the precedence rule three different ways.
        /// </summary>
        public const string PrecedenceNote =
            "These policies are a general summary, not a contract. Where a client's signed "
            + "Commercial Cleaning Service Agreement with Dream Cleaning NYC addresses a subject "
            + "covered here, that agreement and its exhibits govern, and any term individually "
            + "negotiated in it prevails over this summary.";

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  The complete Commercial Cleaning Policies
        // ══════════════════════════════════════════════════════════════════════════════════════

        public static PolicyDocument BuildComplete() => new()
        {
            Key = CompleteKey,
            Title = "Commercial Cleaning Policies",
            Subtitle = "General service policies for commercial clients",
            LegalIdentity = LegalIdentity,
            Version = Version,
            EffectiveDate = EffectiveDate,
            PdfFileName = CompletePdfFileName,
            Intro = new List<PolicyBlock>
            {
                PolicyBlock.P(
                    "This page summarises how " + LegalIdentity + " provides commercial cleaning "
                    + "services in New York City: how work is scoped and scheduled, how a visit is "
                    + "cancelled, how an agreement ends, how invoicing works and how we stand behind "
                    + "the work. The detailed terms for each client are set out in that client's "
                    + "signed Commercial Cleaning Service Agreement (the \"Service Agreement\")."),
                PolicyBlock.P(
                    "These policies apply to commercial cleaning - offices, restaurants, retail "
                    + "premises and similar business locations. They do not apply to residential "
                    + "cleaning, which is booked online and governed by our Terms and Conditions."),
                PolicyBlock.N(PrecedenceNote)
            },
            Sections = new List<PolicySection>
            {
                Section("1", "Scope of Services & Changes", "scope", new[]
                {
                    PolicyBlock.P(
                        "We perform the cleaning services agreed with each client, at the frequency set "
                        + "out in the client's Service Agreement - in a detailed scope of work, a short "
                        + "scope statement or other agreed service directions. Work not agreed is not included. The fee pays for completing the agreed "
                        + "tasks to the agreed standard rather than for a set number of labor hours, "
                        + "and staffing and the order of work are ours to decide."),
                    PolicyBlock.P(
                        "Cleaning removes ordinary soil that is reasonably removable with the agreed "
                        + "methods. It is not restoration, refinishing or repair. Deep cleaning, heavy "
                        + "degreasing, floor stripping and waxing, exterior windows, pest control and "
                        + "specialized remediation are not included unless the Service Agreement or a "
                        + "signed Change Order adds them."),
                    PolicyBlock.P(
                        "Additional work - an extra area, an added task or a one-time clean - is quoted "
                        + "and scheduled separately. A material change to the scope, the recurring "
                        + "frequency or the premises needs written approval from both sides in a "
                        + "Change Order. A verbal request, a text message or an instruction given to a "
                        + "cleaner on site does not change the agreed scope, and doing an extra task "
                        + "once does not make it part of the recurring work."),
                    PolicyBlock.P(
                        "An ordinary scheduling adjustment - moving a visit's day or arrival window - "
                        + "is agreed by email and changes nothing else.")
                }),

                Section("2", "Scheduling, Access & Client Responsibilities", "scheduling-access", new[]
                {
                    PolicyBlock.P(
                        "Each Service Agreement sets the recurring frequency, the regular service day "
                        + "or days and an arrival window. We arrive within that window and tell the "
                        + "client promptly about a material delay. Changing the day or the window needs "
                        + "confirmation from both sides, which can be by email, and does not change the "
                        + "agreed number of visits."),
                    PolicyBlock.P(
                        "The client provides safe and timely access to the premises and to the areas to "
                        + "be cleaned - keys, access cards, codes, accurate alarm instructions, working "
                        + "utilities, and the building sign-in or other access arrangements the crew "
                        + "needs to get in. Surfaces and floors should be reasonably cleared so they can "
                        + "be cleaned, and designated waste and recycling receptacles must be accessible."),
                    PolicyBlock.P(
                        "Access credentials are exchanged securely and separately from the Service "
                        + "Agreement and are never written into it. We limit them to the personnel who "
                        + "need them, never copy or share them, report a loss promptly, and return or "
                        + "delete them within 2 business days after service ends. Credentials are never "
                        + "withheld as security for payment."),
                    PolicyBlock.P(
                        "The client secures cash, valuables and confidential material, and tells us "
                        + "about known hazards, damage and conditions affecting the work. Animals should "
                        + "be secured or kept away from the work area during service."),
                    PolicyBlock.P(
                        "Special instructions - equipment that must stay on, alarm and closing "
                        + "procedures, restricted rooms, landlord or site rules - should be given to us "
                        + "in writing before service begins. An instruction that changes the scope or "
                        + "the cost is handled as a Change Order."),
                    PolicyBlock.P(
                        "The client names a primary on-call contact who can be reached during service. "
                        + "It matters for a specific reason: we treat a visit as failed access only "
                        + "after trying to reach that person from the door.")
                }),

                // Sections 3 and 4 are assembled from the SAME clause builders the standalone
                // Cancellation and Termination Policy uses. That is the whole mechanism preventing
                // the two published documents from contradicting each other.
                Section("3", "Cancellation, Rescheduling & Failed Access", "cancellation",
                    CancellationClauses()),

                Section("4", "Contract Term & Termination", "termination",
                    TerminationClauses()),

                Section("5", "Pricing, Invoicing & Payment", "invoicing", new[]
                {
                    PolicyBlock.P(
                        "Commercial pricing is quoted per client, after a walkthrough, as a recurring "
                        + "fee per completed scheduled visit and stated in the client's Service "
                        + "Agreement. We do not publish client prices."),
                    PolicyBlock.P(
                        "Commercial cleaning is invoiced in advance. We ordinarily issue the invoice at "
                        + "least 7 calendar days before each scheduled visit, and undisputed amounts are "
                        + "due 48 hours before the agreed arrival window begins. If our invoice reaches "
                        + "the client late, the client is given reasonable time to pay and is not "
                        + "charged for a cancellation our late invoice caused."),
                    PolicyBlock.P(
                        "Payment is by ACH or bank transfer to verified instructions. A change of bank "
                        + "details is only ever confirmed through a previously verified telephone "
                        + "number, never by email alone."),
                    PolicyBlock.P(
                        "If an undisputed advance payment is not received when due, we may withhold the "
                        + "affected visit until it is. Undisputed amounts more than 5 calendar days "
                        + "overdue accrue simple interest of 1% per month, equivalent to 12% per year, or "
                        + "the maximum lawful rate if lower. Sales tax is added and stated separately as "
                        + "the law requires."),
                    PolicyBlock.P(
                        "A billing question should be raised promptly - for anything apparent from the "
                        + "invoice itself, within 10 business days of receiving it - and the undisputed "
                        + "portion paid when due. The recurring fee changes only by written agreement of "
                        + "both parties; we never change it unilaterally or retroactively.")
                }),

                Section("6", "Supplies, Equipment & Consumables", "supplies", new[]
                {
                    PolicyBlock.P(
                        "Who provides cleaning supplies, equipment and consumables is agreed for each "
                        + "client and recorded in the client's Service Agreement. There is no single "
                        + "rule that applies to every client."),
                    PolicyBlock.P(
                        "Cleaning supplies and equipment - cleaning products, chemicals, tools, vacuums "
                        + "and similar items - may be provided by Dream Cleaning NYC, by the client, or "
                        + "divided between us as the Service Agreement states."),
                    PolicyBlock.P(
                        "Consumables are allocated item by item, and either Dream Cleaning NYC or the "
                        + "client may provide any of them. Typical consumables include:"),
                    PolicyBlock.B("trash bags and liners"),
                    PolicyBlock.B("paper towels"),
                    PolicyBlock.B("toilet tissue"),
                    PolicyBlock.B("other consumables named in the Service Agreement"),
                    PolicyBlock.P(
                        "A mixed arrangement is common - for example, we provide trash liners while the "
                        + "client provides paper towels and toilet tissue. Each party keeps adequate "
                        + "stock of the items it has agreed to provide. If a client-provided item runs "
                        + "short we let the client know, and we buy substitutes only with the client's "
                        + "written approval of the purchase and the price."),
                    PolicyBlock.P(
                        "Whoever provides them, products are used and stored according to their labels, "
                        + "incompatible chemicals are never mixed, and restroom tools are kept separate "
                        + "from kitchen and dining tools. We may decline to use a client-provided product "
                        + "or item of equipment we reasonably consider unsafe or unsuitable, and we tell "
                        + "the client promptly when we do.")
                }),

                Section("7", "Quality Assurance & Satisfaction Guarantee", "quality", new[]
                {
                    PolicyBlock.P(
                        "We stand behind every visit. If something within the agreed scope was missed "
                        + "or not done to the agreed standard, tell us and we will make it right."),
                    PolicyBlock.P(
                        "Report a cleaning-quality issue within 24 hours after the service is "
                        + "completed, describing what was missed and including a photograph where one "
                        + "is available."),
                    PolicyBlock.P(
                        "If an issue could not reasonably have been noticed within those 24 hours, "
                        + "report it as soon as reasonably possible, and in any case no later than 72 "
                        + "hours after the service was completed. This is a narrow exception for issues "
                        + "that could not reasonably have been discovered sooner, not a general 72-hour "
                        + "reporting period."),
                    PolicyBlock.P(
                        "When we verify an issue with work that was included in the agreed scope, we "
                        + "are given a reasonable opportunity to correct the affected area at no "
                        + "additional charge, ordinarily within 2 business days of the report and "
                        + "access. If we decline or fail to correct it within that time, the client "
                        + "receives a reasonable credit or refund for the deficient portion of the visit."),
                    PolicyBlock.N(
                        "The Satisfaction Guarantee covers verified deficiencies within the agreed "
                        + "scope, and its remedy is correction of the affected area. It is not an "
                        + "automatic full refund and not a free re-cleaning of the entire premises. It "
                        + "does not cover additional work or items outside the agreed scope, or "
                        + "conditions caused after we completed the service - for example by employees, "
                        + "other contractors, building staff, vendors or occupants."),
                    PolicyBlock.P(
                        "A quality concern is not a reason to withhold unrelated invoice amounts, and "
                        + "the guarantee does not limit a claim for property damage or personal "
                        + "injury, which is handled under section 8.")
                }),

                Section("8", "Property Damage & Claims", "damage-claims", new[]
                {
                    PolicyBlock.P(
                        "Report suspected damage promptly - where reasonably practicable, within 5 "
                        + "business days of discovering it - with a description and any photographs "
                        + "available. We report damage we discover ourselves in the same way."),
                    PolicyBlock.P(
                        "Both sides preserve the evidence and give the other a reasonable opportunity "
                        + "to inspect before an item is repaired, replaced or discarded, except where "
                        + "emergency action is needed to protect people or property."),
                    PolicyBlock.P(
                        "Pre-existing wear, damage or deterioration - and conditions that cleaning cannot "
                        + "reverse, such as permanent staining, etching or worn finishes - are not damage "
                        + "caused by cleaning. We record the baseline condition before regular service "
                        + "begins, and may take issue-specific photographs to document pre-existing "
                        + "damage, completed work, a reported deficiency or a damage claim. Those "
                        + "photographs focus on surfaces, avoid people and sensitive information where "
                        + "practicable, and are used only for service, insurance and dispute purposes."),
                    PolicyBlock.P(
                        "We carry commercial general liability insurance with limits of at least "
                        + "$1,000,000 each occurrence and $2,000,000 general aggregate, and provide "
                        + "certificates of insurance before service begins. Any additional insurance "
                        + "requirement, and the limits of each party's liability, are set out in the "
                        + "Service Agreement."),
                    PolicyBlock.N(
                        "Compensation reflects the legally recoverable loss. It is not an upgrade to a "
                        + "newer item than the one affected, and no loss is recovered twice.")
                }),

                Section("9", "Safety, Hazardous Conditions & Right to Refuse or Suspend Service", "safety", new[]
                {
                    PolicyBlock.P(
                        "We may refuse, stop or suspend service in the affected area, or at the "
                        + "premises, when the work cannot reasonably or safely be performed. That "
                        + "includes:"),
                    PolicyBlock.B("unsafe premises, structures, equipment or working conditions"),
                    PolicyBlock.B("an aggressive or uncontrolled animal in the work area"),
                    PolicyBlock.B("harassment, threats or abusive conduct toward our personnel"),
                    PolicyBlock.B(
                        "hazardous or biohazard conditions - sharps, blood or bodily fluids, sewage, "
                        + "substantial mold, suspected asbestos or dangerous chemical spills"),
                    PolicyBlock.B("restricted or unsafe access to the areas to be cleaned"),
                    PolicyBlock.P(
                        "When that happens we tell the client promptly, identify the affected area and "
                        + "carry on with the unaffected work wherever it is safe to do so. Routine "
                        + "restroom cleaning stays included; specialized remediation does not, and no "
                        + "request or offer of extra payment authorizes unsafe or unqualified work. We "
                        + "resume once the condition has been safely resolved."),
                    PolicyBlock.P(
                        "Where the condition comes from a known hazard the client did not disclose or "
                        + "address, the client pays for the work actually completed and reasonable "
                        + "documented loss from the interruption, as the Service Agreement describes. "
                        + "Nothing is charged for work not performed because of a hazard we caused.")
                }),

                Section("10", "Confidentiality, Security & Personnel", "confidentiality", new[]
                {
                    PolicyBlock.P(
                        "Access information - keys, alarm codes, door codes and building credentials - "
                        + "is security-sensitive. It is shared only with the personnel who need it, "
                        + "exchanged through verified channels, never written into a contract or a "
                        + "notice, and returned or deleted when service ends."),
                    PolicyBlock.P(
                        "Each side keeps the other's non-public business, security and operational "
                        + "information confidential and uses it only to perform the Service Agreement. "
                        + "We do not use a client's name, logo, photographs or premises details in "
                        + "marketing without written permission."),
                    PolicyBlock.P(
                        "Our cleaners are our own personnel or qualified subcontractors, trained for "
                        + "their tasks and supervised by us, and we remain responsible for their work. "
                        + "Requests about scope, quality or staffing go to our designated supervisor. A "
                        + "client may ask for an individual to be removed from its site for a "
                        + "documented, reasonable safety, security or performance concern, and we "
                        + "provide a qualified replacement within a reasonable time.")
                }),

                Section("11", "Force Majeure, Governing Agreement & Contact", "general-provisions", new[]
                {
                    PolicyBlock.P(
                        "Neither party is responsible for a delay or failure caused by an event beyond "
                        + "its reasonable control - fire, flood, severe weather, a utility failure, a "
                        + "public-health restriction or governmental action - provided it gives prompt "
                        + "notice and works to resume. Fees already earned remain payable, and a visit "
                        + "that could not take place is not charged as though it had."),
                    PolicyBlock.P(
                        "New York law governs each Service Agreement. A dispute is first raised in "
                        + "writing and discussed between senior representatives, with mediation "
                        + "available on request, and the Service Agreement names the state courts in "
                        + "Kings County, New York as the venue."),
                    PolicyBlock.N(
                        "The executed Commercial Cleaning Service Agreement controls. If this page "
                        + "conflicts with a client's signed Service Agreement, its exhibits or a signed "
                        + "amendment or Change Order, the signed documents govern, and terms negotiated "
                        + "with a particular client remain valid. This page is a general summary and is "
                        + "not itself an offer, a contract, or a set of terms a client accepts by "
                        + "visiting the website."),
                    PolicyBlock.N(
                        "Publishing a later version of these policies does not change the terms of an "
                        + "agreement a client has already signed. Each Service Agreement records the "
                        + "policy version in force when it was prepared, and a signed agreement is "
                        + "changed only by a signed amendment. We do not reserve a right to vary an "
                        + "existing contract by editing a web page."),
                    PolicyBlock.P(
                        "These policies carry a version number and an effective date, shown at the top "
                        + "of this page and on both downloadable PDFs. Questions about them, or about a "
                        + "commercial cleaning proposal, can be sent to us directly:"),
                    PolicyBlock.B(LegalIdentity),
                    PolicyBlock.B("Email: " + ContactEmail),
                    PolicyBlock.B("Phone: " + ContactPhone),
                    PolicyBlock.B("Website: " + Website),
                    PolicyBlock.P(
                        "Formal notices under a signed agreement go to the notice email recorded in "
                        + "that agreement, which may differ from the general address above. Keys, alarm "
                        + "codes, passwords and banking details are never sent to any of these addresses.")
                })
            }
        };

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  The standalone Cancellation and Termination Policy
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The self-contained document handed to a prospective client on its own.
        ///
        /// Its two substantive sections are built by the SAME clause methods that build sections 3
        /// and 4 of the complete policies, so the two published documents cannot state different
        /// notice periods or different caps. Only the framing around them - the title, the
        /// introduction and the closing pointers - differs, because a document that travels alone
        /// has to say what it is and what it leaves out.
        /// </summary>
        public static PolicyDocument BuildCancellationAndTermination() => new()
        {
            Key = CancellationKey,
            Title = "Commercial Cleaning Cancellation & Termination Policy",
            Subtitle = "Cancelling a visit, and ending a commercial agreement",
            LegalIdentity = LegalIdentity,
            Version = Version,
            EffectiveDate = EffectiveDate,
            PdfFileName = CancellationPdfFileName,
            Intro = new List<PolicyBlock>
            {
                PolicyBlock.P(
                    "This document sets out how " + LegalIdentity + " handles the cancellation and "
                    + "rescheduling of individual commercial cleaning visits, and the termination of "
                    + "a commercial cleaning agreement. It reproduces sections 3 and 4 of our "
                    + "Commercial Cleaning Policies so they can be read on their own."),
                PolicyBlock.P(
                    "Two different things are described here and they should not be confused. "
                    + "Cancelling a scheduled visit moves or drops one cleaning. Terminating the "
                    + "agreement ends the recurring arrangement itself. The notice periods, the "
                    + "consequences and the procedures are different for each."),
                PolicyBlock.N(PrecedenceNote)
            },
            Sections = new List<PolicySection>
            {
                Section("1", "Cancellation, Rescheduling & Failed Access", "visit-cancellation",
                    CancellationClauses()),

                Section("2", "Contract Term & Termination", "contract-termination",
                    TerminationClauses()),

                Section("3", "Written Notices & Contact", "notices", new[]
                {
                    PolicyBlock.P(
                        "A cancellation or rescheduling request is an operational notice. It goes to "
                        + "the operational email address and on-call contact recorded in the client's "
                        + "Service Agreement, and it counts from when it is sent, provided it is not "
                        + "returned undeliverable. A text message counts only once the recipient has "
                        + "acknowledged it, which is why the timing of a cancellation should never rest "
                        + "on one."),
                    PolicyBlock.P(
                        "A notice of termination is a formal written notice. It goes to the designated "
                        + "notice email recorded in the Service Agreement, and is treated as received on "
                        + "the next business day after it is sent unless the sender learns that delivery "
                        + "failed. Business days exclude weekends and New York State public holidays, "
                        + "and times are local New York time."),
                    PolicyBlock.P(
                        "The general enquiry address is " + ContactEmail + " and the general telephone "
                        + "number is " + ContactPhone + ". A client with a signed agreement should use "
                        + "the addresses recorded in it, which may be different."),
                    PolicyBlock.N(
                        "This document covers cancellation and termination only. Scope, pricing, "
                        + "supplies, quality, damage claims, safety and confidentiality are set out in "
                        + "the complete Commercial Cleaning Policies and in the client's own signed "
                        + "Service Agreement.")
                })
            }
        };

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Shared clauses - the single source both documents are assembled from
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cancelling, rescheduling and failed access. Section 3 of the complete policies and
        /// section 1 of the standalone document are THIS LIST, not two copies of it.
        /// </summary>
        private static PolicyBlock[] CancellationClauses() => new[]
        {
            PolicyBlock.P(
                "These rules concern individual scheduled visits; ending the agreement itself is a "
                + "separate matter, covered in the next section. Notice is measured against the start "
                + "of the agreed arrival window."),

            PolicyBlock.P(
                "Rescheduling: with at least 24 hours' notice before the arrival window begins, a "
                + "client may reschedule a visit once at no additional charge, to a mutually agreed "
                + "date within 14 calendar days of the original visit. Advance payment carries over to "
                + "the makeup visit."),

            PolicyBlock.P(
                "Late cancellation: where a client cancels fewer than 24 hours before the arrival "
                + "window begins - or does not reasonably cooperate in arranging a makeup within 14 "
                + "calendar days after a timely request - we may charge our reasonable, documented net "
                + "loss from the missed visit, capped at 50% of the pre-tax visit fee, plus tax only "
                + "where the law requires it."),

            PolicyBlock.N(
                "That 50% figure is a ceiling on a documented loss, not a fee that is charged "
                + "automatically. If the loss was smaller the charge is smaller, and if there was no "
                + "loss, there is no charge. Where we cannot offer a reasonable makeup after a timely "
                + "request, no cancellation charge applies at all."),

            PolicyBlock.P(
                "Failed access: the crew arrived within the agreed window, ready to work, and could "
                + "not get in for a reason within the client's reasonable control. Before treating a "
                + "visit as failed access we try to reach the client's on-call contact, wait at least "
                + "20 minutes unless staying would be unsafe, and document the attempt. We may then "
                + "recover our reasonable, documented net loss from the visit, up to that visit's "
                + "pre-tax fee plus any tax the law applies. A failed-access charge replaces any "
                + "cancellation charge for the same visit, and if the visit is made up within 14 "
                + "calendar days, what was paid for it is credited to the makeup."),

            PolicyBlock.N(
                "A full fee for failed access is not automatic either. No charge applies where the "
                + "access problem was our own error, where we arrived outside the agreed window "
                + "without the client's agreement, or where an emergency prevented the visit."),

            PolicyBlock.P(
                "Emergency exceptions: no cancellation charge applies where an emergency beyond the "
                + "affected party's reasonable control - such as fire, flood, a utility failure or a "
                + "government closure - prevents the visit. We try to arrange a makeup within 14 "
                + "calendar days; otherwise the visit is credited."),

            PolicyBlock.P(
                "Cancellation by us: if we cancel a visit for any other reason, the client chooses a "
                + "prompt makeup or a credit, and a refund requested where a makeup is not practical "
                + "is issued within 10 business days."),

            PolicyBlock.P(
                "Repeated missed visits: where 3 client-attributable missed visits occur in a rolling "
                + "8-week period, that may be treated as a failure to keep the agreed frequency. After "
                + "the second we ask in writing for a workable service plan within 7 calendar days, "
                + "and only if that plan is not provided and followed and a third occurs may the "
                + "termination rights for cause be used. No price increase or penalty follows."),

            PolicyBlock.P(
                "The same loss is never charged twice. The detailed calculation of any permitted "
                + "charge is set out in the Service Agreement.")
        };

        /// <summary>
        /// Duration, notice, termination and the final reconciliation. Section 4 of the complete
        /// policies and section 2 of the standalone document.
        /// </summary>
        private static PolicyBlock[] TerminationClauses() => new[]
        {
            PolicyBlock.N(
                "Cancelling a cleaning visit and terminating the agreement are different things with "
                + "different notice periods. The previous section covers a visit. This section covers "
                + "the agreement."),

            PolicyBlock.P(
                "Dream Cleaning NYC has no standard or company-wide minimum commitment. Ongoing "
                + "commercial cleaning begins on the service commencement date stated in the Service "
                + "Agreement and continues until either party ends it."),

            PolicyBlock.P(
                "Either party may terminate ongoing commercial cleaning services with at least 30 "
                + "days' written notice, subject to any minimum service commitment specifically agreed "
                + "in the applicable Service Agreement. Services and payment continue until the "
                + "termination date."),

            PolicyBlock.N(
                "A minimum service commitment is a contract-specific term, not a company-wide rule. "
                + "It exists only where a client and Dream Cleaning NYC specifically agreed one, for "
                + "the period stated in that client's own Service Agreement. Where one applies, notice "
                + "may be given during the commitment, but a termination for convenience takes effect "
                + "no earlier than the end of it."),

            PolicyBlock.P(
                "Contract-specific terms control. The length of any agreed commitment, the notice "
                + "period and any other individually negotiated term are those in the client's signed "
                + "Service Agreement."),

            PolicyBlock.P(
                "Either party may end the agreement at any time for a material breach the other fails "
                + "to cure within 15 calendar days of written notice describing it. We may suspend "
                + "service for non-payment or unsafe conditions, and may terminate for an undisputed "
                + "amount left unpaid for 15 calendar days after a written demand. Either party may "
                + "terminate immediately where continuing would be unlawful or would expose people to "
                + "serious danger, and either may end the affected services with no early-termination "
                + "charge if an event beyond its control prevents substantial performance for 30 "
                + "consecutive calendar days. A minimum commitment never restricts any of these rights."),

            PolicyBlock.N(
                "There is no early-termination fee. Ending an agreement does not trigger a fixed exit "
                + "charge, and no remaining fees are automatically accelerated. A party that ends an "
                + "agreement wrongfully is responsible only for the proven direct loss the Service "
                + "Agreement describes."),

            PolicyBlock.P(
                "When an agreement ends, the client pays for services performed and properly supported "
                + "charges, and we provide a final itemized statement and refund unearned prepayments "
                + "and unused credits within 30 calendar days. An undisputed refund is not held back "
                + "while a separate dispute is resolved.")
        };

        /// <summary>
        /// "2026-09-16" to "September 16, 2026". Used by the PDF writer and by the agreement's
        /// {{POLICY_EFFECTIVE_DATE}} token, so a client reads the same date in the same words on
        /// the page, in the download and in the contract they sign.
        ///
        /// Falls back to the stored string rather than throwing: an unparseable date should print
        /// as it was written, not take a download or a contract render down with it.
        /// </summary>
        public static string FormatEffectiveDate(string isoDate)
        {
            return DateTime.TryParseExact(
                isoDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed)
                ? parsed.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture)
                : isoDate;
        }

        private static PolicySection Section(
            string number, string title, string anchor, IEnumerable<PolicyBlock> blocks) => new()
        {
            Number = number,
            Title = title,
            Anchor = anchor,
            Blocks = blocks.ToList()
        };
    }
}
