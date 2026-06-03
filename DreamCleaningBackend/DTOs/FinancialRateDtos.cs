using System;

namespace DreamCleaningBackend.DTOs
{
    // One month's locked rate row, surfaced to the statistics page so SuperAdmin can see and
    // override the GEL→USD rate used for admin-bonus conversion.
    public class MonthlyFinancialRateDto
    {
        public int Year { get; set; }
        public int Month { get; set; }
        // "2026-06" — convenient key for the frontend.
        public string MonthKey { get; set; } = string.Empty;
        // USD value of 1 GEL.
        public decimal UsdPerGel { get; set; }
        // GEL bonus rate frozen for the month.
        public decimal AdminBonusRatePerOrderGel { get; set; }
        // "auto" | "manual" | "fallback".
        public string FxSource { get; set; } = "auto";
        public bool IsFinalized { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? UpdatedByUserName { get; set; }
    }

    // Body of PUT /api/admin/statistics/financial-rates/{year}/{month}.
    public class SetFxRateDto
    {
        // USD value of 1 GEL (e.g. 0.37). Must be > 0.
        public decimal UsdPerGel { get; set; }
    }
}
