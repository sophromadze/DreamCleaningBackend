using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Builds OrderPricingCalculator inputs from request DTOs by resolving the
    /// catalog rows from the database. The single place that knows how booking
    /// and order-edit DTOs map to the shared calculator's input shape — used by
    /// BookingController (create / create-for-user / prepare-payment / calculate)
    /// and OrderService (update / additional-amount).
    /// </summary>
    public static class OrderPricingInputBuilder
    {
        /// <summary>
        /// THE custom-pricing gate — the only definition of when Custom ("Pre-Arranged") pricing
        /// is honoured.
        ///
        /// Custom pricing bypasses the ENTIRE service catalogue: the calculator's custom branch
        /// returns before the services loop, the rate tiers and the MinimumPrice floor, so the
        /// amount the customer is charged is simply the amount the caller typed. That is only
        /// acceptable for an admin arranging a bespoke job, so it is honoured when BOTH hold:
        ///
        ///   a) the selected service type really IS the custom one (serviceType.IsCustom), AND
        ///   b) the caller is an Admin/SuperAdmin.
        ///
        /// (b) arrives as <paramref name="allowCustomPricing"/>, decided by the caller. This class
        /// deliberately never reads HttpContext — QuoteInput carries no identity, and a pricing
        /// helper that authenticates its own caller is a helper nobody can reason about.
        ///
        /// Before this gate existed, only the raw dto.IsCustomPricing flag was consulted, on every
        /// booking path including the anonymous one — so any caller could set customAmount and name
        /// their own price against an ordinary Residential booking.
        /// </summary>
        public static bool ShouldHonourCustomPricing(
            ServiceType serviceType, CreateBookingDto dto, bool allowCustomPricing)
            => dto.IsCustomPricing && allowCustomPricing && serviceType.IsCustom;

        /// <summary>
        /// Applies <see cref="ShouldHonourCustomPricing"/> to the DTO ITSELF, clearing the custom
        /// fields when the gate refuses. Call this ONCE, at the point the decision is made, and
        /// BEFORE the DTO is either priced or stored in the booking-data session.
        ///
        /// Rewriting the DTO — rather than only filtering at pricing time — is what makes the
        /// charged amount and the persisted amount provably the same decision. prepare-payment
        /// computes the Stripe charge and then parks the DTO in BookingDataService; confirm-payment
        /// later reads that same DTO back and builds the real order from it, re-pricing
        /// independently. If the two re-derived the decision separately they could disagree, and
        /// the customer would be charged one amount while the order recorded another. Normalising
        /// once means every downstream reader sees a DTO that already IS the decision. (Same
        /// pattern PreparePayment already uses for the verified gift-card draw.)
        ///
        /// CustomServiceDisplayName is deliberately left alone: it is a label, not money, and it
        /// is already gated on serviceType.IsCustom where it is consumed.
        /// </summary>
        /// <param name="attemptedAmount">
        /// The refused CustomAmount, for the caller's warning log; null when nothing was refused.
        /// </param>
        /// <returns>True when a custom-pricing request was refused — the caller should log it.</returns>
        public static bool NormalizeCustomPricing(
            ServiceType serviceType, CreateBookingDto dto, bool allowCustomPricing, out decimal? attemptedAmount)
        {
            attemptedAmount = null;

            if (!dto.IsCustomPricing) return false;
            if (ShouldHonourCustomPricing(serviceType, dto, allowCustomPricing)) return false;

            attemptedAmount = dto.CustomAmount;
            dto.IsCustomPricing = false;
            dto.CustomAmount = null;
            dto.CustomCleaners = null;
            dto.CustomDuration = null;
            return true;
        }

        /// <summary>Input for a booking-style DTO (create, prepare-payment, calculate).</summary>
        /// <param name="allowCustomPricing">
        /// Half (b) of the custom-pricing gate — see <see cref="ShouldHonourCustomPricing"/>.
        /// Intentionally has NO default: every call site must state its decision, because a path
        /// that silently inherited "allowed" is exactly how this became reachable anonymously.
        /// </param>
        public static async Task<OrderPricingCalculator.QuoteInput> FromBookingDtoAsync(
            ApplicationDbContext context, ServiceType serviceType, CreateBookingDto dto, bool allowCustomPricing)
        {
            // Re-applied here as well as in NormalizeCustomPricing: this is the last point before
            // the calculator, so a path that forgets to normalise still cannot mis-price.
            var honourCustom = ShouldHonourCustomPricing(serviceType, dto, allowCustomPricing);

            var input = new OrderPricingCalculator.QuoteInput
            {
                BasePrice = serviceType.BasePrice,
                BaseDuration = serviceType.TimeDuration,
                MinimumPrice = serviceType.MinimumPrice,
                IsCustomPricing = honourCustom,
                CustomAmount = honourCustom ? dto.CustomAmount : null,
                CustomCleaners = honourCustom
                    ? (dto.CustomCleaners ?? (dto.MaidsCount > 0 ? dto.MaidsCount : (int?)null))
                    : null,
                CustomDuration = honourCustom ? dto.CustomDuration : null
            };

            await AddServiceLinesAsync(context, input, dto.Services);
            await AddExtraServiceLinesAsync(context, input, dto.ExtraServices);

            // Order matters. Levels is settled first (an apartment cannot carry a stair charge),
            // then the house bedroom floor, and only then the sq.ft floor - because raising
            // bedrooms 0 -> 1 raises the included sq.ft with it.
            ClampLevelsToPropertyType(input, dto.PropertyType);
            ClampBedroomsToPropertyType(input, dto.PropertyType);
            ClampSquareFeetToBedrooms(input);

            return input;
        }

        /// <summary>
        /// Input for an order-edit DTO. The one edit-specific wrinkle is the
        /// original-hours fallback: if the update has a cleaner service but no hours
        /// service, the original order's hours keep the cleaner line priced
        /// (defensive — the frontend always sends both together).
        /// </summary>
        public static async Task<OrderPricingCalculator.QuoteInput> FromUpdateDtoAsync(
            ApplicationDbContext context, Order order, UpdateOrderDto dto)
        {
            // The ServiceType navigation is not guaranteed to be loaded on every call path, and
            // MinimumPrice must not silently fall back to 0 — that would drop the price floor on
            // every order edit while the booking page still applies it.
            var serviceType = order.ServiceType;
            if (serviceType == null && order.ServiceTypeId > 0)
                serviceType = await context.ServiceTypes.FindAsync(order.ServiceTypeId);

            var input = new OrderPricingCalculator.QuoteInput
            {
                BasePrice = serviceType?.BasePrice ?? 0,
                BaseDuration = serviceType?.TimeDuration ?? 0,
                MinimumPrice = serviceType?.MinimumPrice ?? 0
            };

            await AddServiceLinesAsync(context, input, dto.Services);
            await AddExtraServiceLinesAsync(context, input, dto.ExtraServices);

            // Same order as the booking path - see the comment in FromBookingDtoAsync.
            ClampLevelsToPropertyType(input, dto.PropertyType);
            ClampBedroomsToPropertyType(input, dto.PropertyType);
            ClampSquareFeetToBedrooms(input);

            var hasCleaner = input.Services.Any(s => s.ServiceRelationType == "cleaner");
            var hasHours = input.Services.Any(s => s.ServiceRelationType == "hours");
            if (hasCleaner && !hasHours)
            {
                var originalCleanerLine = order.OrderServices?.FirstOrDefault(os =>
                {
                    var svc = context.Services.Find(os.ServiceId);
                    return svc?.ServiceRelationType == "cleaner";
                });
                var originalHours = originalCleanerLine != null ? (int)(originalCleanerLine.Duration / 60) : 0;
                if (originalHours > 0)
                {
                    input.Services.Add(new OrderPricingCalculator.ServiceLineInput
                    {
                        ServiceId = 0, // synthetic — never persisted (hours lines fold into the cleaner line)
                        ServiceRelationType = "hours",
                        Quantity = originalHours
                    });
                }
            }

            return input;
        }

        /// <summary>
        /// Default/minimum square-feet for a bedroom count — mirror of
        /// getSquareFeetForBedrooms in order-pricing.calculator.ts. The UI auto-raises
        /// the Sq.ft service to this when bedrooms change; enforcing it here closes the
        /// gap for direct API calls that skip the UI clamp.
        /// </summary>
        public static int GetSquareFeetForBedrooms(int bedrooms)
        {
            switch (bedrooms)
            {
                case 0: return 400;  // Studio
                case 1: return 650;
                case 2: return 850;
                case 3: return 1000;
                case 4: return 1500;
                case 5: return 1800;
                case 6: return 2000;
                default: return Math.Max(400, bedrooms * 300); // Fallback for 7+
            }
        }

        /// <summary>
        /// An apartment can never carry a stair charge. Forces the levels line to the included
        /// count (1) for anything that is not a House, so a hand-rolled API call cannot buy
        /// levels on a flat, and a customer who switches House -> Apartment during an edit stops
        /// paying for stairs immediately.
        ///
        /// Forcing to 1 rather than deleting the line is deliberate: the line still prices to
        /// exactly $0 and 0 minutes through the self-referencing threshold, and keeping it means
        /// the quote's ServiceLines stay a faithful 1:1 image of what the client submitted, which
        /// is what lets AddOrderLinesFromQuote and PropertyDetailsHelper agree on every path.
        /// PropertyDetailsHelper then stores LevelsQuantity as null for the non-house, so nothing
        /// downstream displays a level count for an apartment.
        /// </summary>
        private static void ClampLevelsToPropertyType(
            OrderPricingCalculator.QuoteInput input, string? propertyType)
        {
            if (PropertyDetailsHelper.IsHouse(propertyType)) return;

            foreach (var levels in input.Services.Where(
                         s => s.ServiceKey == PropertyDetailsHelper.LevelsServiceKey))
            {
                levels.Quantity = PropertyDetailsHelper.SeededIncludedLevels;
            }
        }

        /// <summary>
        /// A house has no studio. Raises bedrooms to 1 for a House, mirroring the booking page's
        /// rule that Studio is neither selectable nor displayed once House is picked.
        ///
        /// Server-side because the booking endpoint accepts direct API calls; without it a caller
        /// could book a "studio house" and pay the studio's zero-quantity price for a property we
        /// have already decided has at least one bedroom. Runs BEFORE ClampSquareFeetToBedrooms
        /// so the raised bedroom count drags the included sq.ft up with it, exactly as the UI does.
        /// </summary>
        private static void ClampBedroomsToPropertyType(
            OrderPricingCalculator.QuoteInput input, string? propertyType)
        {
            if (!PropertyDetailsHelper.IsHouse(propertyType)) return;

            var bedrooms = input.Services.FirstOrDefault(s => s.ServiceKey == "bedrooms");
            if (bedrooms != null && bedrooms.Quantity < 1)
                bedrooms.Quantity = 1;
        }

        // Raises the sqft service quantity to the bedroom-count minimum, exactly like the
        // booking / order-edit pages do client-side. Reads the CONFIGURED thresholds so the
        // clamp and the free allowance are the same data — a hardcoded clamp against
        // admin-configured allowances would let a customer sit below their included amount.
        private static void ClampSquareFeetToBedrooms(OrderPricingCalculator.QuoteInput input)
        {
            var bedrooms = input.Services.FirstOrDefault(s => s.ServiceKey == "bedrooms");
            var sqft = input.Services.FirstOrDefault(s => s.ServiceKey == "sqft");
            if (bedrooms == null || sqft == null) return;

            var minSqft = ResolveMinimumSquareFeet(sqft, bedrooms);
            if (sqft.Quantity < minSqft)
                sqft.Quantity = (int)Math.Ceiling(minSqft);
        }

        /// <summary>
        /// Included quantity for the current bedroom count, using the same FLOOR lookup the
        /// calculator uses (highest row with SourceQuantity &lt;= selected; below all rows, the
        /// lowest). Mirrors getSquareFeetForBedrooms on the frontend. Falls back to the legacy
        /// hardcoded table only when no thresholds are configured for this pair.
        /// </summary>
        private static decimal ResolveMinimumSquareFeet(
            OrderPricingCalculator.ServiceLineInput sqft,
            OrderPricingCalculator.ServiceLineInput bedrooms)
        {
            var rows = sqft.Thresholds
                .Where(t => t.SourceServiceId == bedrooms.ServiceId)
                .OrderBy(t => t.SourceQuantity)
                .ToList();

            if (rows.Count == 0)
                return GetSquareFeetForBedrooms(bedrooms.Quantity);

            var match = rows.LastOrDefault(r => r.SourceQuantity <= bedrooms.Quantity) ?? rows[0];
            return match.IncludedQuantity;
        }

        /// <summary>
        /// Clamps a submitted LEVELS quantity into the range the admin configured on the service
        /// row (MinValue/MaxValue, defaulting to 1..4). Applied only to levels, deliberately:
        /// clamping every service to its configured range would silently change how bedrooms,
        /// sq.ft and cleaner-hours behave on paths that have always accepted the raw value, and
        /// that is a much larger behavioural change than this feature is entitled to make.
        ///
        /// The booking endpoint accepts direct API calls, so without this a caller could submit
        /// 400 levels and be quoted a five-figure stair charge that no UI could ever produce.
        /// </summary>
        private static int ClampLevelsToConfiguredRange(Service service, int quantity)
        {
            if (service.ServiceKey != PropertyDetailsHelper.LevelsServiceKey) return quantity;

            var min = service.MinValue ?? 1;
            var max = service.MaxValue ?? 4;
            if (max < min) max = min;

            return Math.Clamp(quantity, min, max);
        }

        private static async Task AddServiceLinesAsync(
            ApplicationDbContext context,
            OrderPricingCalculator.QuoteInput input,
            IEnumerable<BookingServiceDto> services)
        {
            var serviceDtos = services?.ToList() ?? new List<BookingServiceDto>();
            if (serviceDtos.Count == 0) return;

            // MUST eager-load Thresholds and RateTiers. Lazy loading is NOT enabled on this
            // context (Program.cs registers the DbContext with UseMySql only), so the previous
            // FindAsync left both collections EMPTY — which the calculator reads as "no
            // allowance, no tiers" and silently prices every service under the old flat model.
            // One batched query rather than N FindAsync round-trips; AsSplitQuery avoids the
            // cartesian product of two collection includes.
            var ids = serviceDtos.Select(s => s.ServiceId).Distinct().ToList();
            var catalog = await context.Services
                .Include(s => s.Thresholds)
                .Include(s => s.RateTiers)
                .AsSplitQuery()
                .Where(s => ids.Contains(s.Id))
                .ToListAsync();

            foreach (var serviceDto in serviceDtos)
            {
                var service = catalog.FirstOrDefault(s => s.Id == serviceDto.ServiceId);
                if (service == null) continue;

                input.Services.Add(new OrderPricingCalculator.ServiceLineInput
                {
                    ServiceId = service.Id,
                    Quantity = ClampLevelsToConfiguredRange(service, serviceDto.Quantity),
                    Cost = service.Cost,
                    TimeDuration = service.TimeDuration,
                    ServiceRelationType = service.ServiceRelationType,
                    ServiceKey = service.ServiceKey,
                    ChargeAboveThreshold = service.ChargeAboveThreshold,
                    ZeroQuantityCost = service.ZeroQuantityCost,
                    ZeroQuantityDuration = service.ZeroQuantityDuration,
                    RateTiers = (service.RateTiers ?? new List<ServiceRateTier>())
                        .OrderBy(t => t.FromQuantity)
                        .Select(t => new OrderPricingCalculator.RateTierInput
                        {
                            FromQuantity = t.FromQuantity,
                            Cost = t.Cost,
                            TimeDuration = t.TimeDuration
                        }).ToList(),
                    Thresholds = (service.Thresholds ?? new List<ServiceThreshold>())
                        .OrderBy(t => t.SourceQuantity)
                        .Select(t => new OrderPricingCalculator.ThresholdInput
                        {
                            SourceServiceId = t.SourceServiceId,
                            SourceQuantity = t.SourceQuantity,
                            IncludedQuantity = t.IncludedQuantity
                        }).ToList()
                });
            }
        }

        private static async Task AddExtraServiceLinesAsync(
            ApplicationDbContext context,
            OrderPricingCalculator.QuoteInput input,
            IEnumerable<BookingExtraServiceDto> extraServices)
        {
            foreach (var extraServiceDto in extraServices)
            {
                var extraService = await context.ExtraServices.FindAsync(extraServiceDto.ExtraServiceId);
                if (extraService == null) continue;
                input.ExtraServices.Add(new OrderPricingCalculator.ExtraServiceLineInput
                {
                    ExtraServiceId = extraService.Id,
                    Price = extraService.Price,
                    Duration = extraService.Duration,
                    PriceMultiplier = extraService.PriceMultiplier,
                    IsDeepCleaning = extraService.IsDeepCleaning,
                    IsSuperDeepCleaning = extraService.IsSuperDeepCleaning,
                    IsSameDayService = extraService.IsSameDayService,
                    HasHours = extraService.HasHours,
                    HasQuantity = extraService.HasQuantity,
                    Name = extraService.Name,
                    Quantity = extraServiceDto.Quantity,
                    Hours = extraServiceDto.Hours
                });
            }
        }
    }
}
