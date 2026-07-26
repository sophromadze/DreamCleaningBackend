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
    /// Company "Keywords" tab: what people search to find us, organic and paid side by side.
    /// - Organic = Google Search Console queries (<see cref="Models.SearchConsoleDailyStat"/>):
    ///   impressions/clicks/CTR/avg position.
    /// - Paid = the actual search terms that triggered our Google Ads
    ///   (<see cref="Models.GoogleAdsKeywordDailyStat"/> from search_term_view): clicks/cost/conversions.
    /// Both are aggregated over the selected range and sorted by clicks (biggest first). pageView-gated
    /// by the "keywords" key.
    /// </summary>
    [Route("api/crm/keywords")]
    [ApiController]
    [RequirePageView(AdminViewablePages.Keywords)]
    public class CrmKeywordsController : ControllerBase
    {
        private const int DefaultPageSize = 25;
        private const int MaxPageSize = 200;

        private readonly ApplicationDbContext _context;

        public CrmKeywordsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET api/crm/keywords/organic?period=&from=&to=&page=&pageSize=
        [HttpGet("organic")]
        public async Task<ActionResult<OrganicKeywordResponse>> GetOrganic(
            [FromQuery] string? period, [FromQuery] string? from, [FromQuery] string? to,
            [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize)
        {
            var earliest = await _context.SearchConsoleDailyStats.MinAsync(s => (DateTime?)s.Date);
            var (fromDate, toDate) = ResolveRange(period, from, to, earliest?.Date);
            var all = await AggregateOrganicAsync(fromDate, toDate);

            var (paged, totalCount, totalPages, p, ps) = Paginate(all, page, pageSize);
            return Ok(new OrganicKeywordResponse
            {
                From = fromDate, To = toDate, Items = paged, Totals = OrganicTotals(all),
                Page = p, PageSize = ps, TotalCount = totalCount, TotalPages = totalPages
            });
        }

        // GET api/crm/keywords/paid?period=&from=&to=&page=&pageSize=
        [HttpGet("paid")]
        public async Task<ActionResult<PaidKeywordResponse>> GetPaid(
            [FromQuery] string? period, [FromQuery] string? from, [FromQuery] string? to,
            [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize)
        {
            var earliest = await _context.GoogleAdsKeywordDailyStats.MinAsync(s => (DateTime?)s.Date);
            var (fromDate, toDate) = ResolveRange(period, from, to, earliest?.Date);
            var all = await AggregatePaidAsync(fromDate, toDate);

            var (paged, totalCount, totalPages, p, ps) = Paginate(all, page, pageSize);
            return Ok(new PaidKeywordResponse
            {
                From = fromDate, To = toDate, Items = paged, Totals = PaidTotals(all),
                Page = p, PageSize = ps, TotalCount = totalCount, TotalPages = totalPages
            });
        }

        // GET api/crm/keywords/export?period=&from=&to=  → workbook with Organic + Paid sheets (full range).
        [HttpGet("export")]
        public async Task<IActionResult> Export(
            [FromQuery] string? period, [FromQuery] string? from, [FromQuery] string? to)
        {
            var organicEarliest = await _context.SearchConsoleDailyStats.MinAsync(s => (DateTime?)s.Date);
            var paidEarliest = await _context.GoogleAdsKeywordDailyStats.MinAsync(s => (DateTime?)s.Date);
            // Use the earliest of either source for an "all" export so both sheets share one range.
            DateTime? earliest = (organicEarliest, paidEarliest) switch
            {
                (null, null) => null,
                (null, var p) => p,
                (var o, null) => o,
                var (o, p) => o < p ? o : p
            };
            var (fromDate, toDate) = ResolveRange(period, from, to, earliest?.Date);

            var organic = await AggregateOrganicAsync(fromDate, toDate);
            var paid = await AggregatePaidAsync(fromDate, toDate);

            using var workbook = new XLWorkbook();
            BuildOrganicSheet(workbook, organic);
            BuildPaidSheet(workbook, paid);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var bytes = stream.ToArray();
            var fileName = $"dream-cleaning-keywords_{fromDate:yyyy-MM-dd}_{toDate:yyyy-MM-dd}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        // ── Aggregation ─────────────────────────────────────────────────────────────────

        private async Task<List<OrganicKeywordDto>> AggregateOrganicAsync(DateTime fromDate, DateTime toDate)
        {
            var rows = await _context.SearchConsoleDailyStats
                .Where(s => s.Date >= fromDate && s.Date <= toDate)
                .Select(s => new { s.Query, s.Clicks, s.Impressions, s.Position })
                .ToListAsync();

            return rows
                .GroupBy(r => r.Query)
                .Select(g =>
                {
                    var clicks = g.Sum(r => r.Clicks);
                    var impressions = g.Sum(r => r.Impressions);
                    // Impression-weighted average position (falls back to a plain mean if no impressions).
                    var weighted = impressions > 0
                        ? g.Sum(r => r.Position * r.Impressions) / impressions
                        : (g.Any() ? g.Average(r => r.Position) : 0m);
                    return new OrganicKeywordDto
                    {
                        Query = g.Key,
                        Clicks = clicks,
                        Impressions = impressions,
                        // CTR recomputed over the aggregated range (not an average of daily CTRs), as a percent.
                        Ctr = impressions > 0 ? Math.Round((decimal)clicks / impressions * 100m, 2) : 0m,
                        AvgPosition = Math.Round(weighted, 1)
                    };
                })
                .OrderByDescending(r => r.Clicks)
                .ThenByDescending(r => r.Impressions)
                .ToList();
        }

        private async Task<List<PaidKeywordDto>> AggregatePaidAsync(DateTime fromDate, DateTime toDate)
        {
            var rows = await _context.GoogleAdsKeywordDailyStats
                .Where(s => s.Date >= fromDate && s.Date <= toDate)
                .Select(s => new { s.SearchTerm, s.Clicks, s.Impressions, s.CostUsd, s.Conversions })
                .ToListAsync();

            return rows
                .GroupBy(r => r.SearchTerm)
                .Select(g =>
                {
                    var clicks = g.Sum(r => r.Clicks);
                    var cost = g.Sum(r => r.CostUsd);
                    return new PaidKeywordDto
                    {
                        SearchTerm = g.Key,
                        Clicks = clicks,
                        Impressions = g.Sum(r => r.Impressions),
                        CostUsd = Math.Round(cost, 2),
                        Conversions = Math.Round(g.Sum(r => r.Conversions), 2),
                        Cpc = clicks > 0 ? Math.Round(cost / clicks, 2) : 0m
                    };
                })
                .OrderByDescending(r => r.Clicks)
                .ThenByDescending(r => r.CostUsd)
                .ToList();
        }

        private static OrganicTotalsDto OrganicTotals(List<OrganicKeywordDto> all)
        {
            var clicks = all.Sum(r => r.Clicks);
            var impressions = all.Sum(r => r.Impressions);
            return new OrganicTotalsDto
            {
                Queries = all.Count,
                Clicks = clicks,
                Impressions = impressions,
                Ctr = impressions > 0 ? Math.Round((decimal)clicks / impressions * 100m, 2) : 0m
            };
        }

        private static PaidTotalsDto PaidTotals(List<PaidKeywordDto> all) => new()
        {
            Terms = all.Count,
            Clicks = all.Sum(r => r.Clicks),
            Impressions = all.Sum(r => r.Impressions),
            CostUsd = Math.Round(all.Sum(r => r.CostUsd), 2),
            Conversions = Math.Round(all.Sum(r => r.Conversions), 2)
        };

        // ── Range + paging helpers ──────────────────────────────────────────────────────

        private static (DateTime from, DateTime to) ResolveRange(
            string? period, string? from, string? to, DateTime? earliest)
        {
            var today = NyTimeHelper.NowNy.Date;
            switch ((period ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "last30": return (today.AddDays(-29), today);
                case "week": return (today.AddDays(-(int)today.DayOfWeek), today);
                case "month": return (new DateTime(today.Year, today.Month, 1), today);
                case "year": return (new DateTime(today.Year, 1, 1), today);
                case "all": return (earliest ?? today, today);
                default:
                    var toDate = ParseDate(to) ?? today;
                    var fromDate = ParseDate(from) ?? today.AddDays(-29);
                    if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);
                    return (fromDate, toDate);
            }
        }

        private static (List<T> paged, int totalCount, int totalPages, int page, int pageSize) Paginate<T>(
            List<T> all, int page, int pageSize)
        {
            if (pageSize < 1) pageSize = DefaultPageSize;
            if (pageSize > MaxPageSize) pageSize = MaxPageSize;
            var totalCount = all.Count;
            var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
            if (page < 1) page = 1;
            if (totalPages > 0 && page > totalPages) page = totalPages;
            var paged = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return (paged, totalCount, totalPages, page, pageSize);
        }

        private static DateTime? ParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d.Date;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var any) ? any.Date : null;
        }

        // ── Excel ───────────────────────────────────────────────────────────────────────

        private static void BuildOrganicSheet(XLWorkbook workbook, List<OrganicKeywordDto> rows)
        {
            var ws = workbook.AddWorksheet("Organic (Search Console)");
            string[] headers = { "Query", "Clicks", "Impressions", "CTR (%)", "Avg position" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
            StyleHeader(ws, headers.Length);

            var row = 2;
            foreach (var r in rows)
            {
                ws.Cell(row, 1).Value = r.Query;
                ws.Cell(row, 2).Value = r.Clicks;
                ws.Cell(row, 3).Value = r.Impressions;
                ws.Cell(row, 4).Value = (double)r.Ctr;
                ws.Cell(row, 5).Value = (double)r.AvgPosition;
                row++;
            }
            ws.Column(4).Style.NumberFormat.Format = "0.00";
            ws.Column(5).Style.NumberFormat.Format = "0.0";
            ws.Columns().AdjustToContents();
        }

        private static void BuildPaidSheet(XLWorkbook workbook, List<PaidKeywordDto> rows)
        {
            var ws = workbook.AddWorksheet("Paid (Google Ads)");
            string[] headers = { "Search term", "Clicks", "Impressions", "Cost", "Conversions", "Cost / click" };
            for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
            StyleHeader(ws, headers.Length);

            var row = 2;
            foreach (var r in rows)
            {
                ws.Cell(row, 1).Value = r.SearchTerm;
                ws.Cell(row, 2).Value = r.Clicks;
                ws.Cell(row, 3).Value = r.Impressions;
                ws.Cell(row, 4).Value = (double)r.CostUsd;
                ws.Cell(row, 5).Value = (double)r.Conversions;
                ws.Cell(row, 6).Value = (double)r.Cpc;
                row++;
            }
            ws.Column(4).Style.NumberFormat.Format = "$#,##0.00";
            ws.Column(6).Style.NumberFormat.Format = "$#,##0.00";
            ws.Columns().AdjustToContents();
        }

        private static void StyleHeader(IXLWorksheet ws, int cols)
        {
            var header = ws.Range(1, 1, 1, cols);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
            header.Style.Font.FontColor = XLColor.White;
        }
    }

    // ── DTOs ────────────────────────────────────────────────────────────────────────────

    public class OrganicKeywordDto
    {
        public string Query { get; set; } = string.Empty;
        public int Clicks { get; set; }
        public int Impressions { get; set; }
        public decimal Ctr { get; set; }         // percent
        public decimal AvgPosition { get; set; }
    }

    public class OrganicTotalsDto
    {
        public int Queries { get; set; }
        public int Clicks { get; set; }
        public int Impressions { get; set; }
        public decimal Ctr { get; set; }
    }

    public class OrganicKeywordResponse
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public List<OrganicKeywordDto> Items { get; set; } = new();
        public OrganicTotalsDto Totals { get; set; } = new();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
    }

    public class PaidKeywordDto
    {
        public string SearchTerm { get; set; } = string.Empty;
        public int Clicks { get; set; }
        public int Impressions { get; set; }
        public decimal CostUsd { get; set; }
        public decimal Conversions { get; set; }
        public decimal Cpc { get; set; }
    }

    public class PaidTotalsDto
    {
        public int Terms { get; set; }
        public int Clicks { get; set; }
        public int Impressions { get; set; }
        public decimal CostUsd { get; set; }
        public decimal Conversions { get; set; }
    }

    public class PaidKeywordResponse
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public List<PaidKeywordDto> Items { get; set; } = new();
        public PaidTotalsDto Totals { get; set; } = new();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
    }
}
