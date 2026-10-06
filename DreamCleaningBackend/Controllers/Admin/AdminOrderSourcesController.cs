using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Company → Order Sources (2026-10): how many completed orders came from paid ads, organic
    /// search, direct visits and the other first-touch sources, with the revenue behind each and a
    /// breakdown over time.
    ///
    /// Reporting rules, the same as the rest of the order reporting:
    ///   • bucketed by <c>ServiceDate</c>, the day the cleaning happened;
    ///   • <c>Status = Done</c> and not hidden from the admin list;
    ///   • revenue is <see cref="OrderRevenueMath.Split"/>'s ReportedRevenue — what Statistics calls
    ///     Company Revenue (after discounts and refunds; tips and remitted sales tax excluded).
    ///
    /// The source of each order comes ONLY from <see cref="OrderSourceClassifier"/>.
    /// </summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminOrderSourcesController : AdminControllerBase
    {
        /// <summary>Longest window the report will bucket, so a typo'd year cannot ask for a
        /// ten-thousand-day series.</summary>
        private const int MaxRangeDays = 3 * 366;

        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;

        public AdminOrderSourcesController(ApplicationDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        // GET api/admin/order-sources?from=2026-09-01&to=2026-09-30&granularity=week
        [HttpGet("order-sources")]
        [RequirePageView(AdminViewablePages.OrderSources)]
        public async Task<ActionResult<OrderSourcesReportDto>> GetOrderSources(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string? granularity = null)
        {
            var today = NyTimeHelper.NowNy.Date;
            var end = (to ?? today).Date;
            var start = (from ?? end.AddDays(-29)).Date;
            if (start > end)
                return BadRequest(new { message = "The start date must be on or before the end date." });
            if ((end - start).TotalDays > MaxRangeDays)
                return BadRequest(new { message = "Choose a range of three years or less." });

            var grain = OrderSourcesReport.ResolveGranularity(granularity, start, end);
            if (grain == null)
                return BadRequest(new { message = "Granularity must be day, week or month." });

            var rows = await _context.Orders
                .AsNoTracking()
                .Where(o => o.Status == OrderStatuses.Done && !o.IsHidden
                    && o.ServiceDate >= start && o.ServiceDate < end.AddDays(1))
                .Select(o => new OrderSourcesReport.Row(
                    o.ServiceDate,
                    o.BookedByAdminUserId != null,
                    o.AcquisitionChannel,
                    o.AcquisitionSource,
                    o.AcquisitionMedium,
                    o.AcquisitionClickIdType,
                    o.AcquisitionReferrer,
                    o.SubTotal,
                    o.DiscountAmount,
                    o.SubscriptionDiscountAmount,
                    o.LoyaltyDiscountAmount,
                    o.Tax,
                    o.Tips,
                    o.CompanyDevelopmentTips,
                    o.TotalRefundedAmount,
                    o.PaymentMethod))
                .ToListAsync();

            return Ok(OrderSourcesReport.Build(rows, start, end, grain, OwnHost()));
        }

        // The site's own host, so a referrer from our own pages reads as Direct. A missing key only
        // means self-referrals fall back to the browser's own verdict.
        private string? OwnHost()
        {
            var url = _configuration["Frontend:Url"];
            return !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.Host
                : null;
        }
    }

    /// <summary>The report's arithmetic, kept apart from the controller so it is tested without a
    /// database.</summary>
    public static class OrderSourcesReport
    {
        public sealed record Row(
            DateTime ServiceDate,
            bool BookedByAdmin,
            string? Channel,
            string? Source,
            string? Medium,
            string? ClickIdType,
            string? Referrer,
            decimal SubTotal,
            decimal DiscountAmount,
            decimal SubscriptionDiscountAmount,
            decimal LoyaltyDiscountAmount,
            decimal Tax,
            decimal Tips,
            decimal CompanyDevelopmentTips,
            decimal TotalRefundedAmount,
            PaymentMethod PaymentMethod);

        public const string Day = "day";
        public const string Week = "week";
        public const string Month = "month";

        /// <summary>The requested grain, or one picked from the range length when none was asked
        /// for: days up to ~6 weeks, weeks up to ~6 months, months beyond. Null = unrecognised.</summary>
        public static string? ResolveGranularity(string? requested, DateTime start, DateTime end)
        {
            if (string.IsNullOrWhiteSpace(requested) || requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var days = (end - start).TotalDays + 1;
                return days <= 45 ? Day : days <= 190 ? Week : Month;
            }
            var g = requested.Trim().ToLowerInvariant();
            return g is Day or Week or Month ? g : null;
        }

        /// <summary>The first day of the period a date falls in (weeks start on Monday).</summary>
        public static DateTime PeriodStart(DateTime date, string grain)
        {
            var d = date.Date;
            return grain switch
            {
                Week => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
                Month => new DateTime(d.Year, d.Month, 1),
                _ => d
            };
        }

        private static DateTime NextPeriod(DateTime periodStart, string grain) => grain switch
        {
            Week => periodStart.AddDays(7),
            Month => periodStart.AddMonths(1),
            _ => periodStart.AddDays(1)
        };

        public static OrderSourcesReportDto Build(
            IEnumerable<Row> rows, DateTime start, DateTime end, string grain, string? ownHost)
        {
            var classified = rows.Select(r => new
            {
                Period = PeriodStart(r.ServiceDate, grain),
                Source = OrderSourceClassifier.Classify(
                    new OrderSourceSignals(r.BookedByAdmin, r.Channel, r.Source, r.Medium, r.ClickIdType, r.Referrer),
                    ownHost),
                // Remitted vs retained tax only moves money between Tax and ReportedRevenue for
                // orders paid outside Stripe - the same per-order rule Statistics applies. Top-ups
                // are not split by method here; their revenue still counts in full.
                Revenue = OrderRevenueMath.Split(
                    r.SubTotal, r.DiscountAmount, r.SubscriptionDiscountAmount, r.LoyaltyDiscountAmount,
                    r.Tax, r.Tips, r.CompanyDevelopmentTips, r.TotalRefundedAmount,
                    OrderRevenueMath.ResolveRetainedTax(r.Tax, r.PaymentMethod, 0m, 0m)).ReportedRevenue
            }).ToList();

            var totalOrders = classified.Count;
            var totalRevenue = classified.Sum(c => c.Revenue);

            var sources = OrderSourceClassifier.All.Select(s =>
            {
                var mine = classified.Where(c => c.Source == s.Key).ToList();
                var revenue = mine.Sum(c => c.Revenue);
                return new OrderSourceRowDto
                {
                    Key = s.Key,
                    Label = s.Label,
                    Orders = mine.Count,
                    Revenue = revenue,
                    OrderShare = totalOrders == 0 ? 0m : Math.Round(mine.Count * 100m / totalOrders, 1),
                    RevenueShare = totalRevenue == 0m ? 0m : Math.Round(revenue * 100m / totalRevenue, 1),
                    AverageOrderValue = mine.Count == 0 ? 0m : Math.Round(revenue / mine.Count, 2, MidpointRounding.AwayFromZero)
                };
            }).ToList();

            var periods = new List<OrderSourcePeriodDto>();
            var byPeriod = classified.GroupBy(c => c.Period).ToDictionary(g => g.Key, g => g.ToList());
            for (var p = PeriodStart(start, grain); p <= end; p = NextPeriod(p, grain))
            {
                byPeriod.TryGetValue(p, out var inPeriod);
                inPeriod ??= new();
                periods.Add(new OrderSourcePeriodDto
                {
                    PeriodStart = p,
                    Orders = OrderSourceClassifier.All.ToDictionary(s => s.Key, s => inPeriod.Count(c => c.Source == s.Key)),
                    Revenue = OrderSourceClassifier.All.ToDictionary(s => s.Key, s => inPeriod.Where(c => c.Source == s.Key).Sum(c => c.Revenue))
                });
            }

            return new OrderSourcesReportDto
            {
                From = start,
                To = end,
                Granularity = grain,
                TotalOrders = totalOrders,
                TotalRevenue = totalRevenue,
                Sources = sources,
                Periods = periods
            };
        }
    }
}
