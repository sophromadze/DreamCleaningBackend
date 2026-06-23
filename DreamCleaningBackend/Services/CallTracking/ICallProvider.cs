namespace DreamCleaningBackend.Services.CallTracking
{
    /// <summary>
    /// Abstraction over a telephony provider's call-log API. The rest of the call-tracking
    /// stack (sync service, lead-linking, storage, export, UI) depends only on this interface,
    /// so changing providers means writing one new adapter and re-registering it in DI — nothing
    /// downstream changes. Keep everything here provider-neutral.
    /// </summary>
    public interface ICallProvider
    {
        /// <summary>Stable provider identifier persisted on each record (e.g. "RingCentral").</summary>
        string ProviderName { get; }

        /// <summary>True only when the provider is configured and call-log sync is turned on.</summary>
        bool IsEnabled();

        /// <summary>
        /// Fetch completed calls in the [fromUtc, toUtc] window. Implementations handle
        /// pagination and map records into the neutral DTO. Returns an empty list (never null)
        /// when nothing matches or the provider is disabled.
        /// </summary>
        Task<IReadOnlyList<CallRecordDto>> FetchCallsAsync(
            DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    }
}
