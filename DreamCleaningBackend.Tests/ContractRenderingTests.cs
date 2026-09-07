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
                    ServiceTime = "7:00 PM",
                    FlexibleScheduling = true,
                    PerformedWhileClosed = true,
                    AccessType = "a badge issued by Client"
                },
                Term = new TermSnapshot
                {
                    InitialTermMonths = 6,
                    MinimumCommitmentMonths = 2,
                    TerminationNoticeDays = 45,
                    RenewalType = "month-to-month",
                    GoverningLawState = "New York",
                    VenueCounty = "New York County"
                },
                Pricing = new PricingSnapshot
                {
                    PriceMode = ContractPriceMode.PreTax,
                    PriceInput = 400m,
                    SalesTaxRatePercent = 8.875m,
                    CancellationPercent = 50m
                },
                Advanced = new AdvancedTermsSnapshot
                {
                    CurePeriodDays = 20,
                    PastDueDays = 45,
                    ConfidentialityYears = 3,
                    NonSolicitMonths = 18,
                    NonHireDamages = 7500m,
                    LiabilityCapLookbackMonths = 6,
                    DisputeDiscussionDays = 21,
                    MediationVenue = "New York County, New York"
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
        [InlineData("462.72")]
        [InlineData("462.71")]
        [InlineData("11210")]
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

        // ── the values that must appear ────────────────────────────────────────

        [Fact]
        public void TheDocumentQuotesTheServerDerivedFigures()
        {
            var snapshot = OtherClientSnapshot();
            var rendered = ContractRenderer.Render(snapshot);

            // 400.00 pre-tax at 8.875% -> 35.50 tax -> 435.50 total -> 217.75 / 217.75.
            Assert.Equal(435.50m, snapshot.Pricing.TotalPrice);
            Assert.Contains("$400.00", rendered.PlainText);
            Assert.Contains("$435.50", rendered.PlainText);
            Assert.Contains("$217.75", rendered.PlainText);
        }

        [Fact]
        public void ChangedTermsAreSpelledOutInTheBodyNotJustExhibitB()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());

            Assert.Contains("initial term of six (6) months", rendered.PlainText);
            Assert.Contains("first two (2) months", rendered.PlainText);
            Assert.Contains("forty-five (45) days prior written notice", rendered.PlainText);
            // Advanced terms reach their own sections, not only the collapsed form panel.
            Assert.Contains("within twenty (20) days after receiving written notice", rendered.PlainText);
            Assert.Contains("continues for three (3) years after termination", rendered.PlainText);
            Assert.Contains("for eighteen (18) months after its termination", rendered.PlainText);
        }

        [Fact]
        public void FrequencyOtherThanWeeklyRendersGrammatically()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            Assert.Contains("two (2) scheduled cleaning visits per calendar week", rendered.PlainText);
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

        // ── structure ──────────────────────────────────────────────────────────

        [Fact]
        public void BothExhibitsAreRenderedInFull()
        {
            var rendered = ContractRenderer.Render(OtherClientSnapshot());
            Assert.Contains("EXHIBIT A", rendered.PlainText);
            Assert.Contains("SCOPE OF WORK", rendered.PlainText);
            Assert.Contains("EXHIBIT B", rendered.PlainText);
            Assert.Contains("PRICING AND BILLING SCHEDULE", rendered.PlainText);
            // Exhibit B is a two-column schedule, so it must produce table rows, not paragraphs.
            Assert.Contains(rendered.Blocks, b => b.Kind == ContractBlockKind.ExhibitRow);
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
            snapshot.Scope = ScopeStructureFor("Restaurant");
            var rendered = ContractRenderer.Render(snapshot);

            Assert.Contains("Dining area", rendered.PlainText);
            Assert.Contains("Customer restrooms", rendered.PlainText);
            Assert.Contains("The break room", rendered.PlainText);
            Assert.Contains("Internal cleaning of any equipment", rendered.PlainText);
            Assert.Contains("Sweeping", rendered.PlainText);
        }

        [Fact]
        public void AnUntickedScopeItemIsNotWrittenIntoTheDocument()
        {
            var snapshot = OtherClientSnapshot();
            snapshot.Scope = ScopeStructureFor("Restaurant");
            var included = snapshot.Scope.Groups.First(g => g.Key == "included-areas");
            included.Items.First(i => i.Label == "Dining area").Selected = false;

            var rendered = ContractRenderer.Render(snapshot);
            Assert.DoesNotContain("Dining area", rendered.PlainText);
            // The rest of the group is untouched.
            Assert.Contains("Customer restrooms", rendered.PlainText);
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
