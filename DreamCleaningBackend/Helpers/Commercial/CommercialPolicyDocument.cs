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
    /// <c>DreamCleaningNG/src/app/shared/commercial-policies/commercial-policy.content.json</c> is
    /// the frontend's copy, and <c>CommercialPolicyContentTests</c> deserialises that file and
    /// asserts it is deep-equal to what this class builds - a mirrored pair with a test holding it
    /// together rather than a convention asking somebody to remember.
    ///
    /// EVERY OPERATIVE FIGURE HERE IS READ OUT OF THE MSA, not chosen. The notice windows, the
    /// caps, the insurance limits, the term lengths and the interest rate are the defaults on
    /// <c>ContractSnapshot</c>'s <c>TermSnapshot</c> / <c>AdvancedTermsSnapshot</c> /
    /// <c>PricingSnapshot</c>, and <c>CommercialPolicyContentTests</c> asserts each one against
    /// those defaults so the published policy cannot drift away from the agreement it describes.
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
    ///     sections 1, 16 and 17 say so on the page as well as in the PDF.
    /// </summary>
    public static class CommercialPolicyDocument
    {
        /// <summary>
        /// The published policy version. Frozen onto each generated contract version's snapshot
        /// (<c>ContractSnapshot.PolicyVersion</c>) so a later revision of this file cannot change
        /// what an executed agreement says was in force when it was signed.
        ///
        /// Raise it, and <see cref="EffectiveDate"/> with it, whenever the substance below changes.
        /// </summary>
        public const string Version = "1.0";

        /// <summary>ISO-8601. Printed on the page, both PDFs and Section 36 of every new contract.</summary>
        public const string EffectiveDate = "2026-09-16";

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
            "These policies are general service policies, not a contract. Where a signed Master "
            + "Service Agreement between Dream Cleaning NYC and a client addresses a subject "
            + "covered here, that agreement and its exhibits govern, and any individually "
            + "negotiated term in it prevails over the general policy below.";

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
                    "This page sets out the general policies under which " + LegalIdentity
                    + " provides commercial cleaning services in New York City. It is published so "
                    + "that a prospective client can read how we schedule, cancel, invoice, insure "
                    + "and stand behind commercial work before signing anything."),
                PolicyBlock.N(PrecedenceNote)
            },
            Sections = new List<PolicySection>
            {
                Section("1", "Introduction and Applicability", "introduction", new[]
                {
                    PolicyBlock.P(
                        "Commercial cleaning services are provided by " + LegalIdentity
                        + ", a New York corporation. In these policies \"we\", \"us\" and \"Dream "
                        + "Cleaning NYC\" mean that company, and \"client\" means the business that "
                        + "engages us."),
                    PolicyBlock.P(
                        "These policies apply to commercial cleaning - offices, restaurants, retail "
                        + "premises and similar business locations - and are written to reflect the "
                        + "terms of our commercial Master Service Agreement. They do not apply to "
                        + "residential cleaning, which is booked online and governed by our Terms "
                        + "and Conditions."),
                    PolicyBlock.P(
                        "Two kinds of terms appear on this page, and the difference matters. "
                        + "Company-wide policies describe how we work with every commercial client. "
                        + "Contract-specific terms - the price, the cleaning schedule, the scope of "
                        + "work, the length of the commitment and anything individually negotiated - "
                        + "exist only in a client's own signed agreement and its exhibits. Where a "
                        + "figure is quoted below it is the standard term we offer; a signed "
                        + "agreement may state something different, and if it does, the agreement "
                        + "controls."),
                    PolicyBlock.N(PrecedenceNote)
                }),

                Section("2", "Scope of Services", "scope", new[]
                {
                    PolicyBlock.P(
                        "We perform the cleaning tasks, areas and frequencies set out in Exhibit A "
                        + "of the client's agreement, at the premises identified in it. Services "
                        + "not expressly described there are not included."),
                    PolicyBlock.P(
                        "Each listed task is completed at each scheduled visit unless it is given a "
                        + "different frequency. Completion means removal of ordinary visible soil "
                        + "that is reasonably removable using the agreed methods. Staffing levels "
                        + "and the order the work is done in are ours to decide: the fee buys "
                        + "completion of the agreed tasks, not a stated number of labor hours."),
                    PolicyBlock.P(
                        "All-inclusive pricing does not mean unlimited services. Deep cleaning, "
                        + "restoration-level cleaning and specialized degreasing are not included "
                        + "unless the agreement says so. Results can vary with the age, material, "
                        + "finish and maintenance history of a surface; cleaning is not "
                        + "restoration, refinishing or repair, and we do not warrant removal of "
                        + "permanent staining, etching, rust, worn finishes or soil bonded to a "
                        + "surface over time."),
                    PolicyBlock.P(
                        "A material change to scope, recurring frequency, premises or service "
                        + "requirements is made by a written Change Order signed by both parties. "
                        + "An ordinary change to the day, arrival window or completion deadline of "
                        + "a visit is agreed by email and is not a Change Order. A verbal request, "
                        + "a text message or an instruction given to a cleaner on site does not add "
                        + "work to the agreement, and performing an extra task once does not "
                        + "permanently widen the scope."),
                    PolicyBlock.P(
                        "Some work is excluded from commercial cleaning unless it is added by "
                        + "signed Change Order and performed by a contractor holding the required "
                        + "qualifications, licenses and insurance. That includes kitchen exhaust "
                        + "hood, filter and duct cleaning; grease trap service; the internal "
                        + "cleaning or dismantling of cooking equipment; pest control; mold, "
                        + "biohazard, bloodborne-pathogen and sewage remediation; sharps handling; "
                        + "hazardous materials and asbestos work; exterior window cleaning and work "
                        + "from ladders, lifts or scaffolds; food preparation and handling; floor "
                        + "stripping, waxing, refinishing and grout restoration; snow and ice "
                        + "removal; and repairs, painting, electrical, plumbing or mechanical work.")
                }),

                Section("3", "Service Scheduling and Access", "scheduling-access", new[]
                {
                    PolicyBlock.P(
                        "A commercial agreement fixes a recurring frequency, the regular service "
                        + "day or days and an arrival window rather than a single arrival minute. "
                        + "The window is what matters operationally: it is the period we undertake "
                        + "to arrive in, and it is also the condition on which a failed-access "
                        + "charge depends. We tell the client promptly about a material delay."),
                    PolicyBlock.P(
                        "Changes to the service day, the arrival window or a completion deadline "
                        + "require confirmation by both sides, which can be by email. A request on "
                        + "its own does not move a visit. A change of day or time does not change "
                        + "the agreed number of visits per period."),
                    PolicyBlock.P(
                        "The client provides safe and timely access - keys, access cards, codes and "
                        + "accurate alarm instructions. Credentials are exchanged securely and "
                        + "separately from the agreement itself; they are never written into the "
                        + "contract document. We limit them to personnel who need them for the "
                        + "work, prohibit unauthorized copying or sharing, and notify the client "
                        + "promptly if we learn of a loss or compromise."),
                    PolicyBlock.P(
                        "On leaving, we lock the designated doors, follow the agreed alarm "
                        + "procedure, report any incident promptly and leave refrigeration and "
                        + "other powered equipment in its agreed operating condition. The client "
                        + "identifies the closeout procedure and any equipment that must stay on "
                        + "before service begins."),
                    PolicyBlock.P(
                        "On termination we return physical keys and delete or relinquish access "
                        + "credentials within 2 business days unless a different, safe transition "
                        + "is agreed in writing. Access credentials are never withheld as security "
                        + "for payment. Each side is responsible for re-keying, credential "
                        + "replacement and false-alarm costs to the extent its own negligence or "
                        + "inaccurate instructions caused them.")
                }),

                // Sections 4, 5 and 7 are assembled from the SAME clause builders the standalone
                // Cancellation and Termination Policy uses. That is the whole mechanism preventing
                // the two published documents from contradicting each other.
                Section("4", "Cleaning Cancellation and Rescheduling", "cancellation",
                    CancellationClauses()),

                Section("5", "Contract Duration and Termination", "termination",
                    TerminationClauses()),

                Section("6", "Pricing, Invoicing and Payment", "invoicing", new[]
                {
                    PolicyBlock.P(
                        "Commercial pricing is a recurring pre-tax fee per completed scheduled "
                        + "visit, stated in the client's own agreement. We do not publish client "
                        + "prices, negotiated discounts, invoices or banking details, and nothing "
                        + "on this page quotes a rate."),
                    PolicyBlock.P(
                        "Commercial cleaning is invoiced in advance. We ordinarily issue the "
                        + "invoice at least 7 calendar days before each scheduled visit, and "
                        + "payment of undisputed amounts is due 48 hours before the agreed arrival "
                        + "window begins. If an invoice reaches the client fewer than 5 calendar "
                        + "days before that deadline, the client has at least 3 business days after "
                        + "receiving it to pay, and we agree together whether to proceed or "
                        + "reschedule - with no client cancellation charge caused by our own late "
                        + "invoice."),
                    PolicyBlock.P(
                        "Payment is by ACH or bank transfer using verified instructions. A change "
                        + "to bank details is confirmed through a previously verified telephone "
                        + "number. No email on its own will ever authorize a change of payment "
                        + "destination, in either direction."),
                    PolicyBlock.P(
                        "If an undisputed advance payment is not made when due, we notify the "
                        + "client promptly and may withhold that visit unless payment arrives "
                        + "before the crew is dispatched. A visit withheld for non-payment is not a "
                        + "cancellation by us, and it does not automatically earn the full service "
                        + "fee: any charge for it is limited to the same documented net loss and "
                        + "cap described in section 4. Suspension is no broader or longer than "
                        + "reasonably necessary, and we resume on the next reasonably available "
                        + "agreed date once arrears and the required advance are paid."),
                    PolicyBlock.P(
                        "An undisputed earned amount that is unpaid for more than 5 calendar days "
                        + "after its due date accrues simple interest at 1% per month, calculated "
                        + "daily, equivalent to 12% per year - or the maximum lawful rate if that "
                        + "is lower. Interest is not compounded and is not charged on an amount "
                        + "that is genuinely in dispute while both sides are pursuing the dispute "
                        + "process promptly."),
                    PolicyBlock.P(
                        "We do not charge a flat returned-payment fee. Where a payment is returned "
                        + "or fails for a reason attributable to the client, we may recover the "
                        + "actual, reasonable bank and processing costs we incurred, supported on "
                        + "request."),
                    PolicyBlock.P(
                        "Billing disputes: a client raises an invoice dispute promptly and, for "
                        + "anything reasonably apparent from the invoice itself, within 10 business "
                        + "days of receiving it, identifying the amount and the basis. The "
                        + "undisputed portion is paid when due. Both sides exchange the supporting "
                        + "information reasonably needed and try to resolve the matter within 10 "
                        + "business days, and any amount found payable is paid within 5 business "
                        + "days of a written resolution. Missing that objection window is evidence "
                        + "that the apparent billing details were accepted, but it does not waive a "
                        + "claim about duplicate billing, unperformed services, a latent condition "
                        + "or a right that cannot lawfully be waived."),
                    PolicyBlock.P(
                        "Applicable sales tax is calculated on taxable charges, added to amounts "
                        + "due and stated separately on the invoice as the law requires. A charge "
                        + "for a visit that did not happen receives its own legally required tax "
                        + "treatment; we do not publish a tax-inclusive cancellation or "
                        + "failed-access figure. A client claiming a tax exemption provides a valid "
                        + "certificate before the first invoice date."),
                    PolicyBlock.P(
                        "A change to the recurring pre-tax fee needs mutual written agreement, "
                        + "documented by a signed amendment or Change Order. Neither side can move "
                        + "that fee unilaterally. We may ask for a prospective price review on at "
                        + "least 45 calendar days' written notice explaining a material change in "
                        + "agreed scope or in mandatory labor or performance costs; no increase "
                        + "takes effect unless both sides sign it, and no increase is ever "
                        + "retroactive.")
                }),

                Section("7", "Prepaid Services and Refunds", "prepaid-refunds",
                    PrepaidAndRefundClauses()),

                Section("8", "Client Responsibilities", "client-responsibilities", new[]
                {
                    PolicyBlock.P(
                        "At its own cost, the client provides working water, lighting and "
                        + "electrical power; access to designated lawful waste and recycling "
                        + "receptacles; adequate stocks of the consumables allocated to it; and "
                        + "surfaces and floors reasonably cleared so they can be cleaned."),
                    PolicyBlock.P(
                        "The client secures cash, valuables and confidential material, and "
                        + "discloses known hazards, damage and conditions affecting the work. That "
                        + "responsibility to secure property does not waive a claim for negligent "
                        + "damage or theft attributable to us."),
                    PolicyBlock.P(
                        "Cleaning between our visits, food-contact sanitation, pest control, waste "
                        + "hauling and any excluded maintenance remain the client's to arrange. "
                        + "Before the crew arrives, the client removes or protects exposed food and "
                        + "utensils, safely cools or shuts down equipment that has to be cleaned "
                        + "while off, identifies equipment that must stay running, and tells us "
                        + "about manufacturer restrictions it knows of. We do not disconnect gas, "
                        + "move connected cooking equipment, alter refrigeration settings or move "
                        + "heavy equipment unless a safe, qualified procedure has been specifically "
                        + "authorized."),
                    PolicyBlock.P(
                        "The client designates a primary on-call contact, and may designate a "
                        + "backup if one is available. The primary contact matters for a specific "
                        + "reason: we cannot charge for failed access unless we tried to reach that "
                        + "person from the door."),
                    PolicyBlock.P(
                        "The client tells us about landlord, franchisor or site requirements "
                        + "affecting access, products, insurance or the services before work "
                        + "begins. A requirement that changes the agreed scope or cost is handled "
                        + "as a pricing change or a Change Order rather than absorbed silently."),
                    PolicyBlock.P(
                        "Requests about scope, quality and staffing go to our designated "
                        + "supervisor. A client may ask for an individual to be removed from its "
                        + "site for a documented, reasonable safety, security or material "
                        + "performance concern - never for a discriminatory, retaliatory or "
                        + "otherwise unlawful reason - and we investigate promptly and provide a "
                        + "qualified replacement within a reasonable time.")
                }),

                Section("9", "Cleaning Supplies and Equipment", "supplies", new[]
                {
                    PolicyBlock.P(
                        "We supply, at our own expense, all labor, supervision, cleaning equipment, "
                        + "tools, chemicals, products and ordinary cleaning supplies used to "
                        + "perform the included services. There is no separate equipment or "
                        + "cleaning-product charge on a commercial agreement."),
                    PolicyBlock.P(
                        "The client supplies, at its own cost, toilet tissue, paper towels and "
                        + "trash can liners or trash bags, and keeps adequate stocks of them. If "
                        + "stocks run short we tell the client; we are not required to buy "
                        + "substitutes unless the client approves the purchase and the price in "
                        + "writing."),
                    PolicyBlock.P(
                        "Hand soap and soap dispensers are not part of a commercial cleaning "
                        + "agreement. We do not supply, replenish, repair or replace them at any "
                        + "time. This is stated plainly because an empty dispenser is a poor way to "
                        + "discover a boundary."),
                    PolicyBlock.P(
                        "Products are used and stored according to their labels and applicable "
                        + "requirements, and incompatible chemicals are never mixed. Restroom tools "
                        + "and cloths are kept separate from kitchen and dining tools. A special "
                        + "product or brand requirement is identified before service begins, and a "
                        + "requirement that affects scope or cost is handled as a pricing change or "
                        + "Change Order.")
                }),

                Section("10", "Quality Assurance and Satisfaction Guarantee", "quality", new[]
                {
                    PolicyBlock.P(
                        "Our commercial guarantee is a right to have deficient work put right, and "
                        + "it works like this. A client identifies a material failure to complete "
                        + "an included task to the agreed standard of care within 48 hours of the "
                        + "visit - or promptly after discovery, if it was not reasonably apparent "
                        + "earlier - describing the task and the condition and including any "
                        + "evidence readily available."),
                    PolicyBlock.P(
                        "We then inspect as reasonably necessary and have a reasonable first "
                        + "opportunity to re-perform the deficient task at no charge, ordinarily "
                        + "within 2 business days of the notice and access, or sooner where food "
                        + "safety reasonably requires it. The client provides reasonable access for "
                        + "that corrective visit."),
                    PolicyBlock.P(
                        "If we decline or fail to correct within that period, or correction is no "
                        + "longer reasonably useful, the client receives a reasonable credit or "
                        + "refund attributable to the deficient portion of the visit."),
                    PolicyBlock.N(
                        "The guarantee is re-performance of the task that fell short, and then a "
                        + "credit or refund for that portion of the visit. It is not an "
                        + "unconditional full refund and not a free re-clean of the whole premises. "
                        + "Later re-soiling, and work outside the agreed scope, are not service "
                        + "deficiencies; corrective work stays inside the agreed scope, and "
                        + "anything beyond it is additional paid work."),
                    PolicyBlock.P(
                        "An unrelated invoice amount is not withheld because of a quality "
                        + "complaint. This procedure does not bar reasonable emergency mitigation, "
                        + "termination for an uncured material breach, or claims for personal "
                        + "injury, property damage, fraud, gross negligence, willful misconduct or "
                        + "any other liability that cannot lawfully be limited.")
                }),

                Section("11", "Property Damage and Claims", "damage-claims", new[]
                {
                    PolicyBlock.P(
                        "A client tells us about alleged damage promptly and, where reasonably "
                        + "practicable, within 5 business days of discovering it, with a "
                        + "description and any photographs or other evidence available. We report "
                        + "damage or an incident we discover ourselves on the same basis."),
                    PolicyBlock.P(
                        "Both sides reasonably preserve the evidence and allow inspection before "
                        + "the affected item is repaired, replaced, disposed of or altered. "
                        + "Emergency action reasonably necessary to protect people, food safety, "
                        + "property or continued safe operation can go ahead without waiting for an "
                        + "inspection, with notice and documentation as soon as practicable."),
                    PolicyBlock.P(
                        "Late notice does not automatically waive a claim. Any consequence of it is "
                        + "limited to the actual material prejudice the affected party can prove "
                        + "and that the law permits. This procedure does not shorten a statutory "
                        + "limitations period."),
                    PolicyBlock.P(
                        "Pre-existing wear or damage does not make us responsible for restoring "
                        + "something, but it does not excuse new damage caused by negligent product "
                        + "selection or performance either. We use surface-appropriate products, "
                        + "follow available manufacturer instructions and make a reasonable "
                        + "inconspicuous compatibility check where that is warranted."),
                    PolicyBlock.N(
                        "Compensation reflects the legally recoverable loss. It is not a promise of "
                        + "unlimited compensation, and it is not an upgrade: a claim is not a route "
                        + "to a newer item than the one affected, and no loss is recovered twice "
                        + "across a refund, a credit, an insurance payment and a settlement.")
                }),

                Section("12", "Insurance and Liability", "insurance-liability", new[]
                {
                    PolicyBlock.P(
                        "Throughout performance we maintain Commercial General Liability insurance, "
                        + "written on an occurrence basis, with limits of at least $1,000,000 each "
                        + "occurrence and $2,000,000 general aggregate."),
                    PolicyBlock.P(
                        "We maintain workers' compensation, disability benefits and Paid Family "
                        + "Leave coverage to the extent New York law requires, and provide the "
                        + "applicable evidence of coverage or valid exemption documentation. We "
                        + "require legally appropriate coverage from any subcontractor."),
                    PolicyBlock.P(
                        "Certificates evidencing the required coverage are provided before services "
                        + "begin and on renewal, and we notify a client promptly after learning of "
                        + "a cancellation, lapse or material reduction affecting its services."),
                    PolicyBlock.N(
                        "Additional-insured status, primary and non-contributory wording and a "
                        + "waiver of subrogation are only real when they appear in an actual "
                        + "insurer-issued endorsement listed in the client's own agreement. A "
                        + "certificate of insurance does not amend a policy. Any requirement beyond "
                        + "the limits above is agreed in writing, including who pays for it, and "
                        + "the endorsement has to be issued before the work it covers. We do not "
                        + "publish certificate-holder or additional-insured details for any client."),
                    PolicyBlock.P(
                        "The client maintains premises-liability and property coverage appropriate "
                        + "to its own operations. Neither side's insurance reduces its "
                        + "responsibilities under the agreement, or any liability that cannot "
                        + "lawfully be limited."),
                    PolicyBlock.P(
                        "For ordinary contractual claims, each side's total aggregate liability to "
                        + "the other is capped at 13 times the recurring pre-tax per-visit fee in "
                        + "effect when the earliest event giving rise to a capped claim occurred. "
                        + "That is one aggregate limit for the whole agreement, not a fresh limit "
                        + "per claim or per visit. Neither side is liable to the other for "
                        + "indirect, special, incidental or consequential damages arising from an "
                        + "ordinary breach."),
                    PolicyBlock.P(
                        "Those limits do not apply to claims arising from bodily injury, death or "
                        + "damage to tangible property caused by a party's negligence; to fraud, "
                        + "gross negligence or willful misconduct; to lawful third-party "
                        + "indemnification; or to any liability that cannot lawfully be excluded or "
                        + "limited, including liability governed by New York General Obligations "
                        + "Law section 5-323. The cap also does not limit a client's obligation to "
                        + "pay earned fees, or our obligation to return money that was never earned.")
                }),

                Section("13", "Health, Safety and Hazardous Conditions", "safety", new[]
                {
                    PolicyBlock.P(
                        "We stop the affected work immediately when we reasonably encounter a "
                        + "serious safety hazard, or a condition outside our personnel's training, "
                        + "equipment or lawful scope. That includes sharps, uncontrolled blood or "
                        + "biohazard contamination, suspected asbestos, substantial mold, sewage "
                        + "backup, dangerous chemical spills, electrical hazards and unsafe "
                        + "structures. The same rule covers any other condition that puts a person "
                        + "at serious risk - an uncontrolled animal loose in the work area is "
                        + "handled the same way."),
                    PolicyBlock.P(
                        "When that happens we notify the client promptly, identify the affected "
                        + "area and carry on with the unaffected work wherever that is reasonably "
                        + "safe and practicable. Routine toilet and restroom cleaning in ordinary "
                        + "sanitary conditions stays included; specialized remediation does not, "
                        + "and a request or an offer of extra payment does not authorize "
                        + "unqualified or unlawful work."),
                    PolicyBlock.P(
                        "If the condition was not caused by us and results from a known hazard the "
                        + "client failed to disclose or address, the client pays for the work "
                        + "actually completed plus reasonable, documented unavoidable loss from the "
                        + "interruption, after avoided costs and any replacement earnings - with "
                        + "the total limited to the affected visit's pre-tax fee plus any legally "
                        + "applicable tax. No separate cancellation or failed-access charge is "
                        + "added for the same visit. Nothing is charged for work not performed "
                        + "because of a hazard we caused."),
                    PolicyBlock.P(
                        "Where the interruption comes from an emergency beyond the client's "
                        + "reasonable control, the emergency rule in section 4 applies instead. We "
                        + "resume once the condition is safely remedied and access is agreed; a "
                        + "persistent material condition is addressed as a termination question."),
                    PolicyBlock.P(
                        "During floor work we use appropriate wet-floor warnings and reasonable "
                        + "barriers, leave floors as dry as reasonably practicable and flag any "
                        + "remaining hazard before leaving. The client inspects before reopening "
                        + "the area, which does not waive our liability for a hazard we created.")
                }),

                Section("14", "Confidentiality and Property Security", "confidentiality", new[]
                {
                    PolicyBlock.P(
                        "Each side uses the other's non-public business, financial, customer, "
                        + "operational, security and access information only to perform the "
                        + "agreement or enforce lawful rights, and protects it with reasonable "
                        + "care. Disclosure is permitted to personnel, professional advisers, "
                        + "insurers and approved subcontractors who need to know and are themselves "
                        + "under confidentiality duties, and where the law requires it."),
                    PolicyBlock.P(
                        "The obligation does not cover information that is lawfully public, already "
                        + "lawfully known without restriction, independently developed, or lawfully "
                        + "received from an unrestricted third party. Ordinary confidentiality "
                        + "obligations continue for 2 years after termination; trade secrets stay "
                        + "protected for as long as they qualify as trade secrets, and access "
                        + "credentials for as long as they are usable."),
                    PolicyBlock.P(
                        "Access credentials are the security-sensitive part of commercial cleaning, "
                        + "so they are handled separately from the paperwork: keys, alarm codes and "
                        + "passwords are exchanged through verified channels and are never written "
                        + "into the agreement or into a notice. Each side notifies the other "
                        + "promptly of a known unauthorized disclosure affecting it and cooperates "
                        + "reasonably in limiting the damage. Statutory privacy, security and "
                        + "breach-notification duties are unaffected by anything here."),
                    PolicyBlock.P(
                        "Service photographs focus on surfaces and relevant damage, avoid people "
                        + "and sensitive information wherever reasonably practicable, and are used "
                        + "only for legitimate service, insurance or dispute purposes. We do not "
                        + "use a client's photographs, name, logo or identifying details in "
                        + "marketing without written permission."),
                    PolicyBlock.P(
                        "On request or on termination, protected information is returned or "
                        + "securely deleted, except records reasonably required by law, insurance "
                        + "or the preservation of legal claims - which stay protected.")
                }),

                Section("15", "Service Interruptions and Force Majeure", "force-majeure", new[]
                {
                    PolicyBlock.P(
                        "Neither side is liable for a delay or failure caused by an event beyond "
                        + "its reasonable control - fire, flood, severe weather, an epidemic-related "
                        + "restriction, utility failure, civil unrest or governmental action - "
                        + "provided the event was not caused by that side's own breach or "
                        + "negligence and could not reasonably have been avoided."),
                    PolicyBlock.P(
                        "The affected side notifies the other promptly, describes the impact and "
                        + "expected duration, and makes reasonable efforts to mitigate and resume. "
                        + "Ordinary staffing shortages, increased costs or an inability to pay do "
                        + "not on their own excuse performance."),
                    PolicyBlock.P(
                        "Fees already earned before the interruption stay payable. No service fee "
                        + "is earned for an unperformed visit simply because the agreement is still "
                        + "in force. Prepayments, makeup visits and credits for an interrupted "
                        + "visit follow the emergency rule in section 4."),
                    PolicyBlock.P(
                        "If substantial performance is prevented for 30 consecutive calendar days, "
                        + "either side may terminate the affected services by written notice with "
                        + "no early-termination charge, subject to payment for completed work and "
                        + "the return of unearned amounts.")
                }),

                Section("16", "Changes to Services and Policies", "changes", new[]
                {
                    PolicyBlock.P(
                        "A change to the services, the price, the frequency or the premises is "
                        + "agreed between the parties in writing - a signed amendment or Change "
                        + "Order - and never imposed by one side. Scheduling adjustments are "
                        + "confirmed by email and change nothing else."),
                    PolicyBlock.P(
                        "These published policies carry a version number and an effective date, "
                        + "shown at the top of this page and on both downloadable PDFs. When the "
                        + "substance changes, the version and date change with it."),
                    PolicyBlock.N(
                        "Publishing a new version of this page does not change the terms of an "
                        + "agreement a client has already signed. Each commercial agreement records "
                        + "the policy version in force when it was executed, and a signed agreement "
                        + "is only amended by a signed amendment. We do not reserve a right to vary "
                        + "an existing contract by editing a web page."),
                    PolicyBlock.P(
                        "Where a client is reading this page before signing, the version shown is "
                        + "the one we are currently offering. Where a client has already signed, "
                        + "the version recorded in that agreement is the one that was accepted.")
                }),

                Section("17", "Disputes and Governing Agreement", "disputes", new[]
                {
                    PolicyBlock.P(
                        "A dispute starts with a written notice describing it and the resolution "
                        + "sought. Senior representatives confer in good faith within 10 calendar "
                        + "days. If the matter is still unresolved after 15 calendar days, either "
                        + "side may request non-binding mediation in Kings County, New York, or "
                        + "remotely by agreement, and the mediator's charges are shared equally. "
                        + "Either side may begin proceedings 30 calendar days after the original "
                        + "dispute notice if the matter remains unresolved and that side has "
                        + "cooperated reasonably."),
                    PolicyBlock.P(
                        "Those preliminary steps do not prevent emergency or provisional relief, a "
                        + "filing reasonably necessary to preserve a claim before a limitations "
                        + "deadline, a lawful agency complaint, or an eligible small-claims or "
                        + "commercial-claims proceeding - and they do not, by themselves, pause any "
                        + "statutory deadline."),
                    PolicyBlock.P(
                        "Collection of an undisputed amount can be pursued after a written demand "
                        + "and 5 business days to pay, without mediation. That applies both ways: "
                        + "to earned fees owed to us and to an undisputed refund owed to a client."),
                    PolicyBlock.P(
                        "In a direct action between the parties to recover earned fees, properly "
                        + "supported contractual charges, or a required refund or credit, the "
                        + "substantially prevailing party may recover reasonable attorneys' fees "
                        + "and court costs attributable to that payment dispute, as the court "
                        + "determines. For other direct disputes each side bears its own fees "
                        + "unless a statute provides otherwise."),
                    PolicyBlock.P(
                        "New York law governs, without regard to its conflict-of-laws rules. The "
                        + "parties consent to exclusive venue in the state courts sitting in Kings "
                        + "County, New York and, only where federal subject-matter jurisdiction "
                        + "independently exists, the United States District Court for the Eastern "
                        + "District of New York sitting in Brooklyn. Nothing restricts a lawful "
                        + "claim before an agency, or before a court whose jurisdiction cannot be "
                        + "waived by agreement."),
                    PolicyBlock.N(
                        "The relationship between this page and a signed agreement: the executed "
                        + "Master Service Agreement, its exhibits and any signed amendment or "
                        + "Change Order are the contract. This page describes our general practice "
                        + "and is not itself an offer, a contract, or a set of terms a client "
                        + "accepts by visiting the website. Where the two differ, the signed "
                        + "agreement governs.")
                }),

                Section("18", "Contact Information", "contact", new[]
                {
                    PolicyBlock.P(
                        "Questions about these policies, or about a commercial cleaning proposal, "
                        + "can be sent to us directly."),
                    PolicyBlock.B(LegalIdentity),
                    PolicyBlock.B("Email: " + ContactEmail),
                    PolicyBlock.B("Phone: " + ContactPhone),
                    PolicyBlock.B("Website: " + Website),
                    PolicyBlock.P(
                        "Formal notices under a signed agreement go to the designated notice email "
                        + "recorded in that agreement's Exhibit B, which may differ from the "
                        + "general address above. Scheduling, cancellation, access and urgent "
                        + "safety messages go to the operational email and on-call contact recorded "
                        + "there. Keys, alarm codes, passwords and banking credentials are never "
                        + "sent to any of these addresses.")
                })
            }
        };

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  The standalone Cancellation and Termination Policy
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The self-contained document handed to a prospective client on its own.
        ///
        /// Its three substantive sections are built by the SAME three clause methods that build
        /// sections 4, 5 and 7 of the complete policies, so the two published documents cannot
        /// state different notice periods or different caps. Only the framing around them - the
        /// title, the introduction and the closing pointers - differs, because a document that
        /// travels alone has to say what it is and what it leaves out.
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
                    + "rescheduling of individual commercial cleaning visits, and the termination "
                    + "of a commercial cleaning agreement. It reproduces sections 4, 5 and 7 of our "
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
                Section("1", "Cancelling and Rescheduling an Individual Visit", "visit-cancellation",
                    CancellationClauses()),

                Section("2", "Contract Duration and Termination", "contract-termination",
                    TerminationClauses()),

                Section("3", "Prepaid Services, Refunds and Outstanding Payments", "prepaid-refunds",
                    PrepaidAndRefundClauses()),

                Section("4", "Written Notices and Contact", "notices", new[]
                {
                    PolicyBlock.P(
                        "A cancellation or rescheduling request is an operational notice. It goes "
                        + "to the operational email address and on-call contact recorded in the "
                        + "client's agreement, and it counts from when it is sent, provided it is "
                        + "not returned undeliverable. A text message counts as operational notice "
                        + "only once the recipient has acknowledged it - which is why the timing of "
                        + "a cancellation should never rest on one."),
                    PolicyBlock.P(
                        "A notice of termination, of breach, of assignment or of a dispute is a "
                        + "formal notice. It goes to the designated notice email recorded in the "
                        + "agreement. An email is treated as received on the next business day "
                        + "after it is sent if the sender keeps a transmission record and gets no "
                        + "delivery-failure message; if the sender knows delivery failed, another "
                        + "permitted method has to be used. Where a party has designated a mailing "
                        + "address, personal or courier delivery to it also works, and is effective "
                        + "on documented receipt."),
                    PolicyBlock.P(
                        "Business days exclude Saturdays, Sundays and New York State public "
                        + "holidays, and every time referred to in an agreement is local New York "
                        + "time. Either side can update its contact details by formal notice."),
                    PolicyBlock.P(
                        "The general enquiry address is " + ContactEmail + " and the general "
                        + "telephone number is " + ContactPhone + ". A client with a signed "
                        + "agreement should use the addresses recorded in it, which may be "
                        + "different, rather than these."),
                    PolicyBlock.N(
                        "This document covers cancellation and termination only. Scope, pricing, "
                        + "quality, damage claims, insurance, liability, safety, confidentiality "
                        + "and dispute resolution are set out in the complete Commercial Cleaning "
                        + "Policies and in the client's own signed agreement.")
                })
            }
        };

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Shared clauses - the single source both documents are assembled from
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cancelling, rescheduling and failed access. Section 4 of the complete policies and
        /// section 1 of the standalone document are THIS LIST, not two copies of it.
        /// </summary>
        private static PolicyBlock[] CancellationClauses() => new[]
        {
            PolicyBlock.P(
                "Notice is measured against the start of the agreed arrival window, not against the "
                + "middle of the visit or the end of the working day. Every period below runs from "
                + "that moment."),

            PolicyBlock.P(
                "With at least 24 hours' notice before the arrival window begins, a client may "
                + "reschedule a scheduled visit once at no additional charge, to a mutually agreed "
                + "date within 14 calendar days of the original visit. We offer available dates "
                + "reasonably, consistent with safe access and the client's own operations. Advance "
                + "payment carries over to the makeup visit. Further changes to the same visit are "
                + "by mutual agreement."),

            PolicyBlock.P(
                "A makeup visit belongs to the service period the original visit was scheduled in. "
                + "It satisfies that period and does not take the place of another period's visit "
                + "unless that is expressly agreed."),

            PolicyBlock.P(
                "Where a client cancels fewer than 24 hours before the arrival window begins - or "
                + "does not reasonably cooperate in arranging a makeup within 14 calendar days "
                + "after a timely request - we may charge our reasonable, documented net loss "
                + "attributable to the missed visit, after avoided costs and any replacement "
                + "earnings, capped at 50% of the pre-tax visit fee, plus tax only where the law "
                + "requires it."),

            PolicyBlock.N(
                "That 50% figure is a ceiling on a documented loss, not a fee that is charged "
                + "automatically. If the crew was redeployed, or the loss was smaller, the charge "
                + "is smaller; if there was no loss, there is no charge. Where we cannot offer a "
                + "reasonable makeup opportunity after a timely request, no client cancellation "
                + "charge applies at all."),

            PolicyBlock.P(
                "If a charged visit is rescheduled within 14 calendar days, the charge and every "
                + "other payment for that visit are credited to the makeup, so the total for the "
                + "original and the makeup never exceeds one full service fee plus any properly "
                + "applicable tax. A remaining prepayment for a visit that will not happen is "
                + "credited to the next invoice, or refunded within 30 calendar days if the "
                + "agreement ends first."),

            PolicyBlock.P(
                "Failed access is a specific situation with specific conditions: the crew arrived "
                + "within the agreed window, was ready and able to work, and could not get in for a "
                + "reason within the client's reasonable control. Before it counts, we attempt to "
                + "contact the designated on-call representative and wait at least 20 minutes "
                + "unless staying would be unsafe, and we document the arrival, the attempted "
                + "contact and the reason access failed."),

            PolicyBlock.P(
                "Where access is not provided, we may recover the reasonable, documented net loss "
                + "caused by the failed visit - the agreed pre-tax fee less reasonably avoided "
                + "costs and any replacement earnings - with supporting records, and the total may "
                + "not exceed that visit's pre-tax service fee plus any tax legally applicable to "
                + "the charge. A failed-access charge replaces any cancellation charge for the same "
                + "visit; the two are never both applied. If the visit is rescheduled within 14 "
                + "calendar days, everything paid for the missed visit is credited to the makeup."),

            PolicyBlock.N(
                "A full fee for failed access is not automatic either, and three situations are "
                + "excluded from it outright: an access failure that was our own error, an arrival "
                + "outside the agreed window that the client did not accept, and an emergency "
                + "covered by the rule below."),

            PolicyBlock.P(
                "Emergencies: no cancellation or reservation charge applies where an emergency "
                + "beyond the affected party's reasonable control prevents performance - including "
                + "fire, flood, utility failure or a government closure not caused by that party's "
                + "own breach. Prompt notice and reasonable mitigation are expected. We attempt a "
                + "makeup within 14 calendar days; failing that, the unperformed services are "
                + "credited, and any unused credit is refunded within 30 calendar days after "
                + "termination. A health closure caused by an unresolved condition within the "
                + "client's control is assessed as a hazardous-condition interruption rather than "
                + "treated automatically as an emergency."),

            PolicyBlock.P(
                "Cancellation by us: if we cancel a visit for any reason other than such an "
                + "emergency, the client chooses between a reasonably prompt makeup and a credit "
                + "for the unperformed services. Where a makeup is not reasonably useful or "
                + "possible and the client asks for a refund, we issue it within 10 business days "
                + "of that request. Accepting a credit or a refund does not waive a remedy for a "
                + "material breach."),

            PolicyBlock.P(
                "A cancellation does not reduce the agreed frequency. Paying a permitted "
                + "missed-visit charge settles that one visit; a skipped visit is only a skipped "
                + "visit if it is recorded in writing as one. Where 3 client-attributable missed "
                + "visits occur in a rolling 8-week period, that may amount to a material failure "
                + "to maintain the agreed frequency: after the second, we give written notice and "
                + "ask for a workable service plan within 7 calendar days, and if that plan is not "
                + "provided and followed and a third occurs, we may exercise the termination rights "
                + "described in the next section. Qualifying emergencies and our own cancellations "
                + "do not count towards that total, and no automatic price increase or additional "
                + "penalty follows from it."),

            PolicyBlock.P(
                "Finally, the same loss is never charged twice. A visit cannot attract both its "
                + "full price and a cancellation, failed-access or suspension charge, and a charge "
                + "recovered under one of these rules is not recovered again under another or in a "
                + "termination claim.")
        };

        /// <summary>
        /// Duration, notice and termination. Section 5 of the complete policies and section 2 of
        /// the standalone document.
        /// </summary>
        private static PolicyBlock[] TerminationClauses() => new[]
        {
            PolicyBlock.N(
                "Cancelling a cleaning visit and terminating the agreement are different acts with "
                + "different notice periods. The preceding section covers a visit. This section "
                + "covers the agreement."),

            PolicyBlock.P(
                "A commercial agreement takes effect on its effective date, and recurring services "
                + "commence on the separately stated Service Commencement Date. The initial term "
                + "runs from that commencement date. Both the length of the initial term and the "
                + "length of any minimum commitment are stated in the client's own agreement; the "
                + "term we currently offer as standard is 10 months, with a minimum commitment "
                + "period of the same length."),

            PolicyBlock.N(
                "A minimum commitment is a contract-specific term, not a company-wide rule. It is "
                + "whatever a particular signed agreement states, and it is negotiated before "
                + "signature rather than imposed afterwards."),

            PolicyBlock.P(
                "Either party may terminate for convenience on at least 60 calendar days' written "
                + "notice. Notice can be given during the minimum commitment period, but "
                + "termination for convenience does not take effect before the minimum commitment "
                + "end date stated in the agreement. Services and payment obligations continue "
                + "until the effective termination date."),

            PolicyBlock.P(
                "After the initial term, the agreement continues automatically on a month-to-month "
                + "basis on the same terms. It does not roll into a further fixed term. Either "
                + "party may end that continuation on at least 60 calendar days' written notice."),

            PolicyBlock.P(
                "Either party may terminate at any time, including during the minimum commitment "
                + "period, for a material breach the other fails to cure within 15 calendar days of "
                + "a written notice describing it. The minimum commitment restricts termination for "
                + "convenience only - it never restricts termination for cause, lawful safety "
                + "measures, or termination following a prolonged force-majeure interruption."),

            PolicyBlock.P(
                "We may suspend services for non-payment or for a hazardous condition as described "
                + "elsewhere in these policies, and may terminate for non-payment of an undisputed "
                + "amount once it has remained unpaid for 15 calendar days following a written "
                + "demand identifying it. Either party may terminate immediately by written notice "
                + "where continued performance would be unlawful, or would expose people to an "
                + "imminent serious danger that cannot reasonably be eliminated through suspension "
                + "or other protective measures."),

            PolicyBlock.P(
                "Where substantial performance is prevented by an event beyond a party's reasonable "
                + "control for 30 consecutive calendar days, either party may terminate the "
                + "affected services by written notice with no early-termination charge, subject to "
                + "payment for completed work and the return of unearned amounts."),

            PolicyBlock.N(
                "There is no separate early-termination penalty in our commercial agreement. Ending "
                + "an agreement early does not trigger a fixed exit fee, and no remaining fees are "
                + "automatically accelerated. Where a termination is wrongful, the non-breaching "
                + "party keeps a claim for proven direct damages, subject to mitigation and to the "
                + "liability limits in the agreement - and our claim for unperformed future visits "
                + "is limited to proven net loss through the earliest date on which the client "
                + "could validly have terminated for convenience had notice been given on the "
                + "breach date."),

            PolicyBlock.P(
                "Obligations that survive termination are the ones whose nature requires it: "
                + "accrued payment and refund duties, confidentiality, the claims procedures, "
                + "lawful indemnity, the liability limitations, governing law and dispute "
                + "resolution.")
        };

        /// <summary>
        /// Final reconciliation, prepayments and outstanding balances. Section 7 of the complete
        /// policies and section 3 of the standalone document.
        /// </summary>
        private static PolicyBlock[] PrepaidAndRefundClauses() => new[]
        {
            PolicyBlock.P(
                "Because commercial cleaning is invoiced in advance, an agreement that ends usually "
                + "has money on both sides of it: fees earned for work already done, and "
                + "prepayments for visits that will not now happen."),

            PolicyBlock.P(
                "On termination the client pays the earned fees for services performed and any "
                + "properly supported charges accrued under the agreement. We provide a final "
                + "itemized statement and refund unearned prepayments and unapplied credits, after "
                + "applying undisputed amounts due, within 30 calendar days."),

            PolicyBlock.P(
                "A deduction we dispute has to be identified and supported - and an undisputed "
                + "refund is not held back while a separate dispute is resolved. No amount is "
                + "retained or charged twice for the same loss, and a proper suspension of service "
                + "does not earn full fees for visits that were never performed."),

            PolicyBlock.P(
                "Where a visit is cancelled or missed while the agreement continues, any remaining "
                + "prepayment for that visit is credited to the next invoice rather than refunded, "
                + "unless the agreement ends first - in which case it is refunded within 30 "
                + "calendar days. Where we cancelled the visit and a makeup is not reasonably "
                + "useful or possible, a refund requested by the client is issued within 10 "
                + "business days."),

            PolicyBlock.P(
                "Tax is reconciled to what actually happened. Sales tax collected on a visit that "
                + "did not take place is adjusted; a charge for an unperformed visit gets its own "
                + "legally required tax treatment rather than carrying the tax figure of a "
                + "completed visit."),

            PolicyBlock.P(
                "Outstanding balances survive termination. An undisputed earned amount that is more "
                + "than 5 calendar days overdue accrues simple interest at 1% per month, calculated "
                + "daily, equivalent to 12% per year, or the maximum lawful rate if that is lower - "
                + "and collection of an undisputed amount may be pursued after a written demand and "
                + "5 business days to pay.")
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
