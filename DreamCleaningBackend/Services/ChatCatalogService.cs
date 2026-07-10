using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Read-only catalog + estimate logic for the public chat surface, shared by
    /// ChatController's HTTP endpoints AND the AI agent's tools (get_service_catalog /
    /// calculate_price_estimate call these methods in-process — never an HTTP loopback).
    /// Prices are resolved fresh from the DB through the shared OrderPricingCalculator;
    /// the catalog exposes structure only. No writes anywhere on this path.
    /// </summary>
    public interface IChatCatalogService
    {
        Task<List<ChatServiceTypeDto>> GetServiceCatalogAsync();

        /// <summary>Returns (result, null) on success or (null, errorMessage) on validation failure.</summary>
        Task<(ChatEstimateResponseDto? Result, string? Error)> EstimateAsync(ChatEstimateRequestDto dto);
    }

    public class ChatCatalogService : IChatCatalogService
    {
        public const string EstimateNote =
            "Estimate only — final price may vary with promo codes, exact add-ons, and is confirmed at checkout.";

        private readonly ApplicationDbContext _context;

        public ChatCatalogService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<List<ChatServiceTypeDto>> GetServiceCatalogAsync()
        {
            var serviceTypes = await _context.ServiceTypes
                .AsNoTracking()
                .Include(st => st.Services.Where(s => s.IsActive))
                .Where(st => st.IsActive && !st.IsCustom)
                .OrderBy(st => st.DisplayOrder)
                .ToListAsync();

            var activeExtras = await _context.ExtraServices
                .AsNoTracking()
                .Where(es => es.IsActive)
                .OrderBy(es => es.DisplayOrder)
                .ToListAsync();

            return serviceTypes.Select(st => new ChatServiceTypeDto
            {
                Id = st.Id,
                Name = st.Name,
                Description = st.Description,
                Services = st.Services
                    .OrderBy(s => s.DisplayOrder)
                    .Select(s => new ChatServiceDto
                    {
                        Id = s.Id,
                        Name = s.Name,
                        ServiceKey = s.ServiceKey,
                        ServiceRelationType = s.ServiceRelationType,
                        InputType = s.InputType,
                        MinValue = s.MinValue,
                        MaxValue = s.MaxValue,
                        StepValue = s.StepValue,
                        IsRangeInput = s.IsRangeInput,
                        Unit = s.Unit
                    }).ToList(),
                // Type-specific extras first, then universal ones — same ordering
                // convention as BookingController.GetServiceTypes.
                ExtraServices = activeExtras
                    .Where(es => es.ServiceTypeId == st.Id && !es.IsAvailableForAll)
                    .Concat(activeExtras.Where(es => es.IsAvailableForAll && es.ServiceTypeId == null))
                    .Select(es => new ChatExtraServiceDto
                    {
                        Id = es.Id,
                        Name = es.Name,
                        Description = es.Description,
                        HasQuantity = es.HasQuantity,
                        HasHours = es.HasHours,
                        IsDeepCleaning = es.IsDeepCleaning,
                        IsSuperDeepCleaning = es.IsSuperDeepCleaning,
                        IsSameDayService = es.IsSameDayService,
                        ServiceTypeId = es.ServiceTypeId,
                        IsAvailableForAll = es.IsAvailableForAll
                    }).ToList()
            }).ToList();
        }

        public async Task<(ChatEstimateResponseDto? Result, string? Error)> EstimateAsync(ChatEstimateRequestDto dto)
        {
            var serviceType = await _context.ServiceTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(st => st.Id == dto.ServiceTypeId);
            if (serviceType == null || !serviceType.IsActive)
                return (null, $"Unknown or inactive service type: {dto.ServiceTypeId}");
            if (serviceType.IsCustom)
                return (null, "Custom service types cannot be estimated through chat");

            // ---- Strict validation (the shared input builder silently skips unknown
            // IDs for legacy reasons; this path must reject them instead) ----

            var serviceIds = dto.Services.Select(s => s.ServiceId).ToList();
            var services = await _context.Services
                .AsNoTracking()
                .Where(s => serviceIds.Contains(s.Id))
                .ToListAsync();

            foreach (var line in dto.Services)
            {
                var service = services.FirstOrDefault(s => s.Id == line.ServiceId);
                if (service == null || !service.IsActive)
                    return (null, $"Unknown or inactive service: {line.ServiceId}");
                if (service.ServiceTypeId != dto.ServiceTypeId)
                    return (null, $"Service {line.ServiceId} does not belong to service type {dto.ServiceTypeId}");
                if (line.Quantity < 0)
                    return (null, $"Negative quantity for service {line.ServiceId}");
            }

            var extraIds = dto.ExtraServices.Select(e => e.ExtraServiceId).ToList();
            var extras = await _context.ExtraServices
                .AsNoTracking()
                .Where(es => extraIds.Contains(es.Id))
                .ToListAsync();

            foreach (var line in dto.ExtraServices)
            {
                var extra = extras.FirstOrDefault(es => es.Id == line.ExtraServiceId);
                if (extra == null || !extra.IsActive)
                    return (null, $"Unknown or inactive extra service: {line.ExtraServiceId}");
                if (!extra.IsAvailableForAll && extra.ServiceTypeId != dto.ServiceTypeId)
                    return (null, $"Extra service {line.ExtraServiceId} is not available for service type {dto.ServiceTypeId}");
                if (line.Quantity < 0 || line.Hours < 0)
                    return (null, $"Negative quantity/hours for extra service {line.ExtraServiceId}");
            }

            // ---- Shared pricing path — identical to a real booking, minus discounts ----

            // A minimal booking-shaped DTO: only IDs and quantities; the custom-pricing
            // fields keep their defaults (off), and no discount/gift-card data exists here.
            var bookingShapedDto = new CreateBookingDto
            {
                Services = dto.Services,
                ExtraServices = dto.ExtraServices
            };

            var quoteInput = await OrderPricingInputBuilder.FromBookingDtoAsync(_context, serviceType, bookingShapedDto);
            var quote = OrderPricingCalculator.CalculateQuote(quoteInput);

            // No discounts/tips/gift cards — tax on the plain subtotal via the shared SalesTaxRate.
            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = quote.SubTotal
            });

            return (new ChatEstimateResponseDto
            {
                SubTotal = quote.SubTotal,
                EstimatedTax = totals.Tax,
                EstimatedTotal = totals.Total,
                // Chat-display only: round UP to the 15-minute billing/scheduling
                // granularity (189 → 195) so the model can only ever quote an
                // already-rounded figure. Deliberately does NOT touch the shared
                // OrderPricingCalculator — booking-flow math stays untouched, and
                // ceiling (vs the calculator's internal nearest-15 salary rounding)
                // errs toward over-quoting time to the customer, never under.
                DisplayDurationMinutes = Math.Ceiling(quote.DisplayDuration / 15m) * 15m,
                MaidsCount = quote.MaidsCount,
                DeepCleaningFee = quote.DeepCleaningFee,
                Note = EstimateNote
            }, null);
        }
    }
}
