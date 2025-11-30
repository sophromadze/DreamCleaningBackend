// DreamCleaningBackend/Controllers/MailAdminController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using System.Text.Json;
using TimeZoneConverter;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/admin/mails")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public class MailAdminController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly ILogger<MailAdminController> _logger;

        public MailAdminController(
            ApplicationDbContext context,
            IEmailService emailService,
            ILogger<MailAdminController> logger)
        {
            _context = context;
            _emailService = emailService;
            _logger = logger;
        }

        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ScheduledMailDto>>> GetScheduledMails()
{
    var mails = await _context.ScheduledMails
        .Include(m => m.CreatedBy)
        .Include(m => m.SentMailLogs)
        .OrderByDescending(m => m.CreatedAt)
        .ToListAsync();

    var result = mails.Select(m => new ScheduledMailDto
    {
        Id = m.Id,
        Subject = m.Subject,
        Content = m.Content,
        TargetRoles = JsonSerializer.Deserialize<List<string>>(m.TargetRoles) ?? new List<string>(),
        ScheduleType = m.ScheduleType.ToString(),
        ScheduledDate = m.ScheduledDate,
        ScheduledTime = m.ScheduledTime?.ToString() ?? null,
        DayOfWeek = m.DayOfWeek,
        DayOfMonth = m.DayOfMonth,
        WeekOfMonth = m.WeekOfMonth,
        Frequency = m.Frequency?.ToString() ?? null,
        Status = m.Status.ToString(),
        ScheduleTimezone = m.ScheduleTimezone, // Include timezone
        CreatedBy = $"{m.CreatedBy.FirstName} {m.CreatedBy.LastName}",
        CreatedAt = m.CreatedAt,
        SentAt = m.SentAt,
        RecipientCount = m.RecipientCount,
        TimesSent = m.TimesSent
    }).ToList();

    return Ok(result);
}

        [HttpGet("{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<ScheduledMailDto>> GetScheduledMail(int id)
        {
            var mail = await _context.ScheduledMails
                .Include(m => m.CreatedBy)
                .Include(m => m.SentMailLogs)
                .FirstOrDefaultAsync(m => m.Id == id);

            if (mail == null)
                return NotFound();

            var dto = new ScheduledMailDto
            {
                Id = mail.Id,
                Subject = mail.Subject,
                Content = mail.Content,
                TargetRoles = JsonSerializer.Deserialize<List<string>>(mail.TargetRoles) ?? new List<string>(),
                ScheduleType = mail.ScheduleType.ToString(),
                ScheduledDate = mail.ScheduledDate,
                ScheduledTime = mail.ScheduledTime?.ToString() ?? null,
                DayOfWeek = mail.DayOfWeek,
                DayOfMonth = mail.DayOfMonth,
                WeekOfMonth = mail.WeekOfMonth,
                Frequency = mail.Frequency?.ToString() ?? null,
                Status = mail.Status.ToString(),
                CreatedBy = $"{mail.CreatedBy.FirstName} {mail.CreatedBy.LastName}",
                CreatedAt = mail.CreatedAt,
                SentAt = mail.SentAt,
                RecipientCount = mail.RecipientCount,
                TimesSent = mail.TimesSent,
                SentLogs = mail.SentMailLogs.Select(log => new SentMailLogDto
                {
                    RecipientEmail = log.RecipientEmail,
                    RecipientName = log.RecipientName,
                    RecipientRole = log.RecipientRole,
                    SentAt = log.SentAt,
                    IsDelivered = log.IsDelivered,
                    ErrorMessage = log.ErrorMessage
                }).ToList()
            };

            return Ok(dto);
        }

        [HttpPost]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ScheduledMailDto>> CreateScheduledMail(CreateScheduledMailDto dto)
{
    if (!ModelState.IsValid)
    {
        _logger.LogWarning($"Model validation failed: {string.Join(", ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage))}");
        return BadRequest(ModelState);
    }
    
    var userId = int.Parse(User.FindFirst("UserId")?.Value ?? "0");

    var mail = new ScheduledMail
    {
        Subject = dto.Subject,
        Content = dto.Content,
        TargetRoles = JsonSerializer.Serialize(dto.TargetRoles),
        ScheduleType = Enum.Parse<ScheduleType>(dto.ScheduleType, true),
        ScheduledDate = dto.ScheduledDate,
        ScheduledTime = !string.IsNullOrEmpty(dto.ScheduledTime) && TimeSpan.TryParse(dto.ScheduledTime, out var time) ? time : null,
        DayOfWeek = dto.DayOfWeek,
        DayOfMonth = dto.DayOfMonth,
        WeekOfMonth = dto.WeekOfMonth,
        Frequency = !string.IsNullOrEmpty(dto.Frequency) && Enum.TryParse<Frequency>(dto.Frequency, true, out var freq) ? freq : null,
        Status = Enum.Parse<MailStatus>(dto.Status, true),
        ScheduleTimezone = dto.ScheduleTimezone ?? "America/New_York", // Default to NYC timezone
        CreatedById = userId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        IsActive = true,
        RecipientCount = 0,
        TimesSent = 0
    };

    // Calculate next scheduled time if applicable
    if (mail.ScheduleType == ScheduleType.Scheduled)
    {
        mail.NextScheduledAt = CalculateNextScheduledTime(mail);
    }

    _context.ScheduledMails.Add(mail);
    await _context.SaveChangesAsync();

    // If immediate send, process it now
    if (mail.ScheduleType == ScheduleType.Immediate && mail.Status == MailStatus.Sent)
    {
        await SendMailToRecipients(mail);
    }

    return CreatedAtAction(nameof(GetScheduledMail), new { id = mail.Id }, new { id = mail.Id });
}

        [HttpPut("{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdateScheduledMail(int id, UpdateScheduledMailDto dto)
        {
            var mail = await _context.ScheduledMails.FindAsync(id);
            if (mail == null)
                return NotFound();

            if (mail.Status == MailStatus.Sent)
                return BadRequest(new { message = "Cannot edit sent emails" });

            mail.Subject = dto.Subject;
            mail.Content = dto.Content;
            mail.TargetRoles = JsonSerializer.Serialize(dto.TargetRoles);
            mail.ScheduleType = Enum.Parse<ScheduleType>(dto.ScheduleType, true);
            mail.ScheduledDate = dto.ScheduledDate;
            mail.ScheduledTime = !string.IsNullOrEmpty(dto.ScheduledTime) && TimeSpan.TryParse(dto.ScheduledTime, out var time) ? time : null;
            mail.DayOfWeek = dto.DayOfWeek;
            mail.DayOfMonth = dto.DayOfMonth;
            mail.WeekOfMonth = dto.WeekOfMonth;
            mail.Frequency = !string.IsNullOrEmpty(dto.Frequency) && Enum.TryParse<Frequency>(dto.Frequency, out var freq) ? freq : null;
            mail.Status = Enum.Parse<MailStatus>(dto.Status, true);
            mail.UpdatedAt = DateTime.UtcNow;

            // Recalculate next scheduled time
            if (mail.ScheduleType == ScheduleType.Scheduled)
            {
                mail.NextScheduledAt = CalculateNextScheduledTime(mail);
            }

            await _context.SaveChangesAsync();
            return Ok(new { message = "Mail updated successfully" });
        }

        [HttpDelete("{id}")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteScheduledMail(int id)
        {
            var mail = await _context.ScheduledMails
                .Include(m => m.SentMailLogs)
                .FirstOrDefaultAsync(m => m.Id == id);

            if (mail == null)
                return NotFound();

            _context.ScheduledMails.Remove(mail);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Mail deleted successfully" });
        }

        [HttpPost("{id}/send")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult> SendMail(int id)
        {
            var mail = await _context.ScheduledMails.FindAsync(id);
            if (mail == null)
                return NotFound();

            var result = await SendMailToRecipients(mail);
            
            if (result.Success)
            {
                mail.Status = MailStatus.Sent;
                mail.SentAt = DateTime.UtcNow;
                mail.LastSentAt = DateTime.UtcNow;
                mail.TimesSent++;
                await _context.SaveChangesAsync();

                return Ok(new { 
                    message = $"Email sent successfully to {result.RecipientCount} recipients",
                    recipientCount = result.RecipientCount 
                });
            }

            return BadRequest(new { message = result.ErrorMessage });
        }

        [HttpPost("{id}/cancel")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> CancelScheduledMail(int id)
        {
            var mail = await _context.ScheduledMails.FindAsync(id);
            if (mail == null)
                return NotFound();

            if (mail.Status != MailStatus.Scheduled)
                return BadRequest(new { message = "Can only cancel scheduled emails" });

            mail.Status = MailStatus.Cancelled;
            mail.UpdatedAt = DateTime.UtcNow;
            mail.IsActive = false;
            
            await _context.SaveChangesAsync();
            return Ok(new { message = "Scheduled mail cancelled successfully" });
        }

        [HttpGet("stats")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<MailStatsDto>> GetMailStats()
        {
            var stats = new MailStatsDto
            {
                TotalSent = await _context.ScheduledMails.CountAsync(m => m.Status == MailStatus.Sent),
                ScheduledCount = await _context.ScheduledMails.CountAsync(m => m.Status == MailStatus.Scheduled),
                DraftCount = await _context.ScheduledMails.CountAsync(m => m.Status == MailStatus.Draft),
                RecipientsByRole = await GetRecipientsByRole(),
                SentByMonth = await GetSentByMonth()
            };

            return Ok(stats);
        }

        [HttpGet("user-counts")]
        // [RequirePermission(Permission.View)] // Temporarily removed for debugging
        public async Task<ActionResult<Dictionary<string, int>>> GetUserCountsByRole()
        {
            try
            {
                var counts = new Dictionary<string, int>();
                
                foreach (UserRole role in Enum.GetValues(typeof(UserRole)))
                {
                    var count = await _context.Users.CountAsync(u => u.Role == role && u.IsActive);
                    counts[role.ToString()] = count;
                    _logger.LogInformation($"Role {role}: {count} users");
                }

                // Add total count
                var totalCount = await _context.Users.CountAsync(u => u.IsActive);
                counts["all"] = totalCount;
                _logger.LogInformation($"Total users: {totalCount}");

                return Ok(counts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user counts by role");
                return StatusCode(500, new { message = "Error retrieving user counts" });
            }
        }

        [HttpGet("test-user-counts")]
        public async Task<ActionResult> TestUserCounts()
        {
            try
            {
                var totalUsers = await _context.Users.CountAsync();
                var activeUsers = await _context.Users.CountAsync(u => u.IsActive);
                
                _logger.LogInformation($"Total users in database: {totalUsers}");
                _logger.LogInformation($"Active users in database: {activeUsers}");
                
                return Ok(new { 
                    totalUsers = totalUsers, 
                    activeUsers = activeUsers,
                    message = "Check logs for detailed role counts"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in test user counts");
                return StatusCode(500, new { message = ex.Message });
            }
        }


        private async Task<SendMailResult> SendMailToRecipients(ScheduledMail mail)
        {
            try
            {
                var targetRoles = JsonSerializer.Deserialize<List<string>>(mail.TargetRoles) ?? new List<string>();
                var recipients = new List<User>();

                if (targetRoles.Contains("all"))
                {
                    recipients = await _context.Users
                        .Where(u => u.IsActive)
                        .ToListAsync();
                }
                else
                {
                    var query = _context.Users.Where(u => u.IsActive);
                    
                    if (targetRoles.Contains("Customer"))
                        recipients.AddRange(await query.Where(u => u.Role == UserRole.Customer).ToListAsync());
                    if (targetRoles.Contains("Cleaner"))
                        recipients.AddRange(await query.Where(u => u.Role == UserRole.Cleaner).ToListAsync());
                    if (targetRoles.Contains("Admin"))
                        recipients.AddRange(await query.Where(u => u.Role == UserRole.Admin).ToListAsync());
                    if (targetRoles.Contains("SuperAdmin"))
                        recipients.AddRange(await query.Where(u => u.Role == UserRole.SuperAdmin).ToListAsync());
                    if (targetRoles.Contains("Moderator"))
                        recipients.AddRange(await query.Where(u => u.Role == UserRole.Moderator).ToListAsync());
                    
                    recipients = recipients.Distinct().ToList();
                }

                var sentCount = 0;
                var logs = new List<SentMailLog>();

                foreach (var recipient in recipients)
                {
                    try
                    {
                        // Format the content to preserve line breaks and spacing
                        var formattedContent = FormatEmailContent(mail.Content);
                        
                        await _emailService.SendEmailAsync(
                            recipient.Email,
                            mail.Subject,
                            formattedContent
                        );

                        logs.Add(new SentMailLog
                        {
                            ScheduledMailId = mail.Id,
                            RecipientEmail = recipient.Email,
                            RecipientName = $"{recipient.FirstName} {recipient.LastName}",
                            RecipientRole = recipient.Role.ToString(),
                            SentAt = DateTime.UtcNow,
                            IsDelivered = true
                        });

                        sentCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to send email to {recipient.Email}");
                        
                        logs.Add(new SentMailLog
                        {
                            ScheduledMailId = mail.Id,
                            RecipientEmail = recipient.Email,
                            RecipientName = $"{recipient.FirstName} {recipient.LastName}",
                            RecipientRole = recipient.Role.ToString(),
                            SentAt = DateTime.UtcNow,
                            IsDelivered = false,
                            ErrorMessage = ex.Message
                        });
                    }
                }

                // Save logs
                _context.SentMailLogs.AddRange(logs);
                mail.RecipientCount = sentCount;
                await _context.SaveChangesAsync();

                return new SendMailResult
                {
                    Success = true,
                    RecipientCount = sentCount
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send scheduled mail");
                return new SendMailResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private string FormatEmailContent(string plainTextContent)
        {
            if (string.IsNullOrEmpty(plainTextContent))
                return string.Empty;

            // Escape HTML characters to prevent XSS
            var escaped = System.Net.WebUtility.HtmlEncode(plainTextContent);
            
            // Convert line breaks to HTML line breaks
            // Handle both \r\n (Windows) and \n (Unix/Mac) line breaks
            var formatted = escaped
                .Replace("\r\n", "<br/>")
                .Replace("\n", "<br/>")
                .Replace("\r", "<br/>");
            
            // Wrap in a proper HTML structure with styling to preserve formatting
            return $@"
<!DOCTYPE html>
<html>
<head>
    <style>
        body {{
            font-family: Arial, sans-serif;
            color: #333;
            line-height: 1.6;
            max-width: 600px;
            margin: 0 auto;
            padding: 20px;
        }}
        .content {{
            word-wrap: break-word;
        }}
    </style>
</head>
<body>
    <div class=""content"">{formatted}</div>
</body>
</html>";
        }

        private DateTime? CalculateNextScheduledTime(ScheduledMail mail)
{
    if (string.IsNullOrEmpty(mail.ScheduleTimezone))
    {
        mail.ScheduleTimezone = "America/New_York"; // Default timezone
    }

    try
    {
        var timezone = TZConvert.GetTimeZoneInfo(mail.ScheduleTimezone);
        
        if (mail.Frequency == Frequency.Once && mail.ScheduledDate.HasValue)
        {
            // The scheduled date is already in UTC (converted from frontend)
            return mail.ScheduledDate.Value;
        }

        if (mail.Frequency == Frequency.Weekly && mail.DayOfWeek.HasValue)
        {
            // Get current time in the specified timezone
            var nowInTimezone = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timezone);
            
            // Calculate next occurrence
            var daysUntilTarget = ((int)mail.DayOfWeek.Value - (int)nowInTimezone.DayOfWeek + 7) % 7;
            if (daysUntilTarget == 0) 
            {
                // If it's the same day, check if the time has passed
                if (mail.ScheduledTime.HasValue)
                {
                    var todayWithTime = nowInTimezone.Date.Add(mail.ScheduledTime.Value);
                    if (todayWithTime <= nowInTimezone)
                    {
                        daysUntilTarget = 7; // Schedule for next week
                    }
                }
                else
                {
                    daysUntilTarget = 7; // Next week if no time specified
                }
            }
            
            var nextDateInTimezone = nowInTimezone.AddDays(daysUntilTarget).Date;
            if (mail.ScheduledTime.HasValue)
            {
                nextDateInTimezone = nextDateInTimezone.Add(mail.ScheduledTime.Value);
            }
            
            // Convert back to UTC
            return TimeZoneInfo.ConvertTimeToUtc(nextDateInTimezone, timezone);
        }

        if (mail.Frequency == Frequency.Monthly && mail.DayOfMonth.HasValue)
        {
            // Get current time in the specified timezone
            var nowInTimezone = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timezone);
            
            // Calculate next occurrence
            var year = nowInTimezone.Year;
            var month = nowInTimezone.Month;
            var day = Math.Min(mail.DayOfMonth.Value, DateTime.DaysInMonth(year, month));
            
            var nextDateInTimezone = new DateTime(year, month, day);
            
            if (mail.ScheduledTime.HasValue)
            {
                nextDateInTimezone = nextDateInTimezone.Add(mail.ScheduledTime.Value);
            }
            
            // If the date has passed this month, move to next month
            if (nextDateInTimezone <= nowInTimezone)
            {
                if (month == 12)
                {
                    year++;
                    month = 1;
                }
                else
                {
                    month++;
                }
                
                day = Math.Min(mail.DayOfMonth.Value, DateTime.DaysInMonth(year, month));
                nextDateInTimezone = new DateTime(year, month, day);
                
                if (mail.ScheduledTime.HasValue)
                {
                    nextDateInTimezone = nextDateInTimezone.Add(mail.ScheduledTime.Value);
                }
            }
            
            // Convert back to UTC
            return TimeZoneInfo.ConvertTimeToUtc(nextDateInTimezone, timezone);
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, $"Error calculating next scheduled time for mail {mail.Id}");
    }

    return null;
}

        private async Task<List<RecipientByRoleDto>> GetRecipientsByRole()
        {
            var result = new List<RecipientByRoleDto>();
            
            foreach (UserRole role in Enum.GetValues(typeof(UserRole)))
            {
                var count = await _context.Users.CountAsync(u => u.Role == role && u.IsActive);
                result.Add(new RecipientByRoleDto
                {
                    Role = role.ToString(),
                    Count = count
                });
            }

            return result;
        }

        private async Task<List<SentByMonthDto>> GetSentByMonth()
        {
            var sixMonthsAgo = DateTime.UtcNow.AddMonths(-6);
            
            var sentMails = await _context.ScheduledMails
                .Where(m => m.Status == MailStatus.Sent && m.SentAt >= sixMonthsAgo)
                .GroupBy(m => new { m.SentAt.Value.Year, m.SentAt.Value.Month })
                .Select(g => new SentByMonthDto
                {
                    Month = $"{g.Key.Month}/{g.Key.Year}",
                    Count = g.Count()
                })
                .OrderBy(x => x.Month)
                .ToListAsync();

            return sentMails;
        }

        private class SendMailResult
        {
            public bool Success { get; set; }
            public int RecipientCount { get; set; }
            public string ErrorMessage { get; set; } = string.Empty;
        }

        public static class TimezoneHelper
{
    public static DateTime ConvertToTimezone(DateTime utcDateTime, string timezoneId)
    {
        try
        {
            var timezone = TZConvert.GetTimeZoneInfo(timezoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, timezone);
        }
        catch
        {
            // Fallback to UTC if timezone is invalid
            return utcDateTime;
        }
    }
    
    public static DateTime ConvertFromTimezone(DateTime localDateTime, string timezoneId)
    {
        try
        {
            var timezone = TZConvert.GetTimeZoneInfo(timezoneId);
            return TimeZoneInfo.ConvertTimeToUtc(localDateTime, timezone);
        }
        catch
        {
            // Fallback to treating as UTC if timezone is invalid
            return localDateTime;
        }
    }
}
    }
}