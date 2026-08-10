using ClosedXML.Excel;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace DreamCleaningBackend.Controllers.Crm
{
    /// <summary>
    /// CRM "Ads" tab: per-day Google Ads performance next to real booked orders, so the owner can
    /// see how ad clicks turn into actual jobs. Ad-only by design (2026-07 redesign) — no session
    /// traffic, no channel breakdown, no chart. Reads three existing sources and merges them by day:
    /// ad spend from Expenses ("googleads:" SourceKey), clicks + Google-reported conversions from
    /// GoogleAdsDailyStats, and booked orders from the Orders table. For each day the booked cell also
    /// carries the times of orders that came straight from an ad (first-touch channel "Paid Search").
    /// </summary>
    // Ads is a pageView-gated Company tab (2026-07): SuperAdmin always; a regular Admin only when
    // granted the "ads" page. Both endpoints are GET, which is all RequirePageView permits for
    // granted Admins. Moderators no longer have access (grants are Admin-only).
    [Route("api/crm/ads")]
    [ApiController]
    [RequirePageView(AdminViewablePages.Ads)]
    public class CrmAdsController : ControllerBase
    {
        private const int DefaultPageSize = 10;
        private const int MaxPageSize = 200;

        // The first-touch acquisition channel that means "came straight from a Google Ads click".
        private const string PaidSearchChannel = "Paid Search";

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
            var bookedRows = await FetchBookedOrdersAsync(fromDate, toDate);
            var (items, totals) = await BuildDailyAsync(fromDate, toDate, bookedRows);

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
            var bookedRows = await FetchBookedOrdersAsync(fromDate, toDate);
            var (items, totals) = await BuildDailyAsync(fromDate, toDate, bookedRows);

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
            DateTime fromDate, DateTime toDate, List<BookedOrderRow> bookedRows)
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

            // Real booked orders bucketed into NY calendar days (already fetched + NY-bucketed in
            // FetchBookedOrdersAsync so per-day + totals share one query and one definition).
            var bookedByDate = bookedRows
                .GroupBy(r => r.NyDay)
                .ToDictionary(g => g.Key, g => g.ToList());

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
                var dayRows = bookedByDate.TryGetValue(day, out var lst) ? lst : new List<BookedOrderRow>();
                var booked = dayRows.Count;

                // Times (NY, "h:mm tt") of the orders that came straight from an ad click, ascending.
                var adBookedTimes = dayRows
                    .Where(r => string.Equals(r.Channel, PaidSearchChannel, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => r.NyCreatedAt)
                    .Select(r => r.NyCreatedAt.ToString("h:mm tt", CultureInfo.InvariantCulture))
                    .ToList();

                items.Add(new CrmAdsDailyDto
                {
                    Date = day,
                    AdSpend = spend,
                    Clicks = clicks,
                    GoogleConversions = googleConv,
                    BookedOrders = booked,
                    AdBookedTimes = adBookedTimes,
                    // Conversion rate = conversions ÷ clicks, as a percent (0 clicks ⇒ 0).
                    GoogleConversionRate = clicks > 0 ? Math.Round(googleConv / clicks * 100m, 1) : 0m
                });
            }

            var totals = new CrmAdsTotalsDto
            {
                AdSpend = items.Sum(i => i.AdSpend),
                Clicks = items.Sum(i => i.Clicks),
                GoogleConversions = items.Sum(i => i.GoogleConversions),
                BookedOrders = items.Sum(i => i.BookedOrders),
                AdBookedOrders = items.Sum(i => i.AdBookedTimes.Count)
            };
            totals.GoogleConversionRate = totals.Clicks > 0
                ? Math.Round(totals.GoogleConversions / totals.Clicks * 100m, 1) : 0m;

            return (items, totals);
        }

        // ── Booked-order fetch ──────────────────────────────────────────────────────────

        // One booked order reduced to just what the Ads tab needs: its NY calendar day, the exact NY
        // booking time, and its first-touch channel (used only to pick out the "Paid Search" ad leads).
        private record BookedOrderRow(DateTime NyDay, DateTime NyCreatedAt, string Channel);

        // The SINGLE booked-order query for this tab: CreatedAt in the NY-day range, and a real
        // booking per OrderBookedFilter.IsRealBooking — which drops cancelled, fully-refunded
        // (including cancelled-then-refunded, whose Status reads "Refunded", not "Cancelled") and
        // never-paid abandoned checkouts. The old filter only excluded Status == "Cancelled", so all
        // three of those counted as booked orders and the tab reported more jobs than were ever done.
        //
        // CreatedAt is deliberate: this tab lines bookings up against the day's ad spend, so an order
        // belongs to the day it was BOOKED, not the day it is cleaned. That is why this count does not
        // have to equal the Statistics/Finances order count, which buckets by ServiceDate.
        private async Task<List<BookedOrderRow>> FetchBookedOrdersAsync(DateTime fromDate, DateTime toDate)
        {
            var fromUtc = NyTimeHelper.ToUtc(fromDate);
            var toUtcExclusive = NyTimeHelper.ToUtc(toDate.AddDays(1));

            var raw = await _context.Orders
                .Where(OrderBookedFilter.IsRealBooking)
                .Where(o => o.CreatedAt >= fromUtc && o.CreatedAt < toUtcExclusive)
                .Select(o => new { o.CreatedAt, o.AcquisitionChannel })
                .ToListAsync();

            return raw.Select(o =>
            {
                var ny = NyTimeHelper.ToNy(o.CreatedAt);
                return new BookedOrderRow(ny.Date, ny, (o.AcquisitionChannel ?? string.Empty).Trim());
            }).ToList();
        }

        // ── Excel ───────────────────────────────────────────────────────────────────────

        private static void BuildDailySheet(XLWorkbook workbook, List<CrmAdsDailyDto> items)
        {
            var ws = workbook.AddWorksheet("Daily");
            string[] headers =
            {
                "Date", "Ad spend", "Clicks", "Conversions", "Conv rate (%)",
                "Booked orders", "Booked from ad", "Ad booking times"
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
                ws.Cell(row, 7).Value = it.AdBookedTimes.Count;
                ws.Cell(row, 8).Value = string.Join(", ", it.AdBookedTimes);
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

            var costPerClick = totals.Clicks > 0 ? totals.AdSpend / totals.Clicks : 0m;
            var costPerConversion = totals.GoogleConversions > 0 ? totals.AdSpend / totals.GoogleConversions : 0m;
            var costPerBooked = totals.BookedOrders > 0 ? totals.AdSpend / totals.BookedOrders : 0m;

            var rows = new List<(string Label, object Value)>
            {
                ("Date range", $"{fromDate:yyyy-MM-dd} → {toDate:yyyy-MM-dd}"),
                ("Total ad spend", (double)totals.AdSpend),
                ("Total clicks", totals.Clicks),
                ("Total conversions", (double)totals.GoogleConversions),
                ("Conversion rate (%)", (double)totals.GoogleConversionRate),
                ("Total booked orders", totals.BookedOrders),
                ("Booked orders from ad", totals.AdBookedOrders),
                ("Cost per click", (double)Math.Round(costPerClick, 2, MidpointRounding.AwayFromZero)),
                ("Cost per conversion", (double)Math.Round(costPerConversion, 2, MidpointRounding.AwayFromZero)),
                ("Cost per booked order", (double)Math.Round(costPerBooked, 2, MidpointRounding.AwayFromZero))
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
        // NY booking times ("h:mm tt") of the orders that came straight from an ad (Paid Search).
        public List<string> AdBookedTimes { get; set; } = new();
        public decimal GoogleConversionRate { get; set; }
    }

    public class CrmAdsTotalsDto
    {
        public decimal AdSpend { get; set; }
        public int Clicks { get; set; }
        public decimal GoogleConversions { get; set; }
        public int BookedOrders { get; set; }
        // How many of the booked orders in the range came straight from an ad (Paid Search).
        public int AdBookedOrders { get; set; }
        public decimal GoogleConversionRate { get; set; }
    }
}
