namespace DreamCleaningBackend.Helpers
{
    /// <summary>How a duration is snapped to a scheduling increment.</summary>
    public enum DurationRounding
    {
        /// <summary>Nearest increment, halves away from zero (75 → 90, never 60).</summary>
        Nearest,

        /// <summary>Always up. Used where we must not under-promise time to a customer.</summary>
        Up
    }

    /// <summary>
    /// SINGLE implementation of duration→scheduling-increment rounding on the backend.
    /// Mirrored by DurationUtils in DreamCleaningNG/src/app/utils/duration.utils.ts.
    ///
    /// The mode is explicit at every call site on purpose — the two behaviours are a
    /// deliberate product decision, not an accident:
    ///   - ChatCatalogService  → Up      (the AI agent must never quote less time than we expect)
    ///   - EmailService        → Nearest (matches what the customer saw on the booking page)
    ///   - Cleaner salary      → Nearest (matches the displayed duration cleaners are paid for)
    ///
    /// Always operates on decimal with MidpointRounding.AwayFromZero, matching
    /// OrderPricingCalculator.Round2 and JS Math.round. The previous inline version in
    /// EmailService used Math.Round on a double, which is BANKER'S rounding: exactly
    /// 75 minutes snapped to 60 in emails while the booking page showed 90.
    /// </summary>
    public static class DurationUtils
    {
        public static decimal RoundToIncrement(decimal minutes, decimal increment, DurationRounding mode)
        {
            if (increment <= 0m) return minutes;

            var steps = minutes / increment;
            var rounded = mode == DurationRounding.Up
                ? Math.Ceiling(steps)
                : Math.Round(steps, MidpointRounding.AwayFromZero);

            return rounded * increment;
        }
    }
}
