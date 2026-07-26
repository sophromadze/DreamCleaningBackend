namespace DreamCleaningBackend.Configuration
{
    /// <summary>
    /// Strongly-typed binding of the "Ga4" appsettings section — credentials for the one-time
    /// GA4 → Orders attribution backfill (Google Analytics Data API v1beta runReport). Real values
    /// live in appsettings.Production.json (provisioned separately) and are never hardcoded here.
    ///
    /// The refresh token MUST be authorized for the <c>analytics.readonly</c> scope (the Google Ads
    /// refresh token is scoped to adwords and cannot call this API). PropertyId is the NUMERIC GA4
    /// property id (Admin → Property Settings), NOT the "G-…" measurement id.
    /// </summary>
    public class Ga4Options
    {
        public const string SectionName = "Ga4";

        // OAuth2 credentials. The refresh token must carry https://www.googleapis.com/auth/analytics.readonly.
        public string? OAuth2ClientId { get; set; }
        public string? OAuth2ClientSecret { get; set; }
        public string? OAuth2RefreshToken { get; set; }

        // Numeric GA4 property id used in the API path: properties/{PropertyId}:runReport.
        public string? PropertyId { get; set; }

        // Earliest day to query (yyyy-MM-dd). GA4 event-scoped data is bounded by the property's
        // retention (default 14 months), so anything earlier simply returns no rows. Defaults wide.
        public string StartDate { get; set; } = "2023-01-01";

        // Force outbound HTTP to IPv4 (default true) — the production VPS has IPv6 disabled. Same
        // gotcha as the Google Ads / Reviews / Telegram clients. Applied in Program.cs on the named client.
        public bool ForceIpv4 { get; set; } = true;
    }
}
