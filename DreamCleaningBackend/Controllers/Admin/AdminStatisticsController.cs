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
using DreamCleaningBackend.Services.Reporting;
using DreamCleaningBackend.Helpers;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>Statistics and income reports (SuperAdmin only endpoints).
    /// Split out of the monolithic AdminController; same api/admin route prefix, so URLs are unchanged.</summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminStatisticsController : AdminControllerBase
    {
        private readonly IStatisticsReportService _statisticsReport;
        private readonly IFinancialRateService _financialRateService;
        private readonly IAuditService _auditService;

        public AdminStatisticsController(IStatisticsReportService statisticsReport,
            IFinancialRateService financialRateService,
            IAuditService auditService)
        {
            _statisticsReport = statisticsReport;
            _financialRateService = financialRateService;
            _auditService = auditService;
        }

        [HttpGet("statistics")]
        [RequirePageView(AdminViewablePages.Statistics, AdminViewablePages.Finances)]
        public async Task<ActionResult<OrderStatisticsDto>> GetOrderStatistics(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] bool includeUpcoming = false,
            [FromQuery] DateTime? upcomingTo = null)
            // The report itself lives in StatisticsReportService (2026-10) so the SuperAdmin
            // analytics agent reads the very same figures; this action only serves it.
            => Ok(await _statisticsReport.GetOrderStatisticsAsync(from, to, includeUpcoming, upcomingTo));

        [HttpGet("statistics/daily")]
        [RequirePageView(AdminViewablePages.Statistics)]
        public async Task<ActionResult<List<DailyStatisticsDto>>> GetDailyStatistics(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
            => Ok(await _statisticsReport.GetDailyStatisticsAsync(from, to));

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
