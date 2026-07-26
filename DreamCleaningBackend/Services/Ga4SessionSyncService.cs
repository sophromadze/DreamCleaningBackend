using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using DreamCleaningBackend.Configuration;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Reconciles our first-party <see cref="SessionDailyStat"/> counts with GA4's bot-filtered
    /// session counts. GA4 has real history (further back than our tracking) and a more accurate,
    /// finalized count a few days after the fact — so we backfill history once and re-pull a trailing
    /// window nightly, overwriting each finalized day's rows with GA4's numbers (Origin = "ga4").
    ///
    /// Channel is derived to match our OWN taxonomy, not just GA4's: a session whose GA4 source is an
    /// AI-assistant host becomes "AI Assistant" (GA4 has no such channel group), otherwise GA4's
    /// sessionDefaultChannelGroup is mapped to our set exactly as the order-attribution backfill does.
    ///
    /// Transport reuses the GA4 attribution service's proven path: refresh-token → access-token,
    /// runReport over the shared IPv4 client (<see cref="Ga4AttributionBackfillService.HttpClientName"/>),
    /// bound to the same <see cref="Ga4Options"/>. No new config or HttpClient is registered.
    /// </summary>
    public class Ga4SessionSyncService : IGa4SessionSyncService
    {
        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string ApiHost = "https://analyticsdata.googleapis.com";
        private const int PageSize = 100_000;

        // GA4 finalizes session data within ~2 days; leave the last 2 days as our live counts.
        private const int ReconcileLagDays = 2;
        // Nightly window: re-pull ~a week so any late finalization is caught.
        private const int ReconcileWindowDays = 7;

        // GA4 sessionSource values that mean an AI assistant (matches the frontend AttributionService
        // AI_HOSTS list). Matched as a case-insensitive substring so host variants ("chatgpt.com",
        // "chat.openai.com", "openai") all resolve.
        private static readonly string[] AiSourceTokens =
        {
            "openai", "chatgpt", "gemini", "claude", "perplexity", "copilot", "grok", "x.ai", "deepseek"
        };

        private readonly ApplicationDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Ga4Options _options;
        private readonly ILogger<Ga4SessionSyncService> _logger;

        public Ga4SessionSyncService(
            ApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IOptions<Ga4Options> options,
            ILogger<Ga4SessionSyncService> logger)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_options.OAuth2ClientId)
            && !string.IsNullOrWhiteSpace(_options.OAuth2ClientSecret)
            && !string.IsNullOrWhiteSpace(_options.OAuth2RefreshToken)
            && !string.IsNullOrWhiteSpace(_options.PropertyId);

        public async Task<Ga4SessionSyncResult> BackfillHistoricalAsync(CancellationToken ct = default)
        {
            EnsureConfigured();

            var start = ParseStart(_options.StartDate);
            var to = MostRecentFinalizedDay();
            if (start > to) return new Ga4SessionSyncResult { DateRange = $"{start:yyyy-MM-dd}→{to:yyyy-MM-dd} (empty)" };

            return await SyncRangeAsync(start, to, ct);
        }

        public async Task<Ga4SessionSyncResult> ReconcileRecentAsync(CancellationToken ct = default)
        {
            EnsureConfigured();

            var to = MostRecentFinalizedDay();
            var from = NyTimeHelper.NowNy.Date.AddDays(-ReconcileWindowDays);
            if (from > to) from = to;

            return await SyncRangeAsync(from, to, ct);
        }

        // Newest day GA4 has likely finalized (NY calendar; SessionDailyStat.Date is NY too).
        private static DateTime MostRecentFinalizedDay() =>
            NyTimeHelper.NowNy.Date.AddDays(-ReconcileLagDays);

        private static DateTime ParseStart(string? raw)
        {
            return DateTime.TryParseExact((raw ?? "").Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.Date
                : new DateTime(2023, 1, 1);
        }

        // ── Sync a date range: pull GA4, then overwrite each finalized day with >0 sessions ────

        private async Task<Ga4SessionSyncResult> SyncRangeAsync(DateTime from, DateTime to, CancellationToken ct)
        {
            var accessToken = await GetAccessTokenAsync(ct)
                ?? throw new InvalidOperationException("Could not obtain a GA4 access token (check the analytics.readonly refresh token).");

            var ga4Rows = await FetchAllRowsAsync(accessToken, from, to, ct);

            // Aggregate GA4 rows to sessions per (date, our-channel), plus a per-date total for the
            // >0 guard (never wipe a real day on an empty/failed GA4 response for that day).
            var byDateChannel = new Dictionary<(DateTime Date, string Channel), long>();
            var totalByDate = new Dictionary<DateTime, long>();
            var rawRowCount = 0;

            foreach (var row in ga4Rows)
            {
                if (row["dimensionValues"] is not JArray dims || dims.Count < 4) continue;

                var dateStr = dims[0]?["value"]?.Value<string>();
                if (string.IsNullOrEmpty(dateStr) ||
                    !DateTime.TryParseExact(dateStr, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                    continue;

                var channelGroup = dims[1]?["value"]?.Value<string>();
                var source = dims[2]?["value"]?.Value<string>();
                // dims[3] = medium (unused for the mapping today, kept in the query for completeness).

                var sessions = 0L;
                if (row["metricValues"] is JArray metrics && metrics.Count > 0)
                    long.TryParse(metrics[0]?["value"]?.Value<string>(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out sessions);
                if (sessions <= 0) continue;

                var channel = DeriveChannel(channelGroup, source);
                var key = (date.Date, channel);
                byDateChannel[key] = (byDateChannel.TryGetValue(key, out var s) ? s : 0) + sessions;
                totalByDate[date.Date] = (totalByDate.TryGetValue(date.Date, out var t) ? t : 0) + sessions;
                rawRowCount++;
            }

            var datesToWrite = totalByDate.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToHashSet();

            var result = new Ga4SessionSyncResult
            {
                DateRange = $"{from:yyyy-MM-dd}→{to:yyyy-MM-dd}",
                Ga4Rows = rawRowCount
            };
            if (datesToWrite.Count == 0)
                return result;

            // Overwrite: drop the existing rows for every date we have GA4 data for, then insert the
            // GA4 channel-level rows (Origin = "ga4"). Source/Medium/Campaign are left null — the UI
            // groups sessions by channel only, so the finer dims aren't needed on reconciled rows.
            var existing = await _context.SessionDailyStats
                .Where(s => s.Date >= from && s.Date <= to)
                .ToListAsync(ct);
            var toRemove = existing.Where(e => datesToWrite.Contains(e.Date.Date)).ToList();
            _context.SessionDailyStats.RemoveRange(toRemove);

            var now = DateTime.UtcNow;
            foreach (var ((date, channel), sessions) in byDateChannel)
            {
                if (!datesToWrite.Contains(date)) continue;
                _context.SessionDailyStats.Add(new SessionDailyStat
                {
                    Date = date,
                    Channel = channel,
                    Source = null,
                    Medium = null,
                    Campaign = null,
                    Sessions = (int)Math.Min(sessions, int.MaxValue),
                    Origin = "ga4",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                result.SessionRowsWritten++;
                result.TotalSessions += sessions;
            }

            await _context.SaveChangesAsync(ct);
            result.DaysOverwritten = datesToWrite.Count;

            _logger.LogInformation(
                "GA4 session reconcile {Range}: {Rows} GA4 rows → {Days} day(s) overwritten, {Written} channel-rows, {Sessions} sessions.",
                result.DateRange, result.Ga4Rows, result.DaysOverwritten, result.SessionRowsWritten, result.TotalSessions);

            return result;
        }

        // GA4 sessionDefaultChannelGroup → our channel set, with an AI-assistant override derived from
        // the session source (GA4 has no "AI Assistant" group). GA4's Organic/Paid Social both collapse
        // to our single "Social", and "Email" is kept as its own channel; everything else outside our
        // set (Display, Shopping, Cross-network, …) still falls through to "Unassigned".
        private static string DeriveChannel(string? channelGroup, string? source)
        {
            if (IsAiSource(source)) return "AI Assistant";
            return (channelGroup ?? "").Trim() switch
            {
                "Organic Search" => "Organic Search",
                "Paid Search" => "Paid Search",
                "Direct" => "Direct",
                "Referral" => "Referral",
                "Organic Social" => "Social",
                "Paid Social" => "Social",
                "Email" => "Email",
                "Unassigned" => "Unassigned",
                _ => "Unassigned"
            };
        }

        private static bool IsAiSource(string? source)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;
            var s = source.Trim().ToLowerInvariant();
            return AiSourceTokens.Any(t => s.Contains(t));
        }

        // ── GA4 runReport (paged) ──────────────────────────────────────────────────────────

        private async Task<List<JObject>> FetchAllRowsAsync(
            string accessToken, DateTime from, DateTime to, CancellationToken ct)
        {
            var all = new List<JObject>();
            var offset = 0;

            while (true)
            {
                var (status, body) = await PostRunReportAsync(accessToken, from, to, offset, ct);
                if (status != HttpStatusCode.OK)
                {
                    _logger.LogError("GA4 session runReport failed ({Status}): {Body}", status, Truncate(body, 600));
                    throw new InvalidOperationException($"GA4 session runReport returned {(int)status}.");
                }

                var root = JObject.Parse(body);
                if (root["rows"] is JArray pageRows)
                    all.AddRange(pageRows.OfType<JObject>());

                var rowCount = root["rowCount"]?.Value<int>() ?? all.Count;
                var pageLen = (root["rows"] as JArray)?.Count ?? 0;

                offset += pageLen;
                if (pageLen == 0 || offset >= rowCount) break;
            }

            return all;
        }

        private async Task<(HttpStatusCode status, string body)> PostRunReportAsync(
            string accessToken, DateTime from, DateTime to, int offset, CancellationToken ct)
        {
            var propertyId = new string((_options.PropertyId ?? "").Where(char.IsDigit).ToArray());
            var url = $"{ApiHost}/v1beta/properties/{propertyId}:runReport";

            var requestBody = new
            {
                dateRanges = new[] { new { startDate = from.ToString("yyyy-MM-dd"), endDate = to.ToString("yyyy-MM-dd") } },
                dimensions = new[]
                {
                    new { name = "date" },
                    new { name = "sessionDefaultChannelGroup" },
                    new { name = "sessionSource" },
                    new { name = "sessionMedium" }
                },
                metrics = new[] { new { name = "sessions" } },
                limit = PageSize,
                offset,
                keepEmptyRows = false,
                returnPropertyQuota = false
            };

            var client = _httpClientFactory.CreateClient(Ga4AttributionBackfillService.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(
                JsonConvert.SerializeObject(requestBody), System.Text.Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return (response.StatusCode, body);
        }

        /// <summary>Exchanges the long-lived refresh token for a short-lived access token.</summary>
        private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
        {
            var client = _httpClientFactory.CreateClient(Ga4AttributionBackfillService.HttpClientName);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.OAuth2ClientId!,
                ["client_secret"] = _options.OAuth2ClientSecret!,
                ["refresh_token"] = _options.OAuth2RefreshToken!,
                ["grant_type"] = "refresh_token"
            });

            using var response = await client.PostAsync(TokenEndpoint, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("GA4 session token exchange failed ({Status}): {Body}", response.StatusCode, body);
                return null;
            }
            return JObject.Parse(body)["access_token"]?.Value<string>();
        }

        private void EnsureConfigured()
        {
            if (!IsConfigured)
                throw new InvalidOperationException("GA4 session sync is not configured (missing credentials in the Ga4 section).");
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);
    }
}
