using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Contracts;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The two switches that govern the Contracts module: who holds an officer title, and which
    /// customers count as businesses.
    ///
    /// They live together because both are gated by the CONTRACTS matrix rather than by the app's
    /// normal role hierarchy, and putting them in AdminUsersController would have buried that
    /// distinction among two thousand lines of ordinary role-gated user management.
    ///
    /// Same api/admin prefix as the rest of the admin surface, so nothing about URL shape changes.
    /// </summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public class AdminContractAccessController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ContractAuthorizationService _authorization;
        private readonly IAuditService _auditService;
        private readonly BusinessClientService _businessClients;

        public AdminContractAccessController(
            ApplicationDbContext context,
            ContractAuthorizationService authorization,
            IAuditService auditService,
            BusinessClientService businessClients)
        {
            _context = context;
            _authorization = authorization;
            _auditService = auditService;
            _businessClients = businessClients;
        }

        // ── officer titles ─────────────────────────────────────────────────────

        /// <summary>
        /// Who holds a title today, who could be given one, and whether the bootstrap escape hatch
        /// is currently open. The UI needs all three to explain itself; the server re-checks the
        /// rule on every write regardless of what this returned.
        /// </summary>
        [HttpGet("org-titles")]
        public async Task<ActionResult<OrgTitleOverviewDto>> GetOrgTitles()
        {
            var staff = await _context.Users
                .Where(u => u.IsActive && (u.Role == UserRole.Admin || u.Role == UserRole.SuperAdmin))
                .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
                .Select(u => new OrgTitleHolderDto
                {
                    UserId = u.Id,
                    FullName = (u.FirstName + " " + u.LastName).Trim(),
                    Email = u.Email,
                    Role = u.Role.ToString(),
                    OrgTitle = u.OrgTitle
                })
                .ToListAsync();

            var currentCto = await _authorization.GetCurrentCtoUserIdAsync();
            var (requesterRole, _) = await _authorization.GetCurrentIdentityAsync();

            return Ok(new OrgTitleOverviewDto
            {
                Holders = staff,
                CurrentCtoUserId = currentCto,
                IsBootstrapMode = OrgTitlePolicy.IsBootstrapMode(currentCto),
                // What the caller may actually do, evaluated against a representative target so
                // the UI can grey the controls rather than discovering the refusal on submit.
                CanAssign = OrgTitlePolicy.Evaluate(
                    _authorization.CurrentUserId, requesterRole, currentCto, UserRole.SuperAdmin)
                    == OrgTitleAssignmentResult.Allowed
            });
        }

        /// <summary>
        /// Assigns or clears an officer title. Authority here is NOT the contracts matrix and not a
        /// plain role check - it is <see cref="OrgTitlePolicy"/>, which is bootstrap-or-locked
        /// depending on whether a CTO currently exists. Evaluated at request time, so revoking the
        /// CTO title immediately reopens bootstrap and granting it immediately closes it.
        /// </summary>
        [HttpPut("users/{id}/org-title")]
        public async Task<ActionResult> SetOrgTitle(int id, [FromBody] SetOrgTitleDto dto)
        {
            var target = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (target == null) return NotFound(new { message = "User not found." });

            if (target.Role is not (UserRole.Admin or UserRole.SuperAdmin))
                return BadRequest(new { message = "Officer titles apply to staff accounts only." });

            var currentCto = await _authorization.GetCurrentCtoUserIdAsync();
            var (requesterRole, _) = await _authorization.GetCurrentIdentityAsync();
            var requesterId = _authorization.CurrentUserId;

            var verdict = OrgTitlePolicy.Evaluate(requesterId, requesterRole, currentCto, target.Role);
            if (verdict != OrgTitleAssignmentResult.Allowed)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = OrgTitlePolicy.DescribeRefusal(verdict) });

            // A title is held by one person at a time. Handing CEO or CTO to somebody new clears
            // it from whoever had it, rather than quietly leaving two holders and making
            // "the current CTO" ambiguous for the lock rule above.
            if (dto.OrgTitle != OrgTitle.None)
            {
                var previousHolders = await _context.Users
                    .Where(u => u.OrgTitle == dto.OrgTitle && u.Id != target.Id)
                    .ToListAsync();
                foreach (var holder in previousHolders) holder.OrgTitle = OrgTitle.None;
            }

            // Snapshot BEFORE mutating, so the audit diff names only the title fields — the same
            // shared helper every other user-field edit in the panel uses.
            var originalUser = AuditSnapshot.Of(target);

            target.OrgTitle = dto.OrgTitle;
            target.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _auditService.LogUpdateAsync(originalUser, target);

            return Ok(new
            {
                message = dto.OrgTitle == OrgTitle.None
                    ? $"Cleared the officer title on {target.FirstName} {target.LastName}.".Trim()
                    : $"{target.FirstName} {target.LastName} is now {dto.OrgTitle}.".Trim(),
                orgTitle = target.OrgTitle
            });
        }

        // ── business flag ──────────────────────────────────────────────────────

        /// <summary>
        /// Marks a customer account as a business. Gated by the contracts matrix, where this is one
        /// of only two places CEO and CTO differ: a CEO deliberately does not hold it.
        ///
        /// THIS IS THE SYNCHRONISATION POINT (2026-09). The flag and the account's commercial
        /// client move together here, on the WRITE path, and nowhere else — no read endpoint
        /// creates or changes a client as a side effect of being called. Turning the flag on makes
        /// the client exist (or brings the existing one back); turning it off takes it out of
        /// circulation without deleting anything. <see cref="BusinessClientService"/> owns the
        /// whole state machine and the reasoning behind it.
        ///
        /// The old "unlink it from the contract first" refusal is deliberately GONE. It existed to
        /// stop the flag being cleared out from under a contract relying on it for portal access,
        /// but its effect was that the one place staff could say "this is a business" refused to
        /// let them say otherwise — and the contract was never actually at risk: it keeps its
        /// client, its snapshot and its signatures either way, and only the customer's
        /// self-service view of it goes quiet.
        /// </summary>
        [HttpPut("users/{id}/business-flag")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> SetBusinessFlag(int id, [FromBody] SetBusinessFlagDto dto)
        {
            await _authorization.EnsureCanAsync(ContractAction.ToggleBusinessFlag);

            var target = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (target == null) return NotFound(new { message = "User not found." });

            if (target.Role != UserRole.Customer)
                return BadRequest(new { message = "The business flag applies to customer accounts only." });

            if (target.IsBusiness == dto.IsBusiness)
                return Ok(new { message = "No change.", isBusiness = target.IsBusiness });

            var originalUser = AuditSnapshot.Of(target);

            // Flag + client in one save. ApplyBusinessFlagAsync is idempotent, so a repeated call
            // or a re-tick is a no-op rather than a duplicate client.
            var outcome = await _businessClients.ApplyBusinessFlagAsync(target, dto.IsBusiness, GetCurrentUserId());

            await _auditService.LogUpdateAsync(originalUser, target);

            return Ok(new
            {
                message = dto.IsBusiness
                    ? $"{target.FirstName} {target.LastName} is now flagged as a business{DescribeClient(outcome)}.".Trim()
                    : $"Removed the business flag from {target.FirstName} {target.LastName}{DescribeClient(outcome)}.".Trim(),
                isBusiness = target.IsBusiness
            });
        }

        /// <summary>
        /// Says what happened to the commercial client, so the toast explains the side effect the
        /// admin is about to see on the Clients tab rather than leaving them to discover it.
        /// </summary>
        private static string DescribeClient(BusinessClientService.LinkOutcome outcome) => outcome switch
        {
            BusinessClientService.LinkOutcome.Created => " and now appears under Commercial → Clients",
            BusinessClientService.LinkOutcome.Reactivated => " and their commercial client is active again",
            BusinessClientService.LinkOutcome.Deactivated => ", and their commercial client is no longer listed",
            _ => string.Empty
        };

        /// <summary>
        /// Business-flagged customers, for the "link this contract to an account" picker on the
        /// contract form. Deliberately only business accounts — the link is what grants portal
        /// access, so a residential customer must not be selectable.
        /// </summary>
        [HttpGet("business-customers")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<BusinessCustomerDto>>> GetBusinessCustomers(
            [FromQuery] string? search)
        {
            var query = _context.Users.Where(u => u.IsBusiness && u.IsActive && u.Role == UserRole.Customer);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(u =>
                    u.FirstName.Contains(term) || u.LastName.Contains(term) || u.Email.Contains(term));
            }

            var rows = await query
                .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
                .Take(100)
                .Select(u => new BusinessCustomerDto
                {
                    UserId = u.Id,
                    FullName = (u.FirstName + " " + u.LastName).Trim(),
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    Email = u.Email,
                    Phone = u.Phone,
                    // The account's primary address, for pre-filling the contract form. Oldest
                    // active apartment: it is the one they registered with, and a customer with
                    // several has no "primary" flag to pick from.
                    Address = u.Apartments
                        .Where(a => a.IsActive)
                        .OrderBy(a => a.Id)
                        .Select(a => a.Address)
                        .FirstOrDefault(),
                    City = u.Apartments
                        .Where(a => a.IsActive)
                        .OrderBy(a => a.Id)
                        .Select(a => a.City)
                        .FirstOrDefault(),
                    State = u.Apartments
                        .Where(a => a.IsActive)
                        .OrderBy(a => a.Id)
                        .Select(a => a.State)
                        .FirstOrDefault(),
                    Zip = u.Apartments
                        .Where(a => a.IsActive)
                        .OrderBy(a => a.Id)
                        .Select(a => a.PostalCode)
                        .FirstOrDefault()
                })
                .ToListAsync();

            // A no-email placeholder address must never be offered as a real contact.
            foreach (var row in rows)
            {
                if (Helpers.NoEmailHelper.IsPlaceholder(row.Email)) row.Email = string.Empty;
            }

            return Ok(rows);
        }
    }
}
