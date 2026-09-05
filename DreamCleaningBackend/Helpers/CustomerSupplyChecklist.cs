namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Everything about ONE order that decides the customer's "please provide the following
    /// items" checklist. Built once through <see cref="CustomerSupplyChecklist.Resolve"/> and
    /// carried as a unit, because the answer now depends on FIVE facts and a call site passing
    /// five loose bools positionally is a transposition waiting to happen.
    /// </summary>
    public sealed class SupplyChecklistFacts
    {
        /// <summary>Customer bought "Cleaning Supplies" - WE bring the solutions and the cloths.</summary>
        public bool HasCleaningSupplies { get; init; }

        /// <summary>Customer bought "Cleaning Essentials" - WE bring paper towels, garbage bags
        /// and a toilet brush. Deliberately NOT the broom/vacuum: see
        /// <see cref="CustomerSupplyChecklist.BuildItems"/>.</summary>
        public bool HasCleaningEssentials { get; init; }

        /// <summary>Customer bought the "Vacuum Cleaner" extra - we bring one, so they are not
        /// asked for a broom or vacuum.</summary>
        public bool WeBringVacuum { get; init; }

        /// <summary>Deep / Super Deep Cleaning, or the Oven Cleaning extra on its own.</summary>
        public bool RequiresOvenCleaner { get; init; }

        /// <summary>Custom ("Pre-Arranged") service type - it does not use the supplies workflow.</summary>
        public bool IsCustomServiceType { get; init; }
    }

    /// <summary>
    /// The "please provide the following items" checklist shown to the customer, and the
    /// extra-service name matching it is derived from. Single source for the confirmation
    /// email and SMS (mirrored on the frontend in
    /// <c>src/app/shared/booking/supply-checklist.utils.ts</c> for the booking modal,
    /// booking-success, order-details and order-payment pages) - the two must stay in sync,
    /// or the customer is told to buy a different set of products depending on the surface.
    ///
    /// THREE EXTRAS TAKE ITEMS OFF THE LIST, and they take off different things:
    ///   "Cleaning Supplies"   -> the products we would otherwise ask them to buy (Zep, Windex,
    ///                            cloths, sponge, mop).
    ///   "Cleaning Essentials" -> paper towels, garbage bags, toilet brush. NEVER the broom or
    ///                            vacuum: a cleaner cannot carry one to every job, so the customer
    ///                            either owns one or buys the Vacuum Cleaner extra.
    ///   "Vacuum Cleaner"      -> the broom-or-vacuum line, and only that line.
    /// A customer holding all three is asked for nothing, which is why BuildItems can legitimately
    /// return an EMPTY list and every surface has to render that case as "nothing to prepare"
    /// rather than as an empty bulleted box.
    /// </summary>
    public static class CustomerSupplyChecklist
    {
        /// <summary>Name fragments the extras are matched on. Matched on NAME (contains,
        /// case-insensitive) rather than on Id, because catalogue Ids differ between dev and
        /// production and these rows are admin-created.</summary>
        public const string CleaningSuppliesMatch = "cleaning supplies";
        public const string CleaningEssentialsMatch = "cleaning essentials";
        public const string VacuumMatch = "vacuum";

        /// <summary>What "Cleaning Essentials" buys the customer out of, in checklist order.</summary>
        private static readonly string[] EssentialsItems = { "Paper towels", "Garbage bags", "Toilet brush" };

        /// <summary>The line the Vacuum Cleaner extra buys the customer out of.</summary>
        private const string BroomOrVacuumItem = "Broom or vacuum cleaner";

        public static bool HasCleaningSuppliesExtra(IEnumerable<string?> extraServiceNames) =>
            extraServiceNames.Any(n => Contains(n, CleaningSuppliesMatch));

        /// <summary>
        /// True when the customer bought the "Cleaning Essentials" extra. Note this does NOT
        /// match "Cleaning Supplies" and vice versa - the two are separate purchases that can
        /// be held together, and each removes a different part of the checklist.
        /// </summary>
        public static bool HasCleaningEssentialsExtra(IEnumerable<string?> extraServiceNames) =>
            extraServiceNames.Any(n => Contains(n, CleaningEssentialsMatch));

        /// <summary>True when we bring a vacuum, so the customer is not asked for one.</summary>
        public static bool HasVacuumExtra(IEnumerable<string?> extraServiceNames) =>
            extraServiceNames.Any(n => Contains(n, VacuumMatch));

        /// <summary>
        /// True when the cleaners need an oven-cleaning liquid on site: a Deep / Super Deep
        /// Cleaning booking, OR the Oven Cleaning extra on its own. The oven extra used to be
        /// missed here, so a customer who ordered oven cleaning without deep cleaning was never
        /// told to have Oven Cleaner ready.
        /// </summary>
        public static bool RequiresOvenCleaner(IEnumerable<string?> extraServiceNames)
        {
            var names = extraServiceNames.ToList();
            return names.Any(n => Contains(n, "deep cleaning")) || names.Any(n => Contains(n, "oven"));
        }

        /// <summary>Reads every checklist-relevant fact off the order's extra-service names in one pass.</summary>
        public static SupplyChecklistFacts Resolve(IEnumerable<string?> extraServiceNames, bool isCustomServiceType)
        {
            var names = extraServiceNames.ToList();
            return new SupplyChecklistFacts
            {
                HasCleaningSupplies = HasCleaningSuppliesExtra(names),
                HasCleaningEssentials = HasCleaningEssentialsExtra(names),
                WeBringVacuum = HasVacuumExtra(names),
                RequiresOvenCleaner = RequiresOvenCleaner(names),
                IsCustomServiceType = isCustomServiceType
            };
        }

        /// <summary>Reads the facts straight off an order whose OrderExtraServices are loaded.</summary>
        public static SupplyChecklistFacts Resolve(Models.Order order)
        {
            var names = (order.OrderExtraServices ?? new List<Models.OrderExtraService>())
                .Select(oes => oes.ExtraService?.Name);
            return Resolve(names, order.ServiceType?.IsCustom == true);
        }

        /// <summary>
        /// The checklist itself - what the CUSTOMER has to have on site. Each extra removes only
        /// its own items, so the combinations read:
        ///   nothing bought        -> everything;
        ///   Cleaning Supplies     -> paper towels, garbage bags, broom/vacuum, toilet brush;
        ///   Cleaning Essentials   -> broom/vacuum, plus all the products we would have brought;
        ///   Supplies + Essentials -> broom or vacuum cleaner, and nothing else.
        /// A custom ("Pre-Arranged") service type does not use the supplies workflow, so it never
        /// gets the products block regardless.
        /// </summary>
        public static List<string> BuildItems(SupplyChecklistFacts facts)
        {
            var items = new List<string>();

            if (!facts.HasCleaningEssentials)
            {
                items.Add(EssentialsItems[0]);
                items.Add(EssentialsItems[1]);
            }

            if (!facts.WeBringVacuum)
                items.Add(BroomOrVacuumItem);

            if (!facts.HasCleaningEssentials)
                items.Add(EssentialsItems[2]);

            if (facts.HasCleaningSupplies || facts.IsCustomServiceType)
                return items;

            items.Add(facts.RequiresOvenCleaner
                ? "Zep liquids: Green, Floor (or similar), Oven Cleaner (or similar)"
                : "Zep liquids: Green, Floor (or similar)");
            items.Add("Windex liquid (or similar)");
            items.Add("Cleaning cloths, Sponge and Mop");

            return items;
        }

        private static bool Contains(string? name, string needle) =>
            !string.IsNullOrWhiteSpace(name) && name.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
