using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for Service / ServiceType -> DTO assembly.
    ///
    /// These mappings used to be hand-written inline in seven places (AdminCatalogController's
    /// GetServiceTypes, GetServices, CreateService, CopyService, UpdateService, Deactivate/Activate,
    /// plus BookingController's public GetServiceTypes). Adding a column meant editing all seven,
    /// and missing one produced a blank field in exactly one screen. Route every projection
    /// through here instead — a field added below appears everywhere at once.
    ///
    /// NOTE FOR CALLERS: this operates on materialised entities, not IQueryable. Load with the
    /// navigations you need (Thresholds -> SourceService, RateTiers) and call ToListAsync BEFORE
    /// mapping; EF cannot translate these methods into SQL.
    /// </summary>
    public static class CatalogDtoMapper
    {
        public static ServiceDto ToServiceDto(Service service) => new()
        {
            Id = service.Id,
            Name = service.Name,
            ServiceKey = service.ServiceKey,
            Cost = service.Cost,
            TimeDuration = service.TimeDuration,
            ServiceTypeId = service.ServiceTypeId,
            InputType = service.InputType,
            MinValue = service.MinValue,
            MaxValue = service.MaxValue,
            StepValue = service.StepValue,
            IsRangeInput = service.IsRangeInput,
            Unit = service.Unit,
            ServiceRelationType = service.ServiceRelationType,
            IsActive = service.IsActive,
            DisplayOrder = service.DisplayOrder,
            ChargeAboveThreshold = service.ChargeAboveThreshold,
            ZeroQuantityCost = service.ZeroQuantityCost,
            ZeroQuantityDuration = service.ZeroQuantityDuration,
            Thresholds = (service.Thresholds ?? new List<ServiceThreshold>())
                .OrderBy(t => t.SourceQuantity)
                .Select(ToThresholdDto)
                .ToList(),
            RateTiers = (service.RateTiers ?? new List<ServiceRateTier>())
                .OrderBy(t => t.FromQuantity)
                .Select(ToRateTierDto)
                .ToList()
        };

        public static ServiceThresholdDto ToThresholdDto(ServiceThreshold threshold) => new()
        {
            Id = threshold.Id,
            ServiceId = threshold.ServiceId,
            SourceServiceId = threshold.SourceServiceId,
            SourceServiceKey = threshold.SourceService?.ServiceKey,
            SourceServiceName = threshold.SourceService?.Name,
            SourceQuantity = threshold.SourceQuantity,
            IncludedQuantity = threshold.IncludedQuantity
        };

        public static ServiceRateTierDto ToRateTierDto(ServiceRateTier tier) => new()
        {
            Id = tier.Id,
            ServiceId = tier.ServiceId,
            FromQuantity = tier.FromQuantity,
            Cost = tier.Cost,
            TimeDuration = tier.TimeDuration,
            DisplayOrder = tier.DisplayOrder
        };

        /// <summary>
        /// Service type without its children — used where the caller assembles Services and
        /// ExtraServices itself (the two GetServiceTypes endpoints filter them differently:
        /// the public one hides inactive rows, the admin one shows everything).
        /// </summary>
        public static ServiceTypeDto ToServiceTypeDto(ServiceType serviceType) => new()
        {
            Id = serviceType.Id,
            Name = serviceType.Name,
            BasePrice = serviceType.BasePrice,
            Description = serviceType.Description,
            IsActive = serviceType.IsActive,
            DisplayOrder = serviceType.DisplayOrder,
            HasPoll = serviceType.HasPoll,
            IsCustom = serviceType.IsCustom,
            TimeDuration = serviceType.TimeDuration,
            MinimumPrice = serviceType.MinimumPrice
        };

        /// <summary>Service type with its services attached, ordered by display order.</summary>
        public static ServiceTypeDto ToServiceTypeDto(ServiceType serviceType, IEnumerable<Service> services)
        {
            var dto = ToServiceTypeDto(serviceType);
            dto.Services = services
                .OrderBy(s => s.DisplayOrder)
                .Select(ToServiceDto)
                .ToList();
            return dto;
        }

        /// <summary>
        /// Copies the threshold and rate-tier configuration from one service onto another.
        /// Used by CopyService: a copied Sq.ft service without its tiers silently falls back to
        /// flat pricing, which on a large home is a multi-hundred-dollar overcharge.
        ///
        /// Threshold sources are remapped by ServiceKey within the TARGET's service type, so a
        /// copy into a different service type points at that type's own source service rather
        /// than reaching across into the original's. A source with no counterpart is skipped.
        /// </summary>
        public static void CopyConfiguration(
            Service source, Service target, IEnumerable<Service> targetTypeServices, DateTime now)
        {
            target.ChargeAboveThreshold = source.ChargeAboveThreshold;
            target.ZeroQuantityCost = source.ZeroQuantityCost;
            target.ZeroQuantityDuration = source.ZeroQuantityDuration;

            target.Thresholds ??= new List<ServiceThreshold>();
            target.RateTiers ??= new List<ServiceRateTier>();

            var candidates = targetTypeServices.ToList();

            foreach (var threshold in source.Thresholds ?? new List<ServiceThreshold>())
            {
                var sourceKey = threshold.SourceService?.ServiceKey;
                if (string.IsNullOrWhiteSpace(sourceKey)) continue;

                var remapped = candidates.FirstOrDefault(s =>
                    string.Equals(s.ServiceKey, sourceKey, StringComparison.OrdinalIgnoreCase));

                // No equivalent source in the target's service type — skip rather than pointing
                // at a service under a different type.
                if (remapped == null) continue;

                target.Thresholds.Add(new ServiceThreshold
                {
                    SourceServiceId = remapped.Id,
                    SourceQuantity = threshold.SourceQuantity,
                    IncludedQuantity = threshold.IncludedQuantity,
                    CreatedAt = now
                });
            }

            foreach (var tier in source.RateTiers ?? new List<ServiceRateTier>())
            {
                target.RateTiers.Add(new ServiceRateTier
                {
                    FromQuantity = tier.FromQuantity,
                    Cost = tier.Cost,
                    TimeDuration = tier.TimeDuration,
                    DisplayOrder = tier.DisplayOrder,
                    CreatedAt = now
                });
            }
        }
    }
}
