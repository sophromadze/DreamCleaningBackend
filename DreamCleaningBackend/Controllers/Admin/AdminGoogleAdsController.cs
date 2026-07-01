using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Manual triggers for the Google Ads daily-spend sync (SuperAdmin only). The synced rows land
    /// in the Expenses table, so the Expenses and Statistics pages reflect them with no extra work.
    /// Served under the shared api/admin prefix.
    /// </summary>
    [Route("api/admin/google-ads")]
    [ApiController]
    [Authorize(Roles = "SuperAdmin")]
    public class AdminGoogleAdsController : AdminControllerBase
    {
        private readonly IGoogleAdsCostService _googleAds;
        private readonly ILogger<AdminGoogleAdsController> _logger;

        public AdminGoogleAdsController(
            IGoogleAdsCostService googleAds,
            ILogger<AdminGoogleAdsController> logger)
        {
            _googleAds = googleAds;
            _logger = logger;
        }

        // Full historical pull: BackfillStartDate → yesterday. Idempotent (upsert by SourceKey).
        [HttpPost("backfill")]
        public async Task<ActionResult> Backfill(CancellationToken ct)
        {
            if (!_googleAds.IsConfigured)
                return BadRequest(new { message = "Google Ads sync is not configured." });

            try
            {
                var result = await _googleAds.BackfillAsync(ct);
                return Ok(new { daysSynced = result.DaysSynced, totalUsd = result.TotalUsd });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Google Ads backfill failed.");
                return BadRequest(new { message = ex.Message });
            }
        }

        // Rolling refresh of the trailing 7 days (corrects Google's not-yet-finalized cost).
        [HttpPost("sync-recent")]
        public async Task<ActionResult> SyncRecent(CancellationToken ct)
        {
            if (!_googleAds.IsConfigured)
                return BadRequest(new { message = "Google Ads sync is not configured." });

            try
            {
                var result = await _googleAds.SyncRecentAsync(ct);
                return Ok(new { daysSynced = result.DaysSynced });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Google Ads recent sync failed.");
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
