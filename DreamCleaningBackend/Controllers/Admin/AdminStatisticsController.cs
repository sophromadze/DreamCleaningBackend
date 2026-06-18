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

        public AdminStatisticsController(ApplicationDbContext context,
            IConfiguration configuration,
            IExpenseService expenseService,
            IFinancialRateService financialRateService)
        {
            _context = context;
            _configuration = configuration;
            _expenseService = expenseService;
            _financialRateService = financialRateService;
        }

        // Stripe US standard processing fee: 2.9% of the charged amount + $0.30 per transaction.
        // Overridable via config without code changes. Used for statistics only — never alters
        // the amounts customers/admins see on an order.
        private decimal StripeFeePercent => _configuration.GetValue<decimal>("Stripe:FeePercent", 0.029m);
        private decimal StripeFixedFee => _configuration.GetValue<decimal>("Stripe:FixedFeePerOrder", 0.30m);

        // ───── Statistics (SuperAdmin only) ─────

        [HttpGet("statistics")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<OrderStatisticsDto>> GetOrderStatistics(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            // Counts both Stripe-paid orders (IsPaid=true, PaymentMethod=Normal) and manual-paid
            // orders (PaymentMethod != Normal, IsPaid=false) — see Order.PaymentMethod docs.
            var query = _context.Orders
                .Where(o => (o.IsPaid || o.PaymentMethod != PaymentMethod.Normal) && o.Status == "Done");

            if (from.HasValue)
                query = query.Where(o => o.ServiceDate >= from.Value.Date);

            if (to.HasValue)
                query = query.Where(o => o.ServiceDate < to.Value.Date.AddDays(1));

            var stats = await query.GroupBy(_ => 1).Select(g => new OrderStatisticsDto
            {
                TotalOrders = g.Count(),
                TotalAmount = g.Sum(o => o.SubTotal),
                TotalTaxes = g.Sum(o => o.Tax),
                TotalTips = g.Sum(o => o.Tips) + g.Sum(o => o.CompanyDevelopmentTips),
                TotalCleanersSalary = g.Sum(o => o.CleanerTotalSalary),
                TotalCompanyRevenueGross = g.Sum(o => o.SubTotal) - g.Sum(o => o.Tax) - g.Sum(o => o.CleanerTotalSalary)
            }).FirstOrDefaultAsync() ?? new OrderStatisticsDto();

            // Expenses use the same window. Match the inclusive `to` convention used above.
            var expenseFrom = from?.Date ?? DateTime.MinValue;
            var expenseTo = (to?.Date ?? DateTime.UtcNow.Date).AddDays(1);
            var breakdown = await _expenseService.GetBreakdownAsync(expenseFrom, expenseTo);

            // Re-apply the same date window conditionally (never push DateTime.MinValue into SQL —
            // it's out of MariaDB's DATETIME range; the main query above bounds the same way).
            IQueryable<Order> windowed = _context.Orders.Where(o => o.Status == "Done");
            if (from.HasValue)
                windowed = windowed.Where(o => o.ServiceDate >= from.Value.Date);
            if (to.HasValue)
                windowed = windowed.Where(o => o.ServiceDate < to.Value.Date.AddDays(1));

            // Stripe processing fees — statistics-only. Only real card charges qualify
            // (IsPaid && PaymentMethod==Normal); manual/cash orders are never charged by Stripe.
            var stripeAgg = await windowed
                .Where(o => o.IsPaid && o.PaymentMethod == PaymentMethod.Normal)
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.Count(), Total = g.Sum(o => o.Total) })
                .FirstOrDefaultAsync();
            stats.StripeFees = stripeAgg == null ? 0m
                : decimal.Round(stripeAgg.Total * StripeFeePercent + stripeAgg.Count * StripeFixedFee, 2);

            // Admin bonuses (GEL), converted to USD per-month at each month's locked FX rate.
            // Eligible = assigned + Done + (paid or manual), matching AdminBonusService.
            var bonusByMonth = await windowed
                .Where(o => o.AssignedAdminId != null && (o.IsPaid || o.PaymentMethod != PaymentMethod.Normal))
                .GroupBy(o => new { o.ServiceDate.Year, o.ServiceDate.Month })
                .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
                .ToListAsync();

            decimal adminBonusGel = 0m, adminBonusUsd = 0m;
            foreach (var b in bonusByMonth)
            {
                var snap = await _financialRateService.GetOrCreateAsync(b.Year, b.Month);
                var gel = b.Count * snap.AdminBonusRatePerOrderGel;
                adminBonusGel += gel;
                adminBonusUsd += decimal.Round(gel * snap.UsdPerGel, 2);
            }
            stats.AdminBonusesGel = adminBonusGel;
            stats.AdminBonusesUsd = adminBonusUsd;

            // Grand total expenses = table expenses + Stripe fees + admin bonuses (USD).
            var totalExpenses = breakdown.Total + stats.StripeFees + stats.AdminBonusesUsd;
            stats.TotalExpenses = totalExpenses;
            stats.TotalCompanyRevenue = stats.TotalCompanyRevenueGross - totalExpenses;
            stats.ExpensesBreakdown = breakdown;

            return Ok(stats);
        }

        [HttpGet("statistics/daily")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<List<DailyStatisticsDto>>> GetDailyStatistics(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            // Same filter as /statistics — include manual-paid orders alongside Stripe-paid.
            var query = _context.Orders
                .Where(o => (o.IsPaid || o.PaymentMethod != PaymentMethod.Normal) && o.Status == "Done");

            if (from.HasValue)
                query = query.Where(o => o.ServiceDate >= from.Value.Date);

            if (to.HasValue)
                query = query.Where(o => o.ServiceDate < to.Value.Date.AddDays(1));

            var orders = await query
                .Select(o => new
                {
                    o.ServiceDate,
                    o.SubTotal,
                    o.Tax,
                    o.Tips,
                    o.CompanyDevelopmentTips,
                    o.CleanerTotalSalary,
                    o.Total,
                    o.IsPaid,
                    o.PaymentMethod,
                    o.AssignedAdminId
                })
                .ToListAsync();

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
            decimal BonusUsdFor(int year, int month) =>
                snaps.TryGetValue(year * 100 + month, out var s)
                    ? decimal.Round(s.AdminBonusRatePerOrderGel * s.UsdPerGel, 2)
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
                                      .Sum(o => StripeFeeFor(o.Total));
                    var adminBonuses = g.Where(o => o.AssignedAdminId != null)
                                        .Sum(o => BonusUsdFor(o.ServiceDate.Year, o.ServiceDate.Month));
                    var computed = stripeFees + adminBonuses;
                    return new DailyStatisticsDto
                    {
                        Date = g.Key.ToString("yyyy-MM-dd"),
                        Orders = g.Count(),
                        Amount = g.Sum(o => o.SubTotal),
                        Taxes = g.Sum(o => o.Tax),
                        Tips = g.Sum(o => o.Tips) + g.Sum(o => o.CompanyDevelopmentTips),
                        CleanersSalary = g.Sum(o => o.CleanerTotalSalary),
                        StripeFees = stripeFees,
                        AdminBonuses = adminBonuses,
                        // Expenses starts with the computed fees/bonuses; table expenses are folded in below.
                        Expenses = computed,
                        CompanyRevenue = g.Sum(o => o.SubTotal) - g.Sum(o => o.Tax) - g.Sum(o => o.CleanerTotalSalary) - computed
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
        [Authorize(Roles = "SuperAdmin")]
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
                var result = await _financialRateService.SetManualFxAsync(year, month, dto.UsdPerGel, userId);
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
            var result = await _financialRateService.RefetchAsync(year, month, userId);
            return Ok(result);
        }

    }
}
