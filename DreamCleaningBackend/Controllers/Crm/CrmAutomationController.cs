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
    /// CRM retention automation. v1 is NON-sending: rules create admin review alerts, they never
    /// message customers. This controller manages rule config (enable, thresholds), the alert feed
    /// admins work through, and a manual "run now" for testing. Actual evaluation lives in
    /// <see cref="IAutomationEvaluationService"/> (shared with the background worker).
    /// </summary>
    [Route("api/crm/automation")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class CrmAutomationController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IAutomationEvaluationService _evaluator;

        public CrmAutomationController(ApplicationDbContext context, IAutomationEvaluationService evaluator)
        {
            _context = context;
            _evaluator = evaluator;
        }

        // ── Rules ──

        [HttpGet("rules")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<AutomationRuleDto>>> GetRules()
        {
            await _evaluator.EnsureDefaultRulesAsync();
            await _evaluator.WakeDueSnoozedAlertsAsync();

            var rules = await _context.AutomationRules.OrderBy(r => r.Id).ToListAsync();

            var openCounts = await _context.AutomationAlerts
                .Where(a => a.Status == AutomationAlertStatus.Open)
                .GroupBy(a => a.RuleKey)
                .Select(g => new { RuleKey = g.Key, Count = g.Count() })
                .ToListAsync();
            var openMap = openCounts.ToDictionary(c => c.RuleKey, c => c.Count);

            return Ok(rules.Select(r => MapRule(r, openMap.GetValueOrDefault(r.Key))).ToList());
        }

        /// <summary>Enable/disable a rule or tune its thresholds. SuperAdmin/Admin only — Moderators can't change automation.</summary>
        [HttpPut("rules/{id}")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<AutomationRuleDto>> UpdateRule(int id, [FromBody] UpdateAutomationRuleDto dto)
        {
            var rule = await _context.AutomationRules.FirstOrDefaultAsync(r => r.Id == id);
            if (rule == null) return NotFound(new { message = "Rule not found" });

            if (dto.IsEnabled.HasValue) rule.IsEnabled = dto.IsEnabled.Value;
            if (dto.ThresholdDays.HasValue) rule.ThresholdDays = dto.ThresholdDays.Value;
            if (dto.CooldownDays.HasValue) rule.CooldownDays = dto.CooldownDays.Value;
            rule.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var openCount = await _context.AutomationAlerts
                .CountAsync(a => a.RuleKey == rule.Key && a.Status == AutomationAlertStatus.Open);
            return Ok(MapRule(rule, openCount));
        }

        /// <summary>Evaluate a rule immediately (ignores the enabled flag so admins can preview results).</summary>
        [HttpPost("rules/{id}/run")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<object>> RunRule(int id)
        {
            var rule = await _context.AutomationRules.FirstOrDefaultAsync(r => r.Id == id);
            if (rule == null) return NotFound(new { message = "Rule not found" });

            var created = await _evaluator.EvaluateRuleAsync(rule.Key, ignoreEnabledFlag: true);
            return Ok(new { created, message = $"Created {created} new alert(s)." });
        }

        // ── Alerts ──

        [HttpGet("alerts")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<AutomationAlertDto>>> GetAlerts(
            [FromQuery] string status = "Open",
            [FromQuery] string? ruleKey = null)
        {
            // Snoozed alerts whose remind date has arrived flip back to Open before we read.
            await _evaluator.WakeDueSnoozedAlertsAsync();

            var query = _context.AutomationAlerts.AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && status != "all")
                query = query.Where(a => a.Status == status);
            if (!string.IsNullOrWhiteSpace(ruleKey))
                query = query.Where(a => a.RuleKey == ruleKey);

            var alerts = await query
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new AutomationAlertDto
                {
                    Id = a.Id,
                    RuleKey = a.RuleKey,
                    UserId = a.UserId,
                    CustomerName = a.CustomerName,
                    CustomerEmail = a.User != null ? a.User.Email : null,
                    CustomerPhone = a.User != null ? a.User.Phone : null,
                    Reason = a.Reason,
                    Status = a.Status,
                    RemindAt = a.RemindAt,
                    Attempts = a.Attempts,
                    LastAttemptAt = a.LastAttemptAt,
                    CreatedAt = a.CreatedAt,
                    ResolvedAt = a.ResolvedAt,
                    ResolvedByAdminName = a.ResolvedByAdminName
                })
                .ToListAsync();

            // LTV + last-order come from the Orders table (source of truth), not the stale
            // User.TotalSpentAmount / User.LastOrderDate fields. One grouped query for all users.
            var userIds = alerts.Select(a => a.UserId).Distinct().ToList();
            if (userIds.Count > 0)
            {
                var aggregates = await _context.Orders
                    .Where(o => userIds.Contains(o.UserId) && o.Status != "cancelled")
                    .GroupBy(o => o.UserId)
                    .Select(g => new { UserId = g.Key, Sum = g.Sum(o => o.Total), MaxDate = g.Max(o => o.ServiceDate) })
                    .ToListAsync();
                var aggMap = aggregates.ToDictionary(a => a.UserId);

                foreach (var dto in alerts)
                {
                    if (aggMap.TryGetValue(dto.UserId, out var agg))
                    {
                        dto.CustomerLifetimeValue = agg.Sum;
                        dto.LastOrderDate = agg.MaxDate;
                    }
                }
            }

            return Ok(alerts);
        }

        [HttpGet("alerts/summary")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<AutomationSummaryDto>> GetSummary()
        {
            var open = await _context.AutomationAlerts.CountAsync(a => a.Status == AutomationAlertStatus.Open);
            return Ok(new AutomationSummaryDto { OpenAlerts = open });
        }

        [HttpPut("alerts/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<AutomationAlertDto>> UpdateAlert(int id, [FromBody] UpdateAutomationAlertDto dto)
        {
            if (!AutomationAlertStatus.IsValid(dto.Status))
                return BadRequest(new { message = $"Invalid status '{dto.Status}'." });

            if (dto.Status == AutomationAlertStatus.Snoozed)
            {
                if (!dto.RemindAt.HasValue)
                    return BadRequest(new { message = "A remind date is required to snooze an alert." });
                if (dto.RemindAt.Value.Date <= DateTime.UtcNow.Date)
                    return BadRequest(new { message = "The remind date must be in the future." });
            }

            var alert = await _context.AutomationAlerts
                .Include(a => a.User)
                .FirstOrDefaultAsync(a => a.Id == id);
            if (alert == null) return NotFound(new { message = "Alert not found" });

            alert.Status = dto.Status;
            if (dto.Status == AutomationAlertStatus.Open)
            {
                alert.RemindAt = null;
                alert.ResolvedAt = null;
                alert.ResolvedByAdminId = null;
                alert.ResolvedByAdminName = null;
            }
            else if (dto.Status == AutomationAlertStatus.Snoozed)
            {
                // Snoozed = scheduled to reappear; it's not "resolved" yet.
                alert.RemindAt = dto.RemindAt;
                alert.ResolvedAt = null;
                alert.ResolvedByAdminId = GetUserId();
                alert.ResolvedByAdminName = GetUserDisplayName();
            }
            else // Done / Dismissed
            {
                alert.RemindAt = null;
                alert.ResolvedAt = DateTime.UtcNow;
                alert.ResolvedByAdminId = GetUserId();
                alert.ResolvedByAdminName = GetUserDisplayName();
            }

            await _context.SaveChangesAsync();

            // LTV + last order from the Orders table (source of truth).
            var agg = await _context.Orders
                .Where(o => o.UserId == alert.UserId && o.Status != "cancelled")
                .GroupBy(o => o.UserId)
                .Select(g => new { Sum = g.Sum(o => o.Total), MaxDate = (DateTime?)g.Max(o => o.ServiceDate) })
                .FirstOrDefaultAsync();

            return Ok(new AutomationAlertDto
            {
                Id = alert.Id,
                RuleKey = alert.RuleKey,
                UserId = alert.UserId,
                CustomerName = alert.CustomerName,
                CustomerEmail = alert.User?.Email,
                CustomerPhone = alert.User?.Phone,
                CustomerLifetimeValue = agg?.Sum ?? 0,
                LastOrderDate = agg?.MaxDate,
                Reason = alert.Reason,
                Status = alert.Status,
                RemindAt = alert.RemindAt,
                Attempts = alert.Attempts,
                LastAttemptAt = alert.LastAttemptAt,
                CreatedAt = alert.CreatedAt,
                ResolvedAt = alert.ResolvedAt,
                ResolvedByAdminName = alert.ResolvedByAdminName
            });
        }

        /// <summary>Log a failed contact attempt ("no answer"). Records the attempt + time and keeps
        /// the alert Open so it can be retried — it is NOT resolved or dismissed.</summary>
        [HttpPost("alerts/{id}/no-answer")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<AutomationAlertDto>> LogNoAnswer(int id)
        {
            var alert = await _context.AutomationAlerts
                .Include(a => a.User)
                .FirstOrDefaultAsync(a => a.Id == id);
            if (alert == null) return NotFound(new { message = "Alert not found" });

            alert.Attempts += 1;
            alert.LastAttemptAt = DateTime.UtcNow;
            // Stays actionable: if it was snoozed, bring it back to Open so it's retried now.
            alert.Status = AutomationAlertStatus.Open;
            alert.RemindAt = null;
            alert.ResolvedAt = null;
            alert.ResolvedByAdminId = null;
            alert.ResolvedByAdminName = null;

            await _context.SaveChangesAsync();

            var agg = await _context.Orders
                .Where(o => o.UserId == alert.UserId && o.Status != "cancelled")
                .GroupBy(o => o.UserId)
                .Select(g => new { Sum = g.Sum(o => o.Total), MaxDate = (DateTime?)g.Max(o => o.ServiceDate) })
                .FirstOrDefaultAsync();

            return Ok(new AutomationAlertDto
            {
                Id = alert.Id,
                RuleKey = alert.RuleKey,
                UserId = alert.UserId,
                CustomerName = alert.CustomerName,
                CustomerEmail = alert.User?.Email,
                CustomerPhone = alert.User?.Phone,
                CustomerLifetimeValue = agg?.Sum ?? 0,
                LastOrderDate = agg?.MaxDate,
                Reason = alert.Reason,
                Status = alert.Status,
                RemindAt = alert.RemindAt,
                Attempts = alert.Attempts,
                LastAttemptAt = alert.LastAttemptAt,
                CreatedAt = alert.CreatedAt,
                ResolvedAt = alert.ResolvedAt,
                ResolvedByAdminName = alert.ResolvedByAdminName
            });
        }

        // ── Helpers ──

        private static AutomationRuleDto MapRule(AutomationRule r, int openAlertCount) => new()
        {
            Id = r.Id,
            Key = r.Key,
            Name = r.Name,
            Description = r.Description,
            IsEnabled = r.IsEnabled,
            ThresholdDays = r.ThresholdDays,
            CooldownDays = r.CooldownDays,
            Action = r.Action,
            LastRunAt = r.LastRunAt,
            LastRunCreatedCount = r.LastRunCreatedCount,
            OpenAlertCount = openAlertCount
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
