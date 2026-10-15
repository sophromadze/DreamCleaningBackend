namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Save-time rules for the admin Booking Services screen (services and extra services), stated
    /// once for the create / update endpoints. Each Validate* returns null when the input is fine,
    /// otherwise the message the admin sees.
    ///
    /// <para>
    /// <b>Customer control (a service's InputType).</b> The booking surfaces offer exactly two: a
    /// +/- stepper, stored as <see cref="Stepper"/> ("dropdown" — the historical name, kept so no
    /// row has to be migrated), and the Sq.ft range slider, stored as <see cref="Slider"/> with
    /// IsRangeInput = true. The two columns used to be set independently, and the combinations
    /// booking could not draw — "slider" without IsRangeInput, the never-implemented "number" —
    /// left the customer with no control at all, so <see cref="NormalizeInputType"/> derives one
    /// from the other. The slider is the square-feet slider (its options start at the
    /// bedroom-driven minimum and it reads "sq ft"), so it is only accepted on the
    /// <c>sqft</c> service.
    /// </para>
    /// </summary>
    public static class CatalogInputPolicy
    {
        public const string Stepper = "dropdown";
        public const string Slider = "slider";
        public const string SliderServiceKey = "sqft";

        /// <summary>Upper bound for either cleaning-type multiplier: a typo guard, not a business rule.</summary>
        public const decimal MaxMultiplier = 10m;

        /// <summary>
        /// The stored (InputType, IsRangeInput) pair for what the admin picked. Either field saying
        /// "slider" means the slider; anything else (incl. legacy "number") is the stepper.
        /// </summary>
        public static (string InputType, bool IsRangeInput) NormalizeInputType(string? inputType, bool isRangeInput)
        {
            var slider = isRangeInput || string.Equals(inputType?.Trim(), Slider, StringComparison.OrdinalIgnoreCase);
            return slider ? (Slider, true) : (Stepper, false);
        }

        /// <summary>"" and whitespace mean a regular service and are stored as null.</summary>
        public static string? NormalizeRelationType(string? relationType)
        {
            var trimmed = relationType?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
        }

        public static string? ValidateService(
            string? name, string? serviceKey, string inputType, string? relationType,
            int? minValue, int? maxValue, int? stepValue, decimal cost, decimal timeDuration)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Enter a name for the service.";
            if (string.IsNullOrWhiteSpace(serviceKey)) return "Enter a service key (for example bedrooms).";
            if (cost < 0) return "Cost cannot be negative.";
            if (timeDuration < 0) return "Duration cannot be negative.";
            if (relationType != null && relationType != "cleaner" && relationType != "hours")
                return "Service role must be Regular, Cleaners or Hours for cleaners.";
            if (minValue < 0) return "Minimum cannot be negative.";
            if (minValue.HasValue && maxValue.HasValue && minValue > maxValue)
                return $"Minimum ({minValue}) cannot be greater than maximum ({maxValue}).";
            if (stepValue.HasValue && stepValue < 1) return "Step must be at least 1.";
            if (inputType == Slider && !string.Equals(serviceKey.Trim(), SliderServiceKey, StringComparison.OrdinalIgnoreCase))
                return "The slider is only available for the Sq.ft service (key \"sqft\"). Use the stepper for this service.";
            return null;
        }

        public static string? ValidateExtraService(
            string? name, decimal price, decimal duration, bool hasQuantity, bool hasHours,
            bool isDeepCleaning, bool isSuperDeepCleaning, bool isSameDayService,
            decimal priceMultiplier, decimal timeMultiplier)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Enter a name for the extra service.";
            if (price < 0) return "Price cannot be negative.";
            if (duration < 0) return "Duration cannot be negative.";
            if (hasQuantity && hasHours) return "Choose either \"Customer picks a quantity\" or \"Customer picks hours\", not both.";
            if (isDeepCleaning && isSuperDeepCleaning) return "An extra can be Deep Cleaning or Super Deep Cleaning, not both.";
            if ((isDeepCleaning || isSuperDeepCleaning) && isSameDayService)
                return "A deep cleaning type cannot also be the Same Day Service.";
            if (isDeepCleaning || isSuperDeepCleaning)
            {
                if (priceMultiplier <= 0 || priceMultiplier > MaxMultiplier)
                    return $"Price multiplier must be greater than 0 and at most {MaxMultiplier:0}.";
                if (timeMultiplier <= 0 || timeMultiplier > MaxMultiplier)
                    return $"Time multiplier must be greater than 0 and at most {MaxMultiplier:0}.";
            }
            return null;
        }

        /// <summary>
        /// The multipliers are only read on a Deep / Super Deep extra (OrderPricingCalculator), so any
        /// other extra stores the neutral 1.0 — a stray 1.5 on "Oven Cleaning" would read as though it
        /// did something.
        /// </summary>
        public static (decimal Price, decimal Time) NormalizeMultipliers(
            bool isDeepCleaning, bool isSuperDeepCleaning, decimal priceMultiplier, decimal timeMultiplier)
            => isDeepCleaning || isSuperDeepCleaning ? (priceMultiplier, timeMultiplier) : (1m, 1m);
    }
}
