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
                UndoneAt = log.UndoneAt
            });

            return Ok(result);
        }

        [HttpGet("audit-logs")]
        [RequirePermission(Permission.View)]
        public async Task<IActionResult> GetRecentAuditLogs([FromQuery] int? days = 7)
        {
            var startDate = DateTime.UtcNow.AddDays(-days.Value);

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
                changedFields = log.ChangedFields,  // lowercase
                undoneAt = log.UndoneAt
            }).ToList();

            return Ok(result);
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
