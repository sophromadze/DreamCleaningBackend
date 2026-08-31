using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Attributes;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MaintenanceModeController : ControllerBase
    {
        private readonly IMaintenanceModeService _maintenanceModeService;
        private readonly IAuditService _auditService;

        public MaintenanceModeController(IMaintenanceModeService maintenanceModeService, IAuditService auditService)
        {
            _maintenanceModeService = maintenanceModeService;
            _auditService = auditService;
        }

        [HttpGet("status")]
        [AllowAnonymous]
        public async Task<ActionResult<MaintenanceModeDto>> GetStatus()
        {
            var status = await _maintenanceModeService.GetMaintenanceModeStatus();
            return Ok(status);
        }

        [HttpPost("toggle")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<MaintenanceModeDto>> ToggleMaintenanceMode([FromBody] ToggleMaintenanceModeDto dto)
        {
            var userEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "Unknown";

            // Read before the toggle: this takes the whole public site down, and "when did we go
            // into maintenance and who did it" is the question somebody asks afterwards.
            var before = await _maintenanceModeService.GetMaintenanceModeStatus();

            var status = await _maintenanceModeService.ToggleMaintenanceMode(dto, userEmail);

            await _auditService.LogActionAsync(
                DreamCleaningBackend.Services.AuditEntityTypes.SiteSetting, 0, "MaintenanceModeToggled",
                new { MaintenanceMode = before?.IsEnabled },
                new { MaintenanceMode = status?.IsEnabled, Message = dto?.Message });

            return Ok(status);
        }

        [HttpGet("is-enabled")]
        [AllowAnonymous]
        public async Task<ActionResult<bool>> IsEnabled()
        {
            var isEnabled = await _maintenanceModeService.IsMaintenanceModeEnabled();
            return Ok(isEnabled);
        }
    }
} 