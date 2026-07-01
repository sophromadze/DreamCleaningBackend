namespace DreamCleaningBackend.Configuration
{
    /// <summary>
    /// Strongly-typed binding of the "GoogleAds" appsettings section. The real values live in
    /// appsettings.Production.json (already provisioned) and are never hardcoded here.
    /// Bound via <c>services.Configure&lt;GoogleAdsOptions&gt;(...)</c> and consumed through
    /// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>.
    /// </summary>
    public class GoogleAdsOptions
    {
        public const string SectionName = "GoogleAds";

        // OAuth2 + developer-token credentials for the Google Ads API.
        public string? DeveloperToken { get; set; }
        public string? OAuth2ClientId { get; set; }
        public string? OAuth2ClientSecret { get; set; }
        public string? OAuth2RefreshToken { get; set; }

        // Manager (MCC) account that authorizes the call; digits only, no dashes.
        public string? LoginCustomerId { get; set; }

        // The ads account whose spend we query; digits only, no dashes.
        public string? CustomerId { get; set; }

        // First day (account timezone) the backfill should pull, e.g. "2026-02-16".
        public string? BackfillStartDate { get; set; }

        // Google Ads REST API version segment used in the endpoint path (e.g. "v24"). Kept in
        // config so we can bump it without a code change when the account moves to a newer API.
        // The cost query (segments.date + metrics.cost_micros FROM customer) is stable across
        // versions, so any currently-supported version works.
        public string ApiVersion { get; set; } = "v24";

        // Force outbound HTTP to IPv4 (default true). The production VPS has IPv6 disabled, so
        // dual-stack DNS results otherwise stall the connection — same gotcha the Google Reviews
        // and Telegram clients hit. Leave true in production; flip to false only for local
        // debugging on an IPv6-capable box. Applied in Program.cs on the named HttpClient.
        public bool ForceIpv4 { get; set; } = true;
    }
}
