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
        private readonly IAuditService _auditService;

        public AdminBonusController(IAdminBonusService bonusService, IAuditService auditService)
        {
            _bonusService = bonusService;
            _auditService = auditService;
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

        // The company-wide defaults — SuperAdmin only (owner's call, 2026-08-31). This is the whole
        // pay table: what an administrator earns, what a manager earns, and therefore what every
        // colleague is on. A staff member sees their OWN rates on their own row of GET
        // /api/admin-bonus (which already narrows to the viewer for anyone who is not a SuperAdmin),
        // and that is the only rate that is theirs to know.
        //
        // The role check has to live here, not only on the panel: the endpoint is the thing that
        // discloses, and a hidden block in the UI is not access control.
        [HttpGet("rates")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<AdminBonusRatesDto>> GetRates()
        {
            return Ok(await _bonusService.GetRatesAsync());
        }

        // SuperAdmin only — rates ripple into every future AND past bonus computation, which is
        // what makes correcting a mistyped rate actually fix the affected payouts.
        // OrderAdminAssignmentHistory.BonusRateAtChange keeps what was in force at assignment time.
        [HttpPut("rates")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<AdminBonusRatesDto>> SetRates([FromBody] SetAdminBonusRatesDto dto)
        {
            try
            {
                // Read first — what the rates moved FROM is the interesting half of the audit entry.
                var previous = await _bonusService.GetRatesAsync();
                var updated = await _bonusService.SetRatesAsync(dto, GetUserId());

                await _auditService.LogActionAsync(
                    DreamCleaningBackend.Services.AuditEntityTypes.RewardSetting, 0, "AdminBonusRatesChanged",
                    new
                    {
                        previous.AdministratorNewCustomerRate,
                        previous.AdministratorExistingCustomerRate,
                        previous.ManagerOwnBookingNewCustomerRate,
                        previous.ManagerOwnBookingExistingCustomerRate,
                        previous.ManagerTeamNewCustomerRate,
                        previous.ManagerTeamExistingCustomerRate
                    },
                    new
                    {
                        updated.AdministratorNewCustomerRate,
                        updated.AdministratorExistingCustomerRate,
                        updated.ManagerOwnBookingNewCustomerRate,
                        updated.ManagerOwnBookingExistingCustomerRate,
                        updated.ManagerTeamNewCustomerRate,
                        updated.ManagerTeamExistingCustomerRate
                    });

                return Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // SuperAdmin only — one person's own rates, overriding the company default per field. Two
        // pairs: orders they book themselves, and (for a manager) their share of their
        // administrators' bookings. Sending nulls on every field puts them back on the defaults.
        [HttpPut("admin/{adminId}/rates")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<AdminBonusSummaryDto>> SetOverride(
            int adminId,
            [FromBody] SetAdminBonusOverrideDto dto,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            try
            {
                // The service hands back what it replaced, so the audit entry costs nothing extra.
                // Nulls on either side mean the person was on the company default — worth recording
                // as such rather than as whatever figure that happened to be.
                var previous = await _bonusService.SetRateOverrideAsync(adminId, dto, GetUserId());

                await _auditService.LogActionAsync(
                    DreamCleaningBackend.Services.AuditEntityTypes.RewardSetting, adminId, "AdminBonusOverrideChanged",
                    new
                    {
                        previous.OwnBookingNewCustomerRate,
                        previous.OwnBookingExistingCustomerRate,
                        previous.TeamBookingNewCustomerRate,
                        previous.TeamBookingExistingCustomerRate
                    },
                    new
                    {
                        dto.OwnBookingNewCustomerRate,
                        dto.OwnBookingExistingCustomerRate,
                        dto.TeamBookingNewCustomerRate,
                        dto.TeamBookingExistingCustomerRate
                    });

                // Answer with the row the panel is about to redraw, over the SAME window it is
                // showing, so a rate edit does not need a second round trip to restate the month.
                var (fromDate, toDate) = ResolveMonthRange(from, to);
                return Ok(await _bonusService.GetSummaryForAdminAsync(adminId, fromDate, toDate));
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
