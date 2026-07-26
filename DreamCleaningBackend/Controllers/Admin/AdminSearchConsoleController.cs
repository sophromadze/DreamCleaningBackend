using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Manual triggers for the Search Console organic-keyword sync (SuperAdmin only). Synced rows land
    /// in SearchConsoleDailyStat and surface in the Company "Keywords" tab (organic table). Served
    /// under the shared api/admin prefix.
    /// </summary>
    [Route("api/admin/search-console")]
    [ApiController]
    [Authorize(Roles = "SuperAdmin")]
    public class AdminSearchConsoleController : AdminControllerBase
    {
        private readonly ISearchConsoleSyncService _searchConsole;
        private readonly ILogger<AdminSearchConsoleController> _logger;

        public AdminSearchConsoleController(
            ISearchConsoleSyncService searchConsole,
            ILogger<AdminSearchConsoleController> logger)
        {
            _searchConsole = searchConsole;
            _logger = logger;
        }

        // Full historical pull: BackfillStartDate → most recent available day. Idempotent (upsert).
        [HttpPost("backfill")]
        public async Task<ActionResult> Backfill(CancellationToken ct)
        {
            if (!_searchConsole.IsConfigured)
                return BadRequest(new { message = "Search Console sync is not configured." });

            try
            {
                var result = await _searchConsole.BackfillAsync(ct);
                return Ok(new { rowsUpserted = result.RowsUpserted, daysCovered = result.DaysCovered });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Search Console backfill failed.");
                return BadRequest(new { message = ex.Message });
            }
        }

        // Rolling refresh of the trailing window (corrects Search Console's 2–3 day reporting lag).
        [HttpPost("sync-recent")]
        public async Task<ActionResult> SyncRecent(CancellationToken ct)
        {
            if (!_searchConsole.IsConfigured)
                return BadRequest(new { message = "Search Console sync is not configured." });

            try
            {
                var result = await _searchConsole.SyncRecentAsync(ct);
                return Ok(new { rowsUpserted = result.RowsUpserted, daysCovered = result.DaysCovered });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Search Console recent sync failed.");
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
