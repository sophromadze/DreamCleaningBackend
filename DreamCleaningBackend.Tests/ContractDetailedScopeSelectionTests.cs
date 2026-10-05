using System.Text;
using System.Text.RegularExpressions;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using UglyToad.PdfPig;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// A DETAILED EXHIBIT A SAYS ONLY WHAT WAS SELECTED (template v3.1, 2026-10-01).
    ///
    /// DCC-2026-11363983 selected three restroom tasks (wipe sinks and surfaces, restock paper
    /// towels and tissue, remove restroom trash) and no interior glass. Its PDF still said "Routine
    /// restroom cleaning under ordinary sanitary conditions remains included" (A5) and "Routine
    /// toilet and restroom cleaning ... remains included" (24(a)) - promising toilets, mirrors and
    /// floors nobody bought - printed A7 INTERIOR GLASS AND WINDOWS boilerplate, and jumped from A8
    /// to a hard-coded "A10. ADDITIONAL SCOPE". These tests pin the selection-driven behaviour and
    /// that a version frozen on an older body renders exactly as before.
    /// </summary>
    public class ContractDetailedScopeSelectionTests
    {
        static ContractDetailedScopeSelectionTests()
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        private static ScopeGroup Group(string key, string title, string kind, bool inline, params (string Label, bool Selected, string? Detail)[] items) => new()
        {
            Key = key, Title = title, Kind = kind, Inline = inline,
            Items = items.Select(i => new ScopeItem { Label = i.Label, Selected = i.Selected, Detail = i.Detail }).ToList()
        };

        /// <summary>DCC-2026-11363983's scope selections, with neutral party data.</summary>
        private static ScopeStructure OfficeScope() => new()
        {
            Groups = new List<ScopeGroup>
            {
                Group("area-tasks", "Areas and tasks at each visit", "included", false,
                    ("Reception, entrance and common areas", false, "Clean floors under A4. Wipe accessible cleared surfaces."),
                    ("Restrooms", true, "Wipe sinks and restroom surfaces. Restock Client-provided paper towels and toilet tissue."),
                    ("Kitchen or pantry", true, "Wipe tables and chairs. Keep snack bins, pantry and refrigerator organized."),
                    ("Interior glass", false, "Clean identified interior windows and glass safely reachable from the floor."),
                    ("Whole office", true, "Collect trash and recycling; sweep floors under A4; tidy and wipe accessible cleared surfaces"),
                    ("Conference Rooms", true, "Clean and tidy conference rooms. Wipe tables, desks and chairs."),
                    ("Main Office Space", true, "Remove ordinary desk-area trash. Wipe phone-booth desks."),
                    ("Trash & Recycling", true, "Collect ordinary trash and recycling and place them by the freight elevator."),
                    ("Sunday Tasks", true, "Clean monitors using microfiber screen cloths; tidy and wipe desks; mop floors")),
                Group("included-areas", "Included Areas", "included", true,
                    ("Common areas", true, null), ("Restrooms", true, null), ("Offices", true, null),
                    ("Interior windows and interior glass", false, null), ("Kitchen / Pantry", true, null)),
                Group("excluded-areas", "Excluded Areas", "excluded", true,
                    ("Areas not expressly identified as Included Areas", true, null), ("Storage areas", false, null)),
                Group("kitchen-included", "Kitchen / pantry - included", "included", true,
                    ("Cleaning and wiping of exterior surfaces only", true, null),
                    ("Wipe tables and chairs", true, null),
                    ("Wash ordinary dishes using Client-provided supplies", true, null)),
                Group("kitchen-excluded", "Kitchen / pantry - not included", "excluded", true,
                    ("Internal cleaning of any equipment", true, null)),
                Group("floor-included", "Floor cleaning - included", "included", true,
                    ("Sweeping", true, null), ("Vacuuming where applicable", true, null),
                    ("Mopping of floors", false, null),
                    ("Mopping of floors - Sundays, unless otherwise requested or agreed", true, null)),
                Group("floor-excluded", "Floor cleaning - not included", "excluded", true,
                    ("Stripping", true, null), ("Waxing", true, null)),
                Group("restroom", "Restroom cleaning", "included", true,
                    ("Cleaning of toilets, sinks, mirrors and fixtures", false, null),
                    ("Cleaning of restroom floors", false, null),
                    ("Removal of restroom trash", false, null),
                    ("Wipe sinks and accessible restroom surfaces", true, null),
                    ("Restock Client-provided paper towels and toilet tissue", true, null),
                    ("Remove ordinary restroom trash", true, null)),
                Group("custom-electronics-monitors", "Electronics / Monitors", "included", true,
                    ("On Sundays, wipe monitor screens using an appropriate microfiber screen cloth", true, null)),
                Group("custom-operational-supply-locations", "Operational Supply Locations", "included", true,
                    ("Trash bags: cabinet under the kitchen sink. Mop, broom and vacuum: storage closet", true, null))
            }
        };

        private static ContractSnapshot Detailed(ScopeStructure? scope = null)
        {
            var snapshot = new ContractSnapshot
            {
                ContractNumber = "DCC-2026-00000003", VersionNumber = 1,
                EffectiveDate = new DateTime(2026, 10, 2),
                TemplateBodyText = ContractTemplateSeed.BodyText,
                PremisesType = "office",
                ScopeDetail = ScopeDetailMode.Detailed,
                Contractor = new ContractorSnapshot
                {
                    LegalEntityName = "Nodar Alania Inc.", Dba = "Dream Cleaning NYC", EntityType = "a New York corporation",
                    Address = "8800 20th Ave, Apt 2B", City = "Brooklyn", State = "NY", Zip = "11214",
                    NoticeEmail = "hello@dreamcleaningnyc.com", Phone = "9299301525"
                },
                Client = new ClientSnapshot
                {
                    LegalEntityName = "Example Studio LLC", EntityType = "a limited liability company",
                    PrincipalAddress = "1 Example Plaza, Suite A2", City = "Manhattan", State = "NY", Zip = "10001",
                    NoticeEmail = "ops@example.test", Phone = "2125550100"
                },
                ServiceLocation = new ServiceLocationSnapshot
                {
                    BusinessBrand = "Example", Address = "1 Example Plaza, Suite A2", City = "Manhattan", State = "NY", Zip = "10001"
                },
                ContractorSigner = new SignerSnapshot { FirstName = "Nodar", LastName = "Alania", Title = "CEO", Email = "hello@dreamcleaningnyc.com" },
                ClientSigner = new SignerSnapshot { FirstName = "Jamie", LastName = "Client", Email = "ops@example.test" },
                Schedule = new ScheduleSnapshot
                {
                    FrequencyUnit = "calendar week", VisitsPerPeriod = 6,
                    ServiceDays = new List<string> { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Sunday" },
                    ArrivalWindowStart = "6:00 PM", ArrivalWindowEnd = "7:00 PM", FlexibleScheduling = true,
                    AccessType = "key provided by Client"
                },
                Billing = new BillingCadenceSnapshot { Frequency = ContractBillingFrequency.Weekly, IntervalCount = 1 },
                Term = new TermSnapshot { ServiceCommencementDate = new DateTime(2026, 10, 4) },
                Pricing = new PricingSnapshot
                {
                    PricingBasis = ContractPricingBasis.WeeklyFlatFee, PriceMode = ContractPriceMode.PreTax,
                    PriceInput = 875m, SalesTaxRatePercent = 8.875m, LiabilityCapMultiple = 3
                },
                Contacts = new OperationalContactsSnapshot { ClientOnCallName = "Jamie Client", ContractorSupervisorName = "Nodar Alania" },
                Supplies = new SuppliesSnapshot
                {
                    EquipmentProvidedBy = SupplyProvider.Client, TrashLinersProvidedBy = SupplyProvider.Client,
                    PaperTowelsProvidedBy = SupplyProvider.Client, ToiletTissueProvidedBy = SupplyProvider.Client
                },
                Scope = scope ?? OfficeScope()
            };
            ContractPricingCalculator.Recalculate(snapshot.Pricing, snapshot.Schedule);
            return snapshot;
        }

        private static string Section(string text, string start, string end)
        {
            var a = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(a >= 0, $"'{start}' not found");
            var b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
            return b < 0 ? text[a..] : text[a..b];
        }

        private static List<string> ExhibitAHeadings(RenderedContract rendered) => rendered.Blocks
            .Where(b => b.Kind == ContractBlockKind.SubHeading && Regex.IsMatch(b.Text, @"^A\d+\. "))
            .Select(b => b.Text).ToList();

        // ── 1-4: restrooms are exactly the selected tasks ────────────────────────────────────

        [Fact]
        public void OnlyTheSelectedRestroomTasksAreIncluded()
        {
            var rendered = ContractRenderer.Render(Detailed());
            var text = rendered.PlainText;

            Assert.DoesNotContain("Routine restroom cleaning", text);
            Assert.DoesNotContain("routine restroom cleaning", text);
            var restroom = Section(text, ". RESTROOM CLEANING", ". TRASH AND WASTE");
            Assert.Contains("Wipe sinks and accessible restroom surfaces; Restock Client-provided paper towels and "
                            + "toilet tissue; Remove ordinary restroom trash", restroom);
            Assert.Contains("Only the restroom tasks expressly listed above are included.", restroom);
            Assert.Contains("Specialized biohazard, bloodborne-pathogen, sharps and sewage remediation remain excluded "
                            + "under Sections 24 and 25.", restroom);
        }

        [Theory]
        [InlineData("toilets")]
        [InlineData("Cleaning of toilets")]
        [InlineData("mirrors")]
        [InlineData("restroom floors")]
        [InlineData("Routine toilet")]
        public void UnselectedRestroomWorkAppearsNowhere(string phrase)
        {
            var text = ContractRenderer.Render(Detailed()).PlainText;
            Assert.DoesNotContain(phrase, text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SelectedToiletCleaningIsIncludedWhenChosen()
        {
            var scope = OfficeScope();
            scope.Groups.Single(g => g.Key == "restroom").Items
                .Single(i => i.Label.StartsWith("Cleaning of toilets")).Selected = true;

            var text = ContractRenderer.Render(Detailed(scope)).PlainText;
            Assert.Contains("Cleaning of toilets, sinks, mirrors and fixtures", Section(text, ". RESTROOM CLEANING", ". TRASH AND WASTE"));
        }

        // ── 5: Section 24 never widens the restroom scope ────────────────────────────────────

        [Fact]
        public void SectionTwentyFourDefersToTheScopeOfWork()
        {
            var text = ContractRenderer.Render(Detailed()).PlainText;
            var section = Section(text, "24. HAZARDOUS CONDITIONS", "25. EXCLUDED SERVICES");

            Assert.Contains("Ordinary restroom cleaning is included only to the extent expressly included in Exhibit A; "
                            + "specialized remediation is not.", section);
            Assert.DoesNotContain("remains included", section);
            Assert.DoesNotContain("Routine toilet and restroom cleaning", ContractTemplateSeed.BodyText);
        }

        // ── 6-7: unselected subsections do not render ────────────────────────────────────────

        [Fact]
        public void UnselectedInteriorGlassDoesNotRender()
        {
            var rendered = ContractRenderer.Render(Detailed());
            Assert.DoesNotContain(ExhibitAHeadings(rendered), h => h.Contains("INTERIOR GLASS"));
            Assert.DoesNotContain("Cleaning of the interior windows and interior glass", rendered.PlainText);
        }

        [Fact]
        public void SelectedInteriorGlassRenders()
        {
            var scope = OfficeScope();
            scope.Groups.Single(g => g.Key == "included-areas").Items
                .Single(i => i.Label.StartsWith("Interior windows")).Selected = true;

            var rendered = ContractRenderer.Render(Detailed(scope));
            Assert.Contains(ExhibitAHeadings(rendered), h => h.EndsWith(". INTERIOR GLASS AND WINDOWS"));
        }

        [Theory]
        [InlineData("kitchen-included", "LIMITED KITCHEN SCOPE")]
        [InlineData("floor-included", "FLOOR CLEANING")]
        [InlineData("restroom", "RESTROOM CLEANING")]
        [InlineData("excluded-areas", "EXCLUDED AREAS")]
        public void AnEmptyOptionalSubsectionIsOmitted(string key, string heading)
        {
            var scope = OfficeScope();
            foreach (var item in scope.Groups.Single(g => g.Key == key).Items) item.Selected = false;

            var rendered = ContractRenderer.Render(Detailed(scope));
            Assert.DoesNotContain(ExhibitAHeadings(rendered), h => h.EndsWith(". " + heading));
            Assert.DoesNotContain(": none.", rendered.PlainText);
            Assert.Empty(rendered.UnresolvedTokens);
        }

        [Fact]
        public void TrashSectionFollowsWhetherTrashWorkWasSelected()
        {
            Assert.Contains(ExhibitAHeadings(ContractRenderer.Render(Detailed())), h => h.EndsWith(". TRASH AND WASTE"));

            var scope = OfficeScope();
            foreach (var item in scope.Groups.SelectMany(g => g.Items)
                         .Where(i => Regex.IsMatch(i.Label + " " + i.Detail, "trash|waste|recycling", RegexOptions.IgnoreCase)))
                item.Selected = false;
            Assert.DoesNotContain(ExhibitAHeadings(ContractRenderer.Render(Detailed(scope))), h => h.EndsWith(". TRASH AND WASTE"));
        }

        // ── 8: numbering is sequential, and cross-references follow it ───────────────────────

        [Fact]
        public void ExhibitANumberingIsSequentialWithHiddenSections()
        {
            var rendered = ContractRenderer.Render(Detailed());
            var headings = ExhibitAHeadings(rendered);

            Assert.Equal(new[]
            {
                "A1. INCLUDED AREAS AND TASKS", "A2. EXCLUDED AREAS", "A3. LIMITED KITCHEN SCOPE",
                "A4. FLOOR CLEANING", "A5. RESTROOM CLEANING", "A6. TRASH AND WASTE",
                "A7. BASELINE AND DEEP CLEANING", "A8. ADDITIONAL SCOPE"
            }, headings);
            Assert.DoesNotContain("A10", rendered.PlainText);
            Assert.DoesNotContain("A#", rendered.PlainText);
        }

        [Fact]
        public void CrossReferencesFollowTheNewNumbersAndNeverPointAtAMissingSection()
        {
            var scope = OfficeScope();
            foreach (var item in scope.Groups.Single(g => g.Key == "kitchen-included").Items) item.Selected = false;
            var rendered = ContractRenderer.Render(Detailed(scope));
            var headings = ExhibitAHeadings(rendered);

            // Kitchen hidden: floors move up to A3, and "under A4" in the task table follows them.
            Assert.Contains("A3. FLOOR CLEANING", headings);
            Assert.Contains(rendered.Blocks, b => b.Label == "Whole office" && b.Text.Contains("sweep floors under A3"));
            // A2 used to point at the kitchen subsection, which no longer exists.
            Assert.Contains("The kitchen is included only to the extent stated in Exhibit A.", rendered.PlainText);
            // A unit number in an address is never mistaken for a reference.
            Assert.Contains("Suite A2", rendered.PlainText);
        }

        // ── 9-10: vacuuming and dishwashing stay ─────────────────────────────────────────────

        [Fact]
        public void SelectedVacuumingRemains()
        {
            var text = ContractRenderer.Render(Detailed()).PlainText;
            Assert.Contains("Vacuuming where applicable", Section(text, ". FLOOR CLEANING", ". RESTROOM CLEANING"));
        }

        [Fact]
        public void SelectedDishwashingRemainsAndIsNotExcludedElsewhere()
        {
            var text = ContractRenderer.Render(Detailed()).PlainText;

            Assert.Contains("Wash ordinary dishes using Client-provided supplies",
                Section(text, ". LIMITED KITCHEN SCOPE", ". FLOOR CLEANING"));
            Assert.Contains("(e) Food preparation and food handling; dishwashing, except dishwashing expressly "
                            + "included in Exhibit A;", text);
            Assert.Contains("excluded except a task expressly identified in A1 or in paragraph (a), including any "
                            + "dishwashing listed there.", text);
        }

        [Fact]
        public void ThePdfMatchesThePreviewForTheseSections()
        {
            var snapshot = Detailed();
            var rendered = ContractRenderer.Render(snapshot);
            var block = new ContractSignatureBlockDto
            {
                Contractor = new ContractSignaturePartyDto { PartyLabel = "CONTRACTOR", EntityName = "x", SignerName = "x" },
                Client = new ContractSignaturePartyDto { PartyLabel = "CLIENT", EntityName = "y", SignerName = "y" }
            };
            using var pdf = PdfDocument.Open(new ContractPdfService().GenerateDocument(rendered, snapshot, block, null, true));
            var text = Regex.Replace(string.Join(" ", pdf.GetPages().Select(p => p.Text)), @"\s+", "");

            Assert.Contains("A7.BASELINEANDDEEPCLEANING", text);
            Assert.Contains("A8.ADDITIONALSCOPE", text);
            Assert.DoesNotContain("INTERIORGLASSANDWINDOWS", text);
            Assert.DoesNotContain("Routinerestroomcleaning", text);
            Assert.Contains("Vacuumingwhereapplicable", text);
            Assert.Contains("Washordinarydishes", text);
        }

        // ── 11: a version frozen on an older body renders exactly as before ─────────────────

        [Fact]
        public void AFrozenOlderBodyKeepsItsNumbersAndWording()
        {
            var snapshot = Detailed();
            snapshot.TemplateBodyText =
                "## EXHIBIT A\n## SCOPE OF WORK\n"
                + "### A5. RESTROOM CLEANING\n"
                + "Included work in the customer and employee restrooms consists of {{SCOPE:restroom}}. "
                + "Routine restroom cleaning under ordinary sanitary conditions remains included.\n"
                + "### A7. INTERIOR GLASS AND WINDOWS\nCleaning of the interior glass identified in A1 is included.\n"
                + "### A8. BASELINE AND DEEP CLEANING\nSee A4.\n"
                + "{{SCOPE_ADDITIONAL}}\n";

            var first = ContractRenderer.Render(snapshot);
            Assert.Contains("A5. RESTROOM CLEANING", first.PlainText);
            Assert.Contains("Routine restroom cleaning under ordinary sanitary conditions remains included.", first.PlainText);
            Assert.Contains("A7. INTERIOR GLASS AND WINDOWS", first.PlainText);
            Assert.Contains("See A4.", first.PlainText);
            Assert.Contains("A10. ADDITIONAL SCOPE", first.PlainText);
            Assert.Equal(first.Sha256, ContractRenderer.Render(ContractSnapshot.Parse(snapshot.ToJson())).Sha256);
        }
    }
}
