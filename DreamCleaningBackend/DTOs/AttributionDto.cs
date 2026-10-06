namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// First-touch acquisition attribution captured client-side by the Angular AttributionService
    /// (read from the non-httpOnly `dc_attribution` cookie) and sent with a self-service booking.
    /// The server NEVER trusts these raw values for anything but analytics: the channel is
    /// normalized to a known set, the click id type to a known list, and every field is
    /// length-clamped before it lands on the Order. Timestamp is informational only.
    ///
    /// ClickIdType / ClickId / Referrer (2026-10) are only present on cookies written after the
    /// raw-signal capture shipped; older cookies simply omit them.
    /// </summary>
    public class AttributionDto
    {
        public string? Channel { get; set; }
        public string? Source { get; set; }
        public string? Medium { get; set; }
        public string? Campaign { get; set; }
        public string? LandingPage { get; set; }
        public string? Timestamp { get; set; }
        public string? ClickIdType { get; set; }
        public string? ClickId { get; set; }
        public string? Referrer { get; set; }
    }
}
