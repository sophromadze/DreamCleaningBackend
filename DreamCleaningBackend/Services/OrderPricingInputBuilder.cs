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
