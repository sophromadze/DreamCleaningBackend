using System.Globalization;
using System.Net.Http.Headers;
using DreamCleaningBackend.Configuration;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Reads organic search queries from the Google Search Console Search Analytics API and upserts
    /// them per (day, query) into <see cref="SearchConsoleDailyStat"/> — the data behind the Keywords
    /// dashboard's organic table.
    ///
    /// Transport mirrors GoogleAdsCostService / Ga4AttributionBackfillService: refresh-token →
    /// access-token, REST over a named IPv4 HttpClient (the production VPS has IPv6 disabled).
    /// </summary>
    public class SearchConsoleSyncService : ISearchConsoleSyncService
    {
        public const string HttpClientName = "SearchConsoleIpv4";

        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string ApiHost = "https://searchconsole.googleapis.com";
        private const int RowLimit = 25000;          // API max rows per Search Analytics request
        private const int ReportingLagDays = 3;      // Search Console finalizes data ~2–3 days late
        private const int RecentWindowDays = 5;      // trailing days re-pulled by SyncRecent

        private readonly ApplicationDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SearchConsoleOptions _options;
        private readonly ILogger<SearchConsoleSyncService> _logger;

        public SearchConsoleSyncService(
            ApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IOptions<SearchConsoleOptions> options,
            ILogger<SearchConsoleSyncService> logger)
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
            && !string.IsNullOrWhiteSpace(_options.SiteUrl);

        public async Task<SearchConsoleSyncResult> BackfillAsync(CancellationToken ct = default)
        {
            EnsureConfigured();

            if (!DateTime.TryParseExact(_options.BackfillStartDate, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            {
                throw new InvalidOperationException(
                    $"SearchConsole:BackfillStartDate ('{_options.BackfillStartDate}') is missing or not in yyyy-MM-dd format.");
            }

            var to = MostRecentAvailableDay();
            if (start.Date > to) return new SearchConsoleSyncResult();

            return await SyncRangeAsync(start.Date, to, ct);
        }

        public async Task<SearchConsoleSyncResult> SyncRecentAsync(CancellationToken ct = default)
        {
            EnsureConfigured();

            var to = MostRecentAvailableDay();
            var from = to.AddDays(-(RecentWindowDays - 1));
            return await SyncRangeAsync(from, to, ct);
        }

        // Newest day Search Console is likely to have finalized (today minus the reporting lag).
        private static DateTime MostRecentAvailableDay() =>
            DateTime.UtcNow.Date.AddDays(-ReportingLagDays);

        // ── Sync a date range ─────────────────────────────────────────────────────────────

        private async Task<SearchConsoleSyncResult> SyncRangeAsync(DateTime from, DateTime to, CancellationToken ct)
        {
            var accessToken = await GetAccessTokenAsync(ct)
                ?? throw new InvalidOperationException("Could not obtain a Search Console access token (check the webmasters.readonly refresh token).");

            var rows = await QueryAllRowsAsync(accessToken, from, to, ct);
            return await UpsertAsync(from, to, rows, ct);
        }

        // One aggregated Search Analytics row reduced to what we store.
        private readonly record struct QueryRow(DateTime Date, string Query, int Clicks, int Impressions, decimal Ctr, decimal Position);

        private async Task<List<QueryRow>> QueryAllRowsAsync(
            string accessToken, DateTime from, DateTime to, CancellationToken ct)
        {
            var siteUrl = Uri.EscapeDataString(_options.SiteUrl!.Trim());
            var url = $"{ApiHost}/webmasters/v3/sites/{siteUrl}/searchAnalytics/query";
            var client = _httpClientFactory.CreateClient(HttpClientName);

            var all = new List<QueryRow>();
            var startRow = 0;

            while (true)
            {
                var body = new
                {
                    startDate = from.ToString("yyyy-MM-dd"),
                    endDate = to.ToString("yyyy-MM-dd"),
                    dimensions = new[] { "date", "query" },
                    rowLimit = RowLimit,
                    startRow,
                    dataState = "final"
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Content = new StringContent(
                    JsonConvert.SerializeObject(body), System.Text.Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request, ct);
                var payload = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Search Console query failed ({Status}): {Body}", response.StatusCode, payload);
                    throw new InvalidOperationException($"Search Console searchAnalytics returned {(int)response.StatusCode}.");
                }

                var pageRows = ParseRows(payload);
                all.AddRange(pageRows);

                if (pageRows.Count < RowLimit) break; // last page
                startRow += RowLimit;
            }

            return all;
        }

        private static List<QueryRow> ParseRows(string body)
        {
            var result = new List<QueryRow>();
            var root = JObject.Parse(body);
            if (root["rows"] is not JArray rows) return result;

            foreach (var row in rows)
            {
                if (row["keys"] is not JArray keys || keys.Count < 2) continue;

                var dateStr = keys[0]?.Value<string>();
                if (string.IsNullOrEmpty(dateStr) ||
                    !DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                    continue;

                var query = keys[1]?.Value<string>() ?? string.Empty;
                query = query.Trim();
                if (query.Length > 300) query = query.Substring(0, 300);

                var clicks = (int)Math.Round(row["clicks"]?.Value<double>() ?? 0);
                var impressions = (int)Math.Round(row["impressions"]?.Value<double>() ?? 0);
                var ctr = Math.Round(row["ctr"]?.Value<decimal>() ?? 0m, 4, MidpointRounding.AwayFromZero);
                var position = Math.Round(row["position"]?.Value<decimal>() ?? 0m, 2, MidpointRounding.AwayFromZero);

                result.Add(new QueryRow(date.Date, query, clicks, impressions, ctr, position));
            }

            return result;
        }

        // ── Upsert by (Date, Query) ───────────────────────────────────────────────────────

        private async Task<SearchConsoleSyncResult> UpsertAsync(
            DateTime from, DateTime to, List<QueryRow> rows, CancellationToken ct)
        {
            var result = new SearchConsoleSyncResult();
            if (rows.Count == 0) return result;

            // Load existing rows for the window once; upsert in memory (avoids a query per row).
            var existing = await _context.SearchConsoleDailyStats
                .Where(s => s.Date >= from && s.Date <= to)
                .ToListAsync(ct);

            var byKey = new Dictionary<(DateTime, string), SearchConsoleDailyStat>();
            foreach (var e in existing)
                byKey[(e.Date.Date, e.Query)] = e; // first wins if the table somehow has dupes

            var now = DateTime.UtcNow;
            var days = new HashSet<DateTime>();

            foreach (var r in rows)
            {
                days.Add(r.Date);
                var key = (r.Date, r.Query);
                if (byKey.TryGetValue(key, out var stat))
                {
                    if (stat.Clicks != r.Clicks || stat.Impressions != r.Impressions
                        || stat.Ctr != r.Ctr || stat.Position != r.Position)
                    {
                        stat.Clicks = r.Clicks;
                        stat.Impressions = r.Impressions;
                        stat.Ctr = r.Ctr;
                        stat.Position = r.Position;
                        stat.UpdatedAt = now;
                    }
                }
                else
                {
                    var created = new SearchConsoleDailyStat
                    {
                        Date = r.Date,
                        Query = r.Query,
                        Clicks = r.Clicks,
                        Impressions = r.Impressions,
                        Ctr = r.Ctr,
                        Position = r.Position,
                        UpdatedAt = now
                    };
                    _context.SearchConsoleDailyStats.Add(created);
                    byKey[key] = created;
                }

                result.RowsUpserted++;
            }

            await _context.SaveChangesAsync(ct);
            result.DaysCovered = days.Count;
            return result;
        }

        /// <summary>Exchanges the long-lived refresh token for a short-lived access token.</summary>
        private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
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
                _logger.LogError("Search Console token exchange failed ({Status}): {Body}", response.StatusCode, body);
                return null;
            }
            return JObject.Parse(body)["access_token"]?.Value<string>();
        }

        private void EnsureConfigured()
        {
            if (!IsConfigured)
                throw new InvalidOperationException("Search Console sync is not configured (missing credentials in the SearchConsole section).");
        }
    }
}
