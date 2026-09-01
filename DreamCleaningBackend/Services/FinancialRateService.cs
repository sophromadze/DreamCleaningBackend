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

        // How often the ongoing month's auto FX/bonus snapshot is refreshed.
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);

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

        // Returns (usdPerGel, source). source is "auto" on success or "fallback" if the API call
        // fails — callers decide whether to overwrite an existing value with a fallback.
        private async Task<(decimal usdPerGel, string source)> FetchFxForMonthAsync(int year, int month)
        {
            var date = RepresentativeDate(year, month, DateTime.UtcNow);
            var fallback = _configuration.GetValue<decimal>("ExchangeRates:DefaultUsdPerGel", 0.37m);

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
                                return (decimal.Round(usdPerGel, 6), "auto");
                            }
                        }
                    }
                }

                _logger.LogWarning("NBG FX response had no USD rate for {Year}-{Month}; using fallback.", year, month);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch NBG FX rate for {Year}-{Month}; using fallback {Fallback}.", year, month, fallback);
            }

            return (fallback, "fallback");
        }
    }
}
