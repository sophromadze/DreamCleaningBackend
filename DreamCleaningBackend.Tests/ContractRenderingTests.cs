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
                // A MIXED allocation, answered in full, so every A8 token has a value here and the
                // unanswered case can be tested separately.
                Supplies = new SuppliesSnapshot
                {
                    EquipmentProvidedBy = SupplyProvider.Contractor,
                    TrashLinersProvidedBy = SupplyProvider.Contractor,
                    PaperTowelsProvidedBy = SupplyProvider.Client,
                    ToiletTissueProvidedBy = SupplyProvider.Client,
                    OtherConsumables = new List<ConsumableAllocation>
                    {
                        new() { Item = "coffee filters", ProvidedBy = SupplyProvider.Client }
                    }
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
        /// AN UNANSWERED SITE DETAIL DISAPPEARS (template v2.7). No ruled blank, no "None", no
        /// label - the whole line leaves the agreement, and the preview banner has nothing to
        /// chase, because nothing in the agreement depends on the detail being recorded.
        /// </summary>
        [Fact]
        public void AnUnansweredSiteDetailIsOmittedEntirely()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.SiteDetails.EmployeeRestroomCounts = null;

            var rendered = ContractRenderer.Render(snapshot);

            Assert.DoesNotContain("EMPLOYEE RESTROOM AND FIXTURE COUNTS", rendered.PlainText);
            Assert.DoesNotContain("EMPLOYEE_RESTROOM_COUNTS_IF_SET", rendered.UnresolvedTokens);
            Assert.DoesNotContain(ContractPlaceholders.RuledBlank, rendered.PlainText);
        }

        /// <summary>
        /// A blank completion time, site requirements, food-contact task or initial-work Change
        /// Order used to print "None". None of them is load-bearing when blank - Section 5(c)
        /// means "no deadline" when none is stated, and A3(d) / Section 25(e) exclude food-contact
        /// sanitizing unless a task is named - so the v2.7 agreement drops the lines instead.
        /// </summary>
        [Fact]
        public void BlankOptionalTermsAreOmittedRatherThanPrintedAsNone()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Schedule.CompletionTime = null;
            snapshot.SiteDetails.SiteRequirements = "";
            snapshot.SiteDetails.FoodContactSanitizing = null;
            snapshot.SiteDetails.InitialWorkChangeOrder = " ";

            var rendered = ContractRenderer.Render(snapshot);

            Assert.DoesNotContain("Required completion time", rendered.PlainText);
            Assert.DoesNotContain("REQUIRED COMPLETION TIME", rendered.PlainText);
            Assert.DoesNotContain("SITE, LANDLORD OR BRAND REQUIREMENTS", rendered.PlainText);
            Assert.DoesNotContain("FOOD-CONTACT OR DINING-TABLE SANITIZING", rendered.PlainText);
            Assert.DoesNotContain("INITIAL-WORK CHANGE ORDER", rendered.PlainText);
            Assert.Empty(rendered.UnresolvedTokens);

            // Food-contact sanitizing is still excluded unless a task is named.
            Assert.Contains("Food-contact cleaning and sanitizing are excluded except a task "
                            + "expressly identified in A1 or in paragraph (a), including any dishwashing "
                            + "listed there.", rendered.PlainText);
        }

        /// <summary>
        /// HISTORICAL DOCUMENTS DO NOT MOVE. A body frozen before v2.7 uses the older tokens,
        /// which keep printing a ruled blank, "None" and "Not applicable" exactly as the document
        /// did when it was signed - the blank-means-omitted rule reaches only v2.7 bodies.
        /// </summary>
        [Fact]
        public void AFrozenOlderBodyStillPrintsItsBlanksAndNones()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.SiteDetails.EmployeeRestroomCounts = null;
            snapshot.Schedule.CompletionTime = null;
            snapshot.Insurance = new InsuranceEndorsementsSnapshot();
            snapshot.TemplateBodyText =
                "EMPLOYEE RESTROOM AND FIXTURE COUNTS: {{EMPLOYEE_RESTROOM_COUNTS}}.\n"
                + "REQUIRED COMPLETION TIME: {{COMPLETION_TIME}}.\n"
                + "|Additional endorsements agreed for this engagement|{{AGREED_ENDORSEMENTS}}\n"
                + "|Insurer, policy, endorsement form and edition, protected entity and applicable work|{{ENDORSEMENT_DETAILS}}\n";

            var text = ContractRenderer.Render(snapshot).PlainText;

            Assert.Contains("EMPLOYEE RESTROOM AND FIXTURE COUNTS: " + ContractPlaceholders.RuledBlank, text);
            Assert.Contains("REQUIRED COMPLETION TIME: None", text);
            Assert.Contains("Additional endorsements agreed for this engagement: None", text);
            Assert.Contains("applicable work: Not applicable", text);
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

        // ── the minimum commitment is contract-specific (template v2.7, 2026-09-30) ──

        /// <summary>
        /// A CONTRACT DRAFTED ON THE DEFAULTS HAS NO MINIMUM COMMITMENT, and says nothing that
        /// suggests one exists.
        ///
        /// There is no company-wide commitment; one exists only where a client agreed one. So the
        /// default render must contain no Minimum Commitment Period, no Minimum Commitment End
        /// Date and no Initial Term - only the plain statement that none applies - and the notice
        /// is thirty days everywhere it is quoted. Rendered through the SEEDED body rather than
        /// asserted on the snapshot, because a default nobody wired into the document would pass a
        /// property test and fail here.
        /// </summary>
        [Fact]
        public void TheDefaultContractHasNoMinimumCommitmentAnywhere()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Term = new TermSnapshot
            {
                ServiceCommencementDate = new DateTime(2026, 4, 1)
            };

            var rendered = ContractRenderer.Render(snapshot);
            var text = rendered.PlainText;

            Assert.DoesNotContain("Minimum Commitment Period", text);
            Assert.DoesNotContain("Minimum Commitment End Date", text);
            Assert.DoesNotContain("Initial Term", text);
            Assert.DoesNotContain("MINIMUM COMMITMENT PERIOD", text);

            // It says so, in Section 3, Section 36 and Exhibit B1.
            Assert.Contains("3. TERM AND RENEWAL", text);
            Assert.Contains("(b) No minimum service commitment applies.", text);
            Assert.Contains("(h) Minimum commitment. No minimum service commitment applies.", text);
            Assert.Contains("It begins on the Service Commencement Date stated in Exhibit B and "
                            + "continues on a month-to-month basis until terminated in accordance "
                            + "with Section 4.", text);
            Assert.Contains("Term and termination: Month-to-month from the Service Commencement Date. "
                            + "No minimum service commitment applies.", text);
            Assert.DoesNotContain("Initial Term End Date", text);
            Assert.DoesNotContain("zero (0)", text);

            // Thirty days, in 3(d), 4(a), 36(i) and Exhibit B1 alike.
            Assert.Contains("thirty (30) calendar days' written notice", text);
            Assert.DoesNotContain("sixty (60) calendar days' written notice", text);

            // Cause, nonpayment and safety rights are untouched.
            Assert.Contains("Either Party may terminate this Agreement at any time if the other "
                            + "Party materially breaches this Agreement", text);
            Assert.Contains("Contractor may terminate for nonpayment of an undisputed amount", text);

            // Nothing on a dropped line reaches the preview's warning banner.
            Assert.DoesNotContain("MINIMUM_COMMITMENT_END_DATE", rendered.UnresolvedTokens);
            Assert.DoesNotContain("INITIAL_TERM_END_DATE", rendered.UnresolvedTokens);
            Assert.DoesNotContain(ContractPlaceholders.GuardMinimumCommitment, rendered.UnresolvedTokens);
            Assert.DoesNotContain(ContractPlaceholders.GuardNoMinimumCommitment, rendered.UnresolvedTokens);
        }

        /// <summary>
        /// A CUSTOM COMMITMENT READS EXACTLY AS AGREED, in every place the document states it.
        /// Twelve months here - no preset, just the number the admin typed - with the Initial Term
        /// raised to match by <c>ContractService.NormalizeTerm</c>.
        /// </summary>
        [Theory]
        [InlineData(3, "three (3)", "July 1, 2026")]
        [InlineData(6, "six (6)", "October 1, 2026")]
        [InlineData(12, "twelve (12)", "April 1, 2027")]
        public void ACustomCommitmentIsStatedAsAgreed(int months, string words, string endDate)
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Term = ContractService.NormalizeTerm(new TermSnapshot
            {
                MinimumCommitmentMonths = months,
                ServiceCommencementDate = new DateTime(2026, 4, 1)
            });

            var text = ContractRenderer.Render(snapshot).PlainText;

            Assert.Contains("3. TERM, MINIMUM COMMITMENT PERIOD AND RENEWAL", text);
            Assert.Contains($"{words} calendar months later", text);
            Assert.Contains($"being {words} calendar months after the Service Commencement Date", text);
            Assert.Contains(endDate, text);
            Assert.Contains($"The Initial Term runs for {words} months from the Service "
                            + "Commencement Date", text);
            Assert.Contains("Notice may be delivered during the Minimum Commitment Period, but "
                            + "termination for convenience shall not take effect before the "
                            + "Minimum Commitment End Date", text);

            // Thirty days' notice still applies, and the no-commitment wording is gone.
            Assert.Contains("thirty (30) calendar days' written notice", text);
            Assert.DoesNotContain("No minimum commitment period applies", text);
            Assert.DoesNotContain("no fixed initial term", text);
        }

        /// <summary>
        /// The Initial Term can never end before the earliest date the client may leave, and
        /// without a commitment there is no fixed Initial Term at all.
        /// </summary>
        [Fact]
        public void NormalizeTermKeepsTheInitialTermConsistentWithTheCommitment()
        {
            var raised = ContractService.NormalizeTerm(new TermSnapshot
            {
                MinimumCommitmentMonths = 6, InitialTermMonths = 2
            });
            Assert.Equal(6, raised.InitialTermMonths);

            var longer = ContractService.NormalizeTerm(new TermSnapshot
            {
                MinimumCommitmentMonths = 6, InitialTermMonths = 12
            });
            Assert.Equal(12, longer.InitialTermMonths);

            var none = ContractService.NormalizeTerm(new TermSnapshot
            {
                MinimumCommitmentMonths = 0, InitialTermMonths = 10
            });
            Assert.Equal(0, none.InitialTermMonths);
            Assert.False(none.HasMinimumCommitment);

            var negative = ContractService.NormalizeTerm(new TermSnapshot { MinimumCommitmentMonths = -4 });
            Assert.Equal(0, negative.MinimumCommitmentMonths);
        }

        /// <summary>
        /// AN EXECUTED CONTRACT IS NEVER REWRITTEN. A version frozen against the v2.6 body with the
        /// old ten-month / sixty-day terms renders them exactly as signed: its snapshot carries
        /// both the body and the numbers, and nothing in v2.7 reaches either.
        /// </summary>
        [Fact]
        public void AFrozenTenMonthSixtyDayContractStillReadsAsSigned()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.TemplateBodyText =
                "## 3. TERM, MINIMUM COMMITMENT PERIOD AND RENEWAL\n"
                + "(a) The Initial Term runs for {{INITIAL_TERM_MONTHS}} months from the Service Commencement Date.\n"
                + "(b) The Minimum Commitment Period ends {{MINIMUM_COMMITMENT_MONTHS}} calendar months later.\n"
                + "## 4. TERMINATION\n"
                + "(a) Either Party may terminate for convenience on at least {{TERMINATION_NOTICE_DAYS}} calendar days' written notice.\n";
            snapshot.Term = new TermSnapshot
            {
                InitialTermMonths = 10,
                MinimumCommitmentMonths = 10,
                TerminationNoticeDays = 60,
                ServiceCommencementDate = new DateTime(2026, 4, 1)
            };

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("The Initial Term runs for ten (10) months", rendered.PlainText);
            Assert.Contains("ends ten (10) calendar months later", rendered.PlainText);
            Assert.Contains("sixty (60) calendar days' written notice", rendered.PlainText);
            Assert.Empty(rendered.UnresolvedTokens);
        }

        /// <summary>
        /// A STORED SNAPSHOT WRITTEN BEFORE v2.7 IS READ BACK UNCHANGED. Its JSON has no supplies
        /// block and no latent-deficiency limit; the new defaults must not leak into the term it
        /// recorded, and a parse-and-reserialise round trip (what generating a revision does) must
        /// keep the ten-month commitment and sixty-day notice it was signed with.
        /// </summary>
        [Fact]
        public void ALegacySnapshotKeepsItsTermsThroughParseAndClone()
        {
            const string legacyJson =
                "{\"contractNumber\":\"DC-2026-0001\",\"versionNumber\":1,"
                + "\"templateBodyText\":\"(b) The Minimum Commitment Period ends {{MINIMUM_COMMITMENT_MONTHS}} "
                + "calendar months later, on {{TERMINATION_NOTICE_DAYS}} calendar days' notice.\","
                + "\"term\":{\"initialTermMonths\":10,\"minimumCommitmentMonths\":10,"
                + "\"terminationNoticeDays\":60,\"renewalType\":\"month-to-month\"},"
                + "\"advanced\":{\"qualityComplaintHours\":48}}";

            var parsed = ContractSnapshot.Parse(legacyJson);
            var cloned = parsed.Clone();

            foreach (var snapshot in new[] { parsed, cloned })
            {
                Assert.Equal(10, snapshot.Term.MinimumCommitmentMonths);
                Assert.Equal(10, snapshot.Term.InitialTermMonths);
                Assert.Equal(60, snapshot.Term.TerminationNoticeDays);
                Assert.True(snapshot.Term.HasMinimumCommitment);
                Assert.Equal(48, snapshot.Advanced.QualityComplaintHours);

                var text = ContractRenderer.Render(snapshot).PlainText;
                Assert.Contains("ends ten (10) calendar months later, on sixty (60) calendar days' notice",
                    text);
            }
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
        /// HAND SOAP IS NOT DISCUSSED AT ALL (owner's rule, template v2.7).
        ///
        /// Versions 2.0-2.6 stated an express exclusion in A8(a). The owner does not want the
        /// subject raised in either direction - no promise to supply it and no disclaimer about
        /// not supplying it - so a newly generated agreement contains the word nowhere, whatever
        /// business type or supplies arrangement it is drafted with.
        /// </summary>
        [Theory]
        [InlineData("Restaurant", SupplyProvider.Contractor)]
        [InlineData("Office", SupplyProvider.Client)]
        [InlineData("Restaurant", SupplyProvider.Shared)]
        public void TheAgreementNeverMentionsHandSoap(string businessType, SupplyProvider equipment)
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Scope = ScopeStructureFor(businessType);
            snapshot.Supplies.EquipmentProvidedBy = equipment;
            snapshot.Supplies.EquipmentArrangementNotes = "Contractor provides vacuums and tools; Client provides chemicals";
            var rendered = ContractRenderer.Render(snapshot);

            Assert.DoesNotContain("refill", rendered.PlainText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("replenish", rendered.PlainText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, Regex.Matches(rendered.PlainText, "soap", RegexOptions.IgnoreCase).Count);
            Assert.Equal(0, Regex.Matches(ContractTemplateSeed.BodyText, "soap", RegexOptions.IgnoreCase).Count);
        }

        // ── supplies, equipment and consumables (template v2.7) ─────────────────

        /// <summary>
        /// CLEANING SUPPLIES CAN BE COMPANY-PROVIDED: the recurring fee includes them, supplied at
        /// Contractor's expense with no separate charge - the arrangement v2.6 hardcoded for all.
        /// </summary>
        [Fact]
        public void SuppliesCanBeProvidedByTheContractor()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Supplies.EquipmentProvidedBy = SupplyProvider.Contractor;

            var text = ContractRenderer.Render(snapshot).PlainText;

            Assert.Contains("The recurring fee includes all labor, supervision, cleaning equipment, "
                            + "tools, chemicals, products and ordinary cleaning supplies", text);
            Assert.Contains("No separate equipment or cleaning-product charge applies.", text);
            Assert.Contains("Cleaning equipment, tools, chemicals, products and ordinary cleaning "
                            + "supplies: Contractor", text);
        }

        /// <summary>
        /// CLEANING SUPPLIES CAN BE CLIENT-PROVIDED: the fee then buys labor and supervision, the
        /// Client supplies the products, and Contractor may decline an unsafe or unsuitable one.
        /// The document must not still claim Contractor supplies them at its own expense.
        /// </summary>
        [Fact]
        public void SuppliesCanBeProvidedByTheClient()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Supplies.EquipmentProvidedBy = SupplyProvider.Client;

            var text = ContractRenderer.Render(snapshot).PlainText;

            Assert.Contains("The recurring fee includes all labor and supervision. Client supplies, "
                            + "at its own cost, the cleaning equipment", text);
            Assert.Contains("may decline to use a product or item of equipment it reasonably "
                            + "considers unsafe or unsuitable", text);
            Assert.DoesNotContain("Contractor supplies them at its own expense", text);
            Assert.DoesNotContain("All-inclusive", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Cleaning equipment, tools, chemicals, products and ordinary cleaning "
                            + "supplies: Client", text);
        }

        /// <summary>A shared arrangement must say how it is shared, or it is flagged.</summary>
        [Fact]
        public void ASharedSuppliesArrangementPrintsItsDescriptionOrIsFlagged()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Supplies.EquipmentProvidedBy = SupplyProvider.Shared;
            snapshot.Supplies.EquipmentArrangementNotes = "Contractor provides vacuums; Client provides chemicals";

            var described = ContractRenderer.Render(snapshot);
            Assert.Contains("Divided between the Parties: Contractor provides vacuums; Client "
                            + "provides chemicals", described.PlainText);
            Assert.DoesNotContain("EQUIPMENT_PROVIDED_BY", described.UnresolvedTokens);

            snapshot.Supplies.EquipmentArrangementNotes = null;
            Assert.Contains("EQUIPMENT_PROVIDED_BY", ContractRenderer.Render(snapshot).UnresolvedTokens);
        }

        /// <summary>
        /// CONSUMABLES CAN BE SPLIT ITEM BY ITEM - here Contractor provides the trash liners and
        /// Client the paper towels and toilet tissue - and Section 6(c) no longer assigns them all
        /// to Client in fixed prose.
        /// </summary>
        [Fact]
        public void ConsumablesCanBeAllocatedToEitherPartyItemByItem()
        {
            var text = ContractRenderer.Render(OtherClientSnapshot()).PlainText;

            Assert.Contains("Trash bags and liners: Contractor", text);
            Assert.Contains("Paper towels: Client", text);
            Assert.Contains("Toilet tissue: Client", text);
            Assert.Contains("Other agreed consumables: Client: coffee filters", text);
            Assert.DoesNotContain("Client supplies, at its own cost, toilet tissue, paper towels", text);
            Assert.Contains("are allocated between the Parties in Exhibit B, B3", text);
        }

        /// <summary>
        /// Nothing is assumed: an allocation nobody answered prints a ruled blank and lands in the
        /// preview's warning banner, rather than defaulting to the Client as v2.6 did.
        /// </summary>
        [Fact]
        public void AnUnansweredAllocationIsFlaggedRatherThanAssumed()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Supplies = new SuppliesSnapshot();

            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("EQUIPMENT_PROVIDED_BY", rendered.UnresolvedTokens);
            Assert.Contains("TRASH_LINERS_PROVIDED_BY", rendered.UnresolvedTokens);
            Assert.Contains("PAPER_TOWELS_PROVIDED_BY", rendered.UnresolvedTokens);
            Assert.Contains("TOILET_TISSUE_PROVIDED_BY", rendered.UnresolvedTokens);
            // No further consumables were agreed, so that row leaves the exhibit entirely.
            Assert.DoesNotContain("Other agreed consumables", rendered.PlainText);
            Assert.DoesNotContain("OTHER_CONSUMABLES", rendered.UnresolvedTokens);
        }

        /// <summary>A single consumable is provided by one Party; "Shared" is refused on save.</summary>
        [Fact]
        public void NormalizeSuppliesRefusesASharedConsumableAndDropsNamelessRows()
        {
            var result = ContractService.NormalizeSupplies(new SuppliesSnapshot
            {
                EquipmentProvidedBy = SupplyProvider.Shared,
                EquipmentArrangementNotes = "  split  ",
                PaperTowelsProvidedBy = SupplyProvider.Shared,
                OtherConsumables = new List<ConsumableAllocation>
                {
                    new() { Item = "  ", ProvidedBy = SupplyProvider.Client },
                    new() { Item = " seat covers ", ProvidedBy = SupplyProvider.Contractor }
                }
            });

            Assert.Equal(SupplyProvider.Shared, result.EquipmentProvidedBy);
            Assert.Equal("split", result.EquipmentArrangementNotes);
            Assert.Null(result.PaperTowelsProvidedBy);
            Assert.Single(result.OtherConsumables);
            Assert.Equal("seat covers", result.OtherConsumables[0].Item);
        }

        // ── the Satisfaction Guarantee (template v2.7) ──────────────────────────

        /// <summary>
        /// 24 HOURS, WITH A NARROW 72-HOUR EXCEPTION, AND CORRECTION RATHER THAN A REFUND.
        /// The same rule the published policy and the landing page state.
        /// </summary>
        [Fact]
        public void TheGuaranteeIsTwentyFourHoursWithANarrowSeventyTwoHourException()
        {
            var text = ContractRenderer.Render(OtherClientSnapshot()).PlainText;

            Assert.Contains("within twenty-four (24) hours after completion of the visit", text);
            Assert.Contains("in any event no later than seventy-two (72) hours after completion of "
                            + "the visit; this is a limited exception for deficiencies not "
                            + "reasonably discoverable sooner", text);
            Assert.Contains("reasonable opportunity to correct the affected task or area without "
                            + "additional charge", text);
            Assert.Contains("does not include re-cleaning of the entire Premises", text);
            Assert.Contains("does not by itself entitle Client to a refund of the full visit fee", text);
            Assert.Contains("conditions caused after Contractor completed the visit, including by "
                            + "Client's employees, other contractors, construction crews, building "
                            + "staff, vendors or occupants", text);
            Assert.DoesNotContain("forty-eight (48) hours after the visit", text);
        }

        // ── Scope of Work detail and optional exhibits (template v2.7) ──────────

        private static readonly string[] SiteDetailLabels =
        {
            "APPROXIMATE SERVICED SQUARE FOOTAGE", "CUSTOMER RESTROOM AND FIXTURE COUNTS",
            "EMPLOYEE RESTROOM AND FIXTURE COUNTS", "FLOOR AND SURFACE MATERIALS",
            "INCLUDED KITCHEN EQUIPMENT AND EXTERIOR SURFACES", "TOUCHPOINTS AND CLEARED SURFACES",
            "INTERIOR GLASS AND WINDOW LOCATIONS", "INCLUDED FOOD-CONTACT OR DINING-TABLE SANITIZING",
            "ACCESS METHOD AND CLOSEOUT PROCEDURE REFERENCE", "EQUIPMENT THAT MUST REMAIN OPERATING",
            "WASTE, RECYCLING AND SEPARATELY COLLECTED ORGANICS", "FOOD-SERVICE PERMIT HOLDER",
            "SITE, LANDLORD OR BRAND REQUIREMENTS", "BASELINE WALKTHROUGH DATE AND RECORD",
            "INITIAL-WORK CHANGE ORDER"
        };

        /// <summary>Detailed mode, every site detail filled: every one of them prints.</summary>
        [Fact]
        public void DetailedScopeWithEverySiteDetailPrintsThemAll()
        {
            var text = ContractRenderer.Render(OtherClientSnapshot()).PlainText;

            foreach (var label in SiteDetailLabels) Assert.Contains(label, text);
            Assert.Contains("The Parties shall record the following site details", text);
            Assert.Contains("A1. INCLUDED AREAS AND TASKS", text);
        }

        /// <summary>Detailed mode, two of fifteen filled: exactly those two print.</summary>
        [Fact]
        public void DetailedScopeWithTwoSiteDetailsPrintsOnlyThoseTwo()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.SiteDetails = new SiteDetailsSnapshot
            {
                ApproximateSquareFootage = "3,200 square feet",
                WasteReceptacleLocations = "loading dock, bay 2"
            };

            var rendered = ContractRenderer.Render(snapshot);
            var text = rendered.PlainText;

            Assert.Contains("APPROXIMATE SERVICED SQUARE FOOTAGE: 3,200 square feet", text);
            Assert.Contains("ORGANICS RECEPTACLE LOCATIONS: loading dock, bay 2", text);
            var printed = SiteDetailLabels.Count(label => text.Contains(label, StringComparison.Ordinal));
            Assert.Equal(2, printed);
            Assert.DoesNotContain(ContractPlaceholders.RuledBlank, text);
            Assert.Empty(rendered.UnresolvedTokens);
        }

        /// <summary>With no site details at all, the list and its introduction both disappear.</summary>
        [Fact]
        public void DetailedScopeWithNoSiteDetailsDropsTheListAndItsIntroduction()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.SiteDetails = new SiteDetailsSnapshot();

            var text = ContractRenderer.Render(snapshot).PlainText;

            foreach (var label in SiteDetailLabels) Assert.DoesNotContain(label, text);
            Assert.DoesNotContain("The Parties shall record the following site details", text);
            // The detailed scope itself is still there.
            Assert.Contains("A1. INCLUDED AREAS AND TASKS", text);
            Assert.Contains("A8. BASELINE AND DEEP CLEANING", text);
        }

        /// <summary>
        /// Simplified mode: a one-paragraph Exhibit A, none of the detailed content, and every
        /// reference in the body pointing at the Scope of Work that paragraph records.
        /// </summary>
        [Fact]
        public void SimplifiedScopeRendersOnlyTheShortClause()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.ScopeDetail = ScopeDetailMode.Simplified;

            var rendered = ContractRenderer.Render(snapshot);
            var text = rendered.PlainText;

            Assert.Contains("The Parties have agreed the service scope separately through their "
                            + "service discussions, walkthrough, written instructions, service "
                            + "specifications, or other mutually accepted directions.", text);
            Assert.Contains("Contractor will perform the mutually agreed commercial cleaning services "
                            + "at the Premises according to the agreed schedule.", text);
            Assert.Contains("Material additional work or services outside the agreed scope require "
                            + "separate approval in accordance with Section 9.", text);
            Assert.DoesNotContain("A1. INCLUDED AREAS AND TASKS", text);
            Assert.DoesNotContain("A2. EXCLUDED AREAS", text);
            Assert.DoesNotContain("SERVICE PREMISES:", text);
            foreach (var label in SiteDetailLabels) Assert.DoesNotContain(label, text);

            Assert.Contains("The Scope of Work is recorded in Exhibit A.", text);
            Assert.Contains("work outside the Scope of Work recorded in Exhibit A", text);
            Assert.Empty(rendered.UnresolvedTokens);
        }

        /// <summary>
        /// Omitted mode: NO Exhibit A, and not one reference to it left behind - every clause that
        /// used to point at it now points at the agreed Scope of Work, and the signature block
        /// executes Exhibit B alone. The scope protection survives: unagreed work is still outside
        /// the agreement and still needs approval.
        /// </summary>
        [Fact]
        public void OmittedScopeHasNoExhibitAAndNoReferenceToIt()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.ScopeDetail = ScopeDetailMode.Omitted;

            var rendered = ContractRenderer.Render(snapshot);
            var text = rendered.PlainText;

            Assert.DoesNotContain("Exhibit A", text);
            Assert.DoesNotContain("EXHIBIT A", text);
            Assert.DoesNotContain("Exhibits A and B", text);
            Assert.DoesNotContain(" A1", text);
            Assert.DoesNotContain(rendered.Blocks,
                b => b.Kind == ContractBlockKind.Heading && b.Text == "EXHIBIT A");

            Assert.Contains("(a) Contractor shall provide the commercial cleaning services mutually "
                            + "agreed by the Parties (the \"Services\" or the \"Scope of Work\") at the "
                            + "premises described in paragraph (b) (the \"Premises\").", text);
            Assert.Contains("(c) Exhibit B is incorporated into and forms part of this Agreement.", text);
            Assert.Contains("including Sections 1 through 36, Exhibit B, and the representations", text);
            Assert.Contains("Services not expressly described in the agreed Scope of Work are not "
                            + "included. Materially additional services, and work outside the Scope of "
                            + "Work, require separate approval, and a material change to the Scope of "
                            + "Work requires a Change Order or written confirmation under Section 9.", text);
            Assert.Contains("obligates Contractor to perform work outside the agreed Scope of Work", text);

            // The supplies allocation lives in Exhibit B, so it survives the omission.
            Assert.Contains("B3. SUPPLIES, EQUIPMENT AND CONSUMABLES", text);
            Assert.Empty(rendered.UnresolvedTokens);
        }

        /// <summary>No endorsement beyond Section 20 → no B5 at all; the Section 20 baseline stays.</summary>
        [Fact]
        public void AnEmptyEndorsementsExhibitDisappears()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Insurance = new InsuranceEndorsementsSnapshot();

            var text = ContractRenderer.Render(snapshot).PlainText;

            Assert.DoesNotContain("ADDITIONAL INSURANCE ENDORSEMENTS", text);
            Assert.DoesNotContain("Additional endorsements agreed", text);
            // The legacy blank rendering of the insurer row was "Not applicable".
            Assert.DoesNotContain("applicable work: Not applicable", text);
            Assert.DoesNotContain("Insurer, policy, endorsement form", text);
            Assert.Contains("20. INSURANCE", text);
            Assert.Contains("Commercial General Liability insurance", text);
        }

        /// <summary>A partly filled B5 prints only the filled rows.</summary>
        [Fact]
        public void APartialEndorsementsExhibitPrintsOnlyItsFilledRows()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Insurance = new InsuranceEndorsementsSnapshot { AgreedEndorsements = "Additional insured" };

            var text = ContractRenderer.Render(snapshot).PlainText;

            Assert.Contains("B5. ADDITIONAL INSURANCE ENDORSEMENTS", text);
            Assert.Contains("Additional endorsements agreed for this engagement: Additional insured", text);
            Assert.DoesNotContain("Insurer, policy, endorsement form", text);
            Assert.DoesNotContain("Agreed additional premium", text);
        }

        /// <summary>
        /// Empty optional contact rows leave Exhibit B4. The primaries stay: Section 14 permits a
        /// failed-access charge only after an attempt to reach the on-call contact, so a missing
        /// one is still flagged rather than silently dropped.
        /// </summary>
        [Fact]
        public void EmptyOptionalContactRowsDisappearFromExhibitB4()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Contacts.ContractorApprovalEmail = null;
            snapshot.Contacts.ClientApprovalEmail = "";
            snapshot.Contacts.ContractorBackupContact = null;
            snapshot.Contacts.ClientBackupContact = null;

            var rendered = ContractRenderer.Render(snapshot);
            var text = rendered.PlainText;

            Assert.DoesNotContain("Contractor approval email", text);
            Assert.DoesNotContain("Client approval email", text);
            Assert.DoesNotContain("backup on-call contact:", text);
            Assert.Contains("Client primary on-call contact: Dana Okafor", text);
            Assert.Contains("Client operational email: facilities@northline.example", text);
            Assert.Empty(rendered.UnresolvedTokens);
        }

        /// <summary>
        /// Nothing optional is padded with "None" or "Not applicable": a contract with every
        /// optional field empty contains neither word.
        /// </summary>
        [Fact]
        public void EmptyOptionalFieldsNeverPrintNoneOrNotApplicable()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.SiteDetails = new SiteDetailsSnapshot();
            snapshot.Insurance = new InsuranceEndorsementsSnapshot();
            snapshot.Schedule.CompletionTime = null;
            snapshot.Contacts.ContractorApprovalEmail = null;
            snapshot.Contacts.ClientApprovalEmail = null;
            snapshot.Contacts.ContractorBackupContact = null;
            snapshot.Contacts.ClientBackupContact = null;
            snapshot.Supplies.OtherConsumables.Clear();

            var text = ContractRenderer.Render(snapshot).PlainText;

            Assert.DoesNotMatch(new Regex(@"\bNone\b"), text);
            Assert.DoesNotContain("Not applicable", text);
            Assert.DoesNotContain(ContractPlaceholders.RuledBlank, text);
            Assert.DoesNotContain(ContractPlaceholders.OmitLineSentinel, text);
        }

        /// <summary>The outer limit can never close before the standard window it extends.</summary>
        [Fact]
        public void TheLatentLimitIsNeverShorterThanTheStandardWindow()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Advanced.QualityComplaintHours = 96;
            snapshot.Advanced.QualityLatentDeficiencyLimitHours = 72;

            var text = ContractRenderer.Render(snapshot).PlainText;

            Assert.Contains("no later than ninety-six (96) hours after completion", text);
        }

        // ── structure ──────────────────────────────────────────────────────────

        [Fact]
        public void BothExhibitsAreRenderedInFull()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            Assert.Contains("EXHIBIT A", rendered.PlainText);
            Assert.Contains("SCOPE OF WORK", rendered.PlainText);
            Assert.Contains("EXHIBIT B", rendered.PlainText);
            Assert.Contains("SCHEDULE, PRICING, SUPPLIES AND CONTACTS", rendered.PlainText);
            // Exhibit B is a two-column schedule, so it must produce table rows, not paragraphs.
            Assert.Contains(rendered.Blocks, b => b.Kind == ContractBlockKind.ExhibitRow);

            // All four of Exhibit B's sub-sections, including the two the drafted agreement added.
            Assert.Contains("B1. SERVICE DATES AND SCHEDULE", rendered.PlainText);
            Assert.Contains("B2. PRICING AND PAYMENT", rendered.PlainText);
            Assert.Contains("B3. SUPPLIES, EQUIPMENT AND CONSUMABLES", rendered.PlainText);
            Assert.Contains("B4. AUTHORIZED REPRESENTATIVES AND CONTACTS", rendered.PlainText);
            // B5 prints because this fixture agreed endorsements beyond Section 20.
            Assert.Contains("B5. ADDITIONAL INSURANCE ENDORSEMENTS", rendered.PlainText);
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

            // Since v3.1 an emptied group takes its whole subsection with it - no "none", and no
            // broken "The following are excluded: ." either.
            var rendered = ContractRenderer.Render(snapshot);
            Assert.DoesNotContain("EXCLUDED AREAS", rendered.PlainText);
            Assert.DoesNotContain("excluded: .", rendered.PlainText);
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

            // Numbered after the last subsection that actually rendered (v3.1), never a fixed A10.
            var rendered = ContractRenderer.Render(snapshot);
            var additional = rendered.Blocks.Single(b => b.Kind == ContractBlockKind.SubHeading
                && b.Text.EndsWith(". ADDITIONAL SCOPE"));
            var numbered = rendered.Blocks.Count(b => b.Kind == ContractBlockKind.SubHeading
                && System.Text.RegularExpressions.Regex.IsMatch(b.Text, @"^A\d+\. "));
            Assert.Equal($"A{numbered}. ADDITIONAL SCOPE", additional.Text);
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
