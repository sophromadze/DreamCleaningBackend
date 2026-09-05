using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// WHAT THE CUSTOMER IS TOLD TO HAVE READY.
    ///
    /// Three extras take items off the checklist, and each takes off a DIFFERENT thing:
    ///
    ///   "Cleaning Supplies"   -> the products (Zep, Windex, cloths, sponge, mop)
    ///   "Cleaning Essentials" -> paper towels, garbage bags, toilet brush
    ///   "Vacuum Cleaner"      -> the broom-or-vacuum line, and only that line
    ///
    /// The rule that is easy to get wrong: Cleaning Essentials does NOT cover the broom or
    /// vacuum. A cleaner cannot carry one to every job, so the customer either owns one or buys
    /// the Vacuum Cleaner extra - and the checklist has to keep saying so even when they have
    /// bought everything else.
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

        [Fact]
        public void CleaningEssentialsOnly_LeavesTheBroomPlusTheProductsWeWouldHaveBrought()
        {
            Assert.Equal(new[]
            {
                "Broom or vacuum cleaner",
                "Zep liquids: Green, Floor (or similar)",
                "Windex liquid (or similar)",
                "Cleaning cloths, Sponge and Mop"
            }, ChecklistFor("Cleaning Essentials"));
        }

        [Fact]
        public void SuppliesPlusEssentials_LeavesOnlyTheBroomOrVacuum()
        {
            Assert.Equal(new[] { "Broom or vacuum cleaner" },
                ChecklistFor("Cleaning Supplies", "Cleaning Essentials"));
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
        /// All three bought leaves nothing to prepare. Every surface has to render this as good
        /// news - the email and SMS say so in words rather than printing an empty bulleted box
        /// under a "please provide the following items" heading.
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
                CustomerSupplyChecklist.Resolve(new[] { "Cleaning Essentials" }, isCustomServiceType: true));

            Assert.Equal(new[] { "Broom or vacuum cleaner" }, items);
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
        }
    }
}
