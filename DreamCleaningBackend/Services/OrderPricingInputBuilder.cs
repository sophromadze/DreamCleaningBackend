using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

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
            var input = new OrderPricingCalculator.QuoteInput
            {
                BasePrice = order.ServiceType?.BasePrice ?? 0,
                BaseDuration = order.ServiceType?.TimeDuration ?? 0
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
        // booking / order-edit pages do client-side.
        private static void ClampSquareFeetToBedrooms(OrderPricingCalculator.QuoteInput input)
        {
            var bedrooms = input.Services.FirstOrDefault(s => s.ServiceKey == "bedrooms");
            var sqft = input.Services.FirstOrDefault(s => s.ServiceKey == "sqft");
            if (bedrooms == null || sqft == null) return;

            var minSqft = GetSquareFeetForBedrooms(bedrooms.Quantity);
            if (sqft.Quantity < minSqft)
                sqft.Quantity = minSqft;
        }

        private static async Task AddServiceLinesAsync(
            ApplicationDbContext context,
            OrderPricingCalculator.QuoteInput input,
            IEnumerable<BookingServiceDto> services)
        {
            foreach (var serviceDto in services)
            {
                var service = await context.Services.FindAsync(serviceDto.ServiceId);
                if (service == null) continue;
                input.Services.Add(new OrderPricingCalculator.ServiceLineInput
                {
                    ServiceId = service.Id,
                    Cost = service.Cost,
                    TimeDuration = service.TimeDuration,
                    ServiceRelationType = service.ServiceRelationType,
                    ServiceKey = service.ServiceKey,
                    Quantity = serviceDto.Quantity
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
