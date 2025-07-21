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

        public MaintenanceModeController(IMaintenanceModeService maintenanceModeService)
        {
            _maintenanceModeService = maintenanceModeService;
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
            var status = await _maintenanceModeService.ToggleMaintenanceMode(dto, userEmail);
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