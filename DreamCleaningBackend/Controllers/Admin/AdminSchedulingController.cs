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
    /// <summary>Blocked time slots (scheduling restrictions).
    /// Split out of the monolithic AdminController; same api/admin route prefix, so URLs are unchanged.</summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminSchedulingController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;

        public AdminSchedulingController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ── Blocked Time Slots (Scheduling) ─────────────────────────────

        [HttpGet("blocked-time-slots")]
        public async Task<ActionResult> GetBlockedTimeSlots([FromQuery] string? from, [FromQuery] string? to)
        {
            var fromDate = DateTime.Today;
            var toDate = DateTime.Today.AddMonths(3);

            if (!string.IsNullOrEmpty(from) && DateTime.TryParse(from, out var parsedFrom))
                fromDate = parsedFrom.Date;
            if (!string.IsNullOrEmpty(to) && DateTime.TryParse(to, out var parsedTo))
                toDate = parsedTo.Date;

            var blocked = await _context.BlockedTimeSlots
                .Where(b => b.Date >= fromDate && b.Date <= toDate)
                .OrderBy(b => b.Date)
                .Include(b => b.CreatedByUser)
                .Select(b => new
                {
                    b.Id,
                    date = b.Date.ToString("yyyy-MM-dd"),
                    b.IsFullDay,
                    b.BlockedHours,
                    b.Reason,
                    createdBy = b.CreatedByUser.FirstName + " " + b.CreatedByUser.LastName,
                    b.CreatedAt
                })
                .ToListAsync();

            return Ok(blocked);
        }

        [HttpPost("blocked-time-slots")]
        public async Task<ActionResult> CreateBlockedTimeSlot([FromBody] CreateBlockedTimeSlotDto dto)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out var userId))
                return Unauthorized();

            if (!DateTime.TryParse(dto.Date, out var parsedDate))
                return BadRequest(new { message = "Invalid date format." });

            // Check if a block already exists for this date
            var existing = await _context.BlockedTimeSlots
                .FirstOrDefaultAsync(b => b.Date == parsedDate.Date);

            if (existing != null)
            {
                // Update existing block
                existing.IsFullDay = dto.IsFullDay;
                existing.BlockedHours = dto.IsFullDay ? null : dto.BlockedHours;
                existing.Reason = dto.Reason;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                var blocked = new BlockedTimeSlot
                {
                    Date = parsedDate.Date,
                    IsFullDay = dto.IsFullDay,
                    BlockedHours = dto.IsFullDay ? null : dto.BlockedHours,
                    Reason = dto.Reason,
                    CreatedByUserId = userId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.BlockedTimeSlots.Add(blocked);
            }

            await _context.SaveChangesAsync();
            return Ok(new { message = "Blocked time slot saved successfully." });
        }

        [HttpPut("blocked-time-slots/{id}")]
        public async Task<ActionResult> UpdateBlockedTimeSlot(int id, [FromBody] CreateBlockedTimeSlotDto dto)
        {
            var blocked = await _context.BlockedTimeSlots.FindAsync(id);
            if (blocked == null)
                return NotFound(new { message = "Blocked time slot not found." });

            if (!DateTime.TryParse(dto.Date, out var parsedDate))
                return BadRequest(new { message = "Invalid date format." });

            blocked.Date = parsedDate.Date;
            blocked.IsFullDay = dto.IsFullDay;
            blocked.BlockedHours = dto.IsFullDay ? null : dto.BlockedHours;
            blocked.Reason = dto.Reason;
            blocked.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok(new { message = "Blocked time slot updated successfully." });
        }

        [HttpDelete("blocked-time-slots/{id}")]
        public async Task<ActionResult> DeleteBlockedTimeSlot(int id)
        {
            var blocked = await _context.BlockedTimeSlots.FindAsync(id);
            if (blocked == null)
                return NotFound(new { message = "Blocked time slot not found." });

            _context.BlockedTimeSlots.Remove(blocked);
            await _context.SaveChangesAsync();
            return Ok(new { message = "Blocked time slot deleted successfully." });
        }
    }
}

namespace DreamCleaningBackend.DTOs
{
    public class AcknowledgeReminderDto
    {
        public string Type { get; set; } = string.Empty;
    }

    public class CreateBlockedTimeSlotDto
    {
        public string Date { get; set; } = string.Empty;
        public bool IsFullDay { get; set; }
        public string? BlockedHours { get; set; }
        public string? Reason { get; set; }
    }
}
