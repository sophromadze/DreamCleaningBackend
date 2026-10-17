using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Mcp
{
    /// <summary>
    /// Daily housekeeping for the Claude connector (2026-10): the tool-call audit is kept
    /// <see cref="AuditRetention"/>; spent codes and tokens, sessions that ended a month ago, and
    /// registered clients nobody ever used are removed. Never touches expenses (their McpGrantId is
    /// a plain reference).
    /// </summary>
    public sealed class McpMaintenanceService : BackgroundService
    {
        public static readonly TimeSpan AuditRetention = TimeSpan.FromDays(180);

        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<McpMaintenanceService> _logger;

        public McpMaintenanceService(IServiceScopeFactory scopes, ILogger<McpMaintenanceService> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await RunOnceAsync(db, DateTime.UtcNow, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Claude connector cleanup failed; it runs again tomorrow.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public static async Task RunOnceAsync(ApplicationDbContext db, DateTime nowUtc, CancellationToken ct)
        {
            var auditCutoff = nowUtc - AuditRetention;
            await db.McpToolCallLogs.Where(l => l.CreatedAt < auditCutoff).ExecuteDeleteAsync(ct);

            var dayAgo = nowUtc.AddDays(-1);
            await db.McpAuthorizationCodes.Where(c => c.ExpiresAt < dayAgo).ExecuteDeleteAsync(ct);
            await db.McpTokens.Where(t => t.ExpiresAt < dayAgo || (t.ConsumedAt != null && t.ConsumedAt < dayAgo)).ExecuteDeleteAsync(ct);

            // Previewed change sets (connector and panel): kept 30 days for their outcome, then
            // dropped. The changes themselves live in the audit log, not here.
            var changeSetCutoff = nowUtc.AddDays(-30);
            await db.PendingChangeSets.Where(p => p.ExpiresAt < changeSetCutoff).ExecuteDeleteAsync(ct);

            // Ended a month ago: revoked, or no refresh token left (they were deleted above).
            var monthAgo = nowUtc.AddDays(-30);
            await db.McpGrants
                .Where(g => (g.RevokedAt != null && g.RevokedAt < monthAgo)
                            || (g.CreatedAt < monthAgo && !g.Tokens.Any(t => t.Kind == McpTokenKinds.Refresh)))
                .ExecuteDeleteAsync(ct);

            // Every fresh connection registers a client (DCR); one never used to sign in is noise.
            var weekAgo = nowUtc.AddDays(-7);
            var liveClientIds = db.McpGrants.Select(g => g.ClientId);
            await db.McpOAuthClients
                .Where(c => c.CreatedAt < weekAgo && (c.LastUsedAt == null || c.LastUsedAt < monthAgo) && !liveClientIds.Contains(c.ClientId))
                .ExecuteDeleteAsync(ct);
        }
    }
}
