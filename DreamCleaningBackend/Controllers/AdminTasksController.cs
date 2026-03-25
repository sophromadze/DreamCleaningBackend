using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminTasksController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public AdminTasksController(ApplicationDbContext context)
        {
            _context = context;
        }

        private int GetUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.Parse(userIdClaim!);
        }

        // ════════════════════════════════════════════════
        //  SEARCH (for autocomplete)
        // ════════════════════════════════════════════════

        [HttpGet("tasks/search-clients")]
        public async Task<ActionResult<List<ClientSearchResultDto>>> SearchClients([FromQuery] string? q)
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
                return Ok(new List<ClientSearchResultDto>());

            var query = q.ToLower();
            var clients = await _context.Users
                .Where(u => !u.IsDeleted &&
                    (u.FirstName.ToLower().Contains(query) ||
                     u.LastName.ToLower().Contains(query) ||
                     u.Email.ToLower().Contains(query) ||
                     (u.Phone != null && u.Phone.Contains(query))))
                .Take(15)
                .Select(u => new ClientSearchResultDto
                {
                    Id = u.Id,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    Email = u.Email,
                    Phone = u.Phone
                })
                .ToListAsync();

            return Ok(clients);
        }

        [HttpGet("tasks/search-orders")]
        public async Task<ActionResult<List<OrderSearchResultDto>>> SearchOrders([FromQuery] string? q)
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length < 1)
                return Ok(new List<OrderSearchResultDto>());

            var query = q.ToLower();

            // Try parse as order ID
            int.TryParse(q, out var orderId);

            var orders = await _context.Orders
                .Include(o => o.ServiceType)
                .Where(o =>
                    (orderId > 0 && o.Id == orderId) ||
                    o.ContactFirstName.ToLower().Contains(query) ||
                    o.ContactLastName.ToLower().Contains(query) ||
                    o.ContactEmail.ToLower().Contains(query))
                .OrderByDescending(o => o.OrderDate)
                .Take(15)
                .Select(o => new OrderSearchResultDto
                {
                    Id = o.Id,
                    ContactFirstName = o.ContactFirstName,
                    ContactLastName = o.ContactLastName,
                    ContactEmail = o.ContactEmail,
                    ContactPhone = o.ContactPhone,
                    ServiceAddress = o.ServiceAddress,
                    ServiceTypeName = o.ServiceType != null ? o.ServiceType.Name : "",
                    ServiceDate = o.ServiceDate,
                    Status = o.Status
                })
                .ToListAsync();

            return Ok(orders);
        }

        // ════════════════════════════════════════════════
        //  TASKS
        // ════════════════════════════════════════════════

        [HttpGet("tasks")]
        public async Task<ActionResult<List<AdminTaskDto>>> GetTasks(
            [FromQuery] string? status,
            [FromQuery] string? priority)
        {
            var query = _context.AdminTasks
                .Include(t => t.CreatedByAdmin)
                .AsQueryable();

            if (!string.IsNullOrEmpty(status))
                query = query.Where(t => t.Status == status);

            if (!string.IsNullOrEmpty(priority))
                query = query.Where(t => t.Priority == priority);

            var tasks = await query
                .OrderByDescending(t => t.Priority == "Urgent" ? 0 : t.Priority == "High" ? 1 : t.Priority == "Medium" ? 2 : t.Priority == "Normal" ? 3 : 4)
                .ThenBy(t => t.DueDate)
                .ThenByDescending(t => t.CreatedAt)
                .ToListAsync();

            return Ok(tasks.Select(MapTaskToDto).ToList());
        }

        [HttpGet("tasks/{id}")]
        public async Task<ActionResult<AdminTaskDto>> GetTask(int id)
        {
            var task = await _context.AdminTasks
                .Include(t => t.CreatedByAdmin)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (task == null) return NotFound();
            return Ok(MapTaskToDto(task));
        }

        [HttpPost("tasks")]
        public async Task<ActionResult<AdminTaskDto>> CreateTask([FromBody] CreateAdminTaskDto dto)
        {
            var userId = GetUserId();

            var task = new AdminTask
            {
                Title = dto.Title,
                Description = dto.Description,
                Priority = dto.Priority,
                Status = "Todo",
                DueDate = dto.DueDate,
                ClientName = dto.ClientName,
                ClientEmail = dto.ClientEmail,
                ClientPhone = dto.ClientPhone,
                ClientId = dto.ClientId,
                OrderId = dto.OrderId,
                CreatedByAdminId = userId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.AdminTasks.Add(task);
            await _context.SaveChangesAsync();

            await _context.Entry(task).Reference(t => t.CreatedByAdmin).LoadAsync();

            return CreatedAtAction(nameof(GetTask), new { id = task.Id }, MapTaskToDto(task));
        }

        [HttpPut("tasks/{id}")]
        public async Task<ActionResult<AdminTaskDto>> UpdateTask(int id, [FromBody] UpdateAdminTaskDto dto)
        {
            var task = await _context.AdminTasks
                .Include(t => t.CreatedByAdmin)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (task == null) return NotFound();

            if (dto.Title != null) task.Title = dto.Title;
            if (dto.Description != null) task.Description = dto.Description;
            if (dto.Priority != null) task.Priority = dto.Priority;
            if (dto.Status != null)
            {
                task.Status = dto.Status;
                task.CompletedAt = dto.Status == "Done" ? DateTime.UtcNow : null;
            }
            if (dto.DueDate.HasValue) task.DueDate = dto.DueDate;
            if (dto.ClientName != null) task.ClientName = dto.ClientName;
            if (dto.ClientEmail != null) task.ClientEmail = dto.ClientEmail;
            if (dto.ClientPhone != null) task.ClientPhone = dto.ClientPhone;
            if (dto.ClientId.HasValue) task.ClientId = dto.ClientId;
            if (dto.OrderId.HasValue) task.OrderId = dto.OrderId;
            if (dto.CompletionNote != null) task.CompletionNote = dto.CompletionNote;

            task.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(MapTaskToDto(task));
        }

        [HttpPut("tasks/{id}/status")]
        public async Task<ActionResult<AdminTaskDto>> UpdateTaskStatus(int id, [FromBody] UpdateTaskStatusDto dto)
        {
            var task = await _context.AdminTasks
                .Include(t => t.CreatedByAdmin)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (task == null) return NotFound();

            task.Status = dto.Status;
            task.CompletedAt = dto.Status == "Done" ? DateTime.UtcNow : null;
            if (dto.CompletionNote != null) task.CompletionNote = dto.CompletionNote;
            task.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok(MapTaskToDto(task));
        }

        [HttpDelete("tasks/{id}")]
        public async Task<IActionResult> DeleteTask(int id)
        {
            var task = await _context.AdminTasks.FindAsync(id);
            if (task == null) return NotFound();

            _context.AdminTasks.Remove(task);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        // ════════════════════════════════════════════════
        //  PERSONAL ADMIN TASKS
        // ════════════════════════════════════════════════

        [HttpGet("personal-tasks")]
        public async Task<ActionResult<List<PersonalAdminTaskDto>>> GetPersonalTasks(
            [FromQuery] string? status,
            [FromQuery] string? filter)
        {
            var userId = GetUserId();

            var query = _context.PersonalAdminTasks
                .Include(t => t.AssignedToAdmin)
                .Include(t => t.CreatedByAdmin)
                .AsQueryable();

            // filter: "my" = assigned to me, "created" = created by me, null/empty = both
            if (filter == "my")
                query = query.Where(t => t.AssignedToAdminId == userId);
            else if (filter == "created")
                query = query.Where(t => t.CreatedByAdminId == userId);
            else
                query = query.Where(t => t.AssignedToAdminId == userId || t.CreatedByAdminId == userId);

            if (!string.IsNullOrEmpty(status))
                query = query.Where(t => t.Status == status);

            var tasks = await query
                .OrderByDescending(t => t.Priority == "Urgent" ? 0 : t.Priority == "High" ? 1 : t.Priority == "Medium" ? 2 : t.Priority == "Normal" ? 3 : 4)
                .ThenBy(t => t.DueDate)
                .ThenByDescending(t => t.CreatedAt)
                .ToListAsync();

            return Ok(tasks.Select(MapPersonalTaskToDto).ToList());
        }

        [HttpGet("personal-tasks/{id}")]
        public async Task<ActionResult<PersonalAdminTaskDto>> GetPersonalTask(int id)
        {
            var task = await _context.PersonalAdminTasks
                .Include(t => t.AssignedToAdmin)
                .Include(t => t.CreatedByAdmin)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (task == null) return NotFound();
            return Ok(MapPersonalTaskToDto(task));
        }

        [HttpPost("personal-tasks")]
        public async Task<ActionResult<List<PersonalAdminTaskDto>>> CreatePersonalTask([FromBody] CreatePersonalAdminTaskDto dto)
        {
            var userId = GetUserId();

            if (dto.AssignedToAdminIds == null || dto.AssignedToAdminIds.Count == 0)
                return BadRequest("At least one admin must be assigned.");

            var createdTasks = new List<PersonalAdminTask>();

            foreach (var adminId in dto.AssignedToAdminIds)
            {
                var task = new PersonalAdminTask
                {
                    Title = dto.Title,
                    Description = dto.Description,
                    Priority = dto.Priority,
                    Status = "Todo",
                    DueDate = dto.DueDate,
                    AssignedToAdminId = adminId,
                    CreatedByAdminId = userId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.PersonalAdminTasks.Add(task);
                createdTasks.Add(task);
            }

            await _context.SaveChangesAsync();

            foreach (var task in createdTasks)
            {
                await _context.Entry(task).Reference(t => t.AssignedToAdmin).LoadAsync();
                await _context.Entry(task).Reference(t => t.CreatedByAdmin).LoadAsync();
            }

            return Ok(createdTasks.Select(MapPersonalTaskToDto).ToList());
        }

        [HttpGet("personal-tasks/pending-count")]
        public async Task<ActionResult<int>> GetPendingPersonalTaskCount()
        {
            var userId = GetUserId();
            var count = await _context.PersonalAdminTasks
                .CountAsync(t => t.AssignedToAdminId == userId && t.Status != "Done");
            return Ok(count);
        }

        [HttpPut("personal-tasks/{id}")]
        public async Task<ActionResult<PersonalAdminTaskDto>> UpdatePersonalTask(int id, [FromBody] UpdatePersonalAdminTaskDto dto)
        {
            var task = await _context.PersonalAdminTasks
                .Include(t => t.AssignedToAdmin)
                .Include(t => t.CreatedByAdmin)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (task == null) return NotFound();

            if (dto.Title != null) task.Title = dto.Title;
            if (dto.Description != null) task.Description = dto.Description;
            if (dto.Priority != null) task.Priority = dto.Priority;
            if (dto.Status != null)
            {
                task.Status = dto.Status;
                task.CompletedAt = dto.Status == "Done" ? DateTime.UtcNow : null;
            }
            if (dto.DueDate.HasValue) task.DueDate = dto.DueDate;
            if (dto.AssignedToAdminId.HasValue) task.AssignedToAdminId = dto.AssignedToAdminId.Value;
            if (dto.CompletionNote != null) task.CompletionNote = dto.CompletionNote;

            task.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            if (dto.AssignedToAdminId.HasValue)
                await _context.Entry(task).Reference(t => t.AssignedToAdmin).LoadAsync();

            return Ok(MapPersonalTaskToDto(task));
        }

        [HttpPut("personal-tasks/{id}/status")]
        public async Task<ActionResult<PersonalAdminTaskDto>> UpdatePersonalTaskStatus(int id, [FromBody] UpdateTaskStatusDto dto)
        {
            var task = await _context.PersonalAdminTasks
                .Include(t => t.AssignedToAdmin)
                .Include(t => t.CreatedByAdmin)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (task == null) return NotFound();

            task.Status = dto.Status;
            task.CompletedAt = dto.Status == "Done" ? DateTime.UtcNow : null;
            if (dto.CompletionNote != null) task.CompletionNote = dto.CompletionNote;
            task.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok(MapPersonalTaskToDto(task));
        }

        [HttpDelete("personal-tasks/{id}")]
        public async Task<IActionResult> DeletePersonalTask(int id)
        {
            var userId = GetUserId();
            var task = await _context.PersonalAdminTasks.FindAsync(id);
            if (task == null) return NotFound();

            // Only the creator can delete
            var role = User.FindFirst(ClaimTypes.Role)?.Value;
            if (task.CreatedByAdminId != userId && role != "SuperAdmin")
                return Forbid();

            _context.PersonalAdminTasks.Remove(task);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        // ════════════════════════════════════════════════
        //  CLIENT INTERACTIONS
        // ════════════════════════════════════════════════

        [HttpGet("client-interactions")]
        public async Task<ActionResult<List<ClientInteractionDto>>> GetClientInteractions(
            [FromQuery] string? search,
            [FromQuery] string? status,
            [FromQuery] int? adminId,
            [FromQuery] string? period)
        {
            var query = _context.ClientInteractions
                .Include(ci => ci.Admin)
                .AsQueryable();

            if (!string.IsNullOrEmpty(search))
            {
                var q = search.ToLower();
                query = query.Where(ci =>
                    ci.ClientName.ToLower().Contains(q) ||
                    (ci.ClientPhone != null && ci.ClientPhone.Contains(q)) ||
                    (ci.ClientEmail != null && ci.ClientEmail.ToLower().Contains(q)) ||
                    (ci.Notes != null && ci.Notes.ToLower().Contains(q)));
            }

            if (!string.IsNullOrEmpty(status))
                query = query.Where(ci => ci.Status == status);

            if (adminId.HasValue)
                query = query.Where(ci => ci.AdminId == adminId.Value);

            if (!string.IsNullOrEmpty(period))
            {
                var now = DateTime.UtcNow;
                DateTime from = period switch
                {
                    "today" => now.Date,
                    "thisWeek" => now.Date.AddDays(-(int)now.DayOfWeek),
                    "thisMonth" => new DateTime(now.Year, now.Month, 1),
                    _ => DateTime.MinValue
                };
                if (from != DateTime.MinValue)
                    query = query.Where(ci => ci.InteractionDate >= from);
            }

            var items = await query
                .OrderByDescending(ci => ci.InteractionDate)
                .ToListAsync();

            return Ok(items.Select(MapInteractionToDto).ToList());
        }

        [HttpPost("client-interactions")]
        public async Task<ActionResult<ClientInteractionDto>> CreateClientInteraction(
            [FromBody] CreateClientInteractionDto dto)
        {
            var userId = GetUserId();

            var interaction = new ClientInteraction
            {
                ClientName = dto.ClientName,
                ClientPhone = dto.ClientPhone,
                ClientEmail = dto.ClientEmail,
                ClientId = dto.ClientId,
                InteractionDate = DateTime.UtcNow,
                AdminId = userId,
                Type = dto.Type,
                Notes = dto.Notes,
                Status = dto.Status,
                CreatedAt = DateTime.UtcNow
            };

            _context.ClientInteractions.Add(interaction);
            await _context.SaveChangesAsync();

            await _context.Entry(interaction).Reference(ci => ci.Admin).LoadAsync();

            return CreatedAtAction(null, new { id = interaction.Id }, MapInteractionToDto(interaction));
        }

        [HttpPut("client-interactions/{id}")]
        public async Task<ActionResult<ClientInteractionDto>> UpdateClientInteraction(
            int id, [FromBody] UpdateClientInteractionDto dto)
        {
            var interaction = await _context.ClientInteractions
                .Include(ci => ci.Admin)
                .FirstOrDefaultAsync(ci => ci.Id == id);

            if (interaction == null) return NotFound();

            if (dto.ClientName != null) interaction.ClientName = dto.ClientName;
            if (dto.ClientPhone != null) interaction.ClientPhone = dto.ClientPhone;
            if (dto.ClientEmail != null) interaction.ClientEmail = dto.ClientEmail;
            if (dto.Type != null) interaction.Type = dto.Type;
            if (dto.Notes != null) interaction.Notes = dto.Notes;
            if (dto.Status != null) interaction.Status = dto.Status;

            await _context.SaveChangesAsync();
            return Ok(MapInteractionToDto(interaction));
        }

        [HttpDelete("client-interactions/{id}")]
        public async Task<IActionResult> DeleteClientInteraction(int id)
        {
            var interaction = await _context.ClientInteractions.FindAsync(id);
            if (interaction == null) return NotFound();

            _context.ClientInteractions.Remove(interaction);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        // ════════════════════════════════════════════════
        //  HANDOVER NOTES
        // ════════════════════════════════════════════════

        [HttpGet("handover-notes")]
        public async Task<ActionResult<List<HandoverNoteDto>>> GetHandoverNotes()
        {
            var notes = await _context.HandoverNotes
                .Include(n => n.Admin)
                .OrderByDescending(n => n.CreatedAt)
                .ToListAsync();

            return Ok(notes.Select(MapNoteToDto).ToList());
        }

        [HttpPost("handover-notes")]
        public async Task<ActionResult<HandoverNoteDto>> CreateHandoverNote(
            [FromBody] CreateHandoverNoteDto dto)
        {
            var userId = GetUserId();

            var today = DateTime.UtcNow.Date;
            var taskCount = await _context.AdminTasks
                .CountAsync(t => t.CreatedByAdminId == userId && t.CreatedAt >= today);
            var interactionCount = await _context.ClientInteractions
                .CountAsync(ci => ci.AdminId == userId && ci.CreatedAt >= today);

            var note = new HandoverNote
            {
                Content = dto.Content,
                AdminId = userId,
                TargetAudience = dto.TargetAudience,
                TaskCount = taskCount,
                InteractionCount = interactionCount,
                CreatedAt = DateTime.UtcNow
            };

            _context.HandoverNotes.Add(note);
            await _context.SaveChangesAsync();

            await _context.Entry(note).Reference(n => n.Admin).LoadAsync();

            return CreatedAtAction(null, new { id = note.Id }, MapNoteToDto(note));
        }

        [HttpDelete("handover-notes/{id}")]
        public async Task<IActionResult> DeleteHandoverNote(int id)
        {
            var userId = GetUserId();
            var note = await _context.HandoverNotes.FindAsync(id);
            if (note == null) return NotFound();

            var role = User.FindFirst(ClaimTypes.Role)?.Value;
            if (note.AdminId != userId && role != "SuperAdmin")
                return Forbid();

            _context.HandoverNotes.Remove(note);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        // ════════════════════════════════════════════════
        //  MAPPERS
        // ════════════════════════════════════════════════

        private static AdminTaskDto MapTaskToDto(AdminTask t) => new()
        {
            Id = t.Id,
            Title = t.Title,
            Description = t.Description,
            Priority = t.Priority,
            Status = t.Status,
            DueDate = t.DueDate,
            ClientName = t.ClientName,
            ClientEmail = t.ClientEmail,
            ClientPhone = t.ClientPhone,
            ClientId = t.ClientId,
            OrderId = t.OrderId,
            CreatedByAdminId = t.CreatedByAdminId,
            CreatedByAdminName = t.CreatedByAdmin != null
                ? $"{t.CreatedByAdmin.FirstName} {t.CreatedByAdmin.LastName}"
                : "Unknown",
            CreatedByAdminRole = t.CreatedByAdmin != null ? t.CreatedByAdmin.Role.ToString() : "Admin",
            CompletionNote = t.CompletionNote,
            CompletedAt = t.CompletedAt,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt
        };

        private static ClientInteractionDto MapInteractionToDto(ClientInteraction ci) => new()
        {
            Id = ci.Id,
            ClientName = ci.ClientName,
            ClientPhone = ci.ClientPhone,
            ClientEmail = ci.ClientEmail,
            ClientId = ci.ClientId,
            InteractionDate = ci.InteractionDate,
            AdminName = ci.Admin != null
                ? $"{ci.Admin.FirstName} {ci.Admin.LastName}"
                : "Unknown",
            AdminRole = ci.Admin != null ? ci.Admin.Role.ToString() : "Admin",
            AdminId = ci.AdminId,
            Type = ci.Type,
            Notes = ci.Notes,
            Status = ci.Status,
            CreatedAt = ci.CreatedAt
        };

        private static PersonalAdminTaskDto MapPersonalTaskToDto(PersonalAdminTask t) => new()
        {
            Id = t.Id,
            Title = t.Title,
            Description = t.Description,
            Priority = t.Priority,
            Status = t.Status,
            DueDate = t.DueDate,
            AssignedToAdminId = t.AssignedToAdminId,
            AssignedToAdminName = t.AssignedToAdmin != null
                ? $"{t.AssignedToAdmin.FirstName} {t.AssignedToAdmin.LastName}"
                : "Unknown",
            AssignedToAdminRole = t.AssignedToAdmin != null ? t.AssignedToAdmin.Role.ToString() : "Admin",
            CreatedByAdminId = t.CreatedByAdminId,
            CreatedByAdminName = t.CreatedByAdmin != null
                ? $"{t.CreatedByAdmin.FirstName} {t.CreatedByAdmin.LastName}"
                : "Unknown",
            CreatedByAdminRole = t.CreatedByAdmin != null ? t.CreatedByAdmin.Role.ToString() : "Admin",
            CompletionNote = t.CompletionNote,
            CompletedAt = t.CompletedAt,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt
        };

        private static HandoverNoteDto MapNoteToDto(HandoverNote n) => new()
        {
            Id = n.Id,
            Content = n.Content,
            AdminName = n.Admin != null
                ? $"{n.Admin.FirstName} {n.Admin.LastName}"
                : "Unknown",
            AdminRole = n.Admin != null ? n.Admin.Role.ToString() : "Admin",
            AdminId = n.AdminId,
            TargetAudience = n.TargetAudience,
            CreatedAt = n.CreatedAt,
            TaskCount = n.TaskCount,
            InteractionCount = n.InteractionCount
        };
    }
}
