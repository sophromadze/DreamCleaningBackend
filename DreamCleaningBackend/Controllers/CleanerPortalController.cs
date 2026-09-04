using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// THE CLEANER PORTAL - a read-only section serving two audiences from one place.
    ///
    ///   Cleaner   -> their own jobs: what they are staffed on now (the same details the assignment
    ///                email already sends them) and the dates of what they have finished.
    ///   Admin /   -> every cleaning in the system, past, current and future, for every cleaner,
    ///   SuperAdmin   with a fuller read-only detail view on any one of them. Admins run the
    ///                schedule day to day, so this is their working calendar, not a report.
    ///
    /// NOTHING HERE WRITES ORDER DATA, and that is a design constraint rather than an omission:
    /// cleaners must not be able to change a cleaning from here, and a SuperAdmin editing an order
    /// already has the admin orders panel, where the pricing and approval rules live. A second
    /// editor would be a second place for those rules to be got wrong.
    ///
    /// The single exception is <see cref="SetLanguage"/>, which writes one display preference onto
    /// the CALLER'S OWN cleaner row. It is worth naming rather than hiding: the rule is about the
    /// data, not the verb, and a person choosing which language they read is not an edit to
    /// anybody's cleaning.
    ///
    /// Authorization reuses what already exists - the role-list [Authorize] pattern the rest of
    /// the app uses for staff-only reads - rather than introducing a permission model for one
    /// section. The cleaner half is gated on the role plus the account's cleaner LINK, and
    /// the link is resolved server-side per request: it lives in the database and an admin can
    /// create or remove it at any time, so a JWT minted before that is not evidence of anything.
    /// </summary>
    [Route("api/cleaner-portal")]
    [ApiController]
    [Authorize]
    public class CleanerPortalController : ControllerBase
    {
        private readonly ICleanerPortalService _portalService;
        private readonly ICleanerAccountService _cleanerAccountService;
        private readonly ApplicationDbContext _context;

        public CleanerPortalController(
            ICleanerPortalService portalService,
            ICleanerAccountService cleanerAccountService,
            ApplicationDbContext context)
        {
            _portalService = portalService;
            _cleanerAccountService = cleanerAccountService;
            _context = context;
        }

        private int CurrentUserId =>
            int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;

        private UserRole CurrentRole
        {
            get
            {
                Enum.TryParse<UserRole>(User.FindFirst("Role")?.Value, out var role);
                return role;
            }
        }

        /// <summary>
        /// Who is looking, in which mode, and which cleaner record is behind them. The page calls
        /// this first so it can tell the three states apart: a cleaner with jobs, a cleaner whose
        /// account nobody has linked yet (which must read as "ask an admin to link you", never as
        /// "you have no work"), and a SuperAdmin.
        /// </summary>
        [HttpGet("context")]
        public async Task<ActionResult<CleanerPortalContextDto>> GetContext()
        {
            var role = CurrentRole;
            if (!CleanerAccountLink.CanOpenPortal(role))
                return Forbid();

            var dto = new CleanerPortalContextDto
            {
                IsCleanerView = CleanerAccountLink.IsCleanerView(role),
                IsSystemWideView = CleanerAccountLink.IsSystemWideRole(role)
            };

            if (dto.IsCleanerView)
            {
                var cleanerId = await _cleanerAccountService.ResolveCleanerIdForUserAsync(CurrentUserId);
                dto.CleanerId = cleanerId;
                if (cleanerId.HasValue)
                {
                    var cleaner = await _context.Cleaners
                        .AsNoTracking()
                        .Where(c => c.Id == cleanerId.Value)
                        .Select(c => new { c.FirstName, c.LastName, c.Nationality, c.PortalLanguage })
                        .FirstOrDefaultAsync();

                    if (cleaner != null)
                    {
                        dto.CleanerName = $"{cleaner.FirstName} {cleaner.LastName}".Trim();
                        // Resolved SERVER-side, from the same map the assignment email uses, so a
                        // cleaner cannot be mailed in one language and shown a page in another.
                        dto.Language = CleanerLanguage.Resolve(cleaner.Nationality, cleaner.PortalLanguage);
                        dto.PreferredLanguage = CleanerLanguage.Normalize(cleaner.PortalLanguage);
                    }
                }
            }

            return Ok(dto);
        }

        /// <summary>
        /// A cleaner's own jobs. Cleaner role only: a SuperAdmin asking for "my jobs" is asking the
        /// wrong question and gets pointed at the system-wide endpoint rather than an empty list
        /// that looks like a bug.
        /// </summary>
        [HttpGet("my-jobs")]
        [Authorize(Roles = "Cleaner")]
        public async Task<ActionResult<CleanerPortalMyJobsDto>> GetMyJobs()
        {
            var cleanerId = await _cleanerAccountService.ResolveCleanerIdForUserAsync(CurrentUserId);

            // Not an error: this account is a cleaner's, but no cleaner record is attached to it
            // yet. Empty lists plus the context call's null CleanerId is what the page renders the
            // "not linked" explanation from.
            if (cleanerId == null)
                return Ok(new CleanerPortalMyJobsDto());

            return Ok(await _portalService.GetMyJobsAsync(cleanerId.Value));
        }

        /// <summary>
        /// Every cleaning in the system. Admin + SuperAdmin, enforced on the endpoint and not
        /// merely hidden in the UI - this is every customer's name and address in one list.
        /// </summary>
        [HttpGet("all-jobs")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        public async Task<ActionResult<List<CleanerPortalAdminJobDto>>> GetAllJobs(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string? search)
        {
            return Ok(await _portalService.GetAllJobsAsync(from, to, search));
        }

        /// <summary>
        /// The cleaner's own language for this portal. Null or blank CLEARS it, putting them back
        /// on their nationality's default - which is why the picker sends null for "Automatic"
        /// rather than re-sending whatever that default currently resolves to.
        ///
        /// THIS IS THE ONLY WRITE IN THIS CONTROLLER, and it does not weaken the rule the class
        /// doc states: what may never be written from here is ORDER data. This writes one display
        /// preference on the caller's OWN cleaner row, resolved from their account rather than
        /// taken from the request, so it cannot reach anybody else's record either.
        /// </summary>
        [HttpPut("language")]
        [Authorize(Roles = "Cleaner")]
        public async Task<IActionResult> SetLanguage([FromBody] SetCleanerLanguageDto dto)
        {
            var cleanerId = await _cleanerAccountService.ResolveCleanerIdForUserAsync(CurrentUserId);
            if (cleanerId == null)
                return BadRequest(new { message = "Your account is not linked to a cleaner profile yet." });

            var cleaner = await _context.Cleaners.FirstOrDefaultAsync(c => c.Id == cleanerId.Value);
            if (cleaner == null)
                return NotFound(new { message = "Cleaner not found." });

            // An unsupported code normalises to null rather than being rejected: the page offers a
            // fixed list, so anything else is a stale client, and "follow my nationality" is the
            // safe reading of it.
            cleaner.PortalLanguage = CleanerLanguage.Normalize(dto?.Language);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                language = CleanerLanguage.Resolve(cleaner.Nationality, cleaner.PortalLanguage),
                preferredLanguage = cleaner.PortalLanguage
            });
        }

        /// <summary>
        /// Full read-only detail for one order. Admin + SuperAdmin - it carries pricing, payment,
        /// internal notes and the whole customer record, which is precisely the material the
        /// cleaner view exists to keep out. Admins already read all of it in the orders panel, so
        /// the boundary this endpoint defends is the CLEANER one, not theirs.
        /// </summary>
        [HttpGet("orders/{id}")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        public async Task<ActionResult<CleanerPortalOrderDetailDto>> GetOrderDetail(int id)
        {
            var detail = await _portalService.GetOrderDetailAsync(id);
            if (detail == null) return NotFound(new { message = "Order not found." });
            return Ok(detail);
        }
    }
}
