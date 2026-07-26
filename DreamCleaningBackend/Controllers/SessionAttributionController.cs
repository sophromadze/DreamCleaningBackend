using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Public, fire-and-forget endpoint the browser hits (via sendBeacon) when AttributionService
    /// classifies a NEW session — feeds the CRM Ads-tab funnel (sessions → booked orders). It only
    /// upsert-increments an aggregated counter (SessionDailyStat); it stores NO PII and never affects
    /// booking. Abuse protection is proportionate to a low-value counter write: Cloudflare at the
    /// edge, a per-IP rate limiter, an Origin check, and strict channel/length validation. Always
    /// returns 204 so bots and blocked/failed calls get nothing back and nothing is surfaced.
    /// </summary>
    [ApiController]
    [Route("api/attribution")]
    [AllowAnonymous]
    public class SessionAttributionController : ControllerBase
    {
        public const string RateLimitPolicy = "session-log";

        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _config;

        public SessionAttributionController(ApplicationDbContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
        }

        // POST /api/attribution/session
        [HttpPost("session")]
        [EnableRateLimiting(RateLimitPolicy)]
        public async Task<IActionResult> LogSession([FromBody] AttributionDto? dto)
        {
            // Cheap bot filter: if the request advertises an Origin/Referer, it must be ours. A silent
            // no-op (still 204) on mismatch — we never reveal that it was rejected.
            if (!IsAllowedOrigin())
                return NoContent();

            if (dto == null)
                return NoContent();

            // Same normalization/clamping as order attribution. Sessions always land in a bucket, so
            // a blank/unknown channel becomes "Unassigned" rather than null.
            var channel = AcquisitionChannels.Normalize(dto.Channel) ?? "Unassigned";
            var source = AcquisitionChannels.Clamp(dto.Source, 200);
            var medium = AcquisitionChannels.Clamp(dto.Medium, 100);
            var campaign = AcquisitionChannels.Clamp(dto.Campaign, 200);

            var today = NyTimeHelper.NowNy.Date;

            try
            {
                // Upsert-increment by the full key. Query-based (not a DB unique index) because the
                // free-text columns are nullable and MySQL treats NULLs as distinct in unique indexes.
                var existing = await _context.SessionDailyStats.FirstOrDefaultAsync(s =>
                    s.Date == today &&
                    s.Channel == channel &&
                    s.Source == source &&
                    s.Medium == medium &&
                    s.Campaign == campaign);

                if (existing != null)
                {
                    existing.Sessions++;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    _context.SessionDailyStats.Add(new SessionDailyStat
                    {
                        Date = today,
                        Channel = channel,
                        Source = source,
                        Medium = medium,
                        Campaign = campaign,
                        Sessions = 1,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }

                await _context.SaveChangesAsync();
            }
            catch
            {
                // Best-effort analytics — a failed counter write must never surface an error.
            }

            return NoContent();
        }

        // The request's Origin (or Referer) host must match our frontend host. Missing both ⇒ allow
        // (some same-origin agents omit them); only an explicit foreign origin is dropped.
        private bool IsAllowedOrigin()
        {
            var raw = Request.Headers["Origin"].ToString();
            if (string.IsNullOrEmpty(raw))
                raw = Request.Headers["Referer"].ToString();
            if (string.IsNullOrEmpty(raw))
                return true;

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var origin))
                return true;

            var host = origin.Host.ToLowerInvariant();
            if (host is "localhost" or "127.0.0.1")
                return true;

            var frontendUrl = _config["Frontend:Url"];
            if (!string.IsNullOrEmpty(frontendUrl) && Uri.TryCreate(frontendUrl, UriKind.Absolute, out var fe))
            {
                var feHost = fe.Host.ToLowerInvariant().Replace("www.", "");
                if (host.Replace("www.", "") == feHost)
                    return true;
                return false;
            }

            // No configured frontend host to compare against — don't drop legitimate traffic.
            return true;
        }
    }
}
