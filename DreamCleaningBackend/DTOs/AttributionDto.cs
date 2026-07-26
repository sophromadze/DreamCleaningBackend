namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// First-touch acquisition attribution captured client-side by the Angular AttributionService
    /// (read from the non-httpOnly `dc_attribution` cookie) and sent with a self-service booking.
    /// The server NEVER trusts these raw values for anything but analytics: the channel is
    /// normalized to a known set and every field is length-clamped before it lands on the Order.
    /// LandingPage/Timestamp are informational only and are not persisted (no column for them).
    /// </summary>
    public class AttributionDto
    {
        public string? Channel { get; set; }
        public string? Source { get; set; }
        public string? Medium { get; set; }
        public string? Campaign { get; set; }
        public string? LandingPage { get; set; }
        public string? Timestamp { get; set; }
    }
}
