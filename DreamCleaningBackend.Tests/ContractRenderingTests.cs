using System.Text.RegularExpressions;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE TEMPLATE MUST NOT LEAK A HARDCODED BUSINESS VALUE.
    ///
    /// The seeded body is the executed reference MSA with every client-, location-, price-,
    /// schedule- and term-specific value replaced by a token. The failure mode this guards is
    /// subtle and expensive: a value that was missed stays correct for the ONE client it came
    /// from and is silently wrong for every client afterwards - a contract naming Chick Tastic's
    /// address in someone else's agreement.
    ///
    /// So these tests render the template for a DIFFERENT client at a DIFFERENT price on a
    /// DIFFERENT schedule and assert that nothing from the reference survives.
    /// </summary>
    public class ContractRenderingTests
    {
        /// <summary>A snapshot that shares nothing with the reference agreement.</summary>
        private static ContractSnapshot OtherClientSnapshot()
        {
            var snapshot = new ContractSnapshot
            {
                ContractNumber = "DC-2026-0042",
                VersionNumber = 1,
                EffectiveDate = new DateTime(2026, 3, 1),
                TemplateBodyText = ContractTemplateSeed.BodyText,
                PremisesType = "office",
                Contractor = new ContractorSnapshot
                {
                    LegalEntityName = "Nodar Alania Inc.",
                    Dba = "Dream Cleaning NYC",
                    EntityType = "a New York corporation",
                    Address = "8800 20th Ave, Apt 2B",
                    City = "Brooklyn",
                    State = "NY",
                    Zip = "11214",
                    NoticeEmail = "hello@dreamcleaningnyc.com",
                    Phone = "9299301525"
                },
                Client = new ClientSnapshot
                {
                    LegalEntityName = "Northline Holdings Inc.",
                    EntityType = "a Delaware corporation",
                    PrincipalAddress = "12 Water Street",
                    City = "Jersey City",
                    State = "NJ",
                    Zip = "07302",
                    NoticeEmail = "ap@northline.example",
                    Phone = "2125551234"
                },
                ServiceLocation = new ServiceLocationSnapshot
                {
                    BusinessBrand = "Northline",
                    LocationName = "Midtown office",
                    Address = "455 Madison Ave, Floor 3",
                    City = "New York",
                    State = "NY",
                    Zip = "10022"
                },
                ContractorSigner = new SignerSnapshot
                {
                    FirstName = "Nodar", LastName = "Alania", Title = "CEO",
                    Email = "hello@dreamcleaningnyc.com"
                },
                ClientSigner = new SignerSnapshot
                {
                    FirstName = "Dana", LastName = "Okafor", Title = "Facilities Director",
                    Email = "dana@northline.example"
                },
                Schedule = new ScheduleSnapshot
                {
                    FrequencyUnit = "calendar week",
                    VisitsPerPeriod = 2,
                    ServiceDay = "Tuesday",
                    ServiceDays = new List<string> { "Tuesday", "Thursday" },
                    ServiceTime = "7:00 PM",
                    ArrivalWindowStart = "7:00 PM",
                    ArrivalWindowEnd = "8:00 PM",
                    TimeZoneLabel = "local New York time",
                    CompletionTime = "by 11:00 PM",
                    WeekDefinition = "Monday through Sunday",
                    FlexibleScheduling = true,
                    PerformedWhileClosed = true,
                    AccessType = "a badge issued by Client"
                },
                Term = new TermSnapshot
                {
                    InitialTermMonths = 6,
                    MinimumCommitmentMonths = 2,
                    TerminationNoticeDays = 45,
                    ServiceCommencementDate = new DateTime(2026, 4, 1),
                    RenewalType = "month-to-month",
                    GoverningLawState = "New York",
                    VenueCounty = "New York County"
                },
                Pricing = new PricingSnapshot
                {
                    PriceMode = ContractPriceMode.PreTax,
                    PriceInput = 400m,
                    SalesTaxRatePercent = 8.875m,
                    CancellationPercent = 50m,
                    LateChargePercent = 1m,
                    LiabilityCapMultiple = 10
                },
                Advanced = new AdvancedTermsSnapshot
                {
                    CurePeriodDays = 20,
                    PastDueDays = 45,
                    ConfidentialityYears = 3,
                    DisputeDiscussionDays = 21,
                    MediationVenue = "New York County, New York"
                },
                // Filled in full so EveryPlaceholderInTheSeededTemplateResolves means something.
                // A blank one is a deliberate, separately-tested state - see
                // AnUnansweredSiteDetailPrintsARuledBlankAndIsFlagged.
                SiteDetails = new SiteDetailsSnapshot
                {
                    ApproximateSquareFootage = "3,200 square feet",
                    CustomerRestroomCounts = "2 restrooms, 2 toilets, 2 sinks",
                    EmployeeRestroomCounts = "1 restroom, 1 toilet, 1 sink",
                    FloorMaterials = "carpet tile throughout, sealed concrete at the entrance",
                    KitchenEquipmentAndSurfaces = "pantry counter, microwave exterior, refrigerator exterior",
                    TouchpointLocations = "entry doors, elevator call buttons, conference room handles",
                    InteriorGlassLocations = "reception partition, three conference room walls",
                    FoodContactSanitizing = "None agreed",
                    AccessMethodReference = "Badge access procedure BP-14",
                    EquipmentRestrictions = "server room untouched; do not power down anything",
                    WasteReceptacleLocations = "loading dock, bay 2",
                    FoodServicePermitHolder = "Not applicable",
                    SiteRequirements = "Building requires a certificate of insurance on file",
                    BaselineWalkthroughRecord = "March 20, 2026, photo set BW-3",
                    InitialWorkChangeOrder = "CO-001"
                },
                Contacts = new OperationalContactsSnapshot
                {
                    ContractorApprovalEmail = "contracts@dreamcleaningnyc.com",
                    ContractorOperationalEmail = "hello@dreamcleaningnyc.com",
                    ContractorSupervisorName = "Nodar Alania",
                    ContractorSupervisorPhone = "9299301525",
                    ContractorBackupContact = "Operations desk, (929) 930-1526",
                    ClientApprovalEmail = "contracts@northline.example",
                    ClientNoticeMailingAddress = "12 Water Street, Jersey City, NJ 07302",
                    ClientOperationalEmail = "facilities@northline.example",
                    ClientOnCallName = "Dana Okafor",
                    ClientOnCallPhone = "2125551234",
                    ClientBackupContact = "Security desk, (212) 555-9000"
                },
                Insurance = new InsuranceEndorsementsSnapshot
                {
                    AgreedEndorsements = "Additional insured",
                    EndorsementDetails = "Acme Mutual, policy CGL-99, form CG 20 26 04 13",
                    AdditionalPremium = "None"
                },
                Scope = ScopeStructureFor("Office")
            };
            ContractPricingCalculator.Recalculate(snapshot.Pricing);
            return snapshot;
        }

        private static ScopeStructure ScopeStructureFor(string templateName)
        {
            var seed = ContractScopeTemplateSeed.All().First(t => t.Name == templateName);
            return seed.Structure.Clone();
        }

        // ── the leak guard ─────────────────────────────────────────────────────

        [Theory]
        // Reference client, its addresses, its people and its numbers. None may survive a render
        // for a different client.
        [InlineData("Chick Tastic")]
        [InlineData("Chick-fil-A")]
        [InlineData("1569 Flatbush")]
        [InlineData("Natalie")]
        [InlineData("nataliefinkelstein")]
        [InlineData("732")]
        [InlineData("849.99")]
        [InlineData("925.43")]
        [InlineData("425.00")]
        [InlineData("424.99")]
        [InlineData("11,049.87")]
        [InlineData("11210")]
        // Site facts of the reference premises. These arrived with the drafted Exhibit A and are
        // exactly the kind of value that reads as harmless boilerplate until it is printed in
        // somebody else's agreement.
        [InlineData("5,000 square feet")]
        [InlineData("5000 Square Feet")]
        [InlineData("8:30 AM to 9:30 AM")]
        public void NoValueFromTheReferenceContractSurvivesForADifferentClient(string leaked)
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            Assert.DoesNotContain(leaked, rendered.PlainText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EveryPlaceholderInTheSeededTemplateResolves()
        {
            // A token the snapshot cannot fill is rendered literally so an admin SEES it on the
            // preview. A fully-populated snapshot must leave none.
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Empty(rendered.UnresolvedTokens);
            Assert.DoesNotContain("{{", rendered.PlainText);
        }

        /// <summary>
        /// AN UNANSWERED FIELD IS VISIBLE AND FLAGGED, not silently blank and not "None".
        ///
        /// "Employee restroom and fixture counts: None" asserts the premises has no employee
        /// restroom; an empty string leaves a sentence ending in a stray colon. A ruled blank says
        /// the question is still open, and flagging it as unresolved is what puts it in the
        /// preview banner an admin reads before the client does.
        /// </summary>
        [Fact]
        public void AnUnansweredSiteDetailPrintsARuledBlankAndIsFlagged()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.SiteDetails.EmployeeRestroomCounts = null;

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("EMPLOYEE RESTROOM AND FIXTURE COUNTS: " + ContractPlaceholders.RuledBlank,
                rendered.PlainText);
            Assert.Contains("EMPLOYEE_RESTROOM_COUNTS", rendered.UnresolvedTokens);
            // And it is a blank, not an assertion that there isn't one.
            Assert.DoesNotContain("EMPLOYEE RESTROOM AND FIXTURE COUNTS: None", rendered.PlainText);
        }

        /// <summary>
        /// The opposite rule, for the fields the drafted agreement writes as "[... OR NONE]".
        /// An empty completion time is a TERM the Parties agreed, and a crew reading a blank there
        /// would not know whether one exists.
        /// </summary>
        [Fact]
        public void AnOrNoneFieldPrintsNoneRatherThanABlank()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Schedule.CompletionTime = null;
            snapshot.SiteDetails.SiteRequirements = "";

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("REQUIRED COMPLETION TIME: None", rendered.PlainText);
            Assert.Contains("SITE, LANDLORD OR BRAND REQUIREMENTS AFFECTING THE SERVICES: None",
                rendered.PlainText);
            Assert.DoesNotContain("COMPLETION_TIME", rendered.UnresolvedTokens);
        }

        /// <summary>
        /// Exhibit B's two end dates are DERIVED from the commencement date, so the document can
        /// never state a Minimum Commitment End Date that disagrees with the number of months in
        /// the sentence above it.
        /// </summary>
        [Fact]
        public void TheTermEndDatesAreDerivedFromTheCommencementDate()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            // Commencement 1 April 2026, minimum commitment two months, initial term six.
            Assert.Contains("April 1, 2026", rendered.PlainText);
            Assert.Contains("June 1, 2026", rendered.PlainText);
            Assert.Contains("September 30, 2026", rendered.PlainText);
        }

        /// <summary>
        /// An unset commencement date leaves all three rows BLANK rather than falling back to the
        /// effective date. Guessing would silently shorten the commitment the client is agreeing
        /// to — an agreement signed in March for a May start commits its full length of cleaning.
        /// </summary>
        [Fact]
        public void NoCommencementDateLeavesTheTermDatesBlankRatherThanGuessing()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Term.ServiceCommencementDate = null;

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("SERVICE_COMMENCEMENT_DATE", rendered.UnresolvedTokens);
            Assert.Contains("MINIMUM_COMMITMENT_END_DATE", rendered.UnresolvedTokens);
            Assert.Contains("INITIAL_TERM_END_DATE", rendered.UnresolvedTokens);
            // The effective date is 1 March 2026 and must not have been borrowed.
            Assert.DoesNotContain("Minimum Commitment End Date: March 1, 2026", rendered.PlainText);
        }

        // ── the values that must appear ────────────────────────────────────────

        [Fact]
        public void TheDocumentQuotesTheServerDerivedFigures()
        {
            var snapshot = OtherClientSnapshot();
            var rendered = ContractRenderer.Render(snapshot);

            // 400.00 pre-tax at 8.875% -> 35.50 tax -> 435.50 total.
            Assert.Equal(435.50m, snapshot.Pricing.TotalPrice);
            Assert.Contains("$400.00", rendered.PlainText);
            Assert.Contains("$435.50", rendered.PlainText);

            // The caps are built on the PRE-TAX fee: 50% of $400.00, and a liability cap of
            // ten times it. Half of the tax-inclusive total would be $217.75.
            Assert.Contains("$200.00", rendered.PlainText);
            Assert.Contains("$4,000.00", rendered.PlainText);
            Assert.DoesNotContain("$217.75", rendered.PlainText);
        }

        [Fact]
        public void ChangedTermsAreSpelledOutInTheBodyNotJustExhibitB()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Contains("runs for six (6) months from the Service Commencement Date", rendered.PlainText);
            Assert.Contains("two (2) calendar months later", rendered.PlainText);
            Assert.Contains("forty-five (45) calendar days' written notice", rendered.PlainText);
            // Advanced terms reach their own sections, not only the collapsed form panel.
            Assert.Contains("within twenty (20) calendar days after receiving written notice", rendered.PlainText);
            Assert.Contains("continue for three (3) years after termination", rendered.PlainText);
        }

        /// <summary>
        /// Section 18 is PERSONNEL COORDINATION and expressly imposes no hiring restriction. A
        /// non-solicit clause reintroduced anywhere would contradict the section around it.
        /// </summary>
        [Fact]
        public void TheAgreementImposesNoNonSolicitRestriction()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Contains("imposes no restriction or fee on lawful solicitation", rendered.PlainText);
            Assert.DoesNotContain("liquidated damages", rendered.PlainText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("non-solicit", rendered.PlainText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FrequencyOtherThanWeeklyRendersGrammatically()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            Assert.Contains("two (2) scheduled cleaning visits per calendar week", rendered.PlainText);
            // Two service days, so the sentence around them has to agree in number.
            Assert.Contains("The regular service days are Tuesday and Thursday", rendered.PlainText);
        }

        /// <summary>
        /// Section 5(c) states an arrival WINDOW, because Section 14 only permits a failed-access
        /// charge when the crew arrived inside it. A single start time cannot answer that question.
        /// </summary>
        [Fact]
        public void TheArrivalWindowIsStatedWithBothEndsAndItsTimeZone()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            Assert.Contains("arrival window is 7:00 PM to 8:00 PM, local New York time", rendered.PlainText);
        }

        /// <summary>
        /// A contract with one arrival TIME rather than a window must not print "7:00 PM to".
        /// This is also the shape of a snapshot written before windows existed.
        /// </summary>
        [Fact]
        public void AMissingWindowEndCollapsesToASingleArrivalTime()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Schedule.ArrivalWindowEnd = "";

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("arrival window is 7:00 PM, local New York time", rendered.PlainText);
            Assert.DoesNotContain("7:00 PM to,", rendered.PlainText);
        }

        /// <summary>
        /// Section 15(f) counts missed visits and then refers back to "the second such visit" and
        /// "a third such visit". Both ordinals follow the configured threshold, or an admin who
        /// raises it leaves the clause warning and terminating on the numbers it used to have.
        /// </summary>
        [Fact]
        public void TheMissedVisitOrdinalsFollowTheConfiguredThreshold()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Advanced.MissedVisitThreshold = 4;

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("Four Client-attributable missed visits", rendered.PlainText);
            Assert.Contains("After the third such visit", rendered.PlainText);
            Assert.Contains("a fourth such visit occurs", rendered.PlainText);
        }

        /// <summary>
        /// Section 11(f) quotes the late charge monthly and annually in one sentence. The annual
        /// figure is derived, so the two can never disagree inside the same clause.
        /// </summary>
        [Fact]
        public void TheLateChargeIsQuotedMonthlyAndAnnuallyFromOneRate()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            Assert.Contains(
                "one percent (1%) per month, calculated daily at twelve percent (12%) per year",
                rendered.PlainText);
        }

        [Fact]
        public void ThePremisesNounFollowsTheScopeTemplate()
        {
            // Section 1(b) and 5(f) say "the office is closed" for an office contract - leaving
            // "restaurant" in a template served to an office client is exactly the kind of leak
            // this system exists to prevent.
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            Assert.Contains("Northline office", rendered.PlainText);
            Assert.Contains("while the office is closed", rendered.PlainText);
            Assert.DoesNotContain("restaurant", rendered.PlainText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheServiceAddressIsNeverTheClientAddress()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            // Both appear, and they are different — the reason ServiceLocation is its own entity.
            Assert.Contains("12 Water Street", rendered.PlainText);
            Assert.Contains("455 Madison Ave, Floor 3", rendered.PlainText);
        }

        // ── initials ───────────────────────────────────────────────────────────

        [Fact]
        public void TheDocumentContainsNoInitialsLineAnywhere()
        {
            // Signing happens exactly once, in the signature block. An initials box is a second,
            // unrecorded act of signing that the certificate would know nothing about.
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            Assert.DoesNotContain("Initials", rendered.PlainText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Initial:", rendered.PlainText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheSignatureBlockIsAnAnchorTheHostDraws()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            Assert.Contains(rendered.Blocks, b => b.Kind == ContractBlockKind.SignatureBlock);
            Assert.Contains("dc-doc-signature-anchor", rendered.Html);
        }

        /// <summary>
        /// THE SIGNATURE BLOCK IS THE LAST THING IN THE DOCUMENT, after both exhibits.
        ///
        /// That is where the drafted agreement puts it, and it is where a signer expects it: the
        /// block says the Parties agree to "Sections 1 through 35, Exhibit A and Exhibit B", so
        /// signing above two exhibits somebody has not scrolled to yet is the wrong order to ask
        /// in. It also keeps the executed PDF reading like the Word original.
        /// </summary>
        [Fact]
        public void TheSignatureBlockIsTheLastBlockInTheDocument()
        {
            var blocks = ContractRenderer.Render(OtherClientSnapshot()).Blocks;

            var signature = blocks.FindIndex(b => b.Kind == ContractBlockKind.SignatureBlock);
            var exhibitA = blocks.FindIndex(
                b => b.Kind == ContractBlockKind.Heading && b.Text == "EXHIBIT A");
            var exhibitB = blocks.FindIndex(
                b => b.Kind == ContractBlockKind.Heading && b.Text == "EXHIBIT B");

            Assert.True(exhibitA >= 0 && exhibitB > exhibitA, "Both exhibits should render, A then B.");
            Assert.True(signature > exhibitB,
                "The signature block must come after Exhibit B, not between Section 35 and Exhibit A.");
            Assert.Equal(blocks.Count - 1, signature);
        }

        /// <summary>
        /// HAND SOAP IS OUT OF THE AGREEMENT ENTIRELY (owner's rule).
        ///
        /// Contractor does not supply, replenish, repair or replace soap or its dispensers, ever.
        /// The drafted agreement had it in five places - Section 6(c), the Exhibit A restroom
        /// task row, two recorded site details, A5, A8(a) and an Exhibit B row - and a promise
        /// left in any one of them is a promise in an executed contract. A8(a) states the
        /// exclusion outright rather than staying silent, so a client reads it before signing
        /// instead of discovering it at an empty dispenser.
        /// </summary>
        [Fact]
        public void TheAgreementPromisesNothingAboutHandSoap()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Scope = ScopeStructureFor("Restaurant");
            var rendered = ContractRenderer.Render(snapshot);

            // No obligation to put soap anywhere, in any of the places the drafted agreement had one.
            Assert.DoesNotContain("refill", rendered.PlainText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("replenish", rendered.PlainText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Contractor supplies hand soap", rendered.PlainText);
            Assert.DoesNotContain("compatible hand soap", rendered.PlainText);
            Assert.DoesNotContain("SOAP DISPENSER LOCATIONS", rendered.PlainText);
            Assert.DoesNotContain("COMPATIBLE SOAP PRODUCT", rendered.PlainText);

            // The ONLY surviving mention is the express exclusion, which is deliberate: silence
            // would leave a client discovering it at an empty dispenser.
            Assert.Contains("Hand soap and its dispensers are not included", rendered.PlainText);

            var mentions = Regex.Matches(rendered.PlainText, "soap", RegexOptions.IgnoreCase).Count;
            Assert.True(mentions == 1, $"Expected exactly one mention of soap, found {mentions}.");
        }

        // ── structure ──────────────────────────────────────────────────────────

        [Fact]
        public void BothExhibitsAreRenderedInFull()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            Assert.Contains("EXHIBIT A", rendered.PlainText);
            Assert.Contains("SCOPE OF WORK", rendered.PlainText);
            Assert.Contains("EXHIBIT B", rendered.PlainText);
            Assert.Contains("PRICING, BILLING AND CONTACT DETAILS", rendered.PlainText);
            // Exhibit B is a two-column schedule, so it must produce table rows, not paragraphs.
            Assert.Contains(rendered.Blocks, b => b.Kind == ContractBlockKind.ExhibitRow);

            // All four of Exhibit B's sub-sections, including the two the drafted agreement added.
            Assert.Contains("B1. SERVICE DATES AND SCHEDULE", rendered.PlainText);
            Assert.Contains("B2. PRICING AND PAYMENT", rendered.PlainText);
            Assert.Contains("B3. INSURANCE", rendered.PlainText);
            Assert.Contains("B4. AUTHORIZED REPRESENTATIVES AND CONTACTS", rendered.PlainText);
        }

        /// <summary>
        /// Exhibit A's "Area | Tasks and limits" grid expands into real exhibit ROWS.
        ///
        /// It is a scope group like any other, so a business type owns its own rows and an office
        /// contract never quotes a restaurant's kitchen limits — but the token has to expand to
        /// several lines rather than substitute in place, which is what this pins.
        /// </summary>
        [Fact]
        public void TheAreaTaskTableExpandsIntoExhibitRows()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            var row = rendered.Blocks.Single(
                b => b.Kind == ContractBlockKind.ExhibitRow && b.Label == "Restrooms");
            Assert.Contains("remove ordinary trash under A5", row.Text);

            // And the header row above it survived as a row, not as prose.
            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow && b.Label == "Area");
            Assert.DoesNotContain("{{SCOPE_TABLE", rendered.PlainText);
        }

        /// <summary>
        /// An unticked AREA is not written into Exhibit A at all — neither its name nor the tasks
        /// paragraph that would otherwise describe work nobody agreed to.
        /// </summary>
        [Fact]
        public void AnUntickedAreaTakesItsTasksParagraphWithIt()
        {
            var snapshot = OtherClientSnapshot();
            var table = snapshot.Scope.Groups.First(g => g.Key == "area-tasks");
            table.Items.First(i => i.Label == "Interior glass").Selected = false;

            var rendered = ContractRenderer.Render(snapshot);

            Assert.DoesNotContain(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow && b.Label == "Interior glass");
            // The tasks paragraph goes with it. Asserted on wording unique to the table row —
            // A7's own prose legitimately repeats the "reachable from the floor" limit.
            Assert.DoesNotContain("Clean identified interior windows and glass", rendered.PlainText);
        }

        [Fact]
        public void AllThirtyFiveSectionsSurviveTheRender()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            for (var section = 1; section <= 35; section++)
            {
                Assert.Contains(rendered.Blocks,
                    b => b.Kind == ContractBlockKind.Heading &&
                         b.Text.StartsWith($"{section}. ", StringComparison.Ordinal));
            }
        }

        // ── scope ──────────────────────────────────────────────────────────────

        [Fact]
        public void TheRestaurantScopeTemplateReproducesExhibitAOfTheReference()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.PremisesType = "restaurant";
            snapshot.Scope = ScopeStructureFor("Restaurant");
            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("the dining area", rendered.PlainText);
            Assert.Contains("the customer restrooms", rendered.PlainText);
            Assert.Contains("the break room", rendered.PlainText);
            Assert.Contains("internal equipment cleaning", rendered.PlainText);
            Assert.Contains("sweeping", rendered.PlainText);

            // And the restaurant's own area/task grid, which an office contract never quotes.
            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow && b.Label == "Kitchen");
        }

        [Fact]
        public void AnUntickedScopeItemIsNotWrittenIntoTheDocument()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.PremisesType = "restaurant";
            snapshot.Scope = ScopeStructureFor("Restaurant");
            var included = snapshot.Scope.Groups.First(g => g.Key == "included-areas");
            included.Items.First(i => i.Label == "the dining area").Selected = false;

            var rendered = ContractRenderer.Render(snapshot);
            Assert.DoesNotContain("the dining area", rendered.PlainText);
            // The rest of the group is untouched.
            Assert.Contains("the customer restrooms", rendered.PlainText);
        }

        [Fact]
        public void AnEmptiedScopeGroupStillLeavesAGrammaticalSentence()
        {
            // "The following are excluded: ." is worse than saying none. An admin who clears a
            // whole group must not produce a broken sentence in a signed document.
            var snapshot = OtherClientSnapshot();
            snapshot.Scope = ScopeStructureFor("Restaurant");
            foreach (var item in snapshot.Scope.Groups.First(g => g.Key == "excluded-areas").Items)
                item.Selected = false;

            var rendered = ContractRenderer.Render(snapshot);
            Assert.Contains("The following are excluded: none.", rendered.PlainText);
        }

        [Fact]
        public void AScopeGroupTheBodyDoesNotInlineIsAppendedRatherThanDropped()
        {
            // The Custom template carries an "additional-tasks" group the seeded Exhibit A has no
            // sentence for. Dropping it would silently lose scope an admin had typed in.
            var snapshot = OtherClientSnapshot();
            snapshot.Scope = ScopeStructureFor("Custom");
            var extra = snapshot.Scope.Groups.First(g => g.Key == "additional-tasks");
            extra.Items.Add(new ScopeItem { Label = "Whiteboard cleaning", Selected = true, IsCustom = true });

            var rendered = ContractRenderer.Render(snapshot);
            Assert.Contains("A10. ADDITIONAL SCOPE", rendered.PlainText);
            Assert.Contains("Whiteboard cleaning", rendered.PlainText);
        }

        // ── hashing ────────────────────────────────────────────────────────────

        [Fact]
        public void RenderingIsDeterministic_SoAStoredHashStaysMeaningful()
        {
            // The hash is what a signer attests to. If rendering the same snapshot twice produced
            // different bytes, no signature could ever be checked against its document.
            var first = ContractRenderer.Render(OtherClientSnapshot());
            var second = ContractRenderer.Render(OtherClientSnapshot());
            Assert.Equal(first.Sha256, second.Sha256);
            Assert.Equal(64, first.Sha256.Length);
        }

        [Fact]
        public void ChangingAnyTermChangesTheHash()
        {
            var baseline = ContractRenderer.Render(OtherClientSnapshot()).Sha256;

            var changed = OtherClientSnapshot();
            changed.Pricing.PriceInput = 401m;
            ContractPricingCalculator.Recalculate(changed.Pricing);

            Assert.NotEqual(baseline, ContractRenderer.Render(changed).Sha256);
        }

        // ── snapshot isolation ─────────────────────────────────────────────────

        [Fact]
        public void ASnapshotRoundTripsThroughJsonUnchanged()
        {
            // Versions are stored as JSON and rendered back years later. A field lost in the
            // round trip is a clause that quietly changes wording on re-render.
            var original = OtherClientSnapshot();
            var restored = ContractSnapshot.Parse(original.ToJson());

            Assert.Equal(ContractRenderer.Render(original).Sha256,
                         ContractRenderer.Render(restored).Sha256);
        }

        [Fact]
        public void CloningAScopeStructureDoesNotShareStateWithTheTemplate()
        {
            // Toggling an item on one contract must never mutate the shared scope template.
            var template = ContractScopeTemplateSeed.All().First(t => t.Name == "Restaurant").Structure;
            var copy = template.Clone();
            copy.Groups[0].Items[0].Selected = false;

            Assert.True(template.Groups[0].Items[0].Selected);
        }
    }
}
