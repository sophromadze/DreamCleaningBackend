using System.Net;
using System.Net.Http.Headers;
using DreamCleaningBackend.Configuration;
using DreamCleaningBackend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamCleaningBackend.Services
{
    public class Ga4BackfillResult
    {
        public string DimensionSetUsed { get; set; } = "";
        public string DateRange { get; set; } = "";
        public int Ga4Rows { get; set; }
        public int DistinctTransactions { get; set; }
        public int TransactionsMatchedToOrder { get; set; }
        public int OrdersUpdated { get; set; }
        public int OrdersAlreadyAttributed { get; set; }
        public int TransactionsWithNoOrder { get; set; }
        public Dictionary<string, int> UpdatedByChannel { get; set; } = new();
    }

    /// <summary>Which attribution the backfill writes: first-touch (Acquisition* columns, GA4
    /// firstUser* dims) or converting-session (Converting* columns, GA4 session* dims).</summary>
    public enum Ga4BackfillTarget { FirstTouch, Converting }

    public interface IGa4AttributionBackfillService
    {
        bool IsConfigured { get; }
        Task<Ga4BackfillResult> RunAsync(Ga4BackfillTarget target = Ga4BackfillTarget.FirstTouch, CancellationToken ct = default);
    }

    /// <summary>
    /// ONE-TIME backfill of historical <see cref="Models.Order"/> acquisition attribution from GA4.
    /// Our GA4 purchase event sets <c>transaction_id = Order.Id</c>, so we read purchases from the
    /// Analytics Data API (runReport) and copy the first-touch channel/source/medium/campaign onto
    /// any order whose AcquisitionChannel is still NULL — never overwriting a value the live
    /// cookie-based capture already wrote (and never touching admin "Phone/Unknown" orders, which
    /// are non-null). NOT a recurring job; triggered manually via the SuperAdmin endpoint and safe
    /// to remove once the backfill has been run.
    ///
    /// Transport mirrors GoogleAdsCostService: refresh-token → access-token, REST over a named
    /// IPv4 HttpClient (the VPS has IPv6 disabled).
    /// </summary>
    public class Ga4AttributionBackfillService : IGa4AttributionBackfillService
    {
        public const string HttpClientName = "Ga4Ipv4";

        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string ApiHost = "https://analyticsdata.googleapis.com";
        private const int PageSize = 100_000; // Data API max rows per runReport page
        private const int OrderIdBatchSize = 500;

        private readonly ApplicationDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Ga4Options _options;
        private readonly ILogger<Ga4AttributionBackfillService> _logger;

        public Ga4AttributionBackfillService(
            ApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IOptions<Ga4Options> options,
            ILogger<Ga4AttributionBackfillService> logger)
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

        // ── Dimension candidates ─────────────────────────────────────────────────────────
        // transactionId is event-scoped; the channel/source/medium/campaign dims are user- or
        // session-scoped. For FirstTouch we PREFER firstUser* (matches our live first-touch cookie
        // semantics) and fall back to session* only if GA4 rejects the combination as incompatible.
        // For Converting we query session* only (that IS the converting-session channel). Within each
        // scope we step down (drop campaign, then medium, then source) so a single problematic
        // dimension doesn't sink the whole pull. Channel is always kept.

        private enum Role { Txn, Channel, Source, Medium, Campaign }

        private sealed record Dim(Role Role, string Name);

        private sealed record DimSet(string Label, List<Dim> Dims);

        private static List<DimSet> BuildCandidates(Ga4BackfillTarget target)
        {
            // Converting is session-scoped by definition; first-touch prefers firstUser then falls back.
            var scopes = target == Ga4BackfillTarget.Converting
                ? new[] { "session" }
                : new[] { "firstUser", "session" };

            var sets = new List<DimSet>();
            foreach (var scope in scopes)
            {
                Dim Txn() => new(Role.Txn, "transactionId");
                Dim Channel() => new(Role.Channel, $"{scope}DefaultChannelGroup");
                Dim Source() => new(Role.Source, $"{scope}Source");
                Dim Medium() => new(Role.Medium, $"{scope}Medium");
                Dim Campaign() => new(Role.Campaign, $"{scope}CampaignName");

                sets.Add(new($"{scope}: channel+source+medium+campaign",
                    new() { Txn(), Channel(), Source(), Medium(), Campaign() }));
                sets.Add(new($"{scope}: channel+source+medium",
                    new() { Txn(), Channel(), Source(), Medium() }));
                sets.Add(new($"{scope}: channel+source",
                    new() { Txn(), Channel(), Source() }));
                sets.Add(new($"{scope}: channel only",
                    new() { Txn(), Channel() }));
            }
            return sets;
        }

        // ── Run ──────────────────────────────────────────────────────────────────────────

        public async Task<Ga4BackfillResult> RunAsync(Ga4BackfillTarget target = Ga4BackfillTarget.FirstTouch, CancellationToken ct = default)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("GA4 backfill is not configured (missing credentials in the Ga4 section).");

            var accessToken = await GetAccessTokenAsync(ct)
                ?? throw new InvalidOperationException("Could not obtain a GA4 access token (check the analytics.readonly refresh token).");

            var startDate = string.IsNullOrWhiteSpace(_options.StartDate) ? "2023-01-01" : _options.StartDate.Trim();
            const string endDate = "today";

            // Walk the candidate ladder until one dimension set is accepted by the API.
            var candidates = BuildCandidates(target);
            List<JObject>? rows = null;
            DimSet? used = null;
            string? lastError = null;

            foreach (var candidate in candidates)
            {
                var (ok, fetched, error, incompatible) =
                    await TryFetchAllRowsAsync(accessToken, candidate, startDate, endDate, ct);
                if (ok)
                {
                    rows = fetched;
                    used = candidate;
                    break;
                }

                lastError = error;
                // A hard error (auth, disabled API, bad property) is not a reason to keep walking the
                // ladder — only advance when GA4 specifically reports a dimension incompatibility.
                if (!incompatible)
                    throw new InvalidOperationException($"GA4 runReport failed: {error}");

                _logger.LogWarning("GA4 dimension set '{Set}' was incompatible; trying the next fallback.", candidate.Label);
            }

            if (rows == null || used == null)
                throw new InvalidOperationException($"GA4 runReport failed for every dimension combination. Last error: {lastError}");

            _logger.LogInformation("GA4 backfill ({Target}): using dimension set '{Set}' over {Range}.",
                target, used.Label, $"{startDate}→{endDate}");

            return await ApplyRowsAsync(rows, used, $"{startDate}→{endDate}", target, ct);
        }

        // ── GA4 fetch (paged) ────────────────────────────────────────────────────────────

        private async Task<(bool ok, List<JObject>? rows, string? error, bool incompatible)> TryFetchAllRowsAsync(
            string accessToken, DimSet set, string startDate, string endDate, CancellationToken ct)
        {
            var all = new List<JObject>();
            var offset = 0;

            while (true)
            {
                var (status, body) = await PostRunReportAsync(accessToken, set, startDate, endDate, offset, ct);

                if (status != HttpStatusCode.OK)
                {
                    // GA4 reports incompatible dimension combos as 400 INVALID_ARGUMENT mentioning
                    // "compatib"/"incompatible" — the only case we treat as retry-with-fewer-dims.
                    var incompatible = status == HttpStatusCode.BadRequest &&
                        (body.Contains("incompat", StringComparison.OrdinalIgnoreCase) ||
                         body.Contains("compatib", StringComparison.OrdinalIgnoreCase));
                    return (false, null, $"{(int)status}: {Truncate(body, 600)}", incompatible);
                }

                var root = JObject.Parse(body);
                if (root["rows"] is JArray pageRows)
                    all.AddRange(pageRows.OfType<JObject>());

                var rowCount = root["rowCount"]?.Value<int>() ?? all.Count;
                var pageLen = (root["rows"] as JArray)?.Count ?? 0;

                offset += pageLen;
                if (pageLen == 0 || offset >= rowCount)
                    break;
            }

            return (true, all, null, false);
        }

        private async Task<(HttpStatusCode status, string body)> PostRunReportAsync(
            string accessToken, DimSet set, string startDate, string endDate, int offset, CancellationToken ct)
        {
            var propertyId = new string((_options.PropertyId ?? "").Where(char.IsDigit).ToArray());
            var url = $"{ApiHost}/v1beta/properties/{propertyId}:runReport";

            var requestBody = new
            {
                dateRanges = new[] { new { startDate, endDate } },
                dimensions = set.Dims.Select(d => new { name = d.Name }).ToArray(),
                // A metric is required; transactions keeps it to purchase events with a transaction_id.
                metrics = new[] { new { name = "transactions" } },
                limit = PageSize,
                offset,
                keepEmptyRows = false,
                returnPropertyQuota = false
            };

            var client = _httpClientFactory.CreateClient(HttpClientName);
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
                _logger.LogError("GA4 token exchange failed ({Status}): {Body}", response.StatusCode, body);
                return null;
            }
            return JObject.Parse(body)["access_token"]?.Value<string>();
        }

        // ── Match + update ───────────────────────────────────────────────────────────────

        private sealed record Attribution(string Channel, string? Source, string? Medium, string? Campaign);

        private async Task<Ga4BackfillResult> ApplyRowsAsync(
            List<JObject> rows, DimSet set, string dateRange, Ga4BackfillTarget target, CancellationToken ct)
        {
            var result = new Ga4BackfillResult
            {
                DimensionSetUsed = set.Label,
                DateRange = dateRange,
                Ga4Rows = rows.Count
            };

            // Fixed index of each role within this dimension set (transactionId is always index 0).
            int IndexOf(Role role) => set.Dims.FindIndex(d => d.Role == role);
            var iChannel = IndexOf(Role.Channel);
            var iSource = IndexOf(Role.Source);
            var iMedium = IndexOf(Role.Medium);
            var iCampaign = IndexOf(Role.Campaign);

            // Reduce GA4 rows to one attribution per Order id. transactionId + first-touch dims should
            // be unique per order; if a duplicate appears, first non-empty-channel row wins.
            var byOrderId = new Dictionary<int, Attribution>();
            foreach (var row in rows)
            {
                if (row["dimensionValues"] is not JArray dims || dims.Count == 0) continue;

                var txnRaw = dims[0]?["value"]?.Value<string>();
                if (!int.TryParse(txnRaw, out var orderId) || orderId <= 0) continue; // "(not set)", "(other)", non-numeric

                var channel = MapChannel(ValueAt(dims, iChannel));
                var attribution = new Attribution(
                    channel,
                    CleanGa4Value(ValueAt(dims, iSource), 200),
                    CleanGa4Value(ValueAt(dims, iMedium), 100),
                    CleanGa4Value(ValueAt(dims, iCampaign), 200));

                if (!byOrderId.ContainsKey(orderId))
                    byOrderId[orderId] = attribution;
            }

            result.DistinctTransactions = byOrderId.Count;

            // Update in id-batches. Only rows whose AcquisitionChannel is still NULL are touched.
            var ids = byOrderId.Keys.ToList();
            for (var i = 0; i < ids.Count; i += OrderIdBatchSize)
            {
                var batchIds = ids.Skip(i).Take(OrderIdBatchSize).ToList();
                var orders = await _context.Orders
                    .Where(o => batchIds.Contains(o.Id))
                    .ToListAsync(ct);

                result.TransactionsMatchedToOrder += orders.Count;

                foreach (var order in orders)
                {
                    // NULL-only on the target's channel column — never overwrite the live cookie
                    // capture (or, for first touch, an admin "Phone/Unknown").
                    var existingChannel = target == Ga4BackfillTarget.Converting
                        ? order.ConvertingChannel
                        : order.AcquisitionChannel;
                    if (existingChannel != null)
                    {
                        result.OrdersAlreadyAttributed++;
                        continue;
                    }

                    var a = byOrderId[order.Id];
                    if (target == Ga4BackfillTarget.Converting)
                    {
                        order.ConvertingChannel = a.Channel;
                        order.ConvertingSource = a.Source;
                        order.ConvertingMedium = a.Medium;
                        order.ConvertingCampaign = a.Campaign;
                    }
                    else
                    {
                        order.AcquisitionChannel = a.Channel;
                        order.AcquisitionSource = a.Source;
                        order.AcquisitionMedium = a.Medium;
                        order.AcquisitionCampaign = a.Campaign;
                    }

                    result.OrdersUpdated++;
                    result.UpdatedByChannel[a.Channel] =
                        result.UpdatedByChannel.TryGetValue(a.Channel, out var n) ? n + 1 : 1;
                }

                if (orders.Count > 0)
                    await _context.SaveChangesAsync(ct);
            }

            result.TransactionsWithNoOrder = result.DistinctTransactions - result.TransactionsMatchedToOrder;

            _logger.LogInformation(
                "GA4 backfill complete. Target={Target} Set={Set} Range={Range} Ga4Rows={Rows} DistinctTxns={Txns} " +
                "MatchedOrders={Matched} Updated={Updated} AlreadyAttributed={Already} NoOrder={NoOrder} ByChannel={ByChannel}",
                target, result.DimensionSetUsed, result.DateRange, result.Ga4Rows, result.DistinctTransactions,
                result.TransactionsMatchedToOrder, result.OrdersUpdated, result.OrdersAlreadyAttributed,
                result.TransactionsWithNoOrder, JsonConvert.SerializeObject(result.UpdatedByChannel));

            return result;
        }

        // GA4 default-channel-group value → our stored channel set. Values outside the five we track
        // (Paid Social, Email, Display, Cross-network, …) collapse to "Unassigned"; the raw
        // source/medium/campaign are still stored so the detail isn't lost.
        private static string MapChannel(string? ga4Channel)
        {
            switch ((ga4Channel ?? "").Trim())
            {
                case "Organic Search": return "Organic Search";
                case "Paid Search": return "Paid Search";
                case "Direct": return "Direct";
                case "Referral": return "Referral";
                case "Unassigned": return "Unassigned";
                default: return "Unassigned";
            }
        }

        private static string? ValueAt(JArray dims, int index)
        {
            if (index < 0 || index >= dims.Count) return null;
            return dims[index]?["value"]?.Value<string>();
        }

        // GA4 uses "(not set)" for missing values; store that as null. Meaningful placeholders like
        // "(direct)"/"(none)"/"(organic)" are kept — they mirror what our live capture stores.
        private static string? CleanGa4Value(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            if (trimmed.Equals("(not set)", StringComparison.OrdinalIgnoreCase)) return null;
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);
    }
}
