namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>Outcome of a Google Ads spend sync run.</summary>
    public class GoogleAdsSyncResult
    {
        // Number of days that were inserted or updated (zero-spend days are skipped).
        public int DaysSynced { get; set; }

        // Total ad spend (USD) across the days touched by this run.
        public decimal TotalUsd { get; set; }
    }

    /// <summary>
    /// Pulls per-day Google Ads account spend and writes it into the Expenses table (one Expense
    /// row per day, category "Google Ads", keyed by <c>Expense.SourceKey</c>). Because it lands in
    /// the normal Expenses pipeline, the Expenses page and Statistics page pick it up with no extra
    /// wiring.
    /// </summary>
    public interface IGoogleAdsCostService
    {
        // True only when every required credential/config value is present.
        bool IsConfigured { get; }

        // One-shot historical pull: BackfillStartDate → yesterday (account timezone). Upserts
        // every returned day. Safe to run repeatedly (idempotent by SourceKey).
        Task<GoogleAdsSyncResult> BackfillAsync(CancellationToken ct = default);

        // Rolling refresh of the trailing 7 days (today back 7). Google finalizes cost over 1–3
        // days, so re-syncing recent days corrects earlier estimates.
        Task<GoogleAdsSyncResult> SyncRecentAsync(CancellationToken ct = default);
    }
}
