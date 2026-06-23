namespace DreamCleaningBackend.Services.CallTracking
{
    /// <summary>
    /// Provider-neutral representation of one fetched call. An <see cref="ICallProvider"/>
    /// adapter maps its raw API response into this shape; the sync service then upserts it into
    /// <see cref="Models.CallRecord"/>. DB-only fields (Id, LeadId, CreatedAt) are intentionally
    /// absent — they're owned by persistence/linking, not the provider.
    /// </summary>
    public class CallRecordDto
    {
        public string Provider { get; set; } = string.Empty;
        public string ExternalId { get; set; } = string.Empty;
        public string? SessionId { get; set; }
        public string Direction { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public string? FromNumber { get; set; }
        public string? FromName { get; set; }
        public string? ToNumber { get; set; }
        public DateTime StartTimeUtc { get; set; }
        public int DurationSeconds { get; set; }
        public string? RecordingUrl { get; set; }
    }
}
