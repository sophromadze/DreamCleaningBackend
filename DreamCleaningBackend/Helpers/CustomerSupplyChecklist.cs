using DreamCleaningBackend.Models;

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

        /// <summary>Customer bought "Cleaning Essentials" - WE bring paper towels, garbage bags,
        /// a toilet brush and a broom.</summary>
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
    ///   "Cleaning Essentials" -> paper towels, garbage bags, toilet brush AND A BROOM. The broom
    ///                            was added 2026-09; before that the customer was always asked for
    ///                            a broom or vacuum, so anything still saying "never included" is
    ///                            stale.
    ///   "Vacuum Cleaner"      -> the broom-or-vacuum line, and only that line.
    /// Because Essentials now covers the broom, that line comes off for EITHER of the last two -
    /// we bring a broom, or we bring a vacuum, and the customer was only ever asked for one of
    /// the pair.
    ///
    /// Supplies + Essentials together therefore leave NOTHING, which is why BuildItems can
    /// legitimately return an EMPTY list and every surface has to render that case as "nothing to
    /// prepare" rather than as an empty bulleted box.
    /// </summary>
    public static class CustomerSupplyChecklist
    {
        /// <summary>
        /// LEGACY name fragments (contains, case-insensitive). The extras are recognised by their
        /// ExtraServiceKey ("cleaning-supplies", "cleaning-essentials", "vacuum-cleaner", "oven" -
        /// see ExtraServiceKeys); these fragments only decide for an UNKEYED row.
        /// </summary>
        public const string CleaningSuppliesMatch = "cleaning supplies";
        public const string CleaningEssentialsMatch = "cleaning essentials";
        public const string VacuumMatch = "vacuum";

        // THE ITEMS THEMSELVES, as translation KEYS rather than as English text.
        //
        // A cleaner is told the supplies and essentials lines in their own language, on three
        // surfaces (the assignment email, the assignment SMS and the portal), and each of those
        // surfaces owns its own dictionary. What must NOT vary between them is WHICH items are
        // named and in what order - a cleaner reading "Mop" in the mail and not on the page has no
        // way to tell which of the two is out of date. So the list is resolved once, here, as
        // keys, and the surfaces only translate them.
        //
        // Keys rather than text is also what lets the portal ship the resolved list straight from
        // the server: there is no mirrored frontend copy of this rule to drift.
        public const string ItemZep = "zep";
        public const string ItemZepWithOven = "zepOven";
        public const string ItemWindex = "windex";
        public const string ItemCloths = "cloths";
        public const string ItemSponge = "sponge";
        public const string ItemMop = "mop";
        public const string ItemPaperTowels = "paperTowels";
        public const string ItemGarbageBags = "garbageBags";
        public const string ItemToiletBrush = "toiletBrush";
        public const string ItemBroom = "broom";
        public const string ItemBroomOrVacuum = "broomOrVacuum";

        /// <summary>
        /// The products group, item by item: the Zep liquids, the Windex, the cloths, the sponge
        /// and the mop.
        ///
        /// IT IS THE SAME LIST WHOEVER IS CARRYING IT. With the "Cleaning Supplies" extra WE bring
        /// these; without it the customer has them ready - which is exactly the block
        /// <see cref="BuildItems"/> puts on the customer's own checklist, written one per line
        /// here instead of grouped. That is deliberate: a cleaner has to be told what is expected
        /// to be waiting in the house just as precisely as what is expected in the car, and
        /// reading both off one resolver is what stops the two answers contradicting each other.
        ///
        /// The oven liquid follows <see cref="RequiresOvenCleaner"/> - a Deep / Super Deep
        /// cleaning, or the Oven Cleaning extra on its own - the same condition that puts it on
        /// the customer's list.
        /// </summary>
        public static List<string> SuppliesItemKeys(bool requiresOvenCleaner) => new()
        {
            requiresOvenCleaner ? ItemZepWithOven : ItemZep,
            ItemWindex,
            ItemCloths,
            ItemSponge,
            ItemMop
        };

        /// <summary>
        /// The essentials group, item by item - and the one place the two directions genuinely
        /// differ.
        ///
        /// When WE bring the essentials the fourth item is a BROOM, because that is what the
        /// "Cleaning Essentials" extra buys. When the CUSTOMER is providing them the fourth item
        /// is "broom or vacuum cleaner", and it disappears entirely if they bought the Vacuum
        /// Cleaner extra - in that case we are bringing a vacuum and they were never asked for a
        /// broom. Both halves mirror <see cref="BuildItems"/> exactly; telling a cleaner to expect
        /// a broom nobody asked the customer for is how a crew arrives without one.
        /// </summary>
        public static List<string> EssentialsItemKeys(bool weBringEssentials, bool weBringVacuum)
        {
            var keys = new List<string> { ItemPaperTowels, ItemGarbageBags, ItemToiletBrush };
            if (weBringEssentials)
                keys.Add(ItemBroom);
            else if (!weBringVacuum)
                keys.Add(ItemBroomOrVacuum);
            return keys;
        }

        /// <summary>The line the Vacuum Cleaner extra buys the customer out of.</summary>
        private const string BroomOrVacuumItem = "Broom or vacuum cleaner";

        /// <summary>Unkeyed stand-ins for callers (and specs) that only hold names: the legacy name rules.</summary>
        private static IEnumerable<ExtraService> ByName(IEnumerable<string?> extraServiceNames) =>
            extraServiceNames.Select(n => new ExtraService { Name = n ?? string.Empty });

        public static bool IsCleaningSupplies(ExtraService? extra) =>
            ExtraServiceKeys.Is(extra, ExtraServiceKeys.CleaningSupplies, ExtraServiceKeys.LegacyCleaningSupplies);

        public static bool IsCleaningEssentials(ExtraService? extra) =>
            ExtraServiceKeys.Is(extra, ExtraServiceKeys.CleaningEssentials, ExtraServiceKeys.LegacyCleaningEssentials);

        public static bool IsVacuum(ExtraService? extra) =>
            ExtraServiceKeys.Is(extra, ExtraServiceKeys.VacuumCleaner, ExtraServiceKeys.LegacyVacuum);

        public static bool IsOven(ExtraService? extra) =>
            ExtraServiceKeys.Is(extra, ExtraServiceKeys.Oven, ExtraServiceKeys.LegacyOven);

        public static bool HasCleaningSuppliesExtra(IEnumerable<ExtraService?> extras) => extras.Any(IsCleaningSupplies);

        public static bool HasCleaningEssentialsExtra(IEnumerable<ExtraService?> extras) => extras.Any(IsCleaningEssentials);

        public static bool HasVacuumExtra(IEnumerable<ExtraService?> extras) => extras.Any(IsVacuum);

        /// <summary>Deep / Super Deep (their flags) or the Oven extra - see the name overload below.</summary>
        public static bool RequiresOvenCleaner(IEnumerable<ExtraService?> extras)
        {
            var list = extras.ToList();
            return list.Any(ExtraServiceKeys.IsDeepOrSuperDeep) || list.Any(IsOven);
        }

        /// <summary>Reads every checklist-relevant fact off the order's extras in one pass.</summary>
        public static SupplyChecklistFacts Resolve(IEnumerable<ExtraService?> extras, bool isCustomServiceType)
        {
            var list = extras.ToList();
            return new SupplyChecklistFacts
            {
                HasCleaningSupplies = HasCleaningSuppliesExtra(list),
                HasCleaningEssentials = HasCleaningEssentialsExtra(list),
                WeBringVacuum = HasVacuumExtra(list),
                RequiresOvenCleaner = RequiresOvenCleaner(list),
                IsCustomServiceType = isCustomServiceType
            };
        }

        public static bool HasCleaningSuppliesExtra(IEnumerable<string?> extraServiceNames) =>
            HasCleaningSuppliesExtra(ByName(extraServiceNames));

        /// <summary>
        /// True when the customer bought the "Cleaning Essentials" extra. Note this does NOT
        /// match "Cleaning Supplies" and vice versa - the two are separate purchases that can
        /// be held together, and each removes a different part of the checklist.
        /// </summary>
        public static bool HasCleaningEssentialsExtra(IEnumerable<string?> extraServiceNames) =>
            HasCleaningEssentialsExtra(ByName(extraServiceNames));

        /// <summary>True when we bring a vacuum, so the customer is not asked for one.</summary>
        public static bool HasVacuumExtra(IEnumerable<string?> extraServiceNames) =>
            HasVacuumExtra(ByName(extraServiceNames));

        /// <summary>
        /// True when the cleaners need an oven-cleaning liquid on site: a Deep / Super Deep
        /// Cleaning booking, OR the Oven Cleaning extra on its own. The oven extra used to be
        /// missed here, so a customer who ordered oven cleaning without deep cleaning was never
        /// told to have Oven Cleaner ready.
        /// </summary>
        public static bool RequiresOvenCleaner(IEnumerable<string?> extraServiceNames) =>
            RequiresOvenCleaner(ByName(extraServiceNames));

        /// <summary>Name-only form, for callers holding names: every name follows the legacy (unkeyed) rules.</summary>
        public static SupplyChecklistFacts Resolve(IEnumerable<string?> extraServiceNames, bool isCustomServiceType) =>
            Resolve(ByName(extraServiceNames), isCustomServiceType);

        /// <summary>The order's catalogue extras, through each line's ExtraService link (must be loaded).</summary>
        public static IEnumerable<ExtraService?> ExtrasOf(Order order) =>
            (order.OrderExtraServices ?? new List<OrderExtraService>()).Select(oes => oes.ExtraService);

        /// <summary>Reads the facts straight off an order whose OrderExtraServices are loaded.</summary>
        public static SupplyChecklistFacts Resolve(Order order) =>
            Resolve(ExtrasOf(order), order.ServiceType?.IsCustom == true);

        /// <summary>
        /// The checklist itself - what the CUSTOMER has to have on site. The combinations read:
        ///   nothing bought        -> everything;
        ///   Cleaning Supplies     -> paper towels, garbage bags, broom/vacuum, toilet brush;
        ///   Cleaning Essentials   -> only the products we would have brought (it covers the
        ///                            broom as well now, so nothing from this group survives);
        ///   Supplies + Essentials -> NOTHING AT ALL;
        ///   Vacuum Cleaner        -> drops the broom/vacuum line on its own.
        /// A custom ("Pre-Arranged") service type does not use the supplies workflow, so it never
        /// gets the products block regardless.
        /// </summary>
        public static List<string> BuildItems(SupplyChecklistFacts facts)
        {
            var items = new List<string>();

            // Cleaning Essentials covers this whole group, broom included.
            if (!facts.HasCleaningEssentials)
            {
                items.Add("Paper towels");
                items.Add("Garbage bags");
                // ...unless we are bringing a vacuum instead, which answers the same need.
                if (!facts.WeBringVacuum)
                    items.Add(BroomOrVacuumItem);
                items.Add("Toilet brush");
            }

            if (facts.HasCleaningSupplies || facts.IsCustomServiceType)
                return items;

            items.Add(facts.RequiresOvenCleaner
                ? "Zep liquids: Green, Floor (or similar), Oven Cleaner (or similar)"
                : "Zep liquids: Green, Floor (or similar)");
            items.Add("Windex liquid (or similar)");
            items.Add("Cleaning cloths, Sponge and Mop");

            return items;
        }
    }
}
