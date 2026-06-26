using ClosedXML.Excel;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.CallTracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
// Both DTOs.CallRecordDto (API shape) and Services.CallTracking.CallRecordDto (provider shape)
// are in scope; this controller only deals with the API DTO.
using CallRecordDto = DreamCleaningBackend.DTOs.CallRecordDto;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Read-only call-tracking endpoints for the admin CRM: paged list, aggregate summary, an Excel
    /// export, plus a one-time reclassify backfill. Provider-neutral — it reads the CallRecords
    /// table, never a telephony SDK. Same api/admin route prefix + auth as the other split admin
    /// controllers.
    /// </summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminCallsController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AdminCallsController> _logger;
        private readonly IConfiguration _configuration;

        public AdminCallsController(
            ApplicationDbContext context,
            ILogger<AdminCallsController> logger,
            IConfiguration configuration)
        {
            _context = context;
            _logger = logger;
            _configuration = configuration;
        }

        // GET /api/admin/calls?from=&to=&direction=&category=&excludeNonCustomer=&page=&pageSize=
        [HttpGet("calls")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<CallListResultDto>> GetCalls(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? direction, [FromQuery] string? category,
            [FromQuery] bool excludeNonCustomer = false, [FromQuery] bool adOnly = false,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 500) pageSize = 500;

            var query = BuildQuery(from, to, direction, category, excludeNonCustomer, adOnly);

            var totalCount = await query.CountAsync();
            var entities = await query
                .Include(c => c.Lead)
                .Include(c => c.MatchedCleaner)
                .OrderByDescending(c => c.StartTimeUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
            var items = entities.Select(ToDto).ToList();

            return Ok(new CallListResultDto
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
            });
        }

        // GET /api/admin/calls/summary?from=&to=&direction=
        // Scoped to date + direction only (NOT category) so the breakdown always shows the full
        // picture for the range and the UI can derive the hidden (cleaner + spam) count.
        [HttpGet("calls/summary")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<CallSummaryDto>> GetSummary(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? direction)
        {
            var query = BuildQuery(from, to, direction, null, false, false);

            var rows = await query
                .Select(c => new { c.StartTimeUtc, c.Direction, c.Result, c.CallCategory, c.IsAdCall })
                .ToListAsync();

            var summary = Aggregate(rows.Select(r => (r.StartTimeUtc, r.Direction, r.Result, r.CallCategory, r.IsAdCall)));
            return Ok(summary);
        }

        // GET /api/admin/calls/export?from=&to=&direction=&category=&excludeNonCustomer=
        [HttpGet("calls/export")]
        [RequirePermission(Permission.View)]
        public async Task<IActionResult> Export(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? direction, [FromQuery] string? category,
            [FromQuery] bool excludeNonCustomer = false, [FromQuery] bool adOnly = false)
        {
            var query = BuildQuery(from, to, direction, category, excludeNonCustomer, adOnly);
            var entities = await query
                .Include(c => c.Lead)
                .Include(c => c.MatchedCleaner)
                .OrderByDescending(c => c.StartTimeUtc)
                .ToListAsync();
            var calls = entities.Select(ToDto).ToList();

            var summary = Aggregate(calls.Select(c => (c.StartTimeUtc, c.Direction, c.Result, c.Category, c.IsAdCall)));

            using var workbook = new XLWorkbook();
            BuildCallsSheet(workbook, calls);
            BuildSummarySheet(workbook, summary);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var bytes = stream.ToArray();

            var fromLabel = (from ?? calls.LastOrDefault()?.StartTimeUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd");
            var toLabel = (to ?? DateTime.UtcNow).ToString("yyyy-MM-dd");
            var fileName = $"dream-cleaning-calls_{fromLabel}_{toLabel}.xlsx";

            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }

        // POST /api/admin/calls/reclassify
        // One-time backfill: recompute CallCategory + MatchedCleanerId for EVERY existing record.
        // Idempotent — safe to run repeatedly (e.g. after a deploy or after adding cleaners).
        [HttpPost("calls/reclassify")]
        [RequirePermission(Permission.Update)]
        public async Task<IActionResult> Reclassify(CancellationToken ct)
        {
            var cleanerPhoneMap = await CallClassifier.LoadCleanerPhoneMapAsync(_context, ct);
            var adNumberDigits = CallClassifier.ResolveAdNumberDigits(_configuration);

            // Tracked load (no AsNoTracking) so the recomputed values persist on SaveChanges.
            var all = await _context.CallRecords.ToListAsync(ct);
            foreach (var record in all)
                CallClassifier.Classify(record, cleanerPhoneMap, adNumberDigits);

            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("Call reclassify backfill: reprocessed {Count} records.", all.Count);

            return Ok(new
            {
                reclassified = all.Count,
                customer = all.Count(c => c.CallCategory == CallCategory.Customer),
                cleaner = all.Count(c => c.CallCategory == CallCategory.Cleaner),
                spam = all.Count(c => c.CallCategory == CallCategory.Spam),
                unknown = all.Count(c => c.CallCategory == CallCategory.Unknown),
                adCall = all.Count(c => c.IsAdCall)
            });
        }

        // ── Helpers ──

        private IQueryable<CallRecord> BuildQuery(
            DateTime? from, DateTime? to, string? direction, string? category,
            bool excludeNonCustomer, bool adOnly)
        {
            var query = _context.CallRecords.AsNoTracking().AsQueryable();

            if (from.HasValue)
            {
                var f = DateTime.SpecifyKind(from.Value, DateTimeKind.Utc);
                query = query.Where(c => c.StartTimeUtc >= f);
            }
            if (to.HasValue)
            {
                var t = DateTime.SpecifyKind(to.Value, DateTimeKind.Utc);
                query = query.Where(c => c.StartTimeUtc <= t);
            }
            if (!string.IsNullOrWhiteSpace(direction))
                query = query.Where(c => c.Direction == direction);

            // Explicit category filter (ignore "All"/blank).
            if (!string.IsNullOrWhiteSpace(category) &&
                !string.Equals(category, "All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(c => c.CallCategory == category);
            }

            // Hide cleaners & spam — keep only Customer + Unknown.
            if (excludeNonCustomer)
            {
                query = query.Where(c =>
                    c.CallCategory == CallCategory.Customer || c.CallCategory == CallCategory.Unknown);
            }

            // Ad calls only — independent of the category/hide filters above, so it composes
            // with them (e.g. "Customer + ad calls only").
            if (adOnly)
                query = query.Where(c => c.IsAdCall);

            return query;
        }

        private static CallRecordDto ToDto(CallRecord c) => new()
        {
            Id = c.Id,
            Provider = c.Provider,
            ExternalId = c.ExternalId,
            SessionId = c.SessionId,
            Direction = c.Direction,
            Result = c.Result,
            FromNumber = c.FromNumber,
            FromName = c.FromName,
            ToNumber = c.ToNumber,
            StartTimeUtc = c.StartTimeUtc,
            DurationSeconds = c.DurationSeconds,
            RecordingUrl = c.RecordingUrl,
            LeadId = c.LeadId,
            LeadName = c.Lead != null ? c.Lead.FullName : null,
            Category = c.CallCategory,
            IsAdCall = c.IsAdCall,
            MatchedCleanerId = c.MatchedCleanerId,
            MatchedCleanerName = c.MatchedCleaner != null
                ? $"{c.MatchedCleaner.FirstName} {c.MatchedCleaner.LastName}".Trim()
                : null,
            CreatedAt = c.CreatedAt
        };

        private static bool IsAnswered(string result) =>
            string.Equals(result, "Accepted", StringComparison.OrdinalIgnoreCase);

        private static bool IsMissed(string result) =>
            string.Equals(result, "Missed", StringComparison.OrdinalIgnoreCase);

        private static CallSummaryDto Aggregate(
            IEnumerable<(DateTime StartTimeUtc, string Direction, string Result, string Category, bool IsAdCall)> rows)
        {
            var list = rows.ToList();
            var total = list.Count;
            var answered = list.Count(r => IsAnswered(r.Result));
            var missed = list.Count(r => IsMissed(r.Result));
            var inbound = list.Count(r => string.Equals(r.Direction, "Inbound", StringComparison.OrdinalIgnoreCase));

            var perDay = list
                .GroupBy(r => r.StartTimeUtc.Date)
                .OrderBy(g => g.Key)
                .Select(g => new CallDayCountDto
                {
                    Date = g.Key.ToString("yyyy-MM-dd"),
                    Total = g.Count(),
                    Answered = g.Count(r => IsAnswered(r.Result)),
                    Missed = g.Count(r => IsMissed(r.Result))
                })
                .ToList();

            return new CallSummaryDto
            {
                Total = total,
                Answered = answered,
                Missed = missed,
                Inbound = inbound,
                AnswerRate = total > 0 ? Math.Round(answered * 100.0 / total, 1) : 0,
                Customer = list.Count(r => r.Category == CallCategory.Customer),
                Cleaner = list.Count(r => r.Category == CallCategory.Cleaner),
                Spam = list.Count(r => r.Category == CallCategory.Spam),
                Unknown = list.Count(r => r.Category == CallCategory.Unknown),
                AdCall = list.Count(r => r.IsAdCall),
                PerDay = perDay
            };
        }

        private static string FormatDurationMmSs(int totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return $"{minutes}:{seconds:D2}";
        }

        private static void BuildCallsSheet(XLWorkbook workbook, List<CallRecordDto> calls)
        {
            var ws = workbook.Worksheets.Add("Calls");

            var headers = new[]
            {
                "Date", "Time", "Direction", "Category", "Ad Call", "From Number", "From Name",
                "To Number", "Duration (m:ss)", "Result", "Linked Lead", "Matched Cleaner", "Recording URL"
            };
            for (var i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];

            var headerRange = ws.Range(1, 1, 1, headers.Length);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
            headerRange.Style.Font.FontColor = XLColor.White;

            var row = 2;
            foreach (var c in calls)
            {
                ws.Cell(row, 1).Value = c.StartTimeUtc.ToString("yyyy-MM-dd");
                ws.Cell(row, 2).Value = c.StartTimeUtc.ToString("HH:mm:ss");
                ws.Cell(row, 3).Value = c.Direction;
                ws.Cell(row, 4).Value = c.Category;
                ws.Cell(row, 5).Value = c.IsAdCall ? "Yes" : "No";
                ws.Cell(row, 6).Value = c.FromNumber ?? "";
                ws.Cell(row, 7).Value = c.FromName ?? "";
                ws.Cell(row, 8).Value = c.ToNumber ?? "";
                ws.Cell(row, 9).Value = FormatDurationMmSs(c.DurationSeconds);
                ws.Cell(row, 10).Value = c.Result;
                ws.Cell(row, 11).Value = c.LeadName ?? "";
                ws.Cell(row, 12).Value = c.MatchedCleanerName ?? "";
                ws.Cell(row, 13).Value = c.RecordingUrl ?? "";
                row++;
            }

            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();
        }

        private static void BuildSummarySheet(XLWorkbook workbook, CallSummaryDto summary)
        {
            var ws = workbook.Worksheets.Add("Summary");

            ws.Cell(1, 1).Value = "Metric";
            ws.Cell(1, 2).Value = "Value";
            var topHeader = ws.Range(1, 1, 1, 2);
            topHeader.Style.Font.Bold = true;
            topHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
            topHeader.Style.Font.FontColor = XLColor.White;

            ws.Cell(2, 1).Value = "Total calls";
            ws.Cell(2, 2).Value = summary.Total;
            ws.Cell(3, 1).Value = "Answered";
            ws.Cell(3, 2).Value = summary.Answered;
            ws.Cell(4, 1).Value = "Missed";
            ws.Cell(4, 2).Value = summary.Missed;
            ws.Cell(5, 1).Value = "Answer rate (%)";
            ws.Cell(5, 2).Value = summary.AnswerRate;
            // Ad calls — independent dimension (a call can be Customer/Cleaner/Spam AND ad-sourced).
            ws.Cell(6, 1).Value = "Ad calls";
            ws.Cell(6, 2).Value = summary.AdCall;

            // Category breakdown.
            var catRow = 8;
            ws.Cell(catRow, 1).Value = "Category";
            ws.Cell(catRow, 2).Value = "Count";
            var catHeader = ws.Range(catRow, 1, catRow, 2);
            catHeader.Style.Font.Bold = true;
            catHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#6366F1");
            catHeader.Style.Font.FontColor = XLColor.White;
            ws.Cell(catRow + 1, 1).Value = "Customer";
            ws.Cell(catRow + 1, 2).Value = summary.Customer;
            ws.Cell(catRow + 2, 1).Value = "Cleaner";
            ws.Cell(catRow + 2, 2).Value = summary.Cleaner;
            ws.Cell(catRow + 3, 1).Value = "Spam";
            ws.Cell(catRow + 3, 2).Value = summary.Spam;
            ws.Cell(catRow + 4, 1).Value = "Unknown";
            ws.Cell(catRow + 4, 2).Value = summary.Unknown;

            // Date-grouped table — lines up against Google Ads phone_click for comparison.
            var startRow = catRow + 6;
            ws.Cell(startRow, 1).Value = "Date";
            ws.Cell(startRow, 2).Value = "Total";
            ws.Cell(startRow, 3).Value = "Answered";
            ws.Cell(startRow, 4).Value = "Missed";
            var dayHeader = ws.Range(startRow, 1, startRow, 4);
            dayHeader.Style.Font.Bold = true;
            dayHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#16A34A");
            dayHeader.Style.Font.FontColor = XLColor.White;

            var r = startRow + 1;
            foreach (var day in summary.PerDay)
            {
                ws.Cell(r, 1).Value = day.Date;
                ws.Cell(r, 2).Value = day.Total;
                ws.Cell(r, 3).Value = day.Answered;
                ws.Cell(r, 4).Value = day.Missed;
                r++;
            }

            ws.Columns().AdjustToContents();
        }
    }
}
