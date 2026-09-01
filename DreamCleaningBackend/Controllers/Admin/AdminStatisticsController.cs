using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Hubs;
using System.Linq;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Helpers;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>Statistics and income reports (SuperAdmin only endpoints).
    /// Split out of the monolithic AdminController; same api/admin route prefix, so URLs are unchanged.</summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminStatisticsController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IExpenseService _expenseService;
        private readonly IFinancialRateService _financialRateService;
        private readonly IAuditService _auditService;
        private readonly IAdminBonusService _adminBonusService;

        public AdminStatisticsController(ApplicationDbContext context,
            IConfiguration configuration,
            IExpenseService expenseService,
            IFinancialRateService financialRateService,
            IAuditService auditService,
            IAdminBonusService adminBonusService)
        {
            _context = context;
            _configuration = configuration;
            _expenseService = expenseService;
            _financialRateService = financialRateService;
            _auditService = auditService;
            _adminBonusService = adminBonusService;
        }

        // Stripe US standard processing fee: 2.9% of the charged amount + $0.30 per transaction.
        // Overridable via config without code changes. Used for statistics only — never alters
        // the amounts customers/admins see on an order.
        private decimal StripeFeePercent => _configuration.GetValue<decimal>("Stripe:FeePercent", 0.029m);
        private decimal StripeFixedFee => _configuration.GetValue<decimal>("Stripe:FixedFeePerOrder", 0.30m);

        // ───── Retained sales tax (tax collected outside Stripe) ─────
        // Sales tax charged on a Cash/Zelle/Check/Other payment is not remitted, so the reports
        // count it as company revenue instead of as a pass-through. Everything a caller needs to
        // decide that per order lives here; the arithmetic itself is OrderRevenueMath's.

        /// <summary>
        /// Tax added by each order's PAID edit top-ups, split by how that top-up was collected.
        /// Key is the order id; ViaStripe/Outside are the summed NewTax − OriginalTax deltas.
        /// A missing key simply means the order was never edited (or never topped up).
        /// </summary>
        /// <remarks>
        /// The rows are grouped in memory rather than in SQL: one row per order EDIT is a small
        /// set even over "all time", and a GroupBy on a computed boolean is the kind of thing that
        /// silently falls back to client evaluation anyway.
        /// </remarks>
        private static async Task<Dictionary<int, (decimal ViaStripe, decimal Outside)>>
            LoadAdditionalTaxByOrderAsync(IQueryable<Order> orders)
        {
            var rows = await orders
                .SelectMany(o => o.UpdateHistory)
                .Where(h => h.IsPaid)
                .Select(h => new { h.OrderId, h.PaymentMethod, TaxDelta = h.NewTax - h.OriginalTax })
                .ToListAsync();

            return rows
                .GroupBy(h => h.OrderId)
                .ToDictionary(
                    g => g.Key,
                    g => (
                        ViaStripe: g.Where(h => h.PaymentMethod == PaymentMethod.Normal)
                                    .Sum(h => h.TaxDelta),
                        Outside: g.Where(h => h.PaymentMethod != PaymentMethod.Normal)
                                  .Sum(h => h.TaxDelta)));
        }

        /// <summary>How much of one order's tax was collected outside Stripe.</summary>
        private static decimal RetainedTaxFor(
            Dictionary<int, (decimal ViaStripe, decimal Outside)> additionalTax,
            int orderId,
            decimal tax,
            PaymentMethod paymentMethod)
        {
            additionalTax.TryGetValue(orderId, out var add);
            return OrderRevenueMath.ResolveRetainedTax(tax, paymentMethod, add.ViaStripe, add.Outside);
        }

        // ───── Statistics (SuperAdmin only) ─────

        [HttpGet("statistics")]
        [RequirePageView(AdminViewablePages.Statistics, AdminViewablePages.Finances)]
        public async Task<ActionResult<OrderStatisticsDto>> GetOrderStatistics(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] bool includeUpcoming = false,
            [FromQuery] DateTime? upcomingTo = null)
        {
            // Counts both Stripe-paid orders (IsPaid=true, PaymentMethod=Normal) and manual-paid
            // orders (PaymentMethod != Normal, IsPaid=false) — see Order.PaymentMethod docs.
            //
            // Fully-refunded orders carry Status="Refunded", so they are pulled back in via
            // StatusBeforeRefund: the cleaning still happened and the cleaner was still paid, so
            // that salary is a real cost that has to keep counting. Their revenue is cancelled out
            // by the TotalRefundedAmount subtraction below instead of by dropping the row — which
            // is what keeps a retained cancellation fee counting as income. Orders refunded BEFORE
            // service never had StatusBeforeRefund="Done" and stay excluded, so no cleaner wage is
            // invented for work nobody did.
            //
            // includeUpcoming turns the whole report into a PROJECTION: the window's still-to-happen
            // orders (Active/Pending) join in, answering "what will this period look like once
            // everything already on the books is done". Cancelled orders never join either way. Note
            // an upcoming order's CleanerTotalSalary is whatever is on it today — an unstaffed order
            // contributes 0 salary, so a projection is optimistic on the cost side by design.
            var query = _context.Orders
                .Where(o => (o.IsPaid || o.PaymentMethod != PaymentMethod.Normal)
                    && (o.Status == OrderStatuses.Done
                        || (o.Status == OrderStatuses.Refunded && o.StatusBeforeRefund == OrderStatuses.Done)
                        || (includeUpcoming && (o.Status == OrderStatuses.Active || o.Status == OrderStatuses.Pending))));

            if (from.HasValue)
                query = query.Where(o => o.ServiceDate >= from.Value.Date);

            if (to.HasValue)
                query = query.Where(o => o.ServiceDate < to.Value.Date.AddDays(1));

            // Aggregated in memory rather than in SQL because the refund allocation is a
            // per-order ratio (see OrderRevenueMath.Split) — it cannot be expressed as a sum
            // of columns. /statistics/daily below already materialises the same rows.
            var rows = await query
                .Select(o => new
                {
                    o.Id,
                    o.Status,
                    o.SubTotal,
                    o.DiscountAmount,
                    o.SubscriptionDiscountAmount,
                    o.LoyaltyDiscountAmount,
                    o.Tax,
                    o.Tips,
                    o.CompanyDevelopmentTips,
                    o.CleanerTotalSalary,
                    o.TotalRefundedAmount,
                    o.PaymentMethod
                })
                .ToListAsync();

            var additionalTax = await LoadAdditionalTaxByOrderAsync(query);

            var money = rows
                .Select(o => new
                {
                    o.Status,
                    o.CleanerTotalSalary,
                    Split = OrderRevenueMath.Split(
                        o.SubTotal, o.DiscountAmount, o.SubscriptionDiscountAmount,
                        o.LoyaltyDiscountAmount, o.Tax, o.Tips, o.CompanyDevelopmentTips,
                        o.TotalRefundedAmount,
                        RetainedTaxFor(additionalTax, o.Id, o.Tax, o.PaymentMethod))
                })
                .ToList();

            // "Company Revenue" — the taxable revenue PLUS the sales tax that was collected
            // outside Stripe and so never goes to the state. See OrderRevenueMath.
            var totalRevenue = money.Sum(o => o.Split.ReportedRevenue);
            var totalSalary = money.Sum(o => o.CleanerTotalSalary);

            var stats = new OrderStatisticsDto
            {
                // A fully-refunded order earned nothing, so it doesn't count as an order sold.
                // It stays in the money math above purely to carry its cleaner cost.
                // IsRefunded (not ==): this comparison now runs in memory, where string equality
                // is case-sensitive — unlike the SQL collation the old GroupBy relied on.
                TotalOrders = money.Count(o => !OrderStatuses.IsRefunded(o.Status)),
                TotalAmount = totalRevenue,
                TotalTaxes = money.Sum(o => o.Split.Tax),
                TotalTaxRetained = money.Sum(o => o.Split.TaxRetained),
                TotalTips = money.Sum(o => o.Split.Tips),
                TotalDiscounts = money.Sum(o => o.Split.Discounts),
                TotalCleanersSalary = totalSalary,
                // TotalTaxes is NOT subtracted: it is charged on top of the price, so it was never
                // part of TotalAmount in the first place. Subtracting it here used to double-count
                // it. TotalTaxRetained is the opposite case — it IS inside TotalAmount on purpose,
                // because tax collected outside Stripe is never remitted and stays company money.
                TotalCompanyRevenueGross = totalRevenue - totalSalary
            };

            // Expenses use the same window. Match the inclusive `to` convention used above.
            var expenseFrom = from?.Date ?? DateTime.MinValue;
            var expenseTo = (to?.Date ?? DateTime.UtcNow.Date).AddDays(1);
            var breakdown = await _expenseService.GetBreakdownAsync(expenseFrom, expenseTo);

            // Re-apply the same date window conditionally (never push DateTime.MinValue into SQL —
            // it's out of MariaDB's DATETIME range; the main query above bounds the same way).
            // Same performed-order set as `query` above: a refunded order's Stripe processing fee is
            // still a real cost — Stripe keeps its fee when you refund a charge — so the order has
            // to stay in the fee base rather than vanish with its status change.
            IQueryable<Order> windowed = _context.Orders
                .Where(o => o.Status == OrderStatuses.Done
                    || (o.Status == OrderStatuses.Refunded && o.StatusBeforeRefund == OrderStatuses.Done)
                    || (includeUpcoming && (o.Status == OrderStatuses.Active || o.Status == OrderStatuses.Pending)));
            if (from.HasValue)
                windowed = windowed.Where(o => o.ServiceDate >= from.Value.Date);
            if (to.HasValue)
                windowed = windowed.Where(o => o.ServiceDate < to.Value.Date.AddDays(1));

            // How many booked-but-unfinished orders this window holds. Reported ALWAYS (not only
            // when includeUpcoming is on) so the finances page can label its projection toggle
            // — "include 12 unfinished cleanings" — before anything is folded in.
            //
            // upcomingTo exists because the money window and this count want different end dates.
            // A running filter like "This Month" reports money only up to TODAY (counting revenue
            // against days that haven't happened is meaningless), but every unfinished cleaning is
            // by definition in the future — bounding this count at `to` would report 2 of the
            // month's 5 remaining jobs. Callers pass the real period end here; it defaults to `to`.
            var upcomingEnd = upcomingTo ?? to;
            IQueryable<Order> upcoming = _context.Orders
                .Where(o => (o.IsPaid || o.PaymentMethod != PaymentMethod.Normal)
                    && (o.Status == OrderStatuses.Active || o.Status == OrderStatuses.Pending));
            if (from.HasValue)
                upcoming = upcoming.Where(o => o.ServiceDate >= from.Value.Date);
            if (upcomingEnd.HasValue)
                upcoming = upcoming.Where(o => o.ServiceDate < upcomingEnd.Value.Date.AddDays(1));
            stats.UpcomingOrders = await upcoming.CountAsync();
            stats.IncludesUpcoming = includeUpcoming;

            // Stripe processing fees — statistics-only. Only real card charges qualify
            // (IsPaid && PaymentMethod==Normal); manual/cash orders are never charged by Stripe.
            var stripeAgg = await windowed
                .Where(o => o.IsPaid && o.PaymentMethod == PaymentMethod.Normal)
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.Count(), Total = g.Sum(o => o.Total) })
                .FirstOrDefaultAsync();

            // Mixed-payment correction: when a Stripe order was later topped up via an order edit
            // whose additional amount was collected OUTSIDE Stripe (Zelle/Cash/Check), that part of
            // o.Total never went through the card. Subtract those manually-paid additional amounts
            // from the percentage-fee base so no Stripe fee is charged on money Stripe never touched.
            // The per-order fixed fee stays — the base order still had one real card transaction.
            var manualAdditionalsOnStripeOrders = await windowed
                .Where(o => o.IsPaid && o.PaymentMethod == PaymentMethod.Normal)
                .SelectMany(o => o.UpdateHistory)
                .Where(h => h.IsPaid && h.PaymentMethod != PaymentMethod.Normal)
                .SumAsync(h => (decimal?)h.AdditionalAmount) ?? 0m;

            stats.StripeFees = stripeAgg == null ? 0m
                : decimal.Round((stripeAgg.Total - manualAdditionalsOnStripeOrders) * StripeFeePercent
                                + stripeAgg.Count * StripeFixedFee, 2);

            // Admin bonuses (GEL), converted to USD per-month at each month's locked FX rate.
            // Staff bonuses, taken from the SAME per-order figures the shifts panel pays out
            // (AdminBonusAttribution). One order can owe two people — the administrator who took
            // the booking and the manager they report to — at rates that differ by whether the
            // customer was new, so a count times one rate stopped describing this cost.
            //
            // Fully-refunded orders earn nothing: a refund overwrites Status with "Refunded", which
            // the eligibility predicate already excludes. A PARTIALLY refunded order keeps its
            // bonus — the company kept some of the money. When the projection toggle is on, jobs
            // that are booked and paid but not yet delivered are folded in as well, because they
            // will cost these bonuses.
            var bonusCosts = await _adminBonusService.GetOrderBonusCostsGelAsync(
                from?.Date, to?.Date.AddDays(1), includeUnfinished: includeUpcoming);

            var bonusOrderMonths = await _context.Orders
                .Where(o => bonusCosts.Keys.Contains(o.Id))
                .Select(o => new { o.Id, o.ServiceDate.Year, o.ServiceDate.Month })
                .ToListAsync();

            decimal adminBonusGel = 0m, adminBonusUsd = 0m;
            foreach (var g in bonusOrderMonths.GroupBy(m => new { m.Year, m.Month }))
            {
                var snap = await _financialRateService.GetOrCreateAsync(g.Key.Year, g.Key.Month);
                var gel = g.Sum(m => bonusCosts[m.Id]);
                adminBonusGel += gel;
                adminBonusUsd += decimal.Round(gel * snap.UsdPerGel, 2);
            }
            stats.AdminBonusesGel = adminBonusGel;
            stats.AdminBonusesUsd = adminBonusUsd;

            // ── Google Ads daily run-rate + forecast for the rest of the period ───────────
            // Ad spend is the one expense written one row per day (GoogleAdsCostService upserts
            // SourceKey "googleads:yyyy-MM-dd"), so it has a real per-day rate — and the days of a
            // still-running period that haven't been synced yet can be filled in from it.
            //
            // Denominator is ELAPSED CALENDAR DAYS, not the number of rows: a day with no spend is
            // still a day that cost nothing, and only writing rows for cost > 0 would otherwise
            // inflate the average. Today counts as elapsed even though its sync is partial — the
            // alternative (treating today as a forecast day) would double-count the partial row
            // already in the window.
            //
            // Everything here is in the ads account's timezone (NY), the same zone the sync writes
            // its dates in, so the day boundaries line up.
            var adsCategory = breakdown.ByCategory
                .FirstOrDefault(c => c.CategoryName == GoogleAdsCostService.CategoryName);
            stats.GoogleAdsSpend = adsCategory?.Total ?? 0m;

            var todayNy = NyTimeHelper.NowNy.Date;
            // All-time (no `from`) has no meaningful start, so the run-rate window opens at the
            // first day that actually carries ad spend rather than at DateTime.MinValue.
            var adsStart = from?.Date
                ?? (adsCategory != null && adsCategory.Items.Count > 0
                    ? adsCategory.Items.Min(i => i.Date.Date)
                    : todayNy);
            var adsEnd = to?.Date ?? todayNy;

            var coveredEnd = adsEnd < todayNy ? adsEnd : todayNy;
            stats.GoogleAdsCoveredDays = coveredEnd >= adsStart
                ? (int)(coveredEnd - adsStart).TotalDays + 1
                : 0;
            stats.GoogleAdsDailyAverage = stats.GoogleAdsCoveredDays > 0
                ? decimal.Round(stats.GoogleAdsSpend / stats.GoogleAdsCoveredDays, 2)
                : 0m;
            stats.GoogleAdsProjectedDays = adsEnd > todayNy
                ? (int)(adsEnd - todayNy).TotalDays
                : 0;

            // Only a projection run forecasts the remaining days, and only when there is a rate to
            // forecast from. The amount is folded straight into the Google Ads category so it flows
            // through operating expenses into the bottom line exactly like real synced spend.
            if (includeUpcoming && adsCategory != null
                && stats.GoogleAdsProjectedDays > 0 && stats.GoogleAdsDailyAverage > 0)
            {
                stats.GoogleAdsProjectedSpend =
                    decimal.Round(stats.GoogleAdsDailyAverage * stats.GoogleAdsProjectedDays, 2);

                adsCategory.Total += stats.GoogleAdsProjectedSpend;
                breakdown.Total += stats.GoogleAdsProjectedSpend;
                // Shown when the category row is expanded, so the forecast is never a silent
                // addition the owner can't account for.
                adsCategory.Items.Insert(0, new ExpenseOccurrenceDto
                {
                    ExpenseId = 0,
                    Name = $"Projected ad spend ({stats.GoogleAdsProjectedDays} day"
                        + $"{(stats.GoogleAdsProjectedDays == 1 ? "" : "s")} × "
                        + $"{stats.GoogleAdsDailyAverage:C} average)",
                    CategoryId = adsCategory.CategoryId,
                    CategoryName = adsCategory.CategoryName,
                    Date = adsEnd,
                    Amount = stats.GoogleAdsProjectedSpend,
                    IsRecurring = false
                });
            }

            // Grand total expenses = table expenses + Stripe fees + admin bonuses (USD).
            var totalExpenses = breakdown.Total + stats.StripeFees + stats.AdminBonusesUsd;
            stats.TotalExpenses = totalExpenses;
            stats.TotalCompanyRevenue = stats.TotalCompanyRevenueGross - totalExpenses;
            stats.ExpensesBreakdown = breakdown;

            return Ok(stats);
        }

        [HttpGet("statistics/daily")]
        [RequirePageView(AdminViewablePages.Statistics)]
        public async Task<ActionResult<List<DailyStatisticsDto>>> GetDailyStatistics(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            // Same filter as /statistics — include manual-paid orders alongside Stripe-paid, and
            // keep fully-refunded orders that were performed so their cleaner cost still counts.
            var query = _context.Orders
                .Where(o => (o.IsPaid || o.PaymentMethod != PaymentMethod.Normal)
                    && (o.Status == OrderStatuses.Done
                        || (o.Status == OrderStatuses.Refunded && o.StatusBeforeRefund == OrderStatuses.Done)));

            if (from.HasValue)
                query = query.Where(o => o.ServiceDate >= from.Value.Date);

            if (to.HasValue)
                query = query.Where(o => o.ServiceDate < to.Value.Date.AddDays(1));

            var orders = await query
                .Select(o => new
                {
                    o.Id,
                    o.ServiceDate,
                    o.SubTotal,
                    o.DiscountAmount,
                    o.SubscriptionDiscountAmount,
                    o.LoyaltyDiscountAmount,
                    o.Tax,
                    o.Tips,
                    o.CompanyDevelopmentTips,
                    o.CleanerTotalSalary,
                    o.Total,
                    o.IsPaid,
                    o.PaymentMethod,
                    o.Status,
                    o.TotalRefundedAmount
                })
                .ToListAsync();

            var additionalTax = await LoadAdditionalTaxByOrderAsync(query);

            // Same buckets the /statistics totals use, so a summed chart matches the cards.
            var moneyByOrder = orders.ToDictionary(o => o.Id, o => OrderRevenueMath.Split(
                o.SubTotal, o.DiscountAmount, o.SubscriptionDiscountAmount,
                o.LoyaltyDiscountAmount, o.Tax, o.Tips, o.CompanyDevelopmentTips,
                o.TotalRefundedAmount,
                RetainedTaxFor(additionalTax, o.Id, o.Tax, o.PaymentMethod)));

            // Per-order sum of additional amounts that were paid OUTSIDE Stripe (mirrors the
            // mixed-payment correction in /statistics). Subtracted from each order's Stripe-fee
            // base below so the daily chart's fees/revenue match the totals page.
            var manualAdditionalsByOrder = await query
                .SelectMany(o => o.UpdateHistory)
                .Where(h => h.IsPaid && h.PaymentMethod != PaymentMethod.Normal)
                .GroupBy(h => h.OrderId)
                .Select(g => new { OrderId = g.Key, Sum = g.Sum(x => x.AdditionalAmount) })
                .ToDictionaryAsync(x => x.OrderId, x => x.Sum);

            // Preload the locked month snapshots for every month present in the data (not the raw
            // window — an open-ended "all time" range must not iterate from year 1).
            var snaps = new Dictionary<int, MonthlyFinancialSnapshot>();
            foreach (var m in orders.Select(o => new { o.ServiceDate.Year, o.ServiceDate.Month }).Distinct())
            {
                snaps[m.Year * 100 + m.Month] = await _financialRateService.GetOrCreateAsync(m.Year, m.Month);
            }

            var feePercent = StripeFeePercent;
            var fixedFee = StripeFixedFee;

            decimal StripeFeeFor(decimal total) => decimal.Round(total * feePercent + fixedFee, 2);
            // Staff bonus per ORDER, in GEL, from the same source the totals page and the shifts
            // panel use — an order can owe an administrator and their manager at different rates,
            // so there is no per-order constant to multiply by any more. Orders that earn nothing
            // (unassigned, refunded, unpaid) are simply absent from the dictionary.
            var bonusCosts = await _adminBonusService.GetOrderBonusCostsGelAsync(
                from?.Date, to?.Date.AddDays(1));

            decimal BonusUsdFor(int orderId, int year, int month) =>
                bonusCosts.TryGetValue(orderId, out var gel)
                && snaps.TryGetValue(year * 100 + month, out var s)
                    ? decimal.Round(gel * s.UsdPerGel, 2)
                    : 0m;

            // Per-day expense attribution: each projected occurrence is added to its own day,
            // so the chart shows the actual bill date (e.g. RingCentral hits on the 1st of the month).
            var expenseFrom = from?.Date ?? DateTime.MinValue;
            var expenseTo = (to?.Date ?? DateTime.UtcNow.Date).AddDays(1);
            var occurrences = await _expenseService.GetOccurrencesInRangeAsync(expenseFrom, expenseTo);
            var expensesByDay = occurrences
                .GroupBy(o => o.Date.Date)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

            var dailyMap = orders
                .GroupBy(o => o.ServiceDate.Date)
                .ToDictionary(g => g.Key, g =>
                {
                    var stripeFees = g.Where(o => o.IsPaid && o.PaymentMethod == PaymentMethod.Normal)
                                      .Sum(o => StripeFeeFor(o.Total
                                          - (manualAdditionalsByOrder.TryGetValue(o.Id, out var mAdd) ? mAdd : 0m)));
                    // Mirrors /statistics: a fully-refunded order earns no bonus, but a partially
                    // refunded one does — the company kept part of the money. Both cases are
                    // already decided by which orders bonusCosts contains, so there is nothing to
                    // filter here; a second copy of that rule is exactly how the chart and the
                    // totals card would come to disagree.
                    var adminBonuses = g.Sum(o => BonusUsdFor(o.Id, o.ServiceDate.Year, o.ServiceDate.Month));
                    var computed = stripeFees + adminBonuses;
                    // ReportedRevenue, not Revenue: sales tax collected outside Stripe is company
                    // money (see OrderRevenueMath), so it rides inside Amount rather than Taxes.
                    var revenue = g.Sum(o => moneyByOrder[o.Id].ReportedRevenue);
                    var salary = g.Sum(o => o.CleanerTotalSalary);
                    return new DailyStatisticsDto
                    {
                        Date = g.Key.ToString("yyyy-MM-dd"),
                        Orders = g.Count(o => !OrderStatuses.IsRefunded(o.Status)),
                        Amount = revenue,
                        Taxes = g.Sum(o => moneyByOrder[o.Id].Tax),
                        TaxRetained = g.Sum(o => moneyByOrder[o.Id].TaxRetained),
                        Tips = g.Sum(o => moneyByOrder[o.Id].Tips),
                        CleanersSalary = salary,
                        StripeFees = stripeFees,
                        AdminBonuses = adminBonuses,
                        // Expenses starts with the computed fees/bonuses; table expenses are folded in below.
                        Expenses = computed,
                        // Tax is a pass-through charged on top of Amount, never inside it — see
                        // OrderRevenueMath. Subtracting it here would double-count it.
                        CompanyRevenue = revenue - salary - computed
                    };
                });

            // Fold in expense days that have no orders so the chart still reflects them.
            foreach (var kv in expensesByDay)
            {
                if (!dailyMap.TryGetValue(kv.Key, out var row))
                {
                    row = new DailyStatisticsDto
                    {
                        Date = kv.Key.ToString("yyyy-MM-dd"),
                        Orders = 0
                    };
                    dailyMap[kv.Key] = row;
                }
                // Add table expenses on top of any Stripe-fee / admin-bonus amounts already on this day.
                row.Expenses += kv.Value;
                row.CompanyRevenue -= kv.Value;
            }

            var daily = dailyMap.Values.OrderBy(d => d.Date).ToList();

            return Ok(daily);
        }

        // ── Monthly FX / bonus-rate snapshots (SuperAdmin) ─────────────────────────────
        // Lets SuperAdmin see and override the GEL→USD rate used to convert admin bonuses on
        // the statistics page. Each month is locked once set; overriding one month never touches
        // another.

        [HttpGet("statistics/financial-rates")]
        [RequirePageView(AdminViewablePages.Statistics)]
        public async Task<ActionResult<List<MonthlyFinancialRateDto>>> GetFinancialRates(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            // Default to the trailing 12 months when unbounded, so we never enumerate from year 1.
            var toDate = (to?.Date ?? DateTime.UtcNow.Date).AddDays(1);
            var fromDate = from?.Date ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-11);

            var rows = await _financialRateService.ListAsync(fromDate, toDate);
            return Ok(rows);
        }

        [HttpPut("statistics/financial-rates/{year:int}/{month:int}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<MonthlyFinancialRateDto>> SetFinancialRate(
            int year, int month, [FromBody] SetFxRateDto dto)
        {
            if (month < 1 || month > 12)
                return BadRequest(new { message = "Month must be between 1 and 12." });
            if (dto.UsdPerGel <= 0)
                return BadRequest(new { message = "Exchange rate (USD per GEL) must be greater than zero." });

            var userId = int.Parse(User.FindFirst("UserId")?.Value ?? "0");
            try
            {
                // A manual FX rate re-denominates every GEL figure in that month's reporting, so
                // what it was before is the load-bearing half of the record.
                var previous = await _financialRateService.GetOrCreateAsync(year, month);

                var result = await _financialRateService.SetManualFxAsync(year, month, dto.UsdPerGel, userId);

                await _auditService.LogActionAsync(
                    AuditEntityTypes.SiteSetting, 0, "FinancialRateSet",
                    new { Year = year, Month = month, UsdPerGel = previous.UsdPerGel },
                    new { Year = year, Month = month, UsdPerGel = result?.UsdPerGel, Source = "Manual" });

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("statistics/financial-rates/{year:int}/{month:int}/refetch")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<MonthlyFinancialRateDto>> RefetchFinancialRate(int year, int month)
        {
            if (month < 1 || month > 12)
                return BadRequest(new { message = "Month must be between 1 and 12." });

            var userId = int.Parse(User.FindFirst("UserId")?.Value ?? "0");

            var previous = await _financialRateService.GetOrCreateAsync(year, month);
            var result = await _financialRateService.RefetchAsync(year, month, userId);

            await _auditService.LogActionAsync(
                AuditEntityTypes.SiteSetting, 0, "FinancialRateRefetched",
                new { Year = year, Month = month, UsdPerGel = previous.UsdPerGel },
                new { Year = year, Month = month, UsdPerGel = result?.UsdPerGel, Source = "Refetched" });

            return Ok(result);
        }

    }
}
