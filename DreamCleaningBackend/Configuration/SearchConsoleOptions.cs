namespace DreamCleaningBackend.Configuration
{
    /// <summary>
    /// Strongly-typed binding of the "SearchConsole" appsettings section — credentials for pulling
    /// organic search queries from the Google Search Console API (Search Analytics). Real values live
    /// in appsettings.Production.json (provisioned separately) and are never hardcoded here.
    ///
    /// The refresh token MUST be authorized for the <c>https://www.googleapis.com/auth/webmasters.readonly</c>
    /// scope — the GA4 (analytics.readonly) and Google Ads (adwords) tokens will NOT work here.
    /// <see cref="SiteUrl"/> must exactly match a verified property: either a URL-prefix property
    /// ("https://dreamcleaningnyc.com/", with trailing slash) or a Domain property
    /// ("sc-domain:dreamcleaningnyc.com").
    /// </summary>
    public class SearchConsoleOptions
    {
        public const string SectionName = "SearchConsole";

        public string? OAuth2ClientId { get; set; }
        public string? OAuth2ClientSecret { get; set; }
        public string? OAuth2RefreshToken { get; set; }

        // The verified Search Console property identifier (URL-prefix or sc-domain:…). Sent
        // URL-encoded in the API path: sites/{siteUrl}/searchAnalytics/query.
        public string? SiteUrl { get; set; }

        // Earliest day to backfill (yyyy-MM-dd). Search Console retains ~16 months, so earlier dates
        // simply return no rows. Defaults wide.
        public string BackfillStartDate { get; set; } = "2024-01-01";

        // Force outbound HTTP to IPv4 (default true) — the production VPS has IPv6 disabled. Same
        // gotcha as the Google Ads / GA4 / Reviews / Telegram clients. Applied in Program.cs on the client.
        public bool ForceIpv4 { get; set; } = true;
    }
}
