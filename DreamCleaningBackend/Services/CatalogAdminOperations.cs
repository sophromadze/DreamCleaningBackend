using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Services.ChangeSets;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// The admin Services tab's logic (service types, services, included amounts, rate tiers,
    /// extra services) and the Discounts tab's subscription plans — moved VERBATIM out of
    /// AdminCatalogController in 2026-10 so the Claude connector's write tools run exactly the code
    /// and validation the panel runs. The controller keeps its routes, attributes and signatures and
    /// delegates here, so every panel response is unchanged.
    ///
    /// Methods still return MVC action results on purpose: they are the panel's answers, and
    /// <see cref="ChangeSets.CatalogOperationResult"/> reads the same objects for the connector, so
    /// there is one place deciding what "not found" or "refused" means. The Ok/BadRequest/NotFound/
    /// StatusCode helpers below build exactly what ControllerBase's helpers build.
    ///
    /// Transactions open through <see cref="CatalogTransaction"/>: on its own a method commits as
    /// before; inside a change-set apply (which already holds a transaction) it joins it, so a batch
    /// is all-or-nothing.
    /// </summary>
    public class CatalogAdminOperations
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _auditService;
        private readonly ILogger _logger;

        public CatalogAdminOperations(ApplicationDbContext context, IAuditService auditService, ILogger<CatalogAdminOperations> logger)
            : this(context, auditService, (ILogger)logger)
        {
        }

        internal CatalogAdminOperations(ApplicationDbContext context, IAuditService auditService, ILogger logger)
        {
            _context = context;
            _auditService = auditService;
            _logger = logger;
        }

        private static OkResult Ok() => new();
        private static OkObjectResult Ok(object? value) => new(value);
        private static NotFoundResult NotFound() => new();
        private static NotFoundObjectResult NotFound(object? value) => new(value);
        private static BadRequestObjectResult BadRequest(object? error) => new(error);
        private static ObjectResult StatusCode(int statusCode, object? value) => new(value) { StatusCode = statusCode };

        // Service Types Management
        //
        // Everything from here down to the Subscriptions section — service types, services,
        // thresholds, rate tiers, extra services and the pricing-configuration import/export —
        // is the admin panel's "Services" tab, and it is SuperAdmin-only (2026-09). These
        // endpoints edit the PRICE CATALOGUE every quote is built from, so a mistake here
        // re-prices the whole site rather than one order. The attribute is on each action
        // rather than on the controller because the rest of this file (subscriptions, promo
        // codes, special offers) is the Discounts tab, which regular Admins keep.
        //
        // The GETs are gated too: nothing outside that tab reads them — every other surface
        // (booking, order edit, admin orders, recreate-order) goes through the public
        // api/booking/service-types instead.
        public async Task<ActionResult<List<ServiceTypeDto>>> GetServiceTypes()
        {
            var serviceTypes = await _context.ServiceTypes
                .Include(st => st.Services).ThenInclude(s => s.Thresholds).ThenInclude(t => t.SourceService)
                .Include(st => st.Services).ThenInclude(s => s.RateTiers)
                .AsSplitQuery()
                .OrderBy(st => st.DisplayOrder)
                .ToListAsync();

            // The whole catalogue, resolved per service type below. Admin view deliberately keeps
            // inactive extras, unlike the public endpoint.
            var allExtraServices = await _context.ExtraServices
                .OrderBy(es => es.DisplayOrder)
                .ToListAsync();

            var result = new List<ServiceTypeDto>();

            foreach (var st in serviceTypes)
            {
                var serviceTypeDto = CatalogDtoMapper.ToServiceTypeDto(st, st.Services);
                serviceTypeDto.ExtraServices = CatalogDtoMapper
                    .ResolveConfiguredExtraServices(st, allExtraServices)
                    .Select(CatalogDtoMapper.ToExtraServiceDto)
                    .ToList();

                result.Add(serviceTypeDto);
            }
            return Ok(result);
        }

        public async Task<ActionResult<ServiceTypeDto>> CreateServiceType(CreateServiceTypeDto dto)
        {
            var (serviceKey, serviceKeyError) = await ResolveServiceTypeKeyAsync(dto.ServiceKey, exceptServiceTypeId: null);
            if (serviceKeyError != null) return BadRequest(new { message = serviceKeyError });

            var (displayPrice, displayPriceUnit, displayPriceError) =
                ServiceTypeDisplayPricePolicy.Resolve(dto.DisplayPrice, dto.DisplayPriceUnit);
            if (displayPriceError != null) return BadRequest(new { message = displayPriceError });

            using (var transaction = await CatalogTransaction.BeginAsync(_context))
            {
                try
                {
                    // If display order is not provided or is 0, assign it to the end
                    if (dto.DisplayOrder <= 0)
                    {
                        var maxDisplayOrder = await _context.ServiceTypes
                            .MaxAsync(s => (int?)s.DisplayOrder) ?? 0;
                        dto.DisplayOrder = maxDisplayOrder + 1;
                    }
                    else
                    {
                        // If a specific display order is provided, shift existing service types
                        var existingServiceTypes = await _context.ServiceTypes
                            .Where(s => s.DisplayOrder >= dto.DisplayOrder)
                            .ToListAsync();

                        foreach (var st in existingServiceTypes)
                        {
                            st.DisplayOrder++;
                            st.UpdatedAt = DateTime.UtcNow;
                        }
                    }

                    var serviceType = new ServiceType
                    {
                        Name = dto.Name,
                        BasePrice = dto.BasePrice,
                        Description = dto.Description,
                        DisplayOrder = dto.DisplayOrder,
                        HasPoll = dto.HasPoll,
                        CollectsPropertyType = dto.CollectsPropertyType,
                        IsCustom = dto.IsCustom,
                        IsActive = true,
                        TimeDuration = dto.TimeDuration,
                        MinimumPrice = dto.MinimumPrice,
                        ServiceKey = serviceKey,
                        DisplayPrice = displayPrice,
                        DisplayPriceUnit = displayPriceUnit,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.ServiceTypes.Add(serviceType);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // LOG THE CREATION (after save to get the ID)
                    await _auditService.LogCreateAsync(serviceType);

                    return Ok(CatalogDtoMapper.ToServiceTypeDto(serviceType));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error creating service type", error = ex.Message });
                }
            }
        }

        public async Task<ActionResult<ServiceTypeDto>> UpdateServiceType(int id, UpdateServiceTypeDto dto)
        {
            var serviceType = await _context.ServiceTypes.FindAsync(id);
            if (serviceType == null)
                return NotFound();

            var (serviceKey, serviceKeyError) = await ResolveServiceTypeKeyAsync(dto.ServiceKey, exceptServiceTypeId: id);
            if (serviceKeyError != null) return BadRequest(new { message = serviceKeyError });

            var (displayPrice, displayPriceUnit, displayPriceError) =
                ServiceTypeDisplayPricePolicy.Resolve(dto.DisplayPrice, dto.DisplayPriceUnit);
            if (displayPriceError != null) return BadRequest(new { message = displayPriceError });

            // CREATE A COPY FOR AUDITING
            // FULL scalar snapshot: the "after" side is the live entity, so any field a
            // hand-picked copy missed is recorded as a change from its CLR default and Undo
            // replays that default onto the row. See AuditSnapshot.
            var originalServiceType = AuditSnapshot.Of(serviceType);

            // Check if display order is changing
            bool isDisplayOrderChanging = serviceType.DisplayOrder != dto.DisplayOrder;
            int oldDisplayOrder = serviceType.DisplayOrder;
            int newDisplayOrder = dto.DisplayOrder;

            using (var transaction = await CatalogTransaction.BeginAsync(_context))
            {
                try
                {
                    // Handle display order changes
                    if (isDisplayOrderChanging && newDisplayOrder > 0)
                    {
                        var allServiceTypes = await _context.ServiceTypes
                            .Where(s => s.Id != id)
                            .ToListAsync();

                        if (oldDisplayOrder < newDisplayOrder)
                        {
                            // Moving down: shift items between old and new position up
                            foreach (var st in allServiceTypes.Where(s => s.DisplayOrder > oldDisplayOrder && s.DisplayOrder <= newDisplayOrder))
                            {
                                st.DisplayOrder--;
                                st.UpdatedAt = DateTime.UtcNow;
                            }
                        }
                        else if (oldDisplayOrder > newDisplayOrder)
                        {
                            // Moving up: shift items between new and old position down
                            foreach (var st in allServiceTypes.Where(s => s.DisplayOrder >= newDisplayOrder && s.DisplayOrder < oldDisplayOrder))
                            {
                                st.DisplayOrder++;
                                st.UpdatedAt = DateTime.UtcNow;
                            }
                        }
                    }

                    serviceType.Name = dto.Name;
                    serviceType.BasePrice = dto.BasePrice;
                    serviceType.Description = dto.Description;
                    serviceType.DisplayOrder = dto.DisplayOrder;
                    serviceType.HasPoll = dto.HasPoll;
                    serviceType.CollectsPropertyType = dto.CollectsPropertyType;
                    serviceType.IsCustom = dto.IsCustom;
                    serviceType.TimeDuration = dto.TimeDuration;
                    serviceType.MinimumPrice = dto.MinimumPrice;
                    serviceType.ServiceKey = serviceKey;
                    serviceType.DisplayPrice = displayPrice;
                    serviceType.DisplayPriceUnit = displayPriceUnit;
                    serviceType.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // LOG THE UPDATE
                    await _auditService.LogUpdateAsync(originalServiceType, serviceType);

                    return Ok(CatalogDtoMapper.ToServiceTypeDto(serviceType));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error updating service type", error = ex.Message });
                }
            }
        }

        /// <summary>
        /// Normalizes and validates a requested ServiceType.ServiceKey (rules: ServiceTypeKeyPolicy).
        /// Returns the value to store - null meaning "no key" - or a message for the admin. The
        /// uniqueness check gives the clear message; the unique index is what actually guarantees it.
        /// </summary>
        private async Task<(string? Key, string? Error)> ResolveServiceTypeKeyAsync(string? raw, int? exceptServiceTypeId)
        {
            var key = ServiceTypeKeyPolicy.Normalize(raw);
            if (key == null) return (null, null);

            var problem = ServiceTypeKeyPolicy.DescribeProblem(key);
            if (problem != null) return (null, problem);

            var owner = await _context.ServiceTypes
                .Where(st => st.ServiceKey == key && (exceptServiceTypeId == null || st.Id != exceptServiceTypeId))
                .Select(st => st.Name)
                .FirstOrDefaultAsync();

            return owner != null
                ? (null, ServiceTypeKeyPolicy.DescribeDuplicate(key, owner))
                : (key, null);
        }

        /// <summary>
        /// Normalizes and validates a requested ExtraService.ExtraServiceKey for a row placed at
        /// (<paramref name="isAvailableForAll"/>, <paramref name="serviceTypeId"/>) - rules in
        /// ExtraServiceKeyPolicy. Returns the value to store (null = no key) or a message for the admin.
        /// </summary>
        private async Task<(string? Key, string? Error)> ResolveExtraServiceKeyAsync(
            string? raw, int? exceptExtraServiceId, bool isAvailableForAll, int? serviceTypeId)
        {
            var key = ExtraServiceKeyPolicy.Normalize(raw);
            if (key == null) return (null, null);

            var problem = ExtraServiceKeyPolicy.DescribeProblem(key);
            if (problem != null) return (null, problem);

            var holders = await _context.ExtraServices
                .Where(es => es.ExtraServiceKey == key
                    && (exceptExtraServiceId == null || es.Id != exceptExtraServiceId))
                .ToListAsync();
            var owner = holders.FirstOrDefault(es => ExtraServiceKeyPolicy.Conflicts(isAvailableForAll, serviceTypeId, es));

            return owner != null
                ? (null, ExtraServiceKeyPolicy.DescribeDuplicate(key, owner.Name))
                : (key, null);
        }

        /// <summary>
        /// Field rules (CatalogInputPolicy) plus key uniqueness WITHIN the service type: the booking
        /// and pricing code find a type's bedrooms / sq.ft / levels row by its key, so two rows with
        /// one key make that lookup pick whichever comes first. Returns null when the save may go ahead.
        /// </summary>
        private async Task<string?> ValidateServiceAsync(
            int? exceptServiceId, int serviceTypeId, string? name, string? serviceKey, string inputType,
            string? relationType, int? minValue, int? maxValue, int? stepValue, decimal cost, decimal timeDuration)
        {
            var problem = CatalogInputPolicy.ValidateService(
                name, serviceKey, inputType, relationType, minValue, maxValue, stepValue, cost, timeDuration);
            if (problem != null) return problem;

            var key = serviceKey!.Trim();
            var duplicate = await _context.Services.AnyAsync(s =>
                s.ServiceTypeId == serviceTypeId && s.ServiceKey == key
                && (exceptServiceId == null || s.Id != exceptServiceId));
            return duplicate ? DescribeDuplicateServiceKey(key) : null;
        }

        private static string DescribeDuplicateServiceKey(string key) =>
            $"This service type already has a service with the key \"{key}\". Each key can be used once per service type.";

        public async Task<ActionResult> DeactivateServiceType(int id)
        {
            var serviceType = await _context.ServiceTypes.FindAsync(id);
            if (serviceType == null)
                return NotFound();

            // CREATE A COPY FOR AUDITING
            // Full scalar snapshot - see AuditSnapshot. This copy also omitted MinimumPrice, so
            // deactivating a service type reported its minimum price as falling to zero.
            var originalServiceType = AuditSnapshot.Of(serviceType);

            serviceType.IsActive = false;
            serviceType.UpdatedAt = DateTime.UtcNow;

            // LOG THE UPDATE (deactivation is an update)
            await _auditService.LogUpdateAsync(originalServiceType, serviceType);

            await _context.SaveChangesAsync();

            return Ok();
        }

        public async Task<ActionResult> ActivateServiceType(int id)
        {
            var serviceType = await _context.ServiceTypes.FindAsync(id);
            if (serviceType == null)
                return NotFound();

            // CREATE A COPY FOR AUDITING
            // Full scalar snapshot - see AuditSnapshot. Same omission as the deactivate path.
            var originalServiceType = AuditSnapshot.Of(serviceType);

            serviceType.IsActive = true;
            serviceType.UpdatedAt = DateTime.UtcNow;

            // LOG THE UPDATE (activation is an update)
            await _auditService.LogUpdateAsync(originalServiceType, serviceType);

            await _context.SaveChangesAsync();

            return Ok();
        }

        public async Task<ActionResult> DeleteServiceType(int id)
        {
            var serviceType = await _context.ServiceTypes
                .Include(st => st.Services)
                .Include(st => st.ExtraServices)
                .Include(st => st.Orders)
                .FirstOrDefaultAsync(st => st.Id == id);

            if (serviceType == null)
                return NotFound();

            if (serviceType.Orders.Any())
            {
                return BadRequest(new { message = "Cannot delete service type with existing orders. Please deactivate instead." });
            }

            // LOG THE DELETION BEFORE REMOVING
            await _auditService.LogDeleteAsync(serviceType);

            _context.ServiceTypes.Remove(serviceType);
            await _context.SaveChangesAsync();

            return Ok();
        }

        // Services Management
        public async Task<ActionResult<List<ServiceDto>>> GetServices()
        {
            // Materialise first: CatalogDtoMapper works on entities, not IQueryable, and the
            // thresholds/tiers it maps need their navigations loaded.
            var services = await _context.Services
                .Include(s => s.Thresholds).ThenInclude(t => t.SourceService)
                .Include(s => s.RateTiers)
                .AsSplitQuery()
                .OrderBy(s => s.ServiceTypeId)
                .ThenBy(s => s.DisplayOrder)
                .ToListAsync();

            return Ok(services.Select(CatalogDtoMapper.ToServiceDto).ToList());
        }

        public async Task<ActionResult<ServiceDto>> CreateService(CreateServiceDto dto)
        {
            var (inputType, isRangeInput) = CatalogInputPolicy.NormalizeInputType(dto.InputType, dto.IsRangeInput);
            var relationType = CatalogInputPolicy.NormalizeRelationType(dto.ServiceRelationType);
            var serviceError = await ValidateServiceAsync(
                exceptServiceId: null, dto.ServiceTypeId, dto.Name, dto.ServiceKey, inputType, relationType,
                dto.MinValue, dto.MaxValue, dto.StepValue, dto.Cost, dto.TimeDuration);
            if (serviceError != null) return BadRequest(new { message = serviceError });

            using (var transaction = await CatalogTransaction.BeginAsync(_context))
            {
                try
                {
                    // If display order is not provided or is 0, assign it to the end within the service type
                    if (dto.DisplayOrder <= 0)
                    {
                        var maxDisplayOrder = await _context.Services
                            .Where(s => s.ServiceTypeId == dto.ServiceTypeId)
                            .MaxAsync(s => (int?)s.DisplayOrder) ?? 0;
                        dto.DisplayOrder = maxDisplayOrder + 1;
                    }
                    else
                    {
                        // If a specific display order is provided, shift existing services within the same service type
                        var existingServices = await _context.Services
                            .Where(s => s.ServiceTypeId == dto.ServiceTypeId && s.DisplayOrder >= dto.DisplayOrder)
                            .ToListAsync();

                        foreach (var svc in existingServices)
                        {
                            svc.DisplayOrder++;
                            svc.UpdatedAt = DateTime.UtcNow;
                        }
                    }

                    var service = new Service
                    {
                        Name = dto.Name.Trim(),
                        ServiceKey = dto.ServiceKey.Trim(),
                        Cost = dto.Cost,
                        TimeDuration = dto.TimeDuration,
                        ServiceTypeId = dto.ServiceTypeId,
                        InputType = inputType,
                        MinValue = dto.MinValue,
                        MaxValue = dto.MaxValue,
                        StepValue = dto.StepValue,
                        IsRangeInput = isRangeInput,
                        Unit = dto.Unit,
                        ServiceRelationType = relationType,
                        DisplayOrder = dto.DisplayOrder,
                        IsActive = true,
                        ChargeAboveThreshold = dto.ChargeAboveThreshold,
                        ZeroQuantityCost = dto.ZeroQuantityCost,
                        ZeroQuantityDuration = dto.ZeroQuantityDuration,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.Services.Add(service);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    await _auditService.LogCreateAsync(service);

                    return Ok(CatalogDtoMapper.ToServiceDto(service));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error creating service", error = ex.Message });
                }
            }
        }

        public async Task<ActionResult<ServiceDto>> CopyService(CopyServiceDto dto)
        {
            // Thresholds and tiers must come along: a copied Sq.ft service without its tiers
            // silently falls back to flat Cost x quantity, which on a large home is a
            // multi-hundred-dollar overcharge.
            var sourceService = await _context.Services
                .Include(s => s.Thresholds).ThenInclude(t => t.SourceService)
                .Include(s => s.RateTiers)
                .FirstOrDefaultAsync(s => s.Id == dto.SourceServiceId);

            if (sourceService == null)
                return NotFound("Source service not found");

            var now = DateTime.UtcNow;

            var newService = new Service
            {
                Name = sourceService.Name,
                ServiceKey = sourceService.ServiceKey,
                Cost = sourceService.Cost,
                TimeDuration = sourceService.TimeDuration,
                ServiceTypeId = dto.TargetServiceTypeId,
                InputType = sourceService.InputType,
                MinValue = sourceService.MinValue,
                MaxValue = sourceService.MaxValue,
                StepValue = sourceService.StepValue,
                IsRangeInput = sourceService.IsRangeInput,
                Unit = sourceService.Unit,
                ServiceRelationType = sourceService.ServiceRelationType,
                // Appended after the target type's services; the source's number collided there.
                DisplayOrder = (await _context.Services
                    .Where(s => s.ServiceTypeId == dto.TargetServiceTypeId)
                    .MaxAsync(s => (int?)s.DisplayOrder) ?? 0) + 1,
                IsActive = true,
                CreatedAt = now
            };

            var duplicateKey = await _context.Services.AnyAsync(s =>
                s.ServiceTypeId == dto.TargetServiceTypeId && s.ServiceKey == sourceService.ServiceKey);
            if (duplicateKey)
                return BadRequest(new { message = DescribeDuplicateServiceKey(sourceService.ServiceKey) });

            // Threshold sources are remapped by ServiceKey within the TARGET service type, so a
            // copy into a different type points at that type's own bedrooms rather than reaching
            // back into the original's. Sources with no counterpart there are skipped.
            var targetTypeServices = await _context.Services
                .Where(s => s.ServiceTypeId == dto.TargetServiceTypeId)
                .ToListAsync();

            CatalogDtoMapper.CopyConfiguration(sourceService, newService, targetTypeServices, now);

            _context.Services.Add(newService);
            await _context.SaveChangesAsync();

            // Both rows: the new Service exists (Create), and it exists BECAUSE somebody copied a
            // specific source into a specific service type — which the Create row alone cannot
            // say, and which is exactly what an admin is trying to reconstruct when a copied
            // service prices differently from the original.
            await _auditService.LogCreateAsync(newService);
            await _auditService.LogActionAsync(
                AuditEntityTypes.CatalogueCopy, newService.Id, "ServiceCopied", null, new
                {
                    SourceServiceId = dto.SourceServiceId,
                    SourceServiceName = sourceService.Name,
                    TargetServiceTypeId = dto.TargetServiceTypeId,
                    ThresholdsCopied = newService.Thresholds.Count,
                    RateTiersCopied = newService.RateTiers.Count
                });

            var skipped = (sourceService.Thresholds?.Count ?? 0) - newService.Thresholds.Count;
            if (skipped > 0)
            {
                _logger.LogWarning(
                    "CopyService: {Skipped} included-amount row(s) were dropped copying service {SourceId} " +
                    "into service type {TargetTypeId} — no matching source service key there.",
                    skipped, dto.SourceServiceId, dto.TargetServiceTypeId);
            }

            return Ok(CatalogDtoMapper.ToServiceDto(newService));
        }

        public async Task<ActionResult<ServiceDto>> UpdateService(int id, UpdateServiceDto dto)
        {
            // Thresholds and tiers must be loaded even though this endpoint doesn't modify them:
            // lazy loading is NOT enabled on this context, so without the Includes the mapped
            // response would report them as empty and the admin panel would render the service's
            // configuration as wiped immediately after a save.
            var service = await _context.Services
                .Include(s => s.Thresholds).ThenInclude(t => t.SourceService)
                .Include(s => s.RateTiers)
                .AsSplitQuery()
                .FirstOrDefaultAsync(s => s.Id == id);

            if (service == null)
                return NotFound();

            var (inputType, isRangeInput) = CatalogInputPolicy.NormalizeInputType(dto.InputType, dto.IsRangeInput);
            var relationType = CatalogInputPolicy.NormalizeRelationType(dto.ServiceRelationType);
            var serviceError = await ValidateServiceAsync(
                exceptServiceId: id, dto.ServiceTypeId, dto.Name, dto.ServiceKey, inputType, relationType,
                dto.MinValue, dto.MaxValue, dto.StepValue, dto.Cost, dto.TimeDuration);
            if (serviceError != null) return BadRequest(new { message = serviceError });

            // FULL scalar snapshot - see AuditSnapshot.
            //
            // This copy omitted ChargeAboveThreshold, ZeroQuantityCost and ZeroQuantityDuration,
            // which made it the most dangerous of the partial snapshots after Order and User:
            // ZeroQuantityCost/Duration MUST stay NULL on the levels row (the calculator's
            // generic zero-quantity branch fires for any non-null value, and the bedrooms-keyed
            // studio rule is protected by those columns being null), so replaying this row's
            // fabricated nulls over a service that has them set breaks studio pricing.
            var originalService = AuditSnapshot.Of(service);

            // Check if display order is changing
            bool isDisplayOrderChanging = service.DisplayOrder != dto.DisplayOrder;
            int oldDisplayOrder = service.DisplayOrder;
            int newDisplayOrder = dto.DisplayOrder;

            using (var transaction = await CatalogTransaction.BeginAsync(_context))
            {
                try
                {
                    // Handle display order changes within the same service type
                    if (isDisplayOrderChanging && newDisplayOrder > 0)
                    {
                        var allServices = await _context.Services
                            .Where(s => s.Id != id && s.ServiceTypeId == dto.ServiceTypeId)
                            .ToListAsync();

                        if (oldDisplayOrder < newDisplayOrder)
                        {
                            // Moving down: shift items between old and new position up
                            foreach (var svc in allServices.Where(s => s.DisplayOrder > oldDisplayOrder && s.DisplayOrder <= newDisplayOrder))
                            {
                                svc.DisplayOrder--;
                                svc.UpdatedAt = DateTime.UtcNow;
                            }
                        }
                        else if (oldDisplayOrder > newDisplayOrder)
                        {
                            // Moving up: shift items between new and old position down
                            foreach (var svc in allServices.Where(s => s.DisplayOrder >= newDisplayOrder && s.DisplayOrder < oldDisplayOrder))
                            {
                                svc.DisplayOrder++;
                                svc.UpdatedAt = DateTime.UtcNow;
                            }
                        }
                    }

                    // Update all fields
                    service.Name = dto.Name.Trim();
                    service.ServiceKey = dto.ServiceKey.Trim();
                    service.Cost = dto.Cost;
                    service.TimeDuration = dto.TimeDuration;
                    service.ServiceTypeId = dto.ServiceTypeId;
                    service.InputType = inputType;
                    service.MinValue = dto.MinValue;
                    service.MaxValue = dto.MaxValue;
                    service.StepValue = dto.StepValue;
                    service.IsRangeInput = isRangeInput;
                    service.Unit = dto.Unit;
                    service.ServiceRelationType = relationType;
                    service.DisplayOrder = dto.DisplayOrder;
                    service.ChargeAboveThreshold = dto.ChargeAboveThreshold;
                    service.ZeroQuantityCost = dto.ZeroQuantityCost;
                    service.ZeroQuantityDuration = dto.ZeroQuantityDuration;
                    service.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // LOG THE UPDATE
                    await _auditService.LogUpdateAsync(originalService, service);

                    return Ok(CatalogDtoMapper.ToServiceDto(service));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error updating service", error = ex.Message });
                }
            }
        }

        public async Task<ActionResult> DeactivateService(int id)
        {
            var service = await _context.Services.FindAsync(id);
            if (service == null)
                return NotFound();

            // CREATE A COPY WITH ALL CURRENT VALUES
            var originalService = new Service
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
                DisplayOrder = service.DisplayOrder,
                IsActive = service.IsActive,
                CreatedAt = service.CreatedAt,
                UpdatedAt = service.UpdatedAt
            };

            service.IsActive = false;
            service.UpdatedAt = DateTime.UtcNow;

            // Save first
            await _context.SaveChangesAsync();

            // CREATE UPDATED COPY
            var updatedService = new Service
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
                DisplayOrder = service.DisplayOrder,
                IsActive = service.IsActive,
                CreatedAt = service.CreatedAt,
                UpdatedAt = service.UpdatedAt
            };

            // LOG THE UPDATE
            try
            {
                await _auditService.LogUpdateAsync(originalService, updatedService);
            }
            catch (Exception auditEx)
            {
                _logger.LogError(auditEx, "Audit logging failed");
            }

            return Ok();
        }

        public async Task<ActionResult> ActivateService(int id)
        {
            var service = await _context.Services.FindAsync(id);
            if (service == null)
                return NotFound();

            // CREATE A COPY WITH ALL CURRENT VALUES
            var originalService = new Service
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
                DisplayOrder = service.DisplayOrder,
                IsActive = service.IsActive,
                CreatedAt = service.CreatedAt,
                UpdatedAt = service.UpdatedAt
            };

            service.IsActive = true;
            service.UpdatedAt = DateTime.UtcNow;

            // Save first
            await _context.SaveChangesAsync();

            // CREATE UPDATED COPY
            var updatedService = new Service
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
                DisplayOrder = service.DisplayOrder,
                IsActive = service.IsActive,
                CreatedAt = service.CreatedAt,
                UpdatedAt = service.UpdatedAt
            };

            // LOG THE UPDATE
            try
            {
                await _auditService.LogUpdateAsync(originalService, updatedService);
            }
            catch (Exception auditEx)
            {
                _logger.LogError(auditEx, "Audit logging failed");
            }

            return Ok();
        }

        public async Task<ActionResult> DeleteService(int id)
        {
            var service = await _context.Services
                .Include(s => s.OrderServices)
                .FirstOrDefaultAsync(s => s.Id == id);

            if (service == null)
                return NotFound();

            // Check if there are any orders using this service
            if (service.OrderServices.Any())
            {
                // CHANGED: Return JSON object instead of plain text
                return BadRequest(new { message = "Cannot delete service with existing orders. Please deactivate instead." });
            }

            // This service may be the included-amount SOURCE for another service (e.g. Bedrooms
            // is the source for Square Feet). That FK is Restrict on purpose: silently removing
            // the source would leave the dependent service billing from zero — a large, silent
            // overcharge. Check up front so we can name the dependency instead of surfacing a
            // raw DbUpdateException. Its own thresholds/tiers cascade and need no check.
            // A rule the service holds on ITSELF (Levels: "when Levels is 1, 1 is included") is
            // not a dependency of anything else, so it does not block - it is removed below.
            var dependentServiceNames = await _context.ServiceThresholds
                .Where(t => t.SourceServiceId == id && t.ServiceId != id)
                .Select(t => t.Service.Name)
                .Distinct()
                .ToListAsync();

            if (dependentServiceNames.Any())
            {
                var dependents = string.Join(", ", dependentServiceNames);
                return BadRequest(new
                {
                    message = $"Cannot delete {service.Name} — it is used as the included-amount " +
                              $"source for {dependents}. Remove those included amounts first."
                });
            }

            // The ServiceId cascade would also remove the self-referencing rows (MariaDB allows it
            // despite the Restrict SourceServiceId FK on the same row), but they are deleted
            // explicitly so each one gets its own audit entry, like a rule removed by hand.
            var selfRules = await _context.ServiceThresholds
                .Where(t => t.ServiceId == id && t.SourceServiceId == id)
                .ToListAsync();

            // Audit first: the audit service saves the context, so anything queued for removal
            // before it would be committed on its own, ahead of the service delete.
            foreach (var rule in selfRules)
                await _auditService.LogDeleteAsync(rule);
            await _auditService.LogDeleteAsync(service);

            // One SaveChanges = one transaction: the rules and the service go together or not at all.
            _context.ServiceThresholds.RemoveRange(selfRules);
            _context.Services.Remove(service);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                // Backstop: a dependency created between the check above and the save, or any
                // future restricted FK we haven't special-cased. Never leak the provider error.
                _logger.LogWarning(ex, "Delete blocked by a database constraint for service {ServiceId}", id);
                return BadRequest(new
                {
                    message = $"Cannot delete {service.Name} — another part of the pricing " +
                              "configuration still depends on it."
                });
            }

            return Ok();
        }

        // ===== Included amounts (ServiceThreshold) =====
        // Rows are validated against the SAME triple the unique index enforces —
        // (ServiceId, SourceServiceId, SourceQuantity) — so a clash is reported as a clear
        // message rather than surfacing as a 1062 duplicate-key 500.

        public async Task<ActionResult<List<ServiceThresholdDto>>> GetServiceThresholds(int serviceId)
        {
            if (!await _context.Services.AnyAsync(s => s.Id == serviceId))
                return NotFound(new { message = "Service not found." });

            var rows = await _context.ServiceThresholds
                .Include(t => t.SourceService)
                .Where(t => t.ServiceId == serviceId)
                .OrderBy(t => t.SourceQuantity)
                .ToListAsync();

            return Ok(rows.Select(CatalogDtoMapper.ToThresholdDto).ToList());
        }

        public async Task<ActionResult<ServiceThresholdDto>> CreateServiceThreshold(
            int serviceId, SaveServiceThresholdDto dto)
        {
            var error = await ValidateThresholdAsync(serviceId, dto, excludeId: null);
            if (error != null) return BadRequest(new { message = error });

            var threshold = new ServiceThreshold
            {
                ServiceId = serviceId,
                SourceServiceId = dto.SourceServiceId,
                SourceQuantity = dto.SourceQuantity,
                IncludedQuantity = dto.IncludedQuantity,
                CreatedAt = DateTime.UtcNow
            };

            _context.ServiceThresholds.Add(threshold);
            await _context.SaveChangesAsync();
            await _auditService.LogCreateAsync(threshold);

            await _context.Entry(threshold).Reference(t => t.SourceService).LoadAsync();
            return Ok(CatalogDtoMapper.ToThresholdDto(threshold));
        }

        public async Task<ActionResult<ServiceThresholdDto>> UpdateServiceThreshold(
            int serviceId, int id, SaveServiceThresholdDto dto)
        {
            var threshold = await _context.ServiceThresholds
                .FirstOrDefaultAsync(t => t.Id == id && t.ServiceId == serviceId);
            if (threshold == null) return NotFound();

            var error = await ValidateThresholdAsync(serviceId, dto, excludeId: id);
            if (error != null) return BadRequest(new { message = error });

            var beforeThreshold = AuditSnapshot.Of(threshold);

            threshold.SourceServiceId = dto.SourceServiceId;
            threshold.SourceQuantity = dto.SourceQuantity;
            threshold.IncludedQuantity = dto.IncludedQuantity;
            threshold.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Included amounts drive the levels rule and the bedrooms-to-sqft floor, so an edit
            // here moves quoted prices without touching a single price field.
            await _auditService.LogUpdateAsync(beforeThreshold, threshold);

            await _context.Entry(threshold).Reference(t => t.SourceService).LoadAsync();
            return Ok(CatalogDtoMapper.ToThresholdDto(threshold));
        }

        public async Task<ActionResult> DeleteServiceThreshold(int serviceId, int id)
        {
            var threshold = await _context.ServiceThresholds
                .FirstOrDefaultAsync(t => t.Id == id && t.ServiceId == serviceId);
            if (threshold == null) return NotFound();

            await _auditService.LogDeleteAsync(threshold);
            _context.ServiceThresholds.Remove(threshold);
            await _context.SaveChangesAsync();

            return Ok();
        }

        /// <summary>Returns an error message, or null when the row is valid.</summary>
        private async Task<string?> ValidateThresholdAsync(int serviceId, SaveServiceThresholdDto dto, int? excludeId)
        {
            var service = await _context.Services.FirstOrDefaultAsync(s => s.Id == serviceId);
            if (service == null) return "Service not found.";

            if (dto.SourceQuantity < 0) return "The source quantity cannot be negative.";
            if (dto.IncludedQuantity < 0) return "The included amount cannot be negative.";

            var source = await _context.Services.FirstOrDefaultAsync(s => s.Id == dto.SourceServiceId);
            if (source == null) return "The selected source service does not exist.";

            // Cross-service-type references would let one service type's configuration silently
            // depend on another's.
            if (source.ServiceTypeId != service.ServiceTypeId)
                return $"'{source.Name}' belongs to a different service type and cannot be used as a source here.";

            var clash = await _context.ServiceThresholds.AnyAsync(t =>
                t.ServiceId == serviceId &&
                t.SourceServiceId == dto.SourceServiceId &&
                t.SourceQuantity == dto.SourceQuantity &&
                (excludeId == null || t.Id != excludeId));

            if (clash)
                return $"An included amount for '{source.Name}' = {dto.SourceQuantity} already exists.";

            return null;
        }

        // ===== Rate tiers (ServiceRateTier) =====

        public async Task<ActionResult<List<ServiceRateTierDto>>> GetServiceRateTiers(int serviceId)
        {
            if (!await _context.Services.AnyAsync(s => s.Id == serviceId))
                return NotFound(new { message = "Service not found." });

            var rows = await _context.ServiceRateTiers
                .Where(t => t.ServiceId == serviceId)
                .OrderBy(t => t.FromQuantity)
                .ToListAsync();

            return Ok(rows.Select(CatalogDtoMapper.ToRateTierDto).ToList());
        }

        public async Task<ActionResult<ServiceRateTierDto>> CreateServiceRateTier(
            int serviceId, SaveServiceRateTierDto dto)
        {
            var error = await ValidateRateTierAsync(serviceId, dto, excludeId: null);
            if (error != null) return BadRequest(new { message = error });

            var tier = new ServiceRateTier
            {
                ServiceId = serviceId,
                FromQuantity = dto.FromQuantity,
                Cost = dto.Cost,
                TimeDuration = dto.TimeDuration,
                DisplayOrder = dto.DisplayOrder,
                CreatedAt = DateTime.UtcNow
            };

            _context.ServiceRateTiers.Add(tier);
            await _context.SaveChangesAsync();
            await _auditService.LogCreateAsync(tier);

            return Ok(CatalogDtoMapper.ToRateTierDto(tier));
        }

        public async Task<ActionResult<ServiceRateTierDto>> UpdateServiceRateTier(
            int serviceId, int id, SaveServiceRateTierDto dto)
        {
            var tier = await _context.ServiceRateTiers
                .FirstOrDefaultAsync(t => t.Id == id && t.ServiceId == serviceId);
            if (tier == null) return NotFound();

            var error = await ValidateRateTierAsync(serviceId, dto, excludeId: id);
            if (error != null) return BadRequest(new { message = error });

            var beforeTier = AuditSnapshot.Of(tier);

            tier.FromQuantity = dto.FromQuantity;
            tier.Cost = dto.Cost;
            tier.TimeDuration = dto.TimeDuration;
            tier.DisplayOrder = dto.DisplayOrder;
            tier.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _auditService.LogUpdateAsync(beforeTier, tier);
            return Ok(CatalogDtoMapper.ToRateTierDto(tier));
        }

        public async Task<ActionResult> DeleteServiceRateTier(int serviceId, int id)
        {
            var tier = await _context.ServiceRateTiers
                .FirstOrDefaultAsync(t => t.Id == id && t.ServiceId == serviceId);
            if (tier == null) return NotFound();

            // Removing the 0 band while others remain would leave the first slice of every
            // billable quantity unpriced — a silent undercharge.
            if (tier.FromQuantity == 0m)
            {
                var othersRemain = await _context.ServiceRateTiers
                    .AnyAsync(t => t.ServiceId == serviceId && t.Id != id);
                if (othersRemain)
                    return BadRequest(new
                    {
                        message = "The tier starting at 0 cannot be removed while other tiers exist. " +
                                  "Delete the higher tiers first."
                    });
            }

            await _auditService.LogDeleteAsync(tier);
            _context.ServiceRateTiers.Remove(tier);
            await _context.SaveChangesAsync();

            return Ok();
        }

        /// <summary>Returns an error message, or null when the tier is valid.</summary>
        private async Task<string?> ValidateRateTierAsync(
            int serviceId, SaveServiceRateTierDto dto, int? excludeId)
        {
            if (!await _context.Services.AnyAsync(s => s.Id == serviceId))
                return "Service not found.";

            if (dto.FromQuantity < 0m) return "The tier start cannot be negative.";
            if (dto.Cost < 0m) return "The cost per unit cannot be negative.";
            if (dto.TimeDuration < 0m) return "The minutes per unit cannot be negative.";

            var clash = await _context.ServiceRateTiers.AnyAsync(t =>
                t.ServiceId == serviceId &&
                t.FromQuantity == dto.FromQuantity &&
                (excludeId == null || t.Id != excludeId));

            if (clash) return $"A rate tier starting at {dto.FromQuantity:0.##} already exists.";

            // The lowest tier must anchor at 0, otherwise the slice below it is never priced.
            //
            // Enforced on the RESULTING set, which is what makes this cover EDITS as well as
            // creates: excludeId removes the row being edited from the "is there a 0 band?"
            // check, so moving the only 0 tier up to 400 is rejected exactly like deleting it.
            // Editing a 0 tier's cost or minutes is unaffected — FromQuantity stays 0 and the
            // first clause short-circuits.
            var otherTierHasZero = await _context.ServiceRateTiers.AnyAsync(t =>
                t.ServiceId == serviceId &&
                t.FromQuantity == 0m &&
                (excludeId == null || t.Id != excludeId));

            if (dto.FromQuantity != 0m && !otherTierHasZero)
                return excludeId == null
                    ? "The first rate tier must start at 0. Add that one before adding higher tiers."
                    : "This is the only tier starting at 0, so it cannot be moved to " +
                      $"{dto.FromQuantity:0.##} — everything below that would be unpriced. " +
                      "Add a replacement tier starting at 0 first.";

            return null;
        }

        public async Task<ActionResult<List<ExtraServiceDto>>> GetExtraServices()
        {
            var extraServices = await _context.ExtraServices
                .OrderBy(es => es.DisplayOrder)
                .Select(es => new ExtraServiceDto
                {
                    Id = es.Id,
                    Name = es.Name,
                    ExtraServiceKey = es.ExtraServiceKey,
                    Description = es.Description,
                    Price = es.Price,
                    Duration = es.Duration,
                    Icon = es.Icon,
                    HasQuantity = es.HasQuantity,
                    HasHours = es.HasHours,
                    IsDeepCleaning = es.IsDeepCleaning,
                    IsSuperDeepCleaning = es.IsSuperDeepCleaning,
                    IsSameDayService = es.IsSameDayService,
                    PriceMultiplier = es.PriceMultiplier,
                    TimeMultiplier = es.TimeMultiplier,
                    IsAvailableForAll = es.IsAvailableForAll,
                    IsActive = es.IsActive,
                    DisplayOrder = es.DisplayOrder
                })
                .ToListAsync();

            return Ok(extraServices);
        }

        public async Task<ActionResult<ExtraServiceDto>> CreateExtraService(CreateExtraServiceDto dto)
        {
            var inputError = CatalogInputPolicy.ValidateExtraService(
                dto.Name, dto.Price, dto.Duration, dto.HasQuantity, dto.HasHours,
                dto.IsDeepCleaning, dto.IsSuperDeepCleaning, dto.IsSameDayService,
                dto.PriceMultiplier, dto.TimeMultiplier);
            if (inputError != null) return BadRequest(new { message = inputError });
            var (priceMultiplier, timeMultiplier) = CatalogInputPolicy.NormalizeMultipliers(
                dto.IsDeepCleaning, dto.IsSuperDeepCleaning, dto.PriceMultiplier, dto.TimeMultiplier);

            var (extraServiceKey, extraServiceKeyError) = await ResolveExtraServiceKeyAsync(
                dto.ExtraServiceKey, exceptExtraServiceId: null, dto.IsAvailableForAll, dto.ServiceTypeId);
            if (extraServiceKeyError != null) return BadRequest(new { message = extraServiceKeyError });

            using (var transaction = await CatalogTransaction.BeginAsync(_context))
            {
                try
                {
                    // If display order is not provided or is 0, assign it to the end
                    if (dto.DisplayOrder <= 0)
                    {
                        var query = _context.ExtraServices.AsQueryable();

                        // If specific to a service type, order within that type
                        if (dto.ServiceTypeId.HasValue && !dto.IsAvailableForAll)
                        {
                            query = query.Where(es => es.ServiceTypeId == dto.ServiceTypeId);
                        }

                        var maxDisplayOrder = await query.MaxAsync(es => (int?)es.DisplayOrder) ?? 0;
                        dto.DisplayOrder = maxDisplayOrder + 1;
                    }
                    else
                    {
                        // If a specific display order is provided, shift existing extra services
                        var query = _context.ExtraServices.Where(es => es.DisplayOrder >= dto.DisplayOrder);

                        // If specific to a service type, only shift within that type
                        if (dto.ServiceTypeId.HasValue && !dto.IsAvailableForAll)
                        {
                            query = query.Where(es => es.ServiceTypeId == dto.ServiceTypeId || es.IsAvailableForAll);
                        }

                        var existingExtraServices = await query.ToListAsync();

                        foreach (var es in existingExtraServices)
                        {
                            es.DisplayOrder++;
                            es.UpdatedAt = DateTime.UtcNow;
                        }
                    }

                    var extraService = new ExtraService
                    {
                        Name = dto.Name.Trim(),
                        ExtraServiceKey = extraServiceKey,
                        Description = dto.Description,
                        Price = dto.Price,
                        Duration = dto.Duration,
                        Icon = dto.Icon,
                        HasQuantity = dto.HasQuantity,
                        HasHours = dto.HasHours,
                        IsDeepCleaning = dto.IsDeepCleaning,
                        IsSuperDeepCleaning = dto.IsSuperDeepCleaning,
                        IsSameDayService = dto.IsSameDayService,
                        PriceMultiplier = priceMultiplier,
                        TimeMultiplier = timeMultiplier,
                        ServiceTypeId = dto.ServiceTypeId,
                        IsAvailableForAll = dto.IsAvailableForAll,
                        DisplayOrder = dto.DisplayOrder,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.ExtraServices.Add(extraService);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    await _auditService.LogCreateAsync(extraService);

                    return Ok(CatalogDtoMapper.ToExtraServiceDto(extraService));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error creating extra service", error = ex.Message });
                }
            }
        }

        public async Task<ActionResult<ExtraServiceDto>> CopyExtraService(CopyExtraServiceDto dto)
        {
            var sourceExtraService = await _context.ExtraServices.FindAsync(dto.SourceExtraServiceId);
            if (sourceExtraService == null)
                return NotFound("Source extra service not found");

            // The copy is the same extra, so it keeps the key - unless the target type can already
            // see a row holding it (a universal one, or a copy it already has), in which case it is
            // created unkeyed rather than refused: the admin asked for a copy, not a key.
            string? copiedKey = null;
            if (!string.IsNullOrWhiteSpace(sourceExtraService.ExtraServiceKey))
            {
                var (key, keyError) = await ResolveExtraServiceKeyAsync(
                    sourceExtraService.ExtraServiceKey, exceptExtraServiceId: null,
                    isAvailableForAll: false, serviceTypeId: dto.TargetServiceTypeId);
                copiedKey = keyError == null ? key : null;
            }

            var newExtraService = new ExtraService
            {
                Name = sourceExtraService.Name,
                ExtraServiceKey = copiedKey,
                Description = sourceExtraService.Description,
                Price = sourceExtraService.Price,
                Duration = sourceExtraService.Duration,
                Icon = sourceExtraService.Icon,
                HasQuantity = sourceExtraService.HasQuantity,
                HasHours = sourceExtraService.HasHours,
                IsDeepCleaning = sourceExtraService.IsDeepCleaning,
                IsSuperDeepCleaning = sourceExtraService.IsSuperDeepCleaning,
                IsSameDayService = sourceExtraService.IsSameDayService,
                PriceMultiplier = sourceExtraService.PriceMultiplier,
                TimeMultiplier = sourceExtraService.TimeMultiplier,
                ServiceTypeId = dto.TargetServiceTypeId,
                IsAvailableForAll = false, // When copying to specific service type
                // Appended after everything the target type already shows (its own rows and the
                // universal ones). Keeping the source's number collided with whatever sat there.
                DisplayOrder = (await _context.ExtraServices
                    .Where(es => es.ServiceTypeId == dto.TargetServiceTypeId || es.IsAvailableForAll)
                    .MaxAsync(es => (int?)es.DisplayOrder) ?? 0) + 1,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _context.ExtraServices.Add(newExtraService);
            await _context.SaveChangesAsync();

            await _auditService.LogCreateAsync(newExtraService);
            await _auditService.LogActionAsync(
                AuditEntityTypes.CatalogueCopy, newExtraService.Id, "ExtraServiceCopied", null, new
                {
                    SourceExtraServiceId = dto.SourceExtraServiceId,
                    SourceExtraServiceName = sourceExtraService.Name,
                    TargetServiceTypeId = dto.TargetServiceTypeId
                });

            return Ok(CatalogDtoMapper.ToExtraServiceDto(newExtraService));
        }

        public async Task<ActionResult<ExtraServiceDto>> UpdateExtraService(int id, UpdateExtraServiceDto dto)
        {
            var extraService = await _context.ExtraServices.FindAsync(id);
            if (extraService == null)
                return NotFound();

            // Full scalar snapshot - see AuditSnapshot.
            var originalExtraService = AuditSnapshot.Of(extraService);

            // An absent time multiplier keeps the stored one (see UpdateExtraServiceDto).
            var requestedTimeMultiplier = dto.TimeMultiplier ?? extraService.TimeMultiplier;
            var inputError = CatalogInputPolicy.ValidateExtraService(
                dto.Name, dto.Price, dto.Duration, dto.HasQuantity, dto.HasHours,
                dto.IsDeepCleaning, dto.IsSuperDeepCleaning, dto.IsSameDayService,
                dto.PriceMultiplier, requestedTimeMultiplier);
            if (inputError != null) return BadRequest(new { message = inputError });
            var (priceMultiplier, timeMultiplier) = CatalogInputPolicy.NormalizeMultipliers(
                dto.IsDeepCleaning, dto.IsSuperDeepCleaning, dto.PriceMultiplier, requestedTimeMultiplier);

            // An absent key keeps the stored one (see UpdateExtraServiceDto), but it is still
            // re-checked: moving the extra to another service type, or making it universal, can put
            // it beside a row that already holds the same key.
            var requestedKey = dto.ExtraServiceKeyProvided ? dto.ExtraServiceKey : extraService.ExtraServiceKey;
            var (extraServiceKey, extraServiceKeyError) = await ResolveExtraServiceKeyAsync(
                requestedKey, exceptExtraServiceId: id, dto.IsAvailableForAll, dto.ServiceTypeId);
            if (extraServiceKeyError != null) return BadRequest(new { message = extraServiceKeyError });

            // Check if display order is changing
            bool isDisplayOrderChanging = extraService.DisplayOrder != dto.DisplayOrder;
            int oldDisplayOrder = extraService.DisplayOrder;
            int newDisplayOrder = dto.DisplayOrder;

            using (var transaction = await CatalogTransaction.BeginAsync(_context))
            {
                try
                {
                    // Handle display order changes
                    if (isDisplayOrderChanging && newDisplayOrder > 0)
                    {
                        var query = _context.ExtraServices.Where(es => es.Id != id);

                        // If specific to a service type, only reorder within that context
                        if (dto.ServiceTypeId.HasValue && !dto.IsAvailableForAll)
                        {
                            query = query.Where(es => es.ServiceTypeId == dto.ServiceTypeId || es.IsAvailableForAll);
                        }

                        var allExtraServices = await query.ToListAsync();

                        if (oldDisplayOrder < newDisplayOrder)
                        {
                            // Moving down: shift items between old and new position up
                            foreach (var es in allExtraServices.Where(s => s.DisplayOrder > oldDisplayOrder && s.DisplayOrder <= newDisplayOrder))
                            {
                                es.DisplayOrder--;
                                es.UpdatedAt = DateTime.UtcNow;
                            }
                        }
                        else if (oldDisplayOrder > newDisplayOrder)
                        {
                            // Moving up: shift items between new and old position down
                            foreach (var es in allExtraServices.Where(s => s.DisplayOrder >= newDisplayOrder && s.DisplayOrder < oldDisplayOrder))
                            {
                                es.DisplayOrder++;
                                es.UpdatedAt = DateTime.UtcNow;
                            }
                        }
                    }

                    // Update fields
                    extraService.Name = dto.Name.Trim();
                    extraService.ExtraServiceKey = extraServiceKey;
                    extraService.Description = dto.Description;
                    extraService.Price = dto.Price;
                    extraService.Duration = dto.Duration;
                    extraService.Icon = dto.Icon;
                    extraService.HasQuantity = dto.HasQuantity;
                    extraService.HasHours = dto.HasHours;
                    extraService.IsDeepCleaning = dto.IsDeepCleaning;
                    extraService.IsSuperDeepCleaning = dto.IsSuperDeepCleaning;
                    extraService.IsSameDayService = dto.IsSameDayService;
                    extraService.PriceMultiplier = priceMultiplier;
                    extraService.TimeMultiplier = timeMultiplier;
                    extraService.ServiceTypeId = dto.ServiceTypeId;
                    extraService.IsAvailableForAll = dto.IsAvailableForAll;
                    extraService.DisplayOrder = dto.DisplayOrder;
                    extraService.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // LOG THE UPDATE
                    await _auditService.LogUpdateAsync(originalExtraService, extraService);

                    return Ok(CatalogDtoMapper.ToExtraServiceDto(extraService));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error updating extra service", error = ex.Message });
                }
            }
        }

        public async Task<ActionResult> DeactivateExtraService(int id)
        {
            var extraService = await _context.ExtraServices.FindAsync(id);
            if (extraService == null)
                return NotFound();

            // CREATE A COPY WITH ALL CURRENT VALUES
            var originalExtraService = new ExtraService
            {
                Id = extraService.Id,
                Name = extraService.Name,
                Description = extraService.Description,
                Price = extraService.Price,
                Duration = extraService.Duration,
                Icon = extraService.Icon,
                HasQuantity = extraService.HasQuantity,
                HasHours = extraService.HasHours,
                IsDeepCleaning = extraService.IsDeepCleaning,
                IsSuperDeepCleaning = extraService.IsSuperDeepCleaning,
                IsSameDayService = extraService.IsSameDayService,
                PriceMultiplier = extraService.PriceMultiplier,
                ServiceTypeId = extraService.ServiceTypeId,
                IsAvailableForAll = extraService.IsAvailableForAll,
                DisplayOrder = extraService.DisplayOrder,
                IsActive = extraService.IsActive,
                CreatedAt = extraService.CreatedAt,
                UpdatedAt = extraService.UpdatedAt
            };

            extraService.IsActive = false;
            extraService.UpdatedAt = DateTime.UtcNow;

            // Save first
            await _context.SaveChangesAsync();

            // CREATE UPDATED COPY
            var updatedExtraService = new ExtraService
            {
                Id = extraService.Id,
                Name = extraService.Name,
                Description = extraService.Description,
                Price = extraService.Price,
                Duration = extraService.Duration,
                Icon = extraService.Icon,
                HasQuantity = extraService.HasQuantity,
                HasHours = extraService.HasHours,
                IsDeepCleaning = extraService.IsDeepCleaning,
                IsSuperDeepCleaning = extraService.IsSuperDeepCleaning,
                IsSameDayService = extraService.IsSameDayService,
                PriceMultiplier = extraService.PriceMultiplier,
                ServiceTypeId = extraService.ServiceTypeId,
                IsAvailableForAll = extraService.IsAvailableForAll,
                DisplayOrder = extraService.DisplayOrder,
                IsActive = extraService.IsActive,
                CreatedAt = extraService.CreatedAt,
                UpdatedAt = extraService.UpdatedAt
            };

            // LOG THE UPDATE
            try
            {
                await _auditService.LogUpdateAsync(originalExtraService, updatedExtraService);
            }
            catch (Exception auditEx)
            {
                _logger.LogError(auditEx, "Audit logging failed");
            }

            return Ok();
        }

        public async Task<ActionResult> ActivateExtraService(int id)
        {
            var extraService = await _context.ExtraServices.FindAsync(id);
            if (extraService == null)
                return NotFound();

            // CREATE A COPY WITH ALL CURRENT VALUES
            var originalExtraService = new ExtraService
            {
                Id = extraService.Id,
                Name = extraService.Name,
                Description = extraService.Description,
                Price = extraService.Price,
                Duration = extraService.Duration,
                Icon = extraService.Icon,
                HasQuantity = extraService.HasQuantity,
                HasHours = extraService.HasHours,
                IsDeepCleaning = extraService.IsDeepCleaning,
                IsSuperDeepCleaning = extraService.IsSuperDeepCleaning,
                IsSameDayService = extraService.IsSameDayService,
                PriceMultiplier = extraService.PriceMultiplier,
                ServiceTypeId = extraService.ServiceTypeId,
                IsAvailableForAll = extraService.IsAvailableForAll,
                DisplayOrder = extraService.DisplayOrder,
                IsActive = extraService.IsActive,
                CreatedAt = extraService.CreatedAt,
                UpdatedAt = extraService.UpdatedAt
            };

            extraService.IsActive = true;
            extraService.UpdatedAt = DateTime.UtcNow;

            // Save first
            await _context.SaveChangesAsync();

            // CREATE UPDATED COPY
            var updatedExtraService = new ExtraService
            {
                Id = extraService.Id,
                Name = extraService.Name,
                Description = extraService.Description,
                Price = extraService.Price,
                Duration = extraService.Duration,
                Icon = extraService.Icon,
                HasQuantity = extraService.HasQuantity,
                HasHours = extraService.HasHours,
                IsDeepCleaning = extraService.IsDeepCleaning,
                IsSuperDeepCleaning = extraService.IsSuperDeepCleaning,
                IsSameDayService = extraService.IsSameDayService,
                PriceMultiplier = extraService.PriceMultiplier,
                ServiceTypeId = extraService.ServiceTypeId,
                IsAvailableForAll = extraService.IsAvailableForAll,
                DisplayOrder = extraService.DisplayOrder,
                IsActive = extraService.IsActive,
                CreatedAt = extraService.CreatedAt,
                UpdatedAt = extraService.UpdatedAt
            };

            // LOG THE UPDATE
            try
            {
                await _auditService.LogUpdateAsync(originalExtraService, updatedExtraService);
            }
            catch (Exception auditEx)
            {
                _logger.LogError(auditEx, "Audit logging failed");
            }

            return Ok();
        }

        public async Task<ActionResult> DeleteExtraService(int id)
        {
            var extraService = await _context.ExtraServices
                .Include(es => es.OrderExtraServices)
                .FirstOrDefaultAsync(es => es.Id == id);

            if (extraService == null)
                return NotFound();

            // Check if there are any orders using this extra service
            if (extraService.OrderExtraServices.Any())
            {
                // CHANGED: Return JSON object instead of plain text
                return BadRequest(new { message = "Cannot delete extra service with existing orders. Please deactivate instead." });
            }

            await _auditService.LogDeleteAsync(extraService);

            _context.ExtraServices.Remove(extraService);
            await _context.SaveChangesAsync();

            return Ok();
        }

        // Subscriptions Management
        public async Task<ActionResult<List<SubscriptionDto>>> GetSubscriptions()
        {
            var subscriptions = await _context.Subscriptions
                .OrderBy(s => s.DisplayOrder)
                .Select(s => new SubscriptionDto
                {
                    Id = s.Id,
                    Name = s.Name,
                    Description = s.Description,
                    DiscountPercentage = s.DiscountPercentage,
                    SubscriptionDays = s.SubscriptionDays,
                    IsMostPopular = s.IsMostPopular,
                    IsActive = s.IsActive,
                    DisplayOrder = s.DisplayOrder
                })
                .ToListAsync();
            return Ok(subscriptions);
        }

        public async Task<ActionResult<SubscriptionDto>> CreateSubscription(CreateSubscriptionDto dto)
        {
            using (var transaction = await CatalogTransaction.BeginAsync(_context))
            {
                try
                {
                    // If display order is not provided or is 0, assign it to the end
                    if (dto.DisplayOrder <= 0)
                    {
                        var maxDisplayOrder = await _context.Subscriptions
                            .MaxAsync(s => (int?)s.DisplayOrder) ?? 0;
                        dto.DisplayOrder = maxDisplayOrder + 1;
                    }
                    else
                    {
                        // If a specific display order is provided, shift existing subscriptions
                        var existingSubscriptions = await _context.Subscriptions
                            .Where(s => s.DisplayOrder >= dto.DisplayOrder)
                            .ToListAsync();

                        foreach (var sub in existingSubscriptions)
                        {
                            sub.DisplayOrder++;
                            sub.UpdatedAt = DateTime.UtcNow;
                        }
                    }

                    var subscription = new Subscription
                    {
                        Name = dto.Name,
                        Description = dto.Description,
                        DiscountPercentage = dto.DiscountPercentage,
                        SubscriptionDays = dto.SubscriptionDays,
                        IsMostPopular = dto.IsMostPopular,
                        DisplayOrder = dto.DisplayOrder,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    };

                    var badgeMoves = dto.IsMostPopular
                        ? await ClearMostPopularOnOtherPlansAsync(exceptSubscriptionId: null)
                        : new List<(Subscription Before, Subscription After)>();

                    _context.Subscriptions.Add(subscription);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    await _auditService.LogCreateAsync(subscription);
                    foreach (var (before, after) in badgeMoves)
                        await _auditService.LogUpdateAsync(before, after);

                    return Ok(new SubscriptionDto
                    {
                        Id = subscription.Id,
                        Name = subscription.Name,
                        Description = subscription.Description,
                        DiscountPercentage = subscription.DiscountPercentage,
                        SubscriptionDays = subscription.SubscriptionDays,
                        IsMostPopular = subscription.IsMostPopular,
                        DisplayOrder = subscription.DisplayOrder,
                        IsActive = subscription.IsActive
                    });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error creating subscription", error = ex.Message });
                }
            }
        }

        public async Task<ActionResult<SubscriptionDto>> UpdateSubscription(int id, UpdateSubscriptionDto dto)
        {
            var subscription = await _context.Subscriptions.FindAsync(id);
            if (subscription == null)
            {
                return NotFound();
            }

            // Store original values for audit
            var originalSubscription = new Subscription
            {
                Id = subscription.Id,
                Name = subscription.Name,
                Description = subscription.Description,
                DiscountPercentage = subscription.DiscountPercentage,
                SubscriptionDays = subscription.SubscriptionDays,
                IsMostPopular = subscription.IsMostPopular,
                DisplayOrder = subscription.DisplayOrder,
                IsActive = subscription.IsActive,
                CreatedAt = subscription.CreatedAt,
                UpdatedAt = subscription.UpdatedAt
            };

            // Check if display order is changing
            bool isDisplayOrderChanging = subscription.DisplayOrder != dto.DisplayOrder;
            int oldDisplayOrder = subscription.DisplayOrder;
            int newDisplayOrder = dto.DisplayOrder;

            // Start a transaction for display order changes
            using (var transaction = await CatalogTransaction.BeginAsync(_context))
            {
                try
                {
                    // Handle display order changes
                    if (isDisplayOrderChanging && newDisplayOrder > 0)
                    {
                        // Get all subscriptions except the one being updated
                        var allSubscriptions = await _context.Subscriptions
                            .Where(s => s.Id != id)
                            .ToListAsync();

                        if (oldDisplayOrder < newDisplayOrder)
                        {
                            // Moving down: shift items between old and new position up
                            foreach (var sub in allSubscriptions.Where(s => s.DisplayOrder > oldDisplayOrder && s.DisplayOrder <= newDisplayOrder))
                            {
                                sub.DisplayOrder--;
                                sub.UpdatedAt = DateTime.UtcNow;
                            }
                        }
                        else if (oldDisplayOrder > newDisplayOrder)
                        {
                            // Moving up: shift items between new and old position down
                            foreach (var sub in allSubscriptions.Where(s => s.DisplayOrder >= newDisplayOrder && s.DisplayOrder < oldDisplayOrder))
                            {
                                sub.DisplayOrder++;
                                sub.UpdatedAt = DateTime.UtcNow;
                            }
                        }
                    }

                    // Update the subscription
                    subscription.Name = dto.Name;
                    subscription.Description = dto.Description;
                    subscription.DiscountPercentage = dto.DiscountPercentage;
                    subscription.SubscriptionDays = dto.SubscriptionDays;
                    subscription.DisplayOrder = dto.DisplayOrder;
                    // Absent keeps the stored badge (see UpdateSubscriptionDto.IsMostPopular).
                    if (dto.IsMostPopular.HasValue)
                        subscription.IsMostPopular = dto.IsMostPopular.Value;
                    subscription.UpdatedAt = DateTime.UtcNow;

                    var badgeMoves = subscription.IsMostPopular
                        ? await ClearMostPopularOnOtherPlansAsync(exceptSubscriptionId: subscription.Id)
                        : new List<(Subscription Before, Subscription After)>();

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // Log the update
                    await _auditService.LogUpdateAsync(originalSubscription, subscription);
                    foreach (var (before, after) in badgeMoves)
                        await _auditService.LogUpdateAsync(before, after);

                    return Ok(new SubscriptionDto
                    {
                        Id = subscription.Id,
                        Name = subscription.Name,
                        Description = subscription.Description,
                        DiscountPercentage = subscription.DiscountPercentage,
                        SubscriptionDays = subscription.SubscriptionDays,
                        IsMostPopular = subscription.IsMostPopular,
                        DisplayOrder = subscription.DisplayOrder,
                        IsActive = subscription.IsActive
                    });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error updating subscription", error = ex.Message });
                }
            }
        }

        /// <summary>
        /// At most ONE plan carries the "Most popular" badge: called whenever a create/update leaves a
        /// plan holding it, this takes it off every other plan (tracked, saved by the caller's
        /// SaveChanges). Returns before/after pairs so the caller can audit what moved.
        /// </summary>
        private async Task<List<(Subscription Before, Subscription After)>> ClearMostPopularOnOtherPlansAsync(int? exceptSubscriptionId)
        {
            var holders = await _context.Subscriptions
                .Where(s => s.IsMostPopular && (exceptSubscriptionId == null || s.Id != exceptSubscriptionId))
                .ToListAsync();

            var moves = new List<(Subscription Before, Subscription After)>();
            foreach (var other in holders)
            {
                var before = AuditSnapshot.Of(other);
                other.IsMostPopular = false;
                other.UpdatedAt = DateTime.UtcNow;
                moves.Add((before, other));
            }
            return moves;
        }

        public async Task<ActionResult> DeleteSubscription(int id)
        {
            var subscription = await _context.Subscriptions.FindAsync(id);
            if (subscription == null)
                return NotFound();

            // Check if subscription is being used by any orders or users
            var isUsedInOrders = await _context.Orders.AnyAsync(o => o.SubscriptionId == id);
            var isUsedByUsers = await _context.Users.AnyAsync(u => u.SubscriptionId == id);

            if (isUsedInOrders || isUsedByUsers)
            {
                return BadRequest(new { message = "Cannot delete subscription because it is being used by existing orders or users. Please deactivate it instead." });
            }

            // Log before deletion
            await _auditService.LogDeleteAsync(subscription);

            // Actually delete the subscription
            _context.Subscriptions.Remove(subscription);
            await _context.SaveChangesAsync();

            return Ok();
        }

        public async Task<ActionResult> DeactivateSubscription(int id)
        {
            try
            {
                var subscription = await _context.Subscriptions.FindAsync(id);
                if (subscription == null)
                {
                    return NotFound(new { message = "Subscription not found" });
                }

                // CREATE A COPY WITH ALL CURRENT VALUES
                var originalSubscription = new Subscription
                {
                    Id = subscription.Id,
                    Name = subscription.Name,
                    Description = subscription.Description,
                    DiscountPercentage = subscription.DiscountPercentage,
                    SubscriptionDays = subscription.SubscriptionDays,
                    IsMostPopular = subscription.IsMostPopular,
                    IsActive = subscription.IsActive,
                    DisplayOrder = subscription.DisplayOrder,
                    CreatedAt = subscription.CreatedAt,
                    UpdatedAt = subscription.UpdatedAt
                };

                subscription.IsActive = false;
                subscription.UpdatedAt = DateTime.UtcNow;

                // Save first
                await _context.SaveChangesAsync();

                // CREATE UPDATED COPY
                var updatedSubscription = new Subscription
                {
                    Id = subscription.Id,
                    Name = subscription.Name,
                    Description = subscription.Description,
                    DiscountPercentage = subscription.DiscountPercentage,
                    SubscriptionDays = subscription.SubscriptionDays,
                    IsMostPopular = subscription.IsMostPopular,
                    IsActive = subscription.IsActive,
                    DisplayOrder = subscription.DisplayOrder,
                    CreatedAt = subscription.CreatedAt,
                    UpdatedAt = subscription.UpdatedAt
                };

                // LOG THE UPDATE
                try
                {
                    await _auditService.LogUpdateAsync(originalSubscription, updatedSubscription);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Audit logging failed");
                }

                return Ok(new { message = "Subscription deactivated successfully", subscription });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error deactivating subscription", error = ex.Message });
            }
        }

        public async Task<ActionResult> ActivateSubscription(int id)
        {
            try
            {
                var subscription = await _context.Subscriptions.FindAsync(id);
                if (subscription == null)
                {
                    return NotFound(new { message = "Subscription not found" });
                }

                // CREATE A COPY WITH ALL CURRENT VALUES
                var originalSubscription = new Subscription
                {
                    Id = subscription.Id,
                    Name = subscription.Name,
                    Description = subscription.Description,
                    DiscountPercentage = subscription.DiscountPercentage,
                    SubscriptionDays = subscription.SubscriptionDays,
                    IsMostPopular = subscription.IsMostPopular,
                    IsActive = subscription.IsActive,
                    DisplayOrder = subscription.DisplayOrder,
                    CreatedAt = subscription.CreatedAt,
                    UpdatedAt = subscription.UpdatedAt
                };

                subscription.IsActive = true;
                subscription.UpdatedAt = DateTime.UtcNow;

                // Save first
                await _context.SaveChangesAsync();

                // CREATE UPDATED COPY
                var updatedSubscription = new Subscription
                {
                    Id = subscription.Id,
                    Name = subscription.Name,
                    Description = subscription.Description,
                    DiscountPercentage = subscription.DiscountPercentage,
                    SubscriptionDays = subscription.SubscriptionDays,
                    IsMostPopular = subscription.IsMostPopular,
                    IsActive = subscription.IsActive,
                    DisplayOrder = subscription.DisplayOrder,
                    CreatedAt = subscription.CreatedAt,
                    UpdatedAt = subscription.UpdatedAt
                };

                // LOG THE UPDATE
                try
                {
                    await _auditService.LogUpdateAsync(originalSubscription, updatedSubscription);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Audit logging failed");
                }

                return Ok(new { message = "Subscription activated successfully", subscription });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error activating subscription", error = ex.Message });
            }
        }

        // ===== Poll questions (the Services tab's per-type booking poll) =====
        // Moved from AdminPollsController in 2026-10 for the same reason as everything above;
        // that controller keeps its routes and permissions and delegates here.

        public async Task<ActionResult<PollQuestionDto>> CreatePollQuestion(CreatePollQuestionDto dto)
        {
            var question = new PollQuestion
            {
                Question = dto.Question,
                QuestionType = dto.QuestionType,
                Options = dto.Options,
                IsRequired = dto.IsRequired,
                DisplayOrder = dto.DisplayOrder,
                ServiceTypeId = dto.ServiceTypeId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _context.PollQuestions.Add(question);
            await _context.SaveChangesAsync();

            await _auditService.LogCreateAsync(question);

            var result = new PollQuestionDto
            {
                Id = question.Id,
                Question = question.Question,
                QuestionType = question.QuestionType,
                Options = question.Options,
                IsRequired = question.IsRequired,
                DisplayOrder = question.DisplayOrder,
                IsActive = question.IsActive,
                ServiceTypeId = question.ServiceTypeId
            };

            return Ok(result);
        }

        public async Task<ActionResult> UpdatePollQuestion(int id, PollQuestionDto dto)
        {
            var question = await _context.PollQuestions.FindAsync(id);
            if (question == null)
            {
                return NotFound();
            }

            var before = AuditSnapshot.Of(question);

            question.Question = dto.Question;
            question.QuestionType = dto.QuestionType;
            question.Options = dto.Options;
            question.IsRequired = dto.IsRequired;
            question.DisplayOrder = dto.DisplayOrder;
            question.IsActive = dto.IsActive;
            question.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _auditService.LogUpdateAsync(before, question);

            return Ok();
        }

        public async Task<ActionResult> DeletePollQuestion(int id)
        {
            var question = await _context.PollQuestions.FindAsync(id);
            if (question == null)
            {
                return NotFound();
            }

            await _auditService.LogDeleteAsync(question);

            _context.PollQuestions.Remove(question);
            await _context.SaveChangesAsync();
            return Ok();
        }
    }
}
