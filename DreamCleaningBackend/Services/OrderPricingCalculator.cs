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

        /// <summary>Flat price for a studio (bedrooms quantity = 0), before the cleaning-type multiplier.</summary>
        public const decimal StudioPrice = 10m;

        /// <summary>Base duration in minutes for a studio, before the cleaning-type multiplier.</summary>
        public const decimal StudioDuration = 20m;

        /// <summary>A single maid can work at most this many hours; above it we add maids.</summary>
        public const decimal MaxHoursPerMaid = 6m;

        /// <summary>Per-maid minimum duration in minutes.</summary>
        public const decimal PerMaidMinimumMinutes = 60m;

        /// <summary>Per-maid minimum when the Extra Cleaners extra is selected (2h30m floor).</summary>
        public const decimal ExtraCleanersPerMaidMinimumMinutes = 150m;

        /// <summary>Default cleaner hourly rates: regular vs deep/super-deep orders.</summary>
        public const decimal RegularCleanerHourlyRate = 20m;
        public const decimal DeepCleaningCleanerHourlyRate = 21m;

        /// <summary>The extra service that adds cleaners is identified by name, like the booking page does.</summary>
        public const string ExtraCleanersName = "Extra Cleaners";

        /// <summary>
        /// Round to cents, half away from zero — matches JS Math.round(x * 100) / 100.
        /// Never use bare Math.Round (banker's rounding) in price math.
        /// </summary>
        public static decimal Round2(decimal value) =>
            Math.Round(value, 2, MidpointRounding.AwayFromZero);

        // ===== Inputs =====

        /// <summary>One selected service (bedrooms, bathrooms, cleaners, hours, sqft, ...).</summary>
        public class ServiceLineInput
        {
            public int ServiceId { get; set; }
            public decimal Cost { get; set; }
            public decimal TimeDuration { get; set; }
            public string? ServiceRelationType { get; set; }
            public string? ServiceKey { get; set; }
            public int Quantity { get; set; }
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

            // Custom pricing (admin-entered amount/cleaners/duration) bypasses the
            // service math entirely; discounts/tax/total still apply normally.
            public bool IsCustomPricing { get; set; }
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
                result.SubTotal = Round2(input.CustomAmount ?? input.BasePrice);
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
                else if (service.ServiceKey == "bedrooms" && service.Quantity == 0)
                {
                    // Studio: flat price and duration, both scaled by cleaning type.
                    line.Cost = StudioPrice * priceMultiplier;
                    line.Duration = Math.Round(StudioDuration * priceMultiplier, MidpointRounding.AwayFromZero);
                    subTotal += line.Cost;
                    if (!useExplicitHours)
                    {
                        totalDuration += line.Duration;
                        actualTotalDuration += line.Duration;
                    }
                }
                else if (service.ServiceRelationType == "hours")
                {
                    // Folded into the cleaner line above; never priced on its own.
                    line.ShouldAddToOrder = false;
                }
                else
                {
                    line.Cost = service.Cost * service.Quantity * priceMultiplier;
                    line.Duration = service.TimeDuration * service.Quantity;
                    subTotal += line.Cost;
                    if (!useExplicitHours)
                    {
                        totalDuration += line.Duration;
                        actualTotalDuration += line.Duration;
                    }
                }

                result.ServiceLines.Add(line);
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
                baseMaidsCount = totalHours <= MaxHoursPerMaid
                    ? 1
                    : (int)Math.Ceiling(totalHours / MaxHoursPerMaid);
                displayDuration = totalDuration;
            }

            var maidsCount = baseMaidsCount + extraCleaners;

            if (maidsCount > 1 && !hasCleanerService)
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
            public decimal CompanyDevelopmentTips { get; set; }
            public decimal GiftCardAmountUsed { get; set; }
            public decimal PointsRedeemedDiscount { get; set; }
            public decimal RewardBalanceUsed { get; set; }
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

            var tax = Round2(discountedSubTotal * SalesTaxRate);
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

        /// <summary>Deep/super-deep orders pay cleaners the higher rate.</summary>
        public static decimal GetDefaultCleanerHourlyRate(decimal deepCleaningFee) =>
            deepCleaningFee > 0m ? DeepCleaningCleanerHourlyRate : RegularCleanerHourlyRate;

        /// <summary>
        /// Per-cleaner duration rounded to 15 minutes, then perCleaner/60 × maids × rate.
        /// Only cleaner-hours service types store TotalDuration as per-cleaner; everything
        /// else (including Custom Pricing) stores it as TOTAL across all maids and we divide.
        /// </summary>
        public static decimal CalculateCleanerTotalSalary(
            decimal totalDuration, int maidsCount, bool hasCleanerService, decimal hourlyRate)
        {
            var maids = Math.Max(1, maidsCount);
            var perCleanerDuration = hasCleanerService
                ? totalDuration
                : (maids > 1 ? totalDuration / maids : totalDuration);
            var roundedPerCleaner = Math.Round(perCleanerDuration / 15m, MidpointRounding.AwayFromZero) * 15m;
            return Round2(roundedPerCleaner / 60m * maids * hourlyRate);
        }
    }
}
