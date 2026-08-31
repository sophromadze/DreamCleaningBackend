using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// ONE-TIME GA4 → Orders attribution backfill trigger (SuperAdmin only). Fills historical orders'
    /// AcquisitionChannel/Source/Medium/Campaign from GA4 purchase events (transaction_id = Order.Id),
    /// only where they are still NULL. Not a recurring job — this controller + its service can be
    /// deleted after the backfill has been run once. Served under the shared api/admin prefix.
    /// </summary>
    [Route("api/admin/ga4")]
    [ApiController]
    [Authorize(Roles = "SuperAdmin")]
    public class AdminGa4Controller : AdminControllerBase
    {
        private readonly IGa4AttributionBackfillService _backfill;
        private readonly IGa4SessionSyncService _sessionSync;
        private readonly ILogger<AdminGa4Controller> _logger;
        private readonly IAuditService _auditService;

        public AdminGa4Controller(
            IGa4AttributionBackfillService backfill,
            IGa4SessionSyncService sessionSync,
            ILogger<AdminGa4Controller> logger,
            IAuditService auditService)
        {
            _backfill = backfill;
            _sessionSync = sessionSync;
            _logger = logger;
            _auditService = auditService;
        }

        // POST /api/admin/ga4/backfill-attribution?target=firsttouch|converting  (default firsttouch)
        // Idempotent by construction: re-running only affects orders whose target channel is still
        // NULL, so already backfilled / live-captured / admin Phone-Unknown orders are left untouched.
        // firsttouch → Acquisition* (GA4 firstUser*); converting → Converting* (GA4 session*).
        [HttpPost("backfill-attribution")]
        public async Task<ActionResult> BackfillAttribution([FromQuery] string? target, CancellationToken ct)
        {
            if (!_backfill.IsConfigured)
                return BadRequest(new { message = "GA4 backfill is not configured (missing credentials in the Ga4 section)." });

            var backfillTarget = string.Equals(target, "converting", StringComparison.OrdinalIgnoreCase)
                ? Ga4BackfillTarget.Converting
                : Ga4BackfillTarget.FirstTouch;

            try
            {
                var r = await _backfill.RunAsync(backfillTarget, ct);

                // A backfill WRITES acquisition channels onto historical orders, which is what the
                // CRM Ads tab reports against ad spend. It is audited for the same reason a
                // pricing import is: one click, many rows, and a later "why did last quarter's
                // channel mix change" needs an answer.
                await _auditService.LogActionAsync(
                    AuditEntityTypes.DataSync, 0, "Ga4AttributionBackfill", null, new
                    {
                        Source = "GA4 attribution backfill",
                        Target = backfillTarget.ToString(),
                        DateRange = r.DateRange,
                        OrdersUpdated = r.OrdersUpdated,
                        OrdersAlreadyAttributed = r.OrdersAlreadyAttributed,
                        TransactionsWithNoOrder = r.TransactionsWithNoOrder
                    });

                return Ok(new
                {
                    target = backfillTarget.ToString(),
                    dimensionSetUsed = r.DimensionSetUsed,
                    dateRange = r.DateRange,
                    ga4Rows = r.Ga4Rows,
                    distinctTransactions = r.DistinctTransactions,
                    transactionsMatchedToOrder = r.TransactionsMatchedToOrder,
                    ordersUpdated = r.OrdersUpdated,
                    ordersAlreadyAttributed = r.OrdersAlreadyAttributed,
                    transactionsWithNoOrder = r.TransactionsWithNoOrder,
                    updatedByChannel = r.UpdatedByChannel
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "GA4 attribution backfill failed.");
                return BadRequest(new { message = ex.Message });
            }
        }

        // POST /api/admin/ga4/backfill-sessions
        // ONE-TIME (safe to re-run): reconciles SessionDailyStat against GA4 across the whole available
        // range — fills empty pre-tracking history AND overwrites the already-elapsed live era up to the
        // last finalized day (closing the gap the nightly trailing-window would otherwise never reach).
        // The nightly Ga4SessionSyncBackgroundService maintains the rolling window after this.
        [HttpPost("backfill-sessions")]
        public async Task<ActionResult> BackfillSessions(CancellationToken ct)
        {
            if (!_sessionSync.IsConfigured)
                return BadRequest(new { message = "GA4 session sync is not configured (missing credentials in the Ga4 section)." });

            try
            {
                var r = await _sessionSync.BackfillHistoricalAsync(ct);

                await _auditService.LogActionAsync(
                    AuditEntityTypes.DataSync, 0, "Ga4SessionBackfill", null, new
                    {
                        Source = "GA4 session backfill",
                        DateRange = r.DateRange,
                        DaysOverwritten = r.DaysOverwritten,
                        SessionRowsWritten = r.SessionRowsWritten,
                        TotalSessions = r.TotalSessions
                    });

                return Ok(new
                {
                    dateRange = r.DateRange,
                    ga4Rows = r.Ga4Rows,
                    daysOverwritten = r.DaysOverwritten,
                    sessionRowsWritten = r.SessionRowsWritten,
                    totalSessions = r.TotalSessions
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "GA4 session backfill failed.");
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
