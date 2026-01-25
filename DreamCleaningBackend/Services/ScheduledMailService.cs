using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace DreamCleaningBackend.Services
{
    public class ScheduledMailService : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<ScheduledMailService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

        public ScheduledMailService(IServiceProvider sp, ILogger<ScheduledMailService> logger)
        {
            _sp = sp;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ProcessDueMails();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ScheduledMailService run failed.");
                }
                await Task.Delay(Interval, ct);
            }
        }

        private async Task ProcessDueMails()
        {
            using var scope = _sp.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
            var now = DateTime.UtcNow;
            var due = await ctx.ScheduledMails
                .Where(m => m.Status == MailStatus.Scheduled && m.IsActive && m.NextScheduledAt != null && m.NextScheduledAt <= now)
                .ToListAsync();
            foreach (var mail in due)
            {
                var roles = ParseRoleNames(mail.TargetRoles);
                var recipients = await GetRecipients(ctx, roles);
                var html = EmailFormatHelper.FormatEmailContentWithParagraphs(mail.Content);
                var sentAt = DateTime.UtcNow;
                foreach (var u in recipients)
                {
                    bool ok = false;
                    string err = "";
                    try
                    {
                        await email.SendEmailAsync(u.Email, mail.Subject, html);
                        ok = true;
                    }
                    catch (Exception ex)
                    {
                        err = ex.Message;
                        _logger.LogWarning(ex, "Scheduled mail send failed to {Email}", u.Email);
                    }
                    ctx.SentMailLogs.Add(new SentMailLog
                    {
                        ScheduledMailId = mail.Id,
                        RecipientEmail = u.Email,
                        RecipientName = $"{u.FirstName} {u.LastName}".Trim(),
                        RecipientRole = u.Role.ToString(),
                        SentAt = sentAt,
                        IsDelivered = ok,
                        ErrorMessage = err
                    });
                }
                mail.SentAt = mail.SentAt ?? sentAt;
                mail.LastSentAt = sentAt;
                mail.TimesSent += 1;
                mail.UpdatedAt = DateTime.UtcNow;
                if (mail.Frequency == MailFrequency.Weekly)
                    mail.NextScheduledAt = sentAt.AddDays(7);
                else if (mail.Frequency == MailFrequency.Monthly)
                    mail.NextScheduledAt = sentAt.AddMonths(1);
                else
                {
                    mail.NextScheduledAt = null;
                    mail.Status = MailStatus.Sent;
                    mail.IsActive = false;
                }
                await ctx.SaveChangesAsync();
                _logger.LogInformation("Scheduled mail {Id} sent to {Count} recipients.", mail.Id, recipients.Count);
            }
        }

        private static List<UserRole> ParseRoleNames(string? json)
        {
            try
            {
                var names = JsonConvert.DeserializeObject<string[]>(json ?? "[]");
                if (names == null || names.Length == 0) return new List<UserRole>();
                var list = new List<UserRole>();
                foreach (var n in names)
                    if (Enum.TryParse<UserRole>(n?.Trim(), true, out var r))
                        list.Add(r);
                return list;
            }
            catch { return new List<UserRole>(); }
        }

        private static async Task<List<User>> GetRecipients(ApplicationDbContext ctx, List<UserRole> roles)
        {
            if (roles == null || roles.Count == 0) return new List<User>();
            return await ctx.Users
                .Where(u => u.IsActive && u.CanReceiveCommunications && roles.Contains(u.Role))
                .ToListAsync();
        }
    }
}
