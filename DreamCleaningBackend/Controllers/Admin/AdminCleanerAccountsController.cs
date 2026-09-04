using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The admin panel Cleaners tab: the LOGIN ACCOUNTS cleaners use to open the read-only cleaner
    /// portal, and the link from each one to a row in the Cleaners table.
    ///
    /// Three surfaces mention cleaners and they answer three different questions:
    ///   - Users              -> customers and staff. Cleaner-role accounts are filtered OUT of it.
    ///   - Cleaners Dashboard -> the PEOPLE (/cleaners-dashboard, the Cleaners table): who they are,
    ///                           documents, ranking, availability, wages. Most have no account.
    ///   - Cleaners (this tab) -> which account signs in as which of those people.
    ///
    /// The route keeps its cleaner-accounts path: it is the accounts half that lives here, and
    /// renaming a live URL to follow a tab label buys nothing.
    ///
    /// Route lives under api/admin like every other admin surface, and the class-level attribute
    /// matches AdminUsersController (Moderators may look, not change) with per-action
    /// RequirePermission doing the actual gating - so a regular Admin does everything here that a
    /// SuperAdmin does, deliberately: staffing the crews is their daily work.
    /// </summary>
    [Route("api/admin/cleaner-accounts")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminCleanerAccountsController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _auditService;
        private readonly ILogger<AdminCleanerAccountsController> _logger;

        public AdminCleanerAccountsController(
            ApplicationDbContext context,
            IAuditService auditService,
            ILogger<AdminCleanerAccountsController> logger)
        {
            _context = context;
            _auditService = auditService;
            _logger = logger;
        }

        /// <summary>Every Cleaner-role account, with the cleaner record it drives (if any).</summary>
        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<CleanerAccountDto>>> GetCleanerAccounts()
        {
            var users = await _context.Users
                .Where(u => u.Role == UserRole.Cleaner)
                .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
                .Select(u => new
                {
                    u.Id,
                    u.FirstName,
                    u.LastName,
                    u.Email,
                    u.IsNoEmailUser,
                    u.Phone,
                    u.ProfilePictureUrl,
                    u.IsActive,
                    u.CreatedAt
                })
                .ToListAsync();

            var userIds = users.Select(u => u.Id).ToList();

            var linked = await _context.Cleaners
                .Where(c => c.UserId != null && userIds.Contains(c.UserId.Value))
                .Select(c => new
                {
                    UserId = c.UserId!.Value,
                    CleanerId = c.Id,
                    c.FirstName,
                    c.LastName,
                    c.Email,
                    c.IsActive
                })
                .ToListAsync();

            var linkedByUser = linked.ToDictionary(c => c.UserId);

            var linkedCleanerIds = linked.Select(c => c.CleanerId).ToList();
            var assignmentCounts = await _context.OrderCleaners
                .Where(oc => linkedCleanerIds.Contains(oc.CleanerId))
                .GroupBy(oc => oc.CleanerId)
                .Select(g => new { CleanerId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.CleanerId, g => g.Count);

            var result = users.Select(u =>
            {
                linkedByUser.TryGetValue(u.Id, out var cleaner);
                return new CleanerAccountDto
                {
                    UserId = u.Id,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    // Never hand back a generated placeholder - it looks like a real address and
                    // is the one thing the panel must not offer to mail.
                    Email = u.IsNoEmailUser || NoEmailHelper.IsPlaceholder(u.Email) ? null : u.Email,
                    Phone = u.Phone,
                    ProfilePictureUrl = u.ProfilePictureUrl,
                    IsActive = u.IsActive,
                    CreatedAt = u.CreatedAt,
                    CleanerId = cleaner?.CleanerId,
                    CleanerName = cleaner == null ? null : $"{cleaner.FirstName} {cleaner.LastName}".Trim(),
                    CleanerEmail = cleaner?.Email,
                    CleanerIsActive = cleaner?.IsActive ?? false,
                    AssignedOrdersCount = cleaner != null && assignmentCounts.TryGetValue(cleaner.CleanerId, out var n) ? n : 0
                };
            }).ToList();

            return Ok(result);
        }

        /// <summary>
        /// Cleaner records offered for linking, optionally narrowed by a SEARCH term matching first
        /// name, last name, the two together, email or phone.
        ///
        /// The search is server-side because the roster is the authority on who exists: filtering a
        /// list the client happened to have already would quietly stop finding people the moment the
        /// roster outgrew whatever was fetched. A blank term still returns everybody, so the picker
        /// opens on the full list and the box only ever narrows it.
        ///
        /// Rows already attached to a DIFFERENT account are returned too, named and marked - "why can
        /// I not find X" has to be answerable from the list itself rather than by guessing at an
        /// absence. The client disables them.
        /// </summary>
        [HttpGet("linkable-cleaners")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<LinkableCleanerDto>>> GetLinkableCleaners([FromQuery] string? search)
        {
            var query = _context.Cleaners.AsQueryable();

            var term = (search ?? string.Empty).Trim().ToLowerInvariant();
            if (term.Length > 0)
            {
                // "maria k" has to find Maria Karidze, so the two names concatenated are matched as
                // well as each on its own - a term spanning the gap matches neither column alone.
                query = query.Where(c =>
                    c.FirstName.ToLower().Contains(term) ||
                    c.LastName.ToLower().Contains(term) ||
                    (c.FirstName + " " + c.LastName).ToLower().Contains(term) ||
                    (c.Email != null && c.Email.ToLower().Contains(term)) ||
                    (c.Phone != null && c.Phone.Contains(term)));
            }

            var cleaners = await query
                .OrderByDescending(c => c.IsActive)
                .ThenBy(c => c.FirstName).ThenBy(c => c.LastName)
                .Select(c => new LinkableCleanerDto
                {
                    CleanerId = c.Id,
                    Name = (c.FirstName + " " + c.LastName).Trim(),
                    Email = c.Email,
                    Phone = c.Phone,
                    IsActive = c.IsActive,
                    LinkedUserId = c.UserId,
                    LinkedUserEmail = c.User != null ? c.User.Email : null
                })
                .ToListAsync();

            return Ok(cleaners);
        }

        /// <summary>
        /// Accounts an admin may move INTO the Cleaner role. Customers only: promoting an Admin,
        /// SuperAdmin or Moderator would take their panel away, and that is a decision for the
        /// Users tab's role control where the consequence is visible, not a side effect of a
        /// cleaner search. A search term is required past two characters so the tab never renders
        /// the entire customer base.
        /// </summary>
        [HttpGet("promotable-users")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<PromotableUserDto>>> GetPromotableUsers([FromQuery] string? search)
        {
            var term = (search ?? string.Empty).Trim().ToLowerInvariant();
            if (term.Length < 2)
                return Ok(new List<PromotableUserDto>());

            var users = await _context.Users
                .Where(u => u.Role == UserRole.Customer && !u.IsDeleted)
                .Where(u =>
                    u.FirstName.ToLower().Contains(term) ||
                    u.LastName.ToLower().Contains(term) ||
                    (u.Email != null && u.Email.ToLower().Contains(term)) ||
                    (u.Phone != null && u.Phone.Contains(term)))
                .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
                .Take(25)
                .Select(u => new PromotableUserDto
                {
                    UserId = u.Id,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    Email = u.IsNoEmailUser ? null : u.Email,
                    Phone = u.Phone,
                    Role = u.Role.ToString()
                })
                .ToListAsync();

            return Ok(users);
        }

        /// <summary>
        /// Attaches a Cleaner-role account to a cleaner record, and OVERWRITES that record's email
        /// with the address the account actually signs in with.
        ///
        /// The overwrite is the requirement and it is also the safer behaviour: the whole reason to
        /// link by hand is that the cleaner row has no email or a different one, and leaving a
        /// stale address on it would keep the assignment mail going somewhere the person does not
        /// read. The link itself is a foreign key, so from here on nothing depends on those two
        /// strings continuing to match.
        /// </summary>
        [HttpPut("{userId}/link")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<CleanerAccountDto>> LinkCleaner(int userId, [FromBody] LinkCleanerAccountDto dto)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
                return NotFound(new { message = "Account not found." });

            if (user.Role != UserRole.Cleaner)
                return BadRequest(new { message = "Only a Cleaner-role account can be linked to a cleaner. Change the role first." });

            var cleaner = await _context.Cleaners.FirstOrDefaultAsync(c => c.Id == dto.CleanerId);
            if (cleaner == null)
                return NotFound(new { message = "Cleaner record not found." });

            if (cleaner.UserId.HasValue && cleaner.UserId.Value != userId)
            {
                var holder = await _context.Users
                    .Where(u => u.Id == cleaner.UserId.Value)
                    .Select(u => u.Email)
                    .FirstOrDefaultAsync();
                return BadRequest(new
                {
                    message = $"That cleaner is already linked to another account ({holder ?? "unknown"}). Unlink it there first."
                });
            }

            var originalCleaner = AuditSnapshot.Of(cleaner);
            var originalCleanerEmail = cleaner.Email;

            // One account cannot drive two cleaner records - the unique index would refuse the
            // write anyway, so release the old attachment explicitly rather than surfacing a
            // constraint error the admin cannot act on.
            var previous = await _context.Cleaners
                .Where(c => c.UserId == userId && c.Id != cleaner.Id)
                .ToListAsync();
            var previousSnapshots = previous.Select(c => (Cleaner: c, Before: AuditSnapshot.Of(c))).ToList();
            foreach (var stale in previous)
            {
                stale.UserId = null;
                stale.UpdatedAt = DateTime.UtcNow;
            }

            cleaner.UserId = userId;

            // A no-email account has nothing routable to copy across; leaving the record's existing
            // address alone beats replacing it with a placeholder nobody can send to.
            var accountEmail = NoEmailHelper.ResolveRealEmail(user);
            if (accountEmail != null)
                cleaner.Email = accountEmail;

            cleaner.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            try
            {
                // TWO rows, deliberately. The generic Cleaner update is that record own history
                // (UserId and Email moved on it); the CleanerAccountLink row is the account-level
                // event, keyed on the user id, which is what somebody auditing "who gave this login
                // access to whose schedule" is searching by. Neither answers the other question.
                await _auditService.LogUpdateAsync(originalCleaner, cleaner);
                // The records this write DETACHED get their own field history too - a cleaner whose
                // UserId went null needs the change on its own stream, not only on the account it
                // was taken away from.
                foreach (var (stale, before) in previousSnapshots)
                    await _auditService.LogUpdateAsync(before, stale);
                // A dictionary rather than an anonymous type so the detached-cleaners key is
                // ABSENT on an ordinary link instead of present-and-null: every key in the payload
                // is reported as a changed field, and a permanent empty row reads as a fact.
                var linkPayload = new Dictionary<string, object?>
                {
                    ["AccountName"] = $"{user.FirstName} {user.LastName}".Trim(),
                    ["AccountEmail"] = accountEmail,
                    ["CleanerId"] = cleaner.Id,
                    ["CleanerName"] = $"{cleaner.FirstName} {cleaner.LastName}".Trim(),
                    // What the link did to the cleaner record own address, which is the part with
                    // a side effect: the assignment mail now goes somewhere else.
                    ["CleanerEmailBefore"] = originalCleanerEmail,
                    ["CleanerEmailAfter"] = cleaner.Email
                };
                // Named because the write silently detached them - an absence nobody would
                // otherwise be able to explain from this row.
                if (previous.Count > 0)
                    linkPayload["ReleasedCleanerIds"] = string.Join(", ", previous.Select(c => c.Id));

                await _auditService.LogActionAsync(
                    AuditEntityTypes.CleanerAccountLink, userId, "Linked", null, linkPayload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit logging failed for cleaner link {CleanerId} -> user {UserId}", cleaner.Id, userId);
            }

            return Ok(await BuildAccountDtoAsync(userId));
        }

        /// <summary>
        /// Detaches the account from its cleaner record. The record's email is deliberately LEFT as
        /// it is: it was copied from a real person's mailbox and is still the address the assignment
        /// mail should reach. Unlinking says "this account no longer opens the portal for them", not
        /// "we have lost their contact details".
        /// </summary>
        [HttpDelete("{userId}/link")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<CleanerAccountDto>> UnlinkCleaner(int userId)
        {
            var cleaners = await _context.Cleaners.Where(c => c.UserId == userId).ToListAsync();
            if (cleaners.Count == 0)
                return BadRequest(new { message = "That account is not linked to a cleaner." });

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);

            var snapshots = cleaners
                .Select(c => (Cleaner: c, Before: AuditSnapshot.Of(c)))
                .ToList();

            foreach (var cleaner in cleaners)
            {
                cleaner.UserId = null;
                cleaner.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            // Audited AFTER the save, not before it: AuditService writes through this same context,
            // so logging first committed the detach as a side effect of the audit call and a
            // rejected save would have left a row claiming something that never happened.
            foreach (var (cleaner, before) in snapshots)
            {
                try
                {
                    await _auditService.LogUpdateAsync(before, cleaner);
                    await _auditService.LogActionAsync(
                        AuditEntityTypes.CleanerAccountLink, userId, "Unlinked", null, new
                        {
                            AccountName = user == null ? null : $"{user.FirstName} {user.LastName}".Trim(),
                            AccountEmail = user == null ? null : NoEmailHelper.ResolveRealEmail(user),
                            CleanerId = cleaner.Id,
                            CleanerName = $"{cleaner.FirstName} {cleaner.LastName}".Trim(),
                            // Left as it was on purpose - unlinking says the login no longer opens
                            // the portal, not that we have lost the person contact details.
                            CleanerEmailKept = cleaner.Email
                        });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Audit logging failed unlinking cleaner {CleanerId}", cleaner.Id);
                }
            }

            return Ok(await BuildAccountDtoAsync(userId));
        }

        /// <summary>Re-reads one row so a write can answer with the same shape the list renders.</summary>
        private async Task<CleanerAccountDto?> BuildAccountDtoAsync(int userId)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return null;

            var cleaner = await _context.Cleaners.FirstOrDefaultAsync(c => c.UserId == userId);
            var assignedCount = cleaner == null
                ? 0
                : await _context.OrderCleaners.CountAsync(oc => oc.CleanerId == cleaner.Id);

            return new CleanerAccountDto
            {
                UserId = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = NoEmailHelper.ResolveRealEmail(user),
                Phone = user.Phone,
                ProfilePictureUrl = user.ProfilePictureUrl,
                IsActive = user.IsActive,
                CreatedAt = user.CreatedAt,
                CleanerId = cleaner?.Id,
                CleanerName = cleaner == null ? null : $"{cleaner.FirstName} {cleaner.LastName}".Trim(),
                CleanerEmail = cleaner?.Email,
                CleanerIsActive = cleaner?.IsActive ?? false,
                AssignedOrdersCount = assignedCount
            };
        }
    }
}
