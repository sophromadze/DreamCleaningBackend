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

        /// <summary>
        /// Just the EXHIBIT A portion of the rendered text, so a scope assertion cannot be tripped
        /// up by the agreement's own unrelated vocabulary.
        ///
        /// "office" is the case in point: the preamble puts the CONTRACTOR'S "principal office at"
        /// its address, and Section 22's indemnity covers a Party's "officers, directors". Neither
        /// is a room anybody cleans, and both would break a whole-document search for a word that
        /// must not appear as an included AREA.
        /// </summary>
        private static string ExhibitA(RenderedContract rendered)
        {
            var text = rendered.PlainText;
            var start = text.IndexOf("EXHIBIT A", StringComparison.Ordinal);
            Assert.True(start >= 0, "The rendered contract has no EXHIBIT A heading.");

            var end = text.IndexOf("EXHIBIT B", start, StringComparison.Ordinal);
            Assert.True(end > start, "The rendered contract has no EXHIBIT B heading after A.");

            return text.Substring(start, end - start);
        }

        /// <summary>
        /// Just the INTRODUCTORY paragraph — everything before Section 1 — so a preamble assertion
        /// cannot be satisfied by wording that lives somewhere else in the agreement.
        ///
        /// The premises address is deliberately printed three times (preamble, Section 1(b),
        /// Exhibit A), and "principal office" appears in the preamble for Contractor and nowhere
        /// else, so a whole-document search proves nothing about either.
        /// </summary>
        private static string Preamble(string plainText)
        {
            var end = plainText.IndexOf("1. SERVICES AND PREMISES", StringComparison.Ordinal);
            return end > 0 ? plainText.Substring(0, end) : plainText;
        }

        /// <summary>
        /// The reference agreement's own parties: the values the Chick tastic contract carries, so
        /// the specified opening sentence can be asserted word for word.
        ///
        /// Built by overlaying <see cref="OtherClientSnapshot"/> rather than by copying it, because
        /// everything outside the preamble is irrelevant here and a second full fixture is a second
        /// thing to keep in step.
        /// </summary>
        private static ContractSnapshot ReferenceClientSnapshot()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.EffectiveDate = new DateTime(2026, 9, 20);
            snapshot.PremisesType = "restaurant";
            snapshot.Client.LegalEntityName = "Chick tastic LLC";
            snapshot.Client.EntityType = "a limited liability company";
            snapshot.Client.FormationState = "New York";
            snapshot.ServiceLocation.BusinessBrand = "Chick-fil-A";
            snapshot.ServiceLocation.LocationName = "Flatbush";
            snapshot.ServiceLocation.Address = "1569 Flatbush Ave.";
            snapshot.ServiceLocation.City = "Brooklyn";
            snapshot.ServiceLocation.State = "NY";
            snapshot.ServiceLocation.Zip = "11210";
            return snapshot;
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

        // ── the two fully optional site details (2026-09-15) ───────────────────

        /// <summary>
        /// THE THIRD EMPTY-VALUE RULE, and the one that says "this field is genuinely optional".
        ///
        /// Floor materials and the food-service permit holder are neither an open question (a
        /// ruled blank, which the preview banner then chases an admin about) nor a term of the
        /// deal ("None", which asserts something nobody said). Nothing in the agreement depends on
        /// either one, so an unanswered one takes its whole Exhibit A line with it and the
        /// document is complete without it.
        /// </summary>
        [Fact]
        public void AnUnansweredOptionalSiteDetailOmitsItsLineEntirely()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.SiteDetails.FloorMaterials = null;
            snapshot.SiteDetails.FoodServicePermitHolder = "   ";

            var rendered = ContractRenderer.Render(snapshot);

            Assert.DoesNotContain("FLOOR AND SURFACE MATERIALS", rendered.PlainText);
            Assert.DoesNotContain("FOOD-SERVICE PERMIT HOLDER", rendered.PlainText);

            // No blank underline, no "None", and nothing for the preview banner to chase.
            Assert.DoesNotContain(ContractPlaceholders.RuledBlank, rendered.PlainText);
            Assert.DoesNotContain("FLOOR_MATERIALS", rendered.UnresolvedTokens);
            Assert.DoesNotContain("FOOD_PERMIT_HOLDER", rendered.UnresolvedTokens);

            // And the sentinel itself never reaches a client's screen.
            Assert.DoesNotContain(ContractPlaceholders.OmitLineSentinel, rendered.PlainText);

            // The site-detail lines around them are untouched — only the empty ones go.
            Assert.Contains("APPROXIMATE SERVICED SQUARE FOOTAGE: 3,200 square feet",
                rendered.PlainText);
            Assert.Contains("WASTE, RECYCLING AND SEPARATELY COLLECTED ORGANICS RECEPTACLE "
                            + "LOCATIONS: loading dock, bay 2", rendered.PlainText);
        }

        /// <summary>A completed optional detail still prints, because it is useful site information.</summary>
        [Fact]
        public void ACompletedOptionalSiteDetailStillPrints()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Contains("FLOOR AND SURFACE MATERIALS: carpet tile throughout, sealed concrete "
                            + "at the entrance.", rendered.PlainText);
            Assert.Contains("FOOD-SERVICE PERMIT HOLDER: Not applicable.", rendered.PlainText);
        }

        /// <summary>
        /// A4 MUST NOT DEPEND ON THE FLOOR MATERIALS BEING WRITTEN DOWN.
        ///
        /// The clause used to read "using products and methods compatible with the identified
        /// floor materials", which is an obligation with no content when nothing was identified -
        /// the exhibit contradicted itself the moment the field was left blank. The general
        /// standard applies either way.
        /// </summary>
        [Fact]
        public void FloorCleaningIsObligedWhetherOrNotTheMaterialsWereRecorded()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.SiteDetails.FloorMaterials = null;

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains(
                "using commercially reasonable products and methods appropriate to the surfaces "
                + "actually encountered and following available manufacturer instructions where "
                + "applicable", rendered.PlainText);
            Assert.DoesNotContain("compatible with the identified floor materials", rendered.PlainText);
        }

        /// <summary>
        /// Client is no longer required to name the permit holder before work begins — but it
        /// keeps every underlying responsibility, which is the half that must NOT have been
        /// dropped along with the requirement.
        /// </summary>
        [Fact]
        public void ThePermitHolderIsNotAPreconditionButTheClientKeepsItsPermitObligations()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.SiteDetails.FoodServicePermitHolder = null;

            var rendered = ContractRenderer.Render(snapshot);

            Assert.DoesNotContain("identify the food-service permit holder", rendered.PlainText);

            // Section 26(b), untouched: permits, sanitation, food handling and pest control stay
            // with Client whether or not a name was recorded in Exhibit A.
            Assert.Contains(
                "Client is responsible for its food-service permits, daily and between-visit "
                + "sanitation, food handling, pest-control program", rendered.PlainText);
            Assert.Contains("whether or not a permit holder is identified in Exhibit A",
                rendered.PlainText);
        }

        // ── the ten-month term (2026-09-15) ────────────────────────────────────

        /// <summary>
        /// A CONTRACT DRAFTED ON THE DEFAULTS SAYS TEN MONTHS EVERYWHERE IT SAYS ANYTHING.
        ///
        /// Section 3(a), Section 3(b) and Exhibit B1 all quote the term, and both end dates are
        /// derived from the commencement date rather than typed — so one render is enough to
        /// prove the five references agree. Rendering through the SEEDED body rather than
        /// asserting on the snapshot is the point: a default nobody wired into the document would
        /// pass a property test and fail here.
        /// </summary>
        [Fact]
        public void TheDefaultTermIsTenMonthsInEveryPlaceTheDocumentStatesIt()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Term = new TermSnapshot
            {
                ServiceCommencementDate = new DateTime(2026, 4, 1)
            };

            var rendered = ContractRenderer.Render(snapshot);

            // 3(a) and Exhibit B1's Initial Term End Date row.
            Assert.Contains("The Initial Term runs for ten (10) months from the Service "
                            + "Commencement Date", rendered.PlainText);
            Assert.Contains("through the day immediately preceding its ten (10)-month anniversary",
                rendered.PlainText);
            Assert.Contains("January 31, 2027", rendered.PlainText);

            // 3(b) and Exhibit B1's Minimum Commitment End Date row.
            Assert.Contains("ten (10) calendar months later", rendered.PlainText);
            Assert.Contains("being ten (10) calendar months after the Service Commencement Date",
                rendered.PlainText);
            Assert.Contains("February 1, 2027", rendered.PlainText);

            // The sixty-day notice is unchanged, and so is the rule that it may be GIVEN during
            // the commitment period while taking effect no earlier than its end.
            Assert.Contains("sixty (60) calendar days' written notice", rendered.PlainText);
            Assert.Contains("Notice may be delivered during the Minimum Commitment Period, but "
                            + "termination for convenience shall not take effect before the "
                            + "Minimum Commitment End Date", rendered.PlainText);

            // And it continues month-to-month afterwards rather than renewing for a second term.
            Assert.Contains("continues automatically on a month-to-month basis on the same terms "
                            + "and does not renew for a further fixed term", rendered.PlainText);
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

        /// <summary>
        /// THE ONLY ADDRESS THE AGREEMENT PRINTS FOR CLIENT IS WHERE THE WORK HAPPENS.
        ///
        /// Since 2026-09-15 the Client's own mailing address is out of the document entirely —
        /// see <see cref="TheClientIsIdentifiedByEntityRatherThanByAMailingAddress"/> — so the
        /// service location is the only Client-side address left, and it must still be the
        /// SERVICE one. That is the reason ServiceLocation is its own entity: a client registered
        /// in Jersey City can have a crew turning up in Midtown.
        /// </summary>
        [Fact]
        public void TheServiceAddressIsNeverTheClientAddress()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Contains("455 Madison Ave, Floor 3", rendered.PlainText);
            Assert.DoesNotContain("12 Water Street", rendered.PlainText);
        }

        // ── the Client's mailing address is out of the document (2026-09-15) ───

        /// <summary>
        /// THE PREAMBLE IDENTIFIES CLIENT BY LEGAL ENTITY, NOT BY WHERE IT GETS ITS POST.
        ///
        /// The drafted v2.1 preamble read "…, with its business mailing address at …", which made
        /// an address a precondition of drafting: a client that had not given one printed a ruled
        /// blank on the first line a counterparty reads. Entity name, entity type and formation
        /// state identify the counterparty perfectly well, and Section 32 serves notice by email.
        ///
        /// v2.5 added the SERVICE location to the same sentence — see
        /// <see cref="ThePreambleNamesWhereTheServicesWillBePerformed"/>. It is not the mailing
        /// address returning under another name: it is required to save a contract at all, and it
        /// is worded as the place the work happens rather than as an address of Client's.
        /// </summary>
        [Fact]
        public void TheClientIsIdentifiedByEntityRatherThanByAMailingAddress()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Contains(
                "Northline Holdings Inc., a Delaware corporation, with Services to be performed "
                + "at 455 Madison Ave, Floor 3, New York, NY 10022 (\"Client\")",
                rendered.PlainText);
            Assert.DoesNotContain("business mailing address", rendered.PlainText);
        }

        /// <summary>
        /// Exhibit B4 has no Client mailing-address row, and Section 32 does not imply Client must
        /// have one. The CONTRACTOR's row is deliberately untouched — we do have an address, and
        /// a client is entitled to somewhere to courier a termination notice to.
        /// </summary>
        [Fact]
        public void ExhibitB4HasNoClientMailingAddressRowButKeepsTheContractors()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.DoesNotContain(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Client notice mailing address");
            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Contractor notice mailing address");

            // Formal notice works off the notice EMAIL; a mailing address is an option a Party may
            // designate, never a requirement.
            Assert.Contains("shall be sent to the receiving Party's designated notice email",
                rendered.PlainText);
            Assert.Contains("A Party is not required to designate a mailing address for notices",
                rendered.PlainText);
            // And the client still has a notice email row to be served at.
            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow && b.Label == "Client notice email");
        }

        // ── the preamble names the service location (2026-09-16, v2.5) ────────

        /// <summary>
        /// THE FIRST PARAGRAPH SAYS WHICH BUILDING THE AGREEMENT IS ABOUT.
        ///
        /// Until v2.5 the only address on the first page was the CONTRACTOR's principal office:
        /// Client was identified by entity alone, so a reader had to reach Section 1(b) before the
        /// document said where anybody was going to be cleaning.
        ///
        /// The wording is the load-bearing part. "with Services to be performed at" states what
        /// the address IS — the place the work happens. Calling it Client's principal office, its
        /// registered office, its legal address or its business mailing address would assert
        /// something the service location does not establish, and would be wrong for the ordinary
        /// commercial client: registered in one state, reading its post at an accountant's, and
        /// operating the premises somewhere else entirely.
        /// </summary>
        [Fact]
        public void ThePreambleNamesWhereTheServicesWillBePerformed()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            var preamble = Preamble(rendered.PlainText);

            Assert.Contains(
                "Northline Holdings Inc., a Delaware corporation, with Services to be performed "
                + "at 455 Madison Ave, Floor 3, New York, NY 10022 (\"Client\")",
                preamble);

            // The address is never given a label that would make it an address OF Client.
            foreach (var mislabel in new[]
                     {
                         "Client's principal office",
                         "its principal office at 455 Madison",
                         "registered office",
                         "legal address",
                         "business mailing address"
                     })
            {
                Assert.DoesNotContain(mislabel, preamble);
            }

            // The Contractor's own principal office is untouched — it IS one.
            Assert.Contains(
                "with its principal office at 8800 20th Ave, Apt 2B, Brooklyn, NY 11214 "
                + "(\"Contractor\")",
                preamble);
        }

        /// <summary>
        /// THE REFERENCE AGREEMENT'S EXACT OPENING SENTENCE.
        ///
        /// Word for word what the Chick tastic contract must read, because this change was
        /// specified as a sentence rather than as a behaviour. Every value in it comes from the
        /// snapshot — see <see cref="ThePreambleAddressFollowsTheClientBeingDraftedFor"/> for the
        /// proof that none of it is baked into the template.
        /// </summary>
        [Fact]
        public void ThePreambleReadsExactlyAsSpecifiedForTheReferenceClient()
        {
            var rendered = ContractRenderer.Render(ReferenceClientSnapshot());

            Assert.Contains(
                "This Master Service Agreement (the \"Agreement\") is entered into as of "
                + "September 20, 2026 (the \"Effective Date\") by and between Nodar Alania "
                + "Inc., a New York corporation doing business as Dream Cleaning NYC, with its "
                + "principal office at 8800 20th Ave, Apt 2B, Brooklyn, NY 11214 "
                + "(\"Contractor\"), and Chick tastic LLC, a limited liability company "
                + "organized under the laws of New York, with Services to be performed at "
                + "1569 Flatbush Ave., Brooklyn, NY 11210 (\"Client\"). Contractor and Client "
                + "are each a \"Party\" and together the \"Parties.\"",
                Preamble(rendered.PlainText));
        }

        /// <summary>
        /// THE ADDRESS IS THE SNAPSHOT'S, FOR WHOEVER IS BEING DRAFTED FOR.
        ///
        /// The leak guard this file exists for, applied to the one line the change touches: a
        /// hardcoded premises address stays correct for the single client it came from and is
        /// silently wrong for every client after. Unrelated clients, different sentences, and
        /// neither one's address anywhere in the other's document.
        /// </summary>
        [Fact]
        public void ThePreambleAddressFollowsTheClientBeingDraftedFor()
        {
            var other = ContractRenderer.Render(OtherClientSnapshot()).PlainText;
            var reference = ContractRenderer.Render(ReferenceClientSnapshot()).PlainText;

            Assert.Contains(
                "with Services to be performed at 455 Madison Ave, Floor 3, New York, NY 10022",
                Preamble(other));
            Assert.DoesNotContain("1569 Flatbush", other);

            Assert.Contains(
                "with Services to be performed at 1569 Flatbush Ave., Brooklyn, NY 11210",
                Preamble(reference));
            Assert.DoesNotContain("455 Madison", reference);

            // A third one, sharing nothing with either, to rule out the two fixtures happening to
            // agree with a constant.
            var third = OtherClientSnapshot();
            third.ServiceLocation.Address = "88 Harbor Way, Suite 400";
            third.ServiceLocation.City = "Stamford";
            third.ServiceLocation.State = "CT";
            third.ServiceLocation.Zip = "06902";

            Assert.Contains(
                "with Services to be performed at 88 Harbor Way, Suite 400, Stamford, CT 06902",
                Preamble(ContractRenderer.Render(third).PlainText));
        }

        /// <summary>
        /// SECTION 1(b) AND EXHIBIT A ARE UNCHANGED, AND THE REPETITION IS THE POINT.
        ///
        /// The preamble identifies the deal, Section 1(b) defines the Premises and Exhibit A
        /// records them for the crew. All three read the ONE {{SERVICE_FULL_ADDRESS}} token, so
        /// they cannot disagree — which is also why "the address is already in Section 1(b)" is
        /// the wrong instinct to act on. Both existing sentences still say outright that the
        /// premises address is not necessarily Client's legal or principal address.
        /// </summary>
        [Fact]
        public void SectionOneBAndExhibitAKeepTheirOwnPremisesWording()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            var text = rendered.PlainText;

            Assert.Contains(
                "(b) The Premises are the Northline office operated by Client and located at "
                + "455 Madison Ave, Floor 3, New York, NY 10022. The Premises address is the "
                + "service location only and is not necessarily Client's legal or principal "
                + "business address.",
                text);

            Assert.Contains(
                "SERVICE PREMISES: Northline office operated by Client, 455 Madison Ave, Floor 3, "
                + "New York, NY 10022. Service location only; not necessarily the legal or "
                + "principal business address of Client.",
                text);

            // Three renderings of the one address: the preamble, Section 1(b) and Exhibit A.
            Assert.Equal(3, Regex.Matches(
                text, Regex.Escape("455 Madison Ave, Floor 3, New York, NY 10022")).Count);
        }

        /// <summary>
        /// A CONTRACT FROZEN AGAINST v2.4 OPENS THE WAY IT ALWAYS DID.
        ///
        /// The preamble change is a template VERSION, not a migration. A version stored before it
        /// renders from its own frozen body forever, so an executed agreement keeps the exact
        /// sentence its counterparty signed — no service address in its opening paragraph, and
        /// nothing left unresolved.
        /// </summary>
        [Fact]
        public void AFrozenPreVTwoFiveBodyKeepsItsOwnPreamble()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.TemplateBodyText =
                "This Master Service Agreement is entered into by and between "
                + "{{CONTRACTOR_LEGAL_NAME}} (\"Contractor\"), and {{CLIENT_LEGAL_NAME}}, "
                + "{{CLIENT_ENTITY_DESCRIPTION}} (\"Client\").\n"
                + "@SIGNATURE_BLOCK\n";

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains(
                "Northline Holdings Inc., a Delaware corporation (\"Client\")",
                rendered.PlainText);
            Assert.DoesNotContain("with Services to be performed at", rendered.PlainText);
            Assert.DoesNotContain("455 Madison Ave", rendered.PlainText);
            Assert.DoesNotContain("{{", rendered.PlainText);
            Assert.Empty(rendered.UnresolvedTokens);
        }

        // ── the backup on-call contacts are optional (2026-09-16) ──────────────

        /// <summary>
        /// SECTION 16(c) ASKS CLIENT FOR ONE CONTACT, NOT TWO.
        ///
        /// It used to oblige Client to "designate primary and backup on-call contacts", which made
        /// a second person a term of the agreement - on an account that may well only have one.
        /// The primary stays mandatory because Section 14 hangs a failed-access charge on
        /// Contractor having tried to reach it; the backup is now expressly permissive.
        /// </summary>
        [Fact]
        public void SectionSixteenRequiresOnlyAPrimaryOnCallContact()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Contains(
                "Client shall designate a primary on-call contact in Exhibit B and may designate "
                + "a backup on-call contact if available", rendered.PlainText);
            Assert.DoesNotContain("designate primary and backup on-call contacts", rendered.PlainText);

            // The rest of 16(c) is untouched - the site-requirements duty and the Section 26(b)
            // carve-out both still sit in the same paragraph.
            Assert.Contains(
                "provide applicable landlord, franchisor and site requirements affecting access, "
                + "products, insurance or the Services before work begins", rendered.PlainText);
            Assert.Contains("whether or not a permit holder is identified in Exhibit A",
                rendered.PlainText);
        }

        /// <summary>
        /// A BLANK BACKUP CONTACT TAKES ITS EXHIBIT B4 ROW WITH IT - on either side.
        ///
        /// Neither a ruled blank (which says the question is still open, and puts the field in the
        /// preview's warning banner an admin is then expected to clear) nor "None" (a positive
        /// statement that no second person exists, which nobody made). The row simply is not there,
        /// exactly as an unrecorded floor material leaves no line in Exhibit A.
        /// </summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void ABlankBackupContactOmitsItsRowAndIsNeverChased(bool blankClient, bool blankContractor)
        {
            var snapshot = OtherClientSnapshot();
            if (blankClient) snapshot.Contacts.ClientBackupContact = null;
            // Whitespace is the same as empty - an admin who typed a space has not named anybody.
            if (blankContractor) snapshot.Contacts.ContractorBackupContact = "   ";

            var rendered = ContractRenderer.Render(snapshot);

            if (blankClient)
            {
                Assert.DoesNotContain(rendered.Blocks,
                    b => b.Kind == ContractBlockKind.ExhibitRow &&
                         b.Label == "Client backup on-call contact");
                Assert.DoesNotContain("CLIENT_BACKUP_CONTACT", rendered.UnresolvedTokens);
            }

            if (blankContractor)
            {
                Assert.DoesNotContain(rendered.Blocks,
                    b => b.Kind == ContractBlockKind.ExhibitRow &&
                         b.Label == "Contractor backup on-call contact");
                Assert.DoesNotContain("CONTRACTOR_BACKUP_CONTACT", rendered.UnresolvedTokens);
            }

            // No ruled blank anywhere, no leaked sentinel, and nothing left unresolved at all -
            // every other field of this fixture is filled, so the agreement is complete without
            // the backups and the preview banner has nothing to show.
            Assert.DoesNotContain(ContractPlaceholders.RuledBlank, rendered.PlainText);
            Assert.DoesNotContain(ContractPlaceholders.OmitLineSentinel, rendered.PlainText);
            Assert.DoesNotContain("{{", rendered.PlainText);
            Assert.Empty(rendered.UnresolvedTokens);

            // The PRIMARIES are untouched. They are what Section 14 depends on, and dropping a
            // backup must never quietly take its primary with it.
            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Client primary on-call contact");
            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Contractor supervisor and primary on-call contact");
            Assert.Contains("Dana Okafor", rendered.PlainText);

            // And the document is still a whole document - B4 keeps its heading, and the
            // signature block is still the last thing in it.
            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.SubHeading &&
                     b.Text == "B4. AUTHORIZED REPRESENTATIVES AND CONTACTS");
            Assert.Equal(ContractBlockKind.SignatureBlock, rendered.Blocks.Last().Kind);
        }

        /// <summary>
        /// A BACKUP THAT WAS RECORDED STILL PRINTS, on both sides - which is what keeps every
        /// existing contract and draft rendering exactly as it did before the field became
        /// optional.
        /// </summary>
        [Fact]
        public void ARecordedBackupContactStillPrintsItsRow()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Contractor backup on-call contact" &&
                     b.Text == "Operations desk, (929) 930-1526");
            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Client backup on-call contact" &&
                     b.Text == "Security desk, (212) 555-9000");
        }

        /// <summary>
        /// A CONTRACT EXECUTED AGAINST AN OLDER BODY MUST STILL RENDER THE WAY IT WAS SIGNED.
        ///
        /// {{CLIENT_NOTICE_MAILING_ADDRESS}} is retired from the seeded body but stays MAPPED, the
        /// same arrangement the retired $35 returned-payment fee has. Dropping the token would
        /// leave a frozen v2.0/v2.1 snapshot printing a literal "{{...}}" in the middle of an
        /// executed agreement years after anybody could fix it.
        /// </summary>
        [Fact]
        public void AFrozenOlderBodyStillResolvesTheRetiredMailingAddressToken()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.TemplateBodyText =
                "This Agreement is between the Parties, with its business mailing address at "
                + "{{CLIENT_NOTICE_MAILING_ADDRESS}} (\"Client\").";

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("12 Water Street, Jersey City, NJ 07302", rendered.PlainText);
            Assert.DoesNotContain("{{", rendered.PlainText);
            Assert.Empty(rendered.UnresolvedTokens);
        }

        /// <summary>
        /// A CONTRACT FROZEN AGAINST v2.2 STILL RENDERS THE OBLIGATION IT WAS SIGNED WITH.
        ///
        /// The 16(c) rewording is a template VERSION, not a migration: a version stored before it
        /// renders from its own frozen body forever, so it keeps requiring both contacts. Only its
        /// backup ROW follows the new blank behaviour, and that is right — the token is filled at
        /// render time and there was never a meaningful way to print an underline for a person the
        /// contract does not name.
        /// </summary>
        [Fact]
        public void AFrozenOlderBodyKeepsItsOwnOnCallWordingAndStillPrintsARecordedBackup()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.TemplateBodyText =
                "(c) Client shall designate primary and backup on-call contacts in Exhibit B.\n"
                + "|Client backup on-call contact|{{CLIENT_BACKUP_CONTACT}}\n"
                + "@SIGNATURE_BLOCK\n";

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("designate primary and backup on-call contacts", rendered.PlainText);
            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Client backup on-call contact" &&
                     b.Text == "Security desk, (212) 555-9000");
            Assert.DoesNotContain("{{", rendered.PlainText);
            Assert.Empty(rendered.UnresolvedTokens);

            // And an old version whose backup was never filled in loses only that row — the body
            // around it renders exactly as it always did.
            snapshot.Contacts.ClientBackupContact = null;
            var withoutBackup = ContractRenderer.Render(snapshot);

            Assert.Contains("designate primary and backup on-call contacts", withoutBackup.PlainText);
            Assert.DoesNotContain(withoutBackup.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Client backup on-call contact");
            Assert.DoesNotContain(ContractPlaceholders.RuledBlank, withoutBackup.PlainText);
            Assert.DoesNotContain(ContractPlaceholders.OmitLineSentinel, withoutBackup.PlainText);
            Assert.Empty(withoutBackup.UnresolvedTokens);
        }

        // ── the address line is written once (2026-09-15) ──────────────────────

        /// <summary>
        /// "1569 Flatbush Ave., Brooklyn, NY, Brooklyn, NY 11210" — the actual output before this
        /// was fixed, on the line that says where the work happens.
        ///
        /// A street box is typed by a person or pasted out of a listing, so it routinely already
        /// carries the city and state; appending the structured columns to it printed both twice.
        /// The duplicate must be gone from BOTH places the premises address is rendered — Section
        /// 1(b) and Exhibit A's SERVICE PREMISES line — because they read the one token.
        /// </summary>
        [Fact]
        public void ACityAndStateAlreadyTypedIntoTheStreetAreNotPrintedTwice()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.ServiceLocation.Address = "1569 Flatbush Ave., Brooklyn, NY";
            snapshot.ServiceLocation.City = "Brooklyn";
            snapshot.ServiceLocation.State = "NY";
            snapshot.ServiceLocation.Zip = "11210";

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("1569 Flatbush Ave., Brooklyn, NY 11210", rendered.PlainText);
            Assert.DoesNotContain("Brooklyn, NY, Brooklyn", rendered.PlainText);

            // Both surfaces, not just the one somebody happened to look at.
            Assert.Contains("located at 1569 Flatbush Ave., Brooklyn, NY 11210.", rendered.PlainText);
            Assert.Contains("SERVICE PREMISES: Northline office operated by Client, "
                            + "1569 Flatbush Ave., Brooklyn, NY 11210.", rendered.PlainText);
        }

        [Theory]
        // The street already carries the whole tail, in the spellings people actually type.
        [InlineData("1569 Flatbush Ave., Brooklyn, NY", "1569 Flatbush Ave., Brooklyn, NY 11210")]
        [InlineData("1569 Flatbush Ave., Brooklyn, NY 11210", "1569 Flatbush Ave., Brooklyn, NY 11210")]
        [InlineData("1569 Flatbush Ave., brooklyn, ny", "1569 Flatbush Ave., Brooklyn, NY 11210")]
        [InlineData("1569 Flatbush Ave., NY", "1569 Flatbush Ave., Brooklyn, NY 11210")]
        // An ordinary street with nothing repeated is untouched, INCLUDING a second component that
        // is not a city — an apartment or floor must never be mistaken for one.
        [InlineData("1569 Flatbush Ave.", "1569 Flatbush Ave., Brooklyn, NY 11210")]
        [InlineData("8800 20th Ave, Apt 2B", "8800 20th Ave, Apt 2B, Brooklyn, NY 11210")]
        // The city name INSIDE a street name is part of the street, not a repetition of the city.
        [InlineData("2 Brooklyn Bridge Blvd", "2 Brooklyn Bridge Blvd, Brooklyn, NY 11210")]
        public void TheAddressLineWritesEachPartExactlyOnce(string street, string expected)
        {
            var snapshot = OtherClientSnapshot();
            snapshot.ServiceLocation.Address = street;
            snapshot.ServiceLocation.City = "Brooklyn";
            snapshot.ServiceLocation.State = "NY";
            snapshot.ServiceLocation.Zip = "11210";

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains($"located at {expected}.", rendered.PlainText);
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

        // ── no fixed wording names a room that may not be included (2026-09-16) ─

        /// <summary>
        /// THE INCLUDED AREAS LIST IS THE ONLY THING THAT SAYS WHICH ROOMS ARE IN SCOPE.
        ///
        /// Two pieces of FIXED wording used to name the office regardless: A1's task label
        /// ("Hallways, office and doors") and A2's example list ("...such as the office, the
        /// employee restroom or hallways..."). Neither is a checklist, so both went on naming the
        /// office on a contract whose A1 list did not include one - which is a document that
        /// contradicts itself about what is being cleaned.
        ///
        /// This is the headline case: office unticked, and the word must not survive anywhere.
        /// </summary>
        [Fact]
        public void WithTheOfficeUntickedTheWordAppearsNowhereInTheScope()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.PremisesType = "premises";
            snapshot.ServiceLocation.LocationName = "Midtown site";

            var included = snapshot.Scope.Groups.First(g => g.Key == "included-areas");
            included.Items.First(i => i.Label == "offices").Selected = false;

            var rendered = ContractRenderer.Render(snapshot);

            // The A1 list itself no longer offers it...
            Assert.DoesNotContain("offices", rendered.PlainText);

            // ...and neither does any fixed sentence in EXHIBIT A, which is where both offenders
            // lived: A1's task label and A2's example list. Scoped to the exhibit rather than the
            // whole document ON PURPOSE - the preamble's "principal office at <address>" and
            // Section 22's "its officers, directors" both contain the substring and are nothing to
            // do with a room being cleaned. A blanket search would fail on legitimate text and
            // teach the next person to weaken the assertion.
            Assert.DoesNotContain("office", ExhibitA(rendered), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A1's task row names no individual room. It is a FIXED label, so any room named in it
        /// competes with the checklist above it - and substituting "meeting room" for "office"
        /// would only move the same bug to the next client.
        /// </summary>
        [Fact]
        public void TheA1TaskRowNamesNoIndividualRoom()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Hallways, included rooms and doors");

            foreach (var retired in ContractScopeTemplateSeed.RetiredAreaLabels)
                Assert.DoesNotContain(retired, rendered.PlainText);

            // The task description is deliberately unchanged - it never named a room.
            var row = rendered.Blocks.Single(
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Hallways, included rooms and doors");
            Assert.Equal(
                "Clean exposed floors, identified touchpoints and accessible cleared surfaces. "
                + "Do not handle files, electronics, cash or private materials.", row.Text);
        }

        /// <summary>The restaurant template carries the same generic label, not its own variant.</summary>
        [Fact]
        public void TheRestaurantTemplateUsesTheSameRoomFreeTaskRow()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.PremisesType = "restaurant";
            snapshot.Scope = ScopeStructureFor("Restaurant");

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Hallways, included rooms and doors");
            Assert.DoesNotContain("Hallways, office and doors", rendered.PlainText);
        }

        /// <summary>
        /// A2 STATES THE RULE AND NAMES NO ROOM. Asserted as the exact sentence, because the whole
        /// defect was an example list that could not keep up with a per-contract checklist.
        /// </summary>
        [Fact]
        public void A2StatesTheBackOfHouseRuleWithoutNamingAnyRoom()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Contains(
                "An area expressly identified as an Included Area in A1 remains included even if "
                + "it is physically located in a back-of-house portion of the Premises.",
                rendered.PlainText);

            Assert.DoesNotContain("such as the office", rendered.PlainText);
            Assert.DoesNotContain("the employee restroom or hallways", rendered.PlainText);

            // The surrounding clause is untouched - the rule it states is the point of the
            // paragraph and only the examples were removed.
            Assert.Contains(
                "Back-of-house areas are excluded except for Included Areas expressly identified in A1",
                rendered.PlainText);
            Assert.Contains("The kitchen is included only to the extent stated in A3",
                rendered.PlainText);
        }

        /// <summary>
        /// TICKED, IT STILL PRINTS. Removing the fixed references must not have removed the
        /// office from the checklist - it is a perfectly ordinary selectable area, and an office
        /// contract that never mentions the office would be the opposite bug.
        /// </summary>
        [Fact]
        public void WithTheOfficeTickedItStillAppearsAsAnIncludedArea()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Contains("offices", rendered.PlainText);

            // And on the restaurant template, whose own checklist words it in the singular.
            var restaurant = OtherClientSnapshot();
            restaurant.PremisesType = "restaurant";
            restaurant.Scope = ScopeStructureFor("Restaurant");
            Assert.Contains("the office", ContractRenderer.Render(restaurant).PlainText);
        }

        /// <summary>
        /// A ROOM THE SEED NEVER HEARD OF NEEDS NO TEMPLATE WORDING.
        ///
        /// The admin unticks the office and adds "the meeting room" by hand. That has to render as
        /// an ordinary Included Area, with no office reference reintroduced by any fixed sentence
        /// - which is the arrangement the whole change exists to guarantee.
        /// </summary>
        [Fact]
        public void ACustomRoomRendersWithNoTemplateChangeAndNoOfficeReference()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.PremisesType = "premises";
            snapshot.ServiceLocation.LocationName = "Midtown site";

            var included = snapshot.Scope.Groups.First(g => g.Key == "included-areas");
            included.Items.First(i => i.Label == "offices").Selected = false;
            included.Items.Add(new ScopeItem { Label = "the meeting room", Selected = true });
            included.Items.Add(new ScopeItem { Label = "the monitoring room", Selected = true });

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("the meeting room", rendered.PlainText);
            Assert.Contains("the monitoring room", rendered.PlainText);
            Assert.DoesNotContain("office", ExhibitA(rendered), StringComparison.OrdinalIgnoreCase);

            // The A1 task row is still there and still says nothing about which rooms they are.
            Assert.Contains(rendered.Blocks,
                b => b.Kind == ContractBlockKind.ExhibitRow &&
                     b.Label == "Hallways, included rooms and doors");
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
