using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers.Crm
{
    /// <summary>
    /// CRM sales pipeline. Drives the /admin/crm Leads board: the Kanban pipeline,
    /// lead detail + activity timeline, stage moves, and manual lead entry. Inbound
    /// capture (contact form / quote / live chat) goes through <see cref="ILeadCaptureService"/>;
    /// this controller owns the admin-facing CRUD on top of those leads.
    /// </summary>
    [Route("api/crm/leads")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class CrmLeadsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ILeadCaptureService _leadCapture;

        public CrmLeadsController(ApplicationDbContext context, ILeadCaptureService leadCapture)
        {
            _context = context;
            _leadCapture = leadCapture;
        }

        // ─────────────────────────────────────────────────────────
        //  PIPELINE (Kanban board, grouped by stage)
        // ─────────────────────────────────────────────────────────

        [HttpGet("pipeline")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<LeadPipelineColumnDto>>> GetPipeline(
            [FromQuery] string? search,
            [FromQuery] string? source,
            [FromQuery] int? assignedToAdminId)
        {
            var query = BuildFilteredQuery(search, source, assignedToAdminId, stage: null);

            var leads = await query
                .Include(l => l.AssignedToAdmin)
                .OrderByDescending(l => l.LastActivityAt)
                .ToListAsync();

            // Build a column for every stage so empty columns still render in the board.
            var columns = LeadStage.All.Select(stage =>
            {
                var inStage = leads.Where(l => l.Stage == stage).ToList();
                return new LeadPipelineColumnDto
                {
                    Stage = stage,
                    Count = inStage.Count,
                    TotalEstimatedValue = inStage.Sum(l => l.EstimatedValue ?? 0m),
                    Leads = inStage.Select(MapToDto).ToList()
                };
            }).ToList();

            return Ok(columns);
        }

        [HttpGet("stats")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<LeadStatsDto>> GetStats()
        {
            var now = DateTime.UtcNow;
            var leads = await _context.Leads
                .Select(l => new { l.Stage, l.EstimatedValue, l.NextFollowUpDate })
                .ToListAsync();

            int CountStage(string s) => leads.Count(l => l.Stage == s);
            var won = CountStage(LeadStage.Won);
            var lost = CountStage(LeadStage.Lost);
            var closed = won + lost;
            var openStages = new[] { LeadStage.New, LeadStage.Contacted, LeadStage.Quoted };

            var stats = new LeadStatsDto
            {
                Total = leads.Count,
                New = CountStage(LeadStage.New),
                Contacted = CountStage(LeadStage.Contacted),
                Quoted = CountStage(LeadStage.Quoted),
                Won = won,
                Lost = lost,
                OpenCount = leads.Count(l => openStages.Contains(l.Stage)),
                OpenPipelineValue = leads.Where(l => openStages.Contains(l.Stage)).Sum(l => l.EstimatedValue ?? 0m),
                ConversionRate = closed == 0 ? (double?)null : Math.Round((double)won / closed * 100, 1),
                DueFollowUps = leads.Count(l => openStages.Contains(l.Stage)
                    && l.NextFollowUpDate.HasValue && l.NextFollowUpDate.Value <= now)
            };

            return Ok(stats);
        }

        // ─────────────────────────────────────────────────────────
        //  LIST + DETAIL
        // ─────────────────────────────────────────────────────────

        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<LeadDto>>> GetLeads(
            [FromQuery] string? search,
            [FromQuery] string? stage,
            [FromQuery] string? source,
            [FromQuery] int? assignedToAdminId)
        {
            var leads = await BuildFilteredQuery(search, source, assignedToAdminId, stage)
                .Include(l => l.AssignedToAdmin)
                .OrderByDescending(l => l.LastActivityAt)
                .ToListAsync();

            return Ok(leads.Select(MapToDto).ToList());
        }

        [HttpGet("{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<LeadDetailDto>> GetLead(int id)
        {
            var lead = await _context.Leads
                .Include(l => l.AssignedToAdmin)
                .Include(l => l.Activities)
                .FirstOrDefaultAsync(l => l.Id == id);

            if (lead == null) return NotFound(new { message = "Lead not found" });

            var dto = MapToDetailDto(lead);
            return Ok(dto);
        }

        // ─────────────────────────────────────────────────────────
        //  CREATE / UPDATE / DELETE
        // ─────────────────────────────────────────────────────────

        [HttpPost]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<LeadDetailDto>> CreateLead([FromBody] CreateLeadDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var source = LeadSource.IsValid(dto.Source) ? dto.Source! : LeadSource.Manual;

            var lead = new Lead
            {
                FirstName = Trim(dto.FirstName),
                LastName = Trim(dto.LastName),
                Email = string.IsNullOrWhiteSpace(dto.Email) ? null : dto.Email.Trim().ToLowerInvariant(),
                Phone = dto.Phone,
                ServiceAddress = Trim(dto.ServiceAddress),
                CleaningType = Trim(dto.CleaningType),
                Message = Trim(dto.Message),
                Stage = LeadStage.New,
                Source = source,
                EstimatedValue = dto.EstimatedValue,
                AssignedToAdminId = dto.AssignedToAdminId,
                ClientId = dto.ClientId,
                NextFollowUpDate = dto.NextFollowUpDate,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            };

            _context.Leads.Add(lead);
            await _context.SaveChangesAsync();

            await AddActivity(lead.Id, LeadActivityType.System, "Lead created", system: true);
            await _context.SaveChangesAsync();

            await _context.Entry(lead).Reference(l => l.AssignedToAdmin).LoadAsync();
            await _context.Entry(lead).Collection(l => l.Activities).LoadAsync();

            return CreatedAtAction(nameof(GetLead), new { id = lead.Id }, MapToDetailDto(lead));
        }

        [HttpPut("{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<LeadDetailDto>> UpdateLead(int id, [FromBody] UpdateLeadDto dto)
        {
            var lead = await _context.Leads
                .Include(l => l.AssignedToAdmin)
                .Include(l => l.Activities)
                .FirstOrDefaultAsync(l => l.Id == id);

            if (lead == null) return NotFound(new { message = "Lead not found" });

            if (dto.FirstName != null) lead.FirstName = Trim(dto.FirstName);
            if (dto.LastName != null) lead.LastName = Trim(dto.LastName);
            if (dto.Email != null) lead.Email = string.IsNullOrWhiteSpace(dto.Email) ? null : dto.Email.Trim().ToLowerInvariant();
            if (dto.Phone != null) lead.Phone = dto.Phone;
            if (dto.ServiceAddress != null) lead.ServiceAddress = Trim(dto.ServiceAddress);
            if (dto.CleaningType != null) lead.CleaningType = Trim(dto.CleaningType);
            if (dto.Message != null) lead.Message = Trim(dto.Message);
            if (dto.EstimatedValue.HasValue) lead.EstimatedValue = dto.EstimatedValue;

            // Assigned admin: explicit clear wins, otherwise set when provided.
            if (dto.ClearAssignedAdmin) lead.AssignedToAdminId = null;
            else if (dto.AssignedToAdminId.HasValue) lead.AssignedToAdminId = dto.AssignedToAdminId;

            if (dto.ClearNextFollowUpDate) lead.NextFollowUpDate = null;
            else if (dto.NextFollowUpDate.HasValue) lead.NextFollowUpDate = dto.NextFollowUpDate;

            lead.UpdatedAt = DateTime.UtcNow;
            lead.LastActivityAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Reload assigned admin if it changed
            await _context.Entry(lead).Reference(l => l.AssignedToAdmin).LoadAsync();

            return Ok(MapToDetailDto(lead));
        }

        [HttpPut("{id}/stage")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<LeadDetailDto>> UpdateStage(int id, [FromBody] UpdateLeadStageDto dto)
        {
            if (!LeadStage.IsValid(dto.Stage))
                return BadRequest(new { message = $"Invalid stage '{dto.Stage}'." });

            var lead = await _context.Leads
                .Include(l => l.AssignedToAdmin)
                .Include(l => l.Activities)
                .FirstOrDefaultAsync(l => l.Id == id);

            if (lead == null) return NotFound(new { message = "Lead not found" });

            var from = lead.Stage;
            if (from == dto.Stage)
            {
                // No-op stage move — still return current state so the UI stays in sync.
                return Ok(MapToDetailDto(lead));
            }

            lead.Stage = dto.Stage;
            lead.LostReason = dto.Stage == LeadStage.Lost ? Trim(dto.LostReason) : null;
            lead.UpdatedAt = DateTime.UtcNow;
            lead.LastActivityAt = DateTime.UtcNow;

            var summary = $"Stage changed: {from} → {dto.Stage}";
            if (dto.Stage == LeadStage.Lost && !string.IsNullOrWhiteSpace(dto.LostReason))
                summary += $" (reason: {dto.LostReason!.Trim()})";

            await AddActivity(lead.Id, LeadActivityType.StageChange, summary);
            await _context.SaveChangesAsync();

            await _context.Entry(lead).Collection(l => l.Activities).LoadAsync();
            return Ok(MapToDetailDto(lead));
        }

        [HttpDelete("{id}")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteLead(int id)
        {
            var lead = await _context.Leads.FirstOrDefaultAsync(l => l.Id == id);
            if (lead == null) return NotFound(new { message = "Lead not found" });

            _context.Leads.Remove(lead); // activities cascade
            await _context.SaveChangesAsync();
            return Ok(new { message = "Lead deleted" });
        }

        // ─────────────────────────────────────────────────────────
        //  ACTIVITIES (timeline)
        // ─────────────────────────────────────────────────────────

        [HttpPost("{id}/activities")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<LeadActivityDto>> AddLeadActivity(int id, [FromBody] CreateLeadActivityDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var lead = await _context.Leads.FirstOrDefaultAsync(l => l.Id == id);
            if (lead == null) return NotFound(new { message = "Lead not found" });

            var type = LeadActivityType.IsValid(dto.Type) ? dto.Type! : LeadActivityType.Note;
            var activity = await AddActivity(id, type, dto.Content.Trim());

            lead.LastActivityAt = DateTime.UtcNow;
            lead.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(MapActivityToDto(activity));
        }

        // ─────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────

        private IQueryable<Lead> BuildFilteredQuery(string? search, string? source, int? assignedToAdminId, string? stage)
        {
            var query = _context.Leads.AsQueryable();

            if (!string.IsNullOrWhiteSpace(stage) && LeadStage.IsValid(stage))
                query = query.Where(l => l.Stage == stage);

            if (!string.IsNullOrWhiteSpace(source) && LeadSource.IsValid(source))
                query = query.Where(l => l.Source == source);

            if (assignedToAdminId.HasValue)
                query = query.Where(l => l.AssignedToAdminId == assignedToAdminId.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var q = search.Trim().ToLower();
                query = query.Where(l =>
                    (l.FirstName != null && l.FirstName.ToLower().Contains(q)) ||
                    (l.LastName != null && l.LastName.ToLower().Contains(q)) ||
                    (l.Email != null && l.Email.ToLower().Contains(q)) ||
                    (l.Phone != null && l.Phone.Contains(q)) ||
                    (l.ServiceAddress != null && l.ServiceAddress.ToLower().Contains(q)));
            }

            return query;
        }

        private async Task<LeadActivity> AddActivity(int leadId, string type, string? content, bool system = false)
        {
            var activity = new LeadActivity
            {
                LeadId = leadId,
                Type = type,
                Content = content,
                AdminId = system ? null : GetUserId(),
                AdminName = system ? "System" : GetUserDisplayName(),
                CreatedAt = DateTime.UtcNow
            };
            _context.LeadActivities.Add(activity);
            return activity;
        }

        private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static LeadDto MapToDto(Lead l) => new()
        {
            Id = l.Id,
            FirstName = l.FirstName,
            LastName = l.LastName,
            FullName = l.FullName,
            Email = l.Email,
            Phone = l.Phone,
            ServiceAddress = l.ServiceAddress,
            CleaningType = l.CleaningType,
            Message = l.Message,
            Stage = l.Stage,
            Source = l.Source,
            EstimatedValue = l.EstimatedValue,
            AssignedToAdminId = l.AssignedToAdminId,
            AssignedToAdminName = l.AssignedToAdmin != null
                ? $"{l.AssignedToAdmin.FirstName} {l.AssignedToAdmin.LastName}".Trim()
                : null,
            ClientId = l.ClientId,
            ConvertedOrderId = l.ConvertedOrderId,
            LostReason = l.LostReason,
            NextFollowUpDate = l.NextFollowUpDate,
            CreatedAt = l.CreatedAt,
            UpdatedAt = l.UpdatedAt,
            LastActivityAt = l.LastActivityAt
        };

        private static LeadDetailDto MapToDetailDto(Lead l)
        {
            var baseDto = MapToDto(l);
            var detail = new LeadDetailDto
            {
                Id = baseDto.Id,
                FirstName = baseDto.FirstName,
                LastName = baseDto.LastName,
                FullName = baseDto.FullName,
                Email = baseDto.Email,
                Phone = baseDto.Phone,
                ServiceAddress = baseDto.ServiceAddress,
                CleaningType = baseDto.CleaningType,
                Message = baseDto.Message,
                Stage = baseDto.Stage,
                Source = baseDto.Source,
                EstimatedValue = baseDto.EstimatedValue,
                AssignedToAdminId = baseDto.AssignedToAdminId,
                AssignedToAdminName = baseDto.AssignedToAdminName,
                ClientId = baseDto.ClientId,
                ConvertedOrderId = baseDto.ConvertedOrderId,
                LostReason = baseDto.LostReason,
                NextFollowUpDate = baseDto.NextFollowUpDate,
                CreatedAt = baseDto.CreatedAt,
                UpdatedAt = baseDto.UpdatedAt,
                LastActivityAt = baseDto.LastActivityAt,
                Activities = (l.Activities ?? new List<LeadActivity>())
                    .OrderByDescending(a => a.CreatedAt)
                    .Select(MapActivityToDto)
                    .ToList()
            };
            return detail;
        }

        private static LeadActivityDto MapActivityToDto(LeadActivity a) => new()
        {
            Id = a.Id,
            LeadId = a.LeadId,
            Type = a.Type,
            Content = a.Content,
            AdminId = a.AdminId,
            AdminName = a.AdminName,
            CreatedAt = a.CreatedAt
        };

        private int GetUserId()
        {
            var id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(id, out var parsed) ? parsed : 0;
        }

        private string GetUserDisplayName()
        {
            var first = User.FindFirst(ClaimTypes.GivenName)?.Value ?? User.FindFirst("FirstName")?.Value;
            var last = User.FindFirst(ClaimTypes.Surname)?.Value ?? User.FindFirst("LastName")?.Value;
            var combined = $"{first} {last}".Trim();
            if (!string.IsNullOrWhiteSpace(combined)) return combined;
            return User.FindFirst(ClaimTypes.Name)?.Value
                ?? User.FindFirst(ClaimTypes.Email)?.Value
                ?? "Admin";
        }
    }
}
