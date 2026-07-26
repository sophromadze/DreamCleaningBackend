using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
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
            [FromQuery] string? type,
            [FromQuery] int? assignedToAdminId,
            [FromQuery] string? period,
            [FromQuery] string? dateField)
        {
            // The board shows only active (non-archived) leads; archived leads live in the drawer.
            var query = await ExcludeBlockedUserLeadsAsync(
                BuildFilteredQuery(search, source, type, assignedToAdminId, stage: null,
                    period, dateField, archived: false));

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

            await ApplyClientFlagsAsync(columns.SelectMany(c => c.Leads).ToList());
            return Ok(columns);
        }

        [HttpGet("stats")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<LeadStatsDto>> GetStats()
        {
            var now = DateTime.UtcNow;
            // Stats describe the active pipeline — archived leads are parked, not counted.
            var statsQuery = await ExcludeBlockedUserLeadsAsync(_context.Leads.Where(l => !l.IsArchived));
            var leads = await statsQuery
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
            [FromQuery] string? type,
            [FromQuery] int? assignedToAdminId,
            [FromQuery] string? period,
            [FromQuery] string? dateField,
            [FromQuery] bool? archived)
        {
            var query = await ExcludeBlockedUserLeadsAsync(
                BuildFilteredQuery(search, source, type, assignedToAdminId, stage,
                    period, dateField, archived));

            var leads = await query
                .Include(l => l.AssignedToAdmin)
                .OrderByDescending(l => l.LastActivityAt)
                .ToListAsync();

            var dtos = leads.Select(MapToDto).ToList();
            await ApplyClientFlagsAsync(dtos);
            return Ok(dtos);
        }

        /// <summary>
        /// Archived leads, shown in the drawer below the board. Honors the same search /
        /// source / type / date filters as the pipeline.
        /// </summary>
        [HttpGet("archived")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<LeadDto>>> GetArchived(
            [FromQuery] string? search,
            [FromQuery] string? source,
            [FromQuery] string? type,
            [FromQuery] int? assignedToAdminId,
            [FromQuery] string? period,
            [FromQuery] string? dateField)
        {
            var query = await ExcludeBlockedUserLeadsAsync(
                BuildFilteredQuery(search, source, type, assignedToAdminId, stage: null,
                    period, dateField, archived: true));

            var leads = await query
                .Include(l => l.AssignedToAdmin)
                .OrderByDescending(l => l.ArchivedAt ?? l.LastActivityAt)
                .ToListAsync();

            var dtos = leads.Select(MapToDto).ToList();
            await ApplyClientFlagsAsync(dtos);
            return Ok(dtos);
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

            // Hidden the same way the pipeline/list hide it when it belongs to a blocked user.
            var visibleQuery = await ExcludeBlockedUserLeadsAsync(_context.Leads.Where(l => l.Id == id));
            if (!await visibleQuery.AnyAsync()) return NotFound(new { message = "Lead not found" });

            var dto = MapToDetailDto(lead);
            await ApplyClientFlagsAsync(new[] { dto });
            return Ok(dto);
        }

        /// <summary>
        /// Add-lead form values derived from an existing order, so the admin can create a
        /// lead by order id and have the form auto-filled (contact, address, cleaning type,
        /// order total as estimated value, special instructions).
        /// </summary>
        [HttpGet("order-prefill/{orderId}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<LeadOrderPrefillDto>> GetOrderPrefill(int orderId)
        {
            var order = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.OrderExtraServices).ThenInclude(oes => oes.ExtraService)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null) return NotFound(new { message = $"Order #{orderId} not found" });

            // Compose the full one-line address the same way order views display it.
            var addressParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(order.ServiceAddress)) addressParts.Add(order.ServiceAddress.Trim());
            if (!string.IsNullOrWhiteSpace(order.AptSuite)) addressParts.Add($"Apt {order.AptSuite.Trim()}");
            var cityLine = string.Join(", ", new[] { order.City, order.State }
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
            if (!string.IsNullOrWhiteSpace(order.ZipCode)) cityLine = $"{cityLine} {order.ZipCode.Trim()}".Trim();
            if (!string.IsNullOrWhiteSpace(cityLine)) addressParts.Add(cityLine);

            var serviceTypeName = order.GetDisplayServiceTypeName();
            var isCommercial = serviceTypeName.Contains("office", StringComparison.OrdinalIgnoreCase)
                || serviceTypeName.Contains("commercial", StringComparison.OrdinalIgnoreCase);

            // Residential orders are split into Deep / Regular the same way the admin orders
            // table does it: a deep-cleaning extra on the order (not super-deep) marks it Deep.
            // Prefer the ExtraService flags; fall back to name matching for legacy data.
            var normalizedTypeName = new string(serviceTypeName.ToLowerInvariant()
                .Where(char.IsLetter).ToArray());
            if (normalizedTypeName == "residentialcleaning")
            {
                var isDeep = order.OrderExtraServices.Any(oes =>
                    oes.ExtraService != null
                    && !oes.ExtraService.IsSuperDeepCleaning
                    && (oes.ExtraService.IsDeepCleaning
                        || (oes.ExtraService.Name != null
                            && oes.ExtraService.Name.Contains("deep", StringComparison.OrdinalIgnoreCase)
                            && !oes.ExtraService.Name.Contains("super", StringComparison.OrdinalIgnoreCase))));
                serviceTypeName = isDeep ? "Deep Cleaning" : "Regular Cleaning";
            }

            var dto = new LeadOrderPrefillDto
            {
                OrderId = order.Id,
                FirstName = Trim(order.ContactFirstName),
                LastName = Trim(order.ContactLastName),
                // Never copy generated no-email placeholders into a lead.
                Email = NoEmailHelper.IsPlaceholder(order.ContactEmail) ? null : Trim(order.ContactEmail),
                Phone = Trim(order.ContactPhone),
                ServiceAddress = addressParts.Count > 0 ? string.Join(", ", addressParts) : null,
                CleaningType = Trim(serviceTypeName),
                Type = isCommercial ? LeadType.Commercial : LeadType.Residential,
                Message = Trim(order.SpecialInstructions),
                EstimatedValue = order.Total,
                ClientId = order.UserId
            };

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
            var type = LeadType.IsValid(dto.Type) ? dto.Type! : LeadType.Residential;

            var lead = new Lead
            {
                FirstName = Trim(dto.FirstName),
                LastName = Trim(dto.LastName),
                Email = string.IsNullOrWhiteSpace(dto.Email) ? null : dto.Email.Trim().ToLowerInvariant(),
                Phone = dto.Phone,
                ServiceAddress = Trim(dto.ServiceAddress),
                CleaningType = Trim(dto.CleaningType),
                Type = type,
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
            if (dto.SourceOrderId.HasValue)
                await AddActivity(lead.Id, LeadActivityType.System, $"Imported from order #{dto.SourceOrderId.Value}", system: true);
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

            // Track field-level changes so every edit lands in the timeline (not just stage moves).
            var changes = new List<string>();

            if (dto.FirstName != null) { var v = Trim(dto.FirstName); if (v != lead.FirstName) { lead.FirstName = v; changes.Add("first name"); } }
            if (dto.LastName != null) { var v = Trim(dto.LastName); if (v != lead.LastName) { lead.LastName = v; changes.Add("last name"); } }
            if (dto.Email != null) { var v = string.IsNullOrWhiteSpace(dto.Email) ? null : dto.Email.Trim().ToLowerInvariant(); if (v != lead.Email) { lead.Email = v; changes.Add("email"); } }
            if (dto.Phone != null) { var oldPhone = lead.Phone; lead.Phone = dto.Phone; if (lead.Phone != oldPhone) changes.Add("phone"); }
            if (dto.ServiceAddress != null) { var v = Trim(dto.ServiceAddress); if (v != lead.ServiceAddress) { lead.ServiceAddress = v; changes.Add("address"); } }
            if (dto.CleaningType != null) { var v = Trim(dto.CleaningType); if (v != lead.CleaningType) { lead.CleaningType = v; changes.Add("cleaning type"); } }
            if (LeadType.IsValid(dto.Type) && dto.Type != lead.Type) { lead.Type = dto.Type!; changes.Add("type"); }
            if (dto.Message != null) { var v = Trim(dto.Message); if (v != lead.Message) { lead.Message = v; changes.Add("message"); } }
            if (dto.EstimatedValue.HasValue && dto.EstimatedValue != lead.EstimatedValue) { lead.EstimatedValue = dto.EstimatedValue; changes.Add("estimated value"); }

            // Assigned admin: explicit clear wins, otherwise set when provided.
            if (dto.ClearAssignedAdmin) { if (lead.AssignedToAdminId != null) { lead.AssignedToAdminId = null; changes.Add("unassigned admin"); } }
            else if (dto.AssignedToAdminId.HasValue && dto.AssignedToAdminId != lead.AssignedToAdminId) { lead.AssignedToAdminId = dto.AssignedToAdminId; changes.Add("assigned admin"); }

            // Follow-up date is called out explicitly with its new value.
            if (dto.ClearNextFollowUpDate) { if (lead.NextFollowUpDate != null) { lead.NextFollowUpDate = null; changes.Add("cleared follow-up date"); } }
            else if (dto.NextFollowUpDate.HasValue && dto.NextFollowUpDate != lead.NextFollowUpDate) { lead.NextFollowUpDate = dto.NextFollowUpDate; changes.Add($"follow-up date → {dto.NextFollowUpDate.Value:MMM d, yyyy}"); }

            lead.UpdatedAt = DateTime.UtcNow;
            lead.LastActivityAt = DateTime.UtcNow;

            if (changes.Count > 0)
                await AddActivity(lead.Id, LeadActivityType.Update, "Updated " + string.Join(", ", changes));

            await _context.SaveChangesAsync();

            // Reload assigned admin (may have changed) and the timeline (new Update entry).
            await _context.Entry(lead).Reference(l => l.AssignedToAdmin).LoadAsync();
            await _context.Entry(lead).Collection(l => l.Activities).LoadAsync();

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

        /// <summary>
        /// Soft-archive a lead — moves it out of the board into the archive drawer.
        /// Available to any pipeline editor (Admin/Moderator/SuperAdmin); this is the
        /// non-destructive alternative to delete that Admins use instead of removing leads.
        /// </summary>
        [HttpPut("{id}/archive")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<LeadDetailDto>> ArchiveLead(int id)
        {
            var lead = await _context.Leads
                .Include(l => l.AssignedToAdmin)
                .Include(l => l.Activities)
                .FirstOrDefaultAsync(l => l.Id == id);

            if (lead == null) return NotFound(new { message = "Lead not found" });

            if (!lead.IsArchived)
            {
                lead.IsArchived = true;
                lead.ArchivedAt = DateTime.UtcNow;
                lead.UpdatedAt = DateTime.UtcNow;
                lead.LastActivityAt = DateTime.UtcNow;

                await AddActivity(lead.Id, LeadActivityType.System, "Lead archived");
                await _context.SaveChangesAsync();
                await _context.Entry(lead).Collection(l => l.Activities).LoadAsync();
            }

            return Ok(MapToDetailDto(lead));
        }

        /// <summary>Restore an archived lead back into the active board.</summary>
        [HttpPut("{id}/unarchive")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<LeadDetailDto>> UnarchiveLead(int id)
        {
            var lead = await _context.Leads
                .Include(l => l.AssignedToAdmin)
                .Include(l => l.Activities)
                .FirstOrDefaultAsync(l => l.Id == id);

            if (lead == null) return NotFound(new { message = "Lead not found" });

            if (lead.IsArchived)
            {
                lead.IsArchived = false;
                lead.ArchivedAt = null;
                lead.UpdatedAt = DateTime.UtcNow;
                lead.LastActivityAt = DateTime.UtcNow;

                await AddActivity(lead.Id, LeadActivityType.System, "Lead restored from archive");
                await _context.SaveChangesAsync();
                await _context.Entry(lead).Collection(l => l.Activities).LoadAsync();
            }

            return Ok(MapToDetailDto(lead));
        }

        // Hard-delete is SuperAdmin-only: Admins/Moderators archive instead (see ArchiveLead).
        [HttpDelete("{id}")]
        [Authorize(Roles = "SuperAdmin")]
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

        /// <summary>
        /// Leads that belong to a blocked (deactivated) user — linked via ClientId or carrying
        /// the blocked user's email/phone — are hidden from every CRM read until the user is
        /// unblocked. Blocked users must stay visible only in the admin Users tab.
        /// </summary>
        private async Task<IQueryable<Lead>> ExcludeBlockedUserLeadsAsync(IQueryable<Lead> query)
        {
            var blocked = await _context.Users
                .Where(u => !u.IsActive && !u.IsDeleted)
                .Select(u => new { u.Id, u.Email, u.Phone })
                .ToListAsync();
            if (blocked.Count == 0) return query;

            var ids = blocked.Select(b => b.Id).ToList();
            var emails = blocked
                .Where(b => !string.IsNullOrWhiteSpace(b.Email) && !NoEmailHelper.IsPlaceholder(b.Email))
                .Select(b => b.Email.ToLowerInvariant())
                .ToList();
            // Lead.Phone is stored digits-normalized, so normalize the user side the same way.
            var phones = blocked
                .Select(b => PhoneHelper.NormalizeToDigits(b.Phone))
                .Where(p => p != null)
                .Select(p => p!)
                .ToList();

            query = query.Where(l => l.ClientId == null || !ids.Contains(l.ClientId.Value));
            if (emails.Count > 0)
                query = query.Where(l => l.Email == null || !emails.Contains(l.Email));
            if (phones.Count > 0)
                query = query.Where(l => l.Phone == null || !phones.Contains(l.Phone));
            return query;
        }

        /// <summary>
        /// Stamp each lead DTO with the problem flag of the registered customer it matches —
        /// by ClientId first, then email, then phone — mirroring the lead↔user matching used
        /// to hide blocked users. The flag lives on User.Flag (single source of truth); the
        /// lead itself is never flagged. Only non-None flags are applied.
        /// </summary>
        private async Task ApplyClientFlagsAsync(IReadOnlyCollection<LeadDto> dtos)
        {
            if (dtos == null || dtos.Count == 0) return;

            var flagged = await _context.Users
                .Where(u => u.Flag != CustomerFlagLevel.None && !u.IsDeleted)
                .Select(u => new { u.Id, u.Email, u.Phone, u.Flag, u.FlagReason })
                .ToListAsync();
            if (flagged.Count == 0) return;

            var byId = flagged.ToDictionary(f => f.Id, f => (f.Flag, f.FlagReason));
            var byEmail = new Dictionary<string, (CustomerFlagLevel Flag, string? Reason)>();
            var byPhone = new Dictionary<string, (CustomerFlagLevel Flag, string? Reason)>();
            foreach (var f in flagged)
            {
                if (!string.IsNullOrWhiteSpace(f.Email) && !NoEmailHelper.IsPlaceholder(f.Email))
                    byEmail[f.Email!.ToLowerInvariant()] = (f.Flag, f.FlagReason);
                var p = PhoneHelper.NormalizeToDigits(f.Phone);
                if (!string.IsNullOrWhiteSpace(p))
                    byPhone[p!] = (f.Flag, f.FlagReason);
            }

            foreach (var dto in dtos)
            {
                (CustomerFlagLevel Flag, string? Reason)? match = null;

                if (dto.ClientId.HasValue && byId.TryGetValue(dto.ClientId.Value, out var byIdMatch))
                    match = byIdMatch;
                else if (!string.IsNullOrWhiteSpace(dto.Email)
                         && byEmail.TryGetValue(dto.Email!.ToLowerInvariant(), out var em))
                    match = em;
                else
                {
                    var p = PhoneHelper.NormalizeToDigits(dto.Phone);
                    if (!string.IsNullOrWhiteSpace(p) && byPhone.TryGetValue(p!, out var ph))
                        match = ph;
                }

                if (match.HasValue && match.Value.Flag != CustomerFlagLevel.None)
                {
                    dto.Flag = match.Value.Flag.ToString();
                    dto.FlagReason = match.Value.Reason;
                }
            }
        }

        private IQueryable<Lead> BuildFilteredQuery(string? search, string? source, string? type,
            int? assignedToAdminId, string? stage, string? period = null, string? dateField = null, bool? archived = null)
        {
            var query = _context.Leads.AsQueryable();

            // archived: null = all, false = active board only, true = archive drawer only.
            if (archived.HasValue)
                query = query.Where(l => l.IsArchived == archived.Value);

            if (!string.IsNullOrWhiteSpace(stage) && LeadStage.IsValid(stage))
                query = query.Where(l => l.Stage == stage);

            // Date window: "today/week/month/year" relative to NY business time, applied to
            // either the created date or the last-activity date (caller's choice).
            var startUtc = ResolvePeriodStartUtc(period);
            if (startUtc.HasValue)
            {
                var byActivity = string.Equals(dateField, "activity", StringComparison.OrdinalIgnoreCase);
                query = byActivity
                    ? query.Where(l => l.LastActivityAt >= startUtc.Value)
                    : query.Where(l => l.CreatedAt >= startUtc.Value);
            }

            if (!string.IsNullOrWhiteSpace(source) && LeadSource.IsValid(source))
                query = query.Where(l => l.Source == source);

            if (!string.IsNullOrWhiteSpace(type) && LeadType.IsValid(type))
                query = query.Where(l => l.Type == type);

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

        /// <summary>
        /// Start of the requested period in UTC, computed from NY wall-clock so "today" /
        /// "this week" (Monday-start) / "this month" / "this year" line up with the business
        /// timezone. Returns null for null/empty/"all" or an unknown value (no date filter).
        /// </summary>
        private static DateTime? ResolvePeriodStartUtc(string? period)
        {
            if (string.IsNullOrWhiteSpace(period)) return null;

            var todayNy = NyTimeHelper.NowNy.Date;
            DateTime startNy;
            switch (period.Trim().ToLowerInvariant())
            {
                case "today": startNy = todayNy; break;
                case "week":
                    var diff = ((int)todayNy.DayOfWeek + 6) % 7; // Monday = start of week
                    startNy = todayNy.AddDays(-diff);
                    break;
                case "month": startNy = new DateTime(todayNy.Year, todayNy.Month, 1); break;
                case "year": startNy = new DateTime(todayNy.Year, 1, 1); break;
                default: return null; // "all" or unrecognized
            }

            return NyTimeHelper.ToUtc(startNy);
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
            Type = l.Type,
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
            LastActivityAt = l.LastActivityAt,
            IsArchived = l.IsArchived,
            ArchivedAt = l.ArchivedAt
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
                Type = baseDto.Type,
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
                IsArchived = baseDto.IsArchived,
                ArchivedAt = baseDto.ArchivedAt,
                Flag = baseDto.Flag,
                FlagReason = baseDto.FlagReason,
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
