using System.Globalization;
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
    /// Reads per-day Google Ads account spend and upserts it into the Expenses table so it flows
    /// through the existing Expenses/Statistics pipeline untouched (one Expense per day, category
    /// "Google Ads", idempotent on <see cref="Expense.SourceKey"/>).
    ///
    /// Transport note: we call the Google Ads REST endpoint (googleAds:searchStream) via a named
    /// <see cref="IHttpClientFactory"/> client that forces IPv4 (see Program.cs). The gRPC .NET
    /// SDK builds its own channel with an internal HttpClientHandler and exposes no hook to force
    /// IPv4 (AddressFamily.InterNetwork), which the IPv6-disabled VPS requires — so REST over our
    /// own SocketsHttpHandler is the reliable path here, matching the Google Reviews client.
    /// </summary>
    public class GoogleAdsCostService : IGoogleAdsCostService
    {
        public const string HttpClientName = "GoogleAdsIpv4";

        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string ApiHost = "https://googleads.googleapis.com";
        private const string CategoryName = "Google Ads";

        private readonly ApplicationDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly GoogleAdsOptions _options;
        private readonly ILogger<GoogleAdsCostService> _logger;

        public GoogleAdsCostService(
            ApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IOptions<GoogleAdsOptions> options,
            ILogger<GoogleAdsCostService> logger)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_options.DeveloperToken)
            && !string.IsNullOrWhiteSpace(_options.OAuth2ClientId)
            && !string.IsNullOrWhiteSpace(_options.OAuth2ClientSecret)
            && !string.IsNullOrWhiteSpace(_options.OAuth2RefreshToken)
            && !string.IsNullOrWhiteSpace(_options.LoginCustomerId)
            && !string.IsNullOrWhiteSpace(_options.CustomerId);

        public async Task<GoogleAdsSyncResult> BackfillAsync(CancellationToken ct = default)
        {
            EnsureConfigured();

            if (!DateTime.TryParseExact(_options.BackfillStartDate, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            {
                throw new InvalidOperationException(
                    $"GoogleAds:BackfillStartDate ('{_options.BackfillStartDate}') is missing or not in yyyy-MM-dd format.");
            }

            // "Yesterday" in the ads account timezone (Eastern / America/New_York). segments.date is
            // reported in that zone, so all our day math stays in it too — no UTC conversion.
            var yesterday = NyTimeHelper.NowNy.Date.AddDays(-1);
            if (start.Date > yesterday)
                return new GoogleAdsSyncResult();

            var metrics = await QueryDailyMetricsAsync(start.Date, yesterday, ct);
            return await UpsertAsync(metrics, ct);
        }

        public async Task<GoogleAdsSyncResult> SyncRecentAsync(CancellationToken ct = default)
        {
            EnsureConfigured();

            // Trailing 7-day window (today back 7), re-queried so Google's 1–3 day cost
            // finalization corrects earlier estimates on the rows we already wrote.
            var today = NyTimeHelper.NowNy.Date;
            var from = today.AddDays(-7);

            var metrics = await QueryDailyMetricsAsync(from, today, ct);
            return await UpsertAsync(metrics, ct);
        }

        // ── Google Ads query ────────────────────────────────────────────────────────────

        // Per-day account metrics (cost, clicks, conversions) for [from, to] in the account
        // timezone. A day is included when it has any of cost/clicks/conversions > 0.
        private readonly record struct DailyMetrics(decimal CostUsd, int Clicks, decimal Conversions);

        private async Task<Dictionary<DateTime, DailyMetrics>> QueryDailyMetricsAsync(
            DateTime from, DateTime to, CancellationToken ct)
        {
            var accessToken = await GetAccessTokenAsync(ct);
            if (string.IsNullOrEmpty(accessToken))
                throw new InvalidOperationException("Could not obtain a Google Ads access token.");

            var gaql =
                "SELECT segments.date, metrics.cost_micros, metrics.clicks, metrics.conversions " +
                "FROM customer " +
                $"WHERE segments.date BETWEEN '{from:yyyy-MM-dd}' AND '{to:yyyy-MM-dd}'";

            var customerId = DigitsOnly(_options.CustomerId);
            var loginCustomerId = DigitsOnly(_options.LoginCustomerId);
            var url = $"{ApiHost}/{_options.ApiVersion}/customers/{customerId}/googleAds:searchStream";

            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("developer-token", _options.DeveloperToken);
            request.Headers.TryAddWithoutValidation("login-customer-id", loginCustomerId);
            request.Content = new StringContent(
                JsonConvert.SerializeObject(new { query = gaql }),
                System.Text.Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Google Ads searchStream failed ({Status}): {Body}", response.StatusCode, body);
                throw new InvalidOperationException($"Google Ads searchStream returned {(int)response.StatusCode}.");
            }

            return ParseDailyMetrics(body);
        }

        // searchStream returns a JSON array of stream chunks, each { "results": [ { segments.date,
        // metrics.{costMicros,clicks,conversions} } ] }. Aggregate per date (defensively summing,
        // though FROM customer already yields one row per date).
        private static Dictionary<DateTime, DailyMetrics> ParseDailyMetrics(string body)
        {
            var result = new Dictionary<DateTime, DailyMetrics>();

            var root = JToken.Parse(body);
            // A stream body is normally an array; tolerate a single object too.
            var chunks = root as JArray ?? new JArray(root);

            foreach (var chunk in chunks)
            {
                if (chunk["results"] is not JArray rows) continue;

                foreach (var row in rows)
                {
                    var dateStr = row["segments"]?["date"]?.Value<string>();
                    if (string.IsNullOrEmpty(dateStr) ||
                        !DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date))
                        continue;

                    var metrics = row["metrics"];
                    long.TryParse(metrics?["costMicros"]?.Value<string>(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var micros);
                    long.TryParse(metrics?["clicks"]?.Value<string>(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var clicks);
                    var conversions = metrics?["conversions"]?.Value<decimal>() ?? 0m;

                    if (micros <= 0 && clicks <= 0 && conversions <= 0)
                        continue;

                    var costUsd = Math.Round(micros / 1_000_000m, 2, MidpointRounding.AwayFromZero);

                    var acc = result.TryGetValue(date.Date, out var existing) ? existing : default;
                    result[date.Date] = new DailyMetrics(
                        acc.CostUsd + costUsd,
                        acc.Clicks + (int)clicks,
                        acc.Conversions + conversions);
                }
            }

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
                _logger.LogError("Google Ads token exchange failed ({Status}): {Body}", response.StatusCode, body);
                return null;
            }

            return JObject.Parse(body)["access_token"]?.Value<string>();
        }

        // ── Upsert (Expenses + daily stats) ─────────────────────────────────────────────

        private async Task<GoogleAdsSyncResult> UpsertAsync(
            Dictionary<DateTime, DailyMetrics> metricsByDate, CancellationToken ct)
        {
            var result = new GoogleAdsSyncResult();
            if (metricsByDate.Count == 0)
                return result;

            var categoryId = await GetOrCreateCategoryIdAsync(ct);
            int? systemUserId = null; // resolved lazily, only if we actually insert an expense
            var now = DateTime.UtcNow;

            foreach (var (date, m) in metricsByDate)
            {
                // Ad spend → Expenses (unchanged rule: only days with cost > 0 are written).
                if (m.CostUsd > 0)
                {
                    var key = $"googleads:{date:yyyy-MM-dd}";
                    var existing = await _context.Expenses.FirstOrDefaultAsync(e => e.SourceKey == key, ct);

                    if (existing != null)
                    {
                        if (existing.Amount != m.CostUsd)
                        {
                            existing.Amount = m.CostUsd;
                            existing.UpdatedAt = now;
                        }
                    }
                    else
                    {
                        systemUserId ??= await ResolveSystemUserIdAsync(ct);
                        _context.Expenses.Add(new Expense
                        {
                            Name = "Google Ads Spend",
                            Amount = m.CostUsd,
                            CategoryId = categoryId,
                            StartDate = date,          // account-tz calendar date, stored date-only as-is
                            IsRecurring = false,
                            SourceKey = key,
                            CreatedByUserId = systemUserId.Value,
                            CreatedAt = now,
                            UpdatedAt = now
                        });
                    }

                    result.DaysSynced++;
                    result.TotalUsd += m.CostUsd;
                }

                // Clicks/conversions → GoogleAdsDailyStat (separate table; upsert by day).
                var stat = await _context.GoogleAdsDailyStats.FirstOrDefaultAsync(s => s.Date == date, ct);
                if (stat != null)
                {
                    if (stat.Clicks != m.Clicks || stat.Conversions != m.Conversions)
                    {
                        stat.Clicks = m.Clicks;
                        stat.Conversions = m.Conversions;
                        stat.UpdatedAt = now;
                    }
                }
                else
                {
                    _context.GoogleAdsDailyStats.Add(new GoogleAdsDailyStat
                    {
                        Date = date,
                        Clicks = m.Clicks,
                        Conversions = m.Conversions,
                        UpdatedAt = now
                    });
                }
            }

            await _context.SaveChangesAsync(ct);
            return result;
        }

        // Get-or-create the "Google Ads" category (case-insensitive). Mirrors the PK/DisplayOrder
        // assignment in ExpenseService.CreateCategoryAsync (PK is ValueGeneratedNever).
        private async Task<int> GetOrCreateCategoryIdAsync(CancellationToken ct)
        {
            var existing = await _context.ExpenseCategories
                .FirstOrDefaultAsync(c => c.Name.ToLower() == CategoryName.ToLower(), ct);
            if (existing != null)
                return existing.Id;

            var maxId = await _context.ExpenseCategories.MaxAsync(c => (int?)c.Id, ct) ?? -1;
            var maxOrder = await _context.ExpenseCategories.MaxAsync(c => (int?)c.DisplayOrder, ct) ?? -1;

            var category = new ExpenseCategory
            {
                Id = maxId + 1,
                Name = CategoryName,
                DisplayOrder = maxOrder + 1,
                IsSystem = false,
                CreatedAt = DateTime.UtcNow
            };
            _context.ExpenseCategories.Add(category);
            await _context.SaveChangesAsync(ct);
            return category.Id;
        }

        // Synced expenses still need a valid CreatedByUserId (non-null FK). Attribute them to the
        // lowest-Id SuperAdmin, then Admin, then any user as a last resort.
        private async Task<int> ResolveSystemUserIdAsync(CancellationToken ct)
        {
            var id = await _context.Users
                .Where(u => u.Role == UserRole.SuperAdmin)
                .OrderBy(u => u.Id)
                .Select(u => (int?)u.Id)
                .FirstOrDefaultAsync(ct)
                ?? await _context.Users
                    .Where(u => u.Role == UserRole.Admin)
                    .OrderBy(u => u.Id)
                    .Select(u => (int?)u.Id)
                    .FirstOrDefaultAsync(ct)
                ?? await _context.Users
                    .OrderBy(u => u.Id)
                    .Select(u => (int?)u.Id)
                    .FirstOrDefaultAsync(ct);

            if (id == null)
                throw new InvalidOperationException("No user exists to attribute Google Ads expenses to.");
            return id.Value;
        }

        private void EnsureConfigured()
        {
            if (!IsConfigured)
                throw new InvalidOperationException("Google Ads sync is not configured (missing credentials in the GoogleAds section).");
        }

        private static string DigitsOnly(string? value) =>
            new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
    }
}
