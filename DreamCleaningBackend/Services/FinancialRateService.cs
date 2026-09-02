using System.Net.Sockets;
using System.Text.Json;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class FinancialRateService : IFinancialRateService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<FinancialRateService> _logger;
        private readonly IAdminBonusService _adminBonusService;

        // Shared client. IPv4 is forced because the production VPS has IPv6 disabled — outbound
        // HTTP over IPv6 hangs (see CLAUDE.md operational quirk #2 / TelegramBotService).
        private static readonly HttpClient _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // How often the ongoing month's FX rate is re-fetched. Refreshed lazily, on read, so this
        // is an upper bound on staleness rather than a timer. One hour rather than the original
        // twelve: the figure on screen is checked against a bank or a browser, and half a day of
        // drift is exactly what makes somebody stop trusting it. A closed month never refreshes at
        // all, and a manually pinned rate is never touched.
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

        public FinancialRateService(
            ApplicationDbContext context,
            IConfiguration configuration,
            ILogger<FinancialRateService> logger,
            IAdminBonusService adminBonusService)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
            _adminBonusService = adminBonusService;
        }

        public async Task<MonthlyFinancialSnapshot> GetOrCreateAsync(int year, int month)
        {
            var now = DateTime.UtcNow;
            var isPast = IsMonthInPast(year, month, now);

            var snap = await _context.MonthlyFinancialSnapshots
                .FirstOrDefaultAsync(s => s.Year == year && s.Month == month);

            if (snap == null)
            {
                var (fx, source) = await FetchFxForMonthAsync(year, month);

                snap = new MonthlyFinancialSnapshot
                {
                    Year = year,
                    Month = month,
                    UsdPerGel = fx,
                    FxSource = source,
                    IsFinalized = isPast,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _context.MonthlyFinancialSnapshots.Add(snap);
                await _context.SaveChangesAsync();
                return snap;
            }

            if (snap.IsFinalized)
                return snap;

            // The month has rolled into the past since the row was created — freeze it as-is.
            if (isPast)
            {
                snap.IsFinalized = true;
                snap.UpdatedAt = now;
                await _context.SaveChangesAsync();
                return snap;
            }

            // Ongoing month: periodically refresh the FX rate unless a SuperAdmin pinned it
            // manually. Bonus figures are not snapshotted at all any more — they are computed live
            // from the current rates, so there is nothing here to keep in step.
            if (now - snap.UpdatedAt >= RefreshInterval)
            {
                if (snap.FxSource != "manual")
                {
                    var (fx, source) = await FetchFxForMonthAsync(year, month);
                    // Don't clobber a good auto value with a fallback when the API is briefly down.
                    if (source != "fallback" || snap.FxSource == "fallback")
                    {
                        snap.UsdPerGel = fx;
                        snap.FxSource = source;
                    }
                }

                snap.UpdatedAt = now;
                await _context.SaveChangesAsync();
            }

            return snap;
        }

        public async Task<Dictionary<int, MonthlyFinancialSnapshot>> GetOrCreateForRangeAsync(
            DateTime fromInclusive, DateTime toExclusive)
        {
            var result = new Dictionary<int, MonthlyFinancialSnapshot>();
            if (toExclusive <= fromInclusive)
                return result;

            // Walk month-by-month from the first to the last month touched by the window.
            var cursor = new DateTime(fromInclusive.Year, fromInclusive.Month, 1);
            // toExclusive is exclusive, so the last day actually included is toExclusive - 1 day.
            var lastIncluded = toExclusive.Date.AddDays(-1);
            var end = new DateTime(lastIncluded.Year, lastIncluded.Month, 1);

            while (cursor <= end)
            {
                var snap = await GetOrCreateAsync(cursor.Year, cursor.Month);
                result[cursor.Year * 100 + cursor.Month] = snap;
                cursor = cursor.AddMonths(1);
            }

            return result;
        }

        public async Task<MonthlyFinancialRateDto> SetManualFxAsync(int year, int month, decimal usdPerGel, int byUserId)
        {
            if (usdPerGel <= 0)
                throw new InvalidOperationException("Exchange rate must be greater than zero.");

            var snap = await GetOrCreateAsync(year, month);
            snap.UsdPerGel = usdPerGel;
            snap.FxSource = "manual";
            snap.UpdatedByUserId = byUserId;
            snap.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return ToDto(snap, await ResolveUserNameAsync(byUserId), await BonusTotalGelForMonthAsync(year, month));
        }

        public async Task<MonthlyFinancialRateDto> RefetchAsync(int year, int month, int byUserId)
        {
            var snap = await GetOrCreateAsync(year, month);
            var (fx, source) = await FetchFxForMonthAsync(year, month);
            snap.UsdPerGel = fx;
            snap.FxSource = source;
            snap.UpdatedByUserId = byUserId;
            snap.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return ToDto(snap, await ResolveUserNameAsync(byUserId), await BonusTotalGelForMonthAsync(year, month));
        }

        public async Task<List<MonthlyFinancialRateDto>> ListAsync(DateTime fromInclusive, DateTime toExclusive)
        {
            var map = await GetOrCreateForRangeAsync(fromInclusive, toExclusive);
            var ids = map.Values.Where(s => s.UpdatedByUserId.HasValue).Select(s => s.UpdatedByUserId!.Value).Distinct().ToList();
            var names = await _context.Users
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, Name = u.FirstName + " " + u.LastName })
                .ToDictionaryAsync(x => x.Id, x => x.Name);

            // One pass over the whole window rather than a query per month — a 12-month listing is
            // the default view of this table.
            var bonusTotals = await BonusTotalsGelByMonthAsync(fromInclusive, toExclusive);

            return map.Values
                .OrderByDescending(s => s.Year).ThenByDescending(s => s.Month)
                .Select(s => ToDto(
                    s,
                    s.UpdatedByUserId.HasValue && names.TryGetValue(s.UpdatedByUserId.Value, out var n) ? n : null,
                    bonusTotals.TryGetValue(s.Year * 100 + s.Month, out var gel) ? gel : 0m))
                .ToList();
        }

        public MonthlyFinancialRateDto ToDto(
            MonthlyFinancialSnapshot snap,
            string? updatedByUserName = null,
            decimal adminBonusTotalGel = 0m) => new()
        {
            Year = snap.Year,
            Month = snap.Month,
            MonthKey = $"{snap.Year:D4}-{snap.Month:D2}",
            UsdPerGel = snap.UsdPerGel,
            AdminBonusTotalGel = adminBonusTotalGel,
            FxSource = snap.FxSource,
            IsFinalized = snap.IsFinalized,
            UpdatedAt = snap.UpdatedAt,
            UpdatedByUserName = updatedByUserName
        };

        /// <summary>Staff bonuses owed for one month, in GEL, at today's rates.</summary>
        public async Task<decimal> BonusTotalGelForMonthAsync(int year, int month)
        {
            var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var totals = await BonusTotalsGelByMonthAsync(start, start.AddMonths(1));
            return totals.TryGetValue(year * 100 + month, out var gel) ? gel : 0m;
        }

        /// <summary>
        /// Staff bonuses owed per month across a window, keyed year*100+month. Both sides of every
        /// order (administrator + manager) are already inside the per-order figures — see
        /// IAdminBonusService.GetOrderBonusCostsGelAsync — so nothing here may add a manager share
        /// on top.
        /// </summary>
        private async Task<Dictionary<int, decimal>> BonusTotalsGelByMonthAsync(DateTime fromInclusive, DateTime toExclusive)
        {
            var costs = await _adminBonusService.GetOrderBonusCostsGelAsync(fromInclusive, toExclusive);
            if (costs.Count == 0)
                return new Dictionary<int, decimal>();

            var months = await _context.Orders
                .Where(o => costs.Keys.Contains(o.Id))
                .Select(o => new { o.Id, o.ServiceDate })
                .ToListAsync();

            var totals = new Dictionary<int, decimal>();
            foreach (var m in months)
            {
                var key = m.ServiceDate.Year * 100 + m.ServiceDate.Month;
                totals[key] = totals.TryGetValue(key, out var running) ? running + costs[m.Id] : costs[m.Id];
            }
            return totals;
        }

        // ──────────────────────────────────────────────────────────────────────────────

        private static bool IsMonthInPast(int year, int month, DateTime nowUtc)
        {
            return year < nowUtc.Year || (year == nowUtc.Year && month < nowUtc.Month);
        }


        private async Task<string?> ResolveUserNameAsync(int userId)
        {
            return await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => u.FirstName + " " + u.LastName)
                .FirstOrDefaultAsync();
        }

        // Representative date for an FX lookup: month-end for a closed month, today for the
        // ongoing month (clamped so we never ask the API for a future date).
        private static DateTime RepresentativeDate(int year, int month, DateTime nowUtc)
        {
            var lastDay = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            var today = nowUtc.Date;
            return lastDay <= today ? lastDay : today;
        }

        // Returns (usdPerGel, source). source is "market" or "auto" on success, or "fallback" if
        // every lookup failed — callers decide whether to overwrite an existing value with a
        // fallback.
        //
        // TWO sources, in this order, and the order is the point:
        //
        //   1. The MARKET mid-rate, for a month that is still running. This is the rate a bank or
        //      Google quotes, and it is what the owner checks the page against.
        //   2. The National Bank of Georgia OFFICIAL rate, for a month that has closed — it is the
        //      only one of the two that answers for a historical date, and a closed month is
        //      frozen anyway.
        //
        // NBG used to be the only source and drifted from the market by roughly half a percent —
        // enough that 2,100 GEL read as $800 here and $804.70 in a browser, which is the kind of
        // gap that makes somebody stop trusting the whole page. NBG stays as the fallback because
        // it is authoritative, historical and has never been down.
        private async Task<(decimal usdPerGel, string source)> FetchFxForMonthAsync(int year, int month)
        {
            var now = DateTime.UtcNow;
            var fallback = _configuration.GetValue<decimal>("ExchangeRates:DefaultUsdPerGel", 0.37m);

            // A month still in progress takes today's market rate; a closed month wants the rate
            // that was actually in force, which only NBG can answer for.
            if (!IsMonthInPast(year, month, now))
            {
                var market = await FetchMarketRateAsync();
                if (market.HasValue)
                    return (market.Value, "market");
            }

            var nbg = await FetchNbgRateAsync(year, month);
            if (nbg.HasValue)
                return (nbg.Value, "auto");

            return (fallback, "fallback");
        }

        /// <summary>
        /// Today's market mid-rate for GEL → USD, from the first source that answers. Null when
        /// they all fail, so the caller falls through to NBG rather than treating an outage as a
        /// rate.
        /// </summary>
        /// <remarks>
        /// Two market sources, live one first. Neither needs an API key, and both quote GEL→USD
        /// directly so there is no cross-rate to compute. The second exists because the first is
        /// an undocumented endpoint that can be blocked or throttled without notice — losing the
        /// live rate should cost a few hours of freshness, not drop the whole page back to the
        /// official rate it was moved off.
        /// </remarks>
        private async Task<decimal?> FetchMarketRateAsync()
        {
            var live = await TryFetchAsync("live market", async () =>
            {
                // Intraday, and the closest of the free sources to the figure a browser shows.
                var req = new HttpRequestMessage(HttpMethod.Get,
                    "https://query1.finance.yahoo.com/v8/finance/chart/GELUSD=X?interval=1d&range=1d");
                // Refused without one.
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; DreamCleaning/1.0)");

                using var resp = await _http.SendAsync(req);
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var meta = doc.RootElement
                    .GetProperty("chart").GetProperty("result")[0]
                    .GetProperty("meta");
                return meta.GetProperty("regularMarketPrice").GetDecimal();
            });
            if (live.HasValue) return live;

            return await TryFetchAsync("daily market", async () =>
            {
                using var resp = await _http.GetAsync("https://open.er-api.com/v6/latest/GEL");
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;

                // Answers 200 with {"result":"error"} on a bad request, so the body has to be
                // checked rather than just the status code.
                if (root.TryGetProperty("result", out var result) && result.GetString() != "success")
                    return null;

                return root.GetProperty("rates").GetProperty("USD").GetDecimal();
            });
        }

        /// <summary>
        /// Runs one FX lookup, keeps it from ever throwing, and sanity-checks what it returns.
        /// </summary>
        private async Task<decimal?> TryFetchAsync(string sourceName, Func<Task<decimal?>> fetch)
        {
            try
            {
                var rate = await fetch();
                if (!rate.HasValue) return null;

                // A GEL is worth well under a dollar and well over a cent. A figure outside that
                // means the shape of a response changed, and restating every cost at it would be
                // far worse than falling through to the next source.
                if (rate.Value > 0.05m && rate.Value < 5m)
                    return decimal.Round(rate.Value, 6);

                _logger.LogWarning(
                    "{Source} FX returned an implausible GEL→USD rate ({Rate}); falling through.",
                    sourceName, rate.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Source} FX lookup failed; falling through.", sourceName);
            }

            return null;
        }

        /// <summary>The National Bank of Georgia's official rate. Answers for historical dates.</summary>
        private async Task<decimal?> FetchNbgRateAsync(int year, int month)
        {
            var date = RepresentativeDate(year, month, DateTime.UtcNow);

            try
            {
                // National Bank of Georgia official rates. Supports historical dates, so each month
                // gets the rate that was actually in effect. Returns GEL per `quantity` USD.
                var url = $"https://nbg.gov.ge/gw/api/ct/monetarypolicy/currencies/en/json/?currencies=USD&date={date:yyyy-MM-dd}";
                using var resp = await _http.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    var currencies = root[0].GetProperty("currencies");
                    foreach (var c in currencies.EnumerateArray())
                    {
                        if (c.GetProperty("code").GetString() == "USD")
                        {
                            var rate = c.GetProperty("rate").GetDecimal();      // GEL per `quantity` USD
                            var quantity = c.TryGetProperty("quantity", out var q) ? q.GetInt32() : 1;
                            if (rate > 0)
                            {
                                var usdPerGel = quantity / rate;
                                return decimal.Round(usdPerGel, 6);
                            }
                        }
                    }
                }

                _logger.LogWarning("NBG FX response had no USD rate for {Year}-{Month}.", year, month);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch NBG FX rate for {Year}-{Month}.", year, month);
            }

            return null;
        }
    }
}
