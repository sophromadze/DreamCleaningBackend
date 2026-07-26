namespace DreamCleaningBackend.Services.Interfaces
{
    public class Ga4SessionSyncResult
    {
        public string DateRange { get; set; } = "";
        public int Ga4Rows { get; set; }
        public int DaysOverwritten { get; set; }
        public int SessionRowsWritten { get; set; }
        public long TotalSessions { get; set; }
    }

    /// <summary>
    /// Reconciles <see cref="Models.SessionDailyStat"/> against GA4's bot-filtered session counts:
    /// a one-time historical backfill of the whole available range, and a nightly trailing-window
    /// re-pull that overwrites recently-finalized days. Same-day/very-recent days are left as our
    /// raw first-party ("live") counts until GA4 finalizes them.
    /// </summary>
    public interface IGa4SessionSyncService
    {
        bool IsConfigured { get; }

        /// <summary>One-time: reconcile everything GA4 has, from the configured start date up to the
        /// most recently finalized day — fills previously-empty historical days AND overwrites the
        /// already-elapsed live era, so no day is left permanently un-reconciled.</summary>
        Task<Ga4SessionSyncResult> BackfillHistoricalAsync(CancellationToken ct = default);

        /// <summary>Nightly: re-pull + overwrite the trailing window of finalized days.</summary>
        Task<Ga4SessionSyncResult> ReconcileRecentAsync(CancellationToken ct = default);
    }
}
