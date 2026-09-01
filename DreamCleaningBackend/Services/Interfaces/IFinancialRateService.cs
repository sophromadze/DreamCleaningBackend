using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    // Owns the per-month locked FX snapshot used by the statistics page to convert GEL staff
    // bonuses into USD, and reports what those bonuses come to. Only the exchange rate is frozen
    // per month — the bonus figures are computed live, because a bonus is no longer one rate per
    // order. See MonthlyFinancialSnapshot.
    public interface IFinancialRateService
    {
        // Returns the locked snapshot for the month, creating it (auto-fetching FX) on first
        // access. Refreshes the ongoing month's auto FX periodically; past months are finalized
        // and never change.
        Task<MonthlyFinancialSnapshot> GetOrCreateAsync(int year, int month);

        // Get-or-create every month that overlaps [fromInclusive, toExclusive), keyed by (Year*100+Month).
        Task<Dictionary<int, MonthlyFinancialSnapshot>> GetOrCreateForRangeAsync(DateTime fromInclusive, DateTime toExclusive);

        // SuperAdmin manual override of a month's GEL→USD rate. Marks the row source = "manual"
        // so it's never auto-refreshed afterwards.
        Task<MonthlyFinancialRateDto> SetManualFxAsync(int year, int month, decimal usdPerGel, int byUserId);

        // Clears a manual override and re-fetches the auto FX rate from the API.
        Task<MonthlyFinancialRateDto> RefetchAsync(int year, int month, int byUserId);

        // Lists the (get-or-created) month rows overlapping the window, newest first.
        Task<List<MonthlyFinancialRateDto>> ListAsync(DateTime fromInclusive, DateTime toExclusive);

        // Staff bonuses owed for one month, in GEL, at today's rates.
        Task<decimal> BonusTotalGelForMonthAsync(int year, int month);

        MonthlyFinancialRateDto ToDto(
            MonthlyFinancialSnapshot snap,
            string? updatedByUserName = null,
            decimal adminBonusTotalGel = 0m);
    }
}
