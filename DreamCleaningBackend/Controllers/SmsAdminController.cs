using System.Security.Claims;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/Admin/sms")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class SmsAdminController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ISmsService _smsService;
        private readonly ILogger<SmsAdminController> _logger;

        public SmsAdminController(ApplicationDbContext context, ISmsService smsService, ILogger<SmsAdminController> logger)
        {
            _context = context;
            _smsService = smsService;
            _logger = logger;
        }

        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ScheduledSmsDto>>> GetSmsList([FromQuery] int? status)
        {
            var q = _context.ScheduledSms
                .Include(s => s.CreatedBy)
                .OrderByDescending(s => s.CreatedAt)
                .AsQueryable();
            if (status.HasValue)
                q = q.Where(s => s.Status == (MailStatus)status.Value);
            var list = await q.ToListAsync();
            return Ok(list.Select(MapToDto).ToList());
        }

        [HttpGet("{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<ScheduledSmsDto>> GetSms(int id)
        {
            var s = await _context.ScheduledSms.Include(x => x.CreatedBy).FirstOrDefaultAsync(x => x.Id == id);
            if (s == null) return NotFound();
            return Ok(MapToDto(s));
        }

        [HttpPost]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ScheduledSmsDto>> CreateSms([FromBody] CreateScheduledSmsDto dto)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            if (!_smsService.IsSmsEnabled())
                return BadRequest("SMS is not configured or enabled. Set RingCentral:EnableSmsSending and credentials in appsettings.");
            var targetRoles = dto.TargetRoles ?? "[]";
            var roles = ParseRoleNames(targetRoles);
            var now = DateTime.UtcNow;
            var sms = new ScheduledSms
            {
                Content = (dto.Content ?? "").Trim().Length > 1600 ? (dto.Content ?? "").Trim()[..1600] : (dto.Content ?? "").Trim(),
                TargetRoles = targetRoles,
                ScheduleType = (ScheduleType)dto.ScheduleType,
                ScheduledDate = dto.ScheduledDate,
                ScheduledTime = ParseTimeSpan(dto.ScheduledTime),
                DayOfWeek = dto.DayOfWeek,
                DayOfMonth = dto.DayOfMonth,
                WeekOfMonth = dto.WeekOfMonth,
                Frequency = dto.Frequency.HasValue ? (MailFrequency)dto.Frequency.Value : null,
                ScheduleTimezone = string.IsNullOrWhiteSpace(dto.ScheduleTimezone) ? "Eastern Standard Time" : dto.ScheduleTimezone.Trim(),
                Status = MailStatus.Draft,
                CreatedById = userId.Value,
                CreatedAt = now,
                UpdatedAt = now,
                RecipientCount = 0,
                TimesSent = 0,
                IsActive = true
            };
            sms.RecipientCount = await CountSmsRecipients(roles);
            if (dto.SendNow)
            {
                _context.ScheduledSms.Add(sms);
                await _context.SaveChangesAsync();
                await SendSmsNow(sms);
                return Ok(MapToDto(sms));
            }
            _context.ScheduledSms.Add(sms);
            if (sms.ScheduleType == ScheduleType.Scheduled && sms.ScheduledDate.HasValue && sms.ScheduledTime.HasValue)
            {
                sms.Status = MailStatus.Scheduled;
                sms.NextScheduledAt = ComputeNextScheduled(sms.ScheduledDate.Value, sms.ScheduledTime.Value, sms.Frequency, sms.DayOfWeek, sms.DayOfMonth, sms.WeekOfMonth, sms.ScheduleTimezone)
                    ?? ComputeFirstScheduled(sms.ScheduledDate.Value, sms.ScheduledTime.Value, sms.ScheduleTimezone);
            }
            await _context.SaveChangesAsync();
            return Ok(MapToDto(sms));
        }

        [HttpPut("{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ScheduledSmsDto>> UpdateSms(int id, [FromBody] UpdateScheduledSmsDto dto)
        {
            var sms = await _context.ScheduledSms.Include(s => s.CreatedBy).FirstOrDefaultAsync(s => s.Id == id);
            if (sms == null) return NotFound();
            if (sms.Status != MailStatus.Draft && sms.Status != MailStatus.Scheduled) return BadRequest("Cannot update SMS that is already sent or cancelled.");
            if (dto.Content != null) sms.Content = dto.Content.Length > 1600 ? dto.Content[..1600] : dto.Content;
            if (dto.TargetRoles != null) { sms.TargetRoles = dto.TargetRoles; sms.RecipientCount = await CountSmsRecipients(ParseRoleNames(dto.TargetRoles)); }
            if (dto.ScheduleType.HasValue) sms.ScheduleType = (ScheduleType)dto.ScheduleType.Value;
            if (dto.ScheduledDate.HasValue) sms.ScheduledDate = dto.ScheduledDate;
            if (dto.ScheduledTime != null) sms.ScheduledTime = ParseTimeSpan(dto.ScheduledTime);
            if (dto.DayOfWeek.HasValue) sms.DayOfWeek = dto.DayOfWeek;
            if (dto.DayOfMonth.HasValue) sms.DayOfMonth = dto.DayOfMonth;
            if (dto.WeekOfMonth.HasValue) sms.WeekOfMonth = dto.WeekOfMonth;
            if (dto.Frequency.HasValue) sms.Frequency = (MailFrequency)dto.Frequency.Value;
            if (dto.ScheduleTimezone != null) sms.ScheduleTimezone = dto.ScheduleTimezone.Trim();
            if (dto.IsActive.HasValue) sms.IsActive = dto.IsActive.Value;
            sms.UpdatedAt = DateTime.UtcNow;
            if (sms.ScheduleType == ScheduleType.Scheduled && sms.ScheduledDate.HasValue && sms.ScheduledTime.HasValue)
                sms.NextScheduledAt = ComputeNextScheduled(sms.ScheduledDate.Value, sms.ScheduledTime.Value, sms.Frequency, sms.DayOfWeek, sms.DayOfMonth, sms.WeekOfMonth, sms.ScheduleTimezone)
                    ?? ComputeFirstScheduled(sms.ScheduledDate.Value, sms.ScheduledTime.Value, sms.ScheduleTimezone);
            await _context.SaveChangesAsync();
            return Ok(MapToDto(sms));
        }

        [HttpDelete("{id}")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteSms(int id)
        {
            var sms = await _context.ScheduledSms.FindAsync(id);
            if (sms == null) return NotFound();
            if (sms.Status != MailStatus.Draft && sms.Status != MailStatus.Scheduled && sms.Status != MailStatus.Cancelled) return BadRequest("Cannot delete sent SMS.");
            _context.ScheduledSms.Remove(sms);
            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpPost("{id}/send")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ScheduledSmsDto>> SendNow(int id)
        {
            var sms = await _context.ScheduledSms.Include(s => s.CreatedBy).FirstOrDefaultAsync(s => s.Id == id);
            if (sms == null) return NotFound();
            if (sms.Status != MailStatus.Draft && sms.Status != MailStatus.Scheduled) return BadRequest("SMS already sent or cancelled.");
            if (!_smsService.IsSmsEnabled())
                return BadRequest("SMS is not configured or enabled.");
            await SendSmsNow(sms);
            return Ok(MapToDto(sms));
        }

        [HttpPost("{id}/cancel")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ScheduledSmsDto>> Cancel(int id)
        {
            var sms = await _context.ScheduledSms.Include(s => s.CreatedBy).FirstOrDefaultAsync(s => s.Id == id);
            if (sms == null) return NotFound();
            if (sms.Status != MailStatus.Scheduled) return BadRequest("Only scheduled SMS can be cancelled.");
            sms.Status = MailStatus.Cancelled;
            sms.IsActive = false;
            sms.NextScheduledAt = null;
            sms.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(MapToDto(sms));
        }

        [HttpGet("stats")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<SmsStatsDto>> GetStats()
        {
            var draft = await _context.ScheduledSms.CountAsync(s => s.Status == MailStatus.Draft);
            var scheduled = await _context.ScheduledSms.CountAsync(s => s.Status == MailStatus.Scheduled && s.IsActive);
            var sent = await _context.ScheduledSms.CountAsync(s => s.Status == MailStatus.Sent);
            var totalSent = await _context.ScheduledSms.Where(s => s.Status == MailStatus.Sent).SumAsync(s => s.TimesSent);
            return Ok(new SmsStatsDto { DraftCount = draft, ScheduledCount = scheduled, SentCount = sent, TotalSmsSent = totalSent });
        }

        [HttpGet("user-counts")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<SmsUserCountDto>>> GetUserCounts()
        {
            var roles = Enum.GetNames(typeof(UserRole));
            var data = await _context.Users.Where(u => u.IsActive).Select(u => new { u.Role, u.CanReceiveCommunications, u.Phone }).ToListAsync();
            var list = new List<SmsUserCountDto>();
            foreach (var r in roles)
            {
                if (!Enum.TryParse<UserRole>(r, out var role)) continue;
                var total = data.Count(x => x.Role == role);
                var canReceive = data.Count(x => x.Role == role && x.CanReceiveCommunications);
                var withValidPhone = data.Count(x => x.Role == role && x.CanReceiveCommunications && SmsService.NormalizePhoneToE164(x.Phone) != null);
                list.Add(new SmsUserCountDto { Role = r, Total = total, CanReceive = canReceive, WithValidPhone = withValidPhone });
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

        /// <summary>Count users who would receive SMS: IsActive, CanReceiveCommunications, role in TargetRoles, and valid 10+ digit phone.</summary>
        private async Task<int> CountSmsRecipients(List<UserRole> roles)
        {
            var recipients = await GetRecipientsForSms(roles);
            return recipients.Count;
        }

        /// <summary>Users who would receive SMS: same as mail (IsActive, CanReceiveCommunications, role) but filtered to those with NormalizePhoneToE164(Phone) != null.</summary>
        private async Task<List<User>> GetRecipientsForSms(List<UserRole> roles)
        {
            if (roles == null || roles.Count == 0) return new List<User>();
            var candidates = await _context.Users
                .Where(u => u.IsActive && u.CanReceiveCommunications && roles.Contains(u.Role))
                .ToListAsync();
            return candidates.Where(u => SmsService.NormalizePhoneToE164(u.Phone) != null).ToList();
        }

        private async Task SendSmsNow(ScheduledSms sms)
        {
            var roles = ParseRoleNames(sms.TargetRoles);
            var recipients = await GetRecipientsForSms(roles);
            var text = sms.Content?.Length > 1600 ? sms.Content[..1600] + "..." : (sms.Content ?? "");
            var sentAt = DateTime.UtcNow;
            foreach (var u in recipients)
            {
                var normalized = SmsService.NormalizePhoneToE164(u.Phone);
                if (string.IsNullOrEmpty(normalized)) continue;
                try
                {
                    await _smsService.SendSmsAsync(normalized, text);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SMS send failed to {Phone} (user {UserId})", u.Phone, u.Id);
                }
            }
            sms.SentAt = sms.SentAt ?? sentAt;
            sms.LastSentAt = sentAt;
            sms.TimesSent += 1;
            sms.UpdatedAt = DateTime.UtcNow;
            if (sms.Frequency == MailFrequency.Weekly)
                sms.NextScheduledAt = sentAt.AddDays(7);
            else if (sms.Frequency == MailFrequency.Monthly)
                sms.NextScheduledAt = sentAt.AddMonths(1);
            else
            {
                sms.NextScheduledAt = null;
                sms.Status = MailStatus.Sent;
                sms.IsActive = false;
            }
            if (sms.NextScheduledAt.HasValue) { sms.Status = MailStatus.Scheduled; sms.IsActive = true; }
            await _context.SaveChangesAsync();
            _logger.LogInformation("Scheduled SMS {Id} sent to {Count} recipients (with valid phone).", sms.Id, recipients.Count);
        }

        /// <summary>For one-time (Once): the single occurrence in UTC.</summary>
        private static DateTime? ComputeFirstScheduled(DateTime date, TimeSpan time, string tz)
        {
            try
            {
                var tzi = TimeZoneInfo.FindSystemTimeZoneById(tz);
                var local = DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Unspecified);
                return TimeZoneInfo.ConvertTimeToUtc(local, tzi);
            }
            catch { return null; }
        }

        private static DateTime? ComputeNextScheduled(DateTime date, TimeSpan time, MailFrequency? freq, int? dayOfWeek, int? dayOfMonth, int? weekOfMonth, string tz)
        {
            if (!freq.HasValue || freq == MailFrequency.Once) return null;
            try
            {
                var tzi = TimeZoneInfo.FindSystemTimeZoneById(tz);
                var local = DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Unspecified);
                var next = TimeZoneInfo.ConvertTimeToUtc(local, tzi);
                var now = DateTime.UtcNow;
                while (next <= now)
                {
                    if (freq == MailFrequency.Weekly)
                        next = next.AddDays(7);
                    else if (freq == MailFrequency.Monthly)
                        next = next.AddMonths(1);
                    else
                        next = next.AddDays(7);
                }
                return next;
            }
            catch { return null; }
        }

        private static ScheduledSmsDto MapToDto(ScheduledSms s)
        {
            return new ScheduledSmsDto
            {
                Id = s.Id,
                Content = s.Content,
                TargetRoles = s.TargetRoles,
                ScheduleType = (int)s.ScheduleType,
                ScheduledDate = s.ScheduledDate,
                ScheduledTime = s.ScheduledTime,
                DayOfWeek = s.DayOfWeek,
                DayOfMonth = s.DayOfMonth,
                WeekOfMonth = s.WeekOfMonth,
                Frequency = s.Frequency.HasValue ? (int)s.Frequency.Value : null,
                ScheduleTimezone = s.ScheduleTimezone,
                Status = (int)s.Status,
                CreatedById = s.CreatedById,
                CreatedByEmail = s.CreatedBy?.Email,
                CreatedAt = MarkUtc(s.CreatedAt),
                UpdatedAt = MarkUtc(s.UpdatedAt),
                SentAt = s.SentAt.HasValue ? MarkUtc(s.SentAt.Value) : null,
                LastSentAt = s.LastSentAt.HasValue ? MarkUtc(s.LastSentAt.Value) : null,
                NextScheduledAt = s.NextScheduledAt.HasValue ? MarkUtc(s.NextScheduledAt.Value) : null,
                RecipientCount = s.RecipientCount,
                TimesSent = s.TimesSent,
                IsActive = s.IsActive
            };
        }

        private static DateTime MarkUtc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);
    }
}
