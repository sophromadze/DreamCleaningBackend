using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/admin-bonus")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public class AdminBonusController : ControllerBase
    {
        private readonly IAdminBonusService _bonusService;

        public AdminBonusController(IAdminBonusService bonusService)
        {
            _bonusService = bonusService;
        }

        private int GetUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.Parse(claim!);
        }

        private bool IsSuperAdmin() =>
            User.IsInRole(UserRole.SuperAdmin.ToString());

        // GET /api/admin-bonus?from=&to=&adminId=
        // Admins see only their own row regardless of adminId/from/to.
        // SuperAdmins see every admin in the window; adminId filters to one.
        // Defaults: current calendar month, UTC.
        [HttpGet]
        public async Task<ActionResult<List<AdminBonusSummaryDto>>> GetBonuses(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] int? adminId)
        {
            var (fromDate, toDate) = ResolveMonthRange(from, to);
            var viewerId = GetUserId();
            var isSuper = IsSuperAdmin();
            var rows = await _bonusService.GetBonusesAsync(fromDate, toDate, viewerId, isSuper, adminId);
            return Ok(rows);
        }

        // GET /api/admin-bonus/admin/{adminId}?from=&to=  (defaults to all-time when omitted)
        // Convenience for the user-profile page; admins can only ask about themselves.
        [HttpGet("admin/{adminId}")]
        public async Task<ActionResult<AdminBonusSummaryDto>> GetForAdmin(
            int adminId,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            if (!IsSuperAdmin() && GetUserId() != adminId)
                return Forbid();

            try
            {
                var summary = await _bonusService.GetSummaryForAdminAsync(adminId, from, to);
                return Ok(summary);
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpGet("rate")]
        public async Task<ActionResult<AdminBonusRateDto>> GetRate()
        {
            return Ok(await _bonusService.GetRateAsync());
        }

        // SuperAdmin only — changing the rate ripples into future bonus computations.
        // Historical decisions remain readable via OrderAdminAssignmentHistory.BonusRateAtChange.
        [HttpPut("rate")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<AdminBonusRateDto>> SetRate([FromBody] SetAdminBonusRateDto dto)
        {
            try
            {
                var updated = await _bonusService.SetRateAsync(dto.RatePerOrder, GetUserId());
                return Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // ──────────────────────────────────────────────────────────────────────────────

        private static (DateTime From, DateTime To) ResolveMonthRange(DateTime? from, DateTime? to)
        {
            if (from.HasValue && to.HasValue)
                return (DateTime.SpecifyKind(from.Value.Date, DateTimeKind.Utc),
                        DateTime.SpecifyKind(to.Value.Date.AddDays(1), DateTimeKind.Utc));

            // Default to current UTC calendar month.
            var now = DateTime.UtcNow;
            var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var end = start.AddMonths(1);
            return (start, end);
        }
    }
}
