using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Hubs;
using DreamCleaningBackend.Models;
using System.Security.Claims;
using System.Text.Json;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminTasksController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<UserManagementHub> _hubContext;

        public AdminTasksController(ApplicationDbContext context, IHubContext<UserManagementHub> hubContext)
        {
            _context = context;
            _hubContext = hubContext;
        }

        private int GetUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.Parse(userIdClaim!);
        }

        private (string name, string role) GetAdminInfo()
        {
            var name = User.FindFirst("FirstName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value ?? "Unknown";
            var role = User.FindFirst(ClaimTypes.Role)?.Value ?? "Admin";
            return (name, role);
        }

        private async Task LogActivity(string entityType, int entityId, string? entityTitle, string action, Dictionary<string, object?>? changes = null)
        {
            var userId = GetUserId();
            var (adminName, adminRole) = GetAdminInfo();

            var log = new TaskActivityLog
            {
                EntityType = entityType,
                EntityId = entityId,
                EntityTitle = entityTitle,
                Action = action,
                Changes = changes != null ? JsonSerializer.Serialize(changes) : null,
                AdminId = userId,
                AdminName = adminName,
                AdminRole = adminRole,
                CreatedAt = DateTime.UtcNow
            };

            _context.TaskActivityLogs.Add(log);
            await _context.SaveChangesAsync();
        }

        private Dictionary<string, object?> TrackChanges(Dictionary<string, (object? oldVal, object? newVal)> fields)
        {
            var changes = new Dictionary<string, object?>();
            foreach (var (field, (oldVal, newVal)) in fields)
            {
                var oldStr = oldVal?.ToString() ?? "";
                var newStr = newVal?.ToString() ?? "";
                if (oldStr != newStr)
                {
                    changes[field] = new { from = oldStr, to = newStr };
                }
            }
            return changes;
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

            await LogActivity("SharedTask", task.Id, task.Title, "Created", new Dictionary<string, object?>
            {
                ["Title"] = task.Title, ["Description"] = task.Description, ["Priority"] = task.Priority,
                ["DueDate"] = task.DueDate?.ToString("yyyy-MM-dd"), ["ClientName"] = task.ClientName,
                ["ClientEmail"] = task.ClientEmail, ["ClientPhone"] = task.ClientPhone, ["OrderId"] = task.OrderId
            });

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "shared" });

            return CreatedAtAction(nameof(GetTask), new { id = task.Id }, MapTaskToDto(task));
        }

        [HttpPut("tasks/{id}")]
        public async Task<ActionResult<AdminTaskDto>> UpdateTask(int id, [FromBody] UpdateAdminTaskDto dto)
        {
            var task = await _context.AdminTasks
                .Include(t => t.CreatedByAdmin)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (task == null) return NotFound();

            // Capture old values before update
            var oldValues = new Dictionary<string, (object? oldVal, object? newVal)>();
            if (dto.Title != null) oldValues["Title"] = (task.Title, dto.Title);
            if (dto.Description != null) oldValues["Description"] = (task.Description, dto.Description);
            if (dto.Priority != null) oldValues["Priority"] = (task.Priority, dto.Priority);
            if (dto.Status != null) oldValues["Status"] = (task.Status, dto.Status);
            if (dto.DueDate.HasValue) oldValues["DueDate"] = (task.DueDate?.ToString("yyyy-MM-dd"), dto.DueDate?.ToString("yyyy-MM-dd"));
            if (dto.ClientName != null) oldValues["ClientName"] = (task.ClientName, dto.ClientName);
            if (dto.ClientEmail != null) oldValues["ClientEmail"] = (task.ClientEmail, dto.ClientEmail);
            if (dto.ClientPhone != null) oldValues["ClientPhone"] = (task.ClientPhone, dto.ClientPhone);
            if (dto.ClientId.HasValue) oldValues["ClientId"] = (task.ClientId, dto.ClientId);
            if (dto.OrderId.HasValue) oldValues["OrderId"] = (task.OrderId, dto.OrderId);
            if (dto.CompletionNote != null) oldValues["CompletionNote"] = (task.CompletionNote, dto.CompletionNote);

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

            var changes = TrackChanges(oldValues);
            if (changes.Count > 0)
                await LogActivity("SharedTask", task.Id, task.Title, "Updated", changes);

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "shared" });

            return Ok(MapTaskToDto(task));
        }

        [HttpPut("tasks/{id}/status")]
        public async Task<ActionResult<AdminTaskDto>> UpdateTaskStatus(int id, [FromBody] UpdateTaskStatusDto dto)
        {
            var task = await _context.AdminTasks
                .Include(t => t.CreatedByAdmin)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (task == null) return NotFound();

            var oldStatus = task.Status;
            var oldNote = task.CompletionNote;

            task.Status = dto.Status;
            task.CompletedAt = dto.Status == "Done" ? DateTime.UtcNow : null;
            if (dto.CompletionNote != null) task.CompletionNote = dto.CompletionNote;
            task.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var trackFields = new Dictionary<string, (object? oldVal, object? newVal)>
            {
                ["Status"] = (oldStatus, dto.Status)
            };
            if (dto.CompletionNote != null) trackFields["CompletionNote"] = (oldNote, dto.CompletionNote);
            var changes = TrackChanges(trackFields);
            if (changes.Count > 0)
                await LogActivity("SharedTask", task.Id, task.Title, "StatusChanged", changes);

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "shared" });

            return Ok(MapTaskToDto(task));
        }

        [HttpDelete("tasks/{id}")]
        public async Task<IActionResult> DeleteTask(int id)
        {
            var task = await _context.AdminTasks.FindAsync(id);
            if (task == null) return NotFound();

            var title = task.Title;
            _context.AdminTasks.Remove(task);
            await _context.SaveChangesAsync();

            await LogActivity("SharedTask", id, title, "Deleted");

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "shared" });

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

            // filter: "my" = assigned to me, "created" = created by me, "all" = all tasks (SuperAdmin only), null/empty = both mine
            if (filter == "all")
            {
                var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
                if (role != "SuperAdmin")
                    return Forbid();
                // No user filter — return all personal tasks
            }
            else if (filter == "my")
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

            foreach (var task in createdTasks)
            {
                var assignedName = task.AssignedToAdmin != null ? $"{task.AssignedToAdmin.FirstName} {task.AssignedToAdmin.LastName}" : "Unknown";
                await LogActivity("PersonalTask", task.Id, task.Title, "Created", new Dictionary<string, object?>
                {
                    ["Title"] = task.Title, ["Description"] = task.Description, ["Priority"] = task.Priority,
                    ["DueDate"] = task.DueDate?.ToString("yyyy-MM-dd"), ["AssignedTo"] = assignedName
                });
            }

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "personal" });

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

            var userId = GetUserId();
            var isCreator = task.CreatedByAdminId == userId;

            // Capture old values for logging
            var oldValues = new Dictionary<string, (object? oldVal, object? newVal)>();
            var oldAssignedName = task.AssignedToAdmin != null ? $"{task.AssignedToAdmin.FirstName} {task.AssignedToAdmin.LastName}" : "";

            if (isCreator)
            {
                // Creator can edit everything
                if (dto.Title != null) { oldValues["Title"] = (task.Title, dto.Title); task.Title = dto.Title; }
                if (dto.Description != null) { oldValues["Description"] = (task.Description, dto.Description); task.Description = dto.Description; }
                if (dto.Priority != null) { oldValues["Priority"] = (task.Priority, dto.Priority); task.Priority = dto.Priority; }
                if (dto.Status != null)
                {
                    oldValues["Status"] = (task.Status, dto.Status);
                    task.Status = dto.Status;
                    task.CompletedAt = dto.Status == "Done" ? DateTime.UtcNow : null;
                }
                if (dto.DueDate.HasValue) { oldValues["DueDate"] = (task.DueDate?.ToString("yyyy-MM-dd"), dto.DueDate?.ToString("yyyy-MM-dd")); task.DueDate = dto.DueDate; }
                if (dto.AssignedToAdminId.HasValue) { oldValues["AssignedToAdminId"] = (task.AssignedToAdminId, dto.AssignedToAdminId); task.AssignedToAdminId = dto.AssignedToAdminId.Value; }
            }

            // Assignee (and creator) can always update completion note
            if (dto.CompletionNote != null) { oldValues["CompletionNote"] = (task.CompletionNote, dto.CompletionNote); task.CompletionNote = dto.CompletionNote; }

            task.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            if (isCreator && dto.AssignedToAdminId.HasValue)
                await _context.Entry(task).Reference(t => t.AssignedToAdmin).LoadAsync();

            // Resolve new assigned name for logging
            if (dto.AssignedToAdminId.HasValue && task.AssignedToAdmin != null)
            {
                var newAssignedName = $"{task.AssignedToAdmin.FirstName} {task.AssignedToAdmin.LastName}";
                oldValues["AssignedTo"] = (oldAssignedName, newAssignedName);
                oldValues.Remove("AssignedToAdminId");
            }

            var changes = TrackChanges(oldValues);
            if (changes.Count > 0)
                await LogActivity("PersonalTask", task.Id, task.Title, "Updated", changes);

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "personal" });

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

            var oldStatus = task.Status;
            var oldNote = task.CompletionNote;

            task.Status = dto.Status;
            task.CompletedAt = dto.Status == "Done" ? DateTime.UtcNow : null;
            if (dto.CompletionNote != null) task.CompletionNote = dto.CompletionNote;
            task.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var trackFields2 = new Dictionary<string, (object? oldVal, object? newVal)>
            {
                ["Status"] = (oldStatus, dto.Status)
            };
            if (dto.CompletionNote != null) trackFields2["CompletionNote"] = (oldNote, dto.CompletionNote);
            var changes = TrackChanges(trackFields2);
            if (changes.Count > 0)
                await LogActivity("PersonalTask", task.Id, task.Title, "StatusChanged", changes);

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "personal" });

            return Ok(MapPersonalTaskToDto(task));
        }

        [HttpDelete("personal-tasks/{id}")]
        public async Task<IActionResult> DeletePersonalTask(int id)
        {
            var userId = GetUserId();
            var task = await _context.PersonalAdminTasks
                .Include(t => t.AssignedToAdmin)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task == null) return NotFound();

            // Only the creator can delete
            var role = User.FindFirst(ClaimTypes.Role)?.Value;
            if (task.CreatedByAdminId != userId && role != "SuperAdmin")
                return Forbid();

            var title = task.Title;
            var assignedName = task.AssignedToAdmin != null ? $"{task.AssignedToAdmin.FirstName} {task.AssignedToAdmin.LastName}" : "Unknown";
            _context.PersonalAdminTasks.Remove(task);
            await _context.SaveChangesAsync();

            await LogActivity("PersonalTask", id, title, "Deleted", new Dictionary<string, object?>
            {
                ["AssignedTo"] = assignedName
            });

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "personal" });

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

            await LogActivity("ClientInteraction", interaction.Id, interaction.ClientName, "Created", new Dictionary<string, object?>
            {
                ["ClientName"] = interaction.ClientName, ["ClientPhone"] = interaction.ClientPhone,
                ["ClientEmail"] = interaction.ClientEmail, ["Type"] = interaction.Type,
                ["Notes"] = interaction.Notes, ["Status"] = interaction.Status
            });

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "interactions" });

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

            var oldValues = new Dictionary<string, (object? oldVal, object? newVal)>();
            if (dto.ClientName != null) oldValues["ClientName"] = (interaction.ClientName, dto.ClientName);
            if (dto.ClientPhone != null) oldValues["ClientPhone"] = (interaction.ClientPhone, dto.ClientPhone);
            if (dto.ClientEmail != null) oldValues["ClientEmail"] = (interaction.ClientEmail, dto.ClientEmail);
            if (dto.Type != null) oldValues["Type"] = (interaction.Type, dto.Type);
            if (dto.Notes != null) oldValues["Notes"] = (interaction.Notes, dto.Notes);
            if (dto.Status != null) oldValues["Status"] = (interaction.Status, dto.Status);

            if (dto.ClientName != null) interaction.ClientName = dto.ClientName;
            if (dto.ClientPhone != null) interaction.ClientPhone = dto.ClientPhone;
            if (dto.ClientEmail != null) interaction.ClientEmail = dto.ClientEmail;
            if (dto.Type != null) interaction.Type = dto.Type;
            if (dto.Notes != null) interaction.Notes = dto.Notes;
            if (dto.Status != null) interaction.Status = dto.Status;

            await _context.SaveChangesAsync();

            var changes = TrackChanges(oldValues);
            if (changes.Count > 0)
                await LogActivity("ClientInteraction", interaction.Id, interaction.ClientName, "Updated", changes);

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "interactions" });

            return Ok(MapInteractionToDto(interaction));
        }

        [HttpDelete("client-interactions/{id}")]
        public async Task<IActionResult> DeleteClientInteraction(int id)
        {
            var interaction = await _context.ClientInteractions.FindAsync(id);
            if (interaction == null) return NotFound();

            var clientName = interaction.ClientName;
            _context.ClientInteractions.Remove(interaction);
            await _context.SaveChangesAsync();

            await LogActivity("ClientInteraction", id, clientName, "Deleted");

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "interactions" });

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

            await LogActivity("HandoverNote", note.Id, note.Content.Length > 80 ? note.Content[..80] + "..." : note.Content, "Created", new Dictionary<string, object?>
            {
                ["Content"] = note.Content, ["TargetAudience"] = note.TargetAudience
            });

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "handover" });

            return CreatedAtAction(null, new { id = note.Id }, MapNoteToDto(note));
        }

        [HttpPut("handover-notes/{id}")]
        public async Task<ActionResult<HandoverNoteDto>> UpdateHandoverNote(
            int id, [FromBody] UpdateHandoverNoteDto dto)
        {
            var userId = GetUserId();
            var note = await _context.HandoverNotes.Include(n => n.Admin).FirstOrDefaultAsync(n => n.Id == id);
            if (note == null) return NotFound();

            var role = User.FindFirst(ClaimTypes.Role)?.Value;
            if (note.AdminId != userId && role != "SuperAdmin")
                return Forbid();

            var oldContent = note.Content;
            var oldAudience = note.TargetAudience;

            if (dto.Content != null) note.Content = dto.Content;
            if (dto.TargetAudience != null) note.TargetAudience = dto.TargetAudience;

            await _context.SaveChangesAsync();

            var changes = TrackChanges(new Dictionary<string, (object? oldVal, object? newVal)>
            {
                ["Content"] = (oldContent, note.Content),
                ["TargetAudience"] = (oldAudience, note.TargetAudience)
            });

            if (changes.Count > 0)
            {
                await LogActivity("HandoverNote", note.Id,
                    note.Content.Length > 80 ? note.Content[..80] + "..." : note.Content,
                    "Updated", changes);
            }

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "handover" });

            return Ok(MapNoteToDto(note));
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

            var content = note.Content.Length > 80 ? note.Content[..80] + "..." : note.Content;
            _context.HandoverNotes.Remove(note);
            await _context.SaveChangesAsync();

            await LogActivity("HandoverNote", id, content, "Deleted");

            await _hubContext.Clients.Group("Admins").SendAsync("TasksUpdated", new { type = "handover" });

            return NoContent();
        }

        // ════════════════════════════════════════════════
        //  ACTIVITY LOGS (SuperAdmin only)
        // ════════════════════════════════════════════════

        [HttpGet("task-activity-logs")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<List<TaskActivityLogDto>>> GetTaskActivityLogs(
            [FromQuery] string? entityType,
            [FromQuery] string? action,
            [FromQuery] int? adminId,
            [FromQuery] int? entityId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            var query = _context.TaskActivityLogs.AsQueryable();

            if (!string.IsNullOrEmpty(entityType))
                query = query.Where(l => l.EntityType == entityType);

            if (!string.IsNullOrEmpty(action))
                query = query.Where(l => l.Action == action);

            if (adminId.HasValue)
                query = query.Where(l => l.AdminId == adminId.Value);

            if (entityId.HasValue)
                query = query.Where(l => l.EntityId == entityId.Value);

            var total = await query.CountAsync();

            var logs = await query
                .OrderByDescending(l => l.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(l => new TaskActivityLogDto
                {
                    Id = l.Id,
                    EntityType = l.EntityType,
                    EntityId = l.EntityId,
                    EntityTitle = l.EntityTitle,
                    Action = l.Action,
                    Changes = l.Changes,
                    AdminId = l.AdminId,
                    AdminName = l.AdminName,
                    AdminRole = l.AdminRole,
                    CreatedAt = l.CreatedAt
                })
                .ToListAsync();

            Response.Headers.Append("X-Total-Count", total.ToString());
            return Ok(logs);
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
                ? t.CreatedByAdmin.FirstName
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
                ? ci.Admin.FirstName
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
                ? t.AssignedToAdmin.FirstName
                : "Unknown",
            AssignedToAdminRole = t.AssignedToAdmin != null ? t.AssignedToAdmin.Role.ToString() : "Admin",
            CreatedByAdminId = t.CreatedByAdminId,
            CreatedByAdminName = t.CreatedByAdmin != null
                ? t.CreatedByAdmin.FirstName
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
                ? n.Admin.FirstName
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
