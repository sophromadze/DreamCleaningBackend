using System.Reflection;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// WHAT THE CUSTOMER IS TOLD TO HAVE READY.
    ///
    /// Three extras take items off the checklist, and each takes off a DIFFERENT thing:
    ///
    ///   "Cleaning Supplies"   -> the products (Zep, Windex, cloths, sponge, mop)
    ///   "Cleaning Essentials" -> paper towels, garbage bags, toilet brush, broom
    ///   "Vacuum Cleaner"      -> the broom-or-vacuum line, and only that line
    ///
    /// The broom moved INTO Cleaning Essentials in 2026-09 (it used to be a permanent ask), so
    /// the broom-or-vacuum line now comes off for EITHER of the last two extras - we bring a
    /// broom, or we bring a vacuum, and the customer was only ever asked for one of the pair.
    /// Supplies + Essentials consequently leaves nothing at all.
    ///
    /// These assertions mirror `shared/booking/supply-checklist.utils.spec.ts` case for case.
    /// The two files are the contract that the confirmation email, the SMS, the booking modal
    /// and the order pages all name the same products.
    /// </summary>
    public class CustomerSupplyChecklistTests
    {
        private static List<string> ChecklistFor(params string[] extraNames) =>
            CustomerSupplyChecklist.BuildItems(
                CustomerSupplyChecklist.Resolve(extraNames, isCustomServiceType: false));

        [Fact]
        public void CleaningEssentialsAndCleaningSupplies_AreNotMatchedByEachOther()
        {
            Assert.False(CustomerSupplyChecklist.HasCleaningSuppliesExtra(new[] { "Cleaning Essentials" }));
            Assert.False(CustomerSupplyChecklist.HasCleaningEssentialsExtra(new[] { "Cleaning Supplies" }));
            Assert.True(CustomerSupplyChecklist.HasCleaningEssentialsExtra(new[] { "Cleaning Essentials" }));
        }

        [Fact]
        public void NothingBought_TheCustomerProvidesEverything()
        {
            Assert.Equal(new[]
            {
                "Paper towels",
                "Garbage bags",
                "Broom or vacuum cleaner",
                "Toilet brush",
                "Zep liquids: Green, Floor (or similar)",
                "Windex liquid (or similar)",
                "Cleaning cloths, Sponge and Mop"
            }, ChecklistFor());
        }

        [Fact]
        public void CleaningSuppliesOnly_LeavesTheOriginalFourItems()
        {
            Assert.Equal(new[]
            {
                "Paper towels",
                "Garbage bags",
                "Broom or vacuum cleaner",
                "Toilet brush"
            }, ChecklistFor("Cleaning Supplies"));
        }

        /// <summary>
        /// Essentials covers the broom now, so NOTHING from that group survives - only the
        /// products Cleaning Supplies would have brought.
        /// </summary>
        [Fact]
        public void CleaningEssentialsOnly_LeavesOnlyTheProductsWeWouldHaveBrought()
        {
            Assert.Equal(new[]
            {
                "Zep liquids: Green, Floor (or similar)",
                "Windex liquid (or similar)",
                "Cleaning cloths, Sponge and Mop"
            }, ChecklistFor("Cleaning Essentials"));
        }

        [Fact]
        public void SuppliesPlusEssentials_LeaveNothingAtAll()
        {
            Assert.Empty(ChecklistFor("Cleaning Supplies", "Cleaning Essentials"));
        }

        /// <summary>The broom is part of the set we bring, so it is never also asked for.</summary>
        [Fact]
        public void TheBroomIsOneOfTheItemsCleaningEssentialsCovers()
        {
            Assert.Contains(
                CustomerSupplyChecklist.ItemBroom,
                CustomerSupplyChecklist.EssentialsItemKeys(weBringEssentials: true, weBringVacuum: false));
            Assert.DoesNotContain("Broom or vacuum cleaner", ChecklistFor("Cleaning Essentials"));
        }

        [Fact]
        public void OvenCleanerRule_SurvivesBuyingCleaningEssentials()
        {
            Assert.Contains("Zep liquids: Green, Floor (or similar), Oven Cleaner (or similar)",
                ChecklistFor("Cleaning Essentials", "Oven Cleaning"));
        }

        [Fact]
        public void VacuumExtra_RemovesTheBroomLineAndOnlyThatLine()
        {
            Assert.Equal(new[]
            {
                "Paper towels",
                "Garbage bags",
                "Toilet brush",
                "Zep liquids: Green, Floor (or similar)",
                "Windex liquid (or similar)",
                "Cleaning cloths, Sponge and Mop"
            }, ChecklistFor("Vacuum Cleaner"));
        }

        /// <summary>
        /// An empty checklist leaves nothing to prepare. Every surface has to render this as good
        /// news - the email and SMS say so in words rather than printing an empty bulleted box
        /// under a "please provide the following items" heading. Note it now takes only TWO
        /// extras to reach: Supplies + Essentials, with or without the vacuum.
        /// </summary>
        [Fact]
        public void AllThreeExtras_LeaveAnEmptyChecklist()
        {
            Assert.Empty(ChecklistFor("Cleaning Supplies", "Cleaning Essentials", "Vacuum Cleaner"));
        }

        [Fact]
        public void CustomServiceType_NeverGetsTheProductsBlock()
        {
            var items = CustomerSupplyChecklist.BuildItems(
                CustomerSupplyChecklist.Resolve(Array.Empty<string>(), isCustomServiceType: true));

            Assert.Equal(new[]
            {
                "Paper towels",
                "Garbage bags",
                "Broom or vacuum cleaner",
                "Toilet brush"
            }, items);
        }

        /// <summary>
        /// THE CLEANER'S HALF OF THE SAME ARRANGEMENT. The customer buying "Cleaning Essentials"
        /// means WE bring the paper towels - so the cleaner is told to load them AND the item
        /// drops off the customer's own checklist. Read from one source so those two can never
        /// contradict each other, which is the bug that direction of logic invites.
        /// </summary>
        public class CleanerFacingRules
        {
            private static Order OrderWithExtras(params string[] extraNames) => new Order
            {
                OrderExtraServices = extraNames
                    .Select(n => new OrderExtraService { ExtraService = new ExtraService { Name = n } })
                    .ToList()
            };

            [Fact]
            public void BuyingCleaningEssentials_MeansTheCleanerBringsThem()
            {
                Assert.True(CleanerJobView.RequiresCleanerToBringEssentials(
                    OrderWithExtras("Cleaning Essentials")));
                Assert.False(CleanerJobView.RequiresCleanerToBringEssentials(
                    OrderWithExtras("Cleaning Supplies")));
            }

            /// <summary>
            /// The two flags are independent: buying one must not turn the other on. They are
            /// separate purchases and they put different things in the car.
            /// </summary>
            [Fact]
            public void SuppliesAndEssentials_AreIndependentFlags()
            {
                var essentialsOnly = OrderWithExtras("Cleaning Essentials");
                Assert.False(CleanerJobView.RequiresCleanerToBringSupplies(essentialsOnly));
                Assert.True(CleanerJobView.RequiresCleanerToBringEssentials(essentialsOnly));

                var both = OrderWithExtras("Cleaning Supplies", "Cleaning Essentials");
                Assert.True(CleanerJobView.RequiresCleanerToBringSupplies(both));
                Assert.True(CleanerJobView.RequiresCleanerToBringEssentials(both));
            }

            /// <summary>
            /// It has its own Essentials row on every cleaner surface, so leaving it in the task
            /// list too would name the same thing twice on one screen - the rule Cleaning
            /// Supplies has always followed.
            /// </summary>
            [Fact]
            public void CleaningEssentials_IsNotAlsoListedAsWorkToDo()
            {
                Assert.True(CleanerJobView.IsExtraHiddenFromCleaners("Cleaning Essentials"));
                Assert.True(CleanerJobView.IsExtraHiddenFromCleaners("Cleaning Supplies"));
                Assert.False(CleanerJobView.IsExtraHiddenFromCleaners("Oven Cleaning"));
                // The Vacuum Cleaner extra IS work-adjacent equipment the cleaner carries, and it
                // has no row of its own - it stays in the list.
                Assert.False(CleanerJobView.IsExtraHiddenFromCleaners("Vacuum Cleaner"));
            }

            /// <summary>
            /// THE SAME PRODUCTS WHOEVER IS CARRYING THEM. "Bring cleaning supplies" and "the
            /// customer provides them" are both unactionable without the list - one crew's idea of
            /// what a job needs is not another's - so the items are named in both directions, and
            /// they are the same items. Only the flag beside them changes.
            /// </summary>
            [Fact]
            public void SuppliesItems_AreTheSameListWhicheverWayTheFlagFalls()
            {
                var weBring = CleanerJobView.ResolveSuppliesItemKeys(OrderWithExtras("Cleaning Supplies"));
                var customerProvides = CleanerJobView.ResolveSuppliesItemKeys(OrderWithExtras());

                Assert.Equal(weBring, customerProvides);
                Assert.Equal(new[]
                {
                    CustomerSupplyChecklist.ItemZep,
                    CustomerSupplyChecklist.ItemWindex,
                    CustomerSupplyChecklist.ItemCloths,
                    CustomerSupplyChecklist.ItemSponge,
                    CustomerSupplyChecklist.ItemMop
                }, weBring);
            }

            /// <summary>
            /// The oven liquid rides on the same condition that puts it on the CUSTOMER'S list -
            /// a Deep / Super Deep cleaning, or the Oven Cleaning extra on its own - so the two
            /// halves of the arrangement cannot ask for different chemicals.
            /// </summary>
            [Fact]
            public void SuppliesItems_NameTheOvenLiquidExactlyWhenTheCustomerChecklistDoes()
            {
                Assert.Equal(
                    CustomerSupplyChecklist.ItemZepWithOven,
                    CleanerJobView.ResolveSuppliesItemKeys(OrderWithExtras("Oven Cleaning"))[0]);
                Assert.Equal(
                    CustomerSupplyChecklist.ItemZepWithOven,
                    CleanerJobView.ResolveSuppliesItemKeys(OrderWithExtras("Deep Cleaning"))[0]);
                Assert.Equal(
                    CustomerSupplyChecklist.ItemZep,
                    CleanerJobView.ResolveSuppliesItemKeys(OrderWithExtras())[0]);
            }

            /// <summary>
            /// The one place the two directions legitimately differ. What WE bring under the
            /// extra includes a BROOM; what the customer has ready is a broom OR a vacuum, exactly
            /// as their own checklist words it.
            /// </summary>
            [Fact]
            public void EssentialsItems_SayBroomWhenWeBringThemAndBroomOrVacuumWhenTheCustomerDoes()
            {
                Assert.Equal(new[]
                {
                    CustomerSupplyChecklist.ItemPaperTowels,
                    CustomerSupplyChecklist.ItemGarbageBags,
                    CustomerSupplyChecklist.ItemToiletBrush,
                    CustomerSupplyChecklist.ItemBroom
                }, CleanerJobView.ResolveEssentialsItemKeys(OrderWithExtras("Cleaning Essentials")));

                Assert.Equal(new[]
                {
                    CustomerSupplyChecklist.ItemPaperTowels,
                    CustomerSupplyChecklist.ItemGarbageBags,
                    CustomerSupplyChecklist.ItemToiletBrush,
                    CustomerSupplyChecklist.ItemBroomOrVacuum
                }, CleanerJobView.ResolveEssentialsItemKeys(OrderWithExtras()));
            }

            /// <summary>
            /// ...and it disappears altogether when we are bringing the vacuum, because nobody
            /// ever asked the customer for a broom. Telling a cleaner to expect one that was never
            /// requested is how a crew arrives without the thing they needed.
            /// </summary>
            [Fact]
            public void EssentialsItems_PromiseNoBroomTheCustomerWasNeverAskedFor()
            {
                var keys = CleanerJobView.ResolveEssentialsItemKeys(OrderWithExtras("Vacuum Cleaner"));

                Assert.DoesNotContain(CustomerSupplyChecklist.ItemBroom, keys);
                Assert.DoesNotContain(CustomerSupplyChecklist.ItemBroomOrVacuum, keys);
                Assert.Equal(3, keys.Count);
            }

            /// <summary>
            /// EVERY ITEM A CLEANER CAN BE SHOWN HAS A WORD IN EVERY LANGUAGE WE MAIL IN.
            ///
            /// The item lists are resolved as translation keys precisely so the mail, the SMS and
            /// the portal name the same things; the cost of that is that a key with no entry in
            /// one dictionary reaches somebody as a bare "toiletBrush". This walks every key the
            /// two resolvers can emit against all four label sets, so adding an item without
            /// translating it fails here rather than in a Georgian cleaner's inbox.
            /// </summary>
            [Fact]
            public void EveryItemKeyIsTranslatedInAllFourAssignmentLanguages()
            {
                var keys = CustomerSupplyChecklist.SuppliesItemKeys(requiresOvenCleaner: true)
                    .Concat(CustomerSupplyChecklist.SuppliesItemKeys(requiresOvenCleaner: false))
                    .Concat(CustomerSupplyChecklist.EssentialsItemKeys(weBringEssentials: true, weBringVacuum: false))
                    .Concat(CustomerSupplyChecklist.EssentialsItemKeys(weBringEssentials: false, weBringVacuum: false))
                    .Distinct()
                    .ToList();

                foreach (var language in new[] { "en", "ka", "ru", "es" })
                {
                    var labels = CleanerEmailLabels(language);
                    foreach (var key in keys)
                    {
                        Assert.True(
                            labels.ContainsKey($"item:{key}") && !string.IsNullOrWhiteSpace(labels[$"item:{key}"]),
                            $"Assignment labels for '{language}' are missing supply item '{key}'.");
                    }
                }
            }

            /// <summary>
            /// The label table is a private implementation detail of EmailService and should stay
            /// one - it is read through reflection here rather than widened, because the thing
            /// worth asserting is the translation coverage, not the shape of the accessor.
            /// </summary>
            private static IReadOnlyDictionary<string, string> CleanerEmailLabels(string language)
            {
                var method = typeof(EmailService).GetMethod(
                    "GetCleanerEmailLabels", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(method);
                return (Dictionary<string, string>)method!.Invoke(null, new object[] { language })!;
            }
        }
    }
}
