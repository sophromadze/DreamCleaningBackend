using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Hubs;
using DreamCleaningBackend.Services;
using Newtonsoft.Json;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class AdminController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IPermissionService _permissionService;
        private readonly IOrderService _orderService;
        private readonly IGiftCardService _giftCardService;
        private readonly IAuditService _auditService;

        public AdminController(ApplicationDbContext context, 
            IPermissionService permissionService, 
            IOrderService orderService, 
            IGiftCardService giftCardService, 
            IAuditService auditService)
        {
            _context = context;
            _permissionService = permissionService;
            _orderService = orderService;
            _giftCardService = giftCardService;
            _auditService = auditService;
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
                    Services = st.Services.Select(s => new ServiceDto
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
                        IsActive = s.IsActive
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
                    IsActive = es.IsActive
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
                    IsActive = es.IsActive
                }));

                result.Add(serviceTypeDto);
            }

            return Ok(result);
        }

        [HttpPost("service-types")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ServiceTypeDto>> CreateServiceType(CreateServiceTypeDto dto)
        {
            var serviceType = new ServiceType
            {
                Name = dto.Name,
                BasePrice = dto.BasePrice,
                Description = dto.Description,
                DisplayOrder = dto.DisplayOrder,
                IsActive = true,
                CreatedAt = DateTime.Now
            };

            _context.ServiceTypes.Add(serviceType);
            await _context.SaveChangesAsync();

            // LOG THE CREATION (after save to get the ID)
            await _auditService.LogCreateAsync(serviceType);

            return Ok(new ServiceTypeDto
            {
                Id = serviceType.Id,
                Name = serviceType.Name,
                BasePrice = serviceType.BasePrice,
                Description = serviceType.Description,
                IsActive = serviceType.IsActive
            });
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
                IsActive = serviceType.IsActive
            };

            serviceType.Name = dto.Name;
            serviceType.BasePrice = dto.BasePrice;
            serviceType.Description = dto.Description;
            serviceType.DisplayOrder = dto.DisplayOrder;
            serviceType.UpdatedAt = DateTime.Now;

            // LOG THE UPDATE
            await _auditService.LogUpdateAsync(originalServiceType, serviceType);

            await _context.SaveChangesAsync();

            return Ok(new ServiceTypeDto
            {
                Id = serviceType.Id,
                Name = serviceType.Name,
                BasePrice = serviceType.BasePrice,
                Description = serviceType.Description,
                IsActive = serviceType.IsActive
            });
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
                IsActive = serviceType.IsActive  // This will be true
            };

            serviceType.IsActive = false;
            serviceType.UpdatedAt = DateTime.Now;

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
                IsActive = serviceType.IsActive  // This will be false
            };

            serviceType.IsActive = true;
            serviceType.UpdatedAt = DateTime.Now;

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
                    IsActive = s.IsActive
                })
                .ToListAsync();

            return Ok(services);
        }

        [HttpPost("services")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ServiceDto>> CreateService(CreateServiceDto dto)
        {
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
                CreatedAt = DateTime.Now
            };

            _context.Services.Add(service);
            await _context.SaveChangesAsync();

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
                IsActive = service.IsActive
            });
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
                CreatedAt = DateTime.Now
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
            service.UpdatedAt = DateTime.Now;

            // LOG THE UPDATE
            await _auditService.LogUpdateAsync(originalService, service);

            await _context.SaveChangesAsync();

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
                IsActive = service.IsActive
            });
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
            service.UpdatedAt = DateTime.Now;

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
                Console.WriteLine($"Audit logging failed: {auditEx.Message}");
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
            service.UpdatedAt = DateTime.Now;

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
                Console.WriteLine($"Audit logging failed: {auditEx.Message}");
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
                    IsActive = es.IsActive
                })
                .ToListAsync();

            return Ok(extraServices);
        }

        [HttpPost("extra-services")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ExtraServiceDto>> CreateExtraService(CreateExtraServiceDto dto)
        {
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
                CreatedAt = DateTime.Now
            };

            _context.ExtraServices.Add(extraService);
            await _context.SaveChangesAsync();

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
                IsActive = extraService.IsActive
            });
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
                CreatedAt = DateTime.Now
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
            extraService.UpdatedAt = DateTime.Now;

            // LOG THE UPDATE
            await _auditService.LogUpdateAsync(originalExtraService, extraService);

            await _context.SaveChangesAsync();

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
                IsActive = extraService.IsActive
            });
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
            extraService.UpdatedAt = DateTime.Now;

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
                Console.WriteLine($"Audit logging failed: {auditEx.Message}");
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
            extraService.UpdatedAt = DateTime.Now;

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
                Console.WriteLine($"Audit logging failed: {auditEx.Message}");
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
                    IsActive = s.IsActive
                })
                .ToListAsync();
            return Ok(subscriptions);
        }

        [HttpPost("subscriptions")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<SubscriptionDto>> CreateSubscription(CreateSubscriptionDto dto)
        {
            var subscription = new Subscription
            {
                Name = dto.Name,
                Description = dto.Description,
                DiscountPercentage = dto.DiscountPercentage,
                SubscriptionDays = dto.SubscriptionDays,
                DisplayOrder = dto.DisplayOrder,
                IsActive = true,
                CreatedAt = DateTime.Now
            };
            _context.Subscriptions.Add(subscription);
            await _context.SaveChangesAsync();

            await _auditService.LogCreateAsync(subscription);

            return Ok(new SubscriptionDto
            {
                Id = subscription.Id,
                Name = subscription.Name,
                Description = subscription.Description,
                DiscountPercentage = subscription.DiscountPercentage,
                SubscriptionDays = subscription.SubscriptionDays,
                IsActive = subscription.IsActive
            });
        }

        [HttpPut("subscriptions/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<SubscriptionDto>> UpdateSubscription(int id, UpdateSubscriptionDto dto)
        {
            var subscription = await _context.Subscriptions.FindAsync(id);
            if (subscription == null)
                return NotFound();

            // CREATE A COPY FOR AUDITING
            var originalSubscription = new Subscription
            {
                Id = subscription.Id,
                Name = subscription.Name,
                Description = subscription.Description,
                DiscountPercentage = subscription.DiscountPercentage,
                SubscriptionDays = subscription.SubscriptionDays,
                DisplayOrder = subscription.DisplayOrder,
                IsActive = subscription.IsActive
            };

            subscription.Name = dto.Name;
            subscription.Description = dto.Description;
            subscription.DiscountPercentage = dto.DiscountPercentage;
            subscription.SubscriptionDays = dto.SubscriptionDays;
            subscription.DisplayOrder = dto.DisplayOrder;
            subscription.UpdatedAt = DateTime.Now;

            // LOG THE UPDATE
            await _auditService.LogUpdateAsync(originalSubscription, subscription);

            await _context.SaveChangesAsync();

            return Ok(new SubscriptionDto
            {
                Id = subscription.Id,
                Name = subscription.Name,
                Description = subscription.Description,
                DiscountPercentage = subscription.DiscountPercentage,
                SubscriptionDays = subscription.SubscriptionDays,
                IsActive = subscription.IsActive
            });
        }

        [HttpDelete("subscriptions/{id}")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteSubscription(int id)
        {
            var subscription = await _context.Subscriptions.FindAsync(id);
            if (subscription == null)
                return NotFound();

            subscription.IsActive = false;
            subscription.UpdatedAt = DateTime.Now;

            await _auditService.LogDeleteAsync(subscription);

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
                subscription.UpdatedAt = DateTime.Now;

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
                    Console.WriteLine($"Audit logging failed: {auditEx.Message}");
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
                subscription.UpdatedAt = DateTime.Now;

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
                    Console.WriteLine($"Audit logging failed: {auditEx.Message}");
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
                CreatedAt = DateTime.Now
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
            promoCode.UpdatedAt = DateTime.Now;

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
                promoCode.UpdatedAt = DateTime.Now;

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
                    Console.WriteLine($"Audit logging failed: {auditEx.Message}");
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
                promoCode.UpdatedAt = DateTime.Now;

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
                    Console.WriteLine($"Audit logging failed: {auditEx.Message}");
                }

                return Ok(new { message = "PromoCode activated successfully", promoCode });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error activating promocode", error = ex.Message });
            }
        }

        // Users Management (keeping existing)
        [HttpGet("users")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<UserAdminDto>>> GetUsers()
        {
            var currentUserRole = GetCurrentUserRole();

            var users = await _context.Users
                .Include(u => u.Subscription)
                .Select(u => new UserAdminDto
                {
                    Id = u.Id,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    Email = u.Email,
                    Phone = u.Phone,
                    Role = u.Role.ToString(),
                    AuthProvider = u.AuthProvider,
                    SubscriptionName = u.Subscription != null ? u.Subscription.Name : null,
                    FirstTimeOrder = u.FirstTimeOrder,
                    IsActive = u.IsActive,
                    CreatedAt = u.CreatedAt
                })
                .ToListAsync();

            // Include current user role in response for frontend to use
            return Ok(new
            {
                users = users,
                currentUserRole = currentUserRole.ToString()
            });

        }

        [HttpPut("users/{id}/role")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdateUserRole(int id, UpdateUserRoleDto dto)
        {
            Console.WriteLine($"Admin: Updating user {id} role to {dto.Role}");

            var currentUserRole = GetCurrentUserRole();
            var targetUser = await _context.Users.FindAsync(id);
            if (targetUser == null)
                return NotFound();

            // Create audit copy
            var originalUser = new User
            {
                Id = targetUser.Id,
                FirstName = targetUser.FirstName,
                LastName = targetUser.LastName,
                Email = targetUser.Email,
                Phone = targetUser.Phone,
                Role = targetUser.Role,
                IsActive = targetUser.IsActive,
                AuthProvider = targetUser.AuthProvider,
                FirstTimeOrder = targetUser.FirstTimeOrder
            };

            if (!Enum.TryParse<UserRole>(dto.Role, out var newRole))
                return BadRequest("Invalid role");

            var validationResult = ValidateRoleChange(currentUserRole, targetUser.Role, newRole);
            if (!validationResult.IsValid)
                return BadRequest(new { message = validationResult.ErrorMessage });

            targetUser.Role = newRole;
            targetUser.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            // Log audit
            try
            {
                await _auditService.LogUpdateAsync(originalUser, targetUser);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audit logging failed: {ex.Message}");
            }

            // Send notification and ensure it's delivered
            try
            {
                var userManagementService = HttpContext.RequestServices.GetRequiredService<IUserManagementService>();
                await userManagementService.NotifyUserRoleChanged(id, newRole.ToString());

                // Give time for the notification to be delivered via SignalR
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send role change notification: {ex.Message}");
            }

            return Ok(new { message = "Role updated successfully" });
        }

        [HttpGet("users/{id}/online-status")]
        [RequirePermission(Permission.View)]
        public ActionResult<bool> GetUserOnlineStatus(int id)
        {
            var isOnline = UserManagementHub.IsUserOnline(id);
            return Ok(new { userId = id, isOnline = isOnline });
        }

        private UserRole GetCurrentUserRole()
        {
            var roleClaim = User.FindFirst("Role")?.Value;
            Enum.TryParse<UserRole>(roleClaim, out var role);
            return role;
        }

        private (bool IsValid, string ErrorMessage) ValidateRoleChange(UserRole currentUserRole, UserRole targetCurrentRole, UserRole newRole)
        {
            // Moderators cannot change roles at all (they don't have Update permission, but double-check)
            if (currentUserRole == UserRole.Moderator)
                return (false, "Moderators cannot change user roles");

            // Admins cannot assign SuperAdmin role
            if (currentUserRole == UserRole.Admin && newRole == UserRole.SuperAdmin)
                return (false, "Admins cannot assign SuperAdmin role");

            // Admins cannot remove SuperAdmin role from a SuperAdmin
            if (currentUserRole == UserRole.Admin && targetCurrentRole == UserRole.SuperAdmin)
                return (false, "Admins cannot modify SuperAdmin users");

            // Users cannot demote themselves from SuperAdmin (optional safety check)
            var currentUserId = User.FindFirst("UserId")?.Value;
            if (currentUserId != null && targetCurrentRole == UserRole.SuperAdmin && newRole != UserRole.SuperAdmin)
            {
                // Check if user is trying to demote themselves
                // This is optional - you may want to allow SuperAdmins to demote themselves
                // return (false, "Cannot remove your own SuperAdmin role");
            }

            return (true, string.Empty);
        }

        [HttpPut("users/{id}/status")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdateUserStatus(int id, UpdateUserStatusDto dto)
        {
            Console.WriteLine($"Admin: Updating user {id} status to {dto.IsActive}");

            var targetUser = await _context.Users.FindAsync(id);
            if (targetUser == null)
                return NotFound();

            // Create audit copy
            var originalUser = new User
            {
                Id = targetUser.Id,
                FirstName = targetUser.FirstName,
                LastName = targetUser.LastName,
                Email = targetUser.Email,
                Phone = targetUser.Phone,
                Role = targetUser.Role,
                IsActive = targetUser.IsActive,
                AuthProvider = targetUser.AuthProvider,
                FirstTimeOrder = targetUser.FirstTimeOrder
            };

            var currentUserRole = GetCurrentUserRole();
            var targetUserRole = targetUser.Role;

            if (currentUserRole == UserRole.Admin && targetUserRole == UserRole.SuperAdmin)
            {
                return BadRequest(new { message = "Admins cannot modify SuperAdmin status" });
            }

            var currentUserId = int.Parse(User.FindFirst("UserId")?.Value ?? "0");
            if (currentUserId == id && !dto.IsActive)
            {
                return BadRequest(new { message = "You cannot deactivate yourself" });
            }

            targetUser.IsActive = dto.IsActive;
            targetUser.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            // Log audit
            try
            {
                await _auditService.LogUpdateAsync(originalUser, targetUser);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audit logging failed: {ex.Message}");
            }

            Console.WriteLine($"Admin: User {id} status updated in database");

            // Send notification
            try
            {
                var userManagementService = HttpContext.RequestServices.GetRequiredService<IUserManagementService>();

                if (!dto.IsActive)
                {
                    // User is being blocked
                    await userManagementService.NotifyUserBlocked(id, "Your account has been blocked by an administrator.");
                    // Give more time for block notification
                    await Task.Delay(2000);
                }
                else
                {
                    // User is being unblocked
                    await userManagementService.NotifyUserUnblocked(id);
                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send status change notification: {ex.Message}");
            }

            return Ok(new { message = $"User {(dto.IsActive ? "activated" : "deactivated")} successfully" });
        }

        [HttpGet("permissions")]
        [Authorize]
        public ActionResult<object> GetUserPermissions()
        {
            var roleClaim = User.FindFirst("Role")?.Value;
            if (!Enum.TryParse<UserRole>(roleClaim, out var userRole))
            {
                return BadRequest("Invalid role");
            }

            return Ok(new
            {
                role = userRole.ToString(),
                permissions = new
                {
                    canView = _permissionService.CanView(userRole),
                    canCreate = _permissionService.CanCreate(userRole),
                    canUpdate = _permissionService.CanUpdate(userRole),
                    canDelete = _permissionService.CanDelete(userRole),
                    canActivate = _permissionService.CanActivate(userRole),
                    canDeactivate = _permissionService.CanDeactivate(userRole)
                }
            });
        }

        // Orders Management
        [HttpGet("orders")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<OrderListDto>>> GetAllOrders()
        {
            try
            {
                var orders = await _orderService.GetAllOrdersForAdmin();
                return Ok(orders);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("orders/{orderId}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<OrderDto>> GetOrderDetails(int orderId)
        {
            try
            {
                // For admin, we don't need to check userId
                var order = await _context.Orders
                    .Include(o => o.ServiceType)
                    .Include(o => o.Subscription)
                    .Include(o => o.OrderServices)
                        .ThenInclude(os => os.Service)
                    .Include(o => o.OrderExtraServices)
                        .ThenInclude(oes => oes.ExtraService)
                    .FirstOrDefaultAsync(o => o.Id == orderId);

                if (order == null)
                    return NotFound();

                return new OrderDto
                {
                    Id = order.Id,
                    UserId = order.UserId,
                    ServiceTypeId = order.ServiceTypeId,
                    ServiceTypeName = order.ServiceType?.Name ?? "",
                    OrderDate = order.OrderDate,
                    ServiceDate = order.ServiceDate,
                    ServiceTime = order.ServiceTime,
                    Status = order.Status,
                    SubTotal = order.SubTotal,
                    Tax = order.Tax,
                    Tips = order.Tips,
                    Total = order.Total,
                    DiscountAmount = order.DiscountAmount,
                    SubscriptionDiscountAmount = order.SubscriptionDiscountAmount,
                    PromoCode = order.PromoCode,
                    GiftCardCode = order.GiftCardCode,
                    GiftCardAmountUsed = order.GiftCardAmountUsed,
                    SpecialOfferName = GetSpecialOfferName(order.PromoCode),
                    PromoCodeDetails = GetPromoCodeDetails(order.PromoCode),
                    GiftCardDetails = order.GiftCardCode != null ?
                    $"{MaskGiftCardCode(order.GiftCardCode)} (${order.GiftCardAmountUsed:F2})" : null,
                    SubscriptionId = order.SubscriptionId,
                    SubscriptionName = order.Subscription?.Name ?? "",
                    EntryMethod = order.EntryMethod,
                    SpecialInstructions = order.SpecialInstructions,
                    ContactFirstName = order.ContactFirstName,
                    ContactLastName = order.ContactLastName,
                    ContactEmail = order.ContactEmail,
                    ContactPhone = order.ContactPhone,
                    ServiceAddress = order.ServiceAddress,
                    AptSuite = order.AptSuite,
                    City = order.City,
                    State = order.State,
                    ZipCode = order.ZipCode,
                    TotalDuration = order.TotalDuration,
                    MaidsCount = order.MaidsCount,
                    IsPaid = order.IsPaid,
                    PaidAt = order.PaidAt,
                    Services = order.OrderServices?.Select(os => new OrderServiceDto
                    {
                        Id = os.Id,
                        ServiceId = os.ServiceId,
                        ServiceName = os.Service?.Name ?? "",
                        Quantity = os.Quantity,
                        Cost = os.Cost,
                        Duration = os.Duration
                    }).ToList() ?? new List<OrderServiceDto>(),
                    ExtraServices = order.OrderExtraServices?.Select(oes => new OrderExtraServiceDto
                    {
                        Id = oes.Id,
                        ExtraServiceId = oes.ExtraServiceId,
                        ExtraServiceName = oes.ExtraService?.Name ?? "",
                        Quantity = oes.Quantity,
                        Hours = oes.Hours,
                        Cost = oes.Cost,
                        Duration = oes.Duration
                    }).ToList() ?? new List<OrderExtraServiceDto>()
                };
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        private string? GetSpecialOfferName(string? promoCode)
        {
            if (string.IsNullOrEmpty(promoCode)) return null;

            if (promoCode.StartsWith("SPECIAL_OFFER:"))
            {
                return promoCode.Substring("SPECIAL_OFFER:".Length);
            }

            return null;
        }

        private string? GetPromoCodeDetails(string? promoCode)
        {
            if (string.IsNullOrEmpty(promoCode)) return null;

            if (promoCode.StartsWith("SPECIAL_OFFER:"))
            {
                return null;
            }

            if (promoCode == "firstUse")
            {
                return "First-Time Customer Discount";
            }

            return promoCode;
        }

        private string MaskGiftCardCode(string code)
        {
            if (code.Length >= 4)
            {
                return $"****-****-{code.Substring(code.Length - 4)}";
            }
            return "****";
        }

        [HttpPut("orders/{orderId}/status")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdateOrderStatus(int orderId, [FromBody] UpdateOrderStatusDto dto)
        {
            try
            {
                var order = await _context.Orders.FindAsync(orderId);
                if (order == null)
                    return NotFound();

                // CREATE A COPY FOR AUDITING with only relevant fields
                var originalOrder = new Order
                {
                    Id = order.Id,
                    Status = order.Status,
                    UserId = order.UserId,
                    Total = order.Total,
                    ServiceDate = order.ServiceDate,
                    ServiceTime = order.ServiceTime,
                    OrderDate = order.OrderDate,
                    ContactFirstName = order.ContactFirstName,
                    ContactLastName = order.ContactLastName,
                    ContactEmail = order.ContactEmail,
                    UpdatedAt = order.UpdatedAt
                };

                // Store the previous status for checking
                var previousStatus = order.Status;

                // Update the status
                order.Status = dto.Status;
                order.UpdatedAt = DateTime.Now; // Use UTC for consistency

                // Save changes FIRST
                await _context.SaveChangesAsync();

                // Handle special offer re-marking when reactivating from cancelled status
                if (previousStatus == "Cancelled" && dto.Status == "Active")
                {
                    // Check if order had a discount amount (indicating a special offer was used)
                    if (order.DiscountAmount > 0)
                    {
                        // Find any special offer for this user that might have been the one used
                        var userSpecialOffers = await _context.UserSpecialOffers
                            .Where(uso => uso.UserId == order.UserId && !uso.IsUsed)
                            .Include(uso => uso.SpecialOffer)
                            .ToListAsync();

                        // Try to find a special offer that matches the discount amount
                        var matchingOffer = userSpecialOffers.FirstOrDefault(uso =>
                            (uso.SpecialOffer.IsPercentage &&
                             Math.Round(order.SubTotal * (uso.SpecialOffer.DiscountValue / 100), 2) == order.DiscountAmount) ||
                            (!uso.SpecialOffer.IsPercentage &&
                             uso.SpecialOffer.DiscountValue == order.DiscountAmount));

                        if (matchingOffer != null)
                        {
                            matchingOffer.IsUsed = true;
                            matchingOffer.UsedAt = DateTime.Now;
                            matchingOffer.UsedOnOrderId = orderId;
                            await _context.SaveChangesAsync();

                            Console.WriteLine($"Re-marked special offer {matchingOffer.SpecialOfferId} as used for user {matchingOffer.UserId} after reactivating order {orderId}");
                        }
                    }
                }

                // Create a copy of the updated order for auditing
                var updatedOrder = new Order
                {
                    Id = order.Id,
                    Status = order.Status,
                    UserId = order.UserId,
                    Total = order.Total,
                    ServiceDate = order.ServiceDate,
                    ServiceTime = order.ServiceTime,
                    OrderDate = order.OrderDate,
                    ContactFirstName = order.ContactFirstName,
                    ContactLastName = order.ContactLastName,
                    ContactEmail = order.ContactEmail,
                    UpdatedAt = order.UpdatedAt
                };

                // LOG THE UPDATE AFTER saving
                try
                {
                    await _auditService.LogUpdateAsync(originalOrder, updatedOrder);
                }
                catch (Exception auditEx)
                {
                    // Log the audit failure but don't fail the main operation
                    Console.WriteLine($"Audit logging failed: {auditEx.Message}");
                }

                return Ok(new { message = $"Order status updated to {dto.Status}" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("orders/{orderId}/cancel")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> CancelOrder(int orderId, [FromBody] CancelOrderDto dto)
        {
            try
            {
                var order = await _context.Orders.FindAsync(orderId);
                if (order == null)
                    return NotFound();

                if (order.Status == "Cancelled" || order.Status == "Done")
                    return BadRequest(new { message = "Cannot cancel an order that is already cancelled or done." });

                // CREATE A COPY FOR AUDITING including cancellation fields
                var originalOrder = new Order
                {
                    Id = order.Id,
                    Status = order.Status,
                    CancellationReason = order.CancellationReason,
                    UserId = order.UserId,
                    Total = order.Total,
                    ServiceDate = order.ServiceDate,
                    ServiceTime = order.ServiceTime,
                    OrderDate = order.OrderDate,
                    ContactFirstName = order.ContactFirstName,
                    ContactLastName = order.ContactLastName,
                    ContactEmail = order.ContactEmail,
                    UpdatedAt = order.UpdatedAt
                };

                // Update the order with cancellation info
                order.Status = "Cancelled";
                order.CancellationReason = dto.Reason; // Save the reason
                order.UpdatedAt = DateTime.Now;

                await _context.SaveChangesAsync();

                // Restore special offer if one was used
                var userSpecialOffer = await _context.UserSpecialOffers
                    .FirstOrDefaultAsync(uso => uso.UsedOnOrderId == orderId);

                if (userSpecialOffer != null)
                {
                    userSpecialOffer.IsUsed = false;
                    userSpecialOffer.UsedAt = null;
                    userSpecialOffer.UsedOnOrderId = null;
                    await _context.SaveChangesAsync();

                    // Log that we restored the special offer
                    Console.WriteLine($"Restored special offer {userSpecialOffer.SpecialOfferId} for user {userSpecialOffer.UserId} after admin cancelled order {orderId}");
                }

                // Create updated copy for auditing
                var updatedOrder = new Order
                {
                    Id = order.Id,
                    Status = order.Status,
                    CancellationReason = order.CancellationReason,
                    UserId = order.UserId,
                    Total = order.Total,
                    ServiceDate = order.ServiceDate,
                    ServiceTime = order.ServiceTime,
                    OrderDate = order.OrderDate,
                    ContactFirstName = order.ContactFirstName,
                    ContactLastName = order.ContactLastName,
                    ContactEmail = order.ContactEmail,
                    UpdatedAt = order.UpdatedAt
                };

                // LOG THE UPDATE
                try
                {
                    await _auditService.LogUpdateAsync(originalOrder, updatedOrder);
                }
                catch (Exception auditEx)
                {
                    Console.WriteLine($"Audit logging failed: {auditEx.Message}");
                }

                return Ok(new { message = "Order cancelled successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("users/{userId}/orders")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<OrderListDto>>> GetUserOrders(int userId)
        {
            try
            {
                var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
                if (!userExists)
                    return NotFound(new { message = "User not found" });

                var orders = await _context.Orders
                    .Include(o => o.ServiceType)
                    .Where(o => o.UserId == userId)
                    .OrderByDescending(o => o.OrderDate)
                    .Select(o => new OrderListDto
                    {
                        Id = o.Id,
                        UserId = o.UserId,
                        ContactEmail = o.ContactEmail,
                        ContactFirstName = o.ContactFirstName,
                        ContactLastName = o.ContactLastName,
                        ServiceTypeName = o.ServiceType.Name,
                        ServiceDate = o.ServiceDate,
                        ServiceTime = o.ServiceTime,
                        Status = o.Status,
                        Total = o.Total,
                        ServiceAddress = o.ServiceAddress + (string.IsNullOrEmpty(o.AptSuite) ? "" : $", {o.AptSuite}"),
                        OrderDate = o.OrderDate
                    })
                    .ToListAsync();

                return Ok(orders);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("users/{userId}/apartments")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ApartmentDto>>> GetUserApartments(int userId)
        {
            try
            {
                var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
                if (!userExists)
                    return NotFound(new { message = "User not found" });

                var apartments = await _context.Apartments
                    .Where(a => a.UserId == userId && a.IsActive)
                    .OrderBy(a => a.Name)
                    .Select(a => new ApartmentDto
                    {
                        Id = a.Id,
                        Name = a.Name,
                        Address = a.Address,
                        AptSuite = a.AptSuite,
                        City = a.City,
                        State = a.State,
                        PostalCode = a.PostalCode,
                        SpecialInstructions = a.SpecialInstructions
                    })
                    .ToListAsync();

                return Ok(apartments);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("users/{userId}/profile")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<UserDetailDto>> GetUserProfile(int userId)
        {
            try
            {
                var user = await _context.Users
                    .Include(u => u.Subscription)
                    .Include(u => u.Apartments.Where(a => a.IsActive))
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null)
                    return NotFound(new { message = "User not found" });

                // Calculate user statistics from Orders table
                var userOrders = await _context.Orders
                    .Where(o => o.UserId == userId)
                    .ToListAsync();

                var totalOrders = userOrders.Count;
                var totalSpent = userOrders.Sum(o => o.Total);
                var lastOrderDate = userOrders.OrderByDescending(o => o.OrderDate).FirstOrDefault()?.OrderDate;

                var userDetail = new UserDetailDto
                {
                    Id = user.Id,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email,
                    Phone = user.Phone,
                    Role = user.Role.ToString(),
                    AuthProvider = user.AuthProvider,
                    IsActive = user.IsActive,
                    FirstTimeOrder = user.FirstTimeOrder,
                    SubscriptionId = user.SubscriptionId,
                    SubscriptionName = user.Subscription?.Name,
                    SubscriptionExpiryDate = user.SubscriptionExpiryDate,
                    CreatedAt = user.CreatedAt,
                    TotalOrders = totalOrders,
                    TotalSpent = totalSpent,
                    LastOrderDate = lastOrderDate,
                    ApartmentCount = user.Apartments.Count
                };

                return Ok(userDetail);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("gift-cards")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<GiftCardAdminDto>>> GetAllGiftCards()
        {
            try
            {
                var giftCards = await _giftCardService.GetAllGiftCardsForAdmin();
                return Ok(giftCards);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("gift-cards/{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<GiftCardAdminDto>> GetGiftCardDetails(int id)
        {
            try
            {
                var giftCard = await _context.GiftCards
                    .Include(g => g.PurchasedByUser)
                    .Include(g => g.GiftCardUsages)
                        .ThenInclude(u => u.User)
                    .Include(g => g.GiftCardUsages)
                        .ThenInclude(u => u.Order)
                    .FirstOrDefaultAsync(g => g.Id == id);

                if (giftCard == null)
                    return NotFound(new { message = "Gift card not found" });

                var dto = new GiftCardAdminDto
                {
                    Id = giftCard.Id,
                    Code = giftCard.Code,
                    OriginalAmount = giftCard.OriginalAmount,
                    CurrentBalance = giftCard.CurrentBalance,
                    RecipientName = giftCard.RecipientName,
                    RecipientEmail = giftCard.RecipientEmail,
                    SenderName = giftCard.SenderName,
                    SenderEmail = giftCard.SenderEmail,
                    Message = giftCard.Message,
                    IsActive = giftCard.IsActive,
                    IsPaid = giftCard.IsPaid,
                    CreatedAt = giftCard.CreatedAt,
                    PaidAt = giftCard.PaidAt,
                    PurchasedByUserName = giftCard.PurchasedByUser.FirstName + " " + giftCard.PurchasedByUser.LastName,
                    TotalAmountUsed = giftCard.OriginalAmount - giftCard.CurrentBalance,
                    TimesUsed = giftCard.GiftCardUsages.Count,
                    LastUsedAt = giftCard.GiftCardUsages.OrderByDescending(u => u.UsedAt).FirstOrDefault()?.UsedAt,
                    Usages = giftCard.GiftCardUsages.Select(u => new GiftCardUsageDto
                    {
                        Id = u.Id,
                        AmountUsed = u.AmountUsed,
                        BalanceAfterUsage = u.BalanceAfterUsage,
                        UsedAt = u.UsedAt,
                        OrderReference = $"Order #{u.OrderId} - ${u.Order.Total:F2}",
                        UsedByName = u.User.FirstName + " " + u.User.LastName,
                        UsedByEmail = u.User.Email
                    }).ToList()
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("gift-cards/{id}/deactivate")]
        [RequirePermission(Permission.Deactivate)]
        public async Task<ActionResult> DeactivateGiftCard(int id)
        {
            try
            {
                var giftCard = await _context.GiftCards.FindAsync(id);
                if (giftCard == null)
                    return NotFound(new { message = "Gift card not found" });

                // CREATE A COPY WITH ALL CURRENT VALUES
                var originalGiftCard = new GiftCard
                {
                    Id = giftCard.Id,
                    Code = giftCard.Code,
                    OriginalAmount = giftCard.OriginalAmount,
                    CurrentBalance = giftCard.CurrentBalance,
                    RecipientName = giftCard.RecipientName,
                    RecipientEmail = giftCard.RecipientEmail,
                    SenderName = giftCard.SenderName,
                    SenderEmail = giftCard.SenderEmail,
                    Message = giftCard.Message,
                    IsActive = giftCard.IsActive,
                    PurchasedByUserId = giftCard.PurchasedByUserId,
                    PaymentIntentId = giftCard.PaymentIntentId,
                    IsPaid = giftCard.IsPaid,
                    PaidAt = giftCard.PaidAt,
                    CreatedAt = giftCard.CreatedAt,
                    UpdatedAt = giftCard.UpdatedAt
                };

                giftCard.IsActive = false;
                giftCard.UpdatedAt = DateTime.Now;

                // Save first
                await _context.SaveChangesAsync();

                // CREATE UPDATED COPY
                var updatedGiftCard = new GiftCard
                {
                    Id = giftCard.Id,
                    Code = giftCard.Code,
                    OriginalAmount = giftCard.OriginalAmount,
                    CurrentBalance = giftCard.CurrentBalance,
                    RecipientName = giftCard.RecipientName,
                    RecipientEmail = giftCard.RecipientEmail,
                    SenderName = giftCard.SenderName,
                    SenderEmail = giftCard.SenderEmail,
                    Message = giftCard.Message,
                    IsActive = giftCard.IsActive,
                    PurchasedByUserId = giftCard.PurchasedByUserId,
                    PaymentIntentId = giftCard.PaymentIntentId,
                    IsPaid = giftCard.IsPaid,
                    PaidAt = giftCard.PaidAt,
                    CreatedAt = giftCard.CreatedAt,
                    UpdatedAt = giftCard.UpdatedAt
                };

                // LOG THE UPDATE
                try
                {
                    await _auditService.LogUpdateAsync(originalGiftCard, updatedGiftCard);
                }
                catch (Exception auditEx)
                {
                    Console.WriteLine($"Audit logging failed: {auditEx.Message}");
                }

                return Ok(new { message = "Gift card deactivated successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("gift-cards/{id}/activate")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> ActivateGiftCard(int id)
        {
            try
            {
                var giftCard = await _context.GiftCards.FindAsync(id);
                if (giftCard == null)
                    return NotFound(new { message = "Gift card not found" });

                // CREATE A COPY WITH ALL CURRENT VALUES
                var originalGiftCard = new GiftCard
                {
                    Id = giftCard.Id,
                    Code = giftCard.Code,
                    OriginalAmount = giftCard.OriginalAmount,
                    CurrentBalance = giftCard.CurrentBalance,
                    RecipientName = giftCard.RecipientName,
                    RecipientEmail = giftCard.RecipientEmail,
                    SenderName = giftCard.SenderName,
                    SenderEmail = giftCard.SenderEmail,
                    Message = giftCard.Message,
                    IsActive = giftCard.IsActive,
                    PurchasedByUserId = giftCard.PurchasedByUserId,
                    PaymentIntentId = giftCard.PaymentIntentId,
                    IsPaid = giftCard.IsPaid,
                    PaidAt = giftCard.PaidAt,
                    CreatedAt = giftCard.CreatedAt,
                    UpdatedAt = giftCard.UpdatedAt
                };

                giftCard.IsActive = true;
                giftCard.UpdatedAt = DateTime.Now;

                // Save first
                await _context.SaveChangesAsync();

                // CREATE UPDATED COPY
                var updatedGiftCard = new GiftCard
                {
                    Id = giftCard.Id,
                    Code = giftCard.Code,
                    OriginalAmount = giftCard.OriginalAmount,
                    CurrentBalance = giftCard.CurrentBalance,
                    RecipientName = giftCard.RecipientName,
                    RecipientEmail = giftCard.RecipientEmail,
                    SenderName = giftCard.SenderName,
                    SenderEmail = giftCard.SenderEmail,
                    Message = giftCard.Message,
                    IsActive = giftCard.IsActive,
                    PurchasedByUserId = giftCard.PurchasedByUserId,
                    PaymentIntentId = giftCard.PaymentIntentId,
                    IsPaid = giftCard.IsPaid,
                    PaidAt = giftCard.PaidAt,
                    CreatedAt = giftCard.CreatedAt,
                    UpdatedAt = giftCard.UpdatedAt
                };

                // LOG THE UPDATE
                try
                {
                    await _auditService.LogUpdateAsync(originalGiftCard, updatedGiftCard);
                }
                catch (Exception auditEx)
                {
                    Console.WriteLine($"Audit logging failed: {auditEx.Message}");
                }

                return Ok(new { message = "Gift card activated successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("audit-logs/{entityType}/{entityId}")]
        [RequirePermission(Permission.View)]
        public async Task<IActionResult> GetEntityHistory(string entityType, long entityId)
        {
            var history = await _auditService.GetEntityHistoryAsync(entityType, entityId);

            var result = history.Select(log => new
            {
                log.Id,
                log.Action,
                log.CreatedAt,
                ChangedBy = log.User?.FirstName + " " + log.User?.LastName,
                ChangedByEmail = log.User?.Email,
                OldValues = string.IsNullOrEmpty(log.OldValues) ? null : JsonConvert.DeserializeObject(log.OldValues),
                NewValues = string.IsNullOrEmpty(log.NewValues) ? null : JsonConvert.DeserializeObject(log.NewValues),
                ChangedFields = string.IsNullOrEmpty(log.ChangedFields) ? null : JsonConvert.DeserializeObject<List<string>>(log.ChangedFields)
            });

            return Ok(result);
        }

        [HttpGet("audit-logs")]
        [RequirePermission(Permission.View)]
        public async Task<IActionResult> GetRecentAuditLogs([FromQuery] int? days = 7)
        {
            var startDate = DateTime.Now.AddDays(-days.Value);

            var logs = await _context.AuditLogs
                .Where(a => a.CreatedAt >= startDate)
                .OrderByDescending(a => a.CreatedAt)
                .Include(a => a.User)
                .ToListAsync();

            var result = logs.Select(log => new
            {
                id = log.Id,
                entityType = log.EntityType,
                entityId = log.EntityId,
                action = log.Action,
                createdAt = log.CreatedAt,
                changedBy = log.User?.FirstName + " " + log.User?.LastName,
                changedByEmail = log.User?.Email,
                oldValues = log.OldValues,      // lowercase
                newValues = log.NewValues,      // lowercase
                changedFields = log.ChangedFields  // lowercase
            }).ToList();

            return Ok(result);
        }

        [HttpGet("users/{userId}/history")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> GetUserCompleteHistory(int userId)
        {
            // Get all audit logs related to this user
            var userLogs = await _auditService.GetEntityHistoryAsync("User", userId);

            // Get all orders by this user and their audit logs
            var userOrders = await _context.Orders
                .Where(o => o.UserId == userId)
                .Select(o => o.Id)
                .ToListAsync();

            var orderLogs = new List<AuditLog>();
            foreach (var orderId in userOrders)
            {
                var logs = await _auditService.GetEntityHistoryAsync("Order", orderId);
                orderLogs.AddRange(logs);
            }

            // Combine and format
            var allLogs = userLogs.Concat(orderLogs)
                .OrderByDescending(l => l.CreatedAt)
                .Select(log => new
                {
                    log.Id,
                    log.EntityType,
                    log.EntityId,
                    log.Action,
                    log.CreatedAt,
                    ChangedBy = log.User?.FirstName + " " + log.User?.LastName,
                    OldValues = string.IsNullOrEmpty(log.OldValues) ? null : JsonConvert.DeserializeObject(log.OldValues),
                    NewValues = string.IsNullOrEmpty(log.NewValues) ? null : JsonConvert.DeserializeObject(log.NewValues),
                    ChangedFields = string.IsNullOrEmpty(log.ChangedFields) ? null : JsonConvert.DeserializeObject<List<string>>(log.ChangedFields)
                });

            return Ok(allLogs);
        }
    }
}