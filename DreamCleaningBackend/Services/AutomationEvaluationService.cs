using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class AutomationEvaluationService : IAutomationEvaluationService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AutomationEvaluationService> _logger;

        public AutomationEvaluationService(ApplicationDbContext context, ILogger<AutomationEvaluationService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task EnsureDefaultRulesAsync()
        {
            var exists = await _context.AutomationRules.AnyAsync(r => r.Key == AutomationRuleKeys.Winback);
            if (!exists)
            {
                _context.AutomationRules.Add(new AutomationRule
                {
                    Key = AutomationRuleKeys.Winback,
                    Name = "Win-back lapsed customers",
                    Description = "Flags customers who have a past order but haven't booked again within the threshold. " +
                                  "Creates an admin review alert — it does not message the customer.",
                    IsEnabled = false,
                    ThresholdDays = 21,
                    CooldownDays = 40,
                    Action = AutomationAction.AdminTask,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
            }
        }

        public async Task<int> EvaluateAllEnabledAsync()
        {
            await EnsureDefaultRulesAsync();
            await WakeDueSnoozedAlertsAsync();

            var keys = await _context.AutomationRules
                .Where(r => r.IsEnabled)
                .Select(r => r.Key)
                .ToListAsync();

            var total = 0;
            foreach (var key in keys)
                total += await EvaluateRuleAsync(key);
            return total;
        }

        public async Task<int> WakeDueSnoozedAlertsAsync()
        {
            var now = DateTime.UtcNow;
            var due = await _context.AutomationAlerts
                .Where(a => a.Status == AutomationAlertStatus.Snoozed
                            && a.RemindAt != null && a.RemindAt <= now)
                .ToListAsync();

            if (due.Count == 0) return 0;

            foreach (var a in due)
            {
                a.Status = AutomationAlertStatus.Open;
                a.RemindAt = null;
            }
            await _context.SaveChangesAsync();
            return due.Count;
        }

        public async Task<int> EvaluateRuleAsync(string ruleKey, bool ignoreEnabledFlag = false)
        {
            var rule = await _context.AutomationRules.FirstOrDefaultAsync(r => r.Key == ruleKey);
            if (rule == null) return 0;
            if (!rule.IsEnabled && !ignoreEnabledFlag) return 0;

            var created = ruleKey switch
            {
                AutomationRuleKeys.Winback => await EvaluateWinbackAsync(rule),
                _ => 0
            };

            rule.LastRunAt = DateTime.UtcNow;
            rule.LastRunCreatedCount = created;
            rule.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            if (created > 0)
                _logger.LogInformation("Automation rule '{Rule}' created {Count} alert(s).", ruleKey, created);

            return created;
        }

        /// <summary>
        /// Win-back: a customer with ≥1 non-cancelled order, NOT on an active subscription, whose last
        /// order is older than ThresholdDays. Skips anyone who already has an Open alert or was alerted
        /// within CooldownDays. Creates an admin review alert only — no customer contact.
        /// </summary>
        private async Task<int> EvaluateWinbackAsync(AutomationRule rule)
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-rule.ThresholdDays);
            var cooldownCutoff = now.AddDays(-rule.CooldownDays);

            // Real last-order date per customer, straight from the Orders table (source of truth).
            // The denormalized User.LastOrderDate can be stale, so we don't trust it here.
            var lastOrderByUser = await _context.Orders
                .Where(o => o.Status != "cancelled")
                .GroupBy(o => o.UserId)
                .Select(g => new { UserId = g.Key, LastDate = g.Max(o => o.ServiceDate) })
                .ToListAsync();

            var lapsed = lastOrderByUser
                .Where(x => x.LastDate < cutoff)
                .ToDictionary(x => x.UserId, x => x.LastDate);
            if (lapsed.Count == 0) return 0;

            var lapsedIds = lapsed.Keys.ToList();

            // Of the lapsed customers, keep the real ones (Customer role, not deleted).
            var candidates = await _context.Users
                .Where(u => u.Role == UserRole.Customer && !u.IsDeleted && lapsedIds.Contains(u.Id))
                .Select(u => new
                {
                    u.Id, u.FirstName, u.LastName,
                    u.SubscriptionId,
                    SubDays = u.Subscription != null ? u.Subscription.SubscriptionDays : 0,
                    u.SubscriptionExpiryDate
                })
                .ToListAsync();

            if (candidates.Count == 0) return 0;

            var candidateIds = candidates.Select(c => c.Id).ToList();

            // Existing alerts for this rule on these customers — to skip open ones and respect cooldown.
            var recentAlerts = await _context.AutomationAlerts
                .Where(a => a.RuleKey == rule.Key && candidateIds.Contains(a.UserId))
                .Select(a => new { a.UserId, a.Status, a.CreatedAt })
                .ToListAsync();

            // Treat both Open and Snoozed as "already being handled" — don't create a duplicate.
            var openUserIds = recentAlerts
                .Where(a => a.Status == AutomationAlertStatus.Open || a.Status == AutomationAlertStatus.Snoozed)
                .Select(a => a.UserId)
                .ToHashSet();
            var cooldownUserIds = recentAlerts
                .Where(a => a.CreatedAt >= cooldownCutoff)
                .Select(a => a.UserId)
                .ToHashSet();

            var newAlerts = new List<AutomationAlert>();
            foreach (var c in candidates)
            {
                // Skip active subscribers — their cadence is expected.
                var isSubscribed = c.SubscriptionId != null && c.SubDays > 0 &&
                    (c.SubscriptionExpiryDate == null || c.SubscriptionExpiryDate >= now);
                if (isSubscribed) continue;

                if (openUserIds.Contains(c.Id)) continue;
                if (cooldownUserIds.Contains(c.Id)) continue;

                var lastOrderDate = lapsed[c.Id];
                var days = (int)(now - lastOrderDate).TotalDays;
                var lastStr = lastOrderDate.ToString("yyyy-MM-dd");

                newAlerts.Add(new AutomationAlert
                {
                    RuleKey = rule.Key,
                    UserId = c.Id,
                    CustomerName = $"{c.FirstName} {c.LastName}".Trim(),
                    Reason = $"No order in {days} days (last: {lastStr}). Consider a win-back outreach.",
                    Status = AutomationAlertStatus.Open,
                    CreatedAt = now
                });
            }

            if (newAlerts.Count == 0) return 0;

            _context.AutomationAlerts.AddRange(newAlerts);
            await _context.SaveChangesAsync();
            return newAlerts.Count;
        }
    }
}
