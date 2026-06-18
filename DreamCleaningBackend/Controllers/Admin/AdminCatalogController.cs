using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Hubs;
using System.Linq;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>Catalog management: service types, services, extra services, subscriptions, promo codes.
    /// Split out of the monolithic AdminController; same api/admin route prefix, so URLs are unchanged.</summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminCatalogController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _auditService;
        private readonly ILogger<AdminCatalogController> _logger;

        public AdminCatalogController(ApplicationDbContext context,
            IAuditService auditService,
            ILogger<AdminCatalogController> logger)
        {
            _context = context;
            _auditService = auditService;
            _logger = logger;
        }

        // Service Types Management
        [HttpGet("service-types")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ServiceTypeDto>>> GetServiceTypes()
        {
            var serviceTypes = await _context.ServiceTypes
                .Include(st => st.Services)
                .OrderBy(st => st.DisplayOrder)
                .ToListAsync();

            // Get all extra services that are available for all
            var universalExtraServices = await _context.ExtraServices
                .Where(es => es.IsAvailableForAll && es.ServiceTypeId == null)
                .OrderBy(es => es.DisplayOrder)
                .ToListAsync();

            var result = new List<ServiceTypeDto>();

            foreach (var st in serviceTypes)
            {
                // Get extra services specific to this service type
                var specificExtraServices = await _context.ExtraServices
                    .Where(es => es.ServiceTypeId == st.Id && !es.IsAvailableForAll)
                    .OrderBy(es => es.DisplayOrder)
                    .ToListAsync();

                var serviceTypeDto = new ServiceTypeDto
                {
                    Id = st.Id,
                    Name = st.Name,
                    BasePrice = st.BasePrice,
                    Description = st.Description,
                    IsActive = st.IsActive,
                    DisplayOrder = st.DisplayOrder,
                    HasPoll = st.HasPoll,
                    IsCustom = st.IsCustom,
                    TimeDuration = st.TimeDuration,
                    Services = st.Services
                        .OrderBy(s => s.DisplayOrder)
                        .Select(s => new ServiceDto
                        {
                            Id = s.Id,
                            Name = s.Name,
                            ServiceKey = s.ServiceKey,
                            Cost = s.Cost,
                            TimeDuration = s.TimeDuration,
                            ServiceTypeId = s.ServiceTypeId,
                            InputType = s.InputType,
                            MinValue = s.MinValue,
                            MaxValue = s.MaxValue,
                            StepValue = s.StepValue,
                            IsRangeInput = s.IsRangeInput,
                            Unit = s.Unit,
                            ServiceRelationType = s.ServiceRelationType,
                            IsActive = s.IsActive,
                            DisplayOrder = s.DisplayOrder
                        }).ToList(),
                    ExtraServices = new List<ExtraServiceDto>()
                };

                // Add specific extra services first
                serviceTypeDto.ExtraServices.AddRange(specificExtraServices.Select(es => new ExtraServiceDto
                {
                    Id = es.Id,
                    Name = es.Name,
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
                    IsAvailableForAll = es.IsAvailableForAll,
                    IsActive = es.IsActive,
                    DisplayOrder = es.DisplayOrder
                }));

                // Add universal extra services
                serviceTypeDto.ExtraServices.AddRange(universalExtraServices.Select(es => new ExtraServiceDto
                {
                    Id = es.Id,
                    Name = es.Name,
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
                    IsAvailableForAll = es.IsAvailableForAll,
                    IsActive = es.IsActive,
                    DisplayOrder = es.DisplayOrder
                }));

                // Sort the combined extra services by display order
                serviceTypeDto.ExtraServices = serviceTypeDto.ExtraServices
                    .OrderBy(es => es.DisplayOrder)
                    .ToList();

                result.Add(serviceTypeDto);
            }
            return Ok(result);
        }

        [HttpPost("service-types")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ServiceTypeDto>> CreateServiceType(CreateServiceTypeDto dto)
        {
            using (var transaction = await _context.Database.BeginTransactionAsync())
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
                        IsCustom = dto.IsCustom,
                        IsActive = true,
                        TimeDuration = dto.TimeDuration,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.ServiceTypes.Add(serviceType);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // LOG THE CREATION (after save to get the ID)
                    await _auditService.LogCreateAsync(serviceType);

                    return Ok(new ServiceTypeDto
                    {
                        Id = serviceType.Id,
                        Name = serviceType.Name,
                        BasePrice = serviceType.BasePrice,
                        Description = serviceType.Description,
                        DisplayOrder = serviceType.DisplayOrder,
                        HasPoll = serviceType.HasPoll,
                        IsCustom = serviceType.IsCustom,
                        IsActive = serviceType.IsActive,
                        TimeDuration = serviceType.TimeDuration
                    });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error creating service type", error = ex.Message });
                }
            }
        }

        [HttpPut("service-types/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ServiceTypeDto>> UpdateServiceType(int id, UpdateServiceTypeDto dto)
        {
            var serviceType = await _context.ServiceTypes.FindAsync(id);
            if (serviceType == null)
                return NotFound();

            // CREATE A COPY FOR AUDITING
            var originalServiceType = new ServiceType
            {
                Id = serviceType.Id,
                Name = serviceType.Name,
                BasePrice = serviceType.BasePrice,
                Description = serviceType.Description,
                DisplayOrder = serviceType.DisplayOrder,
                HasPoll = serviceType.HasPoll,
                IsCustom = serviceType.IsCustom,
                IsActive = serviceType.IsActive,
                TimeDuration = serviceType.TimeDuration
            };

            // Check if display order is changing
            bool isDisplayOrderChanging = serviceType.DisplayOrder != dto.DisplayOrder;
            int oldDisplayOrder = serviceType.DisplayOrder;
            int newDisplayOrder = dto.DisplayOrder;

            using (var transaction = await _context.Database.BeginTransactionAsync())
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
                    serviceType.IsCustom = dto.IsCustom;
                    serviceType.TimeDuration = dto.TimeDuration;
                    serviceType.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // LOG THE UPDATE
                    await _auditService.LogUpdateAsync(originalServiceType, serviceType);

                    return Ok(new ServiceTypeDto
                    {
                        Id = serviceType.Id,
                        Name = serviceType.Name,
                        BasePrice = serviceType.BasePrice,
                        Description = serviceType.Description,
                        DisplayOrder = serviceType.DisplayOrder,
                        HasPoll = serviceType.HasPoll,
                        IsCustom = serviceType.IsCustom,
                        IsActive = serviceType.IsActive,
                        TimeDuration = serviceType.TimeDuration
                    });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error updating service type", error = ex.Message });
                }
            }
        }

        [HttpPut("service-types/{id}/deactivate")]
        [RequirePermission(Permission.Deactivate)]
        public async Task<ActionResult> DeactivateServiceType(int id)
        {
            var serviceType = await _context.ServiceTypes.FindAsync(id);
            if (serviceType == null)
                return NotFound();

            // CREATE A COPY FOR AUDITING
            var originalServiceType = new ServiceType
            {
                Id = serviceType.Id,
                Name = serviceType.Name,
                BasePrice = serviceType.BasePrice,
                Description = serviceType.Description,
                DisplayOrder = serviceType.DisplayOrder,
                HasPoll = serviceType.HasPoll,
                IsCustom = serviceType.IsCustom,
                IsActive = serviceType.IsActive,
                TimeDuration = serviceType.TimeDuration
            };

            serviceType.IsActive = false;
            serviceType.UpdatedAt = DateTime.UtcNow;

            // LOG THE UPDATE (deactivation is an update)
            await _auditService.LogUpdateAsync(originalServiceType, serviceType);

            await _context.SaveChangesAsync();

            return Ok();
        }

        [HttpPut("service-types/{id}/activate")]
        [RequirePermission(Permission.Activate)]
        public async Task<ActionResult> ActivateServiceType(int id)
        {
            var serviceType = await _context.ServiceTypes.FindAsync(id);
            if (serviceType == null)
                return NotFound();

            // CREATE A COPY FOR AUDITING
            var originalServiceType = new ServiceType
            {
                Id = serviceType.Id,
                Name = serviceType.Name,
                BasePrice = serviceType.BasePrice,
                Description = serviceType.Description,
                DisplayOrder = serviceType.DisplayOrder,
                HasPoll = serviceType.HasPoll,
                IsCustom = serviceType.IsCustom,
                IsActive = serviceType.IsActive,
                TimeDuration = serviceType.TimeDuration
            };

            serviceType.IsActive = true;
            serviceType.UpdatedAt = DateTime.UtcNow;

            // LOG THE UPDATE (activation is an update)
            await _auditService.LogUpdateAsync(originalServiceType, serviceType);

            await _context.SaveChangesAsync();

            return Ok();
        }

        [HttpDelete("service-types/{id}")]
        [RequirePermission(Permission.Delete)]
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
        [HttpGet("services")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ServiceDto>>> GetServices()
        {
            var services = await _context.Services
                .OrderBy(s => s.ServiceTypeId)
                .ThenBy(s => s.DisplayOrder)
                .Select(s => new ServiceDto
                {
                    Id = s.Id,
                    Name = s.Name,
                    ServiceKey = s.ServiceKey,
                    Cost = s.Cost,
                    TimeDuration = s.TimeDuration,
                    ServiceTypeId = s.ServiceTypeId,
                    InputType = s.InputType,
                    MinValue = s.MinValue,
                    MaxValue = s.MaxValue,
                    StepValue = s.StepValue,
                    IsRangeInput = s.IsRangeInput,
                    Unit = s.Unit,
                    IsActive = s.IsActive,
                    DisplayOrder = s.DisplayOrder
                })
                .ToListAsync();

            return Ok(services);
        }

        [HttpPost("services")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ServiceDto>> CreateService(CreateServiceDto dto)
        {
            using (var transaction = await _context.Database.BeginTransactionAsync())
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
                        Name = dto.Name,
                        ServiceKey = dto.ServiceKey,
                        Cost = dto.Cost,
                        TimeDuration = dto.TimeDuration,
                        ServiceTypeId = dto.ServiceTypeId,
                        InputType = dto.InputType,
                        MinValue = dto.MinValue,
                        MaxValue = dto.MaxValue,
                        StepValue = dto.StepValue,
                        IsRangeInput = dto.IsRangeInput,
                        Unit = dto.Unit,
                        ServiceRelationType = dto.ServiceRelationType, // ADD THIS
                        DisplayOrder = dto.DisplayOrder,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.Services.Add(service);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    await _auditService.LogCreateAsync(service);

                    return Ok(new ServiceDto
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
                        ServiceRelationType = service.ServiceRelationType, // ADD THIS
                        DisplayOrder = service.DisplayOrder,
                        IsActive = service.IsActive
                    });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error creating service", error = ex.Message });
                }
            }
        }

        [HttpPost("services/copy")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ServiceDto>> CopyService(CopyServiceDto dto)
        {
            var sourceService = await _context.Services.FindAsync(dto.SourceServiceId);
            if (sourceService == null)
                return NotFound("Source service not found");

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
                ServiceRelationType = sourceService.ServiceRelationType, // ADD THIS
                DisplayOrder = sourceService.DisplayOrder,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _context.Services.Add(newService);
            await _context.SaveChangesAsync();

            return Ok(new ServiceDto
            {
                Id = newService.Id,
                Name = newService.Name,
                ServiceKey = newService.ServiceKey,
                Cost = newService.Cost,
                TimeDuration = newService.TimeDuration,
                ServiceTypeId = newService.ServiceTypeId,
                InputType = newService.InputType,
                MinValue = newService.MinValue,
                MaxValue = newService.MaxValue,
                StepValue = newService.StepValue,
                IsRangeInput = newService.IsRangeInput,
                Unit = newService.Unit,
                ServiceRelationType = newService.ServiceRelationType, // ADD THIS
                IsActive = newService.IsActive
            });
        }

        [HttpPut("services/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ServiceDto>> UpdateService(int id, UpdateServiceDto dto)
        {
            var service = await _context.Services.FindAsync(id);
            if (service == null)
                return NotFound();

            // CREATE A COPY FOR AUDITING
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
                IsActive = service.IsActive
            };

            // Check if display order is changing
            bool isDisplayOrderChanging = service.DisplayOrder != dto.DisplayOrder;
            int oldDisplayOrder = service.DisplayOrder;
            int newDisplayOrder = dto.DisplayOrder;

            using (var transaction = await _context.Database.BeginTransactionAsync())
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
                    service.Name = dto.Name;
                    service.ServiceKey = dto.ServiceKey;
                    service.Cost = dto.Cost;
                    service.TimeDuration = dto.TimeDuration;
                    service.ServiceTypeId = dto.ServiceTypeId;
                    service.InputType = dto.InputType;
                    service.MinValue = dto.MinValue;
                    service.MaxValue = dto.MaxValue;
                    service.StepValue = dto.StepValue;
                    service.IsRangeInput = dto.IsRangeInput;
                    service.Unit = dto.Unit;
                    service.ServiceRelationType = dto.ServiceRelationType;
                    service.DisplayOrder = dto.DisplayOrder;
                    service.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // LOG THE UPDATE
                    await _auditService.LogUpdateAsync(originalService, service);

                    return Ok(new ServiceDto
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
                        ServiceRelationType = service.ServiceRelationType, // ADD THIS
                        DisplayOrder = service.DisplayOrder,
                        IsActive = service.IsActive
                    });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error updating service", error = ex.Message });
                }
            }
        }

        [HttpPut("services/{id}/deactivate")]
        [RequirePermission(Permission.Deactivate)]
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

        [HttpPut("services/{id}/activate")]
        [RequirePermission(Permission.Activate)]
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

        [HttpDelete("services/{id}")]
        [RequirePermission(Permission.Delete)]
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

            await _auditService.LogDeleteAsync(service);

            _context.Services.Remove(service);
            await _context.SaveChangesAsync();

            return Ok();
        }

        [HttpGet("extra-services")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ExtraServiceDto>>> GetExtraServices()
        {
            var extraServices = await _context.ExtraServices
                .OrderBy(es => es.DisplayOrder)
                .Select(es => new ExtraServiceDto
                {
                    Id = es.Id,
                    Name = es.Name,
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
                    IsAvailableForAll = es.IsAvailableForAll,
                    IsActive = es.IsActive,
                    DisplayOrder = es.DisplayOrder
                })
                .ToListAsync();

            return Ok(extraServices);
        }

        [HttpPost("extra-services")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ExtraServiceDto>> CreateExtraService(CreateExtraServiceDto dto)
        {
            using (var transaction = await _context.Database.BeginTransactionAsync())
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
                        Name = dto.Name,
                        Description = dto.Description,
                        Price = dto.Price,
                        Duration = dto.Duration,
                        Icon = dto.Icon,
                        HasQuantity = dto.HasQuantity,
                        HasHours = dto.HasHours,
                        IsDeepCleaning = dto.IsDeepCleaning,
                        IsSuperDeepCleaning = dto.IsSuperDeepCleaning,
                        IsSameDayService = dto.IsSameDayService,
                        PriceMultiplier = dto.PriceMultiplier,
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

                    return Ok(new ExtraServiceDto
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
                        IsAvailableForAll = extraService.IsAvailableForAll,
                        DisplayOrder = extraService.DisplayOrder,
                        IsActive = extraService.IsActive
                    });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error creating extra service", error = ex.Message });
                }
            }
        }

        [HttpPost("extra-services/copy")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ExtraServiceDto>> CopyExtraService(CopyExtraServiceDto dto)
        {
            var sourceExtraService = await _context.ExtraServices.FindAsync(dto.SourceExtraServiceId);
            if (sourceExtraService == null)
                return NotFound("Source extra service not found");

            var newExtraService = new ExtraService
            {
                Name = sourceExtraService.Name,
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
                ServiceTypeId = dto.TargetServiceTypeId,
                IsAvailableForAll = false, // When copying to specific service type
                DisplayOrder = sourceExtraService.DisplayOrder,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _context.ExtraServices.Add(newExtraService);
            await _context.SaveChangesAsync();

            return Ok(new ExtraServiceDto
            {
                Id = newExtraService.Id,
                Name = newExtraService.Name,
                Description = newExtraService.Description,
                Price = newExtraService.Price,
                Duration = newExtraService.Duration,
                Icon = newExtraService.Icon,
                HasQuantity = newExtraService.HasQuantity,
                HasHours = newExtraService.HasHours,
                IsDeepCleaning = newExtraService.IsDeepCleaning,
                IsSuperDeepCleaning = newExtraService.IsSuperDeepCleaning,
                IsSameDayService = newExtraService.IsSameDayService,
                PriceMultiplier = newExtraService.PriceMultiplier,
                IsAvailableForAll = newExtraService.IsAvailableForAll,
                IsActive = newExtraService.IsActive
            });
        }

        [HttpPut("extra-services/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ExtraServiceDto>> UpdateExtraService(int id, UpdateExtraServiceDto dto)
        {
            var extraService = await _context.ExtraServices.FindAsync(id);
            if (extraService == null)
                return NotFound();

            // CREATE A COPY FOR AUDITING
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
                IsActive = extraService.IsActive
            };

            // Check if display order is changing
            bool isDisplayOrderChanging = extraService.DisplayOrder != dto.DisplayOrder;
            int oldDisplayOrder = extraService.DisplayOrder;
            int newDisplayOrder = dto.DisplayOrder;

            using (var transaction = await _context.Database.BeginTransactionAsync())
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
                    extraService.Name = dto.Name;
                    extraService.Description = dto.Description;
                    extraService.Price = dto.Price;
                    extraService.Duration = dto.Duration;
                    extraService.Icon = dto.Icon;
                    extraService.HasQuantity = dto.HasQuantity;
                    extraService.HasHours = dto.HasHours;
                    extraService.IsDeepCleaning = dto.IsDeepCleaning;
                    extraService.IsSuperDeepCleaning = dto.IsSuperDeepCleaning;
                    extraService.IsSameDayService = dto.IsSameDayService;
                    extraService.PriceMultiplier = dto.PriceMultiplier;
                    extraService.ServiceTypeId = dto.ServiceTypeId;
                    extraService.IsAvailableForAll = dto.IsAvailableForAll;
                    extraService.DisplayOrder = dto.DisplayOrder;
                    extraService.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // LOG THE UPDATE
                    await _auditService.LogUpdateAsync(originalExtraService, extraService);

                    return Ok(new ExtraServiceDto
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
                        IsAvailableForAll = extraService.IsAvailableForAll,
                        DisplayOrder = extraService.DisplayOrder,
                        IsActive = extraService.IsActive
                    });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return StatusCode(500, new { message = "Error updating extra service", error = ex.Message });
                }
            }
        }

        [HttpPut("extra-services/{id}/deactivate")]
        [RequirePermission(Permission.Deactivate)]
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

        [HttpPut("extra-services/{id}/activate")]
        [RequirePermission(Permission.Activate)]
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

        [HttpDelete("extra-services/{id}")]
        [RequirePermission(Permission.Delete)]
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
        [HttpGet("subscriptions")]
        [RequirePermission(Permission.View)]
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
                    IsActive = s.IsActive,
                    DisplayOrder = s.DisplayOrder
                })
                .ToListAsync();
            return Ok(subscriptions);
        }

        [HttpPost("subscriptions")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<SubscriptionDto>> CreateSubscription(CreateSubscriptionDto dto)
        {
            using (var transaction = await _context.Database.BeginTransactionAsync())
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
                        DisplayOrder = dto.DisplayOrder,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.Subscriptions.Add(subscription);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    await _auditService.LogCreateAsync(subscription);

                    return Ok(new SubscriptionDto
                    {
                        Id = subscription.Id,
                        Name = subscription.Name,
                        Description = subscription.Description,
                        DiscountPercentage = subscription.DiscountPercentage,
                        SubscriptionDays = subscription.SubscriptionDays,
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

        [HttpPut("subscriptions/{id}")]
        [RequirePermission(Permission.Update)]
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
            using (var transaction = await _context.Database.BeginTransactionAsync())
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
                    subscription.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // Log the update
                    await _auditService.LogUpdateAsync(originalSubscription, subscription);

                    return Ok(new SubscriptionDto
                    {
                        Id = subscription.Id,
                        Name = subscription.Name,
                        Description = subscription.Description,
                        DiscountPercentage = subscription.DiscountPercentage,
                        SubscriptionDays = subscription.SubscriptionDays,
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

        [HttpDelete("subscriptions/{id}")]
        [RequirePermission(Permission.Delete)]
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

        [HttpPost("subscriptions/{id}/deactivate")]
        [RequirePermission(Permission.Deactivate)]
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

        [HttpPost("subscriptions/{id}/activate")]
        [RequirePermission(Permission.Activate)]
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

        // Promo Codes Management (keeping existing)
        [HttpGet("promo-codes")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<PromoCodeDto>>> GetPromoCodes()
        {
            var promoCodes = await _context.PromoCodes
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new PromoCodeDto
                {
                    Id = p.Id,
                    Code = p.Code,
                    Description = p.Description,
                    IsPercentage = p.IsPercentage,
                    DiscountValue = p.DiscountValue,
                    MaxUsageCount = p.MaxUsageCount,
                    CurrentUsageCount = p.CurrentUsageCount,
                    MaxUsagePerUser = p.MaxUsagePerUser,
                    ValidFrom = p.ValidFrom,
                    ValidTo = p.ValidTo,
                    MinimumOrderAmount = p.MinimumOrderAmount,
                    IsActive = p.IsActive
                })
                .ToListAsync();

            return Ok(promoCodes);
        }

        [HttpPost("promo-codes")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<PromoCodeDto>> CreatePromoCode(CreatePromoCodeDto dto)
        {
            var promoCode = new PromoCode
            {
                Code = dto.Code.ToUpper(),
                Description = dto.Description,
                IsPercentage = dto.IsPercentage,
                DiscountValue = dto.DiscountValue,
                MaxUsageCount = dto.MaxUsageCount,
                MaxUsagePerUser = dto.MaxUsagePerUser,
                ValidFrom = dto.ValidFrom,
                ValidTo = dto.ValidTo,
                MinimumOrderAmount = dto.MinimumOrderAmount,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _context.PromoCodes.Add(promoCode);
            await _context.SaveChangesAsync();

            await _auditService.LogCreateAsync(promoCode);

            return Ok(new PromoCodeDto
            {
                Id = promoCode.Id,
                Code = promoCode.Code,
                Description = promoCode.Description,
                IsPercentage = promoCode.IsPercentage,
                DiscountValue = promoCode.DiscountValue,
                MaxUsageCount = promoCode.MaxUsageCount,
                CurrentUsageCount = promoCode.CurrentUsageCount,
                MaxUsagePerUser = promoCode.MaxUsagePerUser,
                ValidFrom = promoCode.ValidFrom,
                ValidTo = promoCode.ValidTo,
                MinimumOrderAmount = promoCode.MinimumOrderAmount,
                IsActive = promoCode.IsActive
            });
        }

        [HttpPut("promo-codes/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<PromoCodeDto>> UpdatePromoCode(int id, UpdatePromoCodeDto dto)
        {
            var promoCode = await _context.PromoCodes.FindAsync(id);
            if (promoCode == null)
                return NotFound();

            // CREATE A COPY FOR AUDITING
            var originalPromoCode = new PromoCode
            {
                Id = promoCode.Id,
                Code = promoCode.Code,
                Description = promoCode.Description,
                IsPercentage = promoCode.IsPercentage,
                DiscountValue = promoCode.DiscountValue,
                MaxUsageCount = promoCode.MaxUsageCount,
                CurrentUsageCount = promoCode.CurrentUsageCount,
                MaxUsagePerUser = promoCode.MaxUsagePerUser,
                ValidFrom = promoCode.ValidFrom,
                ValidTo = promoCode.ValidTo,
                MinimumOrderAmount = promoCode.MinimumOrderAmount,
                IsActive = promoCode.IsActive
            };

            promoCode.Description = dto.Description;
            promoCode.IsPercentage = dto.IsPercentage;
            promoCode.DiscountValue = dto.DiscountValue;
            promoCode.MaxUsageCount = dto.MaxUsageCount;
            promoCode.MaxUsagePerUser = dto.MaxUsagePerUser;
            promoCode.ValidFrom = dto.ValidFrom;
            promoCode.ValidTo = dto.ValidTo;
            promoCode.MinimumOrderAmount = dto.MinimumOrderAmount;
            promoCode.UpdatedAt = DateTime.UtcNow;

            // LOG THE UPDATE
            await _auditService.LogUpdateAsync(originalPromoCode, promoCode);

            await _context.SaveChangesAsync();

            return Ok(new PromoCodeDto
            {
                Id = promoCode.Id,
                Code = promoCode.Code,
                Description = promoCode.Description,
                IsPercentage = promoCode.IsPercentage,
                DiscountValue = promoCode.DiscountValue,
                MaxUsageCount = promoCode.MaxUsageCount,
                CurrentUsageCount = promoCode.CurrentUsageCount,
                MaxUsagePerUser = promoCode.MaxUsagePerUser,
                ValidFrom = promoCode.ValidFrom,
                ValidTo = promoCode.ValidTo,
                MinimumOrderAmount = promoCode.MinimumOrderAmount,
                IsActive = promoCode.IsActive
            });
        }

        [HttpDelete("promo-codes/{id}")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeletePromoCode(int id)
        {
            var promoCode = await _context.PromoCodes.FindAsync(id);
            if (promoCode == null)
                return NotFound();

            await _auditService.LogDeleteAsync(promoCode);

            _context.PromoCodes.Remove(promoCode);
            await _context.SaveChangesAsync();

            return Ok();
        }

        [HttpPost("promo-codes/{id}/deactivate")]
        [RequirePermission(Permission.Deactivate)]
        public async Task<ActionResult> DeactivatePromoCode(int id)
        {
            try
            {
                var promoCode = await _context.PromoCodes.FindAsync(id);
                if (promoCode == null)
                {
                    return NotFound(new { message = "PromoCode not found" });
                }

                // CREATE A COPY WITH ALL CURRENT VALUES
                var originalPromoCode = new PromoCode
                {
                    Id = promoCode.Id,
                    Code = promoCode.Code,
                    Description = promoCode.Description,
                    IsPercentage = promoCode.IsPercentage,
                    DiscountValue = promoCode.DiscountValue,
                    MaxUsageCount = promoCode.MaxUsageCount,
                    CurrentUsageCount = promoCode.CurrentUsageCount,
                    MaxUsagePerUser = promoCode.MaxUsagePerUser,
                    ValidFrom = promoCode.ValidFrom,
                    ValidTo = promoCode.ValidTo,
                    MinimumOrderAmount = promoCode.MinimumOrderAmount,
                    IsActive = promoCode.IsActive,
                    CreatedAt = promoCode.CreatedAt,
                    UpdatedAt = promoCode.UpdatedAt
                };

                promoCode.IsActive = false;
                promoCode.UpdatedAt = DateTime.UtcNow;

                // Save first
                await _context.SaveChangesAsync();

                // CREATE UPDATED COPY
                var updatedPromoCode = new PromoCode
                {
                    Id = promoCode.Id,
                    Code = promoCode.Code,
                    Description = promoCode.Description,
                    IsPercentage = promoCode.IsPercentage,
                    DiscountValue = promoCode.DiscountValue,
                    MaxUsageCount = promoCode.MaxUsageCount,
                    CurrentUsageCount = promoCode.CurrentUsageCount,
                    MaxUsagePerUser = promoCode.MaxUsagePerUser,
                    ValidFrom = promoCode.ValidFrom,
                    ValidTo = promoCode.ValidTo,
                    MinimumOrderAmount = promoCode.MinimumOrderAmount,
                    IsActive = promoCode.IsActive,
                    CreatedAt = promoCode.CreatedAt,
                    UpdatedAt = promoCode.UpdatedAt
                };

                // LOG THE UPDATE
                try
                {
                    await _auditService.LogUpdateAsync(originalPromoCode, updatedPromoCode);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Audit logging failed");
                }

                return Ok(new { message = "PromoCode deactivated successfully", promoCode });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error deactivating promocode", error = ex.Message });
            }
        }

        [HttpPost("promo-codes/{id}/activate")]
        [RequirePermission(Permission.Activate)]
        public async Task<ActionResult> ActivatePromoCode(int id)
        {
            try
            {
                var promoCode = await _context.PromoCodes.FindAsync(id);
                if (promoCode == null)
                {
                    return NotFound(new { message = "PromoCode not found" });
                }

                // CREATE A COPY WITH ALL CURRENT VALUES
                var originalPromoCode = new PromoCode
                {
                    Id = promoCode.Id,
                    Code = promoCode.Code,
                    Description = promoCode.Description,
                    IsPercentage = promoCode.IsPercentage,
                    DiscountValue = promoCode.DiscountValue,
                    MaxUsageCount = promoCode.MaxUsageCount,
                    CurrentUsageCount = promoCode.CurrentUsageCount,
                    MaxUsagePerUser = promoCode.MaxUsagePerUser,
                    ValidFrom = promoCode.ValidFrom,
                    ValidTo = promoCode.ValidTo,
                    MinimumOrderAmount = promoCode.MinimumOrderAmount,
                    IsActive = promoCode.IsActive,
                    CreatedAt = promoCode.CreatedAt,
                    UpdatedAt = promoCode.UpdatedAt
                };

                promoCode.IsActive = true;
                promoCode.UpdatedAt = DateTime.UtcNow;

                // Save first
                await _context.SaveChangesAsync();

                // CREATE UPDATED COPY
                var updatedPromoCode = new PromoCode
                {
                    Id = promoCode.Id,
                    Code = promoCode.Code,
                    Description = promoCode.Description,
                    IsPercentage = promoCode.IsPercentage,
                    DiscountValue = promoCode.DiscountValue,
                    MaxUsageCount = promoCode.MaxUsageCount,
                    CurrentUsageCount = promoCode.CurrentUsageCount,
                    MaxUsagePerUser = promoCode.MaxUsagePerUser,
                    ValidFrom = promoCode.ValidFrom,
                    ValidTo = promoCode.ValidTo,
                    MinimumOrderAmount = promoCode.MinimumOrderAmount,
                    IsActive = promoCode.IsActive,
                    CreatedAt = promoCode.CreatedAt,
                    UpdatedAt = promoCode.UpdatedAt
                };

                // LOG THE UPDATE
                try
                {
                    await _auditService.LogUpdateAsync(originalPromoCode, updatedPromoCode);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Audit logging failed");
                }

                return Ok(new { message = "PromoCode activated successfully", promoCode });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error activating promocode", error = ex.Message });
            }
        }

    }
}
