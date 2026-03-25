using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/admin/shifts")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public class AdminShiftsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public AdminShiftsController(ApplicationDbContext context)
        {
            _context = context;
        }

        private int GetUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.Parse(userIdClaim!);
        }

        // ════════════════════════════════════════════════
        //  GET admins eligible for shifts (Admin role only, not SuperAdmin)
        // ════════════════════════════════════════════════

        [HttpGet("admins")]
        public async Task<ActionResult<List<ShiftAdminDto>>> GetShiftAdmins()
        {
            var admins = await _context.Users
                .Where(u => !u.IsDeleted && u.IsActive && u.Role == UserRole.Admin)
                .OrderBy(u => u.FirstName)
                .ThenBy(u => u.LastName)
                .Select(u => new ShiftAdminDto
                {
                    Id = u.Id,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    Email = u.Email,
                    ShiftColor = u.ShiftColor
                })
                .ToListAsync();

            return Ok(admins);
        }

        // ════════════════════════════════════════════════
        //  GET shifts for a date range
        // ════════════════════════════════════════════════

        [HttpGet]
        public async Task<ActionResult<List<AdminShiftDto>>> GetShifts(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            var fromDate = from ?? DateTime.UtcNow.Date.AddDays(-7);
            var toDate = to ?? DateTime.UtcNow.Date.AddDays(60);

            var shifts = await _context.AdminShifts
                .Include(s => s.Admin)
                .Include(s => s.CreatedByUser)
                .Where(s => s.ShiftDate >= fromDate && s.ShiftDate <= toDate)
                .OrderBy(s => s.ShiftDate)
                .Select(s => new AdminShiftDto
                {
                    Id = s.Id,
                    ShiftDate = s.ShiftDate,
                    AdminId = s.AdminId,
                    AdminName = s.Admin.FirstName + " " + s.Admin.LastName,
                    AdminRole = s.Admin.Role.ToString(),
                    AdminColor = s.Admin.ShiftColor,
                    Notes = s.Notes,
                    CreatedByUserId = s.CreatedByUserId,
                    CreatedByUserName = s.CreatedByUser.FirstName + " " + s.CreatedByUser.LastName,
                    CreatedAt = s.CreatedAt,
                    UpdatedAt = s.UpdatedAt
                })
                .ToListAsync();

            return Ok(shifts);
        }

        // ════════════════════════════════════════════════
        //  BULK SET shifts for a specific date
        //  Replaces all shifts on that date with the new list
        // ════════════════════════════════════════════════

        [HttpPut("bulk")]
        public async Task<ActionResult<List<AdminShiftDto>>> BulkSetShifts([FromBody] BulkSetShiftsDto dto)
        {
            var shiftDate = dto.ShiftDate.Date;
            var userId = GetUserId();

            // Validate all admin IDs exist and have Admin role
            var validAdmins = await _context.Users
                .Where(u => dto.AdminIds.Contains(u.Id) && !u.IsDeleted && u.IsActive && u.Role == UserRole.Admin)
                .Select(u => u.Id)
                .ToListAsync();

            var invalidIds = dto.AdminIds.Except(validAdmins).ToList();
            if (invalidIds.Any())
                return BadRequest(new { message = $"Invalid admin IDs: {string.Join(", ", invalidIds)}" });

            // Remove existing shifts for this date
            var existingShifts = await _context.AdminShifts
                .Where(s => s.ShiftDate == shiftDate)
                .ToListAsync();
            _context.AdminShifts.RemoveRange(existingShifts);

            // Create new shifts
            var newShifts = dto.AdminIds.Select(adminId => new AdminShift
            {
                ShiftDate = shiftDate,
                AdminId = adminId,
                Notes = dto.Notes,
                CreatedByUserId = userId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }).ToList();

            _context.AdminShifts.AddRange(newShifts);
            await _context.SaveChangesAsync();

            // Return the new shifts with admin details
            var result = await _context.AdminShifts
                .Include(s => s.Admin)
                .Include(s => s.CreatedByUser)
                .Where(s => s.ShiftDate == shiftDate)
                .Select(s => new AdminShiftDto
                {
                    Id = s.Id,
                    ShiftDate = s.ShiftDate,
                    AdminId = s.AdminId,
                    AdminName = s.Admin.FirstName + " " + s.Admin.LastName,
                    AdminRole = s.Admin.Role.ToString(),
                    AdminColor = s.Admin.ShiftColor,
                    Notes = s.Notes,
                    CreatedByUserId = s.CreatedByUserId,
                    CreatedByUserName = s.CreatedByUser.FirstName + " " + s.CreatedByUser.LastName,
                    CreatedAt = s.CreatedAt,
                    UpdatedAt = s.UpdatedAt
                })
                .ToListAsync();

            return Ok(result);
        }

        // ════════════════════════════════════════════════
        //  CREATE a single shift
        // ════════════════════════════════════════════════

        [HttpPost]
        public async Task<ActionResult<AdminShiftDto>> CreateShift([FromBody] CreateAdminShiftDto dto)
        {
            var userId = GetUserId();
            var shiftDate = dto.ShiftDate.Date;

            // Validate admin exists and has Admin role
            var admin = await _context.Users.FindAsync(dto.AdminId);
            if (admin == null || admin.IsDeleted || !admin.IsActive || admin.Role != UserRole.Admin)
                return BadRequest(new { message = "Invalid admin ID. Only users with Admin role can be assigned shifts." });

            // Check for duplicate
            var exists = await _context.AdminShifts
                .AnyAsync(s => s.ShiftDate == shiftDate && s.AdminId == dto.AdminId);
            if (exists)
                return Conflict(new { message = "This admin already has a shift on this date." });

            var shift = new AdminShift
            {
                ShiftDate = shiftDate,
                AdminId = dto.AdminId,
                Notes = dto.Notes,
                CreatedByUserId = userId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.AdminShifts.Add(shift);
            await _context.SaveChangesAsync();

            await _context.Entry(shift).Reference(s => s.Admin).LoadAsync();
            await _context.Entry(shift).Reference(s => s.CreatedByUser).LoadAsync();

            return Ok(new AdminShiftDto
            {
                Id = shift.Id,
                ShiftDate = shift.ShiftDate,
                AdminId = shift.AdminId,
                AdminName = shift.Admin.FirstName + " " + shift.Admin.LastName,
                AdminRole = shift.Admin.Role.ToString(),
                Notes = shift.Notes,
                CreatedByUserId = shift.CreatedByUserId,
                CreatedByUserName = shift.CreatedByUser.FirstName + " " + shift.CreatedByUser.LastName,
                CreatedAt = shift.CreatedAt,
                UpdatedAt = shift.UpdatedAt
            });
        }

        // ════════════════════════════════════════════════
        //  DELETE a single shift
        // ════════════════════════════════════════════════

        [HttpDelete("{id}")]
        public async Task<ActionResult> DeleteShift(int id)
        {
            var shift = await _context.AdminShifts.FindAsync(id);
            if (shift == null)
                return NotFound(new { message = "Shift not found." });

            _context.AdminShifts.Remove(shift);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        // ════════════════════════════════════════════════
        //  SET admin shift color
        // ════════════════════════════════════════════════

        [HttpPut("color/{adminId}")]
        public async Task<ActionResult> SetAdminColor(int adminId, [FromBody] SetAdminColorDto dto)
        {
            var user = await _context.Users.FindAsync(adminId);
            if (user == null || user.IsDeleted || user.Role != UserRole.Admin)
                return NotFound(new { message = "Admin not found." });

            user.ShiftColor = dto.Color;
            await _context.SaveChangesAsync();
            return Ok(new { message = "Color updated." });
        }

        // ════════════════════════════════════════════════
        //  DELETE all shifts for a specific date
        // ════════════════════════════════════════════════

        [HttpDelete("date/{date}")]
        public async Task<ActionResult> DeleteShiftsByDate(DateTime date)
        {
            var shiftDate = date.Date;
            var shifts = await _context.AdminShifts
                .Where(s => s.ShiftDate == shiftDate)
                .ToListAsync();

            if (!shifts.Any())
                return NotFound(new { message = "No shifts found for this date." });

            _context.AdminShifts.RemoveRange(shifts);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
