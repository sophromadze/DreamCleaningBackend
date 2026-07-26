namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for acquisition-channel normalization, shared by order attribution
    /// (BookingCreationService) and session logging (SessionAttributionController). The client
    /// classifies the visit; the server only trusts WHICH known channel it is (never arbitrary
    /// strings) and length-clamps the free-text source/medium/campaign.
    /// </summary>
    public static class AcquisitionChannels
    {
        // The channels a client visit can be classified as (GA4-style). "Phone/Unknown" and
        // "Unattributed" are server/reporting-only buckets and are intentionally NOT in this set.
        public static readonly HashSet<string> Client = new(StringComparer.OrdinalIgnoreCase)
        {
            "Paid Search", "AI Assistant", "Organic Search", "Referral", "Direct", "Unassigned",
            "Social", "Email"
        };

        /// <summary>First-touch channel stamped on admin-booked (phone) orders.</summary>
        public const string AdminManual = "Phone/Unknown";

        /// <summary>Reporting bucket for orders/sessions with no captured channel.</summary>
        public const string Unattributed = "Unattributed";

        /// <summary>
        /// Normalizes a client-supplied channel: blank → null; a recognized channel → its canonical
        /// casing; anything else → "Unassigned". Callers that must always have a value (session
        /// logging) coalesce the null to their own default.
        /// </summary>
        public static string? Normalize(string? channel)
        {
            if (string.IsNullOrWhiteSpace(channel)) return null;
            var trimmed = channel.Trim();
            return Client.TryGetValue(trimmed, out var canonical) ? canonical : "Unassigned";
        }

        /// <summary>Trims and length-clamps a free-text attribution value; blank → null.</summary>
        public static string? Clamp(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
        }
    }
}
