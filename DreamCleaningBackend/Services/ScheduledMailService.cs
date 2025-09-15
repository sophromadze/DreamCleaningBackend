// DreamCleaningBackend/Services/ScheduledMailService.cs
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using TimeZoneConverter;

namespace DreamCleaningBackend.Services
{
    public class ScheduledMailService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ScheduledMailService> _logger;

        public ScheduledMailService(IServiceProvider serviceProvider, ILogger<ScheduledMailService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

                    // Get scheduled mails that are due to be sent
                    var now = DateTime.UtcNow;
                    
                    var dueMails = await context.ScheduledMails
                        .Where(m => m.Status == MailStatus.Scheduled && 
                                   m.IsActive && 
                                   m.NextScheduledAt.HasValue && 
                                   m.NextScheduledAt <= now)
                        .ToListAsync(stoppingToken);

                    foreach (var mail in dueMails)
                    {
                        try
                        {
                            _logger.LogInformation($"Processing scheduled mail {mail.Id} - Subject: {mail.Subject}");
                            
                            // Send the email
                            await SendMailToRecipients(mail, context, emailService);
                            
                            // Update mail status
                            if (mail.Frequency == Frequency.Once)
                            {
                                mail.Status = MailStatus.Sent;
                                mail.SentAt = DateTime.UtcNow;
                                mail.IsActive = false;
                            }
                            else
                            {
                                // For recurring emails, calculate next scheduled time
                                mail.LastSentAt = DateTime.UtcNow;
                                mail.TimesSent++;
                                mail.NextScheduledAt = CalculateNextScheduledTime(mail);
                                
                                _logger.LogInformation($"Recurring mail {mail.Id} rescheduled for {mail.NextScheduledAt} UTC");
                            }
                            
                            mail.UpdatedAt = DateTime.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to send scheduled mail {mail.Id}");
                            mail.Status = MailStatus.Failed;
                            mail.UpdatedAt = DateTime.UtcNow;
                        }
                    }

                    if (dueMails.Any())
                    {
                        await context.SaveChangesAsync(stoppingToken);
                        _logger.LogInformation($"Processed {dueMails.Count} scheduled mails");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing scheduled mails");
                }

                // Wait 1 minute before checking again
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        private async Task SendMailToRecipients(ScheduledMail mail, ApplicationDbContext context, IEmailService emailService)
        {
            var targetRoles = System.Text.Json.JsonSerializer.Deserialize<List<string>>(mail.TargetRoles) ?? new List<string>();
            var users = new List<User>();

            if (targetRoles.Contains("all"))
            {
                users = await context.Users.Where(u => u.IsActive).ToListAsync();
            }
            else
            {
                var roles = targetRoles.Select(r => Enum.Parse<UserRole>(r)).ToList();
                users = await context.Users.Where(u => u.IsActive && roles.Contains(u.Role)).ToListAsync();
            }

            var sentCount = 0;
            foreach (var user in users)
            {
                try
                {
                    await emailService.SendEmailAsync(user.Email, mail.Subject, mail.Content);
                    
                    // Log the sent email
                    var log = new SentMailLog
                    {
                        ScheduledMailId = mail.Id,
                        RecipientEmail = user.Email,
                        RecipientName = $"{user.FirstName} {user.LastName}",
                        RecipientRole = user.Role.ToString(),
                        SentAt = DateTime.UtcNow,
                        IsDelivered = true,
                        ErrorMessage = string.Empty
                    };
                    
                    context.SentMailLogs.Add(log);
                    sentCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send email to {user.Email}");
                    
                    // Log the failed email
                    var log = new SentMailLog
                    {
                        ScheduledMailId = mail.Id,
                        RecipientEmail = user.Email,
                        RecipientName = $"{user.FirstName} {user.LastName}",
                        RecipientRole = user.Role.ToString(),
                        SentAt = DateTime.UtcNow,
                        IsDelivered = false,
                        ErrorMessage = ex.Message
                    };
                    
                    context.SentMailLogs.Add(log);
                }
            }
            
            mail.RecipientCount = sentCount;
            _logger.LogInformation($"Mail {mail.Id} sent to {sentCount} recipients");
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
                
                if (mail.Frequency == Frequency.Weekly && mail.DayOfWeek.HasValue)
                {
                    // Get current time in the specified timezone
                    var nowInTimezone = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timezone);
                    
                    // Calculate next occurrence (always next week since we just sent it)
                    var daysUntilTarget = ((int)mail.DayOfWeek.Value - (int)nowInTimezone.DayOfWeek + 7) % 7;
                    if (daysUntilTarget == 0) daysUntilTarget = 7; // Next week
                    
                    var nextDateInTimezone = nowInTimezone.AddDays(daysUntilTarget).Date;
                    if (mail.ScheduledTime.HasValue)
                    {
                        nextDateInTimezone = nextDateInTimezone.Add(mail.ScheduledTime.Value);
                    }
                    
                    // Convert back to UTC
                    var nextUtc = TimeZoneInfo.ConvertTimeToUtc(nextDateInTimezone, timezone);
                    _logger.LogInformation($"Next weekly occurrence for mail {mail.Id}: {nextUtc} UTC (from {nextDateInTimezone} {mail.ScheduleTimezone})");
                    return nextUtc;
                }

                if (mail.Frequency == Frequency.Monthly && mail.DayOfMonth.HasValue)
                {
                    // Get current time in the specified timezone
                    var nowInTimezone = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timezone);
                    
                    // Calculate next month's occurrence
                    var year = nowInTimezone.Year;
                    var month = nowInTimezone.Month + 1; // Next month since we just sent it
                    
                    if (month > 12)
                    {
                        month = 1;
                        year++;
                    }
                    
                    var day = Math.Min(mail.DayOfMonth.Value, DateTime.DaysInMonth(year, month));
                    var nextDateInTimezone = new DateTime(year, month, day);
                    
                    if (mail.ScheduledTime.HasValue)
                    {
                        nextDateInTimezone = nextDateInTimezone.Add(mail.ScheduledTime.Value);
                    }
                    
                    // Convert back to UTC
                    var nextUtc = TimeZoneInfo.ConvertTimeToUtc(nextDateInTimezone, timezone);
                    _logger.LogInformation($"Next monthly occurrence for mail {mail.Id}: {nextUtc} UTC (from {nextDateInTimezone} {mail.ScheduleTimezone})");
                    return nextUtc;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error calculating next scheduled time for mail {mail.Id}");
            }

            return null;
        }
    }
}