using ClosedXML.Excel;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace DreamCleaningBackend.Controllers.Crm
{
    /// <summary>
    /// CRM "Ads" tab: per-day Google Ads performance next to real booked orders, so the owner can
    /// see how ad clicks turn into actual jobs. Reads three existing sources and merges them by day
    /// (no new aggregation logic): ad spend from Expenses ("googleads:" SourceKey), clicks +
    /// Google-reported conversions from GoogleAdsDailyStats, and booked orders from the Orders table.
    /// </summary>
    [Route("api/crm/ads")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class CrmAdsController : ControllerBase
    {
        private const int DefaultPageSize = 20;
        private const int MaxPageSize = 200;

        private readonly ApplicationDbContext _context;

        public CrmAdsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET api/crm/ads/daily?period=last30|week|month|year|all&from=&to=&page=&pageSize=
        // Totals cover the WHOLE resolved range; items are the requested page (newest first).
        [HttpGet("daily")]
        public async Task<ActionResult<CrmAdsDailyResponse>> GetDaily(
            [FromQuery] string? period, [FromQuery] string? from, [FromQuery] string? to,
            [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize)
        {
            var (fromDate, toDate) = await ResolveRangeAsync(period, from, to);
            var (items, totals) = await BuildDailyAsync(fromDate, toDate);

            if (pageSize < 1) pageSize = DefaultPageSize;
            if (pageSize > MaxPageSize) pageSize = MaxPageSize;
            var totalCount = items.Count;
            var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
            if (page < 1) page = 1;
            if (totalPages > 0 && page > totalPages) page = totalPages;

            var paged = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return Ok(new CrmAdsDailyResponse
            {
                From = fromDate,
                To = toDate,
                Items = paged,
                Totals = totals,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = totalPages
            });
        }

        // GET api/crm/ads/export?period=last30|week|month|year|all&from=&to=
        // Exports the WHOLE resolved range (no paging): sheet 1 = every day, sheet 2 = summary.
        [HttpGet("export")]
        public async Task<IActionResult> Export(
            [FromQuery] string? period, [FromQuery] string? from, [FromQuery] string? to)
        {
            var (fromDate, toDate) = await ResolveRangeAsync(period, from, to);
            var (items, totals) = await BuildDailyAsync(fromDate, toDate);

            using var workbook = new XLWorkbook();
            BuildDailySheet(workbook, items);
            BuildSummarySheet(workbook, fromDate, toDate, totals);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var bytes = stream.ToArray();

            var fileName = $"dream-cleaning-ads_{fromDate:yyyy-MM-dd}_{toDate:yyyy-MM-dd}.xlsx";
            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }

        // ── Range resolution ────────────────────────────────────────────────────────────

        // Presets are computed in the account timezone (NY) so they line up with the ad dates.
        // "all" starts at the earliest day we have any ad data for (backfill start), bounding the
        // Orders scan. Anything else (or explicit from/to) is treated as a custom range.
        private async Task<(DateTime from, DateTime to)> ResolveRangeAsync(string? period, string? from, string? to)
        {
            var today = NyTimeHelper.NowNy.Date;
            switch ((period ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "last30":
                    return (today.AddDays(-29), today); // rolling 30 days, inclusive of today
                case "week":
                    return (today.AddDays(-(int)today.DayOfWeek), today); // Sunday-start week
                case "month":
                    return (new DateTime(today.Year, today.Month, 1), today);
                case "year":
                    return (new DateTime(today.Year, 1, 1), today);
                case "all":
                    return (await EarliestAdDateAsync() ?? today, today);
                default:
                    var toDate = ParseDate(to) ?? today;
                    // No explicit range ⇒ default to the last 30 days.
                    var fromDate = ParseDate(from) ?? today.AddDays(-29);
                    if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);
                    return (fromDate, toDate);
            }
        }

        private async Task<DateTime?> EarliestAdDateAsync()
        {
            var minStat = await _context.GoogleAdsDailyStats.MinAsync(s => (DateTime?)s.Date);
            var minExpense = await _context.Expenses
                .Where(e => e.SourceKey != null && e.SourceKey.StartsWith("googleads:"))
                .MinAsync(e => (DateTime?)e.StartDate);

            if (minStat == null) return minExpense?.Date;
            if (minExpense == null) return minStat?.Date;
            return (minStat < minExpense ? minStat : minExpense)?.Date;
        }

        // ── Merge (spend + clicks/conversions + booked orders) ──────────────────────────

        private async Task<(List<CrmAdsDailyDto> items, CrmAdsTotalsDto totals)> BuildDailyAsync(
            DateTime fromDate, DateTime toDate)
        {
            // Ad spend per day (single source of truth: Expenses, keyed by SourceKey).
            var spendByDate = (await _context.Expenses
                    .Where(e => e.SourceKey != null && e.SourceKey.StartsWith("googleads:")
                                && e.StartDate >= fromDate && e.StartDate <= toDate)
                    .Select(e => new { e.StartDate, e.Amount })
                    .ToListAsync())
                .GroupBy(e => e.StartDate.Date)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

            // Clicks + Google-reported conversions per day.
            var statByDate = (await _context.GoogleAdsDailyStats
                    .Where(s => s.Date >= fromDate && s.Date <= toDate)
                    .ToListAsync())
                .ToDictionary(s => s.Date.Date, s => s);

            // Real booked orders per day. Order.CreatedAt is UTC; bucket it into NY calendar days
            // so it lines up with the Eastern ad dates. Cancelled orders don't count as booked.
            var fromUtc = NyTimeHelper.ToUtc(fromDate);
            var toUtcExclusive = NyTimeHelper.ToUtc(toDate.AddDays(1));
            var bookedByDate = (await _context.Orders
                    .Where(o => o.CreatedAt >= fromUtc && o.CreatedAt < toUtcExclusive
                                && o.Status.ToLower() != "cancelled")
                    .Select(o => o.CreatedAt)
                    .ToListAsync())
                .GroupBy(createdUtc => NyTimeHelper.ToNy(createdUtc).Date)
                .ToDictionary(g => g.Key, g => g.Count());

            // Union of every day that has any signal, newest first.
            var days = spendByDate.Keys
                .Concat(statByDate.Keys)
                .Concat(bookedByDate.Keys)
                .Distinct()
                .OrderByDescending(d => d)
                .ToList();

            var items = new List<CrmAdsDailyDto>();
            foreach (var day in days)
            {
                var spend = spendByDate.TryGetValue(day, out var sp) ? sp : 0m;
                var clicks = statByDate.TryGetValue(day, out var st) ? st.Clicks : 0;
                var googleConv = st != null ? st.Conversions : 0m;
                var booked = bookedByDate.TryGetValue(day, out var bk) ? bk : 0;

                items.Add(new CrmAdsDailyDto
                {
                    Date = day,
                    AdSpend = spend,
                    Clicks = clicks,
                    GoogleConversions = googleConv,
                    BookedOrders = booked,
                    // Conversion rate = conversions ÷ clicks, as a percent (0 clicks ⇒ 0).
                    GoogleConversionRate = clicks > 0 ? Math.Round(googleConv / clicks * 100m, 1) : 0m,
                    BookedConversionRate = clicks > 0 ? Math.Round((decimal)booked / clicks * 100m, 1) : 0m
                });
            }

            var totals = new CrmAdsTotalsDto
            {
                AdSpend = items.Sum(i => i.AdSpend),
                Clicks = items.Sum(i => i.Clicks),
                GoogleConversions = items.Sum(i => i.GoogleConversions),
                BookedOrders = items.Sum(i => i.BookedOrders)
            };
            totals.GoogleConversionRate = totals.Clicks > 0
                ? Math.Round(totals.GoogleConversions / totals.Clicks * 100m, 1) : 0m;
            totals.BookedConversionRate = totals.Clicks > 0
                ? Math.Round((decimal)totals.BookedOrders / totals.Clicks * 100m, 1) : 0m;

            return (items, totals);
        }

        // ── Excel ───────────────────────────────────────────────────────────────────────

        private static void BuildDailySheet(XLWorkbook workbook, List<CrmAdsDailyDto> items)
        {
            var ws = workbook.AddWorksheet("Daily");
            string[] headers =
            {
                "Date", "Ad spend", "Clicks", "Google conversions",
                "Google conv rate (%)", "Booked orders", "Booked conv rate (%)"
            };
            for (var i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];

            var header = ws.Range(1, 1, 1, headers.Length);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
            header.Style.Font.FontColor = XLColor.White;

            var row = 2;
            foreach (var it in items)
            {
                ws.Cell(row, 1).Value = it.Date.ToString("yyyy-MM-dd");
                ws.Cell(row, 2).Value = (double)it.AdSpend;
                ws.Cell(row, 3).Value = it.Clicks;
                ws.Cell(row, 4).Value = (double)it.GoogleConversions;
                ws.Cell(row, 5).Value = (double)it.GoogleConversionRate;
                ws.Cell(row, 6).Value = it.BookedOrders;
                ws.Cell(row, 7).Value = (double)it.BookedConversionRate;
                row++;
            }

            ws.Column(2).Style.NumberFormat.Format = "$#,##0.00";
            ws.Columns().AdjustToContents();
        }

        private static void BuildSummarySheet(
            XLWorkbook workbook, DateTime fromDate, DateTime toDate, CrmAdsTotalsDto totals)
        {
            var ws = workbook.AddWorksheet("Summary");

            ws.Cell(1, 1).Value = "Metric";
            ws.Cell(1, 2).Value = "Value";
            var header = ws.Range(1, 1, 1, 2);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
            header.Style.Font.FontColor = XLColor.White;

            var costPerBooked = totals.BookedOrders > 0 ? totals.AdSpend / totals.BookedOrders : 0m;
            var costPerClick = totals.Clicks > 0 ? totals.AdSpend / totals.Clicks : 0m;

            var rows = new (string Label, object Value)[]
            {
                ("Date range", $"{fromDate:yyyy-MM-dd} → {toDate:yyyy-MM-dd}"),
                ("Total ad spend", (double)totals.AdSpend),
                ("Total clicks", totals.Clicks),
                ("Total Google conversions", (double)totals.GoogleConversions),
                ("Total booked orders", totals.BookedOrders),
                ("Google conversion rate (%)", (double)totals.GoogleConversionRate),
                ("Booked conversion rate (%)", (double)totals.BookedConversionRate),
                ("Cost per booked order", (double)Math.Round(costPerBooked, 2, MidpointRounding.AwayFromZero)),
                ("Cost per click", (double)Math.Round(costPerClick, 2, MidpointRounding.AwayFromZero))
            };

            var row = 2;
            foreach (var (label, value) in rows)
            {
                ws.Cell(row, 1).Value = label;
                switch (value)
                {
                    case double d: ws.Cell(row, 2).Value = d; break;
                    case int n: ws.Cell(row, 2).Value = n; break;
                    default: ws.Cell(row, 2).Value = value.ToString(); break;
                }
                row++;
            }

            ws.Columns().AdjustToContents();
        }

        private static DateTime? ParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            // Accept a plain yyyy-MM-dd or a full ISO timestamp (take the date part).
            if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d))
                return d.Date;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var any) ? any.Date : null;
        }
    }

    public class CrmAdsDailyResponse
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public List<CrmAdsDailyDto> Items { get; set; } = new();
        public CrmAdsTotalsDto Totals { get; set; } = new();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
    }

    public class CrmAdsDailyDto
    {
        public DateTime Date { get; set; }
        public decimal AdSpend { get; set; }
        public int Clicks { get; set; }
        public decimal GoogleConversions { get; set; }
        public int BookedOrders { get; set; }
        public decimal GoogleConversionRate { get; set; }
        public decimal BookedConversionRate { get; set; }
    }

    public class CrmAdsTotalsDto
    {
        public decimal AdSpend { get; set; }
        public int Clicks { get; set; }
        public decimal GoogleConversions { get; set; }
        public int BookedOrders { get; set; }
        public decimal GoogleConversionRate { get; set; }
        public decimal BookedConversionRate { get; set; }
    }
}
