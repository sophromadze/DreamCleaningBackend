namespace DreamCleaningBackend.Services.Interfaces
{
    public class SearchConsoleSyncResult
    {
        public int RowsUpserted { get; set; }
        public int DaysCovered { get; set; }
    }

    /// <summary>
    /// Pulls organic search queries (impressions/clicks/CTR/avg position) from the Google Search
    /// Console Search Analytics API into <see cref="Models.SearchConsoleDailyStat"/>.
    /// </summary>
    public interface ISearchConsoleSyncService
    {
        bool IsConfigured { get; }

        /// <summary>Full historical pull from BackfillStartDate up to the most recent available day.</summary>
        Task<SearchConsoleSyncResult> BackfillAsync(CancellationToken ct = default);

        /// <summary>Trailing-window refresh so Search Console's 2–3 day reporting lag is corrected.</summary>
        Task<SearchConsoleSyncResult> SyncRecentAsync(CancellationToken ct = default);
    }
}
