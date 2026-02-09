using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Newtonsoft.Json;

namespace DreamCleaningBackend.Services
{
    public class ScheduledSmsService : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<ScheduledSmsService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

        public ScheduledSmsService(IServiceProvider sp, ILogger<ScheduledSmsService> logger)
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
                    await ProcessDueSms();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ScheduledSmsService run failed.");
                }
                await Task.Delay(Interval, ct);
            }
        }

        private static bool _tableMissingLogged;

        private async Task ProcessDueSms()
        {
            using var scope = _sp.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var smsService = scope.ServiceProvider.GetRequiredService<ISmsService>();
            if (!smsService.IsSmsEnabled()) return;
            List<ScheduledSms> due;
            try
            {
                var now = DateTime.UtcNow;
                due = await ctx.ScheduledSms
                    .Where(s => s.Status == MailStatus.Scheduled && s.IsActive && s.NextScheduledAt != null && s.NextScheduledAt <= now)
                    .ToListAsync();
            }
            catch (MySqlException ex) when (ex.Message.Contains("doesn't exist"))
            {
                if (!_tableMissingLogged)
                {
                    _tableMissingLogged = true;
                    _logger.LogWarning("ScheduledSms table is missing. Restart the app so it can be created at startup, or run: DELETE FROM __EFMigrationsHistory WHERE MigrationId = '20260125190000_AddScheduledSms'; then dotnet ef database update");
                }
                return;
            }
            foreach (var sms in due)
            {
                var roles = ParseRoleNames(sms.TargetRoles);
                var recipients = await GetRecipientsForSms(ctx, roles);
                var text = sms.Content?.Length > 1600 ? sms.Content[..1600] + "..." : (sms.Content ?? "");
                var sentAt = DateTime.UtcNow;
                foreach (var u in recipients)
                {
                    var normalized = SmsService.NormalizePhoneToE164(u.Phone);
                    if (string.IsNullOrEmpty(normalized)) continue;
                    try
                    {
                        await smsService.SendSmsAsync(normalized, text);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Scheduled SMS send failed to {Phone} (user {UserId})", u.Phone, u.Id);
                    }
                }
                sms.SentAt = sms.SentAt ?? sentAt;
                sms.LastSentAt = sentAt;
                sms.TimesSent += 1;
                sms.UpdatedAt = DateTime.UtcNow;
                if (sms.Frequency == MailFrequency.Weekly || sms.Frequency == MailFrequency.Monthly)
                {
                    if (sms.ScheduledTime.HasValue)
                        sms.NextScheduledAt = ScheduleHelper.NextRecurringUtc(sms.Frequency.Value, sms.DayOfWeek, sms.DayOfMonth, sms.ScheduledTime.Value, sms.ScheduleTimezone, sentAt);
                }
                if (!sms.NextScheduledAt.HasValue)
                {
                    sms.NextScheduledAt = null;
                    sms.Status = MailStatus.Sent;
                    sms.IsActive = false;
                }
                if (sms.NextScheduledAt.HasValue) { sms.Status = MailStatus.Scheduled; sms.IsActive = true; }
                await ctx.SaveChangesAsync();
                _logger.LogInformation("Scheduled SMS {Id} sent to {Count} recipients (with valid phone).", sms.Id, recipients.Count);
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

        /// <summary>Users who receive SMS: IsActive, CanReceiveCommunications, role in list, and NormalizePhoneToE164(Phone) != null.</summary>
        private static async Task<List<User>> GetRecipientsForSms(ApplicationDbContext ctx, List<UserRole> roles)
        {
            if (roles == null || roles.Count == 0) return new List<User>();
            var candidates = await ctx.Users
                .Where(u => u.IsActive && u.CanReceiveCommunications && roles.Contains(u.Role))
                .ToListAsync();
            return candidates.Where(u => SmsService.NormalizePhoneToE164(u.Phone) != null).ToList();
        }
    }
}
