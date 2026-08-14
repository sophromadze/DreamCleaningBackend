namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// The "please provide the following items" checklist shown to the customer, and the
    /// extra-service name matching it is derived from. Single source for the confirmation
    /// email and SMS (mirrored on the frontend in
    /// <c>src/app/shared/booking/supply-checklist.utils.ts</c> for the booking modal,
    /// booking-success, order-details and order-payment pages) — the two must stay in sync,
    /// or the customer is told to buy a different set of products depending on the surface.
    /// </summary>
    public static class CustomerSupplyChecklist
    {
        /// <summary>Items the customer always provides, even with the Cleaning Supplies extra.</summary>
        private static readonly string[] AlwaysRequiredItems =
        {
            "Paper towels",
            "Garbage bags",
            "Broom or vacuum cleaner",
            "Toilet brush"
        };

        public static bool HasCleaningSuppliesExtra(IEnumerable<string?> extraServiceNames) =>
            extraServiceNames.Any(n => Contains(n, "cleaning supplies"));

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

        /// <summary>
        /// The checklist itself. With the Cleaning Supplies extra (or a custom service type,
        /// which doesn't use the supplies workflow at all) the customer only provides the
        /// always-required essentials; otherwise they also need the products we would have brought.
        /// </summary>
        public static List<string> BuildItems(bool hasCleaningSupplies, bool requiresOvenCleaner, bool isCustomServiceType)
        {
            var items = new List<string>(AlwaysRequiredItems);

            if (hasCleaningSupplies || isCustomServiceType)
                return items;

            items.Add(requiresOvenCleaner
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
