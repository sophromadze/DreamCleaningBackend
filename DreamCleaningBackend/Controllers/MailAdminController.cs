using System.Security.Claims;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/Admin/mails")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class MailAdminController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly ILogger<MailAdminController> _logger;

        public MailAdminController(ApplicationDbContext context, IEmailService emailService, ILogger<MailAdminController> logger)
        {
            _context = context;
            _emailService = emailService;
            _logger = logger;
        }

        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ScheduledMailDto>>> GetMails([FromQuery] int? status)
        {
            var q = _context.ScheduledMails
                .Include(m => m.CreatedBy)
                .OrderByDescending(m => m.CreatedAt)
                .AsQueryable();
            if (status.HasValue)
                q = q.Where(m => m.Status == (MailStatus)status.Value);
            var list = await q.Select(m => MapToDto(m)).ToListAsync();
            return Ok(list);
        }

        [HttpGet("{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<ScheduledMailDto>> GetMail(int id)
        {
            var m = await _context.ScheduledMails.Include(x => x.CreatedBy).FirstOrDefaultAsync(x => x.Id == id);
            if (m == null) return NotFound();
            return Ok(MapToDto(m));
        }

        [HttpPost]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ScheduledMailDto>> CreateMail([FromBody] CreateScheduledMailDto dto)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var targetRoles = dto.TargetRoles ?? "[]";
            var roles = ParseRoleNames(targetRoles);
            var now = DateTime.UtcNow;
            var mail = new ScheduledMail
            {
                Subject = dto.Subject.Trim(),
                Content = dto.Content ?? "",
                TargetRoles = targetRoles,
                ScheduleType = (ScheduleType)dto.ScheduleType,
                ScheduledDate = dto.ScheduledDate,
                ScheduledTime = ParseTimeSpan(dto.ScheduledTime),
                DayOfWeek = dto.DayOfWeek,
                DayOfMonth = dto.DayOfMonth,
                WeekOfMonth = dto.WeekOfMonth,
                Frequency = dto.Frequency.HasValue ? (MailFrequency)dto.Frequency.Value : null,
                ScheduleTimezone = string.IsNullOrWhiteSpace(dto.ScheduleTimezone) ? ScheduleHelper.DefaultTimezone : dto.ScheduleTimezone.Trim(),
                Status = MailStatus.Draft,
                CreatedById = userId.Value,
                CreatedAt = now,
                UpdatedAt = now,
                RecipientCount = 0,
                TimesSent = 0,
                IsActive = true
            };
            // Compute recipient count (only users with CanReceiveCommunications and matching role)
            mail.RecipientCount = await CountRecipients(roles);
            if (dto.SendNow)
            {
                _context.ScheduledMails.Add(mail);
                await _context.SaveChangesAsync();
                await SendMailNow(mail);
                return Ok(MapToDto(mail));
            }
            _context.ScheduledMails.Add(mail);
            if (mail.ScheduleType == ScheduleType.Scheduled && mail.ScheduledTime.HasValue &&
                (mail.Frequency == MailFrequency.Once && mail.ScheduledDate.HasValue ||
                 mail.Frequency == MailFrequency.Weekly && mail.DayOfWeek.HasValue ||
                 mail.Frequency == MailFrequency.Monthly && mail.DayOfMonth.HasValue ||
                 mail.Frequency != MailFrequency.Once && mail.ScheduledDate.HasValue))
            {
                mail.Status = MailStatus.Scheduled;
                mail.NextScheduledAt = ScheduleHelper.ComputeNextScheduled(mail.ScheduledDate, mail.ScheduledTime.Value, mail.Frequency, mail.DayOfWeek, mail.DayOfMonth, mail.ScheduleTimezone);
            }
            await _context.SaveChangesAsync();
            return Ok(MapToDto(mail));
        }

        [HttpPut("{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ScheduledMailDto>> UpdateMail(int id, [FromBody] UpdateScheduledMailDto dto)
        {
            var mail = await _context.ScheduledMails.Include(m => m.CreatedBy).FirstOrDefaultAsync(m => m.Id == id);
            if (mail == null) return NotFound();
            if (mail.Status != MailStatus.Draft && mail.Status != MailStatus.Scheduled) return BadRequest("Cannot update mail that is already sent or cancelled.");
            if (dto.Subject != null) mail.Subject = dto.Subject.Trim();
            if (dto.Content != null) mail.Content = dto.Content;
            if (dto.TargetRoles != null) { mail.TargetRoles = dto.TargetRoles; mail.RecipientCount = await CountRecipients(ParseRoleNames(dto.TargetRoles)); }
            if (dto.ScheduleType.HasValue) mail.ScheduleType = (ScheduleType)dto.ScheduleType.Value;
            if (dto.ScheduledDate.HasValue) mail.ScheduledDate = dto.ScheduledDate;
            if (dto.ScheduledTime != null) mail.ScheduledTime = ParseTimeSpan(dto.ScheduledTime);
            if (dto.DayOfWeek.HasValue) mail.DayOfWeek = dto.DayOfWeek;
            if (dto.DayOfMonth.HasValue) mail.DayOfMonth = dto.DayOfMonth;
            if (dto.WeekOfMonth.HasValue) mail.WeekOfMonth = dto.WeekOfMonth;
            if (dto.Frequency.HasValue) mail.Frequency = (MailFrequency)dto.Frequency.Value;
            if (dto.ScheduleTimezone != null) mail.ScheduleTimezone = dto.ScheduleTimezone.Trim();
            if (dto.IsActive.HasValue) mail.IsActive = dto.IsActive.Value;
            mail.UpdatedAt = DateTime.UtcNow;
            if (mail.ScheduleType == ScheduleType.Scheduled && mail.ScheduledTime.HasValue &&
                (mail.Frequency == MailFrequency.Once && mail.ScheduledDate.HasValue ||
                 mail.Frequency == MailFrequency.Weekly && mail.DayOfWeek.HasValue ||
                 mail.Frequency == MailFrequency.Monthly && mail.DayOfMonth.HasValue ||
                 mail.Frequency != MailFrequency.Once && mail.ScheduledDate.HasValue))
                mail.NextScheduledAt = ScheduleHelper.ComputeNextScheduled(mail.ScheduledDate, mail.ScheduledTime.Value, mail.Frequency, mail.DayOfWeek, mail.DayOfMonth, mail.ScheduleTimezone);
            await _context.SaveChangesAsync();
            return Ok(MapToDto(mail));
        }

        [HttpDelete("{id}")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteMail(int id)
        {
            var mail = await _context.ScheduledMails.FindAsync(id);
            if (mail == null) return NotFound();
            if (mail.Status != MailStatus.Draft && mail.Status != MailStatus.Scheduled && mail.Status != MailStatus.Cancelled) return BadRequest("Cannot delete sent mail.");
            _context.ScheduledMails.Remove(mail);
            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpPost("{id}/send")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ScheduledMailDto>> SendNow(int id)
        {
            var mail = await _context.ScheduledMails.Include(m => m.CreatedBy).FirstOrDefaultAsync(m => m.Id == id);
            if (mail == null) return NotFound();
            if (mail.Status != MailStatus.Draft && mail.Status != MailStatus.Scheduled) return BadRequest("Mail already sent or cancelled.");
            await SendMailNow(mail);
            return Ok(MapToDto(mail));
        }

        [HttpPost("{id}/cancel")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ScheduledMailDto>> Cancel(int id)
        {
            var mail = await _context.ScheduledMails.Include(m => m.CreatedBy).FirstOrDefaultAsync(m => m.Id == id);
            if (mail == null) return NotFound();
            if (mail.Status != MailStatus.Scheduled) return BadRequest("Only scheduled mails can be cancelled.");
            mail.Status = MailStatus.Cancelled;
            mail.IsActive = false;
            mail.NextScheduledAt = null;
            mail.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(MapToDto(mail));
        }

        [HttpGet("stats")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<MailStatsDto>> GetStats()
        {
            var draft = await _context.ScheduledMails.CountAsync(m => m.Status == MailStatus.Draft);
            var scheduled = await _context.ScheduledMails.CountAsync(m => m.Status == MailStatus.Scheduled && m.IsActive);
            var sent = await _context.ScheduledMails.CountAsync(m => m.Status == MailStatus.Sent);
            var totalSent = await _context.ScheduledMails.Where(m => m.Status == MailStatus.Sent).SumAsync(m => m.TimesSent);
            return Ok(new MailStatsDto { DraftCount = draft, ScheduledCount = scheduled, SentCount = sent, TotalMailsSent = totalSent });
        }

        [HttpGet("user-counts")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<MailUserCountDto>>> GetUserCounts()
        {
            var roles = Enum.GetNames(typeof(UserRole));
            var list = new List<MailUserCountDto>();
            foreach (var r in roles)
            {
                if (!Enum.TryParse<UserRole>(r, out var role)) continue;
                var total = await _context.Users.CountAsync(u => u.Role == role && u.IsActive);
                var canReceive = await _context.Users.CountAsync(u => u.Role == role && u.IsActive && u.CanReceiveCommunications);
                list.Add(new MailUserCountDto { Role = r, Total = total, CanReceive = canReceive });
            }
            return Ok(list);
        }

        private int? GetCurrentUserId()
        {
            var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(sub, out var id) ? id : null;
        }

        private static TimeSpan? ParseTimeSpan(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (TimeSpan.TryParse(s, out var t)) return t;
            return null;
        }

        private static List<UserRole> ParseRoleNames(string json)
        {
            try
            {
                var names = JsonConvert.DeserializeObject<string[]>(json);
                if (names == null || names.Length == 0) return new List<UserRole>();
                var list = new List<UserRole>();
                foreach (var n in names)
                    if (Enum.TryParse<UserRole>(n?.Trim(), true, out var r))
                        list.Add(r);
                return list;
            }
            catch { return new List<UserRole>(); }
        }

        private async Task<int> CountRecipients(List<UserRole> roles)
        {
            if (roles == null || roles.Count == 0) return 0;
            return await _context.Users
                .Where(u => u.IsActive && u.CanReceiveCommunications && roles.Contains(u.Role))
                .CountAsync();
        }

        private async Task<List<User>> GetRecipients(List<UserRole> roles)
        {
            if (roles == null || roles.Count == 0) return new List<User>();
            return await _context.Users
                .Where(u => u.IsActive && u.CanReceiveCommunications && roles.Contains(u.Role))
                .ToListAsync();
        }

        private async Task SendMailNow(ScheduledMail mail)
        {
            var roles = ParseRoleNames(mail.TargetRoles);
            var recipients = await GetRecipients(roles);
            var html = EmailFormatHelper.FormatEmailContentWithParagraphs(mail.Content);
            var sentAt = DateTime.UtcNow;
            foreach (var u in recipients)
            {
                bool delivered = false;
                string err = "";
                try
                {
                    await _emailService.SendEmailAsync(u.Email, mail.Subject, html);
                    delivered = true;
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                    _logger.LogWarning(ex, "Mail send failed to {Email}", u.Email);
                }
                _context.SentMailLogs.Add(new SentMailLog
                {
                    ScheduledMailId = mail.Id,
                    RecipientEmail = u.Email,
                    RecipientName = $"{u.FirstName} {u.LastName}".Trim(),
                    RecipientRole = u.Role.ToString(),
                    SentAt = sentAt,
                    IsDelivered = delivered,
                    ErrorMessage = err
                });
            }
            mail.SentAt = sentAt;
            mail.LastSentAt = sentAt;
            mail.TimesSent += 1;
            mail.Status = MailStatus.Sent;
            mail.NextScheduledAt = null;
            mail.UpdatedAt = DateTime.UtcNow;
            if (mail.Frequency.HasValue && mail.Frequency != MailFrequency.Once && mail.ScheduledTime.HasValue)
            {
                mail.NextScheduledAt = ScheduleHelper.NextRecurringUtc(
                    mail.Frequency.Value, mail.DayOfWeek, mail.DayOfMonth,
                    mail.ScheduledTime.Value, mail.ScheduleTimezone, sentAt);
            }
            if (mail.NextScheduledAt.HasValue) { mail.Status = MailStatus.Scheduled; mail.IsActive = true; }
            await _context.SaveChangesAsync();
        }

        private static ScheduledMailDto MapToDto(ScheduledMail m)
        {
            return new ScheduledMailDto
            {
                Id = m.Id,
                Subject = m.Subject,
                Content = m.Content,
                TargetRoles = m.TargetRoles,
                ScheduleType = (int)m.ScheduleType,
                ScheduledDate = m.ScheduledDate,
                ScheduledTime = m.ScheduledTime,
                DayOfWeek = m.DayOfWeek,
                DayOfMonth = m.DayOfMonth,
                WeekOfMonth = m.WeekOfMonth,
                Frequency = m.Frequency.HasValue ? (int)m.Frequency.Value : null,
                ScheduleTimezone = m.ScheduleTimezone,
                Status = (int)m.Status,
                CreatedById = m.CreatedById,
                CreatedByEmail = m.CreatedBy?.Email,
                CreatedAt = MarkUtc(m.CreatedAt),
                UpdatedAt = MarkUtc(m.UpdatedAt),
                SentAt = m.SentAt.HasValue ? MarkUtc(m.SentAt.Value) : null,
                LastSentAt = m.LastSentAt.HasValue ? MarkUtc(m.LastSentAt.Value) : null,
                NextScheduledAt = m.NextScheduledAt.HasValue ? MarkUtc(m.NextScheduledAt.Value) : null,
                RecipientCount = m.RecipientCount,
                TimesSent = m.TimesSent,
                IsActive = m.IsActive
            };
        }

        /// <summary>Marks a DateTime as UTC so JSON serialization emits "Z"; values from DB are stored in UTC but have Kind=Unspecified.</summary>
        private static DateTime MarkUtc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);
    }
}
