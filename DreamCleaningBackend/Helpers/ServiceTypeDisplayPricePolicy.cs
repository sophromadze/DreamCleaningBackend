namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// The rules for <c>ServiceType.DisplayPrice</c> / <c>DisplayPriceUnit</c>, stated once for
    /// every writer (the Booking Services create/update endpoints and the pricing-configuration
    /// import).
    ///
    /// <para>
    /// A display price is MARKETING COPY ONLY, for a service type whose price the booking
    /// calculator cannot produce - today Filthy Cleaning, which is inspected (poll) and priced by
    /// hand. It never enters a quote. The public site and /llms.txt read it only for such types;
    /// when it is empty they say "priced after assessment" instead of a number.
    /// </para>
    ///
    /// <para>
    /// Amount and unit are set together or not at all: "$100" without a unit would be read as a
    /// flat price. The unit is one of a fixed list so the site can word it ("$100 per hour per
    /// cleaner", "from $100") without guessing.
    /// </para>
    /// </summary>
    public static class ServiceTypeDisplayPricePolicy
    {
        public const int UnitMaxLength = 30;

        public const string PerHourPerCleaner = "per-hour-per-cleaner";
        public const string PerHour = "per-hour";
        public const string From = "from";

        public static readonly IReadOnlyList<string> Units = new[] { PerHourPerCleaner, PerHour, From };

        /// <summary>
        /// Normalizes a requested pair. Returns the values to store (both null = no display price)
        /// or a message for the admin.
        /// </summary>
        public static (decimal? Amount, string? Unit, string? Error) Resolve(decimal? amount, string? unit)
        {
            var normalizedUnit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();

            if (amount == null && normalizedUnit == null)
                return (null, null, null);

            if (amount == null)
                return (null, null, "Display price needs an amount when a unit is chosen.");

            if (normalizedUnit == null)
                return (null, null, "Display price needs a unit (per hour per cleaner, per hour, or from).");

            if (amount <= 0m)
                return (null, null, "Display price must be greater than 0.");

            if (decimal.Round(amount.Value, 2) != amount.Value)
                return (null, null, "Display price can have at most 2 decimal places.");

            if (!Units.Contains(normalizedUnit))
                return (null, null, $"Display price unit must be one of: {string.Join(", ", Units)}.");

            return (amount, normalizedUnit, null);
        }
    }
}
