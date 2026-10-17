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
using DreamCleaningBackend.Helpers;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>Catalog management: service types, services, extra services, subscriptions, promo codes.
    /// Split out of the monolithic AdminController; same api/admin route prefix, so URLs are unchanged.
    /// NOTE: the service-catalogue half (service types / services / thresholds / rate tiers /
    /// extra services / pricing configuration — the panel's "Services" tab) is SuperAdmin-only per
    /// action; the discounts half stays open to Admins. See the comment above GetServiceTypes.</summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminCatalogController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _auditService;
        private readonly ILogger<AdminCatalogController> _logger;
        private readonly IPricingConfigurationService _pricingConfigurationService;
        private readonly CatalogAdminOperations _operations;

        public AdminCatalogController(ApplicationDbContext context,
            IAuditService auditService,
            ILogger<AdminCatalogController> logger,
            IPricingConfigurationService pricingConfigurationService,
            CatalogAdminOperations? operations = null)
        {
            _context = context;
            _auditService = auditService;
            _logger = logger;
            _pricingConfigurationService = pricingConfigurationService;
            // DI always supplies it; the fallback keeps controller-level tests that build the
            // controller by hand working on the very same code.
            _operations = operations ?? new CatalogAdminOperations(context, auditService, logger);
        }

        // Service types, services, included amounts, rate tiers, extra services and subscription
        // plans: the routes, roles and permissions live HERE; the logic lives in
        // CatalogAdminOperations (moved verbatim 2026-10) so the Claude connector runs the same
        // code. Everything from here down to the Subscriptions section is the admin panel's
        // "Services" tab and is SuperAdmin-only per action, GETs included — see the comment at the
        // top of CatalogAdminOperations for why.

        [HttpGet("service-types")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ServiceTypeDto>>> GetServiceTypes()
        {
            return await _operations.GetServiceTypes();
        }

        [HttpPost("service-types")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ServiceTypeDto>> CreateServiceType(CreateServiceTypeDto dto)
        {
            return await _operations.CreateServiceType(dto);
        }

        [HttpPut("service-types/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ServiceTypeDto>> UpdateServiceType(int id, UpdateServiceTypeDto dto)
        {
            return await _operations.UpdateServiceType(id, dto);
        }

        [HttpPut("service-types/{id}/deactivate")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Deactivate)]
        public async Task<ActionResult> DeactivateServiceType(int id)
        {
            return await _operations.DeactivateServiceType(id);
        }

        [HttpPut("service-types/{id}/activate")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Activate)]
        public async Task<ActionResult> ActivateServiceType(int id)
        {
            return await _operations.ActivateServiceType(id);
        }

        [HttpDelete("service-types/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteServiceType(int id)
        {
            return await _operations.DeleteServiceType(id);
        }


        // ===== Pricing configuration export / import =====
        // Moves a validated pricing setup between environments. Everything resolves by
        // (ServiceType.Name, Service.ServiceKey) — never by Id, because production and local
        // have diverged on surrogate keys.

        /// <summary>Snapshot of pricing configuration. Omit serviceTypeId to export everything.</summary>
        [HttpGet("pricing-configuration/export")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<PricingConfigurationDto>> ExportPricingConfiguration(
            [FromQuery] int? serviceTypeId = null)
        {
            var config = await _pricingConfigurationService.ExportAsync(serviceTypeId);
            config.SourceNote = $"Exported from {Request.Host.Value} on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC";
            return Ok(config);
        }

        /// <summary>
        /// What an import WOULD change. Writes nothing. SuperAdmin-only like the apply step, so
        /// the two can't drift apart in who is allowed to use them.
        /// </summary>
        [HttpPost("pricing-configuration/preview")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<PricingConfigurationDiffDto>> PreviewPricingConfiguration(
            [FromBody] PricingConfigurationDto payload)
        {
            if (GetCurrentUserRole() != UserRole.SuperAdmin)
                return StatusCode(403, new { message = "Only a SuperAdmin can import pricing configuration." });

            return Ok(await _pricingConfigurationService.BuildDiffAsync(payload));
        }

        /// <summary>
        /// Applies a configuration in one transaction. Re-runs validation internally, so calling
        /// this without previewing first is safe — it just skips showing the admin the diff.
        /// </summary>
        [HttpPost("pricing-configuration/apply")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ApplyPricingConfigurationResultDto>> ApplyPricingConfiguration(
            [FromBody] PricingConfigurationDto payload)
        {
            if (GetCurrentUserRole() != UserRole.SuperAdmin)
                return StatusCode(403, new { message = "Only a SuperAdmin can import pricing configuration." });

            var result = await _pricingConfigurationService.ApplyAsync(payload, GetCurrentUserId());
            if (!result.Success)
                return BadRequest(result);

            // A pricing import rewrites many catalogue rows at once. It is recorded as ONE event
            // rather than as an Update per row: the decision an admin made was "apply this
            // configuration", and a hundred individual rows would hide it rather than explain it.
            // Undo-blocked for the same reason — reverting one row of a bulk import leaves the
            // catalogue in a state nobody chose.
            await _auditService.LogActionAsync(
                AuditEntityTypes.PricingConfiguration, 0, "PricingConfigurationApplied", null, new
                {
                    ServiceTypesAffected = result.ServiceTypesUpdated,
                    ServicesAffected = result.ServicesUpdated,
                    ThresholdsWritten = result.ThresholdsWritten,
                    RateTiersWritten = result.RateTiersWritten,
                    Summary = result.Message
                });

            return Ok(result);
        }

        [HttpGet("services")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ServiceDto>>> GetServices()
        {
            return await _operations.GetServices();
        }

        [HttpPost("services")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ServiceDto>> CreateService(CreateServiceDto dto)
        {
            return await _operations.CreateService(dto);
        }

        [HttpPost("services/copy")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ServiceDto>> CopyService(CopyServiceDto dto)
        {
            return await _operations.CopyService(dto);
        }

        [HttpPut("services/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ServiceDto>> UpdateService(int id, UpdateServiceDto dto)
        {
            return await _operations.UpdateService(id, dto);
        }

        [HttpPut("services/{id}/deactivate")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Deactivate)]
        public async Task<ActionResult> DeactivateService(int id)
        {
            return await _operations.DeactivateService(id);
        }

        [HttpPut("services/{id}/activate")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Activate)]
        public async Task<ActionResult> ActivateService(int id)
        {
            return await _operations.ActivateService(id);
        }

        [HttpDelete("services/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteService(int id)
        {
            return await _operations.DeleteService(id);
        }

        [HttpGet("services/{serviceId}/thresholds")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ServiceThresholdDto>>> GetServiceThresholds(int serviceId)
        {
            return await _operations.GetServiceThresholds(serviceId);
        }

        [HttpPost("services/{serviceId}/thresholds")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ServiceThresholdDto>> CreateServiceThreshold(
            int serviceId, SaveServiceThresholdDto dto)
        {
            return await _operations.CreateServiceThreshold(serviceId, dto);
        }

        [HttpPut("services/{serviceId}/thresholds/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ServiceThresholdDto>> UpdateServiceThreshold(
            int serviceId, int id, SaveServiceThresholdDto dto)
        {
            return await _operations.UpdateServiceThreshold(serviceId, id, dto);
        }

        [HttpDelete("services/{serviceId}/thresholds/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteServiceThreshold(int serviceId, int id)
        {
            return await _operations.DeleteServiceThreshold(serviceId, id);
        }

        [HttpGet("services/{serviceId}/rate-tiers")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ServiceRateTierDto>>> GetServiceRateTiers(int serviceId)
        {
            return await _operations.GetServiceRateTiers(serviceId);
        }

        [HttpPost("services/{serviceId}/rate-tiers")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ServiceRateTierDto>> CreateServiceRateTier(
            int serviceId, SaveServiceRateTierDto dto)
        {
            return await _operations.CreateServiceRateTier(serviceId, dto);
        }

        [HttpPut("services/{serviceId}/rate-tiers/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ServiceRateTierDto>> UpdateServiceRateTier(
            int serviceId, int id, SaveServiceRateTierDto dto)
        {
            return await _operations.UpdateServiceRateTier(serviceId, id, dto);
        }

        [HttpDelete("services/{serviceId}/rate-tiers/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteServiceRateTier(int serviceId, int id)
        {
            return await _operations.DeleteServiceRateTier(serviceId, id);
        }

        [HttpGet("extra-services")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ExtraServiceDto>>> GetExtraServices()
        {
            return await _operations.GetExtraServices();
        }

        [HttpPost("extra-services")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ExtraServiceDto>> CreateExtraService(CreateExtraServiceDto dto)
        {
            return await _operations.CreateExtraService(dto);
        }

        [HttpPost("extra-services/copy")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ExtraServiceDto>> CopyExtraService(CopyExtraServiceDto dto)
        {
            return await _operations.CopyExtraService(dto);
        }

        [HttpPut("extra-services/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ExtraServiceDto>> UpdateExtraService(int id, UpdateExtraServiceDto dto)
        {
            return await _operations.UpdateExtraService(id, dto);
        }

        [HttpPut("extra-services/{id}/deactivate")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Deactivate)]
        public async Task<ActionResult> DeactivateExtraService(int id)
        {
            return await _operations.DeactivateExtraService(id);
        }

        [HttpPut("extra-services/{id}/activate")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Activate)]
        public async Task<ActionResult> ActivateExtraService(int id)
        {
            return await _operations.ActivateExtraService(id);
        }

        [HttpDelete("extra-services/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteExtraService(int id)
        {
            return await _operations.DeleteExtraService(id);
        }

        [HttpGet("subscriptions")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<SubscriptionDto>>> GetSubscriptions()
        {
            return await _operations.GetSubscriptions();
        }

        [HttpPost("subscriptions")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<SubscriptionDto>> CreateSubscription(CreateSubscriptionDto dto)
        {
            return await _operations.CreateSubscription(dto);
        }

        [HttpPut("subscriptions/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<SubscriptionDto>> UpdateSubscription(int id, UpdateSubscriptionDto dto)
        {
            return await _operations.UpdateSubscription(id, dto);
        }

        [HttpDelete("subscriptions/{id}")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteSubscription(int id)
        {
            return await _operations.DeleteSubscription(id);
        }

        [HttpPost("subscriptions/{id}/deactivate")]
        [RequirePermission(Permission.Deactivate)]
        public async Task<ActionResult> DeactivateSubscription(int id)
        {
            return await _operations.DeactivateSubscription(id);
        }

        [HttpPost("subscriptions/{id}/activate")]
        [RequirePermission(Permission.Activate)]
        public async Task<ActionResult> ActivateSubscription(int id)
        {
            return await _operations.ActivateSubscription(id);
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

            // CurrentUsageCount is never incremented anywhere, so real usage is derived from
            // actual bookings instead — grouped on the order's stored PromoCode text (MySQL's
            // case-insensitive collation matches it against the code the same way the booking
            // flow validated it), restricted to OrderBookedFilter.IsRealBooking so a cancelled or
            // abandoned checkout doesn't inflate a code's numbers.
            var usageStats = await _context.Orders
                .Where(OrderBookedFilter.IsRealBooking)
                .Where(o => o.PromoCode != null && o.PromoCode != "")
                .GroupBy(o => o.PromoCode)
                .Select(g => new
                {
                    PromoCode = g.Key!,
                    TimesUsed = g.Count(),
                    UniqueUsersUsed = g.Select(o => o.UserId).Distinct().Count()
                })
                .ToListAsync();

            var usageByCode = usageStats.ToDictionary(u => u.PromoCode, u => u, StringComparer.OrdinalIgnoreCase);

            foreach (var dto in promoCodes)
            {
                if (usageByCode.TryGetValue(dto.Code, out var stats))
                {
                    dto.TimesUsed = stats.TimesUsed;
                    dto.UniqueUsersUsed = stats.UniqueUsersUsed;
                }
            }

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

            // Full scalar snapshot - see AuditSnapshot.
            var originalPromoCode = AuditSnapshot.Of(promoCode);

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
