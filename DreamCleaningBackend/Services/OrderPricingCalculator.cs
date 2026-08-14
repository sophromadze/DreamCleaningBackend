using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for all order price math on the backend.
    ///
    /// Every flow that prices an order (booking create, prepare-payment, admin
    /// create-for-user, user order edit, admin order edit, the /booking/calculate
    /// endpoint) must go through this class. Do not re-implement subtotal, tax,
    /// discount, duration, maids-count, or cleaner-salary math anywhere else.
    ///
    /// This class is mirrored 1:1 by the frontend calculator at
    /// DreamCleaningNG/src/app/shared/pricing/order-pricing.calculator.ts.
    /// The two files use the same function names, the same step order, and the
    /// same rounding (half-up / away-from-zero, matching JS Math.round).
    /// ANY change here must be applied to the frontend mirror in the same commit.
    ///
    /// The canonical algorithm is the booking page's calculateTotal() — when in
    /// doubt about semantics, the booking flow's behavior wins.
    /// </summary>
    public static class OrderPricingCalculator
    {
        // ===== Shared constants (mirror: order-pricing.calculator.ts) =====

        /// <summary>NYC sales tax. The only place this rate may be defined on the backend.</summary>
        public const decimal SalesTaxRate = 0.08875m;

        /// <summary>
        /// LEGACY fallback only. Studio pricing is now admin-editable per service via
        /// Service.ZeroQuantityCost / Service.ZeroQuantityDuration; this constant is used
        /// solely when BOTH of those columns are null, so a missing seed can't zero out a
        /// studio booking. Do not reference it in new code.
        /// </summary>
        [Obsolete("Use Service.ZeroQuantityCost (ServiceLineInput.ZeroQuantityCost). Kept only as a null-seed fallback.")]
        public const decimal StudioPrice = 10m;

        /// <summary>
        /// LEGACY fallback only — see <see cref="StudioPrice"/>. Superseded by
        /// Service.ZeroQuantityDuration.
        /// </summary>
        [Obsolete("Use Service.ZeroQuantityDuration (ServiceLineInput.ZeroQuantityDuration). Kept only as a null-seed fallback.")]
        public const decimal StudioDuration = 20m;

        /// <summary>A single maid can work at most this many hours; above it we add maids.</summary>
        public const decimal MaxHoursPerMaid = 6m;

        /// <summary>
        /// Legacy auto-staffing rule: above MaxHoursPerMaid we used to add cleaners and show
        /// the duration divided per cleaner. Disabled 2026-07 — customers now always see the
        /// full total duration and admins set MaidsCount manually per order (the 1-per-6h math
        /// survives only as an admin-panel suggestion). Flip to true together with
        /// AUTO_ADD_CLEANERS_BY_DURATION in order-pricing.calculator.ts to restore.
        /// static readonly (not const) so gated branches don't fold into unreachable code.
        /// </summary>
        public static readonly bool AutoAddCleanersByDuration = false;

        /// <summary>
        /// Scheduling/billing granularity for durations, in minutes. Durations shown to
        /// customers/admins and the per-cleaner duration used for salary are rounded to this.
        /// Mirrored by DURATION_ROUNDING_MINUTES in order-pricing.calculator.ts / duration.utils.ts.
        /// </summary>
        public const decimal DurationRoundingMinutes = 30m;

        /// <summary>Per-maid minimum duration in minutes.</summary>
        public const decimal PerMaidMinimumMinutes = 60m;

        /// <summary>Per-maid minimum when the Extra Cleaners extra is selected (2h30m floor).</summary>
        public const decimal ExtraCleanersPerMaidMinimumMinutes = 150m;

        /// <summary>
        /// Default cleaner hourly rates. Regular residential is the base; deep/super-deep and
        /// move in/out pay the mid rate; heavy-condition and post-construction pay the top rate.
        /// Mirrored by *_CLEANER_HOURLY_RATE in order-pricing.calculator.ts.
        /// </summary>
        public const decimal RegularCleanerHourlyRate = 20m;
        public const decimal DeepCleaningCleanerHourlyRate = 21m;
        public const decimal HeavyDutyCleanerHourlyRate = 25m;

        /// <summary>The extra service that adds cleaners is identified by name, like the booking page does.</summary>
        public const string ExtraCleanersName = "Extra Cleaners";

        /// <summary>
        /// Round to cents, half away from zero — matches JS Math.round(x * 100) / 100.
        /// Never use bare Math.Round (banker's rounding) in price math.
        /// </summary>
        public static decimal Round2(decimal value) =>
            Math.Round(value, 2, MidpointRounding.AwayFromZero);

        /// <summary>
        /// Splits a TAX-INCLUSIVE amount into its pre-tax subtotal and the sales tax inside it,
        /// such that <c>subTotal + tax == amount</c> EXACTLY, for every amount.
        ///
        /// Used by Custom Pricing: the admin types what the customer should pay in total, so both
        /// halves are derived from it instead of the tax being added on top.
        ///
        /// The returned tax must be carried through as <see cref="TotalsInput.TaxOverride"/> —
        /// re-deriving it as <c>Round2(subTotal × SalesTaxRate)</c> reintroduces a cent of drift on
        /// roughly one amount in twenty, because no cent-valued subtotal exists for those amounts
        /// (nothing satisfies <c>S + Round2(S × 8.875%) == 300.00</c>; 275.55 gives 300.01 and
        /// 275.54 gives 299.99). Deriving the tax from the entered amount instead makes the split
        /// exact — the trade is that the tax is up to half a cent off the literal 8.875% of the
        /// subtotal, which is the normal and expected behaviour of tax-inclusive pricing.
        /// </summary>
        public static (decimal subTotal, decimal tax) SplitTaxInclusiveAmount(decimal amountWithTax)
        {
            var amount = Round2(amountWithTax);
            if (amount <= 0) return (0m, 0m);

            var tax = Round2(amount * SalesTaxRate / (1m + SalesTaxRate));
            return (Round2(amount - tax), tax);
        }

        // ===== Inputs =====

        /// <summary>
        /// One marginal rate band over the BILLABLE quantity (i.e. after the included
        /// allowance has been subtracted), NOT over the raw selected quantity.
        /// </summary>
        public class RateTierInput
        {
            /// <summary>Billable quantity at which this band starts. The lowest band must be 0.</summary>
            public decimal FromQuantity { get; set; }
            public decimal Cost { get; set; }
            public decimal TimeDuration { get; set; }
        }

        /// <summary>
        /// Maps a source service's selected quantity to units of THIS service that are
        /// included at no charge (e.g. bedrooms = 2 → 850 sqft included).
        /// </summary>
        public class ThresholdInput
        {
            public int SourceServiceId { get; set; }
            public int SourceQuantity { get; set; }
            public decimal IncludedQuantity { get; set; }
        }

        /// <summary>One selected service (bedrooms, bathrooms, cleaners, hours, sqft, ...).</summary>
        public class ServiceLineInput
        {
            public int ServiceId { get; set; }
            public decimal Cost { get; set; }
            public decimal TimeDuration { get; set; }
            public string? ServiceRelationType { get; set; }
            public string? ServiceKey { get; set; }
            public int Quantity { get; set; }

            /// <summary>When true, the included allowance is subtracted before billing.</summary>
            public bool ChargeAboveThreshold { get; set; }

            /// <summary>Cost when the selected quantity is 0 (e.g. Studio). Null = not applicable.</summary>
            public decimal? ZeroQuantityCost { get; set; }

            /// <summary>Minutes when the selected quantity is 0 (e.g. Studio). Null = not applicable.</summary>
            public decimal? ZeroQuantityDuration { get; set; }

            /// <summary>Empty = flat <see cref="Cost"/>/<see cref="TimeDuration"/> across the whole billable quantity.</summary>
            public List<RateTierInput> RateTiers { get; set; } = new();

            /// <summary>Empty = no allowance, i.e. bill from zero.</summary>
            public List<ThresholdInput> Thresholds { get; set; } = new();
        }

        /// <summary>One selected extra service.</summary>
        public class ExtraServiceLineInput
        {
            public int ExtraServiceId { get; set; }
            public decimal Price { get; set; }
            public decimal Duration { get; set; }
            public decimal PriceMultiplier { get; set; } = 1m;
            public bool IsDeepCleaning { get; set; }
            public bool IsSuperDeepCleaning { get; set; }
            public bool IsSameDayService { get; set; }
            public bool HasHours { get; set; }
            public bool HasQuantity { get; set; }
            public string? Name { get; set; }
            public int Quantity { get; set; }
            public decimal Hours { get; set; }

            public bool IsExtraCleaners => HasQuantity && Name == ExtraCleanersName;
        }

        public class QuoteInput
        {
            public decimal BasePrice { get; set; }
            public decimal BaseDuration { get; set; }
            public List<ServiceLineInput> Services { get; set; } = new();
            public List<ExtraServiceLineInput> ExtraServices { get; set; } = new();

            /// <summary>
            /// Floor for the base-price + services portion of the subtotal (ServiceType.MinimumPrice).
            /// Extras and the deep-cleaning fee stack ON TOP of the floor. 0 = no floor.
            /// </summary>
            public decimal MinimumPrice { get; set; }

            // Custom pricing (admin-entered amount/cleaners/duration) bypasses the
            // service math entirely; discounts/tax/total still apply normally.
            public bool IsCustomPricing { get; set; }
            /// <summary>TAX-INCLUSIVE total the admin typed; the subtotal is derived from it.</summary>
            public decimal? CustomAmount { get; set; }
            public int? CustomCleaners { get; set; }
            public decimal? CustomDuration { get; set; } // per-cleaner minutes
        }

        // ===== Outputs =====

        /// <summary>Per-line result so callers can persist OrderServices without re-deriving costs.</summary>
        public class ServiceLineResult
        {
            public int ServiceId { get; set; }
            public int Quantity { get; set; }
            public decimal Cost { get; set; }
            public decimal Duration { get; set; }
            /// <summary>False for the hours line of a cleaner-hours pair (it is folded into the cleaner line).</summary>
            public bool ShouldAddToOrder { get; set; } = true;
        }

        public class ExtraServiceLineResult
        {
            public int ExtraServiceId { get; set; }
            public int Quantity { get; set; }
            public decimal Hours { get; set; }
            public decimal Cost { get; set; }
            public decimal Duration { get; set; }
        }

        public class QuoteResult
        {
            /// <summary>Rounded subtotal including the deep-cleaning fee. Pre-discount, pre-tax.</summary>
            public decimal SubTotal { get; set; }
            public decimal PriceMultiplier { get; set; } = 1m;
            public decimal DeepCleaningFee { get; set; }

            /// <summary>
            /// TOTAL cleaner-minutes — what Order.TotalDuration stores. For cleaner-hours
            /// service types this is per-cleaner (hours × 60); for everything else it is
            /// the total work across all maids. Floors applied.
            /// </summary>
            public decimal TotalDuration { get; set; }

            /// <summary>Per-maid duration the UI displays. Floors applied.</summary>
            public decimal DisplayDuration { get; set; }

            public int MaidsCount { get; set; }
            public bool HasCleanerService { get; set; }

            /// <summary>True when <see cref="QuoteInput.MinimumPrice"/> actually raised the subtotal.</summary>
            public bool MinimumPriceApplied { get; set; }

            /// <summary>
            /// Custom Pricing only: the exact sales tax contained in the admin-entered tax-inclusive
            /// amount. Pass it to <see cref="CalculateTotals"/> as <see cref="TotalsInput.TaxOverride"/>
            /// so the charged total matches what was typed to the cent. null for every ordinary quote
            /// (tax is derived from the subtotal).
            /// </summary>
            public decimal? TaxOverride { get; set; }

            /// <summary>
            /// Non-fatal pricing anomalies for the caller to log — currently only the
            /// missing-threshold-source fallback, which should never fire in normal operation.
            /// Never surfaced to customers.
            /// </summary>
            public List<string> Warnings { get; set; } = new();

            public List<ServiceLineResult> ServiceLines { get; set; } = new();
            public List<ExtraServiceLineResult> ExtraServiceLines { get; set; } = new();
        }

        // ===== Step 1: multiplier =====

        /// <summary>
        /// Cleaning-type multiplier: Super Deep wins over Deep wins over regular,
        /// regardless of selection order. The fee is the matching extra's flat price,
        /// added to the subtotal at the END (after service costs).
        /// </summary>
        public static (decimal multiplier, decimal deepCleaningFee) ResolvePriceMultiplier(
            IEnumerable<ExtraServiceLineInput> extraServices)
        {
            var super = extraServices.FirstOrDefault(e => e.IsSuperDeepCleaning);
            var deep = extraServices.FirstOrDefault(e => e.IsDeepCleaning);

            if (super != null) return (super.PriceMultiplier, super.Price);
            if (deep != null) return (deep.PriceMultiplier, deep.Price);
            return (1m, 0m);
        }

        // ===== Step 2: subtotal + duration + maids =====

        /// <summary>
        /// The canonical quote: subtotal, durations, maids count and per-line costs.
        /// Mirrors booking.component.ts calculateTotal() step for step.
        /// </summary>
        public static QuoteResult CalculateQuote(QuoteInput input)
        {
            var result = new QuoteResult();

            if (input.IsCustomPricing)
            {
                var perCleaner = input.CustomDuration ?? input.BaseDuration;
                result.MaidsCount = Math.Max(1, input.CustomCleaners ?? 1);
                // The admin-entered amount is the TAX-INCLUSIVE total: the subtotal and the tax are
                // both split out of it (they add back to it exactly) rather than the tax landing on top.
                var (customSubTotal, customTax) = SplitTaxInclusiveAmount(input.CustomAmount ?? input.BasePrice);
                result.SubTotal = customSubTotal;
                result.TaxOverride = customTax;
                result.DisplayDuration = perCleaner;
                // Stored TotalDuration uses the TOTAL convention: per-cleaner × cleaners, min 1h.
                result.TotalDuration = Math.Max(perCleaner * result.MaidsCount, PerMaidMinimumMinutes);
                result.PriceMultiplier = 1m;
                return result;
            }

            var (priceMultiplier, deepCleaningFee) = ResolvePriceMultiplier(input.ExtraServices);
            result.PriceMultiplier = priceMultiplier;
            result.DeepCleaningFee = deepCleaningFee;

            decimal subTotal = 0;
            decimal totalDuration = 0;
            decimal actualTotalDuration = 0;
            decimal displayDuration = 0;

            var hasCleanerService = input.Services.Any(s => s.ServiceRelationType == "cleaner");
            var hoursService = input.Services.FirstOrDefault(s => s.ServiceRelationType == "hours");
            var useExplicitHours = hasCleanerService && hoursService != null;

            result.HasCleanerService = hasCleanerService;

            // Base price always contributes; base duration only when hours aren't explicit.
            subTotal += input.BasePrice * priceMultiplier;
            if (useExplicitHours)
            {
                actualTotalDuration = hoursService!.Quantity * 60m;
                totalDuration = actualTotalDuration;
            }
            else
            {
                totalDuration += input.BaseDuration;
                actualTotalDuration += input.BaseDuration;
            }

            // Services
            foreach (var service in input.Services)
            {
                var line = new ServiceLineResult { ServiceId = service.ServiceId, Quantity = service.Quantity };

                if (service.ServiceRelationType == "cleaner")
                {
                    if (hoursService != null)
                    {
                        var costPerCleanerPerHour = service.Cost * priceMultiplier;
                        line.Cost = costPerCleanerPerHour * service.Quantity * hoursService.Quantity;
                        line.Duration = hoursService.Quantity * 60m;
                        subTotal += line.Cost;
                    }
                }
                else if (service.ServiceRelationType == "hours")
                {
                    // Folded into the cleaner line above; never priced on its own.
                    // Checked before the zero-quantity branches so an hours line can never
                    // be hijacked by them.
                    line.ShouldAddToOrder = false;
                }
                else if (service.Quantity == 0 &&
                         (service.ZeroQuantityCost.HasValue || service.ZeroQuantityDuration.HasValue))
                {
                    // Generic zero-quantity rule (Studio is just bedrooms = 0). Cost takes the
                    // cleaning-type multiplier; duration does NOT — no duration anywhere in the
                    // quote is multiplier-scaled, and Deep Cleaning contributes its own minutes
                    // through its ExtraService row.
                    line.Cost = (service.ZeroQuantityCost ?? 0m) * priceMultiplier;
                    line.Duration = service.ZeroQuantityDuration ?? 0m;
                    subTotal += line.Cost;
                    if (!useExplicitHours)
                    {
                        totalDuration += line.Duration;
                        actualTotalDuration += line.Duration;
                    }
                }
                else if (service.ServiceKey == "bedrooms" && service.Quantity == 0)
                {
                    // Legacy studio fallback — only reachable when BOTH zero-quantity columns
                    // are null, so a missing seed can't silently price a studio at $0.
#pragma warning disable CS0618 // intentional fallback to the obsolete constants
                    line.Cost = StudioPrice * priceMultiplier;
                    line.Duration = StudioDuration;
#pragma warning restore CS0618
                    subTotal += line.Cost;
                    if (!useExplicitHours)
                    {
                        totalDuration += line.Duration;
                        actualTotalDuration += line.Duration;
                    }
                }
                else
                {
                    var (lineCost, lineDuration) =
                        CalculateTieredLine(service, input.Services, priceMultiplier, result.Warnings);
                    line.Cost = lineCost;
                    line.Duration = lineDuration;
                    subTotal += line.Cost;
                    if (!useExplicitHours)
                    {
                        totalDuration += line.Duration;
                        actualTotalDuration += line.Duration;
                    }
                }

                result.ServiceLines.Add(line);
            }

            // Minimum price floor. Applies to base price + services ONLY, so extras and the
            // deep-cleaning fee stack on top of the floor rather than being absorbed by it.
            if (input.MinimumPrice > 0m && subTotal < input.MinimumPrice)
            {
                subTotal = input.MinimumPrice;
                result.MinimumPriceApplied = true;
            }

            // Extra services
            foreach (var extra in input.ExtraServices)
            {
                var line = new ExtraServiceLineResult
                {
                    ExtraServiceId = extra.ExtraServiceId,
                    Quantity = extra.Quantity,
                    Hours = extra.Hours
                };

                if (extra.IsDeepCleaning || extra.IsSuperDeepCleaning)
                {
                    // The fee is added to the subtotal at the end; the stored line keeps the flat price.
                    line.Cost = extra.Price;
                    line.Duration = extra.Duration;
                    if (!useExplicitHours)
                    {
                        totalDuration += line.Duration;
                        actualTotalDuration += line.Duration;
                    }
                }
                else
                {
                    // Same Day Service is exempt from the cleaning-type multiplier.
                    var currentMultiplier = extra.IsSameDayService ? 1m : priceMultiplier;

                    if (extra.HasHours)
                    {
                        line.Cost = extra.Price * extra.Hours * currentMultiplier;
                        line.Duration = extra.Duration * extra.Hours;
                    }
                    else if (extra.HasQuantity)
                    {
                        line.Cost = extra.Price * extra.Quantity * currentMultiplier;
                        line.Duration = extra.Duration * extra.Quantity;
                    }
                    else
                    {
                        line.Cost = extra.Price * currentMultiplier;
                        line.Duration = extra.Duration;
                    }

                    subTotal += line.Cost;
                    if (!useExplicitHours)
                    {
                        totalDuration += line.Duration;
                        actualTotalDuration += line.Duration;
                    }
                }

                result.ExtraServiceLines.Add(line);
            }

            // Maids count: explicit cleaner quantity, or duration-derived; Extra Cleaners add on top.
            var extraCleanersLine = input.ExtraServices.FirstOrDefault(e => e.IsExtraCleaners);
            var extraCleaners = extraCleanersLine?.Quantity ?? 0;
            var hasExtraCleanersSelected = extraCleanersLine != null;

            int baseMaidsCount = 1;
            if (hasCleanerService)
            {
                var cleanerService = input.Services.FirstOrDefault(s => s.ServiceRelationType == "cleaner");
                if (cleanerService != null)
                    baseMaidsCount = Math.Max(1, cleanerService.Quantity);
                displayDuration = actualTotalDuration;
            }
            else
            {
                var totalHours = totalDuration / 60m;
                baseMaidsCount = AutoAddCleanersByDuration && totalHours > MaxHoursPerMaid
                    ? (int)Math.Ceiling(totalHours / MaxHoursPerMaid)
                    : 1;
                displayDuration = totalDuration;
            }

            var maidsCount = baseMaidsCount + extraCleaners;

            if (AutoAddCleanersByDuration && maidsCount > 1 && !hasCleanerService)
            {
                displayDuration = Math.Ceiling(totalDuration / maidsCount);
            }
            else if (hasCleanerService && maidsCount > baseMaidsCount)
            {
                displayDuration = Math.Ceiling(actualTotalDuration / maidsCount);
            }

            // Per-maid floor: 1h normally, 2h30m when Extra Cleaners is selected.
            var perMaidMinMinutes = hasExtraCleanersSelected
                ? ExtraCleanersPerMaidMinimumMinutes
                : PerMaidMinimumMinutes;
            displayDuration = Math.Max(displayDuration, perMaidMinMinutes);

            // TotalDuration semantics: per-cleaner for cleaner-hours types, total for the rest.
            var totalMinMinutes = hasCleanerService
                ? perMaidMinMinutes
                : perMaidMinMinutes * Math.Max(1, maidsCount);
            actualTotalDuration = Math.Max(actualTotalDuration, totalMinMinutes);

            // Auto-staffing off: customers see the full (floored) total, never a per-maid split.
            if (!AutoAddCleanersByDuration && !hasCleanerService)
                displayDuration = actualTotalDuration;

            // Deep cleaning fee lands AFTER service costs.
            subTotal += deepCleaningFee;

            // With explicit hours the display is simply the hours themselves.
            if (useExplicitHours)
                displayDuration = hoursService!.Quantity * 60m;

            result.SubTotal = Round2(subTotal);
            result.TotalDuration = actualTotalDuration;
            result.DisplayDuration = displayDuration;
            result.MaidsCount = maidsCount;

            return result;
        }

        /// <summary>
        /// Resolves how many units of <paramref name="service"/> are included at no charge,
        /// based on the quantities of its configured source services.
        ///
        /// Lookup is a FLOOR match: the highest configured row whose SourceQuantity is &lt;= the
        /// selected source quantity. A source quantity below every row uses the lowest row; above
        /// every row uses the highest. That subsumes exact-match and handles gaps in the config.
        ///
        /// When several source services are configured, the MAXIMUM included value wins — never
        /// the sum. Summing would let two sources grant more free area than the home has.
        /// </summary>
        private static decimal ResolveIncludedQuantity(
            ServiceLineInput service,
            IReadOnlyList<ServiceLineInput> allServices,
            List<string> warnings)
        {
            if (!service.ChargeAboveThreshold) return 0m;
            if (service.Thresholds == null || service.Thresholds.Count == 0) return 0m;

            decimal included = 0m;

            foreach (var group in service.Thresholds.GroupBy(t => t.SourceServiceId))
            {
                var rows = group.OrderBy(t => t.SourceQuantity).ToList();
                if (rows.Count == 0) continue;

                var source = allServices.FirstOrDefault(s => s.ServiceId == group.Key);
                if (source == null)
                {
                    // Fail toward the customer: treat a missing source as quantity 0, which
                    // resolves to the smallest configured allowance rather than to "no allowance".
                    // Billing a large home from zero would be a severe overcharge.
                    warnings.Add(
                        $"Threshold source service {group.Key} was not present in the selection for " +
                        $"service {service.ServiceId} ('{service.ServiceKey}'); treated its quantity as 0.");
                }

                var sourceQuantity = source?.Quantity ?? 0;
                var match = rows.LastOrDefault(r => r.SourceQuantity <= sourceQuantity) ?? rows[0];
                included = Math.Max(included, match.IncludedQuantity);
            }

            return included;
        }

        /// <summary>
        /// Prices one ordinary service line: subtract the included allowance, then apply the
        /// rate tiers MARGINALLY over what remains (each tier bills only the slice of the
        /// billable quantity that falls inside its own band — never the top tier applied to
        /// everything).
        ///
        /// No tiers configured → flat <see cref="ServiceLineInput.Cost"/> /
        /// <see cref="ServiceLineInput.TimeDuration"/> across the whole billable quantity, which
        /// is exactly the pre-refactor behaviour every other service still relies on.
        ///
        /// Cost takes the cleaning-type multiplier; duration does not.
        /// </summary>
        private static (decimal Cost, decimal Duration) CalculateTieredLine(
            ServiceLineInput service,
            IReadOnlyList<ServiceLineInput> allServices,
            decimal priceMultiplier,
            List<string> warnings)
        {
            var included = ResolveIncludedQuantity(service, allServices, warnings);
            var billable = Math.Max(0m, service.Quantity - included);

            decimal cost = 0m;
            decimal duration = 0m;

            if (service.RateTiers == null || service.RateTiers.Count == 0)
            {
                cost = service.Cost * billable;
                duration = service.TimeDuration * billable;
            }
            else
            {
                var tiers = service.RateTiers.OrderBy(t => t.FromQuantity).ToList();
                for (var i = 0; i < tiers.Count; i++)
                {
                    var from = tiers[i].FromQuantity;
                    if (billable <= from) break;

                    var upperBound = i + 1 < tiers.Count
                        ? Math.Min(billable, tiers[i + 1].FromQuantity)
                        : billable;

                    var width = upperBound - from;
                    if (width <= 0m) continue;

                    cost += width * tiers[i].Cost;
                    duration += width * tiers[i].TimeDuration;
                }
            }

            return (cost * priceMultiplier, duration);
        }

        /// <summary>
        /// Persists the calculator's per-line results onto an order. Every flow that
        /// (re)writes OrderServices / OrderExtraServices must use this so stored line
        /// costs always come from the shared math.
        /// </summary>
        public static void AddOrderLinesFromQuote(Order order, QuoteResult quote)
        {
            order.OrderServices ??= new List<Models.OrderService>();
            order.OrderExtraServices ??= new List<OrderExtraService>();

            foreach (var line in quote.ServiceLines)
            {
                if (!line.ShouldAddToOrder) continue;
                order.OrderServices.Add(new Models.OrderService
                {
                    ServiceId = line.ServiceId,
                    Quantity = line.Quantity,
                    Cost = line.Cost,
                    Duration = line.Duration,
                    PriceMultiplier = quote.PriceMultiplier,
                    CreatedAt = DateTime.UtcNow
                });
            }

            foreach (var line in quote.ExtraServiceLines)
            {
                order.OrderExtraServices.Add(new OrderExtraService
                {
                    ExtraServiceId = line.ExtraServiceId,
                    Quantity = line.Quantity,
                    Hours = line.Hours,
                    Cost = line.Cost,
                    Duration = line.Duration,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        // ===== Step 3: discounts (loyalty stacking) =====

        /// <summary>
        /// Loyalty vs subscription vs promo stacking. Round 1: loyalty vs subscription
        /// (tie → subscription). Round 2: surviving loyalty vs promo (tie → promo).
        /// After stacking, at most two slots are non-zero: either {subscription, promo}
        /// or {loyalty} alone or {subscription} alone or {promo} alone.
        /// </summary>
        public static (decimal loyaltyAmount, decimal loyaltyPercentage, decimal subscriptionAmount, decimal promoAmount)
            ResolveLoyaltyStacking(decimal loyaltyCandidateAmount, decimal loyaltyCandidatePercentage,
                                   decimal subscriptionAmount, decimal promoAmount)
        {
            if (loyaltyCandidateAmount <= 0m || loyaltyCandidatePercentage <= 0m)
                return (0m, 0m, subscriptionAmount, promoAmount);

            decimal loyalty = loyaltyCandidateAmount;
            decimal loyaltyPct = loyaltyCandidatePercentage;

            if (loyalty > subscriptionAmount)
            {
                subscriptionAmount = 0m;
            }
            else
            {
                loyalty = 0m;
                loyaltyPct = 0m;
            }

            if (loyalty > 0m)
            {
                if (loyalty > promoAmount)
                {
                    promoAmount = 0m;
                }
                else
                {
                    loyalty = 0m;
                    loyaltyPct = 0m;
                }
            }

            return (loyalty, loyaltyPct, subscriptionAmount, promoAmount);
        }

        // ===== Step 4: tax + total =====

        public class TotalsInput
        {
            public decimal SubTotal { get; set; }
            public decimal DiscountAmount { get; set; }
            public decimal SubscriptionDiscountAmount { get; set; }
            public decimal LoyaltyDiscountAmount { get; set; }
            public decimal Tips { get; set; }

            /// <summary>
            /// RETIRED — "Tips for Company Development" is no longer offered anywhere. No form,
            /// no input DTO, and no caller may set it from user input; new orders are always 0.
            ///
            /// It survives here only so a LEGACY order that stored a non-zero amount still
            /// recomputes to the total its customer actually paid. Pass the order's STORED value
            /// when re-pricing an existing order, and nothing at all otherwise.
            /// </summary>
            public decimal CompanyDevelopmentTips { get; set; }
            public decimal GiftCardAmountUsed { get; set; }
            public decimal PointsRedeemedDiscount { get; set; }
            public decimal RewardBalanceUsed { get; set; }

            /// <summary>
            /// Custom Pricing only (see <see cref="SplitTaxInclusiveAmount"/>): the exact tax contained
            /// in the tax-inclusive amount the admin typed, used verbatim so the total matches it to
            /// the cent.
            ///
            /// Honoured ONLY while no discount has reduced the subtotal. Once one does, the entered
            /// total no longer describes what is owed, so tax reverts to the normal
            /// <c>Round2(discountedSubTotal × SalesTaxRate)</c>.
            /// </summary>
            public decimal? TaxOverride { get; set; }
        }

        public class TotalsResult
        {
            public decimal DiscountedSubTotal { get; set; }
            public decimal Tax { get; set; }
            /// <summary>discountedSubTotal + tax + tips + companyTips — before gift card / points / credits.</summary>
            public decimal TotalBeforeGiftCard { get; set; }
            /// <summary>Final charge amount, clamped at 0.</summary>
            public decimal Total { get; set; }
        }

        /// <summary>
        /// Tax on the DISCOUNTED subtotal; tips are never taxed; gift card, bubble
        /// points and reward credits come off the very end.
        /// </summary>
        public static TotalsResult CalculateTotals(TotalsInput input)
        {
            var discountedSubTotal = input.SubTotal
                - input.DiscountAmount
                - input.SubscriptionDiscountAmount
                - input.LoyaltyDiscountAmount;
            if (discountedSubTotal < 0m) discountedSubTotal = 0m;

            // The override is only meaningful against the subtotal it was split out of, so any
            // discount hands the tax back to the standard rate math.
            var useOverride = input.TaxOverride.HasValue && discountedSubTotal == Round2(input.SubTotal);

            var tax = useOverride ? Round2(input.TaxOverride!.Value) : Round2(discountedSubTotal * SalesTaxRate);
            var totalBeforeGiftCard = discountedSubTotal + tax + input.Tips + input.CompanyDevelopmentTips;

            var total = totalBeforeGiftCard
                - input.GiftCardAmountUsed
                - input.PointsRedeemedDiscount
                - input.RewardBalanceUsed;
            if (total < 0m) total = 0m;

            return new TotalsResult
            {
                DiscountedSubTotal = discountedSubTotal,
                Tax = tax,
                TotalBeforeGiftCard = totalBeforeGiftCard,
                Total = Round2(total)
            };
        }

        /// <summary>Gift card draw: as much of the pre-gift-card total as the balance covers.</summary>
        public static decimal ResolveGiftCardAmountToUse(decimal giftCardBalance, decimal totalBeforeGiftCard) =>
            Math.Min(giftCardBalance, Math.Max(0m, totalBeforeGiftCard));

        // ===== Step 5: cleaner salary =====

        /// <summary>
        /// Default cleaner hourly rate for an order, matched on the EFFECTIVE service-type name
        /// (i.e. the custom "Pre-Arranged" label when there is one — see GetDisplayServiceTypeName)
        /// and, for residential, on whether the deep-cleaning extra was picked:
        ///   heavy condition / post construction → 25, move in/out → 21,
        ///   residential deep / super-deep → 21, everything else → 20.
        /// The rate is only a DEFAULT — admins can override it per order in the orders panel, and
        /// order edits never reset an overridden value.
        /// Mirrored by getDefaultCleanerHourlyRate in order-pricing.calculator.ts.
        /// </summary>
        public static decimal GetDefaultCleanerHourlyRate(decimal deepCleaningFee, string? serviceTypeName = null)
        {
            var name = NormalizeServiceTypeName(serviceTypeName);

            if (name.Contains("heavy") || name.Contains("post construction"))
                return HeavyDutyCleanerHourlyRate;

            if (name.Contains("move"))
                return DeepCleaningCleanerHourlyRate;

            return deepCleaningFee > 0m ? DeepCleaningCleanerHourlyRate : RegularCleanerHourlyRate;
        }

        /// <summary>Lowercased, hyphen/underscore-flattened service-type name for keyword matching.</summary>
        private static string NormalizeServiceTypeName(string? serviceTypeName)
        {
            if (string.IsNullOrWhiteSpace(serviceTypeName)) return string.Empty;

            var flattened = serviceTypeName.Trim().ToLowerInvariant().Replace('-', ' ').Replace('_', ' ');
            return string.Join(" ", flattened.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>
        /// Billable minutes ONE cleaner is paid for, snapped to DurationRoundingMinutes.
        /// SINGLE source for both the payroll figure and the "· X per cleaner" label admins
        /// see — they must never disagree. Mirrored by calculatePerCleanerBillableMinutes
        /// in order-pricing.calculator.ts.
        ///
        /// Only cleaner-hours service types store TotalDuration as per-cleaner; everything
        /// else (including Custom Pricing) stores it as TOTAL across all maids and we divide.
        ///
        /// The split rounds DOWN, and that is the whole point. Rounding each share to the
        /// NEAREST increment and then multiplying back by the cleaner count inflated payroll
        /// purely because an admin raised the count: a 456-minute job paid 450 min (7h30) at
        /// 1 cleaner but 2 × 240 min (2 × 4h) at 2 cleaners, +$10.50 for identical work.
        /// Flooring makes the paid total a multiple of the increment that is always ≤ the
        /// raw total, so raising MaidsCount can never increase what we pay out.
        /// </summary>
        public static decimal CalculatePerCleanerBillableMinutes(
            decimal totalDuration, int maidsCount, bool hasCleanerService)
        {
            var maids = Math.Max(1, maidsCount);

            // Already per-cleaner (cleaner-hours types), or nothing to split: keep the
            // historical Nearest behaviour so single-cleaner orders are untouched.
            if (hasCleanerService || maids == 1)
                return DurationUtils.RoundToIncrement(
                    totalDuration, DurationRoundingMinutes, DurationRounding.Nearest);

            var floored = DurationUtils.RoundToIncrement(
                totalDuration / maids, DurationRoundingMinutes, DurationRounding.Down);

            // Never floor a real job down to zero pay: with more cleaners than there are
            // half-hours of work (6 cleaners on a 1h job) the share floors to 0. Pay one
            // increment instead. This is the ONE case where the never-raises guarantee can
            // be exceeded, and $0 payroll is the worse failure.
            return totalDuration > 0m && floored <= 0m ? DurationRoundingMinutes : floored;
        }

        /// <summary>
        /// Per-cleaner billable minutes / 60 × maids × rate. See
        /// CalculatePerCleanerBillableMinutes for why the split rounds down.
        /// </summary>
        public static decimal CalculateCleanerTotalSalary(
            decimal totalDuration, int maidsCount, bool hasCleanerService, decimal hourlyRate)
        {
            var maids = Math.Max(1, maidsCount);
            var perCleaner = CalculatePerCleanerBillableMinutes(totalDuration, maidsCount, hasCleanerService);
            return Round2(perCleaner / 60m * maids * hourlyRate);
        }
    }
}
