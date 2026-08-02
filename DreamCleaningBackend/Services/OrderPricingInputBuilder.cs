using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
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
        /// <summary>Input for a booking-style DTO (create, prepare-payment, calculate).</summary>
        public static async Task<OrderPricingCalculator.QuoteInput> FromBookingDtoAsync(
            ApplicationDbContext context, ServiceType serviceType, CreateBookingDto dto)
        {
            var input = new OrderPricingCalculator.QuoteInput
            {
                BasePrice = serviceType.BasePrice,
                BaseDuration = serviceType.TimeDuration,
                MinimumPrice = serviceType.MinimumPrice,
                IsCustomPricing = dto.IsCustomPricing,
                CustomAmount = dto.CustomAmount,
                CustomCleaners = dto.CustomCleaners ?? (dto.MaidsCount > 0 ? dto.MaidsCount : (int?)null),
                CustomDuration = dto.CustomDuration
            };

            await AddServiceLinesAsync(context, input, dto.Services);
            await AddExtraServiceLinesAsync(context, input, dto.ExtraServices);
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
                    Cost = service.Cost,
                    TimeDuration = service.TimeDuration,
                    ServiceRelationType = service.ServiceRelationType,
                    ServiceKey = service.ServiceKey,
                    Quantity = serviceDto.Quantity,
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
