using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Models;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using DreamCleaningBackend.Services;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>Audit logs: entity history, recent logs, undo/redo.
    /// Split out of the monolithic AdminController; same api/admin route prefix, so URLs are unchanged.</summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminAuditController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _auditService;

        public AdminAuditController(ApplicationDbContext context,
            IAuditService auditService)
        {
            _context = context;
            _auditService = auditService;
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
                ChangedFields = string.IsNullOrEmpty(log.ChangedFields) ? null : JsonConvert.DeserializeObject<List<string>>(log.ChangedFields),
                UndoneAt = log.UndoneAt,
                UndoBlockedReason = AuditEntityTypes.ResolveUndoBlockedReason(log)
            });

            return Ok(result);
        }

        /// <summary>
        /// The Audits tab feed. <b>Paged, filtered and searched on the SERVER.</b>
        ///
        /// It used to load every row in the window with <c>.Include(a => a.User)</c> and no
        /// Skip/Take, while the UI offered "All Logs (6 Months)" and paged client-side — so the
        /// whole six months came down the wire before the first page could render. With the
        /// coverage sweep multiplying the row count that stopped being merely wasteful.
        ///
        /// <paramref name="days"/> is kept for callers that still send it, but an explicit
        /// <paramref name="from"/>/<paramref name="to"/> range wins when supplied.
        /// </summary>
        [HttpGet("audit-logs")]
        [RequirePermission(Permission.View)]
        public async Task<IActionResult> GetRecentAuditLogs(
            [FromQuery] int? days = 7,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] string? entityType = null,
            [FromQuery] string? action = null,
            [FromQuery] int? changedByUserId = null,
            [FromQuery] string? search = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 200);

            var query = _context.AuditLogs.AsNoTracking().AsQueryable();

            // An explicit range beats the days dropdown. `to` is inclusive of the whole day the
            // admin picked — a date-only picker sends midnight, and "to 31 Aug" must include the
            // 31st rather than stopping at 00:00 on it.
            if (from.HasValue || to.HasValue)
            {
                if (from.HasValue) query = query.Where(a => a.CreatedAt >= from.Value.Date);
                if (to.HasValue) query = query.Where(a => a.CreatedAt < to.Value.Date.AddDays(1));
            }
            else if (days.HasValue && days.Value > 0)
            {
                var startDate = DateTime.UtcNow.AddDays(-days.Value);
                query = query.Where(a => a.CreatedAt >= startDate);
            }

            if (!string.IsNullOrWhiteSpace(entityType) && entityType != "all")
                query = query.Where(a => a.EntityType == entityType);

            if (!string.IsNullOrWhiteSpace(action) && action != "all")
                query = query.Where(a => a.Action == action);

            if (changedByUserId.HasValue && changedByUserId.Value > 0)
                query = query.Where(a => a.UserId == changedByUserId.Value);

            query = ApplySearch(query, search);

            var totalCount = await query.CountAsync();

            var logs = await query
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Include(a => a.User)
                .ToListAsync();

            var items = logs.Select(log => new
            {
                id = log.Id,
                entityType = log.EntityType,
                entityId = log.EntityId,
                action = log.Action,
                createdAt = log.CreatedAt,
                changedBy = log.User == null ? null : (log.User.FirstName + " " + log.User.LastName).Trim(),
                changedByEmail = log.User?.Email,
                changedByUserId = log.UserId,
                oldValues = log.OldValues,
                newValues = log.NewValues,
                changedFields = log.ChangedFields,
                undoneAt = log.UndoneAt,
                // Server-side authority for the Undo button. Null = the row can be reverted; a
                // string is both "disabled" and the tooltip explaining why.
                undoBlockedReason = AuditEntityTypes.ResolveUndoBlockedReason(log)
            }).ToList();

            return Ok(new
            {
                items,
                totalCount,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
            });
        }

        /// <summary>
        /// Search across the three things an admin actually types: an order / entity id, an audit
        /// row id, and an email address.
        ///
        /// The old client-side version treated ANY term beginning with "e" as an entity-id prefix
        /// (<c>search.startsWith('e')</c>), so searching <c>eugene@…</c> looked for entity ids
        /// containing <c>ugene@…</c> and matched nothing — every customer and admin whose email
        /// starts with an "e" was unsearchable. The entity-id form is now recognised only when the
        /// rest of the term is digits.
        /// </summary>
        private static IQueryable<AuditLog> ApplySearch(IQueryable<AuditLog> query, string? search)
        {
            var term = search?.Trim();
            if (string.IsNullOrEmpty(term)) return query;

            // "#123" — this audit row.
            if (term.StartsWith("#") && long.TryParse(term.Substring(1), out var logId))
                return query.Where(a => a.Id == logId);

            // "e123" / "E123" — the entity (order, user, …) the row is about.
            var entityIdForm = Regex.Match(term, @"^[eE](\d+)$");
            if (entityIdForm.Success && long.TryParse(entityIdForm.Groups[1].Value, out var entityId))
                return query.Where(a => a.EntityId == entityId);

            // Bare digits are ambiguous on purpose — an admin typing "296" means order #296, but
            // might mean audit row #296, so both match.
            if (long.TryParse(term, out var numeric))
                return query.Where(a => a.EntityId == numeric || a.Id == numeric);

            var like = term.ToLower();
            return query.Where(a =>
                (a.User != null && a.User.Email != null && a.User.Email.ToLower().Contains(like)) ||
                (a.User != null && a.User.FirstName != null && a.User.FirstName.ToLower().Contains(like)) ||
                (a.User != null && a.User.LastName != null && a.User.LastName.ToLower().Contains(like)) ||
                a.EntityType.ToLower().Contains(like) ||
                a.Action.ToLower().Contains(like));
        }

        /// <summary>
        /// What the Audits tab needs to build its filters, and the ONE definition of which rows
        /// cannot be undone.
        ///
        /// Entity types and actions come from the data rather than a hardcoded list, so a stream
        /// added by a new audited action appears in the dropdown with no frontend change. The
        /// admin list is the set of people who actually appear in the log, not every admin
        /// account — a filter offering names with zero rows behind them is noise.
        /// </summary>
        [HttpGet("audit-logs/metadata")]
        [RequirePermission(Permission.View)]
        public async Task<IActionResult> GetAuditMetadata([FromQuery] int? days = 180)
        {
            var startDate = DateTime.UtcNow.AddDays(-(days ?? 180));
            var scoped = _context.AuditLogs.AsNoTracking().Where(a => a.CreatedAt >= startDate);

            var entityTypes = await scoped
                .Select(a => a.EntityType)
                .Distinct()
                .OrderBy(t => t)
                .ToListAsync();

            var actions = await scoped
                .Select(a => a.Action)
                .Distinct()
                .OrderBy(a => a)
                .ToListAsync();

            var admins = await scoped
                .Where(a => a.UserId != null)
                .Select(a => a.UserId!.Value)
                .Distinct()
                .Join(_context.Users, id => id, u => u.Id, (id, u) => new
                {
                    id = u.Id,
                    name = (u.FirstName + " " + u.LastName).Trim(),
                    email = u.Email
                })
                .OrderBy(u => u.name)
                .ToListAsync();

            return Ok(new
            {
                entityTypes,
                actions,
                admins,
                // Shipped so the component has no copy of the block list. See AuditEntityTypes.
                undoBlockedEntityTypes = AuditEntityTypes.UndoBlockedEntityTypes.ToList(),
                undoBlockedReasons = AuditEntityTypes.UndoBlockedReasons
            });
        }

        /// <summary>SuperAdmin-only: revert the change recorded by an audit row. Database-only —
        /// will not refund payments, recall sent emails, etc. See AuditService for the block list.</summary>
        [HttpPost("audit-logs/{id}/undo")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> UndoAuditLog(long id)
        {
            try
            {
                await _auditService.UndoAsync(id);
                return Ok(new { message = "Change undone." });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to undo change: " + ex.Message });
            }
        }

        /// <summary>SuperAdmin-only: re-apply a change that was previously undone.</summary>
        [HttpPost("audit-logs/{id}/redo")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> RedoAuditLog(long id)
        {
            try
            {
                await _auditService.RedoAsync(id);
                return Ok(new { message = "Change redone." });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Failed to redo change: " + ex.Message });
            }
        }

    }
}
