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
    /// Company "Traffic" tab: first-party visitor/session counts by acquisition channel — "how many
    /// people came to the site, and via which channel" — independent of ad spend. Reads the aggregated
    /// <see cref="Models.SessionDailyStat"/> table (built by the public session-log endpoint). Purely
    /// a visits view: no ad spend/clicks, no booked orders. pageView-gated by the "traffic" key.
    /// </summary>
    [Route("api/crm/traffic")]
    [ApiController]
    [RequirePageView(AdminViewablePages.Traffic)]
    public class CrmTrafficController : ControllerBase
    {
        private const int DefaultPageSize = 20;
        private const int MaxPageSize = 200;

        // Sessions only ever carry the classified web channels (never "Phone/Unknown" — that's
        // order-only — nor "Unattributed" — blank collapses to "Unassigned" at log time).
        private static readonly string[] ChannelOrder =
        {
            "Organic Search", "Paid Search", "AI Assistant", "Direct", "Referral", "Social", "Email", "Unassigned"
        };

        // Short display labels for the UI/Excel headers (the stored channel keeps its full name so
        // attribution matching + the channel filter never break). Only the two long ones are shortened.
        private static readonly Dictionary<string, string> ChannelDisplay = new()
        {
            ["Organic Search"] = "Organic",
            ["Paid Search"] = "Paid"
        };

        private static string Display(string channel) =>
            ChannelDisplay.TryGetValue(channel, out var d) ? d : channel;

        private readonly ApplicationDbContext _context;

        public CrmTrafficController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET api/crm/traffic/daily?period=last30|week|month|year|all&from=&to=&page=&pageSize=
        [HttpGet("daily")]
        public async Task<ActionResult<TrafficDailyResponse>> GetDaily(
            [FromQuery] string? period, [FromQuery] string? from, [FromQuery] string? to,
            [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize)
        {
            var (fromDate, toDate) = await ResolveRangeAsync(period, from, to);
            var rows = await FetchSessionStatsAsync(fromDate, toDate);
            var (items, totals) = BuildDaily(rows);
            var breakdown = BuildBreakdown(rows);

            if (pageSize < 1) pageSize = DefaultPageSize;
            if (pageSize > MaxPageSize) pageSize = MaxPageSize;
            var totalCount = items.Count;
            var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
            if (page < 1) page = 1;
            if (totalPages > 0 && page > totalPages) page = totalPages;

            var paged = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return Ok(new TrafficDailyResponse
            {
                From = fromDate,
                To = toDate,
                Items = paged,
                // Full unpaged daily series, oldest→newest, for the trend chart (items are the paged,
                // newest-first table view). A day range yields at most a few thousand small rows.
                Series = items.OrderBy(i => i.Date).ToList(),
                Totals = totals,
                ChannelBreakdown = breakdown,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = totalPages
            });
        }

        // GET api/crm/traffic/export?period=&from=&to=&channels=Organic Search,Direct
        [HttpGet("export")]
        public async Task<IActionResult> Export(
            [FromQuery] string? period, [FromQuery] string? from, [FromQuery] string? to,
            [FromQuery] string? channels)
        {
            var (fromDate, toDate) = await ResolveRangeAsync(period, from, to);
            var rows = await FetchSessionStatsAsync(fromDate, toDate);
            var (items, _) = BuildDaily(rows);
            var breakdown = BuildBreakdown(rows);
            var selected = ParseChannelFilter(channels);

            using var workbook = new XLWorkbook();
            BuildDailySheet(workbook, items, selected);
            BuildChannelSheet(workbook, breakdown, selected);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var bytes = stream.ToArray();
            var fileName = $"dream-cleaning-traffic_{fromDate:yyyy-MM-dd}_{toDate:yyyy-MM-dd}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        // ── Range resolution (NY calendar days, presets mirror the Ads tab) ─────────────

        private async Task<(DateTime from, DateTime to)> ResolveRangeAsync(string? period, string? from, string? to)
        {
            var today = NyTimeHelper.NowNy.Date;
            switch ((period ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "last30": return (today.AddDays(-29), today);
                case "week": return (today.AddDays(-(int)today.DayOfWeek), today);
                case "month": return (new DateTime(today.Year, today.Month, 1), today);
                case "year": return (new DateTime(today.Year, 1, 1), today);
                case "all":
                    var earliest = await _context.SessionDailyStats.MinAsync(s => (DateTime?)s.Date);
                    return (earliest?.Date ?? today, today);
                default:
                    var toDate = ParseDate(to) ?? today;
                    var fromDate = ParseDate(from) ?? today.AddDays(-29);
                    if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);
                    return (fromDate, toDate);
            }
        }

        // ── Fetch + aggregate ───────────────────────────────────────────────────────────

        private record SessionRow(DateTime Day, string Channel, int Sessions, bool Provisional);

        private async Task<List<SessionRow>> FetchSessionStatsAsync(DateTime fromDate, DateTime toDate)
        {
            return (await _context.SessionDailyStats
                    .Where(s => s.Date >= fromDate && s.Date <= toDate)
                    .Select(s => new { s.Date, s.Channel, s.Sessions, s.Origin })
                    .ToListAsync())
                .Select(s => new SessionRow(
                    s.Date.Date,
                    string.IsNullOrWhiteSpace(s.Channel) ? "Unassigned" : s.Channel.Trim(),
                    s.Sessions,
                    // "Provisional" = still a raw first-party count (not yet reconciled to GA4).
                    !string.Equals(s.Origin, "ga4", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private static int ChannelOrderIndex(string channel)
        {
            var i = Array.IndexOf(ChannelOrder, channel);
            return i >= 0 ? i : int.MaxValue;
        }

        private static List<TrafficChannelCountDto> GroupChannels(IEnumerable<SessionRow> rows) =>
            rows.GroupBy(r => r.Channel)
                .Select(g => new TrafficChannelCountDto { Channel = g.Key, Count = g.Sum(r => r.Sessions) })
                .OrderByDescending(c => c.Count)
                .ThenBy(c => ChannelOrderIndex(c.Channel))
                .ToList();

        private static (List<TrafficDailyDto> items, TrafficTotalsDto totals) BuildDaily(List<SessionRow> rows)
        {
            var items = rows
                .GroupBy(r => r.Day)
                .OrderByDescending(g => g.Key)
                .Select(g =>
                {
                    var channels = GroupChannels(g);
                    return new TrafficDailyDto
                    {
                        Date = g.Key,
                        Sessions = channels.Sum(c => c.Count),
                        Channels = channels,
                        // Provisional if any of the day's rows are still raw first-party (not reconciled).
                        Provisional = g.Any(r => r.Provisional)
                    };
                })
                .ToList();

            var totals = new TrafficTotalsDto { Sessions = items.Sum(i => i.Sessions) };
            return (items, totals);
        }

        private static List<TrafficChannelBreakdownDto> BuildBreakdown(List<SessionRow> rows)
        {
            var total = rows.Sum(r => r.Sessions);
            return GroupChannels(rows)
                .Select(c => new TrafficChannelBreakdownDto
                {
                    Channel = c.Channel,
                    Sessions = c.Count,
                    PercentOfTotal = total > 0 ? Math.Round(c.Count / (decimal)total * 100m, 1) : 0m
                })
                .ToList();
        }

        // ── Excel (channel filter honored, mirroring the Ads export) ────────────────────

        private static HashSet<string>? ParseChannelFilter(string? channels)
        {
            if (channels == null) return null;
            var picked = channels
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(c => ChannelOrder.Contains(c))
                .ToHashSet(StringComparer.Ordinal);
            return picked.Count >= ChannelOrder.Length ? null : picked;
        }

        private static int FilteredSessions(TrafficDailyDto it, HashSet<string>? selected) =>
            selected == null ? it.Sessions : it.Channels.Where(c => selected.Contains(c.Channel)).Sum(c => c.Count);

        // Matches the on-screen matrix exactly: Date, one column per (shown) channel, then Total,
        // and a bold "Range total" footer row summing each channel over the whole range.
        private static void BuildDailySheet(XLWorkbook workbook, List<TrafficDailyDto> items, HashSet<string>? selected)
        {
            var ws = workbook.AddWorksheet("Daily");
            var channelCols = selected == null ? ChannelOrder : ChannelOrder.Where(selected.Contains).ToArray();
            var headers = new[] { "Date" }
                .Concat(channelCols.Select(Display))
                .Concat(new[] { "Total" })
                .ToArray();
            for (var i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];

            var header = ws.Range(1, 1, 1, headers.Length);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
            header.Style.Font.FontColor = XLColor.White;

            var totalCol = headers.Length; // Total is the last column

            var row = 2;
            foreach (var it in items)
            {
                ws.Cell(row, 1).Value = it.Date.ToString("yyyy-MM-dd");
                var byChannel = it.Channels.ToDictionary(c => c.Channel, c => c.Count);
                for (var c = 0; c < channelCols.Length; c++)
                    ws.Cell(row, 2 + c).Value = byChannel.TryGetValue(channelCols[c], out var n) ? n : 0;
                ws.Cell(row, totalCol).Value = FilteredSessions(it, selected);
                row++;
            }

            // Range-total footer row.
            ws.Cell(row, 1).Value = "Range total";
            for (var c = 0; c < channelCols.Length; c++)
                ws.Cell(row, 2 + c).Value = items.Sum(it =>
                    it.Channels.Where(x => x.Channel == channelCols[c]).Sum(x => x.Count));
            ws.Cell(row, totalCol).Value = items.Sum(it => FilteredSessions(it, selected));
            var footer = ws.Range(row, 1, row, headers.Length);
            footer.Style.Font.Bold = true;
            footer.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2FF");

            ws.Columns().AdjustToContents();
        }

        private static void BuildChannelSheet(XLWorkbook workbook, List<TrafficChannelBreakdownDto> breakdown, HashSet<string>? selected)
        {
            var ws = workbook.AddWorksheet("By channel");
            string[] headers = { "Channel", "Sessions", "% of total" };
            for (var i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];

            var header = ws.Range(1, 1, 1, headers.Length);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
            header.Style.Font.FontColor = XLColor.White;

            var shown = selected == null ? breakdown : breakdown.Where(b => selected.Contains(b.Channel)).ToList();
            var filteredTotal = shown.Sum(b => b.Sessions);

            var row = 2;
            foreach (var b in shown)
            {
                var percent = selected == null
                    ? b.PercentOfTotal
                    : (filteredTotal > 0 ? Math.Round((decimal)b.Sessions / filteredTotal * 100m, 1) : 0m);
                ws.Cell(row, 1).Value = Display(b.Channel);
                ws.Cell(row, 2).Value = b.Sessions;
                ws.Cell(row, 3).Value = (double)percent;
                row++;
            }
            ws.Column(3).Style.NumberFormat.Format = "0.0";
            ws.Columns().AdjustToContents();
        }

        private static DateTime? ParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d.Date;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var any) ? any.Date : null;
        }
    }

    public class TrafficDailyResponse
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public List<TrafficDailyDto> Items { get; set; } = new();
        // Full unpaged daily series (oldest→newest) for the trend chart.
        public List<TrafficDailyDto> Series { get; set; } = new();
        public TrafficTotalsDto Totals { get; set; } = new();
        public List<TrafficChannelBreakdownDto> ChannelBreakdown { get; set; } = new();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
    }

    public class TrafficDailyDto
    {
        public DateTime Date { get; set; }
        public int Sessions { get; set; }
        public List<TrafficChannelCountDto> Channels { get; set; } = new();
        // True while this day's sessions are still raw first-party counts (not yet reconciled to GA4).
        public bool Provisional { get; set; }
    }

    public class TrafficChannelCountDto
    {
        public string Channel { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class TrafficTotalsDto
    {
        public int Sessions { get; set; }
    }

    public class TrafficChannelBreakdownDto
    {
        public string Channel { get; set; } = string.Empty;
        public int Sessions { get; set; }
        public decimal PercentOfTotal { get; set; }
    }
}
